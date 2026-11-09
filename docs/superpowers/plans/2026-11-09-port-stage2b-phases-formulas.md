# Cross-platform Port — Stage 2b (Phases, Step Back, formulas, alerts, trees, floating panels) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring the second half of stage 2 from `docs/port/parity.md` to the Avalonia app (`src/Quartermaster.Desktop`): the guided turn sequence (phase strip, phase panel, Next Phase, difficulty-mode orchestration), Step Back, formula dialogs with cheat sheet, batch grid and missing-input dialog, the alert bar with the "what now" line, the module and tables trees, and popping the log and phase panels out into floating windows — each behaving like the WinForms app.

**Architecture:** Same pattern as 2a. UI-free logic still inside WinForms files is first extracted into `src/Quartermaster.Render` (Tasks 1–3), and the WinForms app is switched to call it with identical behaviour. Desktop tasks (4–10) then build Avalonia views over those models and the models Render already has (`PhaseController`, `HistoryController`, `IActionPort`, `FormulaDialogModel`, `BatchModel`, `CheatSheetModel`, `MissingInputs`, `AlertEngine`, `Banner`, `ChipLayout`, `WindowLayoutStore`, `ScreenFit`, `Autosaver`), tested headless.

**Tech Stack:** C# .NET 10, Avalonia 12.1.5 (forced on SkiaSharp 4.154.0), Avalonia.Headless.XUnit (xunit v3) for Desktop, xUnit 2.9.3 for Render, GitHub Actions cross-platform CI.

**Spec:** `docs/superpowers/specs/2026-11-08-cross-platform-design.md`; feature list and stage assignment: `docs/port/parity.md` (2b rows: lines ~82, 85, 88, 114-122, 169, 173; 2b notes at 36-40, 87, 98, 145, 171).

## Global Constraints

- Everything in the Global Constraints of `docs/superpowers/plans/2026-11-08-port-stage2a-rules-map-tools.md` applies unchanged (WinForms unchanged except calls into moved code + off-screen smoke exit 0; Desktop references no WinForms/`System.Windows.*`/WebView2/WinRT/P/Invoke/App/Ocr; labels and punctuation copied from WinForms; `ShortcutGestures`/`CommandRouter`; Mac Ctrl+click and Cmd conventions; hidden information only through `ModuleSession.View`/`CanSee`; never visible windows, never leftover processes; MacFlag collection; no real-game content; noreply commits with both trailers; never push; each Desktop task updates its parity rows).
- Additional WinForms smoke for Tasks 1–3: `QUARTERMASTER_SHOT_OFFSCREEN=1 <App exe> --screenshot-alerts` and `--screenshot-breakdown` exit 0 (existing flags).
- Out of scope (leave as parity notes, do not build): changing difficulty mid-game (Settings, stage 4); Rule ref / rule links opening a PDF (stage 3: the button/link is shown disabled with tooltip "Rulebook viewer comes in a later version"); alert matches that are holders opening the Ledger (stage 3: such a match shows the status "Open the Ledger to see {name}" and selects nothing); tables-tree activation opening the Workbench (stage 4: ignored); popping out map tabs, Window > Cascade/Tile/Pop Out Active Window/Floating windows (stay "-"); periodic autosave (interval comes with Settings, stage 4).
- Debounced refresh (PhaseWatcher equivalent) on Desktop uses `DispatcherTimer` on the UI thread; phases/panel 150 ms, alerts 250 ms, same as WinForms. Controller events raised off the UI thread (`PhaseController.IsOwnerThread == false`) are marshalled with `Dispatcher.UIThread.Post`.
- Floating panels are ordinary owned Avalonia `Window`s; their bounds persist through `WindowLayoutStore` under the same keys as WinForms ("log", "phases") and are clamped with `ScreenFit.Restore` using `Screens.All`.

## Review Focus

1. Step Back while a formula dialog, batch grid or missing-input dialog for a run is open: every such window closes (WinForms `CloseFormulaWindows`) and the status says "Stepped back — reopen from the phase panel"; nothing applies to the rewound state → `Step_back_closes_formula_windows` (Task 6).
2. Hot-seat / viewer limited to one side: phase panel hides the checklist in another side's phase, the formula dialog refuses another side's unit ("That unit {OtherSideText}."), the alert bar evaluates only for the acting side, and the cheat sheet hides private inputs → `Checklist_hidden_in_other_sides_phase` (Task 4), `Formula_for_other_side_unit_refused` (Task 7), `Alerts_for_acting_side_only` (Task 9).
3. Saved floating-panel bounds off every current screen (monitor unplugged, Mac moved from external display): panel reopens on the primary screen's working area, not off-screen → `Saved_float_bounds_off_screen_are_clamped` (Task 10).
4. Next Phase pressed rapidly / Ctrl+Right held: one phase advance per completed press, no second NextPhaseDialog on top of the first, no exception → `Next_phase_reentry_is_ignored` (Task 5).
5. Pending run whose unit was deleted or already applied while its dialog is open: dialog closes like WinForms `CheckPieceThenRefresh`, batch grid row shows "Applied" and Apply all skips it → `Dialog_closes_when_run_no_longer_pending`, `Batch_row_applied_elsewhere_is_skipped` (Task 7/8).

---

### Task 1: Extract phase and step-back presentation + run dispatch to Render

**Files:**
- Create: `src/Quartermaster.Render/PhaseTexts.cs`, `src/Quartermaster.Render/PhaseRunDispatch.cs`
- Modify: `src/Quartermaster.App/PhaseStrip.cs` (Build 70-87), `PhasePanel.cs` (Reload 125-164, ShowRef 166, warning nodes 190, Ack rule 223), `NextPhaseDialog.cs`, `StepBackDialog.cs` (`Where()` 100, row cells), `MainForm.Phases.cs` (`NextTurnCommand` 160, `NextPhase` 169, `JumpToDecision` 205, `OnRunsQueued` 223, `OpenPendingRun` 241-310, `OnDifficultyChanged` 289), `MainForm.History.cs` (`DoStepBackToStart` 138, `DoStepBackTo` 146, `StepBackTo` 157)
- Test: `tests/Quartermaster.Render.Tests/PhaseTextsTests.cs`, `PhaseRunDispatchTests.cs`

**Interfaces:**
- Consumes: `PhaseView`, `PhaseChip`, `Decision`, `WarningHit`, `CheckpointRow`, `HistoryController`, `PhaseController`, `IActionPort`, `PendingRun`, `MissingInputs`, `Difficulty`, `Autosaver`.
- Produces:
  - `static class PhaseTexts` with `IReadOnlyList<StripChip> Strip(PhaseView v)` (`record StripChip(string Text, ChipState State, string? Tooltip)`, includes "Turn n", stage, "Before turn 1", "{Side} player — {phase}", playing text); `PanelText Panel(PhaseView v, string? viewerSideId)` (`record PanelText(string Title, string NextText, bool NextEnabled, string CountText, bool CountRed, string? RuleText, bool ChecklistHidden, IReadOnlyList<WarningLine> Warnings)`; `record WarningLine(string Text, int Index, bool CanAck)`); `string NextPhaseHead(int open)`; `string StepBackWhere(CheckpointRow r)`; `IReadOnlyList<string> StepBackCells(CheckpointRow r)` (Turn, Stage, Side, Phase, Reached, Actions); `string StepBackConfirm(CheckpointRow r)`; constants `NoPhasesText`, `StartGameText` ("Start game ▶"). All strings exactly as listed in WinForms today.
  - `interface IPhaseUi { Task<bool> ConfirmOpenDecisions(IReadOnlyList<Decision> open) /* true = Continue anyway */; Task<CheckpointRow?> PickCheckpoint(IReadOnlyList<CheckpointRow> rows, bool confirmOnly); void OpenUnitDialog(string formulaId, string pieceId, string runId, bool moved); void OpenBatch(string runId); Task<MissingInputResult> AskMissing(PendingRun run); void CloseFormulaWindows(); void Centre(string pieceId); void Status(string text); void RevealPhasePanel(); }`
  - `enum MissingInputResult { Ok, PerUnit, Cancel }` (Render, next to `IPhaseUi`).
  - `sealed class PhaseRunDispatch(ModuleSession s, PhaseController c, HistoryController h, Func<IActionPort> port, IPhaseUi ui, Autosaver? autosaver, Func<DateTimeOffset> now)` with `Task<bool> NextTurnCommand(Func<bool> stepTurnFallback)` (phased pack → NextPhase, else fallback), `Task NextPhase()` (re-entrant calls while one is running return immediately), `void JumpToDecision(Decision d)`, `void OnRunsQueued(IReadOnlyList<PendingRun> runs)` (status "{n} calculation(s) waiting in the phase panel"), `Task OpenPendingRun(string runId)` (difficulty dispatch exactly as WinForms 241-310, incl. the FullyAuto status "{Formula}: {n} unit(s) calculated automatically[, k could not be]" and "That calculation is no longer waiting."), `void OnDifficultyChanged()` ("Difficulty changed — reopen from the phase panel"), `void OnPhaseBoundary()` (`autosaver?.SaveForced(now())`), `Task StepBackToStart()`, `Task StepBackTo()` (SaveForced first, then `port().StepBack(row)`, then `ui.CloseFormulaWindows()` and status "Stepped back — reopen from the phase panel").
- [ ] **Step 1:** Failing tests on `PhaseFixture.Module`/`Pack` with a fake `IPhaseUi` and a recording port: `Strip_texts_before_start_and_in_turn`; `Panel_next_text_includes_next_phase_name`; `Panel_count_text_singular_and_plural`; `Checklist_hidden_in_other_sides_phase_text`; `Next_phase_head_singular_and_plural`; `Step_back_cells_and_confirm_text`; `Next_turn_without_phases_uses_fallback`; `Next_phase_with_open_decisions_asks_and_go_back_keeps_phase`; `Next_phase_reentry_returns_immediately`; `Fully_auto_applies_without_dialog`; `Fully_auto_missing_input_asks_then_per_unit_opens_units`; `Semi_auto_one_unit_opens_unit_dialog_many_opens_batch`; `Step_back_to_start_saves_forced_then_rewinds_and_closes_windows`; `Step_back_to_cancelled_does_nothing`; `Phase_boundary_autosaves`.
- [ ] **Step 2:** Run `dotnet test tests/Quartermaster.Render.Tests --filter "PhaseTexts|PhaseRunDispatch"` → FAIL (types missing).
- [ ] **Step 3:** Implement by moving the logic; WinForms implements `IPhaseUi` in `MainForm.Phases.cs` (its dialogs become adapters) and its strip/panel/dialogs render from `PhaseTexts`.
- [ ] **Step 4:** Full `dotnet test Quartermaster.slnx` PASS; off-screen smokes (Global Constraints) exit 0.
- [ ] **Step 5: Commit** `refactor(render): share phase texts, run dispatch and step back flow`.

### Task 2: Extract formula-window, batch, missing-input and alert-bar rules to Render

**Files:**
- Create: `src/Quartermaster.Render/FormulaWindowRules.cs`, `src/Quartermaster.Render/AlertBarModel.cs`
- Modify: `src/Quartermaster.App/FormulaDialog.cs` (registry/de-dupe 42-66, `CanAct`, `SyncMode` 388 and 91-93, `CheckPieceThenRefresh` 177, Apply fallback 430-434, title), `BatchGridWindow.cs` (cell value rule "Applied", colouring classes, status texts), `MissingInputDialog.cs` (head texts, OK rule, failure text), `AlertBar.cs` (`Label()` 91, severity accents 84), `MainForm.Alerts.cs` (`RecomputeAlerts` 60, `GoToAlert` 82 cycling, `TipFor` 162, menus 102-148)
- Test: `tests/Quartermaster.Render.Tests/FormulaWindowRulesTests.cs`, `AlertBarModelTests.cs`

**Interfaces:**
- Produces:
  - `static class FormulaWindowRules`: `string Key(string formulaId, string pieceId, string? runId)`; `string Title(FormulaDef f, string pieceName)`; `string? Refusal(ModuleSession s, string pieceId)` (null or "That unit {OtherSideText}."); `(bool Checked, bool ShowCheatSheet, bool ShowCalculate) Mode(Difficulty d)`; `bool ShouldClose(ModuleSession s, PhaseController? c, string pieceId, string? runId)`; `BatchCell CellOf(BatchRowView row, string column)` with `record BatchCell(string Text, CellTone Tone)` and `enum CellTone { Normal, Wrong, Applied, Exception }`; batch status constants (all seven texts listed in WinForms); `string MissingHead(string formula, int units, int variables)`; `string MissingFailed(string formula, int units)`; `string MissingResult(int applied, int failed)`; `bool MissingOkEnabled(IReadOnlyDictionary<string,string?> answers, IReadOnlyList<MissingInput> inputs)`.
  - `sealed class AlertBarModel(ModuleSession s)`: `IReadOnlyList<AlertChip> Recompute(PhaseDef? current, string? actingSideId, ISet<string> hiddenThisTurn)` (`record AlertChip(string Id, string Label, AlertSeverity Severity, string Tooltip, int MatchCount)`); `string? NextMatch(string alertId)` (cycles through matches, wraps); `static string OverflowText(int n)` ("+N"); constants `GoToNextMatchText`, `HideUntilNextTurnText`, `NoticeTip`; `static (byte R, byte G, byte B) Accent(AlertSeverity)` (WinForms colours).
- [ ] **Step 1:** Failing tests: `Key_dedupes_same_formula_piece_run`; `Mode_per_difficulty`; `Refusal_for_other_side_unit`; `Should_close_when_run_no_longer_pending_or_piece_gone`; `Batch_cell_applied_and_wrong_tones`; `Batch_status_texts_match_winforms`; `Missing_head_texts_singular_plural`; `Missing_ok_needs_all_answers_in_range`; `Alert_chip_label_and_tooltip`; `Recompute_skips_hidden_alerts`; `Recompute_uses_acting_side`; `Next_match_cycles_and_wraps`.
- [ ] **Step 2–4:** FAIL → move logic; WinForms windows call it → full slnx tests + off-screen smokes PASS.
- [ ] **Step 5: Commit** `refactor(render): share formula window, batch, missing-input and alert bar rules`.

### Task 3: Extract tree models and the dock/float state machine to Render

**Files:**
- Create: `src/Quartermaster.Render/ModuleTreeModel.cs`, `src/Quartermaster.Render/TablesTreeModel.cs`, `src/Quartermaster.Render/PanelDockState.cs`
- Modify: `src/Quartermaster.App/ModuleTreePanel.cs`, `TablesTreePanel.cs`, `PhaseDock.cs`, `LogDock.cs`
- Test: `tests/Quartermaster.Render.Tests/TreeModelTests.cs`, `PanelDockStateTests.cs`

**Interfaces:**
- Produces:
  - `record TreeItem(string Text, string? Tooltip, object? Tag, IReadOnlyList<TreeItem> Children)`; `static class ModuleTreeModel { IReadOnlyList<TreeItem> Build(ModuleModel m, Func<string,bool>? canSeeMap); string VisibilitySignature(ModuleModel m, Func<string,bool>? canSeeMap); }` (Tag = `WindowTreeNode`; tooltip = node kind as WinForms); `static class TablesTreeModel { IReadOnlyList<TreeItem> Build(RulesPack? rules); }` (roots "Tables" and "Formulas", sorting as WinForms, tooltip "{Kind}, {rows}×{cols}", "(no rules pack)" when null; Tag = table id / formula id wrapped in `record TableRef(string Id)` / `record FormulaRef(string Id)`).
  - `sealed class PanelDockState(string key, Func<RectangleI> defaultFloat)` with `bool Wanted`, `bool Floating`, `RectangleI? FloatBounds`, `void Toggle()`, `void Reveal()`, `void PopOut()`, `void DockBack()`, `void Load(SavedWindow? saved)`, `SavedWindow? Save()`, event `Changed`. `record struct RectangleI(int X, int Y, int W, int H)`. Default rects as WinForms: phases = (mainRight-340, mainTop+120, 320, 520); log = (mainLeft+40, mainBottom-320, max(480, mainW-80), 260) — provide `static RectangleI PhasesDefault(RectangleI main)` and `LogDefault(RectangleI main)`.
- [ ] **Step 1:** Failing tests: `Module_tree_matches_module_window_tree`; `Hidden_map_left_out_for_viewer`; `Signature_changes_when_visibility_changes`; `Tables_tree_roots_sorted_with_tooltips`; `Tables_tree_without_rules`; `Dock_state_toggle_popout_dockback`; `Reveal_shows_hidden_panel_in_last_place`; `Dock_state_roundtrips_saved_window`; `Default_float_rects_match_winforms`.
- [ ] **Step 2–4:** FAIL → move logic; WinForms panels/docks use it → full slnx tests + off-screen smokes PASS.
- [ ] **Step 5: Commit** `refactor(render): share tree models and panel dock state`.

### Task 4: Desktop phase strip, phase panel and debounced refresh

**Files:**
- Create: `src/Quartermaster.Desktop/Phases/PhaseStrip.cs`, `Phases/PhasePanel.cs`, `Shell/Debouncer.cs`
- Modify: `src/Quartermaster.Desktop/MainWindow.axaml` (phase strip row under the toolbar; right column in `Body` for the docked panel, 320 wide, with splitter), `MainWindow.axaml.cs` (`CreatePhases` ~696 subscribes controller events; View menu "&Phase panel" toggles)
- Test: `tests/Quartermaster.Desktop.Tests/PhasePanelTests.cs`

**Interfaces:**
- Consumes: `PhaseTexts`, `PanelDockState` (docked part only here), `PhaseController.View()/Start()/Tick/Acknowledge`, `IActionPort`.
- Produces: `sealed class Debouncer(TimeSpan delay, Action run)` with `Request()`, `Flush()`, `Dispose()`; `PhasePanel` with `Reload()`, `SelectChecklist(int)`, events `NextRequested`, `DecisionActivated(Decision)`, `PopToggleRequested`; `MainWindow.Phases` (the panel) and `MainWindow.Strip` for tests.
- Desktop tests may use `PhaseFixture.Module/Pack` (add a `PhaseGame()` helper in the test class: open fixture module, load pack, start setup, `Pump()`).
- [ ] **Step 1:** Failing tests: `No_phases_strip_text`; `Start_game_button_starts_turn_1`; `Strip_chips_follow_phase`; `Panel_lists_decisions_and_warnings`; `Acknowledge_warning`; `Checklist_tick_records`; `Checklist_hidden_in_other_sides_phase`; `View_phase_panel_toggles`; `Panel_refresh_is_debounced` (several state changes → one reload after 150 ms).
- [ ] **Step 2–4:** FAIL → implement → Desktop suite + slnx build PASS.
- [ ] **Step 5:** parity rows (PhaseStrip/Panel, View > Phase panel, PhaseWatcher); **Commit** `feat(desktop): phase strip and phase panel`.

### Task 5: Next Phase, Ctrl+Right, phase-boundary autosave

**Files:** Create `src/Quartermaster.Desktop/Shell/NextPhaseDialog.axaml(.cs)`, `Phases/DesktopPhaseUi.cs`; Modify `MainWindow.axaml.cs` (`NextTurnCommand` ~285 → `PhaseRunDispatch.NextTurnCommand`; create `Autosaver` with root `Autosaver.AutosaveRoot(<app data root used by Desktop>)`), `Shell/Dialogs.cs` (+ `Task<bool> ConfirmOpenDecisionsAsync(Window owner, IReadOnlyList<Decision> open)`), test `FakeDialogs`; Test `tests/Quartermaster.Desktop.Tests/NextPhaseTests.cs`

**Interfaces:** Consumes `PhaseRunDispatch`, `IPhaseUi` (Desktop adapter `DesktopPhaseUi` — dialog methods call `IDialogs`; `OpenUnitDialog`/`OpenBatch`/`AskMissing`/`CloseFormulaWindows` are no-op stubs until Tasks 7–8 fill them), `PhaseTexts.NextPhaseHead`. Dialog: title "Next Phase", list of open decisions, buttons "Continue anyway" / "Go back" (Enter = Go back); double-click on a decision jumps to it.
- [ ] **Step 1:** Failing tests: `Ctrl_right_runs_next_phase_with_phased_pack`; `Ctrl_right_steps_turn_without_phases`; `Open_decisions_ask_and_go_back_stays`; `Continue_anyway_advances`; `Next_phase_reentry_is_ignored` (two Execute calls while the dialog is pending → one dialog, one advance); `Phase_boundary_writes_autosave` (temp root); `Runs_queued_status_text`.
- [ ] **Step 2–4:** FAIL → implement → PASS. **Step 5:** parity rows (NextPhaseDialog; Tools > Turn note about Ctrl+Right; 2a deviation line 37 removed); **Commit** `feat(desktop): next phase and phase-boundary autosave`.

### Task 6: Step Back

**Files:** Create `src/Quartermaster.Desktop/Shell/StepBackDialog.axaml(.cs)`; Modify `Input/HistoryCommands.cs` (+ `phase.stepBack` Ctrl+Left, `phase.stepBackTo`), `MainWindow.axaml.cs` (Edit menu "Step &Back to Start of Phase", "Step Back &To..." after Redo as WinForms; `RefreshHistoryUi` ~472 gates them with WinForms "why" tooltips), `Shell/MainToolbar.cs` ("◀ Step Back", tooltip "Step back to start of phase (Ctrl+Left)"), `Shell/Dialogs.cs` (+ `Task<CheckpointRow?> PickCheckpointAsync(Window owner, IReadOnlyList<CheckpointRow> rows, bool confirmOnly)`), `FakeDialogs`; Test `tests/Quartermaster.Desktop.Tests/StepBackTests.cs`

**Interfaces:** Consumes `PhaseRunDispatch.StepBackToStart/StepBackTo`, `PhaseTexts.StepBackCells/StepBackConfirm`, `HistoryController.Preview/Describe` (summary text in the dialog). Dialog titles "Step Back to Start of Phase" / "Step Back To", columns Turn, Stage, Side, Phase, Reached, Actions, buttons "Step Back" / "Cancel".
- [ ] **Step 1:** Failing tests: `Ctrl_left_steps_back_to_start_of_phase_after_confirm`; `Step_back_to_lists_checkpoints_and_rewinds`; `Cancel_keeps_state`; `Step_back_disabled_before_start_with_reason`; `Toolbar_step_back_button`; `Redo_after_step_back`; `Step_back_closes_formula_windows` (asserted via the `IPhaseUi.CloseFormulaWindows` call count here; Task 7 adds the real-window assertion).
- [ ] **Step 2–4:** FAIL → implement → PASS. **Step 5:** parity rows (Edit > Step Back, StepBackDialog; deviation line 38 removed); **Commit** `feat(desktop): step back`.

### Task 7: Formula dialog, cheat sheet, Calculate submenu

**Files:** Create `src/Quartermaster.Desktop/Formulas/FormulaWindow.cs`, `Formulas/FormulaFormView.cs`, `Formulas/CheatSheetView.cs`, `Formulas/FormulaWindows.cs` (registry); Modify `Map/MapContextMenu.cs` (`Hooks()` ~54: `ShowFormula` set via `MapMenuServices`), `Phases/DesktopPhaseUi.cs` (`OpenUnitDialog`, `CloseFormulaWindows`); Test `tests/Quartermaster.Desktop.Tests/FormulaWindowTests.cs`

**Interfaces:**
- Consumes: `FormulaDialogModel`, `DialogView`/`BoxView`/`InlineRollView`, `FormulaLayout.Build` → `FormulaLayoutModel` (`TextItem`/`BoxItem`/`MirrorItem`), `FormulaMasking.Configure/SendAsync`, `CheatSheetModel.Build`, `FormulaWindowRules` (Task 2), `IActionPort.ApplyTrace`.
- Produces: `static class FormulaWindows { FormulaWindow? Show(MainWindow host, ModuleSession s, string formulaId, string pieceId, RunLink? link); void CloseAllFor(ModuleSession s); int OpenCount { get; } }` with `record RunLink(string RunId, bool Moved)` (reuse WinForms record if moved to Render in Task 1; otherwise define here in Desktop). Non-modal owned window, title from `FormulaWindowRules.Title`. Groups "Formula", "Working"; buttons "&Apply to ledger", "&Calculate" (checked modes only), "Cancel", "&Rule ref…" (disabled, stage 3 tooltip); "Physical dice" checkbox; inline reroll buttons "↻ {text}" with tooltip "Roll again"; Semi-auto shows the cheat sheet beside it ("Cheat sheet"; Variable / Source / Value; used table cell gold).
- [ ] **Step 1:** Failing tests: `Calculate_menu_opens_formula_window`; `Same_formula_piece_run_focuses_existing`; `Typing_box_updates_result`; `Apply_writes_ledger_and_closes`; `Hardcore_shows_calculate_and_checks`; `Semi_auto_shows_cheat_sheet`; `Physical_dice_box`; `Reroll_inline`; `Formula_for_other_side_unit_refused`; `Dialog_closes_when_run_no_longer_pending`; `Step_back_closes_formula_windows` (real window closed, `OpenCount == 0`); `Guest_masked_inputs_asks_host` (FormulaMasking with a fake port: "Asking the host…").
- [ ] **Step 2–4:** FAIL → implement → PASS. **Step 5:** parity rows (FormulaDialog/FormulaFormView/CheatSheetWindow; counter menu Calculate note; deviation line 40 Calculate part removed); **Commit** `feat(desktop): formula dialog and cheat sheet`.

### Task 8: Batch grid and missing-input dialog

**Files:** Create `src/Quartermaster.Desktop/Formulas/BatchGridWindow.cs`, `Shell/MissingInputDialog.axaml(.cs)`; Modify `Phases/DesktopPhaseUi.cs` (`OpenBatch`, `AskMissing`), `Shell/Dialogs.cs` (+ `Task<MissingInputResult> AskMissingAsync(Window owner, ModuleSession s, IActionPort port, PendingRun run)`), `FakeDialogs`, `Formulas/FormulaWindows.cs` (batch windows registered too, key "batch:"+runId); Test `tests/Quartermaster.Desktop.Tests/BatchGridTests.cs`

**Interfaces:** Consumes `BatchModel` (`Variables`, `ChoicesFor`, `RowCount`, `Title`, `Row`, `SetCell`, `CalculateAll`, `ApplyAll`, `ApplyRow`, `SendRowsAsync`, `HostCalculates`), `FormulaWindowRules.CellOf` + batch status texts, `CheatSheetView` (docked right, 300 wide), `MissingInputs.For`, `FormulaWindowRules.Missing*`, `LedgerBreakdown.For` (ledger column tip). Grid: columns Unit, {vars}, Result, Ledger, Exceptions; choice variables are combo cells; buttons "Calculate all", "Apply all", "Apply row"; double-click row opens the unit dialog (`openRow`). Missing-input: title = formula name, "OK" / "Per unit…" / "Cancel".
- [ ] **Step 1:** Failing tests: `Semi_auto_run_with_many_units_opens_batch`; `Edit_cell_and_calculate_all`; `Apply_all_skips_red_rows_with_status`; `Apply_row_without_selection_status`; `Batch_row_applied_elsewhere_is_skipped`; `Double_click_row_opens_unit_dialog`; `Fully_auto_missing_input_ok_applies_all`; `Per_unit_opens_units`; `Missing_ok_disabled_until_answered`; `Step_back_closes_batch_grid`.
- [ ] **Step 2–4:** FAIL → implement → PASS. **Step 5:** parity rows (BatchGridWindow, MissingInputDialog); **Commit** `feat(desktop): batch grid and missing-input dialog`.

### Task 9: Alert bar and "what now" line

**Files:** Create `src/Quartermaster.Desktop/Shell/AlertBar.cs`; Modify `MainWindow.axaml(.cs)` (bar row under the phase strip; visible when any chip or notice, as WinForms without the Settings toggle — Settings is stage 4, default visible); Test `tests/Quartermaster.Desktop.Tests/AlertBarTests.cs`

**Interfaces:** Consumes `AlertBarModel` (Task 2), `Banner.For(PhaseView)`, `ChipLayout.Place`, `IActionPort.HideAlert`, `Debouncer` (250 ms), `PanelDockState.Reveal` (banner click reveals the phase panel), map selection/centre (`CentreOnPiece` ~774). Chip click goes to next match (selects and centres the piece; holder match → stage-3 text from Global Constraints); chip context menu "Go to next match" / "Hide this alert until next turn"; overflow "+N" menu lists hidden-by-width chips; notice chip with tooltip "Click to open the turn and replay it." (the click itself is stage 5 — no-op). Public event `AlertsUpdated` for later outliner.
- [ ] **Step 1:** Failing tests: `Banner_texts_before_start_and_in_phase`; `Banner_click_reveals_phase_panel`; `Negative_value_alert_chip_appears`; `Chip_click_cycles_matches_and_centres`; `Hide_until_next_turn_removes_chip_then_returns`; `Overflow_chip_when_narrow`; `Alerts_for_acting_side_only`; `Recompute_is_debounced`.
- [ ] **Step 2–4:** FAIL → implement → PASS. **Step 5:** parity rows (AlertBar); **Commit** `feat(desktop): alert bar and what-now line`.

### Task 10: Module tree, tables tree, floating log and phase panels

**Files:** Create `src/Quartermaster.Desktop/Shell/ModuleTree.cs`, `Shell/TablesTree.cs`, `Shell/FloatPanelWindow.cs`; Modify `MainWindow.axaml(.cs)` (left column in `Body`: tree over tables tree with a vertical split, fraction from `WindowLayoutStore.LoadLeftSplit/SaveLeftSplit`, default 0.6, clamped 0.2–0.8; View "Module &Tree" checkable, checked by default; log dock ~739-771 and phase dock use `PanelDockState`), `Shell/LogPanel.cs` and `Phases/PhasePanel.cs` ("Pop out" / "Dock" button); Test `tests/Quartermaster.Desktop.Tests/TreesAndFloatTests.cs`

**Interfaces:** Consumes `ModuleTreeModel`, `TablesTreeModel`, `PanelDockState` + `PhasesDefault/LogDefault`, `WindowLayoutStore.LoadModule/SaveModule` (keys "log", "phases"), `ScreenFit.Restore` + `ScreenInfo` built from Avalonia `Screens.All` (working area, scaling×96 as Dpi), `MapTabs.Activate`. Module-tree activation: map node → activate/open its map tab; setup node → setup chooser as WinForms `OnNodeActivated` 957; chart/PDF nodes → status "Opens in a later version" (stage 3). Tables tree activation ignored (stage 4). Tree rebuilds when `ModuleTreeModel.VisibilitySignature` changes (hot-seat handover). Floating window titles "Log" / "Phases"; closing a floating window docks it back hidden like WinForms; bounds saved per module on close of the game.
- [ ] **Step 1:** Failing tests: `Module_tree_lists_maps_and_activates_tab`; `Hidden_map_not_in_tree_for_viewer`; `View_module_tree_toggles_left_column`; `Left_split_fraction_persists`; `Tables_tree_lists_tables_and_formulas`; `Log_pops_out_and_docks_back`; `Phase_panel_pops_out_and_docks_back`; `Float_bounds_persist_per_module`; `Saved_float_bounds_off_screen_are_clamped` (saved rect at x=5000 with one 1920×1080 screen → inside its working area).
- [ ] **Step 2–4:** FAIL → implement → PASS. **Step 5:** parity rows (View > Module Tree, ModuleTreePanel, TablesTreePanel, View > Log floating, LogPanel pop-out, PhaseDock/FloatForm; deviation line 36 removed); **Commit** `feat(desktop): module and tables trees, floating log and phase panels`.

### Task 11: Screenshots, parity, Mac checklist 2b

**Files:** Modify `build/port-screenshots.ps1` (add Desktop headless shots via `--screenshot-state`: phase panel docked mid-turn, Next Phase dialog, Step Back To dialog, formula window Semi-auto with cheat sheet, batch grid, alert bar with chips, module tree, floating log — WinForms off-screen equivalents where flags exist); Create `docs/port/stage2b-mac-checklist.md` (style of `stage2a-mac-checklist.md`: Start game, Next Phase via button and ⌘→, open-decisions dialog, ⌘← Step Back and Step Back To, Calculate from the counter menu (right-click and Ctrl+click), Semi-auto cheat sheet, batch grid typing and combos, alert chip clicks, module tree, pop out log/phases and move them to a second display then reopen, include Gatekeeper notes); Modify `docs/port/parity.md` (consistency pass; 2b deviations listed like 2a's), `docs/superpowers/RESUME.md` CURRENT STATE.
- [ ] **Step 1:** Run `build/port-screenshots.ps1`; view all PNGs; note look differences in the report.
- [ ] **Step 2:** Full `dotnet test Quartermaster.slnx` green (run twice; report any flaky test by name); `dotnet build Quartermaster.CrossPlatform.slnf -c Release` 0 errors.
- [ ] **Step 3: Commit** `docs(port): stage 2b screenshots, parity and Mac checklist`.
