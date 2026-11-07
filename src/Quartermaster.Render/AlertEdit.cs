using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>Pure edit operations and the live match counter behind the workbench Alerts tab.</summary>
public static class AlertEdit
{
    public static RulesPack Add(RulesPack pack, int at)
    {
        var list = pack.Alerts.ToList();
        list.Insert(Math.Clamp(at, 0, list.Count), new AlertDef { Id = Guid.NewGuid().ToString("N"), Name = "New alert" });
        return pack with { Alerts = list };
    }

    /// <summary>Unchanged pack (same instance) when the index is out of range.</summary>
    public static RulesPack Remove(RulesPack pack, int index)
    {
        if (index < 0 || index >= pack.Alerts.Count) return pack;
        return pack with { Alerts = pack.Alerts.Where((_, i) => i != index).ToList() };
    }

    /// <summary>Unchanged pack (same instance) when the move would leave the list.</summary>
    public static RulesPack Move(RulesPack pack, int index, int delta)
    {
        var to = index + delta;
        if (index < 0 || index >= pack.Alerts.Count || to < 0 || to >= pack.Alerts.Count || delta == 0) return pack;
        var list = pack.Alerts.ToList();
        (list[index], list[to]) = (list[to], list[index]);
        return pack with { Alerts = list };
    }

    public static RulesPack Update(RulesPack pack, string id, Func<AlertDef, AlertDef> edit) =>
        pack with { Alerts = pack.Alerts.Select(a => a.Id == id ? edit(a) : a).ToList() };

    /// <summary>"N matches now" for one alert's condition against the open game; the parser's message when it does not parse. Never throws.</summary>
    public static string MatchStatus(AlertDef alert, GameState? state, RulesPack pack)
    {
        try
        {
            if (state == null) return "no game open";
            if (string.IsNullOrWhiteSpace(alert.Condition)) return "no condition";
            if (FormulaEdit.SyntaxProblem(alert.Condition) is { } err) return "Does not parse: " + err;
            string? sideFilter = null;
            if (alert.SideId != null)
            {
                var side = pack.ById<SideDef>(alert.SideId);
                if (side == null) return "unknown side";
                sideFilter = side.UnitFilter;
            }
            var inSide = PieceFilter.Compile(sideFilter, state, pack);
            var cond = PieceFilter.Compile(alert.Condition, state, pack);
            var n = state.Pieces.Values.Count(p => inSide(p) && cond(p));
            return n == 1 ? "1 match now" : n + " matches now";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "cannot evaluate"; }
    }
}
