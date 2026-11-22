# What formulas and filters can read — design

Date: 2026-11-22. Branch `formula-reads` (from master `58083c6`). Short design; the user accepted the
calls below ("go on").

## Intent

Pack authors keep writing two copies of a formula, typed 0/1 inputs or hand-set flags because
expressions cannot see a few facts the engine already knows: which side owns a piece, game-level
values (globals, holder values) inside filters, which side's phase it is, and which map a piece is on.
This batch makes those readable.

Success: one formula with `lookup(Rate, turn(), side())` replaces a per-side pair; an OnStart entry
filter `Weather == 2` skips every unit when the weather is not hot; a phase warning
`holder(Rail, Tonnage) > holder(Rail, Capacity)` shows once as a game-level line; a modifier reads
`if(side() == phaseside(), 1, 0)`; upkeep filters `map() == "Main Map"`.

## Goals

1. `side()`: the name of the side owning the piece (`Ownership.PieceSides`), `""` when none.
2. `phaseside()`: the name of the current phase's side, `""` when the phase has none or there is no phase.
3. `map()`: the piece's map name (`PieceInstance.MapId`, which is the VASSAL map name).
4. `holder(Holder, Property)`: a holder's value (holder and property by bare name, like `lookup`'s table),
   falling back to the property's Default when unset.
5. In piece filters (applies-to, side unit filters, OnStart/OnMove entry filters, warnings, alerts) a bare
   name that is a **Holders**-scope property reads the global holder (Default when unset). Pieces- and
   Both-scope names keep today's piece reading.
6. A warning or alert whose condition is **game-level** shows once, not once per unit.
7. Validator, Workbench preview text, wiki follow.

## Calls made (user accepted)

- `phaseside()` instead of a seat or "acting player" concept: the engine has no notion of who is at the
  keyboard (solo play has none); a formula always runs on one of its runner's pieces, so
  `side() == phaseside()` answers "is the acting player the phasing player".
- Sides are returned by **name** (what authors type into tables), not id.
- `side()` inside a side's own unit filter (directly or through an applies-to it reads) would recurse:
  run-time `EvalError` ("side() is not available in a side's unit filter"); validator error on any side
  unit filter using `side()`.
- Hidden information: no new rule. Player views already strip the other side's Private values, so a
  filter reading one gets an error and is false, exactly as piece values today.
- Game-level = the condition's AST uses no Pieces- or Both-scope property, and none of `name()`,
  `marker()`, `has()`, `side()`, `map()`. Such a condition is evaluated once with no piece; a true result is
  one hit: a warning row with the message as its text and no pieces; an alert with one match
  `AlertMatch(null, HolderDef.GlobalId, message)`. Decision warnings still need acknowledging.

## Non-goals

- Seats / "who is running this" (see above). Reading another piece (#45), sums over pieces (#17).
- Phase conditions stay as built: they may not use `side()`, `map()`, `holder()` (validator forbids them
  with the existing forbidden-function message) — `phaseside()` is allowed (public).
- Formula bindings unchanged; the four functions also work in formulas (formula context has the piece).

## Anti-goals

- A filter that hangs (side() recursion) or crashes Next Phase.
- A game-level warning listing every unit; an old per-unit warning changing to game-level (any old
  condition that reads a piece value keeps its per-unit rows).
- An old pack or save behaving differently or changing its SHA-256.

## Constraints

Public repo, invented data only. No new serialized members expected; if any, `WhenWritingDefault`,
`CurrentFormat` stays 4. No new packages. UI tests headless.

## Left to the builder

Where the recursion guard lives; how the game-level check is shared (one AST helper in Rules used by
Core and the validator); the Workbench/working-line display for the new calls; test layout.
