using ModRemovalAuditor.Audit;

namespace ModRemovalAuditor.Tests;

public class AggregationTests
{
    private static readonly ModKey ModA = ModKey.Parse("a.mod");
    private static readonly ModKey ModB = ModKey.Parse("b.mod");
    private static readonly ModKey Other = ModKey.Parse("other.mod");
    private static readonly FakeDef Gem = new FakeDef("ThingDef", "A_Gem", "gem");
    private static readonly FakeDef Floor = new FakeDef("TerrainDef", "A_Floor", "fancy floor");

    private static AuditReport Build(AuditAccumulator acc, params ModKey[] targets)
        => acc.BuildReport(ScanState.Complete, targets.Select(t => new AuditTarget(t, t.Value, t.Value)).ToList(),
            new[] { new CoverageEntry("map", CoverageStatus.Checked, 1, null) }, 1);

    [Fact]
    public void SameThingObservedTwiceIsCountedOnce()
    {
        AuditAccumulator acc = new AuditAccumulator(new[] { ModA });
        FakeDescriber d = new FakeDescriber();
        object thing = new object();

        acc.Add(Obs.Thing(thing, Gem, stack: 5), Obs.Owned(ModA), d);
        acc.Add(Obs.Thing(thing, Gem, stack: 5, category: AuditCategory.ContainedEquipped), Obs.Owned(ModA), d);

        AuditFinding f = Assert.Single(Build(acc, ModA).Findings);
        Assert.Equal(1, f.Instances);
        Assert.Equal(5, f.Items);
        Assert.Equal(AuditCategory.Items, f.Category);
        Assert.Equal(1, acc.DuplicateObservations);
    }

    [Fact]
    public void StackCountIsSummedAsItems()
    {
        AuditAccumulator acc = new AuditAccumulator(new[] { ModA });
        FakeDescriber d = new FakeDescriber();

        acc.Add(Obs.Thing(new object(), Gem, stack: 12), Obs.Owned(ModA), d);

        AuditFinding f = Assert.Single(Build(acc, ModA).Findings);
        Assert.Equal(1, f.Instances);
        Assert.Equal(12, f.Items);
    }

    [Fact]
    public void TwoStacksOfSameDefAreTwoInstancesWithSummedItems()
    {
        AuditAccumulator acc = new AuditAccumulator(new[] { ModA });
        FakeDescriber d = new FakeDescriber();

        acc.Add(Obs.Thing(new object(), Gem, stack: 12), Obs.Owned(ModA), d);
        acc.Add(Obs.Thing(new object(), Gem, stack: 30), Obs.Owned(ModA), d);

        AuditFinding f = Assert.Single(Build(acc, ModA).Findings);
        Assert.Equal(2, f.Instances);
        Assert.Equal(42, f.Items);
        Assert.Equal(2, f.JumpHandles.Count);
    }

    [Fact]
    public void DifferentMapsAndHoldersAreKeptSeparate()
    {
        AuditAccumulator acc = new AuditAccumulator(new[] { ModA });
        FakeDescriber d = new FakeDescriber();

        acc.Add(Obs.Thing(new object(), Gem, place: new FakePlace("map:1", "Map 1")), Obs.Owned(ModA), d);
        acc.Add(Obs.Thing(new object(), Gem, place: new FakePlace("map:2", "Map 2")), Obs.Owned(ModA), d);
        acc.Add(Obs.Thing(new object(), Gem, category: AuditCategory.ContainedEquipped, place: new FakePlace("holder:Pawn1", "Bob")), Obs.Owned(ModA), d);
        acc.Add(Obs.Thing(new object(), Gem, category: AuditCategory.ContainedEquipped, place: new FakePlace("holder:Pawn2", "Ann")), Obs.Owned(ModA), d);

        AuditReport r = Build(acc, ModA);
        Assert.Equal(4, r.Findings.Count);
        Assert.All(r.Findings, f => Assert.Equal(1, f.Instances));
    }

    [Fact]
    public void ManyTerrainCellsAggregateIntoOneFinding()
    {
        AuditAccumulator acc = new AuditAccumulator(new[] { ModA });
        FakeDescriber d = new FakeDescriber();
        FakePlace place = new FakePlace("map:1:top", "Map 1");

        // A source emits per-row aggregates; 250 rows x 250 cells.
        for (int row = 0; row < 250; row++)
        {
            acc.Add(Obs.Terrain(Floor, 250, place), Obs.Owned(ModA), d);
        }

        AuditFinding f = Assert.Single(Build(acc, ModA).Findings);
        Assert.Equal(62500, f.Instances);
        Assert.Equal(AuditCategory.Terrain, f.Category);
        Assert.True(f.JumpHandles.Count <= 50);
        Assert.True(d.LocationCalls <= 250);
    }

    [Fact]
    public void IrrelevantObservationsAreNotDescribedOrStored()
    {
        AuditAccumulator acc = new AuditAccumulator(new[] { ModA });
        FakeDescriber d = new FakeDescriber();

        for (int i = 0; i < 1000; i++)
        {
            acc.Add(Obs.Thing(new object(), Gem), Obs.Owned(Other), d);
        }

        Assert.Empty(Build(acc, ModA).Findings);
        Assert.Equal(0, d.LocationCalls);
        Assert.Equal(0, acc.TrackedInstanceCount);
    }

    [Fact]
    public void OnePassServesManyTargets()
    {
        AuditAccumulator acc = new AuditAccumulator(new[] { ModA, ModB });
        FakeDescriber d = new FakeDescriber();
        FakeDef bThing = new FakeDef("ThingDef", "B_Chair", "chair");

        acc.Add(Obs.Thing(new object(), Gem), Obs.Owned(ModA), d);
        acc.Add(Obs.Thing(new object(), bThing, category: AuditCategory.Buildings), Obs.Owned(ModB), d);

        AuditReport r = Build(acc, ModA, ModB);
        Assert.Single(r.Findings, f => f.Target == ModA);
        Assert.Single(r.Findings, f => f.Target == ModB);
        Assert.Equal(ModVerdict.Detected, r.VerdictFor(ModA));
        Assert.Equal(ModVerdict.Detected, r.VerdictFor(ModB));
    }

    [Fact]
    public void ShadowedDefIsReportedForEveryRelevantTargetAsUncertain()
    {
        AuditAccumulator acc = new AuditAccumulator(new[] { ModA, ModB });
        FakeDescriber d = new FakeDescriber();
        OriginResolution shadowed = new OriginResolution(ModB, OriginConfidence.Uncertain, OriginReason.Shadowed, new[] { ModA });

        acc.Add(Obs.Thing(new object(), Gem), shadowed, d);

        AuditReport r = Build(acc, ModA, ModB);
        Assert.Equal(2, r.Findings.Count);
        Assert.All(r.Findings, f => Assert.Equal(OriginConfidence.Uncertain, f.Confidence));
        Assert.Contains(ModA, r.Findings.Single(f => f.Target == ModB).AlsoDeclaredBy);
    }

    [Fact]
    public void UnknownOriginIsCollectedSeparatelyAndDeduplicated()
    {
        AuditAccumulator acc = new AuditAccumulator(new[] { ModA });
        FakeDescriber d = new FakeDescriber();
        FakeDef runtime = new FakeDef("ThingDef", "Runtime_1", "runtime thing");
        object thing = new object();

        acc.Add(Obs.Thing(thing, runtime, stack: 3), Obs.Unknown(), d);
        acc.Add(Obs.Thing(thing, runtime, stack: 3), Obs.Unknown(), d);
        acc.Add(Obs.Thing(new object(), runtime, stack: 1), Obs.Unknown(), d);

        AuditReport r = Build(acc, ModA);
        Assert.Empty(r.Findings);
        UnattributedEntry u = Assert.Single(r.Unattributed);
        Assert.Equal(2, u.Instances);
        Assert.Equal(4, u.Items);
        Assert.Equal(ModVerdict.Incomplete, r.VerdictFor(ModA));
    }

    [Fact]
    public void MaterialRoleIsDeduplicatedIndependentlyOfTheThingDef()
    {
        AuditAccumulator acc = new AuditAccumulator(new[] { ModA });
        FakeDescriber d = new FakeDescriber();
        FakeDef wall = new FakeDef("ThingDef", "Wall", "wall");
        FakeDef stuff = new FakeDef("ThingDef", "A_Wood", "fancy wood");
        object thing = new object();

        acc.Add(new AuditObservation(thing, wall, wall.Id, AuditCategory.Buildings, 1, 1, null), Obs.Owned(Other), d);
        acc.Add(new AuditObservation(thing, stuff, stuff.Id, AuditCategory.Material, 1, 1, null), Obs.Owned(ModA), d);
        acc.Add(new AuditObservation(thing, stuff, stuff.Id, AuditCategory.Material, 1, 1, null), Obs.Owned(ModA), d);

        AuditFinding f = Assert.Single(Build(acc, ModA).Findings);
        Assert.Equal(AuditCategory.Material, f.Category);
        Assert.Equal(1, f.Instances);
    }

    [Fact]
    public void FindingLimitMergesFurtherLocationsInsteadOfDroppingCounts()
    {
        AuditAccumulator acc = new AuditAccumulator(new[] { ModA }, maxFindings: 3);
        FakeDescriber d = new FakeDescriber();

        for (int i = 0; i < 10; i++)
        {
            acc.Add(Obs.Thing(new object(), Gem, stack: 2, place: new FakePlace("holder:" + i, "H" + i)), Obs.Owned(ModA), d);
        }

        AuditReport r = Build(acc, ModA);
        Assert.True(r.FindingsTruncated);
        Assert.Equal(10, r.Findings.Sum(f => f.Instances));
        Assert.Equal(20, r.Findings.Sum(f => f.Items));
        Assert.True(r.Findings.Count <= 4);
    }

    [Fact]
    public void NoFindingsWithAllScopesCheckedIsNotDetectedButNeverMoreThanThat()
    {
        AuditAccumulator acc = new AuditAccumulator(new[] { ModA });
        AuditReport r = acc.BuildReport(ScanState.Complete, new[] { new AuditTarget(ModA, "A", "a.mod") },
            new[]
            {
                new CoverageEntry("map", CoverageStatus.Checked, 10, null),
                new CoverageEntry("hediffs", CoverageStatus.NotCovered, 0, null),
            }, 10);

        Assert.Equal(ModVerdict.NoDetectedInstances, r.VerdictFor(ModA));
    }

    [Fact]
    public void FindingsAreSortedDeterministically()
    {
        AuditAccumulator acc = new AuditAccumulator(new[] { ModB, ModA });
        FakeDescriber d = new FakeDescriber();
        FakeDef z = new FakeDef("ThingDef", "Z", "zeta");
        FakeDef a = new FakeDef("ThingDef", "A", "alpha");

        acc.Add(Obs.Thing(new object(), z, category: AuditCategory.Buildings), Obs.Owned(ModB), d);
        acc.Add(Obs.Thing(new object(), z), Obs.Owned(ModA), d);
        acc.Add(Obs.Thing(new object(), a), Obs.Owned(ModA), d);

        AuditReport r = Build(acc, ModB, ModA);
        Assert.Equal(new[] { "A", "Z", "Z" }, r.Findings.Select(f => f.DefId.DefName).ToArray());
        Assert.Equal(ModA, r.Findings[0].Target);
        Assert.Equal(ModB, r.Findings[2].Target);
    }
}
