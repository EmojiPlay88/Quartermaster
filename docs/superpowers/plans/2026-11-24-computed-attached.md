# Computed Properties and Formation Aggregates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `PropertyDef.Expr` computed (read-only, evaluated-on-read) properties, and `attachedsum/attachedcount/attachedmax/attachedmin` over the pieces on a piece's organisation chart.

**Architecture:** Rules gains the four functions (as the hex family), `PropertyDef.Expr`, a `PropertyFacts` helper (computed? effectively public?) and validator checks. Core finds a piece's formation display (first SendTo whose map is an org display) inside `PieceAggregates`, and a new shared reader `PropertyValues` evaluates computed properties wherever rules read a Ledger value. Masking switches its visibility checks to `PropertyFacts.IsPublic`; Ledger/Workbench show computed values read-only.

**Tech Stack:** C# .NET 10, Avalonia (Desktop), xUnit.

**Spec:** `docs/superpowers/specs/2026-11-24-computed-attached-design.md`

## Global Constraints

- Public repo: invented test data only (existing fixtures; Riverland demo names). Never copy CNA module text into tests.
- Old packs keep their SHA-256 and results: `Expr` is omitted from JSON when null/empty; `CurrentFormat` stays 4 (as `Precondition` was added). No save-format change.
- No new NuGet packages. Never open visible windows. One test project at a time, `-c Release`, `--filter` while iterating.
- Never kill processes you did not start. Never `git stash`. Never push.
- Commits: repo-local identity; message ends with a blank line then a `Co-Authored-By:` trailer naming the model that wrote it. Never commit `startapp.cmd`, `.superpowers/`, untracked notes in `docs/`.
- Branch `computed-attached` (from master `57d9527`).
- Signatures: `attachedsum(P[, "f"])`, `attachedcount(["f"])`, `attachedmax(P[, "f"])`, `attachedmin(P[, "f"])` — exactly the arity/literal rules of `hexsum`/`hexcount`/`hexmax`/`hexmin`.
- Exact texts:
  - `{piece} has no organisation chart` (piece label as `PieceAggregates.Label`)
  - `{fn}() is not available here`; `No piece matches "{filter}"` (reused)
  - read-time computed error: `{name}: {message}`; cycle at read time: `{name} is computed from itself`; depth > 16: same text
  - validator: `Property "{name}": the expression does not parse: {msg}`;
    `Property "{name}" is computed from itself: {A} → {B} → {A}` (names joined with ` → `);
    `Formula "{f}": output {n} writes computed property "{name}"` (n 1-based, as existing output messages);
    `Property "{name}" is computed; its Default and Sheet field are ignored` (warning);
    `Property "{name}" is Public but is computed from hidden data; players who do not hold every side see it as Private` (warning)
  - manual Ledger edit refusal: `{name} is computed (= {expr}) and cannot be edited`
  - Ledger tooltip of a computed cell: `= {expr}`; on error: cell text `error`, tooltip the error message.

## Review Focus

1. **Computed property inside a filter/applies-to of itself or a sibling** (A's `AppliesTo` reads computed B whose Expr reads A) → error/no match, never a stack overflow. Test in Task 3 (`Computed_cycle_through_applies_to_is_error_not_overflow`).
2. **Player view of a computed property reading a Private value of another side's piece** → no value (missing), not the Default or a partial result. Test in Task 3 (`Computed_from_private_in_player_view_is_missing`).
3. **HQ on the main map with its chart elsewhere** → `attachedcount()` counts the chart's pieces, not the HQ's stack. Test in Task 2 (`Attached_counts_chart_not_stack`).
4. **Piece with several SendTo traits, the first not to an org display** (e.g. a holding box on an ordinary map) → the first org-display one is used. Test in Task 2 (`Attached_uses_first_org_display_sendto`).
5. **Formula output targeting a computed property in an old-but-edited pack** (validator ignored) → the run refuses with the validator text, nothing written. Test in Task 3 (`Run_refuses_output_to_computed`).

---

### Task 1: Rules — attached functions, Expr, PropertyFacts, validator

**Files:**
- Modify: `src/Quartermaster.Rules/Expr/ExprFacts.cs` (`AttachedAggregates` set; `IsAttachedAggregate(string)`; `IsAggregate` includes them; `IsCountAggregate` adds `attachedcount`; new `NeedsPiece(string fn) => IsHexAggregate(fn) || IsAttachedAggregate(fn)`)
- Modify: `src/Quartermaster.Rules/Expr/Parser.cs` (arity table + aggregate shape checks as the hex ones), `Evaluator.cs` (function list, dispatch to `IEvalContext.Aggregate`), `Layout/FormulaLayout.cs`, `Expr/WorkingLine.cs` (every site that lists hex functions)
- Modify: `src/Quartermaster.Rules/Model/RulesPack.cs` (`PropertyDef.Expr : string?`, doc comment), `Io/RulesPackIo.cs` (omit when null/empty; round-trip)
- Create: `src/Quartermaster.Rules/Validation/PropertyFacts.cs`:
  - `public static bool IsComputed(PropertyDef p)` — `!string.IsNullOrWhiteSpace(p.Expr)`
  - `public static bool ReadsHidden(RulesPack pack, PropertyDef p)` — Expr reads a non-Public property (directly or through another computed one, cycle-safe), `holder()`, or any aggregate; unparsable Expr = true
  - `public static bool IsPublic(RulesPack pack, PropertyDef? p)` — `p != null && p.Visibility == Visibility.Public && !(IsComputed(p) && ReadsHidden(pack, p))`
  - `public static IReadOnlyList<string>? CycleFrom(RulesPack pack, PropertyDef p)` — property names of the first cycle through computed properties starting at `p` (closing name repeated), else null
- Modify: `src/Quartermaster.Rules/Validation/RulesValidator.cs` (the five validator texts; `attached*` forbidden in phase conditions like the hex ones — add to `ForbiddenConditionFunctions`; attached arg checks reuse the aggregate checks), `Validation/FormulaVariables.cs` (replace both `Visibility != Public` checks with `!PropertyFacts.IsPublic`)
- Test: `tests/Quartermaster.Rules.Tests/ComputedTests.cs` (new), `AggregateTests.cs`, registry test list
- [ ] **Step 1: Failing tests**: `Attached_parses_like_hex` (arity errors identical in shape to hexsum's); `Attached_forbidden_in_phase_condition`; `Attached_count_is_count_aggregate`; `Expr_round_trips_and_absent_keeps_hash` (pack without Expr: same JSON bytes as before); `IsPublic_false_when_reading_private` / `_holder` / `_aggregate`; `IsPublic_true_for_public_from_public`; `ReadsHidden_through_chain_and_cycle_safe`; `Validator_expr_does_not_parse`; `Validator_cycle_text` (`Property "A" is computed from itself: A → B → A`); `Validator_output_writes_computed`; `Validator_default_ignored_warning`; `Validator_public_from_hidden_warning`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~Computed|FullyQualifiedName~Aggregate|FullyQualifiedName~Registry"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Rules.Tests; build Core, Render, Desktop, App → PASS / 0 errors.
- [ ] **Step 5: Commit** `feat(rules): computed properties and attached aggregates`

### Task 2: Core — formation display and attached aggregates

**Files:**
- Modify: `src/Quartermaster.Core/PieceAggregates.cs`:
  - `public static MapDef? FormationDisplay(PieceInstance p, ModuleModel model)` — first `SendToDef` in `PieceInfo.TypeOf(p).Traits` (prototype-expanded, as `PieceActions` sees them) whose `MapName` resolves (by `Name` or `Id`, as `ResolveSendTo`) to a map with `IsOrgDisplay`; else null
  - in `Evaluate`: `IsAttachedAggregate(fn)` → `self == null || model == null` → `{fn}() is not available here`; no display → `{piece} has no organisation chart`; pieces = `ctx.OnMap(display.Id)`
  - `AggregateFilters.OnMap(string mapId) : IReadOnlyList<PieceInstance>` (index built once, state order; match `MapId` against map Id and Name, as `PieceAccess.MapHidden`)
  - hidden-in-view: same `HiddenInView` path (no change beyond Rules' `IsCountAggregate`)
- Test: `tests/Quartermaster.Core.Tests/AttachedAggregatesTests.cs` (fixture: `FixtureModule.MinimalBuildFile()` plus a second Map marked as an org display the way `ModuleModelBuilder` detects it, and units with `sendto;...` traits — invented names)
- [ ] **Step 1: Failing tests**: `Attachedsum_over_chart_pieces`; `Attached_counts_chart_not_stack`; `Attached_includes_self_on_chart`; `Attached_uses_first_org_display_sendto`; `No_chart_error` (exact text); `Attached_not_available_without_piece` (game-level context, exact); `Attachedmax_empty_error` (exact); `Attached_in_unit_filter_and_warning`; `Attached_hidden_in_player_view_when_pieces_may_hide`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~Attached"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Core.Tests → PASS.
- [ ] **Step 5: Commit** `feat(core): attached aggregates over a piece's organisation chart`

### Task 3: Core — computed property reader and every rules read site

**Files:**
- Create: `src/Quartermaster.Core/PropertyValues.cs`:
  - `public static double? Get(GameState s, RulesPack pack, ModuleModel? model, string key, PropertyDef def)` — stored: `Ledger.Get`; computed: evaluates `Expr` (parse cached per `PropertyDef` via `ConditionalWeakTable`, like `PieceFilter.AppliesToCache`) for the piece (`LedgerKey` piece → piece context = `PieceFilter`'s filter context with model) or holder (holder context: Holders-scope names read *this* holder's value, through `Get` recursively; Pieces-scope names → `This formula needs a unit`); returns null when the computed property does not apply to the piece (its `AppliesTo` false) or the piece is gone; throws `EvalError("{name}: {message}")` on error, preserving `MissingValue`/`HiddenData`
  - `public static double GetOrDefault(...)` — stored: `Ledger.GetOrDefault`; computed: `Get` ?? throw missing (`{name} has no value on this piece`, `MissingValue = true`)
  - re-entry guard: `[ThreadStatic]` set of (key, property id) + depth counter; re-entry or depth > 16 → `EvalError("{name} is computed from itself")`
- Modify (replace direct ledger reads of rules values with `PropertyValues`): `PieceFilter.cs` (`FilterContext.Stored`/`GetVariable`/`Has` — `Has(computed)` = applies to the piece; `GlobalValue`; `HolderRead`), `PhaseConditions.cs`, `FormulaRunner.cs` (Unit/Global/Holder bindings; output `before` reads stay on `Ledger` but an output whose property `IsComputed` throws the validator text), `PieceAggregates.cs` (values), `LedgerTable.cs` (computed columns: value or null; new `LedgerRow.Errors : IReadOnlyDictionary<int,string>?` for error cells), `LedgerBreakdown.cs` (computed: `= {expr}` line, no history), seeding (`Ledger` seeding code: skip computed properties)
- Test: `tests/Quartermaster.Core.Tests/ComputedPropertiesTests.cs`
- [ ] **Step 1: Failing tests**: `Computed_piece_value_from_two_stored`; `Computed_reads_other_computed`; `Computed_holder_reads_its_own_holder`; `Computed_global_in_phase_condition`; `Computed_in_unit_filter_and_binding`; `Computed_in_sum`; `Computed_not_seeded_and_not_stored`; `Computed_error_is_named` (`Cohesion: Division by zero` shape with the evaluator's real message); `Computed_cycle_through_applies_to_is_error_not_overflow`; `Computed_from_private_in_player_view_is_missing`; `Run_refuses_output_to_computed`; `LedgerTable_shows_computed_and_error_cells`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~Computed|FullyQualifiedName~LedgerTable"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Core.Tests (perf tests must hold) → PASS.
- [ ] **Step 5: Commit** `feat(core): computed properties read everywhere rules read values`

### Task 4: Masking, Ledger/Workbench UI, docs, full runs

**Files:**
- Modify: `src/Quartermaster.Core/PlayerView.cs:119-186` (visibility map from `PropertyFacts.IsPublic`), `src/Quartermaster.Render/FormulaMasking.cs:84`, `src/Quartermaster.Core/PieceFilter.cs` `HiddenInView` (use `IsPublic`), `src/Quartermaster.Render/VisibilityEdit.cs` (shows "(computed from hidden data)" beside a Public computed property that reads hidden data)
- Modify: `src/Quartermaster.Render/OutlinerModel.cs` (values via `PropertyValues`), `LedgerViewRules.CheckManualEdit` (refusal text), `src/Quartermaster.Desktop/Ledger/LedgerWindow.cs` (computed cells read-only, tooltip `= {expr}` / error), `src/Quartermaster.Desktop/Workbench/PropertiesTab.cs` (Expression column; Default/Sheet field cells read-only when set), `src/Quartermaster.App` (build only; WinForms Workbench gets the column only if it shares the Render model — otherwise untouched)
- Modify: `docs/wiki/Formula-Reference.md` (attached functions next to the hex ones, "Computed properties" section, errors table rows), `docs/wiki/Rules-Workbench.md` or the page that documents the Properties tab, `docs/superpowers/RESUME.md` (new CURRENT STATE, old under Previous)
- Test: Core `PlayerView_redacts_public_computed_from_private`; Render `Guest_formula_reading_public_computed_from_private_is_masked`, `Manual_edit_of_computed_refused`; Desktop `Ledger_computed_cell_read_only_with_tooltip`, `PropertiesTab_expression_column_greys_default`
- [ ] **Step 1: Failing tests.**
- [ ] **Step 2: Run** Core `--filter "FullyQualifiedName~PlayerView"`, Render `--filter "FullyQualifiedName~Mask|FullyQualifiedName~LedgerView"`, Desktop `--filter "FullyQualifiedName~Ledger|FullyQualifiedName~PropertiesTab"` → FAIL.
- [ ] **Step 3: Implement**; wiki; RESUME.
- [ ] **Step 4: Full runs**, one at a time: Rules, Core, Render, Desktop, Docs → PASS (known flaky: Core `PlayerViewTests.Perf_1200Pieces`, Render `Piece4EndToEndTests.Perf_SyntheticGame_UndoAndStepBack`, Render `LauncherTests.Refresh_InvalidPath`, Desktop `DocumentViewerToolbarTests.Add_tool_item_appends`, Desktop `MainWindowOnlineTests.Host_players_dialog_and_guest_tooltip`, Desktop `TurnsEndToEndTests.Round_trip_through_mailbox` — re-run once, report both). Build `src/Quartermaster.App -c Release`.
- [ ] **Step 5: Commit** `feat: computed properties masked, read-only in Ledger and Workbench; docs`
