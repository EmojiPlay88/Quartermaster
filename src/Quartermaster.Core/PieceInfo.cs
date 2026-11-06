using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>Read-only facts about a piece (name, marker values, grid location), shared by formulas and the UI.</summary>
public static class PieceInfo
{
    private static readonly Dictionary<string, PieceType> Cache = new(StringComparer.Ordinal);
    private const int CacheLimit = 4096;

    public static PieceType TypeOf(PieceInstance p)
    {
        var key = p.TypeString ?? "";
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var t)) return t;
            t = PieceTypeParser.Parse(key, new DiagnosticLog(), null);
            if (Cache.Count >= CacheLimit) Cache.Clear();
            Cache[key] = t;
            return t;
        }
    }

    /// <summary>The basic piece name; empty when the piece has none.</summary>
    public static string Name(PieceInstance p) => TypeOf(p).Basic.Name;

    /// <summary>Marker (VASSAL Marker trait) values of one trait: the type holds the keys, the trait state holds comma-encoded values.</summary>
    public static IReadOnlyList<(string Key, string Value)> MarkerValues(MarkerDef m, string traitState)
    {
        var values = SequenceEncoder.Decode(traitState ?? "", ',');
        var list = new List<(string, string)>(m.Keys.Count);
        for (var k = 0; k < m.Keys.Count; k++)
            list.Add((m.Keys[k], k < values.Count ? values[k] : ""));
        return list;
    }

    /// <summary>All marker values, keyed case-insensitively; the outermost marker wins for a repeated key.</summary>
    public static IReadOnlyDictionary<string, string> Markers(PieceInstance p)
    {
        var type = TypeOf(p);
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < type.Traits.Count; i++)
        {
            if (type.Traits[i] is not MarkerDef m) continue;
            var state = i < p.TraitStates.Count ? p.TraitStates[i] ?? "" : "";
            foreach (var (k, v) in MarkerValues(m, state)) d.TryAdd(k, v);
        }
        return d;
    }

    /// <summary>Grid location name of the piece in board-local coordinates; null when there is no board or grid under it.</summary>
    public static string? Location(PieceInstance p, ModuleModel model)
    {
        var map = model.Maps.FirstOrDefault(m => m.Id == p.MapId) ?? model.Maps.FirstOrDefault(m => m.Name == p.MapId);
        if (map == null) return null;
        foreach (var b in map.Boards)
        {
            var lx = p.Pos.X - b.Offset.X;
            var ly = p.Pos.Y - b.Offset.Y;
            if (lx < 0 || ly < 0 || lx >= b.Width || ly >= b.Height) continue;
            if (b.Grid == null) return null;
            var name = b.Grid.LocationName(new PointD(lx, ly));
            return string.IsNullOrEmpty(name) ? null : name;
        }
        return null;
    }
}
