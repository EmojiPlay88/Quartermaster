# Authoring and tooling (batch B1) — design (short)

Date: 2026-11-25. Branch `b1-authoring` (from master `ace76fc`). Engine wishlist #11, #75, #12, #64, #15, #20. The user
accepted every default of the batch (one question, "Accept all defaults"); the choices below are those defaults, made
concrete.

## Intent

Big packs are written and tested outside the engine. Six small gaps make that harder than it needs to be:
- only tables, formulas and phases can cite the rulebook (#11);
- checklist lines are bare strings, so packs pin and cross-reference them by position and wording (#75);
- a value that is a code (weather 3) shows as a bare number (#12);
- a holder property shows on every holder, even when one holder keeps it (#64);
- outside test suites match log wording to find failed runs and call internals directly (#15);
- the Clone command seeds the copy from the typed sheet, not the counter's current values (#20).

## Design (decided)

Every new pack field is optional and omitted from JSON when empty. Blank text becomes null on load. Old packs keep their
bytes and SHA-256, and `CurrentFormat` stays 4. Every new expression or reference is checked by the validator. Every new
field gets a Workbench editor and a wiki section with invented examples (the Riverland demo).

### #11 Rule references on more elements
- `RuleRefId : string?` on `PropertyDef`, `HolderDef`, `AlertDef`, `PhaseWarning` and `ChecklistItem` (below).
- **Workbench.** A rule-ref picker: "(none)" plus every pack rule ref as `{label}, page {page}`. It appears as a "Rule ref"
  column in the property, holder and phase-warning grids, and as a "Rule ref:" box on the Alerts tab and in the checklist
  item details. The picker only chooses existing refs. Making refs stays with "Link to current page" on Tables, Formulas
  and Phases.
- **Validator.** A dangling id is a **warning** `{who}: rule reference "{id}" does not exist`. `{who}` is
  `Property "{name}"`, `Holder "{name}"`, `Alert "{name}"`, `Phase "{name}": warning {n}` or
  `Phase "{name}": checklist item {n}` (n is 1-based). The "file on this computer" check of rule refs also lists these
  users.

### #75 Checklist items with stable ids
- `PhaseDef.Checklist : List<ChecklistItem>`. `ChecklistItem { Id?, Text, RuleRefId?, Notes? }`. A JSON converter reads a
  bare string as a text-only item and writes a text-only item back as a string, so old packs keep their bytes. An item
  with any other field is written as an object without its null fields. A string converts implicitly to a text-only
  item, so `Checklist = new() { "Check fuel" }` still compiles.
- **Ids** use letters, digits, `-`, `_` and `.`, and are unique across the pack.
- **Links.** Item text may contain `{item:<id>}` and `{formula:<id>}`. When shown (phase panel, decisions, Workbench
  list), `{item:x}` becomes the target item's text in double quotes, cut to 40 characters with `…`. `{formula:x}`
  becomes the formula's name. A token whose target does not exist is shown unchanged.
- **Ticking.** `TickChecklist` gains `ItemId` (omitted when null). An item with an id is ticked by id
  (`GameState.TickedIds`, omitted when null, cleared on phase change like `Ticked`). An item without an id is ticked by
  index, as today. So reordering items in a game keeps id-ticks. Online, intents stay index-based: the host maps the
  index to the item.
- **Validator.**
  - Error: `Phase "{p}": checklist item {n} has an unusable id "{id}"; use letters, digits, '-', '_' or '.'`.
  - Error: `Phase "{p}": checklist item id "{id}" is used more than once`. Reported on every use after the first, in
    pack order.
  - Warning: `Phase "{p}": checklist item {n} names item "{id}", which does not exist`.
  - Warning: `Phase "{p}": checklist item {n} names formula "{id}", which does not exist`.
- **Workbench.** The checklist list shows `{id}: {shown text}` (or just the shown text). Below the list are details for the
  selected item: "Id:", "Rule ref:", "Notes:" and a "Links:" line. The "Links:" line lists each token's target, or
  `{token} (missing)`.

### #12 Labels for numeric values
- `PropertyDef.Labels : string?` is the id of a one-column table (OneD or Dice). The row matched by the value (the same
  key matching as `lookup`) gives the label text. Values stay numbers everywhere: storage, formulas, CSV.
- **Shown as** `{number} ({label})`, for example `3 (Fog)`. It applies in Ledger cells, Outliner values, counter popup
  ledger lines, the breakdown title and formula working. Working covers a variable's value and the `{prop} = {after}`
  part of a Set output. Add and Subtract outputs stay bare numbers. When no row matches, or the cell is blank, only the
  number is shown.
- **Validator.**
  - Error: `Property "{name}" takes its labels from a table that no longer exists`.
  - Error: `Property "{name}" takes its labels from table "{t}", which has more than one column`.
- **Hidden information.** A label is only shown next to a value the viewer can already see. Labels are computed from the
  player's view (`PlayerView.For`) and from working that `PlayerView` already redacts (`HidesInput`, `HiddenWorkingText`).
  No new path shows a value.

### #64 Holder properties kept by some holders
- `PropertyDef.Holders : List<string>?` holds holder ids (`global` allowed). Null means every holder. An empty list
  becomes null on load. It does not affect pieces.
- **Ledger.** A holder that does not keep the property shows a blank, read-only cell. Editing it is refused with
  `{holder} does not keep {property}`. On the "Depots & Global" tab and on user tabs, a holder-only column is dropped
  when none of the rows shown keeps it.
- **Runtime.** A Holder or Global binding read, `holder()` call or holder or global output on a holder that does not keep
  the property fails the run with `{holder} does not keep {property}`.
- **Validator.**
  - Error: `Property "{name}" names holder "{id}", which does not exist`.
  - Error: `Property "{name}" lists holders but is not a holder property`.
  - Error, binding: `{variable} reads "{p}", which holder "{h}" does not keep`.
  - Error, output: `output {n} writes "{p}", which holder "{h}" does not keep`.
  - Error, `holder()` call: `refers to "{p}", which holder "{h}" does not keep`.
- **Workbench.** The properties grid gets a "Holders" column: comma-separated holder names, blank for all. An unknown name
  is refused with `No holder named "{name}"`. The Formulas tab offers only kept properties for the chosen holder
  (Holder binding and Holder output pickers).

### #15 Failure flag and a stable test harness
- `LogEntry.Error : bool` and `AddLogLine.Error`. Both are omitted when false, so old saves keep their bytes. The flag is
  set on the lines the engine already writes when a triggered run fails or is refused (`{formula}: {unit}: {error}`),
  when a formula is missing (`… not found — skipped`), and when a filter is invalid (`… is not valid — skipped`).
- **Hidden information.** When `PlayerView` redacts a line (`Outcome.Redact`), it clears `Error`. When it drops a line, the
  flag goes with it. A kept line keeps the flag; the viewer already holds that line's owner.
- **`PackHarness`** (Render, stable and documented): `Open(vmod, rules, cacheDir?)`, `NewGame(setup?, difficulty)`,
  `Run(formulaId, pieceName?, inputs?, dice?)`, `Advance()`, `Value(name, property)`, `Log`, `Phase`, `Pending`,
  `ViewFor(sides)`, `Dispose()`. It wraps `ModuleSession`, `FormulaRunner` and `PhaseController`, and plays every side.
  `Log` is the full host log. `ViewFor` returns `PlayerView.For`, so pack tests can check what a side sees. A Docs test
  pins its public surface.

### #20 Clone copies ledger values
- `AddPiece.CloneOf : string?` is the source piece id, omitted when null. The VASSAL Clone action sets it.
- `PropertyDef.CloneAs : CloneValue { Copy, Default }` is omitted when Copy. After the clone is added, its piece
  properties with Copy get the source's **stored** values, as a `SeedLedger` that runs before the normal seeding.
  Properties with Default get what a new counter gets (sheet seed or Default). Computed properties are never stored, so
  they are untouched.
- **Hidden information.**
  - When the clone's side (worked out with the copied values) differs from the source's side, nothing is copied. A
    diagnostic warning `Clone of "{name}" belongs to another side, so its values were not copied` is added.
  - A guest's `AddPiece` with `CloneOf` is refused unless the guest may act on the source and the type string equals the
    source's. The refusal uses existing texts (`Actable`'s text, or `That piece can't be placed.`). So a guest cannot
    copy another side's private values into its own counter.
  - Copied values are the clone's own ledger, filtered by `PlayerView` like any piece's.

## Non-goals
- Text-valued properties.
- Rule refs shown in play for the new elements.
- Making new rule refs from the property, alert or checklist editors.
- Holder pickers at run time (#13).
- A separate `Quartermaster.Testing` assembly or NuGet package.
- Cloning holders.
- Copying ledger history to the clone.

## Anti-goals
- Old packs or saves changing bytes.
- A label, failure flag or cloned value revealing another side's hidden value.
- Tick state silently moving to another item when items are reordered.

## Approaches
Picked: optional fields on the existing records, with one converter for checklist strings and the harness as a thin
facade in Render.

Rejected:
- `List<object>` or a parallel `ChecklistIds` list for #75 (two lists to keep in step).
- A text-valued property type for #12 (touches every evaluator path).
- Per-holder property lists on `HolderDef` for #64 (the wishlist asked for the property side, and the Ledger reads
  properties).
- Copying values in the `AddPiece` reducer for #20 (the reducer has no pack, and a recorded `SeedLedger` replays
  deterministically).

## Left to the builder
- Test layout.
- Where `ChecklistText.Render` caches the item index.
- The exact harness scratch-folder handling.
- The checklist detail layout.
- Wiki examples (invented Riverland):
  - a "Weather" global with a "Weather names" table (1 Clear, 2 Rain, 3 Fog);
  - "Ferry tolls" kept by the North depot only;
  - a "Spare boats" checklist item linked from another item.
