using System.Text.Json.Serialization;
using Quartermaster.Rules;

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

/// <summary>Where the game is in the turn sequence. Repeat is 1-based and is 1 outside blocks; in a nested phase it is the outer block's
/// repeat.</summary>
public sealed record PhasePosition(int Turn, int Repeat, string PhaseId, int PhaseIndex)
{
    /// <summary>The inner block's repeat (B5: a block inside another block), 1-based; 0 outside an inner block. Read by <c>segment()</c>.
    /// Part of record equality. Omitted when 0, so older saves keep their bytes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Inner { get; init; }
}

/// <summary>A phase passed over by Next Phase because its condition was false, at the position it would have had.</summary>
public sealed record SkippedPhase(string PhaseId, string Name, int Turn, int Repeat);

public sealed record PendingUnit(string? PieceId, string Label, double? Moved, bool Applied)
{
    /// <summary>The unit's roll group (B4, a <see cref="FormulaDef.GroupRoll"/> formula): "{sideId}/{k}" (<see cref="RollGroups.Assign"/>),
    /// shared by the run's units in one hex of one side; null = rolls its own. Omitted when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Group { get; init; }

    /// <summary>The unit was resolved because its piece was removed by another run (<see cref="ResolveRun.Gone"/>): counted as removed, not
    /// applied, in the batch summary. Omitted when false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Removed { get; init; }
}

/// <summary>A queued formula run awaiting the player. Trigger is "start" or "move".</summary>
public sealed record PendingRun(string Id, string FormulaId, string FormulaName, string Trigger, IReadOnlyList<PendingUnit> Units, bool NeedsInput)
{
    /// <summary>The side whose player answers this run (a start or end entry's <c>AskSideId</c>, spec #72): only players holding it see
    /// and apply it (solo: anyone), and its commands are stamped with it. Omitted when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AskSideId { get; init; }

    /// <summary>The dice each roll group drew (group → key → dice; B4): the first applied unit of a group fixes a key, the group's other
    /// units reuse it (<see cref="RollGroups.SharedFor"/>). Host only: <see cref="PlayerView"/> drops it. Omitted when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<int>>>? GroupRolls { get; init; }

    /// <summary>B7 #53: the sealed group this run belongs to (the id of the group's first run): a sealed start entry queues one ask run per
    /// side, all with the same seal. Omitted when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Seal { get; init; }

    /// <summary>B7 #53: the side's sealed answer (Input variable → exact typed text, <see cref="SealAnswer"/>), applied when every run of the
    /// group has one. Hidden as the run is: only in the full state and the asked side's view (every view once the game is over). Omitted when
    /// null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? SealedAnswer { get; init; }
}

/// <summary>
/// Where the phase's start entries stopped (spec #32): at position <paramref name="At"/>, entry <paramref name="Next"/> (0-based) is the next
/// to run once nothing started there is waiting. A <paramref name="Next"/> past the entries counts as done.
/// </summary>
public sealed record StartCursor(PhasePosition At, int Next);
