# Phase conditions, turn and stage numbers — design

Date: 2026-11-22. Branch `phase-conditions` (from master `cee2823`).

## Intent

A rules pack's turn sequence is a fixed list of phases. Many games run a phase only when something
holds: the side with the initiative decides each stage whether it goes first or second, some phases
happen only at game start or once a month, some only "if no assaults were made". Today a pack lists
every possible copy and the players click through the ones that do not apply; every formula hooked on
such a copy needs a guard so it changes nothing, and the guards are easy to get wrong. Formulas also
cannot read the turn or stage number, so packs keep a hand-counted turn global that drifts after an
odd undo or skip.

This change lets a phase carry a condition, so the sequence skips it when the condition is false, and
gives every expression `turn()`, `stage()` and `mod(a, b)`.

Success: a pack lists "Side A's player-turn (if first)", "Side B's player-turn", "Side A's player-turn
(if second)" once each with a condition on a global; pressing Next Phase goes straight to the copy that
applies; the skipped copy shows greyed in the phase strip; no guards are needed in its formulas; a
"game start only" phase is `turn() == 1`; a monthly phase is `mod(turn(), 4) == 1`.

## Goals

1. `PhaseDef.Condition` (optional expression). Empty = always runs (today's behaviour).
2. Next Phase (and Start) skip every phase whose condition is false at the position it would start at.
3. A skipped phase runs nothing: no OnStart hooks, no OnMove hooks, no warnings, no checklist.
4. Each skip is visible: a log line per skipped phase and a strip notice "Skipped: <names>".
5. `turn()`, `stage()` and `mod(a, b)` usable in formulas, filters, warnings, alerts and conditions.
6. Validator checks conditions; Workbench (Avalonia) edits them; wiki documents them.

## Non-goals

- Phase *order* chosen by a global (approach B below): not built; conditions on copies cover it.
- A separate `PhaseDef.Once` flag: `turn() == 1` covers it.
- Wishlist #18 (globals and holder values in piece filters): not built here. Conditions get their own
  small reading context (below); piece filters stay as they are.
- Wishlist #21 (`FormulaDef.HookOnly`): dropped; with conditions a pack no longer needs three copies
  of a hooked formula. (Each copy still lists its hooks, but the formula itself is one.)
- Repeat-on-demand blocks, nested blocks, end-of-phase entries (wishlist #6, #34, #35).
- WinForms Workbench editing of conditions (legacy app; it must keep a condition intact when it edits
  and saves a pack — the records are copied with `with`, so it does).

## Anti-goals (a failure even if it "works")

- A condition that silently never fires, with no sign in the log or strip.
- A condition revealing hidden information: it may read only values every player can see.
- Next Phase hanging (every phase skipped) or two undo steps for one press.
- An old pack or an old save behaving differently, or an unchanged pack's SHA-256 changing.

## Approaches considered

- **A. Condition per phase, checked by Next Phase (picked by the user).** One general mechanism
  covering turn order, game-start, monthly and "only if" phases. Cost: the sequencer must read game
  state when advancing.
- **B. Block orders chosen by a global.** Fits the two-orders case exactly, no duplicate copies, but no
  help for game-start, monthly or "only if" phases, which would still need A. Rejected as narrow.
- **C. Enter the phase, then auto-advance when false.** Cheapest code, but start hooks fire, the log
  fills with noise and one press becomes two undo steps. Rejected.

## Design (decided)

### Model

`PhaseDef.Condition : string?`, `[JsonIgnore(WhenWritingDefault)]`. `RulesPackIo.CurrentFormat` stays
4 (same policy as the previous branch's additions); an unchanged pack hashes the same.

### What a condition may read

A condition is evaluated in a **phase context**, not a piece context:
- global properties by name (`HolderDef.GlobalId` ledger, falling back to the property's Default when
  unset — the global holder has no side, so every player sees it);
- `turn()`, `stage()` of the position being tested (not the current one);
- constants, arithmetic, comparisons, `and/or/not`, `if`, `min/max/round/floor/ceil/abs/clamp`,
  `mod`, `lookup(Table, …)` with the above as keys.
Not allowed (validator error; run-time error if it slips through): piece functions `name()`,
`marker()`, `has()`; dice; sided-holder values (they are hidden from the other side, and a greyed chip
would reveal them).

### Skipping

- Next Phase steps position by position with the existing `Sequencer.Next` math and, for each
  candidate, evaluates its condition against the current state with the candidate's turn and stage.
  The first candidate whose condition is true (or empty) becomes the new position.
- Skipped candidates still count for blocks: skipping the last phase of a block still moves to the
  next repeat, and a block whose phases are all skipped in one repeat still counts that repeat.
- Start (first phase of the game) uses the same rule from `Sequencer.First`.
- One press = one undoable action: the existing `AdvancePhase` command gains
  `Skipped : IReadOnlyList<string>?` (the skipped phase names, `WhenWritingNull`), so undo and the
  log show the skips with the step. Old saves have no `Skipped` and load unchanged.
- Log: one line per skipped phase, "Skipped <phase name> (condition false)", inside the same action.
- Strip notice: "Skipped: <name>, <name>" joined into the existing pass/relocation notice text.
- **Loop guard:** if one full turn's worth of candidates (from the current position back to the same
  phase one turn later) are all skipped, stop at the first candidate (treat it as not skipped) and set
  the notice "Every phase was skipped; check the phase conditions". No hang.
- **Condition errors** (unknown name at run time, divide by zero, type error): the phase is **not**
  skipped, and the notice says "Condition of <phase> failed: <message>; phase not skipped". Fail-open so
  a broken condition can never make play stall silently.
- Loading a game or reconciling after a rules edit never moves the game because of a condition; a
  condition is tested only when the game advances into a phase.
- Online: the host advances; guests see the result in the snapshot (advancing needs no guest-side evaluation; the strip's will-skip preview does run on guests, safely, because globals are never hidden except under Lockdown, where previews show defaults).
  Hot-seat: same as offline.

### Phase strip

- New `ChipState.Skipped`, drawn greyed (and struck-through or italic, builder's choice matching the
  theme) with tooltip "Skipped this pass (condition false)" for phases skipped since the turn began.
  The state keeps the skipped ids of the current turn (`GameState`, `WhenWritingDefault`, cleared at
  the start of each turn), so chips stay right after a load or undo.
- Upcoming chips whose condition is false *right now* (evaluated against the current state for their
  next position) are drawn faded with tooltip "Will be skipped unless <condition> changes". This is a
  preview only; the decision is made when advancing.
- The "next phase" label names the phase Next Phase will actually go to.

### `turn()`, `stage()`, `mod`

- `turn()`: `PhasePosition.Turn` (1 before the game starts). `stage()`: `PhasePosition.Repeat`
  (1 outside blocks). Available wherever a position exists: formulas (`FormulaRunner`), piece filters
  (`PieceFilter`), warnings, alerts, conditions. Where no position exists (no phases in the pack),
  both return 1.
- `mod(a, b)`: floored modulo (`a - b*floor(a/b)`), error on `b == 0`; a function, no new operator.
- Added to the parser's known functions, the validator's checks, the formula working/layout display,
  and the Formula Reference wiki.
- Packs that already define a property named `turn`/`stage` are unaffected: these are functions with
  parentheses, properties are bare names.

### Validator

- Condition parses; every name is a global property (scope Holders or Both) or a table in `lookup`;
  forbidden functions (above) are errors with a message naming the phase.
- A condition that is a constant false is a warning ("phase <name> never runs").

### Workbench (Avalonia)

Phases tab: a "Runs only if" text box for the selected phase (empty = always), validated like other
expressions with the error shown inline; a short hint: "Leave empty to always run. Example:
PlayerA == 1, turn() == 1, mod(turn(), 4) == 1."

### Docs

Wiki: Formula Reference (`turn`, `stage`, `mod`; what a condition may read), Making a Rules Pack
(phase conditions, the two-orders recipe with invented demo names), Window Rules Workbench (Phases
tab field), Window Map / phase strip (skipped and will-skip chips, notices). RESUME entry.

## Left to the builder

- Exact class for the phase context (a small `IEvalContext` over globals + position) and how
  `turn()/stage()` reach the existing piece and formula contexts (e.g. default interface members on
  `IEvalContext` returning 1, overridden where a position is known).
- Greyed/struck chip styling within the existing theme.
- Test layout: Core sequencer tests for skip/block/guard/errors/undo; Rules validator tests; Render
  PhaseController tests for notices and chips; Desktop Phases tab test; round-trip and hash tests.

## Constraints

- Public repo: invented test data only; no content from any real game.
- Old saves and packs load unchanged; new members `WhenWritingDefault`/`WhenWritingNull`;
  `CurrentFormat` stays 4; unchanged pack keeps its SHA-256.
- No new NuGet packages; UI tests headless (`[AvaloniaFact]`), never visible windows.
