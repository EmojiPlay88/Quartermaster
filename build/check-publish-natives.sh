#!/usr/bin/env bash
# Fails when a Quartermaster.Desktop publish folder lacks the native libraries the document viewer needs
# (PDFium for PDF pages, Tesseract and Leptonica for OCR) or the bundled tessdata/eng.traineddata.
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
for f in $files tessdata/eng.traineddata; do
  if [ -s "$dir/$f" ]; then echo "ok       $f"; else echo "MISSING  $f (in $dir)" >&2; missing=1; fi
done
exit $missing
