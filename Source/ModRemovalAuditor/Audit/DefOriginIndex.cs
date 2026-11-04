using System;
using System.Collections.Generic;

namespace ModRemovalAuditor.Audit
{
    public enum OriginConfidence
    {
        Confirmed,
        Uncertain,
        Unknown,
    }

    public enum OriginReason
    {
        /// <summary>The live owner lists exactly this def object and no other pack declares the same identity.</summary>
        Declared,

        /// <summary>Another pack also declares a def with the same type and defName (override / shadowing).</summary>
        Shadowed,

        /// <summary>The def claims an owner, but that owner's def list does not contain this def object.</summary>
        NotInOwnerDefs,

        /// <summary>The def has no owning content pack (runtime-generated or patch-inserted).</summary>
        NoOwner,
    }

    public readonly struct OriginResolution
    {
        private static readonly ModKey[] NoMods = Array.Empty<ModKey>();

        private readonly ModKey[] alsoDeclaredBy;

        public OriginResolution(ModKey? owner, OriginConfidence confidence, OriginReason reason, ModKey[] alsoDeclaredBy)
        {
            Owner = owner;
            Confidence = confidence;
            Reason = reason;
            this.alsoDeclaredBy = alsoDeclaredBy ?? NoMods;
        }

        /// <summary>The pack the live def object claims (Def.modContentPack); null if none.</summary>
        public ModKey? Owner { get; }

        public OriginConfidence Confidence { get; }

        public OriginReason Reason { get; }

        /// <summary>Other packs that declare the same identity or list this def object. Never contains <see cref="Owner"/>.</summary>
        public IReadOnlyList<ModKey> AlsoDeclaredBy => alsoDeclaredBy ?? NoMods;

        public bool IsUnknown => Confidence == OriginConfidence.Unknown;

        /// <summary>
        /// True if removing <paramref name="mod"/> could plausibly affect content using this def.
        /// Unknown origins are never relevant to a specific mod; they are reported separately.
        /// </summary>
        public bool IsRelevantTo(ModKey mod)
        {
            if (Confidence == OriginConfidence.Unknown)
            {
                return false;
            }
            if (Owner.HasValue && Owner.Value == mod)
            {
                return true;
            }
            ModKey[] others = alsoDeclaredBy ?? NoMods;
            for (int i = 0; i < others.Length; i++)
            {
                if (others[i] == mod)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Ownership index built from each running content pack's def list. Matching is by def object
    /// reference and by (def type, defName) — never by label, texture path or translated text.
    /// </summary>
    public sealed class DefOriginIndex
    {
        private readonly Dictionary<object, List<ModKey>> listedIn = new Dictionary<object, List<ModKey>>(ReferenceEqualityComparer.Instance);

        private readonly Dictionary<DefIdentity, List<ModKey>> declarers = new Dictionary<DefIdentity, List<ModKey>>();

        public int DeclarationCount { get; private set; }

        public void AddDeclaration(object def, DefIdentity identity, ModKey declaringMod)
        {
            if (def == null)
            {
                throw new ArgumentNullException(nameof(def));
            }
            AddUnique(listedIn, def, declaringMod);
            AddUnique(declarers, identity, declaringMod);
            DeclarationCount++;
        }

        public OriginResolution Resolve(object def, DefIdentity identity, ModKey? liveOwner)
        {
            List<ModKey> sameIdentity;
            declarers.TryGetValue(identity, out sameIdentity);
            List<ModKey> listing = null;
            if (def != null)
            {
                listedIn.TryGetValue(def, out listing);
            }

            if (!liveOwner.HasValue || liveOwner.Value.IsEmpty)
            {
                return new OriginResolution(null, OriginConfidence.Unknown, OriginReason.NoOwner, Others(null, sameIdentity, listing));
            }

            ModKey owner = liveOwner.Value;
            ModKey[] others = Others(owner, sameIdentity, listing);
            bool ownerListsDef = listing != null && listing.Contains(owner);
            if (!ownerListsDef)
            {
                return new OriginResolution(owner, OriginConfidence.Uncertain, OriginReason.NotInOwnerDefs, others);
            }
            if (others.Length > 0)
            {
                return new OriginResolution(owner, OriginConfidence.Uncertain, OriginReason.Shadowed, others);
            }
            return new OriginResolution(owner, OriginConfidence.Confirmed, OriginReason.Declared, null);
        }

        private static ModKey[] Others(ModKey? owner, List<ModKey> a, List<ModKey> b)
        {
            List<ModKey> result = null;
            Collect(owner, a, ref result);
            Collect(owner, b, ref result);
            return result == null ? Array.Empty<ModKey>() : result.ToArray();
        }

        private static void Collect(ModKey? owner, List<ModKey> source, ref List<ModKey> result)
        {
            if (source == null)
            {
                return;
            }
            for (int i = 0; i < source.Count; i++)
            {
                ModKey m = source[i];
                if (owner.HasValue && owner.Value == m)
                {
                    continue;
                }
                if (result == null)
                {
                    result = new List<ModKey>(2);
                }
                if (!result.Contains(m))
                {
                    result.Add(m);
                }
            }
        }

        private static void AddUnique<TKey>(Dictionary<TKey, List<ModKey>> map, TKey key, ModKey mod)
        {
            if (!map.TryGetValue(key, out List<ModKey> list))
            {
                list = new List<ModKey>(1);
                map.Add(key, list);
            }
            if (!list.Contains(mod))
            {
                list.Add(mod);
            }
        }
    }
}
