# Piece 8 — Ledger images and tabs, app updater, release builds

*Design, 2026-11-08. Decided with the user in brainstorming. Items marked **(call)** are the assistant's own calls, not things the user chose; the user may overturn them.*

## Why

The user plays a big logistics game with ~1,170 units. Today the Ledger window is one long grid: holders and units mixed, every property as a column, and a unit name is the only way to tell which counter a row belongs to. The user's words: "add image support for the ledger so I can easily understand whose counter the stats are tied to, and splitting the ledgers by tabs so on one tab are the units, on other is some other stuff". They left the organisation to the assistant, then picked layout **C** from browser mockups (fixed tabs plus user-made tabs).

Second, the user wants an updater: "checks if there's a new version of the app so the user shouldn't install the app again". That needs versioned release builds, so the release pipeline (old resume item 8, first bullet) moves into this piece. The user ruled: **the relay server is its own download and does not ship with the app.**

## Goals

1. Each unit row in the Ledger shows the piece's counter image beside its name.
2. The Ledger is split into tabs: fixed tabs that work with no setup, and personal tabs the user defines (stored on their PC per module).
3. The app knows its version, checks GitHub Releases for a newer one, and updates itself in place on request.
4. A repeatable release build produces the app zip and separate relay downloads.

## Non-goals

- No macOS/Linux desktop app (still the separate Avalonia project, resume item 8).
- No auto-update for the relay server. Relay docs say how to update it.
- No code signing (costs money; SmartScreen may warn on first download — documented in README).
- No delta/patch updates: the whole zip is downloaded.
- No real-game content anywhere: test tabs and fixtures stay invented and labelled TEST.

## Anti-goals (would be failure even if it "works")

- An image or tab that leaks hidden information (a piece, value or image the current player may not see under `PlayerView`).
- The updater touching saves, modules, rules packs, settings or autosaves.
- The updater running mid-game, or replacing files without the user clicking Update.
- An update that leaves the user with no working app (a failed update must roll back).
- The app zip containing the relay, the user's module, rules packs or personal paths.

## Part A — Ledger images

- **Where:** a small counter image at the left of the Name cell of every unit row, in every tab. Holder rows (Global, depots) have no image.
- **What is drawn (call):** the piece's current look, as the map draws it (same `PieceRenderer` as map and `PaletteThumbs`: layers, flipped/reduced sides, markers that change the image), fitted into **64×48 px** (user: row images should be big, "not when I hover, but when I'm not hovering"; same size as palette thumbnails). Not the palette's original slot image.
- **Row height (call):** rows grow to fit the image (~52 px). A toolbar toggle **Show counters** (on by default) switches back to today's compact rows; remembered per module.
- **Hover preview (call, kept as an extra):** hovering the image shows a larger preview, about 200 px on its long side (at least 2× the counter's native module size; capped to fit the screen), rendered fresh at that size so it stays sharp, unit name under it. Hovering the rest of the cell keeps today's breakdown tip.
- **Double-click (call):** double-clicking the image selects and centres the piece on its map (same as outliner click-through).
- **Performance:** images render lazily for visible rows only, on a worker thread, cached by a key of piece type + trait state + size; cache invalidated when the piece's state changes. Scrolling 1,170 rows must stay smooth; a placeholder box shows until the image is ready.
- **Also gets images (call):** the batch grid (one row per unit) and the outliner dock unit rows, same renderer and cache.
- **Hidden info:** rows come from `_s.View` (the `PlayerView`) as today. A piece the player cannot see has no row, so no image. Images are drawn from the viewed piece's state, never the full state.

## Part B — Ledger tabs

### Fixed tabs (always present, in this order)

| Tab | Rows | Columns |
|---|---|---|
| **Units** | pieces (as today, with "Show units without values") | properties with scope Pieces or Both |
| **Depots & Global** | Global + every holder | properties with scope Holders or Both |
| **Everything** | today's full list | all properties |

- The tab label shows the row count, e.g. "Units (1,170)".
- **Units** tab toolbar adds **Side** (All sides / each side from the rules pack's sides; unit filter as the sequencer uses) and **Group by** (Map / Side / None). Grouping shows bold group header rows, as in the mockup; group rows are not editable and are skipped by CSV export (call).
- In hot-seat or online, the Side dropdown lists all sides but rows still come from `PlayerView`, so other sides' private units stay hidden (call: keep the dropdown simple rather than hiding sides).

### User tabs (personal, stored on this PC per module)

- **User's ruling: user tabs are personal**, not part of the rules pack. Each player organises their own Ledger. They are stored with the per-module UI settings (keyed by module, like the launcher's per-module rules pack choice), never in the rules pack, save or turn file. So editing tabs does not change the rules pack hash, does not make saves say "different rules pack", and is not sent to online guests. No rules pack format bump.
- Editor: **Edit tabs…** dialog, opened from a "+" at the end of the Ledger's tab strip and from a toolbar button (call: a dialog off the Ledger rather than a Workbench tab, because the Workbench edits the shared pack). A list of tabs with Add / Remove / Move up / Move down, and an editor for the selected tab:
  - **Name** (required, unique among this module's tabs).
  - **Unit filter**: a boolean expression in the existing expression language, evaluated per piece like user alerts (e.g. `Type == "Pieces" && Fuel < 5`). Empty means all units. A broken expression shows the parse error inline (as the Alerts tab does) and the tab shows no rows with a one-line error banner in the Ledger.
  - **Which pieces a filter runs over (call):** the same piece set as the Units tab: pieces with ledger values, plus pieces matching a Pieces/Both property's `AppliesTo` when "Show units without values" is on. Not every piece in the view (markers, terrain and chits stay out).
  - A property named in a filter or column that no longer exists in the current rules pack: the column is dropped silently, the filter shows its error banner (call).
  - **Columns**: a checklist of properties, in the order shown; Move up/down to reorder. Empty means the same columns as the Units tab.
  - **Include depots & Global**: checkbox (off by default). When on, holder rows appear above the units.
  - **Notes**: optional, shown as the tab's tooltip.
- User tabs appear after the fixed tabs with a small star, in the order set in the dialog. Saving the dialog updates the open Ledger immediately; several Ledger windows for the same module all update.
- Columns are stored by property id; a rules pack change (`RulesChanged`) re-resolves them.
- Visibility: a user tab never widens what is visible; its rows are a filter over `PlayerView` rows, and its filter expression is evaluated against the viewed state (call: a private property used in a filter reads as missing for a player who cannot see it, as alerts already do).

### Shared behaviour

- Filter box, sort by header click, cell editing (with its required note), breakdown tips and header-note tips work in every tab as they do today.
- **Export CSV** exports the current tab's rows and columns.
- **Remembered per module (call):** last selected tab, Show counters, Side and Group by choices, column widths per tab. Stored with the app's per-module UI settings, not in the save or the rules pack.
- The model stays UI-free: extend `LedgerTable` in Core (or a new `LedgerTabs` class) with `Tabs(pack)` and `Rows(tab, view, pack, module, filter, side, includeEmpty)`, so tabs, columns, grouping and filtering are unit-tested without WinForms.

## Part C — App version and updater

### Version

- One version number for the app, set in `Directory.Build.props` (`<Version>0.1.0</Version>`), shown in Help → About and in the launcher footer. Semantic versioning; first release `0.1.0` (call).
- The join handshake (`JoinRequest` and the host's welcome) carries the app version as well as the existing `ProtocolVersion`, end to end through the relay unchanged. A guest with a different app version sees a warning ("Host runs 0.2.0, you run 0.1.0 — update first if anything misbehaves") but stays in; a different `ProtocolVersion` still refuses as today. Call: the lobby listing may also carry the app version as an optional field for a pre-join warning, but an old relay that drops it must not break anything — then the warning simply comes after joining. Turn files record the app version that wrote them; opening one from a different version shows the same kind of warning (call).

### Where updates come from

- GitHub Releases of the public repo `EmojiPlay88/Quartermaster`, read via the unauthenticated REST API (`/repos/{owner}/{repo}/releases/latest`). Only releases with tag `quartermaster-v<semver>` count. Pre-releases ignored (call). **Call:** there are no separate relay releases: each `quartermaster-v<ver>` release holds the app zip and the relay downloads as separate assets, all on one version, so `/releases/latest` is always an app release. The updater picks only the app zip and its `.sha256`.
- **Test feed (call):** a hidden override (environment variable `QUARTERMASTER_UPDATE_FEED`) points the updater at a local folder holding a `release.json` in the GitHub API shape plus the asset files. Used by tests and the hands-on update test, so no fake test release is ever published to real users. The GitHub host allow-list applies to network feeds only.
- The release must carry the app zip `Quartermaster-<version>-win-x64.zip` and `Quartermaster-<version>-win-x64.zip.sha256`. A release without both is treated as "no update".
- Requests use a generic User-Agent (`Quartermaster-Updater/<version>`), no personal data.

### When it checks

- On launcher start, at most once per 24 h, in the background, never blocking the launcher. Help → **Check for updates…** checks now, whatever the timer.
- Settings → General: **Check for updates on start** (on by default). Off means only the manual check runs.
- Offline / API error / rate limit: silent on the automatic check (one line in the app log); the manual check shows a plain message.
- Never checks or prompts inside a game window (call: the launcher is where updates happen).

### What the user sees

- Launcher shows a yellow bar: "Quartermaster 0.2.0 is available. [What's new] [Update now] [Later] [Skip this version]".
  - What's new: the release notes (Markdown body shown as plain text in a dialog).
  - Later: hides the bar until the next launch.
  - Skip this version: never offers this version again (stored in settings); a newer one is offered.
- **Update now:** progress dialog (download with cancel) → verify SHA-256 → if any game windows are open they must be closed first (the launcher only offers Update when no game is open; call) → app exits → helper applies → app restarts into the launcher, showing "Updated to 0.2.0".

### How it applies (Windows)

- The app runs from its own folder (a self-contained publish). The updater:
  1. Downloads the zip to `%LOCALAPPDATA%\Quartermaster\Updates\<version>\`, checks size and SHA-256 against the `.sha256` asset.
  2. Extracts it there into `staged\`, and checks the staged folder contains `Quartermaster.App.exe` whose file version equals the release version.
  3. Copies a small helper (`Quartermaster.Updater.exe`, shipped in the app zip) out to the updates folder and starts it with: app folder, staged folder, app process id, version.
  4. App exits. Helper waits for the process to end (timeout → abort, nothing changed), renames the app folder's contents to `<app>\..\Quartermaster.backup-<oldversion>\` (or moves the whole folder; builder's choice), moves staged files in, starts the new app with `--updated-from <old>`.
  5. New app, on `--updated-from`, confirms it started (writes a marker); the helper, waiting ~30 s for that marker, rolls back to the backup if it does not appear or the process dies immediately, and restarts the old version with a message.
  6. The last backup is kept; older backups and downloaded zips are deleted on the next successful start.
- If the app folder is not writable (e.g. installed under Program Files), the updater says so plainly and offers to open the release page instead of trying elevation (call: no UAC elevation).
- Data never lives in the app folder: saves, modules list, rules packs, autosaves and settings are in Documents / `%LOCALAPPDATA%\Quartermaster` as today. The builder must verify nothing writes into the app folder before relying on the swap.

### Security

- HTTPS only, from `api.github.com` and the GitHub release-asset download hosts; redirects allowed only to GitHub hosts.
- The SHA-256 check guards against corrupt downloads. It does not protect against a compromised GitHub account (no signing); documented as a known limit.
- Zip extraction rejects entries with absolute paths or `..` (zip-slip).

## Part D — Release builds

- A script (`build/release.ps1`, call) that, given a version:
  - runs the tests;
  - publishes the app: `dotnet publish src/Quartermaster.App -c Release -r win-x64 --self-contained`, plus the updater helper into the same folder; zips it as `Quartermaster-<version>-win-x64.zip` and writes the `.sha256`;
  - publishes the relay **separately** for linux-x64, linux-arm64, osx-arm64 and win-x64 (self-contained, single file), each as its own zip/tar with `.sha256`, named `QuartermasterRelay-<version>-<rid>.*`;
  - checks every archive: no personal paths (no user names, no `C:\Users\`), no `.vmod`, no `.qrules`, no `startapp.cmd`, the app zip contains no relay binary;
  - prints the `gh release create quartermaster-v<version> …` command but does not run it.
- Creating the GitHub release is outward-facing: the assistant asks the user before running it, every time.
- `docs/relay-setup.md` gains a "Download a relay binary" section next to Docker, and how to update it (`docker pull` / replace the binary).
- README gets an Install/Update section and the SmartScreen note.

## Testing

- Core/Render unit tests on invented fixtures: tab definitions, columns per tab, filtering by side and by user-tab expression, grouping, CSV per tab, PlayerView filtering (a private unit never appears in any tab), personal tab settings round-trip, rules pack hash unchanged by tab edits.
- Image cache: key changes when trait state changes; hidden pieces never rendered.
- Updater logic (version compare, tag filter, asset selection, sha check, zip-slip rejection, skip-version, 24 h throttle) tested against a fake HTTP handler; no network in tests.
- Helper swap and rollback tested in a temp folder with fake exes (a stub that writes the marker, one that does not).
- Smokes: `--screenshot-ledger-tabs <png>` (Units tab with counters, plus a user tab) and `--screenshot-update-bar <png>` (fake "update available").
- Hands-on for the user: open Ledger, switch tabs, make a "Supply" tab with Edit tabs…, toggle counters, hover an image; then an update from a `0.1.0` build to a `0.1.1` build served from the local test feed (`QUARTERMASTER_UPDATE_FEED`). Publishing the real `0.1.0` release waits for the user's OK.

## Left to the builder

- Exact control choices (TabControl vs tab strip in the existing `DocumentTabStrip` style), image cell painting technique, cache size limit.
- Whether the helper is a separate tiny project or a mode of the app exe (separate project preferred so it can replace the app's files).
- The JSON shape for personal tabs in the per-module settings.
- Where per-module UI settings live, following the existing settings code.
