# Stage 3b library research (2026-11-09)

Input for the stage 3b plan (PDF rulebook viewer, charts, search, OCR on Windows, macOS and Linux). Facts come from the package pages as fetched on 2026-11-09. Items marked *unverified* must be checked in a real publish before the plan relies on them.

## PDF page rendering

- **Primary: PDFtoImage 5.4.0** (MIT, 2026-08-16), https://www.nuget.org/packages/PDFtoImage.
  - It needs SkiaSharp >= 4.150.1, which our 4.154.0 meets.
  - It uses the bblanchon PDFium natives (https://github.com/bblanchon/pdfium-binaries: win-x64, mac-arm64 and linux-x64, rebuilt weekly).
  - PDFium is not thread-safe, so render on one worker.
  - *Unverified:* that the osx-arm64 native lands in a self-contained publish.
- **Fallback: Docnet.Core 2.6.0** (MIT, 2023). It is stale, its PDFium is old, and osx-arm64 support is unverified.
- **Rejected:**
  - MuPDF.NET: AGPL or commercial, so it does not fit an LGPL-2.1 repo.
  - PdfiumViewer: a WinForms control.
  - PDFium.NET: commercial.
- **Search highlights:** PdfPig, which we already ship (0.1.16, Apache-2.0, pre-1.0, so pin it). `page.Letters[i].GlyphRectangle` gives glyph boxes in PDF user space (origin at the bottom left, so flip Y and scale to the render DPI). Map each hit to a letter range, union the boxes, and draw them over the PDFtoImage bitmap. Rotated pages (/Rotate) are not documented, so test them.

## OCR

- **Primary candidate: TesseractOcrNet 0.1.19** (MIT, 2026-04-18, net10.0), https://www.nuget.org/packages/TesseractOcrNet.
  - It ships win-x64, linux-x64 and osx-arm64 natives under `runtimes/<rid>/native`.
  - Set `OcrOptions.TessdataPath` to a bundled tessdata folder, so OCR never downloads anything.
  - Risk: it is a small, new package. Pin it and test it on all three OSes.
- **Fallback: TesseractOCR (Sicos1977) 5.5.2** (Apache-2.0). Its natives are Windows-only, so Mac and Linux would need natives we supply ourselves.
- **Rejected:**
  - charlesw Tesseract 5.2.0: stale, and its osx-arm64 runtime is a third-party prerelease with no licence.
  - Patagames: commercial.
  - Apple Vision: not reachable without native interop.
- **Language data:** tessdata_fast eng.traineddata (Apache-2.0), a few MB.
- **Spec decision still stands:** benchmark Tesseract against Windows.Media.Ocr on the invented OCR fixtures (digits-heavy tables; try psm 6/4, a digit whitelist and 300 DPI renders). If Windows OCR is better on Windows, keep it there and use Tesseract on macOS and Linux.

## Risks

- **macOS Gatekeeper:** unsigned native dylibs (pdfium, tesseract, leptonica) in a downloaded app get quarantined. Our builds are unsigned, so the Mac checklist's existing `xattr` / Open Anyway steps must cover them. Check this on the user's M1.
- **Linux:** the natives need glibc. Tesseract may need libstdc++/libpng at runtime (*unverified*).
- **SkiaSharp:** we force 4.154.0 over Avalonia 12.1.5. Check the transitive versions after adding PDFtoImage.
