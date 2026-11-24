# Piece Sums Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Aggregate functions over pieces (`sum`, `countof`, `maxof`, `minof`) and over this piece's hex (`hexsum`, `hexcount`, `hexmax`, `hexmin`), rules-correct with hidden data masked for guests.

**Architecture:** Rules gains the function names, parser arity/literal checks, evaluator cases dispatching to a new `IEvalContext.Aggregate` default member, layout/working-line support and validator checks. Core implements `Aggregate` once (shared helper over `GameState` pieces, Ledger values, `PieceFilter.Compile`, `PieceInfo.Location`) for the runner, filter, game and phase contexts. Render/Core masking treat aggregates as hidden reads for viewers who do not hold every side. Docs.

**Tech Stack:** C# .NET 10, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-23-piece-sums-design.md`

## Global Constraints

- Public repo: invented test data only (existing fixtures; wiki demo names such as Riverland).
- No model or save change; old packs keep their SHA-256 and results; `CurrentFormat` stays 4.
- No new NuGet packages. Never open visible windows. One test project at a time, `-c Release`, `--filter`.
- Never kill processes you did not start. Never `git stash`. Never push.
- Commits: repo-local identity; message ends with a blank line then a `Co-Authored-By:` trailer naming the model that wrote it. Never commit `startapp.cmd`, `.superpowers/`, untracked notes in `docs/`.
- Branch `piece-sums` (from master `9f541e5`).
- Exact texts:
  - `No piece matches "{filter}"` (maxof/minof/hexmax/hexmin over nothing)
  - `{piece} is not in a hex` (piece label as the log shows it)
  - `{fn}() is not available here`
  - validator: `Formula "{name}": {fn}() names unknown property "{p}"`; `Formula "{name}": the filter of {fn}() does not parse: {msg}` (and the same shapes with the existing prefixes used for filters/warnings/alerts/phase conditions)
- Signatures: `sum(P, "f")`, `countof("f")`, `maxof(P, "f")`, `minof(P, "f")`, `hexsum(P[, "f"])`, `hexcount(["f"])`, `hexmax(P[, "f"])`, `hexmin(P[, "f"])`; P is a bare property name; the filter must be a text literal.

## Review Focus

1. **Hex includes the piece itself; filter can exclude it** → `hexcount()` on a lone piece = 1. Test in Task 2 (`Hexcount_includes_self`).
2. **Unset Ledger values read as the property default** → sums use defaults, like a Unit binding. Test in Task 2 (`Sum_uses_defaults`).
3. **Aggregate inside a unit filter** (quadratic) → correct result; compiled filter cached (no recompile per piece). Test in Task 2 (`Aggregate_in_filter_compiles_once`).
4. **Guest masking** → a guest's dialog for a formula using `sum` of a private property is masked; PlayerView redacts its working line for that viewer; host applies the true value. Test in Task 3 (`Guest_aggregate_masked`, `PlayerView_redacts_aggregate_working`).
5. **Old packs' speed** → existing perf tests (`Perf_1200Pieces`) unchanged; aggregate functions add no per-evaluation cost when unused. Covered by Task 2/3 full runs.

---

### Task 1: Rules — functions, parser, layout, validator

**Files:**
- Modify: `src/Quartermaster.Rules/Expr/Parser.cs` (arity: sum 2, countof 1, maxof 2, minof 2, hexsum 1..2, hexcount 0..1, hexmax 1..2, hexmin 1..2; property argument must be a bare name; filter argument must be a text literal — parse errors like `lookup needs a table name`), `Parser.Variables` must not treat the property name as a variable
- Modify: `src/Quartermaster.Rules/Expr/Evaluator.cs` (function names list; `IEvalContext.Aggregate(string fn, string? property, string? filter) : Value` default member throwing `{fn}() is not available here`; cases dispatching to it)
- Modify: layout/working line/ExprFacts/KeyChoices/LedgerTabs-style sites that special-case bare-name arguments (as `has`/`lookup` are): property name is not a variable box; working line prints the name and the filter literal
- Modify: `src/Quartermaster.Rules/Validation/RulesValidator.cs` (property exists; filter parses via the same compile check used for unit filters; with each context's existing message prefix)
- Test: Rules tests (parser arity/literal errors, evaluator dispatch with a fake context, validator texts, `Variables` excludes property names, registry test list, layout/working line)
- [ ] **Step 1: Failing tests.**
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~Aggregate|FullyQualifiedName~Validator|FullyQualifiedName~Registry|FullyQualifiedName~Layout|FullyQualifiedName~Working"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Rules.Tests; build Core, Render, Desktop, App → PASS / 0 errors.
- [ ] **Step 5: Commit** `feat(rules): aggregate functions over pieces and hexes`

### Task 2: Core — shared helper and contexts

**Files:**
- Create: `src/Quartermaster.Core/PieceAggregates.cs` (`public static Value Evaluate(string fn, string? property, string? filter, GameState s, RulesPack pack, ModuleModel? model, PieceInstance? self, Func<string, Func<PieceInstance,bool>> compile)`; hex = same `MapId` and same non-null `PieceInfo.Location`; values via the Ledger read a Unit binding uses (defaults when unset); exact errors)
- Modify: `src/Quartermaster.Core/FormulaRunner.cs` (`RunContext.Aggregate`, self = the run's piece), `src/Quartermaster.Core/PieceFilter.cs` (filter context: self = piece being filtered; game context: no self), `src/Quartermaster.Core/PhaseConditions.cs` (no self), any other `IEvalContext` that evaluates pieces (warnings/alerts) — every context that can evaluate them; compiled filters cached per filter text per context instance
- Test: `tests/Quartermaster.Core.Tests/PieceAggregatesTests.cs`
- [ ] **Step 1: Failing tests**: `Sum_over_filter`; `Sum_uses_defaults`; `Countof_empty_filter_counts_all`; `Maxof_no_match_error` (exact); `Hexsum_same_map_same_hex_only`; `Hexcount_includes_self`; `Hex_piece_off_grid_error` (exact); `Hex_not_available_in_phase_condition` (exact); `Aggregate_in_filter_compiles_once`; `Aggregate_in_warning_and_alert`; `Formula_aggregate_in_text`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~PieceAggregates"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Core.Tests → PASS.
- [ ] **Step 5: Commit** `feat(core): piece and hex aggregates in every context`

### Task 3: Masking, docs, full runs

**Files:**
- Modify: `src/Quartermaster.Render/FormulaMasking.cs` (`HiddenVariables`: a formula whose text/guards/outputs/auto values use an aggregate over a non-public property, or any count, is hidden for a viewer who does not hold every side — add the existing `HolderCallInput`-style marker), `src/Quartermaster.Core/PlayerView.cs` (`_hiddenInputFormulas` includes such formulas); a shared Rules helper `FormulaVariables.AggregateReads(f)` (fn, property) used by both
- Modify: `docs/wiki/Formula-Reference.md` (all eight functions, errors, hidden-data note), `docs/wiki/Making-a-Rules-Pack.md` (invented examples: stack has an engineer; side's total trucks; inline code if not a demo-pack formula), `docs/superpowers/RESUME.md`
- Test: Render (`Guest_aggregate_masked`, `Host_aggregate_not_masked`, `Aggregate_public_property_sum_not_masked`), Core (`PlayerView_redacts_aggregate_working`)
- [ ] **Step 1: Failing tests.**
- [ ] **Step 2: Run** Render `--filter "FullyQualifiedName~Mask"`, Core `--filter "FullyQualifiedName~PlayerView"` → FAIL.
- [ ] **Step 3: Implement**; wiki; RESUME new CURRENT STATE (old under Previous).
- [ ] **Step 4: Full runs**, one at a time: Rules, Core, Render, Desktop, Docs → PASS (known flaky: Core `PlayerViewTests.Perf_1200Pieces`, Render `Piece4EndToEndTests.Perf_SyntheticGame_UndoAndStepBack`, Render `LauncherTests.Refresh_InvalidPath`, Desktop `DocumentViewerToolbarTests.Add_tool_item_appends`, Desktop `MainWindowOnlineTests.Host_players_dialog_and_guest_tooltip`, Desktop `TurnsEndToEndTests.Round_trip_through_mailbox` — re-run once, report both). Build `src/Quartermaster.App -c Release`.
- [ ] **Step 5: Commit** `feat: aggregates masked for guests; docs`
