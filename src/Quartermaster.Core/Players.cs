using Quartermaster.Rules;

namespace Quartermaster.Core;

public sealed record PlayerDef(string Id, string Name, IReadOnlyList<string> Sides);

/// <summary>Who plays which side. Empty player list = solo: one implicit player holding every side.</summary>
public static class Players
{
    public const int MaxPlayers = 6;

    /// <summary>Copy without null players, null side lists or null side entries (never throws).</summary>
    internal static List<PlayerDef> Clean(IEnumerable<PlayerDef?>? players) =>
        (players ?? Array.Empty<PlayerDef?>())
            .Where(p => p != null && p.Id != null && p.Name != null)
            .Select(p => p! with { Sides = (p.Sides ?? Array.Empty<string>()).Where(x => x != null).ToList() })
            .ToList();

    public static bool IsSolo(GameState s) => s.Players.Count <= 1;

    public static IReadOnlyList<PlayerDef> Effective(GameState s, RulesPack? pack) =>
        s.Players.Count > 0
            ? s.Players
            : new[] { new PlayerDef("solo", "Player", (pack?.Sides ?? new List<SideDef>()).Select(x => x.Id).ToList()) };

    public static PlayerDef? Holding(GameState s, RulesPack? pack, string sideId) =>
        Effective(s, pack).FirstOrDefault(p => p.Sides.Contains(sideId));

    /// <summary>Null when fine (always for fewer than 2 players).</summary>
    public static string? Validate(IReadOnlyList<PlayerDef> players, RulesPack pack)
    {
        if (players.Count < 2) return null;
        if (players.Count > MaxPlayers) return $"At most {MaxPlayers} players are allowed.";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>();
        var known = pack.Sides.Select(x => x.Id).ToHashSet();
        foreach (var p in players)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) return "Every player needs a name.";
            if (!names.Add(p.Name.Trim())) return $"Two players are named \"{p.Name.Trim()}\"; names must be unique.";
            if (!ids.Add(p.Id ?? "")) return $"Two players have the same id \"{p.Id}\".";
            if (p.Sides == null || p.Sides.Count == 0) return $"{p.Name.Trim()} holds no side.";
            foreach (var sid in p.Sides)
                if (!known.Contains(sid)) return $"{p.Name.Trim()} holds \"{sid}\", which is not a side of this rules pack.";
        }
        foreach (var side in pack.Sides)
        {
            int n = players.Count(p => p.Sides.Contains(side.Id));
            if (n == 0) return $"{side.Name} is not held by any player.";
            if (n > 1) return $"{side.Name} is held by more than one player.";
        }
        return null;
    }
}
