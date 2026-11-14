# Port Stage 6b-1: macOS App Bundle and Drag-to-Applications DMG Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** CI's macOS job turns the osx-arm64 publish into a real `Quartermaster.app` (ad-hoc signed) and a `Quartermaster-<v>-osx-arm64.dmg` whose window shows the app, an arrow and an Applications shortcut, so the user installs by drag and drop; plus the update zip `Quartermaster-<v>-osx-arm64.zip` the 6a updater expects.

**Architecture:** Two bash scripts under `build/macos/` (bundle, then dmg + zip) run on the `macos-latest` runner after the existing publish step. The bundle puts the whole publish folder in `Contents/MacOS/` (where 6a's `InstallLayout` and staging already look for `quartermaster-package.json` and `quartermaster-files.txt`), adds `Info.plist`, signs every Mach-O ad hoc, then the bundle. The dmg is laid out with `dmgbuild` (writes Finder's `.DS_Store` directly, no Finder scripting, reliable on CI) over a committed background picture. CI verifies the results and uploads them as one artifact.

**Tech Stack:** bash, `codesign`, `plutil`, `hdiutil`, `ditto` (all on macOS runners), `dmgbuild` (pip, pinned, CI build tool only), Pillow (local one-off to draw the background; output committed), xUnit for one Render test against the real zip.

**Spec:** `docs/superpowers/specs/2026-11-08-cross-platform-design.md` ("Packaging": macOS `.dmg` with drag-to-Applications; plain zip of the `.app` too; unsigned, not notarised; docs explain right-click → Open / `xattr -d com.apple.quarantine`). Stage 6a plan `docs/superpowers/plans/2026-11-14-port-stage6a-updater-install.md` fixes the asset name `Quartermaster-<v>-osx-arm64.zip` holding `Quartermaster.app/`, and the marker kind `mac-app`. The rest of 6b (Windows setup.exe, AppImage/.deb/.rpm, `release.ps1`, CI release job) is a later plan.

## Design calls (Claude's)

- **Bundle layout:** `Quartermaster.app/Contents/Info.plist`, `Contents/MacOS/` = the entire publish folder unchanged (exe `Quartermaster`, natives, tessdata, licences, `Quartermaster.Updater`, marker, file list), `Contents/Resources/` empty for now. Keeping everything in `MacOS/` means `AppContext.BaseDirectory`, the 6a marker location and the file list all stay valid with no code change.
- **Info.plist:** `CFBundleIdentifier` `io.github.emojiplay88.quartermaster`; `CFBundleName` and `CFBundleDisplayName` `Quartermaster`; `CFBundleExecutable` `Quartermaster`; `CFBundlePackageType` `APPL`; `CFBundleShortVersionString` and `CFBundleVersion` = the version; `LSMinimumSystemVersion` = the minimum macOS listed for .NET 10 in dotnet/core `release-notes/10.0/supported-os.md` (builder looks it up; write the value and its source in the script comment); `NSHighResolutionCapable` true; `NSPrincipalClass` `NSApplication`; `LSApplicationCategoryType` `public.app-category.strategy-games`. No icon key yet (default icon; the user may supply one later).
- **Signing:** ad-hoc (`codesign --force --sign -`) every Mach-O file in `Contents/MacOS` (found with `file`), then the bundle itself; no `--deep`. Apple silicon refuses unsigned arm64 code, and the 6a updater copies the helper out of the bundle, so the helper must carry its own signature. Verify with `codesign --verify --strict --verbose=2`.
- **DMG:** volume name `Quartermaster <v>`, window 640x400, app icon at (160, 200), Applications link at (480, 200), icon size 128, background `build/macos/dmg-background.png` (+ `dmg-background@2x.png`, 1280x800): light classic grey (#d4d0c8 family, matching the app), a dark grey arrow from left icon to right icon, caption under the arrow "Drag Quartermaster to Applications". Format UDZO. Built by `dmgbuild` pinned to an exact version at least two weeks old (builder checks `pip index versions dmgbuild`).
- **Update zip:** `ditto -c -k --keepParent Quartermaster.app Quartermaster-<v>-osx-arm64.zip` (keeps modes; no `__MACOSX`). Each output gets `<file>.sha256` (`shasum -a 256`, the hash first on the line, as the 6a updater reads).
- **CI artifact:** one artifact `Quartermaster-macOS` with the dmg, the zip and both `.sha256`. The old `Quartermaster-Desktop-osx-arm64.tar.gz` artifact is dropped (the bundle replaces it).
- **Not here:** notarisation, Developer ID, custom icon, GitHub Release upload.

## Global Constraints

- No new NuGet packages. `dmgbuild` and Pillow are build tools only, never shipped.
- Public repo: no personal paths, no real-game content, noreply commit email, never commit `startapp.cmd`.
- Never run the app with a visible window on the dev machine. CI may run `Quartermaster --version` (no window).
- Asset names exactly: `Quartermaster-<v>-osx-arm64.dmg`, `Quartermaster-<v>-osx-arm64.zip`, each plus `.sha256`. Zip holds `Quartermaster.app/` at its top.
- `Contents/MacOS/quartermaster-package.json` must say kind `mac-app` and the release version.
- Ask before every push, merge and release.
- Work in worktree `.worktrees/s6b1` on branch `port-stage6b1` cut from master `9885e1b`.

## Review Focus

1. **Helper or a native library unsigned** — Apple silicon kills it ("killed: 9") the first time the updater runs it or the app loads it. CI step in Task 3: `codesign --verify --strict` on the bundle AND on `Contents/MacOS/Quartermaster.Updater` and every `.dylib`.
2. **Exec bit lost** somewhere between publish, bundle, dmg and zip — app or helper won't start. Task 3: run `--version` from the mounted dmg's app, and the Render test extracts the real zip through 6a staging and checks mode bits.
3. **6a updater rejects the real zip** (wrong top folder, marker kind, missing file list). Task 3: `Download_MacApp_FromRealBundleZip` runs 6a `UpdateInstaller.Download` against the CI-built zip.
4. **Dmg window empty or icons misplaced** because `.DS_Store` didn't apply. Task 2: CI mounts the dmg and checks `.DS_Store`, `.background/` and the `Applications` symlink exist; the user's checklist confirms the look.
5. **Version mismatch** between plist, marker and file names. Task 3 checks all three equal `dotnet msbuild src/Quartermaster.Desktop -getProperty:Version`.

---

### Task 1: App bundle script

**Files:**
- Create: `build/macos/make-app-bundle.sh`, `build/macos/Info.plist.in`
- Create: `build/macos/test-app-bundle.sh` (structure checks; runs on macOS CI, and with `--no-sign` on Linux CI too)

**Interfaces:**
- Produces: `build/macos/make-app-bundle.sh <publish-dir> <version> <out-dir> [--no-sign]` → `<out-dir>/Quartermaster.app`; exits non-zero with a plain message if `<publish-dir>/Quartermaster` or the marker is missing, or the marker's kind isn't `mac-app` or its version isn't `<version>`. `build/macos/test-app-bundle.sh <app> <version> [--no-sign]` → exit 0 when: plist passes `plutil -lint` (macOS only), plist keys equal the Design calls values, `Contents/MacOS/Quartermaster` and `Contents/MacOS/Quartermaster.Updater` are executable, marker kind/version right, file list present, and (unless `--no-sign`) `codesign --verify --strict` passes for the bundle, the helper and every `.dylib`.

- [ ] **Step 1:** Write `test-app-bundle.sh` first; run it against an empty folder → expect FAIL listing what is missing.
- [ ] **Step 2:** Write `Info.plist.in` (placeholders `@VERSION@`, `@MINOS@`) and `make-app-bundle.sh` (copy with `cp -Rp`, fill the plist with `sed`, sign per Design calls).
- [ ] **Step 3:** Locally (Git Bash on Windows) run both on a fake publish folder with `--no-sign` (a dummy `Quartermaster` file with mode 0755, dummy helper, a `mac-app` marker) → PASS; with a `win-zip` marker → bundle script refuses.
- [ ] **Step 4:** Commit `build(macos): app bundle script with ad-hoc signing`.

### Task 2: DMG and update zip

**Files:**
- Create: `build/macos/make-dmg-background.py` (Pillow; draws both PNGs), `build/macos/dmg-background.png`, `build/macos/dmg-background@2x.png`
- Create: `build/macos/dmg-settings.py` (dmgbuild settings; reads `app`, `background` from `defines`)
- Create: `build/macos/make-dmg.sh`

**Interfaces:**
- Consumes: `Quartermaster.app` from Task 1.
- Produces: `build/macos/make-dmg.sh <app> <version> <out-dir>` → `<out-dir>/Quartermaster-<v>-osx-arm64.dmg`, `<out-dir>/Quartermaster-<v>-osx-arm64.zip`, and their `.sha256`. Requires `dmgbuild` on PATH (clear message if not). After building it mounts the dmg read-only (`hdiutil attach -nobrowse -readonly -mountpoint <tmp>`), checks `Quartermaster.app`, the `Applications` symlink → `/Applications`, `.background/` and `.DS_Store` exist, runs `<mount>/Quartermaster.app/Contents/MacOS/Quartermaster --version` and checks it prints `Quartermaster <v>`, then detaches (always, via `trap`).

- [ ] **Step 1:** Run `make-dmg-background.py` locally (Pillow 12 is installed); look at the PNGs (Read tool) — grey background, arrow left→right, caption readable, nothing at the icon spots. Commit the script and both PNGs.
- [ ] **Step 2:** Write `dmg-settings.py` with the Design calls geometry; `make-dmg.sh` as above.
- [ ] **Step 3:** Commit `build(macos): drag-to-Applications dmg and update zip`. (Runs for real in Task 3's CI.)

### Task 3: CI wiring and the real-zip updater test

**Files:**
- Modify: `.github/workflows/cross-platform.yml` (macOS publish step and upload only; Windows and Linux jobs unchanged)
- Create: `tests/Quartermaster.Render.Tests/Update/MacBundleZipTests.cs`

**Interfaces:**
- Consumes: Tasks 1–2; 6a `UpdateInstaller.Download(IUpdateFeed, UpdateInfo, string updatesRoot, IProgress<double>?, CancellationToken)`, `FolderFeed`, `UpdateInfo` (Kind `MacApp`).
- Produces: CI macOS steps, in order after Test: install `dmgbuild==<pinned>` with `pip3 install --user` (or `pipx`); `VER=$(dotnet msbuild src/Quartermaster.Desktop -getProperty:Version)`; publish as today; `check-publish-natives.sh`; `make-app-bundle.sh publish/osx-arm64 $VER out`; `test-app-bundle.sh out/Quartermaster.app $VER`; `make-dmg.sh out/Quartermaster.app $VER dist`; then `QM_MAC_APP_ZIP=dist/Quartermaster-$VER-osx-arm64.zip dotnet test tests/Quartermaster.Render.Tests -c Release --no-build --filter MacBundleZipTests`; upload artifact `Quartermaster-macOS` = `dist/*` (`if-no-files-found: error`). Remove the old macOS tar.gz step and artifact.
  - `MacBundleZipTests.Download_MacApp_FromRealBundleZip`: returns early (passes) when `QM_MAC_APP_ZIP` is unset (so local and other-OS runs are no-ops — say so in a comment). Otherwise writes a FolderFeed (release.json in GitHub shape, tag `quartermaster-v<v>`, assets = the zip + its `.sha256`, real size) into a temp dir, parses with `PackageKind.MacApp`, calls `Download`, and asserts: `StagedDir` ends with `Quartermaster.app`; `<StagedDir>/Contents/MacOS/Quartermaster` and `.../Quartermaster.Updater` have the user-execute bit (`File.GetUnixFileMode`); `PackageFile.Read(<StagedDir>/Contents/MacOS)` is `mac-app` with the version from the zip name.

- [ ] **Step 1:** Write `MacBundleZipTests.cs`; run locally without the env var → passes as a no-op; with the env var pointing at a hand-made zip that lacks the marker → FAIL with the 6a refusal text (proves the test bites).
- [ ] **Step 2:** Edit the workflow as above.
- [ ] **Step 3:** Commit `ci(macos): build Quartermaster.app, dmg and update zip; test the zip through the updater`.
- [ ] **Step 4:** Ask the controller to push the branch (controller asks the user) and watch the macOS job; fix until green. Record the run id.

### Task 4: Docs

**Files:**
- Modify: `README.md` "Install / Update" (macOS: download the `.dmg`, drag to Applications; first open: right-click → Open, or System Settings → Privacy & Security → Open Anyway; or `xattr -dr com.apple.quarantine /Applications/Quartermaster.app`; updates install in place from the launcher)
- Create: `docs/port/stage6b1-mac-checklist.md` (get the artifact from the Actions run, open the dmg: window shows app, arrow, Applications; drag; eject; first open per README; Launchpad entry; Help > About version; menu bar shows "Quartermaster"; then run the 6a Mac checklist's update steps against this installed app instead of the hand-made bundle; uninstall = drag to Bin, and only if wanted remove the data folder `~/Library/Application Support/Quartermaster` (.NET 8+ maps `SpecialFolder.LocalApplicationData` there on macOS; the checklist has the user confirm it exists with `ls` before deleting))
- Modify: `docs/port/stage6a-mac-checklist.md` (note: a dmg-installed app replaces the hand-made bundle steps)
- Modify: `docs/superpowers/RESUME.md` (CURRENT STATE: 6b-1; Next: 6b-2 Windows setup.exe + Linux AppImage/.deb/.rpm + release.ps1 + CI release job; carry 6a parked items: PackageHandOff 3 s UI block, Linux checklist step 6 contents, verify AppImage env/exe on a real AppImage)

- [ ] **Step 1:** Write the docs.
- [ ] **Step 2:** Commit `docs: macOS dmg install, 6b-1 checklist, RESUME`.

---

## After the tasks

Final whole-branch review (opus) → one fix wave → CI green on win/mac/linux → user picks merge/PR/keep → ask before merge. The user then downloads the `Quartermaster-macOS` artifact and runs `docs/port/stage6b1-mac-checklist.md` on the M1.
