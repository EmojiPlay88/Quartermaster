using System.Text.Json.Serialization;

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
    /// <summary>Input only: asked in every difficulty; the AutoValue is never used to fill it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AlwaysAsk { get; init; }
    /// <summary>Expression: when it is false the binding is not resolved (a Roll is not rolled, an Input is not asked). Null = always used (the Workbench stores null, never "").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? When { get; init; }
    /// <summary>Roll only: expression giving the number of dice (a whole number 1-100) in place of <see cref="DiceCount"/>; it may not read this binding. Null = the fixed DiceCount (the Workbench stores null, never "").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DiceCountExpr { get; init; }
}

public sealed record FormulaOutput
{
    public OutputTarget Target { get; init; }
    public string? HolderId { get; init; }
    public string Property { get; init; } = "";
    public OutputOp Op { get; init; }
    /// <summary>Expression giving this output's value, read from the formula's bindings (the formula's own result is not a variable). Null = the formula's result (the Workbench stores null, never "").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Expr { get; init; }
    /// <summary>Property ids the output may write, one picked per run by <see cref="PropertySelect"/>; when non-empty <see cref="Property"/> is not used. Null = the fixed Property (never an empty list).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? PropertyChoice { get; init; }
    /// <summary>Expression picking from <see cref="PropertyChoice"/>: a number N is the Nth listed property (1-based), a text its property name. Null = none (an error with a non-empty list; never "").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PropertySelect { get; init; }
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
    /// <summary>Phase ids the formula may be run in by a player (menu, formula dialog, host intent); hook runs (OnStart / OnMove / OnEnd) ignore it. Null = any phase (the Workbench stores null, never an empty list).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Phases { get; init; }
    /// <summary>Expression that must be true for the formula to run; false refuses the run. May not read a Roll binding or call dice(). Null = no guard (the Workbench stores null, never "").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Precondition { get; init; }
    /// <summary>Text shown when the precondition refuses a run. Null = "{formula name} cannot be run now".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RefusalMessage { get; init; }
}
