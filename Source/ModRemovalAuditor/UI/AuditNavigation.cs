using System;
using ModRemovalAuditor.Audit;
using RimWorld.Planet;
using Verse;

namespace ModRemovalAuditor.UI
{
    /// <summary>A navigation target captured during a scan. Holds only weak references to game objects.</summary>
    public interface IAuditJumpHandle
    {
        /// <summary>Re-validates against the live game. Returns false (with a translation key) if the target is gone or off-map.</summary>
        bool TryResolve(out GlobalTargetInfo target, out string unavailableKey);
    }

    public sealed class ThingJumpHandle : IAuditJumpHandle
    {
        private readonly System.WeakReference<Thing> thing;

        public ThingJumpHandle(Thing thing)
        {
            this.thing = new System.WeakReference<Thing>(thing);
        }

        public bool TryResolve(out GlobalTargetInfo target, out string unavailableKey)
        {
            target = GlobalTargetInfo.Invalid;
            if (!thing.TryGetTarget(out Thing t) || t == null || t.Destroyed)
            {
                unavailableKey = "MRA_Jump_Gone";
                return false;
            }
            GlobalTargetInfo adjusted = CameraJumper.GetAdjustedTarget(new GlobalTargetInfo(t));
            if (adjusted.HasThing)
            {
                Thing shown = adjusted.Thing;
                if (shown != null && shown.Spawned && Find.Maps != null && Find.Maps.Contains(shown.Map))
                {
                    target = new GlobalTargetInfo(t);
                    unavailableKey = null;
                    return true;
                }
            }
            else if (adjusted.HasWorldObject)
            {
                if (adjusted.WorldObject != null && adjusted.WorldObject.Spawned)
                {
                    target = new GlobalTargetInfo(t);
                    unavailableKey = null;
                    return true;
                }
            }
            // Tile-only or holder-less (world pawn, gravship in flight): no misleading map link.
            unavailableKey = "MRA_Jump_OffMap";
            return false;
        }
    }

    public sealed class CellJumpHandle : IAuditJumpHandle
    {
        private readonly System.WeakReference<Map> map;

        private readonly IntVec3 cell;

        private readonly TerrainDef def;

        private readonly int layer;

        public CellJumpHandle(Map map, IntVec3 cell, TerrainDef def, int layer)
        {
            this.map = new System.WeakReference<Map>(map);
            this.cell = cell;
            this.def = def;
            this.layer = layer;
        }

        public bool TryResolve(out GlobalTargetInfo target, out string unavailableKey)
        {
            target = GlobalTargetInfo.Invalid;
            if (!map.TryGetTarget(out Map m) || m == null || Find.Maps == null || !Find.Maps.Contains(m) || !cell.InBounds(m))
            {
                unavailableKey = "MRA_Jump_Gone";
                return false;
            }
            if (TerrainAt(m) != def)
            {
                unavailableKey = "MRA_Jump_Changed";
                return false;
            }
            target = new GlobalTargetInfo(cell, m);
            unavailableKey = null;
            return true;
        }

        private TerrainDef TerrainAt(Map m)
        {
            int index = m.cellIndices.CellToIndex(cell);
            switch (layer)
            {
                case 0:
                    return m.terrainGrid.TopTerrainAt(index);
                case 1:
                    return m.terrainGrid.UnderTerrainAt(index);
                case 2:
                    return m.terrainGrid.FoundationAt(index);
                default:
                    return m.terrainGrid.TempTerrainAt(index);
            }
        }
    }

    public static class AuditNavigation
    {
        /// <summary>Finds the next still-valid handle of the finding, starting at <paramref name="cursor"/>.</summary>
        public static bool TryFindTarget(AuditFinding finding, ref int cursor, out GlobalTargetInfo target, out string unavailableKey)
        {
            target = GlobalTargetInfo.Invalid;
            unavailableKey = "MRA_Jump_NoLocation";
            if (finding == null || finding.JumpHandles.Count == 0)
            {
                return false;
            }
            int count = finding.JumpHandles.Count;
            for (int i = 0; i < count; i++)
            {
                int index = ((cursor % count) + count + i) % count;
                if (finding.JumpHandles[index] is IAuditJumpHandle handle)
                {
                    if (handle.TryResolve(out target, out string reason))
                    {
                        cursor = index;
                        return true;
                    }
                    unavailableKey = reason ?? unavailableKey;
                }
            }
            return false;
        }

        /// <summary>Validates the finding's location and jumps to it. Returns false and changes nothing if unavailable.</summary>
        public static bool TryJump(AuditFinding finding, ref int cursor)
        {
            if (!TryFindTarget(finding, ref cursor, out GlobalTargetInfo target, out _))
            {
                return false;
            }
            if (target.HasThing)
            {
                CameraJumper.TryJumpAndSelect(target);
            }
            else
            {
                CameraJumper.TryJump(target);
            }
            cursor++;
            return true;
        }
    }
}
