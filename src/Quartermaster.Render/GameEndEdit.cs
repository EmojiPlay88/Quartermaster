using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>Pure edit operations behind the workbench "End of game" tab: the last turn and stage, and the victory conditions. Empty lists and blank text become null.</summary>
public static class GameEndEdit
{
    public const int MaxTurn = 999;

    private static int? Clamp(int? n) => n is { } v ? Math.Clamp(v, 1, MaxTurn) : null;

    public static RulesPack WithLastTurn(RulesPack pack, int? turn) => pack with { LastTurn = Clamp(turn) };

    public static RulesPack WithLastStage(RulesPack pack, int? stage) => pack with { LastStage = Clamp(stage) };

    /// <summary>Appends an empty condition with the first unused id <c>victory-{n}</c>.</summary>
    public static RulesPack AddVictory(RulesPack pack)
    {
        var list = (pack.Victory ?? new()).ToList();
        var n = 1;
        while (list.Any(v => v.Id == $"victory-{n}")) n++;
        list.Add(new VictoryCondition { Id = $"victory-{n}" });
        return pack with { Victory = list };
    }

    /// <summary>Applies the edit to the condition with this id; blank side/message and an empty phase list become null.</summary>
    public static RulesPack UpdateVictory(RulesPack pack, string id, Func<VictoryCondition, VictoryCondition> edit)
    {
        if (pack.Victory == null) return pack;
        return pack with { Victory = pack.Victory.Select(v => v.Id == id ? Normalise(edit(v)) : v).ToList() };
    }

    private static VictoryCondition Normalise(VictoryCondition v) => v with
    {
        SideId = string.IsNullOrWhiteSpace(v.SideId) ? null : v.SideId,
        Message = string.IsNullOrWhiteSpace(v.Message) ? null : v.Message,
        PhaseIds = v.PhaseIds?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() is { Count: > 0 } ids ? ids : null,
    };

    /// <summary>Unchanged pack (same instance) when the index is out of range; the last removal makes the list null.</summary>
    public static RulesPack RemoveVictory(RulesPack pack, int index)
    {
        if (pack.Victory == null || index < 0 || index >= pack.Victory.Count) return pack;
        var list = pack.Victory.Where((_, i) => i != index).ToList();
        return pack with { Victory = list.Count == 0 ? null : list };
    }

    /// <summary>Unchanged pack (same instance) when the move would leave the list.</summary>
    public static RulesPack MoveVictory(RulesPack pack, int index, int delta)
    {
        var to = index + delta;
        if (pack.Victory == null || index < 0 || index >= pack.Victory.Count || to < 0 || to >= pack.Victory.Count || delta == 0) return pack;
        var list = pack.Victory.ToList();
        (list[index], list[to]) = (list[to], list[index]);
        return pack with { Victory = list };
    }

    /// <summary>"true now" / "false now" for one victory condition against the open game, read as a phase condition at the game's position; the parser's
    /// message when it does not parse, "failed: ..." when it cannot be evaluated. Never throws. Informational only: the game is never ended by it.</summary>
    public static string ConditionStatus(VictoryCondition v, GameState? state, RulesPack pack, ModuleModel? model = null)
    {
        try
        {
            if (state == null) return "no game open";
            if (string.IsNullOrWhiteSpace(v.Condition)) return "no condition";
            if (FormulaEdit.SyntaxProblem(v.Condition) is { } err) return "Does not parse: " + err;
            var at = state.Position ?? new PhasePosition(1, 1, pack.Phases.FirstOrDefault()?.Id ?? "", 0);
            string? failed = null;
            var probe = pack with { Victory = new() { v with { PhaseIds = null } } };
            var met = GameEnd.Check(probe, state, at, null, model, m => failed = m) != null;
            if (failed != null) return failed;
            return met ? "true now" : "false now";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "failed: " + ex.Message; }
    }
}
