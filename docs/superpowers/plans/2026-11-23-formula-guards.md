# Formula Guards Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Formulas that refuse their run when a precondition is false, and bindings (rolls, inputs) used only when a condition holds.

**Architecture:** Rules gains `FormulaDef.Precondition`/`RefusalMessage` and `VariableBinding.When`, plus validator checks. Core's `FormulaRunner` checks the precondition before the text and skips bindings whose `When` is false; a refused trace is an ordinary failed trace (`Error`) flagged `Refused`, so every existing "failed trace changes nothing" path refuses for free. Render shows `not used` boxes and the refusal; Desktop Workbench edits the fields.

**Tech Stack:** C# .NET 10, xUnit, Avalonia 12 headless.

**Spec:** `docs/superpowers/specs/2026-11-23-formula-guards-design.md`

## Global Constraints

- Public repo: invented test data only (existing fixtures; wiki demo names such as Riverland).
- Old saves and packs load unchanged; an unchanged pack keeps its SHA-256; `CurrentFormat` stays 4. New members `FormulaDef.Precondition : string?`, `FormulaDef.RefusalMessage : string?`, `VariableBinding.When : string?`, all `WhenWritingNull`; the Workbench stores null for blank text, never "".
- No new NuGet packages. Never open visible windows (`[AvaloniaFact]`). One test project at a time, `-c Release`, `--filter`.
- Never kill processes you did not start. Never `git stash`. Never push.
- Commits: repo-local identity; message ends with a blank line then a `Co-Authored-By:` trailer naming the model that wrote it. Never commit `startapp.cmd`, `.superpowers/`, untracked notes in `docs/`.
- Branch `formula-guards` (from master `32c7531`).
- Exact texts:
  - default refusal: `{formula name} cannot be run now`
  - skipped binding read: `{variable} is not used here: its "Only when" condition is false`
  - dialog box text for a skipped binding: `not used`
  - validator: `Formula "{name}": the precondition cannot use the roll {variable}`; `Formula "{name}": the precondition cannot use dice()`
  - precondition not boolean: same wording as an `if` condition that is not true/false (reuse the evaluator's error)
  - Workbench labels: `Only runs when`, `Refusal message`, binding column `Only when`

## Review Focus

1. **Refusal spends nothing** → a refused run produces no `RollDice` and no `LedgerDelta`, and dice pinned for the text are not consumed. Test in Task 2 (`Refused_run_rolls_and_writes_nothing`).
2. **Input decides another input** → `B` has `When = "A == 2"`; with `A` empty, `B` is neither asked nor missing until `A` is filled; with `A = 1`, `B` shows `not used` and the run completes. Test in Task 2 (`When_depends_on_input`).
3. **Skipped variable read on the taken branch** → exact error, no outputs. Test in Task 2 (`Skipped_binding_read_fails`).
4. **Guest through host** → host intent of a refused formula is not Ok with the message; ledger unchanged. Test in Task 3 (`Host_refuses_precondition`).
5. **Hook run refused / queued run refused** → OnStart unit logs the message and applies nothing; a queued run stays pending. Test in Task 3 (`Hook_refused_unit_logs_message`, `Queued_refused_run_stays_pending`).

---

### Task 1: Model and validator (Rules)

**Files:**
- Modify: `src/Quartermaster.Rules/Model/FormulaDef.cs` (`Precondition`, `RefusalMessage` on `FormulaDef`; `When` on `VariableBinding`)
- Modify: `src/Quartermaster.Rules/Validation/RulesValidator.cs` (`CheckFormula`: parse both expressions and check their variables exactly as the text's are checked — reuse that code; roll/`dice()` errors for the precondition)
- Modify: the pack IO normaliser if it rewrites `FormulaDef`/`VariableBinding` (keep nulls; blank → null)
- Test: Rules tests

**Interfaces:**
- Produces: `FormulaDef.Precondition : string?`, `FormulaDef.RefusalMessage : string?`, `VariableBinding.When : string?` (all `WhenWritingNull`).
- [ ] **Step 1: Failing tests**: `Pack_without_guards_keeps_hash` (SHA pinned before the change; JSON contains no `precondition`/`refusalMessage`/`when`); `Guards_round_trip`; `Validator_precondition_parse_error`; `Validator_precondition_unknown_variable`; `Validator_when_unknown_variable`; `Validator_precondition_uses_roll` (exact text); `Validator_precondition_uses_dice` (exact text).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~Guard|FullyQualifiedName~Validator"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Rules.Tests; build Core, Render, Desktop, App → PASS / 0 errors.
- [ ] **Step 5: Commit** `feat(rules): formula preconditions and bindings used only when`

### Task 2: Runner (Core)

**Files:**
- Modify: `src/Quartermaster.Core/FormulaRunner.cs` (`FormulaTrace.Refused : bool` (default false, last positional parameter or init property); precondition check in `RunCore` after parse, before resolving the text's variables; `When` check in `RunScope.Resolve`/`Bind`)
- Test: `tests/Quartermaster.Core.Tests/FormulaGuardsTests.cs`

**Interfaces:**
- Consumes: Task 1 members.
- Produces: `FormulaTrace.Refused`; a refused trace has `Error = RefusalMessage ?? "{Name} cannot be run now"`, `Result = null`, no outputs, `Refused = true`; `Commands(...)` returns nothing for it (existing `Error != null` rule).
- `VariableState` for a skipped binding: `Value = null`, `MissingReason = null`, plus `Skipped : bool = true` (new member, default false). Skipped states never count as missing (line ~90 `states.Any(x => x.MissingReason != null)` unchanged in meaning). `ValueOf` on a skipped state throws `EvalError` with the exact skipped-read text.
- Precondition evaluation uses the same `RunScope` (so the variables it reads are resolved once and shown); a `When` that reads a missing Input makes the guarded binding wait too (not missing itself, not skipped): treat "condition unknown" as "not yet asked" — state `MissingReason = null`, `Skipped = false`, `Value = null`, and the run returns without a result like a missing input does (dialog shows nothing for it).
- [ ] **Step 1: Failing tests**: `Precondition_true_runs_as_before`; `Precondition_false_refused_with_message`; `Precondition_false_default_message` (exact text); `Refused_run_rolls_and_writes_nothing` (Roll binding in text + pinned dice: no `RollDice`, no delta, `RollsByKey` lacks the key); `Precondition_not_boolean_is_error`; `When_false_roll_not_rolled` (no log dice entry); `When_true_roll_rolled`; `When_false_input_not_missing`; `When_depends_on_input` (Review Focus 2); `Skipped_binding_read_fails` (exact text); `Formula_without_guards_unchanged` (an existing fixture formula: identical trace before/after).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~FormulaGuards"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Core.Tests → PASS.
- [ ] **Step 5: Commit** `feat(core): refused runs and bindings used only when`

### Task 3: Dialog, host, hooks (Render)

**Files:**
- Modify: `src/Quartermaster.Render/FormulaDialogModel.cs` (skipped variable box: text `not used`, auto background, not editable; refused trace: status = the message, Apply disabled — use the existing failed-trace status path if it already shows `Error`)
- Check (change only if needed): `src/Quartermaster.Render/Online/IntentExecutor.cs` (a refused trace returns not Ok with the message as its text), `src/Quartermaster.Render/PhaseController.cs` (failed trace in hooks already logs `{f.Name}: {unit}: {Error}` and applies nothing; `ApplyFormula` keeps the run pending on a failed trace), `PieceMenuModel` (menu item stays enabled — the refusal is shown when run)
- Test: Render tests (`FormulaGuardsRenderTests.cs`)

**Interfaces:**
- Consumes: Task 2 `FormulaTrace.Refused`, `VariableState.Skipped`.
- [ ] **Step 1: Failing tests**: `Dialog_shows_not_used_box`; `Dialog_refused_status_and_no_apply`; `Host_refuses_precondition` (exact message, ledger unchanged); `Hook_refused_unit_logs_message` (OnStart, fully automatic: log line contains the message, no delta); `Queued_refused_run_stays_pending` (semi-automatic: apply refused, `IsPending` still true); `Dialog_when_input_hides_until_answered` (Review Focus 2 at dialog level: B box absent/blank while A empty, `not used` when A = 1).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Render.Tests -c Release --filter "FullyQualifiedName~FormulaGuards"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, then `--filter "FullyQualifiedName~Formula|FullyQualifiedName~Intent|FullyQualifiedName~Phase|FullyQualifiedName~Batch"` → PASS.
- [ ] **Step 5: Commit** `feat(render): not-used boxes and refused runs`

### Task 4: Workbench, docs, full runs

**Files:**
- Modify: `src/Quartermaster.Desktop/Workbench/FormulasTab.cs` (`Only runs when` and `Refusal message` text fields with the formula's other fields; `Only when` column in the bindings grid; blank → null)
- Check: the Desktop formula window renders the `not used` box and refusal status from the model (no new code expected)
- Modify: `docs/wiki/Making-a-Rules-Pack.md`, `docs/wiki/Window-Rules-Workbench.md`, the wiki page describing the formula dialog, `docs/superpowers/RESUME.md`
- Test: `tests/Quartermaster.Desktop.Tests/` (`FormulasTab_precondition_commits_and_clears`, `FormulasTab_binding_when_commits_and_clears`)
- [ ] **Step 1: Failing tests** (headless).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Desktop.Tests -c Release --filter "FullyQualifiedName~FormulasTab"` → FAIL.
- [ ] **Step 3: Implement**; wiki (invented names; refused runs with default message, `Only when` with the input-decides-input example, the skipped-read error, the precondition-cannot-roll rule); RESUME new CURRENT STATE (old under Previous).
- [ ] **Step 4: Full runs**, one at a time: Rules, Core, Render, Desktop, Docs → PASS (known flaky: Core `PlayerViewTests.Perf_1200Pieces`, Render `Piece4EndToEndTests.Perf_SyntheticGame_UndoAndStepBack`, Render `LauncherTests.Refresh_InvalidPath`, Desktop `DocumentViewerToolbarTests.Add_tool_item_appends` — re-run once, report both). Build `src/Quartermaster.App -c Release`.
- [ ] **Step 5: Commit** `feat(desktop): workbench fields for formula guards; docs`
