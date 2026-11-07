# Piece 7 — Rulebook Search, Scans and OCR Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** In the rules workbench the user can search their own rulebook PDFs for the paragraphs a formula needs (with rule-ref links both ways), and read scanned charts with Windows OCR into a grid they correct and import as a table.

**Architecture:** Pure models in Render (search index/query over page texts via PdfPig; OCR word-box → grid model and edit state). A new Windows-only project `Quartermaster.Ocr` wraps Windows.Media.Ocr and Windows.Data.Pdf. The App adds a search panel, rule-ref links, and a scan pane (image + OCR panel) that imports through the existing Paste table dialog.

**Tech Stack:** C# .NET 10, WinForms, xUnit, PdfPig (Apache-2.0, `UglyToad.PdfPig`), WinRT `Windows.Media.Ocr`, `Windows.Data.Pdf`, `Windows.Graphics.Imaging`.

**Spec:** `docs/superpowers/specs/2026-11-07-piece7-ocr-design.md` (§3, §4, §4a, §4b, §5, §6).

## Global Constraints

- Content boundary (hard): engine ships no real-game content; OCR and the index run on the user's own files, locally, only when asked; nothing uploaded; tests use invented text/images/PDFs generated at test time (PdfPig `PdfDocumentBuilder` for PDFs, System.Drawing for images).
- OCR output is never imported without the user confirming the Paste table dialog.
- Exact copy: buttons `Read text`, `Cancel`, `Import into table…`, `Copy text`, `Open scan…`, `Scan this page`, `Back to PDF`, `Find in rulebook`, `Use as rule ref`; checkbox `Fix common OCR digit mistakes`; list title `Used on this page`; hint `Probably defined on page {n}`; no-language message `Windows has no OCR language installed. Add one in Settings → Time & language → Language (Optical character recognition).`; re-read confirm `Replace your edits with a new reading?`; unsearchable `not searchable`.
- Scan render 200 DPI; upscale regions under 600 px tall to ~2× (cap `OcrEngine.MaxImageDimension`); grayscale before OCR; zoom 25–400 %; OCR panel min width 280 px; multi-page TIFF first page only; EXIF orientation applied.
- Numeric-looking cell (after trim): matches `^[+\-–—]?[\dOolIS.,]+%?$`, or range `n-n`/`n–n`, or dice `NdM(+K)`, and contains at least one real digit. Clean-up only on such cells and only unedited: `O/o→0`, `l/I→1`, `S→5`, `–`/`—`→`-`, thin spaces removed.
- TSV export: tabs between cells, newlines between rows, empty cells allowed, tabs/newlines inside cells → spaces.
- Search index cache: `AppDataRoot/search/{moduleSha}/` (honours `QUARTERMASTER_LAYOUT_ROOT`); rebuilt per document when its SHA-256 changes; indexing off the UI thread with progress.
- Query: words (all must appear on the page), "quoted phrase", case-insensitive, diacritic-insensitive; results grouped by document, ordered by page; snippet ≤ 200 chars with hits marked.
- `DocumentKind` unchanged. App must not reference Relay/AspNetCore. Solo/hot-seat/online/turn files unchanged. Test isolation: never real `%LOCALAPPDATA%`. Build warning-free except MSB3277.

## Review Focus

1. A PDF PdfPig can't read (encrypted, corrupt, image-only) → listed `not searchable`, never crashes the workbench, offers OCR for its pages (Task 3 test: corrupt + image-only PDFs).
2. OCR with no language installed / region larger than max / tiny region → clear message or scaled, no crash (Task 2 tests with injected engine availability).
3. User edits cells then presses Read text again or changes column count → confirm, edits kept on No (Task 1 test).
4. Huge rulebook (500 pages) → index builds in background, search stays < 200 ms per query (Task 3 test with a generated 500-page PDF; time budget generous).
5. Rule ref pointing to a page past the end or to a removed document → link shows "(missing)", clicking does nothing harmful (Task 4 test on the model).

---

### Task 1: OCR grid model (Render, pure)

**Files:** Create `src/Quartermaster.Render/Ocr/OcrWord.cs`, `OcrResultModel.cs`, `OcrGridState.cs`, `OcrCleanup.cs`. Tests `tests/Quartermaster.Render.Tests/OcrGridTests.cs`.

**Interfaces — Produces:**
- `public readonly record struct OcrWord(string Text, double X, double Y, double W, double H);` (image pixels).
- `public static class OcrResultModel { string[][] BuildGrid(IReadOnlyList<OcrWord> words, int? columns = null); string ToTsv(IReadOnlyList<IReadOnlyList<string>> grid); string ToText(IReadOnlyList<OcrWord> words); }` — lines by vertical overlap (≥ 50 % of the smaller height), rows = lines; columns from shared x-gaps wider than 1.2 × median word height; `columns` forces N columns by splitting at the N−1 widest shared gaps.
- `public static class OcrCleanup { bool IsNumericLooking(string cell); string Fix(string cell); }` (rules in Global Constraints).
- `public sealed class OcrGridState { OcrGridState(IReadOnlyList<OcrWord> words, bool fixDigits); string[][] Cells; bool HasEdits; void Edit(int r, int c, string text); bool IsEdited(int r, int c); void SetColumns(int? n); void MergeColumns(int left); void SplitColumn(int col); string ToTsv(); }` — rebuilds from words; clean-up applied on build to unedited cells; `SetColumns/MergeColumns/SplitColumn` drop edits (callers confirm first when `HasEdits`).

- [ ] **Step 1: Failing tests:** `Grid_AlignedTable_3x4`; `Grid_RaggedRows_PadsEmpty`; `Grid_MultiWordCell_StaysOneCell`; `Grid_ForcedColumns_SplitsAtWidestGaps`; `Cleanup_OnlyNumericLooking` (`"1O"→"10"`, `"Oil"` unchanged, `"2d6+l"`→`"2d6+1"`, `"3–5"`→`"3-5"`, `"S"` unchanged); `Cleanup_NotAppliedToEditedCells`; `State_HasEdits_AndRebuildDropsEdits`; `Tsv_RoundTripsThroughTablePaste` (`TablePaste.Parse(tsv, true, true)` gives the same keys/cells).
- [ ] **Step 2–4:** RED → implement → PASS. **Step 5:** Commit `feat(render): OCR grid model`.

### Task 2: OCR service (new Windows project)

**Files:** Create `src/Quartermaster.Ocr/Quartermaster.Ocr.csproj` (`net10.0-windows10.0.19041.0`, references Render), `OcrService.cs`, `ImageLoad.cs`, `PdfPageRender.cs`; add to `Quartermaster.slnx`; App references it. Tests `tests/Quartermaster.Ocr.Tests` (same TFM).

**Interfaces — Consumes:** `OcrWord` (Task 1). **Produces:**
- `public sealed class OcrService { static IReadOnlyList<string> Languages(); static string? DefaultLanguage(); bool Available { get; } Task<IReadOnlyList<OcrWord>> ReadAsync(Bitmap image, Rectangle? region, string language, CancellationToken ct); }` — crop region (image pixels), grayscale, upscale/downscale per Global Constraints, convert via PNG MemoryStream → `BitmapDecoder` → `SoftwareBitmap` (Bgra8, Premultiplied), `OcrEngine.TryCreateFromLanguage`, map word boxes back to original image pixels. Throws `OcrUnavailableException` (message = the no-language copy) when no engine.
- `public static class ImageLoad { Bitmap Load(string path); }` — EXIF orientation applied; TIFF first frame.
- `public static class PdfPageRender { Task<Bitmap> RenderAsync(string pdfPath, int page, int dpi = 200, CancellationToken ct = default); int PageCount(string pdfPath); }` (Windows.Data.Pdf).

- [ ] **Step 1: Failing tests** (skip with reason when `OcrService.Languages()` is empty): `Read_InventedTable_RecoversDigits` (render a 4×6 table of invented two-digit numbers in Segoe UI 11 pt at 200 DPI, bordered and unbordered; ≥ 95 % of digit cells exact after `OcrResultModel.BuildGrid` + `OcrCleanup`); `Read_Region_MapsBoxesToImagePixels`; `Read_TinyRegion_Upscaled_StillReads`; `Read_Cancelled_Throws_OperationCanceled`; `ImageLoad_ExifRotated_Upright`; `PdfRender_GeneratedPdf_PageSizeAt200Dpi` (PDF built with PdfPig `PdfDocumentBuilder`); `NoLanguage_ThrowsWithCopy` (inject an engine factory returning null).
- [ ] **Step 2–4:** RED → implement → PASS. **Step 5:** Commit `feat(ocr): Windows OCR service and page rendering`.

### Task 3: Rulebook index and search (Render)

**Files:** Create `src/Quartermaster.Render/Search/RulebookIndex.cs`, `SearchQuery.cs`, `RuleRefLinks.cs`; add PdfPig package to Render. Tests `tests/Quartermaster.Render.Tests/RulebookSearchTests.cs`.

**Interfaces — Produces:**
- `public sealed record PageText(int Page, string Text);` `public sealed record IndexedDocument(string Entry, string Title, string Sha256, bool Searchable, IReadOnlyList<PageText> Pages);`
- `public sealed class RulebookIndex { static RulebookIndex Empty; static Task<RulebookIndex> BuildAsync(string moduleSha, IReadOnlyList<(string Entry, string Title, Func<Stream> Open)> pdfs, IProgress<double>? progress, CancellationToken ct); IReadOnlyList<IndexedDocument> Documents; IReadOnlyList<SearchHit> Search(string query, int max = 200); int? ProbablyDefinedOn(string term, string entry); }` — cache under `AppDataRoot/search/{moduleSha}/` (one JSON per document SHA, gz); unreadable → `Searchable = false`, no throw.
- `public sealed record SearchHit(string Entry, string Title, int Page, string Snippet, IReadOnlyList<(int Start, int Length)> Marks);`
- `SearchQuery.Parse(string) → (IReadOnlyList<string> Words, IReadOnlyList<string> Phrases)`; matching case- and diacritic-insensitive.
- `public static class RuleRefLinks { IReadOnlyList<(string Kind, string Id, string Name)> UsedOnPage(RulesPack pack, string entry, int page); string Describe(RuleRef r, IReadOnlyList<IndexedDocument> docs); }` — "(missing)" when document or page doesn't exist.
- "Probably defined on page N": first page where the term appears in a line that is short (≤ 8 words) or followed by `:` (PdfPig gives no reliable bold info — call), else first occurrence.

- [ ] **Step 1: Failing tests** (PDFs generated with `PdfDocumentBuilder`, invented text): `Search_Words_AllMustAppearOnPage`; `Search_Phrase_Exact`; `Search_CaseAndDiacriticInsensitive`; `Search_ResultsGroupedOrdered_SnippetMarked`; `Index_Cache_ReusedWhenShaSame_RebuiltWhenChanged`; `Index_CorruptAndImageOnlyPdf_NotSearchable_NoThrow`; `Index_500Pages_QueryUnder200ms`; `DefinedOn_HeadingLine`; `UsedOnPage_ListsFormulasTablesPhases`; `RuleRef_MissingDocOrPage_Describe`.
- [ ] **Step 2–4:** RED → implement → PASS. **Step 5:** Commit `feat(render): rulebook index and search`.

### Task 4: Search UI and rule-ref links (App)

**Files:** Create `src/Quartermaster.App/Workbench/SearchPanel.cs`; modify `WorkbenchWindow.cs` (toolbar search box, Ctrl+Shift+F, index build with progress on open), `PdfViewerControl.cs` (open at page with `#search=` term; `Used on this page` list), `FormulasTab.cs`, `TablesTab.cs`, `PhasesTab.cs` (`Find in rulebook` button; rule-ref link opens the page; `Use as rule ref` from a result fills the item's rule ref via the existing rule-ref path).

**Interfaces — Consumes:** Task 3.

- [ ] **Step 1:** Implement; logic stays in Task 3 models. Smoke `--smoke-search` (fixture module gains an invented two-page PDF written by `SmokeFixtureWriter`): build index, search a term, open the hit page, use as rule ref on a formula, check `Used on this page`; `--screenshot-search <png>`.
- [ ] **Step 2:** Run `--smoke-search`, `--smoke-phases --setup 0`; inspect PNG. **Step 3:** Commit `feat(app): rulebook search and rule-ref links`.

### Task 5: Scan pane and OCR import (App)

**Files:** Create `src/Quartermaster.App/Workbench/ScanPane.cs`, `ImagePane.cs`, `OcrPanel.cs`; modify `WorkbenchWindow.cs` / `PdfViewerControl.cs` (`Open scan…`, `Scan this page`, `Back to PDF`, image documents list from module archive entries `.png .jpg .jpeg .gif .bmp`), `TableDialogs.cs` (`PasteTableDialog.Ask(owner, string tsv, string targetDescription)` overload), `TablesTab.cs` (`ImportGrid(string tsv, string? ruleRefEntry, int? page)`: new table name prompt or selected table; rule ref from source).

**Interfaces — Consumes:** Tasks 1, 2.

- Behaviour per spec §3, §4, §4a: region select in image pixels per (source, page); Read text off the UI thread with Cancel; stale results dropped; language combo; Fix digits checkbox; re-read/columns confirm when edited; Import → Paste dialog prefilled; Copy text; no-language message.
- [ ] **Step 1:** Implement. Smoke `--smoke-ocr` (renders an invented table image to scratch, opens it as a scan, selects the region, Read text, checks grid, imports into a new table through the dialog in smoke mode — auto-confirm — and checks the table; skips with a note when no OCR language); `--screenshot-ocr <png>`.
- [ ] **Step 2:** Run `--smoke-ocr`, `--smoke-search`, `--smoke-phases --setup 0`; inspect PNGs. **Step 3:** Commit `feat(app): scan pane and OCR import`.

### Task 6: Docs

- [ ] README: "Rules workbench" gains search and OCR (what they do, local only, nothing uploaded, OCR needs a Windows OCR language). RESUME piece 7 row + next steps; followups piece 7 section with parked items from the ledger. Third-party notice for PdfPig (Apache-2.0) in the existing licenses/notice file. Full suite PASS. Commit `docs: piece 7 search and OCR`.

## Dependencies

Tasks 1, 2 (after 1's `OcrWord` — Task 2 may define the record itself if run in parallel; ruling: Task 1 owns it, Task 2 merges Task 1 first), 3 independent. Task 4 after 3. Task 5 after 1, 2 and 4 (both touch Workbench files). Task 6 last.
