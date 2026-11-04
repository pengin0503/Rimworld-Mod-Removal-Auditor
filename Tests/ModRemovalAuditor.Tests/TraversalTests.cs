using ModRemovalAuditor.Audit;

namespace ModRemovalAuditor.Tests;

/// <summary>Fake holder graph standing in for IThingHolder (maps, pawns, trackers, containers).</summary>
internal sealed class Node
{
    public Node(string name) => Name = name;

    public string Name { get; }

    public List<string> Items { get; } = new List<string>();

    public List<Node> Children { get; } = new List<Node>();

    public bool ThrowOnItems { get; set; }

    public bool NullItems { get; set; }

    public override string ToString() => Name;
}

public class TraversalTests
{
    private static HolderTraversal<Node, string> Make(HashSet<Node> visited = null, int maxDepth = 64)
        => new HolderTraversal<Node, string>(
            (h, list) =>
            {
                if (h.ThrowOnItems)
                {
                    throw new InvalidOperationException("broken holder " + h.Name);
                }
                if (!h.NullItems)
                {
                    list.AddRange(h.Items);
                }
            },
            (h, list) => list.AddRange(h.Children),
            visited ?? new HashSet<Node>(ReferenceEqualityComparer.Instance),
            maxDepth);

    private static List<(string item, string holder)> RunAll(HolderTraversal<Node, string> t, out int steps)
    {
        List<HolderTraversal<Node, string>.Emitted> buffer = new();
        List<(string, string)> all = new();
        steps = 0;
        bool more = true;
        while (more)
        {
            buffer.Clear();
            more = t.Step(buffer);
            steps++;
            all.AddRange(buffer.Select(e => (e.Item, e.Holder.Name)));
            Assert.True(steps < 10000, "traversal did not terminate");
        }
        return all;
    }

    [Fact]
    public void NestedInventoryApparelAndEquipmentAreAllReached()
    {
        Node map = new Node("map");
        Node pawn = new Node("pawn");
        Node inventory = new Node("inventory") { Items = { "gem", "meal" } };
        Node apparel = new Node("apparel") { Items = { "vest" } };
        Node equipment = new Node("equipment") { Items = { "rifle" } };
        Node backpack = new Node("backpack") { Items = { "inner-gem" } };
        map.Children.Add(pawn);
        pawn.Children.AddRange(new[] { inventory, apparel, equipment });
        inventory.Children.Add(backpack);

        HolderTraversal<Node, string> t = Make();
        t.AddRoot(map);
        List<(string item, string holder)> all = RunAll(t, out _);

        Assert.Equal(new[] { "gem", "inner-gem", "meal", "rifle", "vest" }, all.Select(x => x.item).OrderBy(x => x).ToArray());
        Assert.Contains(("inner-gem", "backpack"), all);
        Assert.Equal(0, t.FailedHolders);
    }

    [Fact]
    public void CyclesAndDoubleLinksTerminateAndVisitEachHolderOnce()
    {
        Node a = new Node("a") { Items = { "x" } };
        Node b = new Node("b") { Items = { "y" } };
        a.Children.Add(b);
        b.Children.Add(a);           // cycle
        a.Children.Add(b);           // double link

        HolderTraversal<Node, string> t = Make();
        t.AddRoot(a);
        t.AddRoot(b);
        List<(string item, string holder)> all = RunAll(t, out _);

        Assert.Equal(2, all.Count);
        Assert.Equal(2, t.VisitedHolders);
    }

    [Fact]
    public void SharedVisitedSetPreventsSecondTraversalAcrossScopes()
    {
        HashSet<Node> visited = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        Node caravan = new Node("caravan") { Items = { "silver" } };
        Node map = new Node("map");
        map.Children.Add(caravan);

        HolderTraversal<Node, string> first = Make(visited);
        first.AddRoot(map);
        Assert.Single(RunAll(first, out _));

        HolderTraversal<Node, string> second = Make(visited);
        second.AddRoot(caravan);
        Assert.Empty(RunAll(second, out _));
    }

    [Fact]
    public void TwoMapsAreBothTraversed()
    {
        Node map1 = new Node("map1") { Items = { "a" } };
        Node map2 = new Node("map2") { Items = { "b" } };
        HolderTraversal<Node, string> t = Make();
        t.AddRoot(map1);
        t.AddRoot(map2);

        Assert.Equal(new[] { "a", "b" }, RunAll(t, out _).Select(x => x.item).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void MissingOwnerOrNullContentsAreTolerated()
    {
        Node empty = new Node("empty") { NullItems = true };
        Node child = new Node("child") { Items = { "z" } };
        empty.Children.Add(child);
        HolderTraversal<Node, string> t = Make();
        t.AddRoot(empty);
        t.AddRoot(null);

        Assert.Single(RunAll(t, out _));
    }

    [Fact]
    public void BrokenHolderIsIsolatedAndCounted()
    {
        Node root = new Node("root");
        Node broken = new Node("broken") { ThrowOnItems = true, Items = { "lost" } };
        Node fine = new Node("fine") { Items = { "ok" } };
        root.Children.Add(broken);
        root.Children.Add(fine);

        HolderTraversal<Node, string> t = Make();
        t.AddRoot(root);
        List<(string item, string holder)> all = RunAll(t, out _);

        Assert.Equal(new[] { "ok" }, all.Select(x => x.item).ToArray());
        Assert.Equal(1, t.FailedHolders);
        Assert.Contains("broken holder", t.FirstError.Message);
    }

    [Fact]
    public void DepthLimitIsReportedNotSilentlyIgnored()
    {
        Node root = new Node("d0");
        Node cur = root;
        for (int i = 1; i <= 10; i++)
        {
            Node next = new Node("d" + i) { Items = { "i" + i } };
            cur.Children.Add(next);
            cur = next;
        }

        HolderTraversal<Node, string> t = Make(maxDepth: 4);
        t.AddRoot(root);
        List<(string item, string holder)> all = RunAll(t, out _);

        Assert.Equal(4, all.Count);
        Assert.True(t.DepthLimitHits > 0);
    }

    [Fact]
    public void EachStepIsBoundedToOneUnitOfWork()
    {
        Node big = new Node("storage");
        for (int i = 0; i < 100; i++)
        {
            big.Items.Add("item" + i);
        }
        HolderTraversal<Node, string> t = Make();
        t.AddRoot(big);
        List<HolderTraversal<Node, string>.Emitted> buffer = new();

        bool more = true;
        int maxPerStep = 0;
        while (more)
        {
            buffer.Clear();
            more = t.Step(buffer);
            maxPerStep = Math.Max(maxPerStep, buffer.Count);
        }
        Assert.Equal(1, maxPerStep);
    }

    [Fact]
    public void QueuedRootItemsAreEmittedWithTheirHolder()
    {
        Node pawn = new Node("worldpawn") { Items = { "crown" } };
        HolderTraversal<Node, string> t = Make();
        t.EnqueueItem("pawn-itself", null);
        t.AddRoot(pawn);

        List<(string item, string holder)> all = new();
        List<HolderTraversal<Node, string>.Emitted> buffer = new();
        bool more = true;
        while (more)
        {
            buffer.Clear();
            more = t.Step(buffer);
            all.AddRange(buffer.Select(e => (e.Item, e.Holder?.Name)));
        }
        Assert.Contains(("pawn-itself", null), all);
        Assert.Contains(("crown", "worldpawn"), all);
    }

    [Fact]
    public void HoldersDiscoveredFromItemsAreExpandedOnceAndRespectDepth()
    {
        // A modded holder that forgets to report a child holder: the adapter adds it explicitly.
        Node shelf = new Node("shelf") { Items = { "crate" } };
        Node crate = new Node("crate-holder") { Items = { "gem" } };
        HolderTraversal<Node, string> t = Make(maxDepth: 1);
        t.AddRoot(shelf);
        List<HolderTraversal<Node, string>.Emitted> buffer = new();
        List<string> items = new();
        bool more = true;
        while (more)
        {
            buffer.Clear();
            more = t.Step(buffer);
            foreach (var e in buffer)
            {
                items.Add(e.Item);
                if (e.Item == "crate")
                {
                    more |= t.AddHolder(crate, e.Depth + 1);
                }
            }
        }
        Assert.Equal(new[] { "crate", "gem" }, items.ToArray());
        Assert.False(t.AddHolder(crate, 0)); // already visited
        Assert.False(t.AddHolder(new Node("too-deep"), 2));
        Assert.Equal(1, t.DepthLimitHits);
    }
}
