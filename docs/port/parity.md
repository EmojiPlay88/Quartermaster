# Cross-platform parity checklist (WinForms app vs Quartermaster.Desktop)

Every window, menu command and feature of the WinForms app (`src/Quartermaster.App`), with the stage of the port plan that brings it to the Avalonia app (`src/Quartermaster.Desktop`). Class names follow the table in `docs/wiki/Menus-and-Windows.md`.

How to read the columns:

- **Windows**: ticked when Desktop covers the row and headless tests (Avalonia.Headless) pass on Windows in this branch.
- **Linux**: ticked only for features verified by tests actually run on Linux. Stage 1 rows: verified by the headless tests in the CI workflow `.github/workflows/cross-platform.yml` on ubuntu-latest (green run 42510235329, 2026-11-08). Not yet opened on a real Linux desktop.
- **Docs.Tests on Linux**: pass on ubuntu-latest in CI (run 42510235329).
- **macOS**: ticked only after the user has worked through the stage's Mac checklist on the M1 Mac (`docs/port/stage1-mac-checklist.md`, `docs/port/stage2a-mac-checklist.md`, `docs/port/stage2b-mac-checklist.md`, `docs/port/stage3a-mac-checklist.md`, `docs/port/stage3b-mac-checklist.md`, `docs/port/stage4a-mac-checklist.md`, `docs/port/stage4b-mac-checklist.md`, `docs/port/stage4c-mac-checklist.md`, `docs/port/stage5a-mac-checklist.md`, `docs/port/stage5b-mac-checklist.md`). Stage 1 rows: all checklist steps OK on the user's M1 Mac (2026-11-08), including real GPU rendering.
- **Stage**: 1 = shell, map, pieces; 2a = rules packs, map tools, dice, turn, log; 2b = formulas, phases, step back, module tree, alerts; 3a = Ledger, Outliner, Inventory, Notes, Diagnostics, Overview, Save map as picture, counter pictures; 3b = rulebook PDF viewer, charts, text and HTML documents, Rule ref (search, scan and OCR panes moved to 4a); 4a = Rules Workbench, rulebook search, scans and OCR; 4b = Settings; 4c = OCR language downloads; 5a = app lifecycle: launcher, Back to launcher, Interface Tour, About, update screens; 5b = hot-seat: the new-game Players page, Settings > Game > Players, the handover screen, the status-bar player switcher (this branch); 5 (5c and later) online multiplayer, turn files, installing updates (stage 6); `-` = no plan yet or not applicable off Windows. The stage numbers after 4a are provisional. Rows that say 3 below are done in 3a unless they name the PDF viewer, charts or documents (3b); rows that say 4a name the Workbench, search, scans or OCR; rows that name Settings are 4b; rows that name OCR languages are 4c; rows that name the launcher, the tour, About or the update screens are 5a.

## Stage-1 deviations from the WinForms app (deliberate or known)

- The counter palette follows game master mode (Settings > General), as WinForms. (Stage 1 showed it always; changed in 4b.)
- The new game dialog has no Players page (comes with multiplayer). Done in 5b: Next > opens the Players page.
- The difficulty chosen in the new game dialog is remembered as the default for the next game (`LastDifficulty`, since 4b).
- Mouse wheel on macOS pans and Cmd+wheel zooms (deliberate: trackpad convention). On Windows and Linux the wheel zooms, as WinForms. Pinch zoom is for macOS trackpads.
- The look is the classic grey theme on all three systems, with Tahoma 11px where present, else Verdana, DejaVu Sans.
- Shortcuts use Cmd instead of Ctrl on macOS. There is no native macOS menu bar; the menu bar sits in the window.
- Saves are written and read by the same ModuleSession.SaveNative / LoadNative calls as WinForms, so each app opens the other's saves. Desktop tests cover the Desktop side; open a WinForms save in Desktop (and back) is on the Mac checklist.

### Map behaviour not yet ported

- Dragging between windows: the OLE drag preview in the target window and drops across windows (a drop released outside the view is cancelled).
- (Done in 4b) Settings apply to every open map view: wheel step, drag threshold, hover delay, highlight colour, label size, snap, grid default.

## Stage-2a deviations from the WinForms app (deliberate or known)

Stage 2a = rules packs, map tools (menu, hover popup, LOS, hide, last moved), dice and turn tools, main toolbar, log panel. The 2b items are in the next section.

- The hover popup and the breakdown tip are overlays inside the window, so they never leave it and flip sooner near the window edge. WinForms uses screen-sized popups.
- The hover delay comes from Settings > Map (since 4b); it applies to every open map tab at once.
- The log export picker has a single filter, "Log files (*.txt;*.html)" (default .txt); the format follows the extension. WinForms has two filters.
- There is no chat line offline (WinForms shows it only online); the box comes with multiplayer (stage 5).
- The main toolbar shows only the buttons whose features exist in Desktop (no Overview, Palette yet; Diagnostics, Notes and Inventory are in, in the WinForms order). Buttons are hidden and shown from Settings > Toolbar since 4b.
- The map toolbar sits inside each map tab (WinForms: one per map window); it ends with Save map as picture… (tip "Save the view or the whole map as a PNG").
- The counter menu's Delete counter shows in game master mode (Settings > General) and asks first when "Ask before deleting pieces" is on (since 4b).
- Physical dice are in since 4b (Settings > Dice & Log); rolls and log lines are the shared WinForms ones.
- Rules-pack file pickers and prompts are the Avalonia ones (async); the dirty-pack question is asked only on module change and close, as WinForms.
- Masked counters cannot be right-clicked in either app (shared hit test); left as is.
- WinForms draws popup counters scale-squared at more than 100% Windows scaling; Desktop draws them at the intended size. Identical at 100%.
- The main toolbar order follows the WinForms window (MainForm.BuildToolbar), not the order of `ToolbarIds.All`.
- The popup and tip fonts (Tahoma, Consolas) fall back to other fonts off Windows; the fallback is unchecked on macOS.
- Opening a rules pack made for another module only logs a warning, as WinForms.
- The map toolbar's zoom drop-down button reads "Zoom ▾" (WinForms "Zoom" with a drawn drop-down arrow, which Avalonia buttons do not draw).
- Real dialogs, menu placement and pointer positions were only exercised headless; see `docs/port/stage2a-mac-checklist.md`.

## Stage-2b deviations from the WinForms app (deliberate or known)

Stage 2b = phase strip and panel, Next Phase, Step Back, formula window and cheat sheet, batch grid and missing-input dialog, alert bar, module and tables trees, floating log and phase panels. Behaviour, labels and punctuation of the formula window, the batch grid and the missing-input dialog are copied from WinForms (the shared rules live in Quartermaster.Render: `FormulaWindowRules`, `BatchModel`, `PhaseRunDispatch`).

- The batch grid's Unit cell shows the unit's counter picture (64×48, rows 52 px) with the big hover preview since stage 3a, as WinForms.
- Rule ref (formula window, phase panel, rule links) opens the referenced document in its window at the referenced page (3b); a reference to a document the module lacks shows an error box.
- Changing the difficulty in mid-game works from Settings > Game since 4b (a guest is told the host decides). The windows follow the mode when it changes.
- The notice chip on the alert bar is drawn, but clicking it does nothing until multiplayer (stage 5).
- The map-tab pop-out and the Window menu items (Cascade, Tile, Pop Out Active Window, floating windows list) are not ported; Desktop uses tabs. Only the log and the phase panel pop out.
- The phase-boundary autosave interval comes from Settings since 4b (default 5 minutes).
- The formula window, cheat sheet, batch grid and dialogs are Avalonia windows owned by the main window; their placement is the platform's (centred on the owner), and the cheat sheet opens beside the formula window.
- Tables-tree activation opens the Rules Workbench since stage 4a; document and chart nodes of the module tree open their windows (3b).
- Floating log and phase windows are clamped onto a screen that exists (primary screen as fallback). Real second-display behaviour is only checked by the Mac checklist (`docs/port/stage2b-mac-checklist.md`).
- Open Autosave is done in 4b; Game > Send Turn and the other multiplayer menus around phases wait for stage 5.
- When a rules pack without phases replaces one with phases while the phase panel floats, Desktop docks the panel back (the column stays hidden); WinForms leaves the floating window open showing the no-phases text.
- Known: while a Desktop modal dialog (Step Back, Next Phase, missing input) is open, the floating Phases window and the formula and batch-grid windows still accept input (Avalonia ShowDialog disables only the owner); WinForms disables every window of the app.
- Floating log and phase panels always reopen docked: the layout memory never stores Floating=true, the same as WinForms.
- Fixed in the 2b screenshot pass: the alert bar's hidden measuring chip ("+00") was visible at the left of the banner text.

## Stage-3a deviations from the WinForms app (deliberate or known)

Stage 3a = Ledger window (tabs, counter pictures, editing, CSV), Edit Ledger tabs, Show in Ledger, holder alerts opening the Ledger, Outliner, Inventory, Notes, Diagnostics, Overview, Save map as picture, counter pictures in the batch grid. The row logic is shared in Quartermaster.Render (`LedgerViewRules`, `LedgerJump`, `OutlinerViewRules`, `InventoryRules`, `DiagnosticsView`, `MapExportFlow`, `PreviewPlacement`). Not done in 3a and left for 3b: PDF rulebook viewer, charts, rulebook search, scans and OCR, Rule ref buttons (still disabled).

- The Overview is always a floating owned window (WinForms can dock it as an MDI document under "doc:overview"); document-tab docking is not ported (see 2b: Desktop uses tabs).
- The Ledger's CSV save dialog has a single filter ("CSV files (*.csv)") and the title "Save As" (WinForms: the SaveFileDialog default title; `IDialogs.SaveFileAsync` takes one filter, as the log export).
- Visibility and grouping of the Outliner are kept between runs since 4b (in `settings.json`); its floating bounds are kept per module under "outliner".
- The Ledger's Export CSV picker and the Save map as picture file picker are the Avalonia storage dialogs (async), not the Windows common dialogs.
- The Ledger, Inventory, Notes, Diagnostics and Overview windows are Avalonia windows owned by the main window, one of each per module; their placement is the platform's (centred on the owner) unless bounds were remembered under "doc:ledger", "doc:inventory", "doc:notes", "diag", "doc:overview". They are not MDI children. While a Desktop modal dialog is open they still accept input (as in 2b).
- The counter preview text uses a fixed 12-px font instead of the WinForms status font, so its width can differ by a few pixels.
- The Ledger toolbar wraps onto a second line at the default 820 px width (Show units without values and Refresh move down; the WinForms toolbar fits its buttons on two lines in a different place), seen in the screenshot pass.
- Counter pictures are drawn from the player's view only; the batch grid no longer resets its pictures on a refresh (the content-keyed cache makes that unnecessary).
- The Ledger's group header rows are a lighter grey than the WinForms ones (screenshot pass); the tab strip is a flat strip rather than owner-drawn tabs.
- Real windows, pointer hover, second displays, Numbers and Preview were only exercised headless; see `docs/port/stage3a-mac-checklist.md`.


## Stage-3b deviations from the WinForms app (deliberate or known)

Stage 3b = chart windows, document windows (PDF, text, HTML), the module tree's chart and document nodes, working Rule ref (formula window, phase panel, rule links), the document list / images list / Used on this page in the viewer's toolbar mode. The PDF code lives in `src/Quartermaster.Documents` (PDFtoImage over PDFium on one worker thread; TIFF scans through LibTiff.Net), the page-term highlights in Render `PdfHighlights`.

- A PDF is shown one page at a time, with its own bar (Page: n of N, previous / next, Zoom out / in, Fit width, Fit page, percent). WinForms shows the PDF in its own viewer window. Page Down / Page Up / Home / End change the page; Cmd or Ctrl with the wheel zooms, a plain wheel scrolls.
- HTML documents are not shown inside the app: the window says "This document opens in your web browser." with an Open in browser button and the page opens in the system browser. Text documents are shown read-only in a monospace box.
- Chart and document windows float: separate windows owned by the main window, one per chart or document entry, bounds remembered per module ("chart:name|file", "doc:entry"); there are no MDI children and no docking. They close with the game.
- Rulebook search, the scan pane, image pane and OCR panel moved to stage 4a with the Rules Workbench (done there). The viewer itself has no search box; the search lives in the Workbench and the page is drawn with the term highlighted.
- OCR engine: Windows OCR won the benchmark, so stage 4a adds a Windows-only recognizer as a run-time plugin; Tesseract (English bundled, more languages from 4c) serves Mac and Linux. No OCR runs in 3b.
- The chart window's wheel zooms at the cursor (as the map view); drag pans with the left or middle button.
- Natives: PDFium, Tesseract, Leptonica and `tessdata/eng.traineddata` are in every publish folder; CI (`build/check-publish-natives.sh`) fails the publish step when one is missing. Loading them on macOS and Linux has only run in CI publishes so far, not on a real Mac or Linux desktop until the Mac checklist (`docs/port/stage3b-mac-checklist.md`) is done.

## Stage-4a deviations from the WinForms app (deliberate or known)

Stage 4a = Rules Workbench (Tables, Formulas, Properties, Phases, Alerts, Visibility), its dialogs, rulebook search, scan pane, Open scan..., OCR import. Settings are stage 4b (next section).

- The Workbench is a floating window owned by the main window (bounds remembered per module under `bench:workbench`), not an MDI child; it closes with the session.
- The OCR import's "where to put the table" question is an Avalonia dialog of its own (the WinForms one is a message-box style prompt).
- The edit grids have their own look (grey headers, white cells, blue current cell; cells are text blocks with an editor opened over the current one), not the DataGridView. Keyboard behaviour follows the WinForms grid; Tab out of the last cell, arrow keys inside an open combo dropdown and Ctrl/Cmd+C on cells are checked by hand on the Mac (checklist), not headless (cell copy is not built). Cell edits commit at the end of the edit (no 400 ms cell debounce); the notes boxes still debounce.
- Windows OCR runs through the `ocr-windows` plugin (`Quartermaster.OcrWindows`, never referenced by the Desktop project) and falls back to Tesseract with one log line when the plugin or an OCR language is missing; `QUARTERMASTER_OCR_ENGINE=tesseract|windows` overrides. Mac and Linux use Tesseract: English is bundled and 4c adds downloadable languages (Settings > OCR languages).
- Windows OCR lays the strips out with the gap after each cell (mode 2), which reproduces the WinForms per-strip result and meets the 24/24 gate; the per-cell code differs from WinForms (details in `docs/port/stage3b-ocr-benchmark.md`).
- Flipping through TIFF or scan pages briefly holds two bitmaps (the old one is released when the new one is shown).
- The FormulasTab test preview is embedded and preview-only: no Formula / Working group titles, no notes tooltip, no rule-ref button, no cheat sheet; the sample values typed into it are dropped when another formula is selected, and it is rebuilt on every pack commit.
- The Columns dialog drops all blank lines (WinForms: trailing ones only).
- Cmd instead of Ctrl in the texts and tooltips on macOS (Cmd+Shift+W, Cmd+Shift+F); the scan pane's zoom tooltips name Ctrl/Cmd literally.
- The guest (read-only) Workbench is tested through the guest flag only; online play reaches Desktop in stage 5, which must also close the Workbench before a received turn replays (WinForms MainForm.Turns).
- Screenshot pass (headless, `build/port-screenshots.ps1`): the Phases tab at 1100 px clips some button captions ("Duplicat", "Remov", "Move U"; the Tables tab's Rename button was widened to fit); the Tables shot shows "Choose a document above." in the left pane until a rulebook is chosen. Both are layout differences from the WinForms window, not data differences. The pass also found and fixed two bugs the headless tests could not see: grid cell text drawn invisible (a null local foreground; also in the Rule ref row and the alert match status) and fixed-width grids centred instead of left-aligned.
- CI publishes macOS and Linux Desktop builds only: there is no win-x64 Desktop publish step yet (the `ocr-windows` plugin is only checked by local publishes with `build/check-publish-natives.sh`).
- The Windows SDK projection DLLs shipped in `ocr-windows/` need their redistribution terms read before the next release (open user item).

## Stage-4b deviations from the WinForms app (deliberate or known)

Stage 4b = Tools > Settings (General, Game, Display, Map, Dice & Log, Toolbar, Keyboard, Folders), live application of every setting, File > Open Autosave..., physical dice entry, remembered difficulty and Outliner state. `settings.json` is read and written by the same `SettingsStore` as WinForms; no new fields.

- OCR language downloads are not in 4b: the WinForms original has none and the feature needs a network fetch. They are stage 4c (next section).
- Settings > Game shows Difficulty and its hint only. Where WinForms has the Players box there is a grey label "Players: comes with multiplayer (stage 5)"; Players is ported with `PlayersEditor` in stage 5. Done in 5b: the Players box is live.
- The counter palette is shown only in game master mode, as WinForms. Desktop has no `--gm` option: game master mode comes from the Settings > General checkbox only.
- The counter menu's Delete counter follows Settings > General game master mode and asks first per "Ask before deleting pieces", as WinForms.
- Toolbar tooltips show the shortcut, e.g. "1d6 (F5)", "LOS (L)"; WinForms toolbar tips have no key.
- General > "Check for updates on start" is shown and stored, but stays inactive until installing updates comes with stage 6 (5a uses it only with the test feed, see the Stage-5a deviations). "Select the last module in the launcher" is live since 5a: every module opened in a game window the launcher loop started is remembered (`LastModulePath`, as WinForms), and a fresh launcher selects it.
- Colours are chosen in a small own dialog (48 basic swatches like the WinForms colour dialog plus a #RRGGBB box), not the system colour dialog.
- On macOS a physical Control key (without Cmd) is not a shortcut modifier (Ctrl maps to Cmd); the capture dialog swallows it.
- The settings window is a modal Avalonia window with the same eight tabs as WinForms.
- Real macOS panels (Open Autosave start folder, number boxes, shortcut capture) are only checked by `docs/port/stage4b-mac-checklist.md`.

## Stage-4c deviations from the WinForms app (deliberate or known)

Stage 4c = OCR language downloads: Settings > OCR languages (last page), a catalog of the tessdata_fast 4.1.0 languages, download / remove with a SHA-256 check, Tesseract reading the downloaded files, display names and "More languages..." in the Workbench OCR panel. This is a new feature with no WinForms counterpart (WinForms reads with the OCR languages it already has). Formats are unchanged (rules pack 4, save 7, `settings.json` has no new field).

- Nothing touches the network until the user clicks Download. The only source is `https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/<code>.traineddata`; a file is used only when its length and SHA-256 match the catalog. Files live under the app data folder (`OcrLanguages`), never in the app folder. Bundled English cannot be removed.
- On Windows the Windows OCR plugin still reads the languages Windows has; a Tesseract download is used when Windows OCR lacks the language (the page says Windows languages are added in Windows Settings).
- `QUARTERMASTER_OCR_FEED=<folder>` replaces the network with a folder of `.traineddata` files (tests and screenshots).
- Screenshots use the real catalog with nothing downloaded; `build/port-screenshots.ps1` writes `stage4c-desktop-settings-ocr.png` and `stage4c-desktop-ocr-dropdown.png`. There are no WinForms equivalents.
- Real downloads, removal and the offline message on macOS are only checked by `docs/port/stage4c-mac-checklist.md`.

## Stage-5a deviations from the WinForms app (deliberate or known)

Stage 5a = app lifecycle: the launcher and Play offline windows, the launcher ↔ game loop with File > Back to launcher, Help > Interface Tour (and the automatic first tour), Help > About, and the update *screens* on a test feed. Formats are unchanged (rules pack 4, save 7, `settings.json` has no new field).

- The launcher's **Online lobby ▶** is shown, disabled, with the tooltip "Online play comes with stage 5c."; Play offline's **Open turn file…** is shown, disabled, with "Turn files come with stage 5d.".
- No update notices after an update (`--updated-from`, `--update-failed`, `--update-refused`, `UpdateStartup`): they belong to installing, which is stage 6. There is no `--smoke-launcher`; headless tests cover the launcher ↔ game loop instead.
- The update UI exists only while `QUARTERMASTER_UPDATE_FEED` points at a folder feed (tests and screenshots): without it there is no update bar, no "Check for updates" link and no Help > Check for updates…, so nothing can reach GitHub. Update now shows the Update window and stops with "Installing updates comes with the packaged release (stage 6).".
- Desktop update state (last check, skipped version) lives in `Updates-desktop/state.json` under the app data folder, not in the shared `settings.json`; `LastUpdateCheckUtc` and `SkippedVersion` there stay WinForms-only. "Check for updates on start" is read from `settings.json` as WinForms.
- **Play offline ▶** is disabled, with the "not ready" text as tooltip, while the selected module is still being checked (hashed); WinForms enables it and shows that text in a message box on click.
- The tour shows only the steps whose target is on screen, so the count is smaller when panels are hidden (for example "(1 of 7)" with the Outliner hidden, "(1 of 4)" without a rules pack).
- The automatic first tour starts 500 ms after the game opens (after the alert bar's 250 ms debounce, so the bar is on screen when the steps are chosen); WinForms starts it at once. As WinForms it may start after any module open (from the launcher, a module on the command line, or File > Open Module…), but only in a window the launcher loop opened, never in tests or screenshot runs.
- WinForms is unchanged.
- Real macOS menus (Cmd shortcut text), windows and the tour balloon's transparency are only checked by `docs/port/stage5a-mac-checklist.md`.

## Stage-5b deviations from the WinForms app (deliberate or known)

Stage 5b = hot-seat: the Players page of the new-game dialog, Settings > Game > Players, the handover screen, the status-bar player switcher, and every window following the current player's view. Formats are unchanged (rules pack 4, save 7, `settings.json` has no new field); a game saved with players opens with the same players in either app. The player at the screen is not saved: on load the handover goes to whoever acts now (both apps).

- **Game > Players… is not in 5b.** In WinForms it exists only while online (SeatsDialog); offline the list is edited in Settings > Game > Players, as in Desktop. It comes with 5c.
- **Turn games** (turn files stopping hot-seat, the turn view sides) come with 5d; Desktop has no turn files yet.
- **Online guards** in the Players edit: the guest refusal ("only the host...") is ported; the "While the game is online, change seats with Game → Players…." refusal sits behind a placeholder `IsOnline` that is always false until 5c. A guest session detaches hot-seat and restores the hidden windows (stand-in for WinForms StopHotSeatForOnline; 5c reuses it).
- **SidesWatch is not a class** (see its row): each Desktop tab subscribes to the view change itself.
- **No `--smoke-handover` / `--smoke-player-view`**: headless tests cover them. The screenshots come from `--screenshot-state handover | players-page | settings-players | player-menu`.
- **A Players page without two sides:** the page is still reachable with Next >, Add player is disabled, the WinForms hint is shown and the game is solo (WinForms parity).
- **A guest changing Players:** the host-only message appears twice on Apply (difficulty and players), as WinForms.
- **Hiding windows:** the handover hides the windows owned by or opened for this game (Desktop's window tracking); a launcher or another game's main window is untouched. A dialog owned by the hidden main window (the discard question) is re-owned by the handover screen.
- **Players box stretches to page width** (WinForms 440 px): Settings > Game > Players keeps the WinForms height (210) but fills the page, so a row's Remove button is never cut off.
- **A stray close of the handover screen** (Cmd+Q / OS shutdown closes it for real, past the close interception): the hidden windows come back for the main window's own close question; if the app does not end (No), the handover screen comes back for the pending player.
- The handover screen uses the main window's current bounds, not its restore bounds when that window is maximised or minimised (WinForms RestoreBounds); a maximised main window gives a maximised screen.
- The editor and the handover screen live in `Quartermaster.Desktop.Shell` (files under `Players/`), the models in `Quartermaster.Render` (a `.Players` namespace would shadow Core `Players`). WinForms is unchanged (the optional refactor onto the shared models was skipped).
- Real macOS behaviour (nothing but the handover screen visible in Mission Control and Cmd+Tab, Cmd+Q on the screen) is only checked by `docs/port/stage5b-mac-checklist.md`.

## Before the stage release

Headless tests (Avalonia.Headless) draw without a GPU, so they do not cover the real rendering paths. Before stage 1 is released:

- Add a software-rendering fallback option (for example a command-line switch or environment variable that makes Avalonia render with its software/CPU renderer) for machines whose GPU driver fails.
- Linux manual smoke: on a real Linux desktop (X11 and Wayland if possible), open the Riverland demo, start a game, pan and zoom the map, drag a counter, drop one from the palette, save and reopen. This covers the GPU rendering, fonts and input paths no headless test reaches.

## Menu bar: File

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| Open Module... | 1 | ✔ | ✔ | ✔ | Ctrl/Cmd+O |
| New Game | 1 | ✔ | ✔ | ✔ | Ctrl/Cmd+N |
| Load Setup... | 1 | ✔ | ✔ | ✔ |  |
| Open Game... | 1 | ✔ | ✔ | ✔ | Ctrl/Cmd+G |
| Open Autosave... | 4b | ✔ |  |  | File > Open Autosave...: the picker starts in this module's autosave folder (or the autosave root); the file opens as Open Game |
| Open Turn File... | 5 |  |  |  |  |
| Save Game | 1 | ✔ | ✔ | ✔ | Ctrl/Cmd+S |
| Save Game As... | 1 | ✔ | ✔ | ✔ | Ctrl/Cmd+Shift+S |
| Export Viewer Copy... | 5 |  |  |  |  |
| New Rules Pack... | 2a | ✔ |  |  |  |
| Open Rules Pack... | 2a | ✔ |  |  |  |
| Save Rules Pack / Save Rules Pack As... | 2a | ✔ |  |  |  |
| Back to launcher | 5a | ✔ |  |  | closes the game (unsaved-changes prompt as Exit) and shows the launcher with that module selected; also on a game started with a module on the command line; no shortcut (as WinForms); hidden in screenshot runs and tests that start the window outside the launcher loop |
| Exit | 1 | ✔ | ✔ | ✔ | asks about unsaved changes, as WinForms |

## Menu bar: Edit, View, Game, Tools, Window, Help

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| Edit > Undo / Redo (caption follows the history) | 1 | ✔ | ✔ | ✔ | Ctrl/Cmd+Z, Ctrl/Cmd+Y, Ctrl/Cmd+Shift+Z |
| Edit > Step Back to Start of Phase / Step Back To... | 2b | ✔ |  |  | after Redo, as WinForms; Ctrl+Left (Cmd+Left on macOS) steps back to the start of the phase after the confirm dialog; Step Back To... lists the checkpoints; off with the WinForms reason tooltip (guest, replay, turn already sent, old save without history); autosaves first, closes the formula windows and batch grids, status "Stepped back — reopen from the phase panel"; Redo works afterwards |
| View > Show Grid | 1 | ✔ | ✔ | ✔ |  |
| View > Zoom to Fit, Zoom 100%, Zoom > 25%, 50%, 100%, 200%, Fit | 1 | ✔ | ✔ | ✔ | labels from the map context menu and toolbar |
| View > Module Tree | 2b | ✔ |  |  | checked by default; hides the left column (tree over tables tree) |
| View > Diagnostics | 3a | ✔ |  |  | Ctrl/Cmd+D; needs a module; opens the one Diagnostics window (also from the toolbar button and the status-bar link) |
| View > Log | 2a | ✔ |  |  | Ctrl+Shift+L; docked at the bottom (170 high, resizable) or floating (Pop out / Dock); while floating it docks back |
| View > Phase panel | 2b | ✔ |  |  | Docked at the right (320 wide, resizable), checked while shown or floating; while floating it docks the panel back |
| View > Outliner | 3a | ✔ |  |  | hidden by default (WinForms OutlinerVisible); docked at the right of the maps after the phase panel (440 wide, resizable) or floating; checked while shown or floating; while floating it docks back |
| View > Ledger | 3a | ✔ |  |  | Ctrl/Cmd+L; needs a rules pack; opens the one Ledger window (owned, not modal, bounds under "doc:ledger"); with counters selected on the active map it is Show in Ledger |
| Show in Ledger (map → ledger row, Ctrl/Cmd+L) | 3a | ✔ |  |  | counter menu "Show in Ledger" (after a separator; disabled with "The Ledger needs a rules pack (File > Open Rules Pack… or New Rules Pack…)." without one) and View > Ledger / Ctrl/Cmd+L with counters selected on the active map: opens the Ledger, moves to the tab that shows them (clearing the filter when it hides them, shared `LedgerJump`), selects their rows and scrolls to the first; status "Shown in the Ledger." / "{n} counters shown in the Ledger." or the reason some have no row (hidden counters never get one) |
| View > Overview | 3a | ✔ |  |  | needs a game; opens the one Overview window (also from the toolbar button between Notes and Inventory) |
| View > Counter palette (GM only) | 1 | ✔ |  |  | shown only when a game is open and game master mode is on (Settings > General, as WinForms); game master mode only since 4b — re-check on macOS and Linux |
| Game > Send Turn..., Players..., Leave Online Game | 5 |  |  |  | open: Players… exists in WinForms only while online (SeatsDialog, 5c); offline the list is edited in Settings > Game > Players (5b) |
| Tools > Rules Workbench | 4a | ✔ |  |  | Cmd/Ctrl+Shift+W; one floating window per session (bounds under `bench:workbench`), refused while a received turn replays |
| Tools > Inventory, Notes | 3a | ✔ |  |  | need a game; one window each, owned by the main window, not modal |
| Tools > Dice (1d6, 2d6, module dice, Custom, Multi-roll) | 2a | ✔ |  |  | F5, F6; rolls go through the shared DiceTurnActions (Render), so the log lines are the WinForms ones; the roll's line shows in the status bar for 5 s. Physical dice entry (Settings > Dice & Log) asks for each die by hand with "Enter a whole number from 1 to N."; Cancel records nothing; the last roll and the default are remembered |
| Tools > Turn (Next, Previous, Set) | 2a | ✔ |  |  | Ctrl+Right (Cmd+Right on macOS) steps the turn; with a phased rules pack it is Next Phase, as WinForms (2b). Turn label in the status bar |
| Tools > Settings... | 4b | ✔ |  |  | Ctrl+, (Cmd+, on macOS); opens the Settings window over the shared SettingsStore; a failed save is shown |
| Window > Cascade, Tile, Close All, Pop Out, floating list | - |  |  |  | Desktop uses tabs, not MDI; not ported in 2b (only the log and phase panel pop out) |
| Help > Interface Tour, Check for updates, About | 5a | ✔ |  |  | Interface Tour needs a game; Check for updates… is shown only with the test feed (`QUARTERMASTER_UPDATE_FEED`) and, in a game, offers to go back to the launcher (WinForms text); About shows "Quartermaster <version>" and "A VASSAL module reader and engine." |

## Main window and shell

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| MainForm: toolbar | 2a | ✔ |  |  | WinForms order: Open Module, New Game, Load Setup, Open Game, Save Game, LOS, Undo, Redo, ◀ Step Back, 1d6, 2d6, Dice, Next turn; gated by CommandAvailability (Undo / Redo / Step Back by the history, with the WinForms tooltips). Diagnostics, Notes and Inventory follow Save Game; Overview follows Notes, Palette is left out until its stage; buttons are hidden and shown live from Settings > Toolbar; tooltips show the shortcut, e.g. "1d6 (F5)" (WinForms tips have no key) |
| MainForm: status bar (module, message, tiling progress, location, zoom) | 1 | ✔ | ✔ | ✔ |  |
| MainForm: title text (name, version, file, unsaved star, rules name) | 1 | ✔ | ✔ | ✔ |  |
| MainForm: unsaved-changes prompt on close, Abandon replay prompt | 1 | ✔ | ✔ | ✔ |  |
| MainForm: error guard (warning box on a bad file) | 1 | ✔ | ✔ | ✔ |  |
| MainForm: command line <vmod> --setup N / --new | 1 | ✔ | ✔ | ✔ | --rules and --new-rules too (stage 2a); the other options come later |
| MainForm: shortcut router (Cmd on macOS) | 1 | ✔ | ✔ | ✔ | keys are user-editable in Settings > Keyboard and apply at once to the key dispatch, menu gestures and toolbar tooltips; unknown override ids in `settings.json` are kept on save |
| ModuleTreePanel | 2b | ✔ |  |  | map nodes activate the tab; setup nodes open the setup chooser (no preselect yet); chart nodes open their chart window (3b); document nodes open their document window, one per document; rebuilds when the viewer changes |
| TablesTreePanel | 2b | ✔ |  |  | Tables and Formulas folders, refreshed 200 ms after rules changes; double-click opens the Workbench on that table or formula (4a); split with the module tree remembered (ui.json) |
| DocumentTabStrip (map tabs) | 1 | ✔ | ✔ | ✔ | one tab per visible map |
| WindowManager (floating windows) | - |  |  |  |  |
| AlertBar | 2b | ✔ |  |  | "What now" banner (click reveals the phase panel), severity chips with the "+N" overflow menu, chip menu (matches, Hide until next turn), notice chip (click does nothing until stage 5); holder matches open the Ledger and select the holder's row (Depots & Global tab, filter cleared when it hides it; never the counters selected on the map); shown when a game is open and Settings > Display "Alert bar" is on (applies live) |
| TourOverlay | 5a | ✔ |  |  | classic tooltip-yellow balloon with an arrow toward the step's target; Back / Next / Skip tour, Esc skips; steps whose target is hidden are left out; starts once by itself on the first game opened in a launcher-loop window (from the launcher, the command line or File > Open Module…; `TourShown`); without a compositor the arrow strip and corners take the control colour instead of black; closes when the game is replaced or the module closes |
| PhaseStrip, PhasePanel, PhaseDock, PhaseFloatForm | 2b | ✔ |  |  | Strip and docked panel done (Next Phase button runs the shared Next Phase flow with the open-decisions question and phase-boundary autosave). Pop out / Dock moves the panel to a floating "Phases" window (default beside the main window's right edge, bounds remembered per module under "phases", fitted onto a screen that exists with the primary screen as fallback); closing that window docks the panel back, shown; rule reference link opens the document at its page (3b) |
| NextPhaseDialog | 2b | ✔ |  |  | Continue anyway / Go back (Enter goes back); double-click jumps to the decision. Phase-boundary autosave interval comes from Settings > General (autosave minutes) |
| StepBackDialog | 2b | ✔ |  |  | "Step Back to Start of Phase" (confirm text and summary) / "Step Back To" (Turn, Stage, Side, Phase, Reached, Actions; newest first, summary follows the selection, double-click picks); Step Back / Cancel |
| ReplayBar | 5 |  |  |  |  |
| UpdateBar, UpdateDialog, WhatsNewDialog, AppUpdates | 5a | ✔ |  |  | screens only; installing is stage 6. Only with the test feed (`QUARTERMASTER_UPDATE_FEED`): the launcher's bar (What's new / Update now / Later / Skip this version), Check for updates link, What's new window, Update window; Update now stops with "Installing updates comes with the packaged release (stage 6)." |

## Map and pieces

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| MapWindow: map toolbar (Line of sight, Last moved, Hide counters, Centre on selected, Zoom) | 2a | ✔ |  |  | one toolbar above the tabs acting on the selected map and Save map as picture… |
| MapWindow: one map per tab, board tiles prepared in the background, placeholder on failure | 1 | ✔ | ✔ | ✔ |  |
| MapCanvas: pan (drag, wheel, trackpad) | 1 | ✔ | ✔ | ✔ | mac wheel = pan, Cmd+wheel = zoom |
| MapCanvas: zoom (wheel, pinch, menus) | 1 | ✔ | ✔ | ✔ |  |
| MapCanvas: click select, Ctrl/Cmd+click multi-select, rubber band | 1 | ✔ | ✔ | ✔ |  |
| MapCanvas + MapDragController: drag, snap, drop, stacking | 1 | ✔ | ✔ | ✔ |  |
| MapCanvas: Esc cancels a drag | 1 | ✔ | ✔ | ✔ |  |
| MapCanvas: view refresh, selection prune and drag cancel on game change or rewind | 1 | ✔ | ✔ | ✔ |  |
| MapCanvas: cursor location and zoom text in the status bar | 1 | ✔ | ✔ | ✔ |  |
| Grid overlay and grid location names | 1 | ✔ | ✔ | ✔ |  |
| PaletteDock / PalettePanel: search, folder tree, counter thumbnails | 1 | ✔ | ✔ | ✔ |  |
| PaletteDrop: drag a counter onto a map | 1 | ✔ | ✔ | ✔ |  |
| MapCanvas: hover stack popup (CounterDetailPopup) | 2a | ✔ |  |  | same layout and pixels as WinForms (shared `CounterPopupLayout`); shown in the window overlay, so it stays inside the map window rather than the screen; hover delay from Settings > Map (applies live to every map tab) |
| MapCanvas: hover-stack highlight | 2a | ✔ |  |  |  |
| MapCanvas: double-click stack expand | 2a | ✔ |  |  | online guests send it through their action port, as WinForms |
| MapCanvas: right-click and Ctrl+click context menu | 2a | ✔ |  |  | counter menu from the shared PieceMenuModel; empty-map menu ends with Save map as picture…; macOS Ctrl+click opens it; Calculate opens the formula window (2b); Show in Ledger opens the Ledger at the counters' rows (3a) |
| MapCanvas: Line of sight tool (LosTool), Esc cancels LOS | 2a | ✔ |  |  | L key or toolbar; left-drag measures, Esc or right-click cancels; drawn in the view's picture so it stays sharp on HiDPI; on macOS Ctrl+click counts as right-click |
| MapCanvas: Hide counters (H) | 2a | ✔ |  |  | H key, toolbar and the empty-map menu; draws the board only (all counters) and clears the selection, as WinForms |
| MapCanvas: Last moved highlight | 2a | ✔ |  |  | toolbar toggle; follows moves, undo and new games |
| Cross-window drag preview and drops (OLE) | - |  |  |  | Windows-specific |
| PropertySheetDialog | 2a | ✔ |  |  |  |
| BreakdownTip | 2a | ✔ |  |  | on the popup's ledger lines; the ledger and inventory grid tips are in the Ledger and Inventory windows (3a) |
| CounterDetailPopup interactive ledger lines | 2a | ✔ |  |  | click-through away from the lines, as WinForms |
| InputDialog | 2a | ✔ |  |  | also the rules-pack name prompt |
| ExportChoiceDialog, ExportProgressDialog, MapExport (save the map as a picture) | 3a | ✔ |  |  | choice (current view / whole map at 25, 50, 100%), file picker, progress dialog with Cancel; one export at a time; cancelled when the game or the main window closes; whole-map render off the UI thread via the shared MapExportFlow |
| OverviewWindow | 3a | ✔ |  |  | floating owned window (260 x 240 at Left+Width-300, Top+110, or the bounds saved under doc:overview); follows the active map tab; coarsest tile level, grey where a board has no tiles, red viewport rectangle; click or drag centres the map; always floating (no MDI docking) |

## New game dialog

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| SetupChooserDialog: New game from module + setups, difficulty, double-click = OK | 1 | ✔ | ✔ | ✔ | difficulty remembered since 4b; Next > opens the Players page since 5b (Back / OK / Cancel there) |
| PlayersEditor | 5b | ✔ |  |  | one row per player (name, a check box per side, Remove), Add player, the WinForms hint; used on the new-game Players page and in Settings > Game > Players (210 high, as wide as the page; WinForms 440×210). Row rules and texts are shared in Render (`PlayersEditModel`). `build/port-screenshots.ps1` writes stage5b-desktop-players-page.png and stage5b-desktop-settings-players.png |
| Remembered rules pack per module (RulesMemory) | 2a | ✔ |  |  | the pack is loaded with the module; a missing file is ignored |

## Rules, formulas, dice, log

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| FormulaDialog, FormulaFormView, CheatSheetWindow | 2b | ✔ |  |  | non-modal window owned by the main window, one per formula, unit and run; Calculate in Hardcore and Semi-auto, cheat sheet beside it in Semi-auto; online guests ask the host; closes when its run or unit is gone; Rule ref enabled while the reference resolves, opens the document at its page (3b); the window rules are shared in Render FormulaWindowRules |
| BatchGridWindow, MissingInputDialog | 2b | ✔ |  |  | batch grid is a non-modal window per run (virtualised rows, choice columns are editable dropdowns, Calculate all / Apply all / Apply row, cheat sheet docked in Semi-auto, Ledger breakdown tip); stage 3a: the unit's counter picture (64×48) in the Unit cell, drawn on a worker from the viewed state only (dotted placeholder while drawing, nothing for hidden units), rows 52 px, Unit column 150 + 72, hovering the picture shows the counter preview with the unit's name; queued runs open them through the shared run dispatch (several units: the batch grid, double-click a row for its unit window; fully automatic: the missing-input dialog, "Per unit…" opens the batch grid or the unit window); nothing opens for a run that is no longer pending; OK in the missing-input dialog needs non-blank answers only, as WinForms |
| CounterPreviewTip, CounterCellImages (counter preview and list thumbnails) | 3a | ✔ |  |  | shared `CounterThumbnails` cache (worker-drawn, visible rows first, 600 cap, shown bitmaps never disposed; the palette uses it with its own 3000 cap) and `CounterPreview` popup (no activation; size from `CounterImages.PreviewSize`, placed by the shared `PreviewPlacement`: cursor + 16, flipped at the screen edges); only pieces in the viewed state; MapView exposes `Tiles` and `Snapshot()` (same pixels as on screen) for map export |
| DiceTools, DiceDialog, TurnDialog | 2a | ✔ |  |  | the UI-free part is Render DiceTurnActions, used by both apps |
| LogPanel, LogDock, LogFloatForm, ChatBox | 2a | ✔ |  |  | kind toggles, search, Copy, Export log (txt/html, one picker filter), virtualised list, double-click centres the piece; row logic shared in Render LogPanelRules. Pop out / Dock moves it to a floating window (bounds remembered per module); the chat line shows only online (stage 5) |
| OutlinerPanel, OutlinerDock, OutlinerFloatForm | 3a | ✔ |  |  | Now list (double-click jumps to the decision), Group: By map / org display or By side, folders / groups / pieces with 24x18 counter pictures (visible pieces only), units and aggregated property columns with breakdown tips and header notes, alert dots from the shared alert hub, column widths kept per column, expanded state kept across reloads, refresh put off while hidden. Pop out / Dock moves it to a floating "Outliner" window (bounds remembered per module under "outliner"). Visibility and grouping are kept between runs in `settings.json` |
| PhaseWatcher | 2b | ✔ |  |  | `PhaseWatch` + `Debouncer` (DispatcherTimer, 150 ms) |

## Ledger, notes, inventory, documents, search

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| LedgerWindow (+ Tabs, Counters), EditTabsDialog, CounterPreviewTip | 3a | ✔ |  |  | "Ledger" 820×560: Filter:, Side: (All sides + the pack's sides), Group by: Map/Side/None, Show counters, Edit tabs…, Export CSV…, Show units without values, Refresh; tabs "Units (n)", "Depots & Global (n)", "Everything", "★ name" (notes as tooltip), "+"; broken user-tab filter banner; columns Name, Kind, one per property (notes as header tooltip; click sorts within groups, blanks last; drag the header edge to resize); virtualised rows; 64×48 counter pictures drawn on a worker from the viewed state only (rows on screen first, 600 kept), hover preview, double-click a unit row's counter picture centres its piece (Show counters on); hover a value for its breakdown; double-click, F2 or typing edits a value (number parsed invariant then the user's locale, required note, other side's values refused); CSV in UTF-8 with BOM; prefs per module via LedgerTabStore; reloads (debounced 200 ms) on game, rules and viewer changes. Edit tabs… and "+" open "Edit Ledger tabs" (720×460, min 620×400): list with Add / Remove / Move up / Move down, Name:, Unit filter: (parse errors in red below; "Empty means all units. Example: Fuel < 5"), Columns: ticked and ordered with Up / Down ("None ticked means the same columns as the Units tab."; holder columns marked), "Include depots & Global", Notes:; problems listed in red, OK refused while any; "+" opens it with a fresh "New tab" and shows that tab after OK; other Ledger windows of the module follow the saved list (LedgerTabStore.Changed) |
| NotesWindow, InventoryWindow | 3a | ✔ |  |  | Notes: monospace box, commits after 1 s, on leaving the box and on close; the main window flushes it before the game closes or is replaced. Inventory: group by name / map / marker / ledger property, filter (300 ms), Total hover tip, double-click steps through the group's pieces; counts only what the viewer may see |
| ChartWindow | 3b | ✔ |  |  | floating window owned by the main window, one per chart (name + file), titled with the chart name; toolbar "Fit" and "100%" ("100%" is 1 image pixel per DIP, 2 device pixels on a Retina screen, same as the map view; the image is fetched and sampled at zoom x screen scaling); the picture on the workspace grey, linear sampling (mipmaps below 100 %); first layout fits; wheel zooms ×1.25 / ×0.8 at the cursor; left or middle drag pans (size-all cursor); bounds remembered per module under "chart:name|file"; all close with the game |
| PdfWindow, PdfViewerControl | 3b | ✔ |  |  | DocumentWindow hosts the DocumentViewer without its toolbar: one window per document entry owned by the main window, bounds remembered per module under "doc:{entry}", closed with the session; opened from the module tree or a Rule ref (OpenDocumentAtPage reuses the window and moves the page) |
| DiagnosticsWindow | 3a | ✔ |  |  | Severity / Category / Message / Context, Show filter, Copy all (clipboard), live updates from any thread; the status-bar link ("n warning(s)/error(s) - click here to view diagnostics") opens it |
| SearchPanel, ScanPane, ImagePane, OcrPanel | 4a | ✔ |  |  | Workbench tools (4a): rulebook search with highlights and Use as rule ref, Scan this page, Open scan… (image, multi-page TIFF, PDF; an opened PDF joins the search and its refs are `scan:{file}`), region read, grid fix, Import into table. OCR engine per OS behind `IOcrRecognizer`: Windows OCR through the `ocr-windows` plugin (24/24 on the invented pages against Tesseract 21/24), with the fallback rules in `docs/port/stage3b-ocr-benchmark.md`; Tesseract (English bundled; other languages downloaded in Settings > OCR languages since 4c) on Mac and Linux |

## Rules Workbench and Settings

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| WorkbenchWindow and tabs (Tables, Formulas, Properties, Phases, Alerts, Visibility) | 4a | ✔ |  |  | Tables-tree activation, problems list, rule ref row, Find in rulebook; headless-tested incl. guest read-only (guest flag only until stage 5 brings online to Desktop). `build/port-screenshots.ps1` writes stage4a-desktop-workbench*.png and the WinForms equivalents |
| ColumnsDialog, PasteTableDialog, BlockDialog | 4a | ✔ |  |  | plus the import-target dialog of the OCR import (own Avalonia dialog, see the 4a deviations) |
| SettingsDialog and SettingsTabs (General, Game, Display, Map, Dice and Log, Toolbar, Folders) | 4b | ✔ |  |  | Pages General, Game, Display, Map, Dice & Log, Toolbar, Keyboard, Folders (the Keyboard page is a tab of its own as in WinForms); OK / Apply / Cancel / Restore defaults as WinForms; every setting applies live to the open windows. Game page: Difficulty and Players (5b); the OCR languages page was added in 4c |
| OCR languages page (Settings), OCR panel language list | 4c | ✔ |  |  | New in 4c, no WinForms counterpart: download / remove tessdata_fast 4.1.0 languages (SHA-256 checked, nothing without a click), "More languages..." in the OCR panel opens the page; see the Stage-4c deviations |
| KeyboardTab, ShortcutCaptureDialog | 4b | ✔ |  |  | Set... / Clear / Reset / Reset all, conflict and reserved-key texts as WinForms; capture shows Ctrl+... (Cmd... on macOS), a physical Control key alone is swallowed on macOS |

## Multiplayer and launcher

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| LauncherForm, PlayOfflineForm | 5a | ✔ |  |  | app start without arguments shows the launcher (module list with hashing progress, Add module… / Remove, rules pack per module with change… / use (none), Play offline ▶); Play offline: Continue (newest autosave), recent games, New game…, Open saved game…, Open selected, Back; Online lobby ▶ and Open turn file… are shown disabled (stages 5c, 5d). `build/port-screenshots.ps1` writes stage5a-desktop-launcher / offline / update-bar / tour.png |
| HandoverForm (hot seat) | 5b | ✔ |  |  | "Pass to Anna (Red)" + Continue, in the main window's place with every other Quartermaster window hidden; closing it asks the main window's discard question; waits for an open modal dialog. stage5b-desktop-handover.png |
| OnlineLobbyForm, JoinDialog, HostSavedDialog, HostSidesPanel | 5 |  |  |  |  |
| SeatsDialog, SideChoiceDialog, SendTurnDialog, SendWaitForm, WhoAreYouDialog, MailboxOptions | 5 |  |  |  |  |
| SidesWatch | 5b | ✔ |  |  | not a class: each Desktop window and Workbench tab follows `ViewChanged` (coalesced through the existing post pattern), so every window shows only the current player's view |
| Status bar player item (hot seat) | 5b | ✔ |  |  | "Player: Anna ▾" at the right of the status bar, only in a hot-seat game; the menu lists the players with the current one ticked, a choice hands over. stage5b-desktop-player-menu.png |

## Shared behaviour that must stay identical

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| Saves open in both apps (ModuleSession.SaveNative / LoadNative) | 1 | ✔ | ✔ | ✔ | same calls; cross-app open is on the Mac checklist |
| Same classic grey look, one theme on all systems | 1 | ✔ | ✔ | ✔ | side-by-side shots: build/port-screenshots.ps1 |
| Desktop references no WinForms, WebView2, WinRT, App or Ocr project | 1 | ✔ | ✔ | ✔ | project file |

