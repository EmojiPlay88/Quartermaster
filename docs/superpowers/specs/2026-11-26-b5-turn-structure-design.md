# Turn structure and end of game (batch B5) — design (short)

Date: 2026-11-26. Branch `b5-turn-structure` (from master `7993778`). Engine wishlist #6, #34, #7, #62, #73 (end and victory part;
the reveal part is B7). The user accepted every default of the batch; the choices below are those defaults, made concrete. Details
the defaults left open are marked "decided". Builds on the B2 phase pipeline (`PhaseController.Advance`/`EnterPhase`, start cursor,
`ResetAt`), the B3 piece outputs (Remove `ToMapId`: a map's first board centre) and the B4 pending-run paths.

## Intent

The turn sequence is a fixed list of phases with fixed-count blocks, and a game never ends. Big packs work around five gaps:
- a fire-and-move exchange that repeats "until both players pass" needs a block with a large count and skipped phases (#6);
- an exchange inside a stage (a per-round cap inside a repeated day) has no second counter and no way to reset per round (#34);
- a defender who fires back during the attacker's phase cannot run a unit-less formula or write a global there (#7);
- reinforcements and withdrawals on a turn schedule are tracked on paper (#62);
- the last turn and the victory check are done by hand, and nothing records the winner (#73).

## Design (decided)

Every new pack field is optional and omitted from JSON when empty or default; blank text and empty lists become null on load
(`RulesPackIo.Normalise`). Old packs keep their bytes and SHA-256, `CurrentFormat` stays 4. Old saves keep their bytes: every new
save, command and intent member is omitted when null or default. Every new expression is parsed and checked by the validator. Every
new field gets a Workbench editor and a wiki section with invented Riverland examples (no content from any real game). Public API
changes are additive (new members on positional records are init properties in the record body; new interface members are default
methods; new overloads, never changed signatures).

### Pack model (Rules)
- `PhaseBlock.Count` (unchanged member, still always written): `0` now means **open-ended** (repeats until the acting player chooses
  Continue at its last phase). Negative counts stay invalid. A pack with `count: 0` was invalid before (validator error, block ignored),
  so no valid old pack changes behaviour.
- `PhaseDef.Reactors : List<string>?` (side ids, `WhenWritingNull`): sides that may also act in this phase.
- `RulesPack.Schedule : List<ScheduleRow>?`, `RulesPack.LastTurn : int?`, `RulesPack.LastStage : int?`,
  `RulesPack.Victory : List<VictoryCondition>?` (all `WhenWritingNull`, appended after `MapOwners`).
- `public enum ScheduleAction { Arrive, Leave, Return }`.
- `ScheduleRow { Id, Turn : int, Stage : int?, PhaseId, Piece, Action : ScheduleAction (WhenWritingDefault), ZoneMapId : string?,
  FormulaId : string?, Label : string? }`. `Piece` is a unit filter (as `AppliesTo`; a single counter is `name() == "Relief barge 1"`).
- `VictoryCondition { Id, Condition, SideId : string?, Message : string?, PhaseIds : List<string>? }` (decided: an `Id` is added so the
  validator, the Workbench and the recorded result can name it).
- New function `segment()`: parser arity (0, 0), in `Evaluator.FunctionNames`; `IEvalContext.Segment()` is a default method returning 0.
  Allowed wherever `stage()` is (phase conditions included).

JSON (invented):
```json
{ "firstPhaseId": "n-combat", "lastPhaseId": "n-combat", "count": 0, "label": "Fire round" }
{ "id": "s-combat", "name": "South Combat", "sideId": "south", "reactors": ["north"], ... }
"lastTurn": 8,
"victory": [ { "id": "v-river", "condition": "NorthPorts >= 4", "sideId": "north", "message": "The Northern League holds the river", "phaseIds": ["n-supply"] } ],
"schedule": [ { "id": "r-relief", "turn": 3, "phaseId": "n-supply", "piece": "startswith(name(), \"Relief barge\")",
  "zoneMapId": "map-north-hold", "formulaId": "f-fresh-crew", "label": "Relief barges arrive" } ]
```

### #6 Open-ended blocks and one nesting level (Core `Sequencer`)
- **Nesting** (decided): a block may lie strictly inside one other block (an inner block); a third level, a partial overlap or two
  blocks over the same phases are ignored by `Sequencer` (first valid in pack order wins, as today) and reported by the validator.
  `Sequencer.ValidBlocks` keeps returning every block it uses; new `Sequencer.InnerBlock(pack, phaseIndex)` / `OuterBlock(...)`.
- **`PhasePosition.Inner : int`** (body init, `WhenWritingDefault`): the inner block's repeat, 1-based; 0 outside an inner block.
  `Repeat` stays the outer (or only) block's repeat, so `stage()`, `runs(…, "stage")`, `LogEntry.Stage` and `SkippedPhase.Repeat` keep
  their meaning. Record equality includes `Inner` (so `EndedAt`, `StartCursor.At` compare whole positions).
- **`segment()`** returns `Position.Inner` (0 outside an inner block): `FormulaRunner.RunContext`, `PhaseConditions.PhaseContext`
  (`at.Inner`), `PieceFilter` contexts (a `SegmentAt` beside `StageAt`).
- **`Sequencer.Next(pack, cur, bool repeat)`** (new overload; the old one = `repeat: false`):
  1. at the last phase of the inner block: an open-ended inner block repeats when `repeat` (which is then used up), a fixed one while
     `Inner < Count`; repeating goes to the inner block's first phase, `Inner + 1`, same `Repeat`;
  2. else at the last phase of the outer block: open-ended repeats when `repeat` is still unused, fixed while `Repeat < Count`;
     repeating goes to the outer first phase, `Repeat + 1`, `Inner` = 1 when that phase is in an inner block, else 0;
  3. else the next phase: `Repeat` kept inside the same outer block (clamped to `Count` only when `Count > 0`; never `Math.Clamp` with
     a max of 0), else 1; `Inner` kept inside the same inner block, 1 on entering one, 0 outside; past the last phase: next turn.
  `First` gives `Inner = 1` when phase 0 is in an inner block. `Resolve` clamps `Repeat`/`Inner` the same way.
- **`Sequencer.RepeatChoice(pack, pos) : PhaseBlock?`**: the block whose repeat the player chooses at `pos`: the inner block ending here
  when open-ended; else (no inner block ending here, or a fixed one with `Inner >= Count`) the outer block ending here when open-ended;
  else null.
- **`StageText`**: per level `{label} {n} of {count}` (fixed, as today) or `{label} {n}` (open-ended), label blank → `Stage`; outer then
  inner joined with `, `. A pack without open-ended or nested blocks gets exactly today's text.
- **`PhaseConditions`**: new overload `Next(pack, s, cur, model, bool repeat)`; the walk passes `repeat` to the first step only.
  `TurnLength` counts each phase times the counts of the blocks around it, an open-ended block counting 1.
- **The question** (Render): `PhaseView.RepeatAsk : RepeatAsk?` (body init; `record RepeatAsk(string Label, int Number)`) is set
  when `RepeatChoice` gives a block, the viewer may press Next Phase (`PlayingText` null), the game is not over, and the press would
  advance (the phase has no `OnEnd` entries or `EndedAt == pos`). `PhaseRunDispatch.NextPhase`, after the open-decisions check, asks
  `IPhaseUi.AskRepeat(RepeatAsk) : Task<bool?>` (default method: `false` = Continue; null = Cancel, nothing happens), then calls
  `port().NextPhase(repeat)`.
  - `PhaseController.Advance(bool repeat)` (new overload; `Advance()` = Continue): passes `repeat` to `PhaseConditions.Next`.
  - `IActionPort.NextPhase(bool repeat)` default method `=> NextPhase()`; `LocalActions`, `RemoteActions`, `ActionPortSwitch`
    implement it. `NextPhaseIntent.Repeat : bool` (body init, `WhenWritingDefault`); the host passes it to `Advance(repeat)` after the
    existing `PhaseGate` (acting side of the phase; online: the seat holding it). Hot-seat: the player at the screen is the acting
    side's (the handover is unchanged). A side-less last phase: whoever may press Next Phase answers (as today).
  - `PackHarness.Advance(bool repeat)` (new overload).
- **Edge cases** (decided): a press that stops for end-of-phase runs asks nothing; the next press asks. Undo of the press undoes the
  choice. `SkippedBefore` walks with `repeat` while the walk's `Repeat`/`Inner` are below the target's, so skipped chips stay right in
  later repeats. The will-skip preview walks Continue.

### #34 Rounds inside a stage (docs only)
An open-ended inner block, a property that resets at its first phase (`ResetAt`, B2) and `segment()` cover it. Wiki recipe "Fire
rounds inside a day": Riverland outer block `Day` (count 2) over North Movement–North Combat, inner open-ended block `Fire round` over
North Combat, `FiredThisRound` resets at North Combat, precondition `FiredThisRound < 1`, `if(segment() > 3, 1, 0)` for a tiring
modifier. No engine work beyond #6.

### #7 Sides that react in another side's phase
- `PhaseDef.Reactors`: those sides may also act in the phase, alongside the acting side (DECISION default **parallel**: no reaction
  sub-step, no order).
- Piece runs are already ownership-based (`PieceAccess.CanAct`: a held side's or a side-less counter, in any phase), so they do not
  change. Reactors widen the **phase-ownership** checks: new Core `Sequencer.MayAct(RulesPack pack, PhaseDef phase, IReadOnlySet<string>?
  sides)` = sides null (solo), a phase with no known side, the acting side held, or any held side among the phase's known `Reactors`.
  - Online host: `FormulaIntentRunner.ActingSideHeld` uses it, so a reacting guest may run a unit-less formula and write globals.
    `PhaseGate` (tick, acknowledge, Next Phase) stays the acting side's (decided: the phase belongs to its side).
  - `ModuleSession.ActorSideFor`: the acting side when held, else the first held reacting side, else as today (stamps follow it).
  - `PhaseController.View`: a hot-seat or guest viewer holding a reacting side (not the acting one) gets
    `PlayingText = "{side} is playing {phase}; you may react"` (still blocks Next Phase and the checklist; decisions stay hidden).
    `PhaseView.Reactors : IReadOnlyList<string>` (body init, default empty): the reacting side names; the phase panel shows
    `Reacting: {names}`.
  - Hot-seat handover is unchanged (it goes to the acting side's player); a reacting player takes the screen with **Current Player**.
  - OnMove `FiresFor`, alerts and private dice are unchanged.

### #62 Reinforcement and withdrawal schedule
- A row is **reached** at position `pos` when `pos.Turn > Turn`; or `pos.Turn == Turn` and `pos` is in the row's phase with
  (`Stage` null or `pos.Repeat >= Stage`); or `pos.Turn == Turn` and `pos` is in another phase of the same outer block as the row's
  phase and the stage comes first: `pos.Repeat > (Stage ?? 1)`, or `pos.Repeat == (Stage ?? 1)` and `pos` is a later phase. Inside that
  block a later phase of an EARLIER repeat is not reached. Outside such a block (a phase not in the row's block), `pos` is reached when
  its phase is later than the row's. This is `ScheduleDue.Reached`. **Due now** = reached and the position is exactly the row's
  (turn, phase, and stage when set); else **overdue**. A row of a phase the pack lacks is never reached.
- DECISION default **list overdue only**: nothing is placed or withdrawn by the engine on its own. A reached row stays listed (due now,
  then overdue) until a player acts on it; it is never dropped.
- **State.** `GameState.ScheduleDone : List<string>?` (row ids, `WhenWritingNull`). Command `MarkScheduled(string RowId, string Text,
  DateTimeOffset? At = null, string? Who = null)`: adds the id once and logs a Turn line `Text` = `Schedule: {label}`. Label = the row's
  `Label`, else `Arrivals, turn {T}` / `Withdrawals, turn {T}` / `Returns, turn {T}` (decided: no counter names and no counts in the
  line).
- **Counters of a row** (Core `ScheduleDue.Counters(pack, state, model, row)`): the pieces matching `Piece`; Arrive and Return: those
  on the zone map; Leave: those not on it (every matching piece when `ZoneMapId` is null). The zone map is found by id, else by name
  (as `ToMapId`).
- **Due list** (Core `ScheduleDue.For(RulesPack, GameState, ModuleModel?, IReadOnlySet<string>? sides) : IReadOnlyList<DueRow>`;
  `record DueRow(ScheduleRow Row, string Label, bool Now, IReadOnlyList<string> PieceIds)`): reached, not done rows, game not over, in
  order (turn, stage, phase index, pack order). `PieceIds` = the row's counters the viewer may act on (`PieceAccess.CanAct`), computed
  on the viewer's own state. A row with none is left out, except in solo (sides null), where it is listed with `no counter matches`
  and no button. State text: `Due now` or `Overdue since turn {T}`.
- **Acting** (`PhaseController.ActOnSchedule(string rowId) : string?`, null = done, else the refusal), one action labelled `{label}`:
  - **Place** (Arrive, Return): `MarkScheduled`; then `FormulaId` (when set) runs for each of the row's counters through the same path
    as a `ForEach = Units` start entry (`Apply(f, "schedule", units, …)`, every difficulty as there), stamped for the acting player.
    The counters stay on the zone map; the Desktop selects them and shows that map, and the player drags them to their hexes
    (decided: the engine picks no hex).
  - **Withdraw** (Leave): each counter moved to the zone map's first board centre (one `MovePieces`, no `OnMove` triggers, its own move
    text) or deleted (`RemovePiece`) when `ZoneMapId` is null; then `MarkScheduled`; then `FormulaId` as above on the moved counters (a
    deleting row cannot have a formula: validator).
  - Refused (texts in the constraints): game over; row unknown, not reached, done, or no counter the actor may act on (one neutral
    text); `Sequencer.MayAct` false for the actor (another side's phase).
- **Next Phase check**: a due-now row with counters is a `Decision` of the new kind `DecisionKind.Schedule`, Text `Due: {label}`,
  PieceIds its counters (`Decisions.Compute`, from the viewer's state). Overdue rows only show in the list.
- **Online**: `ScheduleIntent(string RowId)` (`$t` `"schedule"`); `IActionPort.ActOnSchedule(string rowId) : string?` default method
  refusing with `NotUnderstoodText`; `RemoteActions` sends it. The host (`IntentExecutor`) checks `BadId`, then runs
  `ActOnSchedule` inside `c.ActingFor(sides, who)`, so the counters are those the **sender** may act on in the full state.
- **Hidden information**: the pack (and so the schedule) is public; done ids are public; a guest's list is built from its own view;
  the log line has no names or counts; moves keep their redacted move text.

### #73 Last turn and victory (end part)
- **State.** `GameState.GameOver : GameOverState?` (`WhenWritingNull`), `record GameOverState(string Text, IReadOnlyList<string>?
  Winners, string? VictoryId, PhasePosition? At)`. Command `EndGame(string Text, IReadOnlyList<string>? Winners, string? VictoryId,
  DateTimeOffset? At = null, string? Who = null)`: logs, for every open pending run, the `skipped: …` line and batch summary exactly as
  `AdvancePhase` does (factored into one helper), clears `Pending` and `StartCursor`, sets `GameOver` (At = Position), logs a Turn line
  `Game over: {Text}`. `PlayerView` keeps it (public). Undo removes it. B7 hooks "game over lifts hiding" onto it.
- **Last turn** (Core `GameEnd.Reached(RulesPack pack, PhasePosition cur, PhasePosition next) : bool`): with `LastTurn` set,
  `next.Turn > LastTurn`; with `LastStage` also set, also when `cur.Turn == LastTurn`, `cur` is in a block with `cur.Repeat ==
  LastStage`, and `next` is not in that block's same repeat. `PhaseController.Advance`, after end entries and the conditions step and
  before `AdvancePhase`: when reached, it checks every victory condition (ignoring `PhaseIds`, in order) at the current position; the
  first true gives the result, else `Text` = `the last turn has been played`, no winner. `EndGame` in the Next Phase action; the
  position stays; `PhaseBoundary` is raised (the final autosave).
- **Victory on entry**: `EnterPhase`, after the reset and seeding and before the start entries, checks the conditions whose
  `PhaseIds` is null or holds the phase (pack order). The first true ends the game (`EndGame`) and the start entries do not run.
  Also in `Start()`.
- **Evaluation** (Core `GameEnd.Check(RulesPack, GameState, PhasePosition at, string? phaseId, ModuleModel?, Action<string>? warn)
  : VictoryCondition?`): the phase-condition context (globals, tables, `turn()`, `stage()`, `segment()`, `phaseside()`, whole-game
  aggregates) on the host's full state. Fail-closed: an error counts as not met and is passed to `warn` (Render: one diagnostics warning
  per condition per session, `Victory condition {n} failed: {error}; it counts as not met`). Host only: never on a guest, a viewer copy
  or while replaying (as `ResetAtEntry`); replay re-applies the recorded `EndGame`.
- **Result text**: the condition's `Message`; blank → `{side name} wins`; blank and no side → `the game has ended`. `Winners` =
  `[SideId]` or null.
- **After the end**: `Advance` does nothing and sets the notice `The game is over.`; `PhaseView.GameOverText` (body init) =
  `Game over: {Text}`; the banner and strip show it; the guest's `NextPhaseIntent` and `ScheduleIntent` are refused with the same text.
  Counters, ledgers, the log, undo and saving still work (decided: only the turn sequence stops).

### Validator
Blocks (existing prefix `Block "{label}"`):
- error `Block "{label}": count cannot be negative` (replaces `count must be at least 1`, which no longer applies: 0 is open-ended)
- error `Block "{label}" overlaps block "{other}"` (unchanged; now only for partial overlap)
- error `Block "{label}" covers the same phases as block "{other}"`
- error `Block "{label}" is inside block "{other}", which is itself inside another block; blocks nest one level only`
- error `Blocks "{a}" and "{b}" both end at phase "{p}" and both repeat until a player stops; give one of them a count`
Phases:
- error `Phase "{p}": reacting side "{id}" does not exist`
- warning `Phase "{p}" has reacting sides but no side of its own; every side may already act in it`
- warning `Phase "{p}" lists its own side as reacting`
Game end:
- error `The last turn must be at least 1`; error `The last stage must be at least 1`; error `A last stage needs a last turn`
- warning `The last stage is set but no block repeats`
- `Victory condition {n}` prefix (1-based): error `has no id`; error `id "{id}" is used twice`; error `has no condition`; the condition is
  checked exactly as a phase condition (`CheckPhaseCondition`'s rules and tails, with `condition {tail}`); error `side "{id}" does not
  exist`; error `phase "{id}" does not exist`
Schedule (`Schedule row {n}` prefix, 1-based):
- error `has no id`; error `id "{id}" is used twice`; error `turn must be at least 1`; error `stage must be at least 1`
- error `has no phase`; error `phase "{id}" does not exist`; warning `has a stage, but phase "{name}" is not in a block`
- error `has no counter filter`; error `counters {problem}` (`ParseProblem` tail), then `FilterRefProblems` tails with the same prefix
  (`runs()`, `row()`, `dice()` not available there: `counters {fn}() is not available in a schedule row`)
- error `needs a zone map to arrive from` (Arrive, Return without `ZoneMapId`)
- error `zone map "{id}" was not found in the module` (with the module's map ids, as `ToMapId`)
- error `formula "{id}" does not exist`
- error `deletes its counters, so it cannot run a formula on them; give it a zone map` (Leave, no zone map, a formula)
- warning `comes after the last turn` (`Turn > LastTurn`)
Expressions: `segment()` parses everywhere `stage()` does; arity errors as for `stage()`.

### Workbench (Avalonia Desktop; the WinForms App builds only)
- **Phases tab**: under Side, `Also acting:` with one check box per other side; tooltip `Sides that may also act in this phase: run
  their own unit-less formulas and write global values. Only the phase's own side ticks, acknowledges and presses Next Phase.`
  `PhaseEdit.SetReactors(pack, phaseId, IReadOnlyList<string>? ids)` (empty → null).
- **Make block**: check box `Until a player stops` (disables the count; result count 0), tooltip `Repeat these phases until the
  acting player chooses Continue at the last one.` `PhaseEdit.MakeBlock` accepts count 0 and a block strictly inside one block that
  has no inner block (or strictly around one block with no outer block); other overlaps keep `These phases are already in a block`;
  new `Blocks nest one level only`. The phase list's block marker shows `(until stopped)` for count 0.
- **New tab `End of game`** (`WorkbenchTab.EndOfGame`): `The game ends after turn` check box + number (1-999), `Last stage` check box +
  number; the victory list (Add, Remove, Move Up, Move Down) with Condition (and a live `true now` / `false now` line over the viewer's
  state, as alerts), Side combo (`(no winner)` + sides), Message, Checked in (comma-separated phase names; `No phase named "{name}"`;
  blank = every phase). `GameEndEdit.WithLastTurn/WithLastStage/AddVictory/UpdateVictory/RemoveVictory/MoveVictory`.
- **New tab `Schedule`** (`WorkbenchTab.Schedule`): a grid Turn, Stage, Phase (combo), Action (combo), Counters (filter text with the
  parse error under the grid), Zone map (combo: blank + module maps), Formula (combo: blank + formulas), Label; Add, Remove, Move Up,
  Move Down. `ScheduleEdit.Add/Update/Remove/Move` (new ids `sched-{n}`).
- Problems list: the new issues jump to these tabs (`WorkbenchTab.Schedule`, `WorkbenchTab.EndOfGame`, additive enum members).

### Play UI (Desktop)
- The Next Phase question: `ChoiceDialog`, caption `Next Phase`, message `End of {label} {n}: play another {label} or continue?`,
  buttons `Another {label}`, `Continue`, `Cancel` (default `Continue`).
- Phase panel: `Reacting: {names}` under the side; `Game over: {text}` replaces the next-phase line; a **Due** section (hidden when the
  list is empty): one row per `DueRow` (label, state, `Place` or `Withdraw` button with the tooltips of the constraints; a row click
  centres its first counter). After a local Place: the counters are selected and their map shown; status `Placed: drag the counters
  from {map} to their hexes`. A guest gets the status after the host's answer.
- Banner: `Game over: {text}` (as `Now: …`).

## Public API changes
All additive: `PhaseDef.Reactors`; `RulesPack.Schedule/LastTurn/LastStage/Victory`; `ScheduleRow`, `ScheduleAction`, `VictoryCondition`;
`IEvalContext.Segment` (default method); `PhasePosition.Inner` (body init); `Sequencer.Next(pack, cur, repeat)`, `RepeatChoice`,
`MayAct`, `InnerBlock`, `OuterBlock`; `PhaseConditions.Next(…, repeat)`; `GameState.GameOver/ScheduleDone`; `GameOverState`, `EndGame`,
`MarkScheduled`; `GameEnd`, `ScheduleDue`, `DueRow`; `DecisionKind.Schedule`; `PhaseView.RepeatAsk/Reactors/GameOverText`, `RepeatAsk`;
`PhaseController.Advance(bool)`, `ActOnSchedule`; `IPhaseUi.AskRepeat` and `IActionPort.NextPhase(bool)`, `ActOnSchedule` (default
methods); `NextPhaseIntent.Repeat`, `ScheduleIntent`; `PackHarness.Advance(bool)`, `PackHarness.GameOver`; `PhaseEdit.SetReactors`,
`GameEndEdit`, `ScheduleEdit`; `WorkbenchTab.Schedule/EndOfGame`. Behavioural: `count: 0` blocks (invalid before) now repeat until
stopped; a block nested in another (invalid before) is now used; the negative-count validator text changes.

## Non-goals
More than one nesting level; a reaction sub-step or order between acting and reacting sides; reactors ticking, acknowledging or
advancing; the engine choosing a hex for arrivals or placing counters by itself; schedule rows that add palette counters; a
`runs(…, "segment")` period; lifting hidden information at game over (B7); the parked B3 minor "guest Next Phase has no ask-run check"
(B7); the WinForms App's new UI.

## Anti-goals
Old packs or saves changing bytes; a guest learning another side's counters from the due list, a refusal or the schedule log line; the
engine evaluating victory on a guest; a game that cannot be undone out of game over; a fixed-block pack whose turn walk changes.

## Approaches
Picked: the choice as a `repeat` flag on the existing Next Phase path (one action, one undo, the existing acting-side gate online);
`Inner` on the position (one save member, `segment()` reads it); reactors as one shared `Sequencer.MayAct` predicate; schedule done-ness
as explicit ids (robust when counters return to the zone map); game over as state set by one command.
Rejected: a separate "repeat" intent and command (a second gate to keep in step); deriving done rows from counter positions (a
withdrawn counter coming back looks undone); auto-placing arrivals (no hex in the row; DECISION default list only); evaluating victory
in `View()` (would run on guests over hidden data).

## Left to the builder
Test layout; where `GameEnd`/`ScheduleDue` live in Core; the Due section's layout; column widths; wiki prose around the exact strings.
