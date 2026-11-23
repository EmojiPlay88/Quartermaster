# Dice charts — design (short)

Date: 2026-11-23. Branch `dice-charts` (from master `835e070`). Engine wishlist #28, #50. Approved by the user
("approve").

## Intent

Some dice charts print, in each row, a range per column that differs from row to row; `lookup` matches only row
and column keys, so packs expand such charts into 36-column d66 grids. Some charts are rolled once per plane of a
squadron (a dozen or more dice), but a Roll binding draws a fixed number of dice and its value is only their sum, so
the player runs the formula once per plane.

## Design (decided)

**`lookuprange(Table, row, value)` (#28).** 2-D tables only. Finds the row as `lookup` does, then parses each cell
of that row with the existing table key parser (`TableLookup.ParseKey`: `2-4`, `5+`, `3-`, single numbers, text) and
returns the **column key** (as a value: number when the key is numeric, else text) of the first cell, in column
order, that `TableLookup.Matches` the value. Errors: no cell matches → `No column of {table} row {row} holds {value}`;
a table that is not 2-D → `lookuprange needs a two-way table`. Available wherever `lookup` is (formulas, filters,
warnings, phase conditions), through a new `IEvalContext.LookupRange` that every context with `Lookup` implements
with one shared `TableLookup` helper.

**Dice count from an expression (#50).** `VariableBinding.DiceCountExpr : string?`. When set it replaces
`DiceCount` for that Roll binding: evaluated each run (may read inputs and other bindings, not the binding itself),
must give a whole number 1–100 (`dice()`'s limits and wording). A pinned roll whose count differs is ignored (existing
rule).

**Counting dice (#50).** `count(R, lo, hi)`: how many of Roll binding `R`'s dice are ≥ lo and ≤ hi. `R` keeps its
value (the sum), so one roll can feed several counts. `R` must name a Roll binding (validator error
`Formula "{name}": count() needs a roll binding, not {variable}`); at run time through a new `IEvalContext.DiceOf`
(only the formula runner implements it; elsewhere `count() is not available here`). A skipped Roll binding (its
"Only when" is false) gives the existing skipped-read error.

**Validator.** `lookuprange` arguments checked like `lookup` (table exists, table name). `DiceCountExpr` parses and
its variables are bound; it may not read the binding itself.

**Workbench (Avalonia).** Formulas tab bindings grid: a `Dice count` expression column/field for Roll bindings
(blank → null, the fixed number is used). Wiki: Formula reference (both functions), Making a Rules Pack (a
crash-landing style example with invented names).

**Compatibility.** `DiceCountExpr` omitted when null; an unchanged pack keeps its SHA-256; `CurrentFormat` stays 4.

## Non-goals
One roll shared by several pieces (#36); the list of dice as a value; ranges in row keys found by cell contents.

## Anti-goals
An old pack's results changing; dice counts outside 1–100; `count` silently reading a non-roll variable.

## Approaches
Picked: `count(R, lo, hi)` over a Roll binding's dice. Rejected: a per-binding "sum / count in range" mode — only one
count per roll, while charts such as crash landing need two from the same dice.

## Left to the builder
Exact Workbench layout; how many dice the dialog shows per line; test layout.
