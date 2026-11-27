namespace Quartermaster.Core;

/// <summary>
/// B8 #8: translates the side ids a game holds from a rules pack's former side ids to its current ones (the map is
/// <c>SideFacts.FormerMap</c>: former id → listing side id, ordinal). Translated: every member that holds a side id in a state
/// (<see cref="GameState.Players"/> sides, <see cref="PendingRun.AskSideId"/>, <see cref="LogEntry.SideId"/>,
/// <see cref="GameOverState.Winners"/>, a viewer copy's <see cref="GameState.ViewSides"/>) and in a command (see <see cref="MappedMembers"/>),
/// so a loaded game's ownership, visibility and asking rules see the pack's current sides. Side lists are deduplicated in order once
/// translated (two former ids of one side give it once). Log text is never rewritten: it is what was shown at the time, a player's record
/// of the game, and a side id inside it is prose, not a reference; a replay regenerates only lines a command writes
/// (e.g. the <c>Players:</c> line of <see cref="SetPlayers"/>). Opaque keys that embed a side id (a roll group <c>"{side}/{k}"</c>) are
/// left alone: they only group units and are never read as a side. Ids not in the map, and every member when nothing maps, are unchanged.
/// </summary>
public static class FormerSides
{
    /// <summary>The members translated, as <c>"Type.Member"</c> (the guard test compares them with every side-id member by reflection).</summary>
    internal static IReadOnlyList<string> MappedMembers { get; } =
    [
        "GameState.ViewSides", "PlayerDef.Sides", "PendingRun.AskSideId", "LogEntry.SideId", "GameOverState.Winners",
        "RemovePiece.SideId", "LedgerDelta.SideId", "RollDice.SideId", "AddLogLine.SideId", "EndGame.Winners",
        "TickChecklist.SideId", "AcknowledgeWarning.SideId",
    ];

    /// <summary>True when <paramref name="s"/> holds a side id the map translates. The state is not changed.</summary>
    public static bool Uses(GameState s, IReadOnlyDictionary<string, string> map) => Walk(s, map, write: false);

    /// <summary>True when <paramref name="c"/> holds a side id the map translates.</summary>
    public static bool Uses(GameCommand c, IReadOnlyDictionary<string, string> map) => !ReferenceEquals(Apply(c, map), c);

    /// <summary>Translates <paramref name="s"/> in place; true when something changed.</summary>
    public static bool Apply(GameState s, IReadOnlyDictionary<string, string> map) => Walk(s, map, write: true);

    /// <summary>The command with its side ids translated: the same instance when nothing changes, else a <c>with</c> copy.</summary>
    public static GameCommand Apply(GameCommand c, IReadOnlyDictionary<string, string> map)
    {
        if (c == null || map == null || map.Count == 0) return c!;
        switch (c)
        {
            case RemovePiece r when Id(r.SideId, map) is var x && x != r.SideId: return r with { SideId = x };
            case LedgerDelta d when Id(d.SideId, map) is var x && x != d.SideId: return d with { SideId = x };
            case RollDice d when Id(d.SideId, map) is var x && x != d.SideId: return d with { SideId = x };
            case AddLogLine l when Id(l.SideId, map) is var x && x != l.SideId: return l with { SideId = x };
            case TickChecklist t when Id(t.SideId, map) is var x && x != t.SideId: return t with { SideId = x };
            case AcknowledgeWarning a when Id(a.SideId, map) is var x && x != a.SideId: return a with { SideId = x };
            case EndGame e when List(e.Winners, map, sort: false) is var w && !ReferenceEquals(w, e.Winners): return e with { Winners = w };
            case SetPlayers p when Players(p.Players, map) is { } players: return p with { Players = players };
            case QueueRuns q when Runs(q.Runs, map) is { } runs: return q with { Runs = runs };
            default: return c;
        }
    }

    static bool Walk(GameState s, IReadOnlyDictionary<string, string> map, bool write)
    {
        if (s == null || map == null || map.Count == 0) return false;
        bool changed = false;
        if (s.Players != null && Players(s.Players, map) is { } players)
        {
            changed = true;
            if (write) s.Players = players.ToList();
        }
        if (s.Pending != null && Runs(s.Pending, map) is { } runs)
        {
            changed = true;
            if (write) s.Pending = runs.ToList();
        }
        if (s.Log != null)
            for (int i = 0; i < s.Log.Count; i++)
            {
                var e = s.Log[i];
                if (e == null || Id(e.SideId, map) is not { } x || x == e.SideId) continue;
                if (!write) return true;
                s.Log[i] = e with { SideId = x };
                changed = true;
            }
        if (s.GameOver is { } over && List(over.Winners, map, sort: false) is var w && !ReferenceEquals(w, over.Winners))
        {
            changed = true;
            if (write) s.GameOver = over with { Winners = w };
        }
        if (s.ViewSides != null && List(s.ViewSides, map, sort: true) is var v && !ReferenceEquals(v, s.ViewSides))
        {
            changed = true;
            if (write) s.ViewSides = v!.ToList();
        }
        return changed;
    }

    static string? Id(string? id, IReadOnlyDictionary<string, string> map) =>
        id != null && map.TryGetValue(id, out var to) ? to : id;

    /// <summary>The list translated (deduplicated in order, or sorted ordinal), or the same instance when no entry is translated.</summary>
    static IReadOnlyList<string>? List(IReadOnlyList<string>? list, IReadOnlyDictionary<string, string> map, bool sort)
    {
        if (list == null || !list.Any(x => x != null && map.ContainsKey(x))) return list;
        var mapped = list.Select(x => Id(x, map)!).Distinct(StringComparer.Ordinal);
        return (sort ? mapped.Where(x => x != null).OrderBy(x => x, StringComparer.Ordinal) : mapped).ToList();
    }

    /// <summary>The players with their sides translated, or null when no player changes.</summary>
    static List<PlayerDef>? Players(IReadOnlyList<PlayerDef> players, IReadOnlyDictionary<string, string> map)
    {
        List<PlayerDef>? result = null;
        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i];
            if (p == null || List(p.Sides, map, sort: false) is not { } sides || ReferenceEquals(sides, p.Sides)) continue;
            result ??= players.ToList();
            result[i] = p with { Sides = sides };
        }
        return result;
    }

    /// <summary>The runs with their asked side translated, or null when no run changes.</summary>
    static List<PendingRun>? Runs(IReadOnlyList<PendingRun> runs, IReadOnlyDictionary<string, string> map)
    {
        List<PendingRun>? result = null;
        for (int i = 0; i < runs.Count; i++)
        {
            var r = runs[i];
            if (r == null || Id(r.AskSideId, map) is not { } x || x == r.AskSideId) continue;
            result ??= runs.ToList();
            result[i] = r with { AskSideId = x };
        }
        return result;
    }
}
