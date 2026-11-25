using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>What a formula output does to a piece besides writing a value (B3): place a palette counter, remove (or move) a counter, set a
/// layer's level or a mask.</summary>
public enum PieceEffectKind { Place, Remove, SetLayer, SetMask }

/// <summary>
/// One effect of a run, resolved when the trace is made (<see cref="FormulaTrace.Effects"/>) and turned into commands by
/// <see cref="FormulaRunner.Commands"/>. <paramref name="PieceId"/> is the piece acted on (Place: the new piece's id); <paramref name="Text"/>
/// is the working part, naming the piece as the acting player sees it ("Unknown piece" for a masked pick).
/// </summary>
public sealed record PieceEffect(PieceEffectKind Kind, string PieceId, string Text)
{
    /// <summary>The log line of the effect ("{formula}: {part}" with the piece's real name).</summary>
    public string LogText { get; init; } = "";
    /// <summary>The piece's real name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Place: the new piece's type and state, map, position and the stack it joins.</summary>
    public string? TypeString { get; init; }
    public string? StateString { get; init; }
    public string? MapId { get; init; }
    public PointD Pos { get; init; }
    public string? StackOnto { get; init; }

    /// <summary>Remove: the map the piece moves to (null = deleted); <see cref="Pos"/> is then its position there.</summary>
    public string? ToMapId { get; init; }

    /// <summary>SetLayer/SetMask: the trait's state index and its new state.</summary>
    public int TraitIndex { get; init; }
    public string? NewState { get; init; }

    /// <summary>The side of the piece acted on (null = a piece of no side): stamps its log line.</summary>
    public string? SideId { get; init; }
}
