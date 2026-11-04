using System;
using System.Collections.Generic;

namespace ModRemovalAuditor.Audit
{
    /// <summary>
    /// Incremental, cycle-safe depth-first walk over a holder graph (IThingHolder in game).
    /// Each <see cref="Step"/> performs exactly one unit of work: either emits one held item or
    /// expands one holder. A failing holder is isolated and counted instead of aborting the walk.
    /// </summary>
    public sealed class HolderTraversal<THolder, TItem>
        where THolder : class
        where TItem : class
    {
        public readonly struct Emitted
        {
            public Emitted(TItem item, THolder holder, int depth)
            {
                Item = item;
                Holder = holder;
                Depth = depth;
            }

            public TItem Item { get; }

            /// <summary>The holder whose contents included the item; null for directly queued root items.</summary>
            public THolder Holder { get; }

            public int Depth { get; }
        }

        private readonly Action<THolder, List<TItem>> getItems;

        private readonly Action<THolder, List<THolder>> getChildren;

        private readonly HashSet<THolder> visited;

        private readonly int maxDepth;

        private readonly Stack<(THolder holder, int depth)> stack = new Stack<(THolder, int)>();

        private readonly Queue<Emitted> pending = new Queue<Emitted>();

        private readonly List<TItem> itemBuffer = new List<TItem>();

        private readonly List<THolder> childBuffer = new List<THolder>();

        public HolderTraversal(Action<THolder, List<TItem>> getItems, Action<THolder, List<THolder>> getChildren, HashSet<THolder> visited, int maxDepth = 64)
        {
            this.getItems = getItems ?? throw new ArgumentNullException(nameof(getItems));
            this.getChildren = getChildren ?? throw new ArgumentNullException(nameof(getChildren));
            this.visited = visited ?? throw new ArgumentNullException(nameof(visited));
            this.maxDepth = Math.Max(0, maxDepth);
        }

        public int VisitedHolders { get; private set; }

        public int FailedHolders { get; private set; }

        public Exception FirstError { get; private set; }

        public int DepthLimitHits { get; private set; }

        public bool HasWork => pending.Count > 0 || stack.Count > 0;

        public void AddRoot(THolder root)
        {
            if (root != null)
            {
                stack.Push((root, 0));
            }
        }

        /// <summary>
        /// Adds a holder found on an emitted item (e.g. a container thing whose parent did not report it
        /// as a child holder). Returns false if it was already visited or exceeds the depth limit.
        /// </summary>
        public bool AddHolder(THolder holder, int depth)
        {
            if (holder == null || visited.Contains(holder))
            {
                return false;
            }
            if (depth > maxDepth)
            {
                DepthLimitHits++;
                return false;
            }
            stack.Push((holder, depth));
            return true;
        }

        /// <summary>Queues an item that is not inside any traversed holder (e.g. a world pawn itself).</summary>
        public void EnqueueItem(TItem item, THolder holder)
        {
            if (item != null)
            {
                pending.Enqueue(new Emitted(item, holder, 0));
            }
        }

        /// <summary>Does one unit of work. Returns true while more work remains.</summary>
        public bool Step(List<Emitted> output)
        {
            if (pending.Count > 0)
            {
                output.Add(pending.Dequeue());
                return HasWork;
            }
            while (stack.Count > 0)
            {
                (THolder holder, int depth) = stack.Pop();
                if (!visited.Add(holder))
                {
                    // Already expanded via another path: skipping costs no real work, keep looking.
                    continue;
                }
                VisitedHolders++;
                Expand(holder, depth);
                return HasWork;
            }
            return HasWork;
        }

        public void Clear()
        {
            stack.Clear();
            pending.Clear();
            itemBuffer.Clear();
            childBuffer.Clear();
        }

        private void Expand(THolder holder, int depth)
        {
            itemBuffer.Clear();
            childBuffer.Clear();
            try
            {
                getItems(holder, itemBuffer);
                getChildren(holder, childBuffer);
            }
            catch (Exception ex)
            {
                FailedHolders++;
                if (FirstError == null)
                {
                    FirstError = ex;
                }
                itemBuffer.Clear();
                childBuffer.Clear();
                return;
            }
            for (int i = 0; i < itemBuffer.Count; i++)
            {
                if (itemBuffer[i] != null)
                {
                    pending.Enqueue(new Emitted(itemBuffer[i], holder, depth));
                }
            }
            // Push in reverse so children are expanded in their natural order.
            for (int i = childBuffer.Count - 1; i >= 0; i--)
            {
                THolder child = childBuffer[i];
                if (child == null || visited.Contains(child))
                {
                    continue;
                }
                if (depth + 1 > maxDepth)
                {
                    DepthLimitHits++;
                    continue;
                }
                stack.Push((child, depth + 1));
            }
            itemBuffer.Clear();
            childBuffer.Clear();
        }
    }
}
