# Task 7 report: Updater helper

Branch: worktree-agent-a26fbd8cd73c95b24 (merged `piece8` first).

## Implemented
- `src/Quartermaster.Updater` (net10.0-windows console, no project refs, self-contained single-file win-x64 publish; verified with `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`, produces Quartermaster.Updater.exe).
- `Swapper(appDir, stagedDir, backupDir, journalPath)`: `Apply` / `Rollback` / `Resume` (returns `ResumeOutcome` None/RolledBack/Completed). Journal steps backup -> install -> done (rolledback after rollback); written atomically (tmp + move). Moves contents of the app folder, never the folder itself. Retries moves up to 10 s (`RetryWindow`) on IOException/UnauthorizedAccessException. Cross-volume: copy to `<name>.qm-partial`, rename, delete source, so a crash never leaves a half-copy that looks complete.
- Crash semantics: crash in "backup" -> Resume restores old files (consistent old version). Crash in "install" -> Resume finishes install (consistent new version).
- `Startup.WaitForMarker(marker, processAlive, timeout)`.
- `UpdateRunner` (injected `IProcessHost`: IsAlive/Start/Kill) and `Program.Main` with the exact CLI from the controller decisions. Flow: wait for pid (exit-timeout, else exit 2, nothing changed) -> delete stale marker -> Resume -> Apply -> start `Quartermaster.App.exe --updated-from <old>` -> WaitForMarker -> on failure kill new, Rollback, start old with `--update-failed <version>`, exit 3. Swap exception: rollback, start old, exit 4. Journal deleted at end. Log: `<marker folder>\helper.log`; journal `<marker folder>\swap-journal.json`. Backup dir: sibling `Quartermaster.backup-<old>` if parent writable (probe file), else `<marker folder>\backup-<old>`.
- Added `tests/Quartermaster.Updater.Tests` and both projects to `Quartermaster.slnx`. Test project targets net10.0-windows to reference the helper.

## Tests
18 tests, all pass (all named tests from the brief plus: stays-locked throws, no-journal Resume, marker-appears success path, dies-immediately rollback, crashed-journal main flow, ChooseBackupDir x2, ParseArgs). Full solution `dotnet build Quartermaster.slnx`: 0 errors.

## TDD evidence
RED: tests written first; `dotnet test` failed to build (CS5001, no Main / types missing). GREEN: after implementation, `Passed! Failed: 0, Passed: 18`.

## Files
src/Quartermaster.Updater/{Quartermaster.Updater.csproj,Program.cs,Swapper.cs}; tests/Quartermaster.Updater.Tests/{csproj,SwapperTests.cs,RunnerTests.cs}; Quartermaster.slnx.

## Self-review / concerns
- Cross-volume paths are implemented but not exercised by a test (only one volume available); logic is the same retry/rename pattern.
- If a Resume finds a "rolledback"/"backup" journal the helper restarts the old app with `--update-failed` and does not retry the update; staged files may be partly consumed in that case.
- `--update-failed` / `--updated-from` handling and marker writing belong to the app task (Task 8). Marker path is deleted by the helper before launching, so a stale marker cannot fool it.
- Helper does not restart the old app when the pid-wait times out (app presumably still running).
- Backup folder is overwritten if a stale one with the same name exists.
