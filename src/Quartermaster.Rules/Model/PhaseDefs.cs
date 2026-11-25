using System.Text.Json.Serialization;

namespace Quartermaster.Rules;

public sealed record SideDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? UnitFilter { get; init; }
}

public enum ForEachMode { Once, Units }

public sealed record StartEntry
{
    public string FormulaId { get; init; } = "";
    public ForEachMode ForEach { get; init; } = ForEachMode.Once;
    public string? UnitFilter { get; init; }
}

/// <summary>Whose moves fire an OnMove entry, relative to the phase's acting side.</summary>
public enum MoveSide { Acting, Other, Any }

public sealed record MoveEntry
{
    public string FormulaId { get; init; } = "";
    public string? UnitFilter { get; init; }
    /// <summary>Which side's moves fire the entry; Acting (the default) is the only behaviour older packs had.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public MoveSide FiresFor { get; init; } = MoveSide.Acting;
}

public sealed record PhaseWarning
{
    public string Condition { get; init; } = "";
    /// <summary>May contain the token {unit}, replaced by the unit name in per-unit rows.</summary>
    public string Message { get; init; } = "";
    public bool IsDecision { get; init; }
    /// <summary>Id of the rule reference this warning cites. Omitted from the pack JSON when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuleRefId { get; init; }
}

public sealed record PhaseDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? SideId { get; init; }
    public string Instructions { get; init; } = "";
    public string? RuleRefId { get; init; }
    public List<ChecklistItem> Checklist { get; init; } = new();
    public List<StartEntry> OnStart { get; init; } = new();
    /// <summary>Runs when the phase is left, like OnStart. Null = none (the Workbench stores null, never an empty list).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StartEntry>? OnEnd { get; init; }
    public List<MoveEntry> OnMove { get; init; } = new();
    public List<PhaseWarning> Warnings { get; init; } = new();
    public string? Notes { get; init; }
    /// <summary>Boolean expression over global properties, turn() and stage(); when false the phase is skipped. Null or empty = always runs.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Condition { get; init; }
}

public sealed record PhaseBlock
{
    public string FirstPhaseId { get; init; } = "";
    public string LastPhaseId { get; init; } = "";
    public int Count { get; init; } = 1;
    public string Label { get; init; } = "";
}
