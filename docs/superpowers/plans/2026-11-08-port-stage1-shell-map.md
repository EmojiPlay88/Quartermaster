# Cross-platform Port — Stage 1 (Shell, Map, Pieces) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A new cross-platform Avalonia app (`Quartermaster.Desktop`) that runs on Windows, macOS (M1) and Linux, opens a VASSAL module, starts a setup, shows the maps with pieces in the classic grey look, lets you pan/zoom, select, drag and stack pieces, place pieces from the palette, undo/redo, and save/open `.qsave` games — with the WinForms app untouched.

**Architecture:** New project `src/Quartermaster.Desktop` (Avalonia, `net10.0`, no Windows APIs) sits beside `src/Quartermaster.App` (WinForms, keeps shipping). Both use the existing platform-neutral libraries (Core, Vassal, Rules, Render). Map drawing reuses `Render.MapRenderer`/`PieceRenderer`/`Viewport`/`DropResolver`; drag logic `MapDragController` moves from App into Render so both UIs share it. All Desktop UI tests run **headless** (Avalonia.Headless) — no window ever appears on anyone's desktop.

**Tech Stack:** C# .NET 10, Avalonia 11.x (exact version pinned in Task 1), Avalonia.Headless.XUnit, SkiaSharp (repo's 4.154.0 unless Task 1 rules otherwise), xUnit, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-08-cross-platform-design.md` (stage 1 of 5: "app shell + board/map + pieces").

## Global Constraints

- WinForms app (`src/Quartermaster.App`) keeps building and behaving exactly as before; only Task 4 touches it (moving `MapDragController` out, no behaviour change).
- `Quartermaster.Desktop` must not reference WinForms, `System.Windows.*`, WebView2, `Windows.*` WinRT APIs, or `Quartermaster.App`/`Quartermaster.Ocr`. Target `net10.0` (not `-windows`).
- Look: the same classic grey look as today's Windows app on all three OSes (one theme). Font: Tahoma 8.25pt (11px) as on Windows today (`Program.cs:295`); fallback chain on other OSes `Tahoma, Verdana, DejaVu Sans, sans-serif`.
- Shortcuts: Cmd instead of Ctrl on macOS (primary modifier mapped per OS). No native macOS global menu bar (menu stays in the window).
- Menu labels copy the WinForms text exactly (e.g. `&Open Module...`, `&New Game`, `Load &Setup...`, `Open &Game...`, `&Save Game`, `Save Game &As...`, `E&xit`) — same punctuation (`...` where WinForms has `...`, `…` where it has `…`).
- Never open visible windows during development or tests: UI tests and screenshots are headless only. The user must not see app windows pop up.
- Saves made by Desktop open in the WinForms app and vice versa (same `ModuleSession.SaveNative/LoadNative`).
- No real-game (CNA) content; fixtures use `tests/Quartermaster.TestFixtures` or the invented Riverland demo (`docs/wiki/demo/`).
- Commits use the repo's noreply email; never commit `startapp.cmd`; end commit messages with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Pushing, creating releases, or enabling workflows on GitHub only when the user asks.

## Review Focus

1. Retina / HiDPI (macOS 2×, Linux fractional scaling): pointer→map coordinates and hit-testing must stay exact and the map must not render blurry → Task 5 test `Pointer_to_map_is_exact_at_scale_2`.
2. macOS input: Cmd+click adds to selection (Ctrl+click is right-click on Mac); trackpad two-finger scroll pans / pinch zooms → Task 6 tests `Mac_multiselect_uses_meta`, Task 5 `Horizontal_wheel_pans`.
3. Paths with spaces and non-ASCII characters on all OSes for module and save files → Task 7 test `Save_and_open_path_with_spaces_and_unicode`.
4. Closing with unsaved changes asks exactly like the WinForms app ("The current game has unsaved changes. Discard them?", `MainForm.cs:373`) → Task 7 test `Close_with_unsaved_changes_asks`.
5. Linux without Tahoma/Verdana/DejaVu installed: falls back to a sans font, no crash, layout still fits → Task 2 test `Missing_fonts_fall_back`.

---

### Task 1: Scaffold `Quartermaster.Desktop` + version gate

**Files:**
- Create: `src/Quartermaster.Desktop/Quartermaster.Desktop.csproj`, `Program.cs`, `App.axaml`, `App.axaml.cs`, `MainWindow.axaml`, `MainWindow.axaml.cs`
- Create: `tests/Quartermaster.Desktop.Tests/Quartermaster.Desktop.Tests.csproj`, `TestApp.cs`, `ShellTests.cs`
- Create: `Quartermaster.CrossPlatform.slnf` (solution filter: Core, Vassal, Rules, Render, Net, Relay, Desktop + their test projects, TestFixtures, Docs.Tests; excludes App, Ocr, Updater and their tests, which target `net10.0-windows` until later stages; if any included project fails to build on non-Windows, drop it from the filter and say so in the report)
- Modify: `Quartermaster.slnx` (add both projects)

**Interfaces:**
- Produces: `Quartermaster.Desktop.App`, `MainWindow` (Title `"Quartermaster"`), `Program.BuildAvaloniaApp()`; test helper `TestApp` registering `[assembly: AvaloniaTestApplication(typeof(TestApp))]` with headless platform (`UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })` + Skia so frames can be captured).

- [ ] **Step 1: Version gate (decision, recorded in report).** Find the newest stable Avalonia 11.x (or 12.x if stable) on NuGet whose Skia backend runs with SkiaSharp 4.154.0 in the same process (the repo's Render uses 4.154.0; two native libSkiaSharp versions in one process is not allowed). Prove it with a throwaway headless test that draws via `Render.MapRenderer` into the Avalonia Skia canvas. If no Avalonia version works with 4.154.0, try aligning the whole repo to the SkiaSharp version Avalonia needs (all existing tests must still pass); if neither works, stop with BLOCKED and the evidence. Pin exact versions (no floating ranges).
- [ ] **Step 2: Failing test** `ShellTests.Main_window_opens_headless`: `[AvaloniaFact]` creates `MainWindow`, `Show()`s it (headless), asserts `Title == "Quartermaster"` and `IsVisible`.
- [ ] **Step 3: Run** `dotnet test tests/Quartermaster.Desktop.Tests` → FAIL (types missing).
- [ ] **Step 4: Implement** the scaffold; `Program.Main` uses `BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)`; csproj `OutputType=WinExe`, `AssemblyName=Quartermaster`, `ApplicationManifest`/icons not needed yet.
- [ ] **Step 5: Run** desktop tests → PASS; `dotnet build Quartermaster.slnx` 0 errors; whole suite green.
- [ ] **Step 6: Linux check (if available):** if `wsl` with a dotnet 10 SDK exists, run `dotnet test tests/Quartermaster.Desktop.Tests` inside WSL (headless only — never launch the app GUI under WSLg). Otherwise note "Linux covered by CI (Task 8)".
- [ ] **Step 7: Commit** `feat(desktop): Avalonia app scaffold with headless tests`.

### Task 2: Classic theme

**Files:** Create `src/Quartermaster.Desktop/Theme/ClassicTheme.axaml`, `Theme/ClassicFonts.cs`; Test `tests/Quartermaster.Desktop.Tests/ThemeTests.cs`

**Interfaces:**
- Produces: `ClassicFonts.FamilyFor(OSPlatform os) -> string` (Windows: `"Tahoma"`; macOS: `"Tahoma, Verdana, sans-serif"`; Linux: `"Tahoma, Verdana, DejaVu Sans, sans-serif"`), `ClassicFonts.SizePx = 11.0`; theme resource keys `Classic.Face` (control face), `Classic.Window` (window/list background), `Classic.Text`, `Classic.GrayText`, `Classic.Highlight`, `Classic.HighlightText`, `Classic.Border`.

- [ ] **Step 1: Pick the colours** by sampling today's WinForms screenshots in `docs/wiki/images/` (e.g. `main-window.png`, `settings-general.png`, `ledger.png`): face, window, text, disabled text, selection highlight, border. Record the hex values and which image/pixel each came from in the report. Style buttons, menus, tabs, text boxes, list/tree, scrollbars, status bar, splitters to match (flat classic WinForms look, no Fluent rounding/animations). Base on Avalonia's Simple theme.
- [ ] **Step 2: Failing tests:** `Font_family_per_os` (three asserts on `FamilyFor`), `Theme_resources_have_sampled_colours` (each key resolves to the recorded hex), `Missing_fonts_fall_back` (headless window with the Linux family string on a system without those fonts renders a `TextBlock` with non-zero `DesiredSize` and no exception).
- [ ] **Step 3: Run → FAIL. Step 4: Implement. Step 5: Run → PASS.**
- [ ] **Step 6:** Headless screenshot of an empty `MainWindow` with a sample menu/button/textbox (`TestHelpers.CaptureToPng(window, path)` using `GetLastRenderedFrame()`) saved to `artifacts/port/theme-sample.png` (gitignored); view it next to `docs/wiki/images/settings-general.png` and note differences in the report.
- [ ] **Step 7: Commit** `feat(desktop): classic grey theme`.

### Task 3: Per-OS shortcuts (Cmd on Mac)

**Files:** Create `src/Quartermaster.Desktop/Input/ShortcutGestures.cs`; Test `tests/Quartermaster.Desktop.Tests/ShortcutGestureTests.cs`

**Interfaces:**
- Consumes: shortcut strings from `Render.CommandRegistry`/`CommandDefaults` (e.g. `"Ctrl+Shift+W"`, `"F5"`, `"Ctrl+,"`, `"L"`).
- Produces: `ShortcutGestures.Parse(string spec, bool mac) -> KeyGesture?` (null for unparseable), `ShortcutGestures.Display(string spec, bool mac) -> string` (Windows/Linux `"Ctrl+Shift+W"`; mac `"⌘⇧W"`), `ShortcutGestures.IsMac` (runtime `OperatingSystem.IsMacOS()`).

- [ ] **Step 1: Failing tests:** `Ctrl_maps_to_meta_on_mac` (`Parse("Ctrl+Shift+W", true)` → `KeyModifiers.Meta|Shift`, `Key.W`); `Ctrl_stays_control_elsewhere`; `Function_key` (`"F5"` → `Key.F5`, no modifiers); `Comma` (`"Ctrl+,"` → `Key.OemComma`); `Alt_is_option_on_mac` (`"Alt+Enter"` → `KeyModifiers.Alt`); `Display_mac_symbols`; `Garbage_is_null` (`"Ctrl+"`); `Every_default_parses` (every entry in `CommandDefaults` parses on both OSes).
- [ ] **Step 2: Run → FAIL. Step 3: Implement. Step 4: Run → PASS. Step 5: Commit** `feat(desktop): per-OS shortcut gestures`.

### Task 4: Share `MapDragController` via Render

**Files:** Move `src/Quartermaster.App/MapDragController.cs` → `src/Quartermaster.Render/MapDragController.cs` (namespace `Quartermaster.Render`, `public sealed class`); Modify App usages (`MapCanvas*.cs`, `PaletteDrop.cs`, others found by build); Test `tests/Quartermaster.Render.Tests/MapDragControllerTests.cs`

**Interfaces:**
- Produces: `public sealed class Quartermaster.Render.MapDragController(ModuleSession s, MapDef map, PieceRenderer pieces, Action<Action> guard)` with its existing public members unchanged (`Format`, `Current`, `Active`, `Incoming`, `Draw(SKCanvas, Viewport, GameState)`, begin/update/end/cancel methods as they exist today). `DragInfo` moves with it if App-only today.

- [ ] **Step 1:** Confirm the file has no WinForms dependency (it has none today besides what the build reveals); anything WinForms-specific stays in App behind a small adapter.
- [ ] **Step 2: Failing test** `Drag_moves_piece_and_snaps` using `FixtureModule`: begin drag on a piece, update to a point, end → `GameState` position equals `DropResolver.ResolveDrag(...)` result; `Cancel_restores` → no command executed.
- [ ] **Step 3: Run → FAIL (type not in Render). Step 4: Move + make public. Step 5:** Render tests PASS; full solution tests PASS; WinForms smoke `Quartermaster.App.exe <demo vmod> --rules <demo qrules> --smoke 3` with `QUARTERMASTER_SHOT_OFFSCREEN=1` exits 0 (off-screen — never visible).
- [ ] **Step 6: Commit** `refactor(render): share MapDragController between UIs`.

### Task 5: `MapView` control (draw, pan, zoom)

**Files:** Create `src/Quartermaster.Desktop/Map/MapView.cs`, `Map/SkiaDrawOperation.cs`; Test `tests/Quartermaster.Desktop.Tests/MapViewTests.cs`

**Interfaces:**
- Consumes: `ModuleSession`, `MapDef`, `MapRenderer.Draw(SKCanvas, MapDef, tiles, GameState, ModuleModel, Viewport, SKSize, MapDrawOptions)`, `Viewport`, `FitView.ZoomToFit`.
- Produces: `MapView : Control` with `Session`, `Map`, `Viewport View`, `ZoomToFit()`, `PointD ScreenToMap(Point p)` (in Avalonia DIPs), event `ViewportChanged`. Draws via `ICustomDrawOperation` + `ISkiaSharpApiLeaseFeature` on the Avalonia Skia canvas (scaled by `RenderScaling` so it's crisp).

Behaviour copied from WinForms `MapCanvas.cs`: wheel zooms at the cursor by `_zoomStep^(delta)` (find `_zoomStep` value there), zoom ignored mid-drag; same pan gesture(s) as MapCanvas (read `MapCanvas.cs` for the button/key used); horizontal wheel (trackpad) pans; pinch (`PointerTouchPadGestureMagnify`) zooms; zoom-to-fit when a map first shows.

- [ ] **Step 1: Failing tests (headless, Riverland demo from `docs/wiki/demo/`):**
  - `Renders_same_pixels_as_renderer`: MapView 800×600 at scale 1 after `ZoomToFit`; captured frame equals (per-pixel, tolerance ≤ 2 per channel) `MapRenderer.Draw` into an `SKSurface` of the same size and viewport.
  - `Wheel_zooms_at_cursor`: wheel +1 notch at (400,300) → map point under cursor unchanged (±0.5), zoom multiplied by the step.
  - `Horizontal_wheel_pans`: horizontal delta changes `View.Pan.X`, zoom unchanged.
  - `Pointer_to_map_is_exact_at_scale_2`: headless window with `RenderScaling = 2`; `ScreenToMap` of a DIP point equals `View.ToMap` of the same DIP point; captured frame is 2× size and the counter edge is sharp (pixel at counter centre equals renderer's colour).
- [ ] **Step 2: Run → FAIL. Step 3: Implement. Step 4: Run → PASS. Step 5: Commit** `feat(desktop): map view with pan and zoom`.

### Task 6: Piece interaction

**Files:** Create `src/Quartermaster.Desktop/Map/MapView.Input.cs`; Test `tests/Quartermaster.Desktop.Tests/PieceInteractionTests.cs`

**Interfaces:**
- Consumes: `MapDragController` (Task 4), `HitTester`, `SelectionRect`, `DropResolver`, `ModuleSession` game execute path exactly as WinForms (`game.Execute(CommandLog.Prepare(move, game.State, _s))` — see `MapDragController`/`MapCanvas`), `HistoryController` for undo/redo, `ShortcutGestures.IsMac`.
- Produces: `MapView.SelectedPieceIds` (`IReadOnlySet<string>`), event `SelectionChanged`.

Behaviour copied from WinForms `MapCanvas`: click selects top piece/stack; add-to-selection modifier is Ctrl on Windows/Linux and **Cmd (Meta) on macOS**; drag on empty map = rubber-band select; dragging a selected piece moves the whole selection with snapping/stacking via `DropResolver`; Esc cancels a drag; only pieces `Session.CanAct(id)` may move.

- [ ] **Step 1: Failing tests (headless pointer events on the demo):**
  - `Click_selects_piece`; `Rubber_band_selects`; `Drag_moves_and_snaps` (state position = `DropResolver` result); `Drop_on_piece_stacks`; `Esc_cancels_drag` (state unchanged); `Undo_redo_move` (Ctrl/Cmd+Z then Ctrl/Cmd+Y via `ShortcutGestures`); `Mac_multiselect_uses_meta` (with mac flag: Meta+click adds; Ctrl+click does not add); `Cannot_move_unactable_piece` (view limited to one side → other side's piece doesn't move).
- [ ] **Step 2: Run → FAIL. Step 3: Implement. Step 4: Run → PASS. Step 5: Commit** `feat(desktop): select, drag, stack and undo pieces`.

### Task 7: Main window shell

**Files:** Modify `src/Quartermaster.Desktop/MainWindow.axaml(.cs)`; Create `Shell/MapTabs.cs`, `Shell/PaletteDock.cs`, `Shell/SetupChooser.axaml(.cs)`, `Shell/Dialogs.cs`; Test `tests/Quartermaster.Desktop.Tests/MainWindowTests.cs`

**Interfaces:**
- Consumes: `ModuleSession.Open/StartNewGame/LoadSetup/LoadNative/SaveNative`, `PaletteModel`, `MapView`, `ShortcutGestures`, `CommandDefaults` ids (`file.openModule`, `file.newGame`, `file.openGame`, `file.saveGame`, `file.saveGameAs`, undo/redo ids).
- Produces: `MainWindow.OpenModule(string path)`, `MainWindow.StartSetup(int? index)`, `MainWindow.SaveGame(string path)`, `MainWindow.OpenGame(string path)`, `MainWindow.HasUnsavedChanges`, `IDialogs` (file pickers + message boxes, swappable in tests), command line `[<vmod>] [--setup <index>|--new]`.

Stage-1 menu (labels exact from `MainForm.cs`): **File**: `&Open Module...`, `&New Game`, `Load &Setup...`, `Open &Game...`, `&Save Game`, `Save Game &As...`, `E&xit`. **Edit**: undo/redo items with labels from `MainForm.History.cs`. **View**: zoom in/out/fit and grid toggle with labels from MapWindow/MapCanvas menus. Other WinForms items are absent until their stage. One tab per visible map (`Session.CanSeeMap`); palette dock lists `PaletteModel` entries with counter images; dragging a palette entry onto the map adds the piece (same command path as WinForms `PaletteDrop`). Status bar shows map coordinates/location under the cursor like WinForms.

- [ ] **Step 1: Failing tests (headless, fake `IDialogs`):**
  - `Menu_labels_match_winforms` (exact strings above).
  - `Open_module_and_start_setup` (demo vmod, setup 0 → map tabs = demo maps, pieces present).
  - `Save_and_reopen_roundtrip` (save temp `.qsave`, reopen in a new window → equal `GameState` JSON).
  - `Save_and_open_path_with_spaces_and_unicode` (`"Riverland game ä ñ 1.qsave"` in a folder with spaces).
  - `Winforms_save_opens` (a `.qsave` produced by `ModuleSession.SaveNative` from a fixture loads).
  - `Close_with_unsaved_changes_asks` (move a piece, close → fake dialog receives "The current game has unsaved changes. Discard them?"; answering No keeps the window open).
  - `Palette_drop_adds_piece`.
- [ ] **Step 2: Run → FAIL. Step 3: Implement. Step 4: Run → PASS. Step 5: Commit** `feat(desktop): main window shell, menus, palette, open/save`.

### Task 8: Headless screenshots, CI, parity checklist, Mac checklist

**Files:**
- Modify: `src/Quartermaster.Desktop/Program.cs` — `--screenshot-main <png>`: renders the main window **headless** (Avalonia.Headless, no real window) with the given module/setup and writes the PNG, then exits.
- Create: `build/port-screenshots.ps1` (runs Desktop `--screenshot-main` and WinForms `--screenshot` off-screen on the Riverland demo → `artifacts/port/stage1-desktop.png` and `artifacts/port/stage1-winforms.png`)
- Create: `.github/workflows/cross-platform.yml` (on push/PR: matrix `windows-latest`, `macos-latest`, `ubuntu-latest`; `dotnet test Quartermaster.CrossPlatform.slnf`; on macOS publish `src/Quartermaster.Desktop -r osx-arm64 --self-contained` and on Ubuntu `-r linux-x64 --self-contained`, upload as workflow artifacts `Quartermaster-Desktop-osx-arm64`/`-linux-x64`). The workflow file is committed; it only runs when the user pushes.
- Create: `docs/port/parity.md` — every WinForms window, menu command and feature (enumerate from `src/Quartermaster.App`, reuse the class→page table in `docs/wiki/Menus-and-Windows.md`), columns `Windows | macOS | Linux`; tick the stage-1 rows that Desktop now covers on Windows (headless tests) and Linux (CI/WSL); macOS ticked only after the user's checklist.
- Create: `docs/port/stage1-mac-checklist.md` — short numbered checklist for the user's M1: download the CI artifact, unzip, first-start Gatekeeper (right-click → Open, or `xattr -d com.apple.quarantine`), open the demo module, start setup, pan with trackpad, pinch zoom, Cmd+click multi-select, drag/stack, Cmd+Z/Cmd+Y, save, reopen, close with unsaved changes; what to report back.

- [ ] **Step 1: Failing test** `Screenshot_flag_writes_png_headless` (run Desktop entry with `--screenshot-main <temp png>` in-process via `Program.RunScreenshot(args)` → PNG exists, size > 0, no `Window` was shown on a real platform).
- [ ] **Step 2: Run → FAIL. Step 3: Implement. Step 4: Run → PASS.**
- [ ] **Step 5:** Run `build/port-screenshots.ps1`; view both PNGs; note look differences in the report (the user judges side by side).
- [ ] **Step 6:** Validate workflow YAML syntax locally (e.g. `actionlint` if available, else careful read); do **not** push.
- [ ] **Step 7:** Full `dotnet test Quartermaster.slnx` green. **Commit** `feat(desktop): headless screenshots, CI workflow, parity and Mac checklists`.
