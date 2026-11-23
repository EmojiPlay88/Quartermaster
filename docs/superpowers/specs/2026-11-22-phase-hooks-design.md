# Phase hooks — design (short)

Date: 2026-11-22. Branch `phase-hooks` (from master `a9d0088`). Wishlist #35, #26, #40. Approved by the user
("go"); the stop-in-phase rule was the user's pick.

## Intent

Pack authors push checks into the wrong phase, route every reaction move through menu formulas and write
"only in the Organization Phase" into captions, because hooks run only on entering a phase, move hooks fire
only for the acting side, and menu formulas run anywhere.

## Design (decided)

**End-of-phase entries (#35).** `PhaseDef.OnEnd : List<StartEntry>` — same shape as OnStart (formula,
ForEach Once/Units, unit filter). Next Phase on a phase with OnEnd entries that have not run this pass:
- runs them (log tag `end`) in the press's action;
- if nothing was queued for the player (all applied automatically), the same press then advances — one undo step;
- if any run was queued (needs input, or semi-automatic confirmation), the game **stays** in the phase, the
  runs are shown as OnStart's are, and the notice is `Finish the end-of-phase runs, then press Next Phase again`.
  The next press advances without re-running them.
- "ran this pass" is state: `GameState.EndedAt : PhasePosition?` set by a new command `EndPhase(PhasePosition At)`,
  cleared by `AdvancePhase`; replays apply it as recorded; undo restores it. Skipped phases run no OnEnd.

**Move formulas for the other side (#26).** `MoveEntry.FiresFor : MoveSide { Acting, Other, Any }`, default
Acting (today). Other = moved pieces not owned by the phase's side; Any = all moved pieces. A phase without a
side: all three behave as today (every moved piece). A run is made for the piece's **owner**: its log stamp and
queued run go to the owner's side, as a run that player made by hand. Map drags are excluded with `map()` in the
entry's unit filter (no new field).

**Menu formulas limited to phases (#40).** `FormulaDef.Phases : List<string>?` (phase ids; null/empty = any).
Player-started runs (piece Calculate menu, formula dialog Run, host intent) outside those phases are greyed /
refused: disabled reason `Only in: {phase names}`; host refusal `{formula name} can only be run in: {phase names}`.
Before the game starts a limited formula is not allowed; a pack with no phases ignores the limit. Hook runs
(OnStart/OnMove/OnEnd) ignore it.

**Validator.** `Formula "{name}": phase list names unknown phase "{id}"` (error). Warning
`Phase "{phase}": {start|move|end} entry runs "{formula}", which is limited to other phases`. OnEnd entries get the
same checks as OnStart entries.

**Workbench (Avalonia).** Phases tab: an "On end" grid like "On start"; a "Fires for" column on the On move grid.
Formulas tab: an "Only in phases" picker (checkable list of phase names; none = any). WinForms Workbench not edited
(must keep the new fields on save, as `with` copies do).

## Non-goals
Repeat-on-demand or repeated segments (#6, #34); end entries for a whole block or turn; a seat concept.

## Anti-goals
Next Phase looping (end entries re-running every press); two undo steps for an automatic press; an old pack or save
changing behaviour or its SHA-256 (new members omitted when default/empty; `CurrentFormat` stays 4); a guest
running a limited formula through the host.

## Approaches
One sensible way: each feature reuses the existing OnStart, OnMove and menu machinery. Rejected: an "end"
pseudo-phase (pollutes the strip, skips/conditions interplay), a seat-based "acting player" check for #26 (no seat
concept; owner is known).

## Left to the builder
Exact grouping of OnMove units by owner for stamps; the Workbench widgets' layout; test layout.
