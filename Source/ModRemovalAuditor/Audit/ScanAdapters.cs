using System;
using System.Collections.Generic;
using ModRemovalAuditor.UI;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ModRemovalAuditor.Audit
{
    /// <summary>
    /// Builds a read-only scan of the currently loaded game. Everything here only reads public game
    /// state; no game object is created, modified, spawned, despawned or saved.
    /// </summary>
    public static class GameScan
    {
        public const string ScopeProvenance = "provenance";
        public const string ScopeMaps = "maps";
        public const string ScopeWorld = "world";
        public const string ScopeWorldPawns = "worldpawns";
        public const string ScopeTerrain = "terrain";

        /// <summary>Scopes this version deliberately does not inspect. Always shown in the coverage box.</summary>
        public static readonly string[] NotCoveredScopes =
        {
            "pawndata",       // hediffs, genes, traits, abilities, xenotypes
            "records",        // bills, research progress, quests, factions, ideoligions, tales/art
            "customdata",     // mod-specific saved fields and runtime-generated content
            "gravshipterrain",
            "unloaded",       // maps or saves that are not currently loaded
        };

        internal static readonly HashSet<Type> ScannedDefRoots = new HashSet<Type> { typeof(ThingDef), typeof(TerrainDef) };

        private static readonly Dictionary<Type, string> DatabaseTypeNames = new Dictionary<Type, string>();

        public static bool IsSelectable(ModContentPack pack)
        {
            return pack != null && !pack.IsCoreMod && !pack.IsOfficialMod && ModKey.TryParse(pack.PackageId, out ModKey key) && !key.IsLudeonOfficial;
        }

        /// <summary>Builds audit targets; dependency metadata of all running mods is read once.</summary>
        public static List<AuditTarget> BuildTargets(IReadOnlyList<ModContentPack> packs)
        {
            Dictionary<ModKey, List<string>> dependents = new Dictionary<ModKey, List<string>>();
            List<ModContentPack> running = LoadedModManager.RunningModsListForReading;
            for (int i = 0; i < running.Count; i++)
            {
                ModContentPack other = running[i];
                List<ModDependency> deps = other.ModMetaData?.Dependencies;
                if (deps == null)
                {
                    continue;
                }
                for (int j = 0; j < deps.Count; j++)
                {
                    if (deps[j]?.packageId == null || !ModKey.TryParse(deps[j].packageId, out ModKey depKey))
                    {
                        continue;
                    }
                    if (!dependents.TryGetValue(depKey, out List<string> names))
                    {
                        names = new List<string>();
                        dependents.Add(depKey, names);
                    }
                    if (!names.Contains(other.Name))
                    {
                        names.Add(other.Name);
                    }
                }
            }
            List<AuditTarget> targets = new List<AuditTarget>(packs.Count);
            for (int i = 0; i < packs.Count; i++)
            {
                AuditTarget target = BuildTarget(packs[i]);
                if (dependents.TryGetValue(target.Key, out List<string> names))
                {
                    target.DependentMods.AddRange(names);
                }
                targets.Add(target);
            }
            return targets;
        }

        private static AuditTarget BuildTarget(ModContentPack pack)
        {
            ModKey key = ModKey.Parse(pack.PackageId);
            AuditTarget target = new AuditTarget(key, pack.Name, pack.PackageIdPlayerFacing);
            foreach (string t in ScannedDefTypeNames())
            {
                target.ScannedDefTypes.Add(t);
            }
            foreach (Def def in pack.AllDefs)
            {
                if (def == null)
                {
                    continue;
                }
                string type = DatabaseTypeName(def.GetType());
                target.DeclaredDefCounts.TryGetValue(type, out int n);
                target.DeclaredDefCounts[type] = n + 1;
            }
            target.HasAssemblies = pack.assemblies?.loadedAssemblies != null && pack.assemblies.loadedAssemblies.Count > 0;
            return target;
        }

        public static AuditSession CreateSession(IReadOnlyList<AuditTarget> targets)
        {
            Game game = Current.Game;
            ScanShared shared = new ScanShared();
            List<IScanSource> sources = new List<IScanSource>
            {
                new ProvenanceSource(shared),
                new MapHolderSource(shared),
                new WorldHolderSource(shared),
                new WorldPawnSource(shared),
                new TerrainSource(shared),
            };
            List<CoverageEntry> notCovered = new List<CoverageEntry>();
            for (int i = 0; i < NotCoveredScopes.Length; i++)
            {
                notCovered.Add(new CoverageEntry(NotCoveredScopes[i], CoverageStatus.NotCovered, 0, null));
            }
            return new AuditSession(
                targets,
                sources,
                shared.Resolve,
                notCovered,
                logError: message => Log.Warning(message),
                abortCheck: () =>
                {
                    if (Current.Game != game || Current.ProgramState != ProgramState.Playing)
                    {
                        return "MRA_AbortGameChanged".Translate().Resolve();
                    }
                    return null;
                });
        }

        public static IEnumerable<string> ScannedDefTypeNames()
        {
            foreach (Type t in ScannedDefRoots)
            {
                yield return DatabaseTypeName(t);
            }
        }

        /// <summary>
        /// Name of the def type that owns the DefDatabase for this def (the top-most non-abstract
        /// ancestor below Def), so subclasses resolve to the same identity as their database.
        /// </summary>
        public static string DatabaseTypeName(Type type)
        {
            if (DatabaseTypeNames.TryGetValue(type, out string name))
            {
                return name;
            }
            Type result = type;
            for (Type b = type.BaseType; b != null && b != typeof(Def); b = b.BaseType)
            {
                if (!b.IsAbstract)
                {
                    result = b;
                }
            }
            name = result.Name;
            DatabaseTypeNames[type] = name;
            return name;
        }

        internal static DefIdentity IdentityOf(Def def) => new DefIdentity(DatabaseTypeName(def.GetType()), def.defName);
    }

    /// <summary>State shared by the sources of one session: provenance index, origin cache and visited holders.</summary>
    internal sealed class ScanShared
    {
        public readonly DefOriginIndex Index = new DefOriginIndex();

        public readonly Dictionary<ModContentPack, ModKey> PackKeys = new Dictionary<ModContentPack, ModKey>(ReferenceEqualityComparer.Instance);

        public readonly HashSet<IThingHolder> VisitedHolders = new HashSet<IThingHolder>(ReferenceEqualityComparer.Instance);

        private readonly Dictionary<Def, OriginResolution> cache = new Dictionary<Def, OriginResolution>(ReferenceEqualityComparer.Instance);

        public OriginResolution Resolve(AuditObservation observation)
        {
            Def def = observation.Def as Def;
            if (def == null)
            {
                return new OriginResolution(null, OriginConfidence.Unknown, OriginReason.NoOwner, null);
            }
            if (cache.TryGetValue(def, out OriginResolution cached))
            {
                return cached;
            }
            ModKey? owner = null;
            ModContentPack pack = def.modContentPack;
            if (pack != null)
            {
                if (!PackKeys.TryGetValue(pack, out ModKey key))
                {
                    if (ModKey.TryParse(pack.PackageId, out key))
                    {
                        PackKeys[pack] = key;
                    }
                }
                if (!key.IsEmpty)
                {
                    owner = key;
                }
            }
            OriginResolution r = Index.Resolve(def, observation.DefId, owner);
            cache[def] = r;
            return r;
        }
    }

    /// <summary>Common describer logic for thing and terrain observations.</summary>
    internal abstract class GameSourceBase : IScanSource
    {
        protected readonly ScanShared Shared;

        protected GameSourceBase(ScanShared shared)
        {
            Shared = shared;
        }

        public abstract string ScopeId { get; }

        public virtual bool IsCritical => false;

        public virtual CoverageStatus FinalStatus => CoverageStatus.Checked;

        public virtual string Warning => null;

        public abstract void Begin();

        public abstract bool Step(List<AuditObservation> output);

        public abstract void End();

        public AuditLocation DescribeLocation(in AuditObservation observation) => GameLocations.Describe(observation);

        public string DescribeDef(object def)
        {
            Def d = def as Def;
            if (d == null)
            {
                return string.Empty;
            }
            string label = d.label;
            return string.IsNullOrEmpty(label) ? d.defName : label.CapitalizeFirst();
        }
    }

    /// <summary>Critical first stage: indexes which pack declares which ThingDef / TerrainDef.</summary>
    internal sealed class ProvenanceSource : GameSourceBase
    {
        private const int DefsPerStep = 500;

        private List<ModContentPack> packs;

        private int packIndex;

        private IEnumerator<Def> defs;

        public ProvenanceSource(ScanShared shared) : base(shared)
        {
        }

        public override string ScopeId => GameScan.ScopeProvenance;

        public override bool IsCritical => true;

        public override void Begin()
        {
            packs = new List<ModContentPack>(LoadedModManager.RunningModsListForReading);
            packIndex = 0;
        }

        public override bool Step(List<AuditObservation> output)
        {
            int budget = DefsPerStep;
            while (budget > 0 && packIndex < packs.Count)
            {
                ModContentPack pack = packs[packIndex];
                if (defs == null)
                {
                    if (!ModKey.TryParse(pack.PackageId, out ModKey key))
                    {
                        packIndex++;
                        continue;
                    }
                    Shared.PackKeys[pack] = key;
                    defs = pack.AllDefs.GetEnumerator();
                }
                ModKey packKey = Shared.PackKeys[pack];
                while (budget > 0 && defs.MoveNext())
                {
                    budget--;
                    Def def = defs.Current;
                    if (def is ThingDef || def is TerrainDef)
                    {
                        Shared.Index.AddDeclaration(def, GameScan.IdentityOf(def), packKey);
                    }
                }
                if (budget > 0)
                {
                    defs.Dispose();
                    defs = null;
                    packIndex++;
                }
            }
            return packIndex < packs.Count;
        }

        public override void End()
        {
            defs?.Dispose();
            defs = null;
            packs = null;
        }
    }

    /// <summary>Shared logic for IThingHolder traversals (maps, world objects, world pawns).</summary>
    internal abstract class HolderSourceBase : GameSourceBase
    {
        protected HolderTraversal<IThingHolder, Thing> Traversal;

        private readonly List<HolderTraversal<IThingHolder, Thing>.Emitted> emitted = new List<HolderTraversal<IThingHolder, Thing>.Emitted>(4);

        private readonly List<IThingHolder> compHolders = new List<IThingHolder>(2);

        protected HolderSourceBase(ScanShared shared) : base(shared)
        {
        }

        public override CoverageStatus FinalStatus =>
            Traversal != null && (Traversal.FailedHolders > 0 || Traversal.DepthLimitHits > 0) ? CoverageStatus.Partial : CoverageStatus.Checked;

        public override string Warning
        {
            get
            {
                if (Traversal == null)
                {
                    return null;
                }
                List<string> parts = new List<string>();
                if (Traversal.FailedHolders > 0)
                {
                    parts.Add("MRA_Warn_HoldersFailed".Translate(Traversal.FailedHolders, Traversal.FirstError?.GetType().Name ?? "?").Resolve());
                }
                if (Traversal.DepthLimitHits > 0)
                {
                    parts.Add("MRA_Warn_DepthLimit".Translate(Traversal.DepthLimitHits).Resolve());
                }
                return parts.Count == 0 ? null : string.Join(" ", parts);
            }
        }

        public override void Begin()
        {
            Traversal = new HolderTraversal<IThingHolder, Thing>(GetItems, GetChildren, Shared.VisitedHolders);
            AddRoots();
        }

        protected abstract void AddRoots();

        public override bool Step(List<AuditObservation> output)
        {
            emitted.Clear();
            bool more = Traversal.Step(emitted);
            for (int i = 0; i < emitted.Count; i++)
            {
                HolderTraversal<IThingHolder, Thing>.Emitted e = emitted[i];
                Thing thing = e.Item;
                if (thing.Destroyed)
                {
                    continue;
                }
                AuditCategory category = CategoryFor(thing, e.Holder);
                GameObservations.AddThing(thing, category, e.Holder, output);
                // Container things whose parent holder did not report them as children are expanded too;
                // the shared visited set guarantees each holder is expanded at most once.
                if (thing is IThingHolder holder)
                {
                    more |= Traversal.AddHolder(holder, e.Depth + 1);
                }
                if (thing is ThingWithComps twc)
                {
                    compHolders.Clear();
                    List<ThingComp> comps = twc.AllComps;
                    for (int c = 0; c < comps.Count; c++)
                    {
                        if (comps[c] is IThingHolder ch)
                        {
                            compHolders.Add(ch);
                        }
                    }
                    for (int c = 0; c < compHolders.Count; c++)
                    {
                        more |= Traversal.AddHolder(compHolders[c], e.Depth + 1);
                    }
                    compHolders.Clear();
                }
            }
            emitted.Clear();
            return more || Traversal.HasWork;
        }

        public override void End()
        {
            if (Traversal != null && Traversal.FailedHolders > 0 && Traversal.FirstError != null)
            {
                Log.Warning("[ModRemovalAuditor] Scope '" + ScopeId + "': " + Traversal.FailedHolders
                    + " container(s) could not be read and were skipped. First error: " + Traversal.FirstError);
            }
            Traversal?.Clear();
        }

        private static AuditCategory CategoryFor(Thing thing, IThingHolder holder)
        {
            if (thing.Spawned)
            {
                return GameObservations.SpawnedCategory(thing);
            }
            return thing.MapHeld != null ? AuditCategory.ContainedEquipped : AuditCategory.WorldCaravan;
        }

        private static void GetItems(IThingHolder holder, List<Thing> output)
        {
            ThingOwner owner = holder.GetDirectlyHeldThings();
            if (owner == null)
            {
                return;
            }
            int count = owner.Count;
            for (int i = 0; i < count; i++)
            {
                Thing t = owner[i];
                if (t != null)
                {
                    output.Add(t);
                }
            }
        }

        private static void GetChildren(IThingHolder holder, List<IThingHolder> output)
        {
            holder.GetChildHolders(output);
        }
    }

    /// <summary>All loaded maps (including pocket maps and maps not currently viewed): spawned things plus nested holders.</summary>
    internal sealed class MapHolderSource : HolderSourceBase
    {
        public MapHolderSource(ScanShared shared) : base(shared)
        {
        }

        public override string ScopeId => GameScan.ScopeMaps;

        protected override void AddRoots()
        {
            List<Map> maps = Find.Maps;
            if (maps == null)
            {
                return;
            }
            // Snapshot: the traversal copies each holder's contents when it is expanded.
            for (int i = maps.Count - 1; i >= 0; i--)
            {
                Traversal.AddRoot(maps[i]);
            }
        }
    }

    /// <summary>World objects: caravans, settlement trade stock, travelling transporters, sites, gravships in transit.</summary>
    internal sealed class WorldHolderSource : HolderSourceBase
    {
        public WorldHolderSource(ScanShared shared) : base(shared)
        {
        }

        public override string ScopeId => GameScan.ScopeWorld;

        protected override void AddRoots()
        {
            World world = Find.World;
            if (world == null)
            {
                return;
            }
            Traversal.AddRoot(world);
            // A gravship in flight is a WorldObject but not an IThingHolder; its things and pawns are listed explicitly.
            List<WorldObject> objects = world.worldObjects?.AllWorldObjects;
            if (objects == null)
            {
                return;
            }
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is Gravship gravship)
                {
                    GravshipRoots.Register(gravship);
                    foreach (Thing t in gravship.Things)
                    {
                        Traversal.EnqueueItem(t, null);
                    }
                    foreach (Pawn p in gravship.Pawns)
                    {
                        Traversal.EnqueueItem(p, null);
                    }
                }
            }
        }

        public override void End()
        {
            base.End();
            GravshipRoots.Clear();
        }
    }

    /// <summary>Pawns kept by the world (faction leaders, quest pawns, mothballed and dead pawns) and their gear.</summary>
    internal sealed class WorldPawnSource : HolderSourceBase
    {
        public WorldPawnSource(ScanShared shared) : base(shared)
        {
        }

        public override string ScopeId => GameScan.ScopeWorldPawns;

        protected override void AddRoots()
        {
            WorldPawns worldPawns = Find.World?.worldPawns;
            if (worldPawns == null)
            {
                return;
            }
            // AllPawnsAliveOrDead returns a reused internal list; copy it before spanning frames.
            List<Pawn> pawns = new List<Pawn>(worldPawns.AllPawnsAliveOrDead);
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn == null)
                {
                    continue;
                }
                Traversal.EnqueueItem(pawn, null);
                Traversal.AddRoot(pawn);
            }
        }
    }

    /// <summary>
    /// Terrain on every loaded map, all four saved layers (top, under, foundation, temporary).
    /// One step = one map row; cells are aggregated per (def, layer) so no per-cell objects are created.
    /// </summary>
    internal sealed class TerrainSource : GameSourceBase
    {
        private static readonly string[] LayerIds = { "top", "under", "foundation", "temp" };

        private readonly Dictionary<(TerrainDef def, int layer), (int count, int firstIndex)> counts =
            new Dictionary<(TerrainDef, int), (int, int)>();

        private List<Map> maps;

        private int mapIndex;

        private int row;

        private int skippedMaps;

        public TerrainSource(ScanShared shared) : base(shared)
        {
        }

        public override string ScopeId => GameScan.ScopeTerrain;

        public override CoverageStatus FinalStatus => skippedMaps > 0 ? CoverageStatus.Partial : CoverageStatus.Checked;

        public override string Warning => skippedMaps > 0 ? "MRA_Warn_MapsRemoved".Translate(skippedMaps).Resolve() : null;

        public override void Begin()
        {
            maps = Find.Maps != null ? new List<Map>(Find.Maps) : new List<Map>();
            mapIndex = 0;
            row = 0;
            counts.Clear();
        }

        public override bool Step(List<AuditObservation> output)
        {
            if (mapIndex >= maps.Count)
            {
                return false;
            }
            Map map = maps[mapIndex];
            List<Map> live = Find.Maps;
            if (map == null || map.terrainGrid == null || live == null || !live.Contains(map))
            {
                skippedMaps++;
                NextMap();
                return mapIndex < maps.Count;
            }
            int width = map.Size.x;
            int height = map.Size.z;
            if (row < height)
            {
                TerrainGrid grid = map.terrainGrid;
                int start = row * width;
                int end = start + width;
                for (int index = start; index < end; index++)
                {
                    Count(grid.TopTerrainAt(index), 0, index);
                    Count(grid.UnderTerrainAt(index), 1, index);
                    Count(grid.FoundationAt(index), 2, index);
                    Count(grid.TempTerrainAt(index), 3, index);
                }
                row++;
            }
            if (row >= height)
            {
                foreach (KeyValuePair<(TerrainDef def, int layer), (int count, int firstIndex)> kv in counts)
                {
                    TerrainDef def = kv.Key.def;
                    IntVec3 cell = map.cellIndices.IndexToCell(kv.Value.firstIndex);
                    TerrainContext context = new TerrainContext(map, cell, def, kv.Key.layer, LayerIds[kv.Key.layer]);
                    output.Add(new AuditObservation(null, def, GameScan.IdentityOf(def), AuditCategory.Terrain, kv.Value.count, 0, context));
                }
                NextMap();
            }
            return mapIndex < maps.Count;
        }

        public override void End()
        {
            counts.Clear();
            maps = null;
        }

        private void NextMap()
        {
            counts.Clear();
            mapIndex++;
            row = 0;
        }

        private void Count(TerrainDef def, int layer, int index)
        {
            if (def == null)
            {
                return;
            }
            (TerrainDef, int) key = (def, layer);
            if (counts.TryGetValue(key, out (int count, int firstIndex) v))
            {
                counts[key] = (v.count + 1, v.firstIndex);
            }
            else
            {
                counts[key] = (1, index);
            }
        }
    }

    internal sealed class TerrainContext
    {
        public TerrainContext(Map map, IntVec3 cell, TerrainDef def, int layer, string layerId)
        {
            Map = map;
            Cell = cell;
            Def = def;
            Layer = layer;
            LayerId = layerId;
        }

        public Map Map { get; }

        public IntVec3 Cell { get; }

        public TerrainDef Def { get; }

        public int Layer { get; }

        public string LayerId { get; }
    }

    /// <summary>Gravships in flight seen by the current world scan, used to describe their (holder-less) contents.</summary>
    internal static class GravshipRoots
    {
        private static readonly Dictionary<Thing, Gravship> ByThing = new Dictionary<Thing, Gravship>(ReferenceEqualityComparer.Instance);

        public static void Register(Gravship gravship)
        {
            foreach (Thing t in gravship.Things)
            {
                if (t != null)
                {
                    ByThing[t] = gravship;
                }
            }
            foreach (Pawn p in gravship.Pawns)
            {
                if (p != null)
                {
                    ByThing[p] = gravship;
                }
            }
        }

        public static Gravship Find(Thing thing) => thing != null && ByThing.TryGetValue(thing, out Gravship g) ? g : null;

        public static void Clear() => ByThing.Clear();
    }

    internal static class GameObservations
    {
        public static AuditCategory SpawnedCategory(Thing thing)
        {
            switch (thing.def.category)
            {
                case ThingCategory.Building:
                    return AuditCategory.Buildings;
                case ThingCategory.Item:
                    return AuditCategory.Items;
                default:
                    return AuditCategory.Other;
            }
        }

        public static void AddThing(Thing thing, AuditCategory category, IThingHolder holder, List<AuditObservation> output)
        {
            ThingDef def = thing.def;
            if (def == null)
            {
                return;
            }
            int stack = Math.Max(1, thing.stackCount);
            output.Add(new AuditObservation(thing, def, GameScan.IdentityOf(def), category, 1, stack, holder));
            ThingDef stuff = thing.Stuff;
            if (stuff != null)
            {
                output.Add(new AuditObservation(thing, stuff, GameScan.IdentityOf(stuff), AuditCategory.Material, 1, stack, holder));
            }
        }
    }

    /// <summary>Lazily produces location text, grouping keys and jump handles for relevant observations.</summary>
    internal static class GameLocations
    {
        private const int MaxParentWalk = 64;

        public static AuditLocation Describe(in AuditObservation observation)
        {
            if (observation.Context is TerrainContext terrain)
            {
                string mapLabel = MapLabel(terrain.Map);
                string layer = ("MRA_TerrainLayer_" + terrain.LayerId).Translate().Resolve();
                string text = "MRA_Loc_Terrain".Translate(mapLabel, layer, terrain.Cell.x, terrain.Cell.z).Resolve();
                return new AuditLocation(
                    "terrain:" + terrain.Map.uniqueID + ":" + terrain.LayerId,
                    text,
                    new CellJumpHandle(terrain.Map, terrain.Cell, terrain.Def, terrain.Layer));
            }
            Thing thing = observation.Instance as Thing;
            if (thing == null)
            {
                return new AuditLocation("unknown", "MRA_Loc_Unknown".Translate().Resolve(), null);
            }
            ThingJumpHandle handle = new ThingJumpHandle(thing);
            if (thing.Spawned)
            {
                Map map = thing.Map;
                return new AuditLocation(
                    "map:" + map.uniqueID,
                    "MRA_Loc_Map".Translate(MapLabel(map), thing.Position.x, thing.Position.z).Resolve(),
                    handle);
            }

            IThingHolder immediate = thing.ParentHolder;
            string kind = HolderKind(immediate);
            Thing anchorThing = null;
            WorldObject anchorObject = null;
            Map anchorMap = null;
            IThingHolder h = immediate;
            for (int i = 0; h != null && i < MaxParentWalk; i++, h = h.ParentHolder)
            {
                if (h is Thing ht)
                {
                    anchorThing = ht;
                    break;
                }
                if (h is ThingComp comp && comp.parent != null)
                {
                    anchorThing = comp.parent;
                    break;
                }
                if (h is WorldObject wo)
                {
                    anchorObject = wo;
                    break;
                }
                if (h is Map m)
                {
                    anchorMap = m;
                    break;
                }
            }

            string kindText = ("MRA_Holder_" + kind).Translate().Resolve();
            if (anchorThing != null)
            {
                string where = RootText(anchorThing);
                return new AuditLocation(
                    "thing:" + anchorThing.ThingID + ":" + kind,
                    "MRA_Loc_Held".Translate(kindText, anchorThing.LabelShortCap, where).Resolve(),
                    handle);
            }
            if (anchorObject != null)
            {
                return new AuditLocation(
                    "wo:" + anchorObject.ID + ":" + kind,
                    "MRA_Loc_HeldWorld".Translate(kindText, anchorObject.LabelCap).Resolve(),
                    handle);
            }
            if (anchorMap != null)
            {
                return new AuditLocation(
                    "map:" + anchorMap.uniqueID + ":unspawned",
                    "MRA_Loc_MapUnspawned".Translate(MapLabel(anchorMap)).Resolve(),
                    handle);
            }
            Gravship gravship = GravshipRoots.Find(thing);
            if (gravship != null)
            {
                return new AuditLocation(
                    "wo:" + gravship.ID + ":gravship",
                    "MRA_Loc_Gravship".Translate(gravship.LabelCap).Resolve(),
                    handle);
            }
            if (thing is Pawn pawn && pawn.Dead)
            {
                return new AuditLocation("worldpawns:dead", "MRA_Loc_WorldPawnDead".Translate(thing.LabelShortCap).Resolve(), handle);
            }
            return new AuditLocation("worldpawns", "MRA_Loc_WorldPawn".Translate(thing.LabelShortCap).Resolve(), handle);
        }

        public static string MapLabel(Map map)
        {
            if (map == null)
            {
                return "?";
            }
            string parent = null;
            try
            {
                parent = map.Parent?.LabelCap;
            }
            catch (Exception)
            {
                // A broken map parent label must not break the scan report.
                parent = null;
            }
            return string.IsNullOrEmpty(parent)
                ? "MRA_MapNumber".Translate(map.uniqueID).Resolve()
                : "MRA_MapNamed".Translate(parent, map.uniqueID).Resolve();
        }

        private static string RootText(Thing anchor)
        {
            if (anchor.Spawned)
            {
                return "MRA_Loc_Map".Translate(MapLabel(anchor.Map), anchor.Position.x, anchor.Position.z).Resolve();
            }
            IThingHolder h = anchor.ParentHolder;
            for (int i = 0; h != null && i < MaxParentWalk; i++, h = h.ParentHolder)
            {
                if (h is Thing t && t.Spawned)
                {
                    return "MRA_Loc_Map".Translate(MapLabel(t.Map), t.Position.x, t.Position.z).Resolve();
                }
                if (h is ThingComp c && c.parent != null && c.parent.Spawned)
                {
                    return "MRA_Loc_Map".Translate(MapLabel(c.parent.Map), c.parent.Position.x, c.parent.Position.z).Resolve();
                }
                if (h is WorldObject wo)
                {
                    return wo.LabelCap;
                }
                if (h is Map m)
                {
                    return MapLabel(m);
                }
            }
            Gravship g = GravshipRoots.Find(anchor);
            if (g != null)
            {
                return "MRA_Loc_Gravship".Translate(g.LabelCap).Resolve();
            }
            return "MRA_Loc_OffMap".Translate().Resolve();
        }

        private static string HolderKind(IThingHolder holder)
        {
            switch (holder)
            {
                case Pawn_InventoryTracker _:
                    return "Inventory";
                case Pawn_ApparelTracker _:
                    return "Apparel";
                case Pawn_EquipmentTracker _:
                    return "Equipment";
                case Pawn_CarryTracker _:
                    return "Carried";
                case Settlement_TraderTracker _:
                    return "TradeStock";
                case null:
                    return "None";
                default:
                    return "Container";
            }
        }
    }
}
