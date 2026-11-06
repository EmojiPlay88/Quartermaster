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

public sealed record PendingUnit(string? PieceId, string Label, double? Moved, bool Applied);

/// <summary>A queued formula run awaiting the player. Trigger is "start" or "move".</summary>
public sealed record PendingRun(string Id, string FormulaId, string FormulaName, string Trigger, IReadOnlyList<PendingUnit> Units, bool NeedsInput);
