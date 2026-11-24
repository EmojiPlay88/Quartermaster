# Reading another piece's property — design (short)

Date: 2026-11-24. Branch `other-piece` (from master `a063e19`). Engine wishlist #45. The user asked for "the most
efficient implementation"; every choice below is the cheapest one that works, reusing existing machinery.

## Intent

A formula reads only the piece it runs on, so a value another counter holds must be retyped: CNA 15.61 compares the
attacker's and the defender's adjusted morale (each from that side's own largest unit), 15.53 compares the largest
units' sizes. A binding that reads a property of a second piece, picked when the formula runs, removes the retyping.

## Design (decided)

- **Binding kind `Piece`.** `VariableBinding.Kind = Piece` with `Property` (a Pieces/Both property, stored or computed)
  and new optional `PieceFilter` (unit-filter text; empty = every piece). Its value is that property of the picked
  piece, read through `PropertyValues` (Default when unset, as a Unit binding; a computed property that does not apply
  = missing).
- **Picking = a choice box.** The Formula dialog shows the binding as a dropdown of candidate pieces: every piece the
  player can see that matches `PieceFilter`, labelled `{name} ({location})` (`{name}` alone when off-grid; duplicate
  labels get ` #2`, ` #3` in state order), sorted by label. The typed value is the label, so it travels guest → host in
  the existing `RunRequest.Typed` map. No map clicking.
- **Resolution.** The runner resolves the label against the candidates visible to the requester (host: the requesting
  guest's sides; local/hot-seat: the current player's view; solo: every piece). An unknown label is the error
  `Choose one of the listed pieces`. The working line names the piece: `Morale of 2/7 Bn` (as the Unit binding's
  description).
- **Always asks.** Like an Input binding it is never auto-filled, so Fully automatic stops for it (as "| always ask").
- **Hidden information (cheapest safe rule).** For a player who does not hold every side, a Piece binding whose
  property is not Public (`PropertyFacts.IsPublic`) is a hidden input: the guest's dialog leaves the calculation to the
  host and the working line is redacted for that player (the existing hidden-input path). A Public property is shown as
  usual. The candidate list contains only pieces the player can see, and masked pieces show as their placeholder name.
- **Workbench.** Binding kind `Piece` in the kind list; details = Property + "Which pieces" filter.
- **Validator.** Property exists and is not Holders-scope (`Formula "{f}": variable "{v}" reads unknown property "{p}"`
  shape as Unit bindings use); filter parses (`... "which pieces" filter does not parse: {msg}`).

## Non-goals
Map clicking to pick; picking by rule ("largest unit"); writing outputs to the picked piece (#43); reading several
properties of one picked piece through one pick (each binding picks on its own).

## Anti-goals
A guest learning a hidden value or a hidden piece's existence through the candidate list or the working; old packs
changing (`PieceFilter` omitted when empty; `CurrentFormat` stays 4).

## Approaches
Picked: choice box over visible candidates + host-decides masking for non-Public properties (reuses Input choices,
Typed transport and the hidden-input path). Rejected: reveal the true value to both players (needs a new "revealed"
redaction exception in PlayerView and the guest path); map-click picking (new dialog↔map wiring on two front ends).

## Left to the builder
Where candidate lists are computed (shared Render helper for both dialogs and the host); test layout; wiki example.
