# Cross-platform Port — Stage 5d (turn files) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Avalonia app plays by turn files as the WinForms app does:
- Game > Send Turn… writes a `.qturn` and can also send it through the relay mailbox.
- File > Open Turn File… and the launcher's Open turn file… replay a received turn with the Replay bar.
- A "New turn waiting" notice chip opens a mailbox turn.
- A guest can File > Export Viewer Copy….
- The mailbox password lives in the OS secret store (DPAPI on Windows as today, Keychain on macOS, Secret Service on Linux; asked once per session where none exists).

**Architecture:**
- The turn logic is already shared and is used unchanged: Render `Turns/` (`TurnUiModel`, `TurnSender`, `TurnOpener`, `TurnReplayModel`, `TurnIdentity`, `ReceivedTurns`, `MailboxService`), `ModuleSession` (`OpenTurn`, `BeginTurnReplay`, `EndTurnReplay`, `RestoreAfterFailedReplay`, `ExportViewerCopy`, `SetViewSides`), `HistoryController.IsTurnGame`, Core `TurnFile`.
- What WinForms keeps in forms moves to Render first (T1): the mailbox and send/who-are-you validation, the open-turn decision.
- The secret store is a new `IPasswordProtector` per OS (T2). It keeps the identity JSON schema: off Windows the protected bytes are an opaque handle to an OS-store entry.
- Desktop gets the dialogs and the Replay bar (T3), then `MainWindow.Turns.cs`, a port of WinForms `MainForm.Turns.cs`, in two halves: the turn game and sending (T4), then opening, replay and the mailbox watch (T5). T6 adds the two-window end-to-end test, docs and screenshots.

**Tech Stack:** C# .NET 10, Avalonia 12.1.5, Avalonia.Headless.XUnit, xUnit. No new NuGet packages (P/Invoke only: Security.framework, libsecret-1 / libglib-2.0).

**Spec:** `docs/superpowers/specs/2026-11-11-port-stage5-design.md` (sub-stage 5d; "Mailbox password" under Already decided; "Secret store (5d)" under Design calls). Behaviour reference: `src/Quartermaster.App/MainForm.Turns.cs` (whole file, incl. nested `SendWaitForm`), `SendTurnDialog.cs`, `WhoAreYouDialog.cs` (with `MailboxValues`, `MailboxOptions`), `ReplayBar.cs`, `PlayOfflineForm.cs:162` (OpenTurnDialog), `MainForm.cs:219-223, 350, 524-532, 626-630, 683, 951`, `MainForm.Online.cs:55-56, 447, 488, 500-520`, `MainForm.Phases.cs:75`.

## Scope rulings (made at planning)

- **The secret store keeps the identity format.** `TurnIdentity` stores `ProtectedPassword` bytes from an `IPasswordProtector`.
  - On Windows the protector stays `DpapiProtector`, so existing `turns/{GameId}.json` files and the WinForms app are untouched.
  - On macOS and Linux a store protector writes the password to the OS store under service `"Quartermaster turn mailbox"` and a random account id. It returns the bytes `"qm-secret:v1:" + accountId` (UTF-8). `Unprotect` looks that account up.
  - A DPAPI blob read off Windows, or a handle the store no longer has, yields no password (null). The spec's "a password stays on the machine it was set on" holds.
- **No secret store** (no libsecret, no D-Bus session, keychain refused): a session protector keeps the password in memory for the app's life. `SetPassword` then returns false, and the WinForms status text shows ("…it is kept until the window closes.").
- **Ask once per session.** When an identity has protected bytes but no readable password, Desktop asks once per game per app session with a small password prompt, keeps the answer in memory, and stores it again (it may now succeed). Cancel means "", as WinForms `PasswordFor` falls back.
- **Real OS stores are tested opt-in.** Tests marked `[SecretStoreFact]` run only when `QUARTERMASTER_SECRET_STORE_TESTS=1` (so CI and other machines never touch a real keychain). The Mac and Linux checklists run them by hand. Everything else uses fake protectors.
- **TaskDialog equivalents** (Retry/Close, Open anyway/Cancel) use one new `IDialogs.ChooseAsync(owner, message, caption, buttons, defaultIndex) → int` (-1 on close). Texts as WinForms.
- **Export Viewer Copy…** is in 5d, as the spec's row says: File menu, guest only, `ModuleSession.ExportViewerCopy`.
- **No `--smoke-turns` port.** Headless tests with the in-process relay cover it; screenshots come from `--screenshot-state` (T6).

## Global Constraints

- Labels, titles, tooltips and message texts exactly as the named WinForms file (Render constants where they exist: `TurnUiModel.*`, `MailboxService.*Text`, `TurnOpener.*Text`, `TurnReplayModel.*Text`, `ModuleSession.*Text`). Classic grey look. Cmd on macOS through `Input/ShortcutGestures`.
- Formats unchanged: `.qturn`, save 7, rules pack 4, `settings.json`, `turns/{GameId}.json` (same fields; off-Windows `ProtectedPassword` holds a store handle). Relay mailbox protocol unchanged. A turn sent by WinForms opens in Desktop and the reverse.
- Hidden information: during a replay the view is public-only; after it, the local player's sides (`TurnUiModel.ViewSides`). The receiver cannot undo the sender's turn (`HistoryController.IsTurnGame`).
- Every mailbox event reaches the window through the 5c `PostToUi` pattern (Dispatcher.UIThread, guarded, dropped once closed). Relay text is shown only after `OnlineLobbyModel.CleanText(…, GuestSession.MaxHostText)`. Every `RelayClient.ConnectAsync` runs off the UI thread, and certificate questions use `Online/RelayTrust.Ask`.
- Tests are headless, never open a visible window, use temp roots (`TestDataRoot`, `TurnIdentity.Load(gameId, root, protector)`), use only the in-process loopback relay (`InProcessRelay`) and never touch the real relay, GitHub, the profile or a real OS secret store (except opt-in `[SecretStoreFact]`).
- Avalonia `TextChanged` is dispatcher-posted: same-tick text-following uses `Shell/TextEdits.OnTextSet`.
- Invented TEST content only (players Alice/Bob, sides Red/Blue). Public repo: no personal paths.
- Commits: repo-local noreply identity; each message ends with a blank line and `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never push. Explicit `git add` paths only.
- Green bar per task: `dotnet build Quartermaster.slnx -c Release` with 0 new warnings, and the task's test projects green. Network tests pass 3 runs in a row. T6 runs the full suite (known-flaky tests in RESUME may be re-run once, named).
- Tasks run one after another in one worktree (`.worktrees/s5d`, branch `port-stage5d`).
- Shared files:
  - `Shell/Dialogs.cs` (`IDialogs`/`Dialogs`), `Program.QuietDialogs` and the test `FakeDialogs` change only in T3.
  - `MainWindow*.cs`, `MainWindow.axaml` and `Launcher/PlayOfflineWindow.cs` change only in T4 and T5 (T6 may add internal screenshot hooks).
  - `docs/port/*`, `RESUME.md`, `build/port-screenshots.ps1` and `Shell/ScreenshotStates.cs` change only in T6.

## Review Focus

1. **The window is closed, or Back to launcher is chosen, while a mailbox send is still in flight.** The user is asked "The turn is still being sent through the relay. Leave anyway?" (default No). No answers Leave anyway → the send is dropped, the file stays, and nothing throws. Pinned in T4 (`Close_during_mailbox_send_asks_and_no_keeps_window`).
2. **A received turn arrives while the Workbench has unsaved edits, or while a formula window or the handover screen is up.** The Workbench closes first (its unsaved-rules question comes before), and the replay runs only after that. Formula windows of the old view close, and hot-seat stops for the turn game. Pinned in T5 (`Open_turn_closes_workbench_and_stops_hot_seat`).
3. **A replay fails halfway (a step throws, or the end does not match the sender).** The previous game, save path, dirty flag and hot-seat current player come back. The notice is raised again for a mailbox item, and the mailbox item is not deleted. Pinned in T5 (`Failed_replay_restores_game_and_keeps_mailbox_item`).
4. **The OS secret store is missing or refuses** (Linux without libsecret, a locked keychain). Sending still works, the status says the password is kept until the window closes, and the next app session asks once. Pinned in T2 (`Missing_store_falls_back_to_session`) and T4 (`Unreadable_password_asked_once_per_session`).
5. **A turn file for another module, an older position, or one already played.** Each gives its WinForms message or the Open anyway / Cancel choice. Cancel leaves the game untouched; a launcher open that is cancelled goes back to the launcher, never an empty window. Pinned in T5 (`Open_turn_checks_and_cancel_keeps_game`, `Launcher_open_turn_cancel_returns_to_launcher`).

---

### Task 1: Render models for the turn dialogs and the open decision

**Files:**
- Create: `src/Quartermaster.Render/Turns/TurnDialogRules.cs`, `src/Quartermaster.Render/Turns/TurnOpenDecision.cs`
- Optional modify (pure refactor): `src/Quartermaster.App/WhoAreYouDialog.cs`, `SendTurnDialog.cs`, `MainForm.Turns.cs` (forward to the new models, texts identical)
- Test: `tests/Quartermaster.Render.Tests/TurnDialogRulesTests.cs`, `tests/Quartermaster.Render.Tests/TurnOpenDecisionTests.cs`

**Interfaces:**
- Consumes: `MailboxService.NoRelayText/NoRecipientText`, `OnlineLobbyModel.ParseRelay`, `HandoverText.SidesText(PlayerDef, RulesPack?)`, `TurnOpener.TurnCheck`, `TurnUiModel.KnowsLocalPlayer`, `PlayerDef`.
- Produces:
  - `public sealed record MailboxValues(bool Use, string? Relay, string? Password)` moved to Render (`ToString`/`PrintMembers` masks the password as `***`). WinForms then uses the Render type.
  - `public static class TurnDialogRules`:
    - `string? MailboxProblem(bool use, string? relay)` (WinForms `MailboxOptions.Problem`).
    - `string? SendProblem(PlayerDef? recipient, bool use, string? relay)` (`NoRecipientText` first).
    - `string? WhoProblem(string? localPlayerId, bool use, string? relay)` (`"Choose which player you are."` first).
    - `string PlayerLabel(PlayerDef p, RulesPack? pack)` (`"{Name} ({sides})"`, Id when Name is blank).
    - `string SendPrompt(string fromName)` → `$"Send {fromName}'s turn. Next you choose where to save the turn file."`.
    - Consts: `SendCaption = "Also send through my relay"`, `WatchCaption = "Watch the turn mailbox on my relay"`, `RelayLabel = "Relay:"`, `PasswordLabel = "Mailbox password (optional, the same for every player):"`, `WhoTitle = "Who are you?"`, `WhoPrompt = "Which player are you? This computer remembers it for this game."`, `static string FromPrompt(string name) => $"This turn is from {name}. Which player are you?"`.
  - `public enum TurnOpenStep { Refuse, AskOpenAnyway, AskWhoAreYou, Replay }` and `public sealed record TurnOpenDecision(TurnOpenStep Step, string? Message)` with `static TurnOpenDecision Decide(TurnCheck check, IReadOnlyList<PlayerDef> players, TurnIdentity? identity, bool openAnywayChosen)`. It maps every check as `MainForm.Turns.cs:403-462` does, including the who-are-you condition `players.Count >= 2 && (check == NewGame || !KnowsLocalPlayer)`.

- [ ] **Step 1: Write the failing tests**
  - `TurnDialogRulesTests`: `Mailbox_off_has_no_problem`, `Blank_relay_is_no_relay_text`, `Bad_relay_returns_parse_error`, `Send_without_recipient_first`, `Who_without_player_first`, `Player_label_uses_sides_and_falls_back_to_id`, `Mailbox_values_mask_password`.
  - `TurnOpenDecisionTests`: one test per `TurnCheck`, plus `Already_played_open_anyway_then_replay` and `New_game_with_two_players_asks_who`.
- [ ] **Step 2: Run them, expect compile failure.** `dotnet test tests/Quartermaster.Render.Tests -c Release --filter "FullyQualifiedName~TurnDialogRules|FullyQualifiedName~TurnOpenDecision"`
- [ ] **Step 3: Implement** as the Interfaces block, bodies lifted from the WinForms files (optional WinForms forwarding; if done, `dotnet build` the App).
- [ ] **Step 4: Run them, expect PASS.**
- [ ] **Step 5: Commit** `feat(render): turn dialog rules and open-turn decision`

---

### Task 2: Mailbox password in the OS secret store

**Files:**
- Create: `src/Quartermaster.Render/Turns/Secrets/SecretStoreProtector.cs`, `Turns/Secrets/ISecretStore.cs`, `Turns/Secrets/MacKeychainStore.cs`, `Turns/Secrets/LinuxSecretServiceStore.cs`, `Turns/Secrets/SessionSecretStore.cs`, `Turns/Secrets/PasswordProtectors.cs`
- Modify: `src/Quartermaster.Render/Quartermaster.Render.csproj` (`InternalsVisibleTo` Desktop.Tests), `tests/Quartermaster.Desktop.Tests/TestDataRoot.cs` (module initializer sets `PasswordProtectors.Override` to an in-memory fake), `src/Quartermaster.Render/Turns/TurnIdentity.cs` (the default protector comes from `PasswordProtectors.ForThisOs()`; `SetPassword` forgets the old store entry; a new `bool HasStoredPassword` = `ProtectedPassword is { Length: > 0 }`)
- Test: `tests/Quartermaster.Render.Tests/SecretStoreTests.cs`

**Interfaces:**
- Consumes: `IPasswordProtector`, `DpapiProtector`, `TurnIdentity`.
- Produces:
  - `public interface ISecretStore { bool TrySet(string account, string secret); string? Get(string account); void Remove(string account); }` (never throws; false/null on any failure).
  - `public sealed class SecretStoreProtector(ISecretStore store) : IPasswordProtector`:
    - `Protect` creates an account `Guid.NewGuid().ToString("N")`, calls `TrySet`, and returns the handle bytes `"qm-secret:v1:"+account`. It throws `CryptographicException` when `TrySet` fails, so `TurnIdentity.SetPassword` returns false as today.
    - `Unprotect` parses the handle (other bytes → `CryptographicException`) and calls `Get` (null → `CryptographicException`).
    - `void Forget(byte[] blob)` removes the account.
  - `IPasswordProtector` gains `void Forget(byte[] blob) {}` as a default interface method (DPAPI: no-op).
  - `MacKeychainStore`: P/Invoke `/System/Library/Frameworks/Security.framework/Security`: `SecKeychainAddGenericPassword`, `SecKeychainFindGenericPassword`, `SecKeychainItemModifyAttributesAndData`, `SecKeychainItemDelete`, `SecKeychainItemFreeContent`, plus `CFRelease` from CoreFoundation. Service `"Quartermaster turn mailbox"`.
  - `LinuxSecretServiceStore`: P/Invoke `libsecret-1.so.0` non-variadic `secret_password_storev_sync` / `secret_password_lookupv_sync` / `secret_password_clearv_sync`, with a `SecretSchema` struct marshalled by hand (name `"org.quartermaster.TurnMailbox"`, one string attribute `"account"`) and a `GHashTable` from `libglib-2.0.so.0` (`g_hash_table_new` with `g_str_hash`/`g_str_equal` from `NativeLibrary.GetExport`, `g_hash_table_insert`, `g_hash_table_unref`, `g_free`, `g_error_free`). Collection `"default"`, label `"Quartermaster turn mailbox"`. `static bool IsAvailable()` (library loads and a probe lookup does not raise a D-Bus error).
  - `SessionSecretStore`: a process-wide in-memory dictionary. Its `TrySet` returns **false** (the secret is kept for this session only) but `Get` finds it.
  - `public static class PasswordProtectors { static IPasswordProtector ForThisOs(); static IPasswordProtector? Override { get; set; } }`:
    - `ForThisOs()` returns `Override` when it is set: the test seam, so Desktop tests on macOS and Linux CI never reach a real keychain. Desktop.Tests sets it to a fake in a `[ModuleInitializer]` next to `TestDataRoot`.
    - Otherwise: `DpapiProtector.Instance` on Windows; `SecretStoreProtector(MacKeychainStore)` on macOS; on Linux `SecretStoreProtector(LinuxSecretServiceStore)` when `IsAvailable()`, else `SecretStoreProtector(SessionSecretStore.Instance)`. Cached once per process.
  - `Quartermaster.Render.csproj` gains `<InternalsVisibleTo Include="Quartermaster.Desktop.Tests" />` so the Desktop tests can await `MailboxService.CurrentPoll` (T5).
  - `public sealed class SecretStoreFactAttribute : FactAttribute` in the test project: skip unless `QUARTERMASTER_SECRET_STORE_TESTS=1` and the OS matches.

- [ ] **Step 1: Write the failing tests** (`SecretStoreTests`, a fake `ISecretStore` with a refusing mode):
  - `Store_protector_round_trips_through_identity_save_and_load` (`turns/{id}.json` holds `qm-secret:v1:` bytes, no plain password anywhere in the file)
  - `Replacing_password_forgets_old_entry`
  - `Missing_store_falls_back_to_session` (refusing store → `SetPassword` false; session store: `GetPassword` works this process, a new `SessionSecretStore` instance does not find it)
  - `Dpapi_blob_off_windows_reads_as_no_password` (store protector given random bytes → `GetPassword()` null, no throw)
  - `Windows_default_is_dpapi` (`[WindowsFact]` from TurnTests)
  - `[SecretStoreFact] Real_store_round_trip` (macOS/Linux only; set, get, forget)
- [ ] **Step 2: Run them, expect compile failure.** `dotnet test tests/Quartermaster.Render.Tests -c Release --filter "FullyQualifiedName~SecretStore"`
- [ ] **Step 3: Implement** the stores and `PasswordProtectors`, then switch `TurnIdentity`'s default. Every P/Invoke path is wrapped so a missing library (`DllNotFoundException`, `EntryPointNotFoundException`) or any OS error means false/null, never a throw out of the store.
- [ ] **Step 4: Run them, expect PASS**, plus the existing `TurnTests` and `MailboxServiceTests`.
- [ ] **Step 5: Commit** `feat(render): mailbox password in the OS secret store (Keychain, Secret Service, session fallback)`

---

### Task 3: Desktop turn dialogs and the Replay bar

**Files:**
- Create: `src/Quartermaster.Desktop/Turns/MailboxOptions.cs`, `Turns/SendTurnDialog.cs`, `Turns/WhoAreYouDialog.cs`, `Turns/SendWaitWindow.cs`, `Turns/ReplayBar.cs`, `Shell/ChoiceDialog.cs`, `Shell/PasswordPrompt.cs` (namespace `Quartermaster.Desktop.Turns` for the turn ones)
- Modify: `src/Quartermaster.Desktop/Shell/Dialogs.cs`, `Program.cs` (`QuietDialogs`), `tests/Quartermaster.Desktop.Tests/MainWindowTests.cs` (`FakeDialogs`)
- Test: `tests/Quartermaster.Desktop.Tests/TurnDialogsTests.cs`, `tests/Quartermaster.Desktop.Tests/ReplayBarTests.cs`

**Interfaces:**
- Consumes: T1 `TurnDialogRules`, `MailboxValues`; Render `TurnUiModel` (`HiddenInfoNote`, `DefaultRecipient`, `Recipients`, `StepText`, `PlayStepMs`, `ShownName`), `TurnReplayModel`.
- Produces:
  - `public sealed record SendTurnChoice(PlayerDef Recipient, bool UseMailbox, string Relay, string Password)`; `public sealed record WhoChoice(string LocalPlayerId, bool UseMailbox, string Relay, string Password)`.
  - New `IDialogs` members (real ones are owner-modal via `ShowDialogTracked`; `QuietDialogs` returns null / -1 / no-op):
    - `Task<SendTurnChoice?> AskSendTurnAsync(Window owner, string fromName, IReadOnlyList<PlayerDef> players, string localPlayerId, RulesPack? pack, MailboxValues mailbox)`
    - `Task<WhoChoice?> AskWhoAreYouAsync(Window owner, string prompt, IReadOnlyList<PlayerDef> players, RulesPack? pack, string? preselect, MailboxValues? mailbox)`
    - `Task<int> ChooseAsync(Window owner, string message, string caption, IReadOnlyList<string> buttons, int defaultIndex)` (-1 when closed)
    - `Task<string?> AskPasswordAsync(Window owner, string caption, string prompt)` (masked box)
    - `Task<bool> WaitForAsync(Window owner, Task work, TimeSpan limit)` (the SendWaitForm: caption `"Send Turn"`, label `"Sending turn…"`; true when `work` ended within `limit`)
  - `public sealed class ReplayBar : UserControl`: `void Bind(TurnReplayModel? model)`, `event Action<Exception>? Failed`, `TurnReplayModel? Model`, `bool Playing`, `string StepText`, `string LineText`, and internal `Button NextButton/PlayAllButton/StopButton` named `replay.next` / `replay.playAll` / `replay.stop`. Texts and behaviour exactly as WinForms `ReplayBar.cs`: `"Next"`, `"Play all"`/`"Finish"`, `"Stop"`, `"Turn from {ShownName}:"`, `"Press Next or Play all to replay the turn."`, a `DispatcherTimer` at `TurnUiModel.PlayStepMs(count)`, the WinForms `Sync` enabling, and a model exception → `Failed`.
  - `MailboxOptions : UserControl` (`Use`, `Relay` trimmed, `Password`, `Problem`; relay MaxLength 512, password MaxLength 256 masked; boxes enabled only while ticked).

- [ ] **Step 1: Write the failing tests**
  - `TurnDialogsTests`: `Send_dialog_defaults_recipient_and_refuses_without_relay`, `Who_dialog_preselects_and_hides_mailbox_when_null`, `Mailbox_boxes_follow_tick_same_tick`, `Choice_dialog_returns_index_and_minus_one_on_close`, `Password_prompt_masks`, `Wait_returns_false_after_limit` (a never-ending task, limit 200 ms).
  - `ReplayBarTests` (a `TurnReplayModel` over a fixture turn as TurnTests builds one): `Next_steps_and_updates_texts`, `Play_all_becomes_finish_then_completes`, `Stop_disables_and_stops_timer`, `Model_exception_raises_failed`.
- [ ] **Step 2: Run them, expect compile failure.** `dotnet test tests/Quartermaster.Desktop.Tests -c Release --filter "FullyQualifiedName~TurnDialogs|FullyQualifiedName~ReplayBar"`
- [ ] **Step 3: Implement**, with labels and texts verbatim from the WinForms files and `TurnDialogRules`.
- [ ] **Step 4: Run them, expect PASS**, plus the whole `Quartermaster.Desktop.Tests` (the `IDialogs` change touches every fake).
- [ ] **Step 5: Commit** `feat(desktop): turn dialogs, replay bar, choice and password prompts`

---

### Task 4: The turn game and Send Turn

**Files:**
- Create: `src/Quartermaster.Desktop/MainWindow.Turns.cs` (first half of the WinForms `MainForm.Turns.cs` port)
- Modify: `MainWindow.Online.cs` (Game menu: `Send &Turn…` first, then the online separator, Players…, Leave; replace the `5d:` comments with calls to `UpdateTurnUi()` / `UpdateMailboxWatch()` (the latter a no-op stub until T5); File > `&Export Viewer Copy…` shown and enabled for guests only), `MainWindow.axaml.cs` (File menu item; `OnGameReplacedAnyThread` calls `FollowOpenGame()`; `CloseSession` clears the turn identity; `DiscardQuestion`/close path awaits `ConfirmSendDoneAsync()` first), `MainWindow.Phases.cs` (`_history.IsTurnGame = () => _turnIdentity is { LocalPlayerId.Length: > 0 }`), `MainWindow.HotSeat.cs` (`StopHotSeatForTurns`)
- Test: `tests/Quartermaster.Desktop.Tests/MainWindowTurnsSendTests.cs`

**Interfaces:**
- Consumes: T1, T2 (`TurnIdentity.Load(gameId)` default protector, `HasStoredPassword`), T3 dialogs; Render `TurnSender.Build/AfterSend`, `TurnUiModel.*`, `ReceivedTurns.Prune`, `MailboxService` (constructed here with `RelayTrust.Pins()` and `c => RelayTrust.Ask(this, _dialogs, c)`, `post: PostToUi`), `ModuleSession.ExportViewerCopy`.
- Produces (used by T5, T6):
  - `internal TurnIdentity? TurnIdentityForTests`, `internal MailboxService? Mailbox`, `internal Task SendTurnAsync()`, `internal Task ExportViewerCopyAsync()`, `internal MenuItem? SendTurnItem`, `internal Task<bool> ConfirmSendDoneAsync()`.
  - `void FollowOpenGame()` (WinForms `:75-105`): it loads the identity for a non-guest, non-viewer-copy game. A turn game (`TurnUiModel.IsTurnGame`) stops hot-seat and sets the view sides; any other local game keeps or creates hot-seat. `string PasswordFor(TurnIdentity id)` asks once per session (ruling) when `HasStoredPassword` and nothing is readable.
  - `void UpdateTurnUi()` (WinForms `:115`): Send Turn enabled only for a local game, not a viewer copy, not replaying, with the tooltips `OnlineText` / `ViewerCopyText` / `ReplayingText`, then `ApplyGates()`.
- Behaviour exactly as WinForms `MainForm.Turns.cs:115-370`:
  - **Send Turn** refuses with `OnlineText` while online or a guest, and with `NeedPlayersText` for fewer than two players. It asks the previous-send question if one is in flight, asks who-are-you when the local player is unknown, then shows the send dialog.
  - It then saves through the file picker (title `"Save Turn"`, `.qturn` filter as an Avalonia `FilePickerFileType`, `TurnUiModel.DefaultFileName`), then runs `SendTurnCore`. That includes undoing the pass line when the write fails, `ApplyMailbox` with its status texts, `RecordSent`, `SaveIdentity`, `Prune`, stopping hot-seat and setting the view sides, and the status `"Turn saved to {file}"`.
  - **Mailbox send**: `"Sending the turn through the relay…"` → `"The turn is in the mailbox on the relay."`. On failure the choice is Retry/Close (`"The turn could not be sent through the relay: {reason}"` + `"The file is saved at {path}; send it another way."`).
  - **Close or Back to launcher during a send:** `WaitForAsync` up to 15 s, then `"The turn is still being sent through the relay. Leave anyway?"`, caption `"Send Turn"`, default No.
  - **Export Viewer Copy…** (guest): the save picker (title `"Export Viewer Copy"`, `*.qsave`, file name `"Viewer copy"`), then the status `"Viewer copy saved: {file}"`.

- [ ] **Step 1: Write the failing tests** (`MainWindowTurnsSendTests`; fixture module with an invented Red/Blue pack and players Alice (Red) / Bob (Blue); temp roots; fake protector through T2's `PasswordProtectors.Override`; the in-process relay for the mailbox cases):
  - `Send_turn_writes_file_records_sent_and_switches_to_turn_view` (hot-seat gone, `Session.View` sides = Alice's, undo below the turn base refused with `HistoryController.AlreadySentText`)
  - `Send_turn_needs_two_players_and_refuses_online`
  - `Empty_turn_logs_passed_and_write_failure_undoes_it`
  - `Mailbox_send_reaches_relay_and_sets_status`
  - `Mailbox_failure_offers_retry_then_close_keeps_file`
  - `Close_during_mailbox_send_asks_and_no_keeps_window`
  - `Unreadable_password_asked_once_per_session`
  - `Guest_export_viewer_copy_writes_viewer_save` (the file loads as a viewer copy: `IsViewerCopy`, `ViewerBanner`)
  - `Send_turn_menu_gates_and_tooltips`
- [ ] **Step 2: Run them, expect failure.** `dotnet test tests/Quartermaster.Desktop.Tests -c Release --filter "FullyQualifiedName~MainWindowTurnsSend"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them, expect PASS**, three runs, plus the whole `Quartermaster.Desktop.Tests` (hot-seat, online and launcher tests stay green).
- [ ] **Step 5: Commit** `feat(desktop): turn games and Send Turn, mailbox send, Export Viewer Copy`

---

### Task 5: Open Turn File, replay and the mailbox watch

**Files:**
- Modify: `src/Quartermaster.Desktop/MainWindow.Turns.cs` (second half of the port), `MainWindow.axaml` (`ReplayBarHost` docked top under the alert bar, hidden unless bound), `MainWindow.axaml.cs` (File > `Open &Turn File…` gated `file.openTurn`; `CloseSession` calls `EndReplayUi()` and `StopMailbox()`; `OnGameReplacedAnyThread` calls `OnTurnGameReplaced()`), `MainWindow.Launch.cs` (`LaunchKind.OpenTurn` as WinForms `MainForm.cs:524-532`), `MainWindow.Alerts.cs` (`AlertBarHost.NoticeClicked += OpenWaitingTurn`), `MainWindow.Workbench.cs` (`internal Task<bool> CloseWorkbenchForReplayAsync()`: the unsaved-rules question first, then `DestroyWorkbench`), `MainWindow.Online.cs` (going online stops the watch: `UpdateMailboxWatch()`), `Launcher/PlayOfflineWindow.cs` (Open turn file… enabled: the picker with title `"Open turn file"` and the `.qturn` filter, finishing with `LaunchKind.OpenTurn`; tooltip as WinForms)
- Modify test: `tests/Quartermaster.Desktop.Tests/LauncherWindowTests.cs` (the 5c "comes with stage 5d" pin becomes the enabled behaviour)
- Test: `tests/Quartermaster.Desktop.Tests/MainWindowTurnsOpenTests.cs`

**Interfaces:**
- Consumes: T1 `TurnOpenDecision`, T3 `ReplayBar`/`ChooseAsync`/`AskWhoAreYouAsync`, T4 `FollowOpenGame`, `PasswordFor`, `Mailbox`, `UpdateTurnUi`; Render `TurnOpener.Check`, `ModuleSession.OpenTurn`, `TurnReplayModel`, `ReceivedTurns.Keep`, `MailboxService.Watch/SetIdentity/Stop/DeleteAsync/TurnWaiting/Status`, `TurnUiModel.NewTurnWaitingText`.
- Produces (used by T6): `internal Task<bool> OpenTurnFileAsync(string path)`, `internal TurnReplayModel? TurnReplay`, `internal ReplayBar ReplayBarControl`, `internal string? TurnNoticeText`, `internal Task OpenWaitingTurnAsync()`.
- Behaviour exactly as WinForms `MainForm.Turns.cs:373-660`:
  - **Open:** refused with `OnlineText` while online or a guest. Read errors give `"Could not open '{path}':\n{msg}"` / `"Could not open the turn:\n{msg}"`. Checks go through `TurnOpenDecision`; Open anyway / Cancel uses `ChooseAsync` with buttons `[OpenAnywayText, "Cancel"]`, default Cancel. Who-are-you uses `FromPrompt`.
  - **Before replay:** close the Workbench (`CloseWorkbenchForReplayAsync`; a cancel there cancels the open), close the formula windows, `StopHotSeatForTurns`, and capture the `ReplayContext`.
  - **Replay:** bind the bar, then the status `"Replaying the turn from {name}: Next, Play all or Stop."`.
  - **`OnReplayChanged`:** on failure, `"The turn could not be replayed; your game is as it was before.\n{err}"`, `RestoreAfterFailedReplay`, restore the save path, dirty flag, recovered flag and hot-seat current, and raise the notice again. On success, `"{from}'s turn is replayed. Your turn."` plus `ViewNotice`/`VersionNotice` (or `EndMismatchText`), dirty, then a forced autosave via `_autosaver.SaveForced("turn replayed")`; the mailbox item is deleted only if that succeeded, then the watch restarts.
  - **Watch:** `UpdateMailboxWatch` (key = GameId, relay, local player, password; off while online), `OnTurnWaiting` (another GameId is ignored; `ReceivedTurns.Keep` on a worker; the notice is `NewTurnWaitingText`), `OpenWaitingTurn`, `ClearWaiting`, `StopMailbox`, `OnTurnGameReplaced` (an abandoned replay re-raises its notice), and `EndReplayUi`.
  - **Launch `OpenTurn`:** a failed or cancelled open with no game goes back to the launcher (posted) when `LauncherAvailable`, else to the setup chooser.

- [ ] **Step 1: Write the failing tests** (`MainWindowTurnsOpenTests`; turns built by a sender window or by `TurnSender` as TurnTests does):
  - `Open_turn_replays_step_by_step_and_ends_in_local_view`
  - `Open_turn_checks_and_cancel_keeps_game` (DifferentModule, OlderPosition, AlreadyPlayed→Cancel, AlreadyPlayed→Open anyway)
  - `Open_turn_new_game_asks_who_are_you_and_remembers`
  - `Open_turn_closes_workbench_and_stops_hot_seat` (Workbench with unsaved edits: the rules question comes first; No-cancel cancels the open)
  - `Failed_replay_restores_game_and_keeps_mailbox_item`
  - `Mailbox_turn_raises_notice_and_click_opens_it` (the in-process relay: a turn put in the mailbox by a `MailboxService` "Bob"; `Watch` polls at once; the test awaits `Mailbox.CurrentPoll` (internal, visible through T2's `InternalsVisibleTo`); chip text `NewTurnWaitingText("Bob")`; `AlertBarHost.ClickNotice()` opens the replay; after the replay and autosave the item is deleted)
  - `Going_online_stops_watch`
  - `Launcher_open_turn_cancel_returns_to_launcher`
  - `Play_offline_open_turn_button_finishes_with_open_turn`
- [ ] **Step 2: Run them, expect failure.** `dotnet test tests/Quartermaster.Desktop.Tests -c Release --filter "FullyQualifiedName~MainWindowTurnsOpen|FullyQualifiedName~LauncherWindow"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them, expect PASS**, three runs, plus the whole `Quartermaster.Desktop.Tests`.
- [ ] **Step 5: Commit** `feat(desktop): open turn files, replay bar, mailbox watch and notice chip`

---

### Task 6: Two-window round trip, screenshots, parity, Mac/Linux checklist, RESUME

**Files:**
- Create: `tests/Quartermaster.Desktop.Tests/TurnsEndToEndTests.cs`
- Modify: `src/Quartermaster.Desktop/Shell/ScreenshotStates.cs` (states `send-turn`, `who-are-you`, `replay-bar`, `turn-notice`, `open-turn-choice`; no network: the notice is set through an internal hook, the replay over a fixture turn), `build/port-screenshots.ps1`, `docs/port/parity.md` (tick the 5d rows for Windows: Send Turn, Open Turn File, ReplayBar, SendTurnDialog/SendWaitForm/WhoAreYouDialog/MailboxOptions, Export Viewer Copy, the notice chip, Turn games; update the "comes with 5d" notes), `docs/port/stage5d-mac-checklist.md` (new; includes the opt-in `QUARTERMASTER_SECRET_STORE_TESTS=1 dotnet test --filter SecretStore` run, Keychain prompt behaviour, a turn sent from WinForms opened on the Mac, and a Linux section for libsecret present/absent), `docs/superpowers/RESUME.md` (CURRENT STATE for 5d; stage 5 complete; next is stage 6 packaging)
- Test: the end-to-end class and the full suite

**Interfaces:**
- Consumes: T4/T5 hooks, `InProcessRelay`.

- [ ] **Step 1: Write `TurnsEndToEndTests`** (two `MainWindow`s over one in-process relay, temp roots per window, fake protectors):
  - `Round_trip_through_mailbox` (Alice sends with the mailbox → Bob's notice → Bob replays and sends back → Alice's notice → Alice replays; each side sees only its own private values after its replay; undo below the received turn refused)
  - `Turn_file_from_winforms_format_opens` (a `.qturn` written by Render `TurnSender` directly, as the WinForms app writes it, opens and replays in Desktop)
  - `Hidden_values_public_only_during_replay` (mid-replay, the receiver's ledger shows no private values of either side)
- [ ] **Step 2: Run them, three runs in a row; fix in Desktop code anything they show, naming each fix in the commit body.**
- [ ] **Step 3: Add the screenshot states; run `build/port-screenshots.ps1 -Only send-turn,who-are-you,replay-bar,turn-notice,open-turn-choice` (off-screen).** Expected: five PNGs, exit 0, no network.
- [ ] **Step 4: Update parity, the checklist and RESUME.** RESUME names the plan, the ledger `.superpowers/sdd/2026-11-13-port-stage5d-turn-files/` and the rulings above.
- [ ] **Step 5: Run the full suite.** `dotnet test Quartermaster.slnx -c Release`. Expected 0 failed (known-flaky re-run once, named).
- [ ] **Step 6: Commit** `test(desktop): turn round trip; docs(port): stage 5d parity, checklist, screenshots, RESUME`
