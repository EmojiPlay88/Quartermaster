# Cross-platform Port — Stage 4a (Rules Workbench, rulebook search, scans and OCR) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring the Rules Workbench to the Avalonia app (`src/Quartermaster.Desktop`) — the window and its six tabs, the table dialogs, Tools > Rules Workbench and Tables-tree activation — together with the workbench tools moved from 3b (rulebook search, scan pane, image pane, OCR panel), a Windows-only OCR recognizer, and the three must-fix items carried from the 3b final review.

**Architecture:** Each WinForms tab becomes an Avalonia `UserControl` in `src/Quartermaster.Desktop/Workbench/` that edits the pack only through `ModuleSession.UpdateRules` and the existing Rules/Render edit helpers (`TableEdit`, `FormulaEdit`, `PhaseEdit`, `AlertEdit`, `VisibilityEdit`); the few rules still inline in WinForms (issue list, issue routing, rule-ref reuse, unique names, delete question, index build lifecycle) are copied into Render (`Render/Workbench/WorkbenchRules`, `Render/Search/RulebookSearchSession`). All grids use one small Desktop control, `EditGrid` (no new UI package). Windows OCR lives in a new Windows-only project `src/Quartermaster.OcrWindows` that Desktop never references: the Windows build copies it into an `ocr-windows/` subfolder and `Documents/OcrEngines` loads it at run time (Tesseract otherwise).

**Tech Stack:** C# .NET 10, Avalonia 12.1.5 (SkiaSharp 4.154.0), PdfPig 0.1.16, PDFtoImage 5.4.0, Tesseract.Native 5.5.2.200 (own P/Invoke), Windows.Media.Ocr through the `net10.0-windows10.0.19041.0` SDK projection (CsWinRT), Avalonia.Headless.XUnit, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-08-cross-platform-design.md` (Goals: full parity incl. Workbench and OCR; "Settled after builder check" → OCR per-OS engine, same interface). Stage list and rows: `docs/port/parity.md` rows 140 (Tools > Rules Workbench), 160 (TablesTreePanel), 231 (SearchPanel, ScanPane, ImagePane, OcrPanel), 237 (WorkbenchWindow and tabs), 238 (ColumnsDialog, PasteTableDialog, BlockDialog). OCR decision: `docs/port/stage3b-ocr-benchmark.md`. Behaviour reference: the WinForms sources named in each task (`src/Quartermaster.App/Workbench/*.cs`).

## Scope rulings (made at planning)

- **Stage 4 is split.** 4a = this plan. **4b (not here):** Settings dialog and tabs, Keyboard tab and shortcut capture, Open Autosave, every deferral that says "until Settings" (OCR language downloads included). Nothing in 4a waits for 4b.
- **Windows OCR isolation (design decision).** `src/Quartermaster.OcrWindows` (`net10.0-windows10.0.19041.0`, no WinForms, references Render only) holds `WindowsRecognizer : IOcrRecognizer`. Desktop stays a single `net10.0` target with no reference to it. When Desktop is built for Windows (RID `win-*`, or no RID on a Windows host) its project file builds OcrWindows and copies the plugin's own files (OcrWindows dll, `Microsoft.Windows.SDK.NET.dll`, `WinRT.Runtime.dll`, its `.deps.json`; never Render or SkiaSharp) into `ocr-windows/` as copy-to-output items, so they also reach `Desktop.Tests` and the publish folder. `OcrEngines.Create()` (Documents) loads it in a plugin `AssemblyLoadContext` that takes Render and SkiaSharp from the default context. Mac/Linux builds and `Quartermaster.CrossPlatform.slnf` never touch the project. Rejected: multi-targeting Desktop (the benchmark note's assumption) — it puts WinRT into the Desktop assembly, doubles Desktop and Desktop.Tests builds and breaks cross-RID publish from one host; raw WinRT COM interop from `net10.0` — too much hand-written vtable code. The benchmark doc gets a line recording this change.
- **Engine choice at run time:** not Windows → Tesseract. Windows → Windows OCR, unless env `QUARTERMASTER_OCR_ENGINE=tesseract`, the plugin fails to load, or Windows has no OCR language installed; in those cases Tesseract (bundled English) is used and one line is logged. `QUARTERMASTER_OCR_ENGINE=windows` forces Windows OCR even with no language, so its unavailable message shows.
- **Lone characters on Windows:** `WindowsRecognizer` handles `OcrLayout.SingleLine` with the WinForms "Cell" anchor-word trick (`OcrService.ReadCellsAsync`, `src/Quartermaster.Ocr/OcrService.cs:116-261`), done per cell on Skia. It must equal the WinForms reading on the four benchmark tables (24/24 each).
- **Logic copied, WinForms unchanged.** Rules moved to Render are copies; the WinForms app keeps its own code (it retires at the switch release). `--smoke 3` still runs when a task touches anything WinForms builds against.
- **Window model:** the Workbench is one floating owned window per session (no MDI), 1000×640, bounds remembered under the WinForms key `bench:workbench`, closed with the session. The import-target TaskDialog becomes an Avalonia dialog with the same caption, heading, text and buttons.

## Global Constraints

- All Global Constraints of `docs/superpowers/plans/2026-11-09-port-stage3b-documents.md` (and the 2b/3a plans it names) apply: labels, tooltips, punctuation and status texts exactly as the named WinForms file; same classic dense grey look on all OSes; Cmd instead of Ctrl on macOS through `Input/ShortcutGestures` (parse and `Display`); never open visible windows — Desktop tests and screenshots headless (Avalonia.Headless), WinForms only with `QUARTERMASTER_SHOT_OFFSCREEN=1`; MacFlag collection for tests touching `ShortcutGestures.IsMac` or static registries; rendering and OCR never on the UI thread, results posted back with `Dispatcher.UIThread.Post` and stale ones dropped (InitGate / `ScanSessionModel` tickets) with their bitmaps disposed.
- **Formats unchanged:** rules pack format **4** (`RulesPackIo.CurrentFormat`), save format **7** (`NativeSave.FormatVersion`). The Workbench edits the same `.qrules` the WinForms Workbench writes; no new fields, no new files beside the pack.
- **Desktop references** no WinForms, WebView2, WinRT, App, Ocr or OcrWindows project (OcrWindows is built and copied, never referenced). `Quartermaster.OcrWindows` is listed in `Quartermaster.slnx` only, not in `Quartermaster.CrossPlatform.slnf`.
- **Public repo:** no content from any real game in code, tests, docs or screenshots. Fixtures are invented and built in code (`FixtureModule`, `SmokeFixtureWriter` with `phases.vmod`/`phases.qrules`/`documents.vmod`, the Riverland demo, `InventedTables`).
- **Commits:** noreply identity; every message ends with a blank line, then `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` and `Claude-Session: https://claude.ai/code/session_01ABcS6Hu7i7gpk8tsutH7g6`. Never push.
- **Green bar per task:** `dotnet build Quartermaster.slnx -c Release` with 0 warnings (the known MSB3277 warning of the App project excepted) and the task's test projects green; the last task runs full `dotnet test Quartermaster.slnx -c Release` (known flaky tests in RESUME may be re-run once).
- **Dialogs** go through `IDialogs` (real: `AvaloniaDialogs` in `Shell/Dialogs.cs`; fakes: `FakeDialogs` in `tests/Quartermaster.Desktop.Tests/MainWindowTests.cs`, `Program.QuietDialogs`). Clipboard access goes through `IDialogs` too (T2).
- **Shared files:** `Shell/Dialogs.cs`, `FakeDialogs`, `Program.QuietDialogs` are changed only by T2. `MainWindow.axaml.cs`, `MainWindow.Trees.cs`, `MainWindow.Workbench.cs` only by T10 (T12 adds one line to `MainWindow.Workbench.cs`, in a later wave). `WorkbenchWindow.Search.cs` is owned by T11 and `WorkbenchWindow.Scans.cs` by T12; T10 creates both as stubs. `docs/port/parity.md`, `RESUME.md` only by T13. `Quartermaster.slnx` only by T3.
- **Worktrees:** one per task, cut from the integration branch `port-stage4a` after the previous wave is merged; at most 3 live worktrees (disk, see RESUME).

## Review Focus

1. **Edits must survive a save.** Any edit in any tab, saved with Save Rules Pack and reopened, gives the same pack (format 4), and merely opening the Workbench, switching tabs or selecting items never marks the pack changed → `Edits_round_trip_through_qrules` in T6–T9; `Opening_workbench_leaves_pack_unchanged` (T10).
2. **The pack changes under an open editor** (Open Rules Pack, Undo, another window's edit, the pack closed) while a grid cell is being edited: no crash, no edit applied to the wrong item, tabs rebind, the no-pack prompt shows when the pack goes → `External_change_while_editing_drops_stale_edit` (T1 EditGrid, T6), `Pack_replaced_rebinds_tabs`, `Pack_closed_shows_prompt` (T10).
3. **Closing the Workbench or the game while something runs** (index build, scan render, OCR read, cell re-reads): close takes ≤ 2 s, no unobserved task exception after GC, no disposed bitmap drawn or read → `Close_during_index_build_is_quiet` (T11), `Close_during_scan_load_and_read_is_quiet` (T12), `Dispose_during_read_is_quiet` (T5).
4. **Awkward scan files:** TIFF orientation 5–8, multi-page TIFF, 100 MP limit, EXIF-rotated JPEG, a PDF that cannot be rendered → upright image, page note, refusal text, managed memory bounded to one raster → T4.
5. **No usable OCR engine** (Windows without an OCR language, plugin folder missing or broken, tessdata missing on Mac/Linux): the panel shows the engine's unavailable text, Read text is disabled, nothing throws, Windows falls back to Tesseract → `Choose_*` (T3), `No_languages_shows_unavailable_message` (T5).

---

### Task 1: Workbench foundation — shared rules, EditGrid, rule-ref row

**Depends on:** nothing. **Files:** Create `src/Quartermaster.Render/Workbench/WorkbenchRules.cs`, `src/Quartermaster.Desktop/Workbench/EditGrid.cs`, `src/Quartermaster.Desktop/Workbench/RuleRefRow.cs`; Test `tests/Quartermaster.Render.Tests/WorkbenchRulesTests.cs`, `tests/Quartermaster.Desktop.Tests/EditGridTests.cs`, `tests/Quartermaster.Desktop.Tests/RuleRefRowTests.cs`.

**Interfaces — Produces** (Render, namespace `Quartermaster.Render.Workbench`; copied from `WorkbenchWindow.cs` 179–330 and `PropertiesTab.cs` 260–330):
```csharp
public enum WorkbenchTab { Tables, Formulas, Properties, Phases, Alerts, Visibility }
public sealed record IssueRow(string Severity, string Item, string Problem, RulesIssue? Issue);
public static class WorkbenchRules
{
    public const string NoPackText = "No rules pack is loaded. A rules pack holds the tables and formulas you write yourself.";
    public const string ReadOnlyText = "Read-only: the host's rules pack for this online game. Only the host can change it.";
    public const string NoProblemsText = "No problems found.";
    public const string RefNotAllowedText = "This document can't be used as a rule ref: rule refs name a module document or an opened scan's file name, never a path on this computer.";
    public const string OpenDocumentFirstText = "Open a document in the left pane first, then go to the page to link.";
    public static string Title(RulesPack? pack, bool guest);                 // "Rules Workbench" / "Rules Workbench — {name}" + " (read-only)"
    public static IReadOnlyList<IssueRow> Issues(RulesPack pack, IReadOnlyList<string> mapIds); // RulesValidator; one NoProblemsText row when empty
    public static (WorkbenchTab Tab, string Id)? IssueTarget(RulesPack pack, string entityId); // order: table, formula, phase, alert, property, holder, map owner → Visibility
    public static (RulesPack Pack, RuleRef Ref) MakeOrReuseRef(RulesPack pack, string label, string entry, int page); // reuse by (entry, page), else new Guid "N"
    public static string MissingRefText(string entry);                       // "The reference points to '{entry}', which is not in this module" + (scan file ? " (open it with Open scan… to see it)." : ".")
    public static string UniqueName(IEnumerable<string> names, string stem); // "{stem} {n}", n from count+1, case-insensitive
    public static string DeleteQuestion(string kind, string name, IReadOnlyList<FormulaDef> users); // PropertiesTab.Confirm text, 15 names then "…"
}
```
Desktop (namespace `Quartermaster.Desktop.Workbench`):
```csharp
public enum EditColumnKind { Text, Combo, Check }
public sealed record EditColumn(string Header, EditColumnKind Kind = EditColumnKind.Text, bool ReadOnly = false, double Weight = 100, IReadOnlyList<string>? Items = null);
public sealed class EditGrid : UserControl
{
    public static readonly IBrush ErrorBrush;            // #FFC8D2 (WinForms Pink 255,200,210)
    public void SetColumns(IReadOnlyList<EditColumn> columns);
    public void SetRows(IReadOnlyList<IReadOnlyList<string>> rows);   // check cells "True"/"False"; keeps the current cell when it still exists
    public void SetCellItems(int row, int col, IReadOnlyList<string> items); // per-cell combo list
    public void SetCellReadOnly(int row, int col, bool readOnly);
    public void SetCellError(int row, int col, string? error);       // ErrorBrush background + tooltip; null clears
    public void SetCellTint(int row, int col, IBrush? brush);         // OCR re-read blue, unused-binding grey
    public int RowCount { get; }
    public string Cell(int row, int col);
    public (int Row, int Col)? CurrentCell { get; }
    public void SelectCell(int row, int col);
    public IReadOnlyList<int> SelectedRows { get; }                   // Shift/Primary-click multi-select (Phases Make block)
    public bool MultiSelect { get; set; }
    public void BeginEdit(int row, int col, string? initialText = null);
    public bool IsEditing { get; }
    public event Action<int, int, string>? CellCommitted;             // Enter, Tab, focus loss, combo choice, check toggle
    public event Action<int, int>? CurrentCellChanged;
}
public interface IRuleRefEditor   // WinForms SearchPanel.cs 7–23, same members
{ bool CanUseRuleRef { get; } bool UseRuleRef(Func<string, RuleRef?> make); void RefreshRuleRef();
  Action<string>? FindInRulebook { get; set; } Func<RuleRef?, RefDisplay>? DescribeRef { get; set; } }
public sealed class RuleRefRow : StackPanel  // "Rule ref:" label, link text, "Link to current page", "Find in rulebook" (hidden until a search exists)
{ public void Show(RuleRef? r, Func<RuleRef?, RefDisplay>? describe); public string Text { get; } public bool IsLink { get; } public bool IsGreyed { get; }
  public Button LinkButton { get; } public Button FindButton { get; } public event Action? LinkClicked; }
```
EditGrid keys: F2 or typing starts an edit (typed character kept), Enter commits and moves down, Tab right, Escape cancels, arrows move; double-click edits; one floating editor, cells drawn as text blocks; header row grey as the Ledger header. `SetRows` while editing cancels the edit without raising `CellCommitted` (Review Focus 2).

- [ ] **Step 1:** Failing tests. `WorkbenchRulesTests`: `Title_variants`; `Issues_empty_pack_gives_no_problems_row`; `Issues_rows_carry_severity_item_problem`; `Issue_target_order_and_map_owner_goes_to_visibility`; `Make_ref_reuses_same_entry_and_page`; `Make_ref_new_has_label_entry_page`; `Missing_ref_text_with_and_without_scan_hint`; `Unique_name_skips_taken_case_insensitive`; `Delete_question_lists_15_then_ellipsis`. `EditGridTests` (`[AvaloniaFact]`): `Typing_starts_edit_and_enter_commits_and_moves_down`; `Escape_cancels_without_commit`; `Tab_commits_and_moves_right`; `Read_only_cell_does_not_edit`; `Combo_choice_commits`; `Check_toggle_commits_true_false`; `Error_cell_has_pink_background_and_tooltip`; `Multi_select_rows`; `External_change_while_editing_drops_stale_edit` (SetRows during an edit → no CellCommitted); `Two_thousand_cells_lay_out_under_500_ms`. `RuleRefRowTests`: `None_shows_none_text_not_link`; `Missing_ref_is_greyed_and_not_clickable`; `Link_click_raises_event`; `Find_button_hidden_until_enabled`.
- [ ] **Step 2:** Run `dotnet test tests/Quartermaster.Render.Tests --filter WorkbenchRules` and `dotnet test tests/Quartermaster.Desktop.Tests --filter "EditGrid|RuleRefRow"` → FAIL (types missing).
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Same filters → PASS; `dotnet build Quartermaster.slnx -c Release` → 0 warnings.
- [ ] **Step 5: Commit** `feat(workbench): shared workbench rules, edit grid and rule-ref row`.

### Task 2: Table dialogs, block dialog, clipboard and import-target prompts

**Depends on:** nothing. **Files:** Create `src/Quartermaster.Desktop/Workbench/ColumnsDialog.cs`, `PasteTableDialog.cs`, `BlockDialog.cs`, `ImportTargetDialog.cs`; Modify `src/Quartermaster.Rules/Tables/TablePaste.cs` (add `Problem`), `src/Quartermaster.Desktop/Shell/Dialogs.cs`, `src/Quartermaster.Desktop/Program.cs` (QuietDialogs), `tests/Quartermaster.Desktop.Tests/MainWindowTests.cs` (FakeDialogs: record calls, scripted answers); Test `tests/Quartermaster.Desktop.Tests/TableDialogsTests.cs`, `tests/Quartermaster.Rules.Tests/TablePasteTests.cs` (extend).

**Interfaces — Produces:**
- `TablePaste.Problem(PastedGrid g, bool firstColumnIsKey) → string?` (moved from WinForms `PasteTableDialog.Problem`, text verbatim).
- New `IDialogs` members:
```csharp
Task<IReadOnlyList<string>?> AskColumnsAsync(Window owner, IReadOnlyList<string> keys);
Task<PastedGrid?> PasteTableAsync(Window owner, string text, string? title = null, string? targetDescription = null, bool tsvOnly = false);
Task<(int Count, string Label)?> AskBlockAsync(Window owner);
Task<bool?> AskImportTargetAsync(Window owner, string selectedTable);   // true = replace selected, false = new table, null = cancel
Task<string?> GetClipboardTextAsync(Window owner);
Task SetClipboardTextAsync(Window owner, string text);
Task ShowInfoAsync(Window owner, string message, string caption);
```
  and `AskYesNoAsync` gains `bool defaultNo = false` (delete confirmations use `true`, as WinForms `MessageBoxDefaultButton.Button2`).
- Dialogs ported from `TableDialogs.cs` and `PhasesTab.cs` 813–843: "Columns" 300×260 (one key per line, blank lines dropped); "Paste table from clipboard" / "Import into table" 520×360 with "First row is the column header", "First column is the row key", preview grid (`EditGrid`-free read-only grid is fine), note line red with `TablePaste.Problem` and OK disabled when `tsvOnly`; "Make block" 300×110 (Repeat count 1–99 default 2, Label). Import target: caption "Import into table", heading "Where should the scanned rows go?", text `Into a new table, or into the selected table "{name}" (its rows, columns and cells are replaced).`, buttons `New table…` (default), `Replace "{name}"`, `Cancel`. `QuietDialogs.PasteTableAsync` with `tsvOnly` confirms with default boxes (WinForms smoke auto-confirm); `AskImportTargetAsync` → false.

- [ ] **Step 1:** Failing tests: `Problem_one_column_with_key_text` (Rules); `Columns_dialog_returns_trimmed_nonblank_keys`; `Columns_cancel_returns_null`; `Paste_preview_follows_checkboxes`; `Paste_tsv_only_problem_disables_ok_and_shows_red_note`; `Paste_title_and_note_texts`; `Block_dialog_limits_and_defaults`; `Import_target_buttons_and_default`; `Clipboard_round_trip_headless`; `Quiet_dialogs_auto_confirm_import`.
- [ ] **Step 2:** `dotnet test tests/Quartermaster.Desktop.Tests --filter TableDialogs` → FAIL.
- [ ] **Step 3:** Implement; every existing `IDialogs` implementation compiles.
- [ ] **Step 4:** Filtered tests PASS; full Desktop tests PASS; 0 warnings.
- [ ] **Step 5: Commit** `feat(workbench): table, paste, block and import-target dialogs`.

### Task 3: Windows OCR recognizer and engine selection

**Depends on:** nothing. **Files:** Create `src/Quartermaster.OcrWindows/Quartermaster.OcrWindows.csproj`, `src/Quartermaster.OcrWindows/WindowsRecognizer.cs`, `src/Quartermaster.Documents/OcrEngines.cs`, `src/Quartermaster.Documents/OcrPluginLoadContext.cs`; Modify `Quartermaster.slnx`, `src/Quartermaster.Desktop/Quartermaster.Desktop.csproj` (plugin build + copy items, Windows only), `tests/Quartermaster.Ocr.Tests/Quartermaster.Ocr.Tests.csproj` (reference OcrWindows; link `../Quartermaster.Documents.Tests/InventedTables.cs`), `tests/Quartermaster.Ocr.Tests/OcrBenchmarkTests.cs` (third engine column), `docs/port/stage3b-ocr-benchmark.md` (4a row + isolation note), `build/check-publish-natives.sh` (win-x64 publish must contain `ocr-windows/Quartermaster.OcrWindows.dll`, `WinRT.Runtime.dll`, `Microsoft.Windows.SDK.NET.dll`; osx/linux publishes must not contain `ocr-windows/`); Test `tests/Quartermaster.Ocr.Tests/WindowsRecognizerTests.cs`, `tests/Quartermaster.Documents.Tests/OcrEnginesTests.cs`, `tests/Quartermaster.Desktop.Tests/OcrPluginTests.cs`.

**Interfaces — Consumes:** `IOcrRecognizer`, `OcrLayout`, `OcrReader` (Render/Ocr), `TesseractRecognizer`. **Produces:**
- `Quartermaster.OcrWindows.WindowsRecognizer : IOcrRecognizer, IDisposable` — `Name = "Windows"`; `Languages` = `OcrEngine.AvailableRecognizerLanguages` tags; `DefaultLanguage` = user-profile engine's tag, else first, else null; `MaxImageDimension = (int)OcrEngine.MaxImageDimension`; `UnavailableMessage` = WinForms `OcrService.NoLanguageMessage` verbatim; `Block` → whole bitmap, words from lines; `SingleLine` → anchor trick (Scope rulings) with Skia, typeface "Segoe UI"; gray `SKBitmap` → `SoftwareBitmap` Bgra8 through a byte buffer; one engine per language; calls serialized with `SemaphoreSlim(1,1).WaitAsync(ct)`; `Dispose` sets the disposed flag first, later calls throw `ObjectDisposedException`.
- `Quartermaster.Documents.OcrEngines`:
```csharp
public const string EngineVariable = "QUARTERMASTER_OCR_ENGINE";   // "tesseract" | "windows"
public const string PluginFolder = "ocr-windows";
public static IOcrRecognizer Create(string? baseDir = null, Action<string>? log = null);   // baseDir default AppContext.BaseDirectory
internal static IOcrRecognizer Choose(bool isWindows, string? engineVariable, Func<IOcrRecognizer?> loadWindows, Func<IOcrRecognizer> tesseract, Action<string>? log);
internal static IOcrRecognizer? LoadWindows(string baseDir, Action<string>? log);   // null (one log line) on any failure
```
  `OcrPluginLoadContext(string pluginDll)` — `AssemblyDependencyResolver`; returns null (default context) for `Quartermaster.Render`, `SkiaSharp` and any assembly already loaded in the default context, so `IOcrRecognizer` keeps one identity.

- [ ] **Step 1: Spike the build wiring** (no test): in `Quartermaster.Desktop.csproj`, condition `('$(RuntimeIdentifier)' == '' and $([MSBuild]::IsOSPlatform('Windows'))) or $(RuntimeIdentifier.StartsWith('win'))`; build OcrWindows (e.g. `ProjectReference` with `ReferenceOutputAssembly="false"`, `SkipGetTargetFrameworkProperties="true"`, or an `<MSBuild>` call in a target) and add its output as `None`/`Content` items with `Link="ocr-windows\%(Filename)%(Extension)"` and `CopyToOutputDirectory="PreserveNewest"`. Accept only when: restore and build give 0 warnings (no NU1201/NU1702); `src/Quartermaster.Desktop/bin/Release/net10.0/ocr-windows/` and `tests/Quartermaster.Desktop.Tests/bin/Release/net10.0/ocr-windows/` hold the plugin files and no Render/SkiaSharp dll; `dotnet publish src/Quartermaster.Desktop -c Release -r win-x64 --self-contained` has `ocr-windows/`; `-r osx-arm64` and `-r linux-x64` publishes have none. Record the chosen mechanism and file list in the report.
- [ ] **Step 2:** Failing tests. `OcrEnginesTests` (all OSes, fakes): `Choose_not_windows_is_tesseract`; `Choose_windows_with_language_is_windows`; `Choose_windows_without_language_falls_back_and_disposes`; `Choose_plugin_missing_falls_back_and_logs_once`; `Choose_env_tesseract_wins`; `Choose_env_windows_forces_windows_without_language`. `WindowsRecognizerTests` (Ocr.Tests, Windows): `Reads_skia_invented_table_digits` (`InventedTables.Table`, bordered and not, ≥ 23/24 through `OcrReader.ReadCellsAsync`); `Single_lone_digit_cell_is_read` (cells "3", "7", "8"); `Block_reads_a_line_of_words`; `Language_tags_and_default`; `Read_after_dispose_throws_object_disposed`; `Queued_reads_are_serialized`. `OcrPluginTests` (Desktop.Tests, `Assert.Skip` off Windows): `Create_loads_windows_recognizer_from_ocr_windows_folder` (Name "Windows", `IOcrRecognizer` from the default context's Render); `Create_with_env_tesseract_gives_tesseract`.
- [ ] **Step 3:** `dotnet test tests/Quartermaster.Documents.Tests --filter OcrEngines`, `dotnet test tests/Quartermaster.Ocr.Tests --filter WindowsRecognizer`, `dotnet test tests/Quartermaster.Desktop.Tests --filter OcrPlugin` → FAIL. Implement. → PASS.
- [ ] **Step 4:** Benchmark: run `OcrBenchmarkTests` with `QUARTERMASTER_OCR_BENCH=1`; the new column (`OcrReader(new WindowsRecognizer())`) must read 24/24 on all four GDI tables. If it does not, stop and report BLOCKED with the numbers (do not tune silently). Append the row, the run-time selection rule and the isolation decision to `docs/port/stage3b-ocr-benchmark.md`; extend `build/check-publish-natives.sh` and verify it locally against the three publish folders from Step 1.
- [ ] **Step 5: Commit** `feat(ocr): Windows OCR recognizer as a run-time plugin with Tesseract fallback`.

### Task 4: Scan loading and the image pane (carry b: TIFF orientation and buffers)

**Depends on:** nothing. **Files:** Create `src/Quartermaster.Documents/ScanLoader.cs`, `src/Quartermaster.Desktop/Workbench/ImagePane.cs`; Modify `src/Quartermaster.Documents/ScanImageLoad.cs`; Test `tests/Quartermaster.Documents.Tests/ScanLoaderTests.cs`, extend `ScanImageLoadTests.cs`, `tests/Quartermaster.Desktop.Tests/ImagePaneTests.cs`.

**Interfaces — Consumes:** `PdfPages`, `ScanImageLoad`, `ScanCache.Extract`, `ScanSource`, `ScanSessionModel` (zoom/region helpers), `RegionEdit`. **Produces:**
```csharp
public sealed record ScanImage(SKBitmap Bitmap, int PageCount, int Page, string? Note);   // Documents; Note "This TIFF has {n} pages; only the first is shown."
public sealed class ScanLoader(Func<string, Stream> openEntry, string archiveSha) : IDisposable
{
    public const int ScanDpi = 200;
    public Task<ScanImage> LoadAsync(ScanSource src, int page, CancellationToken ct);   // PDF page clamped, rendered at 200 DPI via PdfPages; image via ScanImageLoad on a worker
    public static string FailureMessage(ScanSource src, int page, Exception ex);         // the ScanPane.LoadCoreAsync mapping; PDFs: PdfPages.CantOpenAsScanMessage
    public int ExtractCount { get; }                                                     // module entries extracted once per (sha, entry); a failed one retried
}
public sealed class ImagePane : Control   // Desktop; port of ImagePane.cs on SKBitmap (the pane never disposes the bitmap)
{
    public SKBitmap? Image { get; } public System.Drawing.Rectangle? SelectedRegion { get; } public double Zoom { get; } public bool FitMode { get; } public string? Message { get; set; }
    public void SetImage(SKBitmap? image, System.Drawing.Rectangle? region); public void SetRegion(System.Drawing.Rectangle? region); public void CommitRegion(System.Drawing.Rectangle? region);
    public void FitToWindow(); public void SetZoom(double zoom, Avalonia.Point? anchor = null);
    public event Action<System.Drawing.Rectangle?>? SelectionChanged; public event Action? ZoomChanged;
}
```
ImagePane: drag draws a region (`RegionEdit.FromDrag`, ≥ `MinRegionSide`), handles 7 px (`RegionEdit.HitTest/Adjust`, matching cursors), Primary+wheel zooms at the cursor (`ScanSessionModel.StepZoom/ScrollForZoom`), plain wheel scrolls, zoom 25–400 %, Fit, message text centred when no image, image drawn at zoom × RenderScaling through the existing Skia draw-operation pattern (`Map/SkiaDrawOperation.cs`).
ScanImageLoad fix: the TIFF raster is read without library orientation; the TIFF Orientation tag (1–8) is applied while copying into the destination bitmap (ABGR→BGRA converted in that copy), so a load holds one managed raster plus one bitmap.

- [ ] **Step 1:** Failing tests. `ScanImageLoadTests`: `Tiff_orientations_1_to_8_are_upright` (an asymmetric 3×2 pattern written with each tag value); `Tiff_load_allocates_one_raster` (2000×2000 TIFF: `GC.GetAllocatedBytesForCurrentThread` delta ≤ 4·w·h + 1 MB). `ScanLoaderTests`: `Pdf_page_renders_at_200_dpi_and_clamps_page`; `Multi_page_tiff_note`; `Module_entry_extracted_once`; `Failed_extraction_is_retried`; `Failure_messages_match_winforms` (invalid data, not found, out of range, other PDF failure, other image failure); `Cancelled_load_disposes_bitmap`. `ImagePaneTests`: `Drag_selects_region_in_image_pixels`; `Tiny_drag_selects_nothing`; `Handle_drag_resizes_and_move_moves`; `Primary_wheel_zooms_at_cursor_within_limits`; `Fit_shows_whole_image`; `Message_shown_without_image`; `Commit_region_raises_selection_changed`.
- [ ] **Step 2:** Run `--filter "ScanImageLoad|ScanLoader"` (Documents.Tests) and `--filter ImagePane` (Desktop.Tests) → FAIL.
- [ ] **Step 3:** Implement. **Step 4:** PASS; full Documents tests PASS; 0 warnings.
- [ ] **Step 5: Commit** `feat(workbench): scan loader and image pane; TIFF orientation 5-8 and one-raster load`.

### Task 5: OCR panel (carry c: Tesseract after Dispose, queued reads)

**Depends on:** T1 (EditGrid), T2 (dialogs, clipboard). **Files:** Create `src/Quartermaster.Desktop/Workbench/OcrPanel.cs`; Modify `src/Quartermaster.Documents/TesseractRecognizer.cs`; Test `tests/Quartermaster.Desktop.Tests/OcrPanelTests.cs`, extend `tests/Quartermaster.Documents.Tests/TesseractTests.cs`.

**Interfaces — Consumes:** `OcrReader`, `OcrGridState`, `OcrResultModel`, `ScanSessionModel` tickets, `EditGrid`, `IDialogs`. **Produces** (port of `OcrPanel.cs`, all texts verbatim):
```csharp
public sealed record OcrTarget(SKBitmap Image, System.Drawing.Rectangle? Region, ScanSource Source, int Page);
public sealed class OcrPanel : UserControl
{
    public const int MinPanelWidth = 280;
    public const string ReplaceEditsQuestion = "Replace your edits with a new reading?";
    public const string SomeEmptyText = "Some cells came back empty (single characters are often missed): type them in.";
    public OcrPanel(IOcrRecognizer engine, ScanSessionModel model, IDialogs dialogs);
    public Func<OcrTarget?>? Target { get; set; }
    public event Action<string, ScanSource, int>? ImportRequested;   // TSV, source, page
    public bool IsReading { get; } public string StatusText { get; } public string[][] Cells { get; } public bool IsReread(int r, int c); public bool HasGrid { get; } public Task LastRead { get; }
    public Task ReadTextAsync(); public void CancelRead(string? status = null); public void ShowNote(string note); public void ShowGridTab();
}
```
Layout: Language combo (engine `Languages`, default `DefaultLanguage`), Read text, Cancel, "Fix common OCR digit mistakes" (checked), tabs Text / Grid, Columns: combo (Auto, 1…), Merge, Split, Import into table…, Copy text (through `IDialogs.SetClipboardTextAsync`). Read: confirm when the grid has edits; `ReadAsync` on the target, then the empty-cell re-read pass (`ReadCellsAsync` with `MedianWordHeight`), re-read cells tinted blue, report text as WinForms `ReportReading`; `OcrUnavailableException`/`InvalidDataException` messages in the status (red); empty `Languages` → `engine.UnavailableMessage`, Read disabled.
Tesseract fix: `_disposed` set (volatile) before trying to take the gate, so a read never starts after `Dispose` returned even when Dispose timed out waiting; the gate becomes `SemaphoreSlim(1,1)` awaited with `WaitAsync(ct)` before the native call runs on a worker, so queued reads hold no pool thread; the last running read frees the engines when Dispose gave up. An `internal` test hook may hold the gate.

- [ ] **Step 1:** Failing tests. `TesseractTests`: `Read_after_timed_out_dispose_throws_object_disposed`; `Queued_reads_hold_no_pool_threads` (gate held: 64 queued reads raise `ThreadPool.ThreadCount` by ≤ 4 and a fresh `Task.Run` completes within 500 ms; release → all 64 complete); `Cancelled_queued_read_leaves_queue`. `OcrPanelTests` (fake recognizer with scripted words; `InventedTables` image): `Read_whole_image_fills_text_and_grid`; `Read_region_status_text`; `Empty_cells_reread_and_tinted`; `Edited_grid_asks_before_replacing`; `Cancel_sets_status_and_drops_result`; `Stale_read_dropped_after_new_target`; `Fix_digits_toggle_rebuilds`; `Columns_merge_split`; `Import_raises_tsv_source_page`; `Copy_text_uses_clipboard`; `No_languages_shows_unavailable_message`; `Unavailable_exception_shows_red_status`; `Dispose_during_read_is_quiet`.
- [ ] **Step 2:** `--filter Tesseract` (Documents.Tests) and `--filter OcrPanel` (Desktop.Tests) → FAIL.
- [ ] **Step 3:** Implement. **Step 4:** PASS; 0 warnings.
- [ ] **Step 5: Commit** `feat(workbench): OCR panel; Tesseract reads refused after dispose and queued without blocking`.

### Task 6: Tables tab

**Depends on:** T1, T2. **Files:** Create `src/Quartermaster.Desktop/Workbench/TablesTab.cs`; Test `tests/Quartermaster.Desktop.Tests/TablesTabTests.cs`.

**Interfaces — Consumes:** `EditGrid`, `RuleRefRow`, `IRuleRefEditor`, `WorkbenchRules`, `TableEdit`, `NameRules`, `ScanSessionModel.SuggestTableName/KindFor/TargetDescription`, the T2 dialogs. **Produces** (port of `TablesTab.cs`):
```csharp
public sealed class TablesTab : UserControl, IRuleRefEditor
{
    public TablesTab(ModuleSession s, IDialogs dialogs, Func<string, RuleRef?> linkCurrentPage, Action<RuleRef>? showRef = null);
    public string? SelectedTableId { get; } public void SelectTable(string id);
    public Func<string, string, int, RuleRef?>? MakeRuleRef { get; set; }     // (label, entry, page), set by the workbench
    public Task<string?> ImportGridAsync(string tsv, string? ruleRefEntry, int? page);  // DoImport flow; returns the table id
}
```
Left list with New… / Rename… / Delete; Kind: OneD / TwoD / Dice; rule-ref row; bar Add row, Remove row, Add column, Remove column, Columns…, Paste from clipboard…; grid (row key column + one per column key); notes box committed on leaving it. Structural edits through `TableEdit`; refusals for guest / replay with the WinForms texts via `ShowInfoAsync`. Owner window for dialogs = `TopLevel.GetTopLevel(this)`.

- [ ] **Step 1:** Failing tests (invented pack): `Lists_tables_by_name_and_selects`; `New_rename_delete_with_name_rules`; `Kind_change_commits`; `Add_remove_rows_and_columns`; `Cell_edit_commits_to_pack`; `Columns_dialog_sets_keys`; `Paste_from_clipboard_applies_grid`; `Notes_commit_on_leave`; `Link_to_current_page_sets_rule_ref`; `Rule_ref_click_calls_show_ref`; `Import_into_new_table_with_scan_rule_ref`; `Import_into_existing_keeps_its_rule_ref`; `Import_refused_without_pack_or_as_guest_or_replaying`; `External_change_while_editing_drops_stale_edit`; `Edits_round_trip_through_qrules` (save with `RulesPackIo.Save`, reload, records equal, format 4).
- [ ] **Step 2:** `--filter TablesTab` → FAIL. **Step 3:** Implement. **Step 4:** PASS; 0 warnings.
- [ ] **Step 5: Commit** `feat(workbench): tables tab`.

### Task 7: Formulas tab

**Depends on:** T1, T2. **Files:** Create `src/Quartermaster.Desktop/Workbench/FormulasTab.cs`, `FormulasTab.Grids.cs`; Test `tests/Quartermaster.Desktop.Tests/FormulasTabTests.cs`.

**Interfaces — Produces** (port of `FormulasTab.cs` + `FormulasTab.Grids.cs`): `public sealed partial class FormulasTab : UserControl, IRuleRefEditor` — `FormulasTab(ModuleSession s, IDialogs dialogs, Func<string, RuleRef?> linkCurrentPage, Action<RuleRef>? showRef = null)`; `string? SelectedFormulaId`; `void SelectFormula(string id)`. Text and Applies-to commit 400 ms after the last keystroke (WinForms `_timer`), errors from `FormulaEdit.Analyze/ErrorText/SyntaxProblem` shown under the boxes; bindings grid (variable with " (unused)", kind, target combo per cell from `FormulaEdit.TargetOptions`, details parsed by `FormulaEdit.ParseDetails`, caption), Remove unused; outputs grid (target, holder, property, op) with Add/Remove; the "unused configured bindings" question once per variable (`_declined`), through `AskYesNoAsync`.

- [ ] **Step 1:** Failing tests: `Select_and_rename_formula`; `Text_error_shown_and_not_committed`; `Text_commits_after_debounce_and_syncs_bindings`; `Applies_to_syntax_problem_shown`; `Binding_kind_and_target_commit`; `Binding_details_parse_error_keeps_text`; `Remove_unused_bindings`; `Unused_question_asked_once_per_variable`; `Outputs_add_remove_and_edit`; `Rule_ref_link_and_find_in_rulebook`; `Edits_round_trip_through_qrules`.
- [ ] **Step 2:** `--filter FormulasTab` → FAIL. **Step 3:** Implement. **Step 4:** PASS; 0 warnings.
- [ ] **Step 5: Commit** `feat(workbench): formulas tab`.

### Task 8: Phases tab

**Depends on:** T1, T2. **Files:** Create `src/Quartermaster.Desktop/Workbench/PhasesTab.cs`; Test `tests/Quartermaster.Desktop.Tests/PhasesTabTests.cs`.

**Interfaces — Produces** (port of `PhasesTab.cs` 1–812): `public sealed class PhasesTab : UserControl, IRuleRefEditor` — `PhasesTab(ModuleSession s, IDialogs dialogs, Func<string, RuleRef?> linkCurrentPage, Action<RuleRef>? showRef = null)`; `string? SelectedPhaseId`; `void SelectPhase(string id)`; `int PhaseCount`. Phase list (multi-select, blocks shown as in WinForms), Add / Remove / Duplicate / Move up / Move down, Make block (`AskBlockAsync` → `PhaseEdit.MakeBlock` over the selected range) / Remove block, sides (Add / Rename / Remove, side unit filter with syntax error line), name / instructions / notes, the phase grids with expression error cells (`SetCellError` from `FormulaEdit.SyntaxProblem`), rule-ref row.

- [ ] **Step 1:** Failing tests: `Add_remove_duplicate_move_phase`; `Make_block_over_selection_asks_count_and_label`; `Remove_block`; `Side_add_rename_remove_and_filter_error`; `Instructions_and_notes_commit`; `Grid_expression_error_cell`; `Select_phase_by_id`; `Rule_ref_link`; `Edits_round_trip_through_qrules`.
- [ ] **Step 2:** `--filter PhasesTab` → FAIL. **Step 3:** Implement. **Step 4:** PASS; 0 warnings.
- [ ] **Step 5: Commit** `feat(workbench): phases tab`.

### Task 9: Properties, Alerts and Visibility tabs

**Depends on:** T1, T2. **Files:** Create `src/Quartermaster.Desktop/Workbench/PropertiesTab.cs`, `AlertsTab.cs`, `VisibilityTab.cs`; Test `tests/Quartermaster.Desktop.Tests/PropertiesTabTests.cs`, `AlertsTabTests.cs`, `VisibilityTabTests.cs`.

**Interfaces — Produces** (ports of the three WinForms files; tab caption of Properties is "Properties & Holders"):
- `PropertiesTab(ModuleSession s, IDialogs dialogs)`; `void SelectProperty(string id)`; `void SelectHolder(string id)` — properties grid (Applies-to column pink with tooltip on syntax problem, notes), holders grid (row 0 Global read-only, name rules), Add / Delete with `WorkbenchRules.UniqueName` and `DeleteQuestion` (`defaultNo: true`), status line.
- `AlertsTab(ModuleSession s, IDialogs dialogs)`; `string? SelectedAlertId`; `void SelectAlert(string id)` — list with Add / Remove / Up / Down (`AlertEdit`), fields, match status `AlertEdit.MatchStatus(probe, s.View, pack)`.
- `VisibilityTab(ModuleSession s)` — property visibility, private rolls, holder and map owners (`VisibilityEdit`), maps listed with " (org display)".

- [ ] **Step 1:** Failing tests: Properties `Add_property_unique_name`; `Applies_to_error_cell_pink_with_tooltip`; `Holder_name_rule_refused_keeps_old`; `Delete_asks_with_formula_users_default_no`; `Select_property_and_holder`; Alerts `Add_remove_move_alert`; `Match_status_counts_viewer_visible_only`; `Select_alert`; Visibility `Property_visibility_and_private_rolls_commit`; `Holder_and_map_owner_commit`; `Org_display_suffix`; and `Edits_round_trip_through_qrules` in each test class.
- [ ] **Step 2:** `--filter "PropertiesTab|AlertsTab|VisibilityTab"` → FAIL. **Step 3:** Implement. **Step 4:** PASS; 0 warnings.
- [ ] **Step 5: Commit** `feat(workbench): properties, alerts and visibility tabs`.

### Task 10: Workbench window, Tools menu, Tables-tree activation

**Depends on:** T1, T6, T7, T8, T9. **Files:** Create `src/Quartermaster.Desktop/Workbench/WorkbenchWindow.cs`, `WorkbenchWindow.Search.cs` (stub), `WorkbenchWindow.Scans.cs` (stub), `src/Quartermaster.Desktop/MainWindow.Workbench.cs`; Modify `MainWindow.axaml.cs` (Tools menu: `&Rules Workbench` first, gate `tools.workbench`, then Inventory, Notes as now; update the comment), `MainWindow.Trees.cs` (TableActivated / FormulaActivated → `ShowWorkbenchItem`); Test `tests/Quartermaster.Desktop.Tests/WorkbenchWindowTests.cs`.

**Interfaces — Consumes:** the tabs (T6–T9), `DocumentViewer(s, toolbar: true, dialogs)`, `WorkbenchRules`. **Produces:**
```csharp
public sealed partial class WorkbenchWindow : Window
{
    public WorkbenchWindow(ModuleSession s, IDialogs dialogs, Func<Task> newPack, Func<Task> openPack, IOcrRecognizer? ocr = null);
    public DocumentViewer Viewer { get; } public TabControl Tabs { get; }
    public TablesTab Tables { get; } public FormulasTab Formulas { get; } public PropertiesTab Properties { get; } public PhasesTab Phases { get; } public AlertsTab Alerts { get; } public VisibilityTab Visibility { get; }
    public void ShowTable(string id); public void ShowFormula(string id); public void ShowPhase(string id);   // unknown id: no-op; works before the window is shown
    public RuleRef? LinkCurrentPage(string label);
    public IReadOnlyList<IssueRow> IssueRows { get; } public void GoToIssue(int index);
    public string StatusText { get; } public bool PromptShown { get; } public bool ReadOnlyShown { get; }
    // shared with T11/T12 (all in WorkbenchWindow.cs):
    internal readonly ScanSessionModel ScanModel = new();
    internal Grid LeftPane { get; }          // row 0: viewer (T12 adds the scan pane here), row 1: GridSplitter, row 2: search panel slot
    internal StackPanel ToolbarRow { get; }   // "Rulebooks:" label, SearchBox, SearchButton, separator, OpenScanButton (T10 adds them disabled)
    internal TextBox SearchBox { get; } internal Button SearchButton { get; } internal Button OpenScanButton { get; }
    internal IEnumerable<IRuleRefEditor> Editors { get; } internal IRuleRefEditor? ActiveEditor { get; }
    internal Task Warn(string text);        // ShowInfoAsync(this, text, "Rules Workbench")
    internal RuleRef? RefFor(string label, string entry, int page);   // IsAllowedRefEntry + MakeOrReuseRef via s.UpdateRules
    internal void SetStatus(string text);
}
// Stubs created here, filled by T11 (Search.cs) and T12 (Scans.cs); T10 calls them at these points:
//   Search.cs: void WireSearch() (ctor), void RestartIndex() (after an opened PDF), void OnSearchClosed() (Closed), Func<RuleRef?, RefDisplay>? Describe (null)
//   Scans.cs:  void WireScans() (ctor), bool TryLinkScanPage(string label, out RuleRef? r) (false), bool TryShowScanRule(RuleRef r) (false),
//              bool TryShowOpenedScan(string key, int page) (false), void ScanFirstPage(string entry) (no-op),
//              IEnumerable<(string Key, string Title, Func<Stream> Open)> OpenedScanPdfs() (empty), IReadOnlyList<(string Entry, string Title)> ExtraRefDocs() (empty), void OnScansClosed()
```
- `MainWindow.ShowWorkbench()` (Tools menu, Ctrl/Cmd+Shift+W through the command layer) — needs a module; refuses with `ModuleSession.ReplayingText` in the status while replaying; one window per session via the existing `Open<T>` helper, owned, 1000×640, bounds under `bench:workbench`, closed in the session teardown; `newPack`/`openPack` run the existing `NewRulesPack`/`OpenRulesPack` flows. `ShowWorkbenchItem(Action<WorkbenchWindow>)` opens then acts; the tree's activations call `ShowTable`/`ShowFormula`.
- Window (port of `WorkbenchWindow.cs` 65–260, 296–340, 498–505): no-pack prompt (`NoPackText`, `New rules pack…`, `Open…`; hidden for guests); title `WorkbenchRules.Title`; left pane 40 % of the width; issues list (Severity 70, Item 140, Problem) validated 300 ms after the last `RulesChanged`, double-click → `IssueTarget`; read-only banner (`ReadOnlyText`, background 255,240,200) and tab contents disabled for guests; status bar; `ShowRule` opens module documents in the viewer at the page, else `TryShowScanRule`, else `Warn(MissingRefText)`; `LinkCurrentPage` → `TryLinkScanPage`, else viewer page, else `Warn(OpenDocumentFirstText)`; Used on this page refreshed on page/document/rules changes and activates Formula/Table/Phase; prunes `ScanCache` and `RulebookIndex.PruneCache(sha)` once in the background; tabs get `MakeRuleRef = RefFor`.

- [ ] **Step 1:** Failing tests: `Tools_menu_opens_one_workbench_per_session`; `Shortcut_primary_shift_w_opens_workbench` (MacFlag: Cmd on Mac); `Refused_while_replaying`; `Tree_table_and_formula_activation_open_at_item`; `Show_table_before_shown_selects_it`; `No_pack_prompt_buttons_run_flows`; `Pack_closed_shows_prompt`; `Pack_replaced_rebinds_tabs`; `Title_follows_pack_name`; `Issues_debounced_and_go_to_issue_selects_tab_and_item`; `Link_current_page_without_document_warns`; `Rule_ref_to_module_document_opens_viewer_page`; `Rule_ref_to_missing_entry_warns`; `Used_on_page_activates_formula`; `Opening_workbench_leaves_pack_unchanged`; `Closes_with_session_and_remembers_bounds`.
- [ ] **Step 2:** `--filter WorkbenchWindow` → FAIL. **Step 3:** Implement. **Step 4:** PASS; full Desktop tests PASS; 0 warnings.
- [ ] **Step 5: Commit** `feat(workbench): workbench window, Tools menu and tables-tree activation`.

### Task 11: Rulebook search (carry a: rotated-page crop-box highlights)

**Depends on:** T10. **Files:** Create `src/Quartermaster.Render/Search/RulebookSearchSession.cs`, `src/Quartermaster.Desktop/Workbench/SearchPanel.cs`; Fill `src/Quartermaster.Desktop/Workbench/WorkbenchWindow.Search.cs`; Modify `src/Quartermaster.Render/Search/PdfHighlights.cs`; Test `tests/Quartermaster.Render.Tests/RulebookSearchSessionTests.cs`, extend `PdfHighlightsTests.cs`, `tests/Quartermaster.Documents.Tests/HighlightInkTests.cs`, `tests/Quartermaster.Desktop.Tests/SearchPanelTests.cs`, `WorkbenchSearchTests.cs`.

**Interfaces — Consumes:** `RulebookIndex.BuildAsync`, `SearchPanelModel` (`Build`, `ViewerSearchTerm`, `DescribeRef`, `UsedOnPageRows`, `NotSearchableText`, `NoTextStatus`), T10 stubs. **Produces:**
```csharp
public sealed class RulebookSearchSession : IDisposable   // Render.Search; port of StartIndex/BuildIndexAsync/RunSearch (WorkbenchWindow.cs 405–505)
{
    public RulebookSearchSession(string archiveSha, IReadOnlyList<(string Entry, string Title, Func<Stream> Open)> modulePdfs);
    public void Rebuild(IReadOnlyList<(string Key, string Title, Func<Stream> Open)> memoryOnly);  // supersedes a running build; old index usable until ready
    public RulebookIndex? Index { get; } public string Status { get; } public event Action? Changed;   // Changed may fire on any thread
    public SearchResult Run(string query);    // record SearchResult(string Title, IReadOnlyList<SearchRow>? Rows, bool Pending) — pending runs when ready
}
public sealed class SearchPanel : UserControl   // Desktop; port of SearchPanel.cs 47–282
{ public const double ExpandedHeight = 190, HeaderHeight = 24; public bool Collapsed { get; set; } public string Title { get; set; } public void ShowRows(IReadOnlyList<SearchRow> rows);
  public event Action<string, int>? HitActivated, UseAsRuleRef; public event Action<string>? NotSearchableActivated, ScanFirstPage; public event Action? CollapsedChanged; public Func<bool>? CanUseRuleRef { get; set; } }
```
Status texts verbatim: `Indexing rulebooks… {pct} %`, `This module has no PDF rulebooks to search.`, `Rulebooks indexed: {n} searchable[, {bad} {NotSearchableText}].`, `The rulebooks could not be indexed: {message}`. Titles: `Rulebook search: {q} ({n} page|pages)`, `Rulebook search: {q} (runs when indexing finishes)`, `Rulebook search: type in the box above ({shortcut})` and placeholder `Search the rulebooks ({shortcut})` with `ShortcutGestures.Display("Ctrl+Shift+F")`. Workbench: Enter / Search runs; Primary+Shift+F focuses and selects the box; a hit opens the viewer at the page with `ViewerSearchTerm(lastQuery)` (highlights) or `TryShowOpenedScan`; a not-searchable module PDF opens page 1 with `NoTextStatus`; Use as rule ref (`ActiveEditor`, scan hits through `ScanModel.RefForSearchHit`); editors get `FindInRulebook` and `DescribeRef`; `RestartIndex` rebuilds with `OpenedScanPdfs()`; the panel keeps the user's height but never more than half the left pane; failures logged as `Rulebook search failed: {message}` (category `rules`).
PdfHighlights fix: the crop-box origin is applied for every /Rotate value (it is now applied only at 0°), so boxes on rotated pages with an offset crop box land on the rendered words.

- [ ] **Step 1:** Failing tests. `PdfHighlightsTests`: `Rotated_page_with_offset_crop_box_maps_boxes` (90/180/270, CropBox `[50 60 562 752]`, within 2 %). `HighlightInkTests` (Documents): `Highlight_boxes_cover_rendered_ink_on_rotated_cropped_pages` (render with `PdfPages`, dark pixels inside each box). `RulebookSearchSessionTests`: `No_pdfs_status`; `Progress_then_indexed_status`; `Query_before_ready_runs_when_ready`; `Rebuild_supersedes_and_keeps_old_index_until_ready`; `Failure_status_and_empty_index`; `Dispose_during_build_is_quiet`. `SearchPanelTests`: `Rows_headings_hits_bold_not_searchable_grey`; `Enter_and_double_click_activate`; `Context_menu_use_as_rule_ref_enabled_by_callback`; `Scan_page_1_only_on_not_searchable`; `Collapse_toggle`. `WorkbenchSearchTests` (`phases.vmod`-style fixture with `SmokeFixtureWriter.InventedRulebook()`): `Search_supply_lists_pages_and_title`; `Hit_opens_viewer_page_with_highlights`; `Use_as_rule_ref_sets_selected_formula_ref`; `Find_in_rulebook_runs_search`; `Search_shortcut_is_cmd_shift_f_on_mac` (MacFlag); `Close_during_index_build_is_quiet`.
- [ ] **Step 2:** Run the filters (`PdfHighlights|RulebookSearchSession` Render, `HighlightInk` Documents, `SearchPanel|WorkbenchSearch` Desktop) → FAIL.
- [ ] **Step 3:** Implement. **Step 4:** PASS; 0 warnings.
- [ ] **Step 5: Commit** `feat(workbench): rulebook search; highlights on rotated cropped pages`.

### Task 12: Scan pane and scan/OCR wiring in the Workbench

**Depends on:** T2, T3, T4, T5, T10. **Files:** Create `src/Quartermaster.Desktop/Workbench/ScanPane.cs`; Fill `src/Quartermaster.Desktop/Workbench/WorkbenchWindow.Scans.cs`; Modify `src/Quartermaster.Desktop/MainWindow.Workbench.cs` (one app-wide `OcrEngines.Create(log: m => s.Log.Add(DiagnosticSeverity.Info, "ocr", m, ""))` created on first Workbench open, disposed at app exit, passed to the window); Test `tests/Quartermaster.Desktop.Tests/ScanPaneTests.cs`, `WorkbenchScanTests.cs`.

**Interfaces — Consumes:** `ScanLoader`, `ImagePane`, `OcrPanel`, `OcrEngines`, `ScanSessionModel`, `TablesTab.ImportGridAsync`, T10 stubs. **Produces** (port of `ScanPane.cs` and `WorkbenchWindow.cs` 512–756):
```csharp
public sealed class ScanPane : UserControl, IDisposable
{
    public ScanPane(ModuleSession s, ScanSessionModel model, ScanLoader loader, IOcrRecognizer ocr, IDialogs dialogs);
    public ScanSource? Source { get; } public int Page { get; } public SKBitmap? Image { get; } public OcrPanel Ocr { get; } public ImagePane Pane { get; } public Task Loading { get; } public string? Note { get; }
    public event Action? BackToPdf; public event Action<int>? DocumentChosen; public event Action<string, ScanSource, int>? ImportRequested;
    public void SetDocuments(IReadOnlyList<string> pdfTitles, IReadOnlyList<string> imageTitles); public void MarkSelected(int hostIndex);
    public Task ShowSource(ScanSource source, int page = 1);   // cancels a read ("Cancelled: another image was opened."), drops stale loads, keeps the shown bitmap until replaced
}
```
Toolbar as WinForms ScanPane (Back to PDF, document list, Page:, of N, `<` `>`, `−` `+` with tips `Zoom out ({Primary}+wheel)` / `Zoom in ({Primary}+wheel)`, zoom %); image left, OCR panel right (≥ `MinPanelWidth`); region remembered per (source, page) in `ScanModel`; a region change cancels the read ("Cancelled: the region changed."). Workbench: `Scan this page` added to the viewer bar (tip `Render this page as an image (200 DPI) to read a chart with OCR`, enabled for PDFs only); `Open scan…` (tip `Open a scan or photo of a chart (image or PDF) to read it with OCR`) → `OpenFileAsync(title "Open scan", filter from ScanSessionModel.OpenScanFilter)`; the viewer's Images list (module images ≥ 64 KB, opened scans " (opened)", toggle `ScanSessionModel.ImagesToggleText`); the WinForms warnings for not found, wrong type, same-name scan, module document with the same name on import; showing a scan widens the left pane to 60 %; Back to PDF returns to the scanned page; import → `Tables.ImportGridAsync(tsv, ScanSessionModel.RuleRefFor(...))` then `ShowTable`; rule refs to module images and opened scans open them (`TryShowScanRule`); `ExtraRefDocs` = module images + opened scans; an opened PDF calls `RestartIndex()`; failures logged as `Scan failed: {message}`.

- [ ] **Step 1:** Failing tests (fake recognizer scripted from `InventedTables`; an invented PNG and a two-page invented PDF written to a temp folder): `ScanPaneTests`: `Pdf_page_shows_with_page_controls`; `Image_hides_page_controls`; `Next_previous_clamp`; `Region_remembered_per_page`; `Region_change_cancels_read`; `Load_failure_message_shown`; `Back_to_pdf_event`. `WorkbenchScanTests`: `Scan_this_page_shows_scan_pane_at_viewer_page`; `Scan_this_page_disabled_for_non_pdf`; `Open_scan_image_reads_and_imports_table_with_scan_rule_ref` (the WinForms `OcrSmoke` flow: open, region, read, grid, import, table and `scan:{file}` ref); `Opened_pdf_joins_search`; `Images_list_toggle_shows_small_images`; `Rule_ref_to_opened_scan_reopens_it`; `Back_to_pdf_returns_to_scanned_page`; `Same_name_scan_warns`; `Close_during_scan_load_and_read_is_quiet`.
- [ ] **Step 2:** `--filter "ScanPane|WorkbenchScan"` → FAIL. **Step 3:** Implement. **Step 4:** PASS; full Desktop tests PASS; 0 warnings.
- [ ] **Step 5: Commit** `feat(workbench): scan pane, Open scan, Scan this page and OCR import`.

### Task 13: Screenshots, parity, Mac checklist, RESUME

**Depends on:** all. **Files:** Modify `src/Quartermaster.Desktop/Shell/ScreenshotStates.cs` (states `workbench` — Tables tab, Riverland pack; `workbench-formulas`; `workbench-phases`; `workbench-search` — `phases.vmod`/`phases.qrules`, query `supply`; `workbench-scan` — an invented table image drawn with Skia, opened and read with the platform recognizer), `build/port-screenshots.ps1` (stage 4a Desktop shots and the WinForms equivalents `--screenshot-workbench --workbench-tab tables|formulas|phases`, `--screenshot-search`, `--screenshot-ocr`, off-screen), `docs/port/parity.md` (stage line 11: 4 split into 4a/4b; rows 140, 160, 231, 237, 238 ticked for Windows with notes; a "Stage-4a deviations" section: floating Workbench window under `bench:workbench`; Avalonia import-target dialog; own edit grid look; Windows OCR via the `ocr-windows` plugin with the fallback rules, Tesseract English-only on Mac/Linux until 4b; Cmd shortcuts in texts; anything the screenshot pass shows; update the 2b note "Tables-tree activation is ignored until stage 4" and the 3b notes that say search/scan/OCR moved to 4), `docs/superpowers/RESUME.md` (CURRENT STATE); Create `docs/port/stage4a-mac-checklist.md` (open the Workbench from Tools and the Tables tree; edit a table, formula, phase, property, alert, visibility and save/reopen the pack, also opened in WinForms; Cmd+Shift+W and Cmd+Shift+F; search the invented rulebook and open a hit with highlights; Scan this page; Open scan… a photo and a multi-page TIFF; read a region, fix a cell, import into a new table; Tesseract natives load without Gatekeeper prompts, else the existing `xattr` steps).

- [ ] **Step 1:** Add the states; run `build/port-screenshots.ps1` (headless Desktop, off-screen WinForms) and check each new PNG is non-empty and shows the window; note visible differences as deviations.
- [ ] **Step 2:** `dotnet publish src/Quartermaster.Desktop -c Release -r <rid> --self-contained -o <temp>` for `win-x64`, `linux-x64`, `osx-arm64`; run `build/check-publish-natives.sh` on each → pass (ocr-windows only in win-x64).
- [ ] **Step 3:** Full `dotnet test Quartermaster.slnx -c Release` PASS (flaky tests per RESUME re-run once); `dotnet build Quartermaster.CrossPlatform.slnf -c Release` 0 warnings; WinForms off-screen `--smoke 3` exit 0.
- [ ] **Step 4: Commit** `docs(port): stage 4a screenshots, parity, Mac checklist and resume`.

## Waves

At most three worktrees at once; each wave starts from `port-stage4a` after the previous wave is merged.

| Wave | Tasks | Needs merged | Shared-file notes |
|---|---|---|---|
| W1 | T1, T2, T3 | — | disjoint; only T2 edits `Shell/Dialogs.cs`/fakes, only T3 edits `Quartermaster.slnx` and `Desktop.csproj` |
| W2 | T4, T5, T6 | T1, T2 (T4 needs none) | T4 and T5 edit different Documents files (`ScanImageLoad.cs` vs `TesseractRecognizer.cs`) |
| W3 | T7, T8, T9 | T1, T2 | disjoint new files |
| W4 | T10 | T1, T6–T9 | owns `MainWindow*.cs` edits and creates the two stubs |
| W5 | T11, T12 | T10 (+T3, T4, T5 for T12) | T11 owns `WorkbenchWindow.Search.cs`, T12 owns `WorkbenchWindow.Scans.cs`; neither edits `WorkbenchWindow.cs` (ask the controller if a hook is missing) |
| W6 | T13 | all | only task touching `parity.md`, `RESUME.md` |

If disk allows more than three worktrees, T4 can move to W1.
