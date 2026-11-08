# Piece 8 — Ledger images and tabs, updater, release builds — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Counter images and tabs (fixed + personal) in the Ledger; an app version, a GitHub-Releases updater with a swap/rollback helper; a release script that builds the app zip and separate relay downloads.

**Architecture:** UI-free models first (Core `LedgerTabs`, Render `LedgerTabStore`, `CounterImages`, `UpdateChecker`, `UpdateInstaller`), each unit-tested; then thin WinForms wiring (`LedgerWindow`, `EditTabsDialog`, launcher bar). The helper `Quartermaster.Updater` is a separate tiny console project so it can replace the app's files. The relay is untouched apart from nothing — the app version travels in game payloads the relay forwards opaquely.

**Tech Stack:** C# .NET 10, WinForms, SkiaSharp, xUnit, System.Net.Http, System.IO.Compression, PowerShell (release script).

**Spec:** `docs/superpowers/specs/2026-11-08-piece8-ledger-updater-design.md` (read it; this plan argues from it).

## Global Constraints

- Engine ships no real-game content. Fixtures and smoke tabs are invented and labelled TEST.
- Personal ledger tabs are never stored in the rules pack, save or turn file; rules pack format stays 4; `RulesPackIo.ComputeSha256` output unchanged.
- Every per-user file lives under `AppDataRoot.Path` (`QUARTERMASTER_LAYOUT_ROOT` override honoured). Nothing writes into the app folder.
- Rows and images come only from `ModuleSession.View` (the `PlayerView`), never the full state.
- Version `0.1.0` in `Directory.Build.props`. Release tag `quartermaster-v<semver>`. App asset `Quartermaster-<version>-win-x64.zip` + `.zip.sha256`. Relay assets `QuartermasterRelay-<version>-<rid>.zip` (win-x64, osx-arm64) / `.tar.gz` (linux-x64, linux-arm64) + `.sha256`, same release.
- Repo `EmojiPlay88/Quartermaster`; API `https://api.github.com/repos/EmojiPlay88/Quartermaster/releases/latest`; User-Agent `Quartermaster-Updater/<version>`; network hosts allowed: `api.github.com`, `github.com`, `objects.githubusercontent.com`, `release-assets.githubusercontent.com`.
- Test feed env var `QUARTERMASTER_UPDATE_FEED` (local folder with `release.json` + assets).
- Updater never runs inside a game window, never without the user clicking Update now, never elevates.
- No network in tests. Commit identity repo-local noreply; never stage `startapp.cmd`. Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Row counter image 64×48 px, rows 52 px tall when counters are on (user: big images without hovering). Hover preview ~200 px long side (≥ 2× native counter size, capped to screen), rendered fresh, unit name under it.

## Review Focus

1. A user tab whose filter names a private property, in hot-seat as the other side → row count and values match what `PlayerView` shows; no hidden unit appears. (Test in Task 1.)
2. A rules pack edit deletes a property used by a personal tab's column or filter → tab still opens, column dropped, filter shows error banner, no crash. (Task 1 + Task 2.)
3. Corrupt or hand-edited `ledger-tabs.json` (bad JSON, duplicate names, unknown fields) → loads as no user tabs / dedups, never throws, other modules' tabs kept. (Task 2.)
4. Update zip that is truncated, has wrong hash, has a `..\` entry, or lacks `Quartermaster.App.exe` → refused before anything in the app folder is touched. (Task 6.)
5. New version crashes on start after swap → helper restores backup and restarts old version; app folder never left half-copied (a crash mid-move resumes from a journal). (Task 7.)

## File Structure

| File | Responsibility |
|---|---|
| `src/Quartermaster.Core/LedgerTabs.cs` (new) | Tab definitions (fixed + user), columns per tab, rows per tab with side/group/filter |
| `src/Quartermaster.Render/LedgerTabStore.cs` (new) | Per-module personal tabs + Ledger view prefs, `ledger-tabs.json` |
| `src/Quartermaster.Render/CounterImages.cs` (new) | Image cache key, fit math, render of row image and preview |
| `src/Quartermaster.App/LedgerWindow.cs` (modify) | Tab strip, side/group, counters column, preview, double-click |
| `src/Quartermaster.App/EditTabsDialog.cs` (new) | Personal tab editor |
| `src/Quartermaster.App/CounterPreviewTip.cs` (new) | Big hover preview popup |
| `src/Quartermaster.App/BatchGridWindow.cs`, `OutlinerPanel.cs` (modify) | Counter images |
| `Directory.Build.props` (modify), `src/Quartermaster.Render/AppVersion.cs` (new) | Version |
| `src/Quartermaster.Net/Protocol.cs`, `src/Quartermaster.Render/Online/*`, `src/Quartermaster.Core/TurnFile.cs` (modify) | App version in handshake / turn file |
| `src/Quartermaster.Render/Update/UpdateChecker.cs`, `UpdateFeed.cs`, `UpdateState.cs` (new) | Check, parse, throttle, skip |
| `src/Quartermaster.Render/Update/UpdateInstaller.cs` (new) | Download, verify, safe extract, stage, launch helper |
| `src/Quartermaster.Updater/` (new project) + `tests/Quartermaster.Updater.Tests/` | Swap, journal, rollback |
| `src/Quartermaster.App/UpdateBar.cs`, `LauncherForm.cs`, `MainForm.cs`, `SettingsTabs.cs`, `Program.cs` (modify/new) | UI, Help menu, settings toggle, `--updated-from`, smokes |
| `build/release.ps1` (new), `docs/relay-setup.md`, `README.md` | Release builds, docs |

## Task graph

- Task 1 → Task 2 → Task 4. Task 3 independent. Task 4 needs 1, 2, 3. Task 5 needs 3 (shares renderer).
- Task 6 (version + checker + installer) independent of 1–5. Task 7 (helper) independent. Task 8 needs 6, 7. Task 9 needs 6. Task 10 needs 7, 8.
- Parallel waves: {1, 3, 6, 7} → {2, 5, 9} → {4, 8} → {10}.

---

### Task 1: Core ledger tab model

**Files:**
- Create: `src/Quartermaster.Core/LedgerTabs.cs`
- Test: `tests/Quartermaster.Core.Tests/LedgerTabsTests.cs`

**Interfaces:**
- Consumes: `LedgerTable.Rows/Columns`, `LedgerRow`, `PieceFilter.Compile`, `Ownership.PieceSides`, `RulesPack.Sides`.
- Produces:
  ```csharp
  public enum LedgerTabKind { Units, Holders, Everything, User }
  public enum LedgerGrouping { None, Map, Side }
  public sealed record UserLedgerTab(string Name, string? Filter, IReadOnlyList<string> ColumnIds, bool IncludeHolders, string? Notes);
  public sealed record LedgerTabDef(string Key, string Title, LedgerTabKind Kind, UserLedgerTab? User);   // Key: "units","holders","all","user:<name>"
  public sealed record LedgerRowEx(LedgerRow Row, string? PieceId, string? SideId, string? MapName, bool IsGroupHeader);
  public sealed record LedgerTabResult(IReadOnlyList<PropertyDef> Columns, IReadOnlyList<LedgerRowEx> Rows, int Count, string? FilterError);
  public static class LedgerTabs
  {
      public static IReadOnlyList<LedgerTabDef> Tabs(IReadOnlyList<UserLedgerTab> user);          // fixed three, then user in order
      public static IReadOnlyList<PropertyDef> ColumnsFor(LedgerTabDef tab, RulesPack p);
      public static LedgerTabResult Rows(LedgerTabDef tab, GameState view, RulesPack p, ModuleModel m,
          string filter, string? sideId, LedgerGrouping group, bool includeEmpty);
      public static string? FilterError(string? expr);                                          // null = ok
      public static string ToCsv(LedgerTabResult r);                                            // skips group headers
  }
  ```

- [ ] **Step 1: Write failing tests** (fixture: extend `LedgerTableTests.Game()` pattern with `RunnerFx`; add a Pieces-scope `fuel`, Holders-scope `supply`, Both-scope `divisor`, two sides by `UnitFilter`):
  - `Tabs_FixedThreeThenUserInOrder`: keys `units, holders, all, user:Supply TEST`.
  - `Units_ColumnsArePiecesAndBoth` → `Divisor, Fuel`; `Holders_ColumnsAreHoldersAndBoth` → `Divisor, Supply`; `Everything_AllColumns`.
  - `Units_RowsHaveNoHolders`, `Holders_RowsAreGlobalThenHolders`, `Everything_MatchesLedgerTableRows` (same names/order as `LedgerTable.Rows`).
  - `Units_SideFilter_KeepsOnlyThatSide`, `Units_GroupByMap_InsertsHeaderRows` (header `IsGroupHeader`, `Count` excludes headers), `GroupBySide_UnownedUnitsUnderNoSide` (header title `"No side"`).
  - `UserTab_FilterRunsOverUnitsTabSet` (marker piece with no values and no matching AppliesTo never appears even with empty filter), `UserTab_EmptyColumns_UsesUnitsColumns`, `UserTab_ColumnIdsKeepOrder`, `UserTab_UnknownColumnIdDropped`, `UserTab_IncludeHolders_HoldersFirst`.
  - `UserTab_BrokenFilter_NoRowsAndError` (`FilterError` non-null, `Rows` empty).
  - `UserTab_PrivateProperty_ViewHidesIt` (Review Focus 1): build view with `PlayerView` for side B where `fuel` is private to side A; filter `Fuel < 5` returns no side-A units and no values for them.
  - `ToCsv_SkipsGroupHeaders_UsesTabColumns`.
- [ ] **Step 2:** `dotnet test tests/Quartermaster.Core.Tests --filter LedgerTabsTests` → FAIL (types missing).
- [ ] **Step 3:** Implement `LedgerTabs`. Build on `LedgerTable.Rows(view, p, m, "", includeEmpty)` for the base set, then select columns by mapping indices; user-tab filter via `PieceFilter.Compile(filter, view, p)`; side via `Ownership.PieceSides(view, p)`. Text filter applied last, as `LedgerTable` does. Group header rows: `LedgerRow(Key: "group:<title>", Name: title, Kind: "", Values: all null)`.
- [ ] **Step 4:** Run tests → PASS. Whole suite `dotnet test Quartermaster.slnx` green.
- [ ] **Step 5:** Commit `feat(ledger): tab model with fixed and user tabs`.

### Task 2: Personal tab store

**Files:**
- Create: `src/Quartermaster.Render/LedgerTabStore.cs`
- Test: `tests/Quartermaster.Render.Tests/LedgerTabStoreTests.cs`

**Interfaces:**
- Consumes: `UserLedgerTab`, `LedgerGrouping` (Task 1), `AppDataRoot`.
- Produces:
  ```csharp
  public sealed record LedgerPrefs(IReadOnlyList<UserLedgerTab> Tabs, string? LastTab, bool ShowCounters, string? Side,
      LedgerGrouping Grouping, IReadOnlyDictionary<string, IReadOnlyList<int>> ColumnWidths) { public static LedgerPrefs Default { get; } }
  public static class LedgerTabStore
  {
      public static LedgerPrefs Load(string root, string moduleSha);   // never throws
      public static void Save(string root, string moduleSha, LedgerPrefs prefs);   // best effort, atomic tmp+move
      public static event Action<string>? Changed;                     // moduleSha; raised after Save
      public static IReadOnlyList<UserLedgerTab> Normalize(IReadOnlyList<UserLedgerTab> tabs); // trims, drops blank names, dedups names case-insensitively (first wins)
  }
  ```
  File `ledger-tabs.json` under root, object keyed by module SHA-256 (pattern: `RulesMemory`). Default `ShowCounters = true`, `Grouping = Map`.

- [ ] **Step 1: Failing tests:** `Load_Missing_Default`, `SaveLoad_RoundTrip`, `Save_KeepsOtherModules`, `Load_CorruptJson_Default` (Review Focus 3), `Load_DuplicateNames_FirstWins`, `Load_UnknownFields_Ignored`, `Save_RaisesChanged`, `Save_DoesNotTouchRulesPackHash` (save a pack, hash, save prefs, hash again equal).
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement with `System.Text.Json` nodes, tolerant per field like `SettingsStore`.
- [ ] **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(ledger): personal tab store per module`.

### Task 3: Counter image rendering

**Files:**
- Create: `src/Quartermaster.Render/CounterImages.cs`
- Test: `tests/Quartermaster.Render.Tests/CounterImagesTests.cs`

**Interfaces:**
- Consumes: `PieceRenderer.RenderSprite(PieceInstance, float, PieceDrawFlags)`, `LruImageCache`.
- Produces:
  ```csharp
  public sealed class CounterImages(ImageSource images, long maxBytes = 64L << 20)
  {
      public static readonly SKSizeI RowSize = new(64, 48);
      public static string Key(PieceInstance p, SKSizeI size);           // type string + joined trait states + size
      public static SKSizeI PreviewSize(SKRect nativeBounds, SKSizeI screen); // long side max(200, 2*native), capped to 80% of screen, aspect kept
      public SKImage? TryGetCached(PieceInstance p, SKSizeI size);
      public SKImage? Render(PieceInstance p, SKSizeI size);             // thread-safe, cached, null if not drawable
      public void Clear();
  }
  ```
  Logic moves out of `PaletteThumbs.Render` (fit-into-box drawing); `PaletteThumbs` then calls `CounterImages` (no behaviour change).

- [ ] **Step 1: Failing tests:** `Key_ChangesWithTraitState`, `Key_ChangesWithSize`, `PreviewSize_SmallCounter_200LongSide`, `PreviewSize_BigCounter_TwiceNative`, `PreviewSize_CappedToScreen`, `PreviewSize_KeepsAspect`, `Render_FixtureSvgPiece_HasPixels` (use TestFixtures module image), `Render_SecondCall_Cached` (same instance), `Render_UndrawablePiece_Null`.
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement; render preview at the target scale through `RenderSprite(p, zoom)` with zoom = target / native, not by upscaling.
- [ ] **Step 4:** Run → PASS; existing palette tests green.
- [ ] **Step 5:** Commit `feat(render): counter image cache and preview sizing`.

### Task 4: Ledger window tabs, counters, preview, Edit tabs dialog

**Files:**
- Modify: `src/Quartermaster.App/LedgerWindow.cs`
- Create: `src/Quartermaster.App/EditTabsDialog.cs`, `src/Quartermaster.App/CounterPreviewTip.cs`, `src/Quartermaster.App/MainForm.SmokeLedger.cs`
- Modify: `src/Quartermaster.App/Program.cs` (`--screenshot-ledger-tabs <png>`)
- Test: `tests/Quartermaster.Render.Tests/LedgerTabsEditTests.cs` (validation model only)

**Interfaces:**
- Consumes: Tasks 1–3. `ModuleSession.View`, `ModuleSession.ModuleSha256`, `RulesChanged`, map-centre call used by `OutlinerPanel` click-through.
- Produces: `public static class LedgerTabsEdit { public static IReadOnlyList<string> Problems(IReadOnlyList<UserLedgerTab> tabs); }` in Render (blank name, duplicate name, filter error via `LedgerTabs.FilterError`), used by the dialog.

- [ ] **Step 1: Failing tests** for `LedgerTabsEdit.Problems`: blank name, duplicate (case-insensitive), broken filter message contains the parse error, valid list → empty.
- [ ] **Step 2:** Run → FAIL. **Step 3:** implement. **Step 4:** PASS.
- [ ] **Step 5: Window wiring.**
  - Tab strip above the grid (titles `Units (n)`, `Depots & Global (n)`, `Everything`, user tabs `★ Name`, then `+`). `+` and toolbar **Edit tabs…** open `EditTabsDialog`; OK → `LedgerTabStore.Save`; every open Ledger for that module reloads on `LedgerTabStore.Changed`.
  - Toolbar adds **Side** and **Group by** combos (visible on Units and user tabs), **Show counters** toggle. All persisted via `LedgerTabStore` with last tab and column widths per tab key.
  - Counters: when on, row height 52, Name cell painted with image left (from `CounterImages.TryGetCached`; else placeholder box and queue render on worker for visible rows via `ThumbQueue.Order`, then `InvalidateCell`). Holder and group rows: no image.
  - Group header rows bold, read-only, not sortable targets (sorting sorts within groups).
  - Hover over image area → `CounterPreviewTip` shows preview (`CounterImages.PreviewSize`, unit name under), hides on leave/scroll; elsewhere keeps `BreakdownTip`.
  - Double-click image → select and centre piece on its map (reuse outliner's call).
  - Filter error banner for a user tab with a broken filter (Review Focus 2).
  - Export CSV uses `LedgerTabs.ToCsv` of the current tab.
- [ ] **Step 6:** Smoke `--screenshot-ledger-tabs <png>`: open Ledger on fixture module with an invented `Supply TEST` user tab (written into a temp `QUARTERMASTER_LAYOUT_ROOT`), Units tab, counters on; screenshot. Run it and look at the PNG.
- [ ] **Step 7:** Full suite green; commit `feat(ledger): tabs, counter images, big preview, edit tabs dialog`.

### Task 5: Counter images in batch grid and outliner

**Files:** Modify `src/Quartermaster.App/BatchGridWindow.cs`, `src/Quartermaster.App/OutlinerPanel.cs`, `src/Quartermaster.App/PaletteThumbs.cs`.
**Interfaces:** Consumes `CounterImages` (Task 3), one instance per `ModuleSession` (add `CounterImages Counters` property on the App-side session holder created next to `PaletteThumbs`; builder picks the owner).

- [ ] **Step 1:** Batch grid: image cell at the left of unit rows (row height 52), same lazy render and hover preview as Task 4 (`CounterPreviewTip` reused if Task 4 merged first; otherwise image only).
- [ ] **Step 2:** Outliner: 24×18 image left of unit nodes (`CounterImages.Render(p, new(24,18))`), lazy.
- [ ] **Step 3:** Existing smokes `--screenshot-batch` and `--screenshot-outliner` run and show images; full suite green.
- [ ] **Step 4:** Commit `feat(ui): counter images in batch grid and outliner`.

### Task 6: App version, update checker, installer (download/verify/stage)

**Files:**
- Modify: `Directory.Build.props` (`<Version>0.1.0</Version>`)
- Create: `src/Quartermaster.Render/AppVersion.cs`, `src/Quartermaster.Render/Update/UpdateFeed.cs`, `UpdateChecker.cs`, `UpdateState.cs`, `UpdateInstaller.cs`
- Modify: `src/Quartermaster.Render/Settings.cs` + `SettingsStore.cs` (`CheckUpdatesOnStart = true`, `LastUpdateCheckUtc`, `SkippedVersion`)
- Test: `tests/Quartermaster.Render.Tests/UpdateCheckerTests.cs`, `UpdateInstallerTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public static class AppVersion { public static Version Current { get; } public static string Text { get; } }   // from assembly informational version, "0.1.0"
  public sealed record UpdateInfo(Version Version, string Tag, string Notes, Uri ZipUrl, Uri ShaUrl, long ZipSize, Uri PageUrl);
  public interface IUpdateFeed { Task<string?> LatestJson(CancellationToken ct); Task<Stream> Open(Uri asset, CancellationToken ct); }
  public sealed class GitHubFeed(HttpClient http) : IUpdateFeed;      // UA header, host allow-list on every redirect hop
  public sealed class FolderFeed(string folder) : IUpdateFeed;        // release.json + files; Uri = file name
  public static class UpdateFeeds { public static IUpdateFeed Default(HttpClient http); }   // QUARTERMASTER_UPDATE_FEED wins
  public enum CheckOutcome { UpToDate, Available, Skipped, Failed }
  public sealed record CheckResult(CheckOutcome Outcome, UpdateInfo? Info, string? Error);
  public static class UpdateChecker
  {
      public static UpdateInfo? Parse(string releaseJson);           // tag ^quartermaster-v(\d+\.\d+\.\d+)$, not prerelease/draft, both assets present
      public static bool Due(DateTime? lastUtc, DateTime nowUtc);    // >= 24 h or never
      public static Task<CheckResult> Check(IUpdateFeed feed, Version current, string? skipped, CancellationToken ct);
  }
  public sealed record StagedUpdate(Version Version, string StagedDir, string Root);
  public static class UpdateInstaller
  {
      public static Task<StagedUpdate> Download(IUpdateFeed f, UpdateInfo u, string updatesRoot, IProgress<double>? progress, CancellationToken ct);
      // verifies size + SHA-256 (.sha256 first token, hex, case-insensitive), extracts safely, checks Quartermaster.App.exe present
      // and its FileVersion == u.Version; throws UpdateRefused(message) otherwise; deletes partial files on failure
      public static bool AppFolderWritable(string appDir);
      public static void LaunchHelper(StagedUpdate s, string appDir, int pid);   // copies Quartermaster.Updater.exe (+ deps) to s.Root\helper\ and starts it
      public static void CleanupOld(string updatesRoot, Version current);        // deletes other versions' folders
  }
  ```
  `updatesRoot` = `AppDataRoot.Sub("Updates")`.

- [ ] **Step 1: Failing tests** (canned JSON strings; `FolderFeed` on temp dirs; fake `HttpMessageHandler` for `GitHubFeed`):
  - Parse: valid → version 0.2.0 + URLs; wrong tag prefix → null; prerelease → null; draft → null; missing `.sha256` asset → null; bad semver → null.
  - Due: null → true; 23 h → false; 24 h → true.
  - Check: newer → Available; same/older → UpToDate; newer but equals skipped → Skipped; feed throws / 403 rate-limit → Failed with message; offline → Failed.
  - GitHubFeed: sends `User-Agent: Quartermaster-Updater/0.1.0`; redirect to `evil.example` → throws; redirect to `objects.githubusercontent.com` → ok; `http://` → throws.
  - UpdateFeeds.Default with env var set → FolderFeed.
  - Installer (Review Focus 4): good zip → staged dir with exe; wrong hash → `UpdateRefused`, no staged dir; truncated (size mismatch) → refused; entry `..\x.txt` or absolute path → refused; no exe → refused; exe FileVersion mismatch → refused (build fixture exe: copy a tiny test-built exe stamped with version via the test project, or check via a `version.txt` fallback — builder picks, but FileVersion is the production check). Cancel mid-download → partial files gone.
  - `AppFolderWritable`: read-only temp dir → false.
  - Settings round-trip of the three new fields.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** PASS, full suite green.
- [ ] **Step 5:** Commit `feat(update): version, update checker and installer`.

### Task 7: Updater helper (swap, journal, rollback)

**Files:**
- Create: `src/Quartermaster.Updater/Quartermaster.Updater.csproj` (console, `net10.0-windows`, no references to other projects), `Program.cs`, `Swapper.cs`
- Create: `tests/Quartermaster.Updater.Tests/` (added to `Quartermaster.slnx`)

**Interfaces:**
- Produces: CLI `Quartermaster.Updater.exe --app <dir> --staged <dir> --pid <n> --version <v> --old <v> [--marker-timeout 30]`.
  ```csharp
  public sealed class Swapper(string appDir, string stagedDir, string backupDir, string journalPath)
  {
      public void Apply();        // journal steps: "backup", "install", "done"; moves app contents → backupDir, staged → appDir
      public void Rollback();     // restores backupDir → appDir
      public void Resume();       // reads journal after a crash: finishes or rolls back
  }
  public static class Startup { public static bool WaitForMarker(string markerPath, Func<bool> processAlive, TimeSpan timeout); }
  ```
  `backupDir` = sibling `<appDir>\..\Quartermaster.backup-<old>` if parent writable, else `%LOCALAPPDATA%\Quartermaster\Updates\backup-<old>` (builder may choose one; must be same volume for moves, else copy). Marker: `AppDataRoot\Updates\started-<version>` written by the new app (Task 8). Helper writes a log `Updates\helper.log`.

- [ ] **Step 1: Failing tests** (temp dirs with fake files): `Apply_MovesStagedIn_BackupHasOld`, `Apply_FileLockedThenFreed_Retries` (open handle released after 1 s), `Rollback_RestoresOld`, `Resume_AfterCrashInBackup_RollsBack`, `Resume_AfterCrashInInstall_FinishesOrRollsBack` (Review Focus 5 — consistent folder either way), `WaitForMarker_Appears_True`, `WaitForMarker_ProcessDies_False`, `WaitForMarker_Timeout_False`, `Main_PidNeverExits_AbortsNothingChanged` (short timeout arg).
- [ ] **Step 2:** FAIL. **Step 3:** Implement. Main flow: wait for pid exit (60 s, else abort) → `Resume()` if journal exists → `Apply()` → start new `Quartermaster.App.exe --updated-from <old>` → `WaitForMarker` → on false: kill new process if alive, `Rollback()`, start old app with `--update-failed <version>`. Ends by deleting journal.
- [ ] **Step 4:** PASS. **Step 5:** Commit `feat(update): helper with journalled swap and rollback`.

### Task 8: Updater UI

**Files:**
- Create: `src/Quartermaster.App/UpdateBar.cs`, `src/Quartermaster.App/UpdateDialog.cs` (progress), `src/Quartermaster.App/WhatsNewDialog.cs`
- Modify: `LauncherForm.cs` (bar at top, version in footer, background check on show), `MainForm.cs` (Help → Check for updates… opens the launcher's flow only when no game is open, otherwise says "Close the game and use the launcher to update"; About shows `AppVersion.Text`), `SettingsTabs.cs` (General: Check for updates on start), `Program.cs` (`--updated-from <v>` writes marker, launcher shows "Updated to X"; `--update-failed <v>` shows "Update to X failed, kept Y"; `--screenshot-update-bar <png>` with a FolderFeed fixture), `Quartermaster.App.csproj` (publish includes the helper: project reference with `ReferenceOutputAssembly=false` + copy, builder's choice).

- [ ] **Step 1:** Launcher on show: if `CheckUpdatesOnStart && UpdateChecker.Due(...)` run `Check` on a worker; store `LastUpdateCheckUtc`; Available → `UpdateBar` "Quartermaster {v} is available." with What's new / Update now / Later / Skip this version (Skip stores `SkippedVersion`). Failed → app log only. Manual check → message box for UpToDate/Failed.
- [ ] **Step 2:** Update now: disabled while any game window is open; `AppFolderWritable` false → "Quartermaster can't update itself in this folder. Open the download page?" → open `PageUrl`. Else `UpdateDialog` runs `Download` with progress + Cancel; `UpdateRefused` → message, nothing changed; success → `LaunchHelper` → `Application.Exit()`.
- [ ] **Step 3:** On start: `--updated-from` → write marker, `CleanupOld`, show status in launcher.
- [ ] **Step 4:** Smoke `--screenshot-update-bar` run and PNG checked. Full suite green.
- [ ] **Step 5:** Commit `feat(update): launcher update bar, help menu, settings toggle`.

### Task 9: App version in online handshake and turn files

**Files:** Modify `src/Quartermaster.Net/Protocol.cs` (`JoinRequest(..., string? AppVersion = null)`, `Welcome(..., string? HostAppVersion = null)`, `LobbyListing` unchanged — optional pre-join warning skipped, spec allows), `src/Quartermaster.Render/Online/HostSession.cs`, `GuestSession.cs`/`OnlineLobbyModel.cs` (warning text), `src/Quartermaster.Core/TurnFile.cs` (`string? AppVersion = null` appended; `Version` stays 1), turn-file open path (warning).
**Interfaces:** Consumes `AppVersion.Text` (Task 6). Produces `public static string? VersionWarning(string? mine, string? theirs, string who)` in `Quartermaster.Render.Online` (null when equal or theirs null); text: `"{who} runs {theirs}, you run {mine} — update first if anything misbehaves."` with who = `Host` / `This turn file was made with`  — builder words the turn-file variant as `"This turn file was made with {theirs}, you run {mine}…"`.

- [ ] **Step 1: Failing tests:** `VersionWarning_Equal_Null`, `_Different_Text`, `_TheirsNull_Null`; host-session test: guest join with other app version succeeds and guest model surfaces the warning; old JoinRequest JSON without the field still deserialises; relay test: relay forwards a JoinRequest carrying the field unchanged (existing relay is opaque — assert end-to-end in `Piece6bEndToEndTests` style); turn file round-trip with and without `AppVersion`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement; guest shows warning in chat/status line (builder picks), turn-file open shows it in the replay bar.
- [ ] **Step 4:** PASS. **Step 5:** Commit `feat(online): carry app version, warn on mismatch`.

### Task 10: Release script and docs

**Files:** Create `build/release.ps1`; modify `docs/relay-setup.md`, `README.md`, `docs/superpowers/RESUME.md`, `docs/superpowers/piece1-followups.md` (section "Piece 8").

- [ ] **Step 1:** `release.ps1 -Version 0.1.0 [-SkipTests]`: checks `Directory.Build.props` version equals `-Version`; `dotnet test`; publishes app (`-c Release -r win-x64 --self-contained -p:Version=…`) with helper into `artifacts/app/`; zips `Quartermaster-<v>-win-x64.zip`; relay `dotnet publish src/Quartermaster.Relay -c Release -r <rid> --self-contained -p:PublishSingleFile=true` for win-x64, linux-x64, linux-arm64, osx-arm64 into separate archives; writes `<file>.sha256` (`<hex>  <name>`) for each.
- [ ] **Step 2:** Archive checks, script fails if any hit: entry names or text files containing `C:\Users\`, the local user name (`$env:USERNAME`), `.vmod`, `.qrules`, `startapp.cmd`; app zip containing `Quartermaster.Relay*`; relay archives containing `Quartermaster.App*`.
- [ ] **Step 3:** Prints (does not run) `gh release create quartermaster-v<v> <assets> --title "Quartermaster <v>" --notes-file …`.
- [ ] **Step 4:** Run `build/release.ps1 -Version 0.1.0` locally; confirm all archives and checks pass. Then hands-on dry run of the update path: build a 0.1.1 zip (`-p:Version=0.1.1`), make a `FolderFeed` folder (`release.json` + assets), set `QUARTERMASTER_UPDATE_FEED`, run the 0.1.0 publish from a temp folder, click Update now, confirm it restarts as 0.1.1; repeat with a 0.1.1 build that exits immediately and confirm rollback.
- [ ] **Step 5:** Docs: relay-setup "Download a relay binary" + how to update (`docker pull` / replace the binary); README Install / Update section + SmartScreen note + "no code signing; checksum only guards corrupt downloads"; RESUME and followups updated.
- [ ] **Step 6:** Commit `build: release script and install/update docs`. Do not create the GitHub release; ask the user.
