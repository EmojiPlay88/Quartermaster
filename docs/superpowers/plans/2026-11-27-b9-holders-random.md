# Holders at Run Time and Random Draws (B9) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A holder's owner can change in play (`HolderDef.OwnerExpr`, worked out from Public values), and every visibility and acting rule follows the current owner. A Holder binding can let the player choose the holder when the formula runs (`HolderChoices`, judged on the chooser's held set), and outputs can write the chosen holder (`HolderVariable`). A new Draw binding draws k members at random from a weighted pool of counters with host-rolled, pinned dice, and Piece outputs act on each drawn counter by its share, logged per counter to its owner.

**Architecture:** Rules gains the five pack members, `BindingKind.Draw` and the validator texts. Core gains `HolderOwners` (owner from the state), state-aware overloads of `Ownership.SideOfHolder` / `HiddenValues.HolderValueHidden` that every caller moves to, `HolderChoices` (picker candidates), and the runner's holder pick and draw (pool, one die per step, per-piece outputs, log lines). Render gains the dropdown, the draw box, masking marker, pins of draw keys, batch-grid columns and host checks. Desktop gains the Workbench columns; docs.

**Tech Stack:** C# .NET 10, Avalonia (Desktop), xUnit.

**Spec:** `docs/superpowers/specs/2026-11-27-b9-holders-random-design.md`

## Before Task 1: accept the defaults, baseline

The controller reads every `DECISION (default)` of the spec and confirms or changes it (the user's batch autonomy rules apply) before Task 1; a changed default is written into the spec and the constraints file first. The branch starts at master `048a67c` (B8 merged); no merge is needed. Run the Rules, Core and Render test projects once as a baseline and record the counts in the ledger (`.superpowers/sdd/2026-11-27-b9-holders-random/`).

## Global Constraints

Read `.superpowers/sdd/2026-11-27-b9-holders-random/global-constraints.md` (git-ignored; same folder as the ledger) before each task. It holds the invariants and **every exact validator, runtime, Workbench and dialog string**; use them verbatim. In short:
- Public repo: invented data only (Riverland: Northern League `north`, Southern Compact `south`; holders `Ford Dump`, `Mill Dump`, `North Depot`, `South Depot`; Public `DumpOwner` keyed by table `Dumps`; Private `Stores`; barges `SC 1 Barge`..`SC 4 Barge` with Private `Boats`). No content from any published game anywhere (code, tests, docs, packs): no rule numbers, rule text, chart values or unit names. Expression identifiers use letters, digits and `_` only.
- Old packs keep bytes + SHA-256, `CurrentFormat` 4. Old saves keep bytes: B9 adds no save, command or intent member. Every new pack member is omitted when null; blank text becomes null on load; an empty `HolderChoices` becomes null.
- Additive API only: init properties; `BindingKind.Draw` appended; new classes `HolderOwners`, `HolderChoices`, `HolderChoice`, `DrawPreview`; new overloads with distinct parameter lists (`Ownership.SideOfHolder(string, RulesPack, GameState?)`, `HiddenValues.HolderValueHidden(GameState?, RulesPack, ...)`); no changed signatures.
- **One owner function**: every holder-owner question in `src` goes through `Ownership.SideOfHolder(id, pack, state)` / `Ownership.HolderSides(state, pack)`; no rule reads `HolderDef.SideId` or `OwnerExpr` itself (only `HolderOwners`, `SideFacts`, the validator, `VisibilityEdit` and the Workbench).
- An owner expression reads only Public values (validator) and is evaluated straight from the state, so the full state and every view give the same owner; it never calls `HiddenValues`.
- **Validator parses every new expression** (`OwnerExpr`, `DrawCount`) and checks every new reference (holder ids, Holder/Draw variables, weight property).
- Hidden information: guests see hidden inputs masked; the host decides and checks every guest intent (holder labels against the sender's own list; draws only on the host). Nothing of which counters were drawn is shown before Apply; the per-counter draw lines are stamped with the counter's side.
- Determinism: draw dice go through the run's roll path (`FixedRolls`, `RunRequest.Dice`, the roller) under keys `draw:{Var}#{i}`; previews and Apply agree; replay re-executes commands (no re-draw); one run = one undo step.
- Hot-seat and online are both covered by tests in every task that changes behaviour; turn files where state crosses machines.
- Packs without the features pay nothing (no owner evaluation: `HolderOwners.Evaluations` stays 0; no draw code).
- Every new field gets a Workbench editor (Avalonia Desktop; WinForms App builds only) and a wiki section with invented examples.
- `-c Release`, one test project at a time, headless only, never stash/push, repo-local identity, commits end with `Co-Authored-By: Claude <Model> 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Old packs and saves keep bytes**: a hand-written pre-B9 `pack.json` literal (two sides, a holder with a side, a Holder binding with a fixed holder, a Piece binding with a filter and same hex, a Piece output, a Holder output) loads and saves byte-equal with a constant SHA-256 (Task 1 `B9_absent_fields_keep_bytes`); an existing fixture save loads and saves byte-equal (Task 2 `Save_bytes_unchanged_with_owner_expr_pack`).
2. **One owner function**: `grep -rn "SideOfHolder(\|HolderValueHidden(\|\.SideId" src --include=*.cs` — every holder-owner read passes a state (Task 2); `grep -rn "OwnerExpr" src` finds only `HolderOwners`, `RulesPackIo`, `RulesValidator`, `VisibilityEdit` and the Workbench.
3. **No leak**: a side that does not own a holder (now) sees none of its Private values, in hot-seat, online snapshots, Preview verdicts and turn files; a capture flips that at once and undo flips it back; the split of a draw is never shown before Apply and never to a side not holding the drawn counter (Tasks 2, 3, 5).
4. **Determinism**: a draw previewed, previewed again and applied gives one split; replay of the saved log gives the same state without re-drawing; undo/redo of a draw run is one step; a menu batch's live Apply equals its chain (Tasks 4, 5).
5. **Nothing for packs without the features**: `HolderOwners.Evaluations` stays 0 for a pack without owner expressions across a full `PlayerView.For`; formulas without a Draw binding take the old path (Tasks 2, 4).

---

### Task 1: Rules — model fields, Normalise, validator, bytes test — model: sonnet

**Files:**
- Modify: `src/Quartermaster.Rules/Model/RulesPack.cs`: `HolderDef.OwnerExpr : string?` (`WhenWritingNull`, doc comment as the spec).
- Modify: `src/Quartermaster.Rules/Model/FormulaDef.cs`: `BindingKind.Draw` appended after `Piece`; `VariableBinding.HolderChoices : List<string>?`, `VariableBinding.DrawCount : string?`; `FormulaOutput.HolderVariable : string?` (all `WhenWritingNull`; doc comments as the spec).
- Modify: `src/Quartermaster.Rules/Io/RulesPackIo.cs`: `Normalise` — `OwnerExpr`, `DrawCount`, `HolderVariable` blank → null (trimmed as the neighbours are); `HolderChoices` trimmed, blanks and duplicates (ordinal) dropped, empty → null.
- Modify: `src/Quartermaster.Rules/Validation/RulesValidator.cs`: a holder check for `OwnerExpr` (parse, Public-only reads of this holder's or global properties, forbidden functions incl. holder() and piece aggregates, constant folding to a side, no sides); binding checks for `HolderChoices` and Draw (weight/pool as the Piece binding's checks, count parse/`FilterRefProblems`/self read, count on a non-Draw); output checks for `HolderVariable` and for Place/Reveal on a Draw variable; B3's Piece-variable check accepts a Draw variable; the "no output acts on the drawn counters" warning; the sealed-entry draw error next to B7's dice error; B7's filter warning counts a holder with an `OwnerExpr` as sided. Every message verbatim from the constraints file.
- Modify: the Rules helper that lists a binding's expressions (`FormulaVariables.Sources`): include `DrawCount`.
- Test: `tests/Quartermaster.Rules.Tests/B9ModelTests.cs`, `tests/Quartermaster.Rules.Tests/B9ValidatorTests.cs`.
- [ ] **Step 1: Failing tests**:
  - Model/IO: `B9_absent_fields_keep_bytes` (hand-written format-4 literal of Review Focus 1, `Replace("\r\n", "\n")`, byte-equal + constant SHA-256 computed once on the pre-B9 code); `Owner_expr_round_trips_camelCase` (`"ownerExpr"`); `Draw_binding_round_trips` (`"kind": "draw"`, `"drawCount"`, `"sameHex"`, `"pieceFilter"`, `"property"`); `Holder_choices_and_holder_variable_round_trip` (`"holderChoices"`, `"holderVariable"`); `Blank_new_fields_become_null` (`"  "` in each text field, `[" ", ""]` choices); `Holder_choices_trimmed_and_deduplicated`.
  - Validator: one test per message of the constraints file (owner expression 6, holder choices 9, draws 7), incl. `Owner_expr_reading_private_property_error`, `Owner_expr_reading_this_holders_public_property_ok`, `Owner_expr_keyed_global_public_ok`, `Owner_expr_aggregate_refused`, `Owner_expr_constant_naming_no_side_warns` (`"Hill Clans"` in a two-side pack; `3`), `Draw_variable_accepted_as_piece_variable`, `Draw_count_reads_earlier_binding_ok`, `Sealed_formula_draws_error`, `Filter_warning_counts_owner_expr_holder_as_sided` (B7 piece-filter warning with `holder(Ford_Dump, Stores)`, holder with `OwnerExpr` and no `SideId`); `Valid_b9_pack_has_no_issues` (the spec's JSON examples in one pack); `Old_fixture_issue_list_unchanged` (an existing fixture pack: same issues as at `048a67c`).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~B9"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, the full Rules.Tests → PASS. Build Core, Render, Desktop, App (`-c Release`).
- [ ] **Step 5: Commit** `feat: B9 owner expressions, holder choices and draw bindings in the pack; validator`

### Task 2: Core/Render — holder owner from the state, every caller — model: opus

**Files:**
- Create: `src/Quartermaster.Core/HolderOwners.cs`: `HasDynamic`, `Owner(GameState?, RulesPack, HolderDef)`, parse cache per `HolderDef` record, `[ThreadStatic] internal static int Evaluations`; a private `IEvalContext` deriving from the phase-condition context (`PhaseConditions`' `PhaseContext`; make it internal-inheritable if needed, no public change) whose bare names read this holder's kept values, else global values, straight from the state; class doc comment: Public-only, never calls `HiddenValues`, fallback to the fixed owner.
- Modify: `src/Quartermaster.Core/Ownership.cs`: `SideOfHolder(string, RulesPack, GameState?)`, `HolderSides(GameState, RulesPack)` (memo per holder; fixed lookup without `HasDynamic`); the 2-argument doc comment says "the fixed owner; `src` callers pass a state".
- Modify: `src/Quartermaster.Core/HiddenValues.cs`: public `HolderValueHidden(GameState?, RulesPack, string, PropertyDef?, Func<string, bool>)`; the internal memo overload takes a `Func<string, string?>? holderSide`; class doc comment adds "the owner of a holder comes from the state (B9)".
- Modify (move to the state-aware owner; behaviour unchanged without `OwnerExpr`): `src/Quartermaster.Core/Alerts.cs`, `KeyedValues.cs` (`HiddenInView`), `PieceFilter.cs` (`HolderKeyValue`), `PlayerView.cs` (one `HolderSides` per `For`; `Filter` uses it at both holder sites), `PropertyValues.cs`, `FormulaRunner.cs` (output previews of Global/Holder writes), `src/Quartermaster.Render/FormulaMasking.cs` (`HolderHidden` with the session state), `src/Quartermaster.Render/ModuleSession.cs` (`CanEditLedgerFor` with `Game.State`), `src/Quartermaster.Render/Online/IntentExecutor.cs` (`GuestRule` fixed Holder outputs with the state).
- Check (no edit expected; report any other): `grep -rn "SideOfHolder(\|HolderValueHidden(" src --include=*.cs` — only state-aware calls outside `Ownership`/`HiddenValues` themselves; `grep -rn "h.SideId\|holder.SideId\|\.SideId" src/Quartermaster.Core src/Quartermaster.Render --include=*.cs` — no holder `SideId` read outside `Ownership`/`HolderOwners`/`SideFacts`/`VisibilityEdit`.
- Test: `tests/Quartermaster.Core.Tests/HolderOwnersTests.cs`, `tests/Quartermaster.Render.Tests/HolderOwnerHotSeatTests.cs`, `tests/Quartermaster.Render.Tests/HolderOwnerOnlineTests.cs`.
- [ ] **Step 1: Failing tests** (fixture: `north`/`south`; `Ford Dump` with `OwnerExpr = keyed(DumpOwner, "Ford Dump")`, fixed side `north`; `Mill Dump` with `OwnerExpr = Owner` (its own Public holder property); Private `Stores`):
  - Core: `Owner_by_number_and_zero`; `Owner_by_name_and_id_case_insensitive`; `Owner_former_id_reads_listing_side`; `Owner_error_or_unknown_falls_back_to_fixed_side`; `Owner_reads_own_holder_value_then_global`; `Owner_same_on_full_state_and_view` (a `PlayerView.For` of each side gives the full state's owner); `No_owner_expr_never_evaluates` (`HolderOwners.Evaluations` 0 across `PlayerView.For`, `HolderValueHidden`, alerts); `HolderSides_memoises_per_state`; `View_hides_private_values_by_current_owner` (`DumpOwner` 2: `south` view has `Stores` of `Ford Dump`, `north` view lacks it); `Capture_write_flips_visibility_and_undo_flips_back` (a `LedgerDelta` on `DumpOwner`, then `GameSession` undo/redo); `Replay_gives_same_owner` (`NativeSave` write/read); `Below_zero_alert_follows_current_owner`; `Holder_value_hidden_old_overload_unchanged` (fixed owner).
  - Render hot-seat: `Hotseat_ledger_edit_follows_current_owner` (`CanEditLedger`); `Hotseat_handover_after_capture_shows_new_owner_values`; `Formula_masking_follows_current_owner` (a fixed Holder binding on `Ford Dump`: masked for `north` after the capture, not before); `Ally_of_current_owner_sees_values` (B8 alliance: `hills` allied with `south`).
  - Render online: `Guest_snapshot_follows_current_owner`; `Guest_manual_edit_of_captured_holder_refused_old_text` (`That value belongs to a side you don't hold.` as `OtherSide` gives it); `Guest_holder_output_to_captured_holder_refused` (`GuestRule` text); `Guest_holder_output_after_capture_allowed_for_new_owner`; `Host_preview_verdict_masked_by_current_owner`; `Turn_file_view_follows_current_owner` (`PackHarness.ViewFor`); `Save_bytes_unchanged_with_owner_expr_pack` (an existing fixture save opened with the fixture pack: saves byte-equal).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~HolderOwners"`, then Render `--filter "FullyQualifiedName~HolderOwner"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2; Core `--filter "FullyQualifiedName~PlayerView|FullyQualifiedName~HiddenValues|FullyQualifiedName~Keyed|FullyQualifiedName~Alert"`; Render `--filter "FullyQualifiedName~Masking|FullyQualifiedName~IntentExecutor|FullyQualifiedName~HotSeat|FullyQualifiedName~Ledger|FullyQualifiedName~Alliance"`; the full Core.Tests and Render.Tests (flaky `PlayerViewTests.Perf_1200Pieces` and the Render flaky list: re-run once, report both) → PASS. Build Desktop, App.
- [ ] **Step 5: Commit** `feat: holder owners worked out from the game state; visibility and acting rights follow them`

### Task 3: Core/Render — holder picker and Holder-variable outputs — model: opus

**Files:**
- Create: `src/Quartermaster.Core/HolderChoices.cs`: `HolderChoice` record, `For(GameState, RulesPack, VariableBinding, IReadOnlySet<string>?)`, `Unknown`, `None` (texts verbatim); class doc comment: candidates are the listed holders the chooser may act on now.
- Modify: `src/Quartermaster.Core/FormulaRunner.cs`: `BindingKind.Holder` with `HolderChoices` resolves the typed label (memoised per variable, as `Pick`) among `HolderChoices.For(s, pack, b, r.PickSides)`; the always-ask rule includes it; `Target = Holder` outputs with `HolderVariable` (Write and Reveal) write/read the chosen holder (key, subject, `EnsureKept`, hidden preview through the Task 2 overload); `RunRequest.PickSides` doc comment names holder picks.
- Modify: `src/Quartermaster.Render/FormulaDialogModel.cs` (dropdown choices for a holder picker; a typed box always asked), `BatchModel.cs` (`ChoicesFor`, `IsChoiceColumn`), `MenuBatchModel.cs` (choices; `All rows`), `MissingInputs.cs` (always asked), `src/Quartermaster.Render/Online/IntentExecutor.cs` (`FormulaIntentRunner.Prepare` holder pick check with the sender's held set; `GuestRule` skips `HolderVariable` outputs).
- Test: `tests/Quartermaster.Core.Tests/HolderPickTests.cs`, `tests/Quartermaster.Render.Tests/HolderPickDialogTests.cs`, `tests/Quartermaster.Render.Tests/HolderPickOnlineTests.cs`.
- [ ] **Step 1: Failing tests** (Task 2 fixture plus formula `Draw stores`: Holder binding `Dump` over `Ford Dump`, `Mill Dump`, `North Depot`, property `Stores`; Input `Need`; output Holder variable `Dump` Subtract `Need`):
  - Core: `Candidates_are_listed_holders_chooser_may_act_on` (`north` before and after a capture); `Solo_lists_every_listed_holder`; `Duplicate_labels_numbered_in_list_order`; `Blank_waiting_unknown_and_none_texts`; `Picked_holder_value_read_and_working_names_it` (`Stores of Ford Dump`); `Holder_variable_output_writes_chosen_holder` (`LedgerDelta` key `LedgerKey.Holder("h-ford")`); `Holder_variable_reveal_reads_chosen_holder`; `Pick_memoised_once_per_run`; `Fully_automatic_stops_for_holder_pick`; `Undo_takes_back_run_in_one_step`.
  - Render: `Dialog_dropdown_lists_viewer_candidates` (hot-seat: `north` viewer vs `south` viewer); `Hotseat_label_of_other_side_rejected` (`Choose one of the listed holders`); `Batch_grid_holder_column_is_choice_column`; `Menu_batch_all_rows_fills_holder_pick`; `Missing_inputs_asks_holder_pick`.
  - Online: `Guest_dropdown_from_own_view`; `Guest_apply_with_own_holder_applies`; `Guest_label_not_in_own_list_refused` (`Choose one of the listed holders`, for a holder the guest's side lost by capture); `Guest_preview_same_text_whether_label_exists_or_not` (no oracle: a label of another side's holder and an invented label give the same verdict text); `Guest_rule_skips_holder_variable_outputs`.
- [ ] **Step 2: Run** Core `--filter "FullyQualifiedName~HolderPick"`, Render `--filter "FullyQualifiedName~HolderPick"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2; Core `--filter "FullyQualifiedName~FormulaRunner|FullyQualifiedName~PieceChoices"`; Render `--filter "FullyQualifiedName~FormulaDialog|FullyQualifiedName~Batch|FullyQualifiedName~MissingInputs|FullyQualifiedName~IntentExecutor"`; the full Core.Tests and Render.Tests (flaky lists: re-run once, report both) → PASS. Build Desktop, App.
- [ ] **Step 5: Commit** `feat: holders chosen when a formula runs, and outputs that write the chosen holder`

### Task 4: Core — draws in the runner — model: opus

**Files:**
- Modify: `src/Quartermaster.Core/FormulaRunner.cs`: `BindingKind.Draw` resolution (pool on the run's full state: filter, same hex incl. the run piece, weight via `PropertyValues`, order by piece id; count from `DrawCount`; k = min; one die per step under `draw:{Var}#{i}` through `FixedRolls` → `RunRequest.Dice` → roller, out-of-range pins redrawn; never `SharedRolls`); `FormulaTrace.DrawRolls`, `FormulaTrace.Draws`, `DrawPreview`, `OutputPreview.Draw` (body init); outputs with a Draw `PieceVariable` run once per drawn piece with the variable reading that piece's count in the output's value expression; `Commands`: per-piece writes (B3 stamping), then the draw lines `{formula}: {Var}: {n} on {name}` (`PieceId`, `SideId` = the piece's side), then B3's layer/mask lines, reveals, removals; the acting side's working part `{Var} = {k}`; the runtime error texts verbatim; on a player view a pool reading data the view lacks is missing `Hidden from this player`.
- Modify: the runner's dice/count helpers so `count()`, `dice()`, `Rolls` and the dice log never see draw keys.
- Test: `tests/Quartermaster.Core.Tests/DrawTests.cs`, `tests/Quartermaster.Core.Tests/DrawReplayTests.cs`.
- [ ] **Step 1: Failing tests** (fixture: `SC 1 Barge`..`SC 4 Barge` in one hex with `Boats` 3, 1, 0, 2, a `north` battery elsewhere; formula `Shore battery` run on `SC 1 Barge`: Draw `Hits` weight `Boats`, same hex, filter `side() == "Southern Compact"`, count `Shots` (Input); output Piece `Hits` Subtract `Boats` by `Hits`):
  - `Pool_order_by_piece_id_and_zero_weight_skipped`; `Same_hex_includes_run_piece`; `Null_weight_counts_one_each`; `Weight_not_whole_fails_with_text`; `Count_not_whole_or_over_1000_fails_with_text`; `K_is_min_of_count_and_members`; `Pinned_draw_reproduces_split` (FixedRolls `draw:Hits#1..3`); `Out_of_range_pin_redrawn`; `Dice_func_receives_draw_keys_with_remaining_faces`; `Draw_keys_not_in_rolls_count_or_dice_log`; `Not_shared_by_group_roll`; `Output_value_reads_piece_share_elsewhere_k` (formula text reads k; a Global output reading `Hits` gets k); `Each_drawn_piece_gets_own_ledger_delta_stamped_with_its_side`; `Remove_on_draw_removes_each_drawn_piece_last`; `Draw_lines_name_piece_and_share_stamped_with_side`; `Acting_working_shows_k_not_split`; `Pool_hidden_on_view_is_missing_hidden`; `No_draw_binding_old_path` (an existing formula's trace and commands byte-equal to before).
  - Replay/undo: `Replay_does_not_redraw` (save, load: same state; roller not called); `Undo_redo_draw_run_one_step`; `Physical_dice_mode_still_draws_with_roller`.
- [ ] **Step 2: Run** Core `--filter "FullyQualifiedName~Draw"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2; Core `--filter "FullyQualifiedName~FormulaRunner|FullyQualifiedName~RollGroups|FullyQualifiedName~PieceOutput|FullyQualifiedName~Reveal"`; the full Core.Tests (flaky: re-run once, report both) → PASS. Build Render, Desktop, App.
- [ ] **Step 5: Commit** `feat: draw k members at random from a weighted pool of counters`

### Task 5: Render — draws in the dialog, masking, pins, batches and online — model: opus

**Files:**
- Modify: `src/Quartermaster.Render/FormulaMasking.cs`: `DrawInput = "draw"` marker (weight not Public, pool filter may read hidden values by the B7 filter test, or `PieceAccess.PiecesMayHide`); the B7 pre-Apply rule covers it (`PreviewValues`, `PreviewRolls` carry no draw keys, `LedgerPreview` collapses draw outputs).
- Modify: `src/Quartermaster.Render/FormulaDialogModel.cs`: merges `DrawRolls` into its pins; Draw box (read-only, value k or `HiddenBoxText`, tooltip verbatim); outputs with `OutputPreview.Draw` collapse into one line `{Prop} of the drawn counters changes`; no reroll for a draw key.
- Modify: `BatchModel.cs`, `MenuBatchModel.cs` (Ledger column collapse; chain pins per row incl. draw keys), `src/Quartermaster.Render/Online/IntentExecutor.cs` (`FormulaIntentRunner` and `FormulaBatchRunner`: draw keys pinned per sender through the existing `RunRequest.Dice`; preview verdict without draw keys or per-piece values).
- Test: `tests/Quartermaster.Render.Tests/DrawDialogTests.cs`, `tests/Quartermaster.Render.Tests/DrawOnlineTests.cs`.
- [ ] **Step 1: Failing tests** (Task 4 fixture):
  - Dialog/hot-seat: `Calculate_twice_then_apply_same_split`; `Dialog_never_shows_split_before_apply` (box text, outputs text, working); `Masked_viewer_box_reads_hidden` (`north` viewer, Private `Boats`); `Unmasked_draw_shows_k` (Public weight, pieces cannot hide); `Hotseat_south_sees_draw_lines_north_does_not` (`PlayerView.For` per side after Apply); `Undo_after_apply_restores_boats_and_lines`; `Menu_batch_chain_and_live_apply_agree` (two rows on two hexes).
  - Online: `Guest_preview_then_apply_same_split` (host pins by sender); `Guest_preview_verdict_has_no_draw_keys_or_shares`; `Two_guests_pins_independent`; `Guest_batch_rows_draw_independently_and_apply_as_one_step`; `Guest_cannot_supply_draw_dice` (a `FormulaIntent` with typed `draw:Hits#1` values is ignored or refused with `NotUnderstoodText`, as typed dice for a hidden roll are); `Turn_file_replay_keeps_split` (send a turn, replay it: same state, no roller call).
- [ ] **Step 2: Run** Render `--filter "FullyQualifiedName~Draw"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2; Render `--filter "FullyQualifiedName~Masking|FullyQualifiedName~FormulaDialog|FullyQualifiedName~Batch|FullyQualifiedName~IntentExecutor|FullyQualifiedName~Turn"`; the full Render.Tests (flaky list: re-run once, report both) → PASS. Build Desktop, App.
- [ ] **Step 5: Commit** `feat: draws in the formula dialog, batch grids and online runs, pinned and shown only after Apply`

### Task 6: Render/Desktop — Workbench editors — model: sonnet

**Files:**
- Modify: `src/Quartermaster.Render/VisibilityEdit.cs`: `SetHolderOwnerExpr(RulesPack, string holderId, string? text)`.
- Modify: `src/Quartermaster.Render/FormulaEdit.cs`: `WithHolderChoices(RulesPack, VariableBinding, string? names)` (names → ids, comma-separated, quoted names as the Properties tab's `Holders` column parses them; unknown names reported with `No holder is named "{n}"`), `WithDrawCount`, `WithHolderVariable`; `WithKind` clears fields the kind does not use (`HolderChoices` off Holder, `DrawCount` off Draw, `SameHex` off Piece/Draw); `WithOutputTarget` clears `HolderVariable` off Holder; `PieceDetailsHint` reused, new `DrawDetailsHint` (verbatim).
- Modify: `src/Quartermaster.Desktop/Workbench/VisibilityTab.cs` (column `Owner expression` after `Owner side`, tooltip verbatim; problem filter also shows `owner expression` messages), `src/Quartermaster.Desktop/Workbench/FormulasTab.Grids.cs` (Kind lists `Draw`; columns `Holder choices` (10), `Draw count` (11) with tooltips; `Same hex` enabled for Piece and Draw; Draw Target combo `(one each)` + Pieces/Both properties; holder-picker Target options `{Prop} of (chosen holder)`; outputs column `Holder variable` after `Margin`; `Piece` combo lists Draw variables). Older columns keep their indexes.
- Modify: `src/Quartermaster.App/*`: build only.
- Test: `tests/Quartermaster.Render.Tests/B9FormulaEditTests.cs`, `tests/Quartermaster.Desktop.Tests/B9WorkbenchTests.cs` (headless).
- [ ] **Step 1: Failing tests**: Render `Owner_expr_blank_null_trimmed`; `Holder_choices_names_to_ids_unknown_reported`; `With_kind_clears_unused_fields`; `With_output_target_clears_holder_variable`; `Draw_count_blank_null`; Desktop `Owner_expression_column_commits`; `Owner_expression_problem_shown_in_tab`; `Draw_kind_columns_enabled`; `Holder_choices_column_commits`; `Holder_variable_combo_lists_picker_variables`; `Piece_combo_lists_draw_variables`; `Older_column_indexes_unchanged`; `Tooltips_text`; `Problems_list_shows_b9_errors`.
- [ ] **Step 2: Run** Render `--filter "FullyQualifiedName~B9FormulaEdit"`, Desktop `--filter "FullyQualifiedName~B9Workbench"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2; Render `--filter "FullyQualifiedName~FormulaEdit|FullyQualifiedName~VisibilityEdit"`; Desktop `--filter "FullyQualifiedName~Workbench|FullyQualifiedName~FormulasTab|FullyQualifiedName~VisibilityTab"`; the full Desktop.Tests (flaky list: re-run once, report both) → PASS. Build App.
- [ ] **Step 5: Commit** `feat: B9 Workbench owner expressions, holder choices, draw bindings and holder-variable outputs`

### Task 7: Wiki, RESUME, full runs — model: sonnet

**Files:**
- Modify: `docs/wiki/Making-a-Rules-Pack.md` (`## Holders at run time and random draws` after B8's section, the four subsections and examples of the spec; every validator text; the author cautions), `docs/wiki/Formula-Reference.md` (Draw binding, holder choices, Holder variable, runtime messages in the error table), `docs/wiki/Window-Rules-Workbench.md` (the new columns), `docs/wiki/Window-Formula-Dialog.md` (holder dropdown, Draw box), `docs/wiki/Playing-With-Others.md` (dumps that change hands; draw lines per side). Invented Riverland examples only; identifiers without spaces in expressions.
- Modify: `docs/superpowers/RESUME.md`: a new CURRENT STATE section for B9 (B8 moved under Previous); downstream note in neutral wording ("downstream pack": no pack bytes change; holder owners now come from `Ownership.SideOfHolder(id, pack, state)`; a pack may add owner expressions, holder choices and Draw bindings).
- Test: Docs `WikiLintTests`.
- [ ] **Step 1: Write** the wiki and RESUME.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Docs.Tests -c Release --filter "FullyQualifiedName~WikiLint"` → PASS.
- [ ] **Step 3: Full runs**, one at a time: Rules, Vassal, Core, Render, Desktop, Docs, Net → PASS (flaky: re-run once, report both). Build `src/Quartermaster.App -c Release`.
- [ ] **Step 4: Commit** `docs: B9 holders at run time and random draws`
