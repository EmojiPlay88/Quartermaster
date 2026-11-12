# Cross-platform Port — Stage 5b (hot-seat) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Avalonia app plays hot-seat as WinForms does: players set up on the new-game Players page or in Settings > Game > Players, a handover screen ("Pass to Anna (Red)") hides every window between players, the status bar shows and switches the current player, and every Desktop window shows only the current player's view.

**Architecture:**
- The decision logic is already shared: Render `HotSeat` (handover due, take over, rewind, solo ↔ multi), Core `Players` (Validate, Effective, MaxPlayers) and `SetPlayers`, Core `PlayerView` through `ModuleSession.View` / `ViewChanged`. They are used unchanged.
- The logic that lives in WinForms `PlayersEditor` and `HandoverForm` (row rules, exclusive side ticks, result, problem, texts) moves to Render (`PlayersEditModel`, `HandoverText`), so both apps' tests cover it.
- The Desktop app gets `Players/PlayersEditor` (control), a Players page in `SetupChooser`, the Players box in Settings > Game, `Players/HandoverWindow`, `MainWindow.HotSeat.cs` (port of WinForms `MainForm.HotSeat.cs`) and a status-bar player button.

**Tech Stack:** C# .NET 10, Avalonia 12.1.5, Avalonia.Headless.XUnit, xUnit. No new packages.

**Spec:** `docs/superpowers/specs/2026-11-11-port-stage5-design.md` (sub-stage 5b). Behaviour reference: `src/Quartermaster.App/PlayersEditor.cs`, `HandoverForm.cs`, `SidesWatch.cs`, `MainForm.HotSeat.cs`, `MainForm.Phases.cs:78-90` (HoldRuns), `SetupChooserDialog.cs:40-170`, `Settings/SettingsTabs.cs:110-161`, `MainForm.Settings.cs:115-124`; Render `HotSeat.cs`.

## Scope rulings (made at planning)

- **Game > Players… is not in 5b.** In WinForms it exists only while online (`MainForm.Online.cs:452-456`, opens `SeatsDialog`); offline the player list is edited in Settings > Game > Players. It comes with 5c. The parity row "Game > Send Turn..., Players..., Leave Online Game" stays open.
- **Turn games** (`MainForm.Turns.cs` stopping hot-seat, `SetViewSides(TurnUiModel.ViewSides)`) come with 5d. Desktop has no turn files yet, so nothing to stop.
- **Online guards** in `SetGamePlayers`: the `IsGuest` refusal (`IntentExecutor.HostOnlyText`) is ported; the "While the game is online…" refusal is ported behind `IsOnline`, which is always false in Desktop until 5c (a `bool IsOnline => false` placeholder in `MainWindow.HotSeat.cs`, replaced in 5c).
- **SidesWatch** is not ported as a class. Its job (Workbench tabs refresh on a view change, coalesced) is done by each Desktop tab subscribing to `ViewChanged` through the existing `OnUiThread`/post pattern (T4).
- **No `--smoke-handover` / `--smoke-player-view`.** Headless tests cover them. The handover screenshot comes from `--screenshot-state handover` (T5).
- **WinForms** may switch to `PlayersEditModel` / `HandoverText` as a pure refactor (recommended: `HandoverForm.MessageFor/SidesText` and `PlayersEditor.Same` forward to Render). If WinForms is touched, `--smoke 3` and `--screenshot-handover` must still pass.

## Global Constraints

- Labels, tooltips and message texts exactly as the named WinForms file. Classic grey look. Cmd on macOS through `Input/ShortcutGestures`.
- Tests are headless (Avalonia.Headless), never open a visible window, use a temp layout root (`MainWindow(dialogs, cache, null, layoutRoot)` / `new SettingsStore(tmp)`), and never touch the real network, relay, GitHub or profile.
- Formats unchanged: rules pack 4, save 7, `settings.json` (no new fields). A game saved with players in WinForms opens in Desktop with the same players and current player, and the reverse.
- Hot-seat is honour-based (full state stays in memory) but nothing of another side's hidden information may be drawn in any Desktop window while a player is current or the handover screen is up (spec anti-goal; Core `PlayerView` rules of piece 6a).
- Test content is invented and labelled TEST (sides "Red"/"Blue"/"Green" in a test rules pack). No real-game content.
- Commits: repo-local noreply identity; every message ends with a blank line and a `Co-Authored-By:` trailer naming the model. Never push.
- Green bar per task: `dotnet build Quartermaster.slnx -c Release` with 0 new warnings, and the task's test projects green. T5 runs the full `dotnet test Quartermaster.slnx -c Release` (flaky tests listed in RESUME may be re-run once).
- Tasks run one after another in one worktree (they depend on each other).
- Shared files:
  - `Shell/Dialogs.cs`, `Program.QuietDialogs` and the test `FakeDialogs` change only in T2 (plus T3's one-word `ShowDialogTracked` switch).
  - `MainWindow*.cs` and `MainWindow.axaml` change in T2, T3 and T4, in that order.
  - `docs/port/*`, `RESUME.md`, `build/port-screenshots.ps1` and `Shell/ScreenshotStates.cs` change only in T5.

## Review Focus

1. **A handover falls due while a modal dialog is open** (Settings, a message box, the missing-input dialog): the view is already public-only, the screen comes up once the dialog closes, never under it. Pinned in T3 (`Handover_waits_for_modal_dialog`).
2. **The handover screen is closed with the window's X / Alt+F4 / Cmd+Q:** it asks the main window to close (the usual discard question); answering No leaves the handover screen up and nothing else visible. Pinned in T3 (`Closing_handover_asks_main_window_and_No_keeps_it`).
3. **Undo / step back into the current player's phase while the handover screen is up:** `HandoverCancelled` dismisses the screen and restores every hidden window with that player's view. Pinned in T3 (`Rewind_cancels_handover_and_restores_windows`).
4. **Calculations queued by the phase the next player starts in** never open before Continue (they would show that player's data to the leaving one); after Continue they open if still pending. Pinned in T3 (`Runs_wait_for_continue`).
5. **No rules pack, or a pack with fewer than two sides:** the Players page and Settings box show the WinForms hint, Add player is disabled, the game stays solo, no status-bar player button. Pinned in T1 (`Hint_without_pack`, `Hint_with_one_side`) and T2 (`Players_page_disabled_without_two_sides`).

---

### Task 1: Render models for the players editor and handover texts

**Files:**
- Create: `src/Quartermaster.Render/Players/PlayersEditModel.cs`, `src/Quartermaster.Render/Players/HandoverText.cs`
- Optional modify (pure refactor): `src/Quartermaster.App/PlayersEditor.cs`, `src/Quartermaster.App/HandoverForm.cs`
- Test: `tests/Quartermaster.Render.Tests/PlayersEditModelTests.cs`, `tests/Quartermaster.Render.Tests/HandoverTextTests.cs`

**Interfaces:**
- Consumes: Core `PlayerDef(string Id, string Name, IReadOnlyList<string> Sides)`, `Players.Validate(IReadOnlyList<PlayerDef>, RulesPack)`, `Players.MaxPlayers`; Rules `RulesPack.Sides`, `SideDef(Id, Name)`.
- Produces:
  - `public static class HandoverText`: `string MessageFor(PlayerDef p, string sidesText)` → `$"Pass to {p.Name} ({sidesText})"`; `string SidesText(PlayerDef p, RulesPack? pack)` (body as `HandoverForm.SidesText`); `string StatusText(PlayerDef? current)` → `$"Player: {current?.Name ?? "—"}"`; `string MenuText(PlayerDef p, RulesPack? pack)` → `$"{p.Name} ({SidesText(p, pack)})"`; `const string NoPlayers = "(no players)"`; `const string PlayerTip = "Who is at the screen. Choose a player to hand over."`.
  - `public sealed class PlayersEditModel` with: ctor `(RulesPack? pack, IReadOnlyList<PlayerDef> players)`; `IReadOnlyList<SideDef> Sides`; `IReadOnlyList<PlayerRow> Rows`; `bool CanHaveSeveral`; `bool CanAdd`; `bool CanRemove`; `string Hint`; `PlayerRow Add()`; `void Remove(PlayerRow row)`; `void SetName(PlayerRow row, string name)`; `void SetSide(PlayerRow row, int sideIndex, bool held)` (ticking takes the side from every other row); `IReadOnlyList<PlayerDef> Result`; `string? Problem`; `static bool Same(IReadOnlyList<PlayerDef> a, IReadOnlyList<PlayerDef> b)`; `void FillTwoPlayersForTests()` (port of `SmokeTwoPlayers`); `event Action? Changed`.
  - `public sealed class PlayerRow { string Id; string Name; bool[] Held; }` (read-only from outside; changed through the model).
  - Hint texts verbatim from `PlayersEditor.cs:41-44`; default name `$"Player {Rows.Count + 1}"`; fresh ids never reuse a removed id (`NewId`).

- [ ] **Step 1: Write the failing tests** — `PlayersEditModelTests`: `Starts_with_one_player_holding_every_side` (empty list → one row "Player 1", id "p1", all sides held, `Result` empty); `Ticking_a_side_takes_it_from_others`; `Remove_disabled_with_one_row`; `Add_disabled_at_MaxPlayers`; `Removed_id_not_reused` (add p2, remove it, add → p3); `Result_empty_for_one_player`; `Result_trims_names`; `Problem_null_for_valid_two_players`; `Problem_from_Players_Validate` (a side held by nobody → the Validate text); `Problem_without_pack` → `"Open a rules pack to set up players."`; `Hint_without_pack` (same text, `CanAdd` false); `Hint_with_one_side` → `"The rules pack has fewer than two sides: one player plays everything."`; `Hint_with_sides` → `$"Each side is held by exactly one player (at most {Players.MaxPlayers} players)."`; `Same_ignores_solo_lists_and_respects_order`. `HandoverTextTests`: `MessageFor_formats`, `SidesText_uses_names_in_player_order_and_ids_for_unknown`, `StatusText_dash_without_player`, `MenuText_formats`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Render.Tests -c Release --filter "PlayersEditModel|HandoverText"` — expected FAIL (types missing).
- [ ] **Step 3: Implement** both classes per the Interfaces block; `Problem` wraps `Players.Validate` exceptions as `"The player list could not be checked: " + ex.Message` (as WinForms).
- [ ] **Step 4: Run** the same filter — expected PASS. If WinForms was refactored to forward to these, run `dotnet test tests/Quartermaster.App.Tests -c Release` too.
- [ ] **Step 5: Commit** `feat(render): players edit model and handover texts`.

### Task 2: Players editor control, Players page and Settings > Game > Players

**Files:**
- Create: `src/Quartermaster.Desktop/Players/PlayersEditor.cs`, `src/Quartermaster.Desktop/MainWindow.HotSeat.cs` (only the player-list part here: `GamePlayers()`, `SetGamePlayers`)
- Modify: `src/Quartermaster.Desktop/Shell/SetupChooser.axaml(.cs)`, `Shell/Dialogs.cs` (`SetupChoice` gets `IReadOnlyList<PlayerDef>? Players = null`; `ChooseSetupAsync` gets `RulesPack? pack`), `Program.cs` (`QuietDialogs`), `Launcher/PlayOfflineWindow.cs:197-235`, `MainWindow.axaml.cs:639` (`ChooseAndStartAsync` passes the pack and uses `choice.Players`), `Settings/SettingsContext.cs`, `Settings/GamePage.cs`, `MainWindow.Settings.cs`
- Test: `tests/Quartermaster.Desktop.Tests/PlayersEditorTests.cs`, `SetupChooserPlayersTests.cs`, `GamePagePlayersTests.cs`, test `FakeDialogs`

**Interfaces:**
- Consumes: T1 `PlayersEditModel`, `HandoverText`.
- Produces:
  - `public sealed class PlayersEditor : UserControl` with ctor `(RulesPack? pack, IReadOnlyList<PlayerDef> players)`, `PlayersEditModel Model`, `IReadOnlyList<PlayerDef> Result`, `string? Problem`; one row per player: `TextBox` name (width 130), a `CheckBox` per side (text = side name or id), `Remove` button; bottom: `_Add player` button and the grey hint. Named controls for tests: `Name = "PlayersEditor"`, rows' boxes reachable through `internal` lists.
  - `SetupChooser`: buttons `< _Back` (hidden on page 1) and `_Next >` as WinForms; page 2 hosts the `PlayersEditor`; OK with a `Problem` shows page 2 and a warning box titled `Players` with the problem text and stays open; then `NewGameRules.Validate(req, pack)` as WinForms; `SetupChoice.Players` = `Result` when `Count >= 2`, else null.
  - `SettingsContext` gets `Func<(RulesPack? Pack, IReadOnlyList<PlayerDef> Players)?>? GamePlayers = null` and `Action<IReadOnlyList<PlayerDef>>? SetGamePlayers = null`.
  - `GamePage`: the grey `PlayersNote` is replaced by a "Players" group (440×210 as WinForms): the editor when a game is open, else the grey text `Open a game first` (`SettingsTabs.cs:111,140`); `Validate()` returns `editor.Problem` (the existing `SettingsPage.Validate` hook keeps the dialog open); `AfterApply` calls `SetGamePlayers(editor.Result)` when the list differs (`PlayersEditModel.Same`), as `SettingsTabs.cs:157-161`.
  - `MainWindow.SetGamePlayers(IReadOnlyList<PlayerDef>)`: port of `MainForm.HotSeat.cs:240-262` (Same → no-op; replaying → `ModuleSession.ReplayingText`; guest → `IntentExecutor.HostOnlyText`; validate; execute `SetPlayers` through `CommandLog.Prepare`).

- [ ] **Step 1: Write the failing tests** — `PlayersEditorTests`: `Add_and_remove_rows_follow_model`, `Ticking_side_unticks_other_row`, `Add_disabled_without_two_sides` (hint shown). `SetupChooserPlayersTests`: `Next_shows_players_page_and_Back_returns`, `Ok_with_two_valid_players_returns_them`, `Ok_with_problem_stays_on_players_page` (FakeDialogs records the warning text), `Solo_returns_null_players`, `Players_page_disabled_without_two_sides`. `GamePagePlayersTests`: `No_game_shows_no_game_text`, `Apply_sets_players_on_open_game` (game state has 2 players, `SetPlayers` in the log), `Apply_same_list_executes_nothing`, `Guest_refused_with_HostOnlyText`. Also `PlayOffline_new_game_starts_with_players` (choice with 2 players → `Game.State.Players.Count == 2`).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Desktop.Tests -c Release --filter "PlayersEditor|SetupChooserPlayers|GamePagePlayers|PlayOffline"` — expected FAIL.
- [ ] **Step 3: Implement** per the Interfaces block. Text edits update the model through `Shell/TextEdits.OnTextSet` (Avalonia `TextChanged` is dispatcher-posted).
- [ ] **Step 4: Run** the same filter, then the whole Desktop test project — expected PASS.
- [ ] **Step 5: Commit** `feat(desktop): players editor, new-game Players page, Settings > Game > Players`.

### Task 3: Handover screen and hot-seat wiring

**Files:**
- Create: `src/Quartermaster.Desktop/Players/HandoverWindow.cs`
- Create: `src/Quartermaster.Desktop/Shell/ModalTracker.cs`
- Modify: `src/Quartermaster.Desktop/MainWindow.HotSeat.cs`, `MainWindow.Phases.cs:162-168` (create/destroy `HotSeat`, `HoldRuns`), `MainWindow.Tour.cs` (tour closed on handover), and the `ShowDialog` call sites in `Shell/Dialogs.cs`, `MainWindow.Settings.cs`, `Launcher/LauncherWindow.cs` (one-word switch to `ShowDialogTracked`; the only Dialogs.cs change outside T2)
- Test: `tests/Quartermaster.Desktop.Tests/HandoverTests.cs`

**Interfaces:**
- Consumes: Render `HotSeat` (`Watch`, `TakeOver`, `Pending`, `Current`, `Active`, events `HandoverRequired`, `HandoverCancelled`, `CurrentChanged`), `PhaseRunDispatch.HoldRuns`, `FormulaWindows.CloseAllFor(s)`, T1 `HandoverText`.
- Produces:
  - `public sealed class HandoverWindow : Window`: title `Quartermaster`, min size 360×220, control-colour background; big bold (18 pt) centred text bottom half-top, `Continue` button (IsDefault, focused on open) centred below; `PlayerDef Player`, `string Message`, `void SetPlayer(PlayerDef p, string sidesText)`, `event Action? Continued`, `event Action? CloseRequested` (a user close is cancelled and raised as this), `void CloseForReal()`.
  - `public static class ModalTracker { int Depth; bool Open => Depth > 0; Task<T?> ShowDialogTracked<T>(this Window dlg, Window owner); }`. Every `ShowDialog` call site (Dialogs.cs, MainWindow.Settings.cs, LauncherWindow.cs) switches to `ShowDialogTracked`; message boxes and file pickers raised through `IDialogs` increment `Depth` around their await too. A test grep-guard (`No_untracked_ShowDialog`) fails if a `.ShowDialog(` / `.ShowDialog<` call outside `ModalTracker.cs` appears in `src/Quartermaster.Desktop`.
  - `MainWindow` members: `HotSeat? HotSeat`, `bool HandoverShowing`, `HandoverWindow? Handover` (internal, for tests); methods ported from `MainForm.HotSeat.cs`: `CreateHotSeat`, `DestroyHotSeat`, `OnHandoverRequired`, `OnHandoverCancelled`, `OnCurrentPlayerChanged`, `RequestHandover`, `ShowHandoverWhenFree` (retry every 200 ms while `ModalTracker.Open`, `DispatcherTimer`), `ShowHandover` (close tour, counter popups and formula windows; hide every visible Quartermaster window except the main window and the handover screen, remembering them; the handover screen takes the main window's bounds and maximised state; then hide the main window), `ContinueHandover` (`TakeOver` while all is hidden, dismiss, status `Player: {name}`, open held runs still pending), `DismissHandover`, `OpenRunsAfterHandover`.
  - `CloseRequested` posts `Close()` of the main window; if that close is cancelled, the handover screen stays up. Closing the module / Back to launcher / game replaced runs `DestroyHotSeat`, which dismisses without restoring windows that belong to the closed game.

- [ ] **Step 1: Write the failing tests** (`HandoverTests`, a TEST pack with sides Red/Blue, two players "Anna" (Red) / "Ben" (Blue)): `Next_phase_to_other_player_shows_handover_and_hides_windows` (Ledger and a chart window open → hidden; main window hidden; `Handover.Message == "Pass to Ben (Blue)"`); `View_is_public_only_until_continue` (`Session.ViewSides` empty while up); `Continue_takes_over_and_restores_windows` (ViewSides = {Blue}, windows visible again, status `Player: Ben`); `Handover_waits_for_modal_dialog`; `No_untracked_ShowDialog`; `Closing_handover_asks_main_window_and_No_keeps_it`; `Rewind_cancels_handover_and_restores_windows`; `Runs_wait_for_continue`; `Formula_windows_close_on_handover`; `Retargets_when_second_handover_due` (`SetPlayer` instead of a second window); `Becoming_solo_dismisses_handover`; `Closing_module_while_up_dismisses`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Desktop.Tests -c Release --filter Handover` — expected FAIL.
- [ ] **Step 3: Implement** per the Interfaces block; all `HotSeat` events marshal through the existing `OnUiThread` and are guarded by the game instance (as the 4b/5a handlers).
- [ ] **Step 4: Run** the filter, then the whole Desktop test project — expected PASS.
- [ ] **Step 5: Commit** `feat(desktop): hot-seat handover screen`.

### Task 4: Status-bar player switcher and per-player view in every window

**Files:**
- Modify: `src/Quartermaster.Desktop/MainWindow.axaml` (status bar: `SbPlayer` drop-down button after `SbMessage`, hidden unless hot-seat), `MainWindow.HotSeat.cs` (`UpdatePlayerButton`, `BuildPlayerMenu`, `SwitchPlayer`), and any window from the audit below that does not yet follow `ViewChanged`
- Test: `tests/Quartermaster.Desktop.Tests/PlayerSwitcherTests.cs`, `PlayerViewWindowsTests.cs`

**Interfaces:**
- Consumes: T1 `HandoverText.StatusText/MenuText/NoPlayers/PlayerTip`, T3 `RequestHandover`.
- Produces: `SbPlayer` (name `status.player`, tooltip `HandoverText.PlayerTip`), visible only while `HotSeat.Active`; menu items `MenuText` per `Players.Effective(...)`, checked = current, `(no players)` disabled fallback; `SwitchPlayer(PlayerDef)` clears the view (`SetViewSides(empty)`), closes formula windows and requests the handover — the handover comes first even for the chosen player.
- Audit (each must redraw on `ViewChanged` and show nothing of a side the current player does not hold): map + map tabs (`Map/MapView`, `Shell/MapTabs`), window trees, Ledger (all tabs, counters), Inventory, Log panel (other sides' private lines redacted), Outliner, Phase panel and strip, alert bar, counter preview and thumbnails, formula window and batch grid (close when `!CanAct`), Workbench Alerts / Formulas preview / Properties tabs (the WinForms `SidesWatch` users), counter hover popup (closed on handover).

- [ ] **Step 1: Write the failing tests** — `PlayerSwitcherTests`: `Hidden_in_solo_game`, `Shows_current_player`, `Menu_lists_players_with_sides_and_checks_current`, `Switch_shows_handover_first_with_empty_view`. `PlayerViewWindowsTests` (a TEST module with one Red and one Blue hidden-info piece, Anna current, then handover to Ben + Continue): one test per audited window asserting the Blue piece/line/value is absent before and present after (and the Red one the reverse), e.g. `Ledger_follows_current_player`, `Log_redacts_other_sides_private_lines`, `Workbench_properties_tab_follows_view`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Desktop.Tests -c Release --filter "PlayerSwitcher|PlayerViewWindows"` — expected FAIL (switcher missing; any audit gaps fail).
- [ ] **Step 3: Implement** the switcher; fix each window the audit tests show not following the view (subscribe, post, guard by session instance, as the windows that already do).
- [ ] **Step 4: Run** the filter, then the whole Desktop test project — expected PASS.
- [ ] **Step 5: Commit** `feat(desktop): status-bar player switcher; every window follows the player view`.

### Task 5: Screenshots, docs, full suite

**Files:**
- Modify: `src/Quartermaster.Desktop/Shell/ScreenshotStates.cs` (state `handover`: TEST two-player game, handover screen for player 2, captured as its own window), `build/port-screenshots.ps1` (writes `artifacts/port/stage5b-desktop-handover.png`, `stage5b-desktop-players-page.png`, `stage5b-desktop-settings-players.png`, `stage5b-desktop-player-menu.png`), `docs/port/parity.md` (tick PlayersEditor, HandoverForm, SidesWatch, status-bar player item for Windows; lines 16 and 119 updated; "Stage-5b deviations" section incl. the scope rulings), `docs/port/stage5b-mac-checklist.md` (new), `docs/superpowers/RESUME.md` (CURRENT STATE for 5b)

- [ ] **Step 1:** Add the screenshot state and script entries; run `build/port-screenshots.ps1` (headless, off-screen) and check the four PNGs exist and show the handover text, the Players page, the Settings box and the open player menu.
- [ ] **Step 2:** Write the Mac checklist: New game → Next > → two players → OK; Next Phase → handover screen fills the main window's place, nothing else visible (check Mission Control / ⌘+Tab shows no other Quartermaster window); Continue; status-bar player menu → switch; Settings > Game > Players change → Apply; Cmd+Q on the handover screen → discard question; undo during handover.
- [ ] **Step 3:** Update parity.md and RESUME.md.
- [ ] **Step 4:** Run `dotnet test Quartermaster.slnx -c Release` — expected 0 failed (known-flaky tests may be re-run once; report counts).
- [ ] **Step 5: Commit** `docs(port): stage 5b parity, Mac checklist, screenshots`.
