# Cross-platform Port — Stage 4c (OCR language downloads) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The user can download, cancel and remove extra Tesseract OCR languages (tessdata_fast 4.1.0) from a new Settings page, and the OCR panel uses them at once, on every OS.

**Architecture:** A committed catalog (code, name, bytes, SHA-256) is generated once at development time. A UI-free `OcrLanguageService` in `Quartermaster.Documents` owns the catalog, the user folder `AppDataRoot.Sub("OcrLanguages")`, a one-at-a-time download queue (stream to `.part`, hash while streaming, rename on match) and a `LanguagesChanged` event. `TesseractRecognizer` reads the bundled and the user folder and refreshes on that event. On Windows, an `OcrRouter` sends Windows languages to Windows OCR and downloaded languages to Tesseract. The Desktop app gets a Settings page and a "More languages…" item in the OCR panel.

**Tech Stack:** C# .NET 10, Avalonia 12.1.5, Avalonia.Headless.XUnit, xUnit, `System.Net.Http`, `System.Security.Cryptography`. No new packages.

**Spec:** `docs/superpowers/specs/2026-11-11-port-stage4c-ocr-languages-design.md` (approved 2026-11-11). Read it before any task.

## Global Constraints

- Download URL is exactly `https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/<code>.traineddata`. No other host, mirror or fallback.
- Nothing touches the network without a user click. Tests never touch the real network (stub `HttpMessageHandler` or `QUARTERMASTER_OCR_FEED=<folder>`). Only `build/ocr-catalog.ps1`, run by hand, fetches from the real host.
- User-Agent `Quartermaster/<version>` (assembly informational version), no other identifying headers.
- Files are written only under `AppDataRoot.Sub("OcrLanguages")`, never in the app folder. Tests set `QUARTERMASTER_LAYOUT_ROOT` (or pass a folder) to a temp dir.
- A file reaches Tesseract only when its length and SHA-256 equal the catalog's.
- Bundled `eng` = 4,113,088 bytes, SHA-256 `7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2`; never removable.
- Source line on the page, verbatim: `Downloads from github.com/tesseract-ocr/tessdata_fast (tag 4.1.0, Apache-2.0). Nothing is downloaded unless you click Download.` Windows-only second line: `Windows OCR languages are added in Windows Settings.`
- Classic grey look and existing Settings page style. Headless tests only, never a visible window. Work off the UI thread is posted back with `Dispatcher.UIThread.Post`.
- `Quartermaster.App` (WinForms) and `Quartermaster.OcrWindows` are not changed.
- Formats unchanged: rules pack 4, save 7, `settings.json` (no new fields).
- Commits: repo-local noreply identity; every message ends with a blank line, then `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never push.
- Green bar per task: `dotnet build Quartermaster.slnx -c Release` with 0 new warnings, and the task's test projects green. T6 runs the full `dotnet test Quartermaster.slnx -c Release` (flaky tests listed in RESUME may be re-run once).
- Shared files: `MainWindow.Settings.cs` and `MainWindow.Workbench.cs` are changed only by T4; `OcrPanel.cs` only by T5; `docs/port/*`, `THIRD-PARTY-NOTICES`, `build/port-screenshots.ps1`, `Shell/ScreenshotStates.cs` and `docs/superpowers/RESUME.md` only by T6.

## Review Focus

1. **Download cut off mid-file** (network drop, cancel, app killed): no partial file is ever listed or loaded, and a stale `.part` is deleted on the next start. Pinned in T2 (`Truncated_stream_keeps_nothing`, `Stale_part_files_are_deleted_on_start`).
2. **A damaged or hand-edited file in the folder:** shown as Damaged, never handed to Tesseract, fixed by Download. Pinned in T2 (`Wrong_hash_file_is_damaged`) and T3 (`Damaged_file_is_not_a_language`).
3. **Removing a language while a read uses it:** the read finishes, and the panel falls back to the default language. Pinned in T3 (`Remove_waits_for_running_read`) and T5 (`Removing_selected_language_selects_default`).
4. **Folder not writable or disk full:** a plain inline message plus one log line, nothing half-written, and the queue moves on. Pinned in T2 (`Write_failure_reports_and_cleans_up`).
5. **Server errors and redirects:** a 404/500 shows its status code, and a redirect to a non-https URL is refused. Pinned in T2 (`Http_error_names_status`, `Non_https_redirect_is_refused`).

---

### Task 1: Catalog

**Files:**
- Create: `build/ocr-catalog.ps1`, `src/Quartermaster.Documents/Ocr/tessdata-fast-4.1.0.json` (generated, committed), `src/Quartermaster.Documents/Ocr/OcrLanguageCatalog.cs`
- Modify: `src/Quartermaster.Documents/Quartermaster.Documents.csproj` (JSON as `EmbeddedResource`)
- Test: `tests/Quartermaster.Documents.Tests/OcrLanguageCatalogTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record OcrLanguageEntry(string Code, string Name, long Bytes, string Sha256, bool Bundled)`
  - `public static class OcrLanguageCatalog { public const string Tag = "4.1.0"; public static IReadOnlyList<OcrLanguageEntry> Entries { get; } public static OcrLanguageEntry? Find(string code); public static string Url(string code); }`. `Find` is case-insensitive. `Url` returns the Global Constraints URL.

- [ ] **Step 1: Write the script.** `build/ocr-catalog.ps1 [-Out <json>]`:
  - Lists the root `*.traineddata` files at tag 4.1.0 through the GitHub git-trees API (`/repos/tesseract-ocr/tessdata_fast/git/trees/4.1.0`). Skips `script/` and subfolders.
  - Downloads each file to a temp folder and records bytes and lowercase-hex SHA-256.
  - Takes names from a hand-written code→English-name table inside the script. A missing name means `Name = Code`.
  - Writes JSON `{ "tag": "4.1.0", "languages": [ { "code", "name", "bytes", "sha256" } ] }`, sorted by code, LF, and deletes the temp folder.
  - The User-Agent is `Quartermaster-catalog`. There is no `Bundled` field in the JSON; the loader sets `Bundled` for `eng`.
- [ ] **Step 2: Run it once** and commit the JSON. Expected: about 120 entries, and the `eng` row equals the Global Constraints bytes and hash. Record the entry count in the task report.
- [ ] **Step 3: Write the failing tests** in `OcrLanguageCatalogTests`:

```csharp
[Fact] public void Eng_is_bundled_and_matches_the_shipped_file()
// Find("eng"): Bundled, Bytes 4113088, Sha256 "7d4322bd…70b2"; and SHA-256 of AppContext.BaseDirectory/tessdata/eng.traineddata equals it
[Fact] public void Codes_are_unique_and_hashes_are_64_hex()
[Fact] public void Only_eng_is_bundled()
[Fact] public void Url_uses_the_pinned_tag()
// Url("deu") == "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/deu.traineddata"
[Fact] public void Find_is_case_insensitive_and_null_for_unknown()   // Find("DEU") != null; Find("xx_none") == null
```

- [ ] **Step 4:** Run `dotnet test tests/Quartermaster.Documents.Tests --filter OcrLanguageCatalogTests`. Expected: FAIL (no type).
- [ ] **Step 5: Implement `OcrLanguageCatalog`.** It loads the embedded JSON once, lazily, with `System.Text.Json`.
- [ ] **Step 6:** Run the filter again. Expected: PASS.
- [ ] **Step 7: Commit:** "feat(ocr): tessdata_fast 4.1.0 language catalog".

### Task 2: Language service (store, downloads, queue)

**Files:**
- Create: `src/Quartermaster.Documents/Ocr/OcrLanguageService.cs`
- Test: `tests/Quartermaster.Documents.Tests/OcrLanguageServiceTests.cs`

**Interfaces:**
- Consumes: T1 `OcrLanguageCatalog`, `OcrLanguageEntry`.
- Produces:
  - `public enum OcrLanguageState { Bundled, Installed, NotInstalled, Damaged, Queued, Downloading }`
  - `public sealed record OcrLanguageStatus(OcrLanguageEntry Entry, OcrLanguageState State, double Progress, string? Error)`. `Progress` runs 0..1 and is used only while Downloading. `Error` holds the last failure for that code.
  - `public sealed class OcrLanguageService : IDisposable`
    - `public const string FeedVariable = "QUARTERMASTER_OCR_FEED";`
    - `public OcrLanguageService(string? folder = null, HttpMessageHandler? handler = null, Action<string>? log = null)`. `folder` defaults to `AppDataRoot.Sub("OcrLanguages")`. When the `FeedVariable` folder is set, a file is read from `<feed>/<code>.traineddata` instead of the URL.
    - `public string Folder { get; }`
    - `public IReadOnlyList<OcrLanguageStatus> Statuses()`
    - `public IReadOnlyList<string> InstalledCodes()`: the verified downloaded codes, without `eng`.
    - `public string? VerifiedPath(string code)`: the path of a verified downloaded file, else null.
    - `public void Enqueue(IEnumerable<string> codes)`: skips bundled, installed and already-queued codes.
    - `public void CancelAll()`
    - `public Task RemoveAsync(string code)`: awaits `BeforeRemove` handlers, then deletes the file.
    - `public event Action? LanguagesChanged;`: raised after a verified install or a removal, on any thread.
    - `public event Action? StatusChanged;`: raised on progress, state and error changes. Throttled to at most 10 per second per download.
    - `public Func<string, Task>? BeforeRemove { get; set; }`: T3 uses it to wait for a running read.
    - `public Task Idle { get; }`: completes when the queue is empty (for tests).

- [ ] **Step 1: Write the failing tests.** Use a stub handler serving bytes per URL. Make a fake catalog entry by passing `catalog: IReadOnlyList<OcrLanguageEntry>` to an `internal` constructor overload, so the tests use small invented files with known hashes.

```csharp
[Fact] public void Verified_download_is_installed_and_raises_LanguagesChanged()
[Fact] public void Truncated_stream_keeps_nothing()            // handler returns fewer bytes: state NotInstalled, Error set, no .part, no file
[Fact] public void Wrong_hash_file_is_damaged()                 // file in folder with right name, wrong bytes: State Damaged; InstalledCodes excludes it; VerifiedPath null
[Fact] public void Hash_mismatch_download_is_not_kept()
[Fact] public void Stale_part_files_are_deleted_on_start()
[Fact] public void Cancel_deletes_part_and_clears_queue()      // handler blocks until cancelled; after CancelAll: no .part, all NotInstalled
[Fact] public void Queue_runs_one_at_a_time_in_order()
[Fact] public void Http_error_names_status()                    // 404 -> Error contains "404"
[Fact] public void Non_https_redirect_is_refused()              // 302 to http://… -> Error, nothing written
[Fact] public void Write_failure_reports_and_cleans_up()        // folder is a file / read-only: Error set, log called once, next queued code still runs
[Fact] public void Feed_folder_replaces_the_network()           // FeedVariable set: no handler call
[Fact] public void Bundled_eng_cannot_be_removed_or_enqueued()
[Fact] public void User_agent_is_generic()                      // request header User-Agent starts "Quartermaster/", no other custom headers
```

- [ ] **Step 2:** Run `dotnet test tests/Quartermaster.Documents.Tests --filter OcrLanguageServiceTests`. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - One `HttpClient` per service, with the system proxy, `AllowAutoRedirect = false` and redirects followed by hand only to `https`, at most 5.
  - The connect timeout is 30 s (`SocketsHttpHandler.ConnectTimeout`), and `HttpClient.Timeout` is infinite.
  - Stream to `<code>.traineddata.part` with an incremental SHA-256, then `File.Move(part, final, overwrite: true)` only on a length and hash match.
  - Verify installed files once per (path, length, last-write) and cache the result.
- [ ] **Step 4:** Run the filter. Expected: PASS.
- [ ] **Step 5: Commit:** "feat(ocr): language download service".

### Task 3: Recognizers (Tesseract folders, router, language names)

**Files:**
- Modify: `src/Quartermaster.Documents/TesseractRecognizer.cs`, `src/Quartermaster.Documents/OcrEngines.cs`, `src/Quartermaster.Render/Ocr/IOcrRecognizer.cs`
- Create: `src/Quartermaster.Documents/Ocr/OcrRouter.cs`
- Test: `tests/Quartermaster.Documents.Tests/TesseractTests.cs` (extend), `tests/Quartermaster.Documents.Tests/OcrRouterTests.cs`, `tests/Quartermaster.Documents.Tests/OcrEnginesTests.cs` (extend)

**Interfaces:**
- Consumes: T2 `OcrLanguageService` (`InstalledCodes`, `VerifiedPath`, `LanguagesChanged`, `BeforeRemove`), T1 `OcrLanguageCatalog.Find`.
- Produces:
  - `IOcrRecognizer.cs` adds an optional interface, so the plugin is unchanged: `public interface IOcrLanguageList { event Action? LanguagesChanged; string DisplayName(string language); }`.
  - `TesseractRecognizer(string? tessdataPath = null, OcrLanguageService? downloads = null)`. It implements `IOcrLanguageList`. `Languages` is the bundled codes plus `downloads.InstalledCodes()` (bundled wins), recomputed on `downloads.LanguagesChanged`, which it then re-raises.
    - It loads a downloaded code from `downloads.VerifiedPath(code)`.
    - `DisplayName` is the catalog name, or else the code.
    - Removal disposes that language's engine after the gate is free: it sets `downloads.BeforeRemove` to wait on the read gate.
  - `public sealed class OcrRouter : IOcrRecognizer, IOcrLanguageList, IDisposable`, built with `OcrRouter(IOcrRecognizer windows, TesseractRecognizer tesseract)`.
    - `Languages` is the Windows languages, then the Tesseract *downloaded* codes (never the bundled ones).
    - A language in `windows.Languages` goes to Windows, otherwise to Tesseract. `DefaultLanguage = windows.DefaultLanguage`.
    - `Name` is `"Windows"`.
    - `MaxImageDimension` is the minimum of the two.
    - `DisplayName`: Windows tags use `CultureInfo.GetCultureInfo(tag).EnglishName` (the tag itself if that fails), Tesseract tags use the catalog name. When two entries share a display name, it appends " (Windows)" or " (Tesseract)".
  - `OcrEngines.Create(string? baseDir = null, Action<string>? log = null, OcrLanguageService? downloads = null)` keeps every existing rule. When it would return Windows OCR with at least one language and `downloads != null`, it returns `new OcrRouter(windows, new TesseractRecognizer(null, downloads))` instead. The wholesale fallback passes `downloads` to its `TesseractRecognizer`.

- [ ] **Step 1: Write the failing tests.**

```csharp
// TesseractTests (real native engine, an invented page; copy eng.traineddata into a temp "downloads" folder under another code via a fake catalog entry)
[Fact] public void Downloaded_language_appears_after_LanguagesChanged()
[Fact] public void Damaged_file_is_not_a_language()
[Fact] public void Remove_waits_for_running_read()               // hold the gate (HoldGateAsync), RemoveAsync stays pending, release -> completes, language gone
[Fact] public void Bundled_wins_over_a_downloaded_file_of_the_same_code()
// OcrRouterTests (fake IOcrRecognizer for Windows with Languages ["en-US","de-DE"])
[Fact] public void Windows_language_goes_to_windows_downloaded_to_tesseract()
[Fact] public void Bundled_eng_is_not_listed()
[Fact] public void Clashing_display_names_get_engine_suffix()
[Fact] public void Router_raises_LanguagesChanged_from_tesseract()
// OcrEnginesTests (Choose seam)
[Fact] public void Windows_with_languages_and_downloads_gives_router()
[Fact] public void Windows_without_languages_falls_back_to_tesseract_with_downloads()
[Fact] public void Engine_variable_tesseract_and_windows_are_unchanged()
```

- [ ] **Step 2:** Run `dotnet test tests/Quartermaster.Documents.Tests --filter "TesseractTests|OcrRouterTests|OcrEnginesTests"`. Expected: the new tests FAIL.
- [ ] **Step 3: Implement.** Extend the `Choose` seam with a `downloads` parameter, keeping its order of checks.
- [ ] **Step 4:** Run the filter, then all of `tests/Quartermaster.Documents.Tests` and `tests/Quartermaster.Ocr.Tests`. Expected: PASS.
- [ ] **Step 5: Commit:** "feat(ocr): downloaded languages in Tesseract, Windows router".

### Task 4: Settings page and app wiring

**Files:**
- Create: `src/Quartermaster.Desktop/Settings/OcrLanguagesPage.cs`
- Modify: `src/Quartermaster.Desktop/Settings/SettingsWindow.cs` (the page after Folders in `StandardPages`; `SelectPage(int)` or the existing index setter used for the start page), `src/Quartermaster.Desktop/Settings/SettingsContext.cs` (adds `OcrLanguageService? OcrLanguages`), `src/Quartermaster.Desktop/MainWindow.Settings.cs`, `src/Quartermaster.Desktop/MainWindow.Workbench.cs`
- Test: `tests/Quartermaster.Desktop.Tests/OcrLanguagesPageTests.cs`

**Interfaces:**
- Consumes: T2 service, T3 `OcrEngines.Create(…, downloads)`.
- Produces:
  - `MainWindow.ShowSettingsAsync(string? startPage = null)`. `startPage` is a page key; `"ocr-languages"` is the new page. If Settings is already open, the window is activated and switched to that page (keep a reference to the open `SettingsWindow`).
  - `MainWindow.OcrLanguages` (internal, lazy), one `OcrLanguageService` per app, disposed on `Closed`. `AppOcr()` passes it to `OcrEngines.Create`.
  - The page key on `SettingsPage`: `public virtual string Key`, with existing pages keyed by lower-case title; this page's key is `"ocr-languages"` and its title is `"OCR languages"`.

- [ ] **Step 1: Write the failing headless tests** (temp `QUARTERMASTER_LAYOUT_ROOT`, service with stub handler and fake catalog):

```csharp
[AvaloniaFact] public void Page_lists_catalog_with_status_and_size()        // columns Name, Code, Size, Status; eng "Bundled"
[AvaloniaFact] public void Download_selected_rows_queues_and_shows_progress() // status "Downloading n%", then "Installed"
[AvaloniaFact] public void Remove_is_disabled_for_bundled_and_not_installed()
[AvaloniaFact] public void Filter_box_narrows_rows()                          // "germ" -> German only
[AvaloniaFact] public void Source_line_is_verbatim()                          // and the Windows line only when OperatingSystem.IsWindows()
[AvaloniaFact] public void Errors_show_inline()                               // 404 -> row/line text contains "404"
[AvaloniaFact] public void Ok_cancel_and_restore_defaults_do_not_touch_languages()
[AvaloniaFact] public void Closing_during_download_asks_and_cancels_on_yes()  // FakeDialogs answers No -> stays open; Yes -> CancelAll, closes
[AvaloniaFact] public void Show_settings_on_page_opens_ocr_page()
[AvaloniaFact] public void Second_call_while_open_switches_page_not_window()  // ShowSettingsHook counts one window
```

- [ ] **Step 2:** Run `dotnet test tests/Quartermaster.Desktop.Tests --filter "OcrLanguagesPageTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - The page updates from `StatusChanged` through `Dispatcher.UIThread.Post`.
  - Sizes are shown as MB with one decimal.
  - Multi-select is allowed.
  - The close question text is `A language download is still running. Stop it and close Settings?`, with `defaultNo: true`.
  - The page's Apply/OK do nothing to languages.
- [ ] **Step 4:** Run the filter, then all of `tests/Quartermaster.Desktop.Tests` (the Settings page indexes used by existing tests must still hold: the new page is last). Expected: PASS.
- [ ] **Step 5: Commit:** "feat(desktop): Settings > OCR languages page".

### Task 5: OCR panel

**Files:**
- Modify: `src/Quartermaster.Desktop/Workbench/OcrPanel.cs`, plus a `Func<Task>? openLanguages` passed from `WorkbenchWindow` (from `MainWindow`: `() => ShowSettingsAsync("ocr-languages")`)
- Test: `tests/Quartermaster.Desktop.Tests/OcrPanelTests.cs` (extend or create)

**Interfaces:**
- Consumes: T3 `IOcrLanguageList`, T4 `ShowSettingsAsync(string?)`.
- Produces: nothing new for other tasks.

- [ ] **Step 1: Write the failing headless tests** (a fake engine implementing `IOcrRecognizer` and `IOcrLanguageList`):

```csharp
[AvaloniaFact] public void Dropdown_shows_display_names_and_reads_with_tags()  // shows "German", RecognizeAsync gets "deu"
[AvaloniaFact] public void More_languages_item_is_last_and_opens_settings()    // openLanguages called once; selection returns to previous language
[AvaloniaFact] public void LanguagesChanged_refreshes_list_and_keeps_selection()
[AvaloniaFact] public void Removing_selected_language_selects_default()
[AvaloniaFact] public void No_more_languages_item_without_callback()
[AvaloniaFact] public void Empty_engine_still_offers_more_languages()           // Languages empty: status shows UnavailableMessage, "More languages…" enabled
```

- [ ] **Step 2:** Run `dotnet test tests/Quartermaster.Desktop.Tests --filter "OcrPanelTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - Dropdown items are `(Tag, Text)` records whose `ToString` is the text.
  - The "More languages…" item is a sentinel that never stays selected.
  - The panel handles `LanguagesChanged` with a UI-thread post, and unsubscribes on detach.
  - Engines without `IOcrLanguageList` show their tags, as today.
- [ ] **Step 4:** Run the filter, then all of `tests/Quartermaster.Desktop.Tests`. Expected: PASS.
- [ ] **Step 5: Commit:** "feat(desktop): OCR panel display names and More languages".

### Task 6: Docs, screenshots, full suite

**Files:**
- Modify: `THIRD-PARTY-NOTICES` (the tessdata_fast entry names tag 4.1.0 and says further languages are downloaded on request under Apache-2.0), `docs/port/parity.md`, `build/port-screenshots.ps1`, `src/Quartermaster.Desktop/Shell/ScreenshotStates.cs`, `docs/superpowers/RESUME.md`
- Create: `docs/port/stage4c-mac-checklist.md`

- [ ] **Step 1: parity.md.** Add a 4c row and a "Stage-4c deviations" note (a new feature with no WinForms counterpart). Replace every "English only until 4c" (lines ~94, 103, 118, 264, 272, and the 4b Mac checklist line 10) with the 4c state.
- [ ] **Step 2: Mac checklist.** Cover these steps: open Settings → OCR languages; download German; read a German line on a scan; remove German; turn the network off and check the inline message; check Gatekeeper on nothing new (no new natives).
- [ ] **Step 3: Screenshot states.** Add `stage4c-desktop-settings-ocr.png` (the page with a feed folder holding one invented small "language" from a fake catalog) and `stage4c-desktop-ocr-dropdown.png`. The script sets `QUARTERMASTER_OCR_FEED` and a temp layout root. Run the script and look at both pictures.
- [ ] **Step 4:** Run the full `dotnet test Quartermaster.slnx -c Release`. Expected: all pass (known flaky tests may be re-run once).
- [ ] **Step 5: RESUME.md.** Write a CURRENT STATE for 4c, and record that the 4b "Next" is done (PR #3 merged as dc42c9f).
- [ ] **Step 6: Commit:** "docs(port): stage 4c parity, notices, Mac checklist, screenshots".
