# Port Stage 6a: Installing Updates (Desktop app) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Avalonia Desktop app installs its own updates from GitHub Releases on Windows, macOS and Linux (folder swap, `.app` bundle swap, AppImage swap, `.deb`/`.rpm` hand-off), and a WinForms 0.1.0 install can update itself straight onto the Avalonia build.

**Architecture:** Every release build writes `quartermaster-package.json` (version + package kind) next to the app exe. The app reads it to learn how it was installed (`InstallLayout`), picks the matching release asset, downloads and verifies it as today, then either starts the journalled helper (now plain `net10.0`, with a new whole-unit swap mode for `.app` and AppImage) or hands a `.deb`/`.rpm` to the system installer. The Windows zip keeps a copy of the app host named `Quartermaster.App.exe`, so the 0.1.0 WinForms helper can swap and restart onto the Avalonia build unchanged.

**Tech Stack:** C# .NET 10, Avalonia 12.1.5, xUnit, `System.Formats.Tar` (in-box), MSBuild publish targets. No new NuGet packages.

**Spec:** `docs/superpowers/specs/2026-11-11-port-stage5-design.md` ("Updater split") and `docs/superpowers/specs/2026-11-08-cross-platform-design.md` ("Packaging", "Updater on each package type"). Installers themselves (setup.exe, `.dmg`, AppImage/`.deb`/`.rpm` building, `release.ps1`, CI release job) are **stage 6b**, a separate plan.

## Design calls (Claude's; the user handed over the how)

- **Package marker.** `quartermaster-package.json` = `{"version":"0.2.0","package":"win-zip"}` beside the app exe (`AppContext.BaseDirectory`; on macOS that is `Quartermaster.app/Contents/MacOS/`). Kinds: `win-zip`, `win-setup`, `mac-app`, `linux-tar`, `linux-appimage`, `linux-deb`, `linux-rpm`. No marker (a dev build) = cannot self-update; shows the existing `NoManifestText` offer. It also replaces the Win32 FileVersion check for staged folders.
- **Asset names** (6b must produce exactly these):
  | Kind | Update asset |
  |---|---|
  | win-zip, win-setup | `Quartermaster-<v>-win-x64.zip` (unchanged; the 0.1.0 WinForms updater looks for it) |
  | mac-app | `Quartermaster-<v>-osx-arm64.zip` (holds `Quartermaster.app/`) |
  | linux-tar | `Quartermaster-<v>-linux-x64.tar.gz` (holds the app folder's files at the top) |
  | linux-appimage | `Quartermaster-<v>-x86_64.AppImage` |
  | linux-deb | `quartermaster_<v>_amd64.deb` |
  | linux-rpm | `quartermaster-<v>-1.x86_64.rpm` |
  Each with `<asset>.sha256`.
- **Windows exe name / switch-over.** The Avalonia exe stays `Quartermaster.exe`. Windows publishes also carry `Quartermaster.App.exe`, a byte copy of the .NET app host (it loads `Quartermaster.dll` by its embedded name). The 0.1.0 helper checks `Quartermaster.App.exe`'s FileVersion, swaps by `quartermaster-files.txt` and restarts `Quartermaster.App.exe --updated-from 0.1.0`; the Desktop app writes the marker under `AppDataRoot.Sub("Updates")`, which is where 0.1.0 waits. So the switch-over needs no special zip. Old shortcuts keep working.
- **Updates folder.** Desktop moves from `Updates-desktop` to `Updates` (shared with WinForms, which it replaces). `state.json` stays the Desktop's own file there. The old `Updates-desktop` folder is deleted best effort on start.
- **Unit swap** (`.app`, AppImage): rename the unit to a hidden sibling backup `.<name>.qm-backup-<old>` (no `.app` suffix, so macOS never registers it), then move the staged unit in; cross-volume moves copy to `<target>.qm-partial` then rename. Journalled like `Swapper`, same steps and outcomes.
- **Restart.** The helper starts the inner executable directly (`Contents/MacOS/Quartermaster`, or the AppImage file), never `open`, so the pid it watches is the app's.
- **Running copies.** Folder installs: as today, by exe path, checking both `Quartermaster` and `Quartermaster.App` process names. `.app`: by path under the bundle. AppImage: any other process named `Quartermaster` counts (its mount path changes per run).
- **deb/rpm.** Download + SHA-256 verify into the updates folder, then open the package with `xdg-open` and tell the user. No helper, the app keeps running.
- **Test feed.** With `QUARTERMASTER_UPDATE_FEED` set the whole install path now runs (hands-on test). Without it the feed is GitHub, but only when an `InstallLayout` was detected; a dev build never checks GitHub.
- **`--version`.** Prints `Quartermaster <v>` and exits with no window; used by the switch-over smoke and Linux checklists.

## Global Constraints

- C# .NET 10, Avalonia 12.1.5. No new NuGet packages.
- Tests never open visible windows and never touch the real relay, GitHub or the user's profile: test feeds (`QUARTERMASTER_UPDATE_FEED`), temp roots (`QUARTERMASTER_LAYOUT_ROOT`), fake process hosts.
- WinForms keeps working unchanged in behaviour; shared Render changes keep its callers' results identical.
- An update, download or network call never happens without a click, apart from "Check for updates on start" (now live).
- Labels and texts copied verbatim from WinForms where WinForms has them.
- Public repo: no personal paths, no real-game content, noreply commit email, never commit `startapp.cmd`.
- Ask before every push, merge and release.
- Work in worktree `.worktrees/s6a` on branch `port-stage6a` cut from master `b82a566`; ledger `.superpowers/sdd/2026-11-14-port-stage6a/`.

## Review Focus

1. **Crash or power loss mid unit swap** (after backup rename, before install) — next helper run must restore the old `.app`/AppImage, never leave the user with no app. Test in Task 5: `UnitSwap_ResumeAfterBackupStep_RestoresOld`.
2. **Staged folder and app on different volumes** (updates folder on `~`, app on an external disk / `/Applications` on another APFS volume) — copy-then-rename, no half-copied unit visible under the real name. Test in Task 5: `UnitSwap_CrossVolumeMove_UsesPartialName` (simulated via injected mover).
3. **Exec bits lost on Linux/macOS extraction** — the new app or helper would not start and the update rolls back. Test in Task 3: `Extract_TarGz_KeepsExecutableMode` and `Extract_Zip_KeepsExecutableMode` (Unix only, skipped on Windows).
4. **Tar with a symlink or hard link pointing out of the stage** — refused, nothing written. Test in Task 3: `Extract_TarGz_SymlinkEntry_Refused`.
5. **0.1.0 WinForms helper restarts `Quartermaster.App.exe`** — Avalonia must start from that name, show the notice and write `Updates/started-<v>` or 0.1.0 rolls back. Tests in Task 7 (`UpdatedFrom_WritesMarkerInUpdatesRoot`) and Task 8 (`Publish_Win_HasAppHostCopy`).

---

### Task 1: Package marker and install layout (+ RESUME drift fix)

**Files:**
- Create: `src/Quartermaster.Render/Update/PackageInfo.cs`
- Create: `src/Quartermaster.Render/Update/InstallLayout.cs`
- Modify: `docs/superpowers/RESUME.md` (header line: 5d merged as PR #8, master b82a566; 5d table entry "merged as PR #8")
- Test: `tests/Quartermaster.Render.Tests/Update/InstallLayoutTests.cs`

**Interfaces:**
- Produces:
  - `enum PackageKind { WinZip, WinSetup, MacApp, LinuxTar, LinuxAppImage, LinuxDeb, LinuxRpm }`
  - `sealed record PackageInfo(Version Version, PackageKind Kind)`; `static class PackageFile { const string FileName = "quartermaster-package.json"; static PackageInfo? Read(string dir); static void Write(string dir, PackageInfo p); static string KindText(PackageKind k); static PackageKind? ParseKind(string s); }` — JSON keys `version`, `package`; kind texts `win-zip, win-setup, mac-app, linux-tar, linux-appimage, linux-deb, linux-rpm`.
  - `sealed record InstallLayout(PackageKind Kind, Version Version, string AppDir, string? UnitPath, string ExePath)` with `bool SelfUpdates => Kind is not (LinuxDeb or LinuxRpm)` and `static InstallLayout? Detect(string baseDir, Func<string,string?> env)`.
    - `AppDir` = `baseDir` (folder holding the exe and marker).
    - `MacApp`: `UnitPath` = the `.app` two levels above `baseDir` (must end `.app`, else null layout); `ExePath` = `<baseDir>/Quartermaster`.
    - `LinuxAppImage`: `UnitPath` = `env("APPIMAGE")` (null/blank or missing file → null layout); `ExePath` = `UnitPath`.
    - others: `UnitPath` null; `ExePath` = `<baseDir>/Quartermaster.exe` on Windows kinds, `<baseDir>/Quartermaster` otherwise.

- [ ] **Step 1: Write failing tests** — `Detect_NoMarker_Null`, `Detect_WinZip_FolderLayout` (ExePath ends `Quartermaster.exe`, UnitPath null, SelfUpdates true), `Detect_MacApp_UnitIsBundle` (temp `X/Quartermaster.app/Contents/MacOS` → UnitPath `X/Quartermaster.app`), `Detect_MacApp_NotInBundle_Null`, `Detect_AppImage_UsesEnv`, `Detect_AppImage_NoEnv_Null`, `Detect_Deb_NotSelfUpdating`, `PackageFile_RoundTrip_AllKinds`, `PackageFile_BadJsonOrUnknownKind_Null`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Render.Tests --filter InstallLayoutTests` — FAIL (types missing).
- [ ] **Step 3: Implement** both files; `Read` never throws (IO/JSON errors → null); version must be three-part.
- [ ] **Step 4: Run** same filter — PASS.
- [ ] **Step 5: Fix RESUME.md header drift** (text only).
- [ ] **Step 6: Commit** `feat(render): package marker and install layout detection` (+ `docs: RESUME 5d merged as PR #8`).

### Task 2: Asset choice per package kind

**Files:**
- Modify: `src/Quartermaster.Render/Update/UpdateChecker.cs`
- Modify: `src/Quartermaster.Render/Update/UpdateOffer.cs` only if it reads `ZipSize`/`ZipUrl` by name (rename follows)
- Test: `tests/Quartermaster.Render.Tests/Update/UpdateCheckerAssetTests.cs`

**Interfaces:**
- Consumes: `PackageKind` (Task 1).
- Produces:
  - `static string UpdateChecker.AssetName(Version v, PackageKind k)` — exact names in the Design calls table.
  - `static UpdateInfo? UpdateChecker.Parse(string json, PackageKind k)`; the existing `Parse(string json)` = `Parse(json, PackageKind.WinZip)` (WinForms unchanged). `ZipName(v)` stays = `AssetName(v, WinZip)`.
  - `Task<CheckResult> Check(IUpdateFeed, Version current, string? skipped, CancellationToken ct, PackageKind kind = PackageKind.WinZip)`.
  - `UpdateInfo` gains `PackageKind Kind` and `string AssetName` (keep existing positional members; `ZipUrl/ZipSize` keep their names to avoid churn — they mean "the asset").

- [ ] **Step 1: Failing tests** — `AssetName_EachKind` (all 7 rows, v 0.2.0), `Parse_MacKind_PicksOsxZipOnly` (release with all assets → mac zip URL), `Parse_KindAssetMissing_Null` (release with only win zip, kind LinuxAppImage → null), `Parse_NoKind_StillWinZip`.
- [ ] **Step 2: Run** filter `UpdateCheckerAssetTests` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** filter, then `dotnet test tests/Quartermaster.Render.Tests --filter Update` — PASS (old tests unchanged).
- [ ] **Step 5: Commit** `feat(render): pick the update asset for the package kind`.

### Task 3: Staging per kind (zip, tar.gz, single file) and version check by marker

**Files:**
- Modify: `src/Quartermaster.Render/Update/UpdateInstaller.cs` (split extraction into `UpdateStaging.cs` if the file passes ~400 lines)
- Create (if split): `src/Quartermaster.Render/Update/UpdateStaging.cs`
- Test: `tests/Quartermaster.Render.Tests/Update/UpdateStagingTests.cs`

**Interfaces:**
- Consumes: `UpdateInfo.Kind/AssetName` (Task 2), `PackageFile` (Task 1).
- Produces: `UpdateInstaller.Download(...)` unchanged signature; behaviour by `u.Kind`:
  - `WinZip/WinSetup`: as today (zip → `staged/`, `Quartermaster.App.exe` FileVersion check) **or**, when `staged/quartermaster-package.json` exists, check its version instead (so WinForms→Avalonia and Avalonia→Avalonia both pass). Manifest required.
  - `MacApp`: zip → `staged/`; requires `staged/Quartermaster.app/Contents/MacOS/quartermaster-package.json` with version == release and kind `mac-app`, and `quartermaster-files.txt` beside it. `StagedUpdate.StagedDir` = `staged/Quartermaster.app`.
  - `LinuxTar`: tar.gz via `System.Formats.Tar` → `staged/`; marker at top, kind `linux-tar`; manifest required.
  - `LinuxAppImage`: file saved as `staged/Quartermaster.AppImage`, mode `0755`; no inner check (the SHA-256 from the same release binds it). `StagedDir` = that file path.
  - `LinuxDeb/LinuxRpm`: file kept as `<root>/<AssetName>`; `StagedDir` = that file path.
  - Extraction (zip and tar) keeps Unix mode bits (`File.SetUnixFileMode` from entry mode / zip `ExternalAttributes >> 16` when non-zero) on non-Windows; tar refuses symlink, hard link, device and FIFO entries ("The update contains a link or special file ('<name>'); it was not installed."); the existing unsafe-path, duplicate and 4 GB rules apply to tar too.
  - New refusal texts: `The update has no package marker (quartermaster-package.json); it was not installed.`, `The update is version {got}, but the release is {want}; it was not installed.`, `The update is a {gotKind} package, but this copy is {wantKind}; it was not installed.`

- [ ] **Step 1: Failing tests** (FolderFeed fixtures built in temp): `Download_LinuxTar_Stages`, `Download_LinuxTar_VersionMismatch_Refused`, `Extract_TarGz_SymlinkEntry_Refused`, `Extract_TarGz_DotDot_Refused`, `Extract_TarGz_KeepsExecutableMode` (`[SkippableFact]`/`OperatingSystem.IsWindows()` skip), `Extract_Zip_KeepsExecutableMode` (same), `Download_MacApp_StagedDirIsBundle`, `Download_MacApp_WrongKind_Refused`, `Download_AppImage_SavedExecutable`, `Download_Deb_KeptAsFile`, `Download_WinZip_MarkerVersionUsedWhenPresent`, `Download_WinZip_NoMarker_FileVersionStillChecked` (existing behaviour; Windows only).
- [ ] **Step 2: Run** filter `UpdateStagingTests` — FAIL.
- [ ] **Step 3: Implement.** On any failure the version folder is deleted (existing rule).
- [ ] **Step 4: Run** filter + `--filter Update` in Render.Tests — PASS.
- [ ] **Step 5: Commit** `feat(render): stage updates for every package kind`.

### Task 4: Helper on plain net10.0, configurable exe, OS-aware process checks

**Files:**
- Modify: `src/Quartermaster.Updater/Quartermaster.Updater.csproj` (`net10.0`; drop the fixed `RuntimeIdentifier`; keep SelfContained/SingleFile/Invariant for publish only, i.e. under `Condition="'$(RuntimeIdentifier)' != ''"`)
- Modify: `src/Quartermaster.Updater/Program.cs`
- Modify: `tests/Quartermaster.Updater.Tests/Quartermaster.Updater.Tests.csproj` (`net10.0`)
- Modify: `Quartermaster.CrossPlatform.slnf` (add Updater + Updater.Tests)
- Modify: `src/Quartermaster.App/Quartermaster.App.csproj` publish target only if the RID change breaks it (it must still produce `Quartermaster.Updater.exe` for win-x64)
- Test: `tests/Quartermaster.Updater.Tests/RunnerTests.cs`

**Interfaces:**
- Produces:
  - `UpdateOptions.Exe` (`--exe <path relative to --app>`, default `Quartermaster.App.exe` — what the 0.1.0 contract and WinForms pass today).
  - `UpdateOptions.Unit` (`--unit <path>`, optional; Task 5 uses it).
  - `UpdateRunner.OtherCopiesRunning(IProcessHost host, IReadOnlyList<string> exePaths, int selfPid)` — any listed exe; path compare `OrdinalIgnoreCase` on Windows/macOS, `Ordinal` on Linux. Runner passes `[appDir/Exe, appDir/Quartermaster.exe, appDir/Quartermaster.App.exe]` (distinct).
  - Usage line names `Quartermaster.Updater` (no `.exe`) plus `[--exe <rel>] [--unit <path>]`.

- [ ] **Step 1: Failing tests** — `ParseArgs_ExeDefaultAndOverride`, `Main_RestartsConfiguredExe` (fake host records `Quartermaster.exe`), `OtherCopiesRunning_EitherExeName`, `OtherCopiesRunning_CaseRulePerOs`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Updater.Tests` — FAIL.
- [ ] **Step 3: Implement**; `ChooseBackupDir` trims both separators already — keep.
- [ ] **Step 4: Run** Updater.Tests — PASS; `dotnet publish src/Quartermaster.App -c Release -r win-x64 -o %TEMP%\qm6a-app` still lists `Quartermaster.Updater.exe` in its output.
- [ ] **Step 5: Commit** `feat(updater): plain net10.0 helper with configurable exe`.

### Task 5: Whole-unit swap for `.app` bundles and AppImages

**Files:**
- Create: `src/Quartermaster.Updater/UnitSwapper.cs`
- Modify: `src/Quartermaster.Updater/Program.cs` (`UpdateRunner.Run` uses `UnitSwapper` when `Unit` is set)
- Test: `tests/Quartermaster.Updater.Tests/UnitSwapperTests.cs`, `RunnerTests.cs`

**Interfaces:**
- Consumes: `UpdateOptions.Unit`, `ResumeOutcome`, journal JSON style of `Swapper`.
- Produces: `sealed class UnitSwapper(string unitPath, string stagedPath, string backupPath, string journalPath)` with `Version`, `RetryWindow`, `Apply()`, `Rollback()`, `Resume()` (same `ResumeOutcome` meaning as `Swapper`), `DeleteJournal()`, and `static string BackupPathFor(string unitPath, string oldVersion)` = `<parent>/.<unitName>.qm-backup-<old>`. A unit is a directory (`.app`) or a file (AppImage); both work. Internal mover seam `Func<string,string,bool> sameVolume` for tests.
  - Journal steps: `backup` → `install` → `done`; `rollback-clear` → `rollback-restore` → `rolledback`.
  - `Apply` refuses (`ManifestMissingException` reuse is wrong — add `UnitMissingException`, exit code `Refused`) when the staged unit is missing; never deletes the unit unless the backup exists.
  - Runner, unit mode: restart path = `--app` + `--exe` (for AppImage `--app` = parent folder, `--exe` = file name); other-copies check = any other process named after `Exe`'s file name (AppImage) or by path under the unit (`.app`).

- [ ] **Step 1: Failing tests** — `UnitSwap_Dir_Apply_ReplacesAndKeepsBackup`, `UnitSwap_File_Apply_ReplacesAppImage`, `UnitSwap_Rollback_RestoresOld`, `UnitSwap_ResumeAfterBackupStep_RestoresOld`, `UnitSwap_ResumeInstallMine_Completes`, `UnitSwap_CrossVolumeMove_UsesPartialName`, `UnitSwap_StagedMissing_RefusedNothingChanged`, `UnitSwap_BackupNameHasNoAppSuffix`, `Main_UnitMode_NoMarker_RollsBack`.
- [ ] **Step 2: Run** filter `UnitSwap|UnitMode` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Updater.Tests — PASS.
- [ ] **Step 5: Commit** `feat(updater): swap .app bundles and AppImages as one unit`.

### Task 6: Desktop installs updates

**Files:**
- Modify: `src/Quartermaster.Desktop/Updates/DesktopUpdates.cs`
- Modify: `src/Quartermaster.Render/Update/UpdateInstaller.cs` (`LaunchHelper` overload taking `InstallLayout`; `HelperName` per OS)
- Modify: `src/Quartermaster.Render/Update/RunningCopies.cs` (`OthersRunning(IReadOnlyList<string> exePaths, int selfPid, lister)`; old overload delegates)
- Create: `src/Quartermaster.Render/Update/PackageHandOff.cs`
- Test: `tests/Quartermaster.Desktop.Tests/DesktopUpdatesTests.cs`, `tests/Quartermaster.Render.Tests/Update/LaunchHelperTests.cs`

**Interfaces:**
- Consumes: Tasks 1–5.
- Produces:
  - `DesktopUpdates.FolderName = "Updates"`; constructor gains `InstallLayout? layout`; `FromEnvironment(appDataRoot, env, baseDir)`: feed = test feed when `QUARTERMASTER_UPDATE_FEED` set, else `GitHubFeed` **only if** `InstallLayout.Detect` found a layout, else null (no UI, as 5a). Deletes `<root>/Updates-desktop` best effort.
  - Check uses `layout?.Kind ?? PackageKind.WinZip` (test feed with no layout behaves as 5a's win zip).
  - Update now order (WinForms copy verbatim): game open → `"Close the game and use the launcher to update."`; others running → `RunningCopies.CloseOthersText`; no layout → offer page with `UpdateInstaller.NoManifestText + " Open the download page?"`; self-updating kinds: `AppFolderWritable` on `AppDir` (folder kinds) or on the unit's parent (`.app`, AppImage) else `"Quartermaster can't update itself in this folder. Open the download page?"`; helper missing → `$"Quartermaster can't update itself: {HelperName} is missing from its folder. Open the download page?"`; folder kinds without `quartermaster-files.txt` → NoManifestText offer. Then UpdateWindow download → re-check game/others → `LaunchHelper` → `RequestExit()` (app exits). Failure: `"The update was not installed; nothing was changed.\n\n" + message` warning.
  - `UpdateInstaller.HelperName` = `Quartermaster.Updater.exe` on Windows, `Quartermaster.Updater` elsewhere; `LaunchHelper(StagedUpdate s, InstallLayout l, int pid, Func<ProcessStartInfo,Process?> start)` passes `--exe` (`Quartermaster.exe` / `Quartermaster` / `Contents/MacOS/Quartermaster` relative to `--app` = unit parent for `.app`) and `--unit` for unit kinds; copies helper from `l.AppDir`, keeps its mode.
  - `PackageHandOff.Open(string packagePath, Func<ProcessStartInfo,Process?> start)` runs `xdg-open <path>`; Desktop then shows `"Quartermaster {v} was downloaded. Your system's package installer opens now to install it; when it is done, start Quartermaster again."`; on failure `"Could not open the package installer. The package is saved at:\n\n{path}"`. The app keeps running.
  - `InstallLaterText` deleted (and every use).
  - The Desktop app closes through its existing exit path (launcher `Close` → app shutdown); add `internal Action RequestExit` seam for tests.

- [ ] **Step 1: Failing tests** — `UpdateNow_FolderLayout_StartsHelperAndExits` (fake start records args incl. `--exe Quartermaster.exe` on Windows), `UpdateNow_MacLayout_PassesUnit`, `UpdateNow_NoLayout_OffersPage`, `UpdateNow_FolderNotWritable_OffersPage`, `UpdateNow_HelperMissing_OffersPage`, `UpdateNow_OtherCopy_Refuses`, `UpdateNow_Deb_HandsOffAndKeepsRunning`, `UpdateNow_HandOffFails_ShowsPath`, `FromEnvironment_NoFeedNoLayout_NoUi`, `FromEnvironment_LayoutNoTestFeed_UsesGitHubFeedType` (asserts type only; nothing is fetched), `FromEnvironment_RemovesOldUpdatesDesktopFolder`, `RunningCopies_EitherExeName`. Delete/replace the 5a test that asserted `InstallLaterText`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Desktop.Tests --filter DesktopUpdatesTests` and Render `LaunchHelperTests` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** both filters — PASS.
- [ ] **Step 5: Commit** `feat(desktop): install updates (helper, unit swap, deb/rpm hand-off)`.

### Task 7: Start-up after an update, live "Check for updates on start", Help menu, `--version`

**Files:**
- Modify: `src/Quartermaster.Desktop/Shell/StartupArgs.cs`, `src/Quartermaster.Desktop/Program.cs`
- Modify: `src/Quartermaster.Desktop/Updates/DesktopUpdates.cs` (notice + confirm), `src/Quartermaster.Desktop/Launcher/LauncherWindow*.cs` (notice line with optional link, as WinForms)
- Modify: Help > Check for updates… menu wiring (shown whenever `Feed != null`, unchanged rule)
- Test: `tests/Quartermaster.Desktop.Tests/DesktopUpdatesTests.cs`, `LaunchLoopTests.cs`

**Interfaces:**
- Consumes: `UpdateStartup` (Render, unchanged), `DesktopUpdates.UpdatesRoot`.
- Produces: `StartupArgs.UpdatedFrom/UpdateFailed/UpdateRefused` (`--updated-from <v>`, `--update-failed <v>`, `--update-refused <v>`); `DesktopUpdates.OnStart(StartupArgs)` sets `Notice` (failed: waits up to 2 s for the journal to vanish, as WinForms); `DesktopUpdates.OnLauncherShown(string appDir)` writes the marker via `UpdateStartup.ConfirmStarted(UpdatesRoot, appDir, current)` once. Launcher shows `Notice.Text` and the link (`Release page`). `--version` writes `Quartermaster {AppVersion.Text}` to stdout, exit 0, before any Avalonia start.
- "Check for updates on start" now works against whatever feed is live; parity notes for the 5a rows updated in Task 9.

- [ ] **Step 1: Failing tests** — `UpdatedFrom_WritesMarkerInUpdatesRoot` (temp layout root; marker `Updates/started-<current>` exists after the launcher opens, not before), `UpdatedFrom_ShowsUpdatedNotice`, `UpdateFailed_JournalLeft_ShowsReleaseLink`, `UpdateRefused_ShowsNotice`, `AutoCheck_OnStartSetting_RunsAgainstFeed`, `VersionFlag_PrintsAndExits` (calls `Program.Main(["--version"])` with redirected console; asserts output and exit code, no window).
- [ ] **Step 2: Run** filters — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** filters — PASS.
- [ ] **Step 5: Commit** `feat(desktop): update notices, start marker and --version`.

### Task 8: Publish output: helper, marker, file list, `Quartermaster.App.exe`

**Files:**
- Modify: `src/Quartermaster.Desktop/Quartermaster.Desktop.csproj` (publish targets modelled on `Quartermaster.App.csproj`'s `PublishUpdaterHelper` + `WriteReleaseFileList`)
- Modify: `build/check-publish-natives.sh` (also require `Quartermaster.Updater[.exe]`, `quartermaster-files.txt`, `quartermaster-package.json`; win-x64 also `Quartermaster.App.exe`)
- Test: `tests/Quartermaster.Desktop.Tests/PublishLayoutTests.cs` (marked `[Trait("Category","Publish")]`, runs a real `dotnet publish` for the current OS's RID into temp; slow, runs in CI)

**Interfaces:**
- Produces, for `dotnet publish src/Quartermaster.Desktop -r <rid>`:
  - the helper published single-file for the same RID into the output;
  - on `win-*`: `Quartermaster.App.exe` = byte copy of `Quartermaster.exe`;
  - `quartermaster-package.json` with `$(Version)` and kind `$(QmPackageKind)`, default `win-zip` / `mac-app` / `linux-tar` by RID (6b overrides for setup/AppImage/deb/rpm);
  - `quartermaster-files.txt` listing every output file (relative, `/` separators, itself included), written last.
  - The `.app` bundle itself is 6b; on `osx-*` the marker still says `mac-app`.

- [ ] **Step 1: Failing tests** — `Publish_HasHelperMarkerAndFileList`, `Publish_FileListMatchesFolder`, `Publish_Win_HasAppHostCopy` (Windows only; runs `Quartermaster.App.exe --version` from the publish folder with no window → prints current version), `Publish_MarkerKindPerRid`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Desktop.Tests --filter Category=Publish` — FAIL.
- [ ] **Step 3: Implement** the targets; the CI publish steps already call `check-publish-natives.sh`, which now checks the new files.
- [ ] **Step 4: Run** filter — PASS; run `bash build/check-publish-natives.sh <temp publish> win-x64` — all `ok`.
- [ ] **Step 5: Commit** `build(desktop): publish helper, package marker, file list and App.exe host copy`.

### Task 9: End-to-end update on the build machine, docs and checklists

**Files:**
- Create: `tests/Quartermaster.Desktop.Tests/UpdateEndToEndTests.cs`
- Modify: `docs/port/parity.md` (installing-updates rows ticked Windows; notes for 5a rows: test-feed-only text removed), `docs/port/stage5a…` deviations text that says "stage 6" where now done
- Create: `docs/port/stage6a-mac-checklist.md`, `docs/port/stage6a-linux-checklist.md` (hands-on update with a local folder feed: build 0.0.1 and 0.0.2 publishes, `QUARTERMASTER_UPDATE_FEED`, Update now, app restarts, "Updated to 0.0.2.", then a broken 0.0.3 that rolls back)
- Modify: `docs/superpowers/RESUME.md` (CURRENT STATE: stage 6a)

**Interfaces:**
- Consumes: everything above.

- [ ] **Step 1: Failing test** `Update_FolderKind_RealHelper_SwapsAndConfirms` (`[Trait("Category","Publish")]`): publish the helper for the current RID into temp; build an old and a new fake app folder, each with `quartermaster-files.txt` and a fake app = a script (`app.cmd` on Windows, `app.sh` with mode 0755 elsewhere) that writes the marker path it is given via an env var. Start the real helper with `--exe app.cmd|app.sh` and a dead `--pid`; assert exit code 0, the app folder holds the new files, the old ones are in the backup, and the journal is gone. Second case `Update_UnitKind_RealHelper_SwapsDirectoryUnit` (macOS/Linux only): the unit is a directory with the script inside, `--unit` set.
- [ ] **Step 2: Run** — FAIL until wired; then PASS.
- [ ] **Step 3: Write docs and checklists.**
- [ ] **Step 4: Run the full suite** `dotnet test Quartermaster.slnx -c Release` — record pass/skip/fail counts in RESUME.
- [ ] **Step 5: Commit** `test(desktop): end-to-end update; docs(port): stage 6a parity, checklists, RESUME`.

---

## After the tasks

Opus final whole-branch review → one fix wave → scoped re-review → full suite on final HEAD → pre-push checks (noreply emails, no `startapp.cmd`, no personal paths) → user picks merge/PR/keep → CI on win/mac/linux → ask before merge. Then the stage 6b plan (installers, `.app` bundle, AppImage/deb/rpm, `release.ps1`, CI release job, README Install/Update).
