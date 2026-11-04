using UnityEngine;
using Verse;

namespace ModRemovalAuditor
{
    /// <summary>
    /// Mod entry point. Holds no settings (nothing is written to the config folder) and registers
    /// no patches, ticks or listeners. The only UI hooks are this settings page and the
    /// bottom-bar button defined in Defs/MainButtonDefs.
    /// </summary>
    public sealed class ModRemovalAuditorMod : Mod
    {
        public static ModContentPack ContentPack { get; private set; }

        public ModRemovalAuditorMod(ModContentPack content) : base(content)
        {
            ContentPack = content;
        }

        public override string SettingsCategory()
        {
            return "MRA_ModName".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            UI.AuditEntryPoint.DrawSettings(inRect);
        }
    }
}
