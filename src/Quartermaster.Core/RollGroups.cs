using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>
/// Roll groups of a pending run (B4, <see cref="FormulaDef.GroupRoll"/>): units in the same hex and of the same side share the formula's dice.
/// A group never spans sides, so no die and no "these are stacked" fact crosses sides.
/// </summary>
public static class RollGroups
{
    /// <summary>
    /// Each unit's group key, in the order of <paramref name="pieceIds"/>: "{sideId}/{k}" ("/{k}" for a piece of no side), k = 1, 2 ... per
    /// side in order of first appearance of each (map, grid location). A unit off grid, without a piece or whose piece is not in the state
    /// gets null (it rolls its own).
    /// </summary>
    public static IReadOnlyList<string?> Assign(GameState s, RulesPack pack, ModuleModel model, IReadOnlyList<string?> pieceIds)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(model);
        var sides = PieceFilter.SideMap(s, pack);
        var keys = new Dictionary<(string Side, string? Map, string Loc), string>();
        var next = new Dictionary<string, int>(StringComparer.Ordinal);
        var res = new List<string?>(pieceIds?.Count ?? 0);
        foreach (var id in pieceIds ?? Array.Empty<string?>())
        {
            if (id == null || !s.Pieces.TryGetValue(id, out var p) || p == null || PieceInfo.Location(p, model) is not { } loc)
            {
                res.Add(null);
                continue;
            }
            var side = sides.Value(p) ?? "";
            if (!keys.TryGetValue((side, p.MapId, loc), out var key))
            {
                var k = next.GetValueOrDefault(side, 1);
                next[side] = k + 1;
                keys[(side, p.MapId, loc)] = key = $"{side}/{k}";
            }
            res.Add(key);
        }
        return res;
    }

    /// <summary>The dice the group of <paramref name="pieceId"/>'s unit in pending run <paramref name="runId"/> has already drawn (key → dice),
    /// for <see cref="RunRequest.SharedRolls"/>; null when the run, the unit, its group or the group's dice are not there.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<int>>? SharedFor(GameState s, string runId, string? pieceId)
    {
        ArgumentNullException.ThrowIfNull(s);
        var run = (s.Pending ?? new()).FirstOrDefault(r => r != null && r.Id == runId);
        var unit = run?.Units?.FirstOrDefault(u => u != null && u.PieceId == pieceId);
        if (unit?.Group is not { } group || run!.GroupRolls == null) return null;
        return run.GroupRolls.TryGetValue(group, out var dice) ? dice : null;
    }
}
