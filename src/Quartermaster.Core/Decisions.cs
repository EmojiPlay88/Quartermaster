using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Vassal;

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

    /// <summary>Separators that may follow a leading {unit} ("{unit}: set up" reads "set up").</summary>
    private const string Separators = ":,-—";

    /// <summary>
    /// A game-level message with {unit} removed. Only when the message starts with {unit} is the separator run right after it dropped, and
    /// only when that run stands alone (followed by whitespace or the end), so a sign stays: "{unit} -1 MP" reads "-1 MP", and
    /// "-1 MP to all units" is unchanged.
    /// </summary>
    internal static string WithoutUnit(string? message)
    {
        var m = message ?? "";
        var lead = m.TrimStart();
        if (!lead.StartsWith(Token, StringComparison.Ordinal)) return m.Replace(Token, "").Trim();
        var rest = lead[Token.Length..];
        var i = 0;
        while (i < rest.Length && char.IsWhiteSpace(rest[i])) i++;
        var j = i;
        while (j < rest.Length && Separators.Contains(rest[j])) j++;
        if (j > i && (j == rest.Length || char.IsWhiteSpace(rest[j]))) rest = rest[j..];
        return rest.Replace(Token, "").Trim();
    }

    /// <param name="model">For grid locations, so a condition can use the hex aggregates (hexsum() ...); without it they are not available.</param>
    public static IReadOnlyList<WarningHit> Warnings(RulesPack pack, GameState s, PhaseDef phase, ModuleModel? model = null)
    {
        var res = new List<WarningHit>();
        var warnings = phase.Warnings ?? new();
        if (warnings.Count == 0) return res;
        var units = Sequencer.UnitsFor(pack, s, phase, null, null, model);
        for (var i = 0; i < warnings.Count; i++)
        {
            var w = warnings[i];
            if (w == null || string.IsNullOrWhiteSpace(w.Condition)) continue;   // blank condition: no hits
            var msg = w.Message ?? "";
            if (PieceFilter.ParseGameLevel(w.Condition, pack) is { } game)
            {
                // Reads nothing about a piece: true or false once, never once per unit.
                if (PieceFilter.EvaluateGameLevel(game, s, pack, model))
                    res.Add(new WarningHit(i, msg, Array.Empty<string>(), new[] { WithoutUnit(msg) is { Length: > 0 } txt ? txt : w.Condition!.Trim() }, w.IsDecision, s.Acknowledged?.Contains(i) == true));
                continue;
            }
            var match = PieceFilter.Compile(w.Condition, s, pack, model);
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

    /// <summary>
    /// Whether a player view may see a phase's checklist: always in a full state; in a player view (<see cref="GameState.ViewSides"/>) only a
    /// phase with no side or one of the view's sides (as <see cref="PlayerView"/> treats a phase's side). A linked item of a hidden phase keeps
    /// its token, so its text never reaches that player.
    /// </summary>
    public static bool ChecklistShown(PhaseDef phase, GameState s) =>
        (s.ViewSides == null && !s.IsPlayerView) || string.IsNullOrWhiteSpace(phase.SideId) || s.ViewSides?.Contains(phase.SideId) == true;

    private static string AskedSideName(RulesPack pack, string sideId) =>
        pack.Sides?.FirstOrDefault(x => x != null && x.Id == sideId) is { } side && !string.IsNullOrWhiteSpace(side.Name) ? side.Name : sideId;

    public static IReadOnlyList<Decision> Compute(RulesPack pack, GameState s, ModuleModel? model = null)
    {
        var res = new List<Decision>();
        var phase = Sequencer.Current(pack, s);
        if (phase == null) return res;

        var items = phase.Checklist ?? new();
        for (var i = 0; i < items.Count; i++)
            if (!ChecklistState.IsTicked(s, items[i], i))
                res.Add(new Decision(DecisionKind.Checklist, ChecklistText.Render(pack, items[i]?.Text ?? "", ph => ChecklistShown(ph, s)), i, null,
                    Array.Empty<string>()));

        foreach (var r in s.Pending ?? new())
        {
            if (r == null) continue;
            var units = (r.Units ?? Array.Empty<PendingUnit>()).Where(u => u != null).ToList();
            string text;
            if (!string.IsNullOrEmpty(r.AskSideId)) text = $"{r.FormulaName} — {AskedSideName(pack, r.AskSideId)} answers";
            else if (r.NeedsInput) text = $"{r.FormulaName} — needs input";
            else if (units.Count > 1) text = $"{r.FormulaName} — {units.Count(u => u.Applied)} of {units.Count} applied";
            else if (units.Count == 1) text = $"{r.FormulaName} — {units[0].Label}";
            else text = r.FormulaName;
            var ids = units.Where(u => !u.Applied && u.PieceId != null).Select(u => u.PieceId!).ToList();
            res.Add(new Decision(DecisionKind.Pending, text, -1, r.Id, ids));
        }

        foreach (var w in Warnings(pack, s, phase, model))
        {
            if (!w.IsDecision || w.Acknowledged) continue;
            var n = w.PieceIds.Count;
            // Per unit: as before this branch. Game-level (no units): the message reads without {unit}.
            var head = n == 0 ? WithoutUnit(w.Message) : w.Message.Replace(Token, "").Trim();
            if (n == 0 && head.Length == 0) head = w.UnitTexts.FirstOrDefault() ?? "";
            res.Add(new Decision(DecisionKind.Warning, n == 0 ? head : $"{head} — {n} {(n == 1 ? "unit" : "units")}", w.Index, null, w.PieceIds));
        }
        return res;
    }
}
