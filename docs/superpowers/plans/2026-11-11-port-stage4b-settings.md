# Cross-platform Port — Stage 4b (Settings, Keyboard, Open Autosave) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring Tools > Settings (General, Game, Display, Map, Dice & Log, Toolbar, Keyboard, Folders), user-editable shortcuts with key capture, File > Open Autosave and physical dice to the Avalonia app, and close every parity deferral that says "until Settings".

**Architecture:** The Desktop app gets the shared `SettingsStore` (Render) rooted at the same folder as its layout memory, so the WinForms and Avalonia apps read and write one `settings.json`. A new `MainWindow.Settings.cs` applies every setting live (one guarded step per area, as WinForms `MainForm.ApplySettings`). The Settings window is a set of `SettingsPage` user controls in `src/Quartermaster.Desktop/Settings/`; the Keyboard page's rules (effective keys, conflicts, problem text, overrides written) move to a Render model shared by both apps' tests. Physical dice are asked before the roll (Avalonia cannot block on a modal) and handed to `DiceTurnActions` through its existing `AskPhysical` hook.

**Tech Stack:** C# .NET 10, Avalonia 12.1.5 (SkiaSharp 4.154.0), Avalonia.Headless.XUnit, xUnit. No new packages.

**Spec:** `docs/superpowers/specs/2026-11-08-cross-platform-design.md` (full parity; settings carry over unchanged; Cmd on Mac). Rows: `docs/port/parity.md` lines 17, 26, 33, 36, 39, 55, 58, 62, 74, 129, 160, 162, 170, 176, 181, 184, 205, 237, 257, 258. Behaviour reference: `src/Quartermaster.App/SettingsDialog.cs`, `SettingsTabs.cs`, `KeyboardTab.cs`, `ShortcutParser.cs`, `MainForm.Settings.cs`, `MapCanvas.cs:67-80`, `MainForm.cs:750-770` (Open Autosave), `DiceTools.cs:172-186` (physical dice).

## Scope rulings (made at planning)

- **OCR language downloads are NOT in 4b.** The spec wants extra Tesseract languages downloadable from Settings; that is a new feature with a network fetch and no WinForms original. It becomes stage **4c** (own small plan, user approves the download source first). Parity row 103 keeps "English only until 4c".
- **Settings → Game → Players waits for stage 5** (it ports `PlayersEditor` with hot-seat). The Game page shows Difficulty and its hint only; where the Players box was, a grey label "Players: comes with multiplayer (stage 5)". Difficulty still switches the open game mid-game (`PhaseController.SetDifficulty`, guest refused with `IntentExecutor.HostOnlyText`).
- **General → "Select the last module in the launcher" and "Check for updates on start"** are shown and stored (the launcher and updater are stage 5) — same file, same values the WinForms app reads.
- **Palette follows game master mode** (WinForms parity): the palette column shows only when a game is open and `GameMaster` (setting `GameMasterMode`, or `--gm` for the run). Today's Desktop shows it for every game; tests that use the palette set `GameMasterMode = true` in their temp store.
- **Colour choice** uses a small own `ColourDialog` (48 basic swatches as the WinForms `ColorDialog` basic grid + a `#RRGGBB` box), not the Avalonia ColorPicker package (no new packages).
- **Physical ⌃ (Control) on macOS** is not a shortcut modifier (the registry knows Ctrl/Alt/Shift; on Mac Ctrl = ⌘). Capture swallows a key pressed with ⌃ only, as WinForms swallows unsupported keys.
- **Logic copied, WinForms unchanged**, except `KeyboardTab` may call the new Render model if that is a pure refactor (optional; `--smoke 3` and `--screenshot-settings --settings-tab keyboard` must still pass if touched).

## Global Constraints

- All Global Constraints of `docs/superpowers/plans/2026-11-10-port-stage4a-workbench.md` apply (labels, tooltips and texts exactly as the named WinForms file; classic grey look on all OSes; Cmd through `Input/ShortcutGestures`; headless tests only, never a visible window; MacFlag collection for tests touching `ShortcutGestures.IsMac`; work off the UI thread posted back with `Dispatcher.UIThread.Post`).
- **Formats unchanged:** rules pack format 4, save format 7, `settings.json` written by `SettingsStore` exactly as WinForms writes it (no new fields).
- **One settings file:** `new SettingsStore(layoutRoot ?? AppDataRoot.Path)`. Tests always pass a temp `layoutRoot`; no test may read or write the real app data folder.
- **Commits:** repo-local noreply identity `80044157+EmojiPlay88@users.noreply.github.com`; every message ends with a blank line, then `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never push.
- **Green bar per task:** `dotnet build Quartermaster.slnx -c Release` with 0 warnings (the known App MSB3277 excepted) and the task's test projects green; T7 runs full `dotnet test Quartermaster.slnx -c Release` (flaky tests listed in RESUME may be re-run once).
- **Shared files:** `Shell/Dialogs.cs`, `FakeDialogs` (`tests/Quartermaster.Desktop.Tests/MainWindowTests.cs`), `Program.QuietDialogs` only by T2. `MainWindow*.cs`, `Map/MapView*.cs`, `Shell/MapTabs.cs`, `Shell/MainToolbar.cs` only by T5 and T6 (different waves). `Shell/DiceTools.cs` only by T6. `docs/port/parity.md`, `RESUME.md`, `build/port-screenshots.ps1`, `Shell/ScreenshotStates.cs` only by T7.
- **Waves:** W1 = T1, T2; W2 = T3, T4; W3 = T5; W4 = T6; W5 = T7. One worktree per task cut from `port-stage4b` (itself cut from `port-stage4a` or master once PR #2 is merged); at most 3 live worktrees; delete each task's `bin/obj` after merge.

## Review Focus

1. **A bad or foreign `settings.json`** (written by the WinForms app, older, hand-edited, unknown command ids in `ShortcutOverrides`, out-of-range numbers): Desktop starts, clamps like WinForms, keeps unknown override ids on save → `Unknown_override_ids_survive_keyboard_apply` (T4), `Out_of_range_values_are_clamped_on_apply` (T5).
2. **Two shortcuts on one key / a reserved key** (Ctrl+W, Ctrl+Tab, Esc, Enter): OK and Apply refuse with the WinForms text and select the Keyboard page; nothing saved → `Conflict_blocks_ok_and_selects_keyboard_page` (T4), `Problem_text_matches_winforms` (T1).
3. **A changed shortcut reaches every place it shows**: the key dispatches the new command, the old key does nothing, menu gestures and toolbar tooltips show the new text (⌘ on Mac) without a restart → `Override_rebinds_key_menu_and_tooltip` (T5).
4. **Apply while windows are open** (several map tabs, floating log, formula windows, Workbench): every open map view takes the new zoom step/hover delay/highlight/labels/snap at once; one failing step does not stop the rest → `Apply_reaches_every_open_map_view`, `Failing_step_does_not_block_others` (T5).
5. **Physical dice cancelled or mistyped** (letters, 0, > sides, Cancel on die 2 of 3, multi-roll): a typo re-asks with "Enter a whole number from 1 to N.", Cancel records nothing (no partial roll) → `Physical_cancel_records_nothing`, `Physical_bad_value_reasks` (T6).

---

### Task 1: Keyboard edit model (Render) and key formatting (Desktop)

**Files:**
- Create: `src/Quartermaster.Render/KeyboardEditModel.cs`
- Modify: `src/Quartermaster.Desktop/Input/ShortcutGestures.cs`
- Test: `tests/Quartermaster.Render.Tests/KeyboardEditModelTests.cs`, `tests/Quartermaster.Desktop.Tests/ShortcutGestureTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class KeyboardEditModel(CommandRegistry registry)` with `void Load(Settings s)`; `string Effective(string id)` ("" = none); `void Assign(string id, string shortcut)`; `void Reset(string id)`; `void ResetAll()`; `IReadOnlySet<string> Flagged()`; `string? Problem()`; `string? ValidateText()`; `Settings Collect(Settings s)`; `IReadOnlyList<CommandDef> Commands`.
  - `ShortcutGestures.Format(Key key, KeyModifiers mods, bool mac) -> string?` and `Format(Key, KeyModifiers)` using `IsMac`.

- [ ] **Step 1: Write the failing tests** — copy the semantics of `KeyboardTab.cs` (`Load`, `Problem`, `Validate`, `Collect`, `Normal`):
  - `Load_takes_effective_shortcuts`: override `{"tools.settings": "Ctrl+Shift+P"}` → `Effective("tools.settings") == "Ctrl+Shift+P"`, other ids = their default.
  - `Collect_writes_only_differences`: assign a default back → no entry for that id; cleared → `""` entry.
  - `Collect_keeps_unknown_ids`: override `{"x.gone":"F9"}` survives `Collect`.
  - `Problem_text_matches_winforms`: two ids on `F5` → `"F5 is used by <label1>, <label2>"`; an id on `Ctrl+W` → `"Ctrl+W is reserved by the window (<label>)"`; several joined with `"; "`; `ValidateText()` = `"These shortcuts need fixing before the settings can be applied:\n"` + problems joined by `"\n"`.
  - Desktop `Format_round_trips_every_default` (both `mac` values): for each `CommandDefaults` shortcut, `Format` of `Parse(s, mac)`'s key and modifiers equals the normalized string.
  - `Format_mac_meta_is_ctrl`: `Format(Key.OemComma, KeyModifiers.Meta, mac: true) == "Ctrl+,"`; `Format(Key.A, KeyModifiers.Control, mac: true) == null`; `Format(Key.LeftShift, Shift, false) == null`; `Format(Key.F5, None, false) == "F5"`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Render.Tests -c Release --filter KeyboardEditModel` and `dotnet test tests/Quartermaster.Desktop.Tests -c Release --filter ShortcutGesture` → FAIL (types/methods missing).
- [ ] **Step 3: Implement** both. `Format` is the inverse of `TryKey`/`Parse` (same `Named` table, letters, digits, F1–F24); modifier order `Ctrl+Alt+Shift+` as `ShortcutParser.Format`; on mac Meta→Ctrl and Control present → null; off mac Meta present → null.
- [ ] **Step 4: Run** the same two commands → PASS.
- [ ] **Step 5: Commit** `feat(port): keyboard edit model and Avalonia key formatting`.

### Task 2: Shortcut capture and colour dialogs behind IDialogs

**Files:**
- Create: `src/Quartermaster.Desktop/Settings/ShortcutCaptureDialog.cs`, `src/Quartermaster.Desktop/Settings/ColourDialog.cs`
- Modify: `src/Quartermaster.Desktop/Shell/Dialogs.cs`, `src/Quartermaster.Desktop/Program.cs` (QuietDialogs), `tests/Quartermaster.Desktop.Tests/MainWindowTests.cs` (FakeDialogs)
- Test: `tests/Quartermaster.Desktop.Tests/SettingsDialogsTests.cs`

**Interfaces:**
- Consumes: `ShortcutGestures.Format` (T1).
- Produces on `IDialogs`: `Task<string?> AskShortcutAsync(Window owner, string commandLabel)` (null = cancelled, "" = clear); `Task<string?> AskColourAsync(Window owner, string initialHex)` (null = cancelled, else `#RRGGBB` upper-case). FakeDialogs: `Queue<string?> Shortcuts`, `Queue<string?> Colours` (empty queue → null). QuietDialogs return null.
  - `IDialogs.OpenFileAsync` gains an optional last parameter `string? initialFolder = null` (real: `SuggestedStartLocation` via `StorageProvider.TryGetFolderFromPathAsync`; FakeDialogs records it in `LastOpenFolder`).

- [ ] **Step 1: Write the failing tests** (headless):
  - `Capture_esc_cancels`, `Capture_backspace_and_delete_clear` (Result ""), `Capture_ctrl_shift_k_returns_text` ("Ctrl+Shift+K"), `Capture_modifier_alone_is_swallowed` (window stays open).
  - `Capture_texts`: title `"Set shortcut"`, text `"Press the new shortcut for \"<label>\".\nEsc cancels; Backspace or Delete clears."`, client 320×80.
  - `Open_file_passes_initial_folder` (fake records it; existing callers unchanged).
  - `Colour_swatch_click_then_ok_returns_hex`, `Colour_bad_hex_disables_ok`, `Colour_cancel_returns_null`; title `"Colour"`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Desktop.Tests -c Release --filter SettingsDialogs` → FAIL.
- [ ] **Step 3: Implement.** Capture dialog handles `KeyDown` with tunnel routing so Tab/Enter/arrows reach it before focus navigation; text via `SettingsStore.TryParseColor` for the hex box; swatch grid = the 48 WinForms basic colours (8×6).
- [ ] **Step 4: Run** → PASS; also full `Quartermaster.Desktop.Tests` (FakeDialogs change) → PASS.
- [ ] **Step 5: Commit** `feat(port): shortcut capture and colour dialogs`.

### Task 3: Settings window and the seven plain pages

**Files:**
- Create: `src/Quartermaster.Desktop/Settings/SettingsPage.cs`, `SettingsWindow.cs`, `SettingsContext.cs`, `GeneralPage.cs`, `GamePage.cs`, `DisplayPage.cs`, `MapPage.cs`, `DiceLogPage.cs`, `ToolbarPage.cs`, `FoldersPage.cs` (all in `src/Quartermaster.Desktop/Settings/`)
- Test: `tests/Quartermaster.Desktop.Tests/SettingsWindowTests.cs`

**Interfaces:**
- Consumes: `IDialogs.AskColourAsync` (T2).
- Produces:
  - `public abstract class SettingsPage : UserControl` { `abstract string Title`; `abstract void Load(Settings s)`; `abstract Settings Collect(Settings s)`; `virtual string? Validate() => null`; `virtual void AfterApply() {}` }.
  - `public sealed record SettingsContext(IReadOnlyList<(string Id, string Text)> ToolbarItems, string CacheRoot, Func<string?> OpenModuleCache, Func<string, Task> Notify, IDialogs Dialogs, Func<Difficulty?>? GameDifficulty = null, Action<Difficulty>? SetGameDifficulty = null)`.
  - `public sealed class SettingsWindow : Window` — `SettingsWindow(Settings initial, Action<Settings> apply, IReadOnlyList<SettingsPage> pages, Func<Settings>? latest = null, IDialogs? dialogs = null)`; `void SelectPage(int i)`; `int SelectedPage`; `Settings CollectForTest()`; `Task<bool> CommitAsync()`; buttons named `OkButton`, `CancelButton`, `ApplyButton`, `RestoreButton`.
  - `static IReadOnlyList<SettingsPage> SettingsWindow.StandardPages(SettingsContext ctx, SettingsPage keyboard)` → General, Game, Display, Map, Dice & Log, Toolbar, `keyboard`, Folders (WinForms order).

- [ ] **Step 1: Write the failing tests.** Every label, check text, range and default copied from `SettingsTabs.cs`:
  - `Round_trip_every_page`: `Collect` after `Load(s)` on each page equals `s` for a non-default `Settings` (all fields of that page changed).
  - `Ranges_clamp_like_winforms`: Load `AutosaveMinutes = 999` → shows 240; `WheelZoomStep` 1.05–2 step 0.05; drag 0–50; hover 0–5000 step 50; label 6–20; tab min 40–400, max 60–600; Collect `TabMaxWidth = max(min, max)`.
  - `Invalid_time_format_keeps_old`: text `"%%q"` → preview `"(not a valid time format)"`, Collect keeps the old format.
  - `Game_page_without_game_disabled` (hint `"Open a game first"`, Collect leaves `LastDifficulty`); `Game_page_with_game_switches_after_apply` (AfterApply calls `SetGameDifficulty(chosen)`, hint `"A change takes effect from the next calculation."`); Players placeholder label text from the ruling.
  - `Restore_defaults_resets_only_current_page` (takes effect when applied).
  - `Ok_applies_and_closes`, `Apply_stays_open`, `Cancel_after_apply_keeps_applied`, `Commit_merges_onto_latest` (a store change between open and OK — e.g. `RecentSaves` — is kept).
  - `Validate_problem_blocks_commit_and_selects_page` (fake page returning a problem; `ShowWarningAsync` called with caption `"Settings"`).
  - `Folders_sizes_load_off_ui_thread` (text `"Tile cache: x.x MB     Document cache: y.y MB"`), `Folders_clear_skips_open_module` (Notify text from `FoldersTab.Clear`).
  - Window: title `"Settings"`, 560×400, not resizable, page titles in WinForms order.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Desktop.Tests -c Release --filter SettingsWindow` → FAIL.
- [ ] **Step 3: Implement.** Pages lay out as WinForms `Table()` (label column auto, control column fill, 10 px padding, scroll). Folders computes sizes the first time its page becomes visible.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(port): settings window and pages`.

### Task 4: Keyboard page

**Files:**
- Create: `src/Quartermaster.Desktop/Settings/KeyboardPage.cs`
- Test: `tests/Quartermaster.Desktop.Tests/KeyboardPageTests.cs`

**Interfaces:**
- Consumes: `KeyboardEditModel` (T1), `IDialogs.AskShortcutAsync` (T2), `SettingsPage` / `SettingsWindow` (T3 — written against the T3 interface; the wave merges T3 first).
- Produces: `public sealed class KeyboardPage(CommandRegistry registry, IDialogs dialogs, Func<Window?> owner) : SettingsPage` — Title `"Keyboard"`; rows expose `IReadOnlyList<(string Id, string Label, string Shortcut, string Default, bool Flagged)> RowsForTest`; `Task SetSelectedAsync()`; `void SelectId(string id)`.

- [ ] **Step 1: Write the failing tests:**
  - `Columns_and_rows`: columns `Command` 250, `Shortcut` 150, `Default` 100; "(none)" for empty; shortcut and default columns use `ShortcutGestures.Display` (⌘ on Mac, MacFlag collection).
  - `Set_clear_reset_reset_all` (buttons `Set...`, `Clear`, `Reset`, `Reset all`; first three disabled with no selection; double-click = Set).
  - `Conflict_rows_red_and_status_text` (status = `KeyboardEditModel.Problem()`, Firebrick).
  - `Conflict_blocks_ok_and_selects_keyboard_page` (in a `SettingsWindow` with `StandardPages`; warning text = `ValidateText()`; store unchanged).
  - `Unknown_override_ids_survive_keyboard_apply`.
- [ ] **Step 2: Run** `--filter KeyboardPage` → FAIL.
- [ ] **Step 3: Implement** with the shared Workbench `EditGrid` read-only, or a `ListBox` with a three-column item template if `EditGrid` has no read-only mode (implementer's choice; same look).
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(port): keyboard settings page`.

### Task 5: Settings store and live apply in the main window

**Files:**
- Create: `src/Quartermaster.Desktop/MainWindow.Settings.cs`
- Modify: `MainWindow.axaml.cs` (store init in ctor, Tools menu `Separator` + `&Settings...` on `tools.settings`, menu gesture/tooltip refresh list), `MainWindow.Alerts.cs` (`UpdateAlertBarVisible` adds `&& (Settings.AlertBarVisible || alert bar has a notice)` as WinForms `MainForm.Alerts.cs:59`), `MainWindow.Phases.cs` (autosave minutes from the store), `Map/MapView.cs`, `Map/MapView.Hover.cs`, `Shell/MapTabs.cs`, `Shell/MainToolbar.cs`
- Test: `tests/Quartermaster.Desktop.Tests/SettingsApplyTests.cs`

**Interfaces:**
- Consumes: `SettingsWindow`, `StandardPages`, `SettingsContext` (T3), `KeyboardPage` (T4).
- Produces:
  - `public SettingsStore SettingsStore { get; }` on `MainWindow` (public for tests); `MainWindow.ShowSettingsAsync() -> Task`; `bool GameMaster` (setting or `--gm`); `event Action<Settings>? SettingsApplied`.
  - `MapView.ApplySettings(Settings s)` — the six assignments of WinForms `MapCanvas.ApplySettings` (zoom step clamp 1.05–2, drag threshold ≥ 0, hover delay ≥ 1, highlight from `HighlightRgb` into `MapDrawOptions.Highlight`, `PieceRenderer.LabelScale = clamp(LabelFontSize, 6, 20) / 8f`, snap, module grids off when `GridDefault == Never`).
  - `MapTabs.ApplySettings(Settings s)` (every open view, and views opened later), `MapTabs.SetTabWidths(int min, int max)`.
  - `MainToolbar.SetVisible(string id, bool visible)` (separators collapse when both neighbours hidden, as WinForms).

- [ ] **Step 1: Write the failing tests** (temp layout root; settings file written beforehand where needed):
  - `Store_loads_from_layout_root` and `Never_touches_app_data` (no file under `AppDataRoot.Path` changes).
  - `Override_rebinds_key_menu_and_tooltip`: save `{"dice.1d6": "F9"}` → F9 rolls, F5 does nothing, menu gesture text `F9`, toolbar tip `"1d6 (F9)"`.
  - `Apply_reaches_every_open_map_view` (two maps: zoom step, hover delay, drag threshold, snap, label scale, highlight; a view opened after apply also gets them).
  - `Grid_default_sets_show_grid_once` (WinForms `_appliedGrid` rule: only when `GridDefault` changes).
  - `Toolbar_visibility_hides_buttons`; `Palette_only_in_game_master_mode`.
  - `Alert_bar_setting_hides_bar`; `Log_panel_gets_settings` (existing `LogPanel.ApplySettings`); `Dice_last_roll_setting` (`ShowLastRollInStatus`).
  - `Autosave_uses_setting` (`AutosaveMinutes = 0` → no phase autosave).
  - `Out_of_range_values_are_clamped_on_apply`; `Failing_step_does_not_block_others` (a throwing `SettingsApplied` subscriber; later steps still run, one log line `"apply settings: ..."`).
  - `Tools_settings_menu_and_ctrl_comma` (`tools.settings` opens via `ShowSettingsAsync`; in tests through a hook returning the built window without showing it).
- [ ] **Step 2: Run** `--filter SettingsApply` → FAIL.
- [ ] **Step 3: Implement.** `ApplySettings` = WinForms `MainForm.ApplySettings` order, each in `Step(...)` logging `"apply settings: " + ex.Message`; the store's `Changed` is marshalled with `Dispatcher.UIThread.Post` when off-thread. Menu items created through `Item(...)` and toolbar buttons register `(control, cmd)` so `ApplyShortcuts` refreshes `InputGesture` and tooltips. After `ShowSettingsAsync`, a `LastSaveError` shows `"The settings could not be saved: " + err` through `ShowWarningAsync`.
- [ ] **Step 4: Run** → PASS; full `Quartermaster.Desktop.Tests` → PASS (palette tests updated per the ruling).
- [ ] **Step 5: Commit** `feat(port): settings store and live apply`.

### Task 6: Open Autosave, physical dice, remembered difficulty and outliner

**Files:**
- Modify: `MainWindow.axaml.cs` (File menu `Open Auto&save...` after Open Game, gate `file.openAutosave`; setup chooser uses and stores `LastDifficulty`), `MainWindow.Outliner.cs` (visible and grouping kept in `OutlinerVisible` / `OutlinerGrouping`), `Shell/DiceTools.cs`, `Formulas/FormulaWindow.cs` and `Workbench/FormulaPreview.cs` (Physical check starts at `PhysicalDiceDefault`)
- Test: `tests/Quartermaster.Desktop.Tests/AutosaveAndDiceTests.cs`

**Interfaces:**
- Consumes: `MainWindow.SettingsStore` (T5), `IDialogs.OpenFileAsync(..., initialFolder)` (T2).
- Produces: `DiceTools.PhysicalDice { get; set; }`; `Task<IReadOnlyList<int>?> DiceTools.AskPhysicalAsync(int n, int sides)`.

- [ ] **Step 1: Write the failing tests:**
  - `Open_autosave_starts_in_module_folder`: `OpenFileAsync` title `"Open Autosave"`, filters as Open Game, `initialFolder` = `Autosaver.AutosaveRoot(root)/<sha>` when it exists, else the root when it exists, else null; refused with no module.
  - `Opened_autosave_is_not_save_target` (Save goes to Save As; title shows `"(recovered autosave)"`).
  - `Physical_roll_asks_each_die` (texts `"Physical dice"`, `"Value of die {i} of {n} (1–{sides}):"`), `Physical_bad_value_reasks` (`ShowInfoAsync` `"Enter a whole number from 1 to {sides}."`), `Physical_cancel_records_nothing`, `Physical_multi_roll_asks_count_times_times`, `Physical_module_die_asks_one_of_faces`.
  - `Formula_window_physical_starts_from_setting`.
  - `New_game_remembers_difficulty` (store `LastDifficulty` after the chooser; next chooser preselects it).
  - `Outliner_visibility_and_grouping_survive_restart` (new `MainWindow` on the same root).
- [ ] **Step 2: Run** `--filter AutosaveAndDice` → FAIL.
- [ ] **Step 3: Implement.** Physical dice: collect all values first (`n × times` for multi-roll) with `AskTextAsync` + `ShowInfoAsync`, then set `Core.AskPhysical` to a one-shot provider that hands out the collected slices and run the existing `RollSpec`/`RollSpecial`/`MultiRoll`; reset the provider in `finally`.
- [ ] **Step 4: Run** → PASS; full `Quartermaster.Desktop.Tests` → PASS.
- [ ] **Step 5: Commit** `feat(port): open autosave, physical dice, remembered difficulty`.


### Task 7: Screenshots, parity, docs and full run

**Files:**
- Modify: `src/Quartermaster.Desktop/Shell/ScreenshotStates.cs` (states `settings`, `settings-<page>` for general|game|display|map|dice|toolbar|keyboard|folders), `build/port-screenshots.ps1` (stage4b-desktop-settings-*.png beside WinForms `--screenshot-settings --settings-tab <page>` off-screen), `docs/port/parity.md`, `docs/superpowers/RESUME.md`
- Create: `docs/port/stage4b-mac-checklist.md`
- Test: `tests/Quartermaster.Desktop.Tests/ScreenshotTests.cs` (one case per new state)

- [ ] **Step 1: Write the failing test** `Settings_states_render` (each state produces a non-empty PNG headless).
- [ ] **Step 2: Run** `--filter Screenshot` → FAIL; **Step 3:** add the states; **Step 4:** PASS.
- [ ] **Step 5: Parity.** Tick Windows on rows 257, 258, 129, 162; rewrite every "until Settings (stage 4)" note in lines 17–74 and rows 160, 170, 176, 181, 184, 205, 237 to say what is now applied; record the rulings above (palette GM-only, Players stage 5, OCR languages 4c) under a "Stage-4b deviations" list.
- [ ] **Step 6: Mac checklist** — ⌘, opens Settings; capture ⌘⇧K shows ⌘⇧K; ⌃ alone swallowed; colour swatch; changed shortcut in menu; Open Autosave start folder in the macOS panel; physical dice entry.
- [ ] **Step 7: Screenshots** `powershell -File build/port-screenshots.ps1` (off-screen) → user compares stage4b PNGs side by side.
- [ ] **Step 8: Full run** `dotnet test Quartermaster.slnx -c Release` → 0 failed; `--smoke 3` on the WinForms app if T1 touched `KeyboardTab`.
- [ ] **Step 9: RESUME CURRENT STATE** updated (4b done, rulings, deferred list, next = 4c OCR languages or stage 5).
- [ ] **Step 10: Commit** `docs(port): stage 4b parity, screenshots and resume`.
