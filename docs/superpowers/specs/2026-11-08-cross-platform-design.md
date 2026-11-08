# Quartermaster on macOS and Linux — design

Date: 2026-11-08. Status: approved in chat, awaiting written-design review.

## Intent

The user asked (earlier session) for working macOS and Linux binaries. Today only the
relay and the pure libraries (Vassal, Rules, Core, Render, Net) are cross-platform;
the desktop app is WinForms (~25k lines in `src/Quartermaster.App`) and uses
Windows-only pieces. The user and the people they play with use a mix of Windows,
M1 Macs and Linux, and every one of them must be able to play a full game.

## Goals

- **Full parity** (user: "the whole app should work properly"). Every feature works
  on Windows, macOS and Linux: board/map, pieces, Ledger and tabs, Workbench, phases,
  undo, hot-seat, online lobbies, turn files, search, PDF rulebooks, OCR, mailbox
  password protection, updater, launcher, accessibility features.
- **One UI codebase**: rebuild the UI once in **Avalonia** for all three OSes
  (user chose option 2).
- **Windows looks the same** (user's requirement): Avalonia styled to match today's
  classic grey WinForms look — same layout, same windows, same buttons, Tahoma/
  Segoe-like fonts, same colours. A Windows player should barely notice.
- Saves, turn files, settings, ledger tabs, scan caches carry over unchanged.
- Mixed-OS multiplayer works (Windows host, Mac and Linux joiners, and every other
  combination), including turn files moved between OSes.

## Targets

- Windows x64 (as today).
- macOS **Apple Silicon only** (osx-arm64, M1 and later). Intel Macs: non-goal.
- Linux **x64**, glibc distros: Ubuntu, Mint, Debian, Fedora, Arch (and other popular
  defaults). ARM Linux desktop: non-goal. (The relay keeps its existing linux-arm64
  build.)

## Packaging (user agreed: installers plus portable)

- Windows: `Quartermaster-Setup-<ver>.exe` — per-user install (no admin) into the
  user's local app data, Start-menu shortcut, uninstaller. Plain zip kept (portable).
  No .msi (needs admin, aimed at managed networks).
- macOS: `.dmg` with drag-to-Applications; plain zip of the `.app` too. **Unsigned,
  not notarised** (user: live with the warning); docs explain right-click → Open /
  `xattr -d com.apple.quarantine`.
- Linux: **AppImage** (all distros, self-updates), **.deb** (Ubuntu/Mint/Debian),
  **.rpm** (Fedora), plain tar.gz. No Arch/AUR package (AppImage covers Arch).
- All unsigned; SmartScreen/Gatekeeper warnings documented.

## Updater on each package type

- Windows zip and setup.exe install, macOS `.app`, Linux AppImage and tar.gz:
  self-update via the existing journalled swap helper (ported to run on all three).
- Linux .deb/.rpm: installed system-wide, so no silent self-update. The update bar
  downloads the new package and hands it to the system installer (opens it), telling
  the user what is happening.
- The updater picks the release asset matching its OS and package type.

## Windows-only parts and their replacements (same behaviour everywhere)

- WinForms UI → Avalonia.
- SkiaSharp.Views.WindowsForms → Avalonia's Skia surface (Render models reused).
- WebView2 PDF viewer → a cross-platform PDF renderer (e.g. PDFium-based) inside the
  app, so rulebook viewing, search hit highlighting and page jumps behave the same.
- Windows.Media.Ocr / Windows.Data.Pdf (`Quartermaster.Ocr`) → a cross-platform OCR
  engine (e.g. Tesseract) used on all three OSes, so results are the same on every
  machine and the scan cache stays portable. Existing caches keep working.
- DPAPI in `Render/Turns/TurnIdentity.cs` → OS keychain (Windows Credential/DPAPI,
  macOS Keychain, Linux Secret Service/libsecret), with a passphrase fallback when no
  keyring is available (headless or minimal Linux desktops).
- Updater helper (`net10.0-windows`) → plain `net10.0`, OS-aware process checks and
  restart (`.app` bundles, AppImage path).

## Rollout (my call)

- The WinForms app keeps shipping unchanged until the Avalonia app reaches parity.
- Then a single release switches all three OSes at once; Windows users are updated
  to the Avalonia build by the normal updater.
- Built in stages, each mergeable and tested: (1) app shell + board/map + pieces,
  (2) Ledger + Workbench + dialogs, (3) online + turn files + hot-seat, (4) PDF/OCR/
  search, (5) passwords + updater + installers/packaging.

## Testing

- Automated tests run on Windows, macOS and Linux (CI on GitHub Actions runners).
- Linux checked locally via WSL (with a GUI) by us.
- The user has an M1 Mac and tests each Mac build with a short checklist we supply.
- Smoke/screenshot flags keep working on the Avalonia app.

## Non-goals

- Intel Macs, ARM Linux desktop, Arch/AUR package, code signing / notarisation,
  mobile, a browser version.

## Anti-goals

- Windows players see a different-looking app.
- A feature that works on Windows but is missing or worse on Mac/Linux at the switch
  release.
- Breaking existing saves, turn files, ledger tabs, or online play between versions.
- Any real-game (CNA) content in tests, screenshots or docs.

## Settled after builder check

User's decisions:
- **Look on Mac/Linux**: the same classic grey look as Windows on all three OSes
  (one theme). Only shortcuts adapt: Cmd instead of Ctrl on macOS (shortcut
  parser/display maps the primary modifier per OS). No native macOS global menu bar.
- **Features during the port**: new features may keep landing while the port is
  built, **as long as every feature in a GitHub release ships for all versions in
  that release**. So: any feature added to the WinForms app during the port is also
  added to the Avalonia app (it joins the parity checklist), and the switch release
  contains it on Windows, macOS and Linux. Keep new UI logic in the shared libraries
  (Core/Render) wherever possible so it is written once.

My calls:
- **"Looks the same" check**: the user judges side-by-side screenshots (WinForms vs
  Avalonia, same `--screenshot-*` flag, same fixture) at the end of each stage. No
  pixel-diff harness.
- **Parity checklist**: a file in the repo listing every window, command and feature
  of the WinForms app (enumerated from the code), ticked per OS. The switch release
  happens only when every row is ticked on all three OSes.
- **Building**: GitHub Actions builds and tests on windows/macos/ubuntu runners on
  push, and builds the release packages (.dmg needs a Mac runner). Creating or
  publishing a GitHub Release still happens only when the user asks.
- **OCR**: first compare Tesseract against Windows.Media.Ocr on the existing invented
  OCR fixtures. If Tesseract is as good, use it everywhere; if worse on Windows,
  Windows keeps Windows OCR and Mac/Linux use Tesseract (per-OS engine, same
  interface). English language data ships with the app; other languages are
  downloadable from Settings, with a clear message when a language is missing.
- **Mailbox password**: stored via the OS secret store (DPAPI on Windows as today,
  Keychain on macOS, Secret Service on Linux). If Linux has no secret store, the app
  asks once per session and stores nothing. A saved password stays on the machine it
  was set on (as today); existing Windows `turns/{GameId}.json` files keep working.

## Left to the builder

Exact libraries for PDF rendering, OCR and keyring (within the constraints above);
installer tooling (e.g. Inno Setup, create-dmg, appimagetool, nfpm/fpm); how the
classic look is themed; stage boundaries in detail.
