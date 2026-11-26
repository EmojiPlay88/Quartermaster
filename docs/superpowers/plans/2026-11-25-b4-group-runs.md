# Group and Repeated Runs (B4) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One formula over a selection or stack, or many runs on one counter, in one batch grid applied as one undo step; dice rolled once per group (batch, or hex and side for hook runs); Input choices drawn from a table filtered per counter.

**Architecture:** Rules gains `FormulaDef.GroupRoll`, `VariableBinding.ChoicesTable/ChoicesFilter`, `row()` and validator checks. Core gains shared rolls in `RunRequest` (logged once), roll groups on pending runs (saved through `QueueRuns`/`ResolveRun`), `InputChoices`, and the carried minors. Render shares group dice on every pending-run path, adds the in-memory `MenuBatchModel` (preview chain on a cloned state, all-or-nothing apply) and a per-batch online intent. Desktop opens it from the counter menu and the formula window. Workbench editors; docs.

**Tech Stack:** C# .NET 10, Avalonia (Desktop), xUnit.

**Spec:** `docs/superpowers/specs/2026-11-25-b4-group-runs-design.md`

## Global Constraints

Read `.superpowers/sdd/2026-11-25-b4-group-runs/global-constraints.md` (git-ignored; same folder as the ledger) before each task. It holds the invariants and **every exact validator, run-time, log, menu and Workbench string**; use them verbatim. In short:
- Public repo: invented data only (Riverland). Old packs keep bytes + SHA-256, `CurrentFormat` 4. Old saves keep bytes.
- Additive API only: new record members as body init properties; `IEvalContext.Row` and `IActionPort.ApplyFormulaBatchAsync` are default methods; new overloads, never changed signatures.
- Hidden information: rows only for counters the viewer may act on, from its own view; groups never span sides; `PlayerView` drops `GroupRolls`; the host decides group dice and choices; refusals never name a list.
- One undo step per menu batch; each row is its own run (`AppliedRun`); a group's dice logged once.
- `-c Release`, one test project at a time, headless only, never stash/push, repo-local identity, `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Old packs and saves keep bytes**: a hand-written pre-B4 `pack.json` + formula literal loads and saves byte-equal with a constant SHA-256 (Task 1, `B4_absent_fields_keep_bytes`); B2's test uses a pre-B2 literal (Task 1); `PendingUnit`/`PendingRun`/`ResolveRun` literals from master round-trip unchanged (Task 2, `Pending_save_without_groups_keeps_bytes`).
2. **Group dice logged once and shared**: two units in one hex of one side share a roll, one `RollDice` line; another hex or the other side rolls its own (Task 2 `SharedRolls_not_logged`, Task 3 `Hook_group_one_roll_per_hex_and_side`).
3. **No leak**: a guest's view has no `GroupRolls`; a guest's batch of hidden or other-side counters is refused with the neutral texts; a choices refusal never lists choices (Tasks 2, 3, 4).
4. **One undo, in order**: a menu batch of three rows where row 2 writes row 3's counter applies as one action; row 3 sees row 2's write; one Undo restores all (Task 4 `Menu_batch_chain_and_one_undo`).
5. **No re-roll**: a guest's preview, then Apply of the same batch, uses the same dice; reordering rows on different counters does not change a row's dice (Task 4 `Guest_batch_pins_survive_reorder`).

---

### Task 1: Rules — model, row(), validator, bytes tests — model: sonnet

**Files:**
- Modify: `src/Quartermaster.Rules/Model/FormulaDef.cs`: `FormulaDef.GroupRoll` (`WhenWritingDefault`); `VariableBinding.ChoicesTable`, `ChoicesFilter` (`WhenWritingNull`). Doc comments as the spec.
- Modify: `src/Quartermaster.Rules/Io/RulesPackIo.cs`: `Normalise` turns blank `ChoicesTable`/`ChoicesFilter` into null.
- Modify: `src/Quartermaster.Rules/Expr/Parser.cs` (`["row"] = (0, 0)`), `Evaluator.cs` (`"row"` in `FunctionNames`; `EvalCall` → `ctx.Row()`), `IEvalContext.Row() => throw new EvalError("row() can only be used in a choices filter")`.
- Modify: `src/Quartermaster.Rules/Validation/RulesValidator.cs`: the choices-table messages in the binding loop / `CheckBinding`; the choices filter parsed with `ParseProblem` + `FilterRefProblems` (`row()` allowed there only); the `row()` tail in `FilterRefProblems` elsewhere; `"row"` in `ForbiddenConditionFunctions`; the `GroupRoll` warning (no Roll binding and no `dice()` in `FormulaVariables.Sources` or the text).
- Test: `tests/Quartermaster.Rules.Tests/GroupRunsModelTests.cs`, `tests/Quartermaster.Rules.Tests/GroupRunsValidatorTests.cs`; modify `tests/Quartermaster.Rules.Tests/PhasePipelineFieldsTests.cs` (B2 bytes test).
- [ ] **Step 1: Failing tests**:
  - Model/IO: `B4_absent_fields_keep_bytes` (hand-written format-4 pack literal with an Input with `choices`, a Roll binding and outputs, no B4 fields; `s.Replace("\r\n", "\n")` on literals; byte-equal + constant SHA-256 computed once on the pre-B4 code); `New_fields_round_trip_camelCase` (`"groupRoll": true`, `"choicesTable"`, `"choicesFilter"`); `False_GroupRoll_omitted`; `Blank_choices_texts_become_null`;
  - B2: `B2_absent_fields_keep_bytes` rewritten to load a hand-written pre-B2 literal (phases with `onStart`/`onEnd` entries, properties without `defaultExpr`/`resetAt`), compare bytes and a constant SHA-256 (as B3's test);
  - Language: `Row_parses_no_arguments`; `Row_default_context_throws`;
  - Validator: `ChoicesTable_unknown`, `ChoicesTable_not_input`, `ChoicesTable_with_choices`, `ChoicesTable_range_keys`, `ChoicesFilter_without_table_warns`, `ChoicesFilter_parse_error`, `ChoicesFilter_unknown_property`, `Row_outside_choices_filter` (formula text, applies-to, a phase condition), `GroupRoll_without_dice_warns`, `GroupRoll_with_inline_dice_ok`, `Valid_b4_formula_has_no_issues`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~GroupRuns|FullyQualifiedName~PhasePipelineFields"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, then the full Rules.Tests → PASS. Build Core, Render, Desktop, App (`-c Release`).
- [ ] **Step 5: Commit** `feat: B4 group roll, choices table, row(), validator`

### Task 2: Core — shared rolls, roll groups, choices table, saves, carried minors — model: opus

**Files:**
- Modify: `src/Quartermaster.Core/FormulaRunner.cs`: `RunRequest.SharedRolls` (body init); `FormulaTrace.SharedRollKeys`; `RunScope.RollKey` takes a matching shared key first (recorded in `_byKey`, not in `_rolls`); `TypedRoll` unchanged; `Pick` memoised per variable; `Input` with `ChoicesTable` via `InputChoices` (messages per the constraints; `HiddenData` rows kept on a player view).
- Create: `src/Quartermaster.Core/RollGroups.cs` (`Assign`, `SharedFor`), `src/Quartermaster.Core/InputChoices.cs` (filter evaluated with `PieceFilter`'s context plus `row()`; `PieceFilter.FilterContext` gains `Row()` when given a row value).
- Modify: `src/Quartermaster.Core/Phases.cs` (`PendingUnit.Group`, `Removed`; `PendingRun.GroupRolls`), `src/Quartermaster.Core/Commands.cs` (`ResolveRun.GroupRolls`, `Gone`), `src/Quartermaster.Core/GameState.cs` (`DoResolve` merge first-wins and `Removed`; `BatchSummary` `, {r} removed`), `src/Quartermaster.Core/PlayerView.cs` (drop `GroupRolls`), `src/Quartermaster.Core/PieceChoices.cs` (`[ThreadStatic] internal static int ViewBuilds`).
- Test: `tests/Quartermaster.Core.Tests/GroupRollTests.cs`, `tests/Quartermaster.Core.Tests/ChoicesTableTests.cs`.
- [ ] **Step 1: Failing tests**:
  - Shared rolls: `SharedRolls_used_and_not_logged` (no `RollDice` for the key, working and `count()` read it); `SharedRolls_mismatched_count_rolls_own`; `SharedRolls_inline_dice_key`; `SharedRollKeys_listed`;
  - Groups: `Assign_same_hex_same_side_one_group`; `Assign_other_side_same_hex_other_group`; `Assign_off_grid_null`; `SharedFor_reads_run_group`;
  - Saves: `Pending_save_without_groups_keeps_bytes` (hand-written `queueRuns`/`resolveRun` literals from master's serializer, round-trip equal); `ResolveRun_group_rolls_first_wins`; `ResolveRun_gone_marks_removed_and_summary`; `Summary_unchanged_without_removed`; `PlayerView_drops_group_rolls`; `PlayerView_keeps_own_unit_group`;
  - Choices: `ChoicesTable_lists_filtered_keys`; `ChoicesTable_no_filter_all_keys`; `ChoicesTable_no_piece_all_keys`; `ChoicesTable_typed_not_listed_missing`; `ChoicesTable_none_left_missing`; `ChoicesFilter_error_missing`; `ChoicesFilter_hidden_row_kept_on_view`; `ChoicesTable_numeric_keys_give_numbers`;
  - Minor: `Pick_resolved_once_per_run` (`ViewBuilds` = 1 for a Piece binding plus a Piece output with `PickSides`).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~GroupRoll|FullyQualifiedName~ChoicesTable"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, then the full Core.Tests → PASS; build Render, Desktop, App.
- [ ] **Step 5: Commit** `feat: shared group rolls, roll groups on pending runs, choices table`

### Task 3: Render — group dice on pending-run paths, choices in dialogs, removed counts — model: opus

**Files:**
- Modify: `src/Quartermaster.Render/PhaseController.cs`: `Apply` assigns `PendingUnit.Group` (only `GroupRoll`); the Fully automatic loop shares dice per group (local dictionary) and counts removed units in its summary; `ApplyFormula` puts `{group: rolled keys}` on `ResolveRun`; `ResolveGoneUnits` sends `Gone = true`.
- Modify: `src/Quartermaster.Render/FormulaDialogModel.cs`: `SharedRolls` into every `RunRequest`; shared Roll box (caption suffix, tooltip, not editable, no reroll); dropdowns via `InputChoices.For`.
- Modify: `src/Quartermaster.Render/BatchModel.cs`: `SharedRolls` per row (state's group dice, else the group's first open row's pinned dice); `ChoicesFor(variable, row)` via `InputChoices`; `Title` with removed.
- Modify: `src/Quartermaster.Render/MissingInputs.cs` (`ApplyAll` re-reads `SharedFor` per unit), `src/Quartermaster.Render/Online/IntentExecutor.cs` (`FormulaIntentRunner.Run`: `SharedRolls` from the full state; group pin key `group:{runId}:{group}`), `src/Quartermaster.Desktop/Formulas/FormulaWindow.cs` and `src/Quartermaster.App/FormulaDialog.cs` (run link → `SharedFor`).
- Test: `tests/Quartermaster.Render.Tests/GroupRollRunTests.cs`, `tests/Quartermaster.Render.Tests/GroupRollOnlineTests.cs` (the existing online/guest harness; grep `FormulaIntentRunner` in Render.Tests), `tests/Quartermaster.Render.Tests/ChoicesTableDialogTests.cs`.
- [ ] **Step 1: Failing tests**:
  - Hook runs: `Hook_group_one_roll_per_hex_and_side` (SemiAuto, batch grid rows of one group preview the same dice; one `RollDice` line after both applied); `Hook_group_undo_first_row_rerolls_group`; `FullyAuto_group_shares_dice`; `Non_group_formula_units_have_no_group`; `MissingInputs_applyall_shares_group`; `Unit_dialog_with_run_link_uses_group_dice`;
  - Online: `Guest_group_rows_preview_same_dice`; `Guest_view_has_no_group_rolls`; `Guest_applied_group_row_then_second_uses_state_dice`;
  - Removed: `FullyAuto_summary_counts_removed`; `Batch_title_counts_removed`; `ResolveGoneUnits_marks_gone`;
  - Choices: `Dialog_dropdown_lists_piece_choices`; `Batch_choices_per_row_piece`; `Guest_choices_refusal_neutral` (host refusal text `Not one of the choices for this piece`, no list).
- [ ] **Step 2: Run** Render `--filter "FullyQualifiedName~GroupRoll|FullyQualifiedName~ChoicesTable"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, the full Render.Tests, the full Core.Tests; build Desktop and App → PASS.
- [ ] **Step 5: Commit** `feat: group dice on pending runs; choices per counter in dialogs`

### Task 4: Render — menu batch model and per-batch online intent — model: opus

**Files:**
- Create: `src/Quartermaster.Render/IBatchRows.cs` (interface; `BatchModel` implements it, no member changes), `src/Quartermaster.Render/MenuBatchModel.cs` (rows, `All rows`, `AddRow`/`RemoveRow` with the 100 limit, preview chain on `GameState.DeepClone`, group dice, `ApplyAll` all-or-nothing in one action, `ActionLabel`, `SendAsync` for a guest).
- Modify: `src/Quartermaster.Render/Online/Intents.cs` (`FormulaBatchIntent`, `FormulaBatchRow`, `FormulaBatchVerdict`; `[JsonDerivedType(typeof(FormulaBatchIntent), "formulaBatch")]`), `src/Quartermaster.Render/Online/IntentExecutor.cs` (dispatch; `FormulaIntentRunner.Prepare` extracted; new `FormulaBatchRunner` with the pin keys of the spec, chain on a clone, one action), `src/Quartermaster.Render/Actions/ActionPort.cs` (default method), `LocalActions.cs`, `RemoteActions.cs`, `ActionPortSwitch.cs`, `src/Quartermaster.Render/FormulaWindowRules.cs` (the new texts), `src/Quartermaster.Render/PieceMenuModel.cs` (`ShowFormulaBatch` hook, `Calculate on selection` / `Calculate on stack` submenus).
- Test: `tests/Quartermaster.Render.Tests/MenuBatchTests.cs`, `tests/Quartermaster.Render.Tests/MenuBatchOnlineTests.cs`, `tests/Quartermaster.Render.Tests/PieceMenuBatchTests.cs`.
- [ ] **Step 1: Failing tests**:
  - Model: `Menu_batch_chain_and_one_undo`; `All_rows_fills_blank_cells_only`; `Add_row_same_counter_own_dice`; `Add_row_limit_100`; `Group_roll_batch_one_roll_logged_once`; `No_group_roll_each_row_rolls`; `Row_removed_by_earlier_row_skipped` (Exceptions `removed by an earlier row`); `Placed_piece_never_a_row`; `Runs_counts_each_row` (row 3's `runs()` sees rows 1-2; a `runs() < 2` precondition blocks the batch with `Nothing applied: row 3 cannot be applied yet.`); `Apply_all_or_nothing`; `Action_label_units_vs_runs`; `Hot_seat_rows_only_actable_and_remasked`; `BatchModel_implements_IBatchRows_unchanged`;
  - Online: `Guest_batch_one_action_on_host`; `Guest_batch_preview_then_apply_same_dice`; `Guest_batch_pins_survive_reorder`; `Guest_batch_hidden_counter_refused` (`Row 2: That unit no longer exists.`); `Guest_batch_other_side_refused`; `Guest_batch_pending_unit_refused` (`UsePendingText`); `Guest_batch_over_100_rows_not_understood`; `Old_port_default_refuses`;
  - Menu: `Selection_submenu_lists_formulas_for_two_plus`; `Stack_submenu_when_not_in_selection`; `Submenu_skips_other_side_counters`; `Submenu_absent_without_hook`; `Phase_limited_greyed_with_reason`.
- [ ] **Step 2: Run** Render `--filter "FullyQualifiedName~MenuBatch|FullyQualifiedName~PieceMenuBatch"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, the full Render.Tests; build Desktop and App → PASS.
- [ ] **Step 5: Commit** `feat: menu batch over a selection, stack or repeated runs; one intent per batch`

### Task 5: Desktop — grid for menu batches, menu wiring, Several runs… — model: sonnet

**Files:**
- Modify: `src/Quartermaster.Desktop/Formulas/BatchGridWindow.cs`: takes `IBatchRows` (pending-run constructor unchanged); for a `MenuBatchModel`: the `All rows` line, `Add row` / `Remove row` buttons with tooltips, no `Apply row`, the menu-batch status texts, guest `Apply all`/`Calculate all` through `ApplyFormulaBatchAsync`.
- Modify: `src/Quartermaster.Desktop/Formulas/FormulaWindows.cs` (`ShowMenuBatch(host, s, port, formulaId, pieceIds, firstRowTyped)`; registry key `menu:{guid}`; `CloseAllFor` closes them), `src/Quartermaster.Desktop/Formulas/FormulaWindow.cs` (`Several runs…` for a menu run), `src/Quartermaster.Desktop/MainWindow.axaml.cs` and `src/Quartermaster.Desktop/Map/MapContextMenu.cs` (`ShowFormulaBatch` hook).
- Test: `tests/Quartermaster.Desktop.Tests/MenuBatchGridTests.cs` (headless).
- [ ] **Step 1: Failing tests**: `Menu_grid_has_all_rows_line`; `Menu_grid_add_and_remove_row`; `Menu_grid_no_apply_row_button`; `Menu_grid_apply_all_status`; `Menu_grid_nothing_applied_status`; `Several_runs_opens_grid_with_typed_row`; `Several_runs_hidden_for_run_link`; `Map_menu_calculate_on_selection_opens_grid`; `Pending_grid_unchanged` (existing columns and buttons).
- [ ] **Step 2: Run** Desktop `--filter "FullyQualifiedName~MenuBatchGrid"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, the full Desktop.Tests; build App → PASS.
- [ ] **Step 5: Commit** `feat: Desktop menu batch grid and entries`

### Task 6: Workbench, wiki, RESUME, full runs — model: sonnet

**Files:**
- Modify: `src/Quartermaster.Desktop/Workbench/FormulasTab.cs` (`Roll once for the group` check box with tooltip), `src/Quartermaster.Desktop/Workbench/FormulasTab.Grids.cs` (bindings columns `Choices table` (8, combo blank + table names), `Choices filter` (9) after `Same hex`, header tooltips).
- Modify: `src/Quartermaster.Render/FormulaEdit.cs`: `WithGroupRoll`, `WithChoicesTable` (null clears the filter), `WithChoicesFilter`; blank → null.
- Modify: `src/Quartermaster.App/Workbench/*`: build only.
- Modify: `docs/wiki/Making-a-Rules-Pack.md` (`## Group and repeated runs`: `### Rolling once for a group` with `Ford crossing`, `### Choices from a table` with `Barge cargo`/`Cargo`; the validator texts), `docs/wiki/Formula-Reference.md` (`### row()`, choices table, group roll, error rows), `docs/wiki/Window-Formula-Dialog.md` (`### Running on several counters`, `### Several runs on one counter`), `docs/wiki/Window-Rules-Workbench.md`, `docs/wiki/Menus-and-Windows.md`.
- Modify: `docs/superpowers/RESUME.md`: new CURRENT STATE for B4, B3's moved under Previous; downstream note (neutral wording "downstream pack": it can retire its scratch-property group rolls, one-dialog-per-counter formation entries and prefixed choice lists; nothing in it changes bytes; grep for `IActionPort` implementations, which now get a default member).
- Test: Desktop `FormulasTab_group_roll_checkbox_commits`, `FormulasTab_choices_columns_commit`, `FormulasTab_old_binding_columns_keep_places`, `FormulasTab_clear_choices_table_clears_filter`; Render `FormulaEdit_choices_and_group_roll`; Docs `Wiki_mentions_row_function` (extend the existing function-docs test if one pins `FunctionNames`).
- [ ] **Step 1: Failing tests.**
- [ ] **Step 2: Run** Desktop `--filter "FullyQualifiedName~FormulasTab"`, Render `--filter "FullyQualifiedName~FormulaEdit"`, Docs `--filter "FullyQualifiedName~Wiki"` → FAIL.
- [ ] **Step 3: Implement**; wiki; RESUME.
- [ ] **Step 4: Full runs**, one at a time: Rules, Vassal, Core, Render, Desktop, Docs → PASS (flaky: re-run once, report both). Build `src/Quartermaster.App -c Release`.
- [ ] **Step 5: Commit** `feat: B4 editors; docs`
