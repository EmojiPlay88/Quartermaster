namespace Quartermaster.Render;

/// <summary>Stable toolbar item ids (settings key them, not captions) and the migration of older caption-keyed settings.</summary>
public static class ToolbarIds
{
    /// <summary>All toolbar items in display order: id and the friendly label shown in Settings.</summary>
    public static IReadOnlyList<(string Id, string Label)> All { get; } = new[]
    {
        ("tool.openModule", "Open Module"), ("tool.newGame", "New Game"), ("tool.loadSetup", "Load Setup"), ("tool.openGame", "Open Game"),
        ("tool.saveGame", "Save Game"), ("tool.diagnostics", "Diagnostics"), ("tool.notes", "Notes"), ("tool.overview", "Overview"),
        ("tool.inventory", "Inventory"),
        ("edit.undo", "Undo"), ("edit.redo", "Redo"), ("phase.stepBack", "Step back to start of phase"), ("turn.next", "Next turn"), ("tool.palette", "Counter palette (game master mode)"),
        ("dice.1d6", "Roll 1d6"), ("dice.2d6", "Roll 2d6"), ("dice.menu", "Dice menu"), ("tool.los", "Line of sight"),
    };

    static readonly Dictionary<string, string> Legacy = new(StringComparer.Ordinal)
    {
        ["Open Module"] = "tool.openModule", ["New Game"] = "tool.newGame", ["Load Setup"] = "tool.loadSetup", ["Open Game"] = "tool.openGame",
        ["Save Game"] = "tool.saveGame", ["Diagnostics"] = "tool.diagnostics", ["1d6"] = "dice.1d6", ["2d6"] = "dice.2d6", ["Dice"] = "dice.menu",
    };

    /// <summary>Maps caption keys of older settings to ids; an id already present wins; keys that are neither a caption nor an id are dropped.</summary>
    public static IReadOnlyDictionary<string, bool> Migrate(IReadOnlyDictionary<string, bool> old)
    {
        var known = All.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, bool>();
        foreach (var (k, v) in old) if (known.Contains(k)) result[k] = v;
        foreach (var (k, v) in old) if (Legacy.TryGetValue(k, out var id)) result.TryAdd(id, v);
        return result;
    }
}
