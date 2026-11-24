# A piece's own value for a table rating — design (short)

Date: 2026-11-25. Branch `cell-override` (from master `357042d`). Engine wishlist #65. The user delegated the pick
and prefers the most efficient implementation; the choices below are the cheapest that work.

## Intent

Some rules change a printed rating for one counter only (an upgraded unit's anti-armor or offensive rating). Formulas
read ratings from unit tables by the piece's name, so today every formula that reads such a column needs an extra
piece property and an `if` around the table value, and a new formula that forgets it reads the printed rating
silently. A property that *overrides* a table column for the piece that holds it removes the `if`s.

## Design (decided)

- **`PropertyDef.OverridesTable`** (table id) and **`PropertyDef.OverridesColumn`** (column key; null/empty for a 1-D
  table). Both optional; omitted from JSON when empty (old packs keep bytes and SHA-256; `CurrentFormat` stays 4).
- **When it applies.** A table read (Table binding, inline `lookup()`) with a piece being evaluated (formula run on a
  unit, unit filter, piece warning/alert) where: the table is `OverridesTable`; the located column is
  `OverridesColumn` (1-D: any); the located row key equals the piece's own name (`name()`, compared as table keys are
  compared); and the piece **has** a value for the property (`has(P)`: a stored Ledger value, or a computed property
  that applies). Then the read returns the property's value instead of the cell. Otherwise the cell, as today.
  `lookuprange()` is unchanged (it searches values, not one cell).
- **Several properties overriding the same cell:** the first in pack order with a value wins; validator warning.
- **Working line / source.** A Table binding's description becomes `{table}, row {r}, column {c}: {property} of
  {piece} (printed {cell})` when overridden; inline lookups show the value as usual.
- **Hidden information (cheapest safe rule).** A Table binding or inline lookup of a table that a non-Public property
  overrides counts as a hidden read for a player who does not hold every side (same path as hidden Unit inputs) —
  only when the formula runs on a piece of a side that player does not hold, like a Private Unit binding.
- **Workbench.** Properties tab: "Overrides table" (table picker) and "column" (column picker, disabled for 1-D).
- **Validator.** Table exists (`Property "{name}" overrides a table that no longer exists`); column exists for 2-D
  (`Property "{name}" overrides column "{c}", which table "{t}" does not have`); property must be Pieces/Both
  scope (`Property "{name}" overrides a table but is not a piece property`); duplicate overrides warning
  (`Properties "{a}" and "{b}" both override {t}, column {c}; "{a}" wins`).
- **Ledger.** Unchanged display; the breakdown tooltip of the property adds `overrides {table}, column {c}`.

## Non-goals
Overriding by any key other than the piece's own name; overriding a whole row; per-piece overrides of `lookuprange`;
UI showing the printed value next to every Ledger cell.

## Anti-goals
Old packs changing; a table read silently using an override for the wrong piece (e.g. a lookup by another piece's
name while evaluating this one must use the cell); an override leaking a Private value through a formula's working.

## Approaches
Picked: two fields on `PropertyDef`, applied at the single table-read choke point in piece contexts. Rejected: a
separate "overrides" list on `TableDef` (two places to edit, harder to see from the property); computed properties
with `if` (what packs do today; the problem this solves).

## Left to the builder
Where the choke point lives (one helper used by `RunContext`, `FilterContext` and the Table binding); test layout;
wiki example (invented: Riverland raft with an upgraded "Speed").
