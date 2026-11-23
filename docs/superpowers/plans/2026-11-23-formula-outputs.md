# Formula Outputs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Each formula output may have its own value expression (same run, same dice), and an output's property may be picked at run time from a validated list.

**Architecture:** Rules gains `FormulaOutput.Expr`, `PropertyChoice`, `PropertySelect`, validator checks, and `FormulaVariables` support. Core's runner computes every output's value and property before anything is written. Render treats `PropertyChoice` wherever outputs' properties matter; Desktop Workbench edits the fields.

**Tech Stack:** C# .NET 10, xUnit, Avalonia 12 headless.

**Spec:** `docs/superpowers/specs/2026-11-23-formula-outputs-design.md`

## Global Constraints

- Public repo: invented test data only (existing fixtures; wiki demo names such as Riverland).
- Old saves and packs load unchanged; an unchanged pack keeps its SHA-256; `CurrentFormat` stays 4. New members `FormulaOutput.Expr : string?`, `FormulaOutput.PropertyChoice : List<string>?`, `FormulaOutput.PropertySelect : string?`, all `WhenWritingNull`; blank → null, empty list → null.
- No new NuGet packages. Never open visible windows (`[AvaloniaFact]`). One test project at a time, `-c Release`, `--filter`.
- Never kill processes you did not start. Never `git stash`. Never push.
- Commits: repo-local identity; message ends with a blank line then a `Co-Authored-By:` trailer naming the model that wrote it. Never commit `startapp.cmd`, `.superpowers/`, untracked notes in `docs/`.
- Branch `formula-outputs` (from master `af9c12e`).
- Exact texts:
  - `The value for {property} is not a number`
  - `{value} is not one of the output's properties` (value via `Value.Display()`)
  - validator: `Formula "{name}": output {n} has a property list but no "Pick by"` (n 1-based)
  - Workbench: `Value` (hint `Blank = the formula's result.`), `Choose from`, `Pick by`

## Review Focus

1. **All-or-nothing** → if any output's expression or pick fails, the trace has an error and no output previews, so nothing is written. Test in Task 2 (`Failing_second_output_writes_nothing`).
2. **Same dice** → a Roll read by the text and by two output expressions is rolled once; all three see the same dice. Test in Task 2 (`Output_exprs_share_the_run_dice`).
3. **Pick by an Input choice text** → choice `Slot B` picks property named `Slot B` (case/whitespace-insensitive). Test in Task 2 (`Pick_by_choice_name`).
4. **Old formulas unchanged** → identical trace, working line and outputs. Test in Task 2 (`Formula_without_new_fields_unchanged`).
5. **Deleting a property listed in a PropertyChoice** → removed from the list (and the list nulled when empty), and "formulas using this property" finds it. Test in Task 3 (`Property_delete_cleans_choice_lists`).

---

### Task 1: Rules — model, validator, variables

**Files:**
- Modify: `src/Quartermaster.Rules/Model/FormulaDef.cs` (`FormulaOutput.Expr`, `PropertyChoice`, `PropertySelect`), pack IO normaliser (`RulesPackIo` ~296: blank → null, empty list → null, drop null ids)
- Modify: `src/Quartermaster.Rules/Validation/RulesValidator.cs` (`CheckOutput`: each listed id checked as `Property` is; expression/selector parse + bound variables reusing the text's checks; the exact "no Pick by" error)
- Modify: `src/Quartermaster.Rules/Validation/FormulaVariables.cs` (`Used`/`Sources`/`Reach`/`HolderReads` include output `Expr` and `PropertySelect` variables, transitively, like guard expressions; formulas without them give identical lists)
- Test: Rules tests
- [ ] **Step 1: Failing tests**: `Pack_without_output_fields_keeps_hash`; `Output_fields_round_trip`; `Validator_output_choice_unknown_property`; `Validator_output_choice_without_pick_by` (exact); `Validator_output_expr_unknown_variable`; `Used_includes_output_expr_and_pick_by`; `Used_unchanged_without_output_fields`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~Output|FullyQualifiedName~Validator|FullyQualifiedName~Used|FullyQualifiedName~Hash"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Rules.Tests; build Core, Render, Desktop, App → PASS / 0 errors.
- [ ] **Step 5: Commit** `feat(rules): per-output values and properties picked at run time`

### Task 2: Core — runner

**Files:**
- Modify: `src/Quartermaster.Core/FormulaRunner.cs` (name expansion adds output `Expr`/`PropertySelect` variables; output loop: value = `Expr` evaluated in the run scope or the result; property = fixed or picked; every output computed before the trace is returned; per-output working parts show each output's own value; skipped/waiting/missing semantics as for any read)
- Test: `tests/Quartermaster.Core.Tests/FormulaOutputsTests.cs`
- [ ] **Step 1: Failing tests**: `Output_expr_gives_its_own_value`; `Blank_expr_takes_the_result`; `Output_exprs_share_the_run_dice`; `Output_expr_not_number_error` (exact); `Pick_by_number`; `Pick_by_choice_name`; `Pick_out_of_list_error` (exact); `Failing_second_output_writes_nothing`; `Working_line_shows_each_value`; `Output_expr_reads_waiting_input_waits`; `Formula_without_new_fields_unchanged`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~FormulaOutputs"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Core.Tests; Render.Tests `--filter "FullyQualifiedName~Formula|FullyQualifiedName~Batch|FullyQualifiedName~Intent|FullyQualifiedName~Mask"` → PASS.
- [ ] **Step 5: Commit** `feat(core): per-output values and picked properties in the runner`

### Task 3: Render, Workbench, docs, full runs

**Files:**
- Modify: `src/Quartermaster.Render/FormulaEdit.cs` (~402 "formulas using property" includes `PropertyChoice`; property delete cleanup removes ids from `PropertyChoice`, nulling an emptied list; output edit helpers for the new fields), `src/Quartermaster.Render/FormulaMasking.cs` (an output whose expression/selector reads hidden data is masked like the text — check `HolderReads` coverage from Task 1 suffices; any per-output target masking that reads `o.Property` also considers `PropertyChoice`)
- Modify: `src/Quartermaster.Desktop/Workbench/FormulasTab.Grids.cs` (outputs grid: `Value` column with hint; `Choose from` multi-property picker and `Pick by` expression; blank → null; syntax error rejected like `Only when`)
- Modify: `docs/wiki/Formula-Reference.md`, `docs/wiki/Making-a-Rules-Pack.md` (invented example: one roll → several supply losses; pick a slot by an input), `docs/wiki/Window-Rules-Workbench.md`, `docs/superpowers/RESUME.md`
- Test: Render tests (`Property_delete_cleans_choice_lists`, `Formulas_using_property_finds_choice`), Desktop tests (`FormulasTab_output_value_commits_and_clears`, `FormulasTab_output_choice_commits_and_clears`)
- [ ] **Step 1: Failing tests.**
- [ ] **Step 2: Run** Render `--filter "FullyQualifiedName~FormulaEdit"`, Desktop `--filter "FullyQualifiedName~FormulasTab"` → FAIL.
- [ ] **Step 3: Implement**; wiki; RESUME new CURRENT STATE (old under Previous).
- [ ] **Step 4: Full runs**, one at a time: Rules, Core, Render, Desktop, Docs → PASS (known flaky: Core `PlayerViewTests.Perf_1200Pieces`, Render `Piece4EndToEndTests.Perf_SyntheticGame_UndoAndStepBack`, Render `LauncherTests.Refresh_InvalidPath`, Desktop `DocumentViewerToolbarTests.Add_tool_item_appends`, Desktop `MainWindowOnlineTests.Host_players_dialog_and_guest_tooltip` — re-run once, report both). Build `src/Quartermaster.App -c Release`.
- [ ] **Step 5: Commit** `feat(desktop): output value and property choice in the workbench; docs`
