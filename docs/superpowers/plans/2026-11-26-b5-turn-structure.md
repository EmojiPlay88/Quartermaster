# Turn Structure and End of Game (B5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Blocks that repeat until the acting player stops, one nesting level with `segment()`; sides that react in another side's phase; a reinforcement and withdrawal schedule listed when due and acted on by the player; a last turn and victory conditions that end the game and record the winner.

**Architecture:** Rules gains `PhaseDef.Reactors`, `RulesPack.Schedule/LastTurn/LastStage/Victory`, `segment()` and validator checks. Core gains nested and open-ended blocks in `Sequencer` (`PhasePosition.Inner`, a `repeat` flag on `Next`), `MayAct`, game-over state (`EndGame`, `GameEnd`) and schedule state (`MarkScheduled`, `ScheduleDue`). Render asks Repeat/Continue on the Next Phase path (local, hot-seat, online through `NextPhaseIntent.Repeat`), ends the game at the last turn or on a victory at phase entry, widens the acting-side checks to reactors, and acts on schedule rows (`ActOnSchedule`, `ScheduleIntent`). Desktop shows the question, the Due section and game over. Workbench editors; docs.

**Tech Stack:** C# .NET 10, Avalonia (Desktop), xUnit.

**Spec:** `docs/superpowers/specs/2026-11-26-b5-turn-structure-design.md`

## Global Constraints

Read `.superpowers/sdd/2026-11-26-b5-turn-structure/global-constraints.md` (git-ignored; same folder as the ledger) before each task. It holds the invariants and **every exact validator, run-time, log, dialog, panel and Workbench string**; use them verbatim. In short:
- Public repo: invented data only (Riverland). No content from any real game anywhere (code, tests, docs, packs).
- Old packs keep bytes + SHA-256, `CurrentFormat` 4. Old saves keep bytes. Every new pack, save, command and intent member is omitted when null or default; blank text and empty lists become null on load.
- Additive API only: new record members as body init properties; `IEvalContext.Segment`, `IPhaseUi.AskRepeat`, `IActionPort.NextPhase(bool)`, `IActionPort.ActOnSchedule` are default methods; new overloads, never changed signatures.
- The validator parses every new expression. Every new field has a Workbench editor and a wiki section.
- Hidden information: guests see hidden inputs masked, the host decides; victory is evaluated only on the host; a guest's due list comes from its own view; refusals are neutral; the schedule log line has no names or counts.
- Online guest intents are checked by the host: the Repeat choice through the existing `PhaseGate`; schedule rows with the sender's sides (`ActingFor`); reactors through `Sequencer.MayAct`.
- A fixed-count pack without nesting walks its turns exactly as on master (positions, `StageText`, skipped chips).
- `-c Release`, one test project at a time, headless only, never stash/push, repo-local identity, `Co-Authored-By: Claude <Model> 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Old packs and saves keep bytes**: a hand-written pre-B5 `pack.json` literal (blocks, phases with entries, alerts) loads and saves byte-equal with a constant SHA-256 (Task 1, `B5_absent_fields_keep_bytes`); a master save literal with a position, `EndedAt` and `StartCursor` round-trips unchanged and has no `inner`, `gameOver` or `scheduleDone` (Task 3, `Save_without_b5_members_keeps_bytes`).
2. **Fixed walks unchanged**: a table-driven test walks two turns of a pack with two fixed blocks and compares every position and `StageText` with literals taken from master (Task 3, `Fixed_blocks_walk_as_master`).
3. **Open-ended and nested walk**: Repeat/Continue at an open-ended inner block inside a fixed outer block gives the right `Repeat`/`Inner`/`segment()`; `Count = 0` never throws (Task 3).
4. **Gates**: a guest not holding the acting side cannot answer the question (`PhaseGate`); a reacting guest may run a unit-less formula and write a global but not tick or press Next; a guest's schedule intent acts only on counters it may act on and is refused neutrally otherwise (Tasks 6, 7).
5. **Game over**: the last turn ends the game in the Next Phase action with one undo; a victory at phase entry stops the start entries; a guest never evaluates a victory; after the end Next Phase and schedule intents are refused with `The game is over.` (Tasks 4, 5, 6).

---

### Task 1: Rules — model, segment(), normalise, bytes tests — model: sonnet

**Files:**
- Modify: `src/Quartermaster.Rules/Model/PhaseDefs.cs`: `PhaseDef.Reactors` (`List<string>?`, `WhenWritingNull`); doc comment on `PhaseBlock.Count` (0 = until a player stops).
- Create: `src/Quartermaster.Rules/Model/TurnEndDefs.cs`: `ScheduleAction` (`Arrive, Leave, Return`), `ScheduleRow` (`Id`, `Turn`, `Stage` `int?` `WhenWritingNull`, `PhaseId`, `Piece`, `Action` `WhenWritingDefault`, `ZoneMapId`/`FormulaId`/`Label` `WhenWritingNull`), `VictoryCondition` (`Id`, `Condition`, `SideId`/`Message`/`PhaseIds` `WhenWritingNull`). Doc comments as the spec.
- Modify: `src/Quartermaster.Rules/Model/RulesPack.cs`: `Schedule`, `LastTurn`, `LastStage`, `Victory` (all `WhenWritingNull`, declared after `MapOwners`); `ById<T>` also finds `ScheduleRow` and `VictoryCondition`.
- Modify: `src/Quartermaster.Rules/Io/RulesPackIo.cs`: `Normalise`: `Reactors` trimmed, distinct, empty → null; `Schedule`/`Victory` empty → null, null items dropped, blank `ZoneMapId`, `FormulaId`, `Label`, `SideId`, `Message` → null, `PhaseIds` as `ResetAt`; `Id`/`PhaseId`/`Piece`/`Condition` null → "".
- Modify: `src/Quartermaster.Rules/Expr/Parser.cs` (`["segment"] = (0, 0)`), `Evaluator.cs` (`"segment"` in `FunctionNames`; `EvalCall` → `Num(ctx.Segment(), c)`; `IEvalContext.Segment() => 0` with doc comment).
- Test: `tests/Quartermaster.Rules.Tests/TurnStructureModelTests.cs`.
- [ ] **Step 1: Failing tests**:
  - IO: `B5_absent_fields_keep_bytes` (hand-written format-4 literal with sides, phases with `onStart`/`onEnd`/`condition`, two fixed blocks, an alert and a formula; `s.Replace("\r\n", "\n")`; byte-equal + constant SHA-256 computed once on master code); `New_fields_round_trip_camelCase` (`"reactors"`, `"schedule"` with `"action": "leave"` (camelCase enum text), `"lastTurn"`, `"lastStage"`, `"victory"` with `"phaseIds"`); `Arrive_action_omitted`; `Empty_lists_become_null` (`reactors: []`, `schedule: []`, `victory: []`); `Blank_texts_become_null`; `Count_zero_block_round_trips` (`"count": 0` written);
  - Language: `Segment_parses_no_arguments`; `Segment_default_context_is_zero`; `Segment_in_function_names`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~TurnStructureModel"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, then the full Rules.Tests → PASS. Build Core, Render, Desktop, App (`-c Release`).
- [ ] **Step 5: Commit** `feat: B5 pack model — reactors, schedule, last turn, victory, segment()`

### Task 2: Rules — validator — model: sonnet

**Files:**
- Modify: `src/Quartermaster.Rules/Validation/RulesValidator.cs`: `CheckBlocks` (negative count, same phases, one nesting level, partial overlap, two open-ended blocks ending at one phase; count 0 accepted); `CheckPhase` (reactors); new `CheckGameEnd` (last turn/stage, victory conditions: ids, empty, the phase-condition rules through a shared helper extracted from `CheckPhaseCondition` that takes the message prefix, side, phases); new `CheckSchedule(pack, issues, knownMapIds)` (every row message of the constraints; the counter filter through `ParseProblem` + `FilterRefProblems`, and `runs`/`row`/`dice` refused there). Call both from `Validate` after `CheckBlocks`. Issue ids: the row's / condition's id (pack id when blank).
- Test: `tests/Quartermaster.Rules.Tests/TurnStructureValidatorTests.cs`; update the existing test that pins `count must be at least 1` (grep it in `tests/Quartermaster.Rules.Tests`) to the negative-count text.
- [ ] **Step 1: Failing tests**:
  - Blocks: `Count_zero_is_valid`; `Negative_count_error`; `Nested_one_level_ok`; `Nested_two_levels_error`; `Same_phases_error`; `Partial_overlap_still_error`; `Two_open_blocks_same_last_phase_error`; `Open_inner_in_fixed_outer_same_last_ok`;
  - Reactors: `Reactor_unknown_side`; `Reactors_on_sideless_phase_warns`; `Own_side_as_reactor_warns`;
  - Game end: `Last_turn_below_one`; `Last_stage_below_one`; `Last_stage_without_turn`; `Last_stage_without_blocks_warns`; `Victory_no_id`; `Victory_duplicate_id`; `Victory_empty_condition`; `Victory_condition_parse_error`; `Victory_condition_reads_piece_function` (e.g. `name()` → the phase-condition tail with the `Victory condition 1: condition` prefix); `Victory_unknown_side`; `Victory_unknown_phase`; `Victory_segment_allowed`;
  - Schedule: one test per schedule message (`Schedule_no_id`, `Schedule_duplicate_id`, `Schedule_turn_below_one`, `Schedule_stage_below_one`, `Schedule_no_phase`, `Schedule_unknown_phase`, `Schedule_stage_outside_block_warns`, `Schedule_no_filter`, `Schedule_filter_parse_error`, `Schedule_filter_unknown_property`, `Schedule_filter_runs_refused`, `Schedule_arrive_needs_zone`, `Schedule_unknown_zone_map`, `Schedule_unknown_formula`, `Schedule_delete_with_formula`, `Schedule_after_last_turn_warns`); `Valid_b5_pack_has_no_issues`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~TurnStructureValidator"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, then the full Rules.Tests → PASS. Build Core, Render, Desktop, App.
- [ ] **Step 5: Commit** `feat: B5 validator — open and nested blocks, reactors, game end, schedule`

### Task 3: Core — open-ended and nested blocks, Inner, segment(), MayAct — model: opus

**Files:**
- Modify: `src/Quartermaster.Core/Phases.cs`: `PhasePosition.Inner` (body init `int`, `WhenWritingDefault`, doc comment).
- Modify: `src/Quartermaster.Core/Sequencer.cs`: `Spans` accepts count 0 and one nesting level (spans carry their depth); `ValidBlocks` unchanged in meaning; new `OuterBlock`, `InnerBlock`, `Next(pack, cur, bool repeat)` (the old `Next` calls it with false), `RepeatChoice`, `MayAct`; `First`, `Resolve` and `StageText` per the spec (never `Math.Clamp(x, 1, 0)`).
- Modify: `src/Quartermaster.Core/PhaseConditions.cs`: `Next(pack, s, cur, model, bool repeat)` overload (walk passes `repeat` to the first step only); `TurnLength` per the spec; `PhaseContext.Segment() => at.Inner`; make the context reachable for `GameEnd` (internal `Evaluate(pack, s, string expr, PhasePosition at, model)` that throws `EvalError`/`ParseError`).
- Modify: `src/Quartermaster.Core/FormulaRunner.cs` (`RunContext.Segment() => Position?.Inner ?? 0`), `src/Quartermaster.Core/PieceFilter.cs` (`SegmentAt` beside `StageAt`; both contexts).
- Test: `tests/Quartermaster.Core.Tests/SequencerNestingTests.cs`; `tests/Quartermaster.Core.Tests/SegmentTests.cs`.
- [ ] **Step 1: Failing tests**:
  - Walks: `Fixed_blocks_walk_as_master` (two fixed blocks, two turns, every position and `StageText` vs literals produced on master); `Open_block_continue_leaves`; `Open_block_repeat_returns_to_first_with_repeat_plus_one`; `Open_block_count_zero_never_throws` (Next, Resolve, StageText, TurnLength with count 0); `Open_stage_text_has_no_of` (`Fire round 3`); `Inner_fixed_in_outer_fixed_counts` (Repeat and Inner through a whole turn); `Inner_open_in_outer_fixed_repeat_and_continue`; `Inner_fixed_ends_with_outer_open_choice_goes_to_outer` (`RepeatChoice` null while `Inner < Count`, then the outer block); `Entering_inner_block_sets_inner_one`; `First_in_inner_block_inner_one`; `Leaving_inner_block_inner_zero`; `Nested_stage_text_joined` (`Day 1 of 2, Fire round 2`); `Resolve_clamps_inner`; `Two_level_nesting_ignored_as_invalid`; `Conditions_next_repeat_skips_false_first_phase` (repeat into a block whose first phase is skipped walks on inside the block);
  - Positions: `Inner_omitted_when_zero`; `Inner_round_trips`; `Position_equality_includes_inner`; `Save_without_b5_members_keeps_bytes` (hand-written master save literal with `position`, `endedAt`, `startCursor`; round-trip equal, no `inner`);
  - `segment()`: `Segment_in_formula_reads_inner`; `Segment_in_phase_condition_reads_candidate`; `Segment_in_unit_filter`; `Segment_zero_outside_inner_block`;
  - Reactors: `MayAct_solo`; `MayAct_sideless_phase`; `MayAct_acting_side`; `MayAct_reactor_side`; `MayAct_other_side_false`; `MayAct_unknown_reactor_ignored`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~SequencerNesting|FullyQualifiedName~SegmentTests"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, the existing `SequencerTests`, `PhaseConditionTests`, `PhaseStateTests`, then the full Core.Tests → PASS; build Render, Desktop, App.
- [ ] **Step 5: Commit** `feat: open-ended and nested blocks, segment(), MayAct`

### Task 4: Core — game over and schedule state — model: opus

**Files:**
- Modify: `src/Quartermaster.Core/GameState.cs`: `GameOver : GameOverState?`, `ScheduleDone : List<string>?` (both `WhenWritingNull`); reducer cases `EndGame` (open runs' `skipped:` lines and summaries through one helper shared with `DoAdvance`; `Pending` and `StartCursor` cleared; `Game over: {Text}` Turn line) and `MarkScheduled` (id added once; `Text` as a Turn line). Make sure `DeepClone` and `PlayerView.For` (including its fail-closed `Lockdown`) keep both members.
- Modify: `src/Quartermaster.Core/Commands.cs`: `EndGame`, `MarkScheduled` with `[JsonDerivedType]` names `"endGame"`, `"markScheduled"`.
- Modify: `src/Quartermaster.Core/Decisions.cs`: `DecisionKind.Schedule` (appended); `Compute` adds due-now rows with counters (`Due: {label}`), from the state given (sides from `GameState.ViewSides`, null = solo).
- Create: `src/Quartermaster.Core/GameEnd.cs` (`GameOverState`, `GameEnd.Reached`, `GameEnd.Check`, `GameEnd.ResultText`), `src/Quartermaster.Core/ScheduleDue.cs` (`DueRow`, `ScheduleDue.Reached`, `IsNow`, `Label`, `Counters`, `ZoneMap`, `For`).
- Test: `tests/Quartermaster.Core.Tests/GameEndTests.cs`, `tests/Quartermaster.Core.Tests/ScheduleDueTests.cs`.
- [ ] **Step 1: Failing tests**:
  - Game over: `EndGame_sets_state_and_logs`; `EndGame_logs_skipped_open_runs_and_clears_pending`; `EndGame_omitted_when_null`; `EndGame_round_trips`; `PlayerView_keeps_game_over`; `Lockdown_keeps_game_over`; `Reached_after_last_turn`; `Not_reached_without_last_turn`; `Reached_at_end_of_last_stage`; `Not_reached_inside_last_stage`; `Check_first_true_in_order`; `Check_respects_phase_ids`; `Check_error_counts_not_met_and_warns`; `Check_reads_globals_and_whole_game_aggregates`; `Result_text_message_side_default`;
  - Schedule: `Reached_same_phase`; `Reached_later_phase_same_turn`; `Reached_later_turn`; `Not_reached_earlier_phase`; `Stage_row_reached_only_at_or_after_stage`; `Stage_row_reached_in_later_repeat_at_block_first`; `Now_vs_overdue`; `Unknown_phase_never_reached`; `Arrive_counters_on_zone_only`; `Leave_counters_off_zone`; `Leave_without_zone_all_matching`; `Zone_map_by_name`; `For_lists_reached_not_done_in_order`; `For_only_actable_counters` (another side's counters left out); `For_hides_row_without_actable_counters_for_player`; `Solo_lists_row_without_counters`; `For_empty_when_game_over`; `MarkScheduled_adds_once_and_logs`; `ScheduleDone_omitted_when_null`; `Decision_for_due_now_only`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~GameEnd|FullyQualifiedName~ScheduleDue"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, then the full Core.Tests → PASS; build Render, Desktop, App.
- [ ] **Step 5: Commit** `feat: game over and schedule state, due list`

### Task 5: Render — PhaseController: repeat choice, game end, reactors in the view — model: opus

**Files:**
- Modify: `src/Quartermaster.Render/PhaseController.cs`: `Advance(bool repeat)` (old `Advance()` → `Advance(false)`); after the end-entry stop and the conditions step, `GameEnd.Reached` → `GameEnd.Check` (all conditions) → `EndGame` in the same action, `PhaseBoundary` raised; `EnterPhase` checks victory after `ResetAtEntry` (host only, as `ResetAtEntry`) and skips the start entries when it ends the game; `Start()` the same; `Advance` with `GameOver` set: notice `The game is over.`, nothing executed; `View()`: `RepeatAsk` (rules of the spec), `Reactors`, `GameOverText`, the reacting-viewer `PlayingText`; `SkippedBefore` walks with `repeat` per the spec; the victory warning once per condition per session (`s.Log.Add(DiagnosticSeverity.Warning, "rules", …)`). New `record RepeatAsk(string Label, int Number)` and the `PhaseView` init members.
- Modify: `src/Quartermaster.Render/ModuleSession.cs` (`ActorSideFor` prefers a held reacting side after the acting side), `src/Quartermaster.Render/Banner.cs` (`Game over: …`), `src/Quartermaster.Render/PhaseTexts.cs` (the open-ended / nested stage text through `Sequencer.StageText`; `Reacting: {names}`), `src/Quartermaster.Render/PackHarness.cs` (`Advance(bool repeat)`, `GameOver`).
- Test: `tests/Quartermaster.Render.Tests/TurnStructureControllerTests.cs`, `tests/Quartermaster.Render.Tests/GameEndControllerTests.cs`.
- [ ] **Step 1: Failing tests**:
  - Repeat: `View_asks_repeat_at_open_block_last_phase`; `View_no_ask_with_end_entries_pending` (OnEnd not yet run); `View_asks_after_end_entries_ran`; `View_no_ask_for_other_sides_viewer`; `Advance_repeat_goes_back_with_repeat_plus_one`; `Advance_continue_leaves_block`; `Advance_repeat_one_undo`; `Advance_repeat_resets_and_runs_start_entries` (`ResetAt` on the block's first phase resets each repeat); `Inner_repeat_segment_in_formula`; `Fixed_pack_advance_unchanged` (a fixed-block pack's two-turn log lines equal master's literal); `Harness_advance_repeat`;
  - Game end: `Last_turn_ends_game_in_next_phase_action`; `Last_turn_victory_checked_at_end`; `Last_turn_no_victory_text`; `Victory_on_entry_ends_game_and_skips_start_entries`; `Victory_phase_ids_limit`; `Victory_not_checked_on_guest_or_replay`; `Undo_out_of_game_over`; `Advance_after_game_over_does_nothing_notice`; `View_game_over_text`; `Victory_error_warns_once`; `Banner_game_over`;
  - Reactors: `Reactor_viewer_playing_text_you_may_react`; `View_lists_reactors`; `ActorSideFor_prefers_reacting_side`; `Non_reactor_viewer_unchanged`.
- [ ] **Step 2: Run** Render `--filter "FullyQualifiedName~TurnStructureController|FullyQualifiedName~GameEndController"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, the existing `PhaseControllerTests`, `PhasePipelineTests`, `PhaseTextsTests`, `BannerTests`, then the full Render.Tests → PASS; build Desktop and App.
- [ ] **Step 5: Commit** `feat: repeat or continue at open blocks; last turn and victory end the game; reactors in the phase view`

### Task 6: Render — ports, dispatch and online for repeat, reactors and game over — model: opus

**Files:**
- Modify: `src/Quartermaster.Render/PhaseRunDispatch.cs` (`IPhaseUi.AskRepeat(RepeatAsk) => Task.FromResult<bool?>(false)` default; `NextPhase` asks after the decisions check, Cancel does nothing; game over: status `The game is over.`), `src/Quartermaster.Render/Actions/ActionPort.cs` (`string? NextPhase(bool repeat) => NextPhase();`), `LocalActions.cs`, `RemoteActions.cs` (sends `new NextPhaseIntent { Repeat = repeat }`), `ActionPortSwitch.cs`.
- Modify: `src/Quartermaster.Render/Online/Intents.cs` (`NextPhaseIntent.Repeat`, body init, `WhenWritingDefault`), `src/Quartermaster.Render/Online/IntentExecutor.cs` (`NextPhase` passes `Repeat` after `PhaseGate`; refuses `The game is over.` when over; `FormulaIntentRunner.ActingSideHeld` → `Sequencer.MayAct`).
- Test: `tests/Quartermaster.Render.Tests/TurnStructureOnlineTests.cs` (the existing host/guest harness of `IntentExecutorTests`/`GuestSessionTests`), `tests/Quartermaster.Render.Tests/PhaseRunDispatchTests.cs` (extend).
- [ ] **Step 1: Failing tests**:
  - Dispatch: `Dispatch_asks_repeat_and_passes_choice`; `Dispatch_cancel_does_nothing`; `Dispatch_no_question_without_open_block`; `Old_ui_default_continues`; `Dispatch_game_over_status`;
  - Ports: `Local_next_phase_repeat`; `Old_port_default_ignores_repeat`; `Remote_sends_repeat_flag`; `Next_phase_intent_without_repeat_keeps_json` (`{"$t":"nextPhase"}` unchanged);
  - Online: `Guest_acting_side_repeat_applies`; `Guest_other_side_repeat_refused_by_phase_gate`; `Guest_next_phase_after_game_over_refused`; `Reacting_guest_runs_unitless_formula`; `Reacting_guest_writes_global`; `Reacting_guest_cannot_tick_or_advance`; `Non_reacting_guest_still_refused` (`That can't be calculated during another side's phase.`).
- [ ] **Step 2: Run** Render `--filter "FullyQualifiedName~TurnStructureOnline|FullyQualifiedName~PhaseRunDispatch"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, the full Render.Tests; build Desktop and App → PASS.
- [ ] **Step 5: Commit** `feat: repeat choice through ports and intents; reacting guests; game over refusals`

### Task 7: Render — acting on schedule rows, locally and online — model: opus

**Files:**
- Modify: `src/Quartermaster.Render/PhaseController.cs`: `ActOnSchedule(string rowId) : string?` (spec: refusals in order game over, `MayAct`, row/counters neutral; one action labelled `{label}`; Place = `MarkScheduled` then the formula through `Apply(f, "schedule", units, queued)` with the actor's stamp; Withdraw = one `MovePieces` to the zone map's first board centre or `RemovePiece` each, then `MarkScheduled`, then the formula; `RunsQueued` raised after the action).
- Modify: `src/Quartermaster.Render/Actions/ActionPort.cs` (`string? ActOnSchedule(string rowId) => IntentExecutor.NotUnderstoodText;`), `LocalActions.cs`, `RemoteActions.cs` (sends `ScheduleIntent`, returns null), `ActionPortSwitch.cs`; `src/Quartermaster.Render/Online/Intents.cs` (`ScheduleIntent(string RowId)`, `[JsonDerivedType(typeof(ScheduleIntent), "schedule")]`); `src/Quartermaster.Render/Online/IntentExecutor.cs` (dispatch: `BadId` → `NotUnderstoodText`; `ActOnSchedule` inside `c.ActingFor(sides, who)`; refusal text passed through).
- Create: `src/Quartermaster.Render/DueModel.cs`: rows for the panel from `ScheduleDue.For(pack, s.View ?? state, model, s.ViewSides)` with `Label`, `StateText` (`Due now` / `Overdue since turn {T}`), `ButtonText` (`Place` / `Withdraw` / null), `Tooltip` (the constraint texts with the zone map's name), `PieceIds`, `ZoneMapId`.
- Test: `tests/Quartermaster.Render.Tests/ScheduleActionTests.cs`, `tests/Quartermaster.Render.Tests/ScheduleOnlineTests.cs`.
- [ ] **Step 1: Failing tests**:
  - Local: `Place_marks_done_logs_label_one_undo`; `Place_runs_formula_per_counter_semi_auto_queues`; `Place_runs_formula_fully_auto_applies`; `Withdraw_moves_to_zone_centre`; `Withdraw_without_zone_deletes`; `Withdraw_then_formula_on_moved`; `Withdraw_no_on_move_trigger`; `Done_row_refused`; `Unreached_row_refused`; `Game_over_refused`; `Other_sides_phase_refused`; `Reacting_side_may_act`; `Hot_seat_acts_only_on_own_counters`; `Due_model_texts_and_buttons`; `Due_model_hides_other_side_rows`; `Decision_listed_for_due_now`;
  - Online: `Guest_place_own_row_applies`; `Guest_row_with_only_hidden_counters_refused_neutral` (same text as unknown row); `Guest_unknown_row_same_text`; `Guest_bad_id_not_understood`; `Guest_due_list_from_own_view`; `Schedule_log_line_has_no_names`; `Old_port_default_refuses`.
- [ ] **Step 2: Run** Render `--filter "FullyQualifiedName~ScheduleAction|FullyQualifiedName~ScheduleOnline"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, the full Render.Tests; build Desktop and App → PASS.
- [ ] **Step 5: Commit** `feat: act on schedule rows; schedule intent; due list model`

### Task 8: Desktop — question dialog, game over, reacting line, Due section — model: sonnet

**Files:**
- Modify: `src/Quartermaster.Desktop/Phases/DesktopPhaseUi.cs` (`AskRepeat` through `ChoiceDialog` with the constraint texts), `src/Quartermaster.Desktop/Phases/PhasePanel.cs` (`Reacting: {names}`, `Game over: {text}` in place of the next-phase line, the Due section bound to `DueModel`: label, state, button, tooltip, row click centres), `src/Quartermaster.Desktop/Phases/PhaseStrip.cs` (game-over text), `src/Quartermaster.Desktop/MainWindow.Phases.cs` (Place/Withdraw through the port; after a local Place select the counters and show their map; status texts).
- Modify: `src/Quartermaster.App/*`: build only (default interface members cover it).
- Test: `tests/Quartermaster.Desktop.Tests/TurnStructureUiTests.cs` (headless).
- [ ] **Step 1: Failing tests**: `Repeat_dialog_texts_and_buttons`; `Repeat_dialog_cancel_returns_null`; `Panel_shows_reacting_line`; `Panel_shows_game_over`; `Strip_shows_game_over`; `Due_section_hidden_when_empty`; `Due_section_row_place_button_and_tooltip`; `Due_withdraw_button_text`; `Place_selects_counters_and_shows_zone_map`; `Place_status_text`; `Guest_place_sends_intent`.
- [ ] **Step 2: Run** Desktop `--filter "FullyQualifiedName~TurnStructureUi"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, the existing `PhasePanelTests`, `PhaseStripTests`, `NextPhaseTests`, then the full Desktop.Tests; build App → PASS.
- [ ] **Step 5: Commit** `feat: Desktop repeat question, game over, reacting sides and due list`

### Task 9: Workbench editors — model: sonnet

**Files:**
- Create: `src/Quartermaster.Render/GameEndEdit.cs` (`WithLastTurn`, `WithLastStage`, `AddVictory` (ids `victory-{n}`), `UpdateVictory`, `RemoveVictory`, `MoveVictory`), `src/Quartermaster.Render/ScheduleEdit.cs` (`Add` (ids `sched-{n}`), `Update`, `Remove`, `Move`); empty lists → null, blank → null.
- Modify: `src/Quartermaster.Render/PhaseEdit.cs` (`SetReactors`; `MakeBlock` accepts count 0 and one nesting level, `Blocks nest one level only`), `src/Quartermaster.Render/Workbench/WorkbenchRules.cs` (`WorkbenchTab.Schedule`, `WorkbenchTab.EndOfGame` appended; problems jump for schedule rows and victory/game-end issues).
- Modify: `src/Quartermaster.Desktop/Workbench/PhasesTab.cs` (`Also acting:` check boxes with tooltip; block marker `(until stopped)`), `src/Quartermaster.Desktop/Workbench/BlockDialog.cs` (`Until a player stops`), `src/Quartermaster.Desktop/Workbench/WorkbenchWindow.cs` (two pages, `CommitPending`, jumps).
- Create: `src/Quartermaster.Desktop/Workbench/EndOfGameTab.cs`, `src/Quartermaster.Desktop/Workbench/ScheduleTab.cs` (the controls and texts of the constraints; edits through `UpdateRules`, text commits after 400 ms as `AlertsTab`; guests read-only as the other tabs).
- Modify: `src/Quartermaster.App/Workbench/*`: build only.
- Test: Render `tests/Quartermaster.Render.Tests/TurnStructureEditTests.cs` (`SetReactors_empty_null`; `MakeBlock_count_zero`; `MakeBlock_nested_ok`; `MakeBlock_second_level_refused`; `MakeBlock_partial_overlap_refused`; `GameEndEdit_last_turn_and_stage`; `GameEndEdit_victory_add_update_move_remove_last_to_null`; `ScheduleEdit_add_ids_update_move_remove_last_to_null`); Desktop `tests/Quartermaster.Desktop.Tests/TurnStructureWorkbenchTests.cs` (`Phases_reactor_checkboxes_commit`; `Phases_reactor_list_excludes_own_side`; `Block_dialog_until_stopped_gives_zero`; `Block_marker_until_stopped`; `EndOfGame_last_turn_commits`; `EndOfGame_victory_fields_commit`; `EndOfGame_condition_live_line`; `Schedule_grid_row_commits`; `Schedule_filter_error_shown`; `Problems_jump_to_schedule_and_end_tabs`; `Guest_tabs_read_only`).
- [ ] **Step 1: Failing tests.**
- [ ] **Step 2: Run** Render `--filter "FullyQualifiedName~TurnStructureEdit"`, Desktop `--filter "FullyQualifiedName~TurnStructureWorkbench|FullyQualifiedName~PhasesTab"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, the full Render.Tests and Desktop.Tests; build App → PASS.
- [ ] **Step 5: Commit** `feat: B5 Workbench editors — reactors, open and nested blocks, end of game, schedule`

### Task 10: Wiki, RESUME, full runs — model: sonnet

**Files:**
- Modify: `docs/wiki/Making-a-Rules-Pack.md` (`## Turn structure and end of game`: `### Blocks that repeat until a player stops` (Riverland `Fire round` over North Combat), `### Fire rounds inside a day` (#34 recipe: `Day` count 2, `Fire round` inside, `Fired this round` resets at North Combat, `segment()`), `### Sides that react` (South Combat with the Northern League reacting: `Ambush fire`, a unit-less formula writing the global `River alarm`), `### Reinforcements and withdrawals` (`Relief barges arrive` turn 3 from `North holding box` with `f-fresh-crew`; `Hired pilot leaves` turn 5 to `North holding box`), `### The last turn and victory` (`lastTurn` 8; `The Northern League holds the river` when `North ports >= 4`, checked in North Supply); the validator texts in `### What the Problems list says`), `docs/wiki/Formula-Reference.md` (`### segment()`; `segment()` in `## What a phase condition may read`; victory conditions read as phase conditions), `docs/wiki/Window-Phases.md` (`### Another round or continue`, `### Reacting sides`, `### Due list`, `### Game over`), `docs/wiki/Window-Rules-Workbench.md` (the new controls and the two tabs), `docs/wiki/Playing-With-Others.md` (reacting sides online and in hot-seat: Current Player), `docs/wiki/Menus-and-Windows.md` (the Due section and the question), and `Testing a pack outside the engine (stable)` (`Advance(repeat)`, `GameOver`).
- Modify: `docs/superpowers/RESUME.md`: new CURRENT STATE for B5, B4's moved under Previous; downstream note (neutral wording "downstream pack": it can retire large-count exchange blocks, hand-tracked reinforcement lists and manual last-turn checks; nothing in it changes bytes; grep for `IActionPort` and `IPhaseUi` implementations, which get default members).
- Test: Docs `FormulaReferenceTests` (every `FunctionNames` entry documented: covers `segment`), `WikiLintTests`; add `Wiki_turn_structure_strings` (the question, `Reacting:`, `Due now`, `Game over:` texts appear in the wiki).
- [ ] **Step 1: Failing tests** (Docs `--filter "FullyQualifiedName~FormulaReference|FullyQualifiedName~Wiki"`).
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Write** the wiki and RESUME.
- [ ] **Step 4: Full runs**, one at a time: Rules, Vassal, Core, Render, Desktop, Docs → PASS (flaky: re-run once, report both). Build `src/Quartermaster.App -c Release`.
- [ ] **Step 5: Commit** `docs: B5 turn structure and end of game`
