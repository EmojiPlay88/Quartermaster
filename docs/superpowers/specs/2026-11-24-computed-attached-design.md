# Computed properties and formation aggregates — design (short)

Date: 2026-11-24. Branch `computed-attached` (from master `57d9527`). Engine wishlist #30, #42. Shape approved by the
user ("ok go").

## Intent

**#30.** Some values are worked out from other properties (CNA's current cohesion = stored cohesion minus the CP used
over the CPA this stage). Today a pack pastes the expression into every warning and formula, and the value cannot be
shown in the Ledger or named in a filter. A property that holds an expression removes the copies.

**#42.** Formulas and warnings cannot see which units are attached to an HQ (they sit on its organisation chart), so a
formation's lowest CPA, whether it is a shell, its ZOC size and its stacking go to checklists. In the CNA module every
unit and HQ has a `Return to Organisation Chart` SendTo trait naming its formation's map, and the engine already marks
those maps as org displays (`MapDef.IsOrgDisplay`), so the formation can be found without new pack data.

## Design (decided)

### Computed properties (#30)
- `PropertyDef.Expr` (string, optional). When set, the property is **computed**: its value is the expression,
  evaluated when read; it is never stored. Empty/null = an ordinary stored property (old packs unchanged: hash, results).
- Same scopes as stored properties. A Units-scope expression is evaluated for the piece (as a unit filter's numeric
  expression reads that piece's properties, globals and functions, including the piece sums and `attached*` below). A
  Holders-scope expression is evaluated for the holder (holder properties and globals, as a holder binding reads them).
- Read everywhere a stored value is read through the rules: formula bindings, inline reads in formulas, unit filters,
  warnings, alerts, phase conditions, piece sums, Ledger, Outliner, breakdown tooltips. One shared Core reader replaces
  the direct `Ledger.GetOrDefault` calls at those sites.
- Read-only: Ledger cell shows the value with tooltip `= {expr}` and refuses edits; formula outputs, Set actions and
  seeding (`Default`, `SheetField`) do not apply to it. Validator errors: an output or action that writes a computed
  property; `Expr` that does not parse; a computed property that reads itself through other computed properties
  (`Property "{name}" is computed from itself: A → B → A`). Default/SheetField on a computed property: validator warning,
  ignored.
- Errors at read time (division by zero, a missing hex, a cycle a broken pack slipped through, depth > 16): the read
  is an error with the property's name (`{name}: {message}`), and the Ledger shows `error` with the message as tooltip.
  A filter or warning treats it as it treats any other evaluation error today.
- Hidden information: a computed property counts as **Private** for masking and PlayerView redaction when its
  expression reads a Private property, `holder()`, or any piece sum / `attached*` call, whatever its declared
  Visibility. Validator warning when it is declared Public in that case
  (`Property "{name}" is Public but is computed from hidden data; players who do not hold every side see it as Private`).

### Formation aggregates (#42)
- `attachedsum(Property[, "filter"])`, `attachedcount(["filter"])`, `attachedmax(Property[, "filter"])`,
  `attachedmin(Property[, "filter"])`: the piece-sum family (same argument rules, same filter syntax and caching, same
  empty results: sum/count 0, max/min error `No piece matches "{filter}"`).
- The pieces: every piece whose map is **the piece's formation display** = the map named by the first SendTo trait of
  the piece being evaluated whose destination map exists and is an org display. Only pieces physically on that display
  count (a unit of the formation that sits on the main map is not attached; user-facing wording: "on its organisation
  chart"). The piece itself counts if it is on that display (a filter can exclude it).
- No such trait: error `{piece} has no organisation chart`. Only where a piece is evaluated (as the hex ones);
  elsewhere `{fn}() is not available here`. Forbidden in phase conditions (as the hex ones).
- Hidden information: exactly as the hex aggregates (`AggregateCallHidden`; a count always hidden; any when pieces may
  hide).

### Workbench and docs
- Properties tab: an "Expression" column; a property with an expression shows Default/Sheet field greyed.
- Wiki: Formula Reference (attached functions, computed properties, errors table), Rules Workbench properties page.

## Non-goals
Writable computed values; per-piece override of table cells (#65); reading another piece's property (#45); formation
membership of pieces not on the chart; SendTo destinations other than the map (board/grid of the chart are ignored).

## Anti-goals
Old packs slowing down or changing results; a Public computed property leaking a Private value; computed values that
silently read as 0 on error; Ledger edits that look accepted but are discarded.

## Approaches
Picked: expression on `PropertyDef`, read through one shared reader; formation = org display named by the piece's
own SendTo trait. Rejected: storing computed values and refreshing them on every change (stale values, extra commands
in the log, sync work online); a pack-level "formation map" setting per piece type (data the module already has);
counting formation members anywhere on the board by name matching (fragile, no module data for it).

## Left to the builder
Where the shared reader lives and how it is cached per state version; how Expr is added to the pack format (follow how
`Precondition` was added in #61); Workbench column layout; test layout; wiki examples.
