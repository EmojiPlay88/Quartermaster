# Launcher and Lobbies — Design

*Date: 2026-11-07. Builds on pieces 1–6a as merged to `master` and on the multiplayer spec (`2026-11-07-piece6-multiplayer-design.md`). This document adds a launcher in front of the game window and replaces 6b's "Host Online / Join Online by code" dialogs with lobbies. Where it disagrees with the multiplayer spec on how players find and join games, this document wins.*

## 1. Intent

Today Quartermaster opens straight into the game window and you open a module from the File menu. The user wants a front door like VASSAL's Module Manager: open Quartermaster, **choose a module**, then either go to an **online lobby** or **play offline**, and **set up a new game** from there.

What the user said (condensed):
- First thing on game night: **choose a module**.
- Then a **lobby screen**, then **making and setting up the new game**.
- The lobby lists **only other people's lobbies**, plus buttons to **create my own lobby** and **host a saved game**.
- Solo and hot-seat start from a separate **Play offline** button.
- The mockup flow (module list → online lobby or play offline → new game setup) was accepted as shown, including the assistant's guesses: rules pack chosen on the module list, a lock icon on password lobbies, "Continue" for the latest autosave, turn files opened from Play offline.

Everything else below is the assistant's call, marked "(call)" where a different answer is reasonable.

## 2. Content boundary (unchanged, hard)

No real-game content. The module list shows the user's own module files; nothing from a module is shipped or uploaded. Lobby listings carry only what the host types (lobby name, player names) plus side names from the rules pack and the module's hash and title.

## 3. Screens

### 3.1 Module list (the launcher's first screen)
- A window that opens on start when no module is given on the command line (call: command-line paths and smoke flags keep working and skip the launcher).
- List of the user's modules: title (from the module), version, file name, last played. Remembered in Settings (paths only); missing files shown greyed with "(file not found)".
- **Add module…** (file dialog), **Remove** (from the list only, never deletes the file).
- Under the list: the **rules pack** used with the selected module (remembered per module; "change…" opens a file dialog; "(none)" allowed).
- Two big buttons: **Online lobby ▶** and **Play offline ▶**.
- Double-click a module = Play offline (call).
- The launcher closes when a game window opens and returns when the game window is closed (call: one window at a time; File → **Back to launcher** in the game window).

### 3.2 Play offline
- For the selected module: **Continue** (newest autosave, with turn and phase), recent saved games for this module (remembered paths), **New game…**, **Open saved game…**, **Open turn file…** (turn files arrive in 6c; until then the button is hidden — call).
- **New game…** opens the setup (§3.4) in offline mode.

### 3.3 Online lobby (arrives with 6b)
- Header: relay address (remembered; editable) and connection state.
- Table of **other people's lobbies for this module** (same module hash): lobby name, host name, free sides, players (n/max), lock icon when it has a password. The user's own lobby is not listed (call: it shows in the game window instead).
- Buttons: **Join…** (asks for the password if locked, then side choice), **Create lobby…** (setup §3.4 in online mode), **Host a saved game…** (pick a save, then name/password; seats come from the save's player list), **Refresh** (also auto-refresh every 10 s).
- Lobbies whose host has left disappear.

### 3.4 New game setup (shared)
One dialog for offline and online, replacing today's setup chooser:
- Online only: **Lobby name**, **Password** (optional; empty = open lobby).
- **Setup**: the module's predefined setups or a blank game (as today).
- **Difficulty** (as today).
- **Players / sides** (6a's Players page): offline = hot-seat players or solo; online = the host's own sides plus **open seats** for the remaining sides.
- **Start** opens the game window; online also publishes the lobby.

## 4. What changes in multiplayer (6b)

- **Lobby listing on the relay.** A host publishes a small **public listing**: lobby name, host name, module hash, module title, side names, free sides, player count, locked flag, protocol version. The relay keeps listings while the host is connected and returns them to clients that ask for a module hash. Listing data is **not** encrypted (it must be readable to be listed); game data stays end-to-end encrypted as before.
- **Joining by lobby** replaces typing a game code. Internally the lobby still has a code, used as the room id and as the salt for the password-derived keys.
- **Open lobbies (no password):** the end-to-end key is derived from the lobby code alone, so the relay could in principle read an open lobby's game. Stated in the Create lobby dialog: `Without a password, the relay can read this game.` (call).
- **Rate and size limits** apply to listings too (name ≤ 40 chars, at most 20 lobbies per IP, listing refreshed by the host every 30 s, expires after 90 s without refresh).
- Host Online / Join Online dialogs from the 6b plan are dropped; the game window keeps **Game → Leave online game** and **Game → Players…** (seats).

## 4a. Decisions settled after the builder check (all calls)

1. **Scope of the first build:** the launcher's offline parts only (§6 step 1). The **Online lobby ▶** button is shown greyed with the tooltip `Online play arrives with the online update.` until 6b. The 6b plan is revised separately to add lobbies.
2. **Lobby code in the listing:** the relay generates the code when the lobby is created and includes it in the listing. Joiners use it as the room id and as the salt for both password-derived secrets (unchanged from the multiplayer spec). The relay checks the password **verifier** on join, so a wrong password is refused by the relay before any game data moves. An open lobby uses the verifier and key of the empty password — the relay can read such a game, as the warning says.
3. **Modules are identified by their SHA-256** (as autosaves and `RulesMemory` already are). The launcher caches each module's SHA in Settings together with the file's size and last-write time and re-hashes only when those change. The rules pack per module reuses `RulesMemory` (keyed by SHA). If a module file is replaced by a new version, its SHA changes: **Continue** shows only autosaves of the current SHA; recent saved games are listed by path regardless and opening one made with another module version gives today's "different module" message.
4. **Startup:** with no module on the command line, the launcher **always** shows. The existing "reopen last module" setting becomes **"Select the last module in the launcher"** (default on) — it preselects instead of skipping. Command-line paths and smoke flags skip the launcher as today.
5. **Window lifecycle:** the program loops: launcher → game window → (game window closes via **File → Back to launcher**, with the usual unsaved-changes prompt) → launcher again. Closing the game window with its close box exits the app (as today). **File → Open Module** keeps working in place inside the game window. A game window opened from the command line also has **Back to launcher**.
6. **Title and version:** read from the module's metadata entry only (no full parse); fall back to the file name when missing.
7. **Own lobby:** the client hides listings whose code is its own hosted lobby; otherwise every listing shows (two people behind one IP see each other's lobbies).
8. **Host a saved game:** the save's module SHA must match the selected module (else `This save belongs to a different module`); the host picks their own sides on the Players page; every other side becomes an open seat, regardless of who held it in the save.

## 5. Goals, non-goals, anti-goals

**Goals:** a VASSAL-like front door; one place to pick a module and its rules pack; offline play with Continue and recent saves; online lobbies per module; one shared new-game setup.

**Non-goals:** downloading modules from the internet (VASSAL's module library); user accounts or profiles; friends lists; chat in the lobby before joining (chat starts in the game); spectators; a public relay run by the project.

**Anti-goals:**
- Breaking today's direct ways in: command-line module paths, File → Open Module, smoke flags.
- Lobby listings that reveal anything from inside the game (only the fields in §4).
- Losing the user's module or rules-pack choices between runs.

## 6. Build order

1. **Launcher (offline)** — module list, rules pack per module, Play offline (Continue, recent saves, New game, Open saved game), the shared setup dialog, Back to launcher. Built **before** 6b, usable on its own.
2. **6b with lobbies** — the existing 6b plan, changed so the relay also stores lobby listings and the App gets the Online lobby screen instead of Host/Join dialogs.
3. **6c turn files** — adds "Open turn file…" to Play offline.

## 7. Testing

- Launcher model (UI-free): module list persistence, missing files, per-module rules pack, recent saves per module, Continue picks the newest autosave for that module.
- Setup model: offline/online field rules, seats from the players page.
- Smokes: launcher screenshot, Play offline screenshot, setup screenshot; command-line paths skip the launcher.
- 6b additions: relay listing publish/refresh/expiry/limits; lobby list per module hash; join by lobby; open-lobby warning.

## 8. Left to the builder

- Layout details in the classic gray look (as in the accepted mockups).
- How "title" and "version" are read from the module (`moduledata` / build file).
- Sorting of the module and lobby lists (call default: last played, then name).
