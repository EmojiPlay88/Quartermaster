# Port Stage 6c: Windows Feedback (install folder, docked log, map tabs, counter palette) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the four things the user hit when trying the Windows build with their module: choose the install folder in setup; the log always starts docked; only the main map opens and map tabs can be closed and reopened; the counter palette moves into the left column between the module tree and the tables tree.

**Architecture:** Avalonia app only (`src/Quartermaster.Desktop`), plus the Inno script. WinForms is untouched. Shared Render code changes only where noted (layout store additions).

**Tech Stack:** C# .NET 10, Avalonia 12, xUnit + Avalonia.Headless (`[AvaloniaFact]`), Inno Setup 6.5 Pascal Script.

**Spec:** the user's request (2026-11-14) with their answers: log came up as a floating window; maps = "main map only + closable" (other maps open from the module tree's Maps folder; close (x) on every tab plus Close / Close others / Close all on right-click; one row of tabs that scrolls; open tabs remembered per module). Parity rule: WinForms opens only the main map on load and has closable document tabs (`src/Quartermaster.App/MainForm.cs:603-623`, `DocumentTabStrip.cs:52-54`).

## Global Constraints

- No new NuGet packages. No real-game content anywhere (tests use the invented demo/test modules already in the repo).
- Never open visible app windows on the dev machine: UI tests are Avalonia.Headless (`[AvaloniaFact]`); any test touching the dispatcher uses `[AvaloniaFact]`.
- Run test projects one at a time (`dotnet test tests/Quartermaster.Desktop.Tests -c Release`, ~4-5 min; filter while iterating).
- Public repo: noreply commits ending `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; never commit `startapp.cmd`, `.superpowers/`, personal paths.
- Worktree `.worktrees/s6c`, branch `port-stage6c-ui` from master `dede0b5`. Pushing this branch for CI is part of the work; merging needs the user's OK.

## Review Focus

1. **A closed main map leaves the app with no tab** → the map area shows the empty backdrop, status bar shows "-", nothing throws; double-clicking the map in the tree brings it back.
2. **Hot-seat / online visibility** (`ModuleSession.CanSeeMap`) still wins: a map the current player can't see never gets a tab, even if it was remembered as open; switching player drops and restores tabs correctly.
3. **Remembered tabs refer to maps that no longer exist** (module updated) → ignored silently.
4. **Install folder picked in the wizard is unsafe** (data folder, a drive root, a non-empty foreign folder) → the wizard says why on Next and stays on the page; silent `/DIR=` keeps the PrepareToInstall refusal (exit 7).
5. **Palette hidden (not game master)** → the left column shows only the two trees with one splitter, no empty gap.

---

### Task 1: Setup lets you choose the install folder

**Files:** Modify `build/windows/quartermaster.iss`, `build/windows/test-setup.ps1`, `docs/port/stage6b2-windows-checklist.md`, `README.md` (Windows install line).
**Interfaces:** Reuses the existing `PrepareToInstall` guard helpers (`IsRootFolder`, data-folder check, non-empty-without-exe check). Produces `function FolderProblem(const Dir: String): String` (empty = OK, else the user message) used by both `NextButtonClick(CurPageID = wpSelectDir)` and `PrepareToInstall`.
- [ ] Step 1: `DisableDirPage=no`; keep `DefaultDirName={localappdata}\Programs\Quartermaster`, `PrivilegesRequired=lowest`, `UsePreviousAppDir=yes` (explicit). Refactor the guard into `FolderProblem`; `NextButtonClick` on `wpSelectDir` shows `MsgBox(FolderProblem(WizardDirValue), mbError, MB_OK)` and returns False when non-empty. Messages plain: data folder → "This is Quartermaster's data folder (your games and settings). Choose another folder."; root → "Choose a folder, not a whole drive."; non-empty → "This folder already has other files in it. Choose an empty folder or a new one."
- [ ] Step 2: Note in the wizard page (`[Messages]` or `SelectDirBrowseLabel` custom message) that a per-user install can't write to Program Files: "Quartermaster installs just for you. Folders such as Program Files need administrator rights and are not supported." Also refuse folders under `{commonpf}` / `{commonpf32}` / `{commonpf64}` in `FolderProblem` with that text (in-app updates could never write there).
- [ ] Step 3: test-setup.ps1: add a silent case `/DIR=$env:ProgramFiles\QmCiTest` → exit code 7 and folder not created. Existing cases unchanged. Docs: checklist step "the setup asks where to install; default is fine"; README one line.
- [ ] Step 4: Commit `build(windows): setup asks where to install`. CI (Windows job) is the test.

### Task 2: The log always starts docked

**Files:** Modify `src/Quartermaster.Desktop/MainWindow.Trees.cs` (`PlaceLog` / `ReleaseLog`), maybe `MainWindow.axaml.cs` (`CreateLog`/`ApplyLog`); Test `tests/Quartermaster.Desktop.Tests/` (the existing log docking tests' file — find with `grep -rn "LogFloat" tests/Quartermaster.Desktop.Tests`).
**Interfaces:** None new. Behaviour: on every module open the log is docked and shown (height = saved/default 170); popping it out lasts until the module closes. The saved float bounds are still kept so the next pop-out returns to the same place.
- [ ] Step 1: Find why the user's first open of their module showed a floating log (read `PanelDockState.Load/Save`, `PanelFloat.Detach`, `PlaceLog`, `ReleaseLog`; check what a fresh profile and a profile with a saved "log" entry do). Write the root cause in the report.
- [ ] Step 2: Failing test `Log_starts_docked_even_if_it_was_floating_when_the_module_closed` (open demo module, float the log, close module, reopen → `LogShown` true, `LogFloat` null). Plus `Log_float_position_is_remembered` (float → move → close module → reopen → float again → same bounds).
- [ ] Step 3: Implement (load the saved state for bounds only; force docked). Run focused tests → PASS; commit `fix(desktop): the log always starts docked`.

### Task 3: Only the main map opens; map tabs close and reopen

**Files:** Modify `src/Quartermaster.Desktop/Shell/MapTabs.cs`, `src/Quartermaster.Desktop/MainWindow.Trees.cs` (`OnTreeNodeActivated` for map nodes), `src/Quartermaster.Desktop/MainWindow.axaml.cs` (bind/rebuild call sites), `src/Quartermaster.Render/WindowLayoutStore.cs` (per-module open-maps list); Tests: new `tests/Quartermaster.Desktop.Tests/MapTabsClosingTests.cs`, `tests/Quartermaster.Render.Tests/` layout store test.
**Interfaces:**
- `MapTabs.Open(string mapId)` — adds the map's tab if missing (respecting `CanSeeMap`), selects it.
- `MapTabs.Close(MapView v)`, `CloseOthers(MapView v)`, `CloseAll()` — remove tabs, cancel that view's tiling, dispose the view.
- `MapTabs.OpenMapIds : IReadOnlyList<string>` and event `OpenMapsChanged`.
- `WindowLayoutStore.LoadOpenMaps(string moduleKey) : IReadOnlyList<string>?` (null = never saved) / `SaveOpenMaps(string moduleKey, IReadOnlyList<string> ids)` — stored in the module's layout file next to the window entries (keep the existing format readable; add a field).
- [ ] Step 1: Failing tests (headless, demo module with ≥2 maps — use or extend the existing test module builder; never real content):
  - `Only_the_main_map_opens_with_the_module` (first non-org map, as `Bind` selects today).
  - `Tree_double_click_opens_a_closed_map_tab`.
  - `Tab_close_button_closes_the_tab` / `Close_others_and_close_all` (context menu items "Close", "Close Others", "Close All").
  - `Closing_every_tab_leaves_the_backdrop_and_status_dash`.
  - `Open_tabs_are_remembered_per_module` (open 2nd map, close module, reopen → both tabs, same selection); `Remembered_map_that_no_longer_exists_is_ignored`.
  - `Hidden_map_never_gets_a_tab_even_if_remembered` (CanSeeMap false via hot-seat helper used by existing HotSeat tests).
  - `Tab_strip_is_one_row` (the strip's items panel is a horizontal StackPanel inside a ScrollViewer, not a WrapPanel).
- [ ] Step 2: Implement. Tab header = name + small "×" button (classic look, tooltip "Close"), middle-click closes too; context menu on the header. Tab strip: TabControl ItemsPanel = horizontal StackPanel, template's header area in a `ScrollViewer` with horizontal scroll (Auto) and hidden vertical; keep `_tabWidths` sizing. `Rebuild` (hot-seat visibility change) keeps the user's open set ∩ visible. Remember on every change (debounced is fine) and on module close.
- [ ] Step 3: Run Desktop tests that touch maps (`--filter "FullyQualifiedName~Map|FullyQualifiedName~HotSeat|FullyQualifiedName~Online"`) then the whole Desktop project once → PASS. Commit `feat(desktop): only the main map opens; map tabs close and reopen`.

### Task 4: The counter palette sits in the left column

**Files:** Modify `src/Quartermaster.Desktop/MainWindow.axaml` (LeftColumnGrid rows; remove PaletteHost/PaletteSplitter from `Body`), `src/Quartermaster.Desktop/MainWindow.axaml.cs` (`UpdatePaletteUi`), `src/Quartermaster.Desktop/MainWindow.Trees.cs` (left-split persistence), `src/Quartermaster.Render/WindowLayoutStore.cs` (second fraction); Tests: existing palette/left column tests (grep `PaletteHost`, `LeftSplit`).
**Interfaces:** `LeftColumnGrid` rows: ModuleTree / splitter / Palette / splitter / TablesTree. `WindowLayoutStore.LoadLeftSplits() : (double Tree, double Palette)` / `SaveLeftSplits(...)` — fractions of the column height, defaults (0.4, 0.35) when the palette shows; old `LeftSplit` files still load (tree fraction, palette default). When the palette is hidden the palette row and its splitter are 0 and the trees split as today.
**Toggle (user, 2026-11-14):** the palette can be switched on and off: View > "Counter Palette" check item (WinForms has the same item, `src/Quartermaster.App/MainForm.cs:247`) and a shortcut-free toolbar-less toggle; shown = game master AND toggled on (default on). The toggle is remembered across sessions (`ui.json`, e.g. `PaletteShown`, default true). The menu item is disabled (not hidden) when not game master, with the tip "Turn on game master mode in Settings > General to place counters".
- [ ] Step 1: Failing tests: `Palette_is_between_module_tree_and_tables_tree` (grid rows order); `Hidden_palette_leaves_no_gap` (row heights 0 when not game master, and when toggled off); `View_menu_toggles_the_palette` (check item flips visibility; disabled when not game master); `Palette_toggle_is_remembered`; `Palette_split_is_remembered`; `Old_ui_json_with_one_split_still_loads`. Drag from palette to map keeps working: existing palette drop test still passes.
- [ ] Step 2: Implement; the palette's folder tree shrinks to fit the narrower column (min heights sensible, thumbnails wrap). Run focused + whole Desktop tests → PASS. Commit `feat(desktop): counter palette in the left column`.

### Task 4b: Every part of the screen can be shown or hidden

**User (2026-11-14):** "everything else on the screen should be toggleable, i want the app to be as customizable as possible".
**Files:** Modify `src/Quartermaster.Desktop/MainWindow.axaml.cs` (View menu build), `MainWindow.Trees.cs`, `MainWindow.Phases.cs`, `MainWindow.Outliner.cs`, `Shell/MapTabs.cs` (map toolbar), `src/Quartermaster.Render/WindowLayoutStore.cs` (`ui.json` gets a `Hidden` set of part ids); Test new `tests/Quartermaster.Desktop.Tests/ViewTogglesTests.cs`.
**Interfaces:** `WindowLayoutStore.LoadHiddenParts() : IReadOnlySet<string>` / `SaveHiddenParts(IEnumerable<string>)`. Part ids and View > "Show" submenu check items (one per part, existing items such as View > Module Tree / Log / Outliner / Counter Palette move into it or stay where they are and share the same state — no duplicates): `toolbar` "Main Toolbar", `phaseStrip` "Phase Strip", `alertBar` "Alert Bar", `statusBar` "Status Bar", `mapToolbar` "Map Toolbar" (every map tab), `moduleTree` "Module Tree", `tablesTree` "Charts & Tables" (module tree and tables tree separately; the left column disappears only when both are off and the palette is off/absent), `palette` "Counter Palette" (Task 4), `log` "Log", `phases` "Phase Panel", `outliner` "Outliner". Plus View > "Show Everything" (reset: every part shown, splits back to defaults).
- Automatic bars stay automatic and are not toggles: the replay bar (only while a received turn is replayed) and the online banner (paused / relay lost warnings).
- Every toggle is remembered across sessions (app-wide, not per module). Hidden parts take no space (rows/columns and their splitters collapse to 0).
- Keyboard: a part's existing shortcut keeps working; hiding the main toolbar or status bar never hides menu commands.
- [ ] Step 1: Failing tests: one `[AvaloniaTheory]`-style test per part id (toggle off → control invisible and takes 0 space; toggle on → back), `Hidden_parts_are_remembered`, `Show_everything_restores_all_parts`, `Left_column_collapses_only_when_all_three_are_off`, `Replay_bar_and_online_banner_are_not_toggles`.
- [ ] Step 2: Implement; run focused then whole Desktop tests → PASS. Commit `feat(desktop): every part of the screen can be shown or hidden`.

### Task 5: Docs

**Files:** Modify `docs/port/parity.md` (deviations: palette position; tabs now as WinForms), `docs/superpowers/RESUME.md` (6c state), `docs/port/stage6b2-windows-checklist.md` (steps for: choose folder, log docked, close/reopen map tab, palette in left column).
- [ ] Step 1: Write; commit `docs: stage 6c Windows feedback`.

---

## After the tasks

Final whole-branch review (opus) → one fix wave → CI green on all three OSes → user picks merge/PR/keep → ask before merge.
