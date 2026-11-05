using System;
using System.Collections.Generic;
using System.Reflection;
using ModRemovalAuditor.UI;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ModRemovalAuditor.Audit
{
    /// <summary>Location context for data owned by a thing (pawn data, bills): described as the owner's location.</summary>
    internal sealed class OwnerContext
    {
        public OwnerContext(Thing owner)
        {
            Owner = owner;
        }

        public Thing Owner { get; }
    }

    /// <summary>
    /// Base for record scopes: snapshots a list in <see cref="Begin"/> and processes one element per step.
    /// A failing element is isolated (counted, first error logged once) and makes the scope Partial.
    /// </summary>
    internal abstract class ListSourceBase<T> : GameSourceBase where T : class
    {
        private List<T> items;

        private int index;

        private int failed;

        private Exception firstError;

        private string limitation;

        protected ListSourceBase(ScanShared shared) : base(shared)
        {
        }

        public override CoverageStatus FinalStatus => failed > 0 || limitation != null ? CoverageStatus.Partial : CoverageStatus.Checked;

        public override string Warning
        {
            get
            {
                List<string> parts = new List<string>(2);
                if (limitation != null)
                {
                    parts.Add(limitation);
                }
                if (failed > 0)
                {
                    parts.Add("MRA_Warn_EntriesFailed".Translate(failed, firstError?.GetType().Name ?? "?").Resolve());
                }
                return parts.Count == 0 ? null : string.Join(" ", parts);
            }
        }

        public override void Begin()
        {
            items = new List<T>();
            index = 0;
            Snapshot(items);
        }

        /// <summary>Copies the elements to scan. Called once; the scan then spans frames on the copy.</summary>
        protected abstract void Snapshot(List<T> output);

        protected abstract void Scan(T item, List<AuditObservation> output);

        /// <summary>Marks the scope Partial with an explanation (e.g. an API that is not available).</summary>
        protected void SetLimitation(string text)
        {
            limitation = text;
        }

        public override bool Step(List<AuditObservation> output)
        {
            if (index < items.Count)
            {
                T item = items[index++];
                if (item != null)
                {
                    int before = output.Count;
                    try
                    {
                        Scan(item, output);
                    }
                    catch (Exception ex)
                    {
                        // Drop what this element emitted so far: a half-read element is not reported.
                        output.RemoveRange(before, output.Count - before);
                        failed++;
                        if (firstError == null)
                        {
                            firstError = ex;
                        }
                    }
                }
            }
            return index < items.Count;
        }

        public override void End()
        {
            if (failed > 0 && firstError != null)
            {
                Log.Warning("[ModRemovalAuditor] Scope '" + ScopeId + "': " + failed + " entr(ies) could not be read and were skipped. First error: " + firstError);
            }
            items = null;
        }

        protected static void Emit(List<AuditObservation> output, object instance, Def def, AuditCategory category, object context)
        {
            if (instance == null || def == null)
            {
                return;
            }
            output.Add(new AuditObservation(instance, def, GameScan.IdentityOf(def), category, 1, 0, context));
        }
    }

    /// <summary>
    /// Pawn data of every pawn reached by the thing traversals (maps, world, world pawns, gravships):
    /// hediffs, xenogenes and endogenes, xenotype, traits, abilities, pawn kind and backstories.
    /// </summary>
    internal sealed class PawnDataSource : ListSourceBase<Pawn>
    {
        public PawnDataSource(ScanShared shared) : base(shared)
        {
        }

        public override string ScopeId => GameScan.ScopePawnData;

        protected override void Snapshot(List<Pawn> output)
        {
            output.AddRange(Shared.Pawns);
        }

        protected override void Scan(Pawn pawn, List<AuditObservation> output)
        {
            // Destroyed dead pawns are still scanned: the world keeps them (relations, records) with their data.
            OwnerContext context = new OwnerContext(pawn);
            const AuditCategory c = AuditCategory.PawnData;

            Emit(output, pawn, pawn.kindDef, c, context);

            List<Hediff> hediffs = pawn.health?.hediffSet?.hediffs;
            if (hediffs != null)
            {
                for (int i = 0; i < hediffs.Count; i++)
                {
                    Emit(output, hediffs[i], hediffs[i]?.def, c, context);
                }
            }

            Pawn_GeneTracker genes = pawn.genes;
            if (genes != null)
            {
                // Xenogenes/Endogenes are the saved lists; GenesListForReading would build a cache.
                AddGenes(genes.Xenogenes, context, output);
                AddGenes(genes.Endogenes, context, output);
                // Xenotype falls back to Baseliner (official content) for custom xenotypes; CustomXenotype is
                // not read because it computes and caches state.
                Emit(output, pawn, genes.Xenotype, c, context);
            }

            Pawn_StoryTracker story = pawn.story;
            if (story != null)
            {
                Emit(output, pawn, story.Childhood, c, context);
                Emit(output, pawn, story.Adulthood, c, context);
                List<Trait> traits = story.traits?.allTraits;
                if (traits != null)
                {
                    for (int i = 0; i < traits.Count; i++)
                    {
                        Emit(output, traits[i], traits[i]?.def, c, context);
                    }
                }
            }

            // The saved ability list only; AllAbilitiesForReading also includes abilities granted at runtime.
            List<Ability> abilities = pawn.abilities?.abilities;
            if (abilities != null)
            {
                for (int i = 0; i < abilities.Count; i++)
                {
                    Emit(output, abilities[i], abilities[i]?.def, c, context);
                }
            }
        }

        private static void AddGenes(List<Gene> genes, OwnerContext context, List<AuditObservation> output)
        {
            if (genes == null)
            {
                return;
            }
            for (int i = 0; i < genes.Count; i++)
            {
                Emit(output, genes[i], genes[i]?.def, AuditCategory.PawnData, context);
            }
        }
    }

    /// <summary>Bills (recipes) of every bill giver reached by the thing traversals, including surgery bills on pawns.</summary>
    internal sealed class BillSource : ListSourceBase<Thing>
    {
        public BillSource(ScanShared shared) : base(shared)
        {
        }

        public override string ScopeId => GameScan.ScopeBills;

        protected override void Snapshot(List<Thing> output)
        {
            output.AddRange(Shared.BillGivers);
        }

        protected override void Scan(Thing thing, List<AuditObservation> output)
        {
            if (thing.Destroyed || !(thing is IBillGiver giver))
            {
                return;
            }
            if (thing is Pawn pawn && pawn.health == null)
            {
                // Pawn.BillStack is health.surgeryBills.
                return;
            }
            List<Bill> bills = giver.BillStack?.Bills;
            if (bills == null || bills.Count == 0)
            {
                return;
            }
            OwnerContext context = new OwnerContext(thing);
            for (int i = 0; i < bills.Count; i++)
            {
                Emit(output, bills[i], bills[i]?.recipe, AuditCategory.Bills, context);
            }
        }
    }

    /// <summary>The def of every world object (settlements, sites, caravans, ...) and the parts of each site.</summary>
    internal sealed class WorldObjectDefSource : ListSourceBase<WorldObject>
    {
        public WorldObjectDefSource(ScanShared shared) : base(shared)
        {
        }

        public override string ScopeId => GameScan.ScopeWorldObjects;

        protected override void Snapshot(List<WorldObject> output)
        {
            List<WorldObject> all = Find.World?.worldObjects?.AllWorldObjects;
            if (all != null)
            {
                output.AddRange(all);
            }
        }

        protected override void Scan(WorldObject wo, List<AuditObservation> output)
        {
            AuditLocation location = new AuditLocation(
                "wo:" + wo.ID,
                "MRA_Loc_WorldObject".Translate(wo.LabelCap).Resolve(),
                new WorldObjectJumpHandle(wo));
            Emit(output, wo, wo.def, AuditCategory.WorldObjects, location);
            if (wo is Site site && site.parts != null)
            {
                for (int i = 0; i < site.parts.Count; i++)
                {
                    Emit(output, site.parts[i], site.parts[i]?.def, AuditCategory.WorldObjects, location);
                }
            }
        }
    }

    internal sealed class FactionSource : ListSourceBase<Faction>
    {
        public FactionSource(ScanShared shared) : base(shared)
        {
        }

        public override string ScopeId => GameScan.ScopeFactions;

        protected override void Snapshot(List<Faction> output)
        {
            List<Faction> all = Find.World?.factionManager?.AllFactionsListForReading;
            if (all != null)
            {
                output.AddRange(all);
            }
        }

        protected override void Scan(Faction faction, List<AuditObservation> output)
        {
            AuditLocation location = new AuditLocation(
                "faction:" + faction.loadID,
                "MRA_Loc_Faction".Translate(faction.Name ?? faction.def?.label ?? "?").Resolve(),
                null);
            Emit(output, faction, faction.def, AuditCategory.Factions, location);
        }
    }

    /// <summary>Memes, precepts and culture of every ideoligion (skipped with a note when Ideology is not active).</summary>
    internal sealed class IdeologySource : ListSourceBase<Ideo>
    {
        public IdeologySource(ScanShared shared) : base(shared)
        {
        }

        public override string ScopeId => GameScan.ScopeIdeology;

        protected override void Snapshot(List<Ideo> output)
        {
            List<Ideo> all = Find.World?.ideoManager?.IdeosListForReading;
            if (all != null)
            {
                output.AddRange(all);
            }
        }

        protected override void Scan(Ideo ideo, List<AuditObservation> output)
        {
            AuditLocation location = new AuditLocation(
                "ideo:" + ideo.id,
                "MRA_Loc_Ideo".Translate(ideo.name ?? "?").Resolve(),
                null);
            if (ideo.memes != null)
            {
                for (int i = 0; i < ideo.memes.Count; i++)
                {
                    Emit(output, ideo, ideo.memes[i], AuditCategory.Ideology, location);
                }
            }
            List<Precept> precepts = ideo.PreceptsListForReading;
            if (precepts != null)
            {
                for (int i = 0; i < precepts.Count; i++)
                {
                    Emit(output, precepts[i], precepts[i]?.def, AuditCategory.Ideology, location);
                }
            }
            Emit(output, ideo, ideo.culture, AuditCategory.Ideology, location);
        }
    }

    /// <summary>The root script of every quest (active, offered and historical). Quest internals are not inspected.</summary>
    internal sealed class QuestSource : ListSourceBase<Quest>
    {
        public QuestSource(ScanShared shared) : base(shared)
        {
        }

        public override string ScopeId => GameScan.ScopeQuests;

        protected override void Snapshot(List<Quest> output)
        {
            List<Quest> all = Find.QuestManager?.QuestsListForReading;
            if (all != null)
            {
                output.AddRange(all);
            }
        }

        protected override void Scan(Quest quest, List<AuditObservation> output)
        {
            if (quest.root == null)
            {
                return;
            }
            string state = ("MRA_QuestState_" + quest.State).Translate().Resolve();
            AuditLocation location = new AuditLocation(
                "quests",
                "MRA_Loc_Quest".Translate(quest.name ?? "?", state).Resolve(),
                null);
            Emit(output, quest, quest.root, AuditCategory.Quests, location);
        }
    }

    /// <summary>
    /// Saved research state: progress, techprints, anomaly knowledge and the current project.
    /// Public accessors such as ResearchManager.GetProgress and ResearchProjectDef.IsFinished insert
    /// zero entries into the saved dictionary, so the progress dictionary is read directly instead.
    /// </summary>
    internal sealed class ResearchSource : ListSourceBase<ResearchProjectDef>
    {
        private static readonly FieldInfo ProgressField =
            typeof(ResearchManager).GetField("progress", BindingFlags.Instance | BindingFlags.NonPublic);

        private ResearchManager manager;

        private Dictionary<ResearchProjectDef, float> progress;

        private ResearchProjectDef current;

        public ResearchSource(ScanShared shared) : base(shared)
        {
        }

        public override string ScopeId => GameScan.ScopeResearch;

        protected override void Snapshot(List<ResearchProjectDef> output)
        {
            manager = Find.ResearchManager;
            if (manager == null)
            {
                return;
            }
            progress = ProgressField?.GetValue(manager) as Dictionary<ResearchProjectDef, float>;
            if (progress == null)
            {
                SetLimitation("MRA_Warn_ResearchProgressUnreadable".Translate().Resolve());
            }
            current = manager.GetProject();
            output.AddRange(DefDatabase<ResearchProjectDef>.AllDefsListForReading);
        }

        protected override void Scan(ResearchProjectDef def, List<AuditObservation> output)
        {
            float done = 0f;
            if (progress != null)
            {
                progress.TryGetValue(def, out done);
            }
            int techprints = manager.GetTechprints(def);
            float knowledge = manager.GetKnowledge(def);
            bool isCurrent = def == current;
            if (done <= 0f && techprints <= 0 && knowledge <= 0f && !isCurrent)
            {
                return;
            }
            List<string> parts = new List<string>(4);
            float cost = def.Cost;
            float spent = Math.Max(done, knowledge);
            if (spent > 0f)
            {
                parts.Add(cost > 0f && spent >= cost
                    ? "MRA_Research_Finished".Translate().Resolve()
                    : "MRA_Research_Progress".Translate(cost > 0f ? (spent / cost).ToStringPercent() : spent.ToString("0.#")).Resolve());
            }
            if (techprints > 0)
            {
                parts.Add("MRA_Research_Techprints".Translate(techprints).Resolve());
            }
            if (isCurrent)
            {
                parts.Add("MRA_Research_Current".Translate().Resolve());
            }
            AuditLocation location = new AuditLocation("research", "MRA_Loc_Research".Translate(string.Join(", ", parts)).Resolve(), null);
            Emit(output, def, def, AuditCategory.Research, location);
        }

        public override void End()
        {
            base.End();
            manager = null;
            progress = null;
            current = null;
        }
    }
}
