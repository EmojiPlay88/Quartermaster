# Formula Reads Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expressions gain `side()`, `phaseside()`, `map()` and `holder(H, P)`; piece filters read global properties by name; a warning or alert whose condition never reads the piece shows once.

**Architecture:** Rules gets four functions (Parser arity, Evaluator dispatch, default `IEvalContext` members that throw) and one AST helper deciding "game-level". Core's `FilterContext` and `RunContext` implement the members (with a recursion guard for `side()` in side unit filters) and filters fall back to the global holder for Holders-scope names; `Decisions.Warnings` and `AlertEngine.User` evaluate game-level conditions once. Validator, desktop display check and wiki follow.

**Tech Stack:** C# .NET 10, xUnit, Avalonia 12 headless.

**Spec:** `docs/superpowers/specs/2026-11-22-formula-reads-design.md`

## Global Constraints

- Public repo, hard boundary: no content from any real game; invented test data only (existing fixtures, wiki demo names such as Riverland).
- Old saves and packs load unchanged; an unchanged pack keeps its SHA-256; `RulesPackIo.CurrentFormat` stays 4; any new serialized member `WhenWritingDefault`/`WhenWritingNull` (none expected).
- No new NuGet packages. Never open visible windows (UI tests `[AvaloniaFact]`).
- One test project at a time, `-c Release`, `--filter` while iterating.
- Never kill/stop processes you did not start. Never `git stash`. Never push.
- Commits: repo-local identity; message ends with a blank line then `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never commit `startapp.cmd`, `.superpowers/`, untracked notes in `docs/`.
- Branch `formula-reads` (from master `58083c6`; spec commit `95c6b09`).
- Exact texts:
  - default member error: `{fn}() is not available here`
  - recursion error: `side() is not available in a side's unit filter`
  - unknown holder error (wherever expressions are validated): `refers to unknown holder "{x}"` (tail, prefixed like the existing `refers to unknown table` messages)
  - validator side filter error: `Side "{name}": unit filter may not use side()`
  - phase conditions reuse `Phase "{name}": condition may not use {fn}()` for `side`, `map`, `holder`
- Returned texts: `side()` and `phaseside()` return the side **Name** (`""` when none); `map()` returns `PieceInstance.MapId`.

## Review Focus

1. **Old per-unit warnings stay per-unit** — any condition reading a Pieces/Both property or a piece function keeps one row per unit. Test in Task 3 (`Piece_warning_stays_per_unit`).
2. **`side()` in a side unit filter, directly or via a property applies-to it reads** → no hang, filter false. Test in Task 2 (`Side_in_unit_filter_does_not_recurse`, `Side_via_applies_to_does_not_recurse`).
3. **A Both-scope name** in a filter keeps reading the piece (not the global), even when the global holder has a value. Test in Task 2 (`Both_scope_reads_piece_not_global`).
4. **Player view hides another side's Private holder value** → `holder(...)` errors → filter false; game-level warning not shown. Test in Task 3 (`Hidden_holder_value_hides_game_level_warning`).
5. **A game-level decision warning** still needs acknowledging and clears when acknowledged. Test in Task 3 (`Game_level_decision_acknowledges`).

---

### Task 1: The four functions and the game-level helper (Rules)

**Files:**
- Modify: `src/Quartermaster.Rules/Expr/Parser.cs` (Arity; `holder` args must both be `VarRef`, as `lookup`'s first; variable collection skips both `holder` args, like `lookup`'s first)
- Modify: `src/Quartermaster.Rules/Expr/Evaluator.cs` (`IEvalContext`, `EvalCall`, `FunctionNames`)
- Modify: `src/Quartermaster.Rules/Expr/WorkingLine.cs` (show `holder(H, P)` like `lookup` if the working display needs a case)
- Create: `src/Quartermaster.Rules/Expr/ExprFacts.cs`
- Modify: `src/Quartermaster.Rules/Validation/RulesValidator.cs` (`ForbiddenConditionFunctions` += `side`, `map`, `holder`)
- Test: `tests/Quartermaster.Rules.Tests/Expr/ReadFunctionsTests.cs`, `EvaluatorRegistryTests.cs`, validator phase-condition tests

**Interfaces:**
- Produces: `IEvalContext` default members `Value Side()`, `Value PhaseSide()`, `Value MapName()`, `Value HolderValue(string holder, string property)` — each throws `EvalError("{fn}() is not available here")`.
- Arity: `side (0,0)`, `phaseside (0,0)`, `map (0,0)`, `holder (2,2)`.
- `public static class ExprFacts { public static bool IsGameLevel(Expr e, RulesPack pack); }` — false if the AST contains a `VarRef` naming a property with scope Pieces or Both, or a call to `name`, `marker`, `has`, `side`, `map`; a `VarRef` that is `lookup`'s table or a `holder` argument is not a property read; unknown names count as piece-level (false).

- [ ] **Step 1: Failing tests**: `Defaults_throw_not_available` (a bare test context: each of the four → `EvalError` with the exact text); `Holder_needs_bare_names` (`holder("A", B)` → ParseError; `holder(A)` → ParseError); `Variables_skip_holder_args` (`Parser` variable list of `holder(Rail, Tonnage) > X` is `[X]`); `IsGameLevel` theory: `Flag == 1` (Flag Holders) true; `holder(Rail, Tonnage) > 3` true; `Fuel > 0` (Pieces) false; `Both1 > 0` (Both) false; `name() == "a"` false; `side() == "North"` false; `phaseside() == "North"` true; `lookup(T, Flag) > 0` true; `Unknown > 0` false; registry test lists the four names; validator: phase condition `side() == "x"` → `Phase "P": condition may not use side()` (same for `map()`, `holder(H, X)`), `phaseside() == "North"` clean.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~ReadFunctions|FullyQualifiedName~EvaluatorRegistry|FullyQualifiedName~PhaseCondition"` → FAIL.
- [ ] **Step 3: Implement** per Interfaces.
- [ ] **Step 4: Run** Step 2 then full Rules.Tests → PASS.
- [ ] **Step 5: Commit** `feat(rules): side(), phaseside(), map() and holder() in expressions`

### Task 2: Filters and formulas read them (Core)

**Files:**
- Modify: `src/Quartermaster.Core/PieceFilter.cs` (`FilterContext`), `src/Quartermaster.Core/FormulaRunner.cs` (`RunContext`), `src/Quartermaster.Core/Ownership.cs` (recursion guard), `src/Quartermaster.Core/PhaseConditions.cs` (`PhaseSide()` = the candidate phase's side name)
- Test: `tests/Quartermaster.Core.Tests/FilterReadsTests.cs`

**Interfaces:**
- Consumes: Task 1 members.
- `Side()` = name of `Ownership.SideOfPiece(p, s, pack)` side, `""` when null. While `Ownership` evaluates side unit filters, `Side()` throws `EvalError("side() is not available in a side's unit filter")` (a `[ThreadStatic]` depth counter or a flag passed into the filter contexts Ownership compiles — builder's choice; must cover applies-to evaluated inside a unit filter).
- `PhaseSide()` = name of `Sequencer.Current(pack, s)?.SideId`'s side, `""` otherwise. `MapName()` = `p.MapId`. `HolderValue(h, prop)`: holder by Name among the pack's holders (globals are read by bare name, not through `holder`; unknown holder → `EvalError("No holder named '{h}'")`), property by name (unknown → existing unknown-property error), value from `LedgerKey.Holder(id)` else Default; in a player view a missing Private value on a sided holder is not defaulted (throw, as `HiddenInView` does for pieces).
- `FilterContext.GetVariable`: Holders-scope property → global holder value else Default. Pieces/Both unchanged.
- `RunContext`: `Side/PhaseSide/MapName/HolderValue` the same (piece from `run.Piece`; no piece → existing "This formula needs a unit" for `side()`/`map()`).

- [ ] **Step 1: Failing tests**: `Filter_reads_side_and_map`; `Filter_reads_phaseside` (current phase side North → `phaseside() == "North"`; no phases → `""`); `Filter_reads_global_by_name` (Holders prop `Weather` set 2 → `Weather == 2` true; unset → Default); `Both_scope_reads_piece_not_global`; `Filter_reads_holder_value` (+ Default fallback, unknown holder false); `Side_in_unit_filter_does_not_recurse` (side North unit filter `side() == "North"` → `Ownership` returns null, no stack overflow); `Side_via_applies_to_does_not_recurse`; `Formula_reads_side_and_holder` (formula `if(side() == phaseside(), 1, 0) + holder(Rail, Tonnage)`); `Hidden_private_holder_in_player_view_is_not_defaulted`; `Condition_phaseside_is_candidate_phase` (PhaseConditions: P2 side South condition `phaseside() == "South"` runs).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~FilterReads|FullyQualifiedName~PhaseCondition"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, then full Core.Tests → PASS.
- [ ] **Step 5: Commit** `feat(core): filters and formulas read side, phase side, map, holders and globals`

### Task 3: Game-level warnings and alerts show once (Core)

**Files:**
- Modify: `src/Quartermaster.Core/Decisions.cs` (`Warnings`), `src/Quartermaster.Core/Alerts.cs` (`User`), `src/Quartermaster.Core/PieceFilter.cs` (add a no-piece evaluation)
- Test: `tests/Quartermaster.Core.Tests/GameLevelAlertTests.cs`

**Interfaces:**
- Consumes: `ExprFacts.IsGameLevel`, Task 2 contexts.
- `PieceFilter.EvaluateGameLevel(string expr, GameState s, RulesPack pack) : bool` — evaluates with a context that has no piece (piece functions throw), globals/holders/turn/stage/phaseside as Task 2; parse/eval error → false.
- Warning hit when game-level and true: `WarningHit(i, msg, PieceIds: [], UnitTexts: [text])` where `text` = msg with any `{unit}` token removed and the result trimmed; `IsDecision`/`Acknowledged` as today.
- Alert hit when game-level and true: one `AlertMatch(null, HolderDef.GlobalId, msg)`; `SideId` gating (acting side) unchanged; the side unit filter is not applied.
- Non-game-level conditions: exactly today's per-piece code path.

- [ ] **Step 1: Failing tests**: `Game_level_warning_shows_once` (3 units, warning `Flag == 1`, Flag=1 → one hit, no piece ids, text = message); `Game_level_warning_false_shows_nothing`; `Piece_warning_stays_per_unit`; `Game_level_alert_shows_once` (one match with HolderId global); `Game_level_alert_respects_side`; `Game_level_decision_acknowledges`; `Hidden_holder_value_hides_game_level_warning` (player view of side A, warning `holder(SouthStore, Fuel) > 0` on a Private prop of South's holder → no hit).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~GameLevel"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Core.Tests, and `dotnet test tests/Quartermaster.Render.Tests -c Release --filter "FullyQualifiedName~Alert|FullyQualifiedName~Warning|FullyQualifiedName~Phase"` → PASS.
- [ ] **Step 5: Commit** `feat(core): warnings and alerts that never read the piece show once`

### Task 4: Validator for filters

**Files:**
- Modify: `src/Quartermaster.Rules/Validation/RulesValidator.cs` (`FilterRefProblems`, side unit filter check)
- Test: Rules validator tests

**Interfaces:**
- `FilterRefProblems`: `holder(H, P)` → unknown holder tail `refers to unknown holder "H"` when no pack holder has Name H; P checked as a property (existing tail); neither arg is a property read otherwise.
- Every side whose `UnitFilter` contains a `side()` call → error `Side "{name}": unit filter may not use side()`.
- [ ] **Step 1: Failing tests**: `Validator_filter_holder_unknown_holder`, `Validator_filter_holder_unknown_property`, `Validator_filter_holder_clean`, `Validator_side_filter_uses_side`, `Validator_filter_new_functions_clean` (`side() == "North" and map() == "Main" and phaseside() == ""` in an applies-to → no issues).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~Validator"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Rules.Tests → PASS.
- [ ] **Step 5: Commit** `feat(rules): validate holder() and side() in filters`

### Task 5: Desktop shows game-level hits, docs, full runs

**Files:**
- Check/modify: `src/Quartermaster.Desktop/Phases/PhasePanel.cs` (warning row with no pieces: shows the text, selecting it selects nothing, Acknowledge works), the Avalonia alert bar (match with no piece: no "go to piece" action)
- Modify: `docs/wiki/Formula-Reference.md` (four functions; "bare names in filters read globals"; game-level rule), `docs/wiki/Making-a-Rules-Pack.md` (recipes: per-side rate table, weather entry filter, capacity warning, phasing modifier, upkeep on main map only), `docs/superpowers/RESUME.md`
- Test: `tests/Quartermaster.Desktop.Tests/` (`PhasePanel_game_level_warning_row`, `AlertBar_game_level_match`)

- [ ] **Step 1: Failing tests** (headless) for the two rows: text shown, no crash on select/go-to, acknowledge clears.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Desktop.Tests -c Release --filter "FullyQualifiedName~PhasePanel|FullyQualifiedName~Alert"` → FAIL (or PASS if already handled — then keep the tests as pins and note it).
- [ ] **Step 3: Implement** any needed guard; write the wiki and RESUME (new CURRENT STATE on top, old under Previous; decisions: names not ids, phaseside instead of seats, game-level rule, recursion error).
- [ ] **Step 4: Full runs**, one at a time: Rules, Core, Render, Desktop, Docs → PASS (known flaky: Core `PlayerViewTests.Perf_1200Pieces`, Render `Piece4EndToEndTests.Perf_SyntheticGame_UndoAndStepBack`, Render `LauncherTests.Refresh_InvalidPath`, Desktop `DocumentViewerToolbarTests.Add_tool_item_appends`, TableLookup perf guard — re-run once, report both). Build `src/Quartermaster.App -c Release`.
- [ ] **Step 5: Commit** `docs: side(), phaseside(), map(), holder() and game-level warnings`
