# Port Stage 6b-2: Windows Setup, Linux Packages and the Release Build Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every download a release needs is built and checked by GitHub Actions: Windows `Quartermaster-Setup-<v>.exe` (per-user) and zip, Linux AppImage, `.deb`, `.rpm` and tar.gz, plus the macOS dmg/zip from 6b-1. `build/release.ps1` collects one CI run's files, adds the relay downloads, re-runs its privacy checks and prints the `gh release create` command.

**Architecture:** One publish per OS. Small scripts per package type copy the publish folder, rewrite `quartermaster-package.json` to the package's kind, and wrap it (Inno Setup, appimagetool, nfpm, tar, zip). The existing `cross-platform.yml` jobs build and smoke-test each package on every push and upload them as artifacts. A new `release.yml` (manual `workflow_dispatch`, read-only) runs the same scripts with a version input and uploads one combined artifact. `release.ps1` switches from the WinForms zip to the CI artifacts; it still creates nothing.

**Tech Stack:** bash, PowerShell, Inno Setup 6 (Windows runner), appimagetool and nfpm (pinned release binaries, SHA-256 checked), Docker `fedora` image for the rpm smoke test, Pillow (one-off icon drawing, output committed).

**Spec:** `docs/superpowers/specs/2026-11-08-cross-platform-design.md` ("Packaging", "Updater on each package type", "Rollout"); stage 6a plan (asset-name table, package kinds, `quartermaster-files.txt`, `Quartermaster.App.exe` switch-over copy); stage 6b-1 plan (macOS bundle/dmg, CI macOS job).

## Design calls (Claude's)

- **One publish, many kinds.** `build/set-package-kind.sh <folder> <kind>` / `build/Set-PackageKind.ps1` rewrite only the marker's `package` value (version unchanged). `quartermaster-files.txt` lists names only, so it stays valid.
- **Windows setup.exe** (Inno Setup 6): `AppId={{6F0B6C2E-9C1F-4E8E-9C55-5C5E0A1D2B7E}`, per-user `PrivilegesRequired=lowest`, `DefaultDirName={localappdata}\Programs\Quartermaster` (not the data folder `%LOCALAPPDATA%\Quartermaster`), Start-menu shortcut "Quartermaster" → `Quartermaster.exe`, optional desktop shortcut (unchecked), `CloseApplications=yes`, `[UninstallDelete]` removes `{app}` and `{localappdata}\Programs\Quartermaster.backup-*` (the 6a updater's backups) but never the data folder; marker kind `win-setup`; `OutputBaseFilename=Quartermaster-Setup-<v>`; LZMA2 solid. Installing over an existing install upgrades in place. Unsigned (SmartScreen documented).
- **Windows zip** `Quartermaster-<v>-win-x64.zip` = the Avalonia publish (kind `win-zip`, includes `Quartermaster.App.exe` copy) — this is the switch-over asset; WinForms is no longer packaged by `release.ps1`. Making an actual release still waits for the user and the parity rule.
- **Linux tar.gz** `Quartermaster-<v>-linux-x64.tar.gz`: files at the top (6a staging expects that), kind `linux-tar`.
- **AppImage** `Quartermaster-<v>-x86_64.AppImage`: AppDir = `usr/bin/` (publish, kind `linux-appimage`), `AppRun` (shell: `exec "$APPDIR/usr/bin/Quartermaster" "$@"`), `quartermaster.desktop`, `quartermaster.png`; built with appimagetool pinned to an exact release ≥ 2 weeks old, binary SHA-256 checked against a value written in the script.
- **.deb / .rpm** via nfpm (pinned, SHA-256 checked): files to `/opt/quartermaster/` (kind `linux-deb` / `linux-rpm`), symlink `/usr/bin/quartermaster` → `/opt/quartermaster/Quartermaster`, desktop file `/usr/share/applications/quartermaster.desktop`, icon `/usr/share/icons/hicolor/256x256/apps/quartermaster.png`; deps deb `libfontconfig1, libfreetype6, libgl1`, rpm `fontconfig, freetype, mesa-libGL`; names `quartermaster_<v>_amd64.deb`, `quartermaster-<v>-1.x86_64.rpm`; maintainer "Quartermaster contributors", license LGPL-2.1, homepage the GitHub repo.
- **Icon:** none exists. `build/icon/make-icon.py` draws a plain 256x256 PNG (grey #d4d0c8 rounded square, dark "Q" monogram), committed as `build/icon/quartermaster.png`; used by AppImage, deb, rpm. The Windows exe and macOS bundle keep their default icons in this stage.
- **CI per push** (`cross-platform.yml`): Windows job builds setup.exe + zip and runs them; Linux job builds tar, AppImage, deb, rpm and runs them; artifacts `Quartermaster-Windows`, `Quartermaster-Linux` (plus 6b-1's `Quartermaster-macOS`).
- **Release build** (`release.yml`): `workflow_dispatch` with input `version` (x.y.z, must equal Directory.Build.props), `permissions: contents: read`; three jobs reuse the same scripts and one final job merges everything into artifact `Quartermaster-release-<v>`. No tags, no GitHub Release.
- **`release.ps1 -FromRun <run-id>`:** downloads the combined artifact with `gh run download`, checks the exact asset list and every `.sha256`, re-runs its archive privacy checks (no local paths, user names, `.vmod`, `.qrules`, `startapp.cmd`; app and relay kept apart) on the zip/tar.gz, builds the relay downloads as today, writes the release notes (one line per asset) and prints `gh release create`. The old WinForms build path is removed.
- **6a leftovers here:** `PackageHandOff.Open` stops blocking the UI thread (async wait); Linux checklist step 6 lists what really remains in `Updates/`.
- **Not here:** code signing, custom Windows/macOS icons, publishing a release, the real-AppImage `APPIMAGE` env check (moves into the Linux checklist).

## Global Constraints

- No new NuGet packages. Build tools (Inno Setup, appimagetool, nfpm, Docker images, Pillow) never ship inside the app.
- Every downloaded build tool is pinned to an exact version ≥ 2 weeks old and its SHA-256 checked before use (Inno via `choco install innosetup --version <x>` is acceptable when `iscc` is absent; choco checks its own hashes).
- Asset names exactly: `Quartermaster-Setup-<v>.exe`, `Quartermaster-<v>-win-x64.zip`, `Quartermaster-<v>-linux-x64.tar.gz`, `Quartermaster-<v>-x86_64.AppImage`, `quartermaster_<v>_amd64.deb`, `quartermaster-<v>-1.x86_64.rpm`, `Quartermaster-<v>-osx-arm64.dmg`, `Quartermaster-<v>-osx-arm64.zip`, each plus `.sha256` ("<hash>  <name>").
- Marker kinds per asset: setup `win-setup`, win zip `win-zip`, tar `linux-tar`, AppImage `linux-appimage`, deb `linux-deb`, rpm `linux-rpm`, mac `mac-app`.
- Workflows: `permissions: contents: read`; no secrets; nothing creates tags or releases.
- Public repo: no personal paths, no real-game content, noreply commit email, never commit `startapp.cmd`. Never open visible app windows on the dev machine (CI runs `--version` only).
- Ask before every push, merge and release. Pushing this branch for CI runs is authorized when the user approves the plan.
- Worktree `.worktrees/s6b2`, branch `port-stage6b2` from master `d05e139`.

## Review Focus

1. **Uninstall deletes user data** (`%LOCALAPPDATA%\Quartermaster` vs install dir `%LOCALAPPDATA%\Programs\Quartermaster`). Task 2 CI: install, create a file in the data folder, uninstall, assert the data file survives and `{app}` is gone.
2. **Package kind wrong in a package** → the updater picks the wrong asset or refuses. Each packaging task's CI step reads the installed/extracted marker and asserts the kind.
3. **App won't start from a package** (missing exec bit, missing deps on a clean distro). Task 3/4 CI: run `--version` from the extracted AppImage, the installed deb (ubuntu), the installed rpm (fedora container), the tar.
4. **Release artifact incomplete or names drift** from the 6a table. Task 5: `release.ps1 -FromRun` fails on any missing/extra asset or bad `.sha256`; a Pester-free PowerShell self-test with a fake artifact folder.
5. **Upgrade over an older setup install** leaves stale files or a second entry in Apps & features. Task 2 CI: install v-a, then the same setup again (`/VERYSILENT`), assert one uninstall registry key under HKCU for the AppId.

---

### Task 1: Package-kind rewrite and icon

**Files:** Create `build/set-package-kind.sh`, `build/Set-PackageKind.ps1`, `build/icon/make-icon.py`, `build/icon/quartermaster.png`, `build/test-set-package-kind.sh`.
**Interfaces:** Produces `set-package-kind.sh <folder> <kind>` and `Set-PackageKind.ps1 -Folder <f> -Kind <k>`: rewrite `<folder>/quartermaster-package.json` to `{"version":"<same>","package":"<kind>"}` (same JSON shape the Desktop csproj writes); refuse unknown kinds (the 7 texts) and a missing/unparseable marker with a non-zero exit and a plain message.
- [ ] Step 1: Write `test-set-package-kind.sh` (temp folder, marker for 0.2.0 win-zip → rewrite to linux-deb → assert exact JSON; bad kind refused; missing marker refused). Run → FAIL.
- [ ] Step 2: Implement both rewriters; run the bash test (Git Bash) → PASS; run the PS one by hand on a temp folder → same JSON.
- [ ] Step 3: Draw the icon; Read the PNG to look at it; commit `build: package-kind rewrite and app icon`.

### Task 2: Windows setup.exe and zip in CI

**Files:** Create `build/windows/quartermaster.iss`, `build/windows/make-windows-packages.ps1`, `build/windows/test-setup.ps1`; Modify `.github/workflows/cross-platform.yml` (Windows job).
**Interfaces:** Consumes Task 1 `Set-PackageKind.ps1`. Produces `make-windows-packages.ps1 -Publish <dir> -Version <v> -Out <dir>` → setup.exe + zip + `.sha256` (zip built like `release.ps1`'s `New-ZipFromFolder`, forward slashes); `test-setup.ps1 -Setup <exe> -Version <v>` does: silent per-user install to a temp `/DIR=`, assert marker kind `win-setup`, run `Quartermaster.exe --version` with stdout redirected → `Quartermaster <v>`, write a sentinel file `%LOCALAPPDATA%\Quartermaster\ci-sentinel.txt` (the real data folder — the script refuses to run unless `$env:CI -eq 'true'`, so it never touches a developer's profile; it deletes the sentinel at the end), reinstall the same setup silently and assert one HKCU uninstall key for the AppId, silent uninstall, assert install dir gone and sentinel still there.
- [ ] Step 1: Write `test-setup.ps1`; Inno script; packaging script.
- [ ] Step 2: Locally, if `iscc` is available (`winget`/choco not required — skip if absent and say so), build and run the test; else `-WhatIf`-style dry run of the packaging script's file staging.
- [ ] Step 3: Wire CI Windows steps after Test: publish win-x64 (as today) → `check-publish-natives.sh` → packaging → `test-setup.ps1` → unzip the zip to temp and run `Quartermaster.exe --version` and `Quartermaster.App.exe --version` → upload artifact `Quartermaster-Windows`.
- [ ] Step 4: Commit `build(windows): per-user setup.exe and zip, tested in CI`.

### Task 3: Linux tar.gz and AppImage in CI

**Files:** Create `build/linux/make-tar.sh`, `build/linux/make-appimage.sh`, `build/linux/quartermaster.desktop`, `build/linux/AppRun`; Modify `cross-platform.yml` (Linux job).
**Interfaces:** Consumes Task 1. Produces `make-tar.sh <publish> <v> <out>`, `make-appimage.sh <publish> <v> <out>` (downloads pinned appimagetool into a cache dir, checks SHA-256, builds with `ARCH=x86_64`, no network update info).
- [ ] Step 1: Write scripts; `bash -n`; desktop file passes `desktop-file-validate` in CI (install `desktop-file-utils`).
- [ ] Step 2: CI Linux steps after Test: publish linux-x64 → natives check → tar → AppImage → smoke: extract the tar to temp, marker kind `linux-tar`, `--version`; AppImage: `--appimage-extract-and-run --version` → `Quartermaster <v>`, and `--appimage-extract` then marker kind `linux-appimage` in `squashfs-root/usr/bin/`.
- [ ] Step 3: Commit `build(linux): tar.gz and AppImage, tested in CI`.

### Task 4: Linux .deb and .rpm in CI

**Files:** Create `build/linux/nfpm.yaml` (templated paths via env), `build/linux/make-deb-rpm.sh`; Modify `cross-platform.yml`.
**Interfaces:** Consumes Tasks 1, 3 (desktop file, icon). Produces `make-deb-rpm.sh <publish> <v> <out>` → both packages + `.sha256`.
- [ ] Step 1: Write nfpm config and script (pinned nfpm, SHA-256 checked).
- [ ] Step 2: CI: `sudo apt-get install -y ./quartermaster_<v>_amd64.deb` → `quartermaster --version` → `cat /opt/quartermaster/quartermaster-package.json` kind `linux-deb` → `sudo apt-get remove -y quartermaster` → `/opt/quartermaster` gone; rpm: `docker run --rm -v "$PWD/dist:/dist" fedora:<pinned> bash -c 'dnf install -y /dist/quartermaster-<v>-1.x86_64.rpm && quartermaster --version && grep -q linux-rpm /opt/quartermaster/quartermaster-package.json'`. Upload artifact `Quartermaster-Linux` (tar, AppImage, deb, rpm + sha256).
- [ ] Step 3: Commit `build(linux): deb and rpm via nfpm, tested in CI`.

### Task 5: Release workflow and release.ps1 -FromRun

**Files:** Create `.github/workflows/release.yml`, `build/test-release-collect.ps1`; Modify `build/release.ps1`, `.github/workflows/cross-platform.yml` (factor the packaging steps into reusable composite actions or `workflow_call` so both workflows share them — builder's choice, no duplicated step lists).
**Interfaces:** Consumes Tasks 2–4 and 6b-1's macOS steps. Produces `release.yml` (`workflow_dispatch`, input `version`; fails if it differs from Directory.Build.props; jobs win/mac/linux build their packages; job `collect` downloads them and uploads `Quartermaster-release-<v>` with exactly the 8 assets + 8 `.sha256`). `release.ps1 -Version <v> -FromRun <id> [-ArtifactDir <dir>] [-SkipRelay]` (`-ArtifactDir` skips the download and `-SkipRelay` skips the relay publishes, both for tests): asset list exact, hashes match, privacy checks on win zip and tar.gz, relay downloads built as today, notes list each asset with one plain line, prints `gh release create quartermaster-v<v> <all files> --title "Quartermaster <v>" --notes-file <notes>`. Without `-FromRun`/`-ArtifactDir` it refuses with a message pointing at `release.yml`.
- [ ] Step 1: `test-release-collect.ps1` builds a fake artifact dir (small dummy files with correct names and `.sha256`), runs `release.ps1 -ArtifactDir ... -SkipRelay` → expects success; removes one asset → expects a failure naming it; corrupts one hash → failure. Run → FAIL.
- [ ] Step 2: Implement; run → PASS.
- [ ] Step 3: Commit `build: release workflow and release.ps1 collects CI assets`.

### Task 6: 6a leftovers

**Files:** Modify `src/Quartermaster.Render/Update/PackageHandOff.cs`, `src/Quartermaster.Desktop/Updates/DesktopUpdates.cs`, their tests; `docs/port/stage6a-linux-checklist.md`.
**Interfaces:** `PackageHandOff.OpenAsync(string path, Func<ProcessStartInfo, Process?> start, CancellationToken ct) : Task<bool>` waits up to 3 s with `WaitForExitAsync`; `Open` (sync) removed; Desktop awaits it.
- [ ] Step 1: Failing test `PackageHandOff_DoesNotBlockCaller` (fake process that exits after 2 s; the returned task is not completed synchronously). Implement; run Render + Desktop tests (one at a time) → PASS.
- [ ] Step 2: Fix Linux checklist step 6 (what stays in `Updates/` after a successful update: `helper.log`, `state.json`, `started-<v>`, `<v>/helper/`).
- [ ] Step 3: Commit `fix: package hand-off waits without blocking the UI; checklist`.

### Task 7: Docs

**Files:** Modify `README.md` (Install / Update: Windows setup.exe first, zip as portable; Linux AppImage / .deb / .rpm / tar.gz; maintainers: `release.yml` then `release.ps1 -FromRun`), `docs/port/parity.md` (packaging rows), `docs/superpowers/RESUME.md`; Create `docs/port/stage6b2-windows-checklist.md` (download artifact, run setup, Start menu, update from a test feed into the setup install, uninstall keeps data), `docs/port/stage6b2-linux-checklist.md` (AppImage: run, update; verify `APPIMAGE` in `/proc/<pid>/environ` of the running app and that `/proc/<pid>/exe` is under `APPDIR` — the 6a parked check; deb/rpm install, the update bar hands the package to the system installer).
- [ ] Step 1: Write docs. Step 2: Commit `docs: Windows and Linux install, 6b-2 checklists, RESUME`.

---

## After the tasks

Final whole-branch review (opus) → one fix wave → CI green → run `release.yml` once on the branch with the current version (read-only, artifacts only) to prove the combined artifact → user picks merge/PR/keep → ask before merge. Publishing the switch-over release is a separate decision for the user, after the Mac/Linux checklists.
