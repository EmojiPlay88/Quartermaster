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

If Windows OCR is better on Windows, keep it there and use Tesseract on macOS and Linux. Windows is better here, so stage 4 adds a Windows-only recognizer behind `IOcrRecognizer` (the Desktop Windows build multi-targets). Stage 3b does not change the engine choice: Tesseract is the only recognizer built, on all three OSes.
