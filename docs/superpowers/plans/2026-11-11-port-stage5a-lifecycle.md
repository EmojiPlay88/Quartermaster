# Cross-platform Port — Stage 5a (app lifecycle) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Avalonia app starts at a launcher, plays offline (Continue, recent saves, New game, Open saved game), returns with File > Back to launcher, and has Help > Interface Tour, About and the update screens, which run against the test feed only.

**Architecture:**
- Launcher and update logic that lives in WinForms forms moves into small Render models, unit-tested and usable by both apps. `ModuleLibrary`, `RecentGames`, `LaunchRequest`, `UpdateChecker`, `TourSteps` and `AppVersion` are reused as they are.
- The Desktop app gets three new windows (`LauncherWindow`, `PlayOfflineWindow`, `TourOverlay`) and a `LaunchLoop` that alternates launcher ↔ `MainWindow`, as WinForms `Program.Main` does.
- The update screens use a `DesktopUpdates` state object whose feed is null unless `QUARTERMASTER_UPDATE_FEED` is set.

**Tech Stack:** C# .NET 10, Avalonia 12.1.5, Avalonia.Headless.XUnit, xUnit. No new packages.

**Spec:** `docs/superpowers/specs/2026-11-11-port-stage5-design.md` (approved 2026-11-11; sub-stage 5a and "Updater split"). Behaviour reference: `src/Quartermaster.App/LauncherForm.cs`, `PlayOfflineForm.cs`, `LauncherShots.cs`, `AppUpdates.cs`, `UpdateBar.cs`, `UpdateDialog.cs`, `WhatsNewDialog.cs`, `TourOverlay.cs`, `MainForm.Tour.cs`, `MainForm.cs:231,269-271,489-535,562-580`, `Program.cs:229-350`.

## Scope rulings (made at planning)

- **Online lobby button** (launcher) is shown, disabled, with the tooltip `Online play comes with stage 5c.` **Open turn file…** (Play offline) is shown, disabled, with `Turn files come with stage 5d.`
- **No update notices** (`--updated-from`, `--update-failed`, `--update-refused`, `UpdateStartup`). They belong to installing, which is stage 6.
- **The "Check for updates" link and Help > Check for updates… exist only while `DesktopUpdates.Feed` is not null** (the test feed is set). In normal use they are hidden, so nothing can reach GitHub.
- **Desktop update state** (last check, skipped version) lives in `AppDataRoot.Sub("Updates-desktop")/state.json`, not in the shared `settings.json`. `Settings.LastUpdateCheckUtc` and `Settings.SkippedVersion` stay WinForms-only.
- **No `--smoke-launcher`.** Headless tests cover the launcher ↔ game loop instead.
- **Auto tour on first game open** runs only when the window was started by `LaunchLoop` (`MainWindow.AutoTour = true`), never in tests or screenshot runs, as in WinForms smoke mode.
- **WinForms is unchanged**, except that it may call the new Render models if that is a pure refactor. That is optional; if WinForms is touched, `--smoke 3`, `--screenshot-launcher` and `--screenshot-offline` must still pass.

## Global Constraints

- Labels, tooltips and message texts exactly as the named WinForms file. Classic grey look. Cmd on macOS through `Input/ShortcutGestures`.
- Tests are headless (Avalonia.Headless), never open a visible window, use a temp layout root (`MainWindow(dialogs, cache, null, layoutRoot)` / `new SettingsStore(tmp)`), and never touch the real network, relay, GitHub or profile.
- The Desktop app never builds a `GitHubFeed` and never creates an `HttpClient` for updates in stage 5a.
- Formats unchanged: rules pack 4, save 7, `settings.json` (no new fields).
- `Quartermaster.Updater` and `build/release.ps1` are not changed.
- Commits: repo-local noreply identity; every message ends with a blank line and a `Co-Authored-By:` trailer naming the model. Never push.
- Green bar per task: `dotnet build Quartermaster.slnx -c Release` with 0 new warnings, and the task's test projects green. T6 runs the full `dotnet test Quartermaster.slnx -c Release` (flaky tests listed in RESUME may be re-run once).
- Shared files:
  - `Shell/Dialogs.cs`, `Program.QuietDialogs` and the test `FakeDialogs` change only in T2.
  - `MainWindow*.cs` and `App.axaml.cs` change in T3, T4 and T5, in that order.
  - `docs/port/*`, `RESUME.md`, `build/port-screenshots.ps1` and `Shell/ScreenshotStates.cs` change only in T6.

## Review Focus

1. **A module file moved or deleted after it was added:** the row shows `{file} (file not found)` in grey, and Play offline refuses with the WinForms message, without crashing. Pinned in T1 (`Missing_row_texts`) and T2 (`Play_offline_on_missing_module_refuses`).
2. **Closing the launcher window** ends the app, with no hidden window left. Closing the game window *without* Back to launcher also ends the app. Pinned in T3 (`Closing_launcher_ends_loop`, `Closing_game_window_ends_loop`).
3. **Back to launcher with unsaved changes:** the existing discard question; No keeps the game open and stays out of the launcher. Pinned in T3 (`Back_to_launcher_respects_discard_question`).
4. **No test feed set:** no update check, no link, no Help item, no network. Pinned in T5 (`No_feed_means_no_update_ui_and_no_check`).
5. **Tour on a game with no rules pack, or with hidden panels:** steps without a visible target are skipped, and the "(i of n)" count matches the visible steps. Pinned in T4 (`Tour_skips_missing_targets`).

---

### Task 1: Render models for the launcher, play offline and update offer

**Files:**
- Create: `src/Quartermaster.Render/Launcher/LauncherModel.cs`, `src/Quartermaster.Render/Launcher/PlayOfflineModel.cs`, `src/Quartermaster.Render/Update/UpdateOffer.cs`
- Test: `tests/Quartermaster.Render.Tests/LauncherModelTests.cs`, `tests/Quartermaster.Render.Tests/UpdateOfferTests.cs`

**Interfaces:**
- Consumes: the existing `ModuleLibrary`, `ModuleRow`, `ModuleEntry`, `RecentGames`, `ContinueInfo`, `LaunchRequest`, `LaunchKind`, `NewGameRequest`, `Settings`, `UpdateInfo`, `CheckResult`, `CheckOutcome`, `UpdateChecker.Due` and `AppVersion`.
- Produces:
  - `public static class LauncherModel` with these members:
    - `const string NotFound = "(file not found)"`.
    - `static string TitleText(ModuleRow r)`: the title, or the file name when the title is blank.
    - `static string FileText(ModuleRow r)`: `"{file} (file not found)"` when the row is missing.
    - `static string LastPlayedText(ModuleEntry e)`: `"yyyy-MM-dd HH:mm"` local time, or `""`.
    - `static string RulesLabel(string? rulesPath)`: `"Rules pack: (none)"` / `"Rules pack: {file name}"`.
    - `static int InitialSelection(IReadOnlyList<ModuleRow> rows, string? justPlayed, Settings s)`. It returns -1 when there are no rows. Otherwise it picks the justPlayed path if present; then, when `s.ReopenLastModule`, `s.LastModulePath`; then the first row that isn't missing.
    - `static LauncherButtons Buttons(ModuleRow? selected, bool hashReady)`.
    - `const string MissingModuleText = "This module's file was not found. Remove it from the list or add it again."`, `const string MissingTooltip = "This module's file was not found. Remove it or add it again."`, `const string NotReadyText = "This module is still being checked; try again in a moment."` and `const string FooterFormat = "Quartermaster {0}"`.
  - `public sealed record LauncherButtons(bool PlayOffline, bool Remove, string? PlayOfflineTip)`.
  - `public static class PlayOfflineModel` with these members:
    - `static string ContinueText(ContinueInfo? c)`: the exact WinForms `PlayOfflineForm.ContinueText`.
    - `static string Title(ModuleRow r)`: `"Play offline – {TitleText}"`.
    - `static LaunchRequest Finish(ModuleRow r, string? rulesPath, LaunchKind kind, NewGameRequest? ng, string? savePath)`.
    - `const string SaveMissingText = "That saved game's file was not found."`.
  - `public static class UpdateOffer` with these members:
    - `static string BarText(UpdateInfo i)`: `"Quartermaster {AppVersion.Format(i.Version)} is available."`.
    - `static readonly IReadOnlyList<string> BarActions = ["What's new", "Update now", "Later", "Skip this version"]`.
    - `static bool AutoCheckDue(bool checkUpdatesOnStart, bool checkedThisRun, DateTime? lastUtc, DateTime nowUtc)`.
    - `static string ManualText(CheckResult r)`: the UpToDate and Failed texts as in WinForms.
    - `static string SkippedQuestion(UpdateInfo i)`.
    - `static string WhatsNewTitle(UpdateInfo i)`, `const string NoNotes = "(This release has no notes.)"`.
    - `static string DownloadStatus(string zipName, long bytes)`: `"Downloading {zip} ({MB:0.0} MB)…"`.
    - `const string Unpacking = "Checking and unpacking…"`, `const string Cancelling = "Cancelling…"`, `const string NotInstalledFormat = "The update was not installed; nothing was changed.\n\n{0}"`.

- [ ] **Step 1: Write the failing tests.** Copy every text from the WinForms file named in the Interfaces.

```csharp
[Fact] public void Missing_row_texts()                    // FileText "x.vmod (file not found)"; TitleText falls back to file name when Title blank
[Fact] public void Initial_selection_prefers_just_played_then_last_module_then_first_present()
[Fact] public void Initial_selection_ignores_last_module_when_reopen_off()
[Fact] public void Buttons_disable_play_on_missing_and_not_ready()   // tip MissingTooltip / NotReadyText
[Fact] public void Rules_label_none_and_named()
[Fact] public void Continue_text_matches_winforms()      // null -> "Continue: (no autosave yet)"; with turn+phase; with neither
[Fact] public void Finish_builds_launch_request()
// UpdateOfferTests
[Fact] public void Bar_text_and_actions()                 // 9.9.9 -> "Quartermaster 9.9.9 is available."
[Fact] public void Auto_check_due_rules()                 // off, already checked this run, not due (<24h), due
[Fact] public void Manual_texts_for_up_to_date_and_failed()
[Fact] public void Download_status_formats_megabytes()    // 1572864 bytes -> "(1.5 MB)"
```

- [ ] **Step 2:** Run `dotnet test tests/Quartermaster.Render.Tests --filter "LauncherModelTests|UpdateOfferTests"`. Expected: FAIL (types missing).
- [ ] **Step 3: Implement** the three static classes. They are pure, with no I/O beyond `Path.GetFileName`.
- [ ] **Step 4:** Run the filter. Expected: PASS.
- [ ] **Step 5: Commit:** "feat(render): launcher, play offline and update offer models".

### Task 2: Launcher and Play offline windows

**Files:**
- Create: `src/Quartermaster.Desktop/Launcher/LauncherWindow.cs`, `src/Quartermaster.Desktop/Launcher/PlayOfflineWindow.cs`
- Modify: `src/Quartermaster.Desktop/Shell/Dialogs.cs` (add `Task<IReadOnlyList<string>> OpenFilesAsync(Window owner, string title, IReadOnlyList<FileFilter> filters)` for multi-select Add module), `src/Quartermaster.Desktop/Program.cs` (`QuietDialogs`), the test `FakeDialogs`
- Test: `tests/Quartermaster.Desktop.Tests/LauncherWindowTests.cs`

**Interfaces:**
- Consumes: T1's models, plus `ModuleLibrary`, `RecentGames`, `SettingsStore`, `IDialogs` (including `ChooseSetupAsync`), `ModuleSession.Open` and `OpenRules`.
- Produces:
  - `public sealed class LauncherWindow : Window`:
    - Constructor: `LauncherWindow(SettingsStore store, string layoutRoot, IDialogs dialogs, string? justPlayed, IUpdateSlot? updates = null)`. T2 declares `public interface IUpdateSlot { void Attach(LauncherWindow w); }` in `Launcher/IUpdateSlot.cs`, and calls `updates?.Attach(this)` once the window is open. T5 implements the interface.
    - Members: `Task<LaunchRequest?> Result`, `Task RefreshTask`, `ModuleLibrary Library` (internal), `SettingsStore Store` (internal), `IDialogs Dialogs` (internal), `ListBox Modules` (internal; rows show Module / Version / File / Last played), `string FooterText`.
    - Bar slots for T5: `internal Panel UpdateBarHost` and `internal Panel FooterLinks`.
    - Buttons, with exact captions: "Add module…", "Remove" (the Delete key too), "Online lobby ▶" (disabled, scope ruling), "Play offline ▶" (default).
    - The rules row: `Rules pack: …` with "change…" and "use (none)", the tooltip `Play this module without a rules pack.`, and the progress text `Checking modules… N of M`.
    - The title is "Quartermaster"; size 680x400, minimum 560x320.
  - `public sealed class PlayOfflineWindow : Window`:
    - Constructor: `PlayOfflineWindow(ModuleRow row, ModuleLibrary library, RecentGames recent, SettingsStore store, IDialogs dialogs, string cacheRoot)`.
    - Result: `Task<LaunchRequest?> Result`. The window is 520x360.
    - The Continue button and line; a recent-games list with the columns "Recent games" and "Saved".
    - Buttons: "New game…", "Open saved game…" (filter `Quartermaster saves (*.qsave)`, starting in the newest save's folder), "Open turn file…" (disabled, scope ruling), "Open selected", "Back".
    - New game opens the module on a worker (with the remembered rules pack), calls `dialogs.ChooseSetupAsync(this, session.Model, preselectNew: false, store.Current.LastDifficulty)`, stores `LastDifficulty`, and finishes with `NewGameRequest(Offline, choice.Setup, choice.Difficulty, null, null, null)`.
    - While that runs, a busy flag blocks closing and the buttons.

- [ ] **Step 1: Write the failing headless tests** (temp layout root; the module is `Demo("Riverland-Demo.vmod")`, copied to temp):

```csharp
[AvaloniaFact] public void Lists_added_module_with_title_version_and_last_played()
[AvaloniaFact] public void Add_module_uses_multi_select_and_adds_each()
[AvaloniaFact] public void Remove_and_delete_key_remove_selected()
[AvaloniaFact] public void Play_offline_on_missing_module_refuses()       // ShowInfoAsync(MissingModuleText); Result not completed
[AvaloniaFact] public void Online_lobby_is_disabled_with_stage_tip()
[AvaloniaFact] public void Rules_pack_change_and_use_none_persist()      // ModuleLibrary.RulesPackFor after reopen
[AvaloniaFact] public void Closing_launcher_completes_result_null()
// PlayOffline
[AvaloniaFact] public void Continue_line_shows_latest_autosave()
[AvaloniaFact] public void Continue_returns_continue_request()
[AvaloniaFact] public void New_game_returns_new_game_request_and_stores_difficulty()
[AvaloniaFact] public void Open_selected_missing_save_says_not_found()
[AvaloniaFact] public void Back_returns_null()
```

- [ ] **Step 2:** Run `dotnet test tests/Quartermaster.Desktop.Tests --filter LauncherWindowTests`. Expected: FAIL.
- [ ] **Step 3: Implement** the windows, using the existing Desktop dialog style. The refresh runs `Library.RefreshAsync` with progress posted to the UI thread.
- [ ] **Step 4:** Run the filter, then all of `tests/Quartermaster.Desktop.Tests`. Expected: PASS.
- [ ] **Step 5: Commit:** "feat(desktop): launcher and play offline windows".

### Task 3: Launcher ↔ game loop and Back to launcher

**Files:**
- Create: `src/Quartermaster.Desktop/Launcher/LaunchLoop.cs`
- Modify: `src/Quartermaster.Desktop/App.axaml.cs`, `src/Quartermaster.Desktop/MainWindow.axaml.cs` (File menu, start from a launch request, closing), `src/Quartermaster.Render/CommandAvailability.cs` (`file.backToLauncher`)
- Test: `tests/Quartermaster.Desktop.Tests/LaunchLoopTests.cs`

**Interfaces:**
- Consumes: the T2 windows, `LaunchBookkeeping`, `StartupDecision`.
- Produces:
  - `public sealed class LaunchLoop`:
    - Constructor: `LaunchLoop(Func<string?, LauncherWindow> launcher, Func<MainWindow> game, Action<Window> setMainWindow, Action shutdown)`.
    - Method: `Task RunAsync()`. It shows the launcher, awaits `Result`, and stops (calls `shutdown`) on null. Otherwise it creates the game window, sets `LauncherAvailable = true` and `AutoTour = true`, awaits `StartFromLaunchAsync`, and waits for it to close. If `ReturnToLauncher` is set, it loops with `justPlayed = OpenedModulePath ?? request.ModulePath`; otherwise it shuts down.
  - `MainWindow` additions:
    - `internal bool LauncherAvailable { get; set; }`.
    - `internal bool AutoTour { get; set; }`.
    - `public bool ReturnToLauncher { get; }` and `public string? OpenedModulePath { get; }`.
    - `public Task StartFromLaunchAsync(LaunchRequest r)`. It opens the module and loads the rules pack if one is set and differs. Then by kind:
      - NewGame: `LoadSetup` / `StartNewGame` with the difficulty, storing `LastDifficulty`.
      - OpenSave / Continue: `OpenGame`.
      - Any other kind: `ChooseAndStartAsync`.
    - After that, `LaunchBookkeeping.ModuleOpened` and `SaveUsed`.
    - `internal Task BackToLauncherAsync()`.
    - The File menu gets "Back to launcher", id `file.backToLauncher`, visible only when `LauncherAvailable`.
  - App startup: with no module path and no screenshot args, `App` runs a `LaunchLoop` (`desktop.ShutdownMode = OnExplicitShutdown`). Any module argument keeps today's direct start (`StartupDecision.For`).

- [ ] **Step 1: Write the failing tests** (fake launcher factory returning windows whose `Result` is set by the test):

```csharp
[AvaloniaFact] public void Closing_launcher_ends_loop()                      // shutdown called once, no game window
[AvaloniaFact] public void New_game_request_opens_game_and_marks_played()  // library LastPlayed set
[AvaloniaFact] public void Back_to_launcher_reopens_launcher_with_just_played_selected()
[AvaloniaFact] public void Back_to_launcher_respects_discard_question()    // FakeDialogs answers No -> game stays, launcher not shown
[AvaloniaFact] public void Closing_game_window_ends_loop()
[AvaloniaFact] public void Open_save_request_loads_save_and_adds_recent()
[AvaloniaFact] public void Back_to_launcher_menu_hidden_without_launcher()
[AvaloniaFact] public void Module_argument_skips_launcher()                // StartupDecision.DirectModule path unchanged
```

- [ ] **Step 2:** Run `dotnet test tests/Quartermaster.Desktop.Tests --filter LaunchLoopTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** Back to launcher sets a flag and calls `Close()`. The existing `OnClosing` confirmations still apply, and a cancelled close clears the flag.
- [ ] **Step 4:** Run the filter, then all of `tests/Quartermaster.Desktop.Tests`. Expected: PASS.
- [ ] **Step 5: Commit:** "feat(desktop): launcher loop and Back to launcher".

### Task 4: Help menu, Interface Tour, About

**Files:**
- Create: `src/Quartermaster.Desktop/Shell/TourOverlay.cs`, `src/Quartermaster.Desktop/MainWindow.Tour.cs`
- Modify: `src/Quartermaster.Desktop/MainWindow.axaml.cs` (`Top("&Help")` with "Interface &Tour" `help.tour` and "&About" `help.about`; the auto tour after a launch start)
- Test: `tests/Quartermaster.Desktop.Tests/TourTests.cs`

**Interfaces:**
- Consumes: `TourSteps.All`, `TourStep`, and `Settings.TourShown`.
- Produces:
  - `public sealed class TourOverlay : Window`, a borderless balloon owned by the main window:
    - Constructor: `TourOverlay(IReadOnlyList<TourStep> steps, Func<string, Rect?> resolve, Action<bool> finished)`.
    - Members: `int Index`, `int Count` (visible steps), `string CurrentTitle` (`"{Title}  (i of n)"`), `bool IsDone`, `void Go(int index)`.
    - Buttons "< Back", "Next >" ("Done" on the last step) and "Skip tour"; Esc skips.
    - Placement:
      - Width 330, arrow 12, padding 10, tooltip-yellow fill.
      - A wide target (width ≥ 2×height) tries Below, Above, Right, Left; any other tries Right, Left, Below, Above.
      - Fall back when there's no room.
  - `MainWindow.ShowTour()` does nothing without a game or with a tour already open.
  - `internal Rect? ResolveTourTarget(string id)` maps these targets, in window coordinates: ModuleTree, MapWindow (the active map view), PhaseStrip, PhasePanel, AlertBar, OutlinerPanel, LedgerMenu (the View menu, only with a rules pack) and EditMenu.
  - `MarkTourShown()`.
  - The auto tour runs once after `StartFromLaunchAsync` when `AutoTour && !TourShown`.
  - About shows `ShowInfoAsync(this, $"Quartermaster {AppVersion.Text}\nA VASSAL module reader and engine.", "About Quartermaster")`.

- [ ] **Step 1: Write the failing headless tests** (a game on Riverland-Demo, temp layout):

```csharp
[AvaloniaFact] public void Help_menu_has_tour_and_about()
[AvaloniaFact] public void Tour_walks_visible_steps_and_done_marks_shown()
[AvaloniaFact] public void Tour_skips_missing_targets()                  // no rules pack -> LedgerMenu step skipped, "(1 of 7)"
[AvaloniaFact] public void Skip_and_escape_mark_shown()
[AvaloniaFact] public void Tour_needs_a_game()
[AvaloniaFact] public void Auto_tour_only_when_AutoTour_and_not_shown()
[AvaloniaFact] public void Balloon_prefers_below_for_wide_target_and_right_for_tall()
[AvaloniaFact] public void About_shows_version_text()
```

- [ ] **Step 2:** Run `dotnet test tests/Quartermaster.Desktop.Tests --filter TourTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** Keep the placement maths in a static, testable method: `static (Point topLeft, TourSide side) Place(Rect target, Size balloon, Rect screen)`.
- [ ] **Step 4:** Run the filter, then all of `tests/Quartermaster.Desktop.Tests`. Expected: PASS.
- [ ] **Step 5: Commit:** "feat(desktop): Help menu, Interface Tour and About".

### Task 5: Update screens on the test feed

**Files:**
- Create: `src/Quartermaster.Desktop/Updates/DesktopUpdates.cs`, `src/Quartermaster.Desktop/Updates/UpdateBar.cs`, `src/Quartermaster.Desktop/Updates/UpdateWindow.cs`, `src/Quartermaster.Desktop/Updates/WhatsNewWindow.cs`
- Modify: `src/Quartermaster.Desktop/Launcher/LauncherWindow.cs` (attach through `IUpdateSlot`), `src/Quartermaster.Desktop/MainWindow.axaml.cs` (Help > "Check for &updates…" `help.updates`, only when `LauncherAvailable && DesktopUpdates.Feed != null`), `src/Quartermaster.Desktop/App.axaml.cs` (pass the updates object)
- Test: `tests/Quartermaster.Desktop.Tests/DesktopUpdatesTests.cs`

**Interfaces:**
- Consumes: T1 `UpdateOffer`, `UpdateChecker.Check`, `FolderFeed`, `UpdateFeeds.EnvVar`, `UpdateInstaller.Download`, `UpdateInstaller.CleanupOld`, and the T2 `IUpdateSlot`.
- Produces:
  - `public sealed class DesktopUpdates : IUpdateSlot`:
    - Constants: `const string FolderName = "Updates-desktop"` and `const string InstallLaterText = "Installing updates comes with the packaged release (stage 6)."`.
    - `static DesktopUpdates FromEnvironment(string? appDataRoot = null)`. The feed is a `FolderFeed` only when `UpdateFeeds.EnvVar` is set; otherwise it is null.
    - Members: `IUpdateFeed? Feed`, `string UpdatesRoot`, `bool CheckedThisRun`, `bool CheckOnNextLauncher`, `UpdateInfo? Offered`, `bool Later`.
    - State: `DateTime? LastCheckUtc` and `string? SkippedVersion`, read from and written to `UpdatesRoot/state.json`.
    - `void Attach(LauncherWindow w)`: does nothing when `Feed` is null. Otherwise it adds the footer link "Check for updates" and runs the auto check when `UpdateOffer.AutoCheckDue(store.Current.CheckUpdatesOnStart, …)` or `CheckOnNextLauncher`.
  - `public sealed class UpdateBar : Border`: pale-yellow `#FFF4C8` with a bottom line. Members: `void Show(string text, params (string Caption, Action Click)[] actions)` (empty text hides it), `void Clear()`, `void SetActionEnabled(string caption, bool on)`, `string Message`, `IReadOnlyList<string> Actions`.
  - The bar's actions:
    - "What's new" opens `WhatsNewWindow`: the title from `UpdateOffer.WhatsNewTitle`, monospace read-only notes (or `UpdateOffer.NoNotes`), an "Open the release page" link and "Close".
    - "Update now" opens `UpdateWindow`, which downloads to `UpdatesRoot` with progress and Cancel and the `UpdateOffer` status texts. On success it shows `InstallLaterText` and never starts the helper.
    - "Later" hides the bar for this run.
    - "Skip this version" saves `SkippedVersion`.
  - Manual check texts: `UpdateOffer.ManualText`; Skipped asks `SkippedQuestion`, and Yes clears the skip. In a game, Help > Check for updates… asks `"Close the game and use the launcher to update.\n\nGo back to the launcher now (File → Back to launcher)?"` with the title "Check for updates". Yes sets `CheckOnNextLauncher` and goes back to the launcher.

- [ ] **Step 1: Write the failing tests** (a temp folder feed written like WinForms `LauncherShots.ShotUpdateBar`: release.json for `quartermaster-v9.9.9`, a dummy zip and `.sha256`; the feed is passed directly, not through the process environment):

```csharp
[AvaloniaFact] public void No_feed_means_no_update_ui_and_no_check()     // FromEnvironment with var unset: Feed null; Attach adds nothing
[AvaloniaFact] public void Feed_offer_shows_bar_text_and_actions()
[AvaloniaFact] public void Skip_this_version_persists_in_desktop_state_not_settings()
[AvaloniaFact] public void Later_hides_bar_for_this_run()
[AvaloniaFact] public void Whats_new_shows_notes_or_placeholder()
[AvaloniaFact] public void Update_now_downloads_then_says_install_later_and_starts_nothing()
[AvaloniaFact] public void Manual_check_up_to_date_and_failed_texts()
[AvaloniaFact] public void Help_check_for_updates_in_game_asks_then_goes_back()
[AvaloniaFact] public void Auto_check_respects_setting_and_24h()
```

- [ ] **Step 2:** Run `dotnet test tests/Quartermaster.Desktop.Tests --filter DesktopUpdatesTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** No `GitHubFeed`, and no `HttpClient`.
- [ ] **Step 4:** Run the filter, then all of `tests/Quartermaster.Desktop.Tests`. Expected: PASS.
- [ ] **Step 5: Commit:** "feat(desktop): update screens on the test feed".

### Task 6: Screenshots, docs, full suite

**Files:**
- Modify: `src/Quartermaster.Desktop/Program.cs` (pre-game screenshot states `--screenshot-launcher <png>`, `--screenshot-offline <png>`, `--screenshot-update-bar <png>`; they need `QUARTERMASTER_LAYOUT_ROOT` set, as `LauncherShots.RequireScratchRoot` does, and seed as `LauncherShots.Seed` does), `src/Quartermaster.Desktop/Shell/ScreenshotStates.cs` (a `tour` state: the balloon on step 1), `build/port-screenshots.ps1`, `docs/port/parity.md`, `docs/superpowers/RESUME.md`
- Create: `docs/port/stage5a-mac-checklist.md`

- [ ] **Step 1:** Add the screenshot paths and states. Run `build/port-screenshots.ps1` and look at all four pictures: launcher, offline, update bar, tour.
- [ ] **Step 2: parity.md.**
  - Tick Windows on rows 162, 189, 207, 212 and 290. Row 212 notes "screens only; installing is stage 6".
  - Add a "Stage-5a deviations" list: the scope rulings above, and that the update UI exists only with `QUARTERMASTER_UPDATE_FEED`.
  - Remove the 4b note that "Select the last module in the launcher" does nothing.
- [ ] **Step 3: Mac checklist.** Cover these steps:
  - start with no arguments → launcher;
  - add a module;
  - Play offline → New game;
  - File → Back to launcher (Cmd shortcut menu text);
  - Continue;
  - Help → Interface Tour;
  - About;
  - with a test feed, the update bar, What's new and Update now → the stage 6 message.
- [ ] **Step 4:** Run the full `dotnet test Quartermaster.slnx -c Release`. Expected: all pass (known flaky tests may be re-run once).
- [ ] **Step 5: RESUME.md.** Write a CURRENT STATE for 5a; note that the 4c entry is merged (PR #4, 7bd4feb); next: 5b plan after 5a merges.
- [ ] **Step 6: Commit:** "docs(port): stage 5a parity, Mac checklist, screenshots".
