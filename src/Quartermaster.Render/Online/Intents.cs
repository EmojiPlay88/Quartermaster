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
[JsonDerivedType(typeof(LedgerEditIntent), "ledgerEdit")]
[JsonDerivedType(typeof(DiceIntent), "dice")]
[JsonDerivedType(typeof(TickIntent), "tick")]
[JsonDerivedType(typeof(AckIntent), "ack")]
[JsonDerivedType(typeof(NextPhaseIntent), "nextPhase")]
[JsonDerivedType(typeof(HideAlertIntent), "hideAlert")]
[JsonDerivedType(typeof(ChatIntent), "chat")]
public abstract record GameIntent;

/// <summary>One move gesture (any number of groups); the host fires the phase's OnMove formulas.</summary>
public sealed record MoveIntent(IReadOnlyList<MovePieces> Moves) : GameIntent;

/// <summary>A piece command (trait state, add / remove piece, trail toggle, mark unmoved); accepted by type, everything else is refused.</summary>
public sealed record PieceCommandIntent(GameCommand Command) : GameIntent;

/// <summary>Run formula X on unit Y with these typed values, or with the engine's values when <paramref name="Calculate"/>; apply when it checks out.</summary>
public sealed record FormulaIntent(string FormulaId, string? PieceId, string? RunId, IReadOnlyDictionary<string, string> Typed, bool Calculate) : GameIntent;

/// <summary>A manual ledger change with its (required) note.</summary>
public sealed record LedgerEditIntent(string Key, string PropertyId, double After, string Note) : GameIntent;

/// <summary>A dice-tool roll; the host rolls.</summary>
public sealed record DiceIntent(DiceSpec Spec, string Purpose) : GameIntent;

public sealed record TickIntent(int Index, bool Ticked) : GameIntent;

public sealed record AckIntent(int Index) : GameIntent;

public sealed record NextPhaseIntent : GameIntent;

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

/// <summary>The host's answer to a formula intent. Never carries a hidden value, in the boxes or in the message.</summary>
public sealed record FormulaVerdict(bool Applied, IReadOnlyList<FormulaBox> Boxes, string? Message)
{
    public static FormulaVerdict Refused(string message) => new(false, Array.Empty<FormulaBox>(), message);
}

/// <summary>What the host replies to an intent: accepted or not, a message for the sender, and the verdict of a formula intent.</summary>
public sealed record IntentResult(bool Ok, string? Message, FormulaVerdict? Verdict = null)
{
    public static IntentResult Done() => new(true, null);
    public static IntentResult Refused(string message) => new(false, message);
}
