# Piece 2b: UI Organisation, Player Tools, Counter Palette, Settings — Design

*Date: 2026-11-06. Builds on pieces 1 and 2 (branch `piece2`). Parent spec: `2026-11-06-quartermaster-engine-design.md`. Piece 3 (turn walkthrough plus difficulty modes) comes after this.*

## 1. Intent

The user wants the program to feel like a complete tabletop client before the turn walkthrough arrives. That means four things:
- clearer organisation of reference material;
- the everyday tools VASSAL players rely on, such as dice, line of sight and notes;
- a drag-and-drop counter palette;
- a Settings window to customise everything.

The difficulty modes (hardcore, semi-automatic, fully automatic) the user asked for in the same request are **not** in this piece. They depend on the turn walkthrough firing formulas at the right phase, so they belong to piece 3. This piece does build the **log panel** that fully automatic mode will write to.

## 2. Goals, non-goals, anti-goals

**Goals**
1. The left panel splits into a **Charts** tree and a **Tables** tree, one above the other.
2. A docked **Log panel** at the bottom of the main window records dice, moves, placements, formula applications and ledger edits. It is filterable and persisted in the save.
3. Player tools:
   - dice;
   - notes;
   - line of sight and range;
   - overview mini-map;
   - map view tools;
   - inventory;
   - turn marker.
4. A **counter palette** that places new counters from the module's own counter definitions by drag-and-drop.
5. A **Settings** window, with the settings persisted and applied live.

**Non-goals**
- Difficulty modes, sequencer, alerts and tooltips (piece 3).
- Chat channels and multiplayer (pieces 4–5). The log panel's design leaves room for chat lines.
- Per-role tool visibility. Roles don't exist yet. This piece uses a "Game master mode" switch and per-tool toolbar toggles instead.

**Anti-goals**
- Tools that bypass the command log. Every game-state change, including dice results, placements and notes, must be a command, so replay, saves and future multiplayer keep working.
- Content baked into the engine (spec §2). The palette shows the user's module's own counter definitions at runtime, and nothing is copied into the repo.

## 3. Charts and Tables trees

- The left panel becomes a vertical split with two `TreeView`s, and the user can drag the splitter. Its position is saved in the global layout.
  - **Top: Module.** The existing tree (maps, org displays, charts, rulebooks and documents, setups).
  - **Bottom: Tables.** The open rules pack's tables, grouped as "Tables", plus a "Formulas" folder (read-only listing). Double-clicking a table opens the Rules Workbench on it; double-clicking a formula opens it in the Formulas tab.
- The user asked for "charts and tables" separately. **My reading:** the module's chart images stay in the top tree, and the user's own tables, from the rules pack, get the bottom tree. If the user meant splitting chart images from rulebook documents instead, it's a small change; this is flagged in §11.

## 4. Log panel

- It is docked at the bottom of the main window, can be resized, collapsed and popped out (WindowManager), and is shown by default.
- **Entries:** an `OrderedDictionary`-free, append-only list in `GameState.Log` of `LogEntry(long Seq, string Kind, string Text, string? Who, string? PieceId, string? MapId, DateTime At)`. Each kind has its own entry content:

  | Kind | Content |
  |---|---|
  | `dice` | the expression and results, e.g. "2d6 = 4 + 3 = 7"; for module special dice, the face texts |
  | `move` | "{piece} moved {fromLoc} → {toLoc}"; location names come from grids, falling back to coordinates |
  | `place` | "{piece} placed at {loc}" |
  | `delete` | "{piece} removed" |
  | `formula` | the working line from LedgerDelta |
  | `ledger` | manual edits with their note |
  | `note` | a public note change, summarised |
  | `turn` | turn marker changes |
  | `system` | anything else the engine reports |

- **Entries come from commands.** The reducer appends log entries when it applies commands, so they are deterministic and replay identically, and are saved with the game. NativeSave format 3 reads formats 1 and 2, which get an empty log.
  - Dice use the existing `RollDice` command, with a new optional `Expression` field. Formula and ledger entries reuse `LedgerDelta`.
  - Move, place and delete entries are derived from `MovePieces`/`AddPiece`/`RemovePiece`. Each of those gets an optional `LogText` that the issuing UI fills in, so the reducer stays free of grid lookups.
- **UI:**
  - a `ListView` (Details) with columns Time, Kind and Text;
  - a filter by kind (checkable toolbar buttons) and a text search;
  - auto-scroll unless the user has scrolled up;
  - "Copy", and "Export log…" (text/HTML) for after-action reports.
  - Double-clicking an entry that has a `PieceId` centres that piece's map on it.
- Formula and ledger entries reuse what piece 2 records; the ledger history stays as it is.

## 5. Player tools

All tools are reachable from a **Tools** menu and a **Tools toolbar**. Each toolbar button can be shown or hidden in Settings → Toolbar.

1. **Dice**
   - Toolbar buttons for **1d6** and **2d6**.
   - A **dice dropdown** listing:
     - the module's special dice, from `ModuleModel.Dice`, which reads the `SpecialDiceButton`/`SpecialDie` definitions;
     - "Custom…", an NdM roller with an optional modifier (+/−);
     - "Multi-roll…", several rolls in one go, e.g. 3 × 2d6.
   - Each roll executes a `RollDice` command. The authority rolls, as now, and results go to the log.
   - The last result also shows in the status bar for 5 s, depending on a setting.
   - Physical-dice mode (a setting) prompts for results instead of rolling.
2. **Notes**
   - A Notes window holding one public note text, saved in the game as a `SetNotes(Text)` command.
   - "Private notes" are deferred to multiplayer, where private means per player.
   - A note change is logged as "notes updated" without the full text.
3. **Line of sight / range**
   - A toggle tool on map windows. Drag from one point to another to draw a line.
   - The status bar shows the distance in grid cells: hex or square distance computed from the grid's snapped centres, plus a numbering label "0405 → 0912: 7 hexes".
   - The tool is UI-only and logs nothing.
   - Hex distance is computed using offset-to-cube conversion matching the grid's orientation and stagger, ported from VASSAL `HexGrid` where applicable.
4. **Overview mini-map**
   - A small dockable window showing the active map at the lowest tile level, with the current viewport rectangle.
   - Clicking or dragging pans the active map. It follows the active map window.
5. **Map view tools**, on the map window's toolbar or right-click on empty space:
   - **Highlight last moved:** outlines the pieces moved by the last move command, with the colour set in Settings.
   - **Hide all counters:** a toggle to read the map underneath; the toggle is per map window.
   - **Centre on selected.**
   - **Zoom presets:** 25 / 50 / 100 / 200 / Fit.
   - **Save map as picture…:** a PNG of the current view, or of the whole map at a chosen zoom capped at 8192 px on the long side. This is rendered from tiles and pieces with a progress bar.
6. **Inventory**
   - A window listing pieces grouped by a chosen key: name, map, any marker or property key (from `PieceInfo.Markers`), or a ledger property.
   - It shows counts and, for numeric ledger properties, totals.
   - A filter box; double-click centres on the piece. Read-only.
7. **Turn marker**
   - A status-bar field "Turn N · Phase …", with menu items Next turn, Previous turn and Set….
   - Changes are a `SetTurn(int Turn, string? Phase)` command and are logged.
   - Piece 3's sequencer will take this over.

## 6. Counter palette

- A dockable panel, by default on the right edge of the main window. It can be hidden and popped out, and is visible only when **Game master mode** is on (Settings, default off).
- **Source:** the module's `PieceWindow` definitions (piece-1 `ModuleModel.Slots` with their `NavPath`), shown as the module's tab, panel and list structure.
- **Display:**
  - a tree of folders on top, with a thumbnail list below;
  - each item is a picture rendered once by `PieceRenderer` at the slot's default state and cached, plus its name;
  - a search box filters all slots by name, case-insensitive substring.
- **Placing:**
  - Drag an item onto any MapWindow (docked or floating) and drop it.
  - On drop:
    - `PieceFactory.FromSlot` creates the piece, so the prototype-expanded type and state match piece-1/2 rules.
    - It snaps via `DropResolver`; dropping on a stack stacks the new piece.
    - One `AddPiece` command is issued with LogText "{name} placed at {loc}".
    - Ledger seeding runs for the new piece if a rules pack is open, through the existing `SeedLedgers`, which is idempotent.
- **Remove:** the existing Delete trait action stays. In Game master mode the map right-click menu also gets **Delete counter** for any piece, logged.

## 7. Settings

**Storage and lifetime**
- `%LOCALAPPDATA%\Quartermaster\settings.json`, written atomically. Unknown or corrupt values fall back to defaults with one diagnostic.
- There is a `Settings` model in Render, UI-free and testable, and a `SettingsChanged` event, so changes apply live where noted.
- The test root is injectable, following the same pattern as WindowLayoutStore and RulesMemory.

**Window:** a classic dialog with a `TabControl`. OK / Cancel / Apply; "Restore defaults" per tab.

| Tab | Settings |
|---|---|
| General | reopen the last module on startup (default off); autosave every N minutes, 0 = off, default 5, to `%LOCALAPPDATA%\Quartermaster\autosave\<sha>\`; confirm before deleting counters (default on); Game master mode (default off) |
| Display | show grid by default (module default / always / never); counter label font size; highlight colour for selection and last-moved; tab width (min/max) |
| Map | wheel zoom step (default ×1.2); snap to grid on drop (default on); drag threshold; stack hover popup delay (default 600 ms) |
| Dice & Log | physical dice default (off); show last roll in status bar (on); log move, place and delete entries (on); log timestamp format |
| Toolbar | a checkbox for each tool button: 1d6, 2d6, dice menu, notes, LOS, overview, inventory, turn, palette |
| Keyboard | a list of commands (open module, save, dice 1d6/2d6, LOS tool, hide counters, next tab, close tab, workbench, ledger…) with an editable shortcut and conflict detection; Reset |
| Folders | cache folder location (read-only display) plus "Clear tile cache" and "Clear document cache" buttons, with sizes shown |

Applied live: grid default, highlight colour, label size, toolbar visibility, shortcuts, and Game master mode (palette visibility). Autosave and the cache take effect immediately.

## 8. Commands added to the core

- `RollDice` gains an optional `Expression`. Old saves without it still load.
- `SetNotes(string Text)`.
- `SetTurn(int Turn, string? Phase)`.
- An optional `LogText` on `AddPiece`, `MovePieces` and `RemovePiece`.
- `GameState` gains `Log` (list), `Notes` (string) and `Turn` (int plus phase).
- NativeSave becomes format 3, backward-compatible.

## 9. Constraints

Everything from pieces 1 and 2 still applies:
- never crash on bad input;
- UI-thread marshalling, with queued lambdas re-checking disposal;
- all changes go through `GameSession.Execute`;
- invariant number display;
- classic gray WinForms look;
- new windows go through `OpenChild`/`WindowManager`, so they get tabs and pop-out;
- smoke mode must exit 0, with new screenshot flags for the palette, log, settings and tools;
- tests never write to the real `%LOCALAPPDATA%`;
- no real-module content in the repo.

## 10. Build order (suggested)

1. Core: the log, notes and turn commands; NativeSave format 3; LogText plumbing.
2. Log panel.
3. Charts/Tables split tree.
4. Dice tools (with special dice) and the turn marker.
5. Settings model and window, with live application.
6. Counter palette and Game master mode.
7. Line of sight and the overview mini-map.
8. Map view tools: highlight last moved, hide counters, zoom presets, save picture.
9. Inventory and notes.
10. End-to-end smoke on a fixture and on the real module (empty pack), plus the user checklist.

## 11. Calls made by the assistant (please check)

- "Charts and Tables" read as module charts versus the user's rules tables (§3).
- The log lives in the game state and is saved with the game, built from commands.
- Notes are a single public note. Private notes wait for multiplayer.
- The palette is visible only in Game master mode until roles exist.
- Autosave defaults to every 5 minutes.
- Tools selected from VASSAL's set: dice, special dice, multi-roll, notes, line of sight, overview map, highlight last moved, hide pieces, centre, zoom presets, save image, inventory and turn marker. Chess clock, random text button and decks are skipped (not used by the module, or not in scope).

## 12. Answers to the builder check (assistant's calls unless noted)

1. **How log entries are made.**
   - Every logged command carries `At` (UTC, set by the authority when the command is issued) and `Who` (player name; "local" for now).
   - The reducer builds the entry from the command alone, so it stays pure and deterministic.
   - `LogText` is optional: when it's null, the reducer writes a generic line from the ids (e.g. "piece {id} moved"). The UI fills it in with readable names and locations whenever it can.
   - The "log move/place/delete" setting only hides entries in the panel. The saved log is always complete.
   - `Seq` = log count + 1 at append time.
2. **Save format 3 is still a snapshot plus the command log, as before.** `GameState` gains `Log`, `Notes` and `Turn`.
   - The log is not capped; "Export log…" exists for long games. Size can be reviewed later if it becomes a problem.
   - `RollDice` gains optional `Expression` and `FaceTexts` (string list, for the module's special dice). The log entry uses `FaceTexts` when present, otherwise the numbers.
3. **Drag and drop already works across windows:** piece 1's map windows accept OLE drags from other windows, docked or popped out.
   - The palette adds a second drag data format for module counter slots, and `MapCanvas` accepts it on drop.
   - `PieceFactory.FromSlot` fills in all `AddPiece` fields. No ownership, side or layer fields exist yet.
4. **The workbench and rules model exist** (piece 2). Two small entry points need adding: `WorkbenchWindow.ShowTable(id)` and `ShowFormula(id)`.
   - The Tables tree refreshes on `RulesChanged` and clears when no pack is open.
5. **Centre on a piece opens or activates that piece's map window** through `OpenChild`, including popped-out ones, then centres it.
6. **Shortcuts are currently hard-coded on menu items.** The plan gives them their own task: a central command registry (command id → action, default shortcut, menu item). The Keyboard tab edits that registry, and the user's overrides are saved in settings.
7. **Game master mode is local visibility only, on purpose.** Commands carry no permission flag. Roles and enforcement come with multiplayer.
8. **Save map as picture** uses the existing `MapRenderer.Draw(SKCanvas, …)`, which already draws at any zoom and viewport. It renders into an offscreen surface, capped at 8192 px on the long side, with tiles taken from the matching pyramid level.
9. **Overview map:** every board already has a tile pyramid. Placeholder boards draw as their background colour, like they do now.
10. **Autosave** goes to `%LOCALAPPDATA%\Quartermaster\autosave\<module sha256>\`, keeping the last 10. There's no recovery screen; File → Open Game can browse that folder.
11. **Smoke flags** follow the existing `--screenshot-<thing> <png>` convention. The settings root is injectable through the same environment override as the layout root.
