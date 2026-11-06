# Piece 2b — UI Organisation, Player Tools, Counter Palette, Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a persistent game log panel, split Module/Tables trees, VASSAL-style player tools (dice, notes, line of sight, overview, map view tools, inventory, turn marker), a drag-and-drop counter palette behind Game master mode, and a Settings window with a central shortcut registry.

**Architecture:**
- **Core** gains log, notes and turn state, all produced by deterministic commands (save format 3).
- **Render** gains UI-free models: `Settings`/`SettingsStore`, `CommandRegistry`, `DiceExpr`, `HexDistance`, `InventoryQuery`, `LogFilter`.
- **App** adds the panels, windows and toolbars, wired through `MainForm`, `OpenChild` and `WindowManager`.

**Tech Stack:** .NET 10, C#, WinForms, SkiaSharp (existing), xUnit.

**Spec:** `docs/superpowers/specs/2026-11-06-piece2b-ui-tools-design.md`. §12 holds binding answers. The parent spec is `2026-11-06-quartermaster-engine-design.md` (§2 content boundary). Earlier rulings are in `docs/superpowers/piece1-followups.md`.

## Global Constraints

- **Content boundary:** no real-module content in the repo. Tests use synthetic fixtures. The palette reads the user's module at runtime only.
- **Commands:**
  - Every game-state change is a command through `GameSession.Execute`.
  - Logged commands carry `At` (UTC `DateTimeOffset`) and `Who` (string, `"local"` for now). Both are set by the issuer, so the reducer stays pure.
  - Log entries are built only by the reducer.
- **Save format:** NativeSave `FormatVersion = 3`. It reads 1, 2 and 3, and older formats load with an empty Log, empty Notes and Turn 1.
- **Settings file:** `%LOCALAPPDATA%\Quartermaster\settings.json`, written atomically.
  - A corrupt or unknown value falls back to its default with one diagnostic.
  - The root is overridable by the env var `QUARTERMASTER_LAYOUT_ROOT` (existing) or injectable in tests.
  - Tests never write to the real `%LOCALAPPDATA%`.
- **Autosave:** `%LOCALAPPDATA%\Quartermaster\autosave\<module sha256>\autosave-<yyyyMMdd-HHmmss>.qsave`. Keep the last 10. The interval defaults to 5 min; 0 turns it off.
- **Never crash on bad input:**
  - All UI handlers are guarded.
  - UI marshalling uses `BeginInvoke(() => { if (!IsDisposed) …; })`.
  - Invariant number display.
- **New windows and panels go through `OpenChild`/`WindowManager`**, so they get tabs and pop-out.
- **Classic gray WinForms look:** Tahoma 8.25pt, system colours.
- **Smoke mode keeps exiting 0.** New flags follow `--screenshot-<thing> <png>`.

## Review Focus

1. **Old saves (formats 1 and 2) and saves with log entries of unknown kind must load.** Missing fields default, and unknown kinds display as `system`. Task 1 test `NativeSave_ReadsFormat2_EmptyLogNotesTurn1`.
2. **Dice input typed by the user:** `0d6`, `3d1`, `200d6`, `d`, `2d6+`, `2d6-3`, `  2D6 + 1 `. Bad input gets a clear message and never throws. Limits are 1–100 dice and 2–1000 sides. Task 3 test `DiceExpr_Parse_BadInputs_ReturnsError`.
3. **Settings file:** corrupt, partially missing, or unknown enum and shortcut strings. Defaults fill the gaps and the app starts. Task 5 test `SettingsStore_CorruptOrPartial_FallsBackPerField`.
4. **Shortcut conflicts:** two commands bound to the same key, or a binding to a key that is reserved (Ctrl+Tab, Ctrl+W are used by tabs). The registry reports the conflict, and saving is refused with a message. Task 6 test `CommandRegistry_Conflict_Detected`.
5. **Palette drop on an unusual target:** an org display, a popped-out map, or outside any board. It snaps where possible. Off-board drops are refused with a status message, and nothing is added. Task 7 test (Render) `PaletteDrop_OutsideBoards_ReturnsNull`.

---

## File Structure

```
src/Quartermaster.Core/
  Commands.cs     – +SetNotes, SetTurn; At/Who/LogText on AddPiece, MovePieces, RemovePiece, RollDice(+Expression, FaceTexts), LedgerDelta(+At, Who)
  GameState.cs    – +Log (List<LogEntry>), Notes (string), Turn (TurnState); reducer appends LogEntry
  GameLog.cs      – LogEntry, LogKind constants, LogBuilder (reducer helper)
  NativeSave.cs   – format 3
src/Quartermaster.Render/
  DiceExpr.cs, HexDistance.cs, InventoryQuery.cs, LogFilter.cs
  Settings.cs, SettingsStore.cs, CommandRegistry.cs, Autosaver.cs
  MapImageExporter.cs, PaletteModel.cs
src/Quartermaster.App/
  LogPanel.cs, TablesTreePanel.cs, DiceTools.cs, NotesWindow.cs, OverviewWindow.cs,
  InventoryWindow.cs, SettingsDialog.cs (+ SettingsTabs.cs), PalettePanel.cs,
  LosTool.cs (MapCanvas overlay), MainForm.cs / MapWindow.cs / MapCanvas.cs (modified)
tests/… mirror per project
```

---

### Task 1: Core log, notes, turn, and save format 3

**Files:**
- Create: `src/Quartermaster.Core/GameLog.cs`
- Modify: `Commands.cs`, `GameState.cs`, `NativeSave.cs`
- Test: `tests/Quartermaster.Core.Tests/GameLogTests.cs`, `NativeSaveTests.cs`

**Interfaces:**
- **Produces:**
  - `sealed record LogEntry(long Seq, string Kind, string Text, string? Who, string? PieceId, string? MapId, DateTimeOffset At)`
  - `static class LogKind` with constants `Dice="dice"`, `Move="move"`, `Place="place"`, `Delete="delete"`, `Formula="formula"`, `Ledger="ledger"`, `Note="note"`, `Turn="turn"`, `System="system"`
  - `sealed record TurnState(int Turn, string? Phase)`
  - `GameState` gains:
    - `List<LogEntry> Log`
    - `string Notes`, default `""`
    - `TurnState Turn`, default `(1, null)`
- **New and changed command records.** New optional parameters all come last and default to null:
  - `AddPiece(..., string? LogText = null, DateTimeOffset? At = null, string? Who = null)`
  - `MovePieces(..., string? LogText = null, DateTimeOffset? At = null, string? Who = null)`
  - `RemovePiece(string PieceId, string? LogText = null, DateTimeOffset? At = null, string? Who = null)`
  - `RollDice(string Purpose, int Count, int Sides, IReadOnlyList<int> Results, string? Expression = null, IReadOnlyList<string>? FaceTexts = null, int Modifier = 0, DateTimeOffset? At = null, string? Who = null)`
  - `LedgerDelta(..., DateTimeOffset? At = null, string? Who = null)`
  - `SetNotes(string Text, DateTimeOffset? At = null, string? Who = null)` → `"setNotes"`
  - `SetTurn(int Turn, string? Phase, DateTimeOffset? At = null, string? Who = null)` → `"setTurn"`
- **Reducer.** It appends exactly one `LogEntry` per applied command of the kinds below, after applying. `Seq` = `Log.Count + 1`, and `At` = the command's `At ?? DateTimeOffset.UnixEpoch`, so replay is deterministic.
  - **AddPiece:**
    - Kind `place`.
    - Text = `LogText ?? $"piece {PieceId} placed"`.
    - PieceId and MapId set.
  - **MovePieces:**
    - Kind `move`.
    - Text = `LogText ?? $"{n} piece(s) moved"`.
    - PieceId = the first id.
  - **RemovePiece:**
    - Kind `delete`.
    - Text = `LogText ?? $"piece {PieceId} removed"`.
  - **RollDice:**
    - Kind `dice`.
    - Text = `"{Purpose}: {Expression ?? $"{Count}d{Sides}"} = {detail} = {total}"`.
    - detail = `FaceTexts` joined `", "` when FaceTexts is present (and then there is no total), otherwise results joined `" + "` plus the modifier as `" + m"`/`" − m"` (U+2212).
    - When `Count == 1` and the modifier is 0, the form is `"{Purpose}: 1d6 = 4"`.
  - **LedgerDelta:**
    - Kind `formula` if `Source == "formula"`, otherwise `ledger`.
    - Text = `Working ?? $"{PropertyId} {Before} → {After}"`. For manual edits, append `" (note: {Note})"`.
  - **SetNotes:**
    - Kind `note`.
    - Text `"notes updated"`.
    - Sets Notes.
  - **SetTurn:**
    - Kind `turn`.
    - Text `"Turn {Turn}"`, plus `" · {Phase}"` when a phase is given (U+00B7).
    - Sets Turn.
  - **SeedLedger** and the other commands produce no entry.
  - The reducer never throws, including with null lists.
- **`NativeSave`:**
  - `FormatVersion = 3`.
  - Reads 1–3.
  - Validate rejects a null `Log` and normalises a null `Notes` to `""`.
  - An entry with an unknown `Kind` is kept as-is (the UI shows it as system).

- [ ] **Step 1: Write the failing tests:**
  - `AddPiece_LogsPlace_WithLogTextOrFallback`
  - `MovePieces_LogsMove`
  - `RemovePiece_LogsDelete`
  - `RollDice_LogText_Numbers_Modifier_FaceTexts` (assert the exact strings: `"Attack: 2d6+1 = 4 + 3 + 1 = 8"`, `"Weather: Clear"`-style FaceTexts `"Weather: Weather die = Clear"`)
  - `LedgerDelta_Formula_UsesWorking`
  - `LedgerDelta_Manual_AppendsNote`
  - `SetNotes_And_SetTurn_UpdateStateAndLog`
  - `Seq_IsCountPlusOne_AndReplayIdentical` (apply the same commands to two states, then compare the JSON)
  - `NativeSave_Format3_RoundTrip`
  - `NativeSave_ReadsFormat2_EmptyLogNotesTurn1`
  - `Commands_JsonDiscriminators_SetNotes_SetTurn`
- [ ] **Step 2:** Run `dotnet test tests/Quartermaster.Core.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement. Existing call sites compile unchanged thanks to the defaults. Update the existing `NativeSave` wrong-format test to use format 4.
- [ ] **Step 4:** Run the full `dotnet test Quartermaster.slnx`. Expected: PASS.
- [ ] **Step 5: Commit:** `feat: game log, notes and turn state (save format 3)`

---

### Task 2: Log panel

**Files:**
- Create: `src/Quartermaster.Render/LogFilter.cs`, `src/Quartermaster.App/LogPanel.cs`
- Modify: `MainForm.cs` (dock bottom, View → Log), `MapWindow.cs`/`MapDragController.cs`, `PieceActions` call sites (fill `LogText`/`At`/`Who` on issued commands)
- Test: `tests/Quartermaster.Render.Tests/LogFilterTests.cs`

**Interfaces:**
- **Produces:**
  - `sealed record LogFilterSpec(IReadOnlySet<string> Kinds, string Search)`
  - `static IReadOnlyList<LogEntry> LogFilter.Apply(IReadOnlyList<LogEntry> log, LogFilterSpec f)`
    - Unknown kinds count as `system`.
    - Search is a case-insensitive substring match on Text.
  - `static string LogFilter.ToText(IReadOnlyList<LogEntry> rows)` gives `"[HH:mm:ss] kind  text"` lines.
  - `static string LogFilter.ToHtml(...)` gives a minimal escaped table.
  - `LogPanel : UserControl`:
    - A ToolStrip with a check button per kind, a search box, Copy and "Export log…".
    - A `ListView` in VirtualMode with columns Time / Kind / Text, auto-scrolling unless the user has scrolled up.
    - Refresh is debounced 150 ms on `Changed`/`GameReplaced`.
    - Double-clicking a row with a PieceId calls `MainForm.CentreOnPiece(pieceId)`. Add that method: it opens or activates the map via `OpenChild`/`WindowManager`, then centres on the piece.
  - Issuers fill `LogText`. Use `PieceInfo.Name` plus grid location names: MapWindow builds `"{name} moved {from} → {to}"`, Delete becomes `"{name} removed"`, and so on. They also set `At = DateTimeOffset.UtcNow` and `Who = "local"`.
  - The panel is docked at the bottom of the MDI client area, resizable through a splitter, and can be popped out via `WindowManager` (register it like a document with key `"log"`).

- [ ] **Step 1: Write the failing tests:**
  - `Filter_ByKind_And_Search`
  - `Filter_UnknownKind_TreatedAsSystem`
  - `ToText_Format`
  - `ToHtml_EscapesText`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement the logic, then the panel and wiring.
- [ ] **Step 4:** Run the full suite. Then smoke on a fixture: `--new --smoke 3 --screenshot-log <scratch>/log.png`, with smoke issuing a 2d6 roll and one move so the panel isn't empty. Expected: exit 0.
- [ ] **Step 5: Commit:** `feat: log panel`

---

### Task 3: Dice tools and turn marker

**Files:**
- Create: `src/Quartermaster.Render/DiceExpr.cs`, `src/Quartermaster.App/DiceTools.cs`
- Modify: `MainForm.cs` (Tools toolbar + menu, status-bar turn field)
- Test: `tests/Quartermaster.Render.Tests/DiceExprTests.cs`

**Interfaces:**
- **Produces:**
  - `sealed record DiceSpec(int Count, int Sides, int Modifier)`
  - `static (DiceSpec? Spec, string? Error) DiceExpr.Parse(string text)`:
    - grammar `^\s*(\d*)\s*[dD]\s*(\d+)\s*([+-]\s*\d+)?\s*$`, where an empty count means 1;
    - Count 1–100, Sides 2–1000, |Modifier| ≤ 1000;
    - errors in English: `"Dice must look like 2d6 or 3d10+2"`, `"Use 1–100 dice"`, `"Dice need 2–1000 sides"`.
  - `static string DiceExpr.Format(DiceSpec s)` gives e.g. `"2d6+1"` or `"d6"` → `"1d6"`.
  - `DiceTools` (App) adds the following:
    - Toolbar buttons **1d6** and **2d6**.
    - A dropdown with:
      - the module's special dice (`ModuleModel.Dice`): roll a face index per die (`sides = Faces.Count`) and pass `FaceTexts`;
      - "Custom…": an InputDialog with live validation via DiceExpr;
      - "Multi-roll…": count × expression, issuing one RollDice per roll, all with the same Purpose `"Multi-roll k/n"`.
    - Each roll executes `RollDice(Purpose: "Roll" or the die name, …, Expression, Modifier, At, Who)` through the session roller (`ModuleSession.SetRoller` remains the test hook).
    - Physical-dice mode (a Settings value from Task 5; until then a field defaulting to off) prompts for results instead of rolling.
    - The last result shows in the status bar for 5 s.
  - **Turn marker:**
    - A status-bar label `"Turn {N}"` plus `" · {Phase}"` when a phase is set.
    - Tools → Turn → Next turn / Previous turn (minimum 1) / Set…, each issuing a `SetTurn`.

- [ ] **Step 1: Write the failing tests:**
  - `DiceExpr_Parse_Valid` (cases `2d6`, `d6`, ` 3D10 + 2 `, `2d6-3`)
  - `DiceExpr_Parse_BadInputs_ReturnsError` (`0d6`, `3d1`, `200d6`, `d`, `2d6+`, `abc`, each with the exact message)
  - `DiceExpr_Format`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Full suite. Smoke rolls 2d6 and a module special die when the fixture has one (add a SpecialDiceButton to the fixture). Expected: exit 0, and the log shows both entries.
- [ ] **Step 5: Commit:** `feat: dice tools and turn marker`

---

### Task 4: Module / Tables split trees

**Files:**
- Create: `src/Quartermaster.App/TablesTreePanel.cs`
- Modify: `MainForm.cs` (left panel becomes a vertical SplitContainer; splitter distance saved in the global layout), `Workbench/WorkbenchWindow.cs` (`ShowTable(string tableId)`, `ShowFormula(string formulaId)`)

**Interfaces:**
- **Produces:**
  - `TablesTreePanel : Panel`:
    - Root folders "Tables" and "Formulas", listing the rules pack's tables and formulas by Name, sorted.
    - It shows "(no rules pack)" when `Rules == null`.
    - It refreshes on `RulesChanged`, debounced 200 ms.
    - Double-clicking a table runs `MainForm.OpenWorkbench()` then `ShowTable(id)`; a formula does the same with `ShowFormula(id)`.
  - `WorkbenchWindow.ShowTable` and `ShowFormula` select the tab and the item. An unknown id is a no-op.
- [ ] **Step 1:** Implement. The logic is thin, so this task has no unit test.
- [ ] **Step 2:** Build, then smoke with the synthetic pack and `--screenshot <scratch>/trees.png`. Expected: exit 0, and both trees are visible.
- [ ] **Step 3: Commit:** `feat: separate module and tables trees`

---

### Task 5: Settings model, store, autosave, and Settings window

**Files:**
- Create: `src/Quartermaster.Render/Settings.cs`, `SettingsStore.cs`, `Autosaver.cs`, `src/Quartermaster.App/SettingsDialog.cs`, `SettingsTabs.cs`
- Modify: `MainForm.cs` (Tools → Settings…, apply live), `MapCanvas.cs` (zoom step, drag threshold, hover delay, highlight colour, label size), `DiceTools.cs` (physical dice, status-bar toggle), `LogPanel.cs` (move/place/delete visibility)
- Test: `tests/Quartermaster.Render.Tests/SettingsTests.cs`, `AutosaverTests.cs`

**Interfaces:**
- **Produces:** `sealed record Settings` with `init` properties and these defaults:
  - **General:**
    - `ReopenLastModule = false`
    - `AutosaveMinutes = 5`
    - `ConfirmDelete = true`
    - `GameMasterMode = false`
  - **Display:**
    - `GridDefault = GridMode.ModuleDefault`, where `enum GridMode { ModuleDefault, Always, Never }`
    - `LabelFontSize = 8`
    - `HighlightColor = "#FFD700"`
    - `TabMinWidth = 80`
    - `TabMaxWidth = 160`
  - **Map:**
    - `WheelZoomStep = 1.2`
    - `SnapOnDrop = true`
    - `DragThresholdPx = 4`
    - `HoverDelayMs = 600`
  - **Dice & Log:**
    - `PhysicalDiceDefault = false`
    - `ShowLastRollInStatus = true`
    - `LogShowMoves = true`
    - `LogTimeFormat = "HH:mm:ss"`
  - **Toolbar:** `IReadOnlyDictionary<string, bool> ToolbarVisible`; a missing key means visible.
  - **Keyboard:** `IReadOnlyDictionary<string, string> ShortcutOverrides` (command id → key string, `""` = unbound).
  - **Global layout fields:** `LeftSplitterDistance`, `LastModulePath`.
- `sealed class SettingsStore(string root)`, with:
  - `Settings Load(DiagnosticLog? log)`. It reads per field: each invalid field falls back to its default and logs one diagnostic per field. Number ranges are clamped: `AutosaveMinutes` 0–240, `WheelZoomStep` 1.05–2, `LabelFontSize` 6–20.
  - `void Save(Settings s)`, atomic.
  - `event Action<Settings>? Changed`.
  - `static string DefaultRoot`, honouring `QUARTERMASTER_LAYOUT_ROOT`.
- `sealed class Autosaver(Func<string?> moduleSha, Action<string> saveTo, string root)`, with:
  - `string? Tick(DateTimeOffset now, int intervalMinutes, bool dirty)`, which returns the written path or null;
  - `static void Prune(string dir, int keep = 10)`.
  - MainForm drives it with a 30 s WinForms Timer. It saves only when the game is dirty and the interval has elapsed.
- `SettingsDialog`:
  - A TabControl with General, Display, Map, Dice & Log, Toolbar, Keyboard (filled by Task 6) and Folders.
  - OK / Cancel / Apply, plus "Restore defaults" per tab.
  - Folders shows the cache path and sizes, with "Clear tile cache" and "Clear document cache". Clearing is refused while a module is open and uses that cache, with a message.

- [ ] **Step 1: Write the failing tests:**
  - `SettingsStore_RoundTrip`
  - `SettingsStore_MissingFile_Defaults`
  - `SettingsStore_CorruptOrPartial_FallsBackPerField` (valid fields kept, invalid enum, out-of-range numbers clamped, one diagnostic per bad field)
  - `Autosaver_SavesOnlyWhenDirtyAndElapsed`
  - `Autosaver_Prune_KeepsNewest10`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement and apply settings live: grid default, highlight colour, label size, zoom step, drag threshold, hover delay, toolbar visibility, palette visibility (Task 7 hooks in), log move visibility and physical dice default.
- [ ] **Step 4:** Full suite. Smoke `--screenshot-settings <scratch>/settings.png`, which opens the dialog and saves DrawToBitmap. Expected: exit 0.
- [ ] **Step 5: Commit:** `feat: settings window, store and autosave`

---

### Task 6: Command registry and rebindable shortcuts

**Files:**
- Create: `src/Quartermaster.Render/CommandRegistry.cs`
- Modify: `MainForm.cs` (register all shortcut-bearing actions; menu items take their ShortcutKeys from the registry; `ProcessCmdKey` dispatches registry shortcuts), `SettingsTabs.cs` (Keyboard tab)
- Test: `tests/Quartermaster.Render.Tests/CommandRegistryTests.cs`

**Interfaces:**
- **Produces:**
  - `sealed record CommandDef(string Id, string Label, string DefaultShortcut)`
  - `sealed class CommandRegistry`, with:
    - `void Register(CommandDef d)`
    - `IReadOnlyList<CommandDef> All`
    - `string EffectiveShortcut(string id, IReadOnlyDictionary<string,string> overrides)`
    - `IReadOnlyList<(string Key, IReadOnlyList<string> Ids)> Conflicts(IReadOnlyDictionary<string,string> overrides)`
    - `static bool TryParseShortcut(string text, out Keys keys)`, which accepts the form `"Ctrl+Shift+W"`; `Keys` comes from System.Windows.Forms, so this helper lives in the App or in Render with a WinForms reference; place it in App if Render has no WinForms reference
    - `static IReadOnlySet<string> Reserved = { "Ctrl+Tab", "Ctrl+Shift+Tab", "Ctrl+W" }`
  - Register at least these ids:
    - `file.openModule` (Ctrl+O)
    - `file.saveGame` (Ctrl+S)
    - `dice.1d6` (F5)
    - `dice.2d6` (F6)
    - `tool.los` (L)
    - `view.hideCounters` (H)
    - `view.ledger` (Ctrl+L)
    - `tools.workbench` (Ctrl+Shift+W)
    - `view.log` (Ctrl+Shift+L)
    - `tools.settings` (Ctrl+,)
    - `turn.next` (Ctrl+Right)
  - The Keyboard tab:
    - lists commands with their effective shortcut;
    - lets you edit a shortcut by capturing a key press, with Clear and Reset;
    - shows conflicts in red;
    - refuses OK while conflicts or reserved keys are present, with a message.

  Single-letter shortcuts (L, H) apply only when a map canvas has focus and no text box is focused.

- [ ] **Step 1: Write the failing tests:**
  - `EffectiveShortcut_DefaultOrOverride`
  - `CommandRegistry_Conflict_Detected`
  - `Reserved_ReportedAsConflict`
  - `TryParseShortcut_Valid_Invalid`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Full suite plus smoke. Expected: exit 0.
- [ ] **Step 5: Commit:** `feat: command registry and rebindable shortcuts`

---

### Task 7: Counter palette and Game master mode

**Files:**
- Create: `src/Quartermaster.Render/PaletteModel.cs`, `src/Quartermaster.App/PalettePanel.cs`
- Modify: `MapCanvas.cs` (accept the palette DataFormat), `MapWindow.cs` (GM "Delete counter" menu item, honours ConfirmDelete), `MainForm.cs` (dock the panel right, visible iff GameMasterMode)
- Test: `tests/Quartermaster.Render.Tests/PaletteModelTests.cs`

**Interfaces:**
- **Produces:**
  - `sealed record PaletteNode(string Title, IReadOnlyList<PaletteNode> Children, IReadOnlyList<PieceSlotDef> Slots)`
  - `static PaletteNode PaletteModel.Build(ModuleModel m)`, a tree from each slot's `NavPath`. Slots with an empty NavPath go under "Other". Order is the module's order.
  - `static IReadOnlyList<PieceSlotDef> PaletteModel.Search(ModuleModel m, string text)`, a case-insensitive substring match on the slot's BasicPiece name (via PieceTypeParser) and EntryName, capped at 500 results.
  - `static (string MapId, PointD Pos, string? StackOnto)? PaletteModel.ResolveDrop(GameState s, MapDef map, PointD mapPoint, ImageSource images, bool snap)`. It reuses `DropResolver`, returns null when the point lies outside every board, and doesn't snap when `snap` is false.
  - **Drag DataFormat:** `"Quartermaster.SlotPath"`, carrying the slot's identity (`EntryName` plus `Gpid`).
  - **PalettePanel:**
    - The tree is on top. Below it is a `ListView` in LargeIcon mode with thumbnails. Each thumbnail is rendered once per slot with `PieceRenderer` (from `PieceFactory.FromSlot` at a dummy position), 48 px tall, cached.
    - A search box above the list.
    - Dragging an item starts an OLE drag.
    - On drop, MapCanvas resolves the target. If it's null, the status bar shows "Drop onto a board". Otherwise it executes `AddPiece` (from FromSlot, new id `"p:" + Guid.NewGuid().ToString("N")`, `LogText "{name} placed at {loc}"`, `At`, `Who`), and ledger seeding follows through the existing session hook.
  - **Game master mode:** the right-click "Delete counter" item appears only in GM mode. With ConfirmDelete on, a confirm dialog comes first. It issues `RemovePiece` with LogText.

- [ ] **Step 1: Write the failing tests:**
  - `PaletteModel_Build_FollowsNavPath_OtherFolder`
  - `PaletteModel_Search_CaseInsensitive_Capped`
  - `PaletteDrop_SnapsToGrid`
  - `PaletteDrop_OntoStack_StacksOnto`
  - `PaletteDrop_OutsideBoards_ReturnsNull`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Full suite. Smoke `--gm --screenshot-palette <scratch>/palette.png`, which turns GM mode on for the session only and places one counter programmatically through the same drop path. Expected: exit 0, with the log showing the placement.
- [ ] **Step 5: Commit:** `feat: counter palette and game master mode`

---

### Task 8: Line of sight / range and overview mini-map

**Files:**
- Create: `src/Quartermaster.Render/HexDistance.cs`, `src/Quartermaster.App/LosTool.cs`, `OverviewWindow.cs`
- Modify: `MapCanvas.cs`/`MapWindow.cs` (LOS toggle button; overlay drawing), `MainForm.cs` (View → Overview)
- Test: `tests/Quartermaster.Render.Tests/HexDistanceTests.cs`

**Interfaces:**
- **Produces:**
  - `static int? HexDistance.Cells(IMapGrid grid, PointD a, PointD b)`, measured in board-local coordinates:
    - **Square grid:** Chebyshev distance on snapped cell indices.
    - **Hex grid:** convert the snapped centres to offset column/row, then to cube coordinates, and take the cube distance. Offset parity and sideways handling follow the grid's `sideways` flag and stagger, matching the Task-5 (piece-1) HexGrid port.
    - **Zoned grid:** use the zone's grid if both points fall in the same zone, otherwise the background grid.
    - Returns null when there's no grid or the points are on different boards.
  - **LosTool:** a map toolbar toggle (shortcut via registry `tool.los`).
    - Drag draws a line from the snapped start to the snapped current cell.
    - The status bar shows `"{locA} → {locB}: {n} hexes"` (or "squares").
    - Esc or right-click cancels. It logs nothing.
  - **OverviewWindow:**
    - A small dockable window (OpenChild key `"overview"`).
    - It draws the active map window's board tiles at the highest pyramid level, plus a red viewport rectangle.
    - Click or drag pans the active map. It follows `MdiChildActivate` and floating-window activation, and repaints on viewport change (add `MapCanvas.ViewportChanged`).

- [ ] **Step 1: Write the failing tests:**
  - `Square_Distance`
  - `Hex_Distance_Vertical_Flat` (known pairs on a synthetic grid dx=60, dy=70: same column ±3 rows → 3; adjacent columns → 1; a diagonal of 4 → 4)
  - `Hex_Distance_Sideways`
  - `DifferentBoards_Null`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Full suite, then smoke `--screenshot-overview <scratch>/overview.png`. Expected: exit 0.
- [ ] **Step 5: Commit:** `feat: line of sight range tool and overview map`

---

### Task 9: Map view tools

**Files:**
- Create: `src/Quartermaster.Render/MapImageExporter.cs`
- Modify: `MapRenderer.cs` (`MapDrawOptions` gains `IReadOnlySet<string>? HighlightPieceIds`, `bool HideAllPieces`, `SKColor? HighlightColor`), `MapCanvas.cs`/`MapWindow.cs` (toolbar/menu: Highlight last moved, Hide counters (registry `view.hideCounters`), Centre on selected, Zoom presets 25/50/100/200/Fit, Save map as picture…)
- Test: `tests/Quartermaster.Render.Tests/MapImageExporterTests.cs`

**Interfaces:**
- **Produces:**
  - Last-moved pieces = the PieceIds of the most recent `MovePieces` in `Game.Log` (command log) on that map. Highlighting draws a 2 px outline in `HighlightColor`.
  - `static SKBitmap MapImageExporter.Render(MapDef map, IReadOnlyDictionary<string,TileSet> tiles, GameState s, ModuleModel model, MapRenderer r, double zoom, int maxLongSide = 8192)`. It clamps zoom so the long side stays ≤ maxLongSide and draws through the existing `MapRenderer.Draw` into an offscreen surface.
  - "Save map as picture…" offers "Current view" or "Whole map at {zoom}%" and runs in a background Task with progress. The PNG is written atomically.

- [ ] **Step 1: Write the failing tests:**
  - `Export_ClampsLongSide` (a 3000×2000 fixture board at zoom 4 with max 1000 gives a long side of 1000)
  - `Export_DrawsPieces` (a known-colour fixture piece pixel)
  - `Highlight_LastMovedIds_FromLog` (helper `MapViewTools.LastMovedIds(IReadOnlyList<GameCommand> log, string mapId)` in Render)
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Full suite plus smoke. Expected: exit 0.
- [ ] **Step 5: Commit:** `feat: map view tools`

---

### Task 10: Inventory and notes

**Files:**
- Create: `src/Quartermaster.Render/InventoryQuery.cs`, `src/Quartermaster.App/InventoryWindow.cs`, `NotesWindow.cs`
- Modify: `MainForm.cs` (Tools → Inventory, Tools → Notes)
- Test: `tests/Quartermaster.Render.Tests/InventoryQueryTests.cs`

**Interfaces:**
- **Produces:**
  - `enum InventoryKey { Name, Map, Marker, LedgerProperty }`
  - `sealed record InventoryRow(string Group, int Count, double? Total, IReadOnlyList<string> PieceIds)`
  - `static IReadOnlyList<InventoryRow> InventoryQuery.Run(GameState s, ModuleModel m, RulesPack? pack, InventoryKey key, string? keyName, string filter)`:
    - Marker groups by `PieceInfo.Markers(p)[keyName]`; a missing marker groups as "(none)".
    - LedgerProperty groups by property presence and sums the values into `Total`.
    - The filter is a substring match on piece name.
    - Results are sorted by Group.
  - **InventoryWindow:**
    - A key combo, a key-name combo (marker keys collected from pieces, or ledger property names), a filter box and a ListView.
    - Double-clicking a group cycles `CentreOnPiece` through its PieceIds.
    - It refreshes on `Changed`, debounced.
  - **NotesWindow:**
    - A multiline TextBox that commits `SetNotes` 1 s after typing stops, and on close (flush).
    - It reloads from state on `GameReplaced`, and on `Changed` when not focused.

- [ ] **Step 1: Write the failing tests:**
  - `Inventory_ByName_Counts`
  - `Inventory_ByMarker_NoneGroup`
  - `Inventory_ByLedger_Totals`
  - `Inventory_Filter`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Full suite plus smoke. Expected: exit 0.
- [ ] **Step 5: Commit:** `feat: inventory and notes`

---

### Task 11: End-to-end check and user checklist

**Files:**
- Create: `tests/Quartermaster.Render.Tests/Piece2bEndToEndTests.cs`
- Modify: `docs/superpowers/piece1-followups.md` (append a "Piece 2b" section)

- [ ] **Step 1: Write the end-to-end test.** Using a fixture module plus a temp settings root:
  - roll 2d6 through the session;
  - place a counter via the `PaletteModel.ResolveDrop` + `FromSlot` path;
  - move it;
  - set the notes;
  - set turn 2.

  Then save, load, and assert:
  - the log has 5 entries with the right kinds and Seq 1–5;
  - Notes and Turn are restored;
  - the format-2 save fixture still loads.
- [ ] **Step 2:** Run. Expected: PASS. Fix the owning task's code with a regression test if it fails.
- [ ] **Step 3:** Full suite. Then smoke on the real module with an empty pack: `--setup 1 --new-rules SmokePack --gm --smoke 15` plus screenshots of the main window, log, palette and settings. Screenshots go to the scratchpad only. Expected: exit 0. Record the diagnostic counts.
- [ ] **Step 4:** Write the hands-on checklist in the report:
  - dice buttons, special dice, custom and multi-roll;
  - log filter, export, and double-click to centre;
  - the palette drag onto a docked and a popped-out map;
  - LOS distances;
  - the overview pan;
  - hide counters / highlight last moved;
  - save picture;
  - inventory;
  - notes persistence;
  - settings live changes, shortcut rebinding and conflicts;
  - an autosave appearing after 5 minutes.
- [ ] **Step 5: Commit:** `test: piece 2b end-to-end; docs: follow-ups`
