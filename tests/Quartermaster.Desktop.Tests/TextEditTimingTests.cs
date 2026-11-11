using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;
using Xunit;

namespace Quartermaster.Desktop.Tests;

/// <summary>Avalonia raises TextBox.TextChanged through the dispatcher; text set in code must never count as typing.</summary>
public sealed class TextEditTimingTests
{
    [AvaloniaFact]
    public void OnTextSet_runs_synchronously_inside_the_setter()
    {
        var box = new TextBox();
        var w = new Window { Content = box };
        w.Show();
        var loading = false;
        var seen = new List<bool>();
        box.OnTextSet(() => seen.Add(loading));
        loading = true;
        box.Text = "abc";
        loading = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { true }, seen);
        w.Close();
    }

    [AvaloniaFact]
    public void Formula_form_apply_is_not_an_edit()
    {
        var f = new FormulaDef { Id = "f", Name = "F", Text = "W - 1" };
        var pack = new RulesPack { Formulas = new() { f } };
        var edits = new List<(string, string)>();
        var view = new FormulaFormView(FormulaLayout.Build(f, pack), (v, t) => edits.Add((v, t)), _ => { });
        var w = new Window { Content = view };
        w.Show();
        var trace = new FormulaTrace(f, Array.Empty<VariableState>(), null, "", Array.Empty<OutputPreview>(), null, Array.Empty<DiceEntry>(),
            new Dictionary<string, IReadOnlyList<int>>());
        view.Apply(new DialogView(new[] { new BoxView("W", "7", BoxBackground.Auto, "", true, null, null) }, Array.Empty<InlineRollView>(),
            "6", "7 - 1", false, "", true, trace));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(edits);
        w.Close();
    }
}
