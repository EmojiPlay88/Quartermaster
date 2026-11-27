using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>An Input variable that a fully automatic run could not fill. Choices come from <see cref="KeyChoices"/>.</summary>
public sealed record MissingInput(string Variable, IReadOnlyList<string>? Choices, double? Min, double? Max);

/// <summary>UI-free logic of the missing-input dialog: which Input variables are missing, and applying one set of answers to every unit of a run.</summary>
public static class MissingInputs
{
    private static IEnumerable<PendingUnit> Open(ModuleSession s, PendingRun run) =>
        (s.Game?.State.Pending.FirstOrDefault(r => r.Id == run.Id) ?? run).Units.Where(u => !u.Applied);

    /// <summary>
    /// B7: the run the current player is shown for one unit: with the chooser's sides (<see cref="FormulaMasking.PickSides"/>), so a choices
    /// or piece filter is judged on the player's view; and, when the formula's precondition or a typed box's "Only when" reads data hidden
    /// from the player (<see cref="FormulaMasking.GuardsOnHidden"/>), ignoring that precondition and resolving those boxes as if their
    /// guard held, as the formula dialog's shown run does. What the dialog lists never depends on the hidden data.
    /// </summary>
    private static RunRequest Shown(ModuleSession s, FormulaDef f, PendingUnit u, IReadOnlyDictionary<string, string> typed)
    {
        var req = Real(s, f, u, typed);
        var hidden = FormulaMasking.HiddenFor(s, f, u.PieceId);
        if (hidden.Count == 0) return req;
        var guards = FormulaMasking.GuardsOnHidden(f, hidden);
        if (guards.Count == 0) return req;
        HashSet<string>? force = null;
        foreach (var b in f.Bindings)
            if (b is { Kind: BindingKind.Input or BindingKind.Piece, Variable: { } v } && !hidden.Contains(v) && guards.Contains(v))
                (force ??= new HashSet<string>(StringComparer.Ordinal)).Add(v);
        return req with { IgnorePrecondition = guards.Contains(FormulaMasking.PreconditionGuard), ForceWhen = force };
    }

    /// <summary>The run that is applied for one unit: the full state's values, with the chooser's sides (B7).</summary>
    private static RunRequest Real(ModuleSession s, FormulaDef f, PendingUnit u, IReadOnlyDictionary<string, string> typed) =>
        new(f.Id, u.PieceId, typed, NoText, NoRolls, u.Moved, true, null, FormulaMasking.PickSides(s));

    private static readonly IReadOnlyDictionary<string, string> NoText = new Dictionary<string, string>();
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<int>> NoRolls = new Dictionary<string, IReadOnlyList<int>>();

    /// <summary>
    /// True when a unit of the run the current player may act for has a used Piece binding with no value: a pick is made per unit in the
    /// formula dialog, never asked once for the whole run nor filled automatically. Never throws (false on an error).
    /// </summary>
    public static bool NeedsPick(ModuleSession s, PendingRun run)
    {
        try
        {
            if (s.Game is not { } game || s.Runner is not { } runner || s.Rules?.ById<FormulaDef>(run.FormulaId) is not { } f) return false;
            if (!f.Bindings.Any(b => b is { Kind: BindingKind.Piece })) return false;
            foreach (var u in Open(s, run).Where(u => s.CanAct(u.PieceId)))
            {
                var trace = runner.Run(game.State, Shown(s, f, u, NoText));
                if (trace.Variables.Any(v => v.Value == null && !v.Skipped && f.Bindings.FirstOrDefault(x => x.Variable == v.Variable) is { Kind: BindingKind.Piece }))
                    return true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return false;
    }

    /// <summary>The Input variables that stay empty for the run's unfinished units even with the pack's auto values. Never throws.</summary>
    public static IReadOnlyList<MissingInput> For(ModuleSession s, PendingRun run)
    {
        var result = new List<MissingInput>();
        try
        {
            if (s.Game is not { } game || s.Runner is not { } runner || s.Rules?.ById<FormulaDef>(run.FormulaId) is not { } f) return result;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var u in Open(s, run).Where(u => s.CanAct(u.PieceId)))
            {
                var trace = runner.Run(game.State, Shown(s, f, u, NoText));
                foreach (var v in trace.Variables)
                {
                    if (v.Value != null || v.MissingReason != "Waiting for input" || !seen.Add(v.Variable)) continue;
                    var b = f.Bindings.FirstOrDefault(x => x.Variable == v.Variable);
                    if (b is not { Kind: BindingKind.Input }) continue;
                    // One answer for every unit: a choices table offers all its keys (each unit's run checks its own), as the player's view has them.
                    result.Add(new MissingInput(v.Variable, InputChoices.For(s.View ?? game.State, s.Rules, s.Model, f, v.Variable, null), b.Min, b.Max));
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            s.Log.Add(DiagnosticSeverity.Error, "formula", $"Could not work out which inputs are missing for \"{run.FormulaName}\": {ex.Message}");
        }
        return result;
    }

    /// <summary>Runs every unfinished unit with the answers and applies the ones that produce a result through the controller (which resolves each unit).
    /// Units that cannot run give a reason in <c>Failed</c> and stay pending.</summary>
    public static (int Applied, IReadOnlyList<string> Failed) ApplyAll(ModuleSession s, PhaseController controller, PendingRun run, IReadOnlyDictionary<string, string> values)
    {
        var failed = new List<string>();
        var applied = 0;
        if (s.Game == null || s.Runner is not { } runner) return (0, failed);
        var f = s.Rules?.ById<FormulaDef>(run.FormulaId);
        var typed = new Dictionary<string, string>(values);
        var units = Open(s, run).ToList();
        using var action = s.Game.BeginAction(BatchModel.ActionLabel(run.FormulaName, units.Count(u => s.CanAct(u.PieceId))));
        foreach (var u in units)
        {
            if (!s.CanAct(u.PieceId))
            {
                failed.Add($"{(s.CanSee(u.PieceId!) ? u.Label : "A unit")}: {ModuleSession.OtherSideText}");
                continue;
            }
            try
            {
                // The group's dice are read again for each unit: an earlier unit of its group may just have drawn them (B4).
                var request = f != null
                    ? Real(s, f, u, typed)
                    : new RunRequest(run.FormulaId, u.PieceId, typed, NoText, NoRolls, u.Moved, true, null, FormulaMasking.PickSides(s));
                var trace = runner.Run(s.Game.State, request with { SharedRolls = RollGroups.SharedFor(s.Game.State, run.Id, u.PieceId) });
                if (trace.Error != null || trace.Result == null)
                {
                    // B7: for a formula reading data hidden from the player, the reason (a refused precondition's message, a missing value
                    // the hidden data decided) is never shown: only the generic text.
                    var masked = f != null && FormulaMasking.HiddenFor(s, f, u.PieceId).Count > 0;
                    failed.Add($"{u.Label}: {(masked ? FormulaMasking.ErrorHiddenText : trace.Error ?? "still missing input")}");
                    continue;
                }
                if (!controller.ApplyFormula(run.Id, u.PieceId, trace, false))
                {
                    failed.Add($"{u.Label}: no longer waiting");
                    continue;
                }
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
