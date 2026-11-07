# Piece 4 — Undo and step back — Design

*Date: 2026-11-07. Builds on the master spec (`2026-11-06-quartermaster-engine-design.md`, §5 and §9.5) and on pieces 1–3 as merged to `master`. Where this document and master spec §9.5 disagree, this document wins.*

## 1. Intent

The user is learning a large wargame through Quartermaster, so they will make mistakes: a counter dropped on the wrong hex, a formula applied to the wrong unit, a phase skipped too early. Today the only way back is **File → Open Autosave**, which loses everything since the last autosave and gives no idea what it is rewinding. Piece 4 adds two ways back:

- **Undo / Redo** of the last action (Ctrl+Z / Ctrl+Y), like any editor or VASSAL.
- **Step back** to the start of the current phase or any earlier phase, chosen from a list, with a confirmation that says what will be undone.

The user asked to skip the design questions. **Every decision below is the assistant's call** unless it restates master spec §9.5. They are marked "(call)" where a different reasonable answer exists.

## 2. Content boundary (unchanged, hard)

The engine ships no content from any real game. Nothing in this piece needs any. Tests use the existing invented fixtures and invented phases.

## 3. How it works (the core idea)

Every change to a game already goes through `GameSession.Execute` as a logged `GameCommand`, and the reducer `GameState.Apply` is deterministic (dice results and timestamps are stored in the commands, so replay never rerolls). What is missing is the **starting state** the log replays from.

So undo is **truncate-and-replay**, not inverse commands:

1. The session keeps the **genesis state**: a deep clone of the state at the moment the game began (after `NewGameBuilder` / `SaveImporter` / `Replace`, before the first logged command).
2. Rewinding to command index *n* = take the nearest cached state at or before *n*, apply commands up to *n*, and drop the rest of the log.
3. To keep this fast, the session caches a state snapshot **in memory** at every checkpoint (§4). Snapshots are not saved to disk; they are rebuilt on demand.

No command type needs an inverse. New command types added later get undo for free as long as they stay deterministic.

## 4. Actions and checkpoints

**Action** = the group of commands one user gesture produces. Examples: a drag of a stack (one `MovePieces`), a formula Apply (`RollDice` + `LedgerDelta`s + `ResolveRun`), a phase advance (`AdvancePhase` plus everything fully-automatic mode ran on phase entry). The session records action boundaries; how commands are grouped (an explicit `ExecuteAction(params GameCommand[])`, a begin/end scope, or a group id on the command) is the builder's choice, but every existing multi-command call site must produce exactly one action.

**Not actions** (call): `SetStackExpanded` and `SeedLedger`. They are folded into the neighbouring action and never show up as "Undo …" on their own. Undoing an action that contains them undoes them too; that is fine because seeding is idempotent and re-run after every rewind (§6).

**Checkpoint** = the log index right after a phase boundary, i.e. the point where `PhaseController` raises `PhaseBoundary` (after `AdvancePhase` and the on-entry effects). With no phases in the rules pack, a `SetTurn` is a checkpoint instead. Game start is always checkpoint 0.

Consequence (call, important): **stepping back to the start of a phase keeps that phase's automatic on-entry results**, including dice rolled by fully-automatic mode. Stepping back does not reroll them. To undo the automatic results too, step back one phase further (to the start of the previous phase) and press Next Phase again; that rolls new dice, and the log will show it (§7).

## 5. What the user sees

### 5.1 Edit menu (new, between File and View)
- **Undo *‹action›*** — Ctrl+Z. The label names the action, e.g. "Undo move of 3 pieces", "Undo Fuel formula on 2/7 Armd", "Undo Next Phase". No confirmation (Redo is the safety net). Disabled at the undo floor (§8).
- **Redo *‹action›*** — Ctrl+Y (also Ctrl+Shift+Z).
- **Step Back to Start of Phase** — rewinds to the latest checkpoint before the current position. If you are exactly at a checkpoint (nothing done yet this phase), it goes to the previous one.
- **Step Back…** — opens the checkpoint list (§5.3).

Both shortcuts are registered in the existing `CommandRegistry` and are rebindable. `ShortcutGate` already suppresses Ctrl+Z while a text box has focus, so text boxes keep their own undo.

### 5.2 Toolbar and phase panel
- A **◀ Step Back** toolbar button next to Next Phase (rebindable id, hideable like other toolbar items).
- Undo and Redo toolbar buttons with a tooltip naming the action.

### 5.3 Step Back dialog
A list, newest first, of every checkpoint above the undo floor: turn, stage, acting side, phase name, wall-clock time reached, and how many actions were taken in it. Select one, see a summary below the list, press **Step Back**.

The summary (also used by the confirmation for Step Back to Start of Phase) says what will be undone, in plain words:
- number of actions;
- dice rolls undone, with a short note: "Rolling again will give new results";
- ledger values that will change, as "Fuel on 3 units, Ammo on 1 unit";
- pieces moved / added / removed.

Step back always asks for confirmation; Undo never does.

### 5.4 After any rewind
- The map, ledgers, log panel, phase strip and panel, notes and inventory all show the rewound state.
- Open formula dialogs and batch grids close (same as a difficulty change in piece 3), because their pending runs may no longer exist.
- Selection is pruned to pieces that still exist. The viewport does not move.

## 6. Rewinding mechanics

1. **Autosave first.** Before a Step Back (not before a plain Undo) the current game is autosaved, so even a step back followed by new play can be recovered from File → Open Autosave.
2. Truncate and rebuild state (§3).
3. **Re-seed and reconcile.** Run `SeedLedgers()` (picks up rules-pack keys added after the rewind point) and `PhaseController.Reconcile` (the rules pack may have lost the phase the rewound position points at; piece 3 already handles that with a logged reason).
4. **Record it in the log** (call). Append one new command `SteppedBack { ToSeq, ActionsUndone, DiceUndone, Summary }`. It writes a `LogKind.System` line such as *"Stepped back to Turn 3 · Axis Supply. Undid 14 actions, 2 dice rolls."* This keeps the log honest about dice re-rolled after a rewind. A plain Undo does **not** add a line (call: Ctrl+Z in a busy phase would bury the log).
5. The redo stack holds the undone actions (§7).

Rules-pack edits are not in the command log and are **not** undone. Commands carry absolute values (`LedgerDelta.After`, recorded seeds, recorded dice), so replay does not depend on the current rules pack.

## 7. Redo

- Redo re-applies the undone actions exactly as recorded: same dice results, same ledger values, same timestamps.
- The redo stack is **in memory only** and is cleared by any new action, by loading or starting a game, and by a rules-pack change that triggers a reseed or reconcile.
- After a Step Back, Redo replays the stepped-back actions one at a time and the `SteppedBack` log line is removed when the first one is redone (call: so a step back that is fully redone leaves no trace).

## 8. Undo floor and saves

**Save format 5** adds the genesis state and the checkpoint list to `SaveFile`:
`{ Format = 5, ModuleSha256, RulesSha256?, Genesis, Snapshot, Log, Checkpoints }`. A checkpoint entry holds the log index, turn, phase position and labels, and the time reached. Snapshot stays, so loading never needs a replay.

- Loading a format-5 save: full undo back to game start.
- Loading a format 1–4 save (no genesis): the **undo floor** is the load point. Undo and Step Back stop there with a tooltip saying why. The first save afterwards writes format 5 with the loaded snapshot as genesis and only the commands after loading, so the file stays replay-consistent.
- Consistency check (call): on load, replay genesis + log and compare with the stored snapshot (JSON equality, the same test the existing replay tests use). On a mismatch, keep the stored snapshot, set the undo floor to the load point, and write a diagnostic line. Never refuse to load a game over this. If the replay is slow on big games, the builder may run it in the background or on the first undo instead.
- Write saves via temp file plus move (the native save writer currently writes in place; the other writers already use temp + move).
- Size: genesis roughly doubles the file. Acceptable; no compression in this piece.

## 9. Autosave retention (finishes master spec §9.5)

Keep the **last 20** autosaves plus **the first autosave of each turn**, per module. Replaces the flat keep-10. File names gain sub-second precision so two saves in the same second do not overwrite each other.

## 10. Goals, non-goals, anti-goals

**Goals**
- Undo/Redo of the last action, with named actions.
- Step back to any phase checkpoint since game start (or since loading an old save).
- Saves that carry their own replay base; autosave retention from §9.5.
- Every command type undoable with no per-command inverse code.

**Non-goals**
- Multiplayer rules for undo. When multiplayer arrives, only the host may step back and everyone is notified (master spec §9.5); nothing in this piece should block that. The rewind should be one session operation that a host could broadcast.
- Branching history / a timeline view of alternate lines.
- Undoing rules-pack edits, settings, window layout or map view (zoom, LOS, hidden counters).
- "Rewind to here" from a log entry. Possible later on the same mechanism.
- A difficulty-mode restriction on undo (e.g. no undo in hardcore). Possible later as a setting.

**Anti-goals**
- **A silent reroll.** Re-rolling after a step back must be visible in the log.
- **Losing work to a step back with no way back.** Autosave before every step back; Redo until the next action.
- **Undo that drifts.** A rewound state must be byte-identical (JSON) to the state at that point when it was first played. Tests must prove it.
- Undo slow enough to hesitate before pressing it. Target: Undo under 100 ms and Step Back under 1 s on a game of ~1,200 pieces and ~5,000 commands, on the user's machine.

## 11. Testing

- Core: for random sequences of every command type, rewinding to each index equals the state recorded at that index during play (JSON equality).
- Action grouping: every existing call site that issues several commands undoes as one action (formula apply, batch grid Apply all, fully-auto phase entry, replace piece, Next Phase).
- Save round trip: format 5 restores full undo; formats 1–4 load with the floor at the load point; a tampered log falls back to the floor without failing the load.
- Redo: same dice, same values; cleared by a new action.
- Step back across a deleted phase reconciles with a logged reason.
- Autosave retention: 20 + one per turn; no same-second overwrite.
- Performance test on a synthetic game at the target size (§10).

## 12. Left to the builder

- How actions are grouped in `GameSession` (§4).
- Snapshot cache policy in memory (every checkpoint, or every N commands, plus eviction).
- Exact action label wording.
- Whether the consistency check (§8) runs at load, in the background or lazily.
- Dialog layout, within the existing dense gray WinForms style.
