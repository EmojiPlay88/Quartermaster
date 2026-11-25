# Acting on other pieces (batch B3) — design (short)

Date: 2026-11-25. Branch `b3-other-pieces` (from master `74a592f`). Engine wishlist #3, #43, #37, #52, #68. The user accepted
every default of the batch; the choices below are those defaults, made concrete. Details the defaults left open are marked
"decided". Builds on the Piece binding (`2026-11-24-other-piece-design.md`) and on the B2 pass (`FormulaRunner.BeginPass`).

## Intent

A formula changes only values, and only on the counter it runs on, a holder or the global holder. Big packs work around five
gaps with two-half formulas and checklist lines:
- a transfer between two counters (give on one, receive on the other) is two runs whose amounts cannot be checked (#3);
- a result that falls on the other side's counter is applied by that side by hand (#43);
- a result that creates a counter (a wreck, a marker) cannot place it or set its values (#37);
- a result that ends a counter cannot remove it (#52);
- a counter whose layer or mask shows its state is turned by hand after each run (#68).

## Design (decided)

Every new pack field is optional and omitted from JSON when empty or default. Blank text becomes null on load
(`RulesPackIo.Normalise`). Old packs keep their bytes and SHA-256, and `CurrentFormat` stays 4. Old saves keep their bytes:
the one new command member (`RemovePiece.SideId`) is omitted when null. Every new expression is parsed and checked by the
validator. Every new field gets a Workbench editor and a wiki section with invented Riverland examples. Public API changes are
additive (new members on positional records are init properties in the record body).

**One run, one undo step** (decided, as B2 decided for #31): no compound `GameCommand` type. Every command of a run (rolls,
placements, ledger lines, layer/mask changes, removals, the working line, seeds of a placed piece, `ResolveRun`) executes inside
the one `GameSession.BeginAction` the apply path already opens (`PhaseController.ApplyFormula`, the Fully automatic unit loop in
`PhaseController.Apply`, the local branches of Desktop `FormulaWindow` and App `FormulaDialog`). One Undo takes the whole run back.

### The output model (Rules, `src/Quartermaster.Rules/Model/FormulaDef.cs`)
- `public enum OutputAction { Write, Place, Remove, SetLayer, SetMask }`.
- `OutputTarget` gains `Piece` and `Placed`, appended after `Holder` (JSON `"piece"`, `"placed"`; the string enum converter
  makes the numeric values irrelevant to files).
- `FormulaOutput` gains (all `[JsonIgnore(WhenWritingNull)]` / `WhenWritingDefault`):
  - `Action : OutputAction` (default `Write`, omitted). `Write` is today's behaviour.
  - `PieceVariable : string?`: the Piece binding whose picked piece a `Target = Piece` output acts on.
  - `Slot : string?`: Place only. The palette entry, as `ModuleModel.SlotByPath` reads it (nav path segments and entry name
    joined with `/`), else a gpid (`SlotByGpid`), the same lookup the PlaceMarker trait uses (`PieceActions.Place`).
  - `Layer : string?`: SetLayer only. The layer trait's name (`LayerDef.Name`, trimmed, case-insensitive; the first match).
  - `ToMapId : string?`: Remove only. Null = delete the piece; a map id = move it there and keep its ledger.
- Which targets each action takes (decided):

  | Action | Targets | Value (`Expr`, null = the formula's result) |
  |---|---|---|
  | Write | Unit, Global, Holder, Piece, Placed | the number written (Placed: Op must be Set) |
  | Place | Unit (this piece's hex), Piece (the picked piece's hex) | not used |
  | Remove | Unit, Piece | not used |
  | SetLayer | Unit, Piece, Placed | the level: a whole number, 1..levels; 0 = inactive |
  | SetMask | Unit, Piece, Placed | 1 = masked, 0 = not masked |

- `VariableBinding.SameHex : bool` (`WhenWritingDefault`), Piece bindings only.

JSON example (invented):
```json
{ "target": "piece", "pieceVariable": "Receiver", "property": "p-fuel", "op": "add", "expr": "Amount" }
{ "action": "place", "target": "unit", "slot": "Markers/Wreck", "property": "" }
{ "action": "setLayer", "target": "placed", "layer": "Levels", "property": "", "expr": "2" }
{ "action": "remove", "target": "piece", "pieceVariable": "Target", "toMapId": "map-dead", "property": "" }
```

### #3 Writing to another counter
- A `Write` output with `Target = Piece` writes the property of the piece its `PieceVariable` binding picked. The value, Op and
  property rules are those of a Unit output (Pieces/Both scope; computed refused). The binding is always asked (Fully automatic
  stops for it, as today). Before reads the picked piece's value through `PropertyValues` as the binding does.
- **Candidates, same hex first** (decided, applies to every Piece binding): `PieceChoices` sorts the candidates that share the
  run piece's hex (same `MapId` and same `PieceInfo.Location`, as `hexsum`) first, then the rest, each group by label as today.
  Labels and the ` #2` rule are unchanged. Without a run piece, or off grid, the order is today's.
- **`SameHex`**: the candidates are only the pieces in the run piece's hex, never the run piece itself (decided). No run piece:
  missing `Needs a unit`. No candidate: missing `No piece in this hex`.
- API: `PieceChoices.For(GameState, RulesPack, ModuleModel, VariableBinding, IReadOnlySet<string>?, string? runPieceId)`
  overload (the old one keeps its signature and calls it with null). `FormulaRunner.PickedPiece`, the Formula dialog model,
  the batch grid and `FormulaIntentRunner`'s pick check all pass the run piece id, so host and guest build the same list.
- Working: the output's part reads as a Unit output's, with the piece named as the binding names it
  (`Fuel of Blue Barge 4 + 2 = 6`).

### #43 Results on another side's counter
- A `Target = Piece` output may act on a piece of any side, including one the runner does not hold. The run piece rules are
  unchanged (`CanActFor` on the run piece). **A guest writes to a piece it does not hold only through a Piece output**: manual
  ledger edits and Unit outputs keep their refusals. DECISION default: applied at once, undoable; the owner does not confirm.
- **Host decides.** On the host the pick resolves among the candidates the requesting guest sees (`RunRequest.PickSides`). On a
  player view state, a Piece-target output whose target is a masked placeholder is missing `Hidden from this player` (as the
  binding is). `FormulaMasking.PieceOutputInput = "piece output"` (a marker like `HolderCallInput`: masks no box, masks the
  formula's working and errors) is added when any `Target = Piece` output writes a property that is not Public, or when
  `PieceAccess.PiecesMayHide` holds and the formula has any `Target = Piece` output. Static (pack and module), so host and guest
  agree. `PlayerView`'s `_hiddenInputFormulas` is not widened: the line rules below keep the target's values off other sides' logs.
- **Log lines** (decided; no new `PlayerView` rule):
  - Each Piece-target write is its own `LedgerDelta`: `Key` = the target, `PieceId` = the target, `SideId` = the **target's
    side** (from the side map when the trace is made; null for a piece of no side), `Subject` = the target's name,
    `Working` = `{formula}: {part}` only, `Tag` as the run's. The defender (holds the target side) sees
    `{target}: {formula}: Strength 7 − 2 = 5`; the acting side sees `{Defender side} ledger changed` (owner not held); a piece of
    no side is public.
  - The run's working, written on the acting side's own lines (Unit, Global and Holder writes), leaves out Piece-target parts
    and lists effects (below) by the names **the acting player sees** (a masked pick is `Unknown piece`).
  - `runs()`: `AppliedRun` goes on the first Unit, Global or Holder write line. A run with none (only Piece/Placed writes or
    effects) gets its working line from `FormulaRunner.Commands` itself: `AddLogLine(Calc, "{subject}: {working}" + tag,
    PieceId: run piece, SideId: acting side, FormulaId) { AppliedRun }`, subject defaulting to the formula name.
    `PhaseController.LogWorking` returns when the trace has any output or any effect.

### #37 Placing a counter
- A `Place` output builds the palette entry (`PieceFactory.FromSlot`) on the anchor's map and position, stacked onto the
  anchor's stack (as the PlaceMarker trait with no offset). Anchor = the run piece (`Unit`) or the picked piece (`Piece`). At
  most one Place output per formula (decided).
- `Target = Placed` outputs act on the new piece: Write (Op must be Set; Before shows the property's Default), SetLayer,
  SetMask. The new id is made when the trace is made, by `FormulaRunner.NewPieceId` (public `Func<string>`, default
  `"p:" + Guid.NewGuid().ToString("N")`, as `PaletteModel.Place`), so previews and Placed ledger keys agree.
- Seeding (decided): right after the `AddPiece` executes, the apply path calls `ModuleSession.SeedAfterAdd(add)`, then the
  Placed writes run, so a Set overrides a sheet or starting value seed. The placed piece never joins the current entry's units.
- Log: `AddPiece.LogText` = `{formula}: placed {name}` (the reducer's Place line; kept or dropped by the placed piece's own
  visibility). Working part: `placed {name}`.

### #52 Removing a counter
- A `Remove` output deletes its target (`RemovePiece`, ledger removed as today), or with `ToMapId` moves it to the centre of
  that map's first board (`MovePieces`, not stacked; ledger kept). DECISION default: delete.
- **Removals run last** (decided): whatever the listed order, a run's commands are: rolls, Place, ledger writes (own, then
  Piece, then Placed), SetLayer/SetMask, the working line when needed, then removals in output order. So every write before a
  removal is logged against a piece that still exists.
- Log: delete: `RemovePiece.LogText` = `{formula}: removed {name}` with the new `RemovePiece.SideId` (init property, omitted when
  null) = the removed piece's side; `LogBuilder.Delete` stamps it, so the owner keeps the line and other sides drop it (the
  existing deleted-piece rule). Move: `MovePieces.LogText` = `{formula}: moved {name} to {map name}`. Working parts:
  `removed {name}`, `moved {name} to {map name}`.
- In the Fully automatic loop a unit whose piece no longer exists is skipped silently (decided).

### #68 Layers and masks
- `SetLayer`: finds the target's `LayerDef` named `Layer` and writes `TraitState.LayerSetLevel(def, state, n, active: n > 0)`
  with a `SetTraitState`. Value rules: whole number 0..levels (levels = `max(Images, LevelNames)`); 0 only when the layer is not
  always active.
- `SetMask`: the target's first `ObscurableDef`; new `TraitState.ObscuredSet(ObscurableDef, string? state, bool on, string
  obscurer)` writes `"null"` or `"{obscurer};"`. Obscurer = `"local"`, as the Mask menu (`PieceAccess.Masking` ignores it).
- Each change logs `AddLogLine(LogKind.System, text, PieceId: target, SideId: target's side)` with the target's real name:
  `{formula}: {layer} of {name} = {level}` (level name when the layer has one, else the number) and `{formula}: {name} masked` /
  `{formula}: {name} unmasked`. The existing rules drop it for a viewer from whom the piece is hidden or masked afterwards.
  Working parts use the same texts without `{formula}: `.
- **`layer("name")`**: the context piece's level of that layer, 0 when inactive. Parser arity (1, 1); added to
  `Evaluator.FunctionNames`; `IEvalContext.Layer(string name)` is a default method throwing `layer() is not available here`,
  implemented in `FormulaRunner.RunContext` and `PieceFilter.FilterContext`. On a player view placeholder it throws
  `layer() cannot be read in this view` with `HiddenData = true`. Allowed wherever `marker()` is (formulas, bindings,
  preconditions, outputs, filters, applies-to, starting values, warnings, alerts); not in phase conditions (added to
  `ForbiddenConditionFunctions`) and treated as piece-reading by `ExprFacts` exactly as `marker`.
- Layer state is drawn on the map, so `layer()` on a visible piece is not hidden information.

### The BeginPass invariant
- New `FormulaRunner.AfterApply(IReadOnlyList<GameCommand> executed)` (the old `AfterApply()` stays): when any executed command is
  `AddPiece`, `RemovePiece`, `MovePieces` or `SetTraitState`, the open pass drops its side map and aggregate state; they are
  rebuilt lazily on the next run. `PhaseController.Apply` passes each unit's commands. The `BeginPass` doc comment is updated:
  piece commands are allowed inside a pass when followed by this call. A deterministic test pins it.

### Applying (Render)
- New `FormulaApply.Execute(ModuleSession s, IEnumerable<GameCommand> commands, Action<GameCommand> exec)`: executes in order
  and calls `s.SeedAfterAdd` after each `AddPiece`; returns the executed list. Used by `PhaseController.ApplyFormula`,
  `PhaseController.Apply` and the local branches of Desktop `FormulaWindow.Apply` / App `FormulaDialog`.
- `FormulaIntentRunner.Run`: the pick check uses the new `PieceChoices.For` overload with `i.PieceId`; `GuestRule` gains no
  refusal for Piece, Placed or effect outputs (#43 default).

### Validator (errors unless marked; every message prefixed `Formula "{f}": `)
- `output {n} acts on another piece but names no Piece variable`
- `output {n} names variable "{v}", which is not a Piece binding`
- `output {n} names a Piece variable but its target is not Piece`
- `output {n}: {action} cannot target {target}` (enum names, e.g. `output 2: Remove cannot target Global`)
- `output {n} places a piece but names no palette entry`
- `output {n} places palette entry "{slot}", which does not exist in the module` (only when slots are known)
- `output {n} names a palette entry but does not place a piece`
- `output {n} places a second piece; only one output may place a piece`
- `output {n} writes the placed piece, but no output places one` (any action on Placed)
- `output {n} writes the placed piece, so its operation must be Set`
- `output {n} sets a layer but names none`
- `output {n} names a layer but does not set one`
- `output {n} moves the piece to map "{id}", which does not exist in the module` (only when maps are known)
- `output {n} names a map but does not remove a piece`
- `output {n} has a property list but does not write a value`
- the existing scope message with `a piece` for Piece and Placed writes:
  `output property "{p}" cannot be written to a piece (its scope is {scope})`
- warning: `variable "{v}" has "same hex" but is not a Piece binding`
- expression tail (`FilterRefProblems`, so every existing prefix applies): `layer() needs a layer name in quotes`
- Non-Write outputs skip the property checks (their `Property` is `""`).
- New overload `RulesValidator.Validate(RulesPack pack, IReadOnlyCollection<string>? knownMapIds,
  IReadOnlyCollection<string>? knownSlots)` (no optional parameters, so `Validate(pack)` stays unambiguous); the old one calls it
  with null. Workbench passes `Model.Slots` (path and gpid of each).

### Runtime messages (exact)
`An output refers to a deleted Piece variable`; `An output writes the placed piece, but nothing was placed`;
`Palette entry "{slot}" was not found in the module`; `No piece in this hex`; `{name} has no layer "{layer}"`;
`Layer "{layer}" has no level {value}`; `Layer "{layer}" follows a property, so it cannot be set`;
`Layer "{layer}" follows a property; read the property instead` (layer() on such a layer); `{name} has no mask`;
`The mask value must be 0 or 1`; `Map "{map}" was not found in the module`; `Map "{map}" has no board`;
`layer() is not available here`; `layer() cannot be read in this view`.

### Workbench (Avalonia Desktop; the WinForms App builds only)
- Formulas tab, outputs grid, columns appended after `Pick by` (older columns keep their indexes): `Action` (combo of
  `OutputAction` names), `Piece` (combo: blank + the formula's Piece binding variables), `Palette entry` (combo: blank + module
  slot paths), `Layer` (text), `To map` (combo: `(delete)` + map names). The `Target` combo already lists every
  `OutputTarget`. `FormulaEdit` gains `WithOutputAction`, `WithPieceVariable`, `WithSlot`, `WithLayer`, `WithToMap`;
  `WithOutputTarget` clears `PieceVariable` when the target is not Piece; `WithOutputAction` clears the fields the new action
  does not use.
- Bindings grid: `Same hex` check column appended after `Dice count`.
- Tooltips (exact): `Action`: `Write a value (default), place a palette counter, remove the counter, or set its layer or mask.`;
  `Piece`: `The Piece variable whose picked counter this output acts on (target Piece).`; `Palette entry`: `Place: the palette
  counter to place in the hex.`; `Layer`: `Set layer: the layer's name on the counter; the value is the level (0 = off).`;
  `To map`: `Remove: move the counter to this map instead of deleting it.`; `Same hex`: `Piece binding: list only the other
  counters in this unit's hex.`
- **Wiki** (Making-a-Rules-Pack, new `## Acting on other pieces` with five `###` sections; Formula-Reference
  `### layer("name")`, output actions, the runtime messages in the error table; Window-Rules-Workbench columns), invented
  Riverland examples: `Barge transfer` (Unit Subtract Fuel, Piece Add Fuel to `Receiver`, same hex); `Shelling` (the Northern
  League hits a Southern Compact unit's Strength; what each side's log shows); `Wreck` placed with `Salvage` set; `Bridge`
  removed at Strength 0, or moved to `Dead pile`; `Fort level` with `layer("Levels")` and a minefield `SetMask 0` reveal.

## Public API changes
All additive: `OutputAction`; `OutputTarget.Piece`/`Placed` (appended members); `FormulaOutput.Action/PieceVariable/Slot/
Layer/ToMapId`; `VariableBinding.SameHex`; `IEvalContext.Layer` (default method, so downstream `IEvalContext` implementations
still compile); `RulesValidator.Validate` 3-argument overload; `PieceEffect`/`PieceEffectKind` and `FormulaTrace.Effects`
(body init, default empty); `OutputPreview.Target`, `SideId`, `Part`, `Subject` (body init); `FormulaRunner.NewPieceId`,
`FormulaRunner.AfterApply(IReadOnlyList<GameCommand>)`; `PieceChoices.For` overload; `RemovePiece.SideId` (body init);
`TraitState.ObscuredSet`; `FormulaApply.Execute`; `FormulaMasking.PieceOutputInput`.

No signature changes. Behavioural: Piece binding candidates now list same-hex pieces first (order only). A downstream
`switch` over `OutputTarget` with a `_` arm (e.g. "anything else is Holder") silently maps the new members; harmless until a
pack uses them, but worth a grep downstream.

## Non-goals
Owner confirmation before a result lands; picking on the map; placing anywhere but the anchor's hex; several placed pieces per
run; removing holders; other trait changes (rotation, labels, hide); "a shared result both sides can read"; a new `PlayerView`
rule letting the acting side read a Public value on the target line.

## Anti-goals
Old packs or saves changing bytes; a candidate list, preview, error or log line revealing a hidden piece or another side's
hidden value; a piece command inside an open pass without a refresh; a run that undoes in more than one step.

## Approaches
Picked: actions on `FormulaOutput` (one list, one editor), effects resolved in the trace and turned into existing commands,
target-side stamping of Piece lines (no new redaction rule), one action scope for undo, a pass refresh hook.
Rejected: a compound `GameCommand` (new save shape; every consumer would look inside); a separate "effects" list on
`FormulaDef` (two editors, ordering across lists); owner confirmation queue (#43, declined default); redacting the acting
side's whole working when a Piece output exists (loses the attacker's own roll and result).

## Left to the builder
Test layout; `PieceEffect`'s exact members; where `FormulaApply` lives in Render; column widths; wiki prose around the exact
strings.
