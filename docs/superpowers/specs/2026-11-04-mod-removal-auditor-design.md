# Mod Removal Auditor — Design Specification

**Date:** 2026-11-04  
**Status:** Implemented as v1.1, covering the v1.0 scopes plus the Phase 2 pawn data and record scopes (compiled against 1.7.4985; in-game verification pending, see README)  
**Game:** RimWorld 1.7 / Waylith / .NET 10  
**Working title:** Mod Removal Auditor (MRA)  
**Proposed packageId:** `pengin0503.modremovalauditor` (must confirm before release)

## 1. Intent

Allow a player to select one or multiple **currently enabled** RimWorld mods and determine which of their content is still present in the **currently loaded save**. Display counts, holders and locations before disabling a mod. Make a useful, non-destructive pre-removal audit rather than claim universal safe removal.

The immediate problem is large mod sets (~270 mods) where manually checking all items before removal is impractical.

## 2. Goals

1. One-click, on-demand audit from an in-game window, with current mod selection/search and multi-selection.
2. Identify content by **source mod identity** (`packageId`, case-insensitive), avoiding attribution by item name alone.
3. Scan spawned things, buildings, terrain, nested containers/equipment and non-map holdings; report quantities and precise navigable locations when known.
4. Distinguish **found**, **not found in covered scopes**, **uninspected**, **unattributed**, and **scan failed**. Never label a mod safe to remove.
5. User-interface Japanese and English parity.
6. Minimal idle overhead: no ongoing tick scanning, no automatic destructive operations, no Harmony patches unless a concrete API limitation is demonstrated.
7. Allow re-scan and jump to found map items; report off-map items with holder/caravan info instead of misleading map links.
8. Avoid data changes on scan and avoid adding persistent saved state to colony files.

## 3. Non-goals / anti-goals

- No automatic uninstallation, deactivation, destruction of items, conversion of defs, save cleanup, or automatic replacement by vanilla defs.
- No guarantee of zero dangling references or compatibility after mod removal.
- No identification of already-removed mods via live Defs; offline save inspection is a **separate future feature**.
- No blanket reflection crawl through every mod's opaque custom classes, unsafe patching, or reliance on proprietary RimWorld DLLs shipped in the repository.
- No duplicated features of a mod manager (download, sorting, updates, dependency resolution).

## 4. Approaches considered

### A. UI-only item label / search
Simplest, but requires a manual search across every map/container and cannot audit a whole mod. Rejected as the primary solution.

### B. In-game read-only scan, on demand (**recommended / proposed**)
Active mods provide ownership metadata. Traverse known loaded game data collections once per request, attribute present instances, and show coverage. Good usability, no disk parser. This is the v1.0 design.

### C. Offline parser of `.rws` save files
Potentially useful for unavailable mods, but requires save version parsing, secure handling of custom payloads, more compatibility work and different UX. Defer as a separate optional tool/version after in-game proof.

## 5. UX

Entry: in-game utility button (exact placement determined against 1.7 UI API; no intrusive full-screen overlay). A settings option may provide another launch path.

Window contains:

- Searchable list of loaded non-Core mods (name + packageId), checkbox multi-select, and `Scan` button.
- Progress: stage name, scanned nodes / estimated count when determinable, and cancel; scans run while the game is paused.
- Results grouped by mod and then category (`Items`, `Buildings`, `Terrain`, `Contained / Equipped`, `World / Caravan`, `Pawn data`, `Other`). Category presence depends on implemented coverage.
- Each entry: display name, Def name, source packageId, number of **instances**, number of **items** (stackCount where applicable), location/holder, source confidence (`Confirmed`, `Uncertain`, `Unknown`).
- Action `Jump` only if the map/cell/object is still valid. No action for inaccessible locations. `Re-scan` replaces stale results, old output not persisted.
- A coverage box with `Checked`, `Not covered`, `Skipped/failed` per scope. Empty results say **"No items detected within the checked scopes; safe removal not guaranteed"**.
- JP/EN translation files using same message keys, including all warnings.

## 6. Data model and provenance

`ModKey` = normalized lower-case `packageId`, display label stays separate. Mod name is not used for matching. `DefIdentity` = `(def runtime type, defName)`; use a runtime Def reference for the scan and a serializable value object only in ephemeral results.

Source attribution order:

1. Explicit Def origin (`Def.modContentPack`, if available in 1.7), matched to loaded `ModContentPack.PackageId`.
2. Loaded package's `AllDefs` provenance if primary unavailable and validated equivalent.
3. If neither identifies a unique source, mark unknown/ambiguous rather than guess from `defName`, texture path or translated label.

A cross-mod XML patch modifying a Def does **not** make it owned by the patching mod. A C# mod creating runtime Defs can lack reliable provenance: report as uncertainty and state it in coverage.

`AuditFinding`: target mod key, category, Def identity, optional thing ID, object instance count, `stackCount`-based item count, logical holder path, optional map/cell, confidence. Deduplicate by object identity (reference equality) across overlapping enumerators, and distinguish contained item from its containing Pawn/building.

`CoverageEntry`: scope ID, status (`Checked`/`Partial`/`NotCovered`/`Failed`), nodes processed and warning text.

`ScanReport`: date/time, selected ModKeys, findings, coverage, errors, game/map identifier (transient only); not Scribed to game save.

## 7. Coverage and scope

| Data source | v1.0 | Principle |
|---|---|---|
| Spawned map Things / plants / structures / loose items | Required | Scan all currently loaded maps, not only active view |
| Inventory, apparel, equipment, nested ThingOwner inside structures | Required | Recursive traversal with identity guard and cycles protection |
| Caravan members and carried items | Required | World-level caravan collections, avoid duplicates with map pawns |
| Unspawned Pawns held by containers / transporters | Required | Discover through loaded holders |
| Map terrain | Required | Inspect terrain per cell, summarize adjacent or total count; no per-tile result object explosion |
| World pawns not on map/caravan | Required if exposed by 1.7 public API; if not, mark Partial and include a constrained follow-on implementation task before declaring complete |
| Pawn hediff / genes / traits / abilities | Phase 2 (v1.1) | Separate Def-based scanners; context can include pawn history and missing DLC |
| Bills, quests, research progress, factions, ideology, world objects | Phase 2 (v1.1), separately scoped | Avoid false clean verdict until covered |
| Opaque mod-specific save fields, procedural runtime defs, unloaded maps and unavailable mods | Explicitly NotCovered | Do not pretend complete detection |

World and mod-specific data may be saved even if no `Thing` remains, so risk assessment is **coverage-specific**, not binary.

## 8. Scanning and performance

- Do not scan during `Tick` and never run in background. Open/paused window advances a bounded batch on the main thread through `WindowUpdate` or the equivalent 1.7-supported UI update method (verify against actual 1.7 assemblies).
- Stop processing each frame at the earlier of approx 300 nodes or 2 ms of scanning (performance target, calibrated under test). No per-Thing LINQ/allocations in critical loops; aggregate summaries and bounded list of location rows.
- One scan session shared across multi-selected mods: attribute each game node once, not 270 passes.
- Pause world simulation during scan; do not attempt to read live mutable engine collections from worker threads.
- Cancel frees transient buffers, closes iterators and reports a `Cancelled` rather than a completed no-results state.
- If the scope throws, isolate it, log once with context and mark that scope `Failed`. Continue other scopes where feasible, never silently skip.
- On closing the window clear scanning state; no persistent listeners or Harmony patches.
- Benchmark with many mods, ≥2 maps, nested containers, many stacked items; 0 additional per-tick work when window closed.

## 9. Risk communication

Display non-authoritative categories:

- `Detected`: at least one target-sourced reference/Thing in scanned scope; investigate before removal.
- `No detected instances`: no findings **within successfully scanned scopes**. Never say 'safe to uninstall'.
- `Incomplete`: one or more relevant scopes partial/unknown/failed, including unsupported mod-specific custom serialization.

Always advise separate backup and disable/load test. Metadata-only dependency check based on known loaded mod metadata may warn but can miss dynamic DLL dependencies; not a safety guarantee.

## 10. Acceptance criteria

1. Real 1.7 DLL compiles with `net10.0` referencing 1.7 `Assembly-CSharp.dll`, `Waylith.dll` and `UnityStubs.dll` as required; no redistributed proprietary game assemblies.
2. A demo test mod with two spawned Things, one building, stackable item, two nested holder items, a caravan item, and modded terrain is attributed to the right package ID, including when names collide across mods.
3. Test with two maps, one active and one inactive; neither is silently missed.
4. Repeated enumerators for the same Thing do not double count objects or stack count.
5. A modified Thing owned by a different mod via XML patch is **not** attributed to the patching mod.
6. A runtime-generated Def without reliable provenance is reported unknown, not silently counted as safely absent.
7. Missing holder/caravan/unknown map results are represented without broken jump buttons.
8. Pausing, cancelling, re-running and closing the window do not modify save data or leave long-lived subscriptions.
9. JS/keyboard/UI? No: text overflow, scrolling, 270 mod filtering and Japanese/English strings tested in-game.
10. End-to-end audit vs snapshot load succeeds in CI/local 1.7 harness; no new red errors in logs; report coverage accurately.

## 11. Constraints / dependencies / delivery

- Do not alter other mods' code or settings. Implement in an independent new repository when approved.
- Proposed structure: `About/About.xml`, `Languages/Japanese/Keyed/...`, `Languages/English/Keyed/...`, `1.7/Assemblies/...dll`, `Source/...`, `docs/superpowers/...`.
- Build tool: .NET 10 SDK. Use 1.7 game DLLs locally/controlled test runner; tests for pure model/aggregation run without game files. Prefer runtime API over reflection and Harmony.
- Author license, GitHub repository name, release ID and packageId remain decisions for creation/release. Do **not** claim repo or release already exists.

## 12. Open technical validation (not user policy questions)

Check the precise RimWorld 1.7 signatures for the window update hook, all-map enumeration, `ThingOwner` traversal, world pawn registry, terrain access and source `Def` metadata before implementation. If a category cannot be implemented without invasive hooks, mark it Partial/NotCovered rather than silently pretending it works.

## 13. Sources (consulted 2026-11-04)

- RimWorld Wiki: https://rimworldwiki.com/wiki/Modding_Tutorials/RimWorld_1.7_Mod_Updates
- RimWorld Wiki: https://rimworldwiki.com/wiki/Modding_Tutorials/Setting_up_a_solution
- ModContentPack reference (community): https://github.com/Dev-Jahn/rimworld-modding-reference/blob/main/02-modding-fundamentals/mod-structure.md
- Historical decompilation illustrating nested holders (not proof of 1.7 signature): https://github.com/josh-m/RW-Decompile/blob/master/Verse/MapPawns.cs
- Existing What’s That Mod provenance limitations: https://rimmods.com/mods/whats-that-mod