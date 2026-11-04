using ModRemovalAuditor.Audit;

namespace ModRemovalAuditor.Tests;

/// <summary>Context object used by tests: a group key and description per observation.</summary>
internal sealed record FakePlace(string Group, string Text);

internal sealed class FakeDescriber : IObservationDescriber
{
    public int LocationCalls { get; private set; }

    public AuditLocation DescribeLocation(in AuditObservation observation)
    {
        LocationCalls++;
        FakePlace place = observation.Context as FakePlace ?? new FakePlace("none", "none");
        return new AuditLocation(place.Group, place.Text, observation.Instance ?? place);
    }

    public string DescribeDef(object def) => (def as FakeDef)?.Label ?? "?";
}

internal sealed class FakeDef
{
    public FakeDef(string type, string name, string label)
    {
        Id = new DefIdentity(type, name);
        Label = label;
    }

    public DefIdentity Id { get; }

    public string Label { get; }
}

internal static class Obs
{
    public static AuditObservation Thing(object instance, FakeDef def, int stack = 1, AuditCategory category = AuditCategory.Items, FakePlace place = null)
        => new AuditObservation(instance, def, def.Id, category, 1, stack, place ?? new FakePlace("map:0", "Map 0"));

    public static AuditObservation Terrain(FakeDef def, int cells, FakePlace place)
        => new AuditObservation(null, def, def.Id, AuditCategory.Terrain, cells, 0, place);

    public static OriginResolution Owned(ModKey mod)
        => new OriginResolution(mod, OriginConfidence.Confirmed, OriginReason.Declared, null);

    public static OriginResolution Unknown()
        => new OriginResolution(null, OriginConfidence.Unknown, OriginReason.NoOwner, null);
}
