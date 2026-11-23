# Piece sums — design (short)

Date: 2026-11-23. Branch `piece-sums` (from master `9f541e5`). Engine wishlist #17, #67. Design approved by the user;
the user picked "everything" (aggregates see all pieces and values, hidden or not).

## Intent

Many rules turn on other counters: a side's total trucks, all supplies in a hex, an engineer battalion stacked with
the unit, the fortification level of the defending hex. A formula reads only the piece it runs on, so packs make the
player type these facts (0/1 inputs, levels read off a marker's Ledger row) and checklists say what to look for.

## Design (decided)

**Whole-game aggregates.** `sum(Property, "filter")`, `countof("filter")`, `maxof(Property, "filter")`,
`minof(Property, "filter")`.
- `Property` is a bare property name (as `has(...)` takes one); `"filter"` is a text literal in unit-filter syntax,
  compiled with `PieceFilter.Compile` (cached per filter text per evaluation context). An empty filter `""` means
  every piece.
- A piece's value is its Ledger value of the property (default when unset), as a Unit binding reads it.
- `maxof`/`minof` over no pieces → error `No piece matches "{filter}"`; `sum` → 0; `countof` → 0.

**This piece's hex.** `hexsum(Property[, "filter"])`, `hexcount(["filter"])`, `hexmax(Property[, "filter"])`,
`hexmin(Property[, "filter"])`.
- Same hex = same map and same grid location name (`PieceInfo.Location`), including the piece itself (a filter can
  exclude it, e.g. `"name() != ..."`). A piece with no grid location: error `{piece} is not in a hex`.
- Only where there is a piece being evaluated (formula run on a unit, unit filters, piece warnings/alerts);
  elsewhere `hexsum() is not available here` (same for the other three).

**Where.** Whole-game aggregates work in formulas, filters, warnings, alerts and phase conditions; hex aggregates where
a piece is being evaluated. Through new `IEvalContext` default members (`Aggregate(...)`, throwing
`{fn}() is not available here`), implemented with one shared Core helper.

**Hidden information ("everything", user's pick).** Aggregates read every piece and value the rules need, hidden or
not. On a viewer who does not hold every side, a formula using an aggregate over a non-public property, or any count,
is treated as reading hidden data: the guest's dialog is masked and the host decides (as `holder()` of hidden data),
and PlayerView redacts its working line for that viewer.

**Validator.** Function argument checks: property exists (`Formula "{name}": sum() names unknown property "{p}"`, same
shape for the others), filter is a text literal that parses (`... filter of sum() does not parse: {msg}`).

**Performance.** A filter that itself uses an aggregate is evaluated per piece (quadratic). Compiled filters are cached;
the existing perf budgets must hold for packs that do not use aggregates.

**Compatibility.** No model change: new functions only. Old packs unchanged (hash, results).

## Non-goals
Pieces attached to a formation (#42); adjacency/range (#9); values per hex (#10); a piece's activity record (#70).

## Anti-goals
Old packs slowing down or changing results; a guest reading another side's hidden values through an aggregate's
working line; aggregates silently skipping pieces.

## Approaches
Picked: functions with a text filter, reusing the unit-filter compiler. Rejected: new binding kinds per aggregate (more
Workbench surface, no gain); visible-only aggregates (user chose rules-correct results).

## Left to the builder
Exact cache placement; test layout; wiki examples.
