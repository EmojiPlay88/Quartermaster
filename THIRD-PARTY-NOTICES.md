# Third-party notices

Parts of `src/Quartermaster.Vassal` are ported from VASSAL (https://github.com/vassalengine/vassal),
which is licensed under the GNU Lesser General Public License, version 2.1 (LGPL-2.1).

Ported files:

- `src/Quartermaster.Vassal/SequenceEncoder.cs` (from `VASSAL/tools/SequenceEncoder.java`)
- `src/Quartermaster.Vassal/Obfuscation.cs` (from `VASSAL/tools/io/DeobfuscatingInputStream.java`)
- `src/Quartermaster.Vassal/Traits/PieceType.cs` (trait `mySetType` readers from `VASSAL/counters/` BasicPiece, UsePrototype, Embellishment, Labeler, Marker, SendToLocation, Immobilized, PropertySheet, PlaceMarker, Replace, Clone, Delete, Hideable, Obscurable, DynamicProperty, FreeRotator, Footprint, MovementMarkable, NonRectangular)
