# Formula outputs — design (short)

Date: 2026-11-23. Branch `formula-outputs` (from master `af9c12e`). Engine wishlist #19, #39. Approved by the user
("appreve").

## Intent

A formula's one result goes to every output, so one die roll applied to four supply types, a capture taking a
different share of each supply, or filling a start dump with four values each need several formulas and scratch
properties to carry a roll between them. Outputs name fixed properties, so "the slot the player picks" needs one
formula per slot (six menu entries where one would do).

## Design (decided)

**Value per output (#19).** `FormulaOutput.Expr : string?`. When set, that output's value is this expression,
evaluated in the same run (same `RunScope`: same dice, inputs, bindings, cached values) after the formula text; it reads
bindings as the text does (the formula's own result is not a variable). When blank, the output takes the formula's
result (today). An output expression whose value is not a number fails the run with
`The value for {property} is not a number`.
The working line shows each output's own value: `… → Fuel 140 − 12 = 128; Ammo 30 − 6 = 24` (existing per-output
part format; the value printed is that output's value). Variables read only by an output expression count as used
(`FormulaVariables.Used/Sources/Reach`, holder reads for masking, precondition roll reach unaffected).

**Property chosen at run time (#39).** `FormulaOutput.PropertyChoice : List<string>?` (property ids) and
`FormulaOutput.PropertySelect : string?` (expression). When `PropertyChoice` has entries, `Property` is not used and
the output's property is picked per run: a number N picks the Nth listed property (1-based, whole number); a text
value picks the listed property whose name equals it (case-insensitive, trimmed). Otherwise the run fails with
`{value} is not one of the output's properties`. `PropertySelect` blank with a non-empty list is a validator error.

**Validator.** Output expression and selector parse; their variables are bound (reuse the text's checks). Every id in
`PropertyChoice` exists and suits the output's target the way `Property` must today (reuse `CheckOutput`).
Error (n is 1-based): `Formula "{name}": output {n} has a property list but no "Pick by"`. An empty list
means the fixed `Property` is used.

**Everything that looks at outputs' properties** treats `PropertyChoice` like `Property`: "formulas using this
property" (`FormulaEdit` ~402), property delete/cleanup, Workbench lists, masking of outputs to hidden holders. The
online guest rule checks targets/holders, not properties — unchanged.

**Workbench (Avalonia).** Outputs grid: a `Value` column (expression; hint `Blank = the formula's result.`) and a
`Choose from` property picker with a `Pick by` expression; blank → null. WinForms Workbench not edited (keeps the
fields through `with`).

**Compatibility.** New members omitted when null; an unchanged pack keeps its SHA-256; `CurrentFormat` stays 4; a
formula without the new fields gives the identical trace and working line.

## Non-goals
Outputs that place or remove counters (#37, #52); outputs onto another piece (#43); the formula's result as a variable.

## Anti-goals
Old packs' results or working lines changing; a run that writes some outputs and fails on another (all outputs are
computed before anything is written, as today); a typo'd property name found only mid-game.

## Approaches
Picked: optional value expression per output, and a validated property list with a selector. Rejected: naming the
property from an expression's text with no list — not checkable before the formula runs.

## Left to the builder
Workbench widget layout; test layout.
