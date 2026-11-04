using ModRemovalAuditor.Audit;

namespace ModRemovalAuditor.Tests;

public class ProvenanceTests
{
    private static readonly DefIdentity Sword = new DefIdentity("ThingDef", "MRA_Sword");

    [Fact]
    public void PackageIdsAreCaseInsensitiveAndTrimmed()
    {
        ModKey a = ModKey.Parse("  Author.CoolMod ");
        ModKey b = ModKey.Parse("author.coolmod");
        Assert.Equal(a, b);
        Assert.Equal("author.coolmod", a.Value);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyPackageIdIsRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => ModKey.Parse(value));
        Assert.False(ModKey.TryParse(value, out _));
    }

    [Theory]
    [InlineData("ludeon.rimworld", true)]
    [InlineData("Ludeon.RimWorld.Odyssey", true)]
    [InlineData("ludeon.rimworldx", false)]
    [InlineData("someone.ludeon.rimworld", false)]
    public void OfficialContentIsRecognised(string id, bool official)
    {
        Assert.Equal(official, ModKey.Parse(id).IsLudeonOfficial);
    }

    [Fact]
    public void DeclaredDefWithSingleDeclarerIsConfirmed()
    {
        DefOriginIndex index = new DefOriginIndex();
        object def = new object();
        ModKey mod = ModKey.Parse("a.mod");
        index.AddDeclaration(def, Sword, mod);

        OriginResolution r = index.Resolve(def, Sword, mod);

        Assert.Equal(OriginConfidence.Confirmed, r.Confidence);
        Assert.Equal(mod, r.Owner);
        Assert.Equal(OriginReason.Declared, r.Reason);
        Assert.Empty(r.AlsoDeclaredBy);
        Assert.True(r.IsRelevantTo(mod));
    }

    [Fact]
    public void SameLabelDifferentPackagesAreAttributedByDefObjectNotName()
    {
        // Two mods each define a "sword" (same label, different defNames) — ownership follows the def object.
        DefOriginIndex index = new DefOriginIndex();
        object swordA = new object();
        object swordB = new object();
        ModKey a = ModKey.Parse("a.mod");
        ModKey b = ModKey.Parse("b.mod");
        DefIdentity idA = new DefIdentity("ThingDef", "A_Sword");
        DefIdentity idB = new DefIdentity("ThingDef", "B_Sword");
        index.AddDeclaration(swordA, idA, a);
        index.AddDeclaration(swordB, idB, b);

        OriginResolution ra = index.Resolve(swordA, idA, a);
        OriginResolution rb = index.Resolve(swordB, idB, b);

        Assert.True(ra.IsRelevantTo(a));
        Assert.False(ra.IsRelevantTo(b));
        Assert.True(rb.IsRelevantTo(b));
        Assert.False(rb.IsRelevantTo(a));
    }

    [Fact]
    public void XmlPatchingModIsNotOwner()
    {
        // A patch operation edits the def's fields but never re-declares it, so the patcher has no
        // declaration and the live owner stays the declaring mod.
        DefOriginIndex index = new DefOriginIndex();
        object def = new object();
        ModKey owner = ModKey.Parse("owner.mod");
        ModKey patcher = ModKey.Parse("patcher.mod");
        index.AddDeclaration(def, Sword, owner);

        OriginResolution r = index.Resolve(def, Sword, owner);

        Assert.Equal(OriginConfidence.Confirmed, r.Confidence);
        Assert.False(r.IsRelevantTo(patcher));
    }

    [Fact]
    public void OverriddenDefIsUncertainForBothDeclarers()
    {
        // Mod B redefines Core's def with the same defName; B's object wins in the database,
        // Core's object stays in Core's AllDefs. Removing B would not remove the item.
        DefOriginIndex index = new DefOriginIndex();
        object coreDef = new object();
        object overrideDef = new object();
        ModKey core = ModKey.Parse("ludeon.rimworld");
        ModKey b = ModKey.Parse("b.mod");
        index.AddDeclaration(coreDef, Sword, core);
        index.AddDeclaration(overrideDef, Sword, b);

        OriginResolution r = index.Resolve(overrideDef, Sword, b);

        Assert.Equal(OriginConfidence.Uncertain, r.Confidence);
        Assert.Equal(OriginReason.Shadowed, r.Reason);
        Assert.Equal(b, r.Owner);
        Assert.Contains(core, r.AlsoDeclaredBy);
        Assert.True(r.IsRelevantTo(b));
        Assert.True(r.IsRelevantTo(core));
    }

    [Fact]
    public void GeneratedDefWithoutOwnerIsUnknown()
    {
        DefOriginIndex index = new DefOriginIndex();
        object runtimeDef = new object();

        OriginResolution r = index.Resolve(runtimeDef, Sword, null);

        Assert.Equal(OriginConfidence.Unknown, r.Confidence);
        Assert.Equal(OriginReason.NoOwner, r.Reason);
        Assert.Null(r.Owner);
        Assert.False(r.IsRelevantTo(ModKey.Parse("a.mod")));
    }

    [Fact]
    public void OwnerThatDoesNotListTheDefIsUncertain()
    {
        // A C# mod created a def at runtime and set modContentPack, but never registered it in AllDefs.
        DefOriginIndex index = new DefOriginIndex();
        object runtimeDef = new object();
        ModKey claimed = ModKey.Parse("claimed.mod");

        OriginResolution r = index.Resolve(runtimeDef, Sword, claimed);

        Assert.Equal(OriginConfidence.Uncertain, r.Confidence);
        Assert.Equal(OriginReason.NotInOwnerDefs, r.Reason);
        Assert.True(r.IsRelevantTo(claimed));
    }

    [Fact]
    public void DefListedByAnotherPackThanTheLiveOwnerIsUncertain()
    {
        DefOriginIndex index = new DefOriginIndex();
        object def = new object();
        ModKey listedIn = ModKey.Parse("listed.mod");
        ModKey claimed = ModKey.Parse("claimed.mod");
        index.AddDeclaration(def, Sword, listedIn);

        OriginResolution r = index.Resolve(def, Sword, claimed);

        Assert.Equal(OriginConfidence.Uncertain, r.Confidence);
        Assert.True(r.IsRelevantTo(claimed));
        Assert.True(r.IsRelevantTo(listedIn));
    }

    [Fact]
    public void DefIdentityEqualityIsOrdinalAndTypeAware()
    {
        Assert.Equal(new DefIdentity("ThingDef", "X"), new DefIdentity("ThingDef", "X"));
        Assert.NotEqual(new DefIdentity("ThingDef", "X"), new DefIdentity("TerrainDef", "X"));
        Assert.NotEqual(new DefIdentity("ThingDef", "X"), new DefIdentity("ThingDef", "x"));
    }
}
