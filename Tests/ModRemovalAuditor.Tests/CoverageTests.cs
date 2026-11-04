using ModRemovalAuditor.Audit;

namespace ModRemovalAuditor.Tests;

/// <summary>Verdict rules: absence of findings is never upgraded to a clean verdict unless every attempted scope finished.</summary>
public class CoverageTests
{
    private static readonly ModKey ModA = ModKey.Parse("a.mod");
    private static readonly ModKey ModB = ModKey.Parse("b.mod");
    private static readonly FakeDef Gem = new FakeDef("ThingDef", "A_Gem", "gem");

    private static AuditReport Report(ScanState state, IEnumerable<CoverageEntry> coverage, Action<AuditAccumulator> fill = null)
    {
        AuditAccumulator acc = new AuditAccumulator(new[] { ModA, ModB });
        fill?.Invoke(acc);
        return acc.BuildReport(state, new[] { new AuditTarget(ModA, "A", "a.mod"), new AuditTarget(ModB, "B", "b.mod") }, coverage.ToList(), 1);
    }

    [Theory]
    [InlineData(CoverageStatus.Partial)]
    [InlineData(CoverageStatus.Failed)]
    [InlineData(CoverageStatus.Cancelled)]
    [InlineData(CoverageStatus.Pending)]
    public void AnyNonCheckedAttemptedScopeMakesAbsenceIncomplete(CoverageStatus status)
    {
        AuditReport r = Report(ScanState.Complete, new[]
        {
            new CoverageEntry("maps", CoverageStatus.Checked, 1, null),
            new CoverageEntry("world", status, 1, null),
        });

        Assert.Equal(ModVerdict.Incomplete, r.VerdictFor(ModA));
        Assert.False(r.AllAttemptedScopesChecked);
    }

    [Theory]
    [InlineData(ScanState.Cancelled)]
    [InlineData(ScanState.Failed)]
    [InlineData(ScanState.Scanning)]
    public void NonCompleteScanNeverYieldsAllClear(ScanState state)
    {
        AuditReport r = Report(state, new[] { new CoverageEntry("maps", CoverageStatus.Checked, 1, null) });

        Assert.Equal(ModVerdict.Incomplete, r.VerdictFor(ModA));
    }

    [Fact]
    public void OneMatchingInstanceIsDetectedEvenIfOtherScopesFailed()
    {
        AuditReport r = Report(ScanState.Complete,
            new[] { new CoverageEntry("world", CoverageStatus.Failed, 1, "x") },
            acc => acc.Add(Obs.Thing(new object(), Gem), Obs.Owned(ModA), new FakeDescriber()));

        Assert.Equal(ModVerdict.Detected, r.VerdictFor(ModA));
        Assert.Equal(ModVerdict.Incomplete, r.VerdictFor(ModB));
    }

    [Fact]
    public void UnknownOriginIsNeitherPositiveNorDefinitiveNegative()
    {
        AuditReport r = Report(ScanState.Complete,
            new[] { new CoverageEntry("maps", CoverageStatus.Checked, 1, null) },
            acc => acc.Add(Obs.Thing(new object(), Gem), Obs.Unknown(), new FakeDescriber()));

        Assert.Empty(r.Findings);
        Assert.Equal(ModVerdict.Incomplete, r.VerdictFor(ModA));
        Assert.Equal(ModVerdict.Incomplete, r.VerdictFor(ModB));
        Assert.Equal(1, r.UnattributedInstances);
    }

    [Fact]
    public void NotCoveredScopesAreListedButDoNotBlockTheMiddleVerdict()
    {
        AuditReport r = Report(ScanState.Complete, new[]
        {
            new CoverageEntry("maps", CoverageStatus.Checked, 1, null),
            new CoverageEntry("pawndata", CoverageStatus.NotCovered, 0, null),
            new CoverageEntry("customdata", CoverageStatus.NotCovered, 0, null),
        });

        Assert.Equal(ModVerdict.NoDetectedInstances, r.VerdictFor(ModA));
        Assert.Equal(2, r.Coverage.Count(c => c.Status == CoverageStatus.NotCovered));
    }

    [Fact]
    public void ThereIsNoSafeToRemoveVerdict()
    {
        Assert.DoesNotContain(Enum.GetNames(typeof(ModVerdict)), n => n.Contains("Safe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OfficialContentIsNotOfferedAsTarget()
    {
        // The game-side list uses ModContentPack.IsCoreMod/IsOfficialMod plus this package-id check.
        Assert.True(ModKey.Parse("ludeon.rimworld").IsLudeonOfficial);
        Assert.True(ModKey.Parse("ludeon.rimworld.biotech").IsLudeonOfficial);
        Assert.False(ModKey.Parse("pengin0503.modremovalauditor").IsLudeonOfficial);
    }
}
