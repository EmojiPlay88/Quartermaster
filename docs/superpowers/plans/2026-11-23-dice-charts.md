# Dice Charts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `lookuprange(Table, row, value)` for charts whose cells are ranges, Roll bindings whose dice count is an expression, and `count(R, lo, hi)` over a roll's dice.

**Architecture:** Rules gains the two functions (parser arity, evaluator, two new `IEvalContext` default members), a shared `TableLookup.RangeColumn` helper, `VariableBinding.DiceCountExpr` and validator checks. Core implements the context members (runner, filters, phase conditions) and evaluates `DiceCountExpr`. Desktop Workbench edits the field; wiki documents both.

**Tech Stack:** C# .NET 10, xUnit, Avalonia 12 headless.

**Spec:** `docs/superpowers/specs/2026-11-23-dice-charts-design.md`

## Global Constraints

- Public repo: invented test data only (existing fixtures; wiki demo names such as Riverland).
- Old saves and packs load unchanged; an unchanged pack keeps its SHA-256; `CurrentFormat` stays 4. `VariableBinding.DiceCountExpr : string?` with `WhenWritingNull`; blank → null.
- No new NuGet packages. Never open visible windows (`[AvaloniaFact]`). One test project at a time, `-c Release`, `--filter`.
- Never kill processes you did not start. Never `git stash`. Never push.
- Commits: repo-local identity; message ends with a blank line then a `Co-Authored-By:` trailer naming the model that wrote it. Never commit `startapp.cmd`, `.superpowers/`, untracked notes in `docs/`.
- Branch `dice-charts` (from master `835e070`).
- Exact texts:
  - `No column of {table} row {row} holds {value}` (values shown as `Value.Display()`)
  - `lookuprange needs a two-way table`
  - `lookuprange() is not available here`, `count() is not available here` (default context members)
  - validator: `Formula "{name}": count() needs a roll binding, not {variable}`
  - dice count out of range / not whole: reuse `dice()`'s wording with `dice count` as the argument name
  - Workbench: `Dice count` (hint `Blank = the fixed number.`)

## Review Focus

1. **Overlapping or unsorted cell ranges** → first matching cell in column order wins; open-ended `5+`/`3-` cells match. Test in Task 1 (`LookupRange_first_match_open_ends`).
2. **Pinned dice with a changed count** → the pin is ignored and new dice are rolled (existing rule), count matches the new expression. Test in Task 2 (`DiceCountExpr_pin_with_other_count_ignored`).
3. **Count expression reading a waiting/missing input** → the Roll waits (not rolled, no log entry) until the input is filled. Test in Task 2 (`DiceCountExpr_waits_for_input`).
4. **`count` of a skipped Roll** → the skipped-read error, exact text. Test in Task 2 (`Count_of_skipped_roll_errors`).
5. **lookuprange in a filter/warning** → works through filter and phase contexts. Test in Task 2 (`LookupRange_in_filter_and_phase_condition`).

---

### Task 1: Rules — functions, helper, model, validator

**Files:**
- Modify: `src/Quartermaster.Rules/Expr/Parser.cs` (arity: `lookuprange` 3..3, `count` 3..3), `src/Quartermaster.Rules/Expr/Evaluator.cs` (function names list; `IEvalContext.LookupRange(string table, Value row, Value value)` and `IEvalContext.DiceOf(string variable) : IReadOnlyList<int>` as default members throwing the exact texts; cases `lookuprange` (first arg must be a table name, as `lookup`) and `count` (first arg must be a variable name; lo/hi numbers))
- Modify: `src/Quartermaster.Rules/Tables/TableLookup.cs` (`public static Value RangeColumn(TableDef t, Value row, Value value)`: row located as `Lookup` does; errors exact)
- Modify: `src/Quartermaster.Rules/Model/FormulaDef.cs` (`VariableBinding.DiceCountExpr`), pack IO normaliser (blank → null)
- Modify: `src/Quartermaster.Rules/Validation/RulesValidator.cs` (`lookuprange` table checks reuse `lookup`'s; `count` first arg must be a Roll binding; `DiceCountExpr` parses, variables bound, not self); `FormulaVariables.Used/Sources/Reach` include `DiceCountExpr` like an auto value; validator's constant context may leave the defaults
- Test: Rules tests
- [ ] **Step 1: Failing tests**: `LookupRange_finds_column_key` (row "2", value 5 in cells `1-3`,`4-6`,`7+` → column key); `LookupRange_first_match_open_ends`; `LookupRange_no_match_error` (exact); `LookupRange_needs_two_way_table` (exact); `Count_counts_in_range` (fake context `DiceOf` [1,2,5,6], `count(R,1,2)` = 2); `Count_default_context_not_available` (exact); `Pack_without_dice_count_expr_keeps_hash`; `DiceCountExpr_round_trips`; `Validator_count_needs_roll_binding` (exact); `Validator_dice_count_expr_unknown_variable`; `Validator_lookuprange_unknown_table`; `Used_includes_dice_count_expr_variables`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~LookupRange|FullyQualifiedName~Count|FullyQualifiedName~DiceCount|FullyQualifiedName~Validator|FullyQualifiedName~Used"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Rules.Tests; build Core, Render, Desktop, App → PASS / 0 errors.
- [ ] **Step 5: Commit** `feat(rules): lookuprange, count over a roll's dice, dice count expressions`

### Task 2: Core — contexts and runner

**Files:**
- Modify: `src/Quartermaster.Core/FormulaRunner.cs` (`RunContext.LookupRange` via `TableLookup.RangeColumn`; `RunContext.DiceOf(name)`: resolves `name`, requires a Roll binding, returns its dice (`RollsByKey[name]`); skipped → skipped-read error; Roll `Bind`: count = `DiceCountExpr` evaluated in the run scope when set (whole number 1–100, `dice()` wording) else `DiceCount`); name expansion adds `DiceCountExpr` variables as for auto values
- Modify: `src/Quartermaster.Core/PieceFilter.cs` (both contexts), `src/Quartermaster.Core/PhaseConditions.cs` (`LookupRange` via the helper)
- Test: `tests/Quartermaster.Core.Tests/DiceChartsTests.cs`
- [ ] **Step 1: Failing tests**: `Formula_lookuprange_in_text`; `DiceCountExpr_rolls_that_many` (Input `Planes` = 12 → 12 dice logged as one roll entry); `DiceCountExpr_out_of_range_error`; `DiceCountExpr_pin_with_other_count_ignored`; `DiceCountExpr_waits_for_input`; `Count_two_counts_from_one_roll` (pinned [1,2,3,6]: `count(R,1,2)*10 + count(R,3,3)` = 21; `R` still 12); `Count_of_skipped_roll_errors`; `Count_of_non_roll_errors` (runtime message for a hand-edited pack); `LookupRange_in_filter_and_phase_condition`; `Formula_without_new_features_unchanged`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~DiceCharts"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Core.Tests; Render.Tests `--filter "FullyQualifiedName~Formula|FullyQualifiedName~Intent|FullyQualifiedName~Batch"` → PASS.
- [ ] **Step 5: Commit** `feat(core): dice count expressions, count() and lookuprange in every context`

### Task 3: Workbench, docs, full runs

**Files:**
- Modify: `src/Quartermaster.Desktop/Workbench/FormulasTab.cs` / `FormulasTab.Grids.cs` (`Dice count` expression for Roll bindings beside the fixed number; blank → null; syntax error rejected like `Only when`)
- Check: the formula dialog shows many dice readably (one roll line with all dice); the Render `FormulaEdit.SetKind` keeps or clears `DiceCountExpr` sensibly (cleared when kind leaves Roll)
- Modify: `docs/wiki/Formula-Reference.md` (both functions, errors), `docs/wiki/Making-a-Rules-Pack.md` (range chart + per-plane roll example, invented names), `docs/superpowers/RESUME.md`
- Test: `tests/Quartermaster.Desktop.Tests/` (`FormulasTab_dice_count_commits_and_clears`); Render `FormulaEdit` test for SetKind
- [ ] **Step 1: Failing tests** (headless).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Desktop.Tests -c Release --filter "FullyQualifiedName~FormulasTab"` → FAIL.
- [ ] **Step 3: Implement**; wiki; RESUME new CURRENT STATE (old under Previous).
- [ ] **Step 4: Full runs**, one at a time: Rules, Core, Render, Desktop, Docs → PASS (known flaky: Core `PlayerViewTests.Perf_1200Pieces`, Render `Piece4EndToEndTests.Perf_SyntheticGame_UndoAndStepBack`, Render `LauncherTests.Refresh_InvalidPath`, Desktop `DocumentViewerToolbarTests.Add_tool_item_appends`, Desktop `MainWindowOnlineTests.Host_players_dialog_and_guest_tooltip` — re-run once, report both). Build `src/Quartermaster.App -c Release`.
- [ ] **Step 5: Commit** `feat(desktop): dice count field; docs for dice charts`
