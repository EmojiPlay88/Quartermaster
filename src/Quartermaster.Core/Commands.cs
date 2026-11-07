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
[JsonDerivedType(typeof(LedgerDelta), "ledgerDelta")]
[JsonDerivedType(typeof(SeedLedger), "seedLedger")]
[JsonDerivedType(typeof(RollDice), "rollDice")]
[JsonDerivedType(typeof(SetNotes), "setNotes")]
[JsonDerivedType(typeof(SetTurn), "setTurn")]
[JsonDerivedType(typeof(AdvancePhase), "advancePhase")]
[JsonDerivedType(typeof(SetDifficulty), "setDifficulty")]
[JsonDerivedType(typeof(TickChecklist), "tickChecklist")]
[JsonDerivedType(typeof(AcknowledgeWarning), "acknowledgeWarning")]
[JsonDerivedType(typeof(QueueRuns), "queueRuns")]
[JsonDerivedType(typeof(ResolveRun), "resolveRun")]
[JsonDerivedType(typeof(AddLogLine), "addLogLine")]
[JsonDerivedType(typeof(SteppedBack), "steppedBack")]
[JsonDerivedType(typeof(HideAlert), "hideAlert")]
public abstract record GameCommand;

public sealed record AddPiece(string PieceId, string TypeString, string StateString, string MapId, PointD Pos, string? StackOnto, string? LogText = null, DateTimeOffset? At = null, string? Who = null) : GameCommand;
public sealed record MovePieces(IReadOnlyList<string> PieceIds, string MapId, PointD Pos, string? StackOnto, string? LogText = null, DateTimeOffset? At = null, string? Who = null) : GameCommand;
public sealed record RemovePiece(string PieceId, string? LogText = null, DateTimeOffset? At = null, string? Who = null) : GameCommand;
public sealed record SetTraitState(string PieceId, int StateIndex, string NewState) : GameCommand;
public sealed record SetStackExpanded(string StackId, bool Expanded) : GameCommand;
public sealed record ClearMoved(string MapId) : GameCommand;
public sealed record ToggleTrail(string PieceId) : GameCommand;

/// <summary>Sets a ledger value to After (absolute, so replay is deterministic). Source is "formula", "manual" or "seed".</summary>
public sealed record LedgerDelta(string Key, string PropertyId, double Before, double After, string Source, string? FormulaId, string? RuleRefId, string? Note, string? Working, DateTimeOffset? At = null, string? Who = null, string? Tag = null, string? Subject = null) : GameCommand;
public sealed record LedgerSeed(string Key, string PropertyId, double Value);
public sealed record SeedLedger(IReadOnlyList<LedgerSeed> Seeds) : GameCommand;
public sealed record RollDice(string Purpose, int Count, int Sides, IReadOnlyList<int> Results, string? Expression = null, IReadOnlyList<string>? FaceTexts = null, int Modifier = 0, DateTimeOffset? At = null, string? Who = null) : GameCommand;
public sealed record SetNotes(string Text, DateTimeOffset? At = null, string? Who = null) : GameCommand;
public sealed record SetTurn(int Turn, string? Phase, DateTimeOffset? At = null, string? Who = null) : GameCommand;
public sealed record AdvancePhase(PhasePosition? From, PhasePosition To, string FromName, string ToName, string? Reason = null, DateTimeOffset? At = null, string? Who = null) : GameCommand;
public sealed record SetDifficulty(Difficulty Difficulty, DateTimeOffset? At = null, string? Who = null) : GameCommand;
public sealed record TickChecklist(int Index, bool Ticked, string Item, DateTimeOffset? At = null, string? Who = null) : GameCommand;
public sealed record AcknowledgeWarning(int Index, string Message, DateTimeOffset? At = null, string? Who = null) : GameCommand;
public sealed record QueueRuns(IReadOnlyList<PendingRun> Runs, DateTimeOffset? At = null, string? Who = null) : GameCommand;
public sealed record ResolveRun(string RunId, IReadOnlyList<string?> PieceIds, DateTimeOffset? At = null, string? Who = null) : GameCommand;
public sealed record AddLogLine(string Kind, string Text, string? PieceId = null, DateTimeOffset? At = null, string? Who = null) : GameCommand;

/// <summary>Log line recorded after stepping back to a checkpoint. Folding: never an undoable action of its own.</summary>
public sealed record SteppedBack(int ToIndex, int ActionsUndone, int DiceUndone, string Target, DateTimeOffset? At = null, string? Who = null) : GameCommand;

/// <summary>Hides an alert (by alert id) until the turn number changes. An ordinary undoable action.</summary>
public sealed record HideAlert(string AlertId, DateTimeOffset? At = null, string? Who = null) : GameCommand;
