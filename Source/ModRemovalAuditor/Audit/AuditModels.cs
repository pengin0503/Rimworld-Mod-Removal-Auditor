using System;
using System.Collections.Generic;

namespace ModRemovalAuditor.Audit
{
    public enum AuditCategory
    {
        Items,
        Buildings,
        Terrain,
        ContainedEquipped,
        WorldCaravan,
        Material,
        Other,
    }

    public enum CoverageStatus
    {
        Pending,
        Checked,
        Partial,
        Failed,
        Cancelled,

        /// <summary>Out of scope for this version; listed so absence of findings is never read as "safe".</summary>
        NotCovered,
    }

    public enum ScanState
    {
        Idle,
        Scanning,
        Complete,
        Cancelled,

        /// <summary>A critical stage failed or the game changed underneath the scan; no report.</summary>
        Failed,
    }

    public enum ModVerdict
    {
        Detected,
        NoDetectedInstances,
        Incomplete,
    }

    /// <summary>
    /// One thing (or one aggregated run of terrain cells) seen by a scan source. A struct to avoid
    /// per-thing heap allocations; strings and location are only produced for relevant observations.
    /// </summary>
    public readonly struct AuditObservation
    {
        public AuditObservation(object instance, object def, DefIdentity defId, AuditCategory category, int instanceCount, int itemCount, object context)
        {
            Instance = instance;
            Def = def;
            DefId = defId;
            Category = category;
            InstanceCount = instanceCount;
            ItemCount = itemCount;
            Context = context;
        }

        /// <summary>Object identity used for de-duplication (reference equality). Null for aggregates such as terrain.</summary>
        public object Instance { get; }

        /// <summary>Runtime def object used for origin resolution.</summary>
        public object Def { get; }

        public DefIdentity DefId { get; }

        public AuditCategory Category { get; }

        /// <summary>Number of objects (1 for a thing, number of cells for terrain).</summary>
        public int InstanceCount { get; }

        /// <summary>Stack-count based item quantity (0 when not applicable).</summary>
        public int ItemCount { get; }

        /// <summary>Opaque context the describing source uses to produce a location lazily.</summary>
        public object Context { get; }
    }

    /// <summary>Where a finding is. Built only for observations that belong to a selected mod.</summary>
    public sealed class AuditLocation
    {
        public AuditLocation(string groupKey, string description, object jumpHandle)
        {
            GroupKey = groupKey ?? string.Empty;
            Description = description ?? string.Empty;
            JumpHandle = jumpHandle;
        }

        /// <summary>Stable grouping key: findings of the same def are kept separate per map / holder.</summary>
        public string GroupKey { get; }

        /// <summary>Player-facing map / holder / cell text.</summary>
        public string Description { get; }

        /// <summary>Opaque, weakly-referencing navigation handle; null when the location cannot be navigated to.</summary>
        public object JumpHandle { get; }
    }

    /// <summary>Produces player-facing text for observations. Implemented by game-facing sources.</summary>
    public interface IObservationDescriber
    {
        AuditLocation DescribeLocation(in AuditObservation observation);

        string DescribeDef(object def);
    }

    /// <summary>A mod selected for auditing, with metadata gathered before the scan.</summary>
    public sealed class AuditTarget
    {
        public AuditTarget(ModKey key, string displayName, string packageIdPlayerFacing)
        {
            Key = key;
            DisplayName = string.IsNullOrEmpty(displayName) ? key.Value : displayName;
            PackageIdPlayerFacing = string.IsNullOrEmpty(packageIdPlayerFacing) ? key.Value : packageIdPlayerFacing;
        }

        public ModKey Key { get; }

        public string DisplayName { get; }

        public string PackageIdPlayerFacing { get; }

        /// <summary>Number of defs this mod declares, by def type name.</summary>
        public Dictionary<string, int> DeclaredDefCounts { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Def types this version actually scans for (e.g. ThingDef, TerrainDef).</summary>
        public HashSet<string> ScannedDefTypes { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>True if the mod loads its own assemblies (custom C# data may be saved and is not inspected).</summary>
        public bool HasAssemblies { get; set; }

        /// <summary>Display names of other active mods that list this mod as a dependency (metadata only).</summary>
        public List<string> DependentMods { get; } = new List<string>();

        public int UnscannedDefCount
        {
            get
            {
                int n = 0;
                foreach (KeyValuePair<string, int> kv in DeclaredDefCounts)
                {
                    if (!ScannedDefTypes.Contains(kv.Key))
                    {
                        n += kv.Value;
                    }
                }
                return n;
            }
        }
    }

    public sealed class CoverageEntry
    {
        public CoverageEntry(string scopeId, CoverageStatus status, long unitsProcessed, string warning)
        {
            ScopeId = scopeId;
            Status = status;
            UnitsProcessed = unitsProcessed;
            Warning = warning ?? string.Empty;
        }

        public string ScopeId { get; }

        public CoverageStatus Status { get; }

        public long UnitsProcessed { get; }

        public string Warning { get; }
    }

    /// <summary>Aggregated content of one def from one selected mod, in one category and location group.</summary>
    public sealed class AuditFinding
    {
        private readonly List<object> jumpHandles = new List<object>();

        public AuditFinding(ModKey target, AuditCategory category, DefIdentity defId, string defLabel, string groupKey, string locationText)
        {
            Target = target;
            Category = category;
            DefId = defId;
            DefLabel = string.IsNullOrEmpty(defLabel) ? defId.DefName : defLabel;
            GroupKey = groupKey ?? string.Empty;
            LocationText = locationText ?? string.Empty;
            Confidence = OriginConfidence.Confirmed;
        }

        public ModKey Target { get; }

        public AuditCategory Category { get; }

        public DefIdentity DefId { get; }

        public string DefLabel { get; }

        public string GroupKey { get; }

        public string LocationText { get; internal set; }

        public long Instances { get; internal set; }

        public long Items { get; internal set; }

        public OriginConfidence Confidence { get; internal set; }

        public OriginReason Reason { get; internal set; } = OriginReason.Declared;

        /// <summary>Other packs that also declare this def (shadowing), for display.</summary>
        public List<ModKey> AlsoDeclaredBy { get; } = new List<ModKey>();

        /// <summary>Bounded list of navigation handles (one per sampled instance).</summary>
        public IReadOnlyList<object> JumpHandles => jumpHandles;

        /// <summary>True when more instances exist than handles were kept.</summary>
        public bool HandlesTruncated { get; internal set; }

        internal void AddHandle(object handle, int max)
        {
            if (handle == null)
            {
                return;
            }
            if (jumpHandles.Count < max)
            {
                jumpHandles.Add(handle);
            }
            else
            {
                HandlesTruncated = true;
            }
        }
    }

    /// <summary>Instances whose def has no owning content pack. They might belong to any mod, including a selected one.</summary>
    public sealed class UnattributedEntry
    {
        public UnattributedEntry(DefIdentity defId, string defLabel)
        {
            DefId = defId;
            DefLabel = string.IsNullOrEmpty(defLabel) ? defId.DefName : defLabel;
        }

        public DefIdentity DefId { get; }

        public string DefLabel { get; }

        public long Instances { get; internal set; }

        public long Items { get; internal set; }
    }

    public sealed class AuditReport
    {
        public AuditReport(
            DateTime createdUtc,
            ScanState state,
            IReadOnlyList<AuditTarget> targets,
            IReadOnlyList<AuditFinding> findings,
            IReadOnlyList<UnattributedEntry> unattributed,
            IReadOnlyList<CoverageEntry> coverage,
            long unitsProcessed,
            bool findingsTruncated)
        {
            CreatedUtc = createdUtc;
            State = state;
            Targets = targets ?? Array.Empty<AuditTarget>();
            Findings = findings ?? Array.Empty<AuditFinding>();
            Unattributed = unattributed ?? Array.Empty<UnattributedEntry>();
            Coverage = coverage ?? Array.Empty<CoverageEntry>();
            UnitsProcessed = unitsProcessed;
            FindingsTruncated = findingsTruncated;
        }

        public DateTime CreatedUtc { get; }

        public ScanState State { get; }

        public IReadOnlyList<AuditTarget> Targets { get; }

        public IReadOnlyList<AuditFinding> Findings { get; }

        public IReadOnlyList<UnattributedEntry> Unattributed { get; }

        public IReadOnlyList<CoverageEntry> Coverage { get; }

        public long UnitsProcessed { get; }

        /// <summary>True if the per-report finding limit was hit and further locations were merged.</summary>
        public bool FindingsTruncated { get; }

        public long UnattributedInstances
        {
            get
            {
                long n = 0;
                for (int i = 0; i < Unattributed.Count; i++)
                {
                    n += Unattributed[i].Instances;
                }
                return n;
            }
        }

        /// <summary>
        /// True only when every scope that was attempted finished as Checked. Scopes marked
        /// <see cref="CoverageStatus.NotCovered"/> are out of scope by design and listed separately.
        /// </summary>
        public bool AllAttemptedScopesChecked
        {
            get
            {
                if (State != ScanState.Complete)
                {
                    return false;
                }
                for (int i = 0; i < Coverage.Count; i++)
                {
                    CoverageStatus s = Coverage[i].Status;
                    if (s != CoverageStatus.Checked && s != CoverageStatus.NotCovered)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        public ModVerdict VerdictFor(ModKey target)
        {
            for (int i = 0; i < Findings.Count; i++)
            {
                if (Findings[i].Target == target)
                {
                    return ModVerdict.Detected;
                }
            }
            if (!AllAttemptedScopesChecked || UnattributedInstances > 0)
            {
                return ModVerdict.Incomplete;
            }
            return ModVerdict.NoDetectedInstances;
        }
    }
}
