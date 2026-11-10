# OCR benchmark: Tesseract vs Windows.Media.Ocr (2026-11-09)

Test: `tests/Quartermaster.Ocr.Tests/OcrBenchmarkTests.cs`, run once with `QUARTERMASTER_OCR_BENCH=1` on Windows 11 (en-US OCR language installed).

Both engines read the same invented 6 x 4 table of two-digit numbers (GDI-drawn Segoe UI 11 pt at 200 DPI, fixed seed, the table of `OcrServiceTests.InventedTable`), cell by cell through `ReadCellsAsync`:
Windows through `OcrService`, Tesseract 5.5.2 (tessdata_fast `eng`) through `OcrReader(new TesseractRecognizer())` on the same pixels.
A cell counts when its text, after `OcrCleanup.Fix`, equals the drawn number. Times are one warm run per table (both engines were run once before timing; numbers re-measured after fix round 1).

| Table | Windows cells | Windows ms | Tesseract cells | Tesseract ms |
|---|---|---|---|---|
| bordered, 200 DPI | 24/24 | 272 | 21/24 | 428 |
| unbordered, 200 DPI | 24/24 | 287 | 21/24 | 357 |
| bordered, half size | 24/24 | 69 | 24/24 | 81 |
| unbordered, half size | 24/24 | 85 | 24/24 | 86 |

## Reading the numbers

- Windows OCR is exact on all four tables. Tesseract is exact on the half-size tables and misses three of 24 cells on the full-size ones (a 7 read as a slash; see below).
- Windows is as fast or faster on every table.
- Tesseract's weak spot is the digit 7 in a two-digit cell (read as "/", "6/" for 67). `TesseractRecognizer` re-reads a cell that ends or starts in a dangling slash with a digit-only character whitelist. On the Skia-drawn tables (the cross-platform tests) this gives at least 22 of 24 cells exact. An interior slash ("3/7" for 37) cannot be told from a fraction and is left alone, so the gap is not closed on the GDI-drawn full-size tables.
- Small sample: one table, one font, one run. Treat the difference as indicative.

## Decision rule (plan, Scope rulings)

If Windows OCR is better on Windows, keep it there and use Tesseract on macOS and Linux. Windows is better here, so stage 4 adds a Windows-only recognizer behind `IOcrRecognizer` (stage 4a changed how it is built: see below, it is a run-time plugin, not a multi-targeted Desktop). Stage 3b does not change the engine choice: Tesseract is the only recognizer built, on all three OSes.

## Stage 4a: the Windows recognizer as a plugin (2026-11-10)

`Quartermaster.OcrWindows.WindowsRecognizer` (Windows.Media.Ocr behind `IOcrRecognizer`) re-run on the same four GDI tables, through `OcrReader(new WindowsRecognizer())` on the same pixels as the Tesseract column (benchmark test, third engine column; one warm run):

| Table | Windows (WinForms) cells | Windows (WinForms) ms | Tesseract cells | Tesseract ms | Windows 4a plugin cells | Windows 4a plugin ms |
|---|---|---|---|---|---|---|
| bordered, 200 DPI | 24/24 | 219 | 21/24 | 167 | 24/24 | 174 |
| unbordered, 200 DPI | 24/24 | 208 | 21/24 | 186 | 24/24 | 179 |
| bordered, half size | 24/24 | 78 | 24/24 | 60 | 24/24 | 56 |
| unbordered, half size | 24/24 | 86 | 24/24 | 60 | 24/24 | 58 |

**Lone characters.** `OcrReader` hands the engine one cell at a time (`OcrLayout.SingleLine`), so the WinForms "Cell" anchor-word trick (`OcrService.ReadCellsAsync`) is applied per cell: the anchor word "Cell" (Segoe UI, ink height / 0.72), a space, the cell's ink, read as one line, anchor dropped. Three per-cell layouts were measured (Windows plugin cells on the four tables above; lone cells: digits 0-9, "12", "47", "70", "88", "5", "1/2", "A", "x" at 30 px text):

| Per-cell layout | bordered 200 DPI | unbordered 200 DPI | bordered half | unbordered half | lone cells, 30 px |
|---|---|---|---|---|---|
| faithful: anchor, ink, padding | 22/24 | 22/24 | 23/24 | 23/24 | 15/18 (a lone 7 read "71") |
| **chosen: anchor, ink, white gap of 1.6 x ink height** | 24/24 | 24/24 | 24/24 | 24/24 | 16/18 |
| anchor, ink, gap, a trailing "Cell" anchor | 24/24 | 24/24 | 24/24 | 24/24 | 15/18 (a lone 3 read nothing) |

The chosen layout restores what the WinForms strip gave every cell but the last: the 1.6 x ink-height gap after it. Without it the engine invents strokes at the image edge. It is exact on all four tables and reads lone 3, 7 and 8 (`WindowsRecognizerTests.Single_lone_digit_cell_is_read`). A lone "x" comes back as "X" in every layout (case cannot be told from one glyph).

**Engine choice at run time** (`Quartermaster.Documents.OcrEngines.Create`):
- On macOS and Linux, Tesseract is used.
- On Windows, Windows OCR is used. Tesseract (bundled English) is used instead, with one log line, when any of these holds:
  - env `QUARTERMASTER_OCR_ENGINE=tesseract` is set;
  - the plugin is missing or fails to load;
  - Windows has no OCR language installed.
- `QUARTERMASTER_OCR_ENGINE=windows` forces Windows OCR even with no language, so its unavailable message shows.

**Isolation (replaces the multi-targeting assumption above).** `src/Quartermaster.OcrWindows` (`net10.0-windows10.0.19041.0`, references Render only) is never referenced by the Desktop app, which stays a single `net10.0` target.
- **When the plugin is built:** whenever Desktop is built for Windows (a `win-*` RID, or no RID on a Windows host), its project file builds the plugin.
- **What is copied, and where:** only the plugin's own files go to `ocr-windows/` beside the app:
  - `Quartermaster.OcrWindows.dll` and its `.deps.json`;
  - `WinRT.Runtime.dll`;
  - `Microsoft.Windows.SDK.NET.dll`.

  The copy reaches `Desktop.Tests` and the win-x64 publish too.
- **Loading:** a plugin `AssemblyLoadContext` loads it and takes Quartermaster.Render and SkiaSharp from the app, so `IOcrRecognizer` keeps one identity.
- **Mac and Linux:** builds and publishes never touch the plugin. `build/check-publish-natives.sh` checks both sides: the files must be in the win-x64 publish, and `ocr-windows/` must be absent from the others.

Multi-targeting Desktop was rejected because it:
- puts WinRT into the Desktop assembly;
- doubles the Desktop and Desktop.Tests builds;
- breaks cross-RID publishing from one host.
