# Holders at run time and random picks (batch B9) — design (short)

Date: 2026-11-27. Branch `b9-holders-random` (from master `048a67c`, B8 merged). Engine wishlist #13 (holders chosen at run time,
holders that change side) and #55 (picking at random among records), the two items deferred by earlier batches (B4 "Non-goals" names
#55). The choices below are defaults made by the planner; each open choice is marked "DECISION (default)" so the controller can accept or
change it before Task 1. Details no default touches are marked "decided". Builds on the Piece binding and `RunRequest.PickSides` (#45,
`2026-11-24-other-piece-design.md`), the B3 piece outputs (`2026-11-25-b3-other-pieces-design.md`), the B4 pins and group dice
(`2026-11-25-b4-group-runs-design.md`), B6 keyed values, the B7 visibility rule (`2026-11-26-b7-visibility-design.md`) and the B8 held
set (`2026-11-26-b8-teams-design.md`).

## Intent

- A formula output names one fixed holder, so a result that falls on "whichever dump the player draws from" is one formula per dump; and a
  holder's owner is fixed in the pack, so a dump that changes hands in play is modelled as a counter, not a holder (#13).
- Some results name a number of losses that fall at random on the members of a group spread over several counters (three hits among the
  boats of four barges in a hex). Today the owner numbers the boats, rolls by hand and runs the loss formula once per barge (#55).

## Principles (decided)

1. **Ownership stays one function.** Every rule that asks "which side owns this holder" (`HiddenValues.HolderValueHidden`,
   `PlayerView`, `ModuleSession.CanEditLedgerFor`, `IntentExecutor.GuestRule`, the below-zero alert, `FormulaMasking`) gets the answer from
   `Ownership` with the game state at hand. No rule keeps its own copy of the owner, and none reads `HolderDef.SideId` directly any more.
2. **An owner every player can work out.** A holder's run-time owner is computed only from Public data, so the full state and every
   player view give the same owner: no snapshot field, no circular "is the owner hidden" question, nothing for the host to send.
3. **Choices are judged on the chooser.** A holder picker offers the holders the chooser may act on with the chooser's held set (B7
   principle 3, B8 `SideFacts.Held`), on the host as on the guest.
4. **A random draw is dice.** Each draw step is one die through the run's existing roll path (pins, the host's per-sender pins, the
   dialog's pinned rolls), so previewing again draws nothing new, a batch preview chain and its Apply agree, and replay and undo use the
   commands the run produced (no re-draw on replay).
5. **Nothing of which members were drawn is shown before Apply.** The log, after Apply, names which counters gave how many, stamped with
   each counter's side (the B3 rule for Piece-target lines).
6. Packs without the features pay nothing: no owner expression is evaluated when no holder has one; no draw code runs without a Draw
   binding.

Every new pack field is optional and omitted from JSON when empty or default; blank text becomes null on load (`RulesPackIo.Normalise`).
Old packs keep their bytes and SHA-256, `CurrentFormat` stays 4. Old saves keep their bytes: B9 adds **no** save, command or intent member
(holder picks travel as typed labels in `RunRequest.Typed` / `FormulaIntent.Typed`, as Piece picks do; draws produce existing commands).
The validator parses and checks every new expression. Every new field gets a Workbench editor and a wiki section with invented Riverland
examples (no content from any published game); expression identifiers use letters, digits and `_` only. Public API changes are additive
(init properties, new overloads with a distinct parameter list, enum members appended, new classes; no changed signature).

## Design

### Pack model (Rules)
- `HolderDef.OwnerExpr : string?` (`WhenWritingNull`; blank → null): the holder's owner as play goes on (below).
- `BindingKind.Draw` (appended after `Piece`; JSON `"draw"`).
- `VariableBinding.HolderChoices : List<string>?` (`WhenWritingNull`; `Normalise` trims, drops blanks and duplicates (ordinal), empty →
  null): Holder bindings only; the ids of the holders a player chooses from when the formula runs.
- `VariableBinding.DrawCount : string?` (`WhenWritingNull`; blank → null): Draw bindings only; the expression giving how many members to
  draw.
- `FormulaOutput.HolderVariable : string?` (`WhenWritingNull`; blank → null): `Target = Holder` only; the Holder binding (with holder
  choices) whose chosen holder the output writes, in place of `HolderId`.
- A Draw binding reuses `Property` (the weight: each piece's number of members; null = one each), `PieceFilter` (which pieces are in the
  pool) and `SameHex`. A Draw binding's outputs reuse `Target = Piece` with `PieceVariable` = the Draw variable.

JSON (invented):
```json
{ "id": "h-ford", "name": "Ford Dump", "sideId": "north", "ownerExpr": "keyed(DumpOwner, \"Ford Dump\")" }
{ "variable": "Dump", "kind": "holder", "property": "p-stores", "holderChoices": ["h-ford", "h-mill", "h-north"] }
{ "target": "holder", "holderVariable": "Dump", "property": "p-stores", "op": "subtract", "expr": "Need" }
{ "variable": "Hits", "kind": "draw", "property": "p-boats", "sameHex": true, "pieceFilter": "side() == \"Southern Compact\"", "drawCount": "Shots" }
{ "target": "piece", "pieceVariable": "Hits", "property": "p-boats", "op": "subtract", "expr": "Hits" }
```

### #13a Holder owner at run time (Core `HolderOwners`, `Ownership`, `HiddenValues`)
- **Meaning of `OwnerExpr`**: evaluated against a game state, its result names the owner:
  - text: the side whose name or id equals it (trimmed, case-insensitive; a former id reads as its listing side, `SideFacts.Canonical`);
    `""` → no owner (public);
  - a number: a whole N from 1 to the number of sides → the Nth side in pack order; 0 → no owner;
  - anything else (an evaluation error, a text naming no side, a number out of range or not whole) → the fixed `SideId` (which may be null
    = public). DECISION (default): fall back to the fixed owner, not to "hidden from everyone": a broken expression must not lock every
    player out of a ledger; the validator catches what it can.
- **Context** (decided): the phase-condition context (`PhaseConditions`' `PhaseContext`: constants, pure functions, tables, `turn()`,
  `stage()`, `segment()`, `phaseside()`, `keyed()` of a global property) with two changes: a bare property name reads **this holder's**
  value when the holder keeps the property (its Default when unset), else the global holder's; piece aggregates are refused (the
  validator's `ForbiddenConditionFunctions` plus every whole-game piece aggregate it knows: `sum`, `countof` and the rest), so a view missing
  a hidden piece cannot give another owner. Values are read straight from the state (no `HiddenValues` call; the validator
  guarantees every value read is Public, so every view holds it); owner resolution therefore never recurses.
- New `src/Quartermaster.Core/HolderOwners.cs`, `public static class HolderOwners`:
  - `HasDynamic(RulesPack? pack) : bool` — some holder has an `OwnerExpr`.
  - `Owner(GameState? s, RulesPack pack, HolderDef h) : string?` — the rule above; `s` null or no `OwnerExpr` → the fixed owner exactly as
    today (`Ownership`'s existing "existing side" rule: a dangling id → null). Parsed expressions are cached per `HolderDef` record (as
    `FormulaRunner.ParseCache`).
  - `[ThreadStatic] internal static int Evaluations` (tests).
- `Ownership` (additive):
  - `SideOfHolder(string holderId, RulesPack pack, GameState? s) : string?` — the holder's current owner (`HolderOwners.Owner`); the
    existing 2-argument overload stays and keeps meaning "the fixed owner" (doc comment updated: no caller in `src` uses it any more).
  - `HolderSides(GameState s, RulesPack pack) : Func<string, string?>` — bulk use, memoised per holder for one state as it stands (as
    `PieceSides`); without `HasDynamic` it is the fixed lookup (no evaluation).
- `HiddenValues` (additive): `HolderValueHidden(GameState? s, RulesPack pack, string holderId, PropertyDef? property, Func<string, bool>
  holds) : bool` (leading `GameState` parameter, so no overload ambiguity with B7's internal memo overload), and the internal memo overload
  gains a `Func<string, string?>? holderSide` parameter; the order of the B7 rule is unchanged, only the owner comes from the state. The
  B7 4-argument overload stays (fixed owner).
- **Every caller moves to the state-aware owner** (decided; the plan's grep check lists them): `Alerts.cs` (below zero), `HiddenValues`
  itself, `KeyedValues.HiddenInView`, `PieceFilter.HolderKeyValue`, `PlayerView` (one `HolderSides` per `For`, used by `Filter`),
  `PropertyValues.HiddenInView`, `FormulaRunner` (output previews of Global/Holder writes), Render `FormulaMasking.HiddenVariables`
  (`HolderHidden`), `ModuleSession.CanEditLedgerFor`, `IntentExecutor.GuestRule`. `RulesValidator`'s B7 filter warning ("`holder()` of a
  holder with a side") counts a holder with an `OwnerExpr` as a holder with a side.
- **Acting rights follow the current owner** everywhere B7/B8 used `HolderDef.SideId`: manual ledger edits (`CanEditLedgerFor`, host and
  guest), a guest's Holder output (`GuestRule`), the Ledger window's lock, the below-zero chip, and visibility (`PlayerView`, masking).
  Allies (B8) follow through the held set as before.
- **When the owner changes** (decided): it changes when the values it reads change, i.e. through ordinary writes (a formula output, a
  manual edit, a phase hook); undo, redo, replay and turn files restore it with those values. No extra log line (the write that changed it
  is logged; DECISION (default): no "now belongs to" line, since a derived owner has no command of its own). A view rebuilt after the write
  (hot-seat handover, the host's next snapshot) hides or shows the holder's Private values by the new owner at once.
- **Hot-seat, online, turn files**: nothing to send; a guest's view computes the same owner from the same Public values. The host checks
  every guest intent with the state-aware owner.

### #13b Holder chosen at run time (Core `HolderChoices`, runner; Render dialog, grids, host)
- A Holder binding with `HolderChoices` is a **holder picker**: its `HolderId` is not used (validator warning when set). Its value is the
  binding's property on the chosen holder, read as a fixed Holder binding reads it (Default when unset; keyed properties refused as today).
  Working: `{Prop} of {holder name}`.
- **Candidates** (decided; DECISION (default) "only holders the chooser may act on"): the listed holders, in list order, whose current
  owner (`Ownership.SideOfHolder(id, pack, s)`) is null or in the chooser's held set; the chooser is `RunRequest.PickSides` (host: the
  requesting guest's held set; hot-seat: the viewer's; null = solo or a hook run: every listed holder). The Global holder is never a
  candidate (validator error). Label = the holder's name (blank → its id; a repeated label gets ` #2`, ` #3` in list order). So a picked
  holder's values are always visible to its chooser and every write to it is allowed: no masking, no new guest refusal.
- New `src/Quartermaster.Core/HolderChoices.cs`, `public static class HolderChoices`: `For(GameState s, RulesPack pack, VariableBinding b,
  IReadOnlySet<string>? held) : IReadOnlyList<HolderChoice>` (`HolderChoice(string HolderId, string Label)`), constants
  `Unknown = "Choose one of the listed holders"`, `None = "No holder to choose"`.
- **Runner**: the typed label is resolved among `HolderChoices.For` of the run's state with `PickSides` (memoised per variable per run, as
  `Pick`); blank → missing `Waiting for input`; not listed → missing `Choose one of the listed holders`; no candidate → missing `No holder to
  choose`. Always asked (Fully automatic stops for it, as for a Piece binding).
- **Outputs**: `Target = Holder` with `HolderVariable` writes the chosen holder (the variable's pick; Op, value and kept-by rules as a fixed
  Holder output: the property must be kept by every listed holder, validator). A Reveal may name a `HolderVariable` too.
- **Render**: `FormulaDialogModel` shows the picker as a dropdown of labels (both front ends read the model; the WinForms App builds
  only); `BatchModel.ChoicesFor` and `MenuBatchModel` list the labels (a choice column, `All rows` fills it); `MissingInputs` treats it as
  an always-asked box; `FormulaIntentRunner.Prepare`'s pick check refuses a label not in the sender's list with `Choose one of the listed
  holders`; `IntentExecutor.GuestRule` skips `HolderVariable` outputs (the pick check covers them) and checks fixed Holder outputs with the
  state-aware owner.

### #55 Random draws (Core runner; Render dialog, masking, pins)
- **The pool** (decided): the pieces matching `PieceFilter` (unit-filter text; null = every piece) — with `SameHex`, only the pieces in the
  run piece's hex, **including the run piece** (DECISION (default): unlike a Piece binding, a draw falls on a group the run piece belongs to;
  no run piece → missing `Needs a unit`) — each with weight = its `Property` value (read through `PropertyValues`: Default when unset; a
  computed property that does not apply = 0; null `Property` = 1). A weight must be a whole number from 0 to 1000, else the run fails:
  `{Prop} of {name} must be a whole number from 0 to 1000 to draw from`. The pool is built on the **full state** of the run, never on a
  chooser's view (a draw is not a choice; the losses fall on members the acting player may not see). Pool order: piece id, ordinal; the
  members of a piece are numbered one after another in that order.
- **The count**: `DrawCount` is evaluated in the run context (it may read earlier bindings, never the Draw binding itself); it must be a
  whole number from 0 to 1000, else the run fails `The draw count for {Var} must be a whole number from 0 to 1000`. The binding's value is
  k = min(count, members in the pool).
- **The draw** (without replacement): for i = 1..k, one die with `remaining` faces under the key `draw:{Var}#{i}` through the run's roll
  path (`FixedRolls` first, then `RunRequest.Dice`, then the roller); face f takes the f-th remaining member. A pinned die outside 1..remaining
  is drawn again (the pool changed since it was pinned). DECISION (default): always drawn by the engine with the session roller, also in
  physical-dice mode (a die with a changing number of faces is not a table die); never shared by `GroupRoll` (group dice are dice, a draw
  is per run).
- **Trace**: `FormulaTrace.DrawRolls : IReadOnlyDictionary<string, IReadOnlyList<int>>` (body init, empty) holds the draw keys (not in
  `Rolls` or `RollsByKey`, so `count()`, `dice()` and the dice log never see them); `FormulaTrace.Draws : IReadOnlyList<DrawPreview>` (body
  init, empty), `DrawPreview(string Variable, IReadOnlyList<(string PieceId, int Count)> Drawn)` in pool order, pieces with Count ≥ 1 only.
  `FormulaDialogModel` merges `DrawRolls` into its pinned rolls, and the host's per-sender pins cover them through `RunRequest.Dice`, so a
  preview and its Apply draw the same members.
- **Outputs on drawn pieces**: a `Target = Piece` output whose `PieceVariable` is a Draw variable acts once on **each** drawn piece, in pool
  order: `Write` (Unit-output rules, as B3 Piece writes), `Remove`, `SetLayer`, `SetMask` (each drawn piece); `Place` and `Reveal` cannot
  act on a draw (validator). Inside such an output's value expression the Draw variable reads **that piece's count** (decided); everywhere
  else (the formula text, other outputs, preconditions) it reads k. Each per-piece write is its own `LedgerDelta` (B3: key, `PieceId` and
  `SideId` = the target piece's). B3's command order holds (removals last). `OutputPreview.Draw : string?` (body init) names the Draw
  variable on those previews.
- **Log** (decided): after the run's ledger writes and before its layer/mask lines, reveals and removals, one
  `AddLogLine(LogKind.System, "{formula}: {Var}: {n} on {name}", PieceId: piece, SideId: the piece's side)` per drawn piece (the piece's
  real name; a piece of no side → no stamp, public). So the owner of each drawn piece (and its allies) sees which of its counters gave how
  many; other sides see the existing `{Side} ledger changed` for the writes and nothing for the draw lines; the acting side's own working
  shows `{Var} = {k}` and leaves out the Piece-target parts (B3). DECISION (default): the acting side learns k, not the split.
- **Before Apply** (decided): nothing of which pieces were drawn is shown, masked or not: the Draw box shows k (or `HiddenBoxText` when
  masked) with tooltip `Drawn at random when you apply; the log names the counters.`; every output on drawn pieces previews as one line
  `{Prop} of the drawn counters changes` in the dialog and the batch grids' Ledger column; a guest's preview verdict carries no
  `DrawRolls` and no per-piece values.
- **Masking**: `FormulaMasking.DrawInput = "draw"` (a marker like `PieceOutputInput`: masks no box of its own beyond the Draw box, masks the
  formula's working and errors) is added when a Draw binding's weight is not Public, its pool filter may read hidden values (the B7 filter
  test), or `PieceAccess.PiecesMayHide` holds. Static (pack and module), so host and guest agree. A masked formula follows B7's one rule
  (everything before Apply from the shown run).
- **Determinism**: replay re-executes the run's commands (no draw on replay); undo takes the whole run back in one step (B3 "one run, one
  undo step"); a menu batch row draws on the preview chain's state with its own pins, and its live Apply uses those pins.
- **Sealed entries** (B7): a sealed entry's formula may not draw (validator), as it may not roll dice.

### Validator (formula messages prefixed `Formula "{f}": `, holder messages `Holder "{name}": `)
Owner expression:
- error `Holder "{name}": owner expression {problem}` (the `ParseProblem` tail)
- error `Holder "{name}": owner expression reads "{p}", which is not Public; every player must be able to tell who owns the holder`
- error `Holder "{name}": owner expression reads unknown property "{p}"`
- error `Holder "{name}": owner expression cannot use {fn}()` (each forbidden function and aggregate once)
- warning `Holder "{name}": owner expression gives "{v}", which names no side` (a constant that folds, `ConstantContext`)
- warning `Holder "{name}": the pack has no sides, so the owner expression is not used`
Holder choices:
- error `variable "{v}" has holder choices but is not a Holder binding`
- error `variable "{v}" offers unknown holder "{id}"`
- error `variable "{v}" offers the global holder; use a Global binding`
- error `variable "{v}": holder "{name}" does not keep property "{p}"` (each listed holder that does not keep the binding's property)
- warning `variable "{v}" has holder choices and a fixed holder; the fixed holder is not used`
- error `output {n} names variable "{v}", which is not a Holder binding with holder choices`
- error `output {n} names a Holder variable but its target is not Holder`
- error `output {n}: holder "{name}" chosen by "{v}" does not keep property "{p}"`
- warning `output {n} names both a holder and a Holder variable; the holder is not used`
Draws:
- error `variable "{v}" draws at random but has no draw count`
- error `variable "{v}" draw count {problem}` (the `ParseProblem` tail), then `FilterRefProblems` tails with the same prefix
- error `variable "{v}" draw count may not read "{v}"`
- error `variable "{v}" has a draw count but is not a Draw binding`
- the Piece binding's property and filter messages apply to a Draw binding's weight and pool (unknown property, Holders scope, keyed,
  `"which pieces" filter does not parse`), with the weight optional
- error `output {n}: {action} cannot act on the counters of draw "{v}"` (Place, Reveal)
- warning `variable "{v}" draws at random but no output acts on the drawn counters`
- error `start entry {n} is sealed, but formula "{f}" draws at random` (prefix `Phase "{name}": `, as B7's sealed texts)
- A Draw variable is accepted wherever B3 accepts a Piece variable on an output (the existing message `output {n} names variable "{v}",
  which is not a Piece binding` is unchanged and not shown for a Draw variable).
`FormulaVariables.Sources` includes `DrawCount` (so the reference, cycle and masking checks see it).

### Runtime messages (exact)
`Choose one of the listed holders`; `No holder to choose`; `The draw count for {Var} must be a whole number from 0 to 1000`;
`{Prop} of {name} must be a whole number from 0 to 1000 to draw from`; on a player view a Draw whose pool reads data the view lacks is
missing `Hidden from this player` (the host decides).

### Workbench (Avalonia Desktop; the WinForms App builds only)
- Visibility tab, holders grid: column `Owner expression` (text, weight 160) after `Owner side`; tooltip `The holder's owner as play goes
  on: a side's name or id, or a number N for the Nth side (0 = no owner). It may read this holder's and the global holder's Public values.
  Blank = the owner side.` Render `VisibilityEdit.SetHolderOwnerExpr(RulesPack, string holderId, string? text)` (blank → null). The tab's
  problem filter also shows messages containing `owner expression`.
- Formulas tab, bindings grid: the Kind combo lists `Draw`; columns appended after `Choices filter` (older columns keep their indexes):
  `Holder choices` (10, text: holder names separated by commas) and `Draw count` (11, text). `Same hex` (7) is enabled for Piece and Draw.
  A Draw binding's Target combo lists `(one each)` and the Pieces/Both properties; its Details is the pool filter. A Holder binding with
  holder choices lists `{Prop} of (chosen holder)` for each property every listed holder keeps. Tooltips: `Holder choices`: `Holder
  binding: the holders the player chooses from when the formula runs, separated by commas; only those the player may act on are offered.
  Blank = the fixed holder.`; `Draw count`: `Draw binding: how many members to draw at random, a whole number from 0 to 1000; it may use
  earlier variables.`; Draw Details hint: `Which counters to draw from (a unit filter; blank = every counter). Target: the property giving
  each counter's number of members (one each when none).`
- Formulas tab, outputs grid: column `Holder variable` (combo: blank + Holder bindings with holder choices) after `Margin`; the `Piece`
  combo also lists Draw variables. Tooltip: `Target Holder: the Holder variable whose chosen holder this output writes.`
- Render `FormulaEdit.WithHolderChoices(RulesPack, VariableBinding, string? names)` (names → ids; an unknown name is kept out and reported
  by the existing warning box text `No holder is named "{n}"`), `WithDrawCount`, `WithHolderVariable`; `WithKind` clears the fields the new
  kind does not use; `WithOutputTarget` clears `HolderVariable` when the target is not Holder.
- Problems list: the new messages (through `WorkbenchRules.Issues`).

### Wiki (invented Riverland examples only)
- Making-a-Rules-Pack: `## Holders at run time and random draws` with `### Dumps that change hands` (Global Public property `DumpOwner`
  keyed by table `Dumps` (rows `Ford Dump`, `Mill Dump`); holder `Ford Dump` with Owner expression `keyed(DumpOwner, "Ford Dump")`: 1 =
  Northern League, 2 = Southern Compact; formula `Take dump` writes `DumpOwner` at key `"Ford Dump"`; after it the Compact sees the dump's
  Private `Stores` and the League no longer does), `### Choosing a holder when the formula runs` (`Draw stores`: Holder binding `Dump`
  over `Ford Dump`, `Mill Dump`, `North Depot`, output Holder variable `Dump` Subtract `Stores`; each player is offered only the dumps
  their side holds now), `### Random draws among counters` (`Shore battery`: Draw binding `Hits`, weight `Boats`, same hex, filter
  `side() == "Southern Compact"`, draw count `Shots`; output Piece `Hits` Subtract `Boats` by `Hits`; the log reads `Shore battery: Hits: 2 on SC 3 Barge` for the Compact), `### What the Problems
  list says` (every new validator text), and the author cautions (an owner expression reads only Public values; k shows when the pool held
  fewer members than the count).
- Formula-Reference: the Draw binding and its count/weight; holder choices; `HolderVariable`; the runtime messages in the error table.
- Window-Rules-Workbench: the new columns. Window-Formula-Dialog: the holder dropdown and the Draw box. Playing-With-Others: dumps that
  change hands (what each side sees after a capture), draw lines.

## Public API changes
All additive: `HolderDef.OwnerExpr`; `BindingKind.Draw` (appended); `VariableBinding.HolderChoices`, `DrawCount`;
`FormulaOutput.HolderVariable`; `HolderOwners`; `Ownership.SideOfHolder(string, RulesPack, GameState?)`, `Ownership.HolderSides`;
`HiddenValues.HolderValueHidden(GameState?, RulesPack, string, PropertyDef?, Func<string, bool>)`; `HolderChoices`, `HolderChoice`;
`FormulaTrace.DrawRolls`, `FormulaTrace.Draws`, `DrawPreview`, `OutputPreview.Draw`; `FormulaMasking.DrawInput`;
`VisibilityEdit.SetHolderOwnerExpr`; `FormulaEdit.WithHolderChoices/WithDrawCount/WithHolderVariable`. No signature changes.
Behavioural: none for packs without the new fields. A downstream `switch` over `BindingKind` with a `_` arm silently maps `Draw`; worth a
grep downstream.

## Non-goals
Holders created or deleted during play (a pack declares every holder; one that is "nobody's yet" has an owner expression giving 0); an
owner expression reading Private values, pieces or aggregates; a "now belongs to" log line; a holder picker offering holders the chooser
may not act on; picking a holder on the map; draws typed as physical dice; draws shared by a group roll; a batch command that applies a
draw's results in one go (each drawn counter gets the outputs instead); draws from holders or keyed values; showing the split before
Apply; the WinForms App's new editors.

## Anti-goals
A player seeing a Private value of a holder they do not own (now), or the split of a draw on counters of another side; an owner that
differs between the full state and a view; a draw re-drawn by previewing again, by replay or by undo/redo; a run that undoes in more than
one step; old packs or saves changing bytes; a pack without the features paying an evaluation.

## Approaches
Picked: a derived owner from Public data through one `Ownership` entry point with the state (no stored owner, no command, undo for free);
a holder picker as a Holder binding with a candidate list judged on the chooser's held set (the Piece-binding pattern); draws as one die
per step through the run's existing roll path, with per-piece outputs reusing B3 Piece writes and stamping.
Rejected: a stored owner map in `GameState` changed by a new `SetHolderOwner` command (a save/command member, a Workbench action and a
new output action, for what a Public value already expresses); an owner expression over any value with the owner stamped into each view
(circular with the visibility rule it feeds, and a guest could not check it); a "draw" output action producing one batch command (a new
save shape; B3 outputs already act per counter); drawing on the chooser's view (the losses must fall on members the chooser cannot see);
logging the draw dice (they would give the split away to the acting side).

## Left to the builder
Test layout; the shared invented fixture (sides `north` Northern League / `south` Southern Compact; holders `Ford Dump`, `Mill Dump`,
`North Depot`, `South Depot`; Public `DumpOwner` keyed by table `Dumps`; Private `Stores`; barges `SC 1 Barge`..`SC 4 Barge` with Private
`Boats`; a hex board); exact private helper names; column widths; wiki prose around the exact strings.
