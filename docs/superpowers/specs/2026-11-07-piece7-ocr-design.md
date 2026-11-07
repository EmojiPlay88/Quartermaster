# Piece 7 — Scans and OCR — Design

*Date: 2026-11-07. Builds on pieces 1–6c as merged to `master`. Implements master spec §8.3 ("Scans and OCR") and the workbench line "OCR for scans" (§1 item 6). Everything not stated in the master spec is the assistant's call, marked "(call)".*

## 1. Intent

The user builds their rules pack by hand in the rules workbench: tables, formulas, notes. Many rulebooks and charts are **scans** (image-only PDFs, the module's chart PNGs, phone photos). Today the user must retype every number. Piece 7 lets them **select a region of a scanned page, read it with OCR, correct the result beside the image, and import it** into a table (or copy it as text) — never without confirming.

Content boundary (hard, unchanged): the engine ships no real-game content. OCR runs on the user's own files, on their PC, only when they ask; nothing is uploaded; tests use invented images rendered at test time.

## 2. Goals, non-goals, anti-goals

**Goals:** open a scanned page or an image in the workbench; drag a rectangle; get text and a grid preview; edit it; import into a new or existing table through the existing paste path; copy text to the clipboard.

**Non-goals:** automatic full-book OCR; OCR of whole PDFs in the background; handwriting; non-Windows OCR engines; layout analysis beyond one rectangular region; downloading OCR language packs from inside the app.

**Anti-goals:** importing anything without the user pressing Import; sending images anywhere; slowing down the workbench when OCR isn't used; a separate table editor that drifts from the existing paste rules.

## 3. Where it lives

- **Workbench left pane gains an "Image" mode** (call): the document combo also lists the module's image documents and any image/PDF the user adds with **Open scan…** (file dialog: `.png .jpg .jpeg .bmp .gif .tif .tiff .pdf`). PDFs still open in the WebView2 viewer; a toolbar button **Scan this page** renders the current PDF page to an image (Windows.Data.Pdf, at 200 DPI, call) and switches to the Image pane for that page. Images open directly in the Image pane.
- **Image pane**: zoom (fit, 50–400 %), pan, and a **Select region** tool (drag a rectangle; handles to adjust). The last region per image is remembered for the session (call).
- **OCR panel** (beside the image, right of it inside the left pane, call): **Read text** button, a language combo (installed OCR languages; default = user profile language, else first available), a read-only/editable **Text** box and an editable **Grid** preview (DataGridView) with **Columns** (auto / fixed count) and **Merge**/**Split** helpers for a selected column. Buttons: **Import into table…**, **Copy text**.
- Opened scans are session-only unless the user also adds them to the rules pack's documents (existing "Add document" path) (call).

## 4. Behaviour

- **Read text** runs Windows.Media.Ocr on the selected region (or the whole image when nothing is selected) off the UI thread; a small progress note; result in about a second for a typical chart. If no OCR language is installed: `Windows has no OCR language installed. Add one in Settings → Time & language → Language (Optical character recognition).` If the region is larger than the engine's maximum (`OcrEngine.MaxImageDimension`), it is scaled down to fit (call).
- **Grid preview**: built from the OCR words' bounding boxes — words grouped into lines (by vertical overlap), lines into rows, and words into columns by x-position gaps (column boundaries = gaps wider than a threshold, shared across rows; call). **Columns: N** forces N columns (k-means-like split on x, call). The grid is the user's to edit; cells keep what the user typed.
- **Number clean-up** (call, shown as a checkbox **Fix common OCR digit mistakes**, default on): in cells that look numeric, `O→0`, `l/I→1`, `S→5` only when surrounded by digits; `–`/`—`→`-`; thin spaces removed. Never applied to cells the user edited.
- **Import into table…** converts the grid to TSV and opens the existing **Paste table** dialog (`PasteTableDialog`, `TablePaste.Parse`, same header/key checkboxes) targeting: a new table (name prompt) or the table selected in the Tables tab. The rule ref of a new table is filled from the document + page (existing "Link to current page" data) (call).
- **Copy text** copies the Text box (or the grid as TSV when the grid tab is active).
- OCR output is never imported without the user confirming the Paste dialog (spec §8.3).

## 4a. Decisions settled after the builder check (all calls)

1. **WinRT plumbing.** The app is unpackaged; `Windows.Media.Ocr` and `Windows.Data.Pdf` work unpackaged through the TFM's projections (verified on the dev PC: OCR languages `en-US`, `ru` installed). Async ops are awaited with the projection's `AsTask()`. `Bitmap → SoftwareBitmap`: encode to PNG in a `MemoryStream` → `AsRandomAccessStream()` → `BitmapDecoder.CreateAsync` → `GetSoftwareBitmapAsync(Bgra8, Premultiplied)`. Plan Task 1 is a working service with an integration test before any UI. With no OCR language installed, the feature stays visible and **Read text** shows the install message.
2. **Preprocessing.** Before OCR the region is converted to grayscale and **upscaled** when its height is under 600 px (to about 2×, capped at `OcrEngine.MaxImageDimension`); larger regions are scaled down to fit. Ruled lines are not detected; the grid comes from word boxes only. Acceptance: the invented test table (bordered and unbordered variants, Segoe UI 11 pt rendered at 200 DPI, digits and short words) recovers ≥ 95 % of digit cells; real charts are best-effort, corrected by the user.
3. **Regions** are stored in image-pixel coordinates per (source, page) for the session. "Scan this page" always renders at 200 DPI, so regions stay valid. A region is cleared when its image changes size.
4. **Documents.** `DocumentKind` is not changed. The image list is built separately: image entries in the module archive (`.png .jpg .jpeg .gif .bmp`, from the user's own module at runtime) plus files opened with **Open scan…**. `PdfViewerControl` keeps the PDF view; a new `ScanPane` (image pane + OCR panel with a vertical splitter, minimum 280 px for the panel) replaces it in the left pane while an image is shown; a toolbar button **Back to PDF** returns.
5. **Import.** `PasteTableDialog` gains a constructor/`Ask` overload taking prefilled TSV and a target description; the "new table" name prompt and the target choice stay in `TablesTab` (a new `ImportGrid(string tsv, string? ruleRefDoc, int? page)` entry point that the OCR panel calls). The grid is exported as TSV (tabs, empty cells allowed, no quoting; tabs/newlines inside cells replaced by spaces). Loose images give a rule ref of the file name with no page.
6. **Edits.** Clean-up is applied when the grid is built, visibly. Every cell tracks "edited by user"; **Read text** again replaces the grid only after a confirm if any cell was edited (`Replace your edits with a new reading?`). Merge/Split/forced columns rebuild from the OCR words and also confirm when edits exist. So `OcrResultModel` stays pure (words → grid); edit tracking lives in a separate `OcrGridState`.
7. **Numeric-looking cell**: after trimming, matches `^[+\-–—]?[\dOolIS.,]+%?$`, or a range `n-n`/`n–n`, or dice `NdM(+K)` — and contains at least one real digit. Only such cells get `O/o→0`, `l/I→1`, `S→5`, dashes → `-`, thin spaces removed. Words like "Oil" stay untouched.
8. **Tests/CI.** No CI exists; tests and smokes run locally. OCR integration tests and `--smoke-ocr` skip with a clear note when no OCR language is installed. PDF rendering is tested with a one-page PDF generated in the test from a tiny hand-written PDF byte template (no new dependency).
9. **Threading.** One read at a time; **Cancel** while reading; changing the image or region cancels the running read; results for a stale request are dropped.
10. **Smaller calls.** Language: profile language if installed, else `en-US` if installed, else the first installed. Multi-page TIFF: first page only, with a note. EXIF orientation is applied when loading photos. Zoom 25–400 %. Region cleared when the image changes.

## 5. Architecture (for the builder)

- **Render (testable, no WinRT):** `OcrResultModel` — words with boxes → lines → rows → columns → `string[][]` grid; digit clean-up; TSV export; forced column count; merge/split column. Pure, unit-tested with synthetic word boxes.
- **App (WinRT):** `OcrService` wraps `Windows.Media.Ocr.OcrEngine` (`TryCreateFromLanguage`, `RecognizeAsync` on a `SoftwareBitmap`) and `Windows.Data.Pdf` page rendering; converts `System.Drawing.Bitmap` ⇄ `SoftwareBitmap`; returns words with boxes in image coordinates. `ImagePane` (zoom/pan/select), `OcrPanel`. Workbench integration in `WorkbenchWindow`/`PdfViewerControl`.
- Smoke `--smoke-ocr`: renders an invented table (Red/Blue numbers) to a bitmap at test time, runs real Windows OCR on it, checks the grid (skips with a clear note if no OCR language is installed), screenshots the pane.

## 6. Testing

- Unit: grid building from synthetic word boxes (aligned, ragged, merged cells, multi-word cells, header row), forced columns, clean-up rules (only numeric-looking cells, only unedited cells), TSV round trip through `TablePaste.Parse`.
- Integration (Windows, skippable when no OCR language): OCR of a rendered invented table image recovers ≥ 95 % of digits; PDF page render of a generated one-page PDF (if a PDF can be generated in tests; else smoke only).
- Smoke screenshot of the Image pane with a region and the grid.

## 7. Left to the builder

Exact layout in the classic gray look; zoom keys; how the region handles look; thresholds for column gaps (tuned on the invented test image); whether "Scan this page" reuses the PDF's own file path for Windows.Data.Pdf (it should).
