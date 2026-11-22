# Phase Conditions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A phase may carry a condition; Next Phase skips phases whose condition is false (visibly, one undo step), and every expression gains `turn()`, `stage()` and `mod(a, b)`.

**Architecture:** The expression language gets three functions and two default `IEvalContext` members. Core gets a small phase context (globals + a candidate position) and a state-aware stepping function on top of the existing position math in `Sequencer`; `AdvancePhase` records what it skipped so undo, log, replays and chips stay exact. Render's `PhaseController` uses the new stepping and exposes skipped / will-skip chips; the Avalonia strip draws them and the Workbench Phases tab edits the condition.

**Tech Stack:** C# .NET 10, xUnit, Avalonia 12 headless (`[AvaloniaFact]`).

**Spec:** `docs/superpowers/specs/2026-11-22-phase-conditions-design.md` (read it; this plan argues from it).

## Global Constraints

- Public repo, hard boundary: no content from any real game in code, tests, docs or commit messages; invented test data only (existing fixtures such as `PhaseFixture`, `RunnerFx`, the validator's `Clean()` pack; wiki demo names Riverland etc.).
- Old saves and old rules packs load unchanged; an unchanged pack keeps its SHA-256 (`RulesPackIo.ComputeSha256`). Every new serialized member is `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]` (or `WhenWritingNull` for reference types on positional records). `RulesPackIo.CurrentFormat` stays 4.
- No new NuGet packages. Never open visible windows: UI tests are `[AvaloniaFact]`.
- Run one test project at a time with `-c Release` and a `--filter` while iterating; build only affected projects.
- Never kill/stop/close a process you did not start (the user runs games). Never `git stash`. Never push.
- Commits: repo-local noreply identity; message ends with a blank line then `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never commit `startapp.cmd`, `.superpowers/` or untracked notes in `docs/`.
- Branch `phase-conditions` (from master `cee2823`; spec commit `d267a09`).
- Exact texts (verbatim):
  - log line per skipped phase: `Skipped {phase name} (condition false)`
  - strip notice: `Skipped: {name}, {name}`
  - loop guard notice: `Every phase was skipped; check the phase conditions`
  - error notice: `Condition of {phase name} failed: {message}; phase not skipped`
  - skipped chip tooltip: `Skipped this pass (condition false)`
  - will-skip chip tooltip: `Will be skipped unless {condition} changes`
  - Workbench label `Runs only if`; hint `Leave empty to always run. Example: PlayerA == 1, turn() == 1, mod(turn(), 4) == 1.`
  - validator: forbidden function `Phase "{name}": condition may not use {fn}()`; unknown name `Phase "{name}": condition uses "{x}", which is not a global property`; constant false warning `Phase "{name}" never runs (its condition is always false)`.

## Review Focus

1. **A condition reading a global the previous phase just set** (e.g. an initiative phase's formula sets `PlayerA`, then Next Phase) → the condition sees the new value, because it is evaluated at advance time on the current state. Test in Task 2 (`Condition_reads_value_set_in_previous_phase`).
2. **Skipping the last phase of a block, or every phase of one block repeat, or the last phase of the turn** → repeats still count and the turn still wraps exactly as without conditions. Tests in Task 2 (`Skip_last_phase_of_block_moves_to_next_repeat`, `Skip_whole_repeat_counts_it`, `Skip_last_phase_wraps_turn`).
3. **Undo of a press that skipped phases** → back to the phase before the press; skipped chips and `SkippedPhases` restored as before. Test in Task 4 (`Undo_restores_position_and_skipped_chips`).
4. **Hot-seat pass prompt** → "Pass to X player" names the side of the phase actually entered, not a skipped one. Test in Task 4 (`Pass_prompt_uses_destination_after_skips`).
5. **Replays and received turn files** → an `AdvancePhase` replays to its recorded `To` with its recorded skips; conditions are never re-evaluated during replay. Test in Task 2 (`Replayed_advance_ignores_current_conditions`).

---

### Task 1: `turn()`, `stage()`, `mod()` in the expression language

**Files:**
- Modify: `src/Quartermaster.Rules/Expr/Parser.cs` (Arity table), `src/Quartermaster.Rules/Expr/Evaluator.cs` (`IEvalContext`, `EvalCall`)
- Modify: `src/Quartermaster.Core/PieceFilter.cs` (`FilterContext`), `src/Quartermaster.Core/FormulaRunner.cs` (`RunContext`)
- Modify: any other site that enumerates function names (run `grep -rn '"startswith"' src tests` and `grep -rn '"contains"' src`; e.g. working/layout display, masking, `Parser.Variables`) so the three new names are treated like `name()` (no variables)
- Test: `tests/Quartermaster.Rules.Tests/Expr/EvaluatorRegistryTests.cs`, new `tests/Quartermaster.Rules.Tests/Expr/TurnStageModTests.cs`, `tests/Quartermaster.Core.Tests/` (filter + runner tests)

**Interfaces:**
- Produces: `IEvalContext.Turn() : double` and `IEvalContext.Stage() : double` as default interface members returning `1`; Parser arity `turn (0,0)`, `stage (0,0)`, `mod (2,2)`; `mod(a,b) = a - b*floor(a/b)`, `EvalError` "mod: division by zero" when `b == 0`.
- `FilterContext` and `RunContext` override: `Turn() = s.Position?.Turn ?? s.Turn.Turn`, `Stage() = s.Position?.Repeat ?? 1`.

- [ ] **Step 1: Write failing tests**
  - `TurnStageModTests.Mod_is_floored`: `mod(7,4)==3`, `mod(-1,4)==3`, `mod(8,4)==0`, `mod(5.5,2)==1.5`; `mod(1,0)` throws `EvalError`.
  - `TurnStageModTests.Turn_and_stage_default_to_one`: with a context that does not override them, `turn()==1`, `stage()==1`.
  - `TurnStageModTests.Wrong_arity_is_parse_error`: `turn(1)`, `mod(1)` → `ParseError`.
  - `EvaluatorRegistryTests`: add the three names wherever the registry list is pinned.
  - Core: `Filter_reads_turn_and_stage` (state Position = `(3, 2, …)`: filter `turn() == 3 and stage() == 2` true) and `Formula_reads_turn` (a formula `turn() * 10` on a state at turn 4 gives 40).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~TurnStageMod|FullyQualifiedName~EvaluatorRegistry"` → FAIL.
- [ ] **Step 3: Implement** as in Interfaces; update the other function-name sites found by the grep.
- [ ] **Step 4: Run** the Step 2 command, then `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~Filter|FullyQualifiedName~FormulaRunner"` and `dotnet test tests/Quartermaster.Render.Tests -c Release --filter "FullyQualifiedName~Formula"` → PASS.
- [ ] **Step 5: Commit** `feat(rules): turn(), stage() and mod() in expressions`

### Task 2: Phase conditions in Core (model, evaluation, stepping, recording)

**Files:**
- Modify: `src/Quartermaster.Rules/Model/PhaseDefs.cs` (`PhaseDef.Condition`)
- Create: `src/Quartermaster.Core/PhaseConditions.cs` (phase context + stepping)
- Modify: `src/Quartermaster.Core/Commands.cs` (`AdvancePhase.Skipped`), `src/Quartermaster.Core/Phases.cs` (`SkippedPhase`), `src/Quartermaster.Core/GameState.cs` (`SkippedPhases`, `DoAdvance`)
- Test: new `tests/Quartermaster.Core.Tests/PhaseConditionTests.cs`; Rules IO round-trip/hash test in `tests/Quartermaster.Rules.Tests/`

**Interfaces:**
- Consumes: Task 1 `IEvalContext.Turn()/Stage()`, `mod`.
- Produces:
  - `PhaseDef.Condition : string?` (`WhenWritingDefault`).
  - `public sealed record SkippedPhase(string PhaseId, string Name, int Turn, int Repeat);` in `Phases.cs`.
  - `AdvancePhase(..., string? Who = null, IReadOnlyList<SkippedPhase>? Skipped = null)` — new last positional parameter with `[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`.
  - `GameState.SkippedPhases : List<SkippedPhase>?` (`WhenWritingNull`): the skips of the current turn.
  - `public sealed record PhaseStep(PhasePosition To, IReadOnlyList<SkippedPhase> Skipped, string? Notice);`
  - `public static class PhaseConditions`:
    - `bool Runs(RulesPack pack, GameState s, PhaseDef phase, PhasePosition at, out string? error)` — true when Condition empty; evaluates against globals (`LedgerKey.Holder(HolderDef.GlobalId)`, falling back to the property's Default) with `turn()/stage()` = `at`; any `ParseError`/`EvalError` → `error` set, returns true (fail-open).
    - `PhaseStep? First(RulesPack pack, GameState s)` and `PhaseStep? Next(RulesPack pack, GameState s, PhasePosition cur)` — step with `Sequencer.First`/`Sequencer.Next`, testing each candidate with `Runs`; the first runnable candidate is `To`; error messages go into `Notice` as the error notice text.
    - Loop guard: stop after one full turn of candidates (from `cur` to the same phase id one turn later, i.e. at most Σ over phases of their block counts + 1 steps); then `To` = the first candidate, `Notice` = guard text.
  - The phase context forbids piece functions: `PieceName()`/`Marker()`/`Has()`/`RollDice()` throw `EvalError` ("not available in a phase condition").
  - `DoAdvance`: when `a.Skipped` is non-empty, add one `LogKind.Turn` line per skip with the exact text, before the existing turn line; `SkippedPhases` = (previous list if same turn, else empty) + `a.Skipped` entries whose `Turn == a.To.Turn`; null when empty. Turn change clears it.
- [ ] **Step 1: Write failing tests** (`PhaseConditionTests`, invented pack with phases P1..P4, a block P2-P3 count 2, a global `Flag` Default 0):
  - `Empty_condition_runs`; `False_condition_is_skipped` (Next from P1 with P2 `Flag == 1` → To P3, Skipped [P2]).
  - `Condition_reads_value_set_in_previous_phase` (set `Flag=1` in the ledger, then Next → P2 not skipped).
  - `Skip_last_phase_of_block_moves_to_next_repeat`, `Skip_whole_repeat_counts_it`, `Skip_last_phase_wraps_turn` (positions equal to what `Sequencer.Next` gives with the skipped phases removed from the path).
  - `Turn_and_stage_are_the_candidates` (P2 `stage() == 2` skipped in repeat 1, runs in repeat 2; P1 `turn() == 1` skipped on turn 2).
  - `All_skipped_stops_with_guard` (every phase `false` → returns, notice = guard text, no hang).
  - `Error_condition_is_not_skipped` (`1/0 > 0` → runs, notice = error text).
  - `Piece_function_in_condition_errors` (`name() == "x"` → runs, notice error).
  - `First_skips_too` (P1 false at turn 1 → First is P2).
  - `Advance_logs_skips_and_records_them` (apply `AdvancePhase` with Skipped → log lines exact text, `SkippedPhases` set; new turn clears).
  - `Replayed_advance_ignores_current_conditions` (apply a recorded `AdvancePhase` whose `To` is a phase now false → position is that `To`).
  - Save round trip: state with `SkippedPhases` and a command with `Skipped` survive JSON; a state/command without them serializes with no `skippedPhases`/`skipped` text.
  - Rules: `Condition_unset_keeps_pack_hash` (pack without conditions: saved JSON has no `condition`, hash unchanged after load/save) and `Condition_round_trips`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~PhaseCondition"` and Rules `--filter "FullyQualifiedName~Condition"` → FAIL.
- [ ] **Step 3: Implement** per Interfaces.
- [ ] **Step 4: Run** Step 2 commands, then full `dotnet test tests/Quartermaster.Core.Tests -c Release` → PASS.
- [ ] **Step 5: Commit** `feat(core): phases with conditions are skipped when false`

### Task 3: Validator checks conditions

**Files:**
- Modify: `src/Quartermaster.Rules/Validation/RulesValidator.cs` (`CheckPhase`)
- Test: `tests/Quartermaster.Rules.Tests/ValidatorTests.cs` (or the existing validator test file)

**Interfaces:**
- Consumes: `PhaseDef.Condition`, Task 1 function names.
- Rules: condition must parse; each variable must be a property with scope `Holders` or `Both` (a global property) — else the unknown-name error; table names inside `lookup(...)` accepted and unknown tables reported as elsewhere; `name`, `marker`, `has`, `dice` → forbidden-function error; a condition that evaluates to false with no variables and no `turn()/stage()` (constant) → the never-runs warning. All exact texts from Global Constraints.
- [ ] **Step 1: Write failing tests**: `Validator_PhaseCondition_ParseError`, `Validator_PhaseCondition_UnknownName`, `Validator_PhaseCondition_PieceProperty_IsUnknown` (a `Pieces`-scope property), `Validator_PhaseCondition_ForbiddenFunction` (theory over the four names), `Validator_PhaseCondition_ConstantFalse_Warning`, `Validator_PhaseCondition_Clean` (`Flag == 1 and mod(turn(), 4) == 1` and `lookup(T, turn()) > 0` → no issues).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~PhaseCondition"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, then full Rules.Tests → PASS.
- [ ] **Step 5: Commit** `feat(rules): validate phase conditions`

### Task 4: PhaseController uses conditions (advance, notices, chips)

**Files:**
- Modify: `src/Quartermaster.Render/PhaseController.cs` (`Start`, `Advance`, `View`, `ChipState`, `PhaseChip`)
- Test: `tests/Quartermaster.Render.Tests/` (PhaseController tests)

**Interfaces:**
- Consumes: Task 2 `PhaseConditions.First/Next/Runs`, `PhaseStep`, `AdvancePhase.Skipped`, `GameState.SkippedPhases`.
- Produces:
  - `ChipState { Done, Current, Upcoming, Skipped, WillSkip }`.
  - `PhaseChip(string Id, string Name, ChipState State, string? Notes = null, string? Tip = null)` — `Tip` holds the exact skipped / will-skip tooltip.
  - `Start`/`Advance` use `PhaseConditions.First/Next`; the `AdvancePhase` carries `step.Skipped`; the strip notice joins (in this order) relocation notice, `Skipped: …`, step notice, pass prompt with `". "` as today. `OnEnter` runs only for the destination phase. Pass prompt compares destination side with the phase left.
  - `View()`: chips before the current index in this turn whose id is in `SkippedPhases` → `Skipped`; upcoming chips → `WillSkip` when a simulated walk from the current position to the end of the turn (repeated `PhaseConditions.Next` without applying anything) skips them; `nextName` = destination of `PhaseConditions.Next`. The will-skip tooltip uses the phase's condition text.
- [ ] **Step 1: Write failing tests**: `Advance_skips_false_phase_and_notices` (notice contains `Skipped: P2`; P2's OnStart formula not queued/run); `Pass_prompt_uses_destination_after_skips` (P2 side A false, P3 side B: notice says "Pass to B player"); `Undo_restores_position_and_skipped_chips`; `Chips_show_skipped_and_will_skip` (states and exact tooltips); `Next_name_is_destination`; `Start_skips_false_first_phase`; `Error_condition_notice` (exact text).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Render.Tests -c Release --filter "FullyQualifiedName~PhaseController"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, then `--filter "FullyQualifiedName~Phase|FullyQualifiedName~HotSeat|FullyQualifiedName~Guest|FullyQualifiedName~Turn"` → PASS.
- [ ] **Step 5: Commit** `feat(render): Next Phase skips phases whose condition is false`

### Task 5: Avalonia strip and Workbench field

**Files:**
- Modify: the Avalonia phase strip control in `src/Quartermaster.Desktop` (find with `grep -rn "ChipState" src/Quartermaster.Desktop`), `src/Quartermaster.Desktop/Workbench/PhasesTab.cs`
- Check only: WinForms `src/Quartermaster.App` phase strip compiles with the new enum values (draw `Skipped`/`WillSkip` like `Upcoming` if a switch needs a case; no WinForms Workbench change).
- Test: `tests/Quartermaster.Desktop.Tests/` (phase strip + PhasesTab tests)

**Interfaces:**
- Consumes: Task 4 `ChipState.Skipped/WillSkip`, `PhaseChip.Tip`; `PhaseDef.Condition`; Task 3 validator messages.
- Strip: `Skipped` drawn greyed and struck-through, `WillSkip` faded (lower opacity) — both theme-aware like existing chip brushes; tooltip = `chip.Tip`.
- Phases tab: a `Runs only if` text box for the selected phase, label + hint (exact texts), committed like the other phase fields (undoable workbench edit); empty text stores `null`; the phase's validator issues show inline as for other expression fields.
- [ ] **Step 1: Write failing tests** (`[AvaloniaFact]`): `Strip_draws_skipped_and_will_skip_with_tips`; `PhasesTab_condition_commits_and_clears` (type `Flag == 1` → pack phase Condition set; clear → null); `PhasesTab_condition_shows_validator_error` (`name() == "x"` shows the forbidden-function text).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Desktop.Tests -c Release --filter "FullyQualifiedName~PhaseStrip|FullyQualifiedName~PhasesTab"` → FAIL.
- [ ] **Step 3: Implement**; build `src/Quartermaster.App -c Release` to confirm WinForms compiles.
- [ ] **Step 4: Run** Step 2 → PASS.
- [ ] **Step 5: Commit** `feat(desktop): skipped phases in the strip, condition field in the Workbench`

### Task 6: Docs, RESUME, full runs

**Files:**
- Modify: `docs/wiki/Formula-Reference.md`, `docs/wiki/Making-a-Rules-Pack.md`, `docs/wiki/Window-Rules-Workbench.md`, `docs/wiki/Window-Map.md` (phase strip section; use whichever page documents the strip), `docs/superpowers/RESUME.md`

- [ ] **Step 1: Wiki** (plain language, invented demo names only): Formula Reference — `turn()`, `stage()`, `mod(a, b)` rows (floored, error on 0) and "What a phase condition may read" (globals, tables, turn/stage; not piece functions, dice or a side's holder values; errors never skip). Making a Rules Pack — phase conditions; recipe: two copies of a side's player-turn with `Flag == 1` / `Flag == 0`, game-start `turn() == 1`, every fourth turn `mod(turn(), 4) == 1`. Workbench — the `Runs only if` field. Phase strip — skipped / will-skip chips and the four notices (exact texts).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Docs.Tests -c Release` → PASS.
- [ ] **Step 3: RESUME**: new CURRENT STATE at the top of "Where we stopped" (old one under "Previous CURRENT STATE"): branch `phase-conditions`, features one line each, decisions (conditions read only globals/tables/turn/stage; fail-open; loop guard; no Once flag; #21 dropped; WinForms Workbench not edited), test counts, "not pushed; next: final review, user review, merge/PR".
- [ ] **Step 4: Full runs**, one project at a time: Rules, Core, Render, Desktop, Docs (`dotnet test tests/<project> -c Release`) → all PASS; known flaky (re-run once, report both): Core `PlayerViewTests.Perf_1200Pieces`, Render `Piece4EndToEndTests.Perf_SyntheticGame_UndoAndStepBack`, Render `LauncherTests.Refresh_InvalidPath`, Desktop `DocumentViewerToolbarTests.Add_tool_item_appends`, the TableLookup perf guard.
- [ ] **Step 5: Commit** `docs: phase conditions, turn(), stage() and mod()`
