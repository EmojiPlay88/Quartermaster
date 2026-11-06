namespace Quartermaster.Rules;

public enum TableKind { OneD, TwoD, Dice }

/// <summary>User-authored lookup table. Cells[r][c] holds raw text; OneD and Dice tables have a single column headed "Value".</summary>
public sealed record TableDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public TableKind Kind { get; init; }
    public List<string> RowKeys { get; init; } = new();
    public List<string> ColumnKeys { get; init; } = new();
    public List<List<string>> Cells { get; init; } = new();
    public string? RuleRefId { get; init; }
    public string? Notes { get; init; }
}
