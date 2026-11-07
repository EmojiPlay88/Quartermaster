# Piece 5 — Accessibility Layer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Number tooltips with change breakdowns, an alert bar with built-in and user-defined alerts, a "what now" banner, a docked outliner, an interface tour and notes on rules-pack items.

**Architecture:**
- **Core** gains turn/phase on ledger entries (save format 6), a breakdown builder, the alert engine and a `HideAlert` command.
- **Rules** gains pack format 3: `AlertDef`, `PropertyDef.Outliner`, `Notes` on tables, formulas, phases.
- **Render** gains UI-free models: banner text, outliner tree, tour steps.
- **App** stays thin: one shared breakdown tooltip, alert bar, outliner dock, Workbench Alerts tab and notes boxes, tour balloons.

**Tech Stack:** .NET 10, C#, WinForms, SkiaSharp (existing), xUnit.

**Spec:** `docs/superpowers/specs/2026-11-07-piece5-accessibility-design.md` (binding; wins over master spec §10). Background: master spec §2 (content boundary), `docs/superpowers/piece1-followups.md`.

## Global Constraints

- **Content boundary (hard):** no game content. Tests use invented properties ("Fuel", "Ammo"), invented alerts and the existing `PhaseFixture`. Tour text describes the engine only. The builder never edits the user's TEST pack.
- **Never crash on bad user data:** bad alert expressions, dangling ids, old packs and saves give no matches, a validator message or a diagnostic — never an exception reaching the user. UI handlers go through `Guard`.
- **Commands:** every game-state change goes through `GameSession.Execute`; reducer pure and deterministic; replay JSON-identical.
- **Formats:** `RulesPackIo.CurrentFormat = 3` (formats 1–2 load with no alerts, `Outliner = None`, empty notes). `NativeSave.FormatVersion = 6`; format 5 saves keep full undo (`CanUndo` true after load).
- **Exact copy:**
  - Breakdown title `{Property} {value}  ({unit or holder name})`; lines `Start of turn {n}`, `… {N} more changes`, `Earlier turns: net {signed}`; source labels `manual: {note}`, `seeded`, formula name (+ ` · {rule label}` when present); where-text `Turn {n} · {phase}` (U+00B7). Minus sign U+2212. Numbers `0.##` invariant.
  - Banner: `Now: {side} player — {phase}: {first sentence}` + ` · {N} decisions left` when N > 0; without a side: `Now: {phase}: …`; before start `Press Start game ▶ to begin turn 1.`; no phases `No phases defined — free play.`
  - Built-in alert names: `{Property} below zero`, `Formula waiting for input`, `Formula not applied`, `Unacknowledged warning`.
  - Chip menu item `Hide this alert until next turn`. Menus: View `&Outliner`, Help `Interface &Tour`. Workbench tab `Alerts`.
- **Performance:** alerts + outliner recompute debounced 250 ms, < 100 ms for 10 alerts over ~1,200 pieces; dragging counters must not wait on them.
- **Hardcore stays hard:** tooltips show only history; nothing previews a formula result.
- **Layering:** logic in Rules/Core/Render with xUnit tests; App is wiring and controls.
- **Look:** classic gray (Tahoma 8.25pt, system colours, DPI-aware); new docks/windows via the existing dock and `WindowManager` patterns; severity accents red / amber / blue.
- **Build:** `TreatWarningsAsErrors`; warning-free except the known pre-existing MSB3277 WebView2 warning. `dotnet test Quartermaster.slnx` green after every task.
- **Test isolation / smokes:** never touch the real `%LOCALAPPDATA%`; smokes use `QUARTERMASTER_LAYOUT_ROOT=<scratch>/qmroot`; screenshots only in scratch.
- **Commits:** end with a blank line then `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; noreply identity; never stage `startapp.cmd`.

## Review Focus

1. **Format-5 saves made with piece 4** (undo history, no Turn/Phase on ledger entries) must load with full undo, not be rebased by the consistency check. Task 1 test `Format5Save_KeepsUndo_EntriesGainTurn`.
2. **A user alert whose expression is broken or names a deleted property** shows no chip and never throws, while other alerts still work. Task 3 test `UserAlert_BadOrDanglingCondition_NoMatches_OthersStillWork`.
3. **Pieces removed after changes** (history survives piece deletion) and **holders**: breakdowns and alert matches must handle a key whose piece is gone. Task 1 test `Breakdown_RemovedPiece_UsesKeyFallbackName`; Task 3 test `NegativeValue_RemovedPiece_NotMatched`.
4. **Undo/step back while the alert bar or outliner shows matches**: both refresh to the rewound state, and a hidden alert un-hides when its `HideAlert` is undone. Task 3 test `HideAlert_Undo_Unhides`; Task 6 wires `Rewound`.
5. **Huge counts**: a property negative on 800 pieces; chip tooltip shows 10 and `… and 790 more`; cycling wraps. Task 3 test `AlertTooltip_Truncates`; Task 6 smoke.

---

## File Structure

```
src/Quartermaster.Rules/Model/RulesPack.cs   – AlertDef, AlertSeverity, OutlinerAggregate; RulesPack.Alerts; PropertyDef.Outliner
src/Quartermaster.Rules/Model/TableDef.cs, FormulaDef.cs, PhaseDefs.cs – Notes
src/Quartermaster.Rules/Io/RulesPackIo.cs    – format 3
src/Quartermaster.Rules/Validation/*         – alert checks
src/Quartermaster.Core/Ledger.cs             – LedgerEntry Turn/Phase
src/Quartermaster.Core/LedgerBreakdown.cs    – breakdown model + text (new)
src/Quartermaster.Core/Alerts.cs             – AlertEngine, AlertHit, AlertMatch (new)
src/Quartermaster.Core/Commands.cs, GameState.cs, GameLog.cs – HideAlert, HiddenAlerts
src/Quartermaster.Core/NativeSave.cs         – format 6, format-5 undo compatibility
src/Quartermaster.Render/Banner.cs           – banner text (new)
src/Quartermaster.Render/OutlinerModel.cs    – outliner tree (new)
src/Quartermaster.Render/TourSteps.cs        – tour steps (new)
src/Quartermaster.Render/Settings.cs, SettingsStore.cs – AlertBarVisible, OutlinerVisible, OutlinerGrouping, TourShown
src/Quartermaster.App/BreakdownTip.cs        – shared breakdown tooltip (new)
src/Quartermaster.App/AlertBar.cs, MainForm.Alerts.cs – bar, banner, chips (new)
src/Quartermaster.App/OutlinerPanel.cs, OutlinerDock.cs – outliner (new)
src/Quartermaster.App/Workbench/AlertsTab.cs – Alerts tab (new); PropertiesTab, TablesTab, FormulasTab, PhasesTab – notes / outliner column
src/Quartermaster.App/TourOverlay.cs         – balloons (new)
```

## Task dependencies

- Task 1 and Task 2 have no dependencies.
- Task 3 needs 1 and 2. Task 4 needs 3. Task 5 needs 1.
- Task 6 needs 3 and 4. Task 7 needs 4 and 5. Task 8 needs 2 and 3.
- Task 9 needs 6 and 7. Task 10 needs all.

---

### Task 1: Ledger entries know their turn; breakdown builder; save format 6

**Files:**
- Modify: `src/Quartermaster.Core/Ledger.cs`, `GameState.cs` (`DoLedgerDelta`, `DoSeed`), `NativeSave.cs`
- Create: `src/Quartermaster.Core/LedgerBreakdown.cs`
- Test: `tests/Quartermaster.Core.Tests/LedgerBreakdownTests.cs`, `NativeSaveTests.cs`

**Interfaces:**
- Produces:
  - `LedgerEntry` gains trailing optional `int? Turn = null, string? Phase = null`. The reducer fills them from `State.Turn.Turn` and `State.Turn.Phase` at apply time.
  - `public sealed record BreakdownLine(string Amount, string Label, string? Where);`
  - `public sealed record Breakdown(string Title, IReadOnlyList<BreakdownLine> Lines);`
  - `public static class LedgerBreakdown`:
    - `Breakdown For(GameState s, RulesPack? pack, ModuleModel? model, string key, string propertyId)` — history-based.
    - `Breakdown ForTotal(string title, IEnumerable<(string Label, double Value)> parts)` — per-part list, largest absolute first, at most 12 lines then `… {N} more`.
    - `string Format(Breakdown b)` — one line each, `Amount  Label  Where`, aligned with spaces.
  - Unit / holder name in the title: piece name via `PieceInfo`-style naming from `GameState` (fallback: the key's id when the piece was removed), holder name from the pack (fallback id), global holder → `Global`.
- Rules: lines for entries with `Turn == current turn` one per entry (oldest first, at most 12; older ones inside the turn collapse into `… {N} more changes` at the top); a `Start of turn {n}` line with the value before the first current-turn entry; all entries before the current turn (or with null `Turn`) collapse into `Earlier turns: net {signed}`. No history → a single line `no changes recorded`.

- [ ] **Step 1: Write failing tests:**
  - `Reducer_FillsTurnAndPhase` (advance to turn 2 · "Supply A", apply a delta → entry `Turn == 2`, `Phase == "Supply A"`).
  - `Breakdown_CurrentTurnLines_StartOfTurn_EarlierNet` (exact `Format` output for a scripted history across 3 turns, including a manual note, a seed and a formula with a rule ref).
  - `Breakdown_MoreThan12_Collapses`.
  - `Breakdown_NullTurnEntries_CountAsEarlier`.
  - `Breakdown_RemovedPiece_UsesKeyFallbackName` (Review Focus 3).
  - `ForTotal_SortsAndTruncates`.
  - `Format5Save_KeepsUndo_EntriesGainTurn` (Review Focus 1): write a format-5 JSON (entries without Turn/Phase, valid genesis/actions) → load → `CanUndo`, `RebasedOnLoad == false`, entries now carry Turn.
  - `Format6_RoundTrip`.
- [ ] **Step 2:** `dotnet test tests/Quartermaster.Core.Tests --filter "LedgerBreakdownTests|NativeSaveTests"` → FAIL.
- [ ] **Step 3:** Implement. Format-5 compatibility: when the replay of genesis + log differs from the stored snapshot only in `LedgerEntry.Turn/Phase` (compare with those fields nulled in the replayed copy), accept and use the replayed state as the snapshot. Bump `FormatVersion` to 6; update existing tests that assert format 5.
- [ ] **Step 4:** `dotnet test Quartermaster.slnx` → PASS.
- [ ] **Step 5:** Commit `feat(core): ledger breakdowns with turn and phase; save format 6`.

### Task 2: Rules pack format 3 — alerts, outliner properties, notes

**Files:**
- Modify: `src/Quartermaster.Rules/Model/RulesPack.cs`, `TableDef.cs`, `FormulaDef.cs`, `PhaseDefs.cs`, `Io/RulesPackIo.cs`, `Validation/*`
- Test: `tests/Quartermaster.Rules.Tests/RulesPackIoTests.cs`, `ValidatorTests.cs`

**Interfaces:**
- Produces:
  - `public enum AlertSeverity { Info, Warning, Critical }`
  - `public sealed record AlertDef { string Id; string Name; string Condition; string Message; AlertSeverity Severity = Warning; string? SideId; string? Notes; }` (init properties like the other defs)
  - `RulesPack.Alerts : List<AlertDef>`; `ById<AlertDef>` supported.
  - `public enum OutlinerAggregate { None, Sum, Min, Average }`; `PropertyDef.Outliner : OutlinerAggregate = None`.
  - `string? Notes` on `TableDef`, `FormulaDef`, `PhaseDef`.
  - `RulesPackIo.CurrentFormat = 3`.
- Validator: alert with empty name / condition; condition that does not parse; unknown variable (same check phase warnings use); unknown `SideId`; duplicate ids. Messages follow the existing validator style.

- [ ] **Step 1: Write failing tests:** `Format3_RoundTrip_AlertsOutlinerNotes`, `Format2Pack_LoadsWithDefaults`, `Format4Pack_Rejected`, `Validator_FlagsBadAlert` (parse error, unknown property, unknown side, empty name).
- [ ] **Step 2:** `dotnet test tests/Quartermaster.Rules.Tests` → FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** `dotnet test Quartermaster.slnx` → PASS.
- [ ] **Step 5:** Commit `feat(rules): pack format 3 with alerts, outliner properties and notes`.

### Task 3: Alert engine and HideAlert

**Files:**
- Create: `src/Quartermaster.Core/Alerts.cs`
- Modify: `Commands.cs` (`HideAlert`), `GameState.cs` (`HiddenAlerts`, reducer), `GameLog.cs`, `NativeSave.cs` (validate default)
- Test: `tests/Quartermaster.Core.Tests/AlertEngineTests.cs`

**Interfaces:**
- Consumes: Task 1 (format 6), Task 2 (`AlertDef`, `AlertSeverity`).
- Produces:
  - `public sealed record AlertMatch(string? PieceId, string? HolderId, string Text);`
  - `public sealed record AlertHit(string Id, string Name, AlertSeverity Severity, string Message, IReadOnlyList<AlertMatch> Matches, string? Notes);`
  - `public static class AlertEngine { IReadOnlyList<AlertHit> Evaluate(GameState s, RulesPack? pack, ModuleModel model, PhaseDef? current, string? actingSideId); string Tooltip(AlertHit h); }` — hits with ≥ 1 match only, sorted Critical → Info then name; hidden ids excluded. `Tooltip` = message line + first 10 match texts + `… and {N} more`.
  - Built-in ids: `builtin:neg:{propertyId}`, `builtin:input`, `builtin:pending`, `builtin:warning`.
  - `public sealed record HideAlert(string AlertId, DateTimeOffset? At = null, string? Who = null) : GameCommand;` (`$t` `"hideAlert"`, not folding); `GameState.HiddenAlerts : List<string>`; cleared by the reducer when the turn number changes (`AdvancePhase` into a new turn, `SetTurn`). Log line `Alert hidden until next turn: {id}` (kind System).
- Rules: negative value over pieces and holders that still exist (removed pieces' keys are skipped); pending runs split by `NeedsInput`; warnings via `Decisions.Warnings(pack, s, current)` unacknowledged + `IsDecision`; user alerts via `PieceFilter.Compile` over pieces (of `SideId` when set), skipped while another side is acting when phases exist; `{unit}` replaced by the piece name.

- [ ] **Step 1: Write failing tests:** `Builtins_OnScriptedState` (one negative Fuel on a piece and a holder, one needs-input run, one pending run, one unacknowledged decision warning → 4 hits with exact names, severities and counts); `UserAlert_Matches_MessageHasUnitName`; `UserAlert_BadOrDanglingCondition_NoMatches_OthersStillWork` (Review Focus 2); `UserAlert_SideLimited_OnlyWhileActing`; `NegativeValue_RemovedPiece_NotMatched`; `HideAlert_ExcludesUntilTurnChange`; `HideAlert_Undo_Unhides` (Review Focus 4); `AlertTooltip_Truncates` (Review Focus 5: 800 matches); `Perf_TenAlerts_1200Pieces_Under100ms` (`[Trait("Category","Perf")]`, assert < 300 ms, print the time).
- [ ] **Step 2:** `dotnet test tests/Quartermaster.Core.Tests --filter AlertEngineTests` → FAIL.
- [ ] **Step 3:** Implement. `CommandLog.Stamp` in App gets the `HideAlert` case in Task 6.
- [ ] **Step 4:** `dotnet test Quartermaster.slnx` → PASS.
- [ ] **Step 5:** Commit `feat(core): alert engine and hide-until-next-turn`.

### Task 4: Banner text and outliner model

**Files:**
- Create: `src/Quartermaster.Render/Banner.cs`, `OutlinerModel.cs`
- Modify: `Settings.cs`, `SettingsStore.cs`
- Test: `tests/Quartermaster.Render.Tests/BannerTests.cs`, `OutlinerModelTests.cs`, `SettingsTests.cs`

**Interfaces:**
- Consumes: Task 3 (`AlertHit`), Task 2 (`OutlinerAggregate`), `PhaseView` (PhaseController.cs).
- Produces:
  - `public static class Banner { (string Text, string? Tooltip) For(PhaseView v); }` — copy per Global Constraints; first sentence = text up to the first `.`, `!` or `?` followed by space/end, max 120 chars with `…`; tooltip = full instructions.
  - `public enum OutlinerGrouping { OrgDisplays, BySide }`
  - `public enum OutlinerNodeKind { Folder, Group, Piece }`
  - `public sealed record OutlinerColumn(string PropertyId, string Name, OutlinerAggregate Aggregate, string? Notes);`
  - `public sealed record OutlinerNode(string Key, string Name, OutlinerNodeKind Kind, string? PieceId, string? MapId, int Units, IReadOnlyList<double?> Values, IReadOnlyList<AlertSeverity> Alerts, IReadOnlyList<OutlinerNode> Children);`
  - `public sealed record OutlinerTree(IReadOnlyList<OutlinerColumn> Columns, IReadOnlyList<OutlinerNode> Roots);`
  - `public static class OutlinerModel { OutlinerTree Build(GameState s, RulesPack? pack, ModuleModel model, OutlinerGrouping grouping, IReadOnlyList<AlertHit> alerts); }`
  - OrgDisplays: folders from org-display `MapDef.NavPath`, a Group per org-display map with its pieces; then one Group per ordinary map. BySide: a Group per side (pieces matching `SideDef.UnitFilter`), then `Other`. Empty groups omitted. Group values aggregate piece values (Sum / Min / Average; null when no piece has the value). `Alerts` = distinct severities of hits matching the node's pieces (groups: union).
  - Settings: `AlertBarVisible = true`, `OutlinerVisible = false`, `OutlinerGrouping = OrgDisplays`, `TourShown = false` (store keys same names; defaults on bad values).
- [ ] **Step 1: Write failing tests:** `Banner_BeforeStart`, `Banner_NoPhases`, `Banner_InPhase_WithSideAndDecisions`, `Banner_NoSide`, `Banner_LongInstruction_Truncated`; `Outliner_OrgDisplays_FoldersGroupsPieces`, `Outliner_BySide_WithOther`, `Outliner_Aggregates_SumMinAverage`, `Outliner_AlertSeverities_Bubble`, `Outliner_NoOutlinerProperties_NoColumns`; `Settings_NewFields_RoundTrip`.
- [ ] **Step 2:** `dotnet test tests/Quartermaster.Render.Tests --filter "BannerTests|OutlinerModelTests|SettingsTests"` → FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** `dotnet test Quartermaster.slnx` → PASS.
- [ ] **Step 5:** Commit `feat(render): banner text and outliner model`.

### Task 5: Breakdown tooltips everywhere

**Files:**
- Create: `src/Quartermaster.App/BreakdownTip.cs`
- Modify: `LedgerWindow.cs` (~260-275 `OnToolTip`), `CounterDetailPopup.cs`, `Render/MapInteraction.cs` (`PieceDetails` gains ledger lines), `InventoryWindow.cs`, `BatchGridWindow.cs`
- Test: `tests/Quartermaster.Render.Tests/MapInteractionTests.cs`

**Interfaces:**
- Consumes: Task 1 (`LedgerBreakdown.For/ForTotal/Format`).
- Produces: `sealed class BreakdownTip` — one shared tooltip helper (monospace-aligned text in the classic look) with `Show(Control owner, Point at, Breakdown b)` / `Hide()`; used by every caller.
- `PieceDetails.For` gains `IReadOnlyList<(string PropertyId, string Name, double Value)> Ledger` (properties that apply to the piece and have a value). `CounterDetailPopup` lists them; hovering a ledger line shows its breakdown via `BreakdownTip`.
- Ledger window cells: `LedgerBreakdown.For`. Inventory totals: `ForTotal` of contributing pieces. Batch grid ledger column: `For`.
- [ ] **Step 1: Write failing test:** `PieceDetails_IncludesLedgerValues` (applies-to respected; no pack → empty).
- [ ] **Step 2:** run it → FAIL.
- [ ] **Step 3:** Implement the helper and wire the four places.
- [ ] **Step 4:** `dotnet test Quartermaster.slnx` → PASS; manual check of the ledger tooltip in the running app on the phases smoke fixture (see Task 10 for fixture setup).
- [ ] **Step 5:** Commit `feat(app): breakdown tooltips on every ledger value`.

### Task 6: Alert bar and banner

**Files:**
- Create: `src/Quartermaster.App/AlertBar.cs`, `MainForm.Alerts.cs`
- Modify: `MainForm.cs` (place the bar under the toolbar, above the phase strip), `CommandLog.cs` (Stamp `HideAlert`), `SettingsTabs.cs` (Display: "Show alert bar")

**Interfaces:**
- Consumes: Task 3 (`AlertEngine`, `HideAlert`), Task 4 (`Banner`, `Settings.AlertBarVisible`).
- Behaviour per spec §3.2 / §3.4: banner left (click → reveal phase panel; tooltip = instructions), chips right (severity accent, name, count; hover → `AlertEngine.Tooltip` plus notes; click → cycle centre/select via `CentreOnPiece`, holders → open Ledger window and select the holder row; right-click → menu of matches + `Hide this alert until next turn` → executes `HideAlert`). Recompute debounced 250 ms on `Changed`, `Rewound`, `RulesChanged`, `GameReplaced`, and phase changes; computed off the drag path.
- [ ] **Step 1:** Implement; keep logic in `AlertEngine`/`Banner`.
- [ ] **Step 2:** `dotnet test Quartermaster.slnx` → PASS; build warning-free.
- [ ] **Step 3:** Commit `feat(app): alert bar with what-now banner`.

### Task 7: Outliner dock

**Files:**
- Create: `src/Quartermaster.App/OutlinerPanel.cs`, `OutlinerDock.cs`
- Modify: `MainForm.cs` (View → `&Outliner`, host/splitter on the right, create/destroy with the session like `LogDock`)

**Interfaces:**
- Consumes: Task 4 (`OutlinerModel`, settings), Task 5 (`BreakdownTip`, `LedgerBreakdown.ForTotal`), `Decisions` via `PhaseController.View()`.
- Behaviour per spec §3.3: "Now" decisions list (click = same as the phase panel, `JumpToDecision`), grouping dropdown (persisted), tree-list with name / units / outliner columns / alert icons; number tooltips (pieces: `LedgerBreakdown.For`; groups: `ForTotal` over the group's pieces); column header tooltips = property notes; double-click piece → `CentreOnPiece`, org-display group → open that map. Pop-out / dock back like `LogDock` (layout key `outliner`). Refresh on the alert bar's debounced tick (share one recompute).
- [ ] **Step 1:** Implement.
- [ ] **Step 2:** `dotnet test Quartermaster.slnx` → PASS.
- [ ] **Step 3:** Commit `feat(app): outliner dock`.

### Task 8: Workbench — Alerts tab, outliner column, notes

**Files:**
- Create: `src/Quartermaster.App/Workbench/AlertsTab.cs`
- Modify: `WorkbenchWindow.cs`, `PropertiesTab.cs`, `TablesTab.cs`, `FormulasTab.cs`, `PhasesTab.cs`, `TablesTreePanel.cs`, `PhaseStrip.cs`, `PhasePanel.cs`, `FormulaDialog.cs`, `LedgerWindow.cs` (header tooltips)
- Test: `tests/Quartermaster.Render.Tests/PhaseEditTests.cs` or a new `AlertEditTests.cs` for any UI-free edit helpers added

**Interfaces:**
- Consumes: Task 2 (model), Task 3 (`AlertEngine` for the live "N matches now" count).
- Alerts tab: list editor in the style of the Phases tab (add, remove, move up/down; Name, Condition with live match count and parse error text, Message, Severity dropdown, Side dropdown with "(any)", Notes). Commits through the same pack-commit path the Phases tab uses.
- Properties tab: `Outliner` column (None/Sum/Min/Average).
- Notes box (multi-line) for tables, formulas, phases; shown as tooltips: Tables tree nodes, formula dialog title area, phase chips and phase panel heading, ledger column headers.
- [ ] **Step 1:** If you add UI-free helpers (e.g. alert list edits), test them first (RED → GREEN).
- [ ] **Step 2:** Implement.
- [ ] **Step 3:** `dotnet test Quartermaster.slnx` → PASS.
- [ ] **Step 4:** Commit `feat(app): workbench alerts tab, outliner column and notes`.

### Task 9: Interface tour

**Files:**
- Create: `src/Quartermaster.Render/TourSteps.cs`, `src/Quartermaster.App/TourOverlay.cs`
- Modify: `MainForm.cs` (Help → `Interface &Tour`; first game open when `!TourShown`)
- Test: `tests/Quartermaster.Render.Tests/TourStepsTests.cs`

**Interfaces:**
- Produces: `public sealed record TourStep(string Id, string TargetId, string Title, string Text);` `public static class TourSteps { IReadOnlyList<TourStep> All { get; } IReadOnlyList<TourStep> Visible(Func<string, bool> isVisible); }` — 8 steps in order: module tree, map, phase strip + Next Phase, phase panel, alert bar, outliner, ledger, Edit → Undo / Step Back (target ids are control names the App resolves). Text: 1–3 sentences each, engine-only.
- `TourOverlay`: balloon pointing at the target control's screen rectangle; Back / Next / Skip tour; sets `TourShown = true` when finished or skipped.
- [ ] **Step 1: Write failing tests:** `Tour_HasEightSteps_InOrder`, `Tour_Visible_SkipsMissingTargets`, `Tour_Text_IsShort` (≤ 3 sentences, ≤ 300 chars each).
- [ ] **Step 2:** run → FAIL. **Step 3:** implement. **Step 4:** `dotnet test Quartermaster.slnx` → PASS.
- [ ] **Step 5:** Commit `feat(app): interface tour`.

### Task 10: End-to-end, smokes, docs

**Files:**
- Create: `tests/Quartermaster.Render.Tests/Piece5EndToEndTests.cs`
- Modify: `Program.cs`, `MainForm.Smoke.cs`, `README.md`, `docs/superpowers/RESUME.md`, `docs/superpowers/piece1-followups.md`

- [ ] **Step 1: Tests:** `E2E_FullyAuto_AlertsOutlinerBreakdown` — play the invented fixture into turn 2 with a user alert (`Fuel < 50`) and an outliner Sum property; assert alert hits, outliner group totals and a breakdown's `Start of turn 2` line; step back one phase and assert all three follow the rewound state; save/load round trip keeps them.
- [ ] **Step 2:** Smokes: `--screenshot-alerts <png>` (bar with chips and banner), `--screenshot-outliner <png>`, `--screenshot-breakdown <png>` (ledger tooltip), `--screenshot-tour <png>` (first balloon). Fixture via `SmokeFixtureWriter` with `QM_WRITE_SMOKE_FIXTURE` in scratch; run with `QUARTERMASTER_LAYOUT_ROOT`. Inspect each PNG and fix obvious layout defects.
- [ ] **Step 3:** Docs: README "What it does" gains an "Readable at a glance" group (tooltips, alert bar, outliner, what-now, tour, notes); Roadmap drops the accessibility line; keep the user's intro wording; no personal paths or real-game content. RESUME.md piece 5 row + next steps (hands-on test; next piece: multiplayer). followups: piece-5 rulings and minors.
- [ ] **Step 4:** `dotnet test Quartermaster.slnx` → PASS; smokes exit 0.
- [ ] **Step 5:** Commit `test: piece 5 end-to-end and smokes; docs`.
