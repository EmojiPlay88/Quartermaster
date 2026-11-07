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

public sealed record MoveEntry
{
    public string FormulaId { get; init; } = "";
    public string? UnitFilter { get; init; }
}

public sealed record PhaseWarning
{
    public string Condition { get; init; } = "";
    /// <summary>May contain the token {unit}, replaced by the unit name in per-unit rows.</summary>
    public string Message { get; init; } = "";
    public bool IsDecision { get; init; }
}

public sealed record PhaseDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? SideId { get; init; }
    public string Instructions { get; init; } = "";
    public string? RuleRefId { get; init; }
    public List<string> Checklist { get; init; } = new();
    public List<StartEntry> OnStart { get; init; } = new();
    public List<MoveEntry> OnMove { get; init; } = new();
    public List<PhaseWarning> Warnings { get; init; } = new();
    public string? Notes { get; init; }
}

public sealed record PhaseBlock
{
    public string FirstPhaseId { get; init; } = "";
    public string LastPhaseId { get; init; } = "";
    public int Count { get; init; } = 1;
    public string Label { get; init; } = "";
}
