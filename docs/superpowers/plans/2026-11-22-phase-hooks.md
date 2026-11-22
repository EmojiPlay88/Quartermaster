# Phase Hooks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** End-of-phase entries (stopping in the phase when a run needs the player), move entries that fire for the other side's moves, and formulas limited to phases.

**Architecture:** Rules gains three optional model members and validator checks. Core gains an `EndPhase` command and `GameState.EndedAt`, a side-mode unit selection for move entries, and one "allowed in this phase" helper. Render's `PhaseController` runs OnEnd before advancing and fires OnMove per owner; the piece menu, formula dialog and host intent apply the phase limit. Desktop Workbench edits the new fields.

**Tech Stack:** C# .NET 10, xUnit, Avalonia 12 headless.

**Spec:** `docs/superpowers/specs/2026-11-22-phase-hooks-design.md`

## Global Constraints

- Public repo: invented test data only (existing fixtures; wiki demo names such as Riverland).
- Old saves and packs load unchanged; an unchanged pack keeps its SHA-256; `CurrentFormat` stays 4. New members: `PhaseDef.OnEnd : List<StartEntry>?` and `FormulaDef.Phases : List<string>?` with `WhenWritingNull` (null = none/any; the Workbench stores null, never an empty list); `MoveEntry.FiresFor` and `GameState.EndedAt` with `WhenWritingDefault`/`WhenWritingNull`.
- No new NuGet packages. Never open visible windows (`[AvaloniaFact]`). One test project at a time, `-c Release`, `--filter`.
- Never kill processes you did not start. Never `git stash`. Never push.
- Commits: repo-local identity; message ends with a blank line then `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never commit `startapp.cmd`, `.superpowers/`, untracked notes in `docs/`.
- Branch `phase-hooks` (from master `a9d0088`).
- Exact texts:
  - stop notice: `Finish the end-of-phase runs, then press Next Phase again`
  - log tag for OnEnd runs: `end` (as `start`/`move`)
  - disabled reason: `Only in: {phase names}` (names joined with `, `, pack order, duplicates once)
  - host refusal: `{formula name} can only be run in: {phase names}`
  - validator error: `Formula "{name}": phase list names unknown phase "{id}"`
  - validator warning: `Phase "{phase}": {start|move|end} entry runs "{formula}", which is limited to other phases`
  - Workbench labels: `On end`, `Fires for` (values `Acting side`, `Other side`, `Any side`), `Only in phases` (hint `None checked = any phase.`)

## Review Focus

1. **Press, queue, press** → the second press advances and does not re-run OnEnd; undo of the second press returns to the phase with `EndedAt` still set; undo of the first restores it to null. Test in Task 3 (`OnEnd_queued_stops_then_advances_once`).
2. **OnEnd all automatic** → one press = one undo step that both ran the end entries and advanced. Test in Task 3 (`OnEnd_automatic_is_one_undo_step`).
3. **Other-side move in hot-seat** → the run's log stamp and queued run belong to the piece's owner, not the phasing side. Test in Task 3 (`OnMove_other_side_runs_for_owner`).
4. **Guest asks host to run a limited formula in the wrong phase** → refused with the exact text, nothing applied. Test in Task 4 (`Host_refuses_formula_outside_its_phases`).
5. **Phase condition skips the phase with OnEnd / replay of a received turn** → skipped phases never run OnEnd; replay applies recorded `EndPhase`/`AdvancePhase` without re-running. Test in Task 3 (`Skipped_phase_runs_no_OnEnd`, `Replay_applies_EndPhase_as_recorded`).

---

### Task 1: Model and validator (Rules)

**Files:**
- Modify: `src/Quartermaster.Rules/Model/PhaseDefs.cs` (`PhaseDef.OnEnd`, `MoveEntry.FiresFor`, `public enum MoveSide { Acting, Other, Any }`), `src/Quartermaster.Rules/Model/FormulaDef.cs` (`Phases`)
- Modify: `src/Quartermaster.Rules/Validation/RulesValidator.cs` (OnEnd entries checked exactly like OnStart entries — reuse the OnStart checking code, don't copy it; unknown phase id; hooked-outside-phases warning)
- Modify: any pack-edit helpers that enumerate OnStart for renames/removals (grep `OnStart` in `src/Quartermaster.Rules` and `src/Quartermaster.Render`: e.g. formula delete/rename cleanup, phase duplicate) so OnEnd is handled the same; formula delete also removes its id from nothing else; phase delete removes its id from every `FormulaDef.Phases`
- Test: Rules tests (IO + validator)

**Interfaces:**
- Produces: `PhaseDef.OnEnd : List<StartEntry>?` (`WhenWritingNull`), `MoveEntry.FiresFor : MoveSide` (`WhenWritingDefault`, default `Acting`), `FormulaDef.Phases : List<string>?` (`WhenWritingNull`).
- [ ] **Step 1: Failing tests**: `Pack_without_new_fields_keeps_hash` (pinned SHA before change; no `onEnd`/`firesFor`/`phases` text); `New_fields_round_trip`; `Validator_OnEnd_checked_like_OnStart` (unknown formula in OnEnd → same message as OnStart's with "end"); `Validator_formula_unknown_phase`; `Validator_hook_outside_formula_phases` (start/move/end variants, exact text); `Phase_delete_removes_id_from_formula_phases`; `Formula_delete_removes_OnEnd_entries` (if OnStart entries are removed on delete today).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~OnEnd|FullyQualifiedName~FormulaPhases|FullyQualifiedName~Hash|FullyQualifiedName~Validator"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Rules.Tests; build Core, Render, Desktop, App → PASS / 0 errors.
- [ ] **Step 5: Commit** `feat(rules): end-of-phase entries, fires-for on move entries, formula phase limits`

### Task 2: Core state and helpers

**Files:**
- Modify: `src/Quartermaster.Core/Commands.cs` (`EndPhase`), `src/Quartermaster.Core/GameState.cs` (`EndedAt`, apply `EndPhase`, `AdvancePhase` clears `EndedAt`), `src/Quartermaster.Core/Sequencer.cs` (move unit selection), new `src/Quartermaster.Core/FormulaPhases.cs`; `NativeSave.ValidateState` drops an `EndedAt` that does not match `Position` (as it cleans other stale fields)
- Test: `tests/Quartermaster.Core.Tests/PhaseHooksTests.cs`

**Interfaces:**
- `public sealed record EndPhase(PhasePosition At) : GameCommand` — sets `EndedAt = At`; logs nothing.
- `GameState.EndedAt : PhasePosition?` (`WhenWritingNull`).
- `Sequencer.MovedUnitsFor(RulesPack pack, GameState s, PhaseDef phase, FormulaDef? formula, MoveEntry e) : IReadOnlyList<PieceInstance>` — Acting = today's `UnitsFor(pack, s, phase, formula, e.UnitFilter)`; Other = pieces whose `Ownership` side differs from the phase's side (no side counts as other) passing formula applies-to and entry filter; Any = all pieces passing those; phase without side → same as today for all modes.
- `public static class FormulaPhases { bool Allowed(RulesPack pack, GameState s, FormulaDef f, out string? reason); }` — true when `f.Phases` null/empty or pack has no phases; false before the game starts or when the current phase id is not listed; `reason` = `Only in: {names}`.
- [ ] **Step 1: Failing tests**: `EndPhase_sets_and_Advance_clears`; `EndedAt_round_trips_and_is_omitted_when_null`; `MovedUnitsFor_modes` (Acting/Other/Any with two sides; sideless phase); `FormulaPhases_allowed_cases` (null, listed, not listed, before start, no phases, reason text with names in pack order).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~PhaseHooks"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Core.Tests → PASS.
- [ ] **Step 5: Commit** `feat(core): EndPhase state, move side modes, formula phase limits`

### Task 3: PhaseController (Render)

**Files:**
- Modify: `src/Quartermaster.Render/PhaseController.cs` (`Advance`, a new `OnLeave(PhaseDef)` mirroring `OnEnter`, `OnMoveCore`, stamps)
- Test: `tests/Quartermaster.Render.Tests/` (PhaseController tests)

**Interfaces:**
- Consumes: Task 2 `EndPhase`, `EndedAt`, `MovedUnitsFor`.
- `Advance()`: when the current phase has OnEnd entries and `EndedAt != cur`, inside the press's action: run OnEnd (tag `end`), `Exec(new EndPhase(cur))`; if the OnEnd queue is non-empty → set the stop notice, close the action, raise the queue, return null (no advance); else continue to the existing advance in the same action. When `EndedAt == cur` advance directly. Pass prompt / skip logic unchanged.
- `OnMoveCore`: units from `MovedUnitsFor`; for Other/Any, group units by owner side and apply each group with that side as the trigger stamp (hot-seat `_triggerSide`), so log lines and queued runs carry the owner.
- [ ] **Step 1: Failing tests**: `OnEnd_automatic_is_one_undo_step`; `OnEnd_queued_stops_then_advances_once` (semi-automatic mode; notice exact; second press advances, OnEnd not re-run; undo behaviour per Review Focus 1); `Phase_without_OnEnd_advances_as_before`; `Skipped_phase_runs_no_OnEnd`; `Replay_applies_EndPhase_as_recorded`; `OnMove_other_side_fires_for_non_phasing_piece`; `OnMove_acting_default_unchanged`; `OnMove_other_side_runs_for_owner` (hot-seat stamp = owner side); `OnMove_map_filter_excludes_board_drag` (`map() == "Main"` in entry filter).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Render.Tests -c Release --filter "FullyQualifiedName~PhaseController|FullyQualifiedName~OnEnd|FullyQualifiedName~OnMove"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, then `--filter "FullyQualifiedName~Phase|FullyQualifiedName~HotSeat|FullyQualifiedName~Guest|FullyQualifiedName~Turn|FullyQualifiedName~Move"` → PASS.
- [ ] **Step 5: Commit** `feat(render): end-of-phase entries and other-side move formulas`

### Task 4: Phase limits on player-started runs (Render)

**Files:**
- Modify: `src/Quartermaster.Render/PieceMenuModel.cs` (`AddCalculate`: limited formulas shown disabled with the reason), the formula dialog model (`FormulaDialogModel`: Run disabled with the reason), `src/Quartermaster.Render/Online/IntentExecutor.cs` (refusal before running, next to "That formula does not apply"), any unit-less formula list (Game > Formulas window) — grep for where player runs start
- Test: Render tests

**Interfaces:**
- Consumes: `FormulaPhases.Allowed`.
- [ ] **Step 1: Failing tests**: `Menu_greys_formula_outside_its_phases` (Enabled false, DisabledReason `Only in: Organization`); `Menu_formula_allowed_in_listed_phase`; `Dialog_run_disabled_outside_phases`; `Host_refuses_formula_outside_its_phases` (exact refusal, ledger unchanged); `Hooks_ignore_phase_limit` (an OnStart entry in phase B runs a formula limited to A).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Render.Tests -c Release --filter "FullyQualifiedName~PieceMenu|FullyQualifiedName~FormulaDialog|FullyQualifiedName~Intent|FullyQualifiedName~PhaseLimit"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, wider `--filter "FullyQualifiedName~Formula|FullyQualifiedName~Menu|FullyQualifiedName~Online"` → PASS.
- [ ] **Step 5: Commit** `feat(render): formulas limited to phases are greyed and refused elsewhere`

### Task 5: Workbench, docs, full runs

**Files:**
- Modify: `src/Quartermaster.Desktop/Workbench/PhasesTab.cs` (`On end` grid with add/delete like `On start`; `Fires for` column on the On move grid), `src/Quartermaster.Desktop/Workbench/FormulasTab.cs` (`Only in phases` checkable list + hint; none checked → null)
- Check: the desktop piece menu shows disabled items with their reason as tooltip (existing `DisabledReason` path)
- Modify: `docs/wiki/Making-a-Rules-Pack.md`, `docs/wiki/Window-Rules-Workbench.md`, `docs/wiki/Window-Phases.md`, `docs/superpowers/RESUME.md`
- Test: `tests/Quartermaster.Desktop.Tests/` (`PhasesTab_on_end_grid_commits`, `PhasesTab_fires_for_commits`, `FormulasTab_only_in_phases_commits_and_clears`, `PieceMenu_disabled_formula_tooltip`)
- [ ] **Step 1: Failing tests** (headless).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Desktop.Tests -c Release --filter "FullyQualifiedName~PhasesTab|FullyQualifiedName~FormulasTab|FullyQualifiedName~PieceMenu"` → FAIL.
- [ ] **Step 3: Implement**; wiki (invented names; the stop rule, fires-for, phase limits, exact texts); RESUME new CURRENT STATE (old under Previous).
- [ ] **Step 4: Full runs**, one at a time: Rules, Core, Render, Desktop, Docs → PASS (known flaky: Core `PlayerViewTests.Perf_1200Pieces`, Render `Piece4EndToEndTests.Perf_SyntheticGame_UndoAndStepBack`, Render `LauncherTests.Refresh_InvalidPath`, Desktop `DocumentViewerToolbarTests.Add_tool_item_appends`, TableLookup perf guard — re-run once, report both). Build `src/Quartermaster.App -c Release`.
- [ ] **Step 5: Commit** `feat(desktop): workbench fields for phase hooks; docs`
