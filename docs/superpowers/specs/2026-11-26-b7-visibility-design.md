# Visibility, reveals and sealed answers (batch B7) — design (short)

Date: 2026-11-26. Branch `b7-visibility` (from `1f69dd6`, the B5 head, which already holds B6 through a merge; B5 merges to master
unchanged). Engine wishlist #74 (a-d), #47, #53, #73 (the reveal part), plus every carry parked for B7: the B2 guest Next Phase ask-run
check, the hot-seat checklist tick line ("Done: {item}") that shows another side's checklist text and raw `{item:x}` tokens, the B6
carries (hot-seat result box of a formula with hidden inputs; a binding's auto value reading another hidden variable; holder() reads in
Piece filters; choices and Piece filters over hidden `keyed()`/`holder()` values leaking through filtered lists and a guest yes/no
oracle) and the B5/B6 parked `ScheduleRow.ZoneMapId` resolver. The user accepted every default of the batch; the choices below are those
defaults, made concrete. Details the defaults left open are marked "decided". #57 is dropped (batch list).

## Intent

Hidden information works, but several rules disagree with each other or with what players expect:
- a holder owned by a side hides every value from other sides, even a property marked Public (#74a);
- a side's alert shows to everyone during that side's phase and to no one in other phases (#74b); the below-zero alert lists other
  sides' counters and holders (#74c);
- private dice are recognised by the formula's name at the start of the dice line, so two formulas with one name, or a renamed formula,
  are judged wrongly (#74d);
- a recon roll can tell a player "the barge's strength is between 4 and 6" only by hand (#47);
- simultaneous secret choices (bids, orders) need one player to look away (#53);
- after the game ends, players still cannot see each other's data (#73 reveal).

And the carried leaks: in hot-seat a player reads the other side's ticked checklist items and sees formula results built from hidden
inputs; a choices list or a guest's Calculate can answer yes/no about a hidden value.

## Principles (decided)

1. **One source of truth.** Whether a viewer may see a value is answered by one new Core class, `HiddenValues`; every existing rule
   that decided it on its own (`PlayerView`, `KeyedValues`, `FormulaMasking`, `PieceFilter`, `PropertyValues`, the runner's Piece binding
   view check, the alerts) calls it. No second copy of a holder or piece rule is added.
2. **A viewer never learns a value they cannot see**: not from a value, a working line, a result box, a list of choices, a refusal text
   or the difference between two answers to two guesses.
3. **Choices are judged on what the chooser can see.** A Piece binding's candidates already are; an Input's table choices now are too, on
   the host as on the guest, so the list a player sees and the list the host checks are the same list.
4. The game being over lifts every hiding rule for information, never any acting right.

Every new pack field is optional and omitted from JSON when empty or default; blank text becomes null on load (`RulesPackIo.Normalise`).
Old packs keep their bytes and SHA-256, `CurrentFormat` stays 4. Old saves keep their bytes: every new save, command and intent member is
omitted when null or default. Every new expression is parsed and checked by the validator. Every new field gets a Workbench editor and a
wiki section with invented Riverland examples (no content from any published game); expression identifiers use letters, digits and `_`
only. Public API changes are additive (new members on positional records are init properties in the record body; new interface members
are default methods; new overloads, never changed signatures; enum members appended).

## Design (decided)

### Pack model (Rules)
- `OutputAction.Reveal` (appended after `SetMask`; JSON `"reveal"`).
- `FormulaOutput.Margin : string?` (`WhenWritingNull`; blank → null in `Normalise`): Reveal only; an expression giving the width of the
  revealed range.
- `StartEntry.Sealed : bool` (`WhenWritingDefault`): a start entry every side answers in secret (#53).
- New function `askside()`: parser arity (0, 0), appended to `Evaluator.FunctionNames` (after `"keyed"`), dispatched in `EvalCall`;
  `IEvalContext.AskSide() => Value.Of("")` (default method). It is the **name** of the side answering the run (an ask run's
  `AskSideId`, a sealed run's side; the name as `side()` and `phaseside()` give it, the id when the name is blank), else `""`. Allowed everywhere (decided: it is never an error, only empty outside an asked run).

JSON (invented):
```json
{ "formulaId": "f-toll-bid", "forEach": "once", "sealed": true }
{ "target": "Piece", "pieceVariable": "target", "property": "p-strength", "action": "reveal", "margin": "2" }
{ "target": "Global", "property": "p-bid", "op": "Set", "expr": "bid", "key": "askside()" }
```

### #73 reveal and the one visibility rule (Core `HiddenValues`)
New `src/Quartermaster.Core/HiddenValues.cs`, `public static class HiddenValues`:
- `SeesEverything(GameState? full, RulesPack? pack, IReadOnlySet<string>? held) : bool` — true without a pack, for null `held` (solo),
  when every side is held, and (B7 #73) when `full.GameOver != null`. Replaces the "every side held" shortcut of `PlayerView.For`,
  `FormulaMasking.HiddenVariables`, `FormulaMasking.OutputShown` and `KeyedValues.HiddenFor`.
- `HolderValueHidden(RulesPack pack, string holderId, PropertyDef? property, Func<string, bool> holds) : bool` — in this order: a place
  property (`KeyedFacts.IsPlace`) that is not Public → true; the Global holder → false; a holder missing from the pack → true; a holder with
  no (existing) side (`Ownership.SideOfHolder` null) → false; a held owner → false; `property` null (the whole key) → true; else
  **`!PropertyFacts.IsPublic(pack, property)`** (#74a: a Public property of another side's holder is visible).
- `PieceValueHidden(RulesPack pack, PropertyDef? property, string? pieceSide, Func<string, bool> holds) : bool` — a non-Public place →
  true; a piece of no side or a held side → false; else `!PropertyFacts.IsPublic(pack, property)` (null property → true). The callers keep
  their own "the piece is removed, masked or deleted" checks (those hide every value whatever the property).

Callers routed through it (behaviour unchanged except #74a and #73):
- `PlayerView.For`: `if (HiddenValues.SeesEverything(full, pack, held)) return full.DeepClone();` (a game that is over gives every player
  the full state, `IsPlayerView` false, `ViewSides` null). `Filter.ValueHidden` uses `HolderValueHidden`/`PieceValueHidden`;
  `Filter.WholeKeyHidden` drops a holder key only for an unknown holder (a sided holder is filtered per property).
- `KeyedValues.HiddenInView` holder branch → `HolderValueHidden`; piece branch → `PieceValueHidden`; `HiddenFor` uses `SeesEverything`.
- `PieceFilter.HolderKeyValue`: on a player view a value missing from the view is `MissingValue` exactly when `HolderValueHidden` says it
  is hidden (no longer "the whole key is missing"), so a Private value of a holder whose Public values are now in the view never reads its
  Default.
- `PropertyValues.HiddenInView` holder branch → `HolderValueHidden`.
- `FormulaRunner` Piece binding view check (the `Missing(HiddenText)` for a picked piece) → `PieceValueHidden`.
- `FormulaMasking.HiddenVariables` (Render): `HolderHidden(holderId)` becomes `HolderHidden(holderId, PropertyDef?)` over
  `HolderValueHidden` (Holder bindings pass their property; holder(H, P) reads pass P; keyed() with a holder argument passes its property);
  `UnitHidden` over `PieceValueHidden`.
- `ModuleSession.CanEditLedgerFor`, `IntentExecutor.GuestRule` and `PieceAccess` are **acting** rules and stay as they are: a Public value
  of another side's holder becomes readable, never writable; after the game ends nobody gains a right to act on another side's counters
  (decided: `PieceAccess` is not changed for #73).
- Undo out of game over hides again; what a player saw stays seen (decided; the wiki says so).

### #74b/#74c alerts (Core `AlertEngine`, Render `AlertBarModel`)
- New overload `AlertEngine.Evaluate(GameState s, RulesPack? pack, ModuleModel? model, PhaseDef? current, string? actingSideId,
  IReadOnlySet<string>? viewerSides)`; the five-argument overload keeps its exact behaviour (decided: tests and callers outside the bar).
  `viewerSides` null = a viewer holding every side.
- **Side alerts** (`AlertDef.SideId`): shown when the viewer holds that side (null: always), in every phase; never to a viewer not holding
  it. The acting-side test is gone from the new overload. Solo players therefore see a side alert in every phase (decided, the batch default).
- **Below zero** (`builtin:neg:*`): lists only the viewer's own counters (a piece of a held side or of no side; never a masked placeholder)
  and holders (Global, a holder of no side or of a held side). A property whose list is left empty gives no chip.
- **Unacknowledged warning** (`builtin:warning`): only when the viewer holds the current phase's acting side, or the phase has no side
  (decided: the warnings of another side's phase are that side's, as its checklist is).
- `AlertBarModel.Recompute` passes `FormulaMasking.ViewerSides(s)`, or null when `HiddenValues.SeesEverything(s.Game.State, s.Rules, …)`.
  Desktop and the WinForms App reach it through the model; their code does not change.

### #74d private dice by formula id (Core)
- `RollDice.FormulaId : string?` (body init, `WhenWritingNull`), set by `FormulaRunner.Commands` to `t.Formula.Id` for each roll.
- `DiceEntry.FormulaId : string?` (body init, `WhenWritingNull`), copied by the reducer; `LogBuilder.Dice` passes it as the line's
  `LogEntry.FormulaId`.
- `PlayerView`: a dice line or dice entry with a formula id is private exactly when that formula has `PrivateRolls` (a formula id that is
  no longer in the pack counts as private: fail closed). Without a formula id (old logs, dice-tool rolls) the name prefix rule stays.
  `FilterDice` pairs an entry with a line by purpose prefix as today, and also requires equal formula ids when the entry has one.
- Only `Calc`/`Formula` lines read `LogEntry.FormulaId` for working today; a dice line's id is read only by the private-dice rule.

### Checklist tick and acknowledge lines (carry B2/B3)
- `TickChecklist.SideId : string?` and `AcknowledgeWarning.SideId : string?` (body init, `WhenWritingNull`): `PhaseController.Tick` and
  `Acknowledge` set the current phase's side (`Sequencer.ActingSide`; null in a phase without a side). The reducer passes it to
  `LogBuilder.Line(..., sideId: ...)`.
- `PhaseController.Tick` logs the item's text **with its links rendered**: `ChecklistText.Render(pack, item.Text, ph =>
  string.IsNullOrWhiteSpace(ph.SideId) || ph.SideId == phase.SideId)` (a link to an item of another side's phase stays as written). Old
  logs keep their raw text (bytes unchanged).
- `PlayerView.Decide`: a `Turn` line with a side stamp is kept only when the viewer holds that side, else dropped (no other line kind
  changes; no Turn line had a stamp before B7).

### #47 Reveal output (Rules, Core)
- A Reveal output names a Target (Unit, Piece, Holder, Global; not Placed) and a Property, and may have a Margin. It writes nothing.
- **When the value is read:** at the run, in output order, on the run's state: the property's value as a binding of that target reads it
  (stored, else Default where it applies, computed properties evaluated), after this run's earlier Write outputs to the same ledger key and
  property (their `After`). On a player view a value the view lacks is recorded as unknown (no error): a guest's local run never fails on
  it; the host reads the full state.
- **Margin:** evaluated in the run context; blank = 0. It must be a whole number from 0 to 1000, else the run fails:
  `The margin for {Prop} must be a whole number from 0 to 1000`.
- New `RevealPreview(string Subject, string PropertyId, double? Value, int Margin)`; `FormulaTrace.Reveals : IReadOnlyList<RevealPreview>`
  (body init, empty by default). `Subject`: the run piece's name (Unit), the picked piece's name as the requester sees it (Piece, the same
  name the working uses; a masked placeholder reads `Unknown piece`), the holder's name (Holder), `Global` (Global).
- **Nothing of the value is shown before Apply** (decided: a preview would be a free look): the working part of a Reveal is
  `reveal {Prop}`, no box or verdict carries the value, `OutputPreview` lists no reveal.
- **The window (DECISION default: a random window containing the true value, host roll):** drawn in `FormulaRunner.Commands` with the
  runner's roller, one die of `margin + 1` faces: `k = roll − 1`, `lo = v − k`, `hi = lo + margin`; when `v >= 0` and `lo < 0`, the window is
  shifted to `lo = 0`, `hi = margin` (decided: it never reveals a value of 0 by starting below 0). No dice line is logged (the die would
  give the value away).
- **The log line** (one `AddLogLine(LogKind.System, text)` per reveal, no PieceId, no SideId, so every player who can see the subject
  keeps it; a line naming a counter a player cannot see is dropped by the existing name rule): margin 0 → `{subject}: {Prop} {value}`
  (labels as the Ledger shows them, `PropertyLabels.Text`: `3 (Sandstorm)`), else `{subject}: {Prop} between {lo} and {hi}` (numbers
  `0.##`). Order in `Commands`: after the effects' System lines, before removals and moves.
- A guest may run a formula whose Reveal names another side's holder or counter (it writes nothing): `IntentExecutor.GuestRule` skips
  Reveal outputs. The host decides the value.

### #53 Sealed answers (Rules, Core, Render)
- **Queueing.** Entering a phase whose start entry has `Sealed` queues, in one `QueueRuns`, one ask run per side of the pack (pack order),
  each with `AskSideId` = that side and `Seal` = the first run's id (new `PendingRun.Seal : string?`, body init, `WhenWritingNull`), in
  every difficulty (as an ask run). The entry counts as one start entry: the start cursor waits until every run of the group is resolved.
- **Answering.** A side answers its run as an ask run is answered (formula dialog, online `FormulaIntent` with the run id). On Apply, the
  host (`PhaseController.ApplyFormula`) does not execute the trace of a sealed run: it executes one new command
  `SealAnswer(string RunId, IReadOnlyDictionary<string, string> Typed, string LogText, DateTimeOffset? At = null, string? Who = null)`
  (`$t` `"sealAnswer"`; stamped by `CommandLog.Stamp`), whose reducer sets `PendingRun.SealedAnswer : IReadOnlyDictionary<string, string>?`
  (body init, `WhenWritingNull`) on the run and logs a public Turn line `{Side} has answered {formula} (sealed)`. `Typed` holds the trace's
  Input values (`FormulaDialogModel.ExactText`), nothing else. The phase notice reads `Answer sealed; it is applied when every side has
  answered.` A second answer is refused: `Your sealed answer is already in.` (host dialog and `FormulaIntentRunner.Prepare` alike).
- **Hidden.** `PlayerView` already drops another side's ask runs, so a sealed answer is only in the full state and in its own side's view
  (and in every view once the game is over). The answer line names no value.
- **Release.** In the action of the answer that completes the group: a Calc line (`LogKind.NoWorking`) `{formula}: every side has
  answered`, then for each run of the group in pack side order, stamped with its side (`_askStamp`), the formula is run on the current
  state with `RunRequest(f.Id, null, SealedAnswer, none, none, null, UseAutoValues: true) { AskSideId = side }`; a run that applies
  executes its commands and its working line as any applied run; a run that cannot apply logs `{formula}: {Side}: {error}` (Calc,
  `Error = true`, `FormulaId` = the formula, stamped with the side, so it is redacted as its working would be); each run is then resolved
  (`ResolveRun`). Later runs see earlier runs' writes. Then `ContinueEntries` (the next start entries). One undo takes back the last
  answer and the whole release.
- **Leaving early.** A Next Phase press that leaves the phase while a sealed group has any answer logs, before `AdvancePhase`, a Calc line
  (`NoWorking`) `{formula}: not every side answered; the sealed answers were discarded` (the pending runs go as every pending run goes).
- **Open runs.** `PhaseController.OpenAskSide` and a new `PhaseController.OpenAskFor(GameState st, IReadOnlySet<string> sides) :
  PendingRun?` (the first pending ask run with an unapplied unit, no `SealedAnswer`, and a side not in `sides`) count an answered sealed
  run as not open. The hot-seat "is playing" text shows only when the viewer has no open ask run of its own and `OpenAskFor` finds one.
  `Decisions.Compute` lists no answered sealed run.
- **askside()** is set for every ask run (sealed or not): `RunRequest.AskSideId` (body init), `FormulaDialogModel.AskSideId` (set wherever
  a model is built for a pending run: the local dialog path and `FormulaIntentRunner.Prepare`), read by `RunContext.AskSide()`. A sealed bid
  writes a keyed Global property with `Key = askside()` (a table whose row keys are the side names), read later with
  `keyed(TollBid, "Northern League")`.
- **Restrictions (validator):** a sealed entry is a start entry, runs once, in a phase without a side, with no Asks side; its formula rolls
  no dice and picks no piece (decided: dice would need a host roll per side at release that a preview could not pin, and a pick would
  need a list judged on each side's view at release).

### Carry: guest Next Phase while another side answers (B2)
`IntentExecutor.NextPhase`, after `PhaseGate`: when `PhaseController.OpenAskFor(state, sides)` finds a run, the press is refused with
the `PhaseGate` text for the asked side: `{Side} is playing {phase}; wait for your phase.` (the hot-seat panel already reads
`{Side} is playing {phase}`). The host's own press is not changed (the host decides).

### Carry: choices and candidates on the chooser's view (B6)
- `FormulaRunner` `Input` with a choices table: when the run has `PickSides` and the state is not a player view, the choices filter is
  evaluated on the **chooser's view** (`PlayerView.For(s, pack, model, PickSides)`, built once per run and shared with Piece picks through
  a new `PieceChoices.Named` overload taking that view), with the run piece taken from the view (not in the view → `No choice for this
  piece`). On a view a row whose filter reads hidden or missing data is kept (as `InputChoices.Filter` already does). So in hot-seat and
  for a guest's intent (Preview or Apply) the accepted values are exactly the listed ones: `Not one of the choices for this piece` means
  "not in your list", never "excluded by a value you cannot see". Solo and hook runs (no `PickSides`) are unchanged.
- Piece candidates are already built on the chooser's view (`PieceChoices.Named`); B7 adds tests pinning it for `holder()` and `keyed()`
  filters in hot-seat and for a guest's Preview, Apply and `Picks` check (`Choose one of the listed pieces` only for a label not in the
  sender's own list).
- Validator warnings (only in a pack with two or more sides), formula messages prefixed as usual:
  `variable "{v}": its choices filter reads values another side may not see; each player chooses from what they can see` and
  `variable "{v}": its piece filter reads values another side may not see; each player picks from what they can see`. A filter "may read
  hidden values" when it reads a non-Public property (bare name or `keyed()`), `holder()` of a holder with a side, `keyed()` with a holder
  argument of a holder with a side, or a piece aggregate of a non-Public property (`FormulaVariables.AggregateCallHidden` with
  `piecesMayHide: false`).
- `FormulaMasking` keeps B6's keyed() filter rule; holder() in a filter needs no masking (the filter runs on the view, so its outcome holds
  nothing the view lacks).

### Carry: masking of derived bindings and the result box (B6)
- `FormulaMasking.HiddenVariables`, last step: a fixpoint over the bindings — a binding (any kind but Piece) whose own expressions read
  hidden data is hidden too. The expressions are `FormulaVariables.Sources` (auto value, "Only when", dice count, row/column), except that
  an Input counts only its auto value (its "Only when" makes it a host guard, below). "Reads hidden data" is one function,
  `FormulaMasking.SourceReadsHidden`, shared with `GuardsOnHidden`: it names a hidden variable, or makes a holder(), aggregate, lookup() or
  keyed() call while that kind of read is hidden.
- The engine reads a hidden Input's auto value on every run (`RunRequest.AutoInputs`), so a formula with a derived hidden Input (for
  example `B = A + 1`) still runs and applies in hot-seat. Its box keeps showing `HiddenBoxText`.
- **Host guards, for every masked viewer** (hot-seat, the host's screen for a side, the host running a guest's intent, a guest): a typed
  box (Input or Piece) whose "Only when" depends on hidden data (`FormulaMasking.GuardsOnHidden`) is always open and must always be filled.
  Its text, tooltip, the status (`Waiting for: B` while it is blank), Apply, Calculate (never fills it) and a preview verdict's values
  (never carry it) do not depend on whether the guard holds. The engine evaluates the guard on the full state and ignores the typed value
  when the guard is false. While such a box is blank no result or working is shown. A formula with such a guard never shows its result or
  "(negative)" mark. The host's verdict to a guest is the same whether or not the guard holds; it always asks for the value.
- `FormulaDialogModel.View`, result box: an unmasked formula shows its result. A masked formula (`Hidden` not empty) shows its result only
  when the trace has at least one Write output, every Write output of the formula writes the result itself (no value expression of its
  own), and every write is visible to the viewer (`OutputVisible`, as the "(negative)" mark). Otherwise it reads `HiddenBoxText` (`hidden`,
  or `hidden — the host calculates` on a guest). On a guest (`HostCalculates`) a masked formula's result is always hidden (its local result
  is built from guesses). The batch grids show the same text (they read `DialogView.ResultText`).

### Carry: one resolver for a schedule row's zone map (B5/B6)
`ScheduleDue.ZoneMapIn` delegates to `MapRefs.Find`; the validator's schedule check uses `MapRefs.Find(_module.Maps, row.ZoneMapId)`
with a module, else the trimmed id against `knownMapIds`. Message unchanged: `Schedule row {n}: zone map "{id}" was not found in the
module`.

### Validator (formula messages prefixed `Formula "{f}": `, phase messages `Phase "{name}": `)
Reveal outputs (with `output {n}`):
- `output {n} reveals no property`
- `output {n} reveals keyed property "{p}"; a reveal shows one value`
- `output {n} reveals a value, so it takes no value expression`
- `output {n} reveals one property; a property list cannot be revealed`
- `output {n} cannot reveal a value of the placed counter`
- `output {n} has a margin but does not reveal`
- `output {n} margin {problem}` (parse, `ParseGuard` with what = `output {n} margin`)
- `output {n} margin must be a whole number from 0 to 1000` (a constant that folds with `ConstantContext`)
- The existing property, holder and kept-by checks of an output apply to a Reveal's property as to a Write's.
Sealed start entries:
- `start entry {n} is sealed, so it must run once, not for each unit`
- `start entry {n} is sealed, but only a phase without a side may have one`
- `start entry {n} is sealed, so it asks every side; leave Asks side empty`
- `end entry {n} is sealed; only a start entry can be sealed`
- `start entry {n} is sealed, but formula "{f}" rolls dice` (a Roll binding or a dice() call)
- `start entry {n} is sealed, but formula "{f}" picks a piece` (a Piece binding)
- warning `start entry {n} is sealed, but formula "{f}" asks for nothing` (no Input binding)
- warning `start entry {n} is sealed, but the pack has fewer than two sides`
Filters: the two warnings above.

### Workbench (Avalonia Desktop; the WinForms App builds only)
- Formulas tab, outputs grid: column `Margin` (weight 70, text) after `Key`. Tooltip: `Reveal only: how wide the revealed range is. 0 or
  blank shows the exact value; 2 shows a range such as between 3 and 5 that holds the value.` Render `FormulaEdit.WithMargin(FormulaOutput,
  string?)` (blank → null); `FormulaEdit.WithOutputAction` keeps `Property` for Write and Reveal, `Margin` only for Reveal, `Key` only for
  Write.
- Phases tab, start entries grid: column `Sealed` (check, weight 40) after `Asks side` (the end grid has none). Tooltip: `Every side
  answers this row in secret; the answers are applied together, in side order, once every side has answered.`
- Alerts tab: the Side column tooltip reads `Only players holding this side see the alert, in every phase.`
- Problems list: the new messages (through the existing `WorkbenchRules.Issues`).

### Wiki (invented Riverland examples only)
- Making-a-Rules-Pack: `## Visibility, reveals and sealed answers` with `### Public values of a side's holder` (`North Depot` owned by the
  Northern League: `Stores` Public is seen by the Southern Compact, `Reserve` Private is not), `### Side alerts` (`Low fuel` with Side
  Northern League: League players see it in every phase, Compact players never), `### Revealing a value` (`Scout the far bank`: Piece
  variable `target` with filter `side() == "Southern Compact"`, output Action Reveal, Target Piece, Property `Strength`, Margin `2`; the log reads
  `SC 3 Barge: Strength between 4 and 6`), `### Sealed answers` (phase `River toll` with no side, start entry `Toll bid` Sealed; Input
  `bid` 0-10; output Global `TollBid` keyed by table `Toll sides` (rows `Northern League`, `Southern Compact`), Key `askside()`; the next
  entry `Settle toll` reads `keyed(TollBid, "Northern League") > keyed(TollBid, "Southern Compact")`), `### When the game ends` (every player sees everything; undo hides again),
  `### What the Problems list says for visibility` (every new validator text).
- Formula-Reference: `### askside()`; the Reveal output and Margin; the margin run error in the error table.
- Window-Rules-Workbench: Margin, Sealed, the alert Side text. Window-Phases: `### Sealed answers` (answer line, notice, release,
  discard). Window-Log-and-Panels: tick lines are the playing side's own; reveal lines. Playing-With-Others: Public holder values, side
  alerts, sealed answers, game over. Window-Formula-Dialog: the result box reads `hidden` when the result would show a hidden value.

## Public API changes
All additive: `OutputAction.Reveal` (appended); `FormulaOutput.Margin`; `StartEntry.Sealed`; `IEvalContext.AskSide` (default method);
`HiddenValues`; `AlertEngine.Evaluate` 6-argument overload; `RollDice.FormulaId`, `DiceEntry.FormulaId`, `TickChecklist.SideId`,
`AcknowledgeWarning.SideId`, `PendingRun.Seal`, `PendingRun.SealedAnswer`, `RunRequest.AskSideId`, `FormulaTrace.Reveals` (body init);
`RevealPreview`; `SealAnswer` command; `PieceChoices.Named` overload (internal); `PhaseController.OpenAskFor`;
`FormulaDialogModel.AskSideId`; `FormulaEdit.WithMargin`. Behavioural (accepted defaults and carries): Public values of a sided holder
are visible to every side; side alerts follow the viewer's sides; below-zero lists only the viewer's own; a finished game is seen whole;
choices filters are judged on the chooser's view; masked result boxes read `hidden`; a guest's Next Phase waits for another side's
ask run; new tick lines are stamped and rendered.

## Non-goals
A per-property override of the holder rule; hiding a side alert's existence (its name) from other sides in the Workbench; reveals of
keyed values, of a placed counter or of many properties at once; dice in sealed formulas; sealed end entries or sealed menu runs;
partial release; showing other sides that a sealed run is still waiting (beyond the public "has answered" lines); re-rendering old tick
lines; the WinForms App's new columns.

## Anti-goals
A player seeing a Private value of another side's holder, the other side's checklist text, another side's sealed answer before release,
a revealed value before Apply, the window die, or a result built from a hidden input; a guest telling a hidden value apart through
choices, picks, Preview or refusal texts; any acting right gained at game over; old packs or saves changing bytes.

## Approaches
Picked: one static visibility class called by every existing rule; game over as one more "sees everything" case at the entry points of
information (not of acting); sealed answers stored on the pending run (hidden by the existing ask-run view rule) and released in the
completing answer's action; reveal lines as public System lines whose window is drawn at command time on the host.
Rejected: a separate "revealed" flag per value (a second visibility store to keep in step with undo); a sealed answer log hidden by a new
line rule (the run already carries ownership); revealing through a dice line (it would give the offset away); judging choices on the full
state and hiding the refusal (the outcome itself would still differ).

## Left to the builder
Test layout; the shared invented fixture (two sides `north` Northern League / `south` Southern Compact, depots, a hex board); exact private
helper names; wiki prose around the exact strings.
