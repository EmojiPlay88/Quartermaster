using Quartermaster.Rules;
using Quartermaster.Rules.Expr;

namespace Quartermaster.Core;

public enum DecisionKind { Checklist, Pending, Warning }

/// <summary>One open item on the phase panel. Index is the checklist or warning index, or -1 for pending runs.</summary>
public sealed record Decision(DecisionKind Kind, string Text, int Index, string? RunId, IReadOnlyList<string> PieceIds);

/// <summary>A phase warning with the units it currently matches.</summary>
public sealed record WarningHit(int Index, string Message, IReadOnlyList<string> PieceIds, IReadOnlyList<string> UnitTexts, bool IsDecision, bool Acknowledged);

/// <summary>What the phase panel and the Next Phase warning list show. Never throws on bad pack data.</summary>
public static class Decisions
{
    private const string Token = "{unit}";

    public static IReadOnlyList<WarningHit> Warnings(RulesPack pack, GameState s, PhaseDef phase)
    {
        var res = new List<WarningHit>();
        var warnings = phase.Warnings ?? new();
        if (warnings.Count == 0) return res;
        var units = Sequencer.UnitsFor(pack, s, phase, null, null);
        for (var i = 0; i < warnings.Count; i++)
        {
            var w = warnings[i];
            if (w == null || string.IsNullOrWhiteSpace(w.Condition)) continue;   // blank condition: no hits
            var match = PieceFilter.Compile(w.Condition, s, pack);
            var msg = w.Message ?? "";
            var ids = new List<string>();
            var texts = new List<string>();
            foreach (var p in units)
            {
                if (!match(p)) continue;
                ids.Add(p.Id);
                var name = PieceInfo.Name(p);
                texts.Add(msg.Contains(Token) ? msg.Replace(Token, name) : name);
            }
            if (ids.Count == 0) continue;
            res.Add(new WarningHit(i, msg, ids, texts, w.IsDecision, s.Acknowledged?.Contains(i) == true));
        }
        return res;
    }

    public static IReadOnlyList<Decision> Compute(RulesPack pack, GameState s)
    {
        var res = new List<Decision>();
        var phase = Sequencer.Current(pack, s);
        if (phase == null) return res;

        var items = phase.Checklist ?? new();
        for (var i = 0; i < items.Count; i++)
            if (s.Ticked?.Contains(i) != true)
                res.Add(new Decision(DecisionKind.Checklist, items[i], i, null, Array.Empty<string>()));

        foreach (var r in s.Pending ?? new())
        {
            if (r == null) continue;
            var units = (r.Units ?? Array.Empty<PendingUnit>()).Where(u => u != null).ToList();
            string text;
            if (r.NeedsInput) text = $"{r.FormulaName} — needs input";
            else if (units.Count > 1) text = $"{r.FormulaName} — {units.Count(u => u.Applied)} of {units.Count} applied";
            else if (units.Count == 1) text = $"{r.FormulaName} — {units[0].Label}";
            else text = r.FormulaName;
            var ids = units.Where(u => !u.Applied && u.PieceId != null).Select(u => u.PieceId!).ToList();
            res.Add(new Decision(DecisionKind.Pending, text, -1, r.Id, ids));
        }

        foreach (var w in Warnings(pack, s, phase))
        {
            if (!w.IsDecision || w.Acknowledged) continue;
            var n = w.PieceIds.Count;
            var head = w.Message.Replace(Token, "").Trim();
            res.Add(new Decision(DecisionKind.Warning, $"{head} — {n} {(n == 1 ? "unit" : "units")}", w.Index, null, w.PieceIds));
        }
        return res;
    }
}
