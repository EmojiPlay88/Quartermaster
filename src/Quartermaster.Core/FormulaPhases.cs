using Quartermaster.Rules;

namespace Quartermaster.Core;

/// <summary>
/// The phase limit on player-started formula runs (<see cref="FormulaDef.Phases"/>). Hook runs (OnStart / OnMove / OnEnd) ignore it.
/// Never throws.
/// </summary>
public static class FormulaPhases
{
    /// <summary>Start of the disabled reason; the host refusal is built from the same names (<see cref="Refusal"/>).</summary>
    public const string ReasonPrefix = "Only in: ";

    /// <summary>
    /// True when the formula has no phase list or the pack has no phases; otherwise true only while the game is in a listed phase (false
    /// before the game starts). <paramref name="reason"/> is <c>Only in: {names}</c> when not allowed (phase names in pack order, each once),
    /// else null.
    /// </summary>
    public static bool Allowed(RulesPack pack, GameState s, FormulaDef f, out string? reason)
    {
        reason = null;
        var phases = pack?.Phases ?? new List<PhaseDef>();
        // Ids the pack does not know are ignored; a list of only unknown ids means any phase.
        var ids = f?.Phases?.Where(id => !string.IsNullOrWhiteSpace(id) && phases.Any(p => p != null && p.Id == id)).ToHashSet();
        if (ids == null || ids.Count == 0 || phases.Count == 0) return true;
        if (s?.Position is { } pos && ids.Contains(pos.PhaseId)) return true;
        var names = phases.Where(p => p != null && ids.Contains(p.Id)).Select(p => p.Name).Distinct();
        reason = ReasonPrefix + string.Join(", ", names);
        return false;
    }

    /// <summary>The host's refusal, <c>{formula name} can only be run in: {phase names}</c>, from the reason <see cref="Allowed"/> gave.</summary>
    public static string Refusal(FormulaDef f, string reason) =>
        $"{f?.Name} can only be run in: " + (reason != null && reason.StartsWith(ReasonPrefix, StringComparison.Ordinal) ? reason[ReasonPrefix.Length..] : reason);
}
