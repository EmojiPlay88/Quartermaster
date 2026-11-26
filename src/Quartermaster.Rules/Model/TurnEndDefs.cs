using System.Text.Json.Serialization;

namespace Quartermaster.Rules;

/// <summary>What a schedule row does to the counters its filter matches.</summary>
public enum ScheduleAction { Arrive, Leave, Return }

/// <summary>
/// One scheduled event: at a given turn (and stage) and phase, the counters matching <see cref="Piece"/> arrive on the board,
/// leave it, or come back. Omitted from the pack JSON when the pack has none.
/// </summary>
public sealed record ScheduleRow
{
    public string Id { get; init; } = "";
    /// <summary>The turn the row falls due (1 or more).</summary>
    public int Turn { get; init; } = 1;
    /// <summary>The stage within the turn; null = any stage. Omitted from the pack JSON when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Stage { get; init; }
    /// <summary>The phase in which the row is offered.</summary>
    public string PhaseId { get; init; } = "";
    /// <summary>Unit filter choosing the counters the row acts on.</summary>
    public string Piece { get; init; } = "";
    /// <summary>Arrive (the default), Leave or Return. Omitted from the pack JSON when Arrive.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ScheduleAction Action { get; init; } = ScheduleAction.Arrive;
    /// <summary>Id of the zone map counters arrive from or leave to. Omitted from the pack JSON when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ZoneMapId { get; init; }
    /// <summary>Id of a formula run on the counters when the row is done. Omitted from the pack JSON when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FormulaId { get; init; }
    /// <summary>Text shown for the row; null = a default per action. Omitted from the pack JSON when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; init; }
}

/// <summary>A condition that ends the game when it becomes true at the end of a phase. Omitted from the pack JSON when the pack has none.</summary>
public sealed record VictoryCondition
{
    public string Id { get; init; } = "";
    /// <summary>Boolean expression, evaluated like a phase condition.</summary>
    public string Condition { get; init; } = "";
    /// <summary>The winning side; null = no winner. Omitted from the pack JSON when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SideId { get; init; }
    /// <summary>Text shown at game over; null = a default. Omitted from the pack JSON when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
    /// <summary>Ids of the phases after which the condition is checked; null = every phase. Omitted from the pack JSON when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? PhaseIds { get; init; }
}
