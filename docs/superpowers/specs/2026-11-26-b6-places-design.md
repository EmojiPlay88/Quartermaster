# Places, keyed values and geometry (batch B6) — design (short)

Date: 2026-11-26. Branch `b6-places` (from master `7993778`). Engine wishlist #9, #63, #60, #10/#59, #48 (part), plus the minor
carried from B3 ("Remove ToMapId resolved differently by validator vs runtime"). The user accepted every default of the batch; the
choices below are those defaults, made concrete. Details the defaults left open are marked "decided". Builds on the B2 phase entry
(`PhaseController.EnterPhase`), the B3 piece outputs and the existing Ledger window, map status bar and hex distance tool.

B5 (turn structure) is developed in parallel on master's same base and merges before or after B6. B6 stays out of the phase-model
files (`PhaseDefs.cs`, `Sequencer.cs`, `Phases.cs`); where both batches add to shared files, B6 adds self-contained blocks (see
"Parallel batch" at the end).

## Intent

Big packs work around four gaps:
- a formula cannot ask "how far is this counter from that hex" or "which hex is it in" except through a Location binding (#9);
- one property holds one number per counter or holder, so "fuel per type", "barges due per turn" or "a count per row of a table" is
  one property per key (#63, #48 part);
- values that belong to a future turn (arrivals, delays) have to be cleared by hand once that turn is past (#60);
- a hex has no state of its own: a blown bridge, a flooded ford or a minefield is a marker counter or a global per hex (#10, #59).

## Design (decided)

Every new pack field is optional and omitted from JSON when empty or default; blank text becomes null on load
(`RulesPackIo.Normalise`). Old packs keep their bytes and SHA-256, `CurrentFormat` stays 4. Old saves keep their bytes: every new
save member is omitted when null. Every new expression is parsed and checked by the validator. Every new field gets a Workbench
editor and a wiki section with invented Riverland examples (no content of any published game). Public API changes are additive
(new members on positional records are init properties in the record body; new interface members are default methods).

### Pack model (Rules)
- `PropertyDef.KeyedBy : string?` (`WhenWritingNull`). Three forms, matched case-insensitively after trimming:
  - `"table:<table id>"`: one value per row key of that table (#63, #48 part);
  - `"turn"`: one value per turn number; keys below the current turn are dropped when a new turn's phase is entered (#60);
  - `"hex"`: one value per hex name, kept by the Global holder only: **place values** (#10/#59).
  `RulesPackIo.Normalise` trims it and turns blank into null (case kept, so a loaded pack saves as written).
- `FormulaOutput.Key : string?` (`WhenWritingNull`, blank → null in `Normalise`): expression giving the key a Write output writes.
- New Rules static `KeyedFacts` (`src/Quartermaster.Rules/Validation/KeyedFacts.cs`): `enum KeyKind { None, Table, Turn, Hex }`;
  `Parse(string? keyedBy) : (KeyKind Kind, string? TableId)` (unknown form → `(None, null)` and `IsMalformed` true);
  `IsKeyed(PropertyDef?)` (fast null check first, so packs without the feature pay nothing); `IsPlace(PropertyDef?)` (Hex);
  `Display(RulesPack, string? keyedBy)` (`Turn`, `Hex (place)`, `Table: {name}`, blank) and `FromDisplay(RulesPack, string text)`.
- New Rules static `MapRefs` (`src/Quartermaster.Rules/Validation/MapRefs.cs`, uses Vassal `MapDef`):
  `Find(IReadOnlyList<MapDef> maps, string? idOrName) : MapDef?` — trimmed; exact id (ordinal) first, then exact name (ordinal);
  blank → null. The **one** resolver of a Remove output's `ToMapId` (carried minor, below).

JSON (invented):
```json
{ "id": "p-ford", "name": "Ford depth", "scope": "Holders", "keyedBy": "hex", "labels": "t-ford-states", "visibility": "Public", ... }
{ "id": "p-due", "name": "Barges due", "scope": "Holders", "keyedBy": "turn", ... }
{ "id": "p-hulls", "name": "Hull count", "scope": "Holders", "keyedBy": "table:t-hulls", ... }
{ "target": "Global", "property": "p-ford", "op": "Set", "expr": "3", "key": "hex()" }
```

### New functions (Rules language)
Parser arity (in one block after `"attachedmin"`): `["hex"] = (0, 0)`, `["distance"] = (2, 2)`, `["keyed"] = (2, 3)`; the three names
are appended to `Evaluator.FunctionNames` (after `"attachedmin"`) and dispatched in `EvalCall` (one block before `default:`).
`IEvalContext` gains default methods:
- `Value Hex() => throw new EvalError("hex() is not available here")`;
- `double Distance(string a, string b) => throw new EvalError("distance() is not available here")`;
- `double Keyed(string property, Value key, string? holder) => throw new EvalError("keyed() is not available here")`.

**`hex()`** (#9): the grid location name of the piece being evaluated, as text (the same name a Location binding and `hexsum()` use,
`PieceInfo.Location`). No piece: `hex() needs a unit`. Off grid: `{piece} is not in a hex`. No module model in the context:
the default error.

**`distance(a, b)`** (#9): cells between two hex names, as a number. Both arguments must be text (`distance() needs hex names as
text`; a hex name like `0412` would lose its zero as a number). Names are resolved per board through a lazy **per-board name → cell
index** (Vassal `BoardCells`, below): the board of the piece being evaluated is tried first, then every board in module order (maps
in `ModuleModel.Maps` order, boards in map order). Errors: `distance(): no hex named "{x}" on any board`; `distance(): "{a}" and "{b}"
are not on the same board`; `distance() needs a hex or square grid`. Square grids count king moves (as the range tool does); hex
grids cube distance; a zoned grid uses the zone grid that holds both points (`ZonedGrid.GridFor`). `distance(hex(), hex())` = 0.

**`keyed(Prop, key)`** / **`keyed(Prop, key, Holder)`** (#63/#60/#59): reads a keyed property's value for one key. `Prop` (and `Holder`)
are bare names, not variables (like `holder()`): `Parser.Variables` skips them, so they need no binding. Whose value:
- with a third argument: that holder (`HolderByName`; must keep the property, else the existing `EnsureKept` text);
- a Pieces property: the piece being evaluated (none: `keyed() needs a unit`);
- a Holders property: the Global holder;
- a Both property: the piece when the context has one, else the Global holder.
The key is normalised (below); a key never written reads the property's **Default** (as an unset ledger value does). Errors:
`keyed() needs a property name`; `Unknown property '{p}'`; `{p} is not keyed`; the key errors below. In a player view a read whose
value the view may lack (a Private place property; a non-Public piece property on a piece of a side the view does not hold; a holder
owned by a side not held) throws `keyed() reads data this player cannot see` with `HiddenData = true`, so a guest's preview is
undecided and the host decides (as `runs()` does).

**Key normalisation** (Core `KeyedValues.NormaliseKey(RulesPack, ModuleModel?, PropertyDef, Value) : string`, also used by outputs,
the Ledger and the host's intent check):
- Table: the row of the table that `TableLookup.Locate(table, key, null)` finds; the stored key is `TableLookup.Normalise(rowKey)`.
  None: `no row "{k}" in table "{t}"`. (The validator rejects a key table with range row keys, so a key is always one row.)
- Turn: a whole number from 1 to 100000; stored as its invariant integer text. Else `the turn must be a whole number of at least 1`.
- Hex: text; trimmed; with a module model that has any gridded board, it must be a hex of some board (`BoardCells.Find`) and is
  stored as that board's own spelling of the name; else `no hex named "{k}" on any board`. Without a model: the trimmed text.
- Keys are at most 64 characters (`the key is too long`).
In `keyed()` the error texts are prefixed `keyed(): `; in outputs `The key for {property}: `.

### Geometry (Vassal, Render)
- New Vassal static `BoardCells` (`src/Quartermaster.Vassal/Grids/BoardCells.cs`):
  - `Find(ModuleModel model, string name, BoardDef? prefer) : (MapDef Map, BoardDef Board, PointD Local)?` — `prefer` first, then
    module order. A board knows a name when `Grid.LocationToPoint(name)` gives a point inside the board whose `LocationName` is
    the same name (trimmed, ordinal ignore-case; the board's spelling is returned). Results are cached per board in a
    `ConditionalWeakTable<BoardDef, ConcurrentDictionary<string, PointD?>>` (the "per-board name → cell index"; filled lazily).
  - `Cells(IMapGrid? grid, PointD a, PointD b) : int?` — the algorithm moved from Render `HexDistance.Cells` (square: Chebyshev; hex:
    odd-q cube; zoned: `GridFor`; else null). Render `HexDistance.Cells(...)` keeps both public overloads and delegates to it.
- Core `PieceInfo.BoardOf(PieceInstance, ModuleModel) : BoardDef?` (the board under the piece, as `Location` finds it).

### Keyed values in the game state (Core, saves)
- `GameState.Keyed : OrderedDictionary<string, OrderedDictionary<string, OrderedDictionary<string, double>>>?` (`WhenWritingNull`):
  ledger key (`LedgerKey`) → property id → value key → value. Null when empty (a reducer that empties it sets it back to null).
  `Ledgers` never holds a keyed property's value.
- `LedgerDelta.ValueKey : string?` (body init, `WhenWritingNull`): when set, the reducer writes `Keyed[Key][PropertyId][ValueKey]`
  instead of `Ledgers`. `LedgerEntry.ValueKey : string?` (body init, `WhenWritingNull`) is copied onto the history entry.
  `Ledger.History(s, key, propertyId)` returns only entries without a `ValueKey` (unchanged for old saves); new overload
  `Ledger.History(s, key, propertyId, valueKey)`.
- New Core static `KeyedValues` (`src/Quartermaster.Core/KeyedValues.cs`): `Get(GameState, string ledgerKey, string propertyId,
  string valueKey) : double?`, `GetOrDefault(GameState, string ledgerKey, PropertyDef, string valueKey)`, `Keys(GameState, string
  ledgerKey, string propertyId)`, `NormaliseKey` (above), `PlacesAt(GameState, RulesPack, string hex)` (place properties with a stored
  value there, pack order), `PlaceText(RulesPack, IReadOnlyList<(PropertyDef, double)>)` (`"{Prop} {value}"` joined with ` · `, labels
  via `PropertyLabels.Text`), `DropCommand(GameState, RulesPack, int turn) : DropPastTurns?`.
- New command `DropPastTurns(IReadOnlyList<string> PropertyIds, int Turn)` (`$t` `"dropPastTurns"`): removes every value key of those
  properties (every ledger key) whose numeric key is below `Turn`; logs nothing; ledger history kept.
- Reducers: `RemovePiece` also drops `Keyed[p:{id}]`; `ResetProperties` also deletes the listed properties' keyed values (a keyed
  property may reset at a phase: all its keys go). `SeedLedger` never seeds a keyed property (the validator forbids sheet fields and
  starting values on one). Clone copies no keyed value (decided).
- Manual edits write `Working = "{Prop}[{key}] {before} → {after}"`, so the log line and its pairing in `PlayerView` read well.

### Runner (Core `FormulaRunner`)
- `RunContext`, `PieceFilter.FilterContext`, `PieceFilter.GameContext` and `PhaseConditions.PhaseContext` implement `Keyed` (Phase:
  holder properties only, else `keyed() needs a unit`); `RunContext` and `FilterContext` implement `Hex` and `Distance`
  (`GameContext`: `Hex` → `hex() needs a unit`, `Distance` with the model). Shared code lives in `KeyedValues` / `BoardCells`.
- A Write output whose property is keyed evaluates `o.Key` with `RunContext` after its value, normalises it and previews
  `Ledger value = KeyedValues.GetOrDefault(...)`; `OutputPreview.ValueKey : string?` (body init). The working part names the key:
  `"{Prop}[{key}] {b} + {x} = {a}"`, Set: `"{Prop}[{key}] = {label}"`. `Commands` copies `ValueKey` onto the `LedgerDelta`.
  A keyed output with no key, or a key on an unkeyed property (old pack edited by hand), fails the run with the validator's text.
- A Unit/Global/Holder/Piece binding, or a bare property name in a filter, that reads a keyed property fails:
  `"{p}" is keyed; read it with keyed()`. Aggregates over a keyed property fail: `{fn}() cannot total keyed property "{p}"`.
- A Place-target (`OutputTarget.Placed`) keyed write uses the placed piece's ledger key, Before = Default.

### Turn keys are dropped (#60)
`PhaseController.EnterPhase(phase)` (host only, as `ResetAtEntry`) executes `KeyedValues.DropCommand(state, pack, turn)` right after
the resets and before the start entries, when any turn-keyed property has a stored key below the current turn. One Next Phase is
still one undo step. A turn set by hand (Dice & Turn) drops at the next phase entry (decided: no second trigger).

### Places (#10/#59)
- A place property: `KeyedBy = "hex"`, `Scope = Holders`, kept by Global only. Written by a Global output with `Key` = a hex name,
  usually `hex()` (the run piece's hex) or a constant `"0412"`; read with `keyed(Prop, hex())` or `keyed(Prop, "0412")`.
- **Visibility** (DECISION default "no side, Public unless Private"): a place has no side. A Public place property is visible to
  every player; a Private one only in a full view (solo, a hot-seat player holding every side, the host): `PlayerView` removes its
  values from `Keyed` and its history entries, and its log lines redact as hidden ledger values do. Workbench new properties are
  Private by default, so the wiki says to set Public for a map state players should see.
- **Ledger Places tab.** `LedgerTabs.Tabs(user, pack)` (new overload; the old one unchanged) adds `new("places", "Places",
  LedgerTabKind.Places, null)` after Everything when the pack has a place property. New `LedgerTabKind.Places` (appended). Model:
  Core `KeyedTable.Places(GameState view, RulesPack, string filter) : PlacesResult(IReadOnlyList<PropertyDef> Columns,
  IReadOnlyList<PlaceRow> Rows)`, `PlaceRow(string Hex, IReadOnlyList<double?> Values)`; rows = hexes with any stored place value in
  the view, ordered by name (ordinal ignore-case), filter = hex name contains. Cells edit through `IActionPort.EditKeyed`. An
  `Add place` box adds an empty row for a hex the board has (`No hex named "{x}" on any board.`; with no gridded board any text).
  CSV export as other tabs.
- **Map hover.** `MapStatusText.Location(map, m, Func<string, string?>? placeText)` (new overload): after the location name, two
  spaces, `·`, two spaces and the hex's place text when it has one. Desktop `MapTabs.Location` passes
  `hex => KeyedValues.PlaceText(pack, KeyedValues.PlacesAt(view, pack, hex))` from the **viewer's state**. Desktop `MapView` also sets
  a tooltip `{hex}: {place text}` while the pointer rests (hover delay) on a hex with place values and no stack. The WinForms App
  keeps its old status text (builds only).

### Keyed values of a counter or holder (#63/#60, Ledger "small table")
- `LedgerTable.Columns(pack)` leaves keyed properties out (they have no single value), so the main grid, CSV and user tabs are
  unchanged for packs without them.
- Core `KeyedTable.For(GameState view, RulesPack, string ledgerKey) : IReadOnlyList<KeyedRow>`,
  `KeyedRow(PropertyDef Property, string Key, double Value, bool Stored)`: for each keyed property the row's kind keeps (piece:
  Pieces/Both and applies-to; holder: Holders/Both and `KeptBy`; places are excluded, they have their tab), table-keyed → every row
  key of the table in table order (stored or Default); turn-keyed → stored keys ∪ the current turn, ascending.
- Desktop Ledger window: under the grid, a `Keyed values of {row name}` panel (hidden when the selected row has none) with columns
  `Property`, `Key`, `Value`; Value is editable (note asked as for other manual edits) through `IActionPort.EditKeyed`.

### Online and hot-seat
- `IActionPort.EditKeyed(string key, string propertyId, string valueKey, double after, string note)` — default method does nothing
  (a port written before B6); `LocalActions` (checks as `EditLedger`, plus the key normalised), `RemoteActions` (sends the intent),
  `ActionPortSwitch` implement it.
- New `KeyedEditIntent(string Key, string PropertyId, string ValueKey, double After, string Note)` (`$t` `"keyedEdit"`; a new type,
  so an older host answers `NotUnderstoodText` instead of writing an unkeyed value). Host (`IntentExecutor.EditKeyed`): every check
  of `EditLedger` (ids, finite, note, property exists, computed, kept, piece exists and visible, `CanEditLedgerFor`), plus: the
  property is keyed (else `NotUnderstoodText`), `ValueKey` at most 64 characters and normalises (else `NotUnderstoodText`); a place
  property is written only when the sender may write Global values (`CanEditLedgerFor(h:global, sides)`). Unchanged value: done, no
  history.
- Formula runs with keyed outputs need nothing new: the host runs the formula; `FormulaMasking.OutputVisible` and
  `CanEditLedgerFor(o.Key, sides)` already judge the ledger key.
- `PlayerView`: filters `Keyed` exactly as `Ledgers` (whole ledger key hidden → dropped; per property `ValueHidden`), plus the place
  rule above (`_privatePlaces`: place properties that are not Public). Formulas whose `keyed()` reads may be hidden for the viewer (a
  Private place, a holder argument the viewer may not see) join `_hiddenInputFormulas` (new `FormulaVariables.KeyedReads(f)`), so
  their working is never shown. Hot-seat: the existing view switch re-reads the Places tab and keyed panel.

### #48 (part): counts per type
Docs only: a holder (or piece) property keyed by a type table (`Hull count` keyed by table `Hull types` on holder `Boat yard`)
replaces one property per type. Per-plane state (one record per aircraft) stays deferred.

### Carried minor (B3): Remove ToMapId resolved one way
The runtime (`FormulaRunner`, Remove) and the validator now both use `MapRefs.Find` (trimmed; id, then name). New validator overload
`RulesValidator.Validate(RulesPack pack, IReadOnlyCollection<string>? knownMapIds, IReadOnlyCollection<string>? knownSlots,
ModuleModel? module)`: with a module, the ToMapId check is `MapRefs.Find(module.Maps, o.ToMapId) is null` and hex constants are
checked; without one, the three-argument behaviour stays except that `ToMapId` is trimmed before the id check. `WorkbenchRules.Issues`
gains an overload taking the `ModuleModel`, which the Desktop Workbench uses. The Workbench To map column shows the map
`MapRefs.Find` gives (a name reference no longer shows as missing).

### Validator (formula messages prefixed `Formula "{f}": `, property messages as written)
Property (errors unless marked):
- `Property "{name}" is keyed by "{kb}"; use table:<id>, turn or hex`
- `Property "{name}" is keyed by table "{id}", which does not exist`
- `Property "{name}" is keyed by table "{t}", whose row keys are ranges`
- `Property "{name}" is keyed and computed; remove one`
- `Property "{name}" is keyed, so it cannot have a starting value or a sheet field`
- `Property "{name}" is keyed, so it cannot override a table`
- `Property "{name}" is keyed by hex, so it must be a holder property`
- `Property "{name}" is keyed by hex, so only the Global holder keeps it`
- warning `Property "{name}" is keyed, so the outliner does not total it`
Formula outputs (with `output {n}`):
- `output {n} writes keyed property "{p}" but has no key`
- `output {n} has a key but writes property "{p}", which is not keyed`
- `output {n} has a key but does not write a value` (Action other than Write)
- `output {n} lists keyed property "{p}"; a property list cannot hold keyed properties`
- `output {n} writes place property "{p}" to a holder other than Global`
- `output {n} key {problem}` (parse, via the existing `ParseGuard` with what = `output {n} key`), then constant-key tails
Bindings: `variable "{v}" reads keyed property "{p}"; use keyed() in the formula`.
Expression tails (in `FilterRefProblems` and `CheckLookups`, so every existing prefix applies):
- `keyed() needs a property name`; `refers to unknown property "{p}"` (existing tail); `keyed() reads "{p}", which is not keyed`;
  `refers to unknown holder "{h}"` / `refers to "{p}", which holder "{h}" does not keep` (existing tails, third argument);
- `reads keyed property "{p}"; use keyed()` (a bare name of a keyed property in a filter);
- `{fn}() cannot total keyed property "{p}"` (in `AggregateProblems`);
- `distance() needs hex names as text` (an argument that is a number literal);
- constant keys and hex names (a literal, or an argument that folds with `ConstantContext`): `key "{k}" is not a row of table "{t}"`,
  `key must be a whole turn number of at least 1`, and, only with a module, `key "{k}" is not a hex of any board` and
  `distance() names hex "{x}", which is not on any board`. In `keyed()` these read `keyed() key ...`; in outputs `output {n} key ...`.
Where the functions may not be used (errors, existing patterns):
- `hex` and `distance` join `ForbiddenConditionFunctions` (phase conditions; `keyed` is allowed there for holder properties, a piece
  property gives `condition uses "{p}", which is not a global property`);
- `hex`, `distance` join `PieceBoundAggregatesIn` (so property applies-to, side unit filters and starting values reject them with
  their existing texts: there is no grid while seeding and judging ownership);
- `ExprFacts.IsGameLevel`: `hex()` and a `keyed()` of a piece property need a unit.
The module for hex constants is passed to the private helpers through a `[ThreadStatic] private static ModuleModel? _module` set and
cleared (try/finally) by the four-argument `Validate` (decided: keeps every helper signature unchanged).

### Workbench (Avalonia Desktop; the WinForms App builds only)
- Properties grid: column `Keyed by` (16, combo: blank, `Turn`, `Hex (place)`, `Table: {name}` per table), after `Resets at`.
  Tooltip: `Keep one value per key instead of one value: per row of a table, per turn (keys of past turns are dropped when a new turn
  starts) or per hex (place values, kept by the Global holder). Read a value with keyed(Property, key); an output's Key chooses the
  key it writes.`
- Outputs grid: column `Key` (12, text), after `To map`. Tooltip: `For a keyed property: the key this output writes, e.g. hex() for
  the counter's hex, turn() + 2, or a table row in quotes.`
- Render `PropertyEdit.WithKeyedBy(PropertyDef, string?)` (new static, blank → null) and `FormulaEdit.WithKey(FormulaOutput,
  string?)` (blank → null); `FormulaEdit.WithOutputAction` clears `Key` for an action other than Write.
- **Wiki** (invented Riverland examples only):
  - Formula-Reference: `### hex()`, `### distance(a, b)`, `### keyed(Property, key) or keyed(Property, key, Holder)`, the keyed
    output `Key`, the new runtime messages in the error table (the Docs test pins one section per `FunctionNames` entry);
  - Making-a-Rules-Pack: `## Places and keyed values` with `### Values that belong to a hex` (`Ford depth` keyed by hex, labels
    table `Ford states`, formula `Flood the ford` writing `Key = hex()`), `### Values per turn` (`Barges due`, output
    `Key = turn() + 2`, a start entry reading `keyed(Barges due, turn())`), `### One value per table row` (`Hull count` keyed by
    table `Hull types` on holder `Boat yard`), `### Distances` (`Signal the lighthouse` with precondition
    `distance(hex(), "0507") <= 3`), and the validator texts;
  - Window-Ledger: `### Places tab`, `### Keyed values`; Window-Map: the status bar and tooltip place text;
  - Window-Rules-Workbench: the two new columns.

## Public API changes
All additive: `PropertyDef.KeyedBy`; `FormulaOutput.Key`; `KeyedFacts`, `MapRefs`; `IEvalContext.Hex/Distance/Keyed` (default
methods); `BoardCells`; `PieceInfo.BoardOf`; `GameState.Keyed`; `LedgerDelta.ValueKey`, `LedgerEntry.ValueKey`,
`OutputPreview.ValueKey` (body init); `DropPastTurns`; `KeyedValues`, `KeyedTable`, `KeyedRow`, `PlacesResult`, `PlaceRow`;
`Ledger.History` 4-argument overload; `LedgerTabKind.Places` (appended), `LedgerTabs.Tabs(user, pack)`; `RulesValidator.Validate`
4-argument overload; `WorkbenchRules.Issues` overload; `MapStatusText.Location` overload; `IActionPort.EditKeyed` (default method);
`KeyedEditIntent`; `PropertyEdit`, `FormulaEdit.WithKey`. No signature changes. Behavioural: `LedgerTable.Columns` omits keyed
properties (none exist in older packs); a Remove output whose `ToMapId` has spaces around an id now validates as it runs.

## Non-goals
Per-plane records (#48 beyond counts per type); keys of other kinds (side, phase, piece id); a hex value with a side; region or
zone values; distances in map pixels or along roads; line of sight in formulas; keyed values on the outliner; a place layer drawn on
the map; the WinForms App's Places tab, keyed panel and hover text.

## Anti-goals
Old packs or saves changing bytes; a guest reading a Private place value, another side's keyed piece value or a hidden holder's
keyed value from the state, the log, a working line, the status bar or the tooltip; an unkeyed write landing on a keyed property
(or the reverse) from a mixed-version guest; two resolvers of one map reference.

## Approaches
Picked: one keyed store beside `Ledgers` (`GameState.Keyed`) written by the existing `LedgerDelta` with a `ValueKey`, so undo,
replay, log, history and `PlayerView` pairing keep one command; place values as the Global holder's hex-keyed properties (one
visibility rule, no new owner); hex names as keys with a lazy per-board name index; turn-key cleanup as one command on phase entry.
Rejected: composite property ids in `Ledgers` (every reader of `Ledgers` would misread them, "(deleted property)" rows); a separate
"places" collection with its own commands (a second ledger to keep in step with undo and views); board-qualified keys like
`Board/0412` (`hex()` and Location bindings give bare names; a pack whose boards repeat names is rare and resolves to the run piece's
board first); dropping turn keys in the reducer (it does not know the pack).

## Parallel batch (B5)
B5 adds `segment()`, open-ended blocks, `PhaseDef.Reactors`, `RulesPack.Schedule`, `LastTurn`, `VictoryCondition`. Shared files and
how B6 keeps clear of conflicts:
- `Parser.Arity`, `Evaluator.FunctionNames`, `EvalCall`, `IEvalContext`: B6 adds one commented block (`// B6: places and keyed
  values`) at the end of each list (after `"attachedmin"`, before `default:`, after `Row()`); a conflict is "keep both".
- `RulesValidator`: new checks in new private methods (`CheckKeyed`, `CheckKeyedOutput`, `KeyedProblems`, `DistanceProblems`) called
  from one line each; `ForbiddenConditionFunctions` gains `"hex", "distance"` at its end.
- `RulesPackIo.Normalise`: `KeyedBy` and `Key` are single additions inside the existing property and output `with` blocks.
- `PhaseController`: one call line in `EnterPhase` and one new private method; no edit to block, advance or end-of-game code.
- Wiki: new sections only; the Formula-Reference function list gains rows at its end. `docs/superpowers/RESUME.md`: each batch
  adds its own CURRENT STATE section; the second to merge keeps both.

## Left to the builder
Test layout; the module fixture with a numbered hex grid (two boards, one repeated hex name, invented); where `KeyedTable` lives in
Core; the Places tab control layout and column widths; wiki prose around the exact strings.
