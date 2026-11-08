# Cross-platform parity checklist (WinForms app vs Quartermaster.Desktop)

Every window, menu command and feature of the WinForms app (`src/Quartermaster.App`), with the stage of the port plan that brings it to the Avalonia app (`src/Quartermaster.Desktop`). Class names follow the table in `docs/wiki/Menus-and-Windows.md`.

How to read the columns:

- **Windows**: ticked when Desktop covers the row and headless tests (Avalonia.Headless) pass on Windows in this branch.
- **Linux**: ticked only for features verified by tests actually run on Linux. Stage 1 rows: verified by the headless tests in the CI workflow `.github/workflows/cross-platform.yml` on ubuntu-latest (green run 42510235329, 2026-11-08). Not yet opened on a real Linux desktop.
- **Docs.Tests on Linux**: pass on ubuntu-latest in CI (run 42510235329).
- **macOS**: ticked only after the user has worked through `docs/port/stage1-mac-checklist.md` on the M1 Mac. Stage 1: all checklist steps OK on the user's M1 Mac (2026-11-08), including real GPU rendering.
- **Stage**: 1 = shell, map, pieces (this branch); 2 to 5 = planned later stages (2 rules, formulas, log, dice, turns; 3 ledger, notes, inventory, documents, search; 4 workbench and settings; 5 multiplayer, launcher, updates); `-` = no plan yet or not applicable off Windows. The stage numbers after 1 are provisional.

## Stage-1 deviations from the WinForms app (deliberate or known)

- The counter palette is always visible once a game exists. WinForms shows it only in game master mode (View > Counter palette).
- The new game dialog has no Players page (comes with multiplayer).
- The difficulty chosen in the new game dialog is not saved as the default for next time (comes with Settings).
- Mouse wheel on macOS pans and Cmd+wheel zooms (deliberate: trackpad convention). On Windows and Linux the wheel zooms, as WinForms. Pinch zoom is for macOS trackpads.
- The look is the classic grey theme on all three systems, with Tahoma 11px where present, else Verdana, DejaVu Sans.
- Shortcuts use Cmd instead of Ctrl on macOS. There is no native macOS menu bar; the menu bar sits in the window.
- Only the File, Edit and View menus exist, with the items listed below.
- Saves are written and read by the same ModuleSession.SaveNative / LoadNative calls as WinForms, so each app opens the other's saves. Desktop tests cover the Desktop side; open a WinForms save in Desktop (and back) is on the Mac checklist.

### Map behaviour not yet ported

- Hover popup of a stack (CounterDetailPopup) and the hover-stack highlight.
- Double-click on a stack to expand or collapse it.
- Line of sight tool and Esc cancelling the line-of-sight line; the Hide counters key.
- Dragging between windows: the OLE drag preview in the target window and drops across windows (a drop released outside the view is cancelled).
- Last moved highlight (RefreshLastMoved).
- Zoom percentage and cursor location are shown in the status bar, but only as text.
- Applying Settings to the map (wheel step, drag threshold, hover delay, highlight colour, label size, snap, grid default).

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
| New Rules Pack... | 2 | ✔ |  |  |  |
| Open Rules Pack... | 2 | ✔ |  |  |  |
| Save Rules Pack / Save Rules Pack As... | 2 | ✔ |  |  |  |
| Back to launcher | 5 |  |  |  |  |
| Exit | 1 | ✔ | ✔ | ✔ | asks about unsaved changes, as WinForms |

## Menu bar: Edit, View, Game, Tools, Window, Help

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| Edit > Undo / Redo (caption follows the history) | 1 | ✔ | ✔ | ✔ | Ctrl/Cmd+Z, Ctrl/Cmd+Y, Ctrl/Cmd+Shift+Z |
| Edit > Step Back to Start of Phase / Step Back To... | 2 |  |  |  |  |
| View > Show Grid | 1 | ✔ | ✔ | ✔ |  |
| View > Zoom to Fit, Zoom 100%, Zoom > 25%, 50%, 100%, 200%, Fit | 1 | ✔ | ✔ | ✔ | labels from the map context menu and toolbar |
| View > Module Tree | 2 |  |  |  |  |
| View > Diagnostics | 3 |  |  |  |  |
| View > Log | 2 |  |  |  |  |
| View > Phase panel | 2 |  |  |  |  |
| View > Outliner | 3 |  |  |  |  |
| View > Ledger | 3 |  |  |  |  |
| Show in Ledger (map → ledger row, Ctrl/Cmd+L) | 3 |  |  |  |  | comes with the Desktop Ledger; decision logic already shared (`LedgerJump` in Quartermaster.Render) |
| View > Overview | 3 |  |  |  |  |
| View > Counter palette (GM only) | 1 | ✔ | ✔ | ✔ | always visible in Desktop |
| Game > Send Turn..., Players..., Leave Online Game | 5 |  |  |  |  |
| Tools > Rules Workbench | 4 |  |  |  |  |
| Tools > Inventory, Notes | 3 |  |  |  |  |
| Tools > Dice (1d6, 2d6, module dice, Custom, Multi-roll) | 2 |  |  |  | F5, F6 |
| Tools > Turn (Next, Previous, Set) | 2 |  |  |  |  |
| Tools > Settings... | 4 |  |  |  |  |
| Window > Cascade, Tile, Close All, Pop Out, floating list | - |  |  |  | Desktop uses tabs, not MDI; to be decided |
| Help > Interface Tour, Check for updates, About | 5 |  |  |  |  |

## Main window and shell

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| MainForm: toolbar | 2 |  |  |  |  |
| MainForm: status bar (module, message, tiling progress, location, zoom) | 1 | ✔ | ✔ | ✔ |  |
| MainForm: title text (name, version, file, unsaved star, rules name) | 1 | ✔ | ✔ | ✔ |  |
| MainForm: unsaved-changes prompt on close, Abandon replay prompt | 1 | ✔ | ✔ | ✔ |  |
| MainForm: error guard (warning box on a bad file) | 1 | ✔ | ✔ | ✔ |  |
| MainForm: command line <vmod> --setup N / --new | 1 | ✔ | ✔ | ✔ | --rules and --new-rules too (stage 2a); the other options come later |
| MainForm: shortcut router (Cmd on macOS) | 1 | ✔ | ✔ | ✔ | user-editable keys come with Settings |
| ModuleTreePanel | 2 |  |  |  |  |
| TablesTreePanel | 2 |  |  |  |  |
| DocumentTabStrip (map tabs) | 1 | ✔ | ✔ | ✔ | one tab per visible map |
| WindowManager (floating windows) | - |  |  |  |  |
| AlertBar | 2 |  |  |  |  |
| TourOverlay | 5 |  |  |  |  |
| PhaseStrip, PhasePanel, PhaseDock, PhaseFloatForm | 2 |  |  |  |  |
| NextPhaseDialog | 2 |  |  |  |  |
| StepBackDialog | 2 |  |  |  |  |
| ReplayBar | 5 |  |  |  |  |
| UpdateBar, UpdateDialog, WhatsNewDialog, AppUpdates | 5 |  |  |  |  |

## Map and pieces

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
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
| MapCanvas: hover stack popup (CounterDetailPopup) | 2 |  |  |  |  |
| MapCanvas: hover-stack highlight | 2 |  |  |  |  |
| MapCanvas: double-click stack expand | 2 |  |  |  |  |
| MapCanvas: right-click and Ctrl+click context menu | 2 | ✔ |  |  | counter menu from the shared PieceMenuModel; empty-map menu without Hide counters (map tools) and Save map as picture (stage 3); macOS Ctrl+click opens it; Show in Ledger and Calculate wait for the Ledger and formula dialogs (2b) |
| MapCanvas: Line of sight tool (LosTool), Esc cancels LOS | 2 |  |  |  |  |
| MapCanvas: Hide counters (H) | 2 |  |  |  |  |
| MapCanvas: Last moved highlight | 2 |  |  |  |  |
| Cross-window drag preview and drops (OLE) | - |  |  |  | Windows-specific |
| PropertySheetDialog | 2 | ✔ |  |  |  |
| BreakdownTip | 3 |  |  |  |  |
| CounterDetailPopup interactive ledger lines | 3 |  |  |  |  |
| InputDialog | 2 | ✔ |  |  | also the rules-pack name prompt |
| ExportChoiceDialog, ExportProgressDialog, MapExport (save the map as a picture) | 3 |  |  |  |  |
| OverviewWindow | 3 |  |  |  |  |

## New game dialog

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| SetupChooserDialog: New game from module + setups, difficulty, double-click = OK | 1 | ✔ | ✔ | ✔ | no Players page, difficulty not saved |
| PlayersEditor | 5 |  |  |  |  |
| Remembered rules pack per module (RulesMemory) | 2 | ✔ |  |  | the pack is loaded with the module; a missing file is ignored |

## Rules, formulas, dice, log

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| FormulaDialog, FormulaFormView, CheatSheetWindow, BatchGridWindow, MissingInputDialog | 2 |  |  |  |  |
| DiceTools, DiceDialog, TurnDialog | 2 |  |  |  |  |
| LogPanel, LogDock, LogFloatForm, ChatBox | 2 |  |  |  |  |
| OutlinerPanel, OutlinerDock, OutlinerFloatForm | 3 |  |  |  |  |
| PhaseWatcher | 2 |  |  |  |  |

## Ledger, notes, inventory, documents, search

| Feature (WinForms class or menu) | Stage | Windows | macOS | Linux | Note |
|---|---|---|---|---|---|
| LedgerWindow (+ Tabs, Counters), EditTabsDialog, CounterPreviewTip | 3 |  |  |  |  |
| NotesWindow, InventoryWindow | 3 |  |  |  |  |
| PdfWindow, PdfViewerControl, ChartWindow | 3 |  |  |  | needs a cross-platform PDF renderer |
| DiagnosticsWindow | 3 |  |  |  |  |
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

