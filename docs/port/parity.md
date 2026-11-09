# Cross-platform parity checklist (WinForms app vs Quartermaster.Desktop)

Every window, menu command and feature of the WinForms app (`src/Quartermaster.App`), with the stage of the port plan that brings it to the Avalonia app (`src/Quartermaster.Desktop`). Class names follow the table in `docs/wiki/Menus-and-Windows.md`.

How to read the columns:

- **Windows**: ticked when Desktop covers the row and headless tests (Avalonia.Headless) pass on Windows in this branch.
- **Linux**: ticked only for features verified by tests actually run on Linux. Stage 1 rows: verified by the headless tests in the CI workflow `.github/workflows/cross-platform.yml` on ubuntu-latest (green run 42510235329, 2026-11-08). Not yet opened on a real Linux desktop.
- **Docs.Tests on Linux**: pass on ubuntu-latest in CI (run 42510235329).
- **macOS**: ticked only after the user has worked through the stage's Mac checklist on the M1 Mac (`docs/port/stage1-mac-checklist.md`, `docs/port/stage2a-mac-checklist.md`, `docs/port/stage2b-mac-checklist.md`). Stage 1 rows: all checklist steps OK on the user's M1 Mac (2026-11-08), including real GPU rendering.
- **Stage**: 1 = shell, map, pieces; 2a = rules packs, map tools, dice, turn, log; 2b = formulas, phases, step back, module tree, alerts (this branch); 3 ledger, notes, inventory, documents, search; 4 workbench and settings; 5 multiplayer, launcher, updates; `-` = no plan yet or not applicable off Windows. The stage numbers after 2a are provisional.

## Stage-1 deviations from the WinForms app (deliberate or known)

- The counter palette is always visible once a game exists. WinForms shows it only in game master mode (View > Counter palette).
- The new game dialog has no Players page (comes with multiplayer).
- The difficulty chosen in the new game dialog is not saved as the default for next time (comes with Settings).
- Mouse wheel on macOS pans and Cmd+wheel zooms (deliberate: trackpad convention). On Windows and Linux the wheel zooms, as WinForms. Pinch zoom is for macOS trackpads.
- The look is the classic grey theme on all three systems, with Tahoma 11px where present, else Verdana, DejaVu Sans.
- Shortcuts use Cmd instead of Ctrl on macOS. There is no native macOS menu bar; the menu bar sits in the window.
- Saves are written and read by the same ModuleSession.SaveNative / LoadNative calls as WinForms, so each app opens the other's saves. Desktop tests cover the Desktop side; open a WinForms save in Desktop (and back) is on the Mac checklist.

### Map behaviour not yet ported

- Dragging between windows: the OLE drag preview in the target window and drops across windows (a drop released outside the view is cancelled).
- Applying Settings to the map (wheel step, drag threshold, hover delay, highlight colour, label size, snap, grid default).

## Stage-2a deviations from the WinForms app (deliberate or known)

Stage 2a = rules packs, map tools (menu, hover popup, LOS, hide, last moved), dice and turn tools, main toolbar, log panel. The 2b items are in the next section.

- The hover popup and the breakdown tip are overlays inside the window, so they never leave it and flip sooner near the window edge. WinForms uses screen-sized popups.
- The hover delay is fixed at 600 ms until Settings (stage 4).
- The log export picker has a single filter, "Log files (*.txt;*.html)" (default .txt); the format follows the extension. WinForms has two filters.
- There is no chat line offline (WinForms shows it only online); the box comes with multiplayer (stage 5).
- The main toolbar shows only the buttons whose features exist in Desktop (no Overview, Palette yet; Diagnostics, Notes and Inventory are in, in the WinForms order). Hiding buttons via Settings is stage 4.
- The map toolbar sits inside each map tab (WinForms: one per map window); "Save map as picture" waits for stage 3.
- The counter menu has no Show in Ledger until the Ledger (stage 3); no Delete counter until the game master setting exists.
- Physical dice wait for Settings (stage 4); rolls and log lines are the shared WinForms ones.
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
- Rule ref (formula window, phase panel, rule links) is shown disabled with the tooltip "Rulebook viewer comes in a later version" until the rulebook viewer (stage 3).
- Changing the difficulty in mid-game needs Settings (stage 4); a game keeps the difficulty it was started with. The windows follow the mode when it changes (tests drive it through the shared controller).
- Holder alert matches ("Open the Ledger to see {name}") select nothing until the Ledger window exists (stage 3).
- The notice chip on the alert bar is drawn, but clicking it does nothing until multiplayer (stage 5).
- The map-tab pop-out and the Window menu items (Cascade, Tile, Pop Out Active Window, floating windows list) are not ported; Desktop uses tabs. Only the log and the phase panel pop out.
- The phase-boundary autosave uses the WinForms default interval (5 minutes) until Settings (stage 4) offers the choice.
- The formula window, cheat sheet, batch grid and dialogs are Avalonia windows owned by the main window; their placement is the platform's (centred on the owner), and the cheat sheet opens beside the formula window.
- Tables-tree activation is ignored until the Rules Workbench (stage 4); the PDF and chart nodes of the module tree say "Opens in a later version".
- Floating log and phase windows are clamped onto a screen that exists (primary screen as fallback). Real second-display behaviour is only checked by the Mac checklist (`docs/port/stage2b-mac-checklist.md`).
- Open Autosave, Game > Send Turn and the other WinForms menus around phases wait for their own stages (4 and 5).
- When a rules pack without phases replaces one with phases while the phase panel floats, Desktop docks the panel back (the column stays hidden); WinForms leaves the floating window open showing the no-phases text.
- Known: while a Desktop modal dialog (Step Back, Next Phase, missing input) is open, the floating Phases window and the formula and batch-grid windows still accept input (Avalonia ShowDialog disables only the owner); WinForms disables every window of the app.
- Floating log and phase panels always reopen docked: the layout memory never stores Floating=true, the same as WinForms.
- Fixed in the 2b screenshot pass: the alert bar's hidden measuring chip ("+00") was visible at the left of the banner text.

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
| Open Autosave... | 4 |  |  |  |  |
| Open Turn File... | 5 |  |  |  |  |
| Save Game | 1 | ✔ | ✔ | ✔ | Ctrl/Cmd+S |
| Save Game As... | 1 | ✔ | ✔ | ✔ | Ctrl/Cmd+Shift+S |
| Export Viewer Copy... | 5 |  |  |  |  |
| New Rules Pack... | 2a | ✔ |  |  |  |
| Open Rules Pack... | 2a | ✔ |  |  |  |
| Save Rules Pack / Save Rules Pack As... | 2a | ✔ |  |  |  |
| Back to launcher | 5 |  |  |  |  |
| Exit | 1 | ✔ | ✔ | ✔ | asks about unsaved changes, as WinForms |

## Menu bar: Edit, View, Game, Tools, Window, Help

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| Edit > Undo / Redo (caption follows the history) | 1 | ✔ | ✔ | ✔ | Ctrl/Cmd+Z, Ctrl/Cmd+Y, Ctrl/Cmd+Shift+Z |
| Edit > Step Back to Start of Phase / Step Back To... | 2b | ✔ |  |  | after Redo, as WinForms; Ctrl+Left (Cmd+Left on macOS) steps back to the start of the phase after the confirm dialog; Step Back To... lists the checkpoints; off with the WinForms reason tooltip (guest, replay, turn already sent, old save without history); autosaves first, closes the formula windows and batch grids, status "Stepped back — reopen from the phase panel"; Redo works afterwards |
| View > Show Grid | 1 | ✔ | ✔ | ✔ |  |
| View > Zoom to Fit, Zoom 100%, Zoom > 25%, 50%, 100%, 200%, Fit | 1 | ✔ | ✔ | ✔ | labels from the map context menu and toolbar |
| View > Module Tree | 2b | ✔ |  |  | checked by default; hides the left column (tree over tables tree) |
| View > Diagnostics | 3 | ✔ |  |  | Ctrl/Cmd+D; needs a module; opens the one Diagnostics window (also from the toolbar button and the status-bar link) |
| View > Log | 2a | ✔ |  |  | Ctrl+Shift+L; docked at the bottom (170 high, resizable) or floating (Pop out / Dock); while floating it docks back |
| View > Phase panel | 2b | ✔ |  |  | Docked at the right (320 wide, resizable), checked while shown or floating; while floating it docks the panel back |
| View > Outliner | 3 |  |  |  |  |
| View > Ledger | 3 | ✔ |  |  | Ctrl/Cmd+L; needs a rules pack; opens the one Ledger window (owned, not modal, bounds under "doc:ledger"); the jump to the counters selected on the map comes with Show in Ledger |
| Show in Ledger (map → ledger row, Ctrl/Cmd+L) | 3 |  |  |  |  | comes with the Desktop Ledger; decision logic already shared (`LedgerJump` in Quartermaster.Render) |
| View > Overview | 3 |  |  |  |  |
| View > Counter palette (GM only) | 1 | ✔ | ✔ | ✔ | always visible in Desktop |
| Game > Send Turn..., Players..., Leave Online Game | 5 |  |  |  |  |
| Tools > Rules Workbench | 4 |  |  |  |  |
| Tools > Inventory, Notes | 3 | ✔ |  |  | need a game; one window each, owned by the main window, not modal |
| Tools > Dice (1d6, 2d6, module dice, Custom, Multi-roll) | 2a | ✔ |  |  | F5, F6; rolls go through the shared DiceTurnActions (Render), so the log lines are the WinForms ones; the roll's line shows in the status bar for 5 s. Physical dice wait for Settings (stage 4) |
| Tools > Turn (Next, Previous, Set) | 2a | ✔ |  |  | Ctrl+Right (Cmd+Right on macOS) steps the turn; with a phased rules pack it is Next Phase, as WinForms (2b). Turn label in the status bar |
| Tools > Settings... | 4 |  |  |  |  |
| Window > Cascade, Tile, Close All, Pop Out, floating list | - |  |  |  | Desktop uses tabs, not MDI; not ported in 2b (only the log and phase panel pop out) |
| Help > Interface Tour, Check for updates, About | 5 |  |  |  |  |

## Main window and shell

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| MainForm: toolbar | 2a | ✔ |  |  | WinForms order: Open Module, New Game, Load Setup, Open Game, Save Game, LOS, Undo, Redo, ◀ Step Back, 1d6, 2d6, Dice, Next turn; gated by CommandAvailability (Undo / Redo / Step Back by the history, with the WinForms tooltips). Diagnostics, Notes and Inventory follow Save Game; Overview and Palette are left out until their stage; hiding buttons (Settings) comes in stage 4 |
| MainForm: status bar (module, message, tiling progress, location, zoom) | 1 | ✔ | ✔ | ✔ |  |
| MainForm: title text (name, version, file, unsaved star, rules name) | 1 | ✔ | ✔ | ✔ |  |
| MainForm: unsaved-changes prompt on close, Abandon replay prompt | 1 | ✔ | ✔ | ✔ |  |
| MainForm: error guard (warning box on a bad file) | 1 | ✔ | ✔ | ✔ |  |
| MainForm: command line <vmod> --setup N / --new | 1 | ✔ | ✔ | ✔ | --rules and --new-rules too (stage 2a); the other options come later |
| MainForm: shortcut router (Cmd on macOS) | 1 | ✔ | ✔ | ✔ | user-editable keys come with Settings |
| ModuleTreePanel | 2b | ✔ |  |  | map nodes activate the tab; setup nodes open the setup chooser (no preselect yet); chart and PDF nodes say "Opens in a later version" (stage 3); rebuilds when the viewer changes |
| TablesTreePanel | 2b | ✔ |  |  | Tables and Formulas folders, refreshed 200 ms after rules changes; activation ignored until stage 4; split with the module tree remembered (ui.json) |
| DocumentTabStrip (map tabs) | 1 | ✔ | ✔ | ✔ | one tab per visible map |
| WindowManager (floating windows) | - |  |  |  |  |
| AlertBar | 2b | ✔ |  |  | "What now" banner (click reveals the phase panel), severity chips with the "+N" overflow menu, chip menu (matches, Hide until next turn), notice chip (click does nothing until stage 5); holder matches say "Open the Ledger to see ..." until the Ledger window; shown whenever a game is open (Settings toggle in stage 4) |
| TourOverlay | 5 |  |  |  |  |
| PhaseStrip, PhasePanel, PhaseDock, PhaseFloatForm | 2b | ✔ |  |  | Strip and docked panel done (Next Phase button runs the shared Next Phase flow with the open-decisions question and phase-boundary autosave). Pop out / Dock moves the panel to a floating "Phases" window (default beside the main window's right edge, bounds remembered per module under "phases", fitted onto a screen that exists with the primary screen as fallback); closing that window docks the panel back, shown; rule reference shown disabled until the rulebook viewer |
| NextPhaseDialog | 2b | ✔ |  |  | Continue anyway / Go back (Enter goes back); double-click jumps to the decision. Phase-boundary autosave at the WinForms default interval (5 min; no Settings yet) |
| StepBackDialog | 2b | ✔ |  |  | "Step Back to Start of Phase" (confirm text and summary) / "Step Back To" (Turn, Stage, Side, Phase, Reached, Actions; newest first, summary follows the selection, double-click picks); Step Back / Cancel |
| ReplayBar | 5 |  |  |  |  |
| UpdateBar, UpdateDialog, WhatsNewDialog, AppUpdates | 5 |  |  |  |  |

## Map and pieces

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| MapWindow: map toolbar (Line of sight, Last moved, Hide counters, Centre on selected, Zoom) | 2a | ✔ |  |  | one toolbar above the tabs acting on the selected map; Save map as picture… waits for stage 3 |
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
| MapCanvas: hover stack popup (CounterDetailPopup) | 2a | ✔ |  |  | same layout and pixels as WinForms (shared `CounterPopupLayout`); shown in the window overlay, so it stays inside the map window rather than the screen; hover delay fixed at 600 ms until Settings |
| MapCanvas: hover-stack highlight | 2a | ✔ |  |  |  |
| MapCanvas: double-click stack expand | 2a | ✔ |  |  | online guests send it through their action port, as WinForms |
| MapCanvas: right-click and Ctrl+click context menu | 2a | ✔ |  |  | counter menu from the shared PieceMenuModel; empty-map menu without Save map as picture (stage 3); macOS Ctrl+click opens it; Calculate opens the formula window (2b); Show in Ledger waits for the Ledger (stage 3) |
| MapCanvas: Line of sight tool (LosTool), Esc cancels LOS | 2a | ✔ |  |  | L key or toolbar; left-drag measures, Esc or right-click cancels; drawn in the view's picture so it stays sharp on HiDPI; on macOS Ctrl+click counts as right-click |
| MapCanvas: Hide counters (H) | 2a | ✔ |  |  | H key, toolbar and the empty-map menu; draws the board only (all counters) and clears the selection, as WinForms |
| MapCanvas: Last moved highlight | 2a | ✔ |  |  | toolbar toggle; follows moves, undo and new games |
| Cross-window drag preview and drops (OLE) | - |  |  |  | Windows-specific |
| PropertySheetDialog | 2a | ✔ |  |  |  |
| BreakdownTip | 2a | ✔ |  |  | on the popup's ledger lines; the ledger and inventory grid tips come with those windows (stage 3) |
| CounterDetailPopup interactive ledger lines | 2a | ✔ |  |  | click-through away from the lines, as WinForms; the ledger and inventory tips come with those windows (stage 3) |
| InputDialog | 2a | ✔ |  |  | also the rules-pack name prompt |
| ExportChoiceDialog, ExportProgressDialog, MapExport (save the map as a picture) | 3 |  |  |  |  |
| OverviewWindow | 3 |  |  |  |  |

## New game dialog

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| SetupChooserDialog: New game from module + setups, difficulty, double-click = OK | 1 | ✔ | ✔ | ✔ | no Players page, difficulty not saved |
| PlayersEditor | 5 |  |  |  |  |
| Remembered rules pack per module (RulesMemory) | 2a | ✔ |  |  | the pack is loaded with the module; a missing file is ignored |

## Rules, formulas, dice, log

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| FormulaDialog, FormulaFormView, CheatSheetWindow | 2b | ✔ |  |  | non-modal window owned by the main window, one per formula, unit and run; Calculate in Hardcore and Semi-auto, cheat sheet beside it in Semi-auto; online guests ask the host; closes when its run or unit is gone; Rule ref shown disabled (rulebook viewer, stage 3); the window rules are shared in Render FormulaWindowRules |
| BatchGridWindow, MissingInputDialog | 2b | ✔ |  |  | batch grid is a non-modal window per run (virtualised rows, choice columns are editable dropdowns, Calculate all / Apply all / Apply row, cheat sheet docked in Semi-auto, Ledger breakdown tip); stage 3a: the unit's counter picture (64×48) in the Unit cell, drawn on a worker from the viewed state only (dotted placeholder while drawing, nothing for hidden units), rows 52 px, Unit column 150 + 72, hovering the picture shows the counter preview with the unit's name; queued runs open them through the shared run dispatch (several units: the batch grid, double-click a row for its unit window; fully automatic: the missing-input dialog, "Per unit…" opens the batch grid or the unit window); nothing opens for a run that is no longer pending; OK in the missing-input dialog needs non-blank answers only, as WinForms |
| CounterPreviewTip, CounterCellImages (counter preview and list thumbnails) | 3a | ✔ |  |  | shared `CounterThumbnails` cache (worker-drawn, visible rows first, 600 cap, shown bitmaps never disposed; the palette uses it with its own 3000 cap) and `CounterPreview` popup (no activation; size from `CounterImages.PreviewSize`, placed by the shared `PreviewPlacement`: cursor + 16, flipped at the screen edges); only pieces in the viewed state; MapView exposes `Tiles` and `Snapshot()` (same pixels as on screen) for map export |
| DiceTools, DiceDialog, TurnDialog | 2a | ✔ |  |  | the UI-free part is Render DiceTurnActions, used by both apps |
| LogPanel, LogDock, LogFloatForm, ChatBox | 2a | ✔ |  |  | kind toggles, search, Copy, Export log (txt/html, one picker filter), virtualised list, double-click centres the piece; row logic shared in Render LogPanelRules. Pop out / Dock moves it to a floating window (bounds remembered per module); the chat line shows only online (stage 5) |
| OutlinerPanel, OutlinerDock, OutlinerFloatForm | 3 |  |  |  |  |
| PhaseWatcher | 2b | ✔ |  |  | `PhaseWatch` + `Debouncer` (DispatcherTimer, 150 ms) |

## Ledger, notes, inventory, documents, search

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| LedgerWindow (+ Tabs, Counters), EditTabsDialog, CounterPreviewTip | 3 | ✔ |  |  | "Ledger" 820×560: Filter:, Side: (All sides + the pack's sides), Group by: Map/Side/None, Show counters, Edit tabs…, Export CSV…, Show units without values, Refresh; tabs "Units (n)", "Depots & Global (n)", "Everything", "★ name" (notes as tooltip), "+"; broken user-tab filter banner; columns Name, Kind, one per property (notes as header tooltip; click sorts within groups, blanks last; drag the header edge to resize); virtualised rows; 64×48 counter pictures drawn on a worker from the viewed state only (rows on screen first, 600 kept), hover preview, double-click a unit row's Name/Kind/picture centres its piece; hover a value for its breakdown; double-click or F2 edits a value (number parsed invariant then the user's locale, required note, other side's values refused); CSV in UTF-8 with BOM; prefs per module via LedgerTabStore; reloads (debounced 200 ms) on game, rules and viewer changes. Edit tabs… and "+" are shown disabled until EditTabsDialog (next task); typing a digit does not start an edit (F2 or double-click) |
| NotesWindow, InventoryWindow | 3 | ✔ |  |  | Notes: monospace box, commits after 1 s, on leaving the box and on close; the main window flushes it before the game closes or is replaced. Inventory: group by name / map / marker / ledger property, filter (300 ms), Total hover tip, double-click steps through the group's pieces; counts only what the viewer may see |
| PdfWindow, PdfViewerControl, ChartWindow | 3 |  |  |  | needs a cross-platform PDF renderer |
| DiagnosticsWindow | 3 | ✔ |  |  | Severity / Category / Message / Context, Show filter, Copy all (clipboard), live updates from any thread; the status-bar link ("n warning(s)/error(s) - click here to view diagnostics") opens it |
| SearchPanel, ScanPane, ImagePane, OcrPanel | 3 |  |  |  | OCR is Windows-only today; decision pending |

## Rules Workbench and Settings

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| WorkbenchWindow and tabs (Tables, Formulas, Properties, Phases, Alerts, Visibility) | 4 |  |  |  |  |
| ColumnsDialog, PasteTableDialog, BlockDialog | 4 |  |  |  |  |
| SettingsDialog and SettingsTabs (General, Game, Display, Map, Dice and Log, Toolbar, Folders) | 4 |  |  |  |  |
| KeyboardTab, ShortcutCaptureDialog | 4 |  |  |  |  |

## Multiplayer and launcher

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| LauncherForm, PlayOfflineForm | 5 |  |  |  |  |
| HandoverForm (hot seat) | 5 |  |  |  |  |
| OnlineLobbyForm, JoinDialog, HostSavedDialog, HostSidesPanel | 5 |  |  |  |  |
| SeatsDialog, SideChoiceDialog, SendTurnDialog, SendWaitForm, WhoAreYouDialog, MailboxOptions | 5 |  |  |  |  |
| SidesWatch | 5 |  |  |  |  |

## Shared behaviour that must stay identical

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| Saves open in both apps (ModuleSession.SaveNative / LoadNative) | 1 | ✔ | ✔ | ✔ | same calls; cross-app open is on the Mac checklist |
| Same classic grey look, one theme on all systems | 1 | ✔ | ✔ | ✔ | side-by-side shots: build/port-screenshots.ps1 |
| Desktop references no WinForms, WebView2, WinRT, App or Ocr project | 1 | ✔ | ✔ | ✔ | project file |

