# Mod Removal Auditor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (native, recommended) or superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a read-only, on-demand RimWorld 1.7 MOD-removal audit tool that reports remaining content per selected loaded mod, counts and navigation without claiming complete uninstall safety.

**Architecture:** An in-game window controls a single bounded scan session. Independent source adapters enumerate game content as lightweight observations; a provenance index resolves Def ownership; an accumulator deduplicates observations, aggregates counts, and outputs findings and explicit coverage. No persistent game components, Harmony patches or save writes in v1.0.

**Tech Stack:** C# / .NET 10 / RimWorld 1.7 Assembly-CSharp + UnityStubs + Waylith / xUnit or equivalent pure unit tests / RimWorld 1.7 integration build and game log smoke tests.

**Spec:** `docs/superpowers/specs/2026-11-04-mod-removal-auditor-design.md`

## Global Constraints

- Target RimWorld **1.7 only**, target framework **net10.0**; packageId proposed as `pengin0503.modremovalauditor` pending release approval.
- No automatic removal, disabling, destructive cleanup or save serialization.
- UI Japanese and English have equal information.
- On-demand and paused only; no always-running game tick work, no Harmony unless documented as essential.
- Uninspected/unknown/custom mod data must remain visible as uncertainty; never report safe-to-remove.
- No proprietary game DLL committed or distributed. Build against local 1.7 game DLLs; isolated tests can run without them.
- Staged scope: v1.0 Things + holders + world/caravan + terrain; complex pawn/world Def references in v1.1 unless trivially available.

## Review Focus

1. Unattributed runtime-generated Def: cannot be misclassified as a target MOD absence (Task 2 & 7).
2. Same Thing reached by both map enumeration and ThingOwner traversal: counted once (Task 3).
3. Multiple maps and unspawned holder content: must not vanish just because current view is another map (Task 4).
4. Stale location when Thing is removed while result window is open: disable jump / offer re-scan (Task 6).
5. One scope throws or user cancels: clear status `Partial`/`Failed`/`Cancelled`, never a successful zero result (Task 5 & 7).

---

## Proposed file structure

```text
About/About.xml
1.7/Assemblies/ModRemovalAuditor.dll            # build output (not game dependencies)
Source/ModRemovalAuditor/ModRemovalAuditor.csproj
Source/ModRemovalAuditor/ModRemovalAuditorMod.cs
Source/ModRemovalAuditor/Audit/ModKey.cs
Source/ModRemovalAuditor/Audit/DefOriginIndex.cs
Source/ModRemovalAuditor/Audit/AuditModels.cs
Source/ModRemovalAuditor/Audit/AuditAccumulator.cs
Source/ModRemovalAuditor/Audit/AuditSession.cs
Source/ModRemovalAuditor/Audit/ScanAdapters.cs
Source/ModRemovalAuditor/UI/Dialog_ModRemovalAuditor.cs
Source/ModRemovalAuditor/UI/AuditNavigation.cs
Source/ModRemovalAuditor/UI/AuditEntryPoint.cs
Languages/Japanese/Keyed/ModRemovalAuditor.xml
Languages/English/Keyed/ModRemovalAuditor.xml
Tests/ModRemovalAuditor.Tests/ModRemovalAuditor.Tests.csproj
.github/workflows/build.yml
README.md
```

Pure data model/aggregator classes should have no engine references where practical. Scan adapters and UI are isolated game-facing classes. Adjust file names only when the actual 1.7 project APIs require it; record the change in the plan.

## Task 1: 1.7 project scaffold + API reconnaissance

**Files:** `About/About.xml`, `Source/ModRemovalAuditor/ModRemovalAuditor.csproj`, `Source/ModRemovalAuditor/ModRemovalAuditorMod.cs`, `README.md`, `docs/superpowers/...`.

**Interfaces:** Creates the `Verse.Mod` entry point and game reference properties used by later tasks; no scanner yet.

- [ ] **Step 1: Verify actual 1.7 API** against installed `Assembly-CSharp.dll`, `Waylith.dll` and `UnityStubs.dll`: find exact member names/signatures for `Def.modContentPack`, `ModContentPack`, `WindowUpdate`, map contents, `ThingOwner`, world pawns, caravan holdings, terrain retrieval.
- [ ] **Step 2: Create .NET 10 project and About.xml** with `supportedVersions=1.7`, Japanese/English visible name, optional non-invasive entry point placeholder. Use a configurable `RIMWORLD_17_DIR` property for game DLL references; `Private=false`.
- [ ] **Step 3: Run `dotnet build Source/ModRemovalAuditor/ModRemovalAuditor.csproj -c Release`** with `RIMWORLD_17_DIR` pointing to a valid game install; assert `1.7/Assemblies/ModRemovalAuditor.dll` is emitted and the build succeeds.
- [ ] **Step 4: Launch mod in isolated 1.7 profile** (prefer existing `RimWorld-for-mod-test` runner if available) and confirm no load exception / new red errors.
- [ ] **Step 5: Commit** `chore: scaffold net10 RimWorld 1.7 audit mod`.

## Task 2: Ownership matching and scan result data models

**Files:** `Audit/ModKey.cs`, `Audit/DefOriginIndex.cs`, `Audit/AuditModels.cs`, `Tests/ModRemovalAuditor.Tests/ProvenanceTests.cs`.

**Interfaces:** `ModKey.Parse(string packageId) -> ModKey`; `DefOriginIndex.Resolve(DefDescriptor definition) -> OriginResolution`; `OriginResolution` carries `ModKey?`, `Confirmed|Uncertain|Unknown`. `AuditObservation` carries `objectKey`, `DefIdentity`, `category`, `stackCount`, `holderPath`, optional map/cell.

- [ ] **Step 1: Add failing tests**: case-insensitive package IDs, two defs sharing label but different packages, Core exclusion, a patcher changing a foreign Def is not owner, generated unowned Def -> Unknown.
- [ ] **Step 2: Run `dotnet test Tests/ModRemovalAuditor.Tests -c Release --filter FullyQualifiedName~ProvenanceTests`**; expect failing assertions or compilation failure before implementation.
- [ ] **Step 3: Implement pure types and origin resolution**. A game-facing adapter reads 1.7 Def metadata; never assign provenance by translated label, XML patches or texture name.
- [ ] **Step 4: Re-run tests and verify green**; compile 1.7 assembly.
- [ ] **Step 5: Commit** `feat: add def origin and audit models`.

## Task 3: Deterministic aggregation and deduplication

**Files:** `Audit/AuditAccumulator.cs`, `Tests/ModRemovalAuditor.Tests/AggregationTests.cs`.

**Interfaces:** `AuditAccumulator.Add(AuditObservation observation, OriginResolution origin) -> void`; `BuildReport(ScanCoverage coverage) -> AuditReport`. Logical object IDs are unique by game object identity, distinct from display label; terrain count is processed as cell occurrences.

- [ ] **Step 1: Add failing tests**: one Thing observed twice -> one instance; stackCount 12 counted as 12 items; 2 same-def stacks -> 2 instances / proper sum; different holders/2 maps kept separately; many terrain cells aggregate without massive record list.
- [ ] **Step 2: Run the relevant filtered test suite** and observe failures.
- [ ] **Step 3: Implement accumulator** with reference-key or stable ID deduplication, bounded location rows, grouped summary and per-mod filter (one pass, many selected mods).
- [ ] **Step 4: Verify unit tests pass** and commit `feat: aggregate audited contents without duplicates`.

## Task 4: Map, nested holder, caravan, terrain adapters

**Files:** `Audit/ScanAdapters.cs`, `Tests/ModRemovalAuditor.Tests/TraversalTests.cs` and in-game fixture/commands as needed.

**Interfaces:** `IScanAdapter.Enumerate(ScanContext context) -> IEnumerator<AuditObservation>` plus scope status; separate adapters for `MapThing`, `NestedHolder`, `Caravan`, `WorldPawn`, `Terrain`.

- [ ] **Step 1: Add adapter contract tests** with fake maps/holders for nested inventory, equipment, apparel, double-linked references, cycles, two maps and missing owner.
- [ ] **Step 2: Run adapter tests, verify they fail**.
- [ ] **Step 3: Implement game-facing adapters**, using actual 1.7 map and ThingOwner API; scan pawn/body nested holdings without infinite recursion; include world pawns via verified API or explicitly flag `Partial` and complete a tightly scoped follow-on before v1.0 release.
- [ ] **Step 4: Run tests**, compile against 1.7 and test a minimal in-game fixture with spawned items/buildings, nested gear, caravan contents, and modded terrain. Confirm logs and output against known expected counts.
- [ ] **Step 5: Commit** `feat: traverse map and world content on demand`.

## Task 5: Bounded, cancellable main-thread audit session

**Files:** `Audit/AuditSession.cs`, `Tests/ModRemovalAuditor.Tests/SessionTests.cs`.

**Interfaces:** `AuditSession.Start(IReadOnlyList<ModKey> targets)`; `Advance(int nodeBudget, double timeBudgetMs) -> ScanProgress`; `Cancel()`; `Report` only valid on completed scan. No game background threading.

- [ ] **Step 1: Add failing tests**: repeated `Advance(3,...)` processes at most 3 observations per call, cancellation halts, thrown adapter marks Failed and allows next one, no successful completion on partial scan.
- [ ] **Step 2: Verify failing tests**.
- [ ] **Step 3: Implement session state machine** `Idle -> Scanning -> Complete / Cancelled / FailedOrPartial`; batch to earlier of 300 nodes or 2 ms target; one traversal for multiple mods; track coverage.
- [ ] **Step 4: Verify tests** and profile a large fixture; adjust budget based on measured frame delays, retaining deterministic node bound.
- [ ] **Step 5: Commit** `feat: add bounded cancellable scan sessions`.

## Task 6: In-game UI and navigation

**Files:** `UI/Dialog_ModRemovalAuditor.cs`, `UI/AuditNavigation.cs`, `UI/AuditEntryPoint.cs`, `ModRemovalAuditorMod.cs`, `Languages/{Japanese,English}/Keyed/ModRemovalAuditor.xml`.

**Interfaces:** `Dialog_ModRemovalAuditor.StartScan(IReadOnlyList<ModKey>)`; `AuditNavigation.TryJump(AuditFinding f) -> bool` validates map & object existence and reports unavailable locations.

- [ ] **Step 1: Define UI behavior tests/checklist**: 270 mod entries filter quickly, multi-select, sort names, scroll without clipping, scan progress/cancel, results grouped by category/holder, no false safety, invalid jump disabled, same text meaning JP/EN.
- [ ] **Step 2: Implement main screen and entry point**, make it accessible only when a game is loaded. Advance scan in verified Window lifecycle method while game is paused (not in `Tick`).
- [ ] **Step 3: Add JP/EN translations** for every UI label, status and error. Verify no missing keys using static comparison; confirm Waylith UI visual behavior in-game.
- [ ] **Step 4: Perform in-game manual acceptance checklist**, fix UI clipping/focus and stale object locations; verify no save changes after a scan.
- [ ] **Step 5: Commit** `feat: add bilingual audit window and navigation`.

## Task 7: Coverage, uncertainty and end-to-end hardening

**Files:** `Audit/AuditModels.cs`, `UI/Dialog_ModRemovalAuditor.cs`, `Tests/ModRemovalAuditor.Tests/CoverageTests.cs`, `README.md`.

**Interfaces:** `AuditReport.Coverage` exposes all known scope statuses and an always-visible reason why absence of findings is not a safe-to-remove guarantee.

- [ ] **Step 1: Add failing tests**: unknown origin is not positive or definitive negative, disabled/uninstalled mod not offered as scannable, exceptions/unsupported scopes lead to Partial, cancel does not output an all-clear, one matching reference produces Detected.
- [ ] **Step 2: Verify failing tests**.
- [ ] **Step 3: Implement coverage and warning rendering**, and user documentation for backup-before-removal. Exclude `Core` and DLC from selectable targets, but display their classification if a UI filter reveals them.
- [ ] **Step 4: Run tests** plus 1.7 integration on copied saves: case with known remaining items and case after their manual removal; verify no unintended mutation or exceptions.
- [ ] **Step 5: Commit** `fix: make audit coverage and uncertainty explicit`.

## Task 8: Performance, packaging and final review

**Files:** `.github/workflows/build.yml`, `README.md`, final `1.7/Assemblies/ModRemovalAuditor.dll`, `About/About.xml` and localization.

**Interfaces:** Release output is normal RimWorld mod folder, not a naked DLL. README includes installation, use, limitations, tested 1.7 revision and supported language.

- [ ] **Step 1: Add tests to CI** (`dotnet test ... -c Release`); on a runner with legitimate 1.7 game refs, also perform `dotnet build ... -c Release`. If refs absent, mark integration build skipped—not passed.
- [ ] **Step 2: Performance benchmark** large modlist (~270), ≥2 maps, populated storage/inventories; measure scan elapsed, peak memory and per-frame work with game paused. With window closed, idle scan overhead should be zero (aside from normal UI registration).
- [ ] **Step 3: Review every failure-mode in `Review Focus`** against tests; inspect log for new red errors, patch conflicts, UI localization, stale links; independently review ownership and `IThingHolder` recursion.
- [ ] **Step 4: Re-run all tests and 1.7 smoke test**. Record actual game revision, commands/results, and unresolved coverage explicitly in README.
- [ ] **Step 5: Package ZIP** including About/, 1.7/Assemblies/, Languages/, README. Exclude DLLs proprietary to game. Publish only after design/spec confirmation and validation.
- [ ] **Step 6: Commit** `release: package Mod Removal Auditor 1.0.0` and create tag/release only when explicitly authorized.

## Follow-up version (not part of v1.0 acceptance)

- v1.1: Hediffs, genes, traits, abilities, xenotypes, bills, research and selected quests/factions with explicit per-category limitations.
- v1.2: Optional exported report, offline save audit (separate parser), saved checklist of removal candidates, optional compatibility/manifest dependency explanation. These do not authorize automatic deletion.

## Implementation handoff

Recommended execution method: **native, single implementer**, followed by an independent final review if available; modules share data-model interfaces and parallel agents would add integration overhead. No source code, GitHub repository, test run or release is created by this planning document. User approval of the design is required before implementation.
## Implementation notes (2026-11-04)

Executed inline, task by task. Deviations from this plan, verified against the 1.7.4985 rev1269 assemblies:

- **Adapter contract:** `IScanSource.Step(List<AuditObservation>) -> bool` (one budget unit per step) instead of `IEnumerator<AuditObservation>`, so terrain can process one map row per unit and holders can emit several observations.
- **Extra pure file:** `Audit/HolderTraversal.cs` — generic, cycle-safe, incremental holder walk that carries the Task 4 contract tests without game assemblies.
- **Scopes:** `provenance` (critical), `maps` (every spawned thing is in `Map.spawnedThings`, so one Map-holder traversal covers spawned and nested content), `world` (World holder plus gravships in flight), `worldpawns`, `terrain` (top/under/foundation/temp layers). Out-of-scope areas are listed as `NotCovered` in every report.
- **Provenance:** `Def.modContentPack` + reference membership in that pack's `AllDefs` (`DefGenerator.AddImpliedDef` registers implied defs there) + declarers by (database def type, defName). No owner → Unknown; owner not listing the def → Uncertain; several declarers → Uncertain (shadowed).
- **Verdict:** `No detected instances` requires every attempted scope `Checked` and no unknown-origin instances; `NotCovered` scopes are shown but do not force `Incomplete`.
- **Material:** things made of a mod's stuff are reported under a `Material` category.
- **Entry points:** minimized bottom-bar `MainButtonDef` and a button on the mod settings page. Jump closes the window; the last report is kept only in memory (weak references, discarded when another game is loaded).
- **Signatures:** `AuditSession(targets, sources, resolve, ...)` + `Start()`; `AuditAccumulator.BuildReport(state, targets, coverage, units)`; `AuditNavigation.TryJump(finding, ref cursor)`.
- **Not performed (no runnable client):** in-game smoke test, fixture-save integration, UI checklist, in-game performance benchmark. A synthetic core benchmark (300k observations) measured ~1.3 µs per unit.

## v1.1 tasks (2026-11-05)

Scope from the follow-up section, verified against 1.7.4985 rev1269. Everything stays read-only; any accessor that writes (for example `ResearchManager.GetProgress`, `ResearchProjectDef.IsFinished/ProgressReal`, `ResearchManager.CurrentAnomalyKnowledgeProjects`) is not called.

- **Task 9 — de-duplication by (object, def):** instead of (object, material flag), so one pawn can carry several attributed defs (kind, xenotype, backstories) while the same reference seen twice still counts once. Test first.
- **Task 10 — pawn data:** for every pawn reached by the map, world and world-pawn traversals, scan hediffs, xenogenes and endogenes, the xenotype, traits, abilities, the pawn kind and backstories. Scope `pawndata`.
- **Task 11 — records:**
  - `bills`: bills of every bill giver reached by the traversals, including surgery bills.
  - `research`:
    - the saved progress dictionary, read through one targeted reflection read with no write; the scope becomes Partial if the field is missing;
    - techprints;
    - anomaly knowledge;
    - the current project.
  - `quests`: quest root script.
  - `factions`: faction def.
  - `ideology`: memes, precepts and culture.
  - `worldobjects`: world object def and site parts.
- **Task 12 — fix the known v1.0 issues:**
  - show "(+N more)" when a location row stands for several instances;
  - throttle the row rebuild while the window is being resized;
  - remove the unused Waylith reference;
  - update the not-covered list to thoughts, relations, records, titles, work and policies, storage/bill/policy filters, quest internals, tales and art, custom data, gravship terrain, and unloaded data.
- **Task 13 — release:** bump to 1.1.0, add a release workflow that packages the committed mod folder and refuses game assemblies, then merge and publish the release.
