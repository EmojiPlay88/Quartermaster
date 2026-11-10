# Third-party notices

Parts of `src/Quartermaster.Vassal` are ported from VASSAL (https://github.com/vassalengine/vassal),
which is licensed under the GNU Lesser General Public License, version 2.1 (LGPL-2.1).
The full licence text is in [`LICENSES/LGPL-2.1.txt`](LICENSES/LGPL-2.1.txt).

Ported files:

- `src/Quartermaster.Vassal/SequenceEncoder.cs` (from `VASSAL/tools/SequenceEncoder.java`)
- `src/Quartermaster.Vassal/Obfuscation.cs` (from `VASSAL/tools/io/DeobfuscatingInputStream.java`)
- `src/Quartermaster.Vassal/Traits/PieceType.cs` (trait `mySetType` readers from `VASSAL/counters/` BasicPiece, UsePrototype, Embellishment, Labeler, Marker, SendToLocation, Immobilized, PropertySheet, PlaceMarker, Replace, Clone, Delete, Hideable, Obscurable, DynamicProperty, FreeRotator, Footprint, MovementMarkable, NonRectangular)
- `src/Quartermaster.Vassal/Grids/GridNumbering.cs` (from `VASSAL/build/module/map/boardPicker/board/mapgrid/` RegularGridNumbering, SquareGridNumbering, HexGridNumbering)
- `src/Quartermaster.Vassal/Grids/SquareGrid.cs` (from `.../board/SquareGrid.java`)
- `src/Quartermaster.Vassal/Grids/HexGrid.cs` (from `.../board/HexGrid.java`)
- `src/Quartermaster.Vassal/Grids/ZonedGrid.cs` (from `.../board/ZonedGrid.java` and `mapgrid/Zone.java`)
- `src/Quartermaster.Vassal/Model/ModuleModelBuilder.cs` (board arrangement from `VASSAL/build/module/map/BoardPicker.java` default setup and `VASSAL/build/module/Map.java` setBoardBoundaries/getLocation)
- `src/Quartermaster.Core/SaveImporter.cs` (from `VASSAL/build/module/BasicCommandEncoder.java`, `counters/Stack.java`, `counters/BasicPiece.java`, `build/module/map/BoardPicker.java`, `build/module/PlayerRoster.java`, `build/module/GameState.java`)
- `src/Quartermaster.Core/NewGameBuilder.cs` (from `VASSAL/build/module/map/SetupStack.java`)
- `src/Quartermaster.Vassal/Traits/TraitState.cs` (trait `mySetState` readers and `myGetState`/key-event writers from `VASSAL/counters/` Embellishment, FreeRotator, Hideable, Obscurable, DynamicProperty, Labeler, and `build/module/properties/IncrementProperty.java`)
- `src/Quartermaster.Render/ShapeParser.cs` (shape spec parsing from `VASSAL/counters/NonRectangular.java`)
- `src/Quartermaster.Core/PieceActions.cs` (key-command menus and state changes from `VASSAL/counters/` SendToLocation, Clone, Delete, Embellishment, FreeRotator, Hideable, Obscurable, DynamicProperty, PlaceMarker, Replace, Footprint, PropertySheet, Immobilized)
- `src/Quartermaster.Core/PieceFactory.cs` (from `VASSAL/counters/PieceCloner.java` and `BasicPiece.java`)
- `src/Quartermaster.Render/PieceRenderer.cs` (label placement from `VASSAL/counters/Labeler.java` getLabelPosition)
- `src/Quartermaster.Vassal/Traits/TraitDefs.cs` (trait field sets from the `VASSAL/counters/` trait classes)
- `src/Quartermaster.Vassal/Traits/PrototypeExpander.cs` (prototype expansion and starting state from `VASSAL/build/module/PrototypeDefinition.java` and `counters/PieceCloner.java`)

## PdfPig

`src/Quartermaster.Render` uses PdfPig (NuGet package `PdfPig`, https://github.com/UglyToad/PdfPig) to extract
rulebook page text for search. PdfPig is licensed under the Apache License, Version 2.0
(https://www.apache.org/licenses/LICENSE-2.0). The full licence text is in [`LICENSES/Apache-2.0.txt`](LICENSES/Apache-2.0.txt).
PdfPig is used unmodified, as a binary package.

## PDFtoImage, PDFium and LibTiff.Net

`src/Quartermaster.Documents` draws rulebook PDF pages with PDFtoImage and reads TIFF scans with LibTiff.Net. All are used unmodified, as binaries.

| Component | Where it is bundled | Licence | Text |
|---|---|---|---|
| PDFtoImage 5.4.0 (NuGet package `PDFtoImage`, https://github.com/sungaila/PDFtoImage) | managed library | MIT | [`LICENSES/MIT-PDFtoImage.txt`](LICENSES/MIT-PDFtoImage.txt) |
| PDFium (https://pdfium.googlesource.com/pdfium/), built by Benoît Blanchon (NuGet packages `bblanchon.PDFium.Win32`, `.Linux`, `.macOS`, 152.0.7961, https://github.com/bblanchon/pdfium-binaries) | pdfium.dll / libpdfium.so / libpdfium.dylib | Apache-2.0 (PDFium; its bundled third-party parts are BSD-style, see the licence files in the upstream packages) | [`LICENSES/Apache-2.0.txt`](LICENSES/Apache-2.0.txt) |
| LibTiff.Net 2.4.649 (NuGet package `BitMiracle.LibTiff.NET`, https://github.com/BitMiracle/libtiff.net), Copyright (C) 2008-2020 Bit Miracle | managed library | BSD-3-Clause | [`LICENSES/BSD-3-Clause-LibTiff.NET.txt`](LICENSES/BSD-3-Clause-LibTiff.NET.txt) |

## Tesseract, Leptonica and tessdata_fast

`src/Quartermaster.Documents` reads scanned pages with Tesseract OCR. It calls the native libraries through its own small P/Invoke layer.
The native binaries come from the NuGet packages `Tesseract.Native.runtime.win-x64`, `.linux-x64` and `.osx-arm64` (5.5.2.200,
https://github.com/jbtule/tesseract-nuget-platforms, packaging licensed under the Apache License, Version 2.0).
All of the components below are used unmodified, as binaries. The licence texts are in the `LICENSES/` folder.

| Component | Where it is bundled | Licence | Text |
|---|---|---|---|
| Tesseract OCR 5.5.2 (https://github.com/tesseract-ocr/tesseract) | libtesseract / tesseract.dll | Apache-2.0 | [`LICENSES/Apache-2.0.txt`](LICENSES/Apache-2.0.txt) |
| tessdata_fast `eng.traineddata` (https://github.com/tesseract-ocr/tessdata_fast) | `tessdata/` | Apache-2.0 | [`LICENSES/Apache-2.0.txt`](LICENSES/Apache-2.0.txt) |
| Leptonica (https://github.com/DanBloomberg/leptonica), Copyright (C) 2001-2020 Leptonica | libleptonica / leptonica.dll | BSD-2-Clause (Leptonica form) | [`LICENSES/BSD-2-Clause-Leptonica.txt`](LICENSES/BSD-2-Clause-Leptonica.txt) |
| libjpeg-turbo and the IJG JPEG library (libjpeg, libturbojpeg, jpeg62) | all three OSes | IJG, BSD-3-Clause, zlib | [`LICENSES/libjpeg-turbo-LICENSE.md`](LICENSES/libjpeg-turbo-LICENSE.md), [`LICENSES/libjpeg-turbo-README-ijg.txt`](LICENSES/libjpeg-turbo-README-ijg.txt) (this software is based in part on the work of the Independent JPEG Group) |
| LibTIFF (libtiff, tiff.dll) | all three OSes | LibTIFF licence (BSD-style) | [`LICENSES/libtiff.txt`](LICENSES/libtiff.txt) |
| libwebp (libwebp, libwebpdecoder, libwebpdemux, libwebpmux, libsharpyuv) | all three OSes | BSD-3-Clause | [`LICENSES/libwebp.txt`](LICENSES/libwebp.txt) |
| libpng (libpng16) | all three OSes | PNG Reference Library License v2 | [`LICENSES/libpng.txt`](LICENSES/libpng.txt) |
| zlib (libz, z.dll) | all three OSes | zlib | [`LICENSES/zlib.txt`](LICENSES/zlib.txt) |
| OpenJPEG (libopenjp2) | all three OSes | BSD-2-Clause | [`LICENSES/openjpeg.txt`](LICENSES/openjpeg.txt) |
| giflib (libgif, gif.dll) | all three OSes | MIT-style | [`LICENSES/giflib.txt`](LICENSES/giflib.txt) |
| libarchive (libarchive, archive.dll) | all three OSes | BSD-2-Clause | [`LICENSES/libarchive.txt`](LICENSES/libarchive.txt) |
| libcurl | all three OSes | curl licence | [`LICENSES/curl.txt`](LICENSES/curl.txt) |
| OpenSSL 3 (libcrypto, libssl, legacy.dll) | all three OSes | Apache-2.0 | [`LICENSES/Apache-2.0.txt`](LICENSES/Apache-2.0.txt) |
| Mbed TLS (libmbedcrypto, libmbedtls, libmbedx509, libeverest, libp256m) | macOS | Apache-2.0 (we take the Apache-2.0 option of the dual licence) | [`LICENSES/Apache-2.0.txt`](LICENSES/Apache-2.0.txt), [`LICENSES/mbedtls.txt`](LICENSES/mbedtls.txt) |
| liblzma (xz) | all three OSes | 0BSD | [`LICENSES/xz.txt`](LICENSES/xz.txt) |
| LZ4 (liblz4, lz4.dll) | all three OSes | BSD-2-Clause | [`LICENSES/lz4.txt`](LICENSES/lz4.txt) |
| Zstandard (libzstd) | all three OSes | BSD-3-Clause (we take the BSD option) | [`LICENSES/zstd.txt`](LICENSES/zstd.txt) |
| bzip2 (libbz2, bz2.dll) | all three OSes | bzip2 licence (BSD-style) | [`LICENSES/bzip2.txt`](LICENSES/bzip2.txt) |
| libxml2 | Linux and macOS | MIT | [`LICENSES/libxml2.txt`](LICENSES/libxml2.txt) |
