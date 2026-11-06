# Third-party notices

Parts of `src/Quartermaster.Vassal` are ported from VASSAL (https://github.com/vassalengine/vassal),
which is licensed under the GNU Lesser General Public License, version 2.1 (LGPL-2.1).

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
- `src/Quartermaster.Core/NewGameBuilder.cs` (from `VASSAL/build/module/map/SetupStack.java` and `counters/BasicPiece.java`)
