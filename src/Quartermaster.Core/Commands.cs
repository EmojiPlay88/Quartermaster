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
[JsonDerivedType(typeof(SetPlayers), "setPlayers")]
[JsonDerivedType(typeof(SetDifficulty), "setDifficulty")]
[JsonDerivedType(typeof(TickChecklist), "tickChecklist")]
[JsonDerivedType(typeof(AcknowledgeWarning), "acknowledgeWarning")]
[JsonDerivedType(typeof(QueueRuns), "queueRuns")]
[JsonDerivedType(typeof(ResolveRun), "resolveRun")]
[JsonDerivedType(typeof(AddLogLine), "addLogLine")]
[JsonDerivedType(typeof(SteppedBack), "steppedBack")]
[JsonDerivedType(typeof(HideAlert), "hideAlert")]
[JsonDerivedType(typeof(EndPhase), "endPhase")]
[JsonDerivedType(typeof(ResetProperties), "resetProperties")]
public abstract record GameCommand;

/// <summary>CloneOf: the piece this one is a copy of (the Clone command); its stored ledger values are copied (<see cref="LedgerSeeder.BuildCloneSeeds"/>). Omitted when null.</summary>
public sealed record AddPiece(string PieceId, string TypeString, string StateString, string MapId, PointD Pos, string? StackOnto, string? LogText = null, DateTimeOffset? At = null, string? Who = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CloneOf = null) : GameCommand;
public sealed record MovePieces(IReadOnlyList<string> PieceIds, string MapId, PointD Pos, string? StackOnto, string? LogText = null, DateTimeOffset? At = null, string? Who = null) : GameCommand;
public sealed record RemovePiece(string PieceId, string? LogText = null, DateTimeOffset? At = null, string? Who = null) : GameCommand;
public sealed record SetTraitState(string PieceId, int StateIndex, string NewState) : GameCommand;
public sealed record SetStackExpanded(string StackId, bool Expanded) : GameCommand;
public sealed record ClearMoved(string MapId) : GameCommand;
public sealed record ToggleTrail(string PieceId) : GameCommand;

/// <summary>
/// Sets a ledger value to After (absolute, so replay is deterministic). Source is "formula", "manual" or "seed". PieceId: the unit a formula
/// ran on (its log line carries it even for holder outputs); SideId: the side it was run for, when the caller knows it. Both drive PlayerView.
/// </summary>
public sealed record LedgerDelta(string Key, string PropertyId, double Before, double After, string Source, string? FormulaId, string? RuleRefId, string? Note, string? Working, DateTimeOffset? At = null, string? Who = null, string? Tag = null, string? Subject = null, string? PieceId = null, string? SideId = null) : GameCommand
{
    /// <summary>The formula id when this delta is the one line that records an applied run (its first output; spec #71, <c>runs()</c>).
    /// Omitted when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AppliedRun { get; init; }
}
public sealed record LedgerSeed(string Key, string PropertyId, double Value);
/// <summary>CloneOf: set when the seeds copy a cloned piece's values from this source piece; such a seed is its own undoable step (never folding). Omitted when null.</summary>
public sealed record SeedLedger(IReadOnlyList<LedgerSeed> Seeds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CloneOf = null) : GameCommand;
public sealed record RollDice(string Purpose, int Count, int Sides, IReadOnlyList<int> Results, string? Expression = null, IReadOnlyList<string>? FaceTexts = null, int Modifier = 0, DateTimeOffset? At = null, string? Who = null, string? PieceId = null, string? SideId = null) : GameCommand;
public sealed record SetNotes(string Text, DateTimeOffset? At = null, string? Who = null) : GameCommand;
public sealed record SetTurn(int Turn, string? Phase, DateTimeOffset? At = null, string? Who = null) : GameCommand;
public sealed record SetPlayers(IReadOnlyList<PlayerDef> Players, DateTimeOffset? At = null, string? Who = null) : GameCommand;
/// <summary>Skipped: the phases passed over on the way to To (conditions false), in order. Replay applies To and Skipped as recorded.
/// Notice: a condition-error or guard notice from the step, logged as a system line so guests and later readers see it.</summary>
public sealed record AdvancePhase(PhasePosition? From, PhasePosition To, string FromName, string ToName, string? Reason = null, DateTimeOffset? At = null, string? Who = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SkippedPhase>? Skipped = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Notice = null) : GameCommand;
public sealed record SetDifficulty(Difficulty Difficulty, DateTimeOffset? At = null, string? Who = null) : GameCommand;
/// <summary>ItemId: the item's stable id (ticked by id, <see cref="GameState.TickedIds"/>); null = ticked by <paramref name="Index"/>. Omitted when null.</summary>
public sealed record TickChecklist(int Index, bool Ticked, string Item, DateTimeOffset? At = null, string? Who = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ItemId = null) : GameCommand;
public sealed record AcknowledgeWarning(int Index, string Message, DateTimeOffset? At = null, string? Who = null) : GameCommand;
public sealed record QueueRuns(IReadOnlyList<PendingRun> Runs, DateTimeOffset? At = null, string? Who = null) : GameCommand;
public sealed record ResolveRun(string RunId, IReadOnlyList<string?> PieceIds, DateTimeOffset? At = null, string? Who = null) : GameCommand;
/// <summary>FormulaId: set on a calc line that shows a formula's working (see <see cref="LogEntry.FormulaId"/>).
/// Error: the line reports a failed or refused run (see <see cref="LogEntry.Error"/>); omitted when false.</summary>
public sealed record AddLogLine(string Kind, string Text, string? PieceId = null, DateTimeOffset? At = null, string? Who = null, string? SideId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? FormulaId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Error = false) : GameCommand
{
    /// <summary>The formula id when this line records an applied run that has no outputs (its working line; spec #71). Omitted when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AppliedRun { get; init; }
}

/// <summary>Log line recorded after stepping back to a checkpoint. Folding: never an undoable action of its own.</summary>
public sealed record SteppedBack(int ToIndex, int ActionsUndone, int DiceUndone, string Target, DateTimeOffset? At = null, string? Who = null) : GameCommand;

/// <summary>
/// Hides an alert (by alert id) until the turn number changes. An ordinary undoable action. <paramref name="Name"/> is the alert's user-facing
/// name at the time, used only for the log line (the id is logged when it is missing).
/// </summary>
public sealed record HideAlert(string AlertId, DateTimeOffset? At = null, string? Who = null, string? Name = null) : GameCommand;

/// <summary>Marks the phase at <paramref name="At"/> as having run its end-of-phase entries this pass (<see cref="GameState.EndedAt"/>), so the
/// next Next Phase press advances without re-running them. Logs nothing; <see cref="AdvancePhase"/> clears it.</summary>
public sealed record EndPhase(PhasePosition At) : GameCommand;

/// <summary>
/// Deletes the listed properties from every ledger (pieces and holders) and drops a ledger left empty; ledger history is kept. Logs one
/// System line, <paramref name="Text"/> (<c>{phase}: reset {names}</c>: property names only, no counts, no pieces). Spec #56.
/// </summary>
public sealed record ResetProperties(IReadOnlyList<string> PropertyIds, string Text, DateTimeOffset? At = null, string? Who = null) : GameCommand;
