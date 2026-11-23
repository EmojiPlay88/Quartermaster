using Quartermaster.Rules;

namespace Quartermaster.Core;

/// <summary>
/// The phase limit on player-started formula runs (<see cref="FormulaDef.Phases"/>). Hook runs (OnStart / OnMove / OnEnd) ignore it.
/// Never throws.
/// </summary>
public static class FormulaPhases
{
    /// <summary>
    /// True when the formula has no phase list or the pack has no phases; otherwise true only while the game is in a listed phase (false
    /// before the game starts). <paramref name="reason"/> is <c>Only in: {names}</c> when not allowed (phase names in pack order, each once),
    /// else null.
    /// </summary>
    public static bool Allowed(RulesPack pack, GameState s, FormulaDef f, out string? reason)
    {
        reason = null;
        var ids = f?.Phases?.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet();
        var phases = pack?.Phases ?? new List<PhaseDef>();
        if (ids == null || ids.Count == 0 || phases.Count == 0) return true;
        if (s?.Position is { } pos && ids.Contains(pos.PhaseId)) return true;
        var names = phases.Where(p => p != null && ids.Contains(p.Id)).Select(p => p.Name).Distinct();
        reason = "Only in: " + string.Join(", ", names);
        return false;
    }
}
