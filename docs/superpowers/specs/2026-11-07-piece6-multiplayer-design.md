# Piece 6 — Multiplayer — Design

*Date: 2026-11-07. Builds on master spec §11 (`2026-11-06-quartermaster-engine-design.md`) and pieces 1–5 as merged to `master`. Where this document and master spec §11 disagree, this document wins.*

## 1. Intent

The user wants to play their wargame with other people in all three ways tabletop players do:

- **hot-seat** — two or more people at one PC;
- **live online** — a friend elsewhere, moves appearing in real time (like VASSAL's server);
- **turn files** — one turn at a time, a day apart (like VASSAL's play-by-email logs).

Online play must not need anyone to touch their home router. The user has an **Ubuntu VPS** and will run a small relay program on it, packaged as a **Docker container** so it isn't tied to Ubuntu. Hidden information must be **enforced** online: a player's copy of Quartermaster never receives what that player is not allowed to see.

What the user said, condensed: all three modes; a relay server on their VPS (Ubuntu), later asked for as a Docker container so it runs anywhere; hidden-information rules "you decide" (the assistant's proposal below was accepted); turn files like VASSAL plus an optional "send via my VPS" mailbox (the assistant's call, accepted). Everything else is marked "(call)".

## 2. Content boundary (unchanged, hard)

No content from any real game. Visibility settings live in the user's rules pack. Tests use invented sides ("Red", "Blue"), the existing invented fixtures and a synthetic module.

## 3. Shared base — players, sides, visibility

### 3.1 Players and sides
- Sides already exist in the rules pack (piece 3: `SideDef` with a unit filter).
- A game has a **player list**: each player has a name and holds one or more sides. Solo play = one player holding every side (today's behaviour, the default).
- The player list is chosen at game setup (new dialog page after difficulty) and editable later in Settings → Game. It is part of the game state (logged command), so saves and undo keep it.
- **Roles** within a side (master spec §11.1) are a non-goal for this piece (call).

### 3.2 Visibility rules (rules pack format 4)
A new Workbench **Visibility** tab sets, per side:

| Setting | Default | Effect for players who don't hold that side |
|---|---|---|
| Property visibility (`PropertyDef.Visibility`: Private / Public) | Private | Private ledger values of that side's units and holders are not shown (tooltips, ledger, outliner, popup, inventory, breakdowns, alerts that would reveal them). |
| Holder owner (`HolderDef.SideId`, optional) | none = public | A holder owned by a side is private to it. |
| Map / org display owner (`MapOwner { MapId, SideId }` list) | none = public | The map is absent from their module tree, cannot be opened, and its pieces are not sent to them. |
| Private dice (`FormulaDef.PrivateRolls`) | false | Rolls made by that formula show as "{side} rolled dice" without results. Dice-tool rolls are public. |

- **Piece ownership:** a piece belongs to the first side whose unit filter matches it (same rule as the piece-5 outliner). Pieces matching no side are public.
- **VASSAL mask/hide traits** (`Obscurable`, `Hideable`) are respected: when active, the piece shows its mask (or nothing) to players who don't hold its owning side (call: ownership by our side rule, not VASSAL's player-side strings).
- **Phase panel:** during another side's phase a player sees only `{Side} is playing {Phase}`, no decisions, formula windows or cheat sheet.
- **Log:** entries about hidden things are **redacted** for the other side, e.g. `Blue ledger changed` (no numbers), `Blue rolled dice`; moves on owned maps are omitted.
- **Alerts / outliner (piece 5):** computed from the player's filtered view.
- Formats 1–3 load with everything public (today's behaviour).

### 3.3 The filtered view (core of enforcement)
One pure function produces what a player may see:

`PlayerView.For(GameState full, RulesPack pack, ModuleModel model, IReadOnlySet<string> heldSides) → GameState filtered`

It removes pieces on owned maps, applies masks, strips private ledger values and history, redacts log entries and dice. Every multiplayer mode renders from this filtered state. **It must never leak:** tests assert that serialising a filtered state for side Blue contains none of Red's private values.

## 4. Mode 1 — Hot-seat

- When the next phase's acting side is held by a different player than the current one, a full-window **handover screen** covers everything: `Pass to {player} ({sides})` with a **Continue** button. Nothing of either player's private data is visible behind it (windows are hidden, not just covered).
- After Continue, the UI shows that player's filtered view. A player switcher (status bar) lets players swap deliberately; it also shows the handover screen.
- Formula dialogs, batch grids and cheat sheets of the previous player close at handover.
- Hot-seat is honour-based (same PC); the filter is applied to the UI, not to memory (call).
- Without phases, the handover is triggered from the player switcher only.

## 5. Mode 2 — Live online

### 5.1 Roles
- **Host:** the user who starts the session. The host's Quartermaster holds the full game, is the only authority, rolls all dice, and can save.
- **Guests:** join with a code. They hold their chosen sides and see only their filtered view. Guests cannot save the shared game (they may export a "viewer copy" of their filtered state; call).

### 5.2 Flow
1. Host: **Game → Host Online…** → relay address (remembered), password → the relay returns a short **game code** (6 characters, no ambiguous letters). The host shares code + password.
2. Guest: **Game → Join Online…** → relay address, code, password, player name. The guest must already have the same module file (checked by hash); the **rules pack is sent by the host** automatically.
3. Guest picks a free side (or the host assigns). The host sees who is connected (status bar + players list).
4. Play: every guest action becomes a **command request** sent to the host. The host validates it (the guest holds the acting side, or the action is allowed outside the sequence like moving own pieces; call: guests may only change pieces/ledgers of sides they hold), applies it, and sends every player their **updated filtered view**.
5. **Chat:** a chat box in the log panel; chat lines are logged commands (`LogKind.Chat`), so they are saved with the game.
6. **Undo / step back:** host only. Everyone sees the `Stepped back…` line.
7. **Disconnects:** a guest who drops can rejoin with the same code/password/name and gets the current view. If the host drops, guests see "Host disconnected" and the game pauses; the host can resume from a save and re-host with the same code while the relay room lives.

### 5.3 What travels (call)
- Host → guest: after each accepted action, the guest's **filtered snapshot** (compressed). A builder may switch to deltas later only if tests prove the guest's state equals `PlayerView.For` after every step.
- Guest → host: command requests (the same `GameCommand` records), chat lines, side choice.
- Guests never receive the full state, private rolls, or other sides' pending runs.

### 5.4 End-to-end encryption (call)
All game payloads are encrypted between players with a key derived from the game password (PBKDF2-SHA256, AES-GCM). The relay forwards opaque bytes and **never sees the game**. The relay also authenticates rooms by a password hash so strangers cannot join.

## 6. Mode 3 — Turn files

- **Game → Send Turn…** writes a `.qturn` file: the game at the start of this turn (or a hash of it, if the receiver has it), plus every command made since, plus who it is for. Format JSON, gzip; version 1.
- **Opening a turn file** checks module hash and game identity, then **replays the sender's turn step by step**: a replay bar with **Next**, **Play all**, **Stop**, showing each log line as it applies. After the last step the receiver plays their own turn.
- Hidden information in turn files is **hidden on screen, not enforced** (the file holds the whole game; same as VASSAL). Documented plainly (call).
- **Send via my VPS (optional):** the relay also keeps a **mailbox** per game: `Send Turn` can upload the (encrypted) file; the receiver's Quartermaster checks on game open and every few minutes, and shows **"New turn waiting from {player}"** in the alert bar. Files expire after 30 days.

## 7. The relay (any server; Docker first)

- A new project `Quartermaster.Relay`: a small cross-platform .NET console program (no WinForms) that runs anywhere .NET runs — Linux, Windows, macOS, x64 or ARM64.
- **Docker is the main way to run it** (user's request, 2026-11-07, so it isn't tied to Ubuntu): the repo ships a multi-stage `Dockerfile` (SDK image builds, small ASP.NET/runtime image runs, non-root user) and a `docker-compose.yml`. One volume holds the config file, the certificate and the mailbox; one port is published. `docker compose up -d` starts it; restart policy `unless-stopped`. Images build for `linux/amd64` and `linux/arm64`.
- **Without Docker** it still runs as a plain program (self-contained publish), e.g. as a systemd service on Linux or a scheduled task/service on Windows.
- Setup guide `docs/relay-setup.md`: Docker steps first (install Docker, copy the folder or clone, set the port and optional certificate path in the config, `docker compose up -d`, open the port in the firewall, read the fingerprint from `docker compose logs`), then the plain-program steps for Linux (systemd) as the alternative.
- **Transport (call):** WebSocket over TLS on one configurable port (default 47820). Certificates: either the admin points it at a Let's Encrypt certificate, or it generates a self-signed one on first run and prints its fingerprint; clients **pin** the fingerprint on first connect (trust on first use) and warn loudly if it changes.
- **Rooms:** created by a host, identified by game code, protected by a password hash; members are the host and guests. The relay only routes messages between room members. Idle rooms close after 24 hours.
- **Mailbox:** per game code, per recipient player; stores encrypted blobs; size limit 20 MB per file; 30-day expiry.
- **Hardening:** maximum message size, rate limits per connection, no logging of payloads, config file for port/cert/limits.
- Tests run the relay in-process.

## 8. Formats

- Rules pack format 4: `PropertyDef.Visibility`, `HolderDef.SideId`, `MapOwners`, `FormulaDef.PrivateRolls`.
- Save format 7: player list. Older saves load as solo (one player holding all sides).
- `.qturn` version 1 (§6).
- Network protocol version 1, negotiated at join; a client with another version is refused with a clear message.

## 8a. Decisions settled after the builder check (all calls)

1. **Guests send intents; the host computes.** Online, a guest never sends computed results (`LedgerDelta`, `RollDice` results, `QueueRuns`, `ResolveRun`). It sends **intents**: move these pieces; run formula X on unit Y with these typed values (or "calculate"); manual ledger edit with note; roll these dice for this purpose; tick checklist i; acknowledge warning i; Next Phase; hide alert; chat; notes. The host turns each intent into commands with the full game state (exactly as if the host had done it locally, including fully-automatic triggers and dice) and replies with the filtered result. Hardcore checking of typed values happens on the host; the guest's formula dialog shows the host's verdict.
2. **Hidden inputs.** In a guest's filtered state, hidden values are **absent** (not zero). Formula dialogs show such inputs as `hidden — the host calculates`; Calculate and Apply go to the host. Alerts, outliner totals, inventory totals and breakdowns cover **visible units only**, and say so in their tooltip (`visible units only`).
3. **Guest state is a pure snapshot.** Guests have no undo history and no command log, only the redacted log text. Their Edit → Undo / Step Back are disabled (`Only the host can undo`). The host's undo works as in piece 4; guests see the `Stepped back…` line (redacted counts are fine). **Viewer copy** export writes the guest's filtered snapshot as a read-only save that opens in a viewer mode and cannot be continued. Hot-seat undo is global (any player, honour-based).
4. **The host is also a player.** The host's own screen shows the filtered view of the sides the host holds, like hot-seat (filter on the UI, full state in memory). Guests trust the host; this is stated in the Join dialog (`The host's copy runs the game`).
5. **What a guest may do.** Allowed: intents on pieces, units and holders of sides they hold, or that belong to no side; chat; dice-tool rolls; their own view toggles (stack expand, trails — applied locally, not sent). Allowed only while a side they hold is acting (when phases exist): tick, acknowledge, Next Phase. Host only: undo / step back, set turn, difficulty, hide alert, player list, notes (call: notes are shared and edited by the host). A move whose pieces include another side's piece, or that stacks onto another side's piece, is refused with a message.
6. **Two independent secrets from one password.** Room authentication sends the relay a verifier derived with PBKDF2 using salt `qm-auth:{code}`; the end-to-end key is derived with a different salt `qm-key:{code}` and never leaves the clients. On first join the host gives each guest a **rejoin token** (random, stored locally per game code); rejoining a seat needs that token, so one guest cannot take another's seat.
7. **Rules pack is sent whole.** The host sends its rules pack to guests (the user's own content, shared between friends); it is not stripped, so a guest could read the other side's settings in the Workbench. Documented; enforcement covers game state, not rules.
8. **Hot-seat start.** Loading a hot-seat save opens the handover screen for the player holding the acting side (or the first player without phases). App start with no game shows nothing. Handover hides every Quartermaster window, docked or floating.
9. **Turn files carry the whole game.** Saves get a `GameId` (GUID, save format 7). A `.qturn` holds the full game at the start of the sender's turn, the sender's commands (dice results included; replay never rerolls) and the end-state hash. Opening checks the module hash and `GameId`; if the receiver's current game for that id is newer than the file's start (both players moved), it warns `This turn starts from an older position than your game` and offers **Open anyway** (replaces) or **Cancel**.
10. **Edits that break visibility.** Visibility entries naming a deleted side or map are ignored at runtime and flagged by the validator. The player list cannot be edited during a live session except by the host's **Players** dialog (assign free sides, remove a disconnected guest). `MapOwners` also hide owned org displays from the outliner and alert matches of the other side (through `PlayerView`).

## 9. Build order

Three parts, each usable on its own, built and reviewed in order:

1. **6a — Base + hot-seat:** players/sides, visibility rules and Workbench tab, `PlayerView` filter, hot-seat handover, player switcher.
2. **6b — Live online:** relay program + setup guide, host/join, command requests, filtered snapshots, chat, reconnect, encryption.
3. **6c — Turn files:** Send Turn / open with step replay, mailbox on the relay, "New turn waiting".

## 10. Goals, non-goals, anti-goals

**Goals:** the three modes; enforced hidden information online; no router setup; a relay the user can run in Docker on their VPS (or any server) with a one-page guide.

**Non-goals:** roles within a side; more than 6 players; spectators; voice; matchmaking or a public server; an AI opponent; cryptographic hidden information in turn files; the VPS acting as referee (host authority instead).

**Anti-goals:**
- **A guest's copy ever holding another side's private data** online.
- Desync: a guest's view that differs from `PlayerView.For(host state)`.
- The relay reading or storing game contents in plain form.
- Any change that breaks solo play, undo, or existing saves.
- Real-game content anywhere.

## 11. Testing

- `PlayerView`: per setting, plus a leak test that serialises filtered states and searches for every private value/name.
- Hot-seat: handover triggers per phase/side, windows closed, switcher.
- Online: relay in-process with a host and two guests; command requests accepted/rejected by side; filtered snapshots equal `PlayerView.For`; chat; reconnect; host drop; wrong password; protocol version mismatch; encryption round trip; relay never sees plaintext (assert on relay buffers).
- Turn files: write/read round trip, step replay equals the sender's end state, wrong module/game refused, mailbox upload/download/expiry.
- Relay: limits, room expiry, mailbox expiry, cert pinning change warning.

## 12. Left to the builder

- WebSocket/TLS library choices within .NET 10.
- Exact message shapes and compression.
- UI layout of the host/join dialogs, handover screen, replay bar and chat box (classic gray look).
- Game code alphabet and length (6 characters, unambiguous).
