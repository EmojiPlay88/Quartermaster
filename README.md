# Quartermaster

Quartermaster is a desktop engine for playing tabletop wargames, a small alternative to [VASSAL](https://vassalengine.org) aimed at the heaviest board games. It opens the VASSAL modules you already have and then takes the paperwork off your hands. It runs the game's procedures for you, does the arithmetic, rolls the dice, and keeps count of everything a big game asks you to track, from fuel and ammunition to whose turn it is. Go ahead and close that spreadsheet. It's been open since the last campaign and it deserves a rest.

> **Status:** early, hobby project, under active development. Map play, rules packs, formulas, ledgers, the turn sequencer, difficulty modes, undo and step back, and the readable-at-a-glance tools below work. Hot-seat play with several players and live online play work; the rest of the [Roadmap](#roadmap) is not built yet.

## What it does

**Open your own VASSAL module**
- Reads `.vmod` files directly: boards, grids (hex, square, zoned), counters with their traits, prototypes, setups and saved games (`.vsav` import).
- The map is drawn with SkiaSharp, with tiled boards, smooth zoom and SVG counters.
- Drag stacks and multi-selections. Formations keep their shape while you drag them.
- Rulebook PDFs and chart images in the module open in tabs, browser style.

**Rules packs: your rules, your data**
- A rules pack holds the tables, formulas and per-unit properties for a game. The **Rules Workbench** has an editor for each, with the module's rulebook open beside it.
- **Formula dialogs** show the formula itself with a text box in place of each variable. The result calculates as soon as every box is filled. You can type values in or pull them from tables and ledgers.
- **Ledgers** track per-unit values (fuel, ammo, stores and so on) as spreadsheets, and every change is logged.

**Rulebook search and scans (Rules Workbench)**
- *Search:* the search box above the rulebook (or Ctrl+Shift+F) looks through every rulebook PDF in the module. Words match from the start, so "supp" finds "supply". Results are grouped by rulebook and show the page. When the text looks like a rule heading, the result says "Probably defined on page N". **Find in rulebook** jumps to the hit, and **Use as rule ref** fills in a rule reference from it. Rule references on tables, formulas and phases are clickable and open the rulebook at that page. A "Used on this page" list shows which tables, formulas and phases point at the page you are reading.
- *Scans and OCR:* **Open scan…** opens a picture or scanned PDF, and **Scan this page** reads the page of the rulebook you are looking at. The Images list hides small images (icons, bullets) unless you ask for all of them. Drag a box around a table, press **Read text**, and the result appears as a grid beside the image. Digit clean-up fixes common misreads (an O in a number, an l for a 1). Single characters, such as die-roll keys, are read again separately, and those cells are tinted so you can check them. **Import into table…** sends the grid through the same Paste dialog as any other table, and **Copy text** puts it on the clipboard. OCR is not perfect: check the grid against the image before importing.
- Everything runs on your PC and nothing is uploaded. OCR uses the Windows OCR engine, so it needs a Windows OCR language (Settings → Time & language → Language). Scans opened with **Open scan…** only last for the session, and a rule reference to one stores just the file name.

**Turn sequencer and difficulty**
- Phases, sides and repeating blocks come from the rules pack and are edited in the Rules Workbench.
- A tracker strip shows the turn, stage, acting side and phase. The phase panel lists instructions, warnings and the decisions still open. Next Phase warns about open decisions but never blocks you.
- Three difficulty modes, chosen at setup and switchable later:
  - *Hardcore*: you look up every value yourself, and typed values are checked.
  - *Semi-automatic*: formula dialogs come with a cheat-sheet window of the relevant values.
  - *Fully automatic*: the engine runs the formulas at the right phase and on moves, and writes every calculation to the log.
- A wrong value never reaches a ledger. Formulas that run per unit open as one batch grid.

**Undo and step back**
- Undo and Redo (Ctrl+Z, Ctrl+Y) work on whole actions: a drag, a Next Phase with everything it triggered, a formula Apply. The Edit menu and toolbar name the action, for example "Undo move of 3 pieces".
- Step Back returns to the start of the current phase, or to any earlier phase or turn in a list that shows what each step would undo. Dice rolled after that point are undone too, so rolling again gives new results. The log keeps a "Stepped back" line.
- Both rewind by replaying the game's command log, so the state after an undo is exactly the state you had.
- Autosave keeps the last 20 snapshots per module, plus the first autosave of each turn. A save made before this feature loads normally; undo just starts at the point you loaded.

**Readable at a glance**
- Hover any number in a ledger, the inventory, a unit popup, the outliner or a batch grid to see where it came from: the value at the start of the turn, then each change with its formula, rule reference and phase. It only ever shows history, so Hardcore stays hard.
- An alert bar under the toolbar shows what needs attention, as red, amber and blue chips. Built-in alerts cover negative values, missing inputs, pending calculations and phase warnings. You can write your own in the Rules Workbench, for example `Fuel < 50`. Click a chip to select the pieces it points at, right-click to hide it for the turn.
- A "what now" line says what the current phase expects of you.
- An outliner lists units by map or organisation display with totals for the properties you choose (sum, minimum or average), and marks units that have alerts.
- A short interface tour starts the first time you open a game, and is always available from Help.
- Notes can be attached to properties, formulas, tables, phases and alerts, and show as tooltips: on column headers, the formula dialog's title area, phase chips and the phase panel heading.

**Play with others**
- *Hot-seat, available now:* two or more players share one computer. Set the players and which sides each one holds on the new-game page, or later in Settings → Game → Players.
- When the turn passes to another player, a handover screen hides every window ("Pass to Anna (Red)") until they press Continue. Until then the game shows only public information.
- Each player sees only their own side's secrets. In the Rules Workbench's Visibility tab you mark which properties are private, which organisation displays and maps belong to a side, and which formulas roll their dice in private. Other players see "Red ledger changed" or "Red rolled dice" in the log, never the numbers.
- Hot-seat is honour-based: the game hides things on screen, but everything is still in memory, and formula dialogs can show a held unit's inputs in full.
- *Live online play, available now:* open **Online lobby ▶** in the launcher. **Join…** takes a game from the list (or a code), **Create lobby…** starts a new game for others to join, and **Host a saved game…** continues one of your saves online. Games go through a relay that you run yourself, in Docker on any server you control; see [docs/relay-setup.md](docs/relay-setup.md).
- Give the lobby a password and the game is end-to-end encrypted, so the relay cannot read it. An open lobby (no password) can be read by the relay.
- The host's copy runs the game. Guests send what they want to do and receive only what their own sides may see, never another side's private values. Only the host can undo, step back and save; a guest can use File → Export Viewer Copy… to keep a copy of their view. If a guest drops, their seat is kept and they rejoin where they left off. Chat appears in the log.
- *Play by turn files, available now:* for players who are not online at the same time. **Game → Send Turn…** saves your turn as a `.qturn` file for the other player; they open it with **File → Open Turn File…** (or **Open turn file…** in the launcher's Play offline page) and step through it with **Next**, **Play all** and **Stop**, so they see what you did before they take over. Dice results are in the file, so nothing is rerolled.
- Optionally the turn goes through a mailbox on your own relay instead of e-mail or chat. Pick the relay (and, if you like, a password) in the Send Turn dialog; the file is encrypted before it leaves your computer, so the relay only stores unreadable data for up to 30 days. Quartermaster checks the mailbox when you open a game and every five minutes, and shows "New turn waiting from {player}". See the mailbox section of [docs/relay-setup.md](docs/relay-setup.md).
- Turn files carry the whole game, so hidden information is only hidden on screen: anyone with the file could extract it. Use them with people you trust.

**Tabletop tools**
- Dice: 1d6, 2d6, custom expressions (`3d6+2`), special dice and multi-rolls. Every roll goes into the log.
- Log panel: filter by kind and export. Double-click an entry to centre the map on that piece.
- Line of sight and range in hexes or squares, an overview minimap, hide counters, and highlight the last move.
- Notes and an inventory window. Save the map as a picture at 25–100%.
- Game master mode adds a drag-and-drop **counter palette** for placing any piece from the module.

**Comfort**
- Every window can pop out onto another monitor and dock back.
- Separate Charts/Module and Tables trees.
- A Settings window: display, map, dice, toolbar, rebindable keyboard shortcuts with conflict checks, cache folders.
- Autosave keeps the last 20 snapshots per module plus the first of each turn, with File → Open Autosave… to recover one.
- Every change to the game is a logged command, so saves are deterministic and replayable.

## Content policy

**Quartermaster ships no game content.** There are no rules, tables, charts, maps or counters from any published game in this repository. You bring a VASSAL module you own, and you enter the rules data yourself in the Rules Workbench. Rules packs (`*.qrules`), modules (`*.vmod`) and saves (`*.vsav`) are gitignored. Please don't commit or share content you don't have rights to.

The tests use small, invented modules and rules packs.

## Requirements

- Windows 10 or 11
- Microsoft Edge WebView2 Runtime (included with Windows 11), for viewing PDFs
- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) only if you build from source; the release download is self-contained

## Install / Update

1. Download `Quartermaster-<version>-win-x64.zip` from the [Releases page](https://github.com/EmojiPlay88/Quartermaster/releases) and unzip it into a folder you can write to, for example `%USERPROFILE%\Quartermaster`. Not inside `Program Files`: the updater needs to replace the files in that folder.
2. Run `Quartermaster.App.exe`.
3. **SmartScreen:** Windows may say "Windows protected your PC" because the program is new and unsigned. Choose **More info**, then **Run anyway**. There is no code signing. The `.sha256` file next to each download only guards against a corrupt download; it does not prove who made it. If you want to be sure, build from source (below).
4. **Updates:** the launcher checks GitHub for a newer release about once a day and shows an update bar. **Update now** downloads the zip, checks it against the release's `.sha256`, closes Quartermaster, swaps the files and starts the new version. If the new version does not start, the old one is put back. Only the files named in the release's `quartermaster-files.txt` are swapped, so anything else you keep in that folder is left alone; the previous version is kept as one `Quartermaster.backup-<version>` folder beside it (older backups are removed after a successful update). Updating never happens while a game is open, while another Quartermaster window is running, or without you clicking. The launcher's **Check for updates** link checks on demand, and **Skip this version** hides one release. Your modules, rules packs, saves and settings live outside the program folder and are not touched.
5. To update by hand, unzip the new zip over the old folder, or into a new one.

The online-play relay is a separate download (`QuartermasterRelay-<version>-<platform>`), only needed by whoever hosts games; see [docs/relay-setup.md](docs/relay-setup.md).

Maintainers: `build/release.ps1 -Version <version>` builds, checks and checksums the downloads into `artifacts/release/<version>/` and prints the `gh release create` command; it creates nothing itself.

## Getting started

1. Start Quartermaster. The launcher opens with your module list. Use **Add module…** to list a `.vmod` file (the file is not copied or changed; Remove only takes it off the list).
2. Pick a module. The line under the list shows its rules pack; **change…** lets you point it at another one.
3. Press **Play offline ▶**. From there, **Continue** reopens the newest autosave, the recent games list reopens a saved game, **Open saved game…** browses for a file, and **New game…** opens the setup dialog (choose a start, difficulty and players).
4. **File → Back to launcher** closes the game (asking about unsaved changes as usual) and returns to the module list. Closing the window ends the program.

**Online lobby ▶** opens the lobby window for the selected module (Relay: field, `Join…`, `Create lobby…`, `Host a saved game…`). Opening a module from the command line, or with File → Open Module, skips the launcher.

## Build and run

```powershell
git clone https://github.com/EmojiPlay88/Quartermaster.git
cd Quartermaster
dotnet build Quartermaster.slnx
dotnet run --project src/Quartermaster.App -- "C:\path\to\YourModule.vmod"
```

Useful command-line options:

| Option | Effect |
|---|---|
| `<module.vmod>` | Open this module on start |
| `--new` / `--setup <n>` | Start a new game, or load setup number `n` |
| `--rules <path>` | Load a rules pack |
| `--new-rules <name>` | Create a new, empty rules pack |
| `--gm` | Game master mode for this run (counter palette) |
| `--smoke <seconds>` | Self-test run that exits with code 0 on success |

Run the tests:

```powershell
dotnet test Quartermaster.slnx
```

## Project layout

| Project | Role |
|---|---|
| `Quartermaster.Vassal` | `.vmod` parsing: archive, build file, traits, grids (partly ported from VASSAL) |
| `Quartermaster.Rules` | Rules packs and the formula expression language |
| `Quartermaster.Core` | Game state as a command log with a deterministic reducer; saves and `.vsav` import |
| `Quartermaster.Render` | UI-free models: module session, map rendering, settings, palette, export |
| `Quartermaster.App` | The WinForms application |

Design documents and implementation plans are in [`docs/superpowers`](docs/superpowers).

## Roadmap

Nothing is planned beyond what is built; the next piece is whatever the user picks.

## License

Quartermaster is licensed under the **GNU Lesser General Public License v2.1**; see [LICENSE](LICENSE).

Parts of the code are ported from [VASSAL](https://github.com/vassalengine/vassal) (LGPL-2.1). See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the list of ported files. Quartermaster is an independent project and is not affiliated with or endorsed by the VASSAL project.
