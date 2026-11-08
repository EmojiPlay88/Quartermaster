# Cross-platform Port — Stage 2a (Rules packs, map extras, dice/turn, log) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring the first half of stage 2 from `docs/port/parity.md` to the Avalonia app (`src/Quartermaster.Desktop`): rules packs (new/open/save/remember), the map's context menu, hover popup with breakdowns, stack expand, line of sight, hide counters, last-moved highlight, property sheet and input dialogs, Tools > Dice / Turn with the main toolbar, and the log panel with chat — each behaving like the WinForms app.

**Architecture:** Stage 2 is ~7,000 WinForms lines, so it is split: **2a** (this plan) and **2b** (phases orchestration, formula dialogs/batch grid/cheat sheet, alert bar, module/tables trees, docking/floating — separate plan). UI-free logic still living in WinForms files is first extracted into `src/Quartermaster.Render` (Tasks 1–2) so both UIs call one copy; the WinForms app's behaviour stays identical. Desktop tasks then build views over those models, copying WinForms behaviour, tested headless.

**Tech Stack:** C# .NET 10, Avalonia 12.1.5 (forced on SkiaSharp 4.154.0), Avalonia.Headless.XUnit (xunit v3) for Desktop, xUnit 2.9.3 for Render, GitHub Actions cross-platform CI.

**Spec:** `docs/superpowers/specs/2026-11-08-cross-platform-design.md`; feature list and stage assignment: `docs/port/parity.md`.

## Global Constraints

- WinForms app (`src/Quartermaster.App`) keeps building and behaving exactly as before; App edits only replace code moved to Render with calls to it. Verify with existing tests plus the off-screen smoke: `QUARTERMASTER_SHOT_OFFSCREEN=1 <App exe> docs/wiki/demo/Riverland-Demo.vmod --rules docs/wiki/demo/Riverland-Demo.qrules --smoke 3` → exit 0, no process left.
- `Quartermaster.Desktop` must not reference WinForms, `System.Windows.*`, WebView2, `Windows.*` WinRT APIs, P/Invoke, or `Quartermaster.App`/`Quartermaster.Ocr`. Target `net10.0`.
- Behaviour, labels and punctuation copied from the WinForms app (menu texts, dialog texts, status texts). Look: existing ClassicTheme. Shortcuts via `ShortcutGestures`/`CommandRouter` (Cmd on macOS); menus show shortcuts with `InputGesture` only.
- Mac input conventions from stage 1 stay: right-click = context menu, and on macOS Ctrl+click also opens it; Cmd+click adds to selection.
- Hidden information: anything shown (popup, breakdown, log, context menu) must go through `ModuleSession.View`/`CanSee` exactly as WinForms does; never show more than WinForms shows the same viewer.
- Never open visible windows: Desktop tests and screenshots are headless only; WinForms runs only off-screen. Never leave app processes running.
- Tests that set `ShortcutGestures.IsMac` go in the non-parallel "MacFlag" xUnit collection.
- No real-game (CNA) content; fixtures: `tests/Quartermaster.TestFixtures` or the Riverland demo (`docs/wiki/demo/`).
- Commits use the repo's noreply email; never commit `startapp.cmd`; end commit messages with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` and `Claude-Session: https://claude.ai/code/session_01ABcS6Hu7i7gpk8tsutH7g6`.
- Never push or touch GitHub.
- Each Desktop task ends by updating its rows in `docs/port/parity.md` (Windows column ticked when headless tests pass; macOS/Linux columns left for CI/user).

## Review Focus

1. Hidden information in hover popup, breakdown tips and log: a viewer limited to one side must not see the other side's private values or private log entries → tests `Popup_hides_private_values_of_other_side` (Task 5), `Log_filters_private_entries_for_viewer` (Task 8).
2. Unsaved rules pack on close / module change: asks "Save changes to the rules pack '<name>'?" exactly like WinForms (`MainForm.cs:819`) and Cancel keeps everything → `Close_with_dirty_rules_asks` (Task 3).
3. Remembered rules pack file missing or for a different module: opening the module must not crash; same message/behaviour as WinForms (`RulesMemory`, "Rules pack was made for a different module") → `Remembered_pack_missing_is_ignored`, `Pack_for_other_module_warns` (Task 3).
4. Line of sight on HiDPI and while zoomed/panned: the line's endpoints and measured distance must match the map points under the pointer → `Los_measures_hexes_at_scale_2` (Task 6).
5. Context menu on macOS via Ctrl+click and on a masked (other side's) counter: same items as WinForms (masked → "(no commands)") → `Mac_ctrl_click_opens_menu`, `Masked_counter_menu_has_no_commands` (Task 4).

---

### Task 1: Extract map logic to Render (LOS, piece menu, last moved)

**Files:**
- Move: `src/Quartermaster.App/LosTool.cs` → `src/Quartermaster.Render/LosTool.cs` (public, namespace `Quartermaster.Render`)
- Create: `src/Quartermaster.Render/PieceMenuModel.cs`, `src/Quartermaster.Render/LastMoved.cs`
- Modify: `src/Quartermaster.App/MapWindow.cs` (context menu ~271-403), `MapCanvas.cs`/`MapCanvas.Los.cs` (RefreshLastMoved/HighlightLastMoved), callers found by build
- Test: `tests/Quartermaster.Render.Tests/LosToolTests.cs`, `PieceMenuModelTests.cs`, `LastMovedTests.cs`

**Interfaces:**
- Produces: `LosTool` (existing public API: `Begin`, `Update`, `HasLine`, `Dragging`, measuring); `PieceMenuModel.Build(ModuleSession s, string mapId, IReadOnlyList<string> pieceIds, LocalActions? actions) -> IReadOnlyList<MenuEntry>` where `MenuEntry` is `record MenuEntry(string Label, string? CommandId, bool Enabled, string? DisabledReason, IReadOnlyList<MenuEntry> Children, Action? Run, bool IsSeparator)` (Label text exactly as WinForms shows, incl. "(no commands)", the "Calculate" submenu, "Show in &Ledger", property sheet entry); `LastMoved.For(GameState s, string mapId) -> IReadOnlySet<string>` (piece ids WinForms highlights). If `LocalActions` lives in App, move the UI-free part with it.

- [ ] **Step 1:** Failing tests: `Los_measures_distance_like_before` (fixture hex map, begin/update → same hex count as the current App implementation on recorded cases); `Menu_for_movable_piece_lists_actions_in_winforms_order`; `Menu_for_masked_piece_is_no_commands`; `Calculate_submenu_lists_formulas`; `Show_in_ledger_disabled_without_rules`; `Last_moved_matches_previous_highlight` (cases taken from the current App behaviour).
- [ ] **Step 2:** Run → FAIL. **Step 3:** Move/extract; App builds its `ContextMenuStrip` from `MenuEntry` and calls `LastMoved.For`. **Step 4:** Render tests + full solution tests PASS; off-screen smoke (Global Constraints) exit 0, plus `--smoke-show-in-ledger --smoke 3` exit 0.
- [ ] **Step 5: Commit** `refactor(render): share LOS, piece menu and last-moved logic`.

### Task 2: Extract rules-pack flow and command enablement to Render

**Files:** Create `src/Quartermaster.Render/RulesPackFlow.cs`, `src/Quartermaster.Render/CommandAvailability.cs`; Modify `src/Quartermaster.App/MainForm.cs` (~223-230, 801-846, `_needsRules`, `_guestBlocked`); Test `tests/Quartermaster.Render.Tests/RulesPackFlowTests.cs`, `CommandAvailabilityTests.cs`

**Interfaces:**
- Produces: `interface IRulesPackUi { bool? AskSaveChanges(string packName); string? PickOpen(); string? PickSave(string suggestedName); string? AskName(string title, string prompt, string def); void Warn(string text); }`; `RulesPackFlow(ModuleSession s, IRulesPackUi ui, string? rulesMemoryRoot)` with `bool ConfirmSaved()` (WinForms `ConfirmRulesSaved` semantics: Yes saves, No discards, Cancel → false), `void New()`, `void Open()`, `bool Save(bool saveAs)`, `void RememberCurrent()`; `CommandAvailability.For(ModuleSession? s) -> IReadOnlyDictionary<string commandId, (bool Enabled, string? Reason)>` covering the ids WinForms gates with `_needsRules`/`_guestBlocked` (enumerate them from MainForm).
- [ ] **Step 1:** Failing tests with a fake `IRulesPackUi`: `Dirty_pack_yes_saves`, `Dirty_pack_no_discards`, `Dirty_pack_cancel_aborts`, `New_asks_name_and_creates`, `Open_remembers_for_module`, `Save_as_writes_qrules`; `Rules_commands_disabled_without_pack`, `Guest_blocked_commands_disabled_as_guest`.
- [ ] **Step 2–4:** FAIL → implement → WinForms MainForm calls the flow (its MessageBox/dialog code becomes the `IRulesPackUi` adapter); all tests + off-screen smoke PASS.
- [ ] **Step 5: Commit** `refactor(render): share rules-pack flow and command availability`.

### Task 3: Desktop rules packs

**Files:** Modify `src/Quartermaster.Desktop/MainWindow.axaml(.cs)`, `Shell/Dialogs.cs`, `Program.cs`; Test `tests/Quartermaster.Desktop.Tests/RulesPackTests.cs`

**Interfaces:** Consumes `RulesPackFlow`, `IRulesPackUi`, `CommandAvailability`, `RulesMemory`, `ModuleSession.Rules/RulesPath/RulesDirty/RulesChanged`.

File menu gains (labels exact): `New &Rules Pack...`, `Open Rules &Pack...`, `Save Rules Pac&k`, `Save Rules Pack A&s...` in WinForms order. Title shows the rules name like WinForms. Opening a module loads its remembered pack. Command line `--rules <path>`. Close/module change/new game asks about a dirty pack. Menu enablement from `CommandAvailability`.
- [ ] **Step 1:** Failing headless tests: `Menu_has_rules_items_in_winforms_order`; `New_open_save_roundtrip` (fake dialogs); `Remembered_pack_loads_with_module`; `Remembered_pack_missing_is_ignored`; `Pack_for_other_module_warns` (exact WinForms text); `Close_with_dirty_rules_asks`; `Command_line_rules_loads`.
- [ ] **Step 2–4:** FAIL → implement → PASS (Desktop suite + slnx build).
- [ ] **Step 5:** Update parity rows; **Commit** `feat(desktop): rules packs`.

### Task 4: Map context menu, property sheet, input dialog

**Files:** Create `src/Quartermaster.Desktop/Map/MapContextMenu.cs`, `Shell/PropertySheetDialog.axaml(.cs)`, `Shell/InputDialog.axaml(.cs)`; Modify `Map/MapView.Input.cs`; Test `tests/Quartermaster.Desktop.Tests/ContextMenuTests.cs`

**Interfaces:** Consumes `PieceMenuModel`/`MenuEntry` (Task 1), `PieceActions.ReadSheet/WriteSheet` (Core). Right-click (and Ctrl+click on macOS) on a piece/stack opens the menu built from `MenuEntry`; on empty map the map menu (Zoom to Fit / Zoom 100% etc. as WinForms). Disabled items show their reason as tooltip. Input prompts (`PromptText`) use `InputDialog`.
- [ ] **Step 1:** Failing tests: `Right_click_opens_piece_menu_with_model_entries`; `Mac_ctrl_click_opens_menu` (MacFlag); `Masked_counter_menu_has_no_commands`; `Property_sheet_edits_write_back`; `Action_with_prompt_asks_input`; `Empty_map_menu_items`.
- [ ] **Step 2–4:** FAIL → implement → PASS. **Step 5:** parity rows; **Commit** `feat(desktop): map context menu and property sheet`.

### Task 5: Hover popup, breakdown tips, hover-stack highlight, stack expand

**Files:** Create `src/Quartermaster.Desktop/Map/CounterPopup.cs`, `Map/BreakdownTip.cs`; Modify `Map/MapView*.cs`; optionally extract the popup's line layout to `src/Quartermaster.Render/CounterPopupLayout.cs` if the WinForms `CounterDetailPopup` layout logic is UI-free enough (then WinForms uses it too); Test `tests/Quartermaster.Desktop.Tests/HoverTests.cs`

**Interfaces:** Consumes `PieceDetails.For`, `LedgerBreakdown.For`, `PopupPlacement`, `StackLayout.Offsets`, `HitTester`. Popup content, delay, placement and dismissal as WinForms (`CounterDetailPopup.cs`, `MapCanvas.cs` hover logic ~600-660); ledger lines show their breakdown on hover; hover highlights the stack; double-click a stack toggles expand.
- [ ] **Step 1:** Failing tests: `Hover_shows_popup_after_delay`; `Popup_lists_properties_and_ledger_lines`; `Popup_hides_private_values_of_other_side`; `Ledger_line_hover_shows_breakdown`; `Hover_highlights_stack`; `Double_click_expands_and_collapses_stack`; `Popup_not_under_cursor` (PopupPlacement).
- [ ] **Step 2–4:** FAIL → implement → PASS. **Step 5:** parity rows; **Commit** `feat(desktop): hover popup, breakdowns, stack expand`.

### Task 6: Map toolbar: line of sight, hide counters, last moved

**Files:** Create `src/Quartermaster.Desktop/Map/MapToolbar.cs`, `Map/MapView.Los.cs`; Test `tests/Quartermaster.Desktop.Tests/MapToolsTests.cs`

**Interfaces:** Consumes `LosTool`, `LastMoved` (Task 1), `CommandDefaults` ids `tool.los` (L), `view.hideCounters` (H). Map toolbar buttons and labels as WinForms `MapWindow.cs:133-146` (Line of sight, Last moved, Hide counters, Zoom dropdown — Zoom dropdown already exists from stage 1 in the View menu; add it to the toolbar). Esc cancels LOS. LOS line and measurement drawn like WinForms.
- [ ] **Step 1:** Failing tests: `L_starts_los_and_drag_measures`; `Los_measures_hexes_at_scale_2`; `Esc_cancels_los`; `H_hides_and_shows_counters`; `Last_moved_highlights_after_move`; `Toolbar_buttons_toggle_state`.
- [ ] **Step 2–4:** FAIL → implement → PASS. **Step 5:** parity rows; **Commit** `feat(desktop): LOS, hide counters, last moved`.

### Task 7: Tools > Dice / Turn and the main toolbar

**Files:** Create `src/Quartermaster.Desktop/Shell/DiceTools.cs`, `Shell/DiceDialog.axaml(.cs)`, `Shell/TurnDialog.axaml(.cs)`, `Shell/MainToolbar.cs`; extract any UI-free part of WinForms `DiceTools.cs` (roll recording, custom-die parsing beyond `DiceExpr`) to Render first if it is still in App (WinForms then calls it); Test `tests/Quartermaster.Desktop.Tests/DiceTurnTests.cs`

**Interfaces:** Consumes `DiceExpr`, `RollDice` command path, `ModuleSession.Roll`, `ToolbarIds`, `CommandDefaults` (F5/F6 dice shortcuts), turn commands used by WinForms `Tools > Turn` (Next, Previous, Set...). Tools menu labels exact; main toolbar shows the WinForms default buttons from `ToolbarIds` that exist in Desktop so far (others hidden until their stage).
- [ ] **Step 1:** Failing tests: `F5_rolls_1d6_and_logs`; `Custom_dice_expression_rolls`; `Invalid_dice_expression_shows_error`; `Module_dice_listed`; `Turn_next_previous_set`; `Toolbar_has_default_buttons_in_order`; `Rolls_use_session_roller` (deterministic roller injected).
- [ ] **Step 2–4:** FAIL → implement → PASS. **Step 5:** parity rows; **Commit** `feat(desktop): dice, turn tools and main toolbar`.

### Task 8: Log panel and chat

**Files:** Create `src/Quartermaster.Desktop/Shell/LogPanel.axaml(.cs)`, `Shell/ChatBox.cs`; Modify `MainWindow` layout (log docked at the bottom like WinForms default; float/dock comes in 2b); Test `tests/Quartermaster.Desktop.Tests/LogPanelTests.cs`

**Interfaces:** Consumes `LogFilter` (`AllKinds`, `KindLabel`, `Apply`, `Time`, `ToText`, `ToHtml`), `MoveLogText`, session log. View > Log toggles it (label exact). Kind toggle buttons, time format, export to text/HTML (file picker via `IDialogs`), chat box sends via the same session path as WinForms `ChatBox(Action<string> send)` (offline: local note as WinForms does).
- [ ] **Step 1:** Failing tests: `Log_shows_moves_and_rolls`; `Kind_toggle_filters`; `Log_filters_private_entries_for_viewer`; `Export_text_and_html`; `Chat_sends_line`; `Large_log_stays_responsive` (10,000 entries render without timeout — virtualized list).
- [ ] **Step 2–4:** FAIL → implement → PASS. **Step 5:** parity rows; **Commit** `feat(desktop): log panel and chat`.

### Task 9: Screenshots, parity, Mac checklist 2a

**Files:** Modify `build/port-screenshots.ps1` (add shots: context menu open, hover popup, LOS line, log panel — Desktop headless, WinForms off-screen equivalents where flags exist); Create `docs/port/stage2a-mac-checklist.md` (same style as stage 1: right-click and Ctrl+click menus, hover popup and breakdown tips with the pointer, LOS with the trackpad, H, F5/F6 dice (on Mac keyboards these may need fn+F5 — say so), rules pack new/open/save, log, chat; include the Gatekeeper notes from the stage 1 checklist); Modify `docs/port/parity.md` (consistency pass).
- [ ] **Step 1:** Run `build/port-screenshots.ps1`; view all PNGs; note look differences in the report.
- [ ] **Step 2:** Full `dotnet test Quartermaster.slnx` green; `dotnet build Quartermaster.CrossPlatform.slnf -c Release` 0 errors.
- [ ] **Step 3: Commit** `docs(port): stage 2a screenshots, parity and Mac checklist`.
