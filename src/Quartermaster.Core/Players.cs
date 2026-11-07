using Quartermaster.Rules;

namespace Quartermaster.Core;

public sealed record PlayerDef(string Id, string Name, IReadOnlyList<string> Sides);

/// <summary>Who plays which side. Empty player list = solo: one implicit player holding every side.</summary>
public static class Players
{
    public const int MaxPlayers = 6;

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
        foreach (var p in players)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) return "Every player needs a name.";
            if (!names.Add(p.Name.Trim())) return $"Two players are named \"{p.Name.Trim()}\"; names must be unique.";
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
