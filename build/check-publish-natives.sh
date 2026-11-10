#!/usr/bin/env bash
# Fails when a Quartermaster.Desktop publish folder lacks the native libraries the document viewer needs
# (PDFium for PDF pages, Tesseract and Leptonica for OCR), the bundled tessdata/eng.traineddata, or the licence notices
# (LICENSE, THIRD-PARTY-NOTICES.md, LICENSES/), or when the Windows OCR plugin is missing from a win-x64 publish
# (ocr-windows/: the plugin dll, WinRT.Runtime.dll, Microsoft.Windows.SDK.NET.dll) or present in a macOS/Linux publish.
# Usage: build/check-publish-natives.sh <publish-folder> <win-x64|linux-x64|osx-arm64>
set -u
dir="${1:?usage: check-publish-natives.sh <publish-folder> <rid>}"
rid="${2:?usage: check-publish-natives.sh <publish-folder> <rid>}"
case "$rid" in
  win-x64)   files="pdfium.dll tesseract.dll leptonica.dll" ;;
  linux-x64) files="libpdfium.so libtesseract.so libleptonica.so" ;;
  osx-arm64) files="libpdfium.dylib libtesseract.dylib libleptonica.dylib" ;;
  *) echo "unknown rid '$rid'" >&2; exit 2 ;;
esac
missing=0
for f in $files tessdata/eng.traineddata LICENSE THIRD-PARTY-NOTICES.md LICENSES/pdfium-binaries/LICENSE.txt LICENSES/Apache-2.0.txt; do
  if [ -s "$dir/$f" ]; then echo "ok       $f"; else echo "MISSING  $f (in $dir)" >&2; missing=1; fi
done
plugin="ocr-windows/Quartermaster.OcrWindows.dll ocr-windows/WinRT.Runtime.dll ocr-windows/Microsoft.Windows.SDK.NET.dll"
if [ "$rid" = "win-x64" ]; then
  for f in $plugin; do
    if [ -s "$dir/$f" ]; then echo "ok       $f"; else echo "MISSING  $f (in $dir)" >&2; missing=1; fi
  done
elif [ -e "$dir/ocr-windows" ]; then
  echo "UNEXPECTED ocr-windows/ (the Windows OCR plugin) in a $rid publish ($dir)" >&2; missing=1
else
  echo "ok       no ocr-windows/"
fi
exit $missing
