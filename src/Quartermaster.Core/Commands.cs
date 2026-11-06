using System.Text.Json.Serialization;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[JsonDerivedType(typeof(AddPiece), "addPiece")]
[JsonDerivedType(typeof(MovePieces), "movePieces")]
[JsonDerivedType(typeof(RemovePiece), "removePiece")]
[JsonDerivedType(typeof(SetTraitState), "setTraitState")]
[JsonDerivedType(typeof(SetStackExpanded), "setStackExpanded")]
[JsonDerivedType(typeof(ClearMoved), "clearMoved")]
[JsonDerivedType(typeof(ToggleTrail), "toggleTrail")]
public abstract record GameCommand;

public sealed record AddPiece(string PieceId, string TypeString, string StateString, string MapId, PointD Pos, string? StackOnto) : GameCommand;
public sealed record MovePieces(IReadOnlyList<string> PieceIds, string MapId, PointD Pos, string? StackOnto) : GameCommand;
public sealed record RemovePiece(string PieceId) : GameCommand;
public sealed record SetTraitState(string PieceId, int StateIndex, string NewState) : GameCommand;
public sealed record SetStackExpanded(string StackId, bool Expanded) : GameCommand;
public sealed record ClearMoved(string MapId) : GameCommand;
public sealed record ToggleTrail(string PieceId) : GameCommand;
