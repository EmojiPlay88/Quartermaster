using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>An Input variable that a fully automatic run could not fill. Choices come from <see cref="KeyChoices"/>.</summary>
public sealed record MissingInput(string Variable, IReadOnlyList<string>? Choices, double? Min, double? Max);

/// <summary>UI-free logic of the missing-input dialog: which Input variables are missing, and applying one set of answers to every unit of a run.</summary>
public static class MissingInputs
{
    private static IEnumerable<PendingUnit> Open(ModuleSession s, PendingRun run) =>
        (s.Game?.State.Pending.FirstOrDefault(r => r.Id == run.Id) ?? run).Units.Where(u => !u.Applied);

    /// <summary>The Input variables that stay empty for the run's unfinished units even with the pack's auto values. Never throws.</summary>
    public static IReadOnlyList<MissingInput> For(ModuleSession s, PendingRun run)
    {
        var result = new List<MissingInput>();
        try
        {
            if (s.Game is not { } game || s.Runner is not { } runner || s.Rules?.ById<FormulaDef>(run.FormulaId) is not { } f) return result;
            var none = new Dictionary<string, string>();
            var noRolls = new Dictionary<string, IReadOnlyList<int>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var u in Open(s, run))
            {
                var trace = runner.Run(game.State, new RunRequest(f.Id, u.PieceId, none, none, noRolls, u.Moved, true));
                foreach (var v in trace.Variables)
                {
                    if (v.Value != null || v.MissingReason != "Waiting for input" || !seen.Add(v.Variable)) continue;
                    var b = f.Bindings.FirstOrDefault(x => x.Variable == v.Variable);
                    if (b is not { Kind: BindingKind.Input }) continue;
                    result.Add(new MissingInput(v.Variable, KeyChoices.For(s.Rules, f, v.Variable), b.Min, b.Max));
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return result;
    }

    /// <summary>Runs every unfinished unit with the answers and applies the ones that produce a result through the controller (which resolves each unit).
    /// Units that cannot run give a reason in <c>Failed</c> and stay pending.</summary>
    public static (int Applied, IReadOnlyList<string> Failed) ApplyAll(ModuleSession s, PhaseController controller, PendingRun run, IReadOnlyDictionary<string, string> values)
    {
        var failed = new List<string>();
        var applied = 0;
        if (s.Game == null || s.Runner is not { } runner) return (0, failed);
        var noRolls = new Dictionary<string, IReadOnlyList<int>>();
        var none = new Dictionary<string, string>();
        var typed = new Dictionary<string, string>(values);
        foreach (var u in Open(s, run).ToList())
        {
            try
            {
                var trace = runner.Run(s.Game.State, new RunRequest(run.FormulaId, u.PieceId, typed, none, noRolls, u.Moved, true));
                if (trace.Error != null || trace.Result == null)
                {
                    failed.Add($"{u.Label}: {trace.Error ?? "still missing input"}");
                    continue;
                }
                controller.ApplyFormula(run.Id, u.PieceId, trace, true);
                applied++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed.Add($"{u.Label}: {ex.Message}");
            }
        }
        return (applied, failed);
    }
}
