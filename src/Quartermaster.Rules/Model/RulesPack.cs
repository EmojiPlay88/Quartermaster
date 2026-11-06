namespace Quartermaster.Rules;

public enum PropertyScope { Pieces, Holders, Both }

public sealed record PropertyDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public PropertyScope Scope { get; init; }
    public double Default { get; init; }
    /// <summary>Names a VASSAL property-sheet row to seed from.</summary>
    public string? SheetField { get; init; }
    /// <summary>Optional boolean expression limiting which pieces get the property.</summary>
    public string? AppliesTo { get; init; }
    public string? Notes { get; init; }
}

public sealed record HolderDef
{
    /// <summary>The built-in holder; always exists and is never stored in a pack.</summary>
    public const string GlobalId = "global";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Notes { get; init; }
}

public sealed record RuleRef
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string DocumentEntry { get; init; } = "";
    public int Page { get; init; }
}

public sealed record RulesPack
{
    public int Format { get; init; } = 1;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string ModuleSha256 { get; init; } = "";
    public List<PropertyDef> Properties { get; init; } = new();
    public List<HolderDef> Holders { get; init; } = new();
    public List<TableDef> Tables { get; init; } = new();
    public List<FormulaDef> Formulas { get; init; } = new();
    public List<RuleRef> RuleRefs { get; init; } = new();
    public List<SideDef> Sides { get; init; } = new();
    public List<PhaseDef> Phases { get; init; } = new();
    public List<PhaseBlock> Blocks { get; init; } = new();

    public TableDef? TableByName(string name) => Tables.FirstOrDefault(t => Eq(t.Name, name));
    public PropertyDef? PropertyByName(string name) => Properties.FirstOrDefault(p => Eq(p.Name, name));
    public HolderDef? HolderByName(string name) => Holders.FirstOrDefault(h => Eq(h.Name, name));

    /// <summary>Finds an entity by exact id. Supported: PropertyDef, HolderDef, TableDef, FormulaDef, RuleRef, SideDef, PhaseDef.</summary>
    public T? ById<T>(string id) where T : class
    {
        if (typeof(T) == typeof(PropertyDef)) return Properties.FirstOrDefault(x => x.Id == id) as T;
        if (typeof(T) == typeof(HolderDef)) return Holders.FirstOrDefault(x => x.Id == id) as T;
        if (typeof(T) == typeof(TableDef)) return Tables.FirstOrDefault(x => x.Id == id) as T;
        if (typeof(T) == typeof(FormulaDef)) return Formulas.FirstOrDefault(x => x.Id == id) as T;
        if (typeof(T) == typeof(RuleRef)) return RuleRefs.FirstOrDefault(x => x.Id == id) as T;
        if (typeof(T) == typeof(SideDef)) return Sides.FirstOrDefault(x => x.Id == id) as T;
        if (typeof(T) == typeof(PhaseDef)) return Phases.FirstOrDefault(x => x.Id == id) as T;
        return null;
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
