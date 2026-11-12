# Port stage 5: launcher, updates, hot-seat, online play, turn files — design

*2026-11-11. Branch `port-stage5` (from master 7bd4feb, after PR #4). Status: draft for user approval.*

## Intent

Stage 5 brings the rest of the WinForms app's *game lifecycle and multiplayer* to the cross-platform Avalonia app
(`Quartermaster.Desktop`). After stage 5, someone on macOS or Linux can:
- start from the launcher;
- play hot-seat with friends at one screen;
- host or join an online game through the relay;
- send and open turn files;
- update the app in place.

All of it should behave as the WinForms app does today. The authority is the approved cross-platform design
(`docs/superpowers/specs/2026-11-08-cross-platform-design.md`, "full parity") and the rows marked 5 in
`docs/port/parity.md`. Behaviour reference: the WinForms files named below, in `src/Quartermaster.App`.

The user skipped the design questions. Everything not already decided in the cross-platform design is **Claude's call**,
marked as such.

## Already decided (cross-platform design, user-approved 2026-11-08)

- Full parity with the WinForms app, same look, same files (saves, turn files, settings, layout), Cmd on macOS.
- **Updater per package type:**
  - The Windows zip, macOS `.app`, Linux AppImage and tar.gz self-update with the existing journalled swap helper, ported to
    plain `net10.0` with OS-aware process checks and restart (`.app` bundles, AppImage path).
  - Linux `.deb`/`.rpm` hand the downloaded package to the system installer.
  - The updater picks the release asset for its OS and package type.
- **Mailbox password:** stored in the OS secret store: DPAPI on Windows as today, Keychain on macOS, Secret Service on
  Linux. With no Linux secret store, the app asks once per session and stores nothing. Existing Windows
  `turns/{GameId}.json` files keep working, and a password stays on the machine it was set on.
- The WinForms app keeps shipping until the Avalonia app reaches parity. Release creation happens only when the user asks.

## Split (Claude's call)

Stage 5 covers about 20 WinForms surfaces. It's built as **four sub-stages, in this order**. Each has its own plan, its own
branch cut from master and its own PR, merged before the next starts, as 4a/4b/4c were.

| Sub-stage | What | WinForms reference | Parity rows |
|---|---|---|---|
| **5a: app lifecycle** | Launcher (module list, Add/Remove, rules pack per module, Play offline: Continue, recent saves, New game…, Open saved game…); File > Back to launcher; launcher ↔ game window loop; the 4b "Select the last module in the launcher" setting made live. Interface Tour overlay (Help > Interface Tour and first game open). Help > About. Update *screens* only: Check for updates, update bar (What's new / Update now / Later / Skip this version), Update dialog, What's New dialog, the "Check for updates on start" setting stored as today — all wired to the test feed only (see "Updater split"). | LauncherForm, PlayOfflineForm, LauncherShots, MainForm.Tour, TourOverlay, AppUpdates, UpdateBar, UpdateDialog, WhatsNewDialog | 162, 189, 207, 212 (screens; install in stage 6), 290 |
| **5b: hot-seat** | Players editor: setup page, Settings > Game > Players (replacing the grey 4b label), Game > Players…. Handover screen ("Pass to Anna (Red)"), switching the current player in the status bar, and the player view filter applied to every Desktop window. | PlayersEditor, HandoverForm, SidesWatch | 182 (Players…), 250, 291, 294 |
| **5c: online play** | Online lobby (from the launcher), Join, Host saved game, sides panel, Seats and Side choice dialogs, chat box (online only), formula input masking, guest read-only Workbench, the alert-bar notice chip made clickable, Leave Online Game, rejoin. | OnlineLobbyForm (+ JoinDialog, HostSavedDialog, HostSidesPanel), SeatsDialog, SideChoiceDialog, ChatBox | 35, 56, 109, 182 (Leave), 292, 293 (Seats, SideChoice) |
| **5d: turn files** | Game > Send Turn…, Send wait screen, Who are you, Mailbox options, File > Open Turn File…, step replay with the Replay bar, Export Viewer Copy…, "New turn waiting" alert and poll. The Workbench is closed before a received turn replays. The mailbox password moves from DPAPI-only to the OS secret store. | MainForm.Turns, SendTurnDialog (+ SendWaitForm), WhoAreYouDialog, MailboxOptions, ReplayBar, `Render/Turns/TurnIdentity.cs` | 62, 155, 158, 182 (Send Turn), 211, 293 (rest) |

Why this order: 5b's players and per-side view are what 5c and 5d build on. 5a has no multiplayer dependency and gives the
app its real front door, so it comes first.

## Goals

1. Every parity row marked 5 is ticked for Windows by the end of 5d, with Mac and Linux checklists written per sub-stage.
2. Same files as WinForms: a game started, saved or sent in one app opens in the other, and the relay protocol is
   unchanged, so a WinForms host and an Avalonia guest can play together.
3. Each sub-stage is mergeable on its own and leaves the app working.

## Non-goals

- **Installers and packaging** (setup.exe, `.dmg`, AppImage, `.deb`/`.rpm`) and the switch-over release. That's a later
  stage (Claude's call: "stage 6, packaging and release").
  - Stage 6 also owns installing updates (see "Updater split"). Stage 5 ports only the update screens, which run on the test
    feed.
- No relay server changes, no protocol changes, no new save, turn-file or rules-pack formats.
- No changes to the WinForms app except pure refactors that move shared logic into Render, as in 4b.

## Anti-goals

- An update, download or network call without a click, apart from the "check for updates on start" setting and the online
  lobby the user opens. Both behave as in WinForms.
- Showing a player another side's hidden information anywhere in the Desktop app (the PlayerView rules of piece 6a).
- Breaking compatibility with WinForms saves, turn files or relay peers.
- Leaving a guest able to edit rules, or replaying a received turn while the Workbench has unsaved edits.

## Design calls (Claude's)

- **Reuse first.** The Render models already shared with WinForms (`Launcher/`, `Update/`, `Online/`, `Turns/`,
  `HotSeat.cs`, `TourSteps.cs`, `AppVersion.cs`) are used as they are. Where a WinForms form holds logic, that logic moves to
  Render first, so both apps' tests cover it, as the 4b Keyboard model did.
- **Updater split (decided after the builder check).** The install side of updating is tied to how stage 6 packages the
  app, so it moves to **stage 6**. That covers:
  - the Avalonia exe name on Windows and the WinForms → Avalonia switch-over zip;
  - macOS and Linux asset names and the release-script changes;
  - porting the swap helper to `net10.0`, including replacing a `.app` bundle or an AppImage as one unit;
  - checking the staged version without a Win32 FileVersion;
  - the `.deb`/`.rpm` hand-off.

  In 5a:
  - The Desktop app gets the update **screens** and their Render logic (`Update/`, `AppVersion`).
  - Its update check runs **only against `QUARTERMASTER_UPDATE_FEED`**, the test feed. Against the real GitHub feed, the
    Desktop app never checks, offers nothing and downloads nothing. That includes "Check for updates on start", which is
    stored but has no effect until stage 6.
  - "Update now" on the test feed runs as far as the verified download and then stops with a plain message, without
    starting the helper: `Installing updates comes with the packaged release (stage 6).` (Claude's call.)
  - The Desktop app's update state (Updates folder, skip-version, markers) lives under `AppDataRoot.Sub("Updates-desktop")`.
    That keeps the WinForms updater's state untouched until stage 6 decides the switch-over.
  - `build/release.ps1` and `Quartermaster.Updater` are not changed in stage 5.
- **Plans.** Only 5a is planned now. The plans for 5b–5d are written after the previous sub-stage merges, on a branch cut
  from the new master.
- **Secret store (5d).** Calls go straight to the OS APIs (Keychain via Security.framework, Secret Service via libsecret
  over D-Bus, through P/Invoke to `libsecret-1`), with no new NuGet package. If libsecret is missing, the password is asked
  once per session, as the design says.
- **Look.** Classic grey, matching the existing Desktop windows. Each WinForms dialog keeps its labels and texts verbatim.
- **Process.** As in stage 4: per sub-stage, a plan, then subagent-driven tasks with task reviews, an opus final review, one
  fix wave, a PR and the user's OK to merge. Headless tests only, and a Mac checklist per sub-stage.

## Constraints

- C# .NET 10, Avalonia 12.1.5. No new NuGet packages unless a sub-stage plan justifies one and the user approves it.
- Public repo: no personal paths, no real-game content, no secrets in tests.
- Tests never open visible windows and never touch the real relay, GitHub or the user's profile. Use the in-process relay,
  test feeds (`QUARTERMASTER_UPDATE_FEED`) and temp layout roots.
- Ask before every push, merge and release.

## Left to the builder (per sub-stage plan)

- Task boundaries and the exact order inside each sub-stage.
- Which WinForms logic moves to Render, file by file.
- Headless test strategy for multi-window flows (launcher ↔ game window, handover).
