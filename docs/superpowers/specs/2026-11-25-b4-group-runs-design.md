# Group and repeated runs (batch B4) — design (short)

Date: 2026-11-25. Branch `b4-group-runs` (from master `7f2935c`). Engine wishlist #41, #36, #54, #49 (#55 deferred), plus three
minors carried from B3. The user accepted every default of the batch; the choices below are those defaults, made concrete. Details
the defaults left open are marked "decided". Builds on the batch grid (`BatchModel`, Desktop `BatchGridWindow`), the B2 pass and
`runs()`, and the B3 piece outputs.

## Intent

A formula runs on one counter with its own dice. Big packs work around four gaps:
- the same entry applied to every unit of a formation is one dialog per counter (#41);
- a chart rolled once for a group (one roll per stack) is rolled by the first counter and copied by hand to the others (#36);
- dozens of runs of one formula on one counter (one per match-up) are dozens of dialogs (#54);
- an Input's choices are one fixed list, so a counter is offered choices its rules forbid (#49).

## Design (decided)

Every new pack field is optional and omitted from JSON when empty or default; blank text becomes null on load
(`RulesPackIo.Normalise`). Old packs keep their bytes and SHA-256, `CurrentFormat` stays 4. Old saves keep their bytes: every new
save member is omitted when null or default. Every new expression is parsed and checked by the validator. Every new field gets a
Workbench editor and a wiki section with invented Riverland examples. Public API changes are additive (new members on positional
records are init properties in the record body; new interface members are default methods).

### Pack model (Rules)
- `FormulaDef.GroupRoll : bool` (`WhenWritingDefault`): the formula's dice (every Roll binding and every inline `dice()` key) are
  drawn once per group and reused by the group's other runs.
- `VariableBinding.ChoicesTable : string?` (table id) and `VariableBinding.ChoicesFilter : string?` (expression), Input only.
- New function `row()`: parser arity (0, 0), in `Evaluator.FunctionNames`; `IEvalContext.Row()` is a default method throwing
  `row() can only be used in a choices filter`. Forbidden in phase conditions (`ForbiddenConditionFunctions`).

JSON (invented):
```json
{ "id": "f-ford", "name": "Ford crossing", "groupRoll": true, ... }
{ "variable": "Cargo", "kind": "input", "choicesTable": "t-cargo", "choicesFilter": "lookup(Cargo, row(), \"Min size\") <= Size" }
```

### Groups: which runs share dice (#36, decided)
- **Menu batch** (below): the whole batch is one group.
- **Pending runs** (hook runs from OnStart/OnEnd `ForEach = Units`, OnMove, ask runs; every trigger through
  `PhaseController.Apply`): units in the **same hex and of the same side** share dice. Same side is added to the default so a group
  never mixes sides (no dice and no "these are stacked" fact crosses sides). Group keys are assigned when the run is queued, only for a
  `GroupRoll` formula: new Core `RollGroups.Assign(GameState, RulesPack, ModuleModel, IReadOnlyList<string?> pieceIds)` returns per
  unit `"{sideId}/{k}"` (`"/{k}"` for a piece of no side), k = 1, 2 ... per side in unit order of first appearance of each (map,
  location); a unit off grid or without a piece gets null (rolls its own).
- **Fully automatic loop** (`PhaseController.Apply`): the same grouping, dice kept in a local dictionary per entry.
- A single Formula dialog run on a counter is a group of one.
- Per key: the first run (row order) that rolls a key fixes it for the group; later runs reuse it only when count and sides match
  (a `DiceCountExpr` giving another count rolls its own). A typed single-number roll (a sum) is not shared. Typed physical dice on the
  first run are shared.

### Shared rolls in the runner (Core)
- `RunRequest.SharedRolls : IReadOnlyDictionary<string, IReadOnlyList<int>>?` (body init). `RunScope.RollKey` takes a matching shared
  key before pins and the roller; it is recorded in `RollsByKey` (so `count()`, `dice()` and the working read it) but **not** in
  `Rolls`, so `FormulaRunner.Commands` emits no `RollDice` for it: a group's dice are logged once, by the run that drew them.
- `FormulaTrace.SharedRollKeys : IReadOnlyList<string>` (body init, default empty).
- `FormulaDialogModel.SharedRolls` (passed into every `RunRequest` it builds); a shared Roll box shows the value, is not editable,
  caption suffix ` (group roll)`, tooltip `Rolled once for the group`. Reroll is disabled for a shared key.

### Pending-run state (Core, saves)
- `PendingUnit.Group : string?` (`WhenWritingNull`), `PendingUnit.Removed : bool` (`WhenWritingDefault`).
- `PendingRun.GroupRolls : IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<int>>>?` (`WhenWritingNull`):
  group → key → dice.
- `ResolveRun.GroupRolls` (same type, `WhenWritingNull`) and `ResolveRun.Gone : bool` (`WhenWritingDefault`). `GameState.DoResolve`
  merges `GroupRolls` into the run (an existing key is kept: first wins) and marks the resolved units `Removed` when `Gone`.
- `PhaseController.ApplyFormula` with a run id: when the formula is `GroupRoll` and the unit has a group, its `ResolveRun` carries
  `{group: the trace's rolled (non-shared) keys}`. Undoing that row's action drops them with it (the next row rolls again).
- Every path that runs a pending unit passes `SharedRolls = run.GroupRolls[unit.Group]` (Core helper
  `RollGroups.SharedFor(GameState, string runId, string? pieceId)`): `BatchModel.ModelFor`, Desktop `FormulaWindow` with a `RunLink`,
  App `FormulaDialog` with a run link, `MissingInputs.ApplyAll`, `FormulaIntentRunner.Run`.
- `BatchModel` before any row of a group is applied: the group's first open row (grid order) rolls; the other open rows of the group
  get that row model's pinned rolls as `SharedRolls` (so every row of the group previews the same dice).
- **PlayerView** drops `GroupRolls` from every run in a view (the host decides; a guest never needs them). `Group` stays on the units a
  view keeps (only the viewer's own sides' units, and groups never span sides).
- **Online, pending rows** (per-row intents, unchanged): `FormulaIntentRunner` gives a grouped unit the pin key
  `FormulaPins.Key(who, f.Id, "group:{runId}:{group}")` instead of the unit id, so a guest's previews of two rows of one group show the
  same dice; state `GroupRolls` win over pins once a row is applied.

### #41 / #54 Menu batch
- **Opening it.** `PieceMenuModel.Build` adds, after `Calculate`, a submenu `Calculate on selection` (the clicked counter is in a
  selection of 2+ counters) or `Calculate on stack` (otherwise; its stack as `StackInstance` orders it). Each lists the formulas that
  apply to the clicked counter (`PieceFilter.FormulasFor`) and to at least two counters of the set the viewer may act on
  (`ModuleSession.CanAct`); phase-limited ones are greyed with the reason as under `Calculate`. Rows = those counters, in game order
  (selection) or stack order. New hook `PieceMenuHooks.ShowFormulaBatch : Action<string, IReadOnlyList<string>>?` (formula id, piece
  ids); null leaves both submenus out (the WinForms App sets none: build only).
- The Desktop `FormulaWindow` of a menu run (no `RunLink`) gets a button `Several runs…` (tooltip `Open a batch grid for this
  counter: one row per run, applied in order as one step.`) that opens a menu batch with this counter as row 1, its typed values
  copied in.
- **Model.** New Render `MenuBatchModel` (in-memory, never saved: closing the grid discards it). Rows `(PieceId)` each with a
  `FormulaDialogModel`; an `All rows` line whose Input cells (only Input and Piece columns are editable there) fill every row whose own
  cell is blank; `AddRow(afterRow)` adds a blank row for that row's counter (the last row's without a selection), at most 100 rows;
  `RemoveRow(row)` (the last row cannot be removed). Grid title `{formula} — {n} runs`. Both grid models implement the new interface `IBatchRows` (the members the grid
  window uses: `Title`, `Variables`, `RowCount`, `Row`, `ChoicesFor(variable, row)`, `IsChoiceColumn`, `SetCell`, `CalculateAll`,
  `ApplyAll`, `ModelFor`, `OutputsOf`, `Invalidate`, `HostCalculates`, `OpenRows`); `BatchModel` keeps every member it has.
- **Rows apply in order, each sees the earlier rows** (decided: a preview chain). Evaluating rows clones the state once
  (`GameState.DeepClone`) per invalidation; row i runs on the clone, then its commands (`FormulaRunner.Commands`) are applied to the
  clone (`GameState.Apply`) before row i+1 runs. The chain does not seed a placed piece (`SeedAfterAdd` needs the session); the live
  apply does. A row whose piece an earlier row removed shows Exceptions `removed by an earlier row` and is skipped (it does not block).
- **Group roll.** With `GroupRoll`, the first row that rolls a key (row order) fixes it; the other rows get it as `SharedRolls`.
  Without `GroupRoll` every row rolls its own dice.
- **Apply all = all or nothing, one undo step** (decided). No `Apply row` in a menu batch. If any open row (not removed) cannot apply in
  the chain, nothing is applied: `Nothing applied: row {n} cannot be applied yet.` Otherwise one `GameSession.BeginAction` labelled
  `{formula} on {n} units` (n rows, n distinct counters) or `{formula}: {n} runs` (any counter repeated), and each row in order is run
  on the live state with its pinned dice and applied through the port's `ApplyTrace` (`PhaseController.ApplyFormula` joins the open
  action). No pass is opened (batches are small; no `AfterApply` bookkeeping). A row that still fails live (only a placed piece's
  seeds can differ) stops the loop; the rows applied stay and the status names the row (decided: no automatic undo).
  Status: `Applied {n} run(s) as one step.`
- **Each row is its own run** (B2): its own working line and its own `AppliedRun` stamp, so `runs()` counts every row, and row k's
  `runs()` sees rows before it (they are in the chain's log). A precondition that refuses row k blocks the batch like any row that
  cannot apply.
- **B3 outputs in a batch**: a row's Piece write on a later row's counter is seen by that row; a placed piece never becomes a row; a
  removed counter skips its later rows (above).
- **Hidden information.** Rows are only counters the viewer may act on, listed from the viewer's state (a guest's selection and stack
  are its own view). Each row model is masked as today (`FormulaMasking.Configure`); hot-seat: another viewer re-masks the rows and
  drops cached views (as `BatchModel.CheckViewer`).
- **Online guest: one intent per batch** (decided). New `FormulaBatchIntent(string FormulaId, IReadOnlyList<FormulaBatchRow> Rows,
  bool Calculate, bool Preview)` (`$t` `"formulaBatch"`), `FormulaBatchRow(string? PieceId, IReadOnlyDictionary<string, string> Typed,
  IReadOnlyDictionary<string, string>? Picks)`; the guest merges `All rows` into each row before sending. More than 100 rows or any
  `FormulaIntent` size limit broken: `NotUnderstoodText`. Answer: `FormulaBatchVerdict(bool Applied, IReadOnlyList<FormulaVerdict>
  Rows, string? Message)` in `IntentResult`'s verdict JSON. New `IActionPort.ApplyFormulaBatchAsync(FormulaBatchIntent)` default method
  (refuses with `NotUnderstoodText`); `LocalActions`, `RemoteActions`, `ActionPortSwitch` implement it.
- **Host** (`FormulaBatchRunner`, Render/Online): each row passes exactly the checks of `FormulaIntentRunner.Run` (extracted into an
  internal `Prepare` both use: the sender sees the piece, may act for it, `GuestRule`, applies-to, phase limit, pending-run rule
  `UsePendingText`, pick check); a refused row refuses the batch. Dice are pinned per sender: a row's key
  `FormulaPins.Key(who, f.Id, "batch:{pieceId}#{k}")` (k = occurrence of that counter among the rows), the group's
  `FormulaPins.Key(who, f.Id, "batch:group")`. The host runs the chain on a clone of the full state, answers a preview with each row's
  visible values and dice (as `FormulaIntentRunner` does), and applies only when every row can: one action (inside `c.ActingFor`), rows
  in order. A row's message is `Row {n}: {message}` with the existing neutral texts. The guest's status: `The host applied {n} run(s)
  as one step.` or the host's message.
- Pending-run grids are unchanged (a guest's pending rows stay one intent per row).

### #49 Choices from a table
- An Input with `ChoicesTable` offers that table's row keys (normalised as `KeyChoices` does) for which `ChoicesFilter` is true; no
  filter = every key. `ChoicesTable` and a `Choices` list are exclusive (validator).
- The filter is a unit filter (as `AppliesTo`: the counter's own values, `has()`, `name()`, `side()`, `marker()`, `layer()`, lookups,
  aggregates) plus `row()` = the row key (a number when the key is numeric, else text). Formula variables are not readable (no cycles).
  With no run piece the filter is not applied (every key).
- New Core `InputChoices.For(GameState, RulesPack, ModuleModel, FormulaDef, string variable, string? pieceId)`: these keys, else
  `KeyChoices.For`. Used by `FormulaDialogModel` dropdowns, `BatchModel.ChoicesFor(variable, row)`, `MenuBatchModel`, and the runner.
- Runner (`RunScope.Input`): a typed value not among them is missing `Not one of the choices for this piece`; none left: `No choice for
  this piece`; a filter error on a row: `Choices filter: {message}`. On a player view a row whose filter reads hidden data
  (`EvalError.HiddenData`) is kept (the host decides). The host never names the list in a refusal (the neutral text above).
- Value: as `Choices` (a number when every key is numeric, else the text).

### Carried minors (B3)
- **Double pick**: `RunScope` memoises `Pick(b)` per variable, so a Piece binding and its Piece output resolve once per run (one
  `PlayerView.For` on the host). Internal `[ThreadStatic] PieceChoices.ViewBuilds` counter for the test.
- **Removed counted as applied**: `ResolveGoneUnits` sends `ResolveRun { Gone = true }`; `GameState.BatchSummary` writes
  `{formula}: {n} units, {a} applied, {r} removed` when r > 0 (a excludes removed; unchanged text when r = 0, so old saves replay the
  same). The Fully automatic summary in `PhaseController.Apply` counts units skipped as removed the same way. `BatchModel.Title`:
  `{formula} — {a} of {n} applied, {r} removed` when r > 0.
- **B2 bytes test**: `B2_absent_fields_keep_bytes` loads a hand-written pre-B2 literal (as B3's), compares bytes and a constant SHA-256.

### Validator (every message prefixed `Formula "{f}": `)
- error `variable "{v}" draws its choices from unknown table "{id}"`
- error `variable "{v}" has a choices table but is not an Input`
- error `variable "{v}" has both choices and a choices table`
- error `variable "{v}" draws its choices from table "{name}", whose row keys are ranges`
- warning `variable "{v}" has a choices filter but no choices table`
- error `variable "{v}" choices filter {problem}` (the `ParseProblem` tail), then `FilterRefProblems` tails with the same prefix
- warning `rolls once for the group but has no dice` (`GroupRoll`, no Roll binding and no `dice()` in any source)
- expression tail everywhere else (`FilterRefProblems`, so every existing prefix applies): `row() can only be used in a choices filter`

### Workbench (Avalonia Desktop; the WinForms App builds only)
- Formulas tab: check box `Roll once for the group` beside the precondition, tooltip `Roll the dice once for the group: in a batch
  the first run rolls and the others reuse its dice; for a phase entry, one roll per hex and side.`
- Bindings grid, columns appended after `Same hex` (older columns keep their indexes): `Choices table` (8, combo: blank + table names),
  `Choices filter` (9, text). Tooltips: `Input: offer this table's row keys as the choices.`; `Input with a choices table: list only
  the rows for which this is true; row() is the row key, and the counter's own values can be read.`
- `FormulaEdit.WithGroupRoll`, `WithChoicesTable` (null also clears the filter), `WithChoicesFilter`; blank → null.
- **Wiki**: Making-a-Rules-Pack `## Group and repeated runs` (`### Rolling once for a group`: Riverland `Ford crossing`, one roll per
  hex; `### Choices from a table`: `Barge cargo` from table `Cargo` filtered by `lookup(Cargo, row(), "Min size") <= Size`);
  Window-Formula-Dialog `## Batch grid` (`### Running on several counters`, `### Several runs on one counter`, `All rows`, all or
  nothing); Formula-Reference (`row()`, choices table, group roll, the runtime messages in the error table); Window-Rules-Workbench
  (the new controls); Menus-and-Windows (the two submenus).

## Public API changes
All additive: `FormulaDef.GroupRoll`; `VariableBinding.ChoicesTable/ChoicesFilter`; `IEvalContext.Row` (default method);
`RunRequest.SharedRolls`, `FormulaTrace.SharedRollKeys` (body init); `PendingUnit.Group/Removed`, `PendingRun.GroupRolls`,
`ResolveRun.GroupRolls/Gone` (body init); `RollGroups`, `InputChoices`; `FormulaDialogModel.SharedRolls`; `IBatchRows` (implemented by
`BatchModel`); `MenuBatchModel`; `FormulaBatchIntent`, `FormulaBatchRow`, `FormulaBatchVerdict`; `IActionPort.ApplyFormulaBatchAsync`
(default method); `PieceMenuHooks.ShowFormulaBatch`; `FormulaEdit.WithGroupRoll/WithChoicesTable/WithChoicesFilter`; new
`FormulaWindowRules` texts. No signature changes. Behavioural: the batch summary and Fully automatic summary lines gain `, {r} removed`
only when a unit was removed by another run (impossible in packs without B3 Remove outputs).

## Non-goals
Random picks among records (#55); a menu batch saved with the game; per-row Apply in a menu batch; one-intent-per-batch for pending
grids; groups across sides; a group roll shared across separate menu batches or entries; formula variables in a choices filter; the
WinForms App's menu batch UI.

## Anti-goals
Old packs or saves changing bytes; a guest learning another side's pieces or dice from candidates, rows, groups or refusals; a batch
that undoes in more than one step; dice re-rolled by previewing again or by reordering a guest's rows.

## Approaches
Picked: shared rolls as a run-request input (one mechanism for batch, pending groups and the Fully automatic loop); group dice stored on
the pending run through `ResolveRun` (undoable, saved, host-owned); an in-memory menu batch with a preview chain on a cloned state; one
intent per menu batch.
Rejected: a persistent "menu run" in `GameState.Pending` (save shape, phase-advance interplay); deriving group dice from the log
(fragile once pieces move); one intent per row for menu batches (no single undo step, per-unit pins collide for repeated rows);
`lastroll(key)` in expressions (hidden coupling between unrelated runs).

## Left to the builder
Test layout; where `IBatchRows` and `MenuBatchModel` live in Render; the chain's caching; column widths; wiki prose around the exact
strings.
