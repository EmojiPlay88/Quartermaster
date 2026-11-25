using System.Text.Json.Serialization;

namespace Quartermaster.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Difficulty { Hardcore, SemiAuto, FullyAuto }

public static class DifficultyText
{
    public static string Display(Difficulty d) => d switch
    {
        Difficulty.Hardcore => "Hardcore",
        Difficulty.SemiAuto => "Semi-automatic",
        Difficulty.FullyAuto => "Fully automatic",
        _ => d.ToString(),
    };
}

/// <summary>Where the game is in the turn sequence. Repeat is 1-based and is 1 outside blocks.</summary>
public sealed record PhasePosition(int Turn, int Repeat, string PhaseId, int PhaseIndex);

/// <summary>A phase passed over by Next Phase because its condition was false, at the position it would have had.</summary>
public sealed record SkippedPhase(string PhaseId, string Name, int Turn, int Repeat);

public sealed record PendingUnit(string? PieceId, string Label, double? Moved, bool Applied);

/// <summary>A queued formula run awaiting the player. Trigger is "start" or "move".</summary>
public sealed record PendingRun(string Id, string FormulaId, string FormulaName, string Trigger, IReadOnlyList<PendingUnit> Units, bool NeedsInput)
{
    /// <summary>The side whose player answers this run (a start or end entry's <c>AskSideId</c>, spec #72): only players holding it see
    /// and apply it (solo: anyone), and its commands are stamped with it. Omitted when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AskSideId { get; init; }
}

/// <summary>
/// Where the phase's start entries stopped (spec #32): at position <paramref name="At"/>, entry <paramref name="Next"/> (0-based) is the next
/// to run once nothing started there is waiting. A <paramref name="Next"/> past the entries counts as done.
/// </summary>
public sealed record StartCursor(PhasePosition At, int Next);
