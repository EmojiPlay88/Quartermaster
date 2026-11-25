# Acting on Other Pieces (B3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Formula outputs that write another (picked) piece, place a palette counter and write it, remove or move a counter, and set a layer or mask; `layer("name")` to read a layer; same-hex candidates.

**Architecture:** Rules gains `OutputAction`, `OutputTarget.Piece`/`Placed`, new `FormulaOutput` fields, `VariableBinding.SameHex`, `layer()` and validator checks. Core resolves Piece/Placed writes and piece effects in the trace (`FormulaTrace.Effects`) and turns them into existing commands in a fixed order, stamps Piece lines with the target's side, refreshes an open pass after piece commands. Render executes runs through one helper (seeding a placed piece), masks Piece outputs for guests and passes the run piece to candidate lists. Desktop Workbench gets the editors; docs.

**Tech Stack:** C# .NET 10, Avalonia (Desktop), xUnit.

**Spec:** `docs/superpowers/specs/2026-11-25-b3-other-pieces-design.md`

## Global Constraints

Read `.superpowers/sdd/2026-11-25-b3-other-pieces/global-constraints.md` (git-ignored; same folder as the ledger) before each task. It holds the invariants and **every exact validator, run-time, log and Workbench string**; use them verbatim. In short:
- Public repo: invented data only (Riverland). Old packs keep bytes + SHA-256, `CurrentFormat` 4. Old saves keep bytes.
- Additive API only: new record members as body init properties; `IEvalContext.Layer` is a default method; new overloads, never changed signatures.
- Hidden information: candidates on the requester's view; Piece lines stamped with the target's side; acting-side working never shows a Piece-target value; guest dialogs mask Piece outputs (`piece output`); the host decides.
- One undo step per run (one `BeginAction`); removals last; a pass is refreshed after piece commands.
- `-c Release`, one test project at a time, headless only, never stash/push, repo-local identity, `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Old packs keep bytes**: an invented format-4 `pack.json` with every pre-B3 field loads and saves byte-equal, same SHA-256 (Task 1, `B3_absent_fields_keep_bytes`).
2. **Transfer is one run, one undo**: Unit Subtract + Piece Add move the amount; one Undo restores both (Task 4, `Transfer_one_undo_step`).
3. **No leak across sides**: online, the Northern guest's Piece output on a Southern unit's Private property: the guest dialog masks working; the guest's log shows `Southern Compact ledger changed` for the target line and its own working without the target's values; the Southern view shows the full target line (Task 4, `Guest_piece_output_logs_each_side`). Candidates never list a hidden piece (Task 2, `SameHex_candidates_skip_hidden`).
4. **Pass invariant**: a Fully automatic entry whose formula places, removes or relayers on each unit gives the same state as one-by-one runs, and later units' `side()`/aggregates see the change (Task 3, `Pass_refreshes_after_piece_commands`; Task 4, `FullyAuto_entry_with_effects_matches_one_by_one`).
5. **Command order**: rolls, Place, writes, layer/mask, working line, removals — whatever the listed order (Task 3, `Commands_order_fixed_removals_last`).

---

### Task 1: Rules — model, layer() in the language, validator — model: sonnet

**Files:**
- Modify: `src/Quartermaster.Rules/Model/FormulaDef.cs`: `enum OutputAction { Write, Place, Remove, SetLayer, SetMask }`; append `Piece`, `Placed` to `OutputTarget`; `FormulaOutput.Action` (`WhenWritingDefault`), `PieceVariable`, `Slot`, `Layer`, `ToMapId` (`WhenWritingNull`); `VariableBinding.SameHex` (`WhenWritingDefault`). Doc comments as the spec.
- Modify: `src/Quartermaster.Rules/Io/RulesPackIo.cs`: `Normalise` turns blank `PieceVariable`/`Slot`/`Layer`/`ToMapId` into null (outputs inside formulas).
- Modify: `src/Quartermaster.Rules/Expr/Parser.cs` (`["layer"] = (1, 1)`), `Evaluator.cs` (`"layer"` in `FunctionNames`; `EvalCall` case: arg must be `TextLit`, else `EvalError("layer() needs a layer name in quotes")`; result `Num(ctx.Layer(name))`), `IEvalContext.Layer(string name) => throw new EvalError("layer() is not available here")`.
- Modify: `src/Quartermaster.Rules/Expr/ExprFacts.cs`: `layer` is piece-reading wherever `marker` is (game-level checks, `IsGameLevel`).
- Modify: `src/Quartermaster.Rules/Validation/RulesValidator.cs`: 3-argument `Validate(pack, knownMapIds, knownSlots)` overload (old 2-argument one delegates with null); `CheckOutput` gains the action/target table, every B3 output message, Placed and single-Place checks, the `a piece` scope message, skips property checks for non-Write actions; `same hex` warning in the binding loop; `layer` branch in `FilterRefProblems` (`layer() needs a layer name in quotes`); `"layer"` in `ForbiddenConditionFunctions`.
- Test: `tests/Quartermaster.Rules.Tests/OtherPiecesModelTests.cs`, `tests/Quartermaster.Rules.Tests/OtherPiecesValidatorTests.cs`
- [ ] **Step 1: Failing tests**:
  - Model/IO: `B3_absent_fields_keep_bytes` (invented format-4 pack literal, byte-equal + same SHA-256); `New_fields_round_trip_camelCase` (`"action":"setLayer"`, `"target":"placed"`, `"sameHex":true`, `"toMapId"`); `Write_action_and_false_SameHex_omitted`; `Blank_output_texts_become_null`;
  - Language: `Layer_parses_one_argument`; `Layer_needs_quoted_name`; `Layer_default_context_not_available`;
  - Validator: one test per message in the global constraints (`Piece_target_without_variable`, `Variable_not_a_piece_binding`, `Piece_variable_without_piece_target`, `Action_cannot_target` (Remove→Global, Place→Placed, SetMask→Holder), `Place_without_slot`, `Place_unknown_slot_with_known_slots`, `Place_unknown_slot_ignored_without_known_slots`, `Slot_without_place`, `Second_place`, `Placed_without_place`, `Placed_write_must_set`, `SetLayer_without_layer`, `Layer_without_setlayer`, `Remove_unknown_map`, `Map_without_remove`, `Property_list_on_non_write`, `Piece_write_scope_message`, `SameHex_on_non_piece_warns`, `Layer_in_filter_needs_quotes`, `Layer_in_phase_condition_forbidden`); `Remove_output_skips_property_checks`; `Valid_b3_formula_has_no_issues`; `Two_arg_validate_unchanged`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~OtherPieces"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, then the full Rules.Tests → PASS. Build Core, Render, Desktop, App (`-c Release`): the new enum members must not break any `switch` (grep `OutputTarget` in `src/`; `FormulaRunner.RunCore`'s `default:` arm must not treat Piece/Placed as Holder — make it throw `An output refers to a deleted holder` only for Holder, and leave Piece/Placed to Task 2 with a temporary `EvalError("not supported yet")`, listed in the report).
- [ ] **Step 5: Commit** `feat: B3 output actions, Piece/Placed targets, layer(), validator`

### Task 2: Core — writing a picked piece, candidates, log stamping — model: opus

**Files:**
- Modify: `src/Quartermaster.Core/PieceChoices.cs`: overload `For(..., IReadOnlySet<string>? sides, string? runPieceId)` (old one delegates with null); `Named` gains `runPieceId`: same-hex group first (same `MapId` + `PieceInfo.Location` as the run piece, located on the shown state), then the rest, each sorted as today; `SameHex` keeps only that group minus the run piece; constant `NoPieceInHex = "No piece in this hex"`.
- Modify: `src/Quartermaster.Core/FormulaRunner.cs`:
  - `PickedPiece` passes `r.PieceId`; SameHex with no run piece → missing `Needs a unit`;
  - `RunCore`: `Target = Piece` Write resolves the binding named by `PieceVariable` (missing/waiting binding → the run waits as today; not a Piece binding → `An output refers to a deleted Piece variable`); on a player view a placeholder target → missing `Hidden from this player`; key = that piece; scope rule as Unit;
  - `OutputPreview` body init members `Target` (OutputTarget), `SideId` (target side for Piece), `Part` (this output's working part), `Subject` (target name as the binding names it); the trace's `Working` keeps every part (the dialog);
  - `Commands`: Unit/Global/Holder lines carry the working **without** Piece parts; Piece lines: `Key`/`PieceId` = target, `SideId` = `OutputPreview.SideId`, `Subject` = the target's real name, `Working` = `{formula}: {part}`, same `Tag`; `AppliedRun` on the first Unit/Global/Holder line; with none, Commands appends `AddLogLine(LogKind.Calc, "{subject ?? formula name}: {working}" + (tag == null ? "" : $" ({tag})"), t.PieceId, SideId: sideId, FormulaId: t.Formula.Id) { AppliedRun = t.Formula.Id }`.
- Test: `tests/Quartermaster.Core.Tests/PieceOutputTests.cs`, extend `tests/Quartermaster.Core.Tests/PieceChoicesTests.cs` (or the existing Piece binding test file; grep `PieceChoices`).
- [ ] **Step 1: Failing tests**:
  - Candidates: `Same_hex_candidates_listed_first`; `SameHex_limits_to_hex_without_run_piece`; `SameHex_off_grid_no_piece_in_hex`; `SameHex_without_run_piece_needs_unit`; `SameHex_candidates_skip_hidden` (a hidden enemy piece in the hex is absent; a masked one is `Unknown piece (B4)`); `Old_overload_order_unchanged_without_run_piece`;
  - Writes: `Piece_output_adds_to_picked_piece`; `Transfer_subtracts_and_adds_in_one_trace`; `Piece_output_waits_for_pick`; `Piece_output_bad_variable_fails`; `Piece_output_placeholder_hidden_in_view`; `Piece_output_holders_scope_refused`;
  - Lines: `Piece_line_stamped_with_target_side`; `Own_lines_working_omits_piece_parts`; `Applied_run_on_first_own_line`; `Piece_only_run_writes_working_line_with_applied_run`; `PlayerView_actor_sees_ledger_changed_for_enemy_target` and `PlayerView_target_side_sees_full_piece_line` (no PlayerView change: assert the outcome); `Runs_counts_piece_only_run_on_run_piece`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~PieceOutput|FullyQualifiedName~PieceChoices"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, the full Core.Tests and the full Rules.Tests → PASS. Build Render, Desktop, App.
- [ ] **Step 5: Commit** `feat: outputs that write a picked piece; same-hex candidates`

### Task 3: Core — Place, Remove, SetLayer, SetMask, layer(), pass refresh — model: opus

**Files:**
- Create: `src/Quartermaster.Core/PieceEffect.cs`: `enum PieceEffectKind { Place, Remove, SetLayer, SetMask }`; `record PieceEffect(PieceEffectKind Kind, string PieceId, string Text)` with init members for Place (`TypeString`, `StateString`, `MapId`, `Pos`, `StackOnto`), Remove (`ToMapId`, `Pos`, `SideId`), SetLayer/SetMask (`TraitIndex`, `NewState`, `SideId`), plus `LogText` and `Name`.
- Modify: `src/Quartermaster.Core/FormulaRunner.cs`:
  - `public Func<string> NewPieceId { get; set; }` (default `"p:" + Guid.NewGuid().ToString("N")`);
  - `RunCore` resolves Place (slot via `SlotByPath ?? SlotByGpid`, else `Palette entry "{slot}" was not found in the module`; anchor = run piece or picked piece; `PieceFactory.FromSlot`), Placed writes (key = new id, Before = Default; none placed → `An output writes the placed piece, but nothing was placed`), Remove (map lookup and first-board centre; `Map "{map}" was not found in the module`, `Map "{map}" has no board`), SetLayer (`{name} has no layer "{layer}"`, `Layer "{layer}" has no level {value}`, `Layer "{layer}" follows a property, so it cannot be set`), SetMask (`{name} has no mask`, `The mask value must be 0 or 1`); working parts per the global constraints; `FormulaTrace.Effects` (body init, default empty);
  - `Commands`: fixed order (rolls, AddPiece with `LogText`, own writes, Piece writes, Placed writes, SetTraitState + System `AddLogLine` per layer/mask change, the working line when needed, then RemovePiece/MovePieces in output order); a trace with effects and no own write gets the working line (Task 2 rule);
  - `public void AfterApply(IReadOnlyList<GameCommand> executed)`: drops the pass's side map and aggregate state when any is `AddPiece`/`RemovePiece`/`MovePieces`/`SetTraitState`; `[ThreadStatic] internal static int PassRefreshes`; `BeginPass` doc comment updated;
  - `RunContext.Layer`.
- Modify: `src/Quartermaster.Core/PieceFilter.cs`: `FilterContext.Layer` (placeholder in a player view → `EvalError("layer() cannot be read in this view") { HiddenData = true }`; FollowProperty → `Layer "{layer}" follows a property; read the property instead`).
- Modify: `src/Quartermaster.Vassal/Traits/TraitState.cs`: `ObscuredSet(ObscurableDef def, string? state, bool on, string obscurer)`; a shared `FindLayer(type, name)` helper if useful (Core).
- Modify: `src/Quartermaster.Core/Commands.cs`: `RemovePiece.SideId` body init (`WhenWritingNull`); `src/Quartermaster.Core/GameLog.cs`: `LogBuilder.Delete` stamps it.
- Test: `tests/Quartermaster.Core.Tests/PieceEffectTests.cs`, `tests/Quartermaster.Core.Tests/LayerFunctionTests.cs`, extend `tests/Quartermaster.Core.Tests/FormulaPassTests.cs`. Invented module fixture: a palette slot `Markers/Wreck`, a piece type with a 4-level layer `Levels` and an Obscurable trait.
- [ ] **Step 1: Failing tests**:
  - Place: `Place_in_run_piece_hex_stacked`; `Place_in_picked_piece_hex`; `Placed_set_writes_new_piece`; `Placed_without_place_fails`; `Place_unknown_slot_fails`; `Place_uses_NewPieceId`;
  - Remove: `Remove_deletes_and_drops_ledger`; `Remove_to_map_keeps_ledger_and_moves_to_board_centre`; `Remove_line_stamped_with_removed_side` (owner keeps the line, other side drops it); `Remove_save_without_side_keeps_bytes` (old `removePiece` JSON round-trips equal);
  - Layer/mask: `SetLayer_sets_level`; `SetLayer_zero_on_always_active_fails`; `SetLayer_follow_property_fails`; `SetMask_masks_and_unmasks`; `SetMask_line_dropped_for_viewer_who_now_sees_mask`; `Layer_reads_level_and_zero_when_inactive`; `Layer_in_filter`; `Layer_on_placeholder_hidden_data`; `Layer_missing_name_fails`;
  - Order and pass: `Commands_order_fixed_removals_last`; `Effects_only_run_writes_working_line`; `Working_names_masked_pick_as_unknown_piece`; `Pass_refreshes_after_piece_commands` (counter, and a later unit's `side()` sees a SetLayer that changes its side filter); `Pass_not_refreshed_by_ledger_only_commands`.
- [ ] **Step 2: Run** Core `--filter "FullyQualifiedName~PieceEffect|FullyQualifiedName~LayerFunction|FullyQualifiedName~FormulaPass"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, the full Core.Tests, Vassal.Tests and Rules.Tests → PASS. Build Render, Desktop, App.
- [ ] **Step 5: Commit** `feat: formulas that place, remove and set layers or masks; layer()`

### Task 4: Render — applying runs, masking, online and hot-seat — model: opus

**Files:**
- Create: `src/Quartermaster.Render/FormulaApply.cs`: `public static IReadOnlyList<GameCommand> Execute(ModuleSession s, IEnumerable<GameCommand> commands, Action<GameCommand> exec)` (seeds with `s.SeedAfterAdd` after each `AddPiece`).
- Modify: `src/Quartermaster.Render/PhaseController.cs`: `ApplyFormula` and `Apply` execute through `FormulaApply.Execute`; `Apply` skips a unit whose piece is gone and calls `s.Runner.AfterApply(executed)`; `LogWorking` returns when the trace has outputs or effects.
- Modify: `src/Quartermaster.Render/FormulaMasking.cs`: `PieceOutputInput = "piece output"` added per the spec rule; `GuardsOnHidden` treats it like `HolderCallInput`.
- Modify: `src/Quartermaster.Render/Online/IntentExecutor.cs`: `FormulaIntentRunner.Run` pick check passes `i.PieceId` to `PieceChoices.For`; no new `GuestRule` refusal.
- Modify: `src/Quartermaster.Render/FormulaDialogModel*.cs` and the batch grid model: candidate lists pass the run piece id.
- Modify: `src/Quartermaster.Desktop/Formulas/FormulaWindow.cs`, `src/Quartermaster.App/FormulaDialog.cs`: local branch through `FormulaApply.Execute`.
- Test: `tests/Quartermaster.Render.Tests/OtherPiecesApplyTests.cs`, `tests/Quartermaster.Render.Tests/OtherPiecesOnlineTests.cs` (the existing online/guest harness; grep `FormulaIntentRunner` in Render.Tests).
- [ ] **Step 1: Failing tests**:
  - Apply: `Transfer_one_undo_step`; `Place_then_placed_set_one_undo_and_seeded_first` (a sheet-seeded property is seeded, a Set overrides it, Undo removes the piece and its seeds); `Remove_run_piece_resolves_pending_run`; `FullyAuto_entry_with_effects_matches_one_by_one`; `FullyAuto_unit_removed_earlier_skipped`; `Placed_piece_not_in_current_entry_units`; `LogWorking_skips_effects_trace`;
  - Hidden info: `Guest_piece_output_logs_each_side`; `Guest_dialog_masks_piece_output`; `Guest_pick_of_hidden_piece_refused` (`Choose one of the listed pieces`); `Guest_pick_same_hex_resolves_on_host`; `Guest_remove_enemy_piece_applied_at_once_and_undoable`; `Guest_setmask_reveal_enemy_piece`; `Hot_seat_piece_output_uses_current_player_candidates`;
  - Masking: `PieceOutputInput_added_for_private_property`; `PieceOutputInput_added_when_pieces_may_hide`; `PieceOutputInput_absent_for_public_and_no_hiding`.
- [ ] **Step 2: Run** Render `--filter "FullyQualifiedName~OtherPieces"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, the full Render.Tests, the full Core.Tests; build Desktop and App → PASS.
- [ ] **Step 5: Commit** `feat: apply piece outputs and effects as one step; guest masking`

### Task 5: Workbench, wiki, RESUME, full runs — model: sonnet

**Files:**
- Modify: `src/Quartermaster.Desktop/Workbench/FormulasTab.Grids.cs` (+ `FormulasTab.cs` if needed): outputs grid columns `Action`, `Piece`, `Palette entry`, `Layer`, `To map` after `Pick by` (new index constants; older columns keep places), with the exact tooltips; bindings grid `Same hex` check column after `Dice count`. `To map` shows `(delete)` for null.
- Modify: `src/Quartermaster.Render/FormulaEdit.cs`: `WithOutputAction`, `WithPieceVariable`, `WithSlot`, `WithLayer`, `WithToMap`, `WithSameHex`; `WithOutputTarget` clears `PieceVariable` unless Piece; `WithOutputAction` clears unused fields (Slot unless Place, Layer unless SetLayer, ToMapId unless Remove, Property/PropertyChoice/PropertySelect unless Write). Blank → null.
- Modify: `src/Quartermaster.Render/Workbench/WorkbenchRules.cs` and `src/Quartermaster.Desktop/Workbench/WorkbenchWindow.cs`: validate with the module's slot keys (paths and gpids).
- Modify: `src/Quartermaster.App/Workbench/*`: build only.
- Modify: `docs/wiki/Making-a-Rules-Pack.md`: `## Acting on other pieces` with `### Writing to another counter` (`Barge transfer`), `### Results on the other side's counters` (`Shelling`, what each log shows), `### Placing a counter` (`Wreck`, `Salvage`), `### Removing a counter` (`Bridge`, `Dead pile`), `### Layers and masks` (`Fort level`, `layer("Levels")`, minefield reveal); the validator texts.
- Modify: `docs/wiki/Formula-Reference.md`: `### layer("name")`; output actions and targets; error table rows for the runtime messages; Piece binding `Same hex` and same-hex-first order.
- Modify: `docs/wiki/Window-Rules-Workbench.md`: the new columns. `docs/wiki/Window-Formula-Dialog.md`: same-hex order.
- Modify: `docs/superpowers/RESUME.md`: new CURRENT STATE for B3, B2's moved under Previous; notes the downstream follow-up (pack items that can drop two-half formulas; any `OutputTarget` switch with a `_` arm).
- Test: Desktop `FormulasTab_output_action_columns_commit`, `FormulasTab_old_output_columns_keep_places`, `FormulasTab_action_change_clears_unused_fields`, `FormulasTab_same_hex_column`, `FormulasTab_to_map_delete_is_null`; Render `FormulaEdit_with_action_clears_fields`; Docs `Wiki_mentions_layer_function` (extend the existing function-docs test if one pins `FunctionNames`).
- [ ] **Step 1: Failing tests.**
- [ ] **Step 2: Run** Desktop `--filter "FullyQualifiedName~FormulasTab"`, Render `--filter "FullyQualifiedName~FormulaEdit"`, Docs `--filter "FullyQualifiedName~Wiki"` → FAIL.
- [ ] **Step 3: Implement**; wiki; RESUME.
- [ ] **Step 4: Full runs**, one at a time: Rules, Vassal, Core, Render, Desktop, Docs → PASS (flaky: re-run once, report both). Build `src/Quartermaster.App -c Release`.
- [ ] **Step 5: Commit** `feat: B3 editors; docs`
