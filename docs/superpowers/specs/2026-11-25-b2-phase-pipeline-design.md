# Phase pipeline and timed values (batch B2) — design (short)

Date: 2026-11-25. Branch `b2-phase-pipeline` (from master `1d1f727`). Engine wishlist #4, #32, #72, #56, #71, #31 (#29
and #70 dropped as covered). The user accepted every default of the batch (one question, "Accept all defaults"); the
choices below are those defaults, made concrete. Details the defaults left open are marked "decided".

## Intent

Big packs work around six gaps in how a phase starts and how values last:
- a counter's starting value can only be a sheet row or a fixed Default (#4);
- in Semi-automatic and Hardcore, every start entry picks its units before any entry applies, so an entry that reads what
  an earlier one writes finds nothing; Fully automatic does it in order, so the modes differ (#32);
- a player's choice or roll that a start entry must see first has no place to happen (#72);
- a status that lasts "until the next Supply phase" must be cleared by hand (#56);
- "once per stage / turn / game" caps have no record of uses (#71);
- a ForEach = Units start entry costs about a millisecond per counter (#31).

## Design (decided)

Every new pack field is optional and omitted from JSON when empty. Blank text becomes null on load. Old packs keep their
bytes and SHA-256, and `CurrentFormat` stays 4. Old saves keep their bytes: new state, command and log members are omitted
when null. Every new expression is parsed and checked by the validator. Every new field gets a Workbench editor and a
wiki section with invented Riverland examples. Public API changes are additive: new members on positional records are
init properties in the record body, so existing constructors and `Deconstruct` still compile.

### #4 Starting values from an expression
- `PropertyDef.DefaultExpr : string?`. Piece properties only (Scope Pieces or Both; Both seeds pieces, holders keep
  Default).
- **Seeding.** `LedgerSeeder.SeedPiece` treats a property as seeded when it has a `SheetField` or a `DefaultExpr` (and
  is not computed). A sheet value wins. Without one, the expression is evaluated once per piece in the piece's context
  (`PieceFilter.FilterContext`), after the property's applies-to accepted it. Properties are worked out in pack order, and
  the values accepted so far for that piece are visible (the existing `pending` map), so an expression may read a
  sibling's sheet value or an earlier starting value. The result is stored as a seed (`SeedLedger`, folding, as today).
- **Errors** (decided): an evaluation error, or a result that is not a number (`the starting value is not a number`),
  stores the property's Default as the seed. So each piece is evaluated exactly once. A warning (category `rules`, once per
  property) says `Starting value of "{property}" failed on "{piece}": {error}; it starts at {default}`.
- **What it may read** (decided): the piece (properties, `name()`, `marker()`, `side()`, `map()`), globals, tables
  (`lookup`, `lookuprange`), `turn()`, `stage()`, `phaseside()`. Not `dice`, `holder`, `runs` or any piece aggregate. This
  keeps seeds deterministic on redo (`HistoryController` re-runs `SeedLedgers`). It also stops a seed copying another
  side's hidden values into a counter.
- **Validator** (errors):
  - `Property "{name}" has a starting value expression and is computed; remove one`;
  - `Property "{name}": the starting value expression does not parse: {pe}`;
  - `Property "{name}": starting value {tail}` (the `FilterRefProblems` texts);
  - `Property "{name}": {fn}() is not available in a starting value`;
  - `Property "{name}" has a starting value expression but is not a piece property`.
- **Workbench.** A "Starting value" text column in the properties grid, with a tooltip. **Wiki:** "Starting values from an
  expression" (a `Boat crew` property with `lookup(Crew sizes, marker("Kind"))`).

### #32 Start entries apply strictly in order (deliberate behaviour change)
- **What changes.** In every difficulty, `PhaseController.RunEntries` (OnStart) runs entry N, then picks entry N+1's units
  only once entry N has nothing waiting. An entry "has something waiting" when it queued a run: in Semi-automatic and
  Hardcore always (when it has units), and in Fully automatic when a unit needs input. Then the controller stops. It
  records `GameState.StartCursor = new StartCursor(At: position, Next: N + 1)` with the new command `SetStartCursor`.
- **Continuing.** After an answer, `ApplyFormula` checks for a cursor at the current position and no pending `start` run
  left. When both hold, it runs the entries from `Next` on, inside the same action. So one undo takes back the answer and
  everything it released. It does the same stop/record step and then raises `RunsQueued` after the action closes. The
  trigger stamp is worked out as Next Phase works it out (`AsTrigger(phase)` with the host's `s.ViewSides`, not a guest's
  `ActingFor` sides). Commands carry the answering player as `Who`. When the last entry has run, the cursor is cleared
  (`SetStartCursor(null)`).
- **Before:** Semi-automatic and Hardcore queued one run per entry at once, and Fully automatic queued a needs-input run
  and went on. **After:** one entry's runs at a time in all three modes. Fully automatic with no input needed behaves as
  before.
- **Leaving early.** `AdvancePhase` clears the cursor (reducer). If entries were never run, the controller first logs
  `not run: {formula names}` (Calc, NoWorking, trigger stamp), next to the existing `skipped: …` line for open runs.
- **Edge cases** (decided): a cursor whose `Next` is past the end, after a rules edit, counts as done. An old save with
  several pending start runs has no cursor and plays out as before. OnEnd entries keep today's behaviour (non-goal).
- **Tests that change.** `PhaseControllerTests.SemiAuto_OnStartUnits_QueuesOneRunPerEntry_RaisesRunsQueued` becomes
  `SemiAuto_OnStart_QueuesFirstEntry_NextAfterAnswer`. Any test that expects several start runs queued at once, or a
  Fully automatic needs-input run followed by later entries applying in the same press, is updated to the new order. The
  builder greps `OnStart` in `PhaseControllerTests`, `PhaseControllerHooksTests`, `FormulaMaskingTests`,
  `FormulaGuardsRenderTests`, `PhaseLimitTests`, Desktop `HandoverTests`/`DesktopUpdatesTests` and Docs
  `WalkthroughFormulaTests`, and lists every change in the task report. The downstream pack's turn-walk test pins the old
  two-queue order and will need its follow-up.
- **Wiki:** "Start entries run in order" (Riverland North Supply: Weather roll, then depot delivery, then Fuel supply).

### #72 A side answers a start run
- `StartEntry.AskSideId : string?` (start and end entries). The entry always queues its run for that side, in every
  difficulty, so it is never auto-applied by the host. Under #32 the entries after it wait for the answer.
- `PendingRun.AskSideId` (init property, omitted when null).
  - **PlayerView**: an ask run is kept only for players holding that side.
  - **Applying**: `PhaseController.ApplyFormula` refuses an ask run when `ActorSides` does not hold the side (solo:
    allowed). Local open (`PhaseRunDispatch.OpenPendingRun`) and guest intents (`FormulaIntentRunner.Run` in `Online/IntentExecutor.cs`) refuse with
    `That calculation belongs to a side you don't hold.`
  - **Stamping**: an applied ask run is stamped `SideId = AskSideId`, so its rolls and working follow that side's privacy.
  - **Globals**: in a phase without a side, a guest may already write globals and run unit-less formulas
    (`ActingSideHeld` is true), so the asked side's formula may write globals as it stands.
- Decision text: `{formula} — {side name} answers`.
- **Validator** (errors):
  - `Phase "{p}": {kind} entry {n} asks side "{id}", which does not exist`;
  - `Phase "{p}": {kind} entry {n} asks a side, so it must run once, not for each unit`;
  - `Phase "{p}": {kind} entry {n} asks a side, but only a phase without a side may ask one` (decided: a phase with a side
    already has its player).
- **Workbench.** An "Asks side" combo column in the start and end entry grids. **Wiki:** "Asking a side at the start of a
  phase" (a side-less `Dawn` phase whose `River level roll` asks the Northern League; the next entry reads the global it
  wrote).

### #56 Values that reset at a phase
- `PropertyDef.ResetAt : List<string>?` holds phase ids. Entering one of these phases runs the new command
  `ResetProperties(PropertyIds, Text, At, Who)`, right after `AdvancePhase` and before the start entries. Its reducer
  deletes the listed properties from every ledger (pieces and holders) and removes a ledger left empty. It writes one
  System line, `{phase}: reset {names}`. Ledger history is kept.
- **Re-seeding** (decided): `ModuleSession.SeedLedgers()` runs at once after the reset, so sheet and starting value
  properties start again from their starting value. Others read their Default. Reset, seed and start entries are one Next
  Phase action, so there is one undo step.
- Skipped phases (condition false) are not entered, so they reset nothing. Every block repeat that enters the phase
  resets. `Start()` resets for the first phase. DECISION default "delete": the values are deleted, not set to Default.
- **Hidden information.** The line names properties only: no counts, no pieces. Guests receive the filtered state.
- **Validator** (errors): `Property "{name}" resets at phase "{id}", which does not exist`;
  `Property "{name}" is computed, so it has nothing to reset`.
- **Workbench.** A "Resets at" column (comma-separated phase names; `No phase named "{name}"`). **Wiki:** "Values that
  reset at a phase" (`Ferry used` resets at North Supply and South Supply).

### #71 How often a formula has run: `runs()`
- `runs("formula-id")` or `runs("formula-id", "stage" | "turn" | "game")`. The default period is `"game"` (decided). It
  counts the **applied** runs of that formula on the current piece. In a context with no piece, it counts the formula's
  runs made without a unit. Periods:
  - `"stage"` means the same turn and the same `Position.Repeat` (outside blocks, the same as a turn);
  - `"turn"` means the same turn;
  - `"game"` means every turn.
  Failed, refused and undone runs never count. The run being checked is not counted yet, so `runs("f-raid", "stage") < 1`
  in a precondition allows one per stage.
- **Record.** `LogEntry.AppliedRun : string?` (formula id) and `LogEntry.Stage : int?` (Position.Repeat) are set on
  exactly one line per applied run. That line is the first output's ledger line (`LedgerDelta.AppliedRun`, set by
  `FormulaRunner.Commands`), or, for a run with no outputs, its working line (`AddLogLine.AppliedRun`, set by
  `PhaseController.LogWorking`). Both are omitted when null.
- **Evaluation.** A default `IEvalContext.Runs(formulaId, period)` throws `runs() is not available here`. It is
  implemented in `FormulaRunner.RunContext`, `PieceFilter.FilterContext` and `PieceFilter.GameContext`. A run index is
  cached per `GameState.Log` list. It extends with appended lines and is rebuilt when the list or its count shrinks (undo,
  load). So a ForEach pass does not rescan the log per unit.
- **Hidden information.** `PlayerView` clears `AppliedRun` and `Stage` on a redacted line. In a player view, `runs()` on
  a piece the viewer does not hold, or with no piece, throws `runs() cannot be counted in this view` with
  `HiddenData = true`. So a guest's precondition is undecided and the host decides.
- **Where allowed.** Formula text, binding expressions, preconditions, output expressions, entry filters, warnings and
  alerts. It is not allowed in property applies-to, computed expressions, starting values, side unit filters or phase
  conditions.
- **Validator.** Tails on the existing expression prefix:
  - `runs() needs a formula id in quotes`;
  - `runs() names formula "{id}", which does not exist`;
  - `runs() period must be "stage", "turn" or "game"`.
  The not-available messages are listed in the global constraints. The parser arity is `runs` = (1, 2), and it is added to
  `Evaluator.FunctionNames`.
- **Wiki:** Formula Reference `### runs("formula-id") or runs("formula-id", "period")`, and the recipe "How often a formula
  has run" (`Bridge repair` with precondition `runs("f-bridge-repair", "turn") < 1`).

### #31 Per-unit cost of start entries
- **Parse once.** `FormulaRunner` caches the parsed text, precondition, output sources and binding expressions per
  `FormulaDef` record (`ConditionalWeakTable`, as `PieceFilter.AppliesToCache`). Records are replaced on edit, so the cache
  never goes stale.
- **One pass.** `FormulaRunner.BeginPass(GameState s) : IDisposable` (additive). While it is open, runs on that same
  state share one side map (`PieceFilter.SideMap`) and one `AggregateFilters` (compiled filters, hex and map indexes).
  `pass.AfterApply()` drops any value memo after each unit's commands. Invariant: a pass is open only while the commands
  executed write ledgers. B3 (Place/Remove) must close or refresh it on piece commands.
- **Entry filters.** Side filter, applies-to and entry filter are compiled once per entry (`Sequencer.UnitsFor`, as
  today). `BadFilter` parses each once.
- **One notification per entry** (decided, instead of a new compound command type). `GameSession.DeferChanged() :
  IDisposable` (additive). Commands execute and log exactly as before, so the command log, saves and undo are byte-identical
  to the one-by-one path. `Changed` is raised once, with the last command, when the scope closes. The builder checks every
  `Changed` subscriber (Desktop and App windows, `MainForm`, `ModuleSession`) and confirms none needs each command.
- **Benchmark.** Render `PhasePipelinePerfTests.Perf_StartEntry_1000Units`. It uses an invented 1000-piece fixture in
  Fully automatic: one ForEach = Units entry whose formula has one Table binding and one Unit Set output.
  - Target: < 0.2 ms per unit.
  - Robustness:
    - `[Trait("Category", "Perf")]`;
    - returns early (with a note in the output) in Debug builds and under an attached debugger;
    - one warm-up pass, then the best of 3 timed passes;
    - budget 0.2 ms/unit locally and 1.0 ms/unit when `CI == "true"`.
  - Deterministic companions in Core: `Pass_parses_formula_once` and `Pass_builds_side_map_once` (internal
    `[ThreadStatic]` counters, as `PieceAggregates.FilterCompiles`).

## Public API changes
All additive:
- `PropertyDef.DefaultExpr`, `ResetAt`; `StartEntry.AskSideId`;
- `PendingRun.AskSideId`, `LogEntry.AppliedRun`/`Stage`, `LedgerDelta.AppliedRun`, `AddLogLine.AppliedRun` (body init
  properties);
- `GameState.StartCursor` and `record StartCursor(PhasePosition At, int Next)`;
- commands `ResetProperties`, `SetStartCursor`;
- `IEvalContext.Runs` (default method);
- `FormulaRunner.BeginPass`, `GameSession.DeferChanged`.

There is no unavoidable signature change. The one break is behavioural (#32).

## Non-goals
- Strict order for OnEnd entries, and asking a side in a phase that has one.
- Usage caps declared on a formula (#71's larger shape). `runs()` in preconditions covers the batch.
- Expiry "for N stages" on a property. Reset at a phase covers it with stage repeats.
- Starting values for holders, and re-seeding a property when its expression is edited mid-game (only pieces without a
  value are seeded).
- A new decision kind for entries not yet run.

## Anti-goals
- Old packs or saves changing bytes.
- A seed, reset line, run count or ask run revealing another side's hidden value or piece.
- Two difficulties applying start entries in different orders.
- A perf test that fails on a slow CI runner while the code is fine.

## Approaches
Picked: optional fields on existing records, a state cursor plus continuation in `ApplyFormula` for #32, explicit
one-line-per-run stamps in the log for #71, and deferred notification plus parse/pass caches for #31.

Rejected:
- Queuing all entries and re-filtering on answer (#32): the queue would show runs that may vanish.
- Counting ledger history rows for #71: a run with several outputs counts several times, and the history has no stage.
- A `GameState` usage table for #71: a second record of the same facts, and undo would need to keep it in step.
- A compound `GameCommand` for #31: new save shape, and every command consumer (preview, labels, PlayerView) would need
  to look inside it.
- Setting values to Default on reset (#56): deletion lets sheet and starting values come back and keeps ledgers small.

## Left to the builder
- Test layout.
- The run-index type and where it lives (Core, internal).
- The exact `BeginPass` plumbing through `PhaseController.Apply`.
- The column widths.
- The wording of wiki prose, around the exact strings.
