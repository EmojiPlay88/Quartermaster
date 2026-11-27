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
        SideMapBuilds++;
        var sides = (pack.Sides ?? new List<SideDef>())
            .Where(x => x != null && !string.IsNullOrEmpty(x.Id))
            .Select(x => (x.Id, Match: PieceFilter.Compile(x.UnitFilter, s, pack)))
            .ToList();
        return p =>
        {
            if (p == null) return null;
            _inUnitFilter++;
            try
            {
                foreach (var (id, match) in sides)
                    if (match(p)) return id;
                return null;
            }
            finally { _inUnitFilter--; }
        };
    }

    [ThreadStatic] private static int _inUnitFilter;

    /// <summary>How many side maps (<see cref="PieceSides"/>) were built on this thread (tests read it).</summary>
    [ThreadStatic] internal static int SideMapBuilds;

    /// <summary>
    /// True while this thread evaluates a side's unit filter (including any applies-to it reads). side() refuses there: it would ask
    /// for the piece's side again and recurse.
    /// </summary>
    internal static bool InUnitFilter => _inUnitFilter > 0;

    /// <summary>
    /// The holder's fixed owning side (<see cref="HolderDef.SideId"/>); null for the global holder, a public holder, an unknown one, or one
    /// whose owner names a side that no longer exists (spec 8a.10: such owners are ignored at runtime). This is the fixed owner; <c>src</c>
    /// callers pass a state (<see cref="SideOfHolder(string, RulesPack, GameState?)"/>), so an owner expression is followed.
    /// </summary>
    public static string? SideOfHolder(string holderId, RulesPack pack)
    {
        if (holderId == null || holderId == HolderDef.GlobalId) return null;
        return Existing(HolderById(pack, holderId)?.SideId, pack);
    }

    /// <summary>
    /// The holder's owner in <paramref name="s"/> (B9 #13a, <see cref="HolderOwners.Owner"/>): its owner expression when it has one, else
    /// (and when <paramref name="s"/> is null) the fixed owner as <see cref="SideOfHolder(string, RulesPack)"/>. Null = no owner. The one
    /// owner question every rule asks.
    /// </summary>
    public static string? SideOfHolder(string holderId, RulesPack pack, GameState? s)
    {
        if (holderId == null || holderId == HolderDef.GlobalId || pack == null) return null;
        return HolderById(pack, holderId) is { } h ? HolderOwners.Owner(s, pack, h) : null;
    }

    /// <summary>
    /// <see cref="SideOfHolder(string, RulesPack, GameState?)"/> for bulk use on <paramref name="s"/> as it stands: each holder's owner is
    /// worked out once (memo per holder). Without an owner expression in the pack it is the fixed lookup (nothing is evaluated). Make a new
    /// one after the state changes.
    /// </summary>
    public static Func<string, string?> HolderSides(GameState s, RulesPack pack)
    {
        if (!HolderOwners.HasDynamic(pack)) return id => SideOfHolder(id, pack);
        var memo = new Dictionary<string, string?>(StringComparer.Ordinal);
        return id =>
        {
            if (id == null) return null;
            if (!memo.TryGetValue(id, out var side)) memo[id] = side = SideOfHolder(id, pack, s);
            return side;
        };
    }

    private static HolderDef? HolderById(RulesPack pack, string holderId)
    {
        foreach (var h in pack.Holders ?? new List<HolderDef>())
            if (h != null && h.Id == holderId) return h;
        return null;
    }

    /// <summary>The map's owning side (first matching <see cref="MapOwner"/>); null when the map is public or its owner names a deleted side.</summary>
    public static string? SideOfMap(string mapId, RulesPack pack)
    {
        if (mapId == null) return null;
        return Existing((pack.MapOwners ?? new List<MapOwner>()).FirstOrDefault(m => m != null && m.MapId == mapId)?.SideId, pack);
    }

    /// <summary>The side id when it names a side of the pack; null for blank or dangling ids.</summary>
    internal static string? Existing(string? side, RulesPack pack) =>
        string.IsNullOrWhiteSpace(side) || !(pack.Sides ?? new List<SideDef>()).Any(x => x != null && x.Id == side) ? null : side;
}
