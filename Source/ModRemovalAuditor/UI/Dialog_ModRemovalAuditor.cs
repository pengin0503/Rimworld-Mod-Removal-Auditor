using System;
using System.Collections.Generic;
using System.Text;
using ModRemovalAuditor.Audit;
using RimWorld;
using UnityEngine;
using Verse;

namespace ModRemovalAuditor.UI
{
    /// <summary>
    /// Audit window. The game is force-paused while it is open; the scan advances a bounded batch per
    /// frame in <see cref="WindowUpdate"/>. Closing the window cancels any running scan.
    /// </summary>
    public sealed class Dialog_ModRemovalAuditor : Window
    {
        private const float ListWidth = 340f;
        private const float RowHeight = 26f;
        private const float Gap = 10f;
        private const float JumpButtonWidth = 90f;

        private static readonly Color DetectedColor = new Color(1f, 0.72f, 0.35f);
        private static readonly Color IncompleteColor = new Color(1f, 0.92f, 0.45f);
        private static readonly Color NeutralColor = new Color(0.72f, 0.82f, 0.9f);
        private static readonly Color DimColor = new Color(0.65f, 0.65f, 0.65f);

        private readonly List<ModEntry> mods = new List<ModEntry>();
        private readonly List<ModEntry> visibleMods = new List<ModEntry>();
        private readonly HashSet<ModKey> selected = new HashSet<ModKey>();
        private readonly List<ResultRow> rows = new List<ResultRow>();

        private string search = string.Empty;
        private string appliedSearch;
        private Vector2 modScroll;
        private Vector2 resultScroll;
        private AuditSession session;
        private AuditReport report;
        private bool reportFromCache;
        private string statusMessage;
        private float rowsBuiltForWidth = -1f;

        public Dialog_ModRemovalAuditor()
        {
            forcePause = true;
            doCloseX = true;
            doCloseButton = false;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            resizeable = true;
            draggable = true;
            onlyOneOfTypeAllowed = true;
            preventCameraMotion = true;
            optionalTitle = "MRA_ModName".Translate();
        }

        public override Vector2 InitialSize => new Vector2(Mathf.Min(1100f, Verse.UI.screenWidth - 40f), Mathf.Min(760f, Verse.UI.screenHeight - 40f));

        public override void PreOpen()
        {
            base.PreOpen();
            LoadMods();
            AuditResultCache.TryRestore(out report, selected);
            reportFromCache = report != null;
            // Drop selections of mods that are no longer running.
            selected.RemoveWhere(k => !mods.Exists(m => m.Key == k));
            rowsBuiltForWidth = -1f;
        }

        public override void PreClose()
        {
            base.PreClose();
            if (session != null && session.State == ScanState.Scanning)
            {
                session.Cancel();
            }
            session = null;
        }

        public override void WindowUpdate()
        {
            base.WindowUpdate();
            if (session == null || session.State != ScanState.Scanning)
            {
                return;
            }
            session.Advance(AuditSession.DefaultNodeBudget, AuditSession.DefaultTimeBudgetMs);
            switch (session.State)
            {
                case ScanState.Complete:
                    report = session.Report;
                    reportFromCache = false;
                    statusMessage = null;
                    AuditResultCache.Store(report, selected);
                    session = null;
                    rowsBuiltForWidth = -1f;
                    resultScroll = Vector2.zero;
                    break;
                case ScanState.Failed:
                    statusMessage = "MRA_ScanFailed".Translate(session.FailureMessage ?? "?").Resolve();
                    session = null;
                    break;
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Rect listRect = new Rect(inRect.x, inRect.y, Mathf.Min(ListWidth, inRect.width * 0.4f), inRect.height);
            Rect resultRect = new Rect(listRect.xMax + Gap, inRect.y, inRect.width - listRect.width - Gap, inRect.height);
            DrawModList(listRect);
            DrawResults(resultRect);
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }

        // ---------------------------------------------------------------- mod list

        private void LoadMods()
        {
            mods.Clear();
            List<ModContentPack> running = LoadedModManager.RunningModsListForReading;
            for (int i = 0; i < running.Count; i++)
            {
                ModContentPack pack = running[i];
                if (!GameScan.IsSelectable(pack))
                {
                    continue;
                }
                mods.Add(new ModEntry(pack));
            }
            mods.Sort((a, b) =>
            {
                int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.CompareOrdinal(a.Key.Value, b.Key.Value);
            });
            appliedSearch = null;
        }

        private void RefreshFilter()
        {
            if (appliedSearch == search)
            {
                return;
            }
            appliedSearch = search;
            visibleMods.Clear();
            string needle = (search ?? string.Empty).Trim().ToLowerInvariant();
            for (int i = 0; i < mods.Count; i++)
            {
                if (needle.Length == 0 || mods[i].SearchText.Contains(needle))
                {
                    visibleMods.Add(mods[i]);
                }
            }
        }

        private void DrawModList(Rect rect)
        {
            bool scanning = session != null && session.State == ScanState.Scanning;
            float y = rect.y;
            Widgets.Label(new Rect(rect.x, y, rect.width, 24f), "MRA_SelectMods".Translate());
            y += 26f;
            Rect searchRect = new Rect(rect.x, y, rect.width, 28f);
            search = Widgets.TextField(searchRect, search);
            if (string.IsNullOrEmpty(search))
            {
                GUI.color = DimColor;
                Widgets.Label(searchRect.ContractedBy(4f, 4f), "MRA_SearchHint".Translate());
                GUI.color = Color.white;
            }
            y += 32f;
            RefreshFilter();

            float half = (rect.width - 6f) / 2f;
            if (Widgets.ButtonText(new Rect(rect.x, y, half, 26f), "MRA_SelectVisible".Translate(), active: !scanning) && !scanning)
            {
                for (int i = 0; i < visibleMods.Count; i++)
                {
                    selected.Add(visibleMods[i].Key);
                }
            }
            if (Widgets.ButtonText(new Rect(rect.x + half + 6f, y, half, 26f), "MRA_ClearSelection".Translate(), active: !scanning) && !scanning)
            {
                selected.Clear();
            }
            y += 30f;

            float bottomHeight = 70f;
            Rect outRect = new Rect(rect.x, y, rect.width, rect.yMax - y - bottomHeight);
            Widgets.DrawMenuSection(outRect);
            Rect inner = outRect.ContractedBy(2f);
            Rect viewRect = new Rect(0f, 0f, inner.width - 16f, visibleMods.Count * RowHeight * 1.6f);
            Widgets.BeginScrollView(inner, ref modScroll, viewRect);
            float rowH = RowHeight * 1.6f;
            int first = Mathf.Max(0, Mathf.FloorToInt(modScroll.y / rowH));
            int last = Mathf.Min(visibleMods.Count - 1, Mathf.CeilToInt((modScroll.y + inner.height) / rowH));
            for (int i = first; i <= last; i++)
            {
                ModEntry mod = visibleMods[i];
                Rect row = new Rect(0f, i * rowH, viewRect.width, rowH);
                if (i % 2 == 1)
                {
                    Widgets.DrawLightHighlight(row);
                }
                Widgets.DrawHighlightIfMouseover(row);
                bool on = selected.Contains(mod.Key);
                bool before = on;
                Widgets.Checkbox(row.x + 2f, row.y + (rowH - 24f) / 2f, ref on, 24f, disabled: scanning);
                Rect label = new Rect(row.x + 30f, row.y + 2f, row.width - 32f, 22f);
                Text.WordWrap = false;
                Widgets.Label(label, mod.Name.Truncate(label.width));
                Text.Font = GameFont.Tiny;
                GUI.color = DimColor;
                Widgets.Label(new Rect(label.x, label.y + 20f, label.width, 18f), mod.PackageIdPlayerFacing.Truncate(label.width));
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                Text.WordWrap = true;
                TooltipHandler.TipRegion(row, mod.Name + "\n" + mod.PackageIdPlayerFacing);
                if (!scanning && Widgets.ButtonInvisible(new Rect(label.x, row.y, label.width, row.height), doMouseoverSound: false))
                {
                    on = !on;
                }
                if (!scanning && on != before)
                {
                    if (on)
                    {
                        selected.Add(mod.Key);
                    }
                    else
                    {
                        selected.Remove(mod.Key);
                    }
                }
            }
            Widgets.EndScrollView();

            y = outRect.yMax + 6f;
            Widgets.Label(new Rect(rect.x, y, rect.width, 24f), "MRA_SelectedCount".Translate(selected.Count, mods.Count));
            y += 26f;
            Rect button = new Rect(rect.x, y, rect.width, 34f);
            if (scanning)
            {
                if (Widgets.ButtonText(button, "MRA_Cancel".Translate()))
                {
                    session.Cancel();
                    session = null;
                    statusMessage = "MRA_ScanCancelled".Translate().Resolve();
                }
            }
            else
            {
                bool canScan = selected.Count > 0 && AuditEntryPoint.CanOpen;
                string label = report != null ? "MRA_Rescan".Translate() : "MRA_Scan".Translate();
                if (Widgets.ButtonText(button, label, active: canScan) && canScan)
                {
                    StartScan();
                }
            }
        }

        private void StartScan()
        {
            List<AuditTarget> targets = new List<AuditTarget>();
            for (int i = 0; i < mods.Count; i++)
            {
                if (selected.Contains(mods[i].Key))
                {
                    targets.Add(GameScan.BuildTarget(mods[i].Pack));
                }
            }
            StartScan(targets);
        }

        /// <summary>Starts a new scan for the given targets, replacing any previous result.</summary>
        public void StartScan(IReadOnlyList<AuditTarget> targets)
        {
            if (targets == null || targets.Count == 0)
            {
                return;
            }
            session?.Cancel();
            report = null;
            reportFromCache = false;
            statusMessage = null;
            rows.Clear();
            AuditResultCache.Clear();
            session = GameScan.CreateSession(targets);
            session.Start();
        }

        // ---------------------------------------------------------------- results

        private void DrawResults(Rect rect)
        {
            Widgets.DrawMenuSection(rect);
            Rect inner = rect.ContractedBy(8f);
            float y = inner.y;

            // The disclaimer is always visible, whatever the result.
            GUI.color = IncompleteColor;
            string disclaimer = "MRA_Disclaimer".Translate();
            float dh = Text.CalcHeight(disclaimer, inner.width);
            Widgets.Label(new Rect(inner.x, y, inner.width, dh), disclaimer);
            GUI.color = Color.white;
            y += dh + 6f;
            Widgets.DrawLineHorizontal(inner.x, y, inner.width);
            y += 6f;
            Rect body = new Rect(inner.x, y, inner.width, inner.yMax - y);

            if (session != null && session.State == ScanState.Scanning)
            {
                DrawProgress(body);
                return;
            }
            if (report == null)
            {
                string text = statusMessage ?? "MRA_NoReportYet".Translate().Resolve();
                Widgets.Label(body, text);
                return;
            }
            DrawReport(body);
        }

        private void DrawProgress(Rect rect)
        {
            ScanProgress p = session.Progress;
            string scope = p.CurrentScope != null ? ("MRA_Scope_" + p.CurrentScope).Translate().Resolve() : string.Empty;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 24f), "MRA_Scanning".Translate(scope, p.SourceIndex + 1, p.SourceCount));
            Widgets.Label(new Rect(rect.x, rect.y + 26f, rect.width, 24f), "MRA_ScannedUnits".Translate(p.UnitsProcessed.ToString("N0")));
            float pct = p.SourceCount == 0 ? 0f : Mathf.Clamp01((float)p.SourceIndex / p.SourceCount);
            Widgets.FillableBar(new Rect(rect.x, rect.y + 54f, rect.width, 18f), pct);
            GUI.color = DimColor;
            Widgets.Label(new Rect(rect.x, rect.y + 78f, rect.width, 48f), "MRA_ScanningHint".Translate());
            GUI.color = Color.white;
        }

        private void DrawReport(Rect rect)
        {
            float width = rect.width - 16f;
            if (Math.Abs(rowsBuiltForWidth - width) > 0.5f)
            {
                BuildRows(width);
            }
            float total = rows.Count == 0 ? 0f : rows[rows.Count - 1].Y + rows[rows.Count - 1].Height;
            Rect viewRect = new Rect(0f, 0f, width, total);
            Widgets.BeginScrollView(rect, ref resultScroll, viewRect);
            float top = resultScroll.y;
            float bottom = top + rect.height;
            int start = FirstVisibleRow(top);
            for (int i = start; i < rows.Count; i++)
            {
                ResultRow row = rows[i];
                if (row.Y > bottom)
                {
                    break;
                }
                DrawRow(row, new Rect(0f, row.Y, width, row.Height), i);
            }
            Widgets.EndScrollView();
        }

        private int FirstVisibleRow(float top)
        {
            int lo = 0;
            int hi = rows.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (rows[mid].Y + rows[mid].Height < top)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            return lo;
        }

        private void DrawRow(ResultRow row, Rect r, int index)
        {
            switch (row.Kind)
            {
                case RowKind.Heading:
                    Text.Font = GameFont.Medium;
                    Widgets.Label(r, row.Text);
                    Text.Font = GameFont.Small;
                    break;
                case RowKind.SubHeading:
                    GUI.color = NeutralColor;
                    Widgets.Label(r, row.Text);
                    GUI.color = Color.white;
                    break;
                case RowKind.Text:
                    GUI.color = row.Color;
                    Widgets.Label(r, row.Text);
                    GUI.color = Color.white;
                    if (!string.IsNullOrEmpty(row.Tooltip))
                    {
                        TooltipHandler.TipRegion(r, row.Tooltip);
                    }
                    break;
                case RowKind.Finding:
                    DrawFinding(row, r, index);
                    break;
            }
        }

        private void DrawFinding(ResultRow row, Rect r, int index)
        {
            if (index % 2 == 0)
            {
                Widgets.DrawLightHighlight(r);
            }
            Widgets.DrawHighlightIfMouseover(r);
            Rect textRect = new Rect(r.x + 4f, r.y, r.width - JumpButtonWidth - 12f, r.height);
            GUI.color = row.Color;
            Widgets.Label(textRect, row.Text);
            GUI.color = Color.white;
            if (!string.IsNullOrEmpty(row.Tooltip))
            {
                TooltipHandler.TipRegion(textRect, row.Tooltip);
            }

            Rect jumpRect = new Rect(r.xMax - JumpButtonWidth - 4f, r.y + 2f, JumpButtonWidth, Mathf.Min(28f, r.height - 4f));
            int cursor = row.JumpCursor;
            bool canJump = AuditNavigation.TryFindTarget(row.Finding, ref cursor, out _, out string reasonKey);
            if (canJump)
            {
                string label = row.Finding.JumpHandles.Count > 1 ? "MRA_JumpNext".Translate() : "MRA_Jump".Translate();
                if (Widgets.ButtonText(jumpRect, label))
                {
                    int c = row.JumpCursor;
                    Close(doCloseSound: false);
                    if (AuditNavigation.TryJump(row.Finding, ref c))
                    {
                        row.JumpCursor = c;
                    }
                }
                TooltipHandler.TipRegion(jumpRect, "MRA_JumpTip".Translate(row.Finding.JumpHandles.Count));
            }
            else
            {
                Widgets.ButtonText(jumpRect, "MRA_Jump".Translate(), active: false);
                TooltipHandler.TipRegion(jumpRect, reasonKey.Translate());
            }
        }

        private void BuildRows(float width)
        {
            rows.Clear();
            rowsBuiltForWidth = width;
            float y = 0f;

            if (reportFromCache)
            {
                Add(ref y, width, RowKind.Text, "MRA_CachedReport".Translate(report.CreatedUtc.ToLocalTime().ToString("g")).Resolve(), IncompleteColor);
            }
            else
            {
                Add(ref y, width, RowKind.Text, "MRA_ReportTime".Translate(report.CreatedUtc.ToLocalTime().ToString("g"), report.UnitsProcessed.ToString("N0")).Resolve(), DimColor);
            }

            // Per-mod summary.
            Add(ref y, width, RowKind.Heading, "MRA_SummaryHeading".Translate().Resolve(), Color.white);
            for (int i = 0; i < report.Targets.Count; i++)
            {
                AuditTarget target = report.Targets[i];
                ModVerdict verdict = report.VerdictFor(target.Key);
                Color color = verdict == ModVerdict.Detected ? DetectedColor : verdict == ModVerdict.Incomplete ? IncompleteColor : NeutralColor;
                long instances = 0;
                for (int f = 0; f < report.Findings.Count; f++)
                {
                    if (report.Findings[f].Target == target.Key)
                    {
                        instances += report.Findings[f].Instances;
                    }
                }
                string verdictText = ("MRA_Verdict_" + verdict).Translate(instances.ToString("N0")).Resolve();
                Add(ref y, width, RowKind.SubHeading, target.DisplayName + "  (" + target.PackageIdPlayerFacing + ")", Color.white);
                Add(ref y, width, RowKind.Text, "  " + verdictText, color, ("MRA_VerdictTip_" + verdict).Translate().Resolve());
                foreach (string note in TargetNotes(target))
                {
                    Add(ref y, width, RowKind.Text, "  • " + note, DimColor);
                }
            }
            if (report.UnattributedInstances > 0)
            {
                StringBuilder sb = new StringBuilder();
                int shown = 0;
                for (int i = 0; i < report.Unattributed.Count && shown < 15; i++, shown++)
                {
                    UnattributedEntry u = report.Unattributed[i];
                    sb.AppendLine(u.DefLabel + " (" + u.DefId.DefName + "): " + u.Instances.ToString("N0"));
                }
                if (report.Unattributed.Count > shown)
                {
                    sb.AppendLine("…");
                }
                Add(ref y, width, RowKind.Text, "MRA_Unattributed".Translate(report.UnattributedInstances.ToString("N0"), report.Unattributed.Count).Resolve(), IncompleteColor, sb.ToString());
            }

            // Coverage.
            Add(ref y, width, RowKind.Heading, "MRA_CoverageHeading".Translate().Resolve(), Color.white);
            for (int i = 0; i < report.Coverage.Count; i++)
            {
                CoverageEntry c = report.Coverage[i];
                string scope = ("MRA_Scope_" + c.ScopeId).Translate().Resolve();
                string status = ("MRA_Status_" + c.Status).Translate().Resolve();
                string line = "MRA_CoverageLine".Translate(scope, status).Resolve();
                if (!string.IsNullOrEmpty(c.Warning))
                {
                    line += " — " + c.Warning;
                }
                Color color = c.Status == CoverageStatus.Checked ? NeutralColor : c.Status == CoverageStatus.NotCovered ? DimColor : IncompleteColor;
                Add(ref y, width, RowKind.Text, "  " + line, color, ("MRA_ScopeTip_" + c.ScopeId).Translate().Resolve());
            }
            if (report.FindingsTruncated)
            {
                Add(ref y, width, RowKind.Text, "MRA_FindingsTruncated".Translate().Resolve(), IncompleteColor);
            }

            // Findings, grouped by mod then category.
            Add(ref y, width, RowKind.Heading, "MRA_FindingsHeading".Translate().Resolve(), Color.white);
            if (report.Findings.Count == 0)
            {
                Add(ref y, width, RowKind.Text, "MRA_NoFindings".Translate().Resolve(), NeutralColor);
            }
            ModKey? currentTarget = null;
            AuditCategory? currentCategory = null;
            for (int i = 0; i < report.Findings.Count; i++)
            {
                AuditFinding f = report.Findings[i];
                if (currentTarget != f.Target)
                {
                    currentTarget = f.Target;
                    currentCategory = null;
                    Add(ref y, width, RowKind.SubHeading, TargetName(f.Target), Color.white);
                }
                if (currentCategory != f.Category)
                {
                    currentCategory = f.Category;
                    Add(ref y, width, RowKind.Text, ("MRA_Category_" + f.Category).Translate().Resolve(), NeutralColor);
                }
                rows.Add(MakeFindingRow(f, ref y, width));
            }
        }

        private ResultRow MakeFindingRow(AuditFinding f, ref float y, float width)
        {
            string counts = f.Category == AuditCategory.Terrain
                ? "MRA_CountCells".Translate(f.Instances.ToString("N0")).Resolve()
                : f.Items != f.Instances
                    ? "MRA_CountInstancesItems".Translate(f.Instances.ToString("N0"), f.Items.ToString("N0")).Resolve()
                    : "MRA_CountInstances".Translate(f.Instances.ToString("N0")).Resolve();
            string location = f.GroupKey == AuditAccumulator.OverflowGroupKey ? "MRA_Loc_Overflow".Translate().Resolve() : f.LocationText;
            string confidence = ("MRA_Confidence_" + f.Confidence).Translate().Resolve();
            string text = f.DefLabel + "  [" + f.DefId.DefName + "]  " + counts + "\n" + location + "   · " + confidence;

            StringBuilder tip = new StringBuilder();
            tip.AppendLine(f.DefLabel + " — " + f.DefId.DefType + "/" + f.DefId.DefName);
            tip.AppendLine("MRA_TipSource".Translate(TargetName(f.Target)).Resolve());
            tip.AppendLine(("MRA_Reason_" + f.Reason).Translate().Resolve());
            if (f.AlsoDeclaredBy.Count > 0)
            {
                List<string> names = new List<string>();
                for (int i = 0; i < f.AlsoDeclaredBy.Count; i++)
                {
                    names.Add(TargetName(f.AlsoDeclaredBy[i]));
                }
                tip.AppendLine("MRA_TipAlsoDeclared".Translate(string.Join(", ", names)).Resolve());
            }
            if (f.HandlesTruncated)
            {
                tip.AppendLine("MRA_TipHandlesTruncated".Translate(f.JumpHandles.Count).Resolve());
            }

            float textWidth = width - JumpButtonWidth - 16f;
            float h = Mathf.Max(RowHeight * 2f, Text.CalcHeight(text, textWidth) + 4f);
            ResultRow row = new ResultRow(RowKind.Finding, y, h, text, f.Confidence == OriginConfidence.Confirmed ? Color.white : IncompleteColor, tip.ToString().TrimEnd(), f);
            y += h;
            return row;
        }

        private IEnumerable<string> TargetNotes(AuditTarget target)
        {
            if (target.UnscannedDefCount > 0)
            {
                List<string> parts = new List<string>();
                foreach (KeyValuePair<string, int> kv in target.DeclaredDefCounts)
                {
                    if (!target.ScannedDefTypes.Contains(kv.Key))
                    {
                        parts.Add(kv.Key + " ×" + kv.Value);
                    }
                }
                parts.Sort(StringComparer.Ordinal);
                if (parts.Count > 8)
                {
                    int more = parts.Count - 8;
                    parts.RemoveRange(8, more);
                    parts.Add("…");
                }
                yield return "MRA_NoteUnscannedDefs".Translate(target.UnscannedDefCount, string.Join(", ", parts)).Resolve();
            }
            if (target.HasAssemblies)
            {
                yield return "MRA_NoteAssemblies".Translate().Resolve();
            }
            if (target.DependentMods.Count > 0)
            {
                yield return "MRA_NoteDependents".Translate(string.Join(", ", target.DependentMods)).Resolve();
            }
        }

        private string TargetName(ModKey key)
        {
            for (int i = 0; i < report.Targets.Count; i++)
            {
                if (report.Targets[i].Key == key)
                {
                    return report.Targets[i].DisplayName;
                }
            }
            for (int i = 0; i < mods.Count; i++)
            {
                if (mods[i].Key == key)
                {
                    return mods[i].Name;
                }
            }
            return key.Value;
        }

        private void Add(ref float y, float width, RowKind kind, string text, Color color, string tooltip = null)
        {
            GameFont font = kind == RowKind.Heading ? GameFont.Medium : GameFont.Small;
            GameFont old = Text.Font;
            Text.Font = font;
            float h = Mathf.Max(kind == RowKind.Heading ? 34f : 22f, Text.CalcHeight(text, width) + 2f);
            Text.Font = old;
            rows.Add(new ResultRow(kind, y, h, text, color, tooltip, null));
            y += h;
        }

        private enum RowKind
        {
            Heading,
            SubHeading,
            Text,
            Finding,
        }

        private sealed class ResultRow
        {
            public ResultRow(RowKind kind, float y, float height, string text, Color color, string tooltip, AuditFinding finding)
            {
                Kind = kind;
                Y = y;
                Height = height;
                Text = text;
                Color = color;
                Tooltip = tooltip;
                Finding = finding;
            }

            public RowKind Kind { get; }

            public float Y { get; }

            public float Height { get; }

            public string Text { get; }

            public Color Color { get; }

            public string Tooltip { get; }

            public AuditFinding Finding { get; }

            public int JumpCursor { get; set; }
        }

        private sealed class ModEntry
        {
            public ModEntry(ModContentPack pack)
            {
                Pack = pack;
                Key = ModKey.Parse(pack.PackageId);
                Name = string.IsNullOrEmpty(pack.Name) ? pack.PackageIdPlayerFacing : pack.Name;
                PackageIdPlayerFacing = pack.PackageIdPlayerFacing ?? pack.PackageId;
                SearchText = (Name + " " + PackageIdPlayerFacing).ToLowerInvariant();
            }

            public ModContentPack Pack { get; }

            public ModKey Key { get; }

            public string Name { get; }

            public string PackageIdPlayerFacing { get; }

            public string SearchText { get; }
        }
    }

    /// <summary>
    /// Keeps the last completed report in memory only (never written to the save or to disk) so the
    /// result survives closing the window to jump to a location. Game objects are only referenced
    /// weakly; the cache is discarded when a different game is loaded.
    /// </summary>
    internal static class AuditResultCache
    {
        private static System.WeakReference<Game> game;
        private static AuditReport report;
        private static readonly List<ModKey> Selection = new List<ModKey>();

        public static void Store(AuditReport value, IEnumerable<ModKey> selection)
        {
            game = new System.WeakReference<Game>(Current.Game);
            report = value;
            Selection.Clear();
            Selection.AddRange(selection);
        }

        public static bool TryRestore(out AuditReport value, HashSet<ModKey> selectionOut)
        {
            value = null;
            if (report == null || game == null || !game.TryGetTarget(out Game g) || g != Current.Game)
            {
                Clear();
                return false;
            }
            value = report;
            for (int i = 0; i < Selection.Count; i++)
            {
                selectionOut.Add(Selection[i]);
            }
            return true;
        }

        public static void Clear()
        {
            game = null;
            report = null;
            Selection.Clear();
        }
    }
}
