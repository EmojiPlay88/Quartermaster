using Quartermaster.Vassal;

namespace Quartermaster.Rules;

/// <summary>The one resolver of a map reference in a pack (a Remove output's To map).</summary>
public static class MapRefs
{
    /// <summary>The map the text names: trimmed, an exact id (ordinal) first, then an exact name (ordinal). Blank or unknown gives null.</summary>
    public static MapDef? Find(IReadOnlyList<MapDef> maps, string? idOrName)
    {
        var key = idOrName?.Trim();
        if (string.IsNullOrEmpty(key)) return null;
        foreach (var m in maps) if (string.Equals(m.Id, key, StringComparison.Ordinal)) return m;
        foreach (var m in maps) if (string.Equals(m.Name, key, StringComparison.Ordinal)) return m;
        return null;
    }
}
