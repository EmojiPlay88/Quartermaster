using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>One line of the cheat sheet shown beside a manual-mode formula: where the value comes from and what it is (or how to get it).</summary>
public sealed record CheatRow(string Variable, string Source, string ValueText, TableDef? Table, int? HighlightRow, int? HighlightCol);

/// <summary>UI-free model of the manual-mode cheat sheet, built from the expected (engine-only) run.</summary>
public static class CheatSheetModel
{
    /// <param name="hidden">Inputs the viewer may not see (<see cref="FormulaDialogModel.Hidden"/>): left out entirely, not even a row.</param>
    public static IReadOnlyList<CheatRow> Build(FormulaTrace expected, RulesPack pack, IReadOnlySet<string>? hidden = null)
    {
        var rows = new List<CheatRow>();
        foreach (var v in expected.Variables)
        {
            if (hidden != null && hidden.Contains(v.Variable)) continue;
            var b = expected.Formula.Bindings.FirstOrDefault(x => x.Variable == v.Variable);
            var source = v.Source ?? "";
            if (b?.Kind == BindingKind.Roll)
            {
                rows.Add(new(v.Variable, source, $"{b.DiceCount ?? 1}d{b.DiceSides ?? 6} — roll it", null, null, null));
                continue;
            }
            if (b?.Kind == BindingKind.Input && v.Source == null)
            {
                rows.Add(new(v.Variable, source, v.Value != null ? "typed by you" : v.MissingReason ?? "", null, null, null));
                continue;
            }
            var text = v.Value?.Display() ?? v.MissingReason ?? "";
            TableDef? table = null;
            int? hr = null, hc = null;
            if (v.Cell is { } c && pack.ById<TableDef>(c.TableId) is { } t)
            {
                table = t;
                hr = c.Row;
                hc = c.Col;
            }
            rows.Add(new(v.Variable, source, text, table, hr, hc));
        }
        return rows;
    }
}
