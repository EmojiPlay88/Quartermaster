# Quartermaster — Design

*Working name for the engine (builder's call to keep or change). Date: 2026-11-06.*

## 1. Intent

The user wants to play *The Campaign for North Africa* (CNA), the famously huge logistics-heavy board wargame, as a digital game. They have never played it. They own a VASSAL module for it (`CNAv2.1.0.vmod`, ~99 MB, in their Downloads folder) that contains the map, counters, organisation displays, chart images, scenario setups and the rulebooks as PDFs.

VASSAL shows pieces but automates nothing. The user wants:

- the machine to **track fuel, water, ammo and stores** and **calculate every formula**;
- each calculation shown as a **dialog with the formula visible and text boxes in place of the variables**, computing automatically once all boxes are filled (layout "C", §7.3);
- the game to **walk them through each phase automatically**;
- **hot-seat and online multiplayer**;
- a **Windows-native, gray, dense interface** with the feel of *Rule the Waves 3*;
- accessibility comparable to **Paradox grand strategy games (HOI4)**: tooltips that explain every number, alerts, an outliner, and a sense that the game tells you what to do next.

Success, in the user's words: "a fully working tabletop wargame that's very accessible for new players."

## 2. Content boundary (hard constraint)

The engine is generic. **It ships no CNA content and no CNA-derived content: no rules text, tables, formulas, sequence of play, map, counters or chart data are written into the code, tests, samples or docs.**

- Map, counters, org displays, charts, setups and rulebook PDFs are read at runtime from the user's own `.vmod`.
- Rule logic (sequence of play, tables, formulas, alert conditions) lives in a **rules pack** that the **user** writes, using the engine's editors (§8). The builder must not populate a rules pack from the module's PDFs or chart images.
- Online play never transmits module or rules-pack content. Each player must have identical files locally, which is checked by hash (§11).
- Test fixtures use a small synthetic module and rules pack written for testing (fictional pieces and made-up formulas), never the CNA module.

Consequence the user accepted: CNA becomes automated progressively as they enter its rules. Steps that haven't been entered yet still work as manual checklist steps (§9.4), so the game is playable from day one.

## 3. Goals, non-goals, anti-goals

**Goals**
1. Open any VASSAL module that uses the trait subset in §6.3, and the CNA module completely.
2. Data-driven automation: ledgers, lookup tables, formulas, dice, and a guided turn sequencer.
3. Layout-C formula dialogs plus batch summary grids, so hundreds of units don't each need a dialog.
4. HOI4-style accessibility layer.
5. Hot-seat, live online play (host-authoritative), and asynchronous turn files.
6. Rules workbench: rulebook PDF beside the editors, table paste, OCR for scans, live formula testing.

**Non-goals**
- Cross-platform support (Windows 10/11 only).
- Editing VASSAL modules or writing `.vmod` files.
- Full VASSAL trait coverage (§6.3 lists the subset).
- AI opponent.
- A relay or matchmaking server.
- Distribution or publishing.

**Anti-goals** (failure even if the program works)
- Any CNA content baked into the engine (§2).
- A modern, flat, web-app look. It must look like a real Windows desktop program.
- Burying the player in a pop-up for every unit every phase.
- Numbers the player can't trace. Every computed value must be explainable on hover.
- Losing a game state: a crash or disconnect must never cost more than the current step.

## 4. Stack (decided with the user)

- **C# / .NET 10 (LTS), WinForms.** (Changed from .NET 8 during planning: .NET 8 support ends 2026-11-10; .NET 10 SDK and runtime are installed.) Real Win32 controls, MDI.
- **SkiaSharp** (`SKGLControl` or `SKControl`) for the map and org-display surfaces only.
- **Svg.Skia** to render SVG counters, rasterized and cached per zoom level.
- **WebView2** to view PDFs at a given page (`file.pdf#page=N`).
- **Windows.Media.Ocr** (WinRT, via the `net10.0-windows10.0.19041.0` target framework) for OCR in the rules workbench.
- **System.Text.Json** for rules packs and saves.
- **xUnit** for tests.

UI look: default system colors and controls, Tahoma 8pt / MS Sans Serif-style dialogs, gray 3D borders, MDI parent with toolbar, menu, docked panels, and status bar. Use `Application.SetHighDpiMode(PerMonitorV2)`. No custom theming beyond what's needed for the alert bar and the auto-filled (yellow) input boxes.

## 5. Architecture

```
Quartermaster.sln
  src/Quartermaster.Core      – game state, command log, expression language, tables, sequencer (no UI, no I/O)
  src/Quartermaster.Vassal    – .vmod reader, buildFile parser, trait model, .vsav decoder
  src/Quartermaster.Rules     – rules-pack model, load/save, validation
  src/Quartermaster.Net       – host/client protocol, turn files
  src/Quartermaster.App       – WinForms UI
  tests/*                     – xUnit per project + synthetic fixtures
```

**State model: command log (event sourcing).** All changes to game state (move piece, set property, apply ledger delta, roll die, advance step, chat) are serializable `Command`s applied by a deterministic reducer in Core. This one choice gives us:
- undo (per-step checkpoints, §9.5),
- saves (snapshot plus log tail),
- multiplayer (clients send command requests and the host broadcasts accepted commands),
- async turn files (§11.4),
- an auditable game log.

Dice are commands whose result is generated by the **authority** (the local engine in hot-seat, the host online) and recorded in the log. Replay never rerolls.

## 6. Piece 1: Module reader and map

### 6.1 What the CNA module contains (inspected 2026-11-06, structure only)
- 2,279 zip entries: 1,642 SVG, 622 PNG, 4 `.vsav` (blank game plus three scenario setups), 3 rulebook PDFs (text-based, not scans), plus readme, errata, changelog, play notes, `buildFile.xml` (~2 MB) and `moduledata`.
- 5 `Map`s (main map plus 4 control sheets), 98 `WidgetMap`s (organisation displays, organised under 18 `TabWidget`s, 12 `PanelWidget`s, 4 `ListWidget`s), 15 `ChartWindow`s holding 236 PNG `Chart`s, 2,468 `PieceSlot`s, 1,185 `SetupStack`s, 324 `Zone`s, 312 `SquareGrid`s and 6 `HexGrid`s (all with numbering), 98 `ZonedGrid`s, 12 `PrototypeDefinition`s, 3 `SpecialDie` / 2 `SpecialDiceButton`s, 5 `PredefinedSetup`s, 3 `BrowserPDFFile`s, 1 `PlayerRoster`, 2 `PieceWindow`s.
- 42 pieces carry a `propertysheet` trait with fields Ammo / Fuel / Water / Stores. The module tracks supply by hand.

### 6.2 Loading
- Open the `.vmod` read-only as a zip. Never extract it into the program folder; cache rasterized images in `%LOCALAPPDATA%\Quartermaster\cache\<module-hash>\`.
- Parse `buildFile.xml` (or legacy `buildFile`) into a component tree that mirrors VASSAL's class names. Unknown elements are kept as opaque nodes and listed in a **Module Diagnostics** window, not silently dropped.
- Module identity is the SHA-256 of the `.vmod` file, used for caching, rules-pack binding, and the multiplayer check.

### 6.3 Supported piece traits (the CNA module's set)
`piece` (BasicPiece), `prototype`, `sendto`, `label`, `immob`, `propertysheet`, `placemark`, `clone`, `delete`, `mark`, `hide`, `PROP` (dynamic property), `rotate`, `obs` (obscurable), `replace`, `footprint`, `markmoved`, `return`, plus `emb2` (Layer/Embellishment — 687 slots, multi-image counters), `nonRect2` (non-rectangular shape) and saved-game `stack` entries (found during planning). Trait encoding follows VASSAL's `SequenceEncoder` format (`;` fields, tab-separated decorator chain, backslash escaping that nests per level). The builder should port the escaping logic from VASSAL's LGPL source rather than guess it. Unsupported traits load as inert decorators and show up in diagnostics.

### 6.4 Display
- **Map windows** are MDI children with a Skia surface. They support pan and zoom (mouse wheel, 25–400%), grid overlay toggle, coordinate readout in the status bar using the module's grid numbering, stack expansion on hover, and a counter detail viewer (VASSAL's mouse-over stack viewer).
- **Organisation displays** have one MDI child per WidgetMap, navigated by a tree mirroring the Tab/Panel/List widget hierarchy.
- **Chart windows** show image viewers grouped as in the module.
- **Rulebook** opens as an MDI child with WebView2. Every "Rule ref…" link opens `pdf#page=N`.
- **Piece interaction:** drag to move (snaps to grid), right-click menu built from traits (send-to, clone, delete, rotate, mark, …), and a property-sheet editor.

### 6.5 Saves
- Import `.vsav`: zip containing a `savedGame` entry, obfuscated by VASSAL's `ObfuscatingOutputStream` (header `!VCSK`, a hex key byte, then XOR'd bytes as hex). The decoded content is a VASSAL command string (commands separated by the escape character). The builder should confirm the details against VASSAL source. Support the command types that the CNA setup files actually use (add piece, move, change state, global property). Anything unsupported gets reported.
- Native saves use their own format (§5). There's no need to write `.vsav`.

**Acceptance for piece 1:** open `CNAv2.1.0.vmod`, load each of the 4 bundled setups, see every map, org display, chart, and PDF, and move and right-click counters, with no unexplained entries in Module Diagnostics.

## 7. Piece 2: Ledgers, tables, formulas, and the layout-C dialog

### 7.1 Ledgers
- Any piece can have **numeric properties** defined by the rules pack (e.g. `Fuel`, `Water`, …; names are user-defined). If a module `propertysheet` field has the same name, it's bound to the property, so imported setup values are used.
- Every change is a `LedgerDelta` command carrying source (formula id, step id), before value, after value, and a rule reference. This history powers the tooltip breakdown (§10.1).
- Ledgers are also attachable to **non-piece holders** defined in the rules pack (e.g. depots, side-wide pools, global properties).
- There's a **Ledger window**: a spreadsheet-like grid of holders × properties, with filter, sort, export to CSV, and inline editing (which creates a manual-adjustment command with a required note).

### 7.2 Tables
Table kinds:
- 1-D lookup,
- 2-D grid (row key × column key),
- **ranged** rows/columns (e.g. "3–5"),
- **dice tables** (key = roll result).

Keys may be numbers, ranges, or strings. Each table has a name, an optional rule ref (pdf + page), and notes. Tables are created in a grid editor with **paste from clipboard** (TSV/CSV, as copied from a PDF or Excel) and with OCR import (§8.3).

### 7.3 Formula language
- A small, safe expression language, **not** C# scripting.
- Literals, `+ - * /`, parentheses, comparison operators, `and/or/not`, and `if(c, a, b)`.
- Functions: `min max round floor ceil abs clamp`, `lookup(Table, row[, col])`, `dice(n, sides)`, `has(prop)`.
- Variables are named identifiers. Each variable in a formula has a **binding**:
  - `unit.<Prop>`: property of the piece being processed,
  - `global.<Prop>` / `holder(<id>).<Prop>`,
  - `table(<Table>, <row expr>[, <col expr>])`,
  - `terrain`: a value derived from the piece's location, using zone/region properties declared in the rules pack. The builder decides how (e.g. map region tagging or per-hex attributes painted in the workbench),
  - `input`: typed by the player, with an optional range or enum,
  - `roll`: dice (rolled by the authority, or typed if "physical dice" mode is on).
- A formula declares its **outputs**, each as a ledger delta (`unit.Fuel -= result`) and/or a value stored for later steps.

### 7.4 Layout-C dialog (chosen by the user from three mockups)
- Title: formula name and unit name.
- **Formula group box:** the formula is rendered as a line of text, with each variable replaced by a text box captioned underneath.
  - **Yellow** boxes are auto-filled from a binding.
  - **White** boxes are typed by the player.
  - Typing over a yellow box overrides it: the box turns white, and the override is shown in the working line and the log.
  - The result box updates live as soon as all inputs have values. There is no Calculate button.
- **Working group box:** the formula with values substituted, the result, and its effect on the ledger (`12 × 3 × 1.5 ÷ 2 = 27 → Fuel 140 − 27 = 113`).
- Buttons: **Apply to ledger**, **Cancel**, **Rule ref…**. A status bar shows "All inputs filled" or "Waiting for: …".
- Rendering is generated from the expression AST. Long formulas wrap. Nested `if` and `lookup` render as a sub-row with its own boxes.

## 8. Rules pack and rules workbench

### 8.1 Rules pack
- A folder or zip (`.qrules`) of JSON files containing tables, formulas, holders, property definitions, the sequence of play, unit filters, alert conditions, roles, hidden-info settings, and **rule references** (pdf name + page + short label written by the user).
- Bound to a module by its SHA-256, so the engine warns on mismatch.
- It's pure data. No executable code.
- Validation runs on load and live in the editors: unknown identifiers, type errors, unreachable steps, missing tables.

### 8.2 Workbench window
- **Split view:** a rulebook PDF (WebView2) on the left and an editor on the right (Tables / Formulas / Sequence / Filters / Alerts / Holders tabs).
- "Link to current page" fills the rule ref from the PDF viewer's current page. The builder decides how to read the page (WebView2 PDF viewer events, or a page number field).
- **Formula editor:** a text box with syntax check, a variable-binding grid, a **live preview** of the layout-C dialog, and a test panel for entering sample values or choosing a sample piece from the loaded game.
- A **coverage view** lists every step in the sequence and whether it's manual, partly automated, or fully automated.

### 8.3 Scans and OCR
- Scanned pages or image charts (e.g. the module's chart PNGs, or user photos) open in an image pane instead of the PDF pane.
- **Region select → OCR** (Windows.Media.Ocr) produces a text and grid preview **beside the image**. The user corrects it, then imports it into a table or a note.
- OCR output is never imported without the user confirming it.

## 9. Piece 3: Turn sequencer

### 9.1 Structure
A user-defined tree: Turn → Stage → Phase → Step (depth configurable; the three grouping levels are just labels). It's shown as a docked **Sequencer** panel. The current step is highlighted, finished steps are ticked, and there's a **Next ▶** button.

### 9.2 Step kinds
1. **Manual:** instruction text written by the user, a rule ref, an optional checklist, and **Done**.
2. **Calculation:** a formula, a **unit filter** (an expression over piece properties, side, map or zone location, `movedThisPhase`, etc.), and an order.
3. **Dice / event:** roll or table lookup, then the outcome is logged and can set globals or trigger a follow-up step.

Each step also has an **owning role** (§10.4, §11).

### 9.3 Batch execution
For a calculation step:
- Evaluate the filter, then compute every unit whose inputs are all bound.
- Show a **summary grid**: one row per unit with inputs, result, ledger before → after, and an **exceptions** column (values that go negative, alert conditions hit, overrides). Offer **Apply all**.
- Units needing typed input or a roll get a layout-C dialog, queued one after another, with "skip to grid" available.
- Double-clicking any row opens its layout-C dialog for inspection or override.

### 9.4 Coverage fallback
A phase with no steps entered yet shows as a single manual step with its rule ref (if set) and Done, so an incomplete rules pack still gives a playable, guided game.

### 9.5 Checkpoints and undo
Each step boundary is a checkpoint. **Step back** rewinds to the previous checkpoint, with confirmation; online, only the host can do this, and everyone is notified. Autosave happens at every checkpoint (keeping the last 20 plus one per turn).

## 10. Accessibility layer (HOI4-style)

1. **Number tooltips:** hovering any ledger value (in the counter viewer, ledger grid, or outliner) shows its breakdown, e.g. "Fuel 113: start of turn 140; −27 movement (step X, rule ref)". The data comes from the `LedgerDelta` history.
2. **Alert bar** along the top of the main window:
   - icons with counts,
   - built-in alerts (negative ledger value, pending input, unacknowledged event),
   - user-defined alert conditions (filter expression plus message plus severity) from the rules pack.
   - Clicking an alert cycles through and centers the matching pieces.
3. **Outliner** (docked): formations (from the org-display tree), with each formation's key ledger totals and alert icons, and pending decisions for the current step.
4. **"What now" banner** in the sequencer: the current step's instruction in plain words, plus who's acting.
5. **Interface tour:** a first-run guided tour of the UI (panels, dialog colors, sequencer, alerts). It teaches the engine, not CNA rules. It can be replayed from Help.
6. **Glossary / notes:** user-written notes attachable to any table, formula, or step, shown in tooltips.

## 11. Pieces 4 and 5: Multiplayer

### 11.1 Sides and roles
The rules pack defines sides (seeded from the module's `PlayerRoster`) and **roles** per side (e.g. logistics, air, front command; names are the user's). Each step has an owning role. A player can hold several roles, and solo play means holding all of them.

### 11.2 Hot-seat
- When the next step's owner is held by a different person, a full-window **handover screen** ("Pass to <player>: <role>") covers everything until they click Continue.
- Hidden-information settings in the rules pack decide which maps, windows, and piece properties are concealed from the side that isn't acting. VASSAL `obs`/`hide` traits are respected.

### 11.3 Live online
- **The host is the authority.** Others connect over TCP to `host:port` (default port is the builder's call), protected by a session password.
- Messages are length-prefixed JSON, and the protocol is versioned.
- **Handshake:** engine version, module SHA-256, and rules-pack SHA-256. A mismatch refuses the connection and says which file differs. Content is never transferred.
- **Flow:** clients send command requests, the host validates them (does this player own the acting role?), applies them, and broadcasts. The host also generates dice. Everyone sees counter moves and dialog inputs live (the inputs of a dialog being filled are broadcast as preview state).
- Chat and the game log appear in a docked panel.
- **Disconnects:** the host autosaves every checkpoint. A client that rejoins gets a snapshot plus log tail. If the host drops, the game resumes when the host reloads the autosave and players reconnect.
- **NAT:** no relay server. Document port forwarding or a virtual LAN (Tailscale / ZeroTier / Radmin VPN) in Help.
- Inbound data is never executed. Message size limits apply, and malformed input disconnects the client.

### 11.4 Async turn files
- After acting, a player exports a **turn file** (`.qturn`): a snapshot hash plus the command log since the last exchange, and the hashes of the module and rules pack. They send it any way they like.
- The receiver opens it, and the engine verifies the chain and applies it. The player continues until control passes to the other side, then exports.
- Dice in async mode: rolled locally by the acting player and recorded. A seeded, committed roll scheme to deter cheating is optional and can come later.

### 11.5 Social: chat and game log (requested by the user)
One docked **Chat & Log** panel. Chat and game events appear in one time-ordered feed, and all of it is stored permanently in the save.

**Feed contents**
- **Chat** messages, colored by side.
- **Dice rolls**: who rolled, what for (step / formula / unit), dice and result, and the table outcome, e.g. "🎲 Anna (Axis Air) rolled 2d6 = 9 for Weather → Clear".
- **Formula results**: the working line.
- **Ledger changes**: unit, property, before → after.
- **Step and phase changes**, handovers, and out-of-step actions (flagged).
- **System messages**: joins and leaves, reconnects, rules-pack warnings.

**Interaction**
- **Filters**: toggle chat, dice, calculations, ledger, steps, system. Text search.
- **Clickable entries**:
  - a unit name centers the map on it;
  - a calculation entry reopens the layout-C dialog read-only, showing exactly what was entered;
  - a rule ref opens the PDF page.
- **Channels**:
  - *All*;
  - *Side* (team chat, visible only to that side's players, and enforced by the host like other hidden information, §14a.5);
  - *Whisper* to one player.

  Game events follow the same per-side redaction as the rest of the state.
- **Presence list**: who's connected, their side and roles, whose step it is, a typing indicator, and an idle/away marker.
- **Map pings**: Alt+click drops a short-lived ping visible to your channel's audience, with a feed entry ("Ben pinged 2914").
- **Map annotations**: arrows and notes drawn on the map, visible to side or all. They persist until deleted and are logged.
- **Chat in hot-seat** works as a shared notebook. **In async turn files**, chat travels with the command log, so messages sent while it was your turn arrive with the turn.

**Storage and export**
- Chat and events are commands in the log (§5), so they're saved, replayed, and survive rejoin.
- Export the log to text or HTML, the whole game or a filtered range, e.g. for an after-action report.

## 12. Build order

Each piece gets its own implementation plan and is usable on its own.
1. Module reader and map (§6). Already a usable VASSAL-like player.
2. Ledgers, tables, formulas, layout-C dialog, and the workbench basics (§7, §8.1–8.2).
3. Turn sequencer, batch grids, undo, and the accessibility layer (§9, §10). OCR (§8.3) can come here or later.
4. Hot-seat (§11.1–11.2).
5. Online live (including Chat & Log, §11.5), then async turn files (§11.4). A local Chat & Log feed (no channels) already exists from piece 3, because the log is the command log.

## 13. Testing

- Core (expression language, tables, reducer, sequencer, undo) has thorough unit tests.
- Vassal: tests run against a **synthetic fixture module** built in the test project (a handful of pieces exercising every supported trait, a square grid, a hex grid, a zone, a chart, and a `.vsav` produced by VASSAL's obfuscation scheme).
- Net: loopback host/client tests that check hash refusal, role enforcement, and rejoin.
- Manual acceptance against the user's CNA module is done locally by the user and the builder. CNA files are never committed.
- The repo `.gitignore` excludes `*.vmod`, `*.vsav`, `*.qrules`, `*.qturn`, and `.superpowers/`.

## 14. Calls made by the assistant (not discussed with the user)

- Working name "Quartermaster", project layout, file extensions `.qrules` / `.qturn`.
- Command-log architecture.
- A custom expression language instead of scripting (keeps rules packs safe to share between players).
- Batch summary grid, with dialogs only when input is needed.
- No relay server; direct connect plus virtual-LAN guidance.
- Undo is host-only online.
- Autosave retention of 20 plus one per turn.
- The interface tour teaches the UI, not CNA.

## 14a. Answers to the builder check (assistant's calls)

1. **Movement cost.** A move command records the **path**: the list of grid cells, from drag waypoints or click-to-path. MP spent defaults to an `input` binding (the player types it). Optionally, the rules pack can define a **path cost formula** that sums per-cell and per-hexside values from terrain data painted in the workbench; if one exists, MP spent is auto-filled (yellow) and can be overridden. No automatic pathfinding in v1.
2. **Counters ↔ org displays.** The rules pack defines a **membership model**: any piece or holder can have a parent, and an org-display unit can be linked to a map counter (the formation counter). Ledgers live on whichever pieces the user defines them on. Parents show **rolled-up totals** (sum, min, or a custom aggregate per property). Filters can target counters or members, and a member inherits `movedThisPhase` and location from its linked map counter.
3. **Sequencer control flow.** Supported: repeat a node N times or while a condition holds, conditionally skip a node, and set a step's owner with an expression (e.g. from an initiative roll stored in a global). Steps always run **strictly one at a time**, with no parallel steps.
4. **Editing the rules pack mid-game.**
   - IDs in the rules pack are stable GUIDs; display names can be renamed freely.
   - A save stores the hash of the rules pack it last used. Replay applies recorded results and never recomputes, so editing a formula never changes history.
   - Loading a save with a different pack shows a warning listing changed or removed IDs, then continues.
   - Online, every player needs the same hash, so after editing the user has to give the new pack to the other players themselves and they reconnect. The engine never transmits it.

5. **Hidden information (user's decision: really enforced).**
   - **Online, the host filters each client's view.** Each client receives only what its side may see, as set by the rules pack's hidden-info settings and the `obs`/`hide` traits:
     - snapshots, broadcast commands and log entries are redacted;
     - hidden properties and dice are withheld;
     - the other side's hidden pieces arrive as "unknown piece at X", or not at all, according to the settings.
   - So the reducer needs a **per-side projection** of the state, and the protocol needs redacted command variants.
   - Known limitation, accepted: the host's own PC holds the full state. The host's UI hides information like any client's does, but a determined host could look. A "dedicated host" mode (headless host, every player connects as a client) removes this. It's optional and comes after piece 5.
   - Hot-seat and async turn files can't enforce secrets without a server, so there it's on-screen hiding only. The UI says so when such a game is created.
6. **Freedom outside your step (user's decision: free, like VASSAL).** Any player may move any piece they can see, edit property sheets, and make manual ledger adjustments at any time, outside the sequencer.
   - Every such action is logged with the player's name and flagged in the log as *out-of-step*.
   - The host can undo any single logged action, not just rewind to a checkpoint.
   - Out-of-step actions don't affect step completion. Checkpoints still sit at step boundaries.
   - Pieces hidden from a player can't be acted on by that player.

## 15. Left to the builder

The default port; how terrain values are bound to locations (§7.3); how to read the current PDF page from WebView2; the exact Skia control; rasterization cache strategy; and the internal JSON schemas.
