# Piece 3 — Turn Sequencer and Difficulty Modes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A rules-pack-driven phase sequence (tracker strip, phase panel, Next Phase), three difficulty modes plus a Calculate button, formulas that fire at phase start and on unit moves, phase warnings, and a Phases tab in the Rules Workbench.

**Architecture:**
- **Rules** gains the pack-format-2 model (sides, phases, blocks, `Moved` binding, `AutoValue`), two text functions, validator checks and key-dropdown discovery.
- **Core** gains the phase state and commands (save format 4), pure position math (`Sequencer`), open-decision computation (`Decisions`) and the runner hooks (`Moved`, `AutoValue`, value sources).
- **Render** gains the orchestration (`PhaseController`), the checked formula-dialog model, the cheat-sheet and batch-grid models, move measuring and pack editing helpers.
- **App** stays thin: strip, panel, dialogs, batch grid window, settings/setup choices, workbench tab.
- Triggers never fire inside the reducer. They issue logged commands, so replaying the command list reproduces the state without re-running any formula.

**Tech Stack:** .NET 10, C#, WinForms, SkiaSharp (existing), xUnit.

**Spec:** `docs/superpowers/specs/2026-11-06-piece3-sequencer-difficulty-design.md` (binding; it wins over master spec §9). Background: `docs/superpowers/specs/2026-11-06-quartermaster-engine-design.md` (§2 content boundary). Earlier rulings: `docs/superpowers/piece1-followups.md`.

## Global Constraints

- **Content boundary (hard):** no game content anywhere. Phase names, order, instructions, formulas and warnings are authored by the user. Invented phases ("Weather", "Supply", "Movement", "Combat"…) appear only in tests and fixtures. App smoke code uses neutral names ("Smoke phase A"). The builder never edits the user's TEST pack.
- **Never crash on bad user data:** unparsable or wrong-typed expressions, dangling ids, bad blocks, malformed saves and packs give a message, a log line or a diagnostic, never an exception reaching the user. UI handlers are guarded. Marshalling uses `BeginInvoke(() => { if (!IsDisposed) …; })`.
- **Commands:** every game-state change goes through `GameSession.Execute` as a logged command. `At`/`Who` are set by the issuer (`CommandLog.Stamp`/`Prepare` in App, an injected `prepare` hook in Render). The reducer stays pure, builds all log entries, and replay is deterministic. Formula triggers run only in issuers, never in the reducer.
- **Layering:** all logic lives UI-free in Rules/Core/Render with xUnit tests. App is thin wiring and controls.
- **Test isolation:** tests and smokes never touch the real `%LOCALAPPDATA%`. Tests inject temp roots. Smokes run with `QUARTERMASTER_LAYOUT_ROOT=<scratch>/qmroot`.
- **Formats:**
  - Rules pack `CurrentFormat = 2`. Format 1 loads as "no phases" (empty Sides/Phases/Blocks). Format > 2 → `InvalidDataException("pack.json: pack format N is newer than supported format 2")`.
  - NativeSave `FormatVersion = 4`. Formats 1–3 load "before turn 1" (`Position = null`) with `Difficulty = SemiAuto`.
- **Exact UI copy** (from the spec):
  - `Start game ▶`
  - `Next Phase ▶ {next name}`
  - `{N} decisions left` (red when N > 0)
  - `No phases defined — add them in the Rules Workbench (Phases tab)`
  - `Pass to {side} player`
  - `Continue anyway` / `Go back`
  - `Doesn't match ({source})`
  - Log line `Turn 3 · Movement → Combat` (U+00B7, U+2192)
- **Build:** `TreatWarningsAsErrors` is on; the build must be warning-free.
- **Smokes:** smoke runs keep exiting 0. New flags follow the existing `--screenshot-<thing> <png>` style. Screenshots go to the scratchpad only.
- **Look:** Windows-classic look of pieces 1–2b (Tahoma 8.25pt, system colours, DPI-aware). New windows and panels go through `OpenChild`/`WindowManager` so they get tabs and pop-out.
- **Commits:** every commit message ends with a blank line, then `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **The user edits phases mid-game**: deletes the current phase, reorders, removes a block, lowers a block's Count below the current repeat, or deletes every phase. The game relocates per spec §6 with a log line and a strip notice, or falls back to free-form, and never throws. Task 4 tests `Resolve_CurrentPhaseDeleted_ClampsToOldIndex`, `Resolve_RepeatAboveCount_Clamped`, `Resolve_NoPhases_ReturnsNull`; Task 8 test `Reconcile_AfterRulesEdit_LogsAndRelocates`.
2. **Invalid blocks reaching runtime** (overlapping, first after last, Count 0, unknown ids) from a hand-edited pack the validator flagged but the user kept. Advancing ignores those blocks and never loops forever. Task 4 test `Next_InvalidBlocksIgnored_Terminates`.
3. **Bad expressions at runtime**: a warning condition, unit filter or AutoValue that doesn't parse, names a missing property, or compares text with a number. It counts as "no match" (filters/warnings) or "missing" (AutoValue), the phase still runs, and Next Phase still works. Task 5 test `Warnings_BadCondition_NoHits`; Task 8 test `OnStart_BadFilter_LogsLineAndSkips`.
4. **Dangling ids at runtime**: an OnStart/OnMove formula id, side id or rule ref deleted after the phase was authored. The entry is skipped with one `calc` log line (`"{phase}: formula \"{id}\" not found — skipped"`), the other entries still run, and there's no crash. Task 8 test `OnStart_MissingFormula_SkippedWithLogLine`.
5. **Typed value formats in checked mode**: `"3.0"` vs 3, `" sand "` vs key `Sand`, `"1,5"`, an empty box, `"−2"` (U+2212). Numbers compare invariantly, and text compares case- and space-insensitively (TableLookup normalisation). A comma decimal is "not a number" (box red with "Not a number"), never a match. Task 7 test `Checked_TypedFormats_CompareInvariant`.

---

## File Structure

```
src/Quartermaster.Rules/
  Model/PhaseDefs.cs        – SideDef, ForEachMode, StartEntry, MoveEntry, PhaseWarning, PhaseDef, PhaseBlock (new)
  Model/RulesPack.cs        – +Sides, Phases, Blocks; ById<SideDef|PhaseDef>
  Model/FormulaDef.cs       – BindingKind.Moved; VariableBinding.AutoValue
  Io/RulesPackIo.cs         – format 2
  Expr/Parser.cs, Evaluator.cs – startsWith, contains
  Validation/RulesValidator.cs – sides/phases/blocks checks
  Layout/KeyChoices.cs      – key dropdown discovery (new)
  Tables/TableLookup.cs     – +Locate
src/Quartermaster.Core/
  Phases.cs                 – Difficulty, PhasePosition, PendingUnit, PendingRun (new)
  Commands.cs, GameLog.cs, GameState.cs, NativeSave.cs – new commands, calc kind, tags, format 4
  Sequencer.cs              – position math, units per phase (new)
  Decisions.cs              – warnings and open decisions (new)
  PieceFilter.cs            – +Compile
  FormulaRunner.cs          – Moved, AutoValue, Source/Cell, Tag/Subject
src/Quartermaster.Render/
  FormulaDialogModel.cs     – checked mode, Calculate, choices
  CheatSheetModel.cs, MoveMeasure.cs, PhaseController.cs, BatchModel.cs, PhaseEdit.cs (new)
  ModuleSession.cs, Autosaver.cs, Settings.cs, SettingsStore.cs, LogFilter.cs, FormulaEdit.cs (modified)
src/Quartermaster.App/
  PhaseStrip.cs, PhasePanel.cs, PhaseDock.cs, NextPhaseDialog.cs, CheatSheetWindow.cs,
  MissingInputDialog.cs, BatchGridWindow.cs, Workbench/PhasesTab.cs (new)
  MainForm*.cs, FormulaDialog.cs, FormulaFormView.cs, MapDragController.cs, MapWindow.cs,
  SetupChooserDialog.cs, SettingsTabs.cs, LogPanel.cs, CommandLog.cs, Program.cs,
  Workbench/WorkbenchWindow.cs, Workbench/FormulasTab.Grids.cs (modified)
tests/Quartermaster.TestFixtures/PhaseFixture.cs (new; + project refs to Rules and Vassal)
tests/… mirror per project
```

---

### Task 1: Pack format 2 — sides, phases, blocks, Moved, AutoValue

**Files:**
- Create: `src/Quartermaster.Rules/Model/PhaseDefs.cs`
- Modify: `Model/RulesPack.cs`, `Model/FormulaDef.cs`, `Io/RulesPackIo.cs`
- Test: `tests/Quartermaster.Rules.Tests/RulesPackIoTests.cs`

**Interfaces:**
- **Produces** (namespace `Quartermaster.Rules`; records with `init` properties; list defaults `new()`):
  - `sealed record SideDef { string Id = ""; string Name = ""; string? UnitFilter; }`
  - `enum ForEachMode { Once, Units }`
  - `sealed record StartEntry { string FormulaId = ""; ForEachMode ForEach = Once; string? UnitFilter; }`
  - `sealed record MoveEntry { string FormulaId = ""; string? UnitFilter; }`
  - `sealed record PhaseWarning { string Condition = ""; string Message = ""; bool IsDecision; }`. `Message` may contain the token `{unit}`, replaced by the unit name in per-unit rows.
  - `sealed record PhaseDef { string Id = ""; string Name = ""; string? SideId; string Instructions = ""; string? RuleRefId; List<string> Checklist; List<StartEntry> OnStart; List<MoveEntry> OnMove; List<PhaseWarning> Warnings; }`
  - `sealed record PhaseBlock { string FirstPhaseId = ""; string LastPhaseId = ""; int Count = 1; string Label = ""; }`
  - `RulesPack` gains `List<SideDef> Sides`, `List<PhaseDef> Phases`, `List<PhaseBlock> Blocks`. `ById<T>` also supports `SideDef` and `PhaseDef`.
  - `BindingKind` gains `Moved`, appended last.
  - `VariableBinding` gains `string? AutoValue`, an expression.
  - `RulesPackIo.CurrentFormat = 2`:
    - Sides, Phases and Blocks live in `pack.json`, and Save keeps their order. Unlike properties, they are **not** sorted.
    - Load accepts formats 1 and 2. Normalise turns null lists into empty ones and null strings into `""`, and drops null items.
    - Load and Save reject duplicate side ids and duplicate phase ids, like the other kinds. Messages: `Duplicate side id 'x'` and `Duplicate phase id 'x'`.

- [ ] **Step 1: Write the failing tests:**
  - `Format2_RoundTrip_PhasesSidesBlocksInOrder`: three phases saved in the order C, A, B load back as C, A, B, with all fields equal.
  - `Format1_Loads_NoPhases`: a hand-written format-1 `pack.json` gives empty `Sides`/`Phases`/`Blocks`.
  - `Format3_Rejected_NewerThanSupported`: `InvalidDataException` with the message `"pack.json: pack format 3 is newer than supported format 2"`.
  - `NullPhaseLists_Normalised`: JSON `"checklist": null`, `"onStart": [null]` loads as empty lists.
  - `DuplicatePhaseId_LoadAndSaveRejected`
  - `Binding_MovedAndAutoValue_RoundTrip`
- [ ] **Step 2:** Run `dotnet test tests/Quartermaster.Rules.Tests`. Expected: FAIL (types missing).
- [ ] **Step 3:** Implement. Update the existing tests that assert `CurrentFormat == 1`. Existing `ComputeSha256` callers are unaffected.
- [ ] **Step 4:** Run `dotnet test Quartermaster.slnx`. Expected: PASS, with 0 warnings.
- [ ] **Step 5: Commit:** `feat: rules pack format 2 with sides, phases and blocks`

---

### Task 2: Text functions and validator checks

**Files:**
- Modify: `src/Quartermaster.Rules/Expr/Parser.cs`, `Expr/Evaluator.cs`, `Validation/RulesValidator.cs`
- Test: `tests/Quartermaster.Rules.Tests/EvaluatorTests.cs`, `ValidatorTests.cs`

**Interfaces:**
- **Consumes:** the Task 1 model.
- **Produces:**
  - Parser arity: `["startswith"] = (2, 2)`, `["contains"] = (2, 2)`. Function names are lower-cased, so `startsWith(...)` parses.
  - Evaluator: `startsWith(text, prefix)` and `contains(text, part)`. Both take text arguments via `TextArg`, return Bool, and compare `OrdinalIgnoreCase`. A non-text argument gives `EvalError("'startsWith' needs text, got a number")`.
  - New validator issues. EntityId is the phase/side id, or the first phase id for blocks. Message texts:
    - Error `Phase "{name}": formula "{id}" does not exist` (OnStart and OnMove entries)
    - Error `Phase "{name}": side "{id}" does not exist`
    - Error `Phase "{name}": rule reference "{id}" does not exist`
    - Error `Phase "{name}": warning condition {parse message}`, `Phase "{name}": on-start filter {msg}`, `Phase "{name}": on-move filter {msg}`, `Side "{name}": unit filter {msg}`. Unknown property names in these expressions use the existing `UnknownPropertyRefs` and the "refers to unknown property" wording.
    - Error `Phase "{name}": warning message is empty`
    - Error `Block "{label}": phase "{id}" does not exist`; `Block "{label}": its first phase comes after its last phase`; `Block "{label}": count must be at least 1`; `Block "{label}" overlaps block "{other}"`. An empty label is shown as `(unnamed)`.
    - Warning `Phase "{name}": on-move formula "{formula name}" has no Moved binding`
    - Error `Formula "{name}": variable "{v}" auto value {parse msg}`
  - Each phase, side and block check runs inside its own try/catch, so one bad entity can't abort the rest (fixes the piece-2 Task 5 minor for the new code).

- [ ] **Step 1: Write the failing tests:**
  - `StartsWith_Contains_CaseInsensitive` (`startsWith("Red 1", "red")` is true; `contains("Blue Inf", "INF")` is true)
  - `StartsWith_NonText_EvalError`
  - `Validator_PhaseDanglingIds_Errors` (formula, side and ruleRef: three issues with the exact texts above)
  - `Validator_UnparsableConditionAndFilters_Errors`
  - `Validator_Blocks_OverlapReversedCountMissing` (four blocks, four errors)
  - `Validator_OnMoveWithoutMovedBinding_Warning`
  - `Validator_BadAutoValue_Error`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Full suite. Expected: PASS.
- [ ] **Step 5: Commit:** `feat: startsWith/contains and phase validation`

---

### Task 3: Phase state, commands, log tags, save format 4

**Files:**
- Create: `src/Quartermaster.Core/Phases.cs`
- Modify: `Commands.cs`, `GameLog.cs`, `GameState.cs`, `NativeSave.cs`
- Test: `tests/Quartermaster.Core.Tests/PhaseStateTests.cs` (new), `NativeSaveTests.cs`, `GameLogTests.cs`

**Interfaces:**
- **Produces:**
  - `enum Difficulty { Hardcore, SemiAuto, FullyAuto }`, plus `static string DifficultyText.Display(Difficulty d)` → `"Hardcore"`, `"Semi-automatic"`, `"Fully automatic"`.
  - `sealed record PhasePosition(int Turn, int Repeat, string PhaseId, int PhaseIndex)`. `Repeat` is 1-based and is 1 outside blocks.
  - `sealed record PendingUnit(string? PieceId, string Label, double? Moved, bool Applied)`
  - `sealed record PendingRun(string Id, string FormulaId, string FormulaName, string Trigger, IReadOnlyList<PendingUnit> Units, bool NeedsInput)`. `Trigger` is `"start"` or `"move"`. `NeedsInput` marks a fully-automatic run waiting only for inputs that have no AutoValue.
  - `GameState` gains:
    - `PhasePosition? Position` (null = before turn 1)
    - `Difficulty Difficulty = Difficulty.SemiAuto`
    - `List<int> Ticked`
    - `List<int> Acknowledged`
    - `List<PendingRun> Pending`
  - `LogKind.Calc = "calc"`.
  - `LogEntry` gains two trailing parameters, `int? Turn = null` and `string? Phase = null`. `LogBuilder.Make` stamps `(Position.Turn, Turn.Phase)` when `Position != null`.
  - `LedgerDelta` gains two trailing parameters, `string? Tag = null` and `string? Subject = null`. Its log text is `"{Subject}: {Working} ({Tag})"`, leaving out each absent part. The kind is `calc` when `Tag != null`, otherwise unchanged. `LogEntry.PieceId` is set when the key is a piece key.
  - New commands (JSON discriminator in brackets; all end with `DateTimeOffset? At = null, string? Who = null`):
    - `AdvancePhase(PhasePosition? From, PhasePosition To, string FromName, string ToName, string? Reason = null, …)` ["advancePhase"]. The reducer:
      1. Logs one line for each pending run that still has unapplied units:
         - kind `calc`, text `"skipped: {FormulaName}, {labels}"`;
         - at most 3 unit labels joined with `", "`, then `" +{n} more"`.
      2. Logs a batch summary `"{FormulaName}: {n} units, {applied} applied"` (kind `calc`) when the run had more than one unit.
      3. Sets `Position = To` and `Turn = new TurnState(To.Turn, ToName)`, and clears `Ticked`, `Acknowledged` and `Pending`.
      4. Logs kind `turn` with the text:
         - `"Turn {To.Turn} · {FromName} → {ToName}"`;
         - or `"Turn {To.Turn} · {ToName}"` when From is null;
         - or kind `system` with the text `Reason` when Reason is given.
    - `SetDifficulty(Difficulty Difficulty, …)` ["setDifficulty"] logs `system`: `"Difficulty changed to {Display}"`.
    - `TickChecklist(int Index, bool Ticked, string Item, …)` ["tickChecklist"] adds the index to or removes it from `Ticked`, and logs `turn`: `"Done: {Item}"` / `"Not done: {Item}"`.
    - `AcknowledgeWarning(int Index, string Message, …)` ["acknowledgeWarning"] adds to `Acknowledged` and logs `turn`: `"Acknowledged: {Message}"`.
    - `QueueRuns(IReadOnlyList<PendingRun> Runs, …)` ["queueRuns"] appends to `Pending` and logs nothing.
    - `ResolveRun(string RunId, IReadOnlyList<string?> PieceIds, …)` ["resolveRun"] marks those units applied. When every unit is applied, it removes the run and, if the run had more than one unit, logs the batch summary (kind `calc`).
    - `AddLogLine(string Kind, string Text, string? PieceId = null, …)` ["addLogLine"] logs exactly that.
  - The reducer never throws, including on null lists or unknown run ids.
  - `NativeSave.FormatVersion = 4`. It reads 1–4. Validate normalises null `Ticked`/`Acknowledged`/`Pending` to empty lists, drops pending runs whose `Units` is null, and keeps `Position` (null for older formats).

- [ ] **Step 1: Write the failing tests:**
  - `AdvancePhase_SetsPositionTurn_LogsArrowText` (exact `"Turn 3 · Movement → Combat"`)
  - `AdvancePhase_FromNull_LogsStartText`
  - `AdvancePhase_DropsPending_LogsSkippedCapped` (5 unapplied units → `"skipped: Fuel for movement, A, B, C +2 more"`)
  - `AdvancePhase_WithReason_LogsSystem`
  - `SetDifficulty_Logs`
  - `TickChecklist_ToggleAndLog`
  - `AcknowledgeWarning_Recorded`
  - `ResolveRun_AllApplied_RemovesRunAndLogsSummary` (`"Daily water: 3 units, 3 applied"`)
  - `LedgerDelta_TagSubject_CalcKindAndText`
  - `LogEntry_TaggedWithTurnAndPhase_AfterStart`
  - `Commands_JsonDiscriminators_New`
  - `Replay_SameCommands_IdenticalJson`
  - `NativeSave_Format4_RoundTrip`
  - `NativeSave_ReadsFormat3_BeforeTurn1_SemiAuto`
- [ ] **Step 2:** Run `dotnet test tests/Quartermaster.Core.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement. Update the existing wrong-format test to use format 5.
- [ ] **Step 4:** Full suite. Expected: PASS.
- [ ] **Step 5: Commit:** `feat: phase state and commands (save format 4)`

---

### Task 4: Sequencer — position math and units per phase

**Files:**
- Create: `src/Quartermaster.Core/Sequencer.cs`
- Modify: `src/Quartermaster.Core/PieceFilter.cs`
- Test: `tests/Quartermaster.Core.Tests/SequencerTests.cs`

**Interfaces:**
- **Consumes:** `PhasePosition` (Task 3), and `PhaseDef`/`PhaseBlock`/`SideDef` (Task 1).
- **Produces:**
  - `static Func<PieceInstance, bool> PieceFilter.Compile(string? expr, GameState s, RulesPack pack)`. It parses once. Null or empty matches all. A parse error, or an evaluation error on a piece, gives false.
  - `static class Sequencer`:
    - `IReadOnlyList<PhaseBlock> ValidBlocks(RulesPack pack)`: blocks with both ids present, first ≤ last, `Count ≥ 1`, and no overlap with an earlier valid block. Everything else is ignored at runtime.
    - `PhasePosition? First(RulesPack pack)` → `(1, 1, phases[0].Id, 0)`, or null when there are no phases.
    - `PhasePosition? Next(RulesPack pack, PhasePosition cur)`:
      - At the last phase of a valid block with `Repeat < Count`, it goes to the block's first phase with `Repeat + 1`.
      - Otherwise it goes to index + 1 with `Repeat = 1`.
      - Past the end, it goes to `Turn + 1`, index 0.
      - Returns null when there are no phases.
    - `(PhasePosition? Position, string? Notice) Resolve(RulesPack pack, PhasePosition stored)`:
      - If the id exists, it refreshes `PhaseIndex` and clamps `Repeat` to 1..Count of the block (1 outside a block).
      - Otherwise it takes the phase at `min(stored.PhaseIndex, count − 1)`, with the notice `Phase "{stored.PhaseId}" no longer exists; continuing at "{name}"`.
      - With no phases it returns `(null, null)`.
    - `PhaseDef? Current(RulesPack pack, GameState s)`
    - `string? StageText(RulesPack pack, PhasePosition p)` → `"{Label or "Stage"} {Repeat} of {Count}"` inside a valid block, otherwise null.
    - `SideDef? ActingSide(RulesPack pack, PhaseDef phase)`
    - `IReadOnlyList<PieceInstance> UnitsFor(RulesPack pack, GameState s, PhaseDef phase, FormulaDef? formula, string? entryFilter)`. It returns pieces in `GameState.Pieces` order that match the side's `UnitFilter` (when the phase has a known side), the formula's `AppliesTo` and the entry filter.

- [ ] **Step 1: Write the failing tests** (invented phases W, S, M, C, E; a block M..C with Count 2):
  - `First_And_Next_LinearAndWrap` (E → turn 2, W)
  - `Next_Block_RepeatsThenLeaves` (M1 C1 M2 C2 E)
  - `Next_InvalidBlocksIgnored_Terminates` (overlapping, reversed, Count 0 and unknown-id blocks; 20 Next calls from W visit only valid positions and the turn increases)
  - `Resolve_CurrentPhaseDeleted_ClampsToOldIndex` (exact notice text)
  - `Resolve_RepeatAboveCount_Clamped`
  - `Resolve_NoPhases_ReturnsNull`
  - `StageText_InsideBlock` (`"Stage 2 of 2"`; a label `"Round"` gives `"Round 2 of 2"`)
  - `UnitsFor_SideFilterAndEntryFilterAndAppliesTo` (uses `startsWith(name(), "Red")`)
  - `Compile_BadExpression_MatchesNothing`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Full suite. Expected: PASS.
- [ ] **Step 5: Commit:** `feat: phase sequencer position math`

---

### Task 5: Decisions and warnings

**Files:**
- Create: `src/Quartermaster.Core/Decisions.cs`
- Test: `tests/Quartermaster.Core.Tests/DecisionsTests.cs`

**Interfaces:**
- **Consumes:** `Sequencer.Current`/`UnitsFor`, `PieceFilter.Compile` (Task 4), and the state fields (Task 3).
- **Produces:**
  - `enum DecisionKind { Checklist, Pending, Warning }`
  - `sealed record Decision(DecisionKind Kind, string Text, int Index, string? RunId, IReadOnlyList<string> PieceIds)`. `Index` is the checklist or warning index, or −1 for pending runs.
  - `sealed record WarningHit(int Index, string Message, IReadOnlyList<string> PieceIds, IReadOnlyList<string> UnitTexts, bool IsDecision, bool Acknowledged)`. A unit text is the `Message` with `{unit}` replaced by the unit name, or the bare unit name when there's no token.
  - `static IReadOnlyList<WarningHit> Decisions.Warnings(RulesPack pack, GameState s, PhaseDef phase)`. The condition is evaluated per unit of the phase's side (all units when there's no side). Only warnings with at least one hit are returned.
  - `static IReadOnlyList<Decision> Decisions.Compute(RulesPack pack, GameState s)`. It returns empty when `Position` is null or the phase is missing. Order and texts:
    - unticked checklist items: Text = the item;
    - pending runs:
      - more than one unit: `"{FormulaName} — {applied} of {n} applied"`;
      - one unit: `"{FormulaName} — {Label}"`;
      - NeedsInput: `"{FormulaName} — needs input"`;
    - unacknowledged warning decisions: `"{Message without {unit}, trimmed} — {n} unit"` / `"… — {n} units"`.

- [ ] **Step 1: Write the failing tests:**
  - `Compute_OrderAndTexts` (one of each kind; a batch of 3 with 1 applied gives `"Daily water — 1 of 3 applied"`)
  - `Warnings_PerUnitTexts_TokenReplaced`
  - `WarningDecision_ClosesWhenNoUnitMatches`
  - `WarningDecision_ClosesWhenAcknowledged`
  - `Warnings_LimitedToActingSide`
  - `Warnings_BadCondition_NoHits` (unparsable, unknown property, text compared with a number)
  - `Compute_NameOnlyPhase_NoDecisions` (coverage fallback)
  - `Compute_BeforeStart_Empty`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement. Compile each condition once per call, not once per unit.
- [ ] **Step 4:** Full suite. Expected: PASS.
- [ ] **Step 5: Commit:** `feat: phase decisions and warnings`

---

### Task 6: Runner — Moved, AutoValue, value sources, tags

**Files:**
- Modify: `src/Quartermaster.Core/FormulaRunner.cs`, `src/Quartermaster.Rules/Tables/TableLookup.cs`
- Test: `tests/Quartermaster.Core.Tests/FormulaRunnerTests.cs`, `tests/Quartermaster.Rules.Tests/TableTests.cs`

**Interfaces:**
- **Consumes:** `BindingKind.Moved`, `VariableBinding.AutoValue` (Task 1), and `LedgerDelta.Tag`/`Subject` (Task 3).
- **Produces:**
  - `static (int Row, int Col) TableLookup.Locate(TableDef t, Value row, Value? col)`. It throws the same `EvalError`s as `Lookup`, and `Lookup` uses it.
  - `RunRequest` gains two trailing parameters, `double? Moved = null` and `bool UseAutoValues = false`.
  - `sealed record TableCell(string TableId, int Row, int Col)`
  - `VariableState` gains two trailing parameters, `string? Source = null` and `TableCell? Cell = null`. Source texts:
    - Unit: `"{Property} of {unit name}"`
    - Global: `"{Property} (global)"`
    - Holder: `"{Property} of {holder name}"`
    - Table: `"{table name}, row {matched row key}"`, plus `", column {matched column key}"` for TwoD
    - Location: `"location of {unit name}"`
    - Moved: `"distance moved"`
    - Input via AutoValue: `"default"`
    - Roll and typed Input: null
  - Moved binding: `r.Moved` gives `Value.Of(m)` with Auto = true. When it's null, the variable is missing with the reason `"Only known when a unit is moved"`, and the user may type it as an override.
  - AutoValue: with `UseAutoValues` set and no typed value, the AutoValue expression is evaluated in the run context. Its `Display()` text then goes through the same Input validation (choices, min/max). A parse or evaluation error leaves the variable missing with that message.
  - `IReadOnlyList<GameCommand> Commands(FormulaTrace t, string? note, string? tag = null, string? subject = null)` passes Tag and Subject onto each `LedgerDelta`.

- [ ] **Step 1: Write the failing tests:**
  - `Locate_ReturnsMatchedIndices`
  - `Moved_FromRequest_Auto`
  - `Moved_Absent_MissingWithReason`
  - `AutoValue_UsedOnlyWhenRequested` (an Input with AutoValue `"Normal"` and Choices `Slow, Normal, Fast`)
  - `AutoValue_BadExpression_Missing`
  - `Sources_PerBindingKind` (a TwoD table gives `"TestTerrainCost, row Sand, column Dry"` with the Cell indices)
  - `Commands_CarryTagAndSubject`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Full suite. Expected: PASS.
- [ ] **Step 5: Commit:** `feat: runner Moved binding, AutoValue and value sources`

---

### Task 7: Key dropdowns, checked dialog model, Calculate, cheat-sheet model

**Files:**
- Create: `src/Quartermaster.Rules/Layout/KeyChoices.cs`, `src/Quartermaster.Render/CheatSheetModel.cs`
- Modify: `src/Quartermaster.Render/FormulaDialogModel.cs`
- Test: `tests/Quartermaster.Rules.Tests/KeyChoicesTests.cs`, `tests/Quartermaster.Render.Tests/FormulaDialogModelTests.cs`, `CheatSheetModelTests.cs`

**Interfaces:**
- **Consumes:** `RunRequest.Moved`/`UseAutoValues`, `VariableState.Source`/`Cell`, and `Commands(..., tag)` (Task 6).
- **Produces:**
  - `static IReadOnlyList<string>? KeyChoices.For(RulesPack pack, FormulaDef f, string variable)` (same namespace as `FormulaLayout`). The first rule that applies wins:
    1. An Input with `Choices` → those choices.
    2. An Input that is the **whole** `RowExpr`/`ColExpr` of a Table binding (a bare `VarRef`), or the bare row/column argument of `lookup(Table, X[, Y])` in the formula text → that table's row or column keys.
    3. Otherwise null.

    An axis containing any Range/AtLeast/AtMost key gives null. Duplicate keys are removed, keeping their order.
  - `BoxView` gains two trailing parameters, `bool Wrong = false` and `IReadOnlyList<string>? Choices = null`. Choices come from `KeyChoices.For`, for Input boxes only.
  - `FormulaDialogModel` gains:
    - `bool Checked { get; set; }` (manual modes).
    - `double? Moved { get; set; }`
    - `bool AutoCalculated { get; }`
    - `FormulaTrace? LastExpected { get; }`
    - `void Calculate(GameState s)`
  - **Checked behaviour.** `Evaluate` runs twice with the same dice pins:
    - The **expected** run uses the typed Inputs, no overrides and `UseAutoValues = false`.
    - The **actual** run uses the typed Inputs plus the box texts as overrides.
    - Unit, Global, Holder, Table, Location and Moved boxes show only what the user typed (they start empty).
    - Roll boxes behave as today. Input boxes are never checked.
    - A box is `Wrong` when its text is non-empty and doesn't equal the expected value:
      - numbers compare invariantly (`TableLookup.TryNum` rules, U+2212 accepted);
      - text compares by `TableLookup.Matches` on a Text key.
    - A wrong box's Tooltip is `"Doesn't match ({Source})"`, and the status is `"Doesn't match: {var}, {var}"`.
    - `CanApply` is false while any box is wrong or missing.
  - **Calculate.**
    - It runs with `UseAutoValues = true` and no overrides, which rolls the dice and pins them.
    - Each engine value's `Display()` text is copied into the boxes: overrides for non-Input variables, typed values for Inputs.
    - It sets `AutoCalculated = true`. The boxes stay editable.
  - `sealed record CheatRow(string Variable, string Source, string ValueText, TableDef? Table, int? HighlightRow, int? HighlightCol)`
  - `static IReadOnlyList<CheatRow> CheatSheetModel.Build(FormulaTrace expected, RulesPack pack)`:
    - one row per variable;
    - a Roll row shows `"{n}d{s} — roll it"` (never the value);
    - a typed Input row shows `"typed by you"`;
    - a table row carries the whole `TableDef` and the highlight from `Cell`.

- [ ] **Step 1: Write the failing tests:**
  - `KeyChoices_InputChoices`
  - `KeyChoices_BareRowExpr_TableRowKeys`
  - `KeyChoices_LookupArgs_RowAndColumn`
  - `KeyChoices_RangeKeys_Null`
  - `KeyChoices_ComputedExpr_Null` (`RowExpr = "Terrain + 1"`)
  - `Checked_BoxesStartEmpty_CannotApply`
  - `Checked_WrongValue_RedTooltipStatus_ApplyDisabled` (exact `"Doesn't match (TestTerrainCost, row Sand)"`)
  - `Checked_CorrectValues_CanApply`
  - `Checked_TypedFormats_CompareInvariant` (`"3.0"`, `" sand "` match; `"1,5"` is not a number; `"−2"` matches −2)
  - `Calculate_FillsBoxes_SetsAutoCalculated_PinsDice`
  - `Unchecked_BehavesAsPiece2` (the existing tests stay green)
  - `CheatSheet_TableRowHighlighted_DiceNotShown`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Full suite. Expected: PASS.
- [ ] **Step 5: Commit:** `feat: checked formula model, Calculate, key choices and cheat sheet`

---

### Task 8: Phase controller — advance, triggers, modes, move measuring, autosave hook

**Files:**
- Create: `src/Quartermaster.Render/PhaseController.cs`, `src/Quartermaster.Render/MoveMeasure.cs`
- Modify: `ModuleSession.cs` (difficulty on new games), `Autosaver.cs`
- Create (fixture): `tests/Quartermaster.TestFixtures/PhaseFixture.cs`. Add project references from TestFixtures to Rules and Vassal.
- Create: `tests/Quartermaster.Render.Tests/SmokeFixtureWriter.cs`. This env-gated test is skipped when `QM_WRITE_SMOKE_FIXTURE` is unset. It copies `PhaseFixture.Module().Build()` to `<dir>/phases.vmod` and saves `PhaseFixture.Pack(sha)` to `<dir>/phases.qrules`. The written files are never committed. App smokes in Tasks 10–14 use them.
- Test: `tests/Quartermaster.Render.Tests/PhaseControllerTests.cs`, `MoveMeasureTests.cs`, `AutosaverTests.cs`

**Interfaces:**
- **Consumes:** Tasks 3–6.
- **Produces:**
  - `PhaseFixture` contents (invented):
    - `static FixtureModule Module()`: MinimalBuildFile plus a setup "Start" holding pieces "Red 1", "Red 2" and "Blue 1", each with a property sheet `Fuel`.
    - `static RulesPack Pack(string moduleSha)`:
      - properties Fuel (sheet) and Water (default 10);
      - a OneD table `TestTerrainCost` with keys Clear and Sand;
      - sides Red (`startsWith(name(), "Red")`) and Blue;
      - phases Weather (once), Supply (Red; units "Daily water": Water − 1), Movement (Red; OnMove "Fuel for movement" = `Moved * lookup(TestTerrainCost, Terrain)`, where Terrain is an Input with AutoValue `"Clear"`), Combat (checklist "Resolve attacks"; warning `Fuel <= 0`, "Out of fuel", IsDecision) and End (name only);
      - block Movement..Combat, Count 2.
  - `ModuleSession.StartNewGame(Difficulty? difficulty = null)` and `LoadSetup(SetupDef s, Difficulty? difficulty = null)` set `State.Difficulty` before `Replace`. No log entry.
  - `string? Autosaver.SaveNow(DateTimeOffset now, int intervalMinutes)`: writes an autosave right away unless `intervalMinutes <= 0`, prunes, and resets the clock. It never throws, like `Tick`.
  - `static (int Cells, string? Problem) MoveMeasure.Cells(ModuleModel m, string fromMap, PointD from, string toMap, PointD to)`. It uses `HexDistance` on the board under both points (board-local). It returns `(0, "no grid")`, `(0, "different maps")` or `(0, "different boards")` when it can't measure.
  - `sealed record MoveRecord(string PieceId, string FromMap, PointD From)`
  - `enum ChipState { Done, Current, Upcoming }`
  - `sealed record PhaseChip(string Id, string Name, ChipState State)`
  - `sealed record PhaseView(bool NoPhases, bool BeforeStart, int Turn, string? StageText, string? SideName, IReadOnlyList<PhaseChip> Chips, PhaseDef? Current, string? NextName, IReadOnlyList<Decision> Decisions, IReadOnlyList<WarningHit> Warnings, string? Notice)`
  - `sealed class PhaseController(ModuleSession s, Func<GameCommand, GameState, GameCommand> prepare)`. Every command it issues goes through `prepare`.
    - `PhaseView View()`
    - `void Start()`: Position null → `AdvancePhase(null → First)`, then OnEnter.
    - `string? Advance()`:
      - Before turn 1 it calls `Start()`.
      - Otherwise it executes `AdvancePhase(cur → Next)` and then OnEnter.
      - It returns `"Pass to {side} player"` when the acting side changed, or null.
      - It raises `PhaseBoundary`.
      - It's a no-op without phases.
    - **OnEnter.** Each OnStart entry runs in order:
      - A missing formula is skipped with `AddLogLine(calc, "{phase}: formula \"{id}\" not found — skipped")`.
      - Units come from `ForEach.Once` (a single unit with PieceId null, Label = the formula name) or from `Sequencer.UnitsFor`.
      - **FullyAuto:** every unit runs with `UseAutoValues`.
        - A successful run executes `Commands(trace, null, "auto", unitName)`.
        - A run with missing values collects that unit into one `PendingRun` with `NeedsInput = true`, which is queued.
        - A run that errors gets `AddLogLine(calc, "{formula}: {unit}: {error}")`.
        - When there's more than one unit, it ends with `AddLogLine(calc, "{formula}: {n} units, {applied} applied")`.
      - **Hardcore/SemiAuto:** one `QueueRuns` per entry, with run id `Guid.NewGuid().ToString("N")`.
      - `RunsQueued` is raised with the runs that were queued.
    - `void ExecuteMove(IReadOnlyList<MovePieces> moves)`: one gesture.
      1. Captures `MoveRecord`s before the moves and executes every move.
      2. Then, for each OnMove entry of the current phase, takes the moved pieces that match (side + AppliesTo + entry filter).
      3. Measures each with `MoveMeasure`. A `Problem` gives `AddLogLine(calc, "{unit}: moved distance counted as 0 ({problem})")`.
      4. Applies the units per mode, as in OnEnter, with `Trigger = "move"` and each unit's `Moved` set.
    - `void SetDifficulty(Difficulty d)` executes `SetDifficulty` only when it changes. Pending runs are untouched.
    - `void Tick(int index, bool ticked)`
    - `void Acknowledge(int warningIndex)`
    - `void ApplyFormula(string? runId, string? pieceId, FormulaTrace trace, bool calculated)` executes `Commands(trace, null, calculated ? "auto-calculated" : null, unitName)`, then `ResolveRun(runId, [pieceId])` when runId is set.
    - `string? Reconcile()`: after a load or rules change. It applies `Sequencer.Resolve`. When the position changed, it executes `AdvancePhase(stored → resolved, Reason = notice)`. It returns the notice for the strip.
    - `event Action<IReadOnlyList<PendingRun>>? RunsQueued`
    - `event Action? PhaseBoundary`

- [ ] **Step 1: Write the failing tests** (PhaseFixture, temp roots, a fixed roller, `prepare = (c, _) => c`):
  - `Start_EntersFirstPhase_FiresOnStart`
  - `FullyAuto_FullTurnWrap_BlockTwice` (Advance until Turn 2: the log has two `"Movement → Combat"` lines in turn 1, Water fell by 1 per Red unit, and nothing is pending)
  - `FullyAuto_OneLinePerUnitPlusSummary`
  - `FullyAuto_InputWithoutAutoValue_QueuesNeedsInput`
  - `SemiAuto_OnStartUnits_QueuesOneRunPerEntry_RaisesRunsQueued`
  - `Hardcore_SameQueueAsSemiAuto`
  - `ExecuteMove_FormationDrag_OneRunWithAllUnits_MovedSet` (two pieces, distances 2 and 3 cells on the 50 px grid)
  - `ExecuteMove_FullyAuto_AppliesFuel`
  - `ExecuteMove_NoGrid_ZeroAndLogLine`
  - `Advance_DropsPending_LogsSkipped`
  - `Advance_SideChange_ReturnsPassNotice`
  - `SetDifficulty_PendingRunsKept`
  - `ApplyFormula_Calculated_TagsAndResolves`
  - `OnStart_MissingFormula_SkippedWithLogLine`
  - `OnStart_BadFilter_LogsLineAndSkips`
  - `Reconcile_AfterRulesEdit_LogsAndRelocates`
  - `Replay_CommandLog_ReproducesState`: clone the initial state, apply `session.Log`, and the JSON is equal.
  - `MoveMeasure_SquareGrid_Cells`, `MoveMeasure_DifferentMaps_Problem`
  - `Autosaver_SaveNow_WritesUnlessOff`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Full suite. Expected: PASS.
- [ ] **Step 5: Commit:** `feat: phase controller with difficulty modes and triggers`

---

### Task 9: Batch grid model

**Files:**
- Create: `src/Quartermaster.Render/BatchModel.cs`
- Test: `tests/Quartermaster.Render.Tests/BatchModelTests.cs`

**Interfaces:**
- **Consumes:** `FormulaDialogModel` (Task 7), `PendingRun`, `PhaseController.ApplyFormula` (Task 8), and `Decisions.Warnings` (Task 5).
- **Produces:**
  - `sealed record BatchRowView(string? PieceId, string Label, IReadOnlyList<string> Cells, IReadOnlyList<bool> Wrong, string Result, string LedgerText, string Exceptions, bool Applied, bool CanApply)`:
    - `LedgerText` = `"{Prop} {before} → {after}"` joined `"; "`;
    - `Exceptions` = `"goes negative"` and/or the names of warnings the unit hits, joined `"; "`.
  - `sealed class BatchModel(ModuleSession s, PhaseController phases, string runId)`:
    - `IReadOnlyList<string> Variables`
    - `int RowCount`
    - `string Title` → `"{FormulaName} — {applied} of {n} applied"`
    - `BatchRowView Row(int i)`: lazy; it builds one `FormulaDialogModel` per row on demand (`Checked = Difficulty != FullyAuto`, `Moved` from the unit) and caches the view until `Invalidate()`.
    - `void SetCell(int row, string variable, string text)`
    - `void CalculateRow(int row)`
    - `void CalculateAll()`
    - `bool ApplyRow(int row)`: through `phases.ApplyFormula`, with calculated = that row's `AutoCalculated`.
    - `int ApplyAll()`: applies every row that can apply and returns the count.
    - `FormulaDialogModel ModelFor(int row)`
    - `void Invalidate()`
  - A run that's no longer pending (phase ended) gives `RowCount = 0` and the Title `"(finished)"`.

- [ ] **Step 1: Write the failing tests:**
  - `Rows_HardcoreCellsEmpty_CannotApply`
  - `CalculateAll_ThenApplyAll_ResolvesRun_OneSummary`
  - `ApplyRow_Wrong_Refused`
  - `Exceptions_NegativeAndWarning`
  - `LazyRows_1200Units_OnlyVisibleBuilt`: build `Row(0)` and `Row(1199)` only; an internal `BuiltCount` (InternalsVisibleTo) is 2.
  - `RunGone_ZeroRows`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Full suite. Expected: PASS.
- [ ] **Step 5: Commit:** `feat: batch grid model`

---

### Task 10: Tracker strip, phase panel, Next Phase

**Files:**
- Create: `src/Quartermaster.App/PhaseStrip.cs`, `PhasePanel.cs`, `PhaseDock.cs`, `NextPhaseDialog.cs`
- Modify: `MainForm.cs` (create the controller per session, strip under the toolbar, View → Phase panel, `turn.next` handler), `MainForm.Settings.cs` (`PhaseBoundary` → `_autosaver.SaveNow`), `CommandLog.cs` (Stamp the new commands), `Program.cs`/`MainForm.Smoke.cs` (flags)

**Interfaces:**
- **Consumes:** `PhaseController`, `PhaseView`, `Decision`, `WarningHit` (Tasks 5, 8).
- **Produces:**
  - `PhaseStrip : Control` is docked top under `_toolbar`. Content, left to right:
    - `"Turn {n}"`;
    - the StageText when present;
    - one chip per phase: Done chips greyed, the Current chip highlighted, and the Current chip's text `"{Side} player — {Phase}"` when there's a side;
    - the notice (the pass notice or the relocation notice) at the right, until the next advance.
    - Chips wrap onto extra rows; the height grows up to 3 rows, then the strip scrolls horizontally.
    - Before start it shows the `Start game ▶` button. With no phases it shows only the no-phases text.
  - `PhasePanel : UserControl` shows:
    - the phase name and instructions (a read-only multiline box);
    - a rule-ref link (`MainForm.OpenDocumentAtPage`);
    - a "Warnings" TreeView of the matching warnings, expandable to the unit texts, with an **Acknowledge** button for decision warnings;
    - "Decisions left": a CheckedListBox for checklist items (ticking calls `Tick`) plus a list of pending runs and warning decisions;
    - a bottom button `Next Phase ▶ {NextName}` with the label `{N} decisions left`, red when N > 0.
    - Double-clicks:
      - a unit row → `CentreOnPiece`;
      - a pending run → open its UI (Task 11/12 entry point `MainForm.OpenPendingRun(string runId)`; until those tasks land, a stub that writes to the status bar).
    - It refreshes on `Game.Changed`/`GameReplaced`/`RulesChanged`, debounced 150 ms.
  - `PhaseDock` follows the `LogDock` pattern: docked right (shared with the palette host, stacked), pop-out under layout key `"phases"`.
  - `NextPhaseDialog(IReadOnlyList<Decision>)` has a ListBox of the decision texts and the buttons **Continue anyway** (OK) and **Go back** (Cancel). Double-clicking an item returns Cancel plus `JumpTo` = that decision.
  - Next Phase flow:
    - 0 decisions → `Advance()` at once;
    - otherwise the dialog → Continue → `Advance()`.
    - The returned notice goes to the strip.
  - `turn.next` (Ctrl+Right) runs this flow when the pack has phases, otherwise the old `StepTurn(1)`. Tools → Turn keeps Set… and Next/Previous turn as manual corrections.
  - On `GameReplaced` and `RulesChanged`, MainForm calls `Reconcile()` and shows any notice it returns.
  - The controller's `prepare` = `(c, st) => CommandLog.Prepare(c, st, _session.Model)`. `CommandLog.Stamp` covers every new command that has At/Who.
  - Smoke flags: `--difficulty hardcore|semiauto|fullyauto` (used for `--new`/`--setup` starts), `--smoke-phases`, `--screenshot-phases <png>`.

- [ ] **Step 1:** Implement. The logic is already tested in Tasks 4–8, so this task has no new unit tests.
- [ ] **Step 2:** Build and run the full suite. Expected: PASS, 0 warnings.
- [ ] **Step 3:** Write the fixture with `QM_WRITE_SMOKE_FIXTURE=<dir> dotnet test tests/Quartermaster.Render.Tests --filter SmokeFixtureWriter` (Task 8), then smoke:
  `QUARTERMASTER_LAYOUT_ROOT=<scratch>/qmroot dotnet run --project src/Quartermaster.App -- <dir>/phases.vmod --setup 0 --rules <dir>/phases.qrules --difficulty semiauto --smoke 8 --screenshot-phases <scratch>/strip.png`
  Expected: exit 0, and the screenshot shows `Start game ▶`. Repeat with an empty pack (`--new-rules SmokePack`). Expected: the no-phases text.
- [ ] **Step 4: Commit:** `feat: phase tracker strip, phase panel and Next Phase`

---

### Task 11: Formula dialog in the modes, cheat sheet, move routing, missing-input dialog

**Files:**
- Create: `src/Quartermaster.App/CheatSheetWindow.cs`, `MissingInputDialog.cs`
- Modify: `FormulaDialog.cs`, `FormulaFormView.cs`, `MapDragController.cs`, `MapWindow.cs`, `MainForm.cs` (`OpenPendingRun`, `RunsQueued` handler)

**Interfaces:**
- **Consumes:**
  - `FormulaDialogModel.Checked`/`Moved`/`Calculate`/`LastExpected` and `BoxView.Wrong`/`Choices` (Task 7);
  - `CheatSheetModel` (Task 7);
  - `PhaseController.ExecuteMove`/`ApplyFormula`/`RunsQueued` (Task 8).
- **Produces:**
  - `sealed record RunLink(string RunId, double? Moved)` (App).
  - `FormulaDialog.Show(ModuleSession s, string formulaId, string? pieceId, Form owner, MainForm? host, RunLink? link = null)` changes:
    - Dedupe is keyed on (formula, piece, runId).
    - `Checked` is true when `Difficulty` is Hardcore or SemiAuto. This also applies to dialogs opened from a unit's menu. In FullyAuto those keep the piece-2 prefilled behaviour, and the workbench preview is never checked.
    - A **Calculate** button sits beside Apply.
    - Apply calls `PhaseController.ApplyFormula(link?.RunId, pieceId, trace, model.AutoCalculated)` and then closes.
    - Red boxes use `BackColor = Color.MistyRose` and the tooltip from `BoxView`.
    - In SemiAuto it opens a `CheatSheetWindow` beside the dialog. That window is modeless and owned by the dialog, closes with it, and re-renders on each Recalc. It shows:
      - a ListView of Variable / Source / Value;
      - each table drawn whole in a DataGridView, with the highlighted cell in `Color.Gold`.
    - Difficulty switches take effect when the dialog is next opened.
  - `FormulaFormView` turns a box into a `ComboBox { DropDownStyle = DropDown }` when `BoxView.Choices` is non-null. Typing is still allowed. The control interface stays `Type(variable, text)`/`Apply(view)`.
  - All move gestures go through `PhaseController.ExecuteMove`. This covers MapDragController.Commit (all groups of one drop as one call) and MapWindow move actions (Send to / keyboard moves built from `PieceActions`, via `Execute`). Non-move commands keep the old path.
  - `MainForm` handles `RunsQueued`:
    - Hardcore/SemiAuto: one unit → `FormulaDialog.Show` with a RunLink; more than one → `OpenPendingRun` (the batch grid from Task 12; until then, open the first unit's dialog).
    - NeedsInput runs → `MissingInputDialog`. It shows only the Input variables that are missing (with Choices dropdowns), asks once and applies the values to every unit of the run through `ApplyFormula`. A **Per unit…** button opens the batch grid instead. Cancel leaves the run listed as a decision.
  - Smoke flag: `--screenshot-cheatsheet <png>`.

- [ ] **Step 1:** Implement.
- [ ] **Step 2:** Full suite. Expected: PASS.
- [ ] **Step 3:** Run the fixture smoke with `--difficulty hardcore --smoke-phases --smoke 15`. The smoke step (in `MainForm.Smoke.cs`, `SmokePhases`) does the following:
  - starts the game;
  - moves "Red 1" two cells through `ExecuteMove`;
  - asserts that a FormulaDialog opened with an empty Moved box;
  - types a wrong value and asserts Apply is disabled;
  - presses Calculate and asserts Apply is enabled;
  - applies, and asserts that a `formula`-kind log line was added and the run is no longer pending.

  Repeat with `--difficulty semiauto --screenshot-cheatsheet <scratch>/cheat.png`. Expected: exit 0 both times.
- [ ] **Step 4: Commit:** `feat: checked formula dialog, Calculate, cheat sheet and move triggers`

---

### Task 12: Batch grid window, difficulty choice, Game settings tab, Calculations log kind

**Files:**
- Create: `src/Quartermaster.App/BatchGridWindow.cs`
- Modify: `SetupChooserDialog.cs`, `SettingsTabs.cs` (+ `GameTab`), `SettingsDialog.cs`, `MainForm.cs` (`NewGame`, `LoadSetupDialog`, `StartGameFor`, `OpenPendingRun`), `LogPanel.cs`
- Modify: `src/Quartermaster.Render/Settings.cs`, `SettingsStore.cs`, `LogFilter.cs`
- Test: `tests/Quartermaster.Render.Tests/SettingsTests.cs`, `LogFilterTests.cs`

**Interfaces:**
- **Consumes:** `BatchModel` (Task 9), `PhaseController.SetDifficulty` (Task 8), and `ModuleSession.StartNewGame/LoadSetup(…, difficulty)`.
- **Produces:**
  - `Settings.LastDifficulty = Difficulty.SemiAuto`. `SettingsStore` reads it with `r.Enum` (a bad value falls back with one diagnostic) and saves it.
  - `LogFilter.AllKinds` gains `LogKind.Calc`. `static string LogFilter.KindLabel(string kind)` gives `"Calculations"` for `calc` and the kind itself otherwise. `ToText` adds `"T{turn} {phase}"` after the time when the entry is tagged.
  - `LogPanel` adds a "Calculations" filter button and a `Turn · Phase` column filled from `LogEntry.Turn`/`Phase`.
  - `BatchGridWindow(BatchModel)` is opened via `OpenChild` with key `"batch:{runId}"`. It has:
    - a DataGridView in `VirtualMode` with columns Unit, one per variable (editable; Wrong cells in MistyRose with the tooltip), Result, Ledger, and Exceptions;
    - buttons **Calculate all**, **Apply all** and **Apply row**;
    - in SemiAuto, a `CheatSheetWindow` docked right that follows the selected row (`LastExpected` of `ModelFor(row)`).

    Double-clicking a row opens that unit's FormulaDialog with a RunLink. The title follows `BatchModel.Title`, and the window closes itself when the run is gone.
  - `MainForm.OpenPendingRun(string runId)` picks the right UI:
    - a batch → the grid;
    - a single unit → the dialog;
    - NeedsInput → `MissingInputDialog`.
  - `SetupChooserDialog` gains a "Difficulty" group with three radio buttons, each followed by a one-line explanation:
    - Hardcore: `"Find every value in the charts and type it in."`
    - Semi-automatic: `"Type the values in; a cheat sheet shows where each one comes from."`
    - Fully automatic: `"The game rolls and calculates everything itself and logs it."`

    The default is `Settings.LastDifficulty`. The choice is saved back to it and exposed as `SelectedDifficulty`. `NewGame()` now shows this dialog too, preselecting "New game from module". Smoke skips the dialog and uses `--difficulty` (default SemiAuto).
  - `GameTab` (Settings → "Game") has a Difficulty combo. On OK/Apply it calls `PhaseController.SetDifficulty` (when a game is open) and stores `LastDifficulty`. It's disabled with the hint "Open a game first" when there's no game.
  - Smoke flag: `--screenshot-batch <png>`.

- [ ] **Step 1: Write the failing tests:**
  - `SettingsStore_LastDifficulty_RoundTripAndBadValue`
  - `LogFilter_CalcKind_LabelAndFilter`
  - `ToText_IncludesTurnPhaseTag`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Full suite. Then run the fixture smoke with `--difficulty semiauto --smoke-phases --screenshot-batch <scratch>/batch.png`: it advances to Supply, the batch grid opens for "Daily water", and smoke runs Calculate all and Apply all. Expected: exit 0, the decision is gone, and the summary line is logged.
- [ ] **Step 5: Commit:** `feat: batch grid, difficulty choice and calculations log`

---

### Task 13: Rules Workbench Phases tab and binding details

**Files:**
- Create: `src/Quartermaster.Render/PhaseEdit.cs`, `src/Quartermaster.App/Workbench/PhasesTab.cs`
- Modify: `Workbench/WorkbenchWindow.cs` (fourth tab "Phases", `ShowPhase(string id)`), `Workbench/FormulasTab.Grids.cs` (Moved has no details), `src/Quartermaster.Render/FormulaEdit.cs`
- Test: `tests/Quartermaster.Render.Tests/PhaseEditTests.cs`, `FormulaEditTests.cs`

**Interfaces:**
- **Consumes:** the Task 1 model and the Task 2 validator.
- **Produces:**
  - `static class PhaseEdit` (pure; each method returns `(RulesPack Pack, string? Error)`, and the pack is unchanged when there's an error):
    - `Add(pack, string name, int at)` gives a new phase with id `Guid.NewGuid().ToString("N")`.
    - `Duplicate(pack, int index)` gives a new id and the name `"{name} (copy)"`, inserted after the source phase.
    - `Remove(pack, int index)`:
      - a block whose first/last is removed shrinks to its neighbour inside the block;
      - a block left with no phases is removed.
    - `Move(pack, int index, int delta)` gives the error `"That move would put a block's first phase after its last"`.
    - `MakeBlock(pack, int first, int last, int count, string label)` gives the errors `"Select neighbouring phases"`, `"Count must be at least 1"` and `"These phases are already in a block"`.
    - `RemoveBlock(pack, int blockIndex)`
    - `AddSide(pack, string name)`
    - `RenameSide(pack, string id, string name)`, with the error `"Another side already has this name"` (case-insensitive).
    - `RemoveSide(pack, string id)` clears `SideId` on the phases that used it.
  - `FormulaEdit.FormatDetails`/`ParseDetails` accept `| auto: <expr>` after Input details (for example `Slow, Normal, Fast | auto: "Normal"`). This sets `AutoValue`. A parse problem in the auto expression is returned as the error `"Auto value: {msg}"`.
  - `PhasesTab : UserControl`:
    - **Left side:**
      - a Sides list (Add, Rename, Remove, and a unit filter box with a live parse check);
      - below it, the phase ListView, where block members are shown with a `┃ {label} ×{count}` bracket prefix;
      - buttons Add, Duplicate, Remove, Move Up, Move Down, "Make block from selection…" (asks for the count and label) and "Remove block".
    - **Right side**, for the selected phase:
      - name, side combo (with "(none)"), instructions (multiline) and a rule-ref picker (reuse the FormulasTab picker);
      - a checklist ListBox with Add/Remove;
      - an OnStart grid (formula combo, ForEach combo, filter);
      - an OnMove grid (formula combo, filter);
      - a warnings grid (condition with a live parse check through `FormulaEdit.SyntaxProblem`, message, IsDecision).
    - Edits go through `ModuleSession.UpdateRules`, debounced as in FormulasTab (P2-R10 rules apply). The validator issues for the phase are shown under the fields.

- [ ] **Step 1: Write the failing tests:**
  - `Add_Duplicate_Remove_KeepOrder`
  - `Remove_BlockEndShrinks_EmptyBlockRemoved`
  - `Move_BreakingBlock_Refused`
  - `MakeBlock_NonContiguousOrOverlap_Refused`
  - `RemoveSide_ClearsPhaseSide`
  - `RenameSide_Duplicate_Refused`
  - `ParseDetails_InputWithAuto`
  - `FormatDetails_InputWithAuto_RoundTrip`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Full suite. Then smoke `--new-rules SmokePack --screenshot-workbench <scratch>/wb.png --workbench-tab phases`. Extend `--workbench-tab` to accept `phases`. The smoke adds two neutral phases ("Smoke phase A/B") and a block through `PhaseEdit`. Expected: exit 0, and the tab is visible.
- [ ] **Step 5: Commit:** `feat: workbench phases tab`

---

### Task 14: End-to-end, three-mode smokes, docs

**Files:**
- Create: `tests/Quartermaster.Render.Tests/Piece3EndToEndTests.cs`
- Modify: `src/Quartermaster.App/MainForm.Smoke.cs` (complete `SmokePhases`), `docs/superpowers/piece1-followups.md` (append a "Piece 3" section)

- [ ] **Step 1:** Make sure the smoke fixture is current. Rerun `SmokeFixtureWriter` from Task 8 with `QM_WRITE_SMOKE_FIXTURE=<scratch>/fx`.
- [ ] **Step 2: Write the end-to-end tests** (PhaseFixture, temp roots):
  - `E2E_FullyAuto_TurnWrap_SaveLoadReplay`:
    - Start, move "Red 1", and Advance until Turn 2 (with the Movement..Combat block twice).
    - Save as format 4, load, then `Reconcile()` returns null.
    - Replaying the loaded command log onto the initial state gives identical JSON.
  - `E2E_SemiAuto_PendingApplyAndSkip`: apply one queued run through `ApplyFormula`, leave another, and advance. The log has `"skipped: …"`, and there are 0 pending.
  - `E2E_Hardcore_WrongValueBlocksApply_CalculateApplies`: a checked `FormulaDialogModel` for a queued run refuses a wrong value, and Calculate then applies it with the tag `auto-calculated`.
  - `E2E_OldSaveFormat3_BeforeTurn1_StartWorks`
  - `E2E_PackFormat1_NoPhases_FreeForm`: View().NoPhases, and Advance is a no-op.
- [ ] **Step 3:** Run the full suite. Expected: PASS. If a test fails, fix the owning task's code and add a regression test there.
- [ ] **Step 4: Complete `SmokePhases`** so that, in every mode, it starts the game and advances until `Turn == 2`, clearing the decisions as it goes:
  - **manual modes:** Calculate + Apply in each opened dialog and Calculate all + Apply all in each batch grid; tick the checklist; on the first Combat visit, press Next with a decision left so the Next Phase dialog takes the Continue-anyway path (auto-answered in smoke).
  - **FullyAuto:** assert that no FormulaDialog or BatchGridWindow ever opened.
  - **All modes:** assert that the log has two `Movement → Combat` lines in turn 1, that autosave files appeared under `<QUARTERMASTER_LAYOUT_ROOT>/autosave`, and that `Position.Turn == 2`.

  It sets `Program.ExitCode = 1` with a log line on any failed assertion.
- [ ] **Step 5: Run the smokes.** For each of `hardcore`, `semiauto` and `fullyauto`, run:
  `QUARTERMASTER_LAYOUT_ROOT=<scratch>/qmroot-<mode> dotnet run --project src/Quartermaster.App -- <scratch>/fx/phases.vmod --setup 0 --rules <scratch>/fx/phases.qrules --difficulty <mode> --smoke-phases --smoke 30 --screenshot-phases <scratch>/phases-<mode>.png`
  Expected: exit 0 for all three, with screenshots showing the strip and panel. Also rerun the piece-2b smoke on the fixture (`--new --smoke 5`), which should still exit 0.
- [ ] **Step 6: Docs.** Append "Piece 3 — deferred minors and rulings" to `docs/superpowers/piece1-followups.md`. Include generic engine notes only, the rulings made during execution, and the resolved spec ambiguities listed at the end of this plan.
- [ ] **Step 7: Write the hands-on checklist in the report:**
  - Start game ▶;
  - the strip wrapping with many phases;
  - the pass notice;
  - Next Phase with decisions left, including the double-click jump;
  - each mode on the user's own TEST pack (the user extends it, not the builder);
  - the cheat sheet beside the dialog;
  - red boxes;
  - key dropdowns;
  - Calculate;
  - the batch grid at real-module scale;
  - switching difficulty mid-game;
  - deleting the current phase in the workbench mid-game;
  - autosave at the phase boundary.
- [ ] **Step 8: Commit:** `test: piece 3 end-to-end and smokes; docs: follow-ups`

---

## Spec coverage map

| Spec section | Task(s) |
|---|---|
| §1 Intent (guided turn, Next Phase, modes) | 8, 10, 11, 12 |
| §2 Content boundary | Global Constraints; 8 (PhaseFixture invented), 13/14 (neutral smoke names) |
| §3.1 Phase tracker | 10 |
| §3.2 User makes decisions | 5, 10 |
| §3.3 Next Phase warns and lists, continue anyway | 10 |
| §3.4 Three modes, chosen at setup | 7, 8, 11, 12 |
| §3.5 Switchable mid-game in Settings | 8 (`SetDifficulty`), 12 (Game tab) |
| §3.6 Calculate button | 7, 11 |
| §3.7 Phase list editor | 13 |
| §3.8 Acting side shown | 4, 10 |
| §3.9 Repeating groups | 1, 4, 13 |
| §3.10 Key dropdowns | 7, 11, 12 |
| §3.11 Typed values checked, wrong blocks Apply | 7, 11, 12 |
| §4 Goals; anti-goals (no pop-up per unit, traceable numbers, never block) | 8, 9, 10, 12; Global Constraints |
| §4 Non-goal: step back deferred; autosave at boundaries | 8 (`SaveNow`), 10 |
| §5.1 Sides, UnitFilter, startsWith/contains | 1, 2, 4 |
| §5.2 Phases (Checklist, OnStart, OnMove, Warnings, Acknowledge) | 1, 3, 5, 8, 10 |
| §5.3 Repeating blocks, "Stage 2 of 3" | 1, 4, 10 |
| §5.4 Moved binding | 1, 6, 8 (MoveMeasure) |
| §5.5 AutoValue | 1, 6, 13 |
| §5.6 Key dropdowns | 7, 11 |
| §6 Position by id + relocation, Start game, Difficulty, per-phase state, commands, save format 4 | 3, 4, 8, 10 |
| §7.1 Tracker strip, phase panel, coverage fallback, no-phases text | 5, 10 |
| §7.2 Next Phase flow, log, pass notice, OnStart, autosave, skipped runs, Ctrl+Right | 3, 8, 10 |
| §7.3 Modes table, cheat sheet, Calculate, checked values, switching | 7, 8, 11, 12 |
| §7.4 Batch grid | 9, 12 |
| §7.5 Setup dialog difficulty | 12 |
| §8 Workbench Phases tab + validator additions | 2, 13 |
| §9 Logging (working lines tagged, phase/difficulty/skipped/Calculate/batch summary, Calculations kind) | 3, 6, 8, 12 |
| §10 Testing (UI-free list; smokes in three modes with turn wrap) | 1–9, 14 |
| §11 Left to the builder (layouts, pending storage, virtual mode, wording) | 3 (pending in state), 9/12 (virtual mode), 10–13 |

## Spec ambiguities resolved in this plan

- **"Calculations" log kind:** a new kind `calc` for engine-computed lines (fully automatic, Calculate, batch summaries, skipped runs, trigger problems). A manual Apply stays `formula`.
- **Key dropdowns:** offered only when the Input is the whole row/column expression or a bare `lookup` argument, and never for an axis with range keys. A computed expression such as `Terrain + 1` has no single valid key set.
- **Checking Inputs:** Inputs, including those with an AutoValue, are never checked. An AutoValue is a default, not a correct answer.
- **Unit-menu formulas in fully automatic:** a formula opened from a unit's menu in Fully automatic shows the piece-2 prefilled dialog; Hardcore and Semi-automatic open it checked. The spec's "not opened" refers to sequencer triggers.
- **Stage label:** the spec uses both "Stage 2 of 3" and "Stage 2/3"; the plan uses `"{Label or Stage} 2 of 3"` everywhere.
- **Old saves:** they load as Semi-automatic, so nothing applies itself unexpectedly in an existing game.
- **Phase-boundary autosave:** it honours `AutosaveMinutes = 0` as "off".
- **Moved without a measurable distance:** a move across maps or boards counts as 0 with a log line, like a move with no grid.
- **Missing inputs in fully automatic:** the small dialog asks once per run and applies the values to every unit. **Per unit…** opens the batch grid.
- **Pack storage:** sides, phases and blocks are stored in `pack.json` in authored order, not sorted.
- **Relocation:** a mid-game relocation is a logged `AdvancePhase` with a `Reason`, so it replays.
