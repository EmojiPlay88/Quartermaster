# Visibility, Reveals and Sealed Answers (B7) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One visibility rule for every hidden-information decision; Public values of a side's holder seen by every side; side alerts and below-zero lists that follow the viewer's sides; private dice judged by formula id; a finished game seen whole; a Reveal output (exact or a random window); sealed start entries every side answers in secret, applied together; and the parked carries (checklist tick lines, masked result box, derived bindings, choices on the chooser's view, guest Next Phase during another side's answer, schedule zone map resolver).

**Architecture:** Core gains `HiddenValues` (the one source of truth) and routes `PlayerView`, `KeyedValues`, `PieceFilter`, `PropertyValues` and the runner through it; Render's `FormulaMasking` and `AlertBarModel` follow. Rules gains `OutputAction.Reveal` + `FormulaOutput.Margin`, `StartEntry.Sealed`, `askside()` and validator checks. Core's runner records reveals and draws the window when it builds commands; the sealed flow lives in `PhaseController` with a `SealAnswer` command and two `PendingRun` members. Desktop gains two Workbench columns; docs.

**Tech Stack:** C# .NET 10, Avalonia (Desktop), xUnit.

**Spec:** `docs/superpowers/specs/2026-11-26-b7-visibility-design.md`

## Global Constraints

Read `.superpowers/sdd/2026-11-26-b7-visibility/global-constraints.md` (git-ignored; same folder as the ledger) before each task. It holds the invariants and **every exact validator, run-time, log, notice, refusal and Workbench string**; use them verbatim. In short:
- Public repo: invented data only (Riverland: Northern League `north`, Southern Compact `south`). No content from any published game anywhere (code, tests, docs, packs). Expression identifiers use letters, digits and `_` only.
- Old packs keep bytes + SHA-256, `CurrentFormat` 4. Old saves keep bytes. Every new pack, save, command and intent member is omitted when null or default; blank text becomes null on load.
- Additive API only: new record members as body init properties; `IEvalContext.AskSide` is a default method; new overloads, never changed signatures; `OutputAction.Reveal` appended.
- **One source of truth**: whether a viewer may see a value is decided by `HiddenValues` only; never add a second holder/piece/game-over rule.
- Hidden information: guests see hidden inputs masked, the host decides; a viewer never learns a value they cannot see (value, working, result box, choices, refusal text, difference between two answers); game over lifts information hiding, never acting rights. The host checks every guest intent.
- Packs that use no new feature pay nothing measurable (no per-unit work, no extra `PlayerView.For` per run unless the formula has a choices filter or a Piece binding).
- `-c Release`, one test project at a time, headless only, never stash/push, repo-local identity, `Co-Authored-By: Claude <Model> 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Old packs and saves keep bytes**: a hand-written pre-B7 `pack.json` literal (start entries with `askSideId`, outputs with `setMask`/`key`, an alert with a side, private dice) loads and saves byte-equal with a constant SHA-256 (Task 1 `B7_absent_fields_keep_bytes`); a pre-B7 save literal with `rollDice`, `tickChecklist`, `acknowledgeWarning` commands, dice history and a pending ask run round-trips byte-equal (Task 5 `Save_without_b7_members_keeps_bytes`).
2. **One rule**: every caller listed in the spec calls `HiddenValues`; grep finds no other `Ownership.SideOfHolder(...)` visibility test outside `HiddenValues`, `ModuleSession.CanEditLedgerFor` and `IntentExecutor.GuestRule` (acting rules) (Tasks 2, 3).
3. **No leak**: the other side never sees a Private holder value, a ticked item of another side's phase, a sealed answer before release, a revealed value before Apply, a result built from a hidden input; a guest's Preview of a value that a hidden filter would exclude answers exactly as for a listed value (Tasks 2-5, 8, 9).
4. **Game over**: every view is the full state while `GameOver` is set and filtered again after Undo; no guest gains an acting right (Task 2 `Game_over_view_is_full`, Task 3 `Game_over_guest_still_refused_other_side_unit`).
5. **Sealed flow**: two sides answer in either order; the second answer releases both runs in side order in one action; one Undo takes back the second answer and the release; leaving early discards with the exact line (Task 9).

---

### Task 1: Rules — Reveal, Margin, Sealed, askside(), validator, bytes test — model: sonnet

**Files:**
- Modify: `src/Quartermaster.Rules/Model/FormulaDef.cs` (`OutputAction.Reveal` appended after `SetMask`, enum doc comment names it; `FormulaOutput.Margin : string?`, `WhenWritingNull`, doc comment as the spec), `src/Quartermaster.Rules/Model/PhaseDefs.cs` (`StartEntry.Sealed : bool`, `WhenWritingDefault`).
- Modify: `src/Quartermaster.Rules/Io/RulesPackIo.cs`: `Normalise` blanks `Margin` (inside the existing output `with` block).
- Modify: `src/Quartermaster.Rules/Expr/Parser.cs` (arity `["askside"] = (0, 0)` after `"keyed"`), `src/Quartermaster.Rules/Expr/Evaluator.cs` (`"askside"` appended to `FunctionNames`; `EvalCall` → `ctx.AskSide()`; `IEvalContext.AskSide() => Value.Of("")` default method).
- Modify: `src/Quartermaster.Rules/Validation/FormulaVariables.cs`: `ExpressionSources` and `OutputSources` yield `o.Margin`.
- Modify: `src/Quartermaster.Rules/Validation/RulesValidator.cs`: new private `CheckRevealOutput` (called from the output check; Reveal skips the Write-only value/op checks and runs the property/holder/kept-by checks), `CheckSealedEntry` (called from `CheckRunEntries` with `kind`), `FilterHiddenWarnings` (Piece `PieceFilter` and Input `ChoicesFilter`; only with `pack.Sides.Count >= 2`); the schedule zone-map check through `MapRefs.Find(_module.Maps, row.ZoneMapId)` with a module, else `knownMapIds.Contains(row.ZoneMapId.Trim())`. Every message verbatim from the constraints file.
- Test: `tests/Quartermaster.Rules.Tests/B7ModelTests.cs`, `tests/Quartermaster.Rules.Tests/B7ValidatorTests.cs`.
- [ ] **Step 1: Failing tests**:
  - Model/IO: `B7_absent_fields_keep_bytes` (hand-written format-4 pack literal with an `askSideId` start entry, a `setMask` output, a keyed output with `key`, an alert with `sideId`, a `privateRolls` formula, no B7 fields; `Replace("\r\n", "\n")`; byte-equal + constant SHA-256 computed once on the pre-B7 code); `Reveal_and_margin_round_trip_camelCase` (`"action": "reveal"`, `"margin"`); `Sealed_round_trip_and_false_omitted`; `Blank_margin_becomes_null`.
  - Language: `Askside_parses_and_arity` (`askside(1)` arity error); `Askside_default_context_empty`; `Askside_in_function_names`.
  - Validator: one test per Reveal message (8) and per sealed message (8: 6 errors, 2 warnings); `Reveal_property_checks_as_write` (unknown property, holder not kept); `Reveal_with_key_reports_existing_key_text`; `Margin_constant_folds`; `Filter_warning_choices_reads_private`; `Filter_warning_piece_reads_sided_holder`; `Filter_warning_keyed_holder_argument`; `No_filter_warning_single_side`; `No_filter_warning_public_reads`; `Schedule_zone_map_by_name_valid_with_module`; `Schedule_zone_map_with_spaces_valid`; `Schedule_zone_map_unknown_with_module_error`; `Valid_b7_pack_has_no_issues`; `Old_fixture_issue_list_unchanged` (an existing fixture pack: same issues as at `1f69dd6`).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~B7"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, the full Rules.Tests → PASS. Build Core, Render, Desktop, App (`-c Release`).
- [ ] **Step 5: Commit** `feat: B7 reveal output, sealed entries, askside(), validator`

### Task 2: Core — HiddenValues, Public holder values (#74a), game over reveal (#73) — model: opus

**Files:**
- Create: `src/Quartermaster.Core/HiddenValues.cs`: `SeesEverything(GameState? full, RulesPack? pack, IReadOnlySet<string>? held)`, `HolderValueHidden(RulesPack, string holderId, PropertyDef?, Func<string, bool> holds)`, `PieceValueHidden(RulesPack, PropertyDef?, string? pieceSide, Func<string, bool> holds)`, exactly the order of checks in the spec; class doc comment states it is the one source of truth and lists its callers.
- Modify: `src/Quartermaster.Core/PlayerView.cs`: `For` returns `full.DeepClone()` when `SeesEverything` (replaces the every-side shortcut); `Filter.ValueHidden` holder and piece branches through `HiddenValues` (removed/masked/deleted-piece checks stay); `WholeKeyHidden` holder branch: unknown holder only; class doc comment: the holder rule (#74a) and game over (#73).
- Modify: `src/Quartermaster.Core/KeyedValues.cs` (`HiddenInView` through `HiddenValues`; `HiddenFor` uses `SeesEverything`), `src/Quartermaster.Core/PieceFilter.cs` (`HolderKeyValue`: `MissingValue` when the value is missing from a view and `HolderValueHidden`), `src/Quartermaster.Core/PropertyValues.cs` (`HiddenInView` holder branch), `src/Quartermaster.Core/FormulaRunner.cs` (Piece binding view check through `PieceValueHidden`).
- Do not modify `PieceAccess`, `ModuleSession.CanEditLedgerFor`, `IntentExecutor.GuestRule` (acting rules).
- Test: `tests/Quartermaster.Core.Tests/HiddenValuesTests.cs`, `tests/Quartermaster.Core.Tests/B7PlayerViewTests.cs`.
- [ ] **Step 1: Failing tests**: `SeesEverything_solo_all_sides_and_game_over`; `Holder_public_property_of_other_side_visible`; `Holder_private_property_of_other_side_hidden`; `Holder_unknown_hidden_global_visible`; `Private_place_hidden_everywhere`; `Piece_rules_unchanged` (table of the old cases); `View_keeps_public_value_of_other_side_holder` (value, history entry, manual log line kept; formula line still redacted); `View_drops_private_value_of_other_side_holder`; `Holder_read_in_view_private_missing_not_default` (`holder(NorthDepot, Reserve)` in a view → `MissingValue`, never the Default, while `Stores` is in the view); `Keyed_holder_public_visible`; `Game_over_view_is_full` (`IsPlayerView` false, pending ask run of another side present); `Undo_out_of_game_over_hides_again`; `HiddenFor_game_over_false`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~HiddenValues|FullyQualifiedName~B7PlayerView"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, the full Core.Tests (flaky `PlayerViewTests.Perf_1200Pieces`: re-run once, report both) → PASS. Build Render, Desktop, App.
- [ ] **Step 5: Commit** `feat: one visibility rule; public holder values; game over shows everything`

### Task 3: Render — FormulaMasking through HiddenValues; derived bindings; result box — model: opus

**Files:**
- Modify: `src/Quartermaster.Render/FormulaMasking.cs`: `HiddenVariables` and `OutputShown` return "nothing hidden"/true when `HiddenValues.SeesEverything(state, pack, sides)`; `HolderHidden(holderId, PropertyDef?)` and `UnitHidden` through `HiddenValues` (Holder bindings pass `b.Property`, holder(H, P) reads pass P, keyed() reads with a holder pass the keyed property); final fixpoint: a binding (not Piece) whose `FormulaVariables.Sources` read a hidden variable joins `hidden`; class doc comment updated.
- Modify: `src/Quartermaster.Render/FormulaDialogModel.cs` (`View`: the result box rule of the spec; `HostCalculates` + masked → `HiddenBoxText`).
- Test: `tests/Quartermaster.Render.Tests/B7FormulaMaskingTests.cs`, `tests/Quartermaster.Render.Tests/B7ResultBoxTests.cs`.
- [ ] **Step 1: Failing tests**: `Holder_binding_public_property_of_other_side_not_hidden`; `Holder_binding_private_property_of_other_side_hidden`; `Holder_call_public_property_not_masked`; `Auto_value_reading_hidden_variable_hidden` (two levels: `b = a + 1`, `c = b * 2`, `a` hidden → `b`, `c` hidden); `Piece_binding_not_hidden_by_fixpoint`; `Game_over_nothing_hidden`; `Game_over_guest_still_refused_other_side_unit` (`IntentExecutor`, a FormulaIntent on another side's unit after `EndGame` → `That unit belongs to a side you don't hold.`); `Hotseat_result_hidden_when_output_hidden` (`keyed(Mines, hex()) * 2` written to a Private place: result box `hidden`); `Hotseat_result_shown_when_every_output_visible`; `Hotseat_result_hidden_without_outputs`; `Guest_masked_result_host_calculates`; `Batch_row_result_follows_dialog`; `Solo_result_unchanged`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Render.Tests -c Release --filter "FullyQualifiedName~B7FormulaMasking|FullyQualifiedName~B7ResultBox"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, Render `--filter "FullyQualifiedName~Masking|FullyQualifiedName~FormulaDialog|FullyQualifiedName~Batch|FullyQualifiedName~IntentExecutor"`, then the full Render.Tests (flaky list: re-run once, report both) → PASS. Build Desktop, App.
- [ ] **Step 5: Commit** `feat: masking through the one visibility rule; derived bindings and result box masked`

### Task 4: Core/Render — choices and candidates judged on the chooser's view — model: opus

**Files:**
- Modify: `src/Quartermaster.Core/FormulaRunner.cs`: `RunContext` lazy chooser view (`PlayerView.For(s, pack, model, r.PickSides)`, only when `r.PickSides != null && !s.IsPlayerView`, built at most once per run, shared by `Input` choices and `Pick`); `Input` evaluates `InputChoices.Filter` on that view with the view's run piece (missing → `No choice for this piece`); class/method doc comments.
- Modify: `src/Quartermaster.Core/PieceChoices.cs`: internal `Named(GameState shown, bool isView, …)` overload used by `Pick` with the shared view (the public overloads unchanged; `ViewBuilds` still counts real builds).
- Test: `tests/Quartermaster.Core.Tests/ChooserViewTests.cs`, `tests/Quartermaster.Render.Tests/ChoiceOracleTests.cs`.
- [ ] **Step 1: Failing tests**: Core `Hotseat_choice_excluded_only_by_hidden_value_accepted` (choices filter `keyed(Mines, row()) == 0` on a Private place; typed row the full state would exclude → no `Not one of the choices for this piece`); `Hotseat_choice_not_listed_refused`; `Solo_choices_filter_on_full_state`; `Hook_run_choices_unchanged`; `One_view_per_run` (`PieceChoices.ViewBuilds` = 1 with a Piece binding and a choices filter); `No_view_without_filter_or_pick` (0 builds); Render `Guest_preview_hidden_excluded_choice_same_as_listed` (two Preview intents, one value the hidden filter excludes, one it keeps: identical verdict states and status); `Guest_apply_hidden_excluded_choice_applies`; `Guest_unlisted_choice_not_one_of_choices`; `Guest_pick_holder_filter_same_list_as_view` (Piece filter `holder(SouthDepot, Reserve) > 0`); `Guest_pick_keyed_filter_same_list_as_view`; `Guest_pick_label_not_in_own_list_unknown` (`Choose one of the listed pieces`).
- [ ] **Step 2: Run** Core `--filter "FullyQualifiedName~ChooserView"`, Render `--filter "FullyQualifiedName~ChoiceOracle"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, Core `--filter "FullyQualifiedName~Choices|FullyQualifiedName~Runner"`, the full Core.Tests and Render.Tests → PASS. Build Desktop, App.
- [ ] **Step 5: Commit** `fix: choices and piece candidates judged on the chooser's view`

### Task 5: Core/Render — private dice by formula id (#74d); stamped, rendered tick lines — model: sonnet

**Files:**
- Modify: `src/Quartermaster.Core/Commands.cs` (`RollDice.FormulaId`, `TickChecklist.SideId`, `AcknowledgeWarning.SideId`: body init, `WhenWritingNull`), `src/Quartermaster.Core/Ledger.cs` (`DiceEntry.FormulaId`, body init, `WhenWritingNull`), `src/Quartermaster.Core/GameState.cs` (reducer copies `FormulaId` to the dice entry; tick and acknowledge lines pass `sideId`), `src/Quartermaster.Core/GameLog.cs` (`LogBuilder.Dice` passes `formulaId`), `src/Quartermaster.Core/FormulaRunner.cs` (`Commands` sets `FormulaId = t.Formula.Id` on each `RollDice`).
- Modify: `src/Quartermaster.Core/PlayerView.cs`: `IsPrivateRoll` by formula id (unknown id → private), prefix rule only without one; `FilterDice` pairing also requires equal ids when the entry has one; `Decide`: a `Turn` line with a side stamp kept only for a viewer holding it, else dropped; doc comment items.
- Modify: `src/Quartermaster.Render/PhaseController.cs`: `Tick` logs the rendered text (`ChecklistText.Render` with the phase-side predicate) and sets `SideId`; `Acknowledge` sets `SideId`.
- Check (no edit expected): every reader of `LogEntry.FormulaId` reads it only for `Calc`/`Formula` lines; report any other.
- Test: `tests/Quartermaster.Core.Tests/B7LogTests.cs`, `tests/Quartermaster.Render.Tests/B7TickTests.cs`.
- [ ] **Step 1: Failing tests**: `Save_without_b7_members_keeps_bytes` (hand-written pre-B7 save literal from the `1f69dd6` serializer: a `rollDice` and a `tickChecklist` and an `acknowledgeWarning` command, dice history, a pending ask run; round-trip equal); `Formula_roll_carries_formula_id`; `Private_dice_by_id_same_name_public_formula_shown` (two formulas named `Scout`, one private); `Private_dice_renamed_formula_still_private`; `Private_dice_unknown_formula_id_hidden`; `Old_log_prefix_rule_kept`; `Dice_tool_roll_unchanged`; `Tick_line_stamped_hidden_from_other_side`; `Tick_line_side_less_phase_public`; `Ack_line_stamped_hidden_from_other_side`; `Old_unstamped_tick_line_kept`; Render `Tick_renders_links_same_side`; `Tick_keeps_link_to_other_side_phase_raw`; `Hotseat_other_player_log_has_no_tick_text`.
- [ ] **Step 2: Run** Core `--filter "FullyQualifiedName~B7Log"`, Render `--filter "FullyQualifiedName~B7Tick"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, the full Core.Tests, Render `--filter "FullyQualifiedName~PhaseController|FullyQualifiedName~Checklist|FullyQualifiedName~Dice"`, then the full Render.Tests → PASS. Build Desktop, App.
- [ ] **Step 5: Commit** `fix: private dice by formula id; checklist lines belong to the phase's side`

### Task 6: Core/Render — side alerts and own-only lists (#74b, #74c) — model: sonnet

**Files:**
- Modify: `src/Quartermaster.Core/Alerts.cs`: 6-argument `Evaluate` (viewer sides); the 5-argument overload keeps its behaviour verbatim; `Negative` lists only own counters and holders; `Warnings` chip only for the phase's side (or no side); `User` side alerts by viewer sides in every phase. Doc comments.
- Modify: `src/Quartermaster.Render/AlertBarModel.cs`: `Recompute` passes `FormulaMasking.ViewerSides(s)`, null when `HiddenValues.SeesEverything(...)`.
- Test: `tests/Quartermaster.Core.Tests/B7AlertTests.cs`, `tests/Quartermaster.Render.Tests/B7AlertBarTests.cs`.
- [ ] **Step 1: Failing tests**: `Side_alert_shown_to_holder_in_other_side_phase`; `Side_alert_hidden_from_non_holder_in_its_own_phase`; `Side_alert_solo_every_phase`; `Five_argument_overload_unchanged` (the existing cases); `Negative_lists_only_own_pieces`; `Negative_lists_only_own_and_unsided_holders_and_global`; `Negative_skips_masked_placeholder`; `Negative_no_chip_when_list_empty`; `Warning_chip_only_for_phase_side`; `Warning_chip_side_less_phase_all`; Render `Hotseat_alert_bar_follows_view_sides`; `Guest_alert_bar_uses_guest_sides`; `Game_over_alert_bar_all`.
- [ ] **Step 2: Run** Core `--filter "FullyQualifiedName~B7Alert"`, Render `--filter "FullyQualifiedName~B7AlertBar"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, Core `--filter "FullyQualifiedName~Alert"`, Render `--filter "FullyQualifiedName~Alert"`, then the full Core.Tests and Render.Tests → PASS. Build Desktop, App.
- [ ] **Step 5: Commit** `feat: side alerts follow the viewer's sides; own-only below-zero list`

### Task 7: Render/Core — guest Next Phase during another side's answer; schedule zone map resolver — model: sonnet

**Files:**
- Modify: `src/Quartermaster.Render/PhaseController.cs`: new `public static PendingRun? OpenAskFor(GameState st, IReadOnlySet<string> sides)` (first pending run with an `AskSideId` not in `sides` and an unapplied unit); `View()`'s hot-seat "is playing" branch shows only when the viewer has no open ask run of a held side and `OpenAskFor` finds one.
- Modify: `src/Quartermaster.Render/Online/IntentExecutor.cs`: `NextPhase` refuses after `PhaseGate` with the `PhaseGate` text for the asked side.
- Modify: `src/Quartermaster.Core/ScheduleDue.cs`: `ZoneMapIn` delegates to `MapRefs.Find` (doc comment).
- Test: `tests/Quartermaster.Render.Tests/B7NextPhaseGateTests.cs`, `tests/Quartermaster.Core.Tests/ScheduleZoneMapTests.cs`.
- [ ] **Step 1: Failing tests**: `Guest_next_phase_refused_while_other_side_answers` (exact text `Northern League is playing Dawn; wait for your phase.`); `Guest_next_phase_allowed_when_own_side_answers`; `Guest_next_phase_allowed_without_ask_run`; `Host_press_unchanged`; `Hotseat_playing_text_not_shown_when_viewer_has_own_open_ask`; `Zone_map_by_name_and_spaces_resolves` (Core, `ScheduleDue.ZoneMap`); `Zone_map_id_wins_over_name`.
- [ ] **Step 2: Run** Render `--filter "FullyQualifiedName~B7NextPhaseGate"`, Core `--filter "FullyQualifiedName~ScheduleZoneMap"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, Render `--filter "FullyQualifiedName~IntentExecutor|FullyQualifiedName~PhaseController|FullyQualifiedName~Due"`, the full Core.Tests and Render.Tests → PASS. Build Desktop, App.
- [ ] **Step 5: Commit** `fix: guest Next Phase waits for another side's answer; one zone map resolver`

### Task 8: Core/Render — Reveal output runtime (#47) — model: opus

**Files:**
- Modify: `src/Quartermaster.Core/FormulaRunner.cs`: `RevealPreview` record; `FormulaTrace.Reveals` (body init, empty default); Reveal outputs read in output order (after earlier Writes to the same key/property), unknown on a view (no error), Margin evaluated and checked (`The margin for {Prop} must be a whole number from 0 to 1000`), working part `reveal {Prop}`, no `OutputPreview`; `Commands` appends one `AddLogLine(LogKind.System, …)` per reveal with a known value after the effects' System lines and before removals/moves, the window drawn with the runner's roller (spec formula, shift to 0); doc comments of `Commands` (order) and the trace.
- Modify: `src/Quartermaster.Render/Online/IntentExecutor.cs`: `GuestRule` skips Reveal outputs.
- Modify: `src/Quartermaster.Render/FormulaEdit.cs`: `WithMargin`; `WithOutputAction` keeps `Property` for Reveal, `Margin` only for Reveal.
- Test: `tests/Quartermaster.Core.Tests/RevealTests.cs`, `tests/Quartermaster.Render.Tests/RevealIntentTests.cs`.
- [ ] **Step 1: Failing tests**: `Reveal_exact_logs_value_with_label` (a labelled value: `{subject}: Strength 3 (Steady)` with the fixture's labels table); `Reveal_window_contains_value` (roller fixed: margin 2, value 5, roll 2 → `between 4 and 6`); `Reveal_window_shifted_at_zero` (value 0 → `between 0 and 2`); `Reveal_reads_after_earlier_write`; `Reveal_piece_target_uses_shown_name` (masked placeholder → `Unknown piece: …`); `Reveal_holder_and_global_subjects`; `Reveal_margin_error_fails_run`; `Reveal_logs_no_dice_line`; `Reveal_working_shows_no_value`; `Reveal_on_view_unknown_no_error`; `Reveal_line_kept_for_other_side_view`; `Reveal_line_dropped_when_subject_hidden_from_viewer`; `Undo_removes_reveal_line`; Render `Guest_reveal_other_side_holder_allowed`; `Guest_preview_carries_no_reveal_value`; `WithOutputAction_reveal_keeps_property_drops_key`.
- [ ] **Step 2: Run** Core `--filter "FullyQualifiedName~Reveal"`, Render `--filter "FullyQualifiedName~Reveal"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, the full Core.Tests and Render.Tests → PASS. Build Desktop, App.
- [ ] **Step 5: Commit** `feat: reveal output (exact value or a random window)`

### Task 9: Core/Render — sealed answers (#53) and askside() — model: opus

**Files:**
- Modify: `src/Quartermaster.Core/Phases.cs` (`PendingRun.Seal`, `PendingRun.SealedAnswer`: body init, `WhenWritingNull`), `src/Quartermaster.Core/Commands.cs` (`SealAnswer` + `[JsonDerivedType(typeof(SealAnswer), "sealAnswer")]` appended), `src/Quartermaster.Core/GameState.cs` (reducer: set the answer on the run, Turn line `LogText`), `src/Quartermaster.Core/Decisions.cs` (answered sealed runs not listed), `src/Quartermaster.Core/PlayerView.cs` (doc comment: a sealed answer follows the ask-run rule).
- Modify: `src/Quartermaster.Core/FormulaRunner.cs` (`RunRequest.AskSideId`; `RunContext.AskSide()` = that side's name, the id when blank; `PieceFilter` contexts and `PhaseContext` keep the default `""`).
- Modify: `src/Quartermaster.Render/CommandLog.cs` (`Stamp` handles `SealAnswer`), `src/Quartermaster.Render/FormulaDialogModel.cs` (`AskSideId` passed into its `RunRequest`), `src/Quartermaster.Render/PhaseController.cs` (queue one run per side for a sealed start entry; `ApplyFormula` on a sealed run → `SealAnswer` + notice; refusal when already answered; release in the completing action; discard line before `AdvancePhase`; `OpenAskSide`/`OpenAskFor` skip answered sealed runs; every dialog model for a pending run gets `AskSideId`), `src/Quartermaster.Render/Online/IntentExecutor.cs` (`FormulaIntentRunner.Prepare`: `AskSideId` on the model; `Your sealed answer is already in.`).
- Modify: Desktop open path of a pending run's dialog (find with `grep -rn "new FormulaDialogModel(" src/Quartermaster.Desktop`) sets `AskSideId`.
- Test: `tests/Quartermaster.Core.Tests/SealedStateTests.cs`, `tests/Quartermaster.Render.Tests/SealedFlowTests.cs`.
- [ ] **Step 1: Failing tests**: Core `SealAnswer_sets_answer_and_logs_public_line`; `Sealed_answer_in_own_view_only`; `Sealed_answer_in_every_view_at_game_over`; `Answered_sealed_run_not_a_decision`; `Askside_in_run_is_side_name`; `Askside_outside_ask_run_empty`; Render `Sealed_entry_queues_one_run_per_side_in_pack_order`; `First_answer_stores_not_applies` (no ledger change, notice text); `Second_answer_releases_both_in_side_order` (keyed `TollBid` per side; `{formula}: every side has answered` line first); `Release_one_undo_takes_back_answer_and_release`; `Release_failure_logs_side_error_and_continues`; `Second_answer_refused_already_in` (host dialog and guest intent); `Guest_answers_sealed_run`; `Guest_cannot_answer_other_side_sealed_run`; `Leaving_early_discards_with_line`; `Leaving_with_no_answer_no_line`; `Start_cursor_waits_for_release`; `Hotseat_other_player_cannot_see_answer`; `Hotseat_playing_text_after_own_answer`; `Fully_auto_still_asks`; `Solo_answers_each_side`.
- [ ] **Step 2: Run** Core `--filter "FullyQualifiedName~Sealed"`, Render `--filter "FullyQualifiedName~Sealed"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, Render `--filter "FullyQualifiedName~PhaseController|FullyQualifiedName~PhasePipeline|FullyQualifiedName~IntentExecutor"`, the full Core.Tests and Render.Tests → PASS. Build Desktop, App.
- [ ] **Step 5: Commit** `feat: sealed start entries answered in secret and applied together; askside()`

### Task 10: Desktop — Workbench columns and alert side text — model: sonnet

**Files:**
- Modify: `src/Quartermaster.Desktop/Workbench/FormulasTab.Grids.cs` (outputs `Margin` column, weight 70, after `Key`; commit through `FormulaEdit.WithMargin`; header tooltip verbatim; the cell is read-only/blank unless the action is Reveal, as `Key` is unless Write), `src/Quartermaster.Desktop/Workbench/PhasesTab.cs` (start grid `Sealed` check column, weight 40, after `Asks side`; `OnStartCell` case 4; tooltip verbatim; end grid unchanged), `src/Quartermaster.Desktop/Workbench/AlertsTab.cs` (Side column tooltip verbatim).
- Modify: `src/Quartermaster.App/*`: build only.
- Test: `tests/Quartermaster.Desktop.Tests/B7WorkbenchTests.cs` (headless).
- [ ] **Step 1: Failing tests**: `Outputs_margin_column_commits`; `Margin_blank_becomes_null`; `Reveal_action_keeps_property_cell`; `Start_grid_sealed_check_commits`; `End_grid_has_no_sealed_column`; `Alert_side_tooltip_text`; `Problems_list_shows_sealed_error`; `Old_pack_columns_unchanged` (column headers of the outputs and start grids for a pre-B7 pack, plus the two new ones at their places).
- [ ] **Step 2: Run** Desktop `--filter "FullyQualifiedName~B7Workbench"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, Desktop `--filter "FullyQualifiedName~Workbench|FullyQualifiedName~FormulasTab|FullyQualifiedName~PhasesTab"`, the full Desktop.Tests (flaky list: re-run once, report both) → PASS. Build App.
- [ ] **Step 5: Commit** `feat: B7 Workbench editors`

### Task 11: Wiki, RESUME, full runs — model: sonnet

**Files:**
- Modify: `docs/wiki/Making-a-Rules-Pack.md` (`## Visibility, reveals and sealed answers` with the six subsections and examples of the spec; every validator text), `docs/wiki/Formula-Reference.md` (`### askside()`; Reveal output and Margin; the margin error row), `docs/wiki/Window-Rules-Workbench.md` (Margin, Sealed, alert Side row text replaced: `Limits the alert to one side's units; only players holding that side see it, in every phase`), `docs/wiki/Window-Phases.md` (`### Sealed answers`), `docs/wiki/Window-Log-and-Panels.md` (tick lines, reveal lines), `docs/wiki/Playing-With-Others.md` (Public holder values, side alerts, sealed answers, game over), `docs/wiki/Window-Formula-Dialog.md` (result box `hidden`). Invented Riverland examples only; identifiers without spaces in expressions.
- Modify: `docs/superpowers/RESUME.md`: a new CURRENT STATE section for B7 (B5/B6 moved under Previous); downstream note in neutral wording ("downstream pack": Public holder values now show to other sides, side alerts follow the viewer, private dice by id; nothing changes bytes; `AlertEngine.Evaluate` has a 6-argument overload).
- Test: Docs `FormulaReferenceTests` (pins `askside`), `WikiLintTests`.
- [ ] **Step 1: Failing tests**: run Docs `--filter "FullyQualifiedName~FormulaReference"` → FAIL (no `### askside()` yet).
- [ ] **Step 2: Write** the wiki and RESUME.
- [ ] **Step 3: Full runs**, one at a time: Rules, Vassal, Core, Render, Desktop, Docs, Net → PASS (flaky: re-run once, report both). Build `src/Quartermaster.App -c Release`.
- [ ] **Step 4: Commit** `docs: B7 visibility, reveals and sealed answers`
