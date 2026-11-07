using Quartermaster.Rules;

namespace Quartermaster.Core;

/// <summary>Which side owns a piece, holder or map (spec 3.2). Null = public. Never throws.</summary>
public static class Ownership
{
    /// <summary>The first side whose unit filter matches the piece (the outliner's By-side rule); null when none does.</summary>
    public static string? SideOfPiece(PieceInstance p, GameState s, RulesPack pack) => PieceSides(s, pack)(p);

    /// <summary>
    /// Compiles every side's unit filter once, for bulk use. As in the outliner and the sequencer, an empty filter matches every
    /// piece and a bad filter matches none.
    /// </summary>
    public static Func<PieceInstance, string?> PieceSides(GameState s, RulesPack pack)
    {
        var sides = (pack.Sides ?? new List<SideDef>())
            .Where(x => x != null && !string.IsNullOrEmpty(x.Id))
            .Select(x => (x.Id, Match: PieceFilter.Compile(x.UnitFilter, s, pack)))
            .ToList();
        return p =>
        {
            if (p == null) return null;
            foreach (var (id, match) in sides)
                if (match(p)) return id;
            return null;
        };
    }

    /// <summary>
    /// The holder's owning side; null for the global holder, a public holder, an unknown one, or one whose owner names a side that no
    /// longer exists (spec 8a.10: such owners are ignored at runtime).
    /// </summary>
    public static string? SideOfHolder(string holderId, RulesPack pack)
    {
        if (holderId == null || holderId == HolderDef.GlobalId) return null;
        return Existing((pack.Holders ?? new List<HolderDef>()).FirstOrDefault(h => h != null && h.Id == holderId)?.SideId, pack);
    }

    /// <summary>The map's owning side (first matching <see cref="MapOwner"/>); null when the map is public or its owner names a deleted side.</summary>
    public static string? SideOfMap(string mapId, RulesPack pack)
    {
        if (mapId == null) return null;
        return Existing((pack.MapOwners ?? new List<MapOwner>()).FirstOrDefault(m => m != null && m.MapId == mapId)?.SideId, pack);
    }

    /// <summary>The side id when it names a side of the pack; null for blank or dangling ids.</summary>
    private static string? Existing(string? side, RulesPack pack) =>
        string.IsNullOrWhiteSpace(side) || !(pack.Sides ?? new List<SideDef>()).Any(x => x != null && x.Id == side) ? null : side;
}
