namespace Quartermaster.Vassal;

/// <summary>Typed view of a module's buildFile. Built by <see cref="ModuleModelBuilder"/>.</summary>
public sealed class ModuleModel
{
    private readonly Dictionary<string, PieceSlotDef> _byGpid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PieceSlotDef> _byPath = new(StringComparer.Ordinal);

    internal ModuleModel(string name, string version, string vassalVersion, IReadOnlyList<MapDef> maps, IReadOnlyList<PieceSlotDef> slots,
        IReadOnlyDictionary<string, PieceType> prototypes, IReadOnlyList<ChartDef> charts, IReadOnlyList<DocumentDef> documents,
        IReadOnlyList<SetupDef> setups, IReadOnlyList<DieDef> dice, IReadOnlyList<string> sides, WindowTreeNode tree, PrototypeExpander expander,
        IReadOnlyList<string>? setupPieceTypes = null, bool setupsUnreadable = false)
    {
        Name = name; Version = version; VassalVersion = vassalVersion; Maps = maps; Slots = slots; Prototypes = prototypes;
        Charts = charts; Documents = documents; Setups = setups; Dice = dice; Sides = sides; Tree = tree; Expander = expander;
        SetupPieceTypes = setupPieceTypes ?? Array.Empty<string>(); SetupsUnreadable = setupsUnreadable;
        foreach (var s in slots) Index(s);
        foreach (var s in maps.SelectMany(m => m.SetupStacks).SelectMany(st => st.Pieces)) Index(s);
    }

    private void Index(PieceSlotDef s)
    {
        if (s.Gpid.Length > 0) _byGpid.TryAdd(s.Gpid, s);
        _byPath.TryAdd(string.Join('/', s.NavPath.Append(s.EntryName)), s);
    }

    public string Name { get; }
    public string Version { get; }
    public string VassalVersion { get; }
    public IReadOnlyList<MapDef> Maps { get; }
    /// <summary>Palette slots (PieceWindow widgets). Pieces inside SetupStacks are on <see cref="SetupStackDef.Pieces"/>.</summary>
    public IReadOnlyList<PieceSlotDef> Slots { get; }
    public IReadOnlyDictionary<string, PieceType> Prototypes { get; }
    public IReadOnlyList<ChartDef> Charts { get; }
    public IReadOnlyList<DocumentDef> Documents { get; }
    public IReadOnlyList<SetupDef> Setups { get; }
    public IReadOnlyList<DieDef> Dice { get; }
    public IReadOnlyList<string> Sides { get; }
    public WindowTreeNode Tree { get; }
    public PrototypeExpander Expander { get; }
    /// <summary>Distinct type strings of the pieces the module's predefined setup saves (.vsav) add, in first-seen order.</summary>
    public IReadOnlyList<string> SetupPieceTypes { get; }
    /// <summary>True when a predefined setup's save file is missing or could not be read (its piece types are unknown).</summary>
    public bool SetupsUnreadable { get; }

    /// <summary>Slot by gpid; palette slots win over setup-stack pieces.</summary>
    public PieceSlotDef? SlotByGpid(string gpid) => gpid != null && _byGpid.TryGetValue(gpid, out var s) ? s : null;

    /// <summary>Slot by NavPath segments joined with '/', then the entry name.</summary>
    public PieceSlotDef? SlotByPath(string path) => path != null && _byPath.TryGetValue(path, out var s) ? s : null;
}
