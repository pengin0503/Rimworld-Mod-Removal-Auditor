using ModRemovalAuditor.Audit;

namespace ModRemovalAuditor.Tests;

internal sealed class FakeSource : IScanSource
{
    private readonly Queue<List<AuditObservation>> steps = new();
    private readonly FakeDescriber describer = new();

    public FakeSource(string scope, bool critical = false)
    {
        ScopeId = scope;
        IsCritical = critical;
    }

    public string ScopeId { get; }

    public bool IsCritical { get; }

    public int StepCalls { get; private set; }

    public bool Began { get; private set; }

    public bool Ended { get; private set; }

    public int ThrowOnStep { get; set; } = -1;

    public bool ThrowOnBegin { get; set; }

    public CoverageStatus FinalStatus { get; set; } = CoverageStatus.Checked;

    public string Warning { get; set; }

    public FakeSource WithSteps(int count, Func<int, AuditObservation> make)
    {
        for (int i = 0; i < count; i++)
        {
            steps.Enqueue(new List<AuditObservation> { make(i) });
        }
        return this;
    }

    public void Begin()
    {
        Began = true;
        if (ThrowOnBegin)
        {
            throw new InvalidOperationException("begin failed");
        }
    }

    public bool Step(List<AuditObservation> output)
    {
        if (StepCalls++ == ThrowOnStep)
        {
            throw new InvalidOperationException("step failed in " + ScopeId);
        }
        if (steps.Count > 0)
        {
            output.AddRange(steps.Dequeue());
        }
        return steps.Count > 0;
    }

    public void End() => Ended = true;

    public AuditLocation DescribeLocation(in AuditObservation observation) => describer.DescribeLocation(observation);

    public string DescribeDef(object def) => describer.DescribeDef(def);
}

public class SessionTests
{
    private static readonly ModKey ModA = ModKey.Parse("a.mod");
    private static readonly FakeDef Gem = new FakeDef("ThingDef", "A_Gem", "gem");
    private static readonly FakeDef Rock = new FakeDef("ThingDef", "Rock", "rock");

    private static AuditObservation GemObs(int i) => Obs.Thing(new object(), Gem, stack: 1);

    private static AuditObservation RockObs(int i) => Obs.Thing(new object(), Rock, stack: 1);

    private static OriginResolution Resolve(AuditObservation o)
        => ReferenceEquals(o.Def, Gem) ? Obs.Owned(ModA) : Obs.Owned(ModKey.Parse("ludeon.rimworld"));

    private static AuditSession Make(params IScanSource[] sources)
        => new AuditSession(new[] { new AuditTarget(ModA, "A", "a.mod") }, sources, Resolve);

    private static void RunToEnd(AuditSession s, int budget = 300)
    {
        for (int guard = 0; s.State == ScanState.Scanning; guard++)
        {
            s.Advance(budget, 1000);
            Assert.True(guard < 100000);
        }
    }

    [Fact]
    public void AdvanceProcessesAtMostTheNodeBudget()
    {
        FakeSource src = new FakeSource("map").WithSteps(10, GemObs);
        AuditSession s = Make(src);
        s.Start();

        int calls = 0;
        while (s.State == ScanState.Scanning)
        {
            int before = src.StepCalls;
            s.Advance(3, 1000);
            Assert.True(src.StepCalls - before <= 3);
            calls++;
        }
        Assert.Equal(ScanState.Complete, s.State);
        Assert.Equal(4, calls);
        Assert.Equal(10, s.Report.Findings.Single().Instances);
    }

    [Fact]
    public void AdvanceStopsAtTheTimeBudget()
    {
        double now = 0;
        FakeSource src = new FakeSource("map").WithSteps(50, GemObs);
        AuditSession s = new AuditSession(new[] { new AuditTarget(ModA, "A", "a.mod") }, new[] { src }, Resolve,
            clockMs: () => now += 1.0);
        s.Start();

        s.Advance(300, 2.0);

        Assert.InRange(src.StepCalls, 1, 3);
        Assert.Equal(ScanState.Scanning, s.State);
    }

    [Fact]
    public void AlwaysMakesProgressEvenWithZeroBudget()
    {
        FakeSource src = new FakeSource("map").WithSteps(2, GemObs);
        AuditSession s = Make(src);
        s.Start();
        s.Advance(0, 0);
        Assert.Equal(1, src.StepCalls);
    }

    [Fact]
    public void CancelHaltsReleasesAndNeverReportsAllClear()
    {
        FakeSource src = new FakeSource("map").WithSteps(100, RockObs);
        AuditSession s = Make(src);
        s.Start();
        s.Advance(5, 1000);

        s.Cancel();
        int stepsAtCancel = src.StepCalls;
        s.Advance(300, 1000);

        Assert.Equal(ScanState.Cancelled, s.State);
        Assert.Equal(stepsAtCancel, src.StepCalls);
        Assert.True(src.Ended);
        Assert.Null(s.Report);
    }

    [Fact]
    public void ThrowingSourceIsMarkedFailedAndTheNextOneStillRuns()
    {
        FakeSource bad = new FakeSource("holders").WithSteps(5, RockObs);
        bad.ThrowOnStep = 2;
        FakeSource good = new FakeSource("terrain").WithSteps(3, RockObs);
        List<string> logged = new();
        AuditSession s = new AuditSession(new[] { new AuditTarget(ModA, "A", "a.mod") }, new IScanSource[] { bad, good }, Resolve,
            logError: logged.Add);
        s.Start();
        RunToEnd(s);

        Assert.Equal(ScanState.Complete, s.State);
        Assert.True(bad.Ended);
        Assert.True(good.Began && good.Ended);
        CoverageEntry c = s.Report.Coverage.Single(x => x.ScopeId == "holders");
        Assert.Equal(CoverageStatus.Failed, c.Status);
        Assert.Contains("step failed", c.Warning);
        Assert.Equal(CoverageStatus.Checked, s.Report.Coverage.Single(x => x.ScopeId == "terrain").Status);
        Assert.Equal(ModVerdict.Incomplete, s.Report.VerdictFor(ModA));
        Assert.Single(logged);
    }

    [Fact]
    public void ThrowingBeginIsFailedNotSkipped()
    {
        FakeSource bad = new FakeSource("world") { ThrowOnBegin = true };
        AuditSession s = Make(bad, new FakeSource("terrain").WithSteps(1, RockObs));
        s.Start();
        RunToEnd(s);

        Assert.Equal(CoverageStatus.Failed, s.Report.Coverage.Single(x => x.ScopeId == "world").Status);
        Assert.Equal(ModVerdict.Incomplete, s.Report.VerdictFor(ModA));
    }

    [Fact]
    public void CriticalFailureFailsTheWholeScan()
    {
        FakeSource provenance = new FakeSource("provenance", critical: true) { ThrowOnBegin = true };
        FakeSource map = new FakeSource("map").WithSteps(3, GemObs);
        AuditSession s = Make(provenance, map);
        s.Start();
        RunToEnd(s);

        Assert.Equal(ScanState.Failed, s.State);
        Assert.Null(s.Report);
        Assert.False(map.Began);
        Assert.Contains("begin failed", s.FailureMessage);
    }

    [Fact]
    public void PartialSourceMeansIncompleteNotClean()
    {
        FakeSource src = new FakeSource("holders").WithSteps(3, RockObs);
        src.FinalStatus = CoverageStatus.Partial;
        src.Warning = "depth limit";
        AuditSession s = Make(src);
        s.Start();
        RunToEnd(s);

        Assert.Equal(ModVerdict.Incomplete, s.Report.VerdictFor(ModA));
        Assert.Equal("depth limit", s.Report.Coverage.Single().Warning);
    }

    [Fact]
    public void CleanCompleteScanWithoutFindingsIsNoDetectedInstances()
    {
        AuditSession s = new AuditSession(new[] { new AuditTarget(ModA, "A", "a.mod") },
            new[] { new FakeSource("map").WithSteps(3, RockObs) }, Resolve,
            staticCoverage: new[] { new CoverageEntry("hediffs", CoverageStatus.NotCovered, 0, null) });
        s.Start();
        RunToEnd(s);

        Assert.Equal(ModVerdict.NoDetectedInstances, s.Report.VerdictFor(ModA));
        Assert.Contains(s.Report.Coverage, c => c.ScopeId == "hediffs" && c.Status == CoverageStatus.NotCovered);
    }

    [Fact]
    public void AbortCheckFailsTheScanWhenTheGameChanges()
    {
        string abort = null;
        FakeSource src = new FakeSource("map").WithSteps(10, GemObs);
        AuditSession s = new AuditSession(new[] { new AuditTarget(ModA, "A", "a.mod") }, new[] { src }, Resolve,
            abortCheck: () => abort);
        s.Start();
        s.Advance(2, 1000);
        abort = "game changed";
        s.Advance(2, 1000);

        Assert.Equal(ScanState.Failed, s.State);
        Assert.Equal("game changed", s.FailureMessage);
        Assert.Null(s.Report);
        Assert.True(src.Ended);
    }

    [Fact]
    public void ReportIsUnavailableUntilComplete()
    {
        AuditSession s = Make(new FakeSource("map").WithSteps(10, GemObs));
        Assert.Equal(ScanState.Idle, s.State);
        s.Start();
        s.Advance(2, 1000);
        Assert.Null(s.Report);
        Assert.Equal(ScanState.Scanning, s.Progress.State);
        Assert.Equal("map", s.Progress.CurrentScope);
    }

    [Fact]
    public void StartTwiceIsRejected()
    {
        AuditSession s = Make(new FakeSource("map"));
        s.Start();
        Assert.Throws<InvalidOperationException>(() => s.Start());
    }

    [Fact]
    public void ProgressCountsUnitsAcrossSources()
    {
        AuditSession s = Make(new FakeSource("a").WithSteps(4, RockObs), new FakeSource("b").WithSteps(6, RockObs));
        s.Start();
        RunToEnd(s);
        Assert.Equal(10, s.Report.UnitsProcessed);
        Assert.Equal(4, s.Report.Coverage[0].UnitsProcessed);
        Assert.Equal(6, s.Report.Coverage[1].UnitsProcessed);
    }
}
