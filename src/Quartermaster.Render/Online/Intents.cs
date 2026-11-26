using System.Text.Json.Serialization;
using Quartermaster.Core;

namespace Quartermaster.Render;

/// <summary>
/// What a guest asks the host to do (spec 8a.1). Guests never send computed results: the host turns each intent into commands with the
/// full game state, exactly as if it had done the action itself. JSON-polymorphic (<c>$t</c>) so it travels inside network payloads.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[JsonDerivedType(typeof(MoveIntent), "move")]
[JsonDerivedType(typeof(PieceCommandIntent), "piece")]
[JsonDerivedType(typeof(FormulaIntent), "formula")]
[JsonDerivedType(typeof(FormulaBatchIntent), "formulaBatch")]
[JsonDerivedType(typeof(LedgerEditIntent), "ledgerEdit")]
[JsonDerivedType(typeof(DiceIntent), "dice")]
[JsonDerivedType(typeof(TickIntent), "tick")]
[JsonDerivedType(typeof(AckIntent), "ack")]
[JsonDerivedType(typeof(NextPhaseIntent), "nextPhase")]
[JsonDerivedType(typeof(HideAlertIntent), "hideAlert")]
[JsonDerivedType(typeof(ChatIntent), "chat")]
[JsonDerivedType(typeof(ScheduleIntent), "schedule")]
public abstract record GameIntent;

/// <summary>One move gesture (any number of groups); the host fires the phase's OnMove formulas.</summary>
public sealed record MoveIntent(IReadOnlyList<MovePieces> Moves) : GameIntent;

/// <summary>A piece command (trait state, add / remove piece, trail toggle, mark unmoved); accepted by type, everything else is refused.</summary>
public sealed record PieceCommandIntent(GameCommand Command) : GameIntent;

/// <summary>
/// Run formula X on unit Y with these typed values, or with the engine's values when <paramref name="Calculate"/>; apply when it checks out.
/// With <paramref name="Preview"/> the host only runs it (nothing is executed, never applied) and answers with the visible values and the
/// dice it rolled; those dice are pinned for the sender's next Apply of the same formula on the same unit/run (the dialog's Calculate).
/// <paramref name="Picks"/> (optional; absent from older guests' messages): for a Piece binding, the id of the piece its typed label was
/// picked as. The host then refuses the run ("Choose one of the listed pieces") unless the label still resolves, among the sender's own
/// candidates, to that same piece.
/// </summary>
public sealed record FormulaIntent(string FormulaId, string? PieceId, string? RunId, IReadOnlyDictionary<string, string> Typed, bool Calculate,
    bool Preview = false, IReadOnlyDictionary<string, string>? Picks = null) : GameIntent;

/// <summary>
/// A guest's menu batch (B4): formula X run once per row, in order, each row on its own counter (a counter may repeat), applied by the host
/// as one undoable action only when every row can apply. The guest merges its "All rows" line into each row before sending. With
/// <paramref name="Preview"/> the host only runs the chain (nothing is executed) and answers with each row's visible values and dice; those
/// dice stay pinned for the sender's Apply of the same batch. At most <see cref="MenuBatchModel.MaxBatchRows"/> rows.
/// </summary>
public sealed record FormulaBatchIntent(string FormulaId, IReadOnlyList<FormulaBatchRow> Rows, bool Calculate, bool Preview) : GameIntent;

/// <summary>One row of a <see cref="FormulaBatchIntent"/>: its counter, typed values and picks, as a <see cref="FormulaIntent"/> carries them.</summary>
public sealed record FormulaBatchRow(string? PieceId, IReadOnlyDictionary<string, string> Typed, IReadOnlyDictionary<string, string>? Picks);

/// <summary>
/// The host's answer to a <see cref="FormulaBatchIntent"/>: <paramref name="Applied"/> when every row was applied, each row's verdict in
/// row order (states, visible values and dice only, as <see cref="FormulaVerdict"/>), and the message (<c>Row {n}: {message}</c>) of a
/// refused batch. Never carries a hidden value.
/// </summary>
public sealed record FormulaBatchVerdict(bool Applied, IReadOnlyList<FormulaVerdict> Rows, string? Message)
{
    public static FormulaBatchVerdict Refused(string message) => new(false, Array.Empty<FormulaVerdict>(), message);
}

/// <summary>A manual ledger change with its (required) note.</summary>
public sealed record LedgerEditIntent(string Key, string PropertyId, double After, string Note) : GameIntent;

/// <summary>A dice-tool roll; the host rolls.</summary>
public sealed record DiceIntent(DiceSpec Spec, string Purpose) : GameIntent;

public sealed record TickIntent(int Index, bool Ticked) : GameIntent;

public sealed record AckIntent(int Index) : GameIntent;

/// <summary>Next Phase (start the game or advance one phase).</summary>
public sealed record NextPhaseIntent : GameIntent
{
    /// <summary>B5 #6: the sender's answer to the repeat question, true = play another pass of the block (false = Continue). The host
    /// honours it only where it would ask the question itself, after the acting-side check. Omitted when false, so
    /// <c>{"$t":"nextPhase"}</c> is unchanged.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Repeat { get; init; }
}

/// <summary>B5 #62: act on schedule row <paramref name="RowId"/> (Place or Withdraw). The host checks it for the sender: only the row's
/// counters the sender may act on are touched, and an unknown, unreached or done row, or one without such a counter, gets one neutral
/// refusal.</summary>
public sealed record ScheduleIntent(string RowId) : GameIntent;

/// <summary>Host only (spec 8a.5): refused when a guest sends it.</summary>
public sealed record HideAlertIntent(string AlertId, string? Name) : GameIntent;

public sealed record ChatIntent(string Text) : GameIntent;

/// <summary>One input box of a formula as judged by the host. State is <see cref="Ok"/>, <see cref="Wrong"/> or <see cref="Hidden"/> (never a value).</summary>
public sealed record FormulaBox(string Var, string State)
{
    public const string Ok = "ok";
    public const string Wrong = "wrong";
    /// <summary>The input comes from data the sender may not see; the host calculates it.</summary>
    public const string Hidden = "hidden";
}

/// <summary>
/// The host's answer to a formula intent. Never carries a hidden value, in the boxes, the message or <paramref name="Values"/>. A preview
/// carries <paramref name="Values"/> (the visible variables' values, invariant text) and <paramref name="Rolls"/> (the pinned dice by roll
/// key) so the guest's boxes fill as in solo.
/// </summary>
public sealed record FormulaVerdict(bool Applied, IReadOnlyList<FormulaBox> Boxes, string? Message,
    IReadOnlyDictionary<string, string>? Values = null, IReadOnlyDictionary<string, IReadOnlyList<int>>? Rolls = null)
{
    public static FormulaVerdict Refused(string message) => new(false, Array.Empty<FormulaBox>(), message);
}

/// <summary>What the host replies to an intent: accepted or not, a message for the sender, and the verdict of a formula intent.</summary>
public sealed record IntentResult(bool Ok, string? Message, FormulaVerdict? Verdict = null)
{
    /// <summary>The verdict of a menu batch intent (<see cref="FormulaBatchIntent"/>); sent as the result's verdict JSON when <see cref="Verdict"/> is null.</summary>
    public FormulaBatchVerdict? BatchVerdict { get; init; }

    public static IntentResult Done() => new(true, null);
    public static IntentResult Refused(string message) => new(false, message);
}
