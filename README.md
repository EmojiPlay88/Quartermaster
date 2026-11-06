# Quartermaster

Quartermaster is a desktop engine for playing tabletop wargames, a small alternative to [VASSAL](https://vassalengine.org) aimed at the heaviest board games. It opens the VASSAL modules you already have and then takes the paperwork off your hands. It runs the game's procedures for you, does the arithmetic, rolls the dice, and keeps count of everything a big game asks you to track, from fuel and ammunition to whose turn it is.

> **Status:** early, hobby project, under active development. Map play, rules packs, formulas, ledgers and the tools below work. The turn sequencer, difficulty modes and online multiplayer are designed but not built yet (see [Roadmap](#roadmap)).

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
- Autosave keeps the last 10 snapshots per module, with File → Open Autosave… to recover one.
- Every change to the game is a logged command, so saves are deterministic and replayable.

## Content policy

**Quartermaster ships no game content.** There are no rules, tables, charts, maps or counters from any published game in this repository. You bring a VASSAL module you own, and you enter the rules data yourself in the Rules Workbench. Rules packs (`*.qrules`), modules (`*.vmod`) and saves (`*.vsav`) are gitignored. Please don't commit or share content you don't have rights to.

The tests use small, invented modules and rules packs.

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Microsoft Edge WebView2 Runtime (included with Windows 11), for viewing PDFs

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

- **Turn sequencer** that walks you through each phase of play.
- **Difficulty modes**, chosen at setup:
  - *Hardcore*: you look up every value yourself.
  - *Semi-automatic*: formula dialogs come with a cheat-sheet window of the relevant values.
  - *Fully automatic*: the engine runs the formulas at the right phase and writes every calculation to the log.
- Accessibility in the style of grand strategy games: tooltips that explain every number, alerts, an outliner.
- OCR for scanned rulebooks in the workbench.
- Hot-seat, live online multiplayer (host-authoritative, with chat in the log) and play by turn files.

## License

Quartermaster is licensed under the **GNU Lesser General Public License v2.1**; see [LICENSE](LICENSE).

Parts of the code are ported from [VASSAL](https://github.com/vassalengine/vassal) (LGPL-2.1). See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the list of ported files. Quartermaster is an independent project and is not affiliated with or endorsed by the VASSAL project.
