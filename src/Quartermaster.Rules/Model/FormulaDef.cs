namespace Quartermaster.Rules;

public enum BindingKind { Unit, Global, Holder, Table, Location, Input, Roll, Moved }
public enum OutputTarget { Unit, Global, Holder }
public enum OutputOp { Subtract, Add, Set }

public sealed record VariableBinding
{
    public string Variable { get; init; } = "";
    public BindingKind Kind { get; init; }
    public string? Property { get; init; }
    public string? HolderId { get; init; }
    public string? TableId { get; init; }
    public string? RowExpr { get; init; }
    public string? ColExpr { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public List<string>? Choices { get; init; }
    public int? DiceCount { get; init; }
    public int? DiceSides { get; init; }
    public string? Caption { get; init; }
    /// <summary>Expression giving the value to pre-fill (checked mode compares typed input against it).</summary>
    public string? AutoValue { get; init; }
}

public sealed record FormulaOutput
{
    public OutputTarget Target { get; init; }
    public string? HolderId { get; init; }
    public string Property { get; init; } = "";
    public OutputOp Op { get; init; }
}

public sealed record FormulaDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Text { get; init; } = "";
    public List<VariableBinding> Bindings { get; init; } = new();
    public List<FormulaOutput> Outputs { get; init; } = new();
    public string? AppliesTo { get; init; }
    public string? RuleRefId { get; init; }
    public string? Notes { get; init; }
    /// <summary>When true, dice results of this formula are shown only to the rolling side's players.</summary>
    public bool PrivateRolls { get; init; } = false;
}
