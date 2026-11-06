# Piece 1 — Module Reader & Map Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Windows-native WinForms program that opens a VASSAL `.vmod` read-only and shows everything in it: all maps, organisation displays, charts and rulebook PDFs. It loads the module's predefined setups (`.vsav`), lets the player move counters and use their right-click actions, and saves and loads its own game files.

**Architecture:**
- `Quartermaster.Vassal` parses VASSAL formats: SequenceEncoder, buildFile, traits, grids, `.vsav`. It has no UI.
- `Quartermaster.Core` holds game state, which changes only through serializable commands applied by a deterministic reducer. Commands are kept in a log.
- `Quartermaster.Render` is pure SkiaSharp drawing and math, with no WinForms: image source, board tiles, viewport, stacks, pieces, hit-testing.
- `Quartermaster.App` is the WinForms MDI shell that wires the other three together.

**Tech Stack:**
- .NET 10 (LTS), C#
- WinForms
- SkiaSharp, SkiaSharp.Views.WindowsForms
- Svg.Skia
- Microsoft.Web.WebView2
- xUnit

**Spec:** `docs/superpowers/specs/2026-11-06-quartermaster-engine-design.md` (§2, §4, §5, §6 are this piece). Read §2 before anything else.

## Global Constraints

- **No CNA content or CNA-derived content in the repo:**
  - No rules text, tables, map or counter art.
  - No strings copied from the module beyond VASSAL *format* identifiers (class names, trait ids).
  - Tests use synthetic fixtures only (Task 2's `FixtureModule`).
  - `*.vmod *.vsav *.qrules *.qturn .superpowers/ bin/ obj/` are git-ignored (already in `.gitignore`).
- **Target frameworks:**
  - `net10.0` for Vassal, Core and Render.
  - `net10.0-windows10.0.19041.0` with `<UseWindowsForms>true</UseWindowsForms>` for App.
  - Test projects match their subject.
- **Module access:**
  - Open the module read-only with `FileShare.ReadWrite | FileShare.Delete` (VASSAL may hold it open).
  - Never extract it into the program folder.
  - Module identity is the SHA-256 (lowercase hex) of the `.vmod` bytes.
- **Cache root:** `%LOCALAPPDATA%\Quartermaster\cache\<sha256>\`, with `tiles\`, `docs\`, `svg\` subfolders.
- **Unknown or unsupported input never crashes.** It becomes an inert or opaque object plus an entry in `DiagnosticLog`, shown in the Module Diagnostics window.
- **All game-state changes go through `GameSession.Execute(GameCommand)`.** UI code never mutates `GameState` directly.
- **UI style:**
  - `Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)`.
  - Default font Tahoma 8.25pt (`Application.SetDefaultFont`).
  - System colors, standard 3D borders, MDI parent, no custom theming.
- **Map zoom** is clamped to 25%–400%.
- **Supported traits:**
  - Exactly: `piece prototype emb2 label mark sendto immob propertysheet placemark clone delete hide PROP rotate obs footprint markmoved nonRect2 replace return`.
  - `return` is inert in piece 1 (the module has no decks).
  - Any other id becomes an `UnknownTrait`.
- **VASSAL formats are ported from VASSAL 3.7 source** (LGPL, `github.com/vassalengine/vassal`, `vassal-app/src/main/java/VASSAL/...`). Where a test vector in this plan disagrees with VASSAL's behaviour, VASSAL wins: fix the vector and say so in the commit message.

## Review Focus

1. **A missing image file** referenced by a board, piece, layer or chart must draw a magenta-checker placeholder of sensible size and log one diagnostic per missing name, not one per frame. Task 9 test `MissingImage_ReturnsPlaceholderAndLogsOnce`.
2. **Malformed piece definitions** (unknown trait id, truncated fields, a prototype name that doesn't exist, prototypes that reference each other in a cycle) must load as inert traits with diagnostics, never throw. Task 4 tests `UnknownTrait_*`, `TruncatedFields_*`, `MissingPrototype_*`, `CyclicPrototype_*`.
3. **Huge board images** (the real module has a 14310×4632 px board) must be tiled. Rendering never holds a full-resolution decode, and tiling runs once per module hash. Task 9 test `Tiler_ProducesExpectedGridAndLevels` and `Tiler_SecondRunIsCacheHit`.
4. **Entities and odd names.** `&amp;` and similar in attributes and element text, `&` and spaces in zip entry names (e.g. `Rules_A&B.pdf`, `my map.png`), and non-ASCII characters must round-trip. Task 3 test `Parse_DecodesEntitiesAndOddEntryNames`.
5. **The module file is held open by another process** (VASSAL running), and the SHA-256 is stable across opens. Task 3 test `Open_WhileAnotherHandleIsOpen_Succeeds`.

---

## File Structure

```
Quartermaster.sln
src/Quartermaster.Vassal/
  SequenceEncoder.cs        – encode/decode + SequenceDecoder (typed token reader)
  Obfuscation.cs            – !VCSK deobfuscate/obfuscate
  SaveFileReader.cs         – .vsav zip → SaveFile (command strings)
  DiagnosticLog.cs          – Diagnostic, DiagnosticSeverity, DiagnosticLog
  BuildFileParser.cs        – XML → ModuleNode tree
  ModuleNode.cs
  VmodArchive.cs            – zip access, SHA-256, entry streams
  Traits/PieceType.cs       – PieceType, TraitDef base, PieceTypeParser
  Traits/TraitDefs.cs       – one record per supported trait
  Traits/PrototypeExpander.cs
  Grids/IMapGrid.cs, SquareGrid.cs, HexGrid.cs, ZonedGrid.cs, GridNumbering.cs
  Model/ModuleModel.cs      – typed module model + ModuleModelBuilder
  Model/Defs.cs             – MapDef, BoardDef, ChartDef, DocumentDef, SetupDef, PieceSlotDef, StackMetricsDef, DieDef, WindowTreeNode
src/Quartermaster.Core/
  GameState.cs              – PieceInstance, StackInstance, GameState
  Commands.cs               – GameCommand records + reducer (GameState.Apply)
  GameSession.cs            – Execute, Log, Changed event
  NativeSave.cs             – .qsave JSON read/write
  SaveImporter.cs           – SaveFile + ModuleModel → GameState
  NewGameBuilder.cs         – SetupStacks → GameState
  PieceActions.cs           – trait → menu actions → commands
src/Quartermaster.Render/
  ModuleSession.cs          – open module + model + images + tiler + game (UI-free)
  ImageSource.cs            – png/svg from archive, placeholder, cache
  BoardTiler.cs             – tile pyramid on disk
  Viewport.cs               – zoom/pan transforms
  StackLayout.cs            – stack offsets from StackMetricsDef
  PieceRenderer.cs          – draw a piece (layers, rotate, label, moved, hidden/obscured)
  MapRenderer.cs            – draw boards (tiles), grid overlay, stacks, selection
  HitTester.cs
src/Quartermaster.App/
  Program.cs, MainForm.cs, ModuleTreePanel.cs, MapWindow.cs, MapCanvas.cs,
  CounterDetailPopup.cs, ChartWindow.cs, PdfWindow.cs, DiagnosticsWindow.cs,
  PropertySheetDialog.cs, SetupChooserDialog.cs
tests/Quartermaster.TestFixtures/   – FixtureModule builder (synthetic .vmod/.vsav)
tests/Quartermaster.Vassal.Tests/
tests/Quartermaster.Core.Tests/
tests/Quartermaster.Render.Tests/
```

Project references:
- Core → Vassal
- Render → Vassal, Core
- App → all three
- Tests → their subject plus TestFixtures

---

### Task 1: Solution scaffold + SequenceEncoder

**Files:**
- Create: `Quartermaster.sln`, all four `src/*/*.csproj`, `tests/Quartermaster.TestFixtures/*.csproj`, the three test `*.csproj`, `src/Quartermaster.Vassal/SequenceEncoder.cs`, `Directory.Build.props` (`<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<LangVersion>latest</LangVersion>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`)
- Test: `tests/Quartermaster.Vassal.Tests/SequenceEncoderTests.cs`

**Interfaces:**
- Produces:
  - `static string SequenceEncoder.Encode(IEnumerable<string?> tokens, char delim)`
  - `static IReadOnlyList<string> SequenceEncoder.Decode(string? value, char delim)`
  - `sealed class SequenceDecoder(string? value, char delim)` with:
    - `bool HasMoreTokens`
    - `string NextToken(string def = "")`
    - `int NextInt(int def)`
    - `double NextDouble(double def)`
    - `bool NextBoolean(bool def)`
    - `string[] NextStringArray(int minLength)` (comma-list per VASSAL `StringArrayConfigurer` rules)

  A missing or unparsable token returns `def`; this class never throws.

**Format to port** (`VASSAL/tools/SequenceEncoder.java`):
- Tokens are joined by `delim`.
- A `delim` inside a token is written as `\`+`delim`. Backslashes are not otherwise escaped.
- A token that ends with `\`, or starts *and* ends with `'`, is wrapped in single quotes.
- A null token encodes as empty.
- Decoding reverses this.
- Nesting arises naturally: an inner tab-encoded string, appended as a token to an outer tab encoding, gets its tabs written as `\`+tab.

- [ ] **Step 1: Create the solution and projects**

Run (from repo root):
```
dotnet new sln -n Quartermaster
dotnet new classlib -o src/Quartermaster.Vassal -f net10.0
dotnet new classlib -o src/Quartermaster.Core -f net10.0
dotnet new classlib -o src/Quartermaster.Render -f net10.0
dotnet new winforms -o src/Quartermaster.App -f net10.0-windows
dotnet new classlib -o tests/Quartermaster.TestFixtures -f net10.0
dotnet new xunit -o tests/Quartermaster.Vassal.Tests -f net10.0
dotnet new xunit -o tests/Quartermaster.Core.Tests -f net10.0
dotnet new xunit -o tests/Quartermaster.Render.Tests -f net10.0
```
Then:
- Set App's `<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>`.
- Add all projects to the sln and add the references listed above.
- Add packages with `dotnet add package`: `SkiaSharp`, `Svg.Skia` to Render; `SkiaSharp.Views.WindowsForms`, `Microsoft.Web.WebView2` to App.
- Delete the template `Class1.cs` and `UnitTest1.cs`.

Expected: `dotnet build` succeeds.

- [ ] **Step 2: Write failing tests**

```csharp
[Theory]
[InlineData(new[]{"a","b","c"}, ';', "a;b;c")]
[InlineData(new[]{"a;b","c"}, ';', "a\\;b;c")]
[InlineData(new[]{"ends\\","x"}, ';', "'ends\\';x")]
[InlineData(new[]{"'q'","x"}, ';', "''q'';x")]
[InlineData(new[]{"", ""}, ';', ";")]
public void Encode_MatchesVassal(string[] tokens, char d, string expected)
    => Assert.Equal(expected, SequenceEncoder.Encode(tokens, d));

[Theory] /* same rows */ public void Decode_RoundTrips(string[] tokens, char d, string encoded)
    => Assert.Equal(tokens, SequenceEncoder.Decode(encoded, d));

[Fact] public void NestedTabChain_DecodesTwoLevels() {
    var inner = SequenceEncoder.Encode(new[]{"prototype;P", "piece;;;img.png;Name"}, '\t');
    var outer = SequenceEncoder.Encode(new[]{"label;x", inner}, '\t');
    Assert.Equal("label;x\tprototype;P\\\tpiece;;;img.png;Name", outer);
    var lvl1 = SequenceEncoder.Decode(outer, '\t');
    Assert.Equal(inner, lvl1[1]);
}

[Fact] public void Decoder_TypedReads_UseDefaultsOnGarbage() {
    var d = new SequenceDecoder("5;abc;true;2.5", ';');
    Assert.Equal(5, d.NextInt(0)); Assert.Equal(7, d.NextInt(7));
    Assert.True(d.NextBoolean(false)); Assert.Equal(2.5, d.NextDouble(0));
    Assert.False(d.HasMoreTokens); Assert.Equal("z", d.NextToken("z"));
}
```

- [ ] **Step 3:** Run `dotnet test tests/Quartermaster.Vassal.Tests`. Expected: FAIL (types not defined).
- [ ] **Step 4:** Implement `SequenceEncoder` and `SequenceDecoder` by porting `SequenceEncoder.java` (Encoder + Decoder, including the quote rule).
- [ ] **Step 5:** Run the tests. Expected: PASS.
- [ ] **Step 6: Commit** `feat: solution scaffold and VASSAL SequenceEncoder port`

---

### Task 2: Obfuscation, .vsav reader, and synthetic fixture builder

**Files:**
- Create: `src/Quartermaster.Vassal/Obfuscation.cs`, `src/Quartermaster.Vassal/SaveFileReader.cs`, `tests/Quartermaster.TestFixtures/FixtureModule.cs`
- Test: `tests/Quartermaster.Vassal.Tests/SaveFileReaderTests.cs`

**Interfaces:**
- Produces:
  - `static byte[] Obfuscation.Deobfuscate(byte[] data)`: if `data` starts with ASCII `!VCSK`, the key is the byte parsed from the next 2 hex chars, then each following hex pair is XOR'd with the key. Otherwise the data is returned unchanged.
  - `static byte[] Obfuscation.Obfuscate(byte[] plain, byte key)`
  - `sealed record SaveFile(IReadOnlyList<string> Commands)`
  - `static SaveFile SaveFileReader.Read(Stream vsavZip)`: reads zip entry `savedGame`, deobfuscates, decodes UTF-8, and splits on `'\u001B'` (ESC). Empty commands are kept, so indices stay faithful.
  - `const char SaveFileReader.CommandSeparator = '\u001B'`
  - `sealed class FixtureModule` (TestFixtures), a fluent builder that writes a synthetic `.vmod` to a temp path:
    - `FixtureModule AddImage(string name, int w, int h)` writes a solid-color PNG.
    - `FixtureModule AddSvg(string name)` writes a 32×32 rect SVG.
    - `FixtureModule WithBuildFile(string xml)`
    - `FixtureModule AddSave(string entryName, IEnumerable<string> commands)` obfuscates with key `0x5A` and nests a zip containing `savedGame`.
    - `FixtureModule AddFile(string name, byte[] bytes)`
    - `string Build()` returns the path; the file is deleted on `Dispose`.
    - `static string MinimalBuildFile()` returns a buildFile with one Map (one Board using a 200×100 image `board.png` and a SquareGrid dx=50 dy=50 x0=25 y0=25), one PieceSlot whose type is `piece;;;unit.svg;Test Unit`, and one PrototypeDefinition named `Proto` containing `mark;Side`.

- [ ] **Step 1: Write failing tests**
  - `Deobfuscate_ReversesObfuscate`: random bytes, key 0x5A, round-trip equal.
  - `Deobfuscate_PassesThroughPlain`: `"begin_save"` bytes unchanged.
  - `Read_SplitsOnEsc`: a fixture save with commands `["begin_save","","+/1/piece;;;a.png;A/null;10;20;1;0","end_save"]` reads back exactly those 4 strings.
  - `Read_MissingSavedGameEntry_Throws InvalidDataException` with message containing `savedGame`.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement. Use `System.IO.Compression.ZipArchive`. Port the header check from `VASSAL/tools/io/DeobfuscatingInputStream.java` and verify the hex-pair layout against it.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5: Commit** `feat: .vsav reader, deobfuscation, synthetic fixture builder`

---

### Task 3: VmodArchive, BuildFileParser, DiagnosticLog

**Files:**
- Create: `DiagnosticLog.cs`, `ModuleNode.cs`, `BuildFileParser.cs`, `VmodArchive.cs` (all in `src/Quartermaster.Vassal/`)
- Test: `tests/Quartermaster.Vassal.Tests/VmodArchiveTests.cs`, `BuildFileParserTests.cs`

**Interfaces:**
- Produces:
  - `enum DiagnosticSeverity { Info, Warning, Error }`
  - `sealed record Diagnostic(DiagnosticSeverity Severity, string Category, string Message, string? Context)`
  - `sealed class DiagnosticLog`, with:
    - `void Add(DiagnosticSeverity s, string category, string message, string? context = null)`
    - `bool AddOnce(string dedupeKey, DiagnosticSeverity s, string category, string message, string? context = null)`
    - `IReadOnlyList<Diagnostic> Items`
    - `event Action<Diagnostic>? Added`

    It must be thread-safe, because tiling and image loading run off the UI thread.
  - `sealed class ModuleNode`, with:
    - `string ClassName` (full VASSAL class)
    - `string ShortName` (after the last `.`)
    - `IReadOnlyDictionary<string,string> Attributes`
    - `string Text` (concatenated direct text, entity-decoded)
    - `IReadOnlyList<ModuleNode> Children`
    - `ModuleNode? Parent`
    - `string Attr(string name, string def = "")`
    - `IEnumerable<ModuleNode> Descendants(string shortName)`
  - `static ModuleNode BuildFileParser.Parse(Stream xml)` (`XmlReader`, DTD prohibited)
  - `sealed class VmodArchive : IDisposable`, with:
    - `static VmodArchive Open(string path)`
    - `string Path`
    - `string Sha256`
    - `ModuleNode Root` (from `buildFile.xml`, falling back to `buildFile`)
    - `bool HasEntry(string name)`
    - `Stream OpenEntry(string name)`, which returns a seekable `MemoryStream` copy
    - `IReadOnlyCollection<string> EntryNames`

    Entry lookup is exact first, then case-insensitive. Image names are resolved by `ImagePath(string imageName)` → `"images/" + imageName`.

- [ ] **Step 1: Write failing tests** (fixtures from Task 2)
  - `Open_ParsesRootAndHashes`: `Root.ShortName == "GameModule"`, and `Sha256` equals the `SHA256.HashData` of the file, in lowercase hex.
  - `Open_WhileAnotherHandleIsOpen_Succeeds`: hold `new FileStream(path, Open, Read, FileShare.ReadWrite)` and `VmodArchive.Open(path)` still works.
  - `Parse_DecodesEntitiesAndOddEntryNames`: buildFile attribute `name="A &amp; B — ü"` gives `"A & B — ü"`. Entries `images/my map.png` and `Rules_A&B.pdf` both satisfy `HasEntry`, and `OpenEntry` returns their bytes.
  - `Descendants_FindsByShortName`.
  - `AddOnce_DedupesByKey`.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5: Commit** `feat: vmod archive and buildFile parser`

---

### Task 4: Piece types, traits, prototype expansion

**Files:**
- Create: `src/Quartermaster.Vassal/Traits/PieceType.cs`, `TraitDefs.cs`, `PrototypeExpander.cs`
- Test: `tests/Quartermaster.Vassal.Tests/PieceTypeTests.cs`

**Interfaces:**
- Consumes: `SequenceEncoder`/`SequenceDecoder` (Task 1), `DiagnosticLog` (Task 3).
- Produces:
  - `readonly record struct RgbColor(byte R, byte G, byte B)` with `static RgbColor? Parse(string? "r,g,b")` (null/blank → null), in `Traits/TraitDefs.cs`; used by grids and the model too.
  - `abstract record TraitDef(string Id, string RawType)`. Subclasses, one per supported id. Each port reads its fields with `SequenceDecoder(RawType minus "id;", ';')` in the Java `mySetType` order, from these source files:

    | Trait | Java source | Fields the record must expose |
    |---|---|---|
    | `BasicPieceDef` | `piece`, `BasicPiece.java` | `Image`, `Name`, `CloneKey`, `DeleteKey` |
    | `PrototypeRef` | `prototype`, `UsePrototype.java` | `PrototypeName` |
    | `LayerDef` | `emb2`, `Embellishment.java` | `IReadOnlyList<string> Images`, `IReadOnlyList<string> LevelNames`, `int OffsetX/OffsetY`, `bool Loop`, `bool AlwaysActive`, `bool DrawUnderneath`, `string ActivateCommand`, `string UpCommand`, `string DownCommand`, `string ResetCommand`, `int ResetLevel`, `bool FollowProperty`, `string PropertyName`, `string Name` |
    | `LabelDef` | `label`, `Labeler.java` | `MenuCommand`, `FontSize`, `FontStyle`, `Foreground`, `Background` (`RgbColor?`), `VerticalPos`, `HorizontalPos`, `VerticalOffset`, `HorizontalOffset`, `LabelFormat`, `Name` |
    | `MarkerDef` | `mark`, `Marker.java` | `IReadOnlyList<string> Keys` |
    | `SendToDef` | `sendto`, `SendToLocation.java` | `CommandName`, `MapName`, `BoardName`, `X`, `Y`, `GridLocation`, `ZoneName`, `DestinationType` (enum), `Description` |
    | `ImmobileDef` | `immob`, `Immobilized.java` | `bool MoveLocked`, `bool SelectionLocked`, `bool ShiftToSelect`, `bool AltToSelect` |
    | `PropertySheetDef` | `propertysheet`, `PropertySheet.java` | `IReadOnlyList<(string Name,int Type)> Rows`, `MenuName` |
    | `PlaceMarkerDef` | `placemark`, `PlaceMarker.java` | `CommandName`, `MarkerSpec` (raw nested piece type), `MarkerSlotPath`, `OffsetX/OffsetY`, `bool AboveTarget` |
    | `CloneDef` / `DeleteDef` | `clone` `Clone.java` / `delete` `Delete.java` | `CommandName` |
    | `HideableDef` | `hide`, `Hideable.java` | `CommandName`, `Transparency` |
    | `ObscurableDef` | `obs`, `Obscurable.java` | `CommandName`, `MaskImage`, `DisplayStyle` |
    | `DynamicPropertyDef` | `PROP`, `DynamicProperty.java` | `Key`, `bool Numeric`, `int Min/Max`, `bool Wrap`, `IReadOnlyList<(string Menu,string Kind,string Expr)> Commands` |
    | `RotatorDef` | `rotate`, `FreeRotator.java` | `int ValidAngles`, `RotateCwCommand`, `RotateCcwCommand`, `Name` |
    | `FootprintDef` | `footprint`, `Footprint.java` | `CommandName`, `bool InitiallyVisible` |
    | `MovementMarkableDef` | `markmoved`, `MovementMarkable.java` | `Image`, `int XOffset/YOffset` |
    | `NonRectangularDef` | `nonRect2`, `NonRectangular.java` | `string ShapeSpec` (raw; parsed in Render) |
    | `ReplaceDef` | `replace`, `Replace.java` | `CommandName`, `MarkerSpec`, `MarkerSlotPath` |
    | `InertTrait` | `return` | (none) |
    | `UnknownTrait` | any other id | (none) |

  - `sealed record PieceType(IReadOnlyList<TraitDef> Traits)`. Ordered **outermost first**, so `Traits[^1]` is always a `BasicPieceDef`; one is synthesized if missing, with a diagnostic. It also exposes `BasicPieceDef Basic` and `T? Get<T>()`.
  - `static PieceType PieceTypeParser.Parse(string type, DiagnosticLog log, string? context)`. It walks the tab chain: decode the level with `'\t'`, take `[0]` as this trait, recurse into `[1]`, and stop at `piece;`.
  - `static IReadOnlyList<string> PieceTypeParser.SplitState(string state, int traitCount)`. It splits the state chain the same way and pads with `""`.
  - `sealed class PrototypeExpander(IReadOnlyDictionary<string,PieceType> prototypes, DiagnosticLog log)`, with `ExpandedPiece Expand(PieceType t)`. It replaces each `PrototypeRef` with the referenced prototype's traits, minus that prototype's own `BasicPieceDef`, recursively (depth limit 32). Missing or cyclic references become `InertTrait` plus a diagnostic.

  **States stay aligned to the *unexpanded* chain.** VASSAL saves store the state for the unexpanded type, so Core keeps the raw type and state and expands only for behaviour and rendering, using `ExpandedPiece`: `sealed record ExpandedPiece(PieceType Type, IReadOnlyList<int> StateIndex)`. `StateIndex[i]` gives the index into the unexpanded state list of expanded trait `i`, or `-1` for traits that came from a prototype. Prototype trait state lives in the `prototype` trait's own state slot, as a nested tab chain; the builder should confirm this against `UsePrototype.java`.

- [ ] **Step 1: Write failing tests**
  - `Parse_BasicOnly`: `"piece;;;unit.svg;Test Unit"` gives one trait, with `Basic.Image == "unit.svg"` and `Basic.Name == "Test Unit"`.
  - `Parse_ChainOrderOutermostFirst`: use `SequenceEncoder` to build `label → prototype;Proto → piece`, and assert the ids are `["label","prototype","piece"]`.
  - `Parse_Layer_ReadsImagesAndNames`: build an `emb2` type string from the field order in `Embellishment.java`, with images `a.png,b.png`, level names `Up,Down`, and offset `3,-4`. Assert the typed fields.
  - `UnknownTrait_IsInertAndLogged`: a `zzz;1;2` layer becomes `UnknownTrait` and logs a Warning with category `"trait"`.
  - `TruncatedFields_UseDefaults`: `"label"` with no fields doesn't throw and gives defaults.
  - `MissingPrototype_BecomesInert`.
  - `CyclicPrototype_BecomesInert`: A uses B, and B uses A.
  - `Expand_SplicesPrototypeTraits_AndMapsStateIndex`.
  - `SplitState_PadsToTraitCount`.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement the parser, the 21 trait records (ported field orders) and the expander.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5: Commit** `feat: piece type parser, trait defs, prototype expansion`

---

### Task 5: Grids and numbering

**Files:**
- Create: `src/Quartermaster.Vassal/Grids/IMapGrid.cs`, `SquareGrid.cs`, `HexGrid.cs`, `ZonedGrid.cs`, `GridNumbering.cs`
- Test: `tests/Quartermaster.Vassal.Tests/GridTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct PointD(double X, double Y)`, defined in `IMapGrid.cs`.
  - `interface IMapGrid`, with:
    - `PointD Snap(PointD p)`
    - `string LocationName(PointD p)`
    - `bool Visible`
    - `RgbColor Color`
    - `IEnumerable<(PointD A, PointD B)> OverlayLines(RectD area)`, the line segments to draw for the overlay
  - `readonly record struct RectD(double X, double Y, double W, double H)`
  - `SquareGrid` and `HexGrid` are built from a `ModuleNode` via `static IMapGrid? GridFactory.From(ModuleNode boardNode, DiagnosticLog log)`. It handles `SquareGrid`, `HexGrid`, `ZonedGrid` (with `Zone` children: `name`, `path` polygon, optional own grid) and numbering children (`SquareGridNumbering`, `HexGridNumbering`).
  - `GridNumbering` is ported from `RegularGridNumbering.java` plus the square and hex subclasses: `first`, `hType`, `vType`, `hOff`, `vOff`, `hLeading`, `vLeading`, `hDescend`, `vDescend`, `sep`, `stagger`, `locationFormat`.
  - `ZonedGrid.LocationName` returns the zone's `locationFormat` result (default: the zone name) when the point is inside a zone, otherwise the background grid's name.
  - Snapping follows the zone's grid if it has one, otherwise the background grid.
  - Port the formulas from `SquareGrid.java`, `HexGrid.java` (including `sideways`), `ZonedGrid.java` and `Zone.java`.

- [ ] **Step 1: Write failing tests**
  - `Square_SnapsToNearestCenter`: x0=25, y0=25, dx=50, dy=50, so `Snap(64,71)` gives `(75,75)`.
  - `Square_Numbering_NumericWithLeadingZeros`: hType=N, vType=N, first=H, sep="", hLeading=2, vLeading=2, hOff=1, vOff=1. The cell with column index 3 and row index 4 is named `"0405"`.
  - `Hex_SnapCenterIsFixedPoint`: for both `sideways=false` and `true` with dx=60, dy=70, every snapped point re-snaps to itself (property over a 20×20 sample).
  - `Hex_NeighbourCentersSpacing`: adjacent centers in the same column differ by `dy` (non-sideways).
  - `Zoned_PointInZone_UsesZoneName`: a rectangular zone `Box` with `path="0,0;100,0;100,100;0,100"` gives `LocationName(50,50) == "Box"`, and outside it the background grid name is used.
  - `Zoned_ZoneWithOwnGrid_SnapsToZoneGrid`.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement by porting.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5: Commit** `feat: square/hex/zoned grids with numbering`

---

### Task 6: Typed module model

**Files:**
- Create: `src/Quartermaster.Vassal/Model/Defs.cs`, `ModuleModel.cs`
- Test: `tests/Quartermaster.Vassal.Tests/ModuleModelTests.cs`

**Interfaces:**
- Consumes: Tasks 3–5.
- Produces:
  - `sealed record BoardDef(string Name, string? Image, int Width, int Height, IMapGrid? Grid, PointD Offset)`. Width and Height come from `width`/`height` attributes when there's no image; otherwise the App fills them in from the image size. `Offset` is the board's position in the map, laid out per `BoardPicker` (multi-board maps in a grid, plus `edgeWidth`/`edgeHeight`).
  - `sealed record StackMetricsDef(int ExSepX, int ExSepY, int UnexSepX, int UnexSepY, bool Disabled)`
  - `sealed record SetupStackDef(string Name, string OwningBoard, bool UseGridLocation, string Location, int X, int Y, IReadOnlyList<PieceSlotDef> Pieces)`
  - `sealed record MapDef(string Id, string Name, bool IsOrgDisplay, IReadOnlyList<string> NavPath, IReadOnlyList<BoardDef> Boards, int EdgeWidth, int EdgeHeight, RgbColor Background, StackMetricsDef Stacking, IReadOnlyList<SetupStackDef> SetupStacks, string MarkMoved)`
    - `Id` = `mapName`.
    - `IsOrgDisplay` is true for `WidgetMap`.
    - `NavPath` holds the names of the enclosing Tab/Panel/List widgets and ChartWindow.
  - `sealed record PieceSlotDef(string EntryName, string Gpid, string TypeString, string StateString, int Width, int Height, IReadOnlyList<string> NavPath)`. Type and state are split from the slot text `+/null/<type>/<state>` using `SequenceEncoder.Decode(text, '/')`.
  - `sealed record ChartDef(string Name, string FileName, IReadOnlyList<string> NavPath)`
  - `sealed record DocumentDef(string Title, string EntryName, DocumentKind Kind)`, where `enum DocumentKind { Pdf, Html, Text }`. It covers `BrowserPDFFile`, `HelpFile` and the root `.txt` files.
  - `sealed record SetupDef(string Name, string? FileEntry, IReadOnlyList<string> NavPath)`. This is `PredefinedSetup`, where `useFile` and `file` give the entry; a setup with no file starts from SetupStacks.
  - `sealed record DieDef(string Name, IReadOnlyList<string> Faces)`, for `SpecialDie` with `SpecialDieFace` text values.
  - `sealed class WindowTreeNode(string Title, WindowNodeKind Kind, object? Ref, IReadOnlyList<WindowTreeNode> Children)`, where `enum WindowNodeKind { Folder, Map, OrgDisplay, Chart, Document, Setup }`.
  - `sealed class ModuleModel`, with:
    - `string Name`
    - `string Version`
    - `string VassalVersion`
    - `IReadOnlyList<MapDef> Maps`
    - `IReadOnlyList<PieceSlotDef> Slots`
    - `IReadOnlyDictionary<string,PieceType> Prototypes`
    - `IReadOnlyList<ChartDef> Charts`
    - `IReadOnlyList<DocumentDef> Documents`
    - `IReadOnlyList<SetupDef> Setups`
    - `IReadOnlyList<DieDef> Dice`
    - `IReadOnlyList<string> Sides` (from `PlayerRoster` `entry` children)
    - `WindowTreeNode Tree` (root folders: Maps, Organisation Displays, Charts, Rulebooks & Documents, Setups)
    - `PrototypeExpander Expander`
    - `PieceSlotDef? SlotByGpid(string gpid)`
    - `PieceSlotDef? SlotByPath(string path)`
  - `static ModuleModel ModuleModelBuilder.Build(VmodArchive archive, DiagnosticLog log)`. Each element short-name not consumed by the builder adds one `Info` diagnostic with category `"component"`, deduped by class name and carrying a count.

  These short-names are consumed silently (UI plumbing):
  - `ForwardToKeyBuffer`, `ForwardToChatter`, `Scroller`, `MenuDisplayer`, `MapCenterer`, `StackExpander`, `PieceMover`, `KeyBufferer`, `ImageSaver`, `CounterDetailViewer`, `SelectionHighlighters`, `HighlightLastMoved`, `Flare`, `Zoomer`, `ZonedGridHighlighter`
  - `GlobalOptions`, `Chatter`, `KeyNamer`, `GlobalTranslatableMessages`, `Language`, `ColorManager`, `FontManager`, `FontStyle`, `BasicCommandEncoder`, `Documentation`, `AboutScreen`, `GamePieceImageDefinitions`, `PrototypesContainer`, `GlobalProperties`

- [ ] **Step 1: Write failing tests**, on a fixture buildFile with: 1 `Map` (2 boards side by side via BoardPicker), a `ChartWindow` containing a `TabWidget` with a `WidgetMap` "Org A" and a `Chart` "C1", a `PieceWindow` with 2 PieceSlots, a `PrototypeDefinition`, a `PredefinedSetup` with a file, a `BrowserPDFFile`, a `PlayerRoster` with sides `Red,Blue`, and an unknown element `<com.example.Foo/>`.
  - `Build_FindsMapsAndOrgDisplays`: two maps; the WidgetMap has `IsOrgDisplay` and `NavPath == ["<chartwindow name>","<tab name>"]`.
  - `Build_LaysOutBoardsSideBySide`: the second board's `Offset.X == firstBoardWidth`.
  - `Build_SlotSplitsTypeAndState`
  - `Build_TreeHasFiveRootFolders`
  - `Build_UnknownElement_LogsOnceWithCount`
  - `Build_SidesFromRoster`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement (board layout ported from `BoardPicker.java`/`Map.java`).
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5: Commit** `feat: typed module model and window tree`

---

### Task 7: Core state, commands, session, native save

**Files:**
- Create: `src/Quartermaster.Core/GameState.cs`, `Commands.cs`, `GameSession.cs`, `NativeSave.cs`
- Test: `tests/Quartermaster.Core.Tests/GameStateTests.cs`, `NativeSaveTests.cs`

**Interfaces:**
- Produces:
  - `sealed class PieceInstance`, with:
    - `string Id`
    - `string TypeString` (raw, unexpanded)
    - `List<string> TraitStates` (aligned to the unexpanded chain)
    - `string? StackId`
    - `string MapId`
    - `PointD Pos`
    - `List<PointD> Trail` (footprint)
    - `bool Moved`
  - `sealed class StackInstance`, with:
    - `string Id`
    - `string MapId`
    - `PointD Pos`
    - `List<string> PieceIds` (bottom → top)
    - `bool Expanded`
  - `sealed class GameState`, with:
    - `Dictionary<string,PieceInstance> Pieces`
    - `Dictionary<string,StackInstance> Stacks`
    - `List<string> MapOrder`
    - `IEnumerable<StackInstance> StacksOn(string mapId)`
    - `GameState DeepClone()`
  - `abstract record GameCommand`. Subtypes:
    - `AddPiece(string PieceId, string TypeString, string StateString, string MapId, PointD Pos, string? StackOnto)`
    - `MovePieces(IReadOnlyList<string> PieceIds, string MapId, PointD Pos, string? StackOnto)`
    - `RemovePiece(string PieceId)`
    - `SetTraitState(string PieceId, int StateIndex, string NewState)`
    - `SetStackExpanded(string StackId, bool Expanded)`
    - `ClearMoved(string MapId)`
    - `ToggleTrail(string PieceId)`
  - `void GameState.Apply(GameCommand c)` is the deterministic reducer. Moving a piece:
    - removes it from its old stack (an emptied stack is deleted);
    - puts it in `StackOnto` if that's given, otherwise creates a new stack, whose id is `"s:" + pieceId + ":" + monotonic counter held in state`;
    - sets `Moved = true`;
    - appends to `Trail`.
  - `sealed class GameSession(GameState initial, string moduleSha256)`, with:
    - `GameState State`
    - `IReadOnlyList<GameCommand> Log`
    - `void Execute(GameCommand c)`
    - `event Action<GameCommand>? Changed`
  - `static class NativeSave`, with:
    - `void Write(string path, GameSession s)`
    - `GameSession Read(string path)`

    The format is JSON (System.Text.Json, polymorphic `GameCommand` via `[JsonDerivedType]` with discriminator `"$t"`), with fields `{ "format": 1, "moduleSha256", "snapshot": GameState, "log": GameCommand[] }`. The extension is `.qsave`.

    The snapshot is the state at save time, and the log is kept for the later game log. On read, the session starts from the snapshot, and the log is restored but not re-applied.

- [ ] **Step 1: Write failing tests**
  - `Move_StackOntoExisting_AppendsOnTop`
  - `Move_LastPieceOut_DeletesStack`
  - `Move_SetsMovedAndAppendsTrail`
  - `ClearMoved_ClearsOnlyThatMap`
  - `Remove_DeletesPieceAndCleansStack`
  - `Apply_IsDeterministic`: apply the same 50 random commands to two clones; their JSON is equal.
  - `NativeSave_RoundTrip_PreservesStateAndLog`
  - `NativeSave_Read_WrongFormatVersion_Throws`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5: Commit** `feat: core game state, commands, session, native save`

---

### Task 8: Import VASSAL saves and build new games from SetupStacks

**Files:**
- Create: `src/Quartermaster.Core/SaveImporter.cs`, `NewGameBuilder.cs`
- Test: `tests/Quartermaster.Core.Tests/SaveImporterTests.cs`, `NewGameBuilderTests.cs`

**Interfaces:**
- Consumes: `SaveFile` (Task 2), `ModuleModel` (Task 6), `GameState` and commands (Task 7).
- Produces:
  - `sealed record ImportResult(GameState State, int PiecesImported, int CommandsSkipped)`
  - `static ImportResult SaveImporter.Import(SaveFile save, ModuleModel model, DiagnosticLog log)`. Command handling, ported from VASSAL's `BasicCommandEncoder.java`, `Stack.java` and `BasicPiece.java`; verify every layout there:
    - `begin_save`, `end_save` and empty commands are ignored.
    - `+/<id>/<type>/<state>` with a type other than `stack` is a piece. Its state chain's innermost (BasicPiece) state holds `mapId;x;y;gpid;…`, so map and position come from there. A `null` map means the piece is off-map and is kept unplaced; an orphan count goes into diagnostics.
    - `+/<id>/stack/<state>` is a stack. The state holds `mapId;x;y;pieceId…`. Stacks are processed after all pieces, so ids resolve, and their piece ids are assigned in order.
    - A command starting with a map id followed by a tab is a board-picker selection. It records which boards are in use per map; piece 1 shows all of a map's boards regardless, and logs Info if the selection differs.
    - `PLAYER\t…` is roster info, ignored in piece 1.
    - Anything else is skipped, with one diagnostic per distinct 12-character prefix, and counted.
  - `static GameState NewGameBuilder.FromSetupStacks(ModuleModel model, DiagnosticLog log)`. Each SetupStack's slots become pieces at the stack's position (or at its grid location resolved through the board grid) on the owning board's map, plus board offset. Ids are `"n:" + running counter`.

- [ ] **Step 1: Write failing tests**, using a fixture save built by `FixtureModule.AddSave` with synthetic commands:
  - `Import_PlacesPiecesAndStacks`: 3 pieces, and a stack holding 2 of them at (100,50) on map `M`.
  - `Import_StackBeforePieceOrder_StillResolves`
  - `Import_UnknownCommand_SkippedAndLogged`
  - `Import_NullMapPiece_CountedAsOrphan`
  - `NewGame_SetupStackWithGridLocation_ResolvesToSnappedPoint`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5: Commit** `feat: import VASSAL saves and setup stacks`

---

### Task 9: Render library

**Files:**
- Create: `src/Quartermaster.Render/ImageSource.cs`, `BoardTiler.cs`, `Viewport.cs`, `StackLayout.cs`, `PieceRenderer.cs`, `MapRenderer.cs`, `HitTester.cs`
- Test: `tests/Quartermaster.Render.Tests/*Tests.cs`

**Interfaces:**
- Consumes: `VmodArchive`, `ModuleModel`, `PieceType`/`ExpandedPiece`, `GameState`, `DiagnosticLog`.
- Produces:
  - `sealed class ImageSource(VmodArchive archive, string cacheDir, DiagnosticLog log)`, with:
    - `SKImage Get(string imageName, float scale = 1f)`. PNG, JPG and GIF are decoded once. SVG is rendered via `Svg.Skia`'s `SKSvg` at `scale`, bucketed to {0.25, 0.5, 1, 2, 4}. Results go in an LRU cache capped at 512 MB of estimated pixels.
    - `SKSizeI SizeOf(string imageName)`
  - A missing image gives a 64×64 magenta/black checker and `log.AddOnce("img:"+name, Warning, "image", …)`.
  - `sealed class BoardTiler(VmodArchive archive, string cacheDir, DiagnosticLog log)`, with `Task<TileSet> EnsureTilesAsync(BoardDef board, IProgress<double>? progress, CancellationToken ct)`.
    - Tiles are 512×512 PNGs at levels 0..L, where level n is downscaled by 2ⁿ and L is the smallest level at which the whole image fits in 1024 px.
    - Path: `<cacheDir>\tiles\<sanitized board name>\<level>\<tx>_<ty>.png`.
    - A `done.json` marker holds the image size. When it exists, `EnsureTilesAsync` loads the `TileSet` without decoding.
    - The full image is decoded at most once, during tiling.
  - `sealed class TileSet`, with:
    - `int Width`
    - `int Height`
    - `int Levels`
    - `string Root`
    - `string TilePath(int level, int tx, int ty)`
    - `int ChooseLevel(float zoom)` (the largest n with `2⁻ⁿ ≥ zoom`, clamped)
  - `sealed class Viewport`, with:
    - `float Zoom`, clamped to [0.25, 4]
    - `SKPoint Pan` (map coordinates at the top-left of the screen)
    - `SKPoint ToScreen(PointD map)`
    - `PointD ToMap(SKPoint screen)`
    - `void ZoomAt(SKPoint screenAnchor, float factor)` (keeps the anchor fixed)
    - `RectD VisibleMapRect(SKSize clientSize)`
  - `static IReadOnlyList<SKPoint> StackLayout.Offsets(int count, bool expanded, StackMetricsDef m)`. Collapsed stacks offset each piece by `(UnexSepX, -UnexSepY)` cumulatively; expanded stacks use `(ExSepX, -ExSepY)`. Index 0 is the bottom piece. If `m.Disabled`, all offsets are zero.
  - `sealed class PieceRenderer(ImageSource images)`, with `void Draw(SKCanvas c, PieceInstance p, ExpandedPiece x, PointD center, float zoom, PieceDrawFlags flags)`.
    - Order: the BasicPiece image, then each `LayerDef` inner→outer at its active level (taken from its state; an inactive layer isn't drawn; `DrawUnderneath` layers are drawn before the basic image), then `LabelDef` text, then the `MovementMarkableDef` image if `Moved`.
    - `RotatorDef` rotates the whole piece by the angle in its state.
    - `HideableDef` draws hidden pieces at 30% alpha.
    - `ObscurableDef` draws `MaskImage` if obscured.
    - `PieceDrawFlags` is a `[Flags]` enum: `None`, `Selected`, `Ghost`.
  - `sealed class MapRenderer(ImageSource images, PieceRenderer pieces)`, with `void Draw(SKCanvas c, MapDef map, IReadOnlyDictionary<string,TileSet> tiles, GameState s, ModuleModel model, Viewport vp, SKSize client, MapDrawOptions opt)`. It draws only visible tiles at `ChooseLevel(zoom)`, then the grid overlay (if `opt.ShowGrid`), then trails for pieces with visible footprints, then stacks in `GameState.Stacks` insertion order (earlier drawn first), then the selection outline.
  - `sealed record MapDrawOptions(bool ShowGrid, IReadOnlySet<string> SelectedPieceIds, string? HoverStackId)`
  - `static string? HitTester.PieceAt(GameState s, MapDef map, ModuleModel model, ImageSource img, PointD mapPoint)`. It returns the topmost piece whose bounds contain the point, using the `NonRectangularDef` shape if present (otherwise the image rect), and skips pieces whose `ImmobileDef.SelectionLocked` is set.

- [ ] **Step 1: Write failing tests**
  - `MissingImage_ReturnsPlaceholderAndLogsOnce`: two `Get` calls give one diagnostic.
  - `Svg_RendersAtScaleBucket`: a 32×32 SVG at scale 2 gives a 64×64 image.
  - `Tiler_ProducesExpectedGridAndLevels`: a 3000×2000 fixture PNG gives 6×4 tiles at level 0 and L=2; each tile is ≤512 px.
  - `Tiler_SecondRunIsCacheHit`: the second run doesn't decode; assert via an `ImageSource` test hook counter or a file timestamp check.
  - `TileSet_ChooseLevel`: zoom 1 gives 0, 0.5 gives 1, 0.3 gives 1, 0.25 gives 2.
  - `Viewport_ZoomAt_KeepsAnchor`: `ToMap(anchor)` before equals after, within 1e-3.
  - `Viewport_ClampsZoom`
  - `StackLayout_CollapsedAndExpanded`: m=(6,18,2,4) with count 3 gives collapsed `[(0,0),(2,-4),(4,-8)]` and expanded `[(0,0),(6,-18),(12,-36)]`.
  - `PieceRenderer_LayerAtLevel2_DrawsSecondImage`: render to a 64×64 surface, with layer images red and blue, and level state=2. The center pixel is blue.
  - `HitTester_TopmostWins`, `HitTester_SelectionLockedSkipped`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement. Tiling: decode with `SKBitmap.Decode` once, build each level by `SKBitmap.Resize` from the previous level with `SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)`, and write tiles with `SKImage.Encode(SKEncodedImageFormat.Png, 100)`.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5: Commit** `feat: render library (images, tiles, viewport, stacks, pieces, hit-test)`

---

### Task 10: Piece actions (trait menus → commands)

**Files:**
- Create: `src/Quartermaster.Core/PieceActions.cs`
- Test: `tests/Quartermaster.Core.Tests/PieceActionsTests.cs`

**Interfaces:**
- Consumes: `ModuleModel`, `ExpandedPiece`, `GameState`, commands, `IMapGrid`.
- Produces:
  - `sealed record PieceAction(string Label, string Group, Func<GameState, IReadOnlyList<GameCommand>> Build)`
  - `static IReadOnlyList<PieceAction> PieceActions.For(PieceInstance p, ModuleModel model, GameState s, Func<string> newId)`. Actions per trait:

    | Trait | Action and commands |
    |---|---|
    | `SendToDef` | `CommandName` → `MovePieces` to the target map. The point is resolved per `DestinationType`: X/Y plus board offset, a grid location through the board grid's inverse lookup (port `getLocation` from the grid), or the centre of the zone polygon. If it can't be resolved, the action is shown disabled with a diagnostic. |
    | `CloneDef` | `AddPiece` with the same type and current state, stacked onto the source stack |
    | `DeleteDef` | `RemovePiece` |
    | `LayerDef` | activate, up, down and reset commands → `SetTraitState` with the new level string. The state encoding is ported from `Embellishment.java`. |
    | `RotatorDef` | CW/CCW → `SetTraitState` |
    | `HideableDef` / `ObscurableDef` | toggle → `SetTraitState` |
    | `DynamicPropertyDef` | increment/decrement/set commands → `SetTraitState`. "Prompt" kinds are returned with `Group="prompt"`, so the UI asks for a value. |
    | `PlaceMarkerDef` | `AddPiece` from `MarkerSpec`, or from the slot at `MarkerSlotPath` |
    | `ReplaceDef` | the same as `PlaceMarkerDef`, plus `RemovePiece` of the source |
    | `FootprintDef` | `ToggleTrail` |
    | Map-level "Mark all unmoved" | `ClearMoved`. Exposed separately: `PieceActions.ForMap(MapDef m)`. |
    | `PropertySheetDef` | `Group="sheet"`. The UI opens the dialog, and saving produces one `SetTraitState`. |

  `ImmobileDef.MoveLocked` pieces don't get the drag-move; the App checks `PieceActions.CanMove(...)`.

- [ ] **Step 1: Write failing tests**
  - `SendTo_XY_MovesToBoardOffsetPoint`
  - `SendTo_GridLocation_ResolvesThroughNumbering`
  - `SendTo_Unresolvable_IsDisabled`
  - `Clone_StacksOntoSource`
  - `Layer_UpWraps_WhenLoop`
  - `Rotate_Cw_AdvancesAngleIndex`
  - `PlaceMarker_FromSlotPath_AddsNewPiece`
  - `Replace_RemovesSourceAndAddsReplacement`
  - `MoveLocked_CanMoveFalse`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5: Commit** `feat: piece actions from traits`

---

### Task 11: App shell, module loading, diagnostics

**Files:**
- Create: `src/Quartermaster.Render/ModuleSession.cs` (no WinForms), `src/Quartermaster.App/Program.cs`, `MainForm.cs`, `ModuleTreePanel.cs`, `SetupChooserDialog.cs`, `DiagnosticsWindow.cs`
- Test: `tests/Quartermaster.Render.Tests/ModuleSessionSmokeTests.cs`, plus the manual check below

**Interfaces:**
- Consumes: everything above.
- Produces:
  - `sealed class ModuleSession : IDisposable`, with:
    - `VmodArchive Archive`
    - `ModuleModel Model`
    - `DiagnosticLog Log`
    - `ImageSource Images`
    - `BoardTiler Tiler`
    - `GameSession? Game`
    - `string CacheDir`
    - `static ModuleSession Open(string vmodPath)`
    - `void StartNewGame()`
    - `void LoadSetup(SetupDef s)`
    - `void LoadNative(string path)`, which fails with a clear message if the save's `moduleSha256` differs from the open module
    - `void SaveNative(string path)`
  - `MainForm` (MDI container) has:
    - **Menu:**
      - File: Open Module…, New Game, Load Setup…, Open Game…, Save Game, Save Game As…, Exit
      - View: Module Tree, Diagnostics, Show Grid (checked)
      - Window: Cascade, Tile Horizontally, Tile Vertically, Close All, plus the MDI list
      - Help: About
    - **ToolStrip** with the same core actions.
    - **StatusStrip** with panels: module name/version, map coordinate and location name, zoom %, tiling progress.
    - The docked left panel is `ModuleTreePanel`: a `TreeView` over `Model.Tree`. Double-click opens the node's window, or activates it if it's already open.
  - The title is `Quartermaster: <module name> <version>` plus ` — <save name>` and `*` when unsaved.
  - `DiagnosticsWindow` is a `ListView` (Details) with columns Severity, Category, Message, Context. It live-updates via `Log.Added`, has a filter by severity, and offers "Copy all".
  - Open-module flow:
    1. `OpenFileDialog` filtered to `*.vmod`.
    2. A busy cursor while the module is opened and the model built.
    3. If there are diagnostics with Severity ≥ Warning, a status bar notice is clickable to open Diagnostics.
    4. `SetupChooserDialog` lists `Model.Setups` and "New game from module" (SetupStacks).
    5. The Main map (the first `Map`) opens maximized.

- [ ] **Step 1:** Write the `ModuleSession` smoke test on a `FixtureModule`: open, `LoadSetup`, assert pieces > 0, `SaveNative` then `LoadNative` round-trip, and a mismatched hash throws `InvalidOperationException` mentioning "different module".
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement `ModuleSession`, then the forms. `Program.Main` calls `Application.SetHighDpiMode(PerMonitorV2)`, `Application.SetDefaultFont(new Font("Tahoma", 8.25f))` and `ApplicationConfiguration.Initialize()`, wraps everything in a top-level exception dialog, and logs crashes to `%LOCALAPPDATA%\Quartermaster\crash.log`.
- [ ] **Step 4:** Run the tests. Expected: PASS. Then `dotnet run --project src/Quartermaster.App`, open a fixture module, and verify: the tree shows 5 folders, the Diagnostics window lists the fixture's unknown element, and the title updates.
- [ ] **Step 5: Commit** `feat: app shell, module session, diagnostics window`

---

### Task 12: Map windows (maps and org displays)

**Files:**
- Create: `src/Quartermaster.App/MapWindow.cs`, `MapCanvas.cs`, `CounterDetailPopup.cs`, `PropertySheetDialog.cs`

**Interfaces:**
- Consumes: `MapRenderer`, `Viewport`, `HitTester`, `PieceActions`, `GameSession`, `BoardTiler`.
- Produces:
  - `MapWindow(ModuleSession s, MapDef map)`, an MDI child with title `map.Name`. It's the same class for org displays.
  - `MapCanvas : SKControl`, double-buffered, with these inputs:
    - **Wheel** zooms at the cursor (×1.2 per notch).
    - **Middle-drag, or Space + left-drag**, pans.
    - **Left-click** selects the topmost piece; Ctrl+click adds to the selection. Clicking empty space starts a rubber-band selection.
    - **Left-drag on a piece** draws a ghost. On drop, the target is snapped via the board grid; if the drop lands on an existing stack's footprint, the moved pieces stack onto it. This gives one `MovePieces` per drag.
    - **Dropping a piece on another `MapWindow`** moves it to that map (WinForms drag-drop with a custom data format).
    - **Right-click** shows a `ContextMenuStrip` from `PieceActions.For`, grouped by `Group`, with separators. "prompt" actions open an `InputBox`-style dialog; "sheet" opens `PropertySheetDialog`.
    - **Hovering 600 ms over a stack** shows `CounterDetailPopup`, which draws every piece in the stack at 100% with names and properties (from `mark` and `PROP` traits). It hides on mouse-leave.
    - **Double-click a stack** toggles `SetStackExpanded`.
  - The status bar shows the coordinates and grid location name under the cursor, and the zoom %.
  - On first open of a board without tiles, a centered "Preparing map… N%" message is drawn and `EnsureTilesAsync` runs in the background. The canvas then invalidates when tiles are ready.
  - `GameSession.Changed` → `Invalidate()` on all map windows; this is marshalled to the UI thread.
  - Map menu (shown on canvas right-click on empty space): Mark All Unmoved, Show Grid, Zoom to Fit, Zoom 100%.

- [ ] **Step 1:** Implement.
- [ ] **Step 2:** Manual check on a fixture module with a 3000×2000 board:
  - zooming is smooth;
  - zoom clamps at 25% and 400%;
  - dragging snaps to grid centers;
  - dropping onto a piece stacks it;
  - the right-click menu lists Clone and Delete, and both work;
  - hovering shows the detail popup;
  - Save, close, and reopen the save gives an identical layout.
- [ ] **Step 3:** Run `dotnet test`. Expected: all green, no regressions.
- [ ] **Step 4: Commit** `feat: map windows with pan/zoom, drag-move, menus, detail viewer`

---

### Task 13: Charts, documents, and acceptance on the real module

**Files:**
- Create: `src/Quartermaster.App/ChartWindow.cs`, `PdfWindow.cs`
- Modify: `ModuleTreePanel.cs` (open handlers)

**Interfaces:**
- Produces:
  - `ChartWindow(ModuleSession s, ChartDef c)`: a scrollable, zoomable image viewer (reusing `Viewport`, on an `SKControl`) that also has Fit and 100% buttons.
  - `PdfWindow(ModuleSession s, DocumentDef d, int page = 1)`. It extracts the entry once to `<cacheDir>\docs\<sanitized name>` and hosts `WebView2` with its user data folder at `%LOCALAPPDATA%\Quartermaster\webview2`, navigating to `file:///…#page=N`. If the WebView2 runtime is missing, it shows a message with a link to install it.
    - `.txt` documents open in a read-only `TextBox` in Consolas.
    - `void GoToPage(int page)` is used by Rule ref… in piece 2.

- [ ] **Step 1:** Implement.
- [ ] **Step 2: Acceptance on the user's module (local only, never committed).** Open `%USERPROFILE%\Downloads\CNAv2.1.0.vmod` and check each item:
  1. The tree shows 5 Maps (main plus 4 control sheets), 98 organisation displays nested under their tab/panel folders, 236 charts in 15 groups, 3 rulebook PDFs plus the text files, and 5 setups.
  2. Load each of the 4 bundled setups. Import reports 0 skipped commands, apart from known roster and board-picker lines, which are logged as Info. Counters appear on the main map and org displays.
  3. The main map tiles once, with progress shown in the status bar; the second open is instant. Panning at 25% and 400% is smooth.
  4. Spot-check 10 random counters: layered (`emb2`) counters show the right image, labels render, and moved markers appear after a move.
  5. Right-click actions work: send-to a control sheet, clone, delete, a layer change, the property sheet (Ammo/Fuel/Water/Stores edit and persist).
  6. Each PDF opens and `GoToPage(10)` lands on page 10.
  7. Save, close the app, reopen, and Open Game gives an identical state.
  8. The Diagnostics window has no Error entries. Every Warning is explained (a missing image in the module itself, or a deliberately unsupported item). Record the list in the commit message.
- [ ] **Step 3:** Fix anything acceptance turns up, with a regression test on a synthetic fixture for each parser or importer fix.
- [ ] **Step 4:** Run `dotnet test`. Expected: all green.
- [ ] **Step 5: Commit** `feat: chart and document windows; piece 1 acceptance`
