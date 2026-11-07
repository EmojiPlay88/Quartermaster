# Piece 6c — Turn Files and Mailbox Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Players take turns a day apart: **Game → Send Turn…** writes a `.qturn` file (and optionally drops it in an encrypted mailbox on the user's relay); the receiver opens it, watches the sender's turn replay step by step, then plays their own turn.

**Architecture:** Core gains a turn-file format (whole game at the start of the sender's turn + the sender's commands + start/end hashes) and a per-game "turn base" in the session. Render gains the open/replay/send models and a local per-game identity store ("which player am I"). The relay gains a small mailbox (encrypted blobs per box id, 30-day expiry) and the Net client gains mailbox calls; a Render mailbox service polls it. The App adds Send Turn, Open Turn File (File menu and launcher Play offline), the replay bar and the "New turn waiting" alert.

**Tech Stack:** C# .NET 10, WinForms, xUnit, ASP.NET Core relay (Docker), existing Quartermaster.Net protocol.

**Spec:** `docs/superpowers/specs/2026-11-07-piece6-multiplayer-design.md` §6, §7 (mailbox), §8a.9; `docs/superpowers/specs/2026-11-07-launcher-design.md` §3.2 (Open turn file… in Play offline).

## Global Constraints

- Content boundary (hard): no real-game content; tests use invented Red/Blue sides and existing fixtures.
- Turn file: extension `.qturn`, JSON, gzip, `TurnFile.Version = 1`; holds the full game at the start of the sender's turn, the sender's commands (dice results included — replay never rerolls), the end-state hash, module SHA-256, `GameId`, from/to player.
- Hidden information in turn files is **hidden on screen, not enforced** (the file holds the whole game). Say so plainly in the Send Turn dialog and README.
- Exact copy: menu `Game → Send &Turn…`, `File → Open &Turn File…`, launcher Play offline button `Open turn file…`; replay bar buttons `Next`, `Play all`, `Stop`; alert `New turn waiting from {player}`; warning `This turn starts from an older position than your game` with buttons `Open anyway` / `Cancel`; `This turn file belongs to a different module`; end mismatch `The replayed turn does not match the sender's game.`; Send Turn dialog note `Turn files carry the whole game; hidden information is only hidden on screen.`
- Mailbox: per game, per recipient player; stores encrypted blobs only; 30-day expiry. Spec's 20 MB file limit is capped by the relay frame (8 MB); ruling: a mailbox file is at most one relay frame (`Framing.MaxFrame` minus envelope) — Send Turn refuses larger with `This turn is too large for the mailbox; send the file another way.`
- Mailbox keys (password optional, empty = none; GameId is a random GUID only players hold):
  - box key = PBKDF2-SHA256(password, salt `qm-mbox:{GameId}`, 200,000 iterations, 32 bytes);
  - box id = lower-hex SHA-256(box key ‖ UTF-8 recipient PlayerId) — the relay never sees GameId, player ids or password;
  - seal key = PBKDF2-SHA256(password, salt `qm-mbox-key:{GameId}`, 200,000, 32 bytes); blobs sealed with `Secrets.Seal`.
- Relay mailbox limits: 50 items per box, `Relay:MailboxMaxBytes` total default 1 GiB (oldest expired first; full → refuse `The mailbox is full.`), 30 uploads per hour per address, expiry sweep hourly, files under `{DataDir}/mailbox/`, created 0600 / dirs 0700.
- Polling: on game open and every 5 minutes while a game with a mailbox is open; never on the UI thread; never crash on network errors.
- Save format stays 7 (new fields optional, omitted when default); older saves load unchanged with undo history.
- Solo, hot-seat, online unchanged when no turn file is involved. App must not reference Relay/AspNetCore.
- Test isolation: never touch real `%LOCALAPPDATA%` (`QUARTERMASTER_LAYOUT_ROOT` / `AppDataRoot`); smokes only with repo fixtures in scratch.
- Build warning-free except MSB3277; `dotnet test Quartermaster.slnx` green before each final commit.

## Review Focus

1. A turn file from a different module, a corrupt/oversized/garbage `.qturn`, or one whose commands don't reproduce the end hash → clear message, nothing replaced, no crash (Task 1, Task 2 tests).
2. Receiver moved after their own send, then opens the opponent's older-based turn → warning with Open anyway / Cancel; Cancel leaves the game untouched (Task 2 test).
3. Replay interrupted (Stop, window closed mid-replay, Play all then Stop) → game left at a consistent step, autosave valid, replay resumable or finished by Play all (Task 2 test).
4. Mailbox with relay down / wrong password / expired items → polling silent except a single status note; wrong password never crashes (sealed open fails) (Task 4 test).
5. Two quick Send Turns of the same turn (resend) → receiver sees two items; opening the older one after the newer warns (start hash already applied) instead of double-applying (Task 2 test: same start hash already passed → `This turn has already been played.`).

---

### Task 1: Turn-file format and turn base (Core)

**Files:** Create `src/Quartermaster.Core/TurnFile.cs`, `src/Quartermaster.Core/StateHash.cs`; modify `GameSession.cs` (turn base), `NativeSave.cs` (persist `TurnBase`, optional). Tests `tests/Quartermaster.Core.Tests/TurnFileTests.cs`.

**Interfaces — Produces:**
- `public static class StateHash { public static string Of(GameState s); }` — lower-hex SHA-256 of `JsonSerializer.Serialize(s, QuartermasterJson.Options)`.
- `GameSession.TurnBase` (`int`, log index where the local player's current turn began; default 0; ≥ floor) and `void MarkTurnBase()` (sets it to `Log.Count`). Persisted as optional `turnBase` in the save; clamped to the floor/log length on load.
- `public sealed record TurnFile(int Version, string ModuleSha256, string? RulesSha256, string GameId, string FromPlayerId, string FromName, string? ToPlayerId, DateTime CreatedUtc, GameState Start, string StartHash, IReadOnlyList<GameCommand> Commands, string EndHash);`
- `public static class TurnFiles { const int Version = 1; const long MaxPlain = 64 MiB; const int MaxCommands = 100_000; TurnFile Create(GameSession g, string fromPlayerId, string fromName, string? toPlayerId); void Write(string path, TurnFile t); byte[] ToBytes(TurnFile t); TurnFile Read(string path); TurnFile FromBytes(byte[] gz); GameSession Replay(TurnFile t, string moduleSha, Action<GameCommand, int>? step = null); }`
  - `Create`: start = genesis replayed to `TurnBase` (reuse the replay used by `NativeSave`/`RewindTo` logic without mutating `g`); commands = `Log[TurnBase..]`; hashes via `StateHash`. Throws `InvalidOperationException("Nothing to send yet.")` when no commands and no prior send.
  - `FromBytes`: bounded gunzip (`MaxPlain`), version check (`Unsupported turn file version {v}.`), `NativeSave.ValidateState(Start)`, command cap, all as `InvalidDataException`.
  - `Replay`: new `GameSession(Start, moduleSha)` (genesis = Start), executes each command inside its own action scope, calls `step` after each, then `Rebase()` so the receiver cannot undo the sender's turn, then `MarkTurnBase()`.

- [ ] **Step 1: Failing tests:** `RoundTrip_BytesAndFile`; `Create_StartIsStateAtTurnBase_CommandsAfter`; `Replay_EqualsSenderEndState_AndEndHashMatches`; `Replay_NoUndoPastReplay`; `Read_Garbage_Oversized_WrongVersion_TooManyCommands_Throw_InvalidData`; `TurnBase_PersistsInSave_OlderSaveLoadsWithZero_AndKeepsHistory`; `Dice_InCommands_ReplayDoesNotReroll` (a RollDice command's result is identical after replay).
- [ ] **Step 2–4:** RED → implement → PASS. **Step 5:** Commit `feat(core): turn files and turn base`.

### Task 2: Open, replay and send models (Render)

**Files:** Create `src/Quartermaster.Render/Turns/TurnOpener.cs`, `TurnReplayModel.cs`, `TurnSender.cs`, `TurnIdentity.cs`. Modify `ModuleSession.cs` (open-turn entry point, `IsReplaying`). Tests `tests/Quartermaster.Render.Tests/TurnTests.cs`.

**Interfaces — Consumes:** Task 1. **Produces:**
- `TurnIdentity` (store under `AppDataRoot`/`turns/{GameId}.json`, atomic write): `string? LocalPlayerId`, `string? Relay`, `bool UseMailbox`, `byte[]? ProtectedPassword` (DPAPI `ProtectedData`, CurrentUser; tests may inject a no-op protector via an `IPasswordProtector` interface); `static TurnIdentity Load(string gameId)`, `void Save()`; `IReadOnlySet<string> PlayedStartHashes` (start hashes already applied; capped 500).
- `enum TurnCheck { Ok, DifferentModule, OlderPosition, AlreadyPlayed, NewGame }`; `TurnOpener.Check(ModuleSession s, TurnFile t) → TurnCheck`: module SHA mismatch → `DifferentModule`; `StartHash ∈ PlayedStartHashes` → `AlreadyPlayed`; no open game with that GameId → `NewGame`; open game's `StateHash.Of(State) != t.StartHash` → `OlderPosition`; else `Ok`.
- `TurnReplayModel(ModuleSession s, TurnFile t, string localPlayerId)`: `int Step`, `int Count`, `string? CurrentLine`, `bool Done`, `void Next()`, `void PlayAll()`, `void Stop()`, `bool EndMatches` (after done), `event Action? Changed`. Begins by replacing the session's game with a session at `t.Start`; each `Next` executes one command (as in `TurnFiles.Replay`); `Stop` leaves the game at the current step with replay paused (resumable by `Next`/`PlayAll`); when done: `Rebase`, `MarkTurnBase`, record `StartHash` in `PlayedStartHashes`, set `ViewSides` to the local player's sides (hidden on screen).
- `ModuleSession.IsReplaying` — while true, all actions refused through `LocalActions` (`Finish the replay first.`); autosave allowed (the session is consistent after each step).
- `TurnSender.Build(ModuleSession s, string toPlayerId) → TurnFile` (from = `TurnIdentity.LocalPlayerId`); `TurnSender.AfterSend(ModuleSession s)` → `MarkTurnBase()` and record nothing else.
- Local player: if `TurnIdentity.LocalPlayerId` is null, the App asks (Task 5); models take it as a parameter.

- [ ] **Step 1: Failing tests:** `Check_DifferentModule`; `Check_NewGame_WhenNoGameWithId`; `Check_Ok_WhenStateMatchesStart`; `Check_OlderPosition_WhenBothMoved`; `Check_AlreadyPlayed_ResendOpenedTwice`; `Replay_NextStepsOneCommand_LineShown`; `Replay_StopThenPlayAll_FinishesWithEndHash`; `Replay_ActionsRefusedWhileReplaying`; `Replay_Done_ViewSidesAreLocalPlayers_TurnBaseMarked`; `Send_ThenOpponentReplays_BothStatesEqual` (two sessions, two turns each way); `Identity_RoundTrip_UnderLayoutRoot_PasswordProtected`.
- [ ] **Step 2–4:** RED → implement → PASS. **Step 5:** Commit `feat(render): open, replay and send turns`.

### Task 3: Mailbox on the relay (Net + Relay)

**Files:** Modify `src/Quartermaster.Net/Protocol.cs`, `Secrets.cs`, `RelayClient.cs`; create `src/Quartermaster.Relay/Mailbox.cs`; modify relay `Program.cs`, `RelayOptions`, `Rooms.cs` dispatch; `docs/relay-setup.md` (mailbox section, settings table). Tests `tests/Quartermaster.Relay.Tests/MailboxTests.cs`, `tests/Quartermaster.Net.Tests/MailboxClientTests.cs`.

**Interfaces — Produces:**
- Protocol (relay messages): `MailboxPut(string Box, byte[] Sealed)` → `MailboxStored(string ItemId)`; `MailboxList(IReadOnlyList<string> Boxes)` (≤ 32 boxes) → `MailboxItems(IReadOnlyList<MailboxItemInfo> Items)` with `record MailboxItemInfo(string Box, string ItemId, long Size, DateTime StoredUtc)`; `MailboxGet(string Box, string ItemId)` → `MailboxItem(string Box, string ItemId, byte[] Sealed)`; `MailboxDelete(string Box, string ItemId)` → `MailboxDeleted(string ItemId)`; errors as `Refused`. Box ids validated: 64 lower-hex; item ids: relay-generated 32 hex.
- `Secrets.MailboxBoxKey(string password, string gameId) → byte[]`, `Secrets.MailboxBoxId(byte[] boxKey, string playerId) → string`, `Secrets.MailboxSealKey(string password, string gameId) → byte[]` (values in Global Constraints).
- `RelayClient`: `Task<string> MailboxPutAsync(string box, byte[] sealed)`, `Task<IReadOnlyList<MailboxItemInfo>> MailboxListAsync(IReadOnlyList<string> boxes)`, `Task<byte[]> MailboxGetAsync(string box, string itemId)`, `Task MailboxDeleteAsync(string box, string itemId)` (Hello role `mailbox` allowed; any connection may use the mailbox).
- Relay `Mailbox` store: limits in Global Constraints; `RelayOptions.MailboxMaxBytes` (default 1 GiB), `MailboxExpiryDays` (30); sweep hourly (TimeProvider); survives restart (files + small index json per box).

- [ ] **Step 1: Failing tests (relay):** `PutListGetDelete_RoundTrip`; `Expires_After30Days` (fake clock); `BoxCap_50_Refused`; `TotalBytes_Cap_Refused_MailboxFull`; `UploadRate_30PerHourPerAddress`; `BadBoxId_Refused`; `Survives_Restart`; `Files_0600_Dirs_0700` (Unix only, skipped on Windows). **(client):** `Client_Mailbox_AgainstRealRelay`; `Secrets_Mailbox_DerivationsStable` (fixed vectors computed once and pinned).
- [ ] **Step 2–4:** RED → implement → PASS; rebuild Docker image if Docker available (report only). **Step 5:** Commit `feat(relay): encrypted turn mailbox`.

### Task 4: Mailbox service (Render)

**Files:** Create `src/Quartermaster.Render/Turns/MailboxService.cs`. Tests `tests/Quartermaster.Render.Tests/MailboxServiceTests.cs`.

**Interfaces — Consumes:** Tasks 2, 3. **Produces:**
- `MailboxService(Func<string, RelayClient> connect, Action<Action> post, TimeProvider? time = null)`: `Task<bool> SendAsync(TurnIdentity id, TurnFile t, string password)` (seal with seal key, put into recipient's box; refuses oversized with the Global copy text); `void Watch(string gameId, TurnIdentity id, string password)` (poll now and every 5 min the local player's own box; new items → download, open with seal key, `TurnFiles.FromBytes`; raise `TurnWaiting(TurnFile t, string itemId)` via `post`); `Task DeleteAsync(itemId)` after a turn is played; `void Stop()`; `event Action<string>? Status` (one note per failure kind, not per poll).
- Items that fail to open (wrong password, garbage) are ignored and reported once via `Status`.

- [ ] **Step 1: Failing tests (real in-process relay):** `Send_ThenWatch_RaisesTurnWaiting_WithSendersName`; `WrongPassword_Ignored_StatusOnce`; `RelayDown_NoCrash_StatusOnce_RecoversNextPoll` (fake clock); `Played_Item_Deleted`; `Oversized_Refused_WithCopy`.
- [ ] **Step 2–4:** RED → implement → PASS. **Step 5:** Commit `feat(render): turn mailbox service`.

### Task 5: App — Send Turn, Open Turn File, replay bar, alert

**Files:** Create `src/Quartermaster.App/SendTurnDialog.cs`, `ReplayBar.cs`, `MainForm.Turns.cs`, `WhoAreYouDialog.cs`; modify `MainForm.cs` (menus), `PlayOfflineForm.cs` (show `Open turn file…`), `LaunchRequest`/`Program.cs` (`LaunchKind.OpenTurn` with path), `AlertBar.cs` usage, `SmokeRunner`/smoke flags.

**Behaviour:** `Game → Send Turn…`: dialog with recipient (default next player in Players order), Save as `.qturn` (SaveFileDialog, default name `{module} {GameId[..8]} turn {n}.qturn`), checkbox `Also send through my relay` (relay from Settings, password field remembered per game via `TurnIdentity`), the hidden-info note; on success `TurnSender.AfterSend`. `File → Open Turn File…` and launcher `Open turn file…`: read file (errors shown plainly), run `TurnOpener.Check` (DifferentModule → message; AlreadyPlayed → `This turn has already been played.`; OlderPosition → warning with `Open anyway` / `Cancel`; NewGame → `WhoAreYouDialog` picks the local player from the file's players), then the replay bar (`Next`, `Play all`, `Stop`, step `n / N`, current line) docked above the log; when done, if `!EndMatches` show the mismatch copy; if it came from the mailbox, delete the item. Alert bar `New turn waiting from {player}` (click → open). Requires no network when no relay set. Guests/online sessions: Send Turn disabled.
- Smokes: `--smoke-turns` (two in-process sessions on the phases fixture: send, open, replay all, compare hashes; with `--screenshot-turns <png>` capture the replay bar mid-replay). Mailbox path covered by Task 4/6 tests (no relay in App).

- [ ] **Step 1:** Model-level tests for dialog defaults (recipient = next player, file name) in `tests/Quartermaster.Render.Tests/TurnUiModelTests.cs` (put the logic in Render models).
- [ ] **Step 2:** Implement; run `--smoke-turns`, `--smoke-phases`, `--screenshot-launcher`; inspect PNGs. **Step 3:** Commit `feat(app): send and open turn files with replay`.

### Task 6: End-to-end and docs

- [ ] **Step 1:** `tests/Quartermaster.Render.Tests/Piece6cEndToEndTests.cs`: two players (Red, Blue) over a real in-process relay mailbox: Red plays and sends, Blue's service raises TurnWaiting, Blue replays (end hash matches), plays, sends back; Red replays; three rounds; final states equal; resend → AlreadyPlayed; Blue moves then opens an older-based turn → OlderPosition.
- [ ] **Step 2:** Docs: README "Play with others" adds turn files (+ mailbox, hidden-info note); `docs/relay-setup.md` mailbox section final check; RESUME 6c row + next step OCR; followups 6c section. No personal hosts/paths/emails.
- [ ] **Step 3:** Full suite PASS. **Step 4:** Commit `test: piece 6c end-to-end; docs`.

## Dependencies

Task 1 ∥ Task 3 (independent). Task 2 after 1. Task 4 after 2 and 3. Task 5 after 4. Task 6 last.
