# Phase Pipeline and Timed Values (B2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Starting values from an expression; start entries applied strictly in order in every difficulty; a side asked to answer a start run; properties reset when a phase begins; `runs()` counts of applied runs; a cheaper per-unit start entry.

**Architecture:** Rules gains `PropertyDef.DefaultExpr`/`ResetAt`, `StartEntry.AskSideId`, the `runs()` function (parser, evaluator, `IEvalContext.Runs` default) and validator checks. Core seeds from expressions, adds the `ResetProperties` and `SetStartCursor` commands, `GameState.StartCursor`, run stamps on log lines with a run index, `PendingRun.AskSideId` filtering, parse/pass caches and `GameSession.DeferChanged`. Render's `PhaseController` runs entries in order with a cursor and continues them from `ApplyFormula`, resets at phase entry, and enforces ask runs (dispatch and guest intents). Desktop/Workbench gets the editors; docs.

**Tech Stack:** C# .NET 10, Avalonia (Desktop), xUnit.

**Spec:** `docs/superpowers/specs/2026-11-25-b2-phase-pipeline-design.md`

## Global Constraints

Read `.superpowers/sdd/2026-11-25-b2-phase-pipeline/global-constraints.md` (git-ignored; same folder as the ledger) before each task. It holds the invariants and **every exact validator, run-time and Workbench string**; use them verbatim. In short:
- Public repo: invented data only. Old packs keep bytes + SHA-256, `CurrentFormat` 4. Old saves keep bytes.
- Additive API only: new record members as body init properties. `IEvalContext.Runs` is a default method.
- Hidden information: host-only seeding, reset and continuation; redaction clears `AppliedRun`/`Stage`; `runs()` is `HiddenData` on unheld or unit-less views; ask runs are seen and applied only by the asked side.
- One undo step: reset + seed + entries in the Next Phase action; continuation in the answer's action.
- `-c Release`, one test project at a time, headless only, never stash/push, repo-local identity, `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Old packs keep bytes**: an invented format-4 `pack.json` literal with every pre-B2 field loads and saves byte-equal, same SHA-256 (Task 1, `B2_absent_fields_keep_bytes`).
2. **Strict order in every mode**: entry 2's filter reads what entry 1 writes. In SemiAuto and Hardcore, entry 2 queues only after entry 1's run is answered, and its units are those entry 1 made eligible. FullyAuto with no input gives the same final ledger (Task 3, `Strict_order_same_result_in_all_difficulties`).
3. **Undo of an answer undoes the continuation** (Task 3, `Undo_answer_undoes_continued_entries`).
4. **Ask run privacy**: the guest not holding the asked side sees no pending run, and its intent is refused with `That calculation belongs to a side you don't hold.`. The asked guest applies it, stamped with the asked side; its private roll is hidden from the other (Task 3, `Ask_run_hidden_and_refused_for_other_side`).
5. **runs() does not leak**: a redacted line has `AppliedRun == null`. On a guest's view, `runs()` on an enemy piece is `HiddenData` (Task 2, `Redacted_line_clears_run_stamp`, `Runs_hidden_on_unheld_piece`).
6. **Perf**: the counters prove parse-once and side-map-once per pass. The timing test is robust (Task 4).

---

### Task 1: Rules — fields, runs() in the language, validator — model: sonnet

**Files:**
- Modify: `src/Quartermaster.Rules/Model/RulesPack.cs`: `PropertyDef.DefaultExpr : string?`, `ResetAt : List<string>?` (both `[JsonIgnore(WhenWritingNull)]`, doc comments).
- Modify: `src/Quartermaster.Rules/Model/PhaseDefs.cs`: `StartEntry.AskSideId : string?` (`WhenWritingNull`).
- Modify: `src/Quartermaster.Rules/Io/RulesPackIo.cs`: `Normalise` turns a blank `DefaultExpr`/`AskSideId` into null. `ResetAt` is trimmed, blanks dropped, made distinct, and an empty list becomes null.
- Modify: `src/Quartermaster.Rules/Expr/Parser.cs`: `["runs"] = (1, 2)`.
- Modify: `src/Quartermaster.Rules/Expr/Evaluator.cs`: `"runs"` in `FunctionNames`. `EvalCall` case `"runs"`: arg 0 must be a `TextLit` (else `runs() needs a formula id in quotes`). Arg 1 is optional and must be a `TextLit` of `stage`/`turn`/`game`, case-insensitive (else `runs() period must be "stage", "turn" or "game"`); default `game`. The result is `Num(ctx.Runs(id, period))`. `IEvalContext`: `double Runs(string formulaId, string period) => throw new EvalError("runs() is not available here");`.
- Modify: `src/Quartermaster.Rules/Validation/RulesValidator.cs`:
  - `CheckStartingValue` (all #4 texts);
  - `CheckResets` (#56 texts);
  - in `CheckRunEntries`, the #72 texts with entry number and kind;
  - a `runs` branch beside the `holder` branch in `FilterRefProblems` (~line 210) and in the formula expression walk (~line 768), giving the three tails;
  - `runs` added to the applies-to forbidden check (`PieceBoundAggregatesIn` caller), to `CheckComputed` (`runs() is not available in a computed property`), to `CheckSide` (`Side "{s}": runs() is not available in a side's unit filter`) and to `ForbiddenConditionFunctions`;
  - a starting-value forbidden list (dice, holder, runs, all aggregates).
- Test: `tests/Quartermaster.Rules.Tests/PhasePipelineFieldsTests.cs`
- [ ] **Step 1: Failing tests**:
  - `B2_absent_fields_keep_bytes`; `B2_fields_round_trip`; `Blank_fields_load_as_null` (ResetAt `[" ", ""]` → null);
  - `Parser_runs_arity`; `Evaluator_runs_args_and_default_period` (fake context records `(id, period)`); `Evaluator_runs_bad_args_texts`; `Default_context_runs_not_available`;
  - `Validator_starting_value_errors` (every #4 text, each forbidden fn); `Validator_reset_errors`; `Validator_ask_entry_errors` (start and end kinds, 1-based n);
  - `Validator_runs_tails_in_formula_warning_alert_filter`; `Validator_runs_forbidden_places` (applies-to, computed, side, phase condition).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~PhasePipelineFields"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, then the full Rules.Tests. Build `src/Quartermaster.Core`, `src/Quartermaster.Render`, `src/Quartermaster.Desktop`, `src/Quartermaster.App` (`-c Release`) → PASS / 0 errors.
- [ ] **Step 5: Commit** `feat(rules): starting value, reset and ask fields; runs(); validator`

### Task 2: Core — expression seeds, reset command, run stamps and runs() — model: opus

**Files:**
- Modify: `src/Quartermaster.Core/Ledger.cs` (`LedgerSeeder`):
  - `Seeded(p)` is true for SheetField or DefaultExpr;
  - `SeedPiece` keeps sheet values first. For accepted properties without a sheet value, it evaluates `DefaultExpr` in pack order with `accepted` as pending. On error or a non-number, it seeds `Default` and logs the diagnostic `AddOnce("seedexpr:" + id)`.
- Modify: `src/Quartermaster.Core/PieceFilter.cs`: `internal static Value EvaluateWithPending(Expr e, PieceInstance p, GameState s, RulesPack pack, IReadOnlyDictionary<string,double> pending)` (throws). `FilterContext.Runs` and `GameContext.Runs` call `RunIndex`.
- Modify: `src/Quartermaster.Core/Commands.cs`:
  - `[JsonDerivedType(typeof(ResetProperties), "resetProperties")]`, `public sealed record ResetProperties(IReadOnlyList<string> PropertyIds, string Text, DateTimeOffset? At = null, string? Who = null) : GameCommand`;
  - `LedgerDelta` and `AddLogLine` gain the body init property `AppliedRun : string?` (`WhenWritingNull`).
- Modify: `src/Quartermaster.Core/GameLog.cs`: `LogEntry` gains the body init properties `AppliedRun : string?` and `Stage : int?` (`WhenWritingNull`). `LogBuilder.Ledger`/`Line` copy `AppliedRun` and set `Stage = s.Position?.Repeat` only when `AppliedRun` is set.
- Modify: `src/Quartermaster.Core/GameState.cs`: the reducer `ResetProperties` removes the ids from every `Ledgers` inner dictionary and drops emptied keys. It adds one System line with `Text`.
- Modify: `src/Quartermaster.Core/GameHistory.cs`: label `ResetProperties` → `Reset values`.
- Modify: `src/Quartermaster.Core/FormulaRunner.cs`: `Commands` sets `AppliedRun = t.Formula.Id` on the first `LedgerDelta`. `RunContext.Runs` → `RunIndex`.
- Create: `src/Quartermaster.Core/RunIndex.cs`: `internal static class RunIndex`.
  - `Count(GameState s, string formulaId, string? pieceId, string period)`.
  - It throws `runs() names formula "{id}", which does not exist` for an unknown id (pack passed in).
  - In a player view (`s.IsPlayerView`), a null piece or a piece of a side not in `ViewSides` throws `runs() cannot be counted in this view` with `HiddenData = true`.
  - The index is a `ConditionalWeakTable<List<LogEntry>, Index>`: incremental on append, rebuilt when the count drops.
- Modify: `src/Quartermaster.Core/PlayerView.cs`: a redacted line gets `AppliedRun = null, Stage = null`. Update the class summary.
- Test: `tests/Quartermaster.Core.Tests/StartingValueTests.cs`, `ResetPropertiesTests.cs`, `RunsFunctionTests.cs`
- [ ] **Step 1: Failing tests**:
  - Seeds: `Expression_seeds_when_no_sheet_value`; `Sheet_value_wins_over_expression`; `Expression_reads_sibling_and_earlier_seed`; `Expression_honours_applies_to`; `Expression_error_seeds_default_and_warns_once`; `Non_number_result_seeds_default`; `Expression_evaluated_once_per_piece` (second `BuildSeeds` → null); `Both_scope_holders_keep_default`;
  - Reset: `Reset_deletes_on_pieces_and_holders_and_logs_line`; `Reset_drops_empty_ledger`; `Reset_line_names_properties_only` (PlayerView for another side: the same text, no counts); `Old_commands_and_log_serialise_unchanged` (LedgerDelta/AddLogLine/LogEntry without AppliedRun or Stage: no `appliedRun`/`stage` in JSON);
  - Runs: `Runs_counts_applied_runs_per_piece_and_period` (stage/turn/game across a block repeat); `Runs_multi_output_run_counts_once`; `Runs_no_output_run_counts_once`; `Runs_failed_and_refused_not_counted`; `Runs_undo_drops_count` (GameSession rewind); `Runs_unitless_counts_runs_without_unit`; `Runs_in_precondition_allows_one_per_stage`; `Runs_index_incremental_and_rebuilt`; `Redacted_line_clears_run_stamp`; `Runs_hidden_on_unheld_piece`; `Runs_unitless_hidden_in_player_view`.
- [ ] **Step 2: Run** Core `--filter "FullyQualifiedName~StartingValue|FullyQualifiedName~ResetProperties|FullyQualifiedName~RunsFunction"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, the full Core.Tests, and Rules.Tests `--filter "FullyQualifiedName~PhasePipelineFields"`. Build Render, Desktop, App → PASS.
- [ ] **Step 5: Commit** `feat(core): starting value expressions, reset command, run stamps and runs()`

### Task 3: Phase pipeline — strict order, ask runs, reset at entry — model: opus

**Files:**
- Modify: `src/Quartermaster.Core/Phases.cs`: `PendingRun` gains the body init property `AskSideId : string?` (`WhenWritingNull`). Add `public sealed record StartCursor(PhasePosition At, int Next)`.
- Modify: `src/Quartermaster.Core/Commands.cs`: `[JsonDerivedType(typeof(SetStartCursor), "setStartCursor")]`, `public sealed record SetStartCursor(StartCursor? Cursor) : GameCommand` (logs nothing).
- Modify: `src/Quartermaster.Core/GameState.cs`: `StartCursor? StartCursor` (`WhenWritingNull`). The reducer sets it, and `DoAdvance` clears it.
- Modify: `src/Quartermaster.Core/PlayerView.cs`: an ask run is kept only when `_held` contains `AskSideId` (before the unit-less phase-side rule).
- Modify: `src/Quartermaster.Core/Decisions.cs`: an ask run's text is `{formula} — {side name} answers`.
- Modify: `src/Quartermaster.Render/PhaseController.cs`:
  - `RunEntries(phase, entries, trigger, from)` runs from index `from`. For `start`, it stops after the first entry that queued a run and executes `SetStartCursor(new(pos, i + 1))`, or `SetStartCursor(null)` when the cursor was set and all ran.
  - `Apply` always queues an entry with `AskSideId` (`PendingRun { AskSideId = … }`) and reports whether it queued.
  - `ApplyFormula`: refuses an ask run when `ActorSides` is non-null and lacks the side. It stamps an ask run with `AskSideId`. After `ResolveRun`, `ContinueEntries()` runs inside the action when `StartCursor.At == position` and no `start` run is pending. The trigger stamp comes from `s.ViewSides`. Queued runs are raised after the action closes.
  - `Advance`: before `AdvancePhase`, a set cursor with entries left logs `not run: {names}`.
  - `EnterPhase` (Start/Advance): `ResetProperties` for properties whose `ResetAt` contains the phase id (pack order; text `{phase}: reset {names}`), then `s.SeedLedgers()`, then `OnEnter`.
- Modify: `src/Quartermaster.Render/PhaseRunDispatch.cs`: in `OpenPendingRun`, an ask run whose side the view does not hold → `ui.Status("That calculation " + ModuleSession.OtherSideText + ".")`.
- Modify: `src/Quartermaster.Render/Online/IntentExecutor.cs`: in `FormulaIntentRunner.Run`, a `RunId` of an ask run whose side the sender lacks is refused with the same text.
- Modify (tests whose expectations change, listed in the report): `tests/Quartermaster.Render.Tests/PhaseControllerTests.cs` (`SemiAuto_OnStartUnits_QueuesOneRunPerEntry_RaisesRunsQueued` → `SemiAuto_OnStart_QueuesFirstEntry_NextAfterAnswer`). Grep `OnStart` in `PhaseControllerHooksTests`, `FormulaMaskingTests`, `FormulaGuardsRenderTests`, `PhaseLimitTests`, Desktop `HandoverTests`, `DesktopUpdatesTests` and Docs `WalkthroughFormulaTests`.
- Test: `tests/Quartermaster.Render.Tests/PhasePipelineTests.cs`
- [ ] **Step 1: Failing tests**:
  - Order: `Strict_order_same_result_in_all_difficulties`; `SemiAuto_entry_two_units_chosen_after_answer`; `FullyAuto_needs_input_stops_later_entries`; `Batch_apply_all_continues_after_last_unit`; `Undo_answer_undoes_continued_entries`; `Next_phase_with_entries_left_logs_not_run`; `Cursor_past_end_counts_as_done`; `Old_save_without_cursor_plays_as_before`; `StartCursor_and_ask_serialise_omitted_when_null`;
  - Online and hot-seat: `Guest_answer_continuation_stamped_as_phase`; `Hot_seat_continuation_raises_runs_queued`;
  - Ask runs: `Ask_entry_queues_in_fully_auto`; `Ask_run_hidden_and_refused_for_other_side`; `Ask_run_applied_by_asked_side_writes_global_stamped`; `Ask_run_solo_any_player`; `Ask_decision_text`;
  - Reset: `Reset_at_phase_entry_then_reseeds_then_entries`; `Reset_one_undo_with_next_phase`; `Skipped_phase_does_not_reset`; `Start_resets_first_phase`; `Block_repeat_resets_each_pass`.
- [ ] **Step 2: Run** Render `--filter "FullyQualifiedName~PhasePipeline"` → FAIL.
- [ ] **Step 3: Implement**, then update the changed tests.
- [ ] **Step 4: Run** Step 2, the full Render.Tests and the full Core.Tests. Docs `--filter "FullyQualifiedName~Walkthrough"`. Build Desktop, App → PASS.
- [ ] **Step 5: Commit** `feat: start entries in strict order, ask runs, reset at phase entry`

### Task 4: Per-unit cost — parse cache, pass, deferred notification, benchmark — model: opus

**Files:**
- Modify: `src/Quartermaster.Core/FormulaRunner.cs`:
  - a per-`FormulaDef` parse cache (`ConditionalWeakTable`) for the text, precondition, `FormulaVariables.OutputSources` and binding expressions;
  - `[ThreadStatic] internal static int Parses`;
  - `public IDisposable BeginPass(GameState s)` (one `Lazy` side map and one `AggregateFilters` shared by `RunScope` while open on the same state instance); `AfterApply()` drops value memos.
- Modify: `src/Quartermaster.Core/Ownership.cs`: `[ThreadStatic] internal static int SideMapBuilds` (counted in `PieceSides`).
- Modify: `src/Quartermaster.Core/GameSession.cs`: `public IDisposable DeferChanged()`. Nested scopes join. `Changed` is raised once with the last command on the outer dispose, and not at all when nothing ran. Commands, the log, actions and checkpoints are unchanged.
- Modify: `src/Quartermaster.Render/PhaseController.cs`: `Apply` (FullyAuto path) wraps the unit loop in `s.Runner.BeginPass(state)` and `g.DeferChanged()`, and calls `AfterApply()` after each unit's commands.
- Audit (no change expected; record in report): every `Changed +=` subscriber in `src/Quartermaster.Desktop`, `src/Quartermaster.App` and `ModuleSession`.
- Test: `tests/Quartermaster.Core.Tests/FormulaPassTests.cs`, `tests/Quartermaster.Core.Tests/DeferChangedTests.cs`, `tests/Quartermaster.Render.Tests/PhasePipelinePerfTests.cs`
- [ ] **Step 1: Failing tests**:
  - Core: `Pass_parses_formula_once`; `Pass_builds_side_map_once`; `Pass_sees_earlier_units_writes` (unit 2's aggregate reads unit 1's output); `Edited_formula_record_reparsed`; `DeferChanged_raises_once_with_last_command`; `DeferChanged_log_identical_to_plain_execute` (command JSON equal); `DeferChanged_nested_and_empty`;
  - Render: `FullyAuto_entry_raises_changed_once`; `FullyAuto_entry_log_unchanged_by_pass` (log equal to a pre-B2 golden built with plain execution); `Perf_StartEntry_1000Units`:
    - `[Trait("Category","Perf")]`;
    - `#if DEBUG` and `Debugger.IsAttached` → write `skipped (Debug build or debugger)` and return;
    - the invented 1000-piece fixture;
    - one warm-up, best of 3;
    - writes `Start entry, {n} units: {ms:0.000} ms per unit (best of 3)`;
    - budget 0.2 ms/unit, or 1.0 when `CI == "true"`.
- [ ] **Step 2: Run** Core `--filter "FullyQualifiedName~FormulaPass|FullyQualifiedName~DeferChanged"`, Render `--filter "FullyQualifiedName~PhasePipelinePerf|FullyQualifiedName~PhasePipeline"` → FAIL.
- [ ] **Step 3: Implement.** Measure before and after, and report both numbers.
- [ ] **Step 4: Run** Step 2, the full Core.Tests and the full Render.Tests → PASS.
- [ ] **Step 5: Commit** `perf: parse once and share caches across a start entry pass`

### Task 5: Workbench, wiki, RESUME, full runs — model: sonnet

**Files:**
- Modify: `src/Quartermaster.Desktop/Workbench/PropertiesTab.cs`:
  - columns `Starting value` and `Resets at`, appended after `Clone as` (new column-index constants; older columns keep their places), with their tooltips;
  - `Resets at` parses comma-separated phase names (case-insensitive) to ids; an unknown name sets status `No phase named "{name}"` and commits nothing;
  - blank → null.
- Modify: `src/Quartermaster.Desktop/Workbench/PhasesTab.cs`: the `_start` and `_end` grids get `Asks side` (combo: `(none)` + side names), with its tooltip. `OnStartCell`/`OnEndCell` map name → id, `(none)` → null.
- Modify: `src/Quartermaster.App/Workbench/*`: build only (no new editors in the WinForms app).
- Modify: `docs/wiki/Making-a-Rules-Pack.md`, new sections with invented Riverland examples and the exact validator texts:
  - "Starting values from an expression": `Boat crew`, `lookup(Crew sizes, marker("Kind"))`;
  - "Start entries run in order": North Supply;
  - "Asking a side at the start of a phase": `Dawn`, `River level roll` asks the Northern League;
  - "Values that reset at a phase": `Ferry used` at North Supply and South Supply;
  - "How often a formula has run": `Bridge repair` with `runs("f-bridge-repair", "turn") < 1`.
- Modify: `docs/wiki/Formula-Reference.md`: `### runs("formula-id") or runs("formula-id", "period")`. Add `runs()` to the list of what a phase condition may not read.
- Modify: `docs/wiki/Window-Phases.md`: entries in order, `not run:` line, ask runs.
- Modify: `docs/wiki/Window-Rules-Workbench.md`: the new columns.
- Modify: `docs/superpowers/RESUME.md`: a new CURRENT STATE for B2, the old one moved under Previous. It notes the #32 behaviour change and the downstream pack follow-up (its turn-walk test pins the old two-queue order).
- Test: Desktop `PropertiesTab_starting_value_and_resets_at_commit`, `PropertiesTab_unknown_reset_phase_refused`, `PropertiesTab_old_columns_keep_places`, `PhasesTab_asks_side_column_start_and_end`, `PhasesTab_asks_side_none_clears`; Docs `Wiki_mentions_runs_function` (if the Docs suite pins function docs, extend that test instead).
- [ ] **Step 1: Failing tests.**
- [ ] **Step 2: Run** Desktop `--filter "FullyQualifiedName~PropertiesTab|FullyQualifiedName~PhasesTab"`, Docs `--filter "FullyQualifiedName~Wiki"` → FAIL.
- [ ] **Step 3: Implement**; wiki; RESUME.
- [ ] **Step 4: Full runs**, one at a time: Rules, Core, Render, Desktop, Docs → PASS (flaky list in the global constraints: re-run once, report both). Build `src/Quartermaster.App -c Release`.
- [ ] **Step 5: Commit** `feat: B2 editors; docs`
