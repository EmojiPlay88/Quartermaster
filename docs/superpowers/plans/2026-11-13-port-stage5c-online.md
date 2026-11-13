# Cross-platform Port — Stage 5c (online play) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Avalonia app plays live online games as the WinForms app does: from the launcher's Online lobby a player joins a listed lobby, creates one or hosts a saved game. In the game window the host manages seats (Game > Players…), a guest chooses sides and plays through the host, both chat under the log, a guest's Workbench is read-only, and Game > Leave Online Game ends it. A dropped guest rejoins.

**Architecture:**
- The online logic is already shared and is used unchanged: Render `Online/` (`HostSession`, `GuestSession`, `IntentExecutor`, `OnlineLobbyModel`, `Seats`, `RejoinTokens`, `RelayTransport`, `ChatText`), `Actions/RemoteActions`, `ActionPortSwitch`, `LocalActions`, `FormulaMasking`, Net `RelayClient` / `PinStore` / `Secrets`.
- Desktop already has the guest-aware pieces from earlier stages: `Shell/ChatBox` + `LogPanel.SetChat`, the guest paths of `FormulaWindow` / `BatchGridWindow` / `FormulaPreview` (masking, send to host), `HistoryCommands` guest refusals, `MapView.Hover` remote piece commands.
- What WinForms keeps inside forms moves to Render first (T1): the seat rows and the side-choice rules. The Desktop app then gets the dialogs (T2), the lobby window and launcher wiring (T3), `MainWindow.Online.cs`, a port of WinForms `MainForm.Online.cs` (T4), and the read-only Workbench plus an end-to-end test over a real in-process relay (T5).

**Tech Stack:** C# .NET 10, Avalonia 12.1.5, Avalonia.Headless.XUnit, xUnit. No new NuGet packages. `Quartermaster.Desktop.Tests` gains a project reference to `src/Quartermaster.Relay` (in-process relay, as `Quartermaster.Render.Tests` already does).

**Spec:** `docs/superpowers/specs/2026-11-11-port-stage5-design.md` (sub-stage 5c). Behaviour reference: `src/Quartermaster.App/MainForm.Online.cs`, `OnlineLobbyForm.cs` (with `JoinDialog`, `HostSavedDialog`, `RelayPrompts`), `SeatsDialog.cs` (with `SideChoiceDialog`, `HostSidesPanel`), `ChatBox.cs`, `LauncherForm.cs:360-395`, `SetupChooserDialog.cs` (Online mode), `MainForm.cs:425-540` (launch kinds), `Workbench/WorkbenchWindow.cs:175-198` (read-only).

## Scope rulings (made at planning)

- **The alert-bar notice chip moves to 5d.** In WinForms its only source and only click action are the turn mailbox (`MainForm.Turns.cs:42, 628`, "Click to open the turn and replay it."). There is nothing to click in 5c. Parity row "notice chip" stays open.
- **Export Viewer Copy…, the turn-mailbox watch (`UpdateMailboxWatch`) and `UpdateTurnUi`** are 5d, as the spec's 5d row says. `MainWindow.Online.cs` leaves a one-line comment where WinForms calls them.
- **Game > Send Turn…** is 5d. In 5c the Game menu holds only Players… and Leave Online Game, and is visible only while online or starting (WinForms shows it for any game because of Send Turn).
- **Rejoin needs no new code.** `GuestSession` stores and uses `RejoinTokens` under `AppDataRoot`. 5c pins it with a test (T5).
- **Formula input masking is already in Desktop** (`FormulaMasking.Configure` / `SendAsync` in the formula windows). 5c pins it end to end with a real guest (T5) and fixes only what that test shows.
- **Certificate trust.** `RelayClient.ConnectAsync` takes a synchronous `Func<CertificateCheck,bool>` that may run on any thread. Desktop always calls `ConnectAsync` from `Task.Run`, so the callback is never on the UI thread. The callback blocks on `Dispatcher.UIThread.InvokeAsync(() => dialogs.AskYesNoAsync(owner, message, title, defaultNo: changed)).GetAwaiter().GetResult()`. If it is ever called on the UI thread it returns false and traces (never deadlock). Trusted pins share the WinForms file (`AppDataRoot.Path/relay-pins.json`).
- **No `--smoke-online` port.** Headless tests with the in-process relay cover it. The screenshots come from `--screenshot-state` (T6).

## Global Constraints

- Labels, tooltips, titles and message texts exactly as the named WinForms file (constants from Render where they exist: `OnlineLobbyModel.*Text`, `HostSession.*Text`, `GuestSession.*Text`, `OnlineLobbyForm.JoinText/CreateText/HostSavedText/RefreshText` copied verbatim). Classic grey look. Cmd on macOS through `Input/ShortcutGestures`.
- Relay protocol, save format 7, rules pack format 4 and `settings.json` unchanged (`RelayAddress`, `PlayerName` already exist). A WinForms host and a Avalonia guest must play together (same Render sessions, so no protocol code is written).
- Every network event reaches a window through a `PostToUi` that runs on `Dispatcher.UIThread`, is guarded, and is dropped once the window is closed. Nothing the relay or another player sends may crash a window. Relay text is shown only after `OnlineLobbyModel.CleanText(…, GuestSession.MaxHostText)`.
- Tests are headless, never open a visible window, use a temp layout root (`QUARTERMASTER_LAYOUT_ROOT` via the existing test helper, `new SettingsStore(tmp)`), use only the in-process relay bound to loopback (`RelayApp.Build` with `Relay:BindAddress = loopback`, as `Piece6bEndToEndTests`), never the real relay, GitHub or profile.
- Avalonia `TextChanged` is dispatcher-posted: anything that must follow typed text in the same tick (the open-lobby warning, OK checks) uses `Shell/TextEdits.OnTextSet`.
- Test content is invented and labelled TEST (sides Red/Blue/Green). No real-game content. The repo is public: no personal paths.
- Commits: repo-local noreply identity. Every message ends with a blank line and `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never push. Never commit `startapp.cmd` or untracked `docs/` CNA notes.
- Green bar per task: `dotnet build Quartermaster.slnx -c Release` with 0 new warnings, and the task's test projects green. T6 runs the full `dotnet test Quartermaster.slnx -c Release` (flaky tests listed in RESUME may be re-run once).
- Tasks run one after another in one worktree (`.worktrees/s5c`, branch `port-stage5c`).
- Shared files:
  - `Shell/Dialogs.cs` (`IDialogs`, `Dialogs`), `Program.QuietDialogs` and the test `FakeDialogs` change only in T2.
  - `MainWindow*.cs` and `MainWindow.axaml` change only in T4 (T5 may add a test hook).
  - `docs/port/*`, `RESUME.md`, `build/port-screenshots.ps1` and `Shell/ScreenshotStates.cs` change only in T6.

## Review Focus

1. **The relay answers with a self-signed certificate nobody has trusted yet.** The trust question shows over the lobby window (or over the game window on a guest's reconnect) and the network thread waits for it without freezing the UI. "No" fails the connect with a message, and nothing hangs. Pinned in T3 (`Unknown_certificate_asks_once_and_no_fails_cleanly`).
2. **The lobby window is closed while a join is in flight.** The close is refused until the join ends. If the window does go away, the joined client is disposed and never leaks into a game window. Pinned in T3 (`Close_during_join_waits_then_disposes_client`).
3. **The host leaves, or the relay drops, while a guest has a formula window open or the side choice up.** The guest gets the banner (`GuestSession.HostGoneText` / `RelayLostText`), no crash, and Apply stays off while there is no answer. When the guest is refused, it is told why and goes back to the launcher. Pinned in T5 (`Host_leaves_guest_sees_banner_then_returns_to_launcher`).
4. **A guest's hidden information.** A guest holding Blue never sees Red's private values in the ledger, the formula window inputs, the log or the map, before or after a snapshot. Pinned in T5 (`Guest_sees_only_own_side`).
5. **Hosting fails after hot-seat was running** (bad address, refused room, closed relay). The game continues locally with its saved players and the hot-seat controller back, and the message says why. Pinned in T4 (`Hosting_failure_keeps_local_game_and_hot_seat`).

---

### Task 1: Render models for seats and side choice

**Files:**
- Create: `src/Quartermaster.Render/Online/SeatRows.cs`, `src/Quartermaster.Render/Online/SideChoiceModel.cs`
- Optional modify (pure refactor): `src/Quartermaster.App/SeatsDialog.cs`
- Test: `tests/Quartermaster.Render.Tests/SeatRowsTests.cs`, `tests/Quartermaster.Render.Tests/SideChoiceModelTests.cs`

**Interfaces:**
- Consumes: `Seats` (`All`, `FreeSides(RulesPack?)`), `Seat` (`PlayerId`, `Name`, `Sides`, `Connected`, `IsHost`), `SideDef`.
- Produces:
  - `public sealed record SeatRow(string Player, string Sides, string Status, bool Greyed, Seat? Seat)`.
  - `public static class SeatRows`:
    - `IReadOnlyList<SeatRow> For(Seats seats, RulesPack? pack)`: status `"host (you)"` / `"left"` / `"choosing a side"` / `"connected"`, sides `"—"` when none, names from the pack (id when the name is empty), and a last row `("(open seat)", free names, "free", true, null)` when any side is free.
    - `bool CanAssign(Seat? s)` (`s != null`) and `bool CanFree(Seat? s)` (`s is { IsHost: false } && (!s.Connected || s.Sides.Count == 0)`).
    - `IReadOnlyList<SideDef> Offered(Seats seats, RulesPack pack, Seat seat)` (free sides plus the seat's own, in pack order).
    - Texts: `const string Note = "Free sides are open seats in the lobby. A seat can be freed when its player has left or holds no side."`, `static string FreeQuestion(string name) => $"Free {name}'s seat? Their sides become open seats again."`, `const string CannotFreeText = "Only a seat whose player has left, or that holds no side yet, can be freed."`, `static string AssignTitle(string name) => $"Sides for {name}"`, `const string AssignPrompt = "Sides this player holds:"`.
  - `public static class SideChoiceModel`: `IReadOnlyList<string> InitialTicks(IReadOnlyList<SideDef> sides, IReadOnlyCollection<string>? ticked)` (the one side when there is exactly one and `ticked` is null; else `ticked`); `const string TickOneText = "Tick at least one side."`; `const string ChooseTitle = "Choose your side"`, `const string ChoosePrompt = "Play these sides:"`; `const string NoFreeSideText = "No side is free in this game any more. You can chat; the host can give you a side."`; `static string Label(SideDef s)` (name, or id when empty).

- [ ] **Step 1: Write the failing tests**

`SeatRowsTests`: `Rows_show_status_and_open_seat` (host Red, guest Blue connected, guest "Cleo" with no side, pack Red/Blue/Green → rows `("…", "Red", "host (you)")`, `("…", "Blue", "connected")`, `("Cleo", "—", "choosing a side")`, `("(open seat)", "Green", "free")`); `Left_seat_is_greyed_and_freeable`; `Host_seat_never_freeable`; `Offered_is_free_plus_own_in_pack_order`; `Unnamed_side_shows_id`.
`SideChoiceModelTests`: `Single_side_preticked_when_nothing_ticked`; `Ticked_kept_when_given`; `Two_sides_none_preticked`.

- [ ] **Step 2: Run them, expect compile failure.** `dotnet test tests/Quartermaster.Render.Tests -c Release --filter "FullyQualifiedName~SeatRows|FullyQualifiedName~SideChoiceModel"`
- [ ] **Step 3: Implement `SeatRows` and `SideChoiceModel`** as the Interfaces block, bodies lifted from `SeatsDialog.Fill/UpdateButtons/Assign` and `SideChoiceDialog`. Optionally switch WinForms `SeatsDialog`/`SideChoiceDialog` to them (pure refactor; then `dotnet build` the App and keep its texts identical).
- [ ] **Step 4: Run the tests, expect PASS.**
- [ ] **Step 5: Commit** `feat(render): seat rows and side choice models for the online dialogs`

---

### Task 2: Desktop online dialogs

**Files:**
- Create: `src/Quartermaster.Desktop/Online/SideChoiceDialog.cs`, `Online/HostSidesPanel.cs`, `Online/JoinDialog.cs`, `Online/HostSavedDialog.cs`, `Online/SeatsDialog.cs`, `Online/RelayTrust.cs` (namespace `Quartermaster.Desktop.Online`)
- Modify: `src/Quartermaster.Desktop/Shell/Dialogs.cs` (`IDialogs` + `Dialogs`), `Program.cs` (`QuietDialogs`), `tests/Quartermaster.Desktop.Tests/MainWindowTests.cs` (`FakeDialogs`)
- Test: `tests/Quartermaster.Desktop.Tests/OnlineDialogsTests.cs`

**Interfaces:**
- Consumes: T1 `SeatRows`, `SideChoiceModel`; `OnlineLobbyModel` (`JoinNote`, `OpenLobbyWarning`, `NeedsPackText`, `ValidateHostSides`, `CertificatePrompt`); `HostSession` (`NoNameText`, `AssignSides`, `FreeSeat`, `SeatsChanged`); `ChatText.CleanName/Clean/MaxName`; `NewGameRules.MaxLobbyName`; `LobbyRow`.
- Produces:
  - `public sealed record JoinChoice(string PlayerName, string Password)`; `public sealed record HostSavedChoice(string LobbyName, string Password, string PlayerName, IReadOnlyList<string> HostSides)`.
  - New `IDialogs` members (real ones show the dialog with `ShowDialogTracked`; `QuietDialogs` returns null/no-op):
    - `Task<IReadOnlyList<string>?> ChooseSidesAsync(Window owner, string title, string prompt, IReadOnlyList<SideDef> sides, IReadOnlyCollection<string>? ticked = null, string? note = null)`
    - `Task<JoinChoice?> AskJoinAsync(Window owner, LobbyRow row, string playerName)`
    - `Task<HostSavedChoice?> AskHostSavedAsync(Window owner, RulesPack pack, string playerName, string lobbyName, IReadOnlyCollection<string> hostSides)`
    - `Task ShowSeatsAsync(Window owner, HostSession host, RulesPack? pack)`
  - `public sealed class HostSidesPanel : UserControl` with `HostSidesPanel(RulesPack? pack, string playerName, IReadOnlyCollection<string>? preselect = null)`, `string PlayerName`, `IReadOnlyList<string> HostSides`, `string? Problem`, `PlayerDef ToPlayer()` (as WinForms; Name `"HostSidesPanel"`).
  - `public static class RelayTrust`: `static PinStore Pins()` (`AppDataRoot.Path/relay-pins.json`); `static bool Ask(Window owner, IDialogs dialogs, CertificateCheck c)` (the ruling above).
- Texts and layout: verbatim from WinForms `JoinDialog` (title `"Join " + row.Lobby`, `"Hosted by {Host} · free sides: {FreeSides}"`, `"Your name:"`, `"Password:"` only when `row.Locked`, note `JoinNote + " You see only your sides' view; the host's rules pack is sent to you."`, buttons Join/Cancel, empty name → `HostSession.NoNameText` and stays open), `HostSavedDialog` (title `"Host a saved game"`, `"Lobby name:"`, `"Password:"`, open-lobby warning visible while the password is empty, group `"You and the open seats"`, Host/Cancel; empty lobby → `"Give the lobby a name."`, else `HostSidesPanel.Problem`), `HostSidesPanel` (`"Your name:"`, `"Sides you hold (every other side is an open seat):"`, `NeedsPackText` in firebrick under fewer than two sides), `SeatsDialog` (title `"Players"`, columns Player/Sides/Status, `&Assign sides…`, `&Free seat`, Close, note `SeatRows.Note`; follows `SeatsChanged` while open and unsubscribes on close; double-click = Assign), `SideChoiceDialog` (OK with nothing ticked → `SideChoiceModel.TickOneText`, stays open).

- [ ] **Step 1: Write the failing tests** (`OnlineDialogsTests`, `[AvaloniaFact]`, windows built but never shown visibly):
  - `SideChoice_single_side_preticked_and_ok_requires_a_tick`
  - `Join_dialog_hides_password_for_open_lobby_and_refuses_empty_name`
  - `HostSaved_warning_follows_password_and_problem_blocks_ok` (type a password via `TextEdits.OnTextSet` → warning hidden in the same tick; empty lobby → `"Give the lobby a name."`; host ticks every side → `OnlineLobbyModel.NoOpenSideText`)
  - `HostSidesPanel_shows_needs_pack_with_one_side`
  - `Seats_dialog_follows_seats_changed_and_unsubscribes_on_close` (a `HostSession` over a fake `ITransport` as `HostSessionTests` builds one)
  - `Relay_trust_on_ui_thread_returns_false`
- [ ] **Step 2: Run them, expect compile failure.** `dotnet test tests/Quartermaster.Desktop.Tests -c Release --filter "FullyQualifiedName~OnlineDialogs"`
- [ ] **Step 3: Implement the dialogs, `RelayTrust` and the `IDialogs` members** (FakeDialogs: scripted answers in public fields `SidesAnswer`, `JoinAnswer`, `HostSavedAnswer`, plus a `SeatsShown` counter).
- [ ] **Step 4: Run the tests, expect PASS.** Also run the whole `Quartermaster.Desktop.Tests` (the `IDialogs` change touches every fake).
- [ ] **Step 5: Commit** `feat(desktop): online dialogs - join, host saved, seats, side choice`

---

### Task 3: Online lobby window, launcher button and Create lobby

**Files:**
- Create: `src/Quartermaster.Desktop/Launcher/OnlineLobbyWindow.cs`
- Modify: `src/Quartermaster.Desktop/Launcher/LauncherWindow.cs` (Online lobby ▶ enabled as WinForms `LauncherForm.cs:360-395`, tooltip `"Select a module first."` / the hint / `"Join or host a live online game of this module."`, still-being-checked message), `src/Quartermaster.Desktop/Shell/SetupChooser.axaml(.cs)` (Online mode)
- Modify: `tests/Quartermaster.Desktop.Tests/Quartermaster.Desktop.Tests.csproj` (add `ProjectReference` to `..\..\src\Quartermaster.Relay\Quartermaster.Relay.csproj`)
- Create: `tests/Quartermaster.Desktop.Tests/InProcessRelay.cs` (starts `RelayApp.Build` on a free loopback port with a temp `Relay:DataDir`, exposes `Uri Uri`, `IAsyncDisposable`; copied in spirit from `Piece6bEndToEndTests.InitializeAsync`)
- Test: `tests/Quartermaster.Desktop.Tests/OnlineLobbyWindowTests.cs`

**Interfaces:**
- Consumes: T2 dialogs and `RelayTrust`; `OnlineLobbyModel` (`Title`, `ParseRelay`, `Rows`, `HostPort`, `HostedCodes`, `CheckSave`, `SuggestedHostSides`, `ValidateHostSides`, `CleanText`); `ModuleLibrary.RulesPackFor`; `RulesPackIo.Load`; `SettingsStore` (`RelayAddress`, `PlayerName`, `LastDifficulty`).
- Produces:
  - `public sealed class OnlineLobbyWindow : Window` with `OnlineLobbyWindow(ModuleRow row, ModuleLibrary library, SettingsStore store, IDialogs dialogs)`, `Task<LaunchRequest?> Result` (null when closed without a choice), `Task RefreshAsync(bool manual)`, `Task<LaunchRequest?> JoinAsync(LobbyListing listing, string playerName, string password)`, `internal IReadOnlyList<LobbyRow> Rows`, `internal string StateText`, `internal Button JoinButton/CreateButton/HostSavedButton/RefreshButton`, `internal TextBox RelayBox`.
  - Behaviour as WinForms `OnlineLobbyForm`: 640×380 (min 520×300), columns Lobby/Host/Free sides/Players with a lock glyph for locked rows and greyed rows with the `CannotJoin` tooltip, a 10 s auto refresh only after a good refresh and only while visible and not busy, Enter in the relay box refreshes, the state texts (`"Enter the relay address and press Refresh."`, `"Connecting…"`, `"Connected to {host:port} — {n} lobby/lobbies"`, `"This module is still being checked; try again in a moment."`, `"Joining…"`, `"Joined " + name`), the relay address saved on a good refresh, the close refused while joining, the browser client disposed on close.
  - Create lobby…: opens the module (`Task.Run(ModuleSession.Open)`), shows the SetupChooser in Online mode, and returns `LaunchRequest(path, packPath, LaunchKind.HostOnline, req.WithoutPassword() with { Players = null }, null, new OnlineLaunch(uri, host.Name, req.Password ?? "", req.LobbyName, host.Sides))`. Host a saved game…: file picker (`*.qsave`), `CheckSave`, `AskHostSavedAsync`, then `LaunchRequest(path, packPath, LaunchKind.HostOnline, null, save, new OnlineLaunch(uri, name, password, lobby, sides))`.
  - SetupChooser: `SetupChooser(ModuleModel? model, Difficulty d, bool preselectNew, RulesPack? pack, IDialogs? dialogs, GameMode mode, string? hostName = null, string? lobbyName = null)`. In Online mode: title `"Create lobby"`, a lobby panel (`"Lobby name:"`, `"Password:"`, the open-lobby warning) on the game page, and a `HostSidesPanel` instead of the `PlayersEditor` on the second page. The result is `NewGameRequest(GameMode.Online, setup, difficulty, [host player], lobbyName, password)`. New `IDialogs.ChooseOnlineSetupAsync(Window owner, ModuleModel model, Difficulty d, RulesPack pack, string hostName, string lobbyName) → Task<NewGameRequest?>` (added to `Dialogs`, `QuietDialogs`, `FakeDialogs` in this task. This is the one exception to the T2-only rule for `Dialogs.cs`).

- [ ] **Step 1: Write the failing tests** (`OnlineLobbyWindowTests`, real in-process relay, pins pre-trusted in a temp `PinStore` unless the test is about trust):
  - `Refresh_lists_this_modules_lobbies_and_saves_address` (a `HostSession`-less room created through a raw `RelayClient.CreateRoomAsync` with a `OnlineLobbyModel.Draft` for the fixture module's sha; another module's room is not listed)
  - `Bad_address_shows_message_and_does_not_connect` (`"nope:xx"` → `OnlineLobbyModel.BadAddressText` in `StateText`)
  - `Join_returns_request_with_open_client` (`Kind == JoinOnline`, `Online.Client` connected, `Online.Code` normalised, `PlayerName` saved to settings)
  - `Wrong_password_join_fails_and_disposes_client`
  - `Unknown_certificate_asks_once_and_no_fails_cleanly` (FakeDialogs answers No to the yes/no: one question asked, state shows the failure, no lingering client, UI thread not blocked: the test's own `await` finishes within 10 s)
  - `Close_during_join_waits_then_disposes_client`
  - `Create_lobby_returns_host_request_without_password_in_new_game` (FakeDialogs `ChooseOnlineSetupAsync` answer: `req.NewGame.Password == null`, `req.Online.Password == "pw"`, `HostSides == ["red"]`)
  - `Create_lobby_needs_pack_with_two_sides` (one-side pack → `NeedsPackText` warning, no request)
  - `Launcher_online_button_opens_lobby_and_completes_result`
  - `Setup_chooser_online_mode_shows_lobby_panel_and_host_sides`
- [ ] **Step 2: Run them, expect failure.** `dotnet test tests/Quartermaster.Desktop.Tests -c Release --filter "FullyQualifiedName~OnlineLobbyWindow"`
- [ ] **Step 3: Implement** `InProcessRelay`, `OnlineLobbyWindow`, the launcher button and the SetupChooser Online mode. Every `ConnectAsync` goes through `Task.Run` with `c => RelayTrust.Ask(this, _dialogs, c)` (the ruling above).
- [ ] **Step 4: Run them, expect PASS**, plus the existing `LauncherWindowTests` and `SetupChooserTests`.
- [ ] **Step 5: Commit** `feat(desktop): online lobby window, launcher Online lobby button, Create lobby setup`

---

### Task 4: The game window online (host and guest)

**Files:**
- Create: `src/Quartermaster.Desktop/MainWindow.Online.cs` (port of WinForms `MainForm.Online.cs`)
- Modify: `MainWindow.HotSeat.cs` (remove the `IsOnline => false` placeholder; `SetGamePlayers` refusal unchanged), `MainWindow.axaml` (an `OnlineBanner` `Border` + `TextBlock` docked top after `AlertBarHost`, background `#FFF0C8`, bold, hidden by default; a `SbOnlineBox` `Border` with `SbOnline` `TextBlock` in the status bar between `SbTiling` and `SbPlayerBox`), `MainWindow.axaml.cs` (keep a `_localActions` field next to `_actions`; `ApplyGates` uses `CommandAvailability.For(_session, IsOnline || _onlineStarting)`; a `&Game` top menu built by `BuildGameMenu()`; `CloseSession` calls `EndOnline(notifyGuests: true)` first; the view-changed handler calls `UpdateOnlineUi()` while online), `MainWindow.Launch.cs` (the `HostOnline` and `JoinOnline` cases as WinForms `MainForm.cs:515-534`; a failed open disposes `r.Online?.Client`)
- Test: `tests/Quartermaster.Desktop.Tests/MainWindowOnlineTests.cs`

**Interfaces:**
- Consumes: T2 `IDialogs.ChooseSidesAsync/ShowSeatsAsync`, `RelayTrust`; T3 `InProcessRelay`; Render `HostSession`, `GuestSession`, `IntentExecutor(s, phases)`, `RemoteActions`, `RelayTransport`, `Secrets.AuthVerifier/GameKey`, `OnlineLobbyModel.Draft/MarkHosted/UnmarkHosted/StatusText/GuestBanner/ValidateHostSides`, `ModuleSession.ViewerBanner`; 5b `CreateHotSeat` / `DestroyHotSeat(restoreWindows)`.
- Produces (used by T5 and T6):
  - `bool IsOnline => _host != null || _guest != null;`
  - `public HostSession? HostOnline`, `public GuestSession? GuestOnline`, `internal string OnlineStatusText` ("" when hidden), `internal string OnlineBannerText` ("" when hidden), `internal string? OnlineError`, `internal MenuItem? GamePlayersItem`, `internal MenuItem? GameLeaveItem`.
  - `internal Task LeaveOnlineGameAsync()` (Game > Leave Online Game), `internal Task ShowPlayersDialogAsync()` (Game > Players…), `internal Task AskSideAsync()`.
- Behaviour, all as WinForms `MainForm.Online.cs` (same texts, same order of steps):
  - Hosting: `ValidateHostSides`, stop hot-seat (`DestroyHotSeat(restoreWindows: true)`), connect via `Task.Run`, reserve, create the room, clear the save's players only after the room exists, start `HostSession` with `PostToUi`, status `"Your lobby is open: {name} (code {code})."`. On failure: `OnlineFailed` → `EndOnline`, `RestartHotSeat`, and the message `"The game could not go online:\n{why}\n\nIt continues here as a local game."`.
  - Joining: take the lobby's client, set `o.Trust.Ask` to `RelayTrust.Ask(this, …)`, build `GuestSession` + `RemoteActions`, swap `_actions.Inner`. On failure: the message `"Could not join the online game:\n{why}"`, then back to the launcher (`BackToLauncherAsync`, posted, never inside the open).
  - Guest states: `ChoosingSide` asks the side after 400 ms (`DispatcherTimer`), once, with `SideChoiceModel.ChooseTitle/ChoosePrompt` and `OnlineLobbyModel.JoinNote` (or `NoFreeSideText`). `Paused` with `NoAnswer` asks `GuestSession.NoAnswerText + ".\n\nLeave the online game and go back to the launcher?"`. `Refused` shows `StatusText ?? "The host removed you from the game."` and goes back to the launcher.
  - Game menu: `&Players…` (enabled for the host, or for a guest still choosing; otherwise the tooltip `HostSession.SidesFixedText`) and `&Leave Online Game`, both visible only while online or starting. Leave asks `"Leave the online game and go back to the launcher?"` (guest) or `"Leave the online game? The lobby closes and the other players are disconnected. The game continues here as a local game."` (host), caption `"Leave Online Game"`. The host then sees the status `"You left the online game; it continues here as a local game."`.
  - Status `OnlineLobbyModel.StatusText(...)` with the WinForms trouble words, the banner (`RelayLostText` for a host without the relay, `GuestBanner` for a guest, else `ViewerBanner`), chat set or cleared on `_log.SetChat`, host chat errors cleaned to the status bar, and `ApplyGates` after every refresh.
  - `EndOnline` is idempotent (the `_leavingOnline` guard), unhooks every event, restores `_localActions`, and disposes the relay client after 400 ms when the host told guests, else at once. It never throws.

- [ ] **Step 1: Write the failing tests** (`MainWindowOnlineTests`, `[AvaloniaFact]`, in-process relay, fixture module with the invented Red/Blue/Green pack, temp layout root):
  - `Host_from_launch_request_opens_lobby_and_shows_status` (`OnlineStatusText` starts `"Online: "` and has the code; Game menu items visible; chat box present)
  - `Hosting_failure_keeps_local_game_and_hot_seat` (a closed port; a game saved with two hot-seat players → after the failure `HotSeat != null`, `Players.Count == 2`, `OnlineError` set, no chat box)
  - `Guest_joins_chooses_side_and_plays_through_host` (second `MainWindow` from the T3 `JoinAsync` request; FakeDialogs `SidesAnswer = ["blue"]`; the guest's `Actions.IsRemote`; a ledger edit by the guest reaches the host's game)
  - `Chat_line_reaches_both_logs`
  - `Host_players_dialog_and_guest_tooltip` (host: `ShowSeatsAsync` called; seated guest: Players… disabled with `SidesFixedText`)
  - `Host_leave_returns_to_local_with_hot_seat_restored`
  - `Guest_cannot_save_undo_or_edit_players` (gates: `file.saveGame` off with its reason; `HistoryCommands.CanUndo` false; Settings players refusal shows `IntentExecutor.HostOnlyText`)
  - `Closing_window_ends_online_without_throwing`
- [ ] **Step 2: Run them, expect failure.** `dotnet test tests/Quartermaster.Desktop.Tests -c Release --filter "FullyQualifiedName~MainWindowOnline"`
- [ ] **Step 3: Implement** `MainWindow.Online.cs` and the listed modifications.
- [ ] **Step 4: Run them, expect PASS**, plus the whole `Quartermaster.Desktop.Tests` (hot-seat and launcher tests must stay green).
- [ ] **Step 5: Commit** `feat(desktop): host and join online games in the game window`

---

### Task 5: Read-only guest Workbench and the end-to-end online test

**Files:**
- Modify: `src/Quartermaster.Desktop/Workbench/WorkbenchWindow.cs` (a read-only strip and title, as WinForms `WorkbenchWindow.cs:175-198`)
- Test: `tests/Quartermaster.Desktop.Tests/WorkbenchGuestTests.cs`, `tests/Quartermaster.Desktop.Tests/OnlineEndToEndTests.cs`

**Interfaces:**
- Consumes: T4 `HostOnline`, `GuestOnline`, `OnlineBannerText`, `LeaveOnlineGameAsync`; `ModuleSession.IsGuest`.
- Produces: `WorkbenchWindow.ApplyReadOnly()` (private), `internal bool ReadOnlyShown`. The strip text is `"Read-only: the host's rules pack for this online game. Only the host can change it."` (background `#FFF0C8`). The title is `"Rules Workbench — {pack name} (read-only)"`. Every tab's editors are disabled (`IsEnabled = false` on each tab's content), and the "no pack" prompt is hidden for a guest. It re-applies whenever the session's rules change (a snapshot can replace the pack).

- [ ] **Step 1: Write the failing tests**
  - `WorkbenchGuestTests.Guest_workbench_is_read_only` (session marked guest as the existing guest-flag tests do: strip shown, title ends `" (read-only)"`, a tab's editor `IsEnabled == false`, the rule-ref search action unavailable)
  - `WorkbenchGuestTests.Host_workbench_is_editable`
  - `OnlineEndToEndTests` (one in-process relay, a host window holding Green with Red private, two guest windows):
    - `Guest_sees_only_own_side` (guest Blue: the ledger rows, a formula window's masked inputs (`FormulaMasking`), the log lines and the map pieces show nothing private of Red; Red's private value `31313` appears nowhere in the guest's ledger texts)
    - `Guest_formula_apply_goes_through_host`
    - `Guest_drops_and_rejoins_same_seat` (dispose the guest's relay client; a new `JoinAsync` with the same name and the same temp layout root → `GuestOnline.Sides` still `{blue}` with no side question)
    - `Host_leaves_guest_sees_banner_then_returns_to_launcher` (host `LeaveOnlineGameAsync` with FakeDialogs OK → guest refused: info shown, then the guest window closes with `ReturnToLauncher == true`)
    - `Relay_stops_guest_shows_relay_lost_banner_without_crash`
- [ ] **Step 2: Run them, expect failure.** `dotnet test tests/Quartermaster.Desktop.Tests -c Release --filter "FullyQualifiedName~WorkbenchGuest|FullyQualifiedName~OnlineEndToEnd"`
- [ ] **Step 3: Implement the read-only Workbench.** Fix in the Desktop windows anything the end-to-end tests show leaking or crashing. Each fix is named in the commit body.
- [ ] **Step 4: Run them, expect PASS**, three runs in a row (network timing), plus the whole `Quartermaster.Desktop.Tests`.
- [ ] **Step 5: Commit** `feat(desktop): read-only guest workbench; online end-to-end tests`

---

### Task 6: Screenshots, parity, Mac checklist, RESUME

**Files:**
- Modify: `src/Quartermaster.Desktop/Shell/ScreenshotStates.cs` (states `online-lobby`, `online-host`, `online-guest`, `seats`, `side-choice`, off-screen as every state, **without any network**: the Desktop app must not reference `Quartermaster.Relay`. The lobby shows invented `LobbyListing` rows through an `internal void ShowRowsForScreenshot(IReadOnlyList<LobbyRow>)`. The host, seats and side-choice states use a `HostSession` over a private `NullTransport : ITransport` in `ScreenshotStates`. The guest state sets the banner and status through T4's `UpdateOnlineUi` with a `GuestSession` over the same null transport, left in `Joining`), `build/port-screenshots.ps1`, `docs/port/parity.md` (tick the 5c rows for Windows: Online lobby, Join, Host saved, sides panel, Seats, SideChoice, ChatBox online, guest read-only Workbench, Leave Online Game, rejoin; leave the notice chip, Send Turn and Export Viewer Copy rows open for 5d with that reason), `docs/port/stage5c-mac-checklist.md` (new: relay certificate prompt, join from a second machine against a WinForms host, chat, leave, rejoin after Wi-Fi off/on, Cmd+Q while online), `docs/superpowers/RESUME.md` (CURRENT STATE for 5c)
- Test: the screenshot run itself and the full suite

- [ ] **Step 1: Add the screenshot states.** Run `build/port-screenshots.ps1 -Only online-lobby,online-host,online-guest,seats,side-choice` (off-screen). Expected: five PNGs, exit 0, and no network use.
- [ ] **Step 2: Update parity, the Mac checklist and RESUME.** RESUME names the plan, the ledger `.superpowers/sdd/2026-11-13-port-stage5c-online/`, the rulings above and what 5d inherits (notice chip, Send Turn, Export Viewer Copy, mailbox watch, closing the Workbench before a received turn replays).
- [ ] **Step 3: Run the full suite.** `dotnet test Quartermaster.slnx -c Release`. Expected: 0 failed (known-flaky tests from RESUME may be re-run once, and are named if re-run).
- [ ] **Step 4: Commit** `docs(port): stage 5c parity, Mac checklist, screenshots, RESUME`
