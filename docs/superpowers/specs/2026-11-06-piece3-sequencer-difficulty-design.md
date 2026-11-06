# Piece 3 — Turn sequencer and difficulty modes — Design

*Date: 2026-11-06. Builds on the master spec (`2026-11-06-quartermaster-engine-design.md`, §9) and on pieces 1, 2 and 2b as merged to `master`. Where this document and master spec §9 disagree, this document wins: it records what the user decided later.*

## 1. Intent

The user wants Quartermaster to play like a guided computer game: the screen always says what to do now, the user makes the decisions (move this unit, attack there), and a big **Next Phase** button moves on, Civilization-style. How much bookkeeping the user does by hand is set by a **difficulty mode**, from "find every number yourself in the charts" to "like HOI4: the game calculates everything and just logs it".

The user has never played the game they are building this for. The sequencer is how they will learn it: their own rules pack walks them through each turn.

## 2. Content boundary (unchanged, hard)

The engine ships no game content. Phase names, their order, their instructions, which formulas they fire and which warnings they show are **all authored by the user** in the rules pack via the workbench. The builder must not enter any real game's sequence of play. Tests use invented phases ("Weather", "Supply", "Movement"…) in the synthetic fixtures.

## 3. What the user decided (their words, condensed)

1. **Phase tracker on screen** that tells you what to do, in order.
2. In each phase **you make the decisions**: move pieces and so on.
3. **Next Phase button**, like Civilization's Next Turn, that **tells you there are decisions left** and **lists them**. It **warns only and lets you continue anyway**.
4. **Three difficulty modes**, chosen in a dialog at game setup:
   - **Hardcore (full manual):** the formula dialog pops up and you find every value yourself (tables, charts, rulebook) and type it in.
   - **Semi-automatic (cheat sheet):** the formula dialog plus a second window displaying all the values the formula needs; you read them and type them in yourself.
   - **Fully automatic:** formulas don't pop up. The game rolls dice, updates supplies and calculates everything by itself at the right phase without bothering the players. The log shows every calculation.
5. Difficulty is **switchable mid-game, in Settings**.
6. Formula dialogs get a **Calculate** button: like fully automatic, but just for that formula. It fills in the numbers and calculates.
7. Phases are built in the workbench with **a list editor where you add phases in order**.
8. Each phase says **which side is acting**, and the tracker **shows whose turn it is**.
9. **Repeating groups** of phases within a turn are included.
10. Values that are **table keys** (like "Clear") are picked from a **dropdown of all the keys** that can go there, not typed.
11. Typed values **are compared** with the right ones. Hardcore differs from semi-automatic **only** in having no cheat sheet.

## 4. Goals, non-goals, anti-goals

**Goals**
- A rules-pack-driven sequence of phases with a tracker strip, a phase panel and a Next Phase button.
- The three difficulty modes, plus the Calculate button, working with the formulas, tables and ledgers from piece 2.
- Formulas that fire by themselves: at the start of a phase, and when a unit is moved during a phase.
- Warnings per phase (e.g. "Fuel ≤ 0").
- A Phases tab in the Rules Workbench.

**Non-goals (later pieces)**
- Online multiplayer, hidden information, chat (pieces 4–5).
- The HOI4 accessibility layer beyond phase warnings: number tooltips, outliner (master spec §10).
- OCR (master spec §8.3).
- Step back / undo to a phase checkpoint (master spec §9.5). *Assistant's call: deferred; autosave at phase boundaries is in.*
- An AI opponent.

**Anti-goals**
- Any real game's turn sequence or rules baked into the engine or tests.
- Burying the player in pop-ups. One dialog per unit for a phase-wide formula is a failure (see §7.3).
- A number the player cannot trace: every automatic calculation is in the log with its full working.
- Blocking the player: Next Phase never refuses.

## 5. Rules pack additions (pack format 2; format 1 loads as "no phases")

### 5.1 Sides
`Sides`: an ordered list of `{ Id, Name, UnitFilter }` the user defines (e.g. two sides). Optional; with no sides, phases have no acting side and the tracker shows none.
*Assistant's call (builder check):* `UnitFilter` is an expression saying which pieces belong to the side. A phase with a `SideId` limits its OnStart, OnMove and Warnings to that side's units (each entry's own filter narrows further). Modules often mark sides only by piece name, so the expression language gains `startsWith(text, prefix)` and `contains(text, part)` for use with `name()`.

### 5.2 Phases
`Phases`: an ordered list. Each phase has:
- `Id`, `Name`, `SideId` (optional), `Instructions` (plain text shown in the phase panel), `RuleRefId` (optional link to the rulebook page).
- `Checklist`: manual items ("Place reinforcements", "Check supply lines"). Each one is a decision until ticked.
- `OnStart`: formulas that fire when the phase starts. Each entry is `{ FormulaId, ForEach: "once" | "units", UnitFilter? }`. With `units`, the formula runs once per piece that matches the formula's AppliesTo and the optional extra filter.
- `OnMove`: formulas tied to moving a unit in this phase. Each entry is `{ FormulaId, UnitFilter? }`. They fire after each move of a matching unit (one drag = one move; a formation drag fires once per unit).
- `Warnings`: `{ Condition, Message, IsDecision }`, where Condition is a per-unit boolean expression (e.g. `Fuel <= 0`) and Message may name the unit. When IsDecision is set, the warning is listed as **one** open decision ("Out of fuel — 12 units"), expandable to the units. *Assistant's call (builder check):* it closes when no unit matches or when the user presses **Acknowledge** on it (logged `AcknowledgeWarning` command, per warning per phase visit).

### 5.3 Repeating blocks *(confirmed by the user after the builder check: include them)*
A contiguous run of phases can be marked as a block that repeats N times per turn (N ≥ 1). The tracker shows "Stage 2 of 3" while inside one. The model is a flat list plus optional `{ FirstPhaseId, LastPhaseId, Count, Label }` blocks that must not overlap.

### 5.4 New formula binding: Moved
A new `BindingKind.Moved` gives the distance of the move that triggered the formula, in grid units (hexes or squares, using the existing `HexDistance` / grid measure). With no grid it gives 0 and a warning in the log. Outside an OnMove trigger it is "missing", so the user types it in. The existing `Location` binding gives where the unit ended up.

### 5.5 Input defaults
`VariableBinding` gains an optional `AutoValue` expression. Fully automatic and Calculate use it for `Input` bindings (e.g. `"Normal"` for pace). *Assistant's call, needed so fully automatic can run without asking.*

### 5.6 Key dropdowns (user's decision)
Any box whose value is used as a table key is a **dropdown** of the valid keys instead of a text box, in the formula dialog, the batch grid and the "missing values" prompt. The engine finds them itself: an `Input` used in a Table binding's `RowExpr`/`ColExpr` (or in `lookup(Table, X)` in the formula text) gets that table's row or column keys. An `Input` with `Choices` uses those. Numeric range keys ("1-3", "6+") are not offered as a dropdown, because any number in range is valid. The user can still type.

## 6. Game state additions (native save format 4; formats 1–3 load at "no phase yet")

- `Position`: turn number, block repeat index, **phase Id** (not index). *Assistant's call (builder check):* the user edits phases mid-game, so position is kept by Id. If a loaded game's phase Id no longer exists in the pack, the game is placed on the phase that now sits at the old index (clamped to the list), with a log line and a notice in the strip.
- **Entering the first phase.** New games and format 1–3 saves start "before turn 1": the strip shows **Start game ▶**. Pressing it enters turn 1, phase 1, and fires its OnStart. Nothing is applied at setup time. *(Assistant's call.)*
- `Difficulty`: Hardcore | SemiAuto | FullyAuto.
- Per phase in progress: ticked checklist items, and pending OnMove/OnStart formula runs not yet applied (in the manual modes).
- New logged commands:
  - `AdvancePhase` (from → to, carries the turn number; wraps to the next turn after the last phase, replacing `SetTurn` for normal play — `SetTurn` stays as a manual correction tool);
  - `SetDifficulty`;
  - `TickChecklist`.
- All via `GameSession.Execute`, deterministic, and replayable like everything else.

## 7. Behaviour

### 7.1 Tracker strip and phase panel
- **Tracker strip** under the toolbar in the main window: turn number, block label ("Stage 2/3") when inside a block, then the phases. Done phases greyed, the current one highlighted, the acting side shown as "Axis player — Movement". It wraps or scrolls when the list is long.
- **Phase panel** (dockable, pop-out like the others):
  - the instructions;
  - the rule ref link;
  - the warnings currently matching;
  - the **Decisions left** list: unticked checklist items, pending formula runs, warning-decisions;
  - the **Next Phase ▶ <next name>** button, with "N decisions left" in red when N > 0.
- Double-clicking a decision that concerns a unit centres the map on it, as in the log. A pending formula opens its dialog.
- **Coverage fallback** (master spec §9.4): a phase with nothing but a name still shows and works as a plain step.
- **No phases in the pack:** the strip says "No phases defined — add them in the Rules Workbench (Phases tab)", and play is free-form as in piece 2b.

### 7.2 Next Phase
- With 0 decisions left it advances at once.
- With decisions left it shows a dialog listing them by name, with **Continue anyway** and **Go back**. Double-clicking an item jumps to it.
- On advance:
  - log "Turn 3 · Movement → Combat";
  - when the acting side changes, show a "Pass to <side> player" notice in the strip *(assistant's call; it is a notice, not a modal screen)*;
  - run the new phase's OnStart formulas;
  - autosave (phase boundary).
- Pending, unapplied formula runs from the old phase are dropped with a log line naming them ("skipped: Fuel for movement, Inf 3rd Bde").
- Keyboard: Ctrl+Right (`turn.next`) now means Next Phase when phases exist.

### 7.3 The three modes at runtime

| | Hardcore | Semi-automatic | Fully automatic |
|---|---|---|---|
| Formula dialog | Opens with **all** boxes empty, including those the engine could fill (unit, global, holder, table, location, moved). Typed values are checked | Exactly like hardcore **plus the cheat-sheet window** | Not opened; computed and applied |
| Dice | Roll button or physical dice, as today | Same | Rolled by the engine, logged |
| Calculate button | Available: fills every box it can, the user reviews, then Apply | Same | n/a |
| OnStart, ForEach units | Batch grid (§7.4) with empty cells | Batch grid plus cheat sheet for the selected row | Applied to every unit, one log line per unit, plus a phase summary line |
| OnMove | One unit moved: its formula dialog opens after the drop. Several units moved in one drag: one batch grid (§7.4) for that drag *(assistant's call, builder check)* | Same plus cheat sheet | Applied at once, logged |
| Values the engine cannot fill (an Input with no AutoValue) | Typed | Typed | Only those boxes are asked, in a small dialog; the run is listed as a decision until done |

- **Cheat sheet:** a modeless window next to the formula dialog. For each variable it shows where the value comes from and the value or table: a unit/global/holder property with its current value; a table shown whole, with the row/cell the formula would use highlighted; the location and moved distance. Dice are not pre-rolled.
- **Calculate:** fills every box it can, with the same values fully automatic would use (rolls dice too). The filled boxes stay editable before Apply. The log marks the run "auto-calculated".
- **Typed values are checked** (user's decision), in hardcore and semi-automatic alike; hardcore differs only in having no cheat sheet. When a typed value differs from what the engine would use, the box turns red and its tooltip says "Doesn't match: should be 4 (TestTerrainCost, row Sand)". Apply still works. Applying with a mismatch asks "Use your values anyway?", and the log records both the typed and the expected value. *(The red box, the revealed value and the allowed override are the assistant's guesses.)* Dice and Input values with no engine source are not checked.
- **Switching difficulty** (Settings → Game tab, or the setup dialog) logs "Difficulty changed to X" and takes effect from the next formula run. Already-pending runs keep their place in the decisions list and open in the new mode.

### 7.4 Batch grid (for ForEach-units in the manual modes)
One window per OnStart entry, and per multi-unit drag for OnMove:
- one row per unit;
- columns for the formula's variables, the result, and the ledger value before → after;
- an exceptions column (goes negative, warning hit);
- **Calculate all**, **Apply all**, and **Apply row**. Double-clicking a row opens that unit's formula dialog.

It is listed as one decision ("Daily water — 0 of 1170 applied") until applied or the phase ends. In hardcore, cells start empty and the user may type values or use Calculate per row/all. *(Master spec §9.3, adapted.)*

### 7.5 Setup dialog
When a new game is started (New Game, a setup, or an import), a **Difficulty** choice is offered with a one-line explanation per mode. The default is the last one used.

## 8. Rules Workbench: Phases tab
- **Left:** a list of phases in order (Add, Duplicate, Remove, Move Up, Move Down) and a Sides list editor above it. Blocks are shown as bracketed groups, with "Make block from selection…" and "Remove block".
- **Right:** the selected phase's fields:
  - name, side, instructions;
  - rule ref picker;
  - checklist items;
  - OnStart and OnMove entry grids (formula picker, ForEach, filter);
  - warnings grid (condition with live parse check, message, IsDecision).
- **Validator additions:**
  - missing formula/side/ruleRef ids;
  - unparsable conditions and filters;
  - overlapping blocks;
  - an OnMove formula with no Moved binding (warning only).

## 9. Logging
Every automatic calculation logs the same working line the formula dialog shows (`Fuel = ceil(4 × 4 × 2 ÷ 2) = 16 → Fuel 20 − 16 = 4`), tagged with turn and phase. Also logged:
- phase changes and difficulty changes;
- skipped runs and Calculate runs;
- a batch summary ("Daily water: 1170 units, 1170 applied").

The log panel's filter gains a "Calculations" kind.

## 10. Testing
- UI-free tests for:
  - the pack model and IO (format 1 → 2);
  - the validator;
  - phase advance and wrap, blocks and repeats;
  - decisions computation;
  - OnStart and OnMove triggering;
  - the per-mode behaviour as commands/state (no UI);
  - Moved distance;
  - AutoValue;
  - save format 3 → 4.
- Smoke runs on the synthetic fixture with an invented phase list in all three modes, including a full turn wrap in fully automatic. Exit 0.
- The real module is used only by hand. The user's TEST pack (`Documents/Quartermaster/Rules/CNA-TEST-invented.qrules`) can be extended by the user, not by the builder.

## 11. Left to the builder
- Exact layouts, docking defaults and control choices, within the Windows-classic look of pieces 1–2b.
- How pending runs are stored, as long as replay is deterministic.
- Batch grid performance for about 1,200 rows: virtual mode is fine.
- Wording of messages.
