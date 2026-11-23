# Formula guards — design (short)

Date: 2026-11-23. Branch `formula-guards` (from master `32c7531`). Engine wishlist #61, #46, #33. Approach A approved
by the user ("ok yeah").

## Intent

Some actions are forbidden while a record says so, some charts call for one more die only after a given result,
and some inputs matter for only a few of a formula's choices. Today a formula always writes its outputs, so packs
write sentinels (-1) the player must notice and undo by hand. Every Roll binding is rolled and every Input is asked
on every run, so the log fills with unused dice and the dialog shows inputs that change nothing.

## Design (decided)

**Refused runs (#61).** `FormulaDef.Precondition : string?` (expression) and `FormulaDef.RefusalMessage : string?`.
- The runner checks the precondition before the formula text is evaluated. If it is false, the run is refused:
  nothing is rolled for the text, nothing is written, and the trace carries `Error = message` and `Refused = true`.
  Without a message the text is `{formula name} cannot be run now`.
- The check is in the runner (Core), so every path refuses the same way. Piece menu and formula dialog runs show
  the message and cannot be applied. A host refuses a guest's intent with the message. A hook run's unit is
  handled like today's failed trace (the existing log line with the message; nothing applied). A queued run stays
  pending, as a failed trace does today.
- The precondition may read the formula's bindings (unit, global, holder, table, location, moved, input). It may not
  read a Roll binding or call `dice()`: a refusal must not spend dice.
- A precondition that is not true or false is an error, as an `if` condition is.

**Bindings used only when (#46, #33).** `VariableBinding.When : string?` (expression).
- When the condition is false, the binding is not resolved: a Roll is not rolled (no log entry), an Input is not
  asked and is not "missing". The formula dialog shows the variable as `not used`.
- If the text then reads that variable on the branch it takes, the run fails with
  `{variable} is not used here: its "Only when" condition is false`. That surfaces pack mistakes instead of using 0.
- `When` may read other bindings (including Inputs, so one input can decide whether another is asked) but not
  the binding itself or a cycle (existing "depends on itself" error).
- Everything else keeps today's resolve-everything-first behaviour.

**Validator.** Both expressions parse; unknown variables are errors with the formula's name, as for the text.
`Formula "{name}": the precondition cannot use the roll {variable}` (error); same for `dice()`.

**Workbench (Avalonia).** Formulas tab: `Only runs when` (expression) and `Refusal message` fields; each binding
row gets an `Only when` field. WinForms Workbench not edited (keeps the fields through `with` copies).

**Compatibility.** New members are null by default and omitted when null (`WhenWritingNull`); an unchanged pack keeps
its SHA-256; `CurrentFormat` stays 4.

## Non-goals
Resolving only what evaluation touches (#29); choices that depend on the piece (#49); warnings built from preconditions.

## Anti-goals
A refusal that spends dice or writes anything; an old pack's dialog or results changing; a skipped binding silently
read as 0; a guest getting around a precondition through the host.

## Approaches
A (picked): per-binding `When` plus a formula precondition, both explicit and checked up front. B (rejected): resolve
only what evaluation touches — it would also solve #29, but inputs would appear one at a time as earlier ones are
filled, changing every formula dialog in every pack.

## Left to the builder
Field layout in the Workbench; how `not used` is styled in the dialog; test layout.
