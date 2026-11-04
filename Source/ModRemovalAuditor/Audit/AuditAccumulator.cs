using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ModRemovalAuditor.Audit
{
    /// <summary>
    /// Single-pass aggregation for all selected mods at once. Only observations relevant to a selected
    /// mod (or of unknown origin) are de-duplicated, described and stored; everything else is discarded
    /// after a constant-time check, so memory stays proportional to what is actually reported.
    /// </summary>
    public sealed class AuditAccumulator
    {
        public const int DefaultMaxFindings = 4000;

        public const int DefaultMaxHandlesPerFinding = 50;

        /// <summary>Group key used when the finding limit is reached; counts are merged, never dropped.</summary>
        public const string OverflowGroupKey = "*overflow*";

        private readonly ModKey[] targets;

        private readonly int maxFindings;

        private readonly int maxHandles;

        private readonly HashSet<InstanceKey> seen = new HashSet<InstanceKey>();

        private readonly Dictionary<FindingKey, AuditFinding> findings = new Dictionary<FindingKey, AuditFinding>();

        private readonly Dictionary<DefIdentity, UnattributedEntry> unattributed = new Dictionary<DefIdentity, UnattributedEntry>();

        private readonly List<ModKey> relevantScratch = new List<ModKey>(2);

        private bool truncated;

        public AuditAccumulator(IReadOnlyList<ModKey> targets, int maxFindings = DefaultMaxFindings, int maxHandlesPerFinding = DefaultMaxHandlesPerFinding)
        {
            if (targets == null)
            {
                throw new ArgumentNullException(nameof(targets));
            }
            this.targets = new ModKey[targets.Count];
            for (int i = 0; i < targets.Count; i++)
            {
                this.targets[i] = targets[i];
            }
            this.maxFindings = Math.Max(1, maxFindings);
            maxHandles = Math.Max(0, maxHandlesPerFinding);
        }

        public long DuplicateObservations { get; private set; }

        public int TrackedInstanceCount => seen.Count;

        public int FindingCount => findings.Count;

        public void Add(in AuditObservation observation, in OriginResolution origin, IObservationDescriber describer)
        {
            if (origin.IsUnknown)
            {
                if (!FirstSighting(observation))
                {
                    return;
                }
                if (!unattributed.TryGetValue(observation.DefId, out UnattributedEntry entry))
                {
                    entry = new UnattributedEntry(observation.DefId, describer?.DescribeDef(observation.Def));
                    unattributed.Add(observation.DefId, entry);
                }
                entry.Instances += observation.InstanceCount;
                entry.Items += observation.ItemCount;
                return;
            }

            relevantScratch.Clear();
            for (int i = 0; i < targets.Length; i++)
            {
                if (origin.IsRelevantTo(targets[i]))
                {
                    relevantScratch.Add(targets[i]);
                }
            }
            if (relevantScratch.Count == 0)
            {
                return;
            }
            if (!FirstSighting(observation))
            {
                return;
            }

            AuditLocation location = describer?.DescribeLocation(observation) ?? new AuditLocation(string.Empty, string.Empty, null);
            string defLabel = null;
            for (int i = 0; i < relevantScratch.Count; i++)
            {
                ModKey target = relevantScratch[i];
                FindingKey key = new FindingKey(target, observation.Category, observation.DefId, location.GroupKey);
                if (!findings.TryGetValue(key, out AuditFinding finding))
                {
                    if (defLabel == null)
                    {
                        defLabel = describer?.DescribeDef(observation.Def) ?? observation.DefId.DefName;
                    }
                    if (findings.Count >= maxFindings)
                    {
                        truncated = true;
                        key = new FindingKey(target, observation.Category, observation.DefId, OverflowGroupKey);
                        if (!findings.TryGetValue(key, out finding))
                        {
                            finding = new AuditFinding(target, observation.Category, observation.DefId, defLabel, OverflowGroupKey, string.Empty);
                            findings.Add(key, finding);
                        }
                    }
                    else
                    {
                        finding = new AuditFinding(target, observation.Category, observation.DefId, defLabel, location.GroupKey, location.Description);
                        findings.Add(key, finding);
                    }
                }
                finding.Instances += observation.InstanceCount;
                finding.Items += observation.ItemCount;
                finding.AddHandle(location.JumpHandle, maxHandles);
                Merge(finding, origin, target);
            }
        }

        public AuditReport BuildReport(ScanState state, IReadOnlyList<AuditTarget> auditTargets, IReadOnlyList<CoverageEntry> coverage, long unitsProcessed)
        {
            List<AuditFinding> list = new List<AuditFinding>(findings.Values);
            list.Sort(CompareFindings);
            List<UnattributedEntry> unknown = new List<UnattributedEntry>(unattributed.Values);
            unknown.Sort((a, b) =>
            {
                int c = b.Instances.CompareTo(a.Instances);
                return c != 0 ? c : string.CompareOrdinal(a.DefId.ToString(), b.DefId.ToString());
            });
            return new AuditReport(DateTime.UtcNow, state, auditTargets, list, unknown, coverage, unitsProcessed, truncated);
        }

        /// <summary>Releases all transient buffers (used on cancel / window close).</summary>
        public void Clear()
        {
            seen.Clear();
            seen.TrimExcess();
            findings.Clear();
            unattributed.Clear();
        }

        private bool FirstSighting(in AuditObservation observation)
        {
            if (observation.Instance == null)
            {
                // Aggregates (terrain runs) are unique by construction.
                return true;
            }
            if (seen.Add(new InstanceKey(observation.Instance, observation.Category == AuditCategory.Material)))
            {
                return true;
            }
            DuplicateObservations++;
            return false;
        }

        private static void Merge(AuditFinding finding, in OriginResolution origin, ModKey target)
        {
            if (origin.Confidence > finding.Confidence)
            {
                finding.Confidence = origin.Confidence;
                finding.Reason = origin.Reason;
            }
            if (origin.Owner.HasValue && origin.Owner.Value != target && !finding.AlsoDeclaredBy.Contains(origin.Owner.Value))
            {
                finding.AlsoDeclaredBy.Add(origin.Owner.Value);
            }
            IReadOnlyList<ModKey> others = origin.AlsoDeclaredBy;
            for (int i = 0; i < others.Count; i++)
            {
                if (others[i] != target && !finding.AlsoDeclaredBy.Contains(others[i]))
                {
                    finding.AlsoDeclaredBy.Add(others[i]);
                }
            }
        }

        private static int CompareFindings(AuditFinding a, AuditFinding b)
        {
            int c = string.CompareOrdinal(a.Target.Value, b.Target.Value);
            if (c != 0)
            {
                return c;
            }
            c = a.Category.CompareTo(b.Category);
            if (c != 0)
            {
                return c;
            }
            c = string.Compare(a.DefLabel, b.DefLabel, StringComparison.OrdinalIgnoreCase);
            if (c != 0)
            {
                return c;
            }
            c = string.CompareOrdinal(a.DefId.DefName, b.DefId.DefName);
            if (c != 0)
            {
                return c;
            }
            c = string.CompareOrdinal(a.DefId.DefType, b.DefId.DefType);
            return c != 0 ? c : string.CompareOrdinal(a.GroupKey, b.GroupKey);
        }

        private readonly struct InstanceKey : IEquatable<InstanceKey>
        {
            private readonly object instance;

            private readonly bool materialRole;

            public InstanceKey(object instance, bool materialRole)
            {
                this.instance = instance;
                this.materialRole = materialRole;
            }

            public bool Equals(InstanceKey other) => ReferenceEquals(instance, other.instance) && materialRole == other.materialRole;

            public override bool Equals(object obj) => obj is InstanceKey other && Equals(other);

            public override int GetHashCode() => RuntimeHelpers.GetHashCode(instance) * 2 + (materialRole ? 1 : 0);
        }

        private readonly struct FindingKey : IEquatable<FindingKey>
        {
            private readonly ModKey target;

            private readonly AuditCategory category;

            private readonly DefIdentity def;

            private readonly string group;

            public FindingKey(ModKey target, AuditCategory category, DefIdentity def, string group)
            {
                this.target = target;
                this.category = category;
                this.def = def;
                this.group = group ?? string.Empty;
            }

            public bool Equals(FindingKey other) =>
                target == other.target && category == other.category && def.Equals(other.def) && string.Equals(group, other.group, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is FindingKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(target, (int)category, def, StringComparer.Ordinal.GetHashCode(group));
        }
    }
}
