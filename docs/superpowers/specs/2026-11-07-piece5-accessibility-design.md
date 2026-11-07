# Piece 5 — Accessibility layer (HOI4-style) — Design

*Date: 2026-11-07. Builds on master spec §10 (`2026-11-06-quartermaster-engine-design.md`) and on pieces 1–4 as merged to `master`. Where this document and master spec §10 disagree, this document wins.*

## 1. Intent

The user is learning a large, logistics-heavy wargame through Quartermaster. The engine already tracks the numbers, runs formulas and walks through phases. What is missing is the layer that makes a big game **readable at a glance**, the way grand-strategy games like Hearts of Iron IV do it:

- every number says where it came from when you hover it;
- problems announce themselves (an alert bar) instead of hiding in a 1,200-row ledger;
- one panel shows your formations, their key totals and what still needs doing;
- the screen always says what to do now.

The user asked to skip the design questions. **Every decision below is the assistant's call** unless it restates master spec §10. Calls where a different reasonable answer exists are marked "(call)".

## 2. Content boundary (unchanged, hard)

The engine ships no content from any real game. Alerts, outliner totals, notes and the tour text describe the **engine**, never a real game's rules. User-defined alerts and notes are authored by the user in the Rules Workbench. Tests and fixtures use invented properties ("Fuel", "Ammo"), invented alerts and the existing invented phase fixture. The tour uses neutral wording ("your rules pack", "a phase").

## 3. The six parts

### 3.1 Number tooltips (master §10.1)

Hovering a ledger value shows how it got there. Example, for a piece's Fuel:

```
Fuel 113  (2/7 Test Bn)
  Start of turn 3 ........ 140
  −27  Fuel Use            Turn 3 · Supply A
  +12  manual: "captured dump"  Turn 3 · Movement
  −12  TEST Attrition      Turn 3 · Combat
  Earlier turns: net −60 from 200 (seeded)
```

- **Grouping (call):** changes in the current turn are listed one per line (newest last, at most 12; more collapse into "… N more changes"). Everything before the current turn collapses into one "Earlier turns" line with the net change and the seeded starting value.
- **Labels:** formula changes show the formula name (and rule reference label when present); manual changes show `manual:` plus the note; seeds show `seeded`. Reuse and extend `Ledger.Describe`.
- **Where:** everywhere a ledger value is shown —
  - Ledger window cells (replaces today's last-10 list);
  - the map hover popup (`CounterDetailPopup`), which gains a **ledger section** listing the piece's ledger values (call: values only for properties that apply to the piece; each line hoverable for its breakdown, or the breakdown shown as a nested tooltip/expander — builder's choice of mechanism);
  - Inventory totals (breakdown = the list of contributing pieces with their values, not history; call);
  - Batch grid "Ledger" column;
  - the Outliner (§3.3).
- **Data:** a breakdown needs to know which turn and phase each change happened in. `LedgerEntry` gains `Turn` and `Phase` (name), filled by the reducer from the state at apply time (deterministic, so replay stays exact).
- **Save compatibility:** save format becomes 6. Format-5 saves must **keep full undo**: their entries load with `Turn`/`Phase` null (shown as "earlier"), and the undo consistency check must not reject a format-5 save just because replay now fills the new fields (compare with those fields normalised, or rebuild the snapshot from replay — builder's choice, tested).

### 3.2 Alert bar (master §10.2)

A thin bar under the toolbar (above the phase tracker strip), visible whenever a game is open (hideable in Settings → Display).

- **Left:** the "What now" banner (§3.4).
- **Right:** alert chips, one per alert that currently has matches: icon by severity, short name, count. Chips sort by severity (Critical, Warning, Info), then name. No matches = no chip.
- **Click a chip:** cycles through the matching pieces, centring and selecting each in turn on its map (the Inventory window's cycle pattern). For a holder match, opens the Ledger window and selects the holder row.
- **Hover a chip:** tooltip with the alert's message and the first 10 matches ("… and N more").
- **Right-click a chip:** menu with every match (click one to centre), plus "Hide this alert until next turn" (call: per-game, stored in game state as a logged command so undo and saves respect it).

**Built-in alerts (call: always on, can be hidden per game as above):**

| Alert | Severity | Matches |
|---|---|---|
| Negative value | Critical | every piece or holder with any ledger value < 0; one chip per property (`Fuel below zero`) |
| Formula waiting for input | Warning | units of pending runs that need input (fully automatic mode) |
| Formula not applied | Info | units of pending runs not yet applied (hardcore / semi-automatic) |
| Unacknowledged warning | Warning | units of the current phase's unacknowledged decision warnings |

**User-defined alerts (master §10.2):** the rules pack gains a list of alerts:

```
AlertDef { Id, Name, Condition, Message, Severity, SideId?, Notes }
```

- `Condition` is a unit-filter expression, evaluated per piece with the existing `PieceFilter` (same language as phase warnings and side filters), e.g. `Fuel < 20 and Ammo < 10`.
- `Message` may contain `{unit}`.
- `Severity`: Info, Warning or Critical.
- `SideId` (optional): only pieces of that side; and (call) the chip shows only while that side is acting, when phases are defined.
- A bad expression never throws: the alert gets no matches and the Workbench validator flags it.
- **Workbench:** a new **Alerts** tab, list editor in the style of the Phases tab, with a live "N matches now" counter beside the condition box.

**Recalculation:** after game changes, rewinds and rules changes, debounced (call: 250 ms), off the critical path of a drag. Target: under 100 ms for 10 alerts over ~1,200 pieces.

### 3.3 Outliner (master §10.3)

A docked panel (View → Outliner; docks on the right, pop-out like the other docks; call).

- **Top section, "Now":** the current phase's open decisions (reuse `Decisions.Compute`), each clickable exactly like in the phase panel. Hidden when there are no phases.
- **Tree:** the formation tree.
  - **Default grouping (call):** the module's organisation-display tree — the same folders and org-display maps the module tree shows — with the pieces currently on each org display as children. Pieces on ordinary maps go under one group per map.
  - **Alternative grouping:** "By side" (sides from the rules pack), switchable with a dropdown at the top of the panel. Remembered in Settings.
- **Columns:** name, unit count, then one column per **outliner property** (below), then alert icons. Group rows show totals over their pieces; piece rows show the piece's values. Every number has the §3.1 tooltip (for group totals: the per-piece list).
- **Outliner properties:** `PropertyDef` gains `Outliner` (none / sum / min / average; call: default none). The Workbench Properties tab gets the column. With no outliner properties set, the tree still shows names, counts and alerts.
- **Double-click** a piece row: centre on it. Double-click a group backed by an org display: open that display.
- Refresh on the same debounced trigger as alerts.

### 3.4 "What now" banner (master §10.4)

Left side of the alert bar, one line:

`Now: Allied player — Supply A: {first sentence of the phase instructions} · 3 decisions left`

- Before the game starts: `Press Start game ▶ to begin turn 1.`
- No phases defined: `No phases defined — free play.` (call)
- Click: reveals the phase panel. Tooltip: the full instructions.
- The side name and decisions count come from `PhaseController.View()`; nothing new is computed.

### 3.5 Interface tour (master §10.5)

A short first-run tour of the **engine's** interface, replayable from Help → Interface Tour.

- About 8 steps, each a balloon pointing at a real control with 1–3 sentences: module tree, map, phase tracker strip and Next Phase, phase panel, alert bar, outliner, ledger, formula dialog colours and difficulty, Edit → Undo / Step Back. Buttons: Back, Next, Skip tour.
- Steps whose control is not visible (e.g. no rules pack loaded) are skipped.
- Shows once, automatically, the first time a game is opened (call); remembered in Settings.
- Text describes the engine only; no game rules.

### 3.6 Notes on tables, formulas, phases and alerts (master §10.6)

Rules-pack items gain a user-written `Notes` field where they lack one: tables, formulas, phases, alerts (properties and holders already have one).

- Edited in the Workbench beside each item (multi-line box).
- Shown as tooltips: Tables tree nodes, the formula dialog title area, phase chips in the tracker strip and the phase panel heading, alert chips, ledger column headers (property notes), outliner column headers.
- Empty notes show nothing extra.

## 4. Formats

- **Rules pack format 3:** `Alerts` list; `PropertyDef.Outliner`; `Notes` on `TableDef`, `FormulaDef`, `PhaseDef`. Formats 1–2 load with no alerts, no outliner properties and empty notes. Format > 3 is rejected with the existing message pattern.
- **Save format 6:** `LedgerEntry.Turn`, `LedgerEntry.Phase`; the per-game hidden-alert list (if the "hide until next turn" command is built as a logged command). Formats 1–5 load as today; format 5 keeps full undo (§3.1).

## 5. Goals, non-goals, anti-goals

**Goals**
- The six parts of §3, working in all three difficulty modes.
- Everything driven by the user's rules pack; no game content shipped.
- No noticeable lag when dragging counters on a ~1,200-piece game.

**Non-goals**
- Multiplayer behaviour (who sees which alerts) — comes with multiplayer.
- OCR.
- A full formation hierarchy model beyond the module's org-display tree and sides.
- Charts or graphs of ledger history over time.
- Sound or pop-up notifications for alerts.

**Anti-goals**
- **Burying the player in pop-ups.** Alerts live in the bar; nothing modal appears because an alert fired.
- **A number the player cannot trace.** Every ledger value shown anywhere has a breakdown.
- **Giving hardcore mode away.** Tooltips show history (what already happened), never what a formula *would* produce; alerts never show values the player is supposed to look up.
- **Losing undo history** on saves made before this piece.
- Any real game's alerts, totals or tour text baked into the engine, tests or docs.

## 6. Testing

- Breakdown text: grouping by turn, collapse rule, labels per source, null `Turn` (old saves) shown as earlier.
- Reducer fills `Turn`/`Phase`; replay JSON-identical; format-5 save loads with `CanUndo` intact.
- Alerts: built-ins on a scripted state; user alert with a good, a bad and a side-limited condition; recalculation timing on a synthetic 1,200-piece game.
- Hide-until-next-turn: undoable, saved, clears at turn change.
- Outliner model: org-display grouping, by-side grouping, sum/min/average totals, decisions section.
- Banner text for before-start, no-phases and in-phase cases.
- Rules pack format 3 round trip; formats 1–2 load; validator flags a bad alert condition.
- Tour: step list skips missing controls (model-level test); smoke screenshot of one balloon.
- App smokes: alert bar, outliner, tooltip popup screenshots (scratchpad only).

## 7. Left to the builder

- Tooltip mechanism (custom popup vs `ToolTip`), as long as it is fast and follows the classic look.
- Exact chip icons (drawn, not image files) and colours, within system colours plus a red/amber/blue severity accent.
- Outliner column widths and the exact group sort order.
- Balloon drawing for the tour.
- How the hidden-alert list is stored, provided it is undoable and saved.
