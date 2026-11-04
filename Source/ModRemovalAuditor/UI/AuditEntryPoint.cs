using RimWorld;
using UnityEngine;
using Verse;

namespace ModRemovalAuditor.UI
{
    /// <summary>Launch paths for the audit window. Neither path keeps state between frames.</summary>
    public static class AuditEntryPoint
    {
        public static bool CanOpen => Current.ProgramState == ProgramState.Playing && Current.Game != null;

        public static void Open()
        {
            if (!CanOpen)
            {
                Messages.Message("MRA_NeedLoadedGame".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            Dialog_ModRemovalAuditor existing = Find.WindowStack.WindowOfType<Dialog_ModRemovalAuditor>();
            if (existing != null)
            {
                Find.WindowStack.TryRemove(existing);
                return;
            }
            Find.WindowStack.Add(new Dialog_ModRemovalAuditor());
        }

        public static void DrawSettings(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Label("MRA_SettingsIntro".Translate());
            listing.Gap();
            if (CanOpen)
            {
                if (listing.ButtonText("MRA_OpenAuditor".Translate()))
                {
                    // Close the options/settings stack first so the audit window is not hidden behind it.
                    Find.WindowStack.TryRemove(typeof(Dialog_ModSettings), doCloseSound: false);
                    Find.WindowStack.TryRemove(typeof(Dialog_Options), doCloseSound: false);
                    Open();
                }
            }
            else
            {
                listing.Label("MRA_NeedLoadedGame".Translate());
            }
            listing.End();
        }
    }

    /// <summary>Worker for the minimized bottom-bar button. Opens or closes the audit window.</summary>
    public sealed class MainButtonWorker_OpenModRemovalAuditor : MainButtonWorker
    {
        public override void Activate()
        {
            AuditEntryPoint.Open();
        }
    }
}
