# Cross-platform Port — Stage 3b (rulebook viewer, charts, Rule ref, PDF/OCR backends) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring module documents and charts to the Avalonia app (`src/Quartermaster.Desktop`) — a cross-platform PDF rulebook viewer with page navigation, zoom and search-hit highlights, text and HTML documents, chart windows, working Rule ref buttons — and build the cross-platform PDF-page, image-file and OCR backends (Tesseract) that the Rules Workbench's search and scan panes will use.

**Architecture:** A new cross-platform project `src/Quartermaster.Documents` (net10.0) wraps the native libraries: PDFtoImage/PDFium for page pictures (one process-wide worker thread, PDFium is not thread-safe), Skia + LibTiff.Net for scan image files, TesseractOcrNet for OCR. Engine-neutral OCR logic (crop, scale, cell reading) moves into `src/Quartermaster.Render/Ocr` behind `IOcrRecognizer`, written on SkiaSharp. Search-hit boxes come from PdfPig glyph rectangles (`Render/Search/PdfHighlights`). Desktop gets `Documents/DocumentViewer` (hosted by `DocumentWindow` now, by the Workbench in stage 4) and `Documents/ChartWindow`.

**Execution waves (parallel worktrees, as the user asked):** W1 {T1, T2, T3, T4}; W2 {T5, T6}; W3 {T7, T8}; W4 {T9}; then final review. Tasks in one wave touch disjoint files except the shared ones listed in Global Constraints.

**Tech Stack:** C# .NET 10, Avalonia 12.1.5 (SkiaSharp 4.154.0), PDFtoImage 5.4.0, PdfPig 0.1.16, TesseractOcrNet 0.1.19, BitMiracle.LibTiff.NET 2.4.649, Avalonia.Headless.XUnit, xUnit 2.9.3, GitHub Actions cross-platform CI.

**Spec:** `docs/superpowers/specs/2026-11-08-cross-platform-design.md` (sections "Windows-only parts and their replacements", "Settled after builder check" → OCR); library research `docs/port/stage3b-research.md`; stage list `docs/port/parity.md` (rows 146, 152, 201, 215, 217; notes 54, 60, 70).

## Scope rulings (made at planning)

- **Search, scan and OCR panes move to stage 4.** In WinForms the rulebook search panel, Scan this page, Open scan…, the image pane and the OCR panel live only inside the Rules Workbench's left pane and feed its Tables/Formulas/Phases tabs. They have no host before the Workbench exists, so their Avalonia UI is built with it in stage 4. Stage 3b builds everything they stand on: the viewer (with toolbar mode, Used on this page, highlights), PDF page pictures, scan image loading and the OCR engine. A player loses nothing: outside the Workbench WinForms offers only document windows, chart windows and Rule ref links.
- **OCR engine:** Tesseract (TesseractOcrNet) on all three OSes in the Avalonia app. T5 runs the spec's benchmark against Windows.Media.Ocr on the invented fixtures and records the numbers. If Windows OCR wins on Windows, stage 4 adds a Windows-only recognizer (the Desktop Windows build multi-targets) behind the same `IOcrRecognizer`; that is not built in 3b. English data (tessdata_fast `eng.traineddata`) ships with the app; downloadable languages come with Settings (stage 4).
- **Viewer look:** WinForms shows PDFs in Edge's built-in viewer (its own toolbar, continuous scroll). The Avalonia viewer shows one page at a time with its own navigation bar (page box "of N", Previous/Next, Zoom out/in, Fit width, Fit page). Recorded as a parity deviation.
- **HTML documents** have no in-app viewer (no WebView on all three OSes): the viewer extracts the file, opens it in the system browser once, and shows `This document opens in your web browser.` with an `Open in browser` button. Parity deviation.
- **Chart and document windows float** (owned, non-modal, one per chart/document, bounds remembered under the WinForms keys `chart:{name}|{file}` and `doc:{entry}`), like the Overview in 3a. No tab docking.

## Global Constraints

- All Global Constraints of `docs/superpowers/plans/2026-11-09-port-stage2b-phases-formulas.md` and `2026-11-09-port-stage3a-ledger-tools.md` apply (WinForms unchanged; off-screen smokes `--smoke 3` exit 0 when WinForms is touched; Desktop free of WinForms/WinRT/App/Ocr project references; labels and punctuation exactly as WinForms; Cmd on Mac via ShortcutGestures; headless only, never visible windows; MacFlag collection for tests touching static registries or IsMac; no real-game content — every PDF, image and table in tests is invented and built in code; noreply commits with both trailers; never push; parity rows updated by the task that changes them).
- `Quartermaster.Documents` targets plain `net10.0`, references `Quartermaster.Render` only, and is listed in `Quartermaster.slnx` and `Quartermaster.CrossPlatform.slnf` (with its test project `tests/Quartermaster.Documents.Tests`). Package versions pinned exactly: `PDFtoImage 5.4.0`, `TesseractOcrNet 0.1.19`, `BitMiracle.LibTiff.NET 2.4.649`. After adding any package, `dotnet list package --include-transitive` must still show SkiaSharp 4.154.0 only.
- Every PDFium call (open, page count, page size, render) runs on the single worker of `PdfPages` (process-wide). Disposing a `PdfPages` or closing a window waits for its running render up to 2 s, then gives up quietly; no task is left faulted and unobserved (pattern of `BoardTiler.EnsureTilesAsync`).
- Rendering never on the UI thread; results reach controls with `Dispatcher.UIThread.Post`; stale results (newer page/zoom/document requested, or window closed) are dropped with `InitGate` tickets and their bitmaps disposed. A shown bitmap is never disposed while visible.
- OCR never downloads anything: Tesseract gets `TessdataPath` = the app's bundled `tessdata` folder (copied to output and publish).
- Self-contained publishes for `win-x64`, `osx-arm64` and `linux-x64` must contain the pdfium and tesseract natives and `tessdata/eng.traineddata`; T9 adds a CI check that fails the publish step otherwise.
- Shared files edited by several tasks of one wave (keep hunks small, appended, localized): `Quartermaster.slnx`, `Quartermaster.CrossPlatform.slnf`, `MainWindow.axaml(.cs)`, `MainWindow.Trees.cs`, `Shell/Dialogs.cs` + `FakeDialogs` + `Program.QuietDialogs`, `docs/port/parity.md`, `THIRD-PARTY-NOTICES.md`. New MainWindow wiring goes in new partials `MainWindow.Charts.cs` (T4) and `MainWindow.Documents.cs` (T7).

## Review Focus

1. A broken PDF (corrupt bytes, encrypted, zero pages) or a missing document entry: the viewer shows `This document could not be opened: {message}`, logs one warning (category `document`, the document title as source, as WinForms `Fail`), and nothing throws → `Corrupt_pdf_shows_message_and_logs_once` (T6), `Open_corrupt_bytes_throws_invalid_data` (T1).
2. Closing a document window, the game, or the app while a page is rendering: no use of a disposed `PdfPages`, no unobserved task exception, close not blocked longer than 2 s → `Dispose_waits_for_running_render` (T1), `Close_during_render_is_quiet` (T6).
3. Very large pages or high zoom (800 % of an A3 page at Retina scale 2): the render DPI is lowered so the bitmap stays ≤ 60,000,000 pixels; no OOM → `Render_clamps_to_60_megapixels` (T1), `Zoom_800_on_large_page_stays_under_cap` (T6).
4. Hi-DPI screens: pages render at `72 × zoom × RenderScaling` DPI so text is crisp on Retina and 150 % Windows → `Render_dpi_follows_render_scaling` (T6).
5. Search highlights on pages that are rotated (/Rotate 90/180/270), have no text layer, or don't contain the term: correct boxes on rotated pages, none otherwise, never an exception → `Rotated_page_boxes_map_to_rendered_orientation`, `Page_without_text_has_no_hits`, `Term_not_on_page_has_no_hits` (T3).

---

### Task 1: Documents project — PDF page pictures and scan image files

**Files:** Create `src/Quartermaster.Documents/Quartermaster.Documents.csproj`, `PdfPages.cs`, `ScanImageLoad.cs`; `tests/Quartermaster.Documents.Tests/Quartermaster.Documents.Tests.csproj` (xUnit 2.9.3, references `Quartermaster.TestFixtures` for Skia natives and `Quartermaster.Render.Tests`' PDF builder is NOT referenced — copy the small `Pdf(params string[][] pages)` helper from `tests/Quartermaster.Render.Tests/RulebookSearchTests.cs:33` into `TestPdfs.cs`), `PdfPagesTests.cs`, `ScanImageLoadTests.cs`; Modify `Quartermaster.slnx`, `Quartermaster.CrossPlatform.slnf`.

**Interfaces — Produces** (namespace `Quartermaster.Documents`):
- `sealed class PdfPages : IDisposable`
  - `static PdfPages Open(string path)` — reads the file into memory (no lock left on the file); throws `InvalidDataException` (message from PDFium, or `"The PDF has no pages."`) for corrupt, encrypted or empty files.
  - `int PageCount { get; }`
  - `SizeD PageSize(int page)` — 1-based, in points, after /Rotate (width/height swapped for 90/270).
  - `Task<SKBitmap> RenderAsync(int page, double dpi, CancellationToken ct)` — 1-based page clamped to `1..PageCount`; white background; annotations drawn; DPI lowered when needed so `width × height ≤ MaxPixels`.
  - `const long MaxPixels = 60_000_000;` `static double ClampDpi(SizeD pagePoints, double dpi)` (pure, tested).
  - `const string CantOpenAsScanMessage = "This PDF can't be opened as a scan.";` (WinForms `PdfPageRender.CantOpenMessage`, used by stage 4).
  - `Dispose()` — cancels queued renders, waits for the running one up to 2 s.
  - All PDFium calls run on one process-wide dedicated worker thread (a `BlockingCollection` loop or equivalent); `RenderAsync`'s task observes its own fault (BoardTiler pattern).
- `static class ScanImageLoad` — port of `src/Quartermaster.Ocr/ImageLoad.cs` to SkiaSharp:
  - `SKBitmap Load(string path, out int pageCount)` and `SKBitmap Load(string path)`; formats png jpg jpeg bmp gif tif tiff (`ScanSessionModel.IsScanFile`); EXIF orientation 1–8 applied (`SKCodec.EncodedOrigin`); TIFF via LibTiff.Net, first page, `pageCount` = TIFF directory count (1 for others); size read from the header first and > 100,000,000 pixels refused with `InvalidDataException(TooLargeMessage)`; the file is not locked after return.
  - `const string TooLargeMessage = "This image is too large to scan (over 100 megapixels).";` `const long MaxPixels = 100_000_000;`

- [ ] **Step 1:** Failing tests. `PdfPagesTests`: `Page_count_and_sizes` (3-page invented PDF, A4 612×792 pts); `Render_page_at_72_dpi_matches_point_size`; `Render_has_white_background_and_dark_text_pixels`; `Render_clamps_to_60_megapixels` (pure `ClampDpi` on a 2384×3370-pt page at 1200 dpi → result pixels ≤ 60,000,000 and within 1 % of it); `Page_out_of_range_is_clamped`; `Open_corrupt_bytes_throws_invalid_data`; `Open_does_not_lock_file` (delete the file after Open succeeds); `Concurrent_renders_from_many_threads_all_succeed` (8 threads × 4 renders on two `PdfPages`); `Dispose_waits_for_running_render` (dispose during a render; no exception escapes, `TaskScheduler.UnobservedTaskException` sees nothing after GC — copy the helper from `ImageAndTileTests.Tiler_failure_nobody_awaits_is_not_an_unobserved_task_exception`); `Rotated_page_size_is_swapped` (hand-written minimal PDF bytes with `/Rotate 90`). `ScanImageLoadTests`: port every test of `tests/Quartermaster.Ocr.Tests/RenderAndLoadTests.cs` that concerns `ImageLoad` (EXIF 1–8 incl. 5 transposed and 7 transversed, multi-page TIFF first page + count, no file lock, small PNG, over 100 MP refused from a crafted PNG header without allocating) using Skia/LibTiff to write the fixtures.
- [ ] **Step 2:** `dotnet test tests/Quartermaster.Documents.Tests` → FAIL (types missing).
- [ ] **Step 3:** Implement. Check `dotnet list src/Quartermaster.Documents package --include-transitive` shows SkiaSharp 4.154.0 only (pin `SkiaSharp 4.154.0` in the csproj if needed). Check `dotnet publish src/Quartermaster.Documents -c Release -r osx-arm64 --self-contained false -o <temp>` and the same for `linux-x64` and `win-x64` place a pdfium native (`libpdfium.dylib` / `libpdfium.so` / `pdfium.dll`) under the output; record the paths in the report.
- [ ] **Step 4:** `dotnet test tests/Quartermaster.Documents.Tests` → PASS; `dotnet build Quartermaster.slnx` → 0 warnings.
- [ ] **Step 5: Commit** `feat(documents): PDF page pictures and scan image loading`.

### Task 2: Engine-neutral OCR reader on SkiaSharp

**Files:** Create `src/Quartermaster.Render/Ocr/IOcrRecognizer.cs`, `src/Quartermaster.Render/Ocr/OcrReader.cs`; Test `tests/Quartermaster.Render.Tests/OcrReaderTests.cs`. WinForms and `src/Quartermaster.Ocr` are not changed.

**Interfaces — Produces** (namespace `Quartermaster.Render.Ocr`):
```csharp
public enum OcrLayout { Block, SingleLine }
public sealed class OcrUnavailableException(string message) : Exception(message);
public interface IOcrRecognizer
{
    string Name { get; }                       // "Tesseract", "Windows"
    int MaxImageDimension { get; }             // largest side the engine accepts
    IReadOnlyList<string> Languages { get; }   // tags the engine has data for, e.g. "eng"
    string? DefaultLanguage { get; }
    string UnavailableMessage { get; }         // shown when Languages is empty
    /// <summary>Word boxes in <paramref name="gray"/>'s pixels. Called off the UI thread; the bitmap is not kept.</summary>
    Task<IReadOnlyList<OcrWord>> RecognizeAsync(SKBitmap gray, OcrLayout layout, string language, CancellationToken ct);
}
```
- `sealed class OcrReader(IOcrRecognizer engine)`:
  - `Task<IReadOnlyList<OcrWord>> ReadAsync(SKBitmap image, SKRectI? region, string language, CancellationToken ct)` — same algorithm and constants as `OcrService.ReadAsync` (`src/Quartermaster.Ocr/OcrService.cs:78`): intersect region, grayscale at native scale (scaled down to `MaxImageDimension`), re-read ~2× upscaled (capped) when no words or median height < 20 px, keep the reading with more characters (ties keep native); boxes mapped back to the original image's pixels. The caller's bitmap is copied before the first await.
  - `Task<IReadOnlyList<OcrWord?>> ReadCellsAsync(SKBitmap image, IReadOnlyList<SKRect> boxes, double padding, string language, CancellationToken ct)` — per box: crop, ink bounds (`InkBounds` as WinForms), blank cells skipped (null), grayscale, ×3 upscale (`CellScale`), white padding `max(4, round(padding × 3))`, recognised with `OcrLayout.SingleLine`; result = the cell's words joined by a space with their union box in image pixels. (The WinForms anchor-strip trick is specific to Windows OCR and stays in `Quartermaster.Ocr`.)
  - Errors: `OcrUnavailableException(engine.UnavailableMessage)` when `Languages` is empty; `OcrUnavailableException($"The OCR language {language} isn't installed.")` when the language is missing; `InvalidDataException("This image is too large to read.")` when an allocation fails.
  - `internal static` helpers with the WinForms names and behaviour: `NativeScale`, `UpscaleFactor`, `MedianHeight`, `InkBounds`, `CharCount`.

- [ ] **Step 1:** Failing tests with a fake recognizer (records the bitmaps and layouts it gets, returns scripted words): port `Scale_NativeFitsEngineMaximum_UpscaleIsCapped`, `MedianHeight_OfWords`, and the non-engine `[Fact]`s of `tests/Quartermaster.Ocr.Tests/OcrServiceTests.cs` (lines 147–232) to Skia; plus `Read_maps_boxes_back_to_image_pixels_for_region_and_downscale`; `Read_rereads_upscaled_when_words_small_and_keeps_more_characters`; `Read_tie_keeps_native`; `Read_copies_bitmap_before_await` (dispose the source right after the call, the read still succeeds); `Cells_blank_cell_is_null_and_not_sent`; `Cells_use_single_line_layout_and_union_box`; `No_languages_throws_unavailable_message`; `Missing_language_message_text`.
- [ ] **Step 2:** FAIL. **Step 3:** implement. **Step 4:** `dotnet test tests/Quartermaster.Render.Tests --filter OcrReader` PASS, then full `dotnet test Quartermaster.slnx` PASS.
- [ ] **Step 5: Commit** `feat(render): engine-neutral OCR reader`.

### Task 3: Search-hit boxes from the PDF text layer

**Files:** Create `src/Quartermaster.Render/Search/PdfHighlights.cs`; Test `tests/Quartermaster.Render.Tests/PdfHighlightsTests.cs`.

**Interfaces — Produces** (namespace `Quartermaster.Render.Search`):
- `sealed class PdfHighlights : IDisposable` — `static PdfHighlights Open(string path)` (PdfPig, file read into memory); `IReadOnlyList<RectD> Find(int page, string? term)` — 1-based page; boxes as **fractions of the rendered page** (0..1, origin top-left, in the page's displayed orientation after /Rotate), one box per matched word piece (a hit spanning two lines gives two boxes); empty for null/blank term, page out of range, no text, or no match. Thread-safe (internal lock); per-page letter lists cached.
- Matching uses the same rules as rulebook search: `SearchQuery.NormaliseWithMap` and `SearchQuery.IndexOfWordStart` on the page text built from `page.Letters` (a space inserted where PdfPig letters are whitespace or where the gap to the previous letter exceeds 0.3 × its font size, or the baseline changes), every occurrence highlighted. The term is what `SearchPanelModel.ViewerSearchTerm(query)` gives.

- [ ] **Step 1:** Failing tests with invented PDFs (copy the `Pdf(...)` helper usage from `RulebookSearchTests`): `Finds_every_occurrence_on_page`; `Word_start_matching_like_search` ("supp" matches "Supply", not "resupply"); `Case_and_diacritics_insensitive`; `Hit_across_line_break_gives_two_boxes`; `Boxes_are_page_fractions_top_left_origin` (a word drawn at a known position: box within 2 % of the expected fractions); `Rotated_page_boxes_map_to_rendered_orientation` (hand-written PDF with `/Rotate 90` and `/Rotate 180`); `Page_without_text_has_no_hits`; `Term_not_on_page_has_no_hits`; `Page_out_of_range_has_no_hits`; `Null_or_blank_term_has_no_hits`.
- [ ] **Step 2:** FAIL. **Step 3:** implement. **Step 4:** PASS (filter, then full Render tests).
- [ ] **Step 5: Commit** `feat(render): PDF search-hit boxes`.

### Task 4: Desktop chart windows

**Files:** Create `src/Quartermaster.Desktop/Documents/ChartWindow.cs`, `src/Quartermaster.Desktop/MainWindow.Charts.cs`; Modify `MainWindow.Trees.cs` (chart nodes open a chart window instead of `LaterVersionText`; document nodes keep it until T7), `docs/port/parity.md` (ChartWindow row, ModuleTreePanel row note); Test `tests/Quartermaster.Desktop.Tests/ChartWindowTests.cs`.

**Interfaces — Produces:**
- `sealed class ChartWindow : Window` — `ChartWindow(ModuleSession s, ChartDef chart)`; title `chart.Name`; toolbar buttons `Fit` and `100%`; canvas draws `s.Images.Get(chart.FileName, zoom)` with the WinForms sampling (linear+mipmaps below 100 %), background = theme AppWorkspace colour; first layout fits; wheel zooms ×1.25 / ×0.8 at the cursor (`Viewport.ZoomAt`); left or middle drag pans with the SizeAll cursor; `Fit()`, `ActualSize()`, `internal Viewport View` for tests. Port of `src/Quartermaster.App/ChartWindow.cs`.
- `MainWindow.OpenChart(ChartDef chart)` — one window per key `chart.Name + "|" + chart.FileName` (activates an existing one), owned by the main window, bounds remembered under `chart:{key}` with `WindowLayoutStore`/`ScreenFit` like the 3a windows; all chart windows close in the session teardown.

- [ ] **Step 1:** Failing headless tests (`[AvaloniaFact]`, invented module with a chart image from `FixtureModule`): `Tree_chart_node_opens_one_window_per_chart`; `Second_activation_activates_existing`; `Fit_shows_whole_image_centered`; `Actual_size_is_zoom_1`; `Wheel_zooms_at_cursor`; `Drag_pans`; `Windows_close_with_session`; `Bounds_remembered_under_chart_key`.
- [ ] **Step 2:** FAIL. **Step 3:** implement. **Step 4:** `dotnet test tests/Quartermaster.Desktop.Tests --filter ChartWindow` PASS, then full Desktop tests PASS.
- [ ] **Step 5: Commit** `feat(desktop): chart windows`.

### Task 5: Tesseract recognizer, bundled English data, OCR benchmark

**Files:** Create `src/Quartermaster.Documents/TesseractRecognizer.cs`, `src/Quartermaster.Documents/tessdata/eng.traineddata` (download from `https://github.com/tesseract-ocr/tessdata_fast/raw/main/eng.traineddata`, record its SHA-256 in the report), `tests/Quartermaster.Documents.Tests/TesseractTests.cs`, `tests/Quartermaster.Documents.Tests/InventedTables.cs`, `tests/Quartermaster.Ocr.Tests/OcrBenchmarkTests.cs`, `docs/port/stage3b-ocr-benchmark.md`; Modify `src/Quartermaster.Documents/Quartermaster.Documents.csproj` (package, `tessdata/**` copied to output and publish), `tests/Quartermaster.Ocr.Tests/Quartermaster.Ocr.Tests.csproj` (reference Documents), `THIRD-PARTY-NOTICES.md` (Tesseract Apache-2.0, Leptonica BSD-2-Clause, TesseractOcrNet MIT, tessdata_fast Apache-2.0).

**Interfaces — Consumes:** `IOcrRecognizer`, `OcrLayout`, `OcrReader` (T2). **Produces:**
- `sealed class TesseractRecognizer : IOcrRecognizer, IDisposable` — `TesseractRecognizer(string? tessdataPath = null)` (default `Path.Combine(AppContext.BaseDirectory, "tessdata")`); `Name = "Tesseract"`; `Languages` = the `*.traineddata` names in the folder; `DefaultLanguage = "eng"` when present else the first; `MaxImageDimension = 10000`; `UnavailableMessage = "No OCR language data was found. Reinstall Quartermaster to restore English."`; `OcrLayout.Block` → page segmentation mode 6, `SingleLine` → 7; one engine per language reused, calls serialized with a lock; never downloads.
- `static class InventedTables` (test helper) — `(SKBitmap Image, string[][] Expected) Table(bool bordered, int seed = 7341)`: the same 6×4 two-digit table as `OcrServiceTests.InventedTable` (cell 200×70, margin 60, 200 DPI), drawn with Skia using a font that exists on all three CI OSes (`SKTypeface.FromFamilyName("DejaVu Sans")` falling back to `SKTypeface.Default`).

- [ ] **Step 1:** Failing tests (`TesseractTests`, run on all OSes): `Languages_include_eng_from_bundled_folder`; `Reads_invented_table_digits` (bordered and unbordered: ≥ 22 of 24 cells exact through `OcrReader.ReadCellsAsync`); `Reads_a_line_of_words` (an invented sentence, ≥ 90 % words exact via `ReadAsync`); `Missing_folder_gives_unavailable_message`; `Parallel_reads_are_serialized_and_succeed`.
- [ ] **Step 2:** FAIL. **Step 3:** implement; the tessdata file is copied to `bin` and to `dotnet publish` output (check `publish/tessdata/eng.traineddata` exists for `-r linux-x64`, `-r osx-arm64` and `-r win-x64`, record native file names in the report).
- [ ] **Step 4:** PASS. Then the benchmark: `OcrBenchmarkTests` (Windows only, skipped unless env `QUARTERMASTER_OCR_BENCH=1`) runs the existing invented tables of `OcrServiceTests` (the GDI-drawn ones, bordered and not, plus the 2× smaller variant) through Windows `OcrService.ReadCellsAsync` and through `OcrReader(new TesseractRecognizer())` on the same pixels, and writes cell accuracy and time per table. Run it once with the env var set and record the table in `docs/port/stage3b-ocr-benchmark.md` with the decision rule from the plan's Scope rulings (do not change the engine choice in 3b).
- [ ] **Step 5: Commit** `feat(documents): Tesseract OCR with bundled English data` and `docs(port): OCR benchmark Tesseract vs Windows`.

### Task 6: Desktop document viewer

**Files:** Create `src/Quartermaster.Desktop/Documents/DocumentViewer.cs`, `Documents/PdfPageView.cs` (the page surface: bitmap + highlight boxes); Modify `src/Quartermaster.Desktop/Quartermaster.Desktop.csproj` (reference `Quartermaster.Documents`), `Shell/Dialogs.cs` + `FakeDialogs` (an injectable `OpenInBrowser(string path)` that uses `TopLevel.Launcher.LaunchFileInfoAsync`; fake records the path); Test `tests/Quartermaster.Desktop.Tests/DocumentViewerTests.cs`.

**Interfaces — Consumes:** `PdfPages` (T1), `PdfHighlights` (T3), `DocumentCache.Extract`, `InitGate`, `SearchPanelModel.ViewerSearchTerm`. **Produces:**
- `sealed class DocumentViewer : UserControl, IDisposable` — `DocumentViewer(ModuleSession s, bool toolbar)` (the `toolbar` part — document list, Used on this page — is T8; T6 accepts the flag and ignores it):
  - `void Open(DocumentDef d, int page = 1, string? search = null)`; `void GoToPage(int page, string? search = null)` (a plain page move clears the search, as WinForms); `DocumentDef? Document`; `int Page`; `int PageCount` (0 for non-PDF); `string? SearchTerm`; `event Action<int>? PageChanged`; `event Action? DocumentOpened`; `double Zoom`; `bool FitWidth`; `Task Rendering` (the latest render, for tests).
  - Navigation bar (always shown for PDFs): `Page:` box (1..PageCount), `of {N}` label, `<` (tip `Previous page`) and `>` (tip `Next page`), `Zoom out`, `Zoom in`, `Fit width`, `Fit page`, zoom label `{n} %`. Keys: PageUp/PageDown, Home/End (first/last page), Primary+Plus/Minus zoom, Primary+0 fit width; Primary+wheel zooms; plain wheel scrolls. Zoom steps ×1.25 within 10 %–800 %. Opens at Fit width; window resizes keep Fit width/Fit page.
  - PDF pages render through `PdfPages.RenderAsync(page, 72 × zoom × TopLevel.RenderScaling)` off the UI thread, debounced 150 ms on zoom/resize, keeping the previous bitmap (scaled) until the new one arrives; at most 3 rendered pages kept; stale results dropped and disposed.
  - Highlights: when `SearchTerm` is set, `PdfHighlights.Find(page, term)` boxes drawn translucent yellow (`#FFFF00` at 40 % alpha) over the page; the first box is scrolled into view.
  - Text documents: read-only, monospace (theme mono font), no wrap, both scroll bars, file text from `DocumentCache.Extract`.
  - HTML documents: extract, call `Dialogs.OpenInBrowser(path)` once per `Open`, show `This document opens in your web browser.` and an `Open in browser` button.
  - Failures: `This document could not be opened: {message}` shown; `s.Log.Add(DiagnosticSeverity.Warning, "document", message, d.Title)` once per failed open.
  - `Dispose()` (and detaching from the visual tree) invalidates the gate, disposes `PdfPages`/`PdfHighlights` (bounded 2 s wait) and every cached bitmap not on screen.

- [ ] **Step 1:** Failing headless tests (invented 3-page PDF and a text and an HTML document added to an invented module through `FixtureModule`): `Opens_pdf_at_page_with_page_count`; `Next_previous_and_page_box_clamp`; `PageUp_PageDown_Home_End`; `Plain_page_move_clears_search`; `Fit_width_then_zoom_steps_and_limits`; `Render_dpi_follows_render_scaling` (headless RenderScaling 2 → render request DPI = 144 × zoom); `Zoom_800_on_large_page_stays_under_cap`; `Stale_render_is_dropped_and_disposed`; `Search_term_draws_highlights_on_hit_page_only`; `Text_document_shows_text_read_only`; `Html_document_opens_browser_once_and_shows_button`; `Corrupt_pdf_shows_message_and_logs_once`; `Close_during_render_is_quiet` (no unobserved task exception after GC); `Page_changed_and_document_opened_events`.
- [ ] **Step 2:** FAIL. **Step 3:** implement. **Step 4:** `--filter DocumentViewer` PASS, full Desktop tests PASS, `dotnet build Quartermaster.slnx` 0 warnings.
- [ ] **Step 5: Commit** `feat(desktop): document viewer with PDF pages, zoom and search highlights`.

### Task 7: Document windows, Rule ref buttons, module-tree documents

**Files:** Create `src/Quartermaster.Desktop/Documents/DocumentWindow.cs`, `src/Quartermaster.Desktop/MainWindow.Documents.cs`; Modify `MainWindow.Trees.cs` (document nodes open a document window; remove `LaterVersionText` when unused), `Formulas/FormulaWindow.cs` (Rule ref enabled and working; remove `RuleRefLaterText`), `Phases/PhasePanel.cs` (rule link enabled and working; remove `RuleViewerTip`), `docs/port/parity.md` (PdfWindow, FormulaDialog, PhasePanel, ModuleTreePanel rows; notes 54 and 60); Test `tests/Quartermaster.Desktop.Tests/DocumentWindowTests.cs`, extend `FormulaWindowTests.cs` and `PhasePanelTests.cs`.

**Interfaces — Consumes:** `DocumentViewer` (T6). **Produces:**
- `sealed class DocumentWindow : Window` — `DocumentWindow(ModuleSession s, DocumentDef d, int page = 1)`; title `d.Title`; hosts `DocumentViewer(s, toolbar: false)`; `DocumentDef Document`; `void GoToPage(int page)`; disposes the viewer on close.
- `MainWindow.OpenDocument(DocumentDef d)` and `public void OpenDocumentAtPage(string documentEntry, int page)` — one window per `EntryName` (reused: `GoToPage` + activate), owned by the main window, bounds under `doc:{entry}`; a missing entry shows the error `The rule reference points to '{documentEntry}', which is not in this module.` (WinForms `MainForm.OpenDocumentAtPage`); all document windows close in the session teardown.
- Formula window Rule ref: enabled when `formula.RuleRefId` resolves in `s.Rules` (re-checked when the formula changes, as WinForms FormulaDialog 132/210), click → `OpenDocumentAtPage(rr.DocumentEntry, rr.Page)` through the owning main window.
- Phase panel: the rule link shows `PhaseTexts.RuleLinkText(r)` when `PhaseTexts.RuleRefOf(v, s.Rules)` is set (hidden otherwise), click → `OpenDocumentAtPage`.

- [ ] **Step 1:** Failing headless tests: `Tree_document_node_opens_one_window_per_entry`; `Open_at_page_reuses_window_and_moves_page`; `Missing_entry_shows_rule_reference_error`; `Document_windows_close_with_session`; `Bounds_remembered_under_doc_key`; `Formula_rule_ref_enabled_only_when_ref_resolves`; `Formula_rule_ref_opens_document_at_page`; `Phase_rule_link_text_and_click_opens_page`; `Phase_without_ref_hides_link`.
- [ ] **Step 2:** FAIL. **Step 3:** implement. **Step 4:** filtered tests PASS, full Desktop tests PASS.
- [ ] **Step 5: Commit** `feat(desktop): document windows and working Rule ref links`.

### Task 8: Viewer toolbar mode (document list, images list, Used on this page)

**Files:** Modify `src/Quartermaster.Desktop/Documents/DocumentViewer.cs` (the `toolbar: true` part); Test `tests/Quartermaster.Desktop.Tests/DocumentViewerToolbarTests.cs`.

**Interfaces — Consumes:** `DocumentViewer` (T6), `SearchPanelModel.UsedOnPageRows` (exists). **Produces** (port of `src/Quartermaster.App/Workbench/PdfViewerControl.cs` lines 43–129 and 314–345; the Workbench in stage 4 is the only host):
- Document list (ComboBox, 180 px, read-only) before the navigation bar: the module's PDF documents (`DocumentKind.Pdf`, in model order); `IReadOnlyList<DocumentDef> PdfDocuments`; choosing one opens it; empty module shows `This module has no PDF documents.`, otherwise `Choose a document above.` until one is opened.
- `const string ImagesSeparator = "──── Images ────";` `void SetImageDocuments(IReadOnlyList<string> titles)` (separator + titles after the PDFs; the separator can't be chosen); `event Action<int>? ImageDocumentChosen` (index into the given titles; the list snaps back to the open PDF).
- `void AddToolItem(Control item)` (appends to the bar).
- `void EnableUsedOnPage()`; `void SetUsedOnPage(IReadOnlyList<(string Kind, string Id, string Text)> items)`; `IReadOnlyList<string> UsedOnPageLines`; `event Action<string, string>? UsedItemActivated` — a 50 px panel under the bar titled `Used on this page` (bold); empty list shows `No document open.` or `Nothing in the rules pack points here.` in the grey text colour; double-click or Enter activates.

- [ ] **Step 1:** Failing headless tests: `Toolbar_lists_only_pdf_documents`; `Choosing_document_opens_it`; `Empty_module_text` and `Choose_document_text`; `Images_after_separator_raise_image_chosen_and_snap_back`; `Separator_cannot_be_chosen`; `Add_tool_item_appends`; `Used_on_page_hidden_until_enabled`; `Used_on_page_empty_texts_grey`; `Used_item_double_click_and_enter_activate`; `Toolbar_false_has_no_document_list`.
- [ ] **Step 2:** FAIL. **Step 3:** implement. **Step 4:** filtered tests PASS, full Desktop tests PASS.
- [ ] **Step 5: Commit** `feat(desktop): viewer document list and Used on this page`.

### Task 9: Screenshots, parity, Mac checklist 3b, native-library check

**Files:** Modify `src/Quartermaster.Desktop/Shell/ScreenshotStates.cs` (states `chart`, `document`, `document-search` — the invented rulebook from `SmokeFixtureWriter.InventedRulebook()` opened with a search term), `build/port-screenshots.ps1`, `docs/port/parity.md` (stage line and deviations list for 3b: one page at a time with own bar; HTML in the browser; chart/document windows float; search/scan/OCR panes moved to stage 4; OCR engine Tesseract pending the benchmark), `.github/workflows/cross-platform.yml` (after each publish step, fail when `pdfium`, `tesseract` natives or `tessdata/eng.traineddata` are missing from the publish folder), `THIRD-PARTY-NOTICES.md` (PDFtoImage MIT, PDFium BSD-3-Clause/Apache-2.0, LibTiff.Net BSD-3-Clause, if not added yet); Create `docs/port/stage3b-mac-checklist.md` (open a chart, zoom/pan; open a PDF from the module tree, page through, zoom, Fit width, Retina sharpness; text and HTML documents; Rule ref from a formula and a phase; first-run Gatekeeper steps covering `libpdfium.dylib` and the tesseract/leptonica dylibs with the existing `xattr` instructions); Update `docs/superpowers/RESUME.md` CURRENT STATE.

- [ ] **Step 1:** Add the screenshot states; run `build/port-screenshots.ps1` for the three new states (headless) and check each PNG is non-empty and shows the window.
- [ ] **Step 2:** Run locally `dotnet publish src/Quartermaster.Desktop -c Release -r <rid> --self-contained -o <temp>` for `win-x64`, `linux-x64`, `osx-arm64`, and run the same check script the CI step uses against each folder → all present.
- [ ] **Step 3:** Full `dotnet test Quartermaster.slnx -c Release` PASS (known flaky tests listed in RESUME may be re-run once); WinForms off-screen `--smoke 3` exit 0.
- [ ] **Step 4: Commit** `docs(port): stage 3b screenshots, parity, Mac checklist and native check`.
