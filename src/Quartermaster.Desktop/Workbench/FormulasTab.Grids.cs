using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Desktop.Workbench;

/// <summary>The bindings and outputs grids of the formulas editor.</summary>
public sealed partial class FormulasTab
{
    readonly EditGrid _bind = new();
    readonly EditGrid _out = new();
    readonly List<IReadOnlyList<TargetOption>> _targets = new();
    readonly Button _removeUnused = new() { Content = "Remove unused bindings" };
    readonly Button _addOutput = new() { Content = "Add output" }, _removeOutput = new() { Content = "Remove output" };

    static string[] KindNames => Enum.GetNames<BindingKind>();

    Control BuildBindingsBox()
    {
        _bind.SetColumns(
        [
            new EditColumn("Variable", ReadOnly: true, Weight: 60),
            new EditColumn("Kind", EditColumnKind.Combo, Weight: 70, Items: KindNames),
            new EditColumn("Target", EditColumnKind.Combo, Weight: 100),
            new EditColumn("Details", Weight: 120),
            new EditColumn("Caption", Weight: 70),
            new EditColumn("Only when", Weight: 90),
            new EditColumn("Dice count", Weight: 80),
        ]);
        _bind.CellCommitted += (r, c, text) => Run(() => OnBindingCell(r, c, text));
        _removeUnused.Click += (_, _) => Run(() => { FlushPending(final: false); Commit(FormulaEdit.RemoveUnused); RebuildBindings(); });
        var bar = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        bar.Children.Add(_removeUnused);
        return Group("Bindings (what each variable means)", _bind, bar);
    }

    static string DetailsHint(BindingKind k) => k switch
    {
        BindingKind.Input => "A range such as 0..20, or choices separated by commas (put a choice that holds a comma in \"double quotes\"); add | auto: expression for the automatic value. Add | always ask to ask the player every time, even in Fully automatic.",
        BindingKind.Roll => "Dice, such as 2d6.",
        BindingKind.Table => "row: expression | col: expression (the expressions may use other variables).",
        _ => "",
    };

    const string DiceCountHint = "Blank = the fixed number.";

    Control BuildOutputsBox()
    {
        _out.SetColumns(
        [
            new EditColumn("Target", EditColumnKind.Combo, Weight: 60, Items: Enum.GetNames<OutputTarget>()),
            new EditColumn("Holder", EditColumnKind.Combo, Weight: 80),
            new EditColumn("Property", EditColumnKind.Combo, Weight: 100),
            new EditColumn("Op", EditColumnKind.Combo, Weight: 60, Items: Enum.GetNames<OutputOp>()),
            new EditColumn("Value", Weight: 90),
            new EditColumn("Choose from", Weight: 100),
            new EditColumn("Pick by", Weight: 80),
        ]);
        _out.SetHeaderToolTip(4, OutputValueHint);
        _out.SetHeaderToolTip(5, ChooseFromHint);
        _out.SetHeaderToolTip(6, PickByHint);
        _out.CellCommitted += (r, c, text) => Run(() => OnOutputCell(r, c, text));
        _addOutput.Click += (_, _) => Run(() => { FlushPending(final: false); Commit(FormulaEdit.AddOutput); RebuildOutputs(); });
        _removeOutput.Click += (_, _) => Run(() =>
        {
            if (_out.CurrentCell is not { } c) return;
            FlushPending(final: false);
            Commit(f => FormulaEdit.RemoveOutput(f, c.Row));
            RebuildOutputs();
        });
        var bar = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2), Spacing = 4 };
        bar.Children.Add(_addOutput);
        bar.Children.Add(_removeOutput);
        return Group("Outputs (what the result changes)", _out, bar);
    }

    // ---------- bindings ----------

    void RebuildBindings()
    {
        _bind.IsEnabled = Current != null;
        _targets.Clear();
        if (Current is not { } f || _s.Rules is not { } pack) { _bind.SetRows([]); return; }
        var unused = FormulaEdit.Unused(f).Select(b => b.Variable).ToHashSet(StringComparer.Ordinal);
        var rows = new List<IReadOnlyList<string>>();
        foreach (var b in f.Bindings)
            rows.Add([unused.Contains(b.Variable) ? b.Variable + " (unused)" : b.Variable, b.Kind.ToString(), FormulaEdit.TargetDisplay(pack, b), FormulaEdit.FormatDetails(b), b.Caption ?? "", b.When ?? "", b.DiceCountExpr ?? ""]);
        _bind.SetRows(rows);   // resets per-cell items, read-only state and hints: they go on afterwards
        for (var r = 0; r < f.Bindings.Count; r++)
        {
            var b = f.Bindings[r];
            var opts = FormulaEdit.TargetOptions(pack, b.Kind);
            _targets.Add(opts);
            _bind.SetCellItems(r, 2, opts.Select(o => o.Display).ToList());
            if (opts.Count == 0 && b.Kind is not (BindingKind.Unit or BindingKind.Global or BindingKind.Holder or BindingKind.Table))
                _bind.SetCellReadOnly(r, 2, true);
            var noDetails = b.Kind is BindingKind.Unit or BindingKind.Global or BindingKind.Holder or BindingKind.Location or BindingKind.Moved;
            _bind.SetCellReadOnly(r, 3, noDetails);
            _bind.SetCellToolTip(r, 3, DetailsHint(b.Kind));
            _bind.SetCellReadOnly(r, 6, b.Kind != BindingKind.Roll);
            _bind.SetCellToolTip(r, 6, b.Kind == BindingKind.Roll ? DiceCountHint : "");
        }
    }

    void OnBindingCell(int row, int col, string text)
    {
        if (col < 1 || Current is not { } f || row >= f.Bindings.Count) return;
        var b = f.Bindings[row];
        var v = b.Variable;
        var again = false;
        switch (col)
        {
            case 1:
                if (!Enum.TryParse<BindingKind>(text, out var kind) || kind == b.Kind) return;
                Commit(x => FormulaEdit.SetKind(x, v, kind));
                again = true;
                break;
            case 2:
                var opt = row < _targets.Count ? _targets[row].FirstOrDefault(o => o.Display == text) : null;
                if (opt == null) return;
                Commit(x => FormulaEdit.SetBinding(x, v, y => FormulaEdit.ApplyTarget(y, opt)));
                break;
            case 3:
                var (nb, err) = FormulaEdit.ParseDetails(b, text);
                if (err != null) { _err.Text = err; _goto.IsVisible = false; again = true; break; }
                // A table's row/column expression may use new variables: give them binding rows (Input) so they can be set up.
                Commit(x =>
                {
                    var y = FormulaEdit.SetBinding(x, v, _ => nb);
                    return nb.Kind == BindingKind.Table ? FormulaEdit.Sync(y, FormulaEdit.UsedVariables(y), keepConfigured: true).Formula : y;
                });
                again = FormulaEdit.FormatDetails(nb) != text || nb.Kind == BindingKind.Table;
                break;
            case 4:
                Commit(x => FormulaEdit.SetCaption(x, v, text));
                break;
            case 6:
                if (b.Kind != BindingKind.Roll) return;
                again = true;
                if (TryExpression(text, "Dice count", out var count)) Commit(x => SyncAfter(FormulaEdit.SetBinding(x, v, z => z with { DiceCountExpr = count })));
                break;
            case 5:
                again = true;
                if (TryExpression(text, "Only when", out var when)) Commit(x => SyncAfter(FormulaEdit.SetBinding(x, v, z => z with { When = when })));
                break;
        }
        if (again) Dispatcher.UIThread.Post(() => Run(RebuildBindings));   // not inside the grid's own commit
    }

    /// <summary>
    /// A blank is null, never ""; a syntax error is shown in the status line (labelled with the column) and refused. The caller then rebuilds the
    /// grid, so a refused cell shows the stored value again.
    /// </summary>
    bool TryExpression(string text, string column, out string? expr)
    {
        expr = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (FormulaEdit.SyntaxProblem(expr) is not { } problem) return true;
        _err.Text = column + ": " + problem;
        _goto.IsVisible = false;
        return false;
    }

    /// <summary>An expression may read variables nothing else does: give them binding rows (Input) so they can be set up.</summary>
    static FormulaDef SyncAfter(FormulaDef y) =>
        FormulaEdit.Analyze(y.Text).Error != null ? y : FormulaEdit.Sync(y, FormulaEdit.UsedVariables(y), keepConfigured: true).Formula;

    // ---------- outputs ----------

    const string OutputValueHint = "Blank = the formula's result.";
    const string ChooseFromHint = "Properties to choose from, separated by commas (put a name that holds a comma in \"double quotes\"). The property is picked by \"Pick by\": a number (1 = the first) or a property name.";
    const string PickByHint = "An expression: a number picks the Nth property in \"Choose from\"; text picks the property with that name. Needed when \"Choose from\" is set.";

    void RebuildOutputs()
    {
        _out.IsEnabled = Current != null;
        _addOutput.IsEnabled = _removeOutput.IsEnabled = Current != null;
        _removeUnused.IsEnabled = Current != null;
        if (Current is not { } f || _s.Rules is not { } pack) { _out.SetRows([]); return; }
        var holders = pack.Holders.Select(h => h.Name).ToList();
        var props = pack.Properties.Select(p => p.Name).ToList();
        var rows = new List<IReadOnlyList<string>>();
        foreach (var o in f.Outputs)
        {
            var holder = o.Target == OutputTarget.Holder
                ? o.HolderId == null ? "" : pack.ById<HolderDef>(o.HolderId)?.Name ?? FormulaEdit.Missing
                : o.Target == OutputTarget.Global ? HolderDef.GlobalId : "";
            var prop = pack.ById<PropertyDef>(o.Property)?.Name ?? (o.Property.Length == 0 ? "" : FormulaEdit.Missing);
            var choice = FormulaEdit.FormatNameList((o.PropertyChoice ?? new()).Select(id => pack.ById<PropertyDef>(id)?.Name ?? FormulaEdit.Missing));
            rows.Add([o.Target.ToString(), holder, prop, o.Op.ToString(), o.Expr ?? "", choice, o.PropertySelect ?? ""]);
        }
        _out.SetRows(rows);
        for (var r = 0; r < f.Outputs.Count; r++)
        {
            if (f.Outputs[r].Target == OutputTarget.Holder) _out.SetCellItems(r, 1, holders);
            else _out.SetCellReadOnly(r, 1, true);
            _out.SetCellItems(r, 2, props);
            _out.SetCellToolTip(r, 4, OutputValueHint);
            _out.SetCellToolTip(r, 5, ChooseFromHint);
            _out.SetCellToolTip(r, 6, PickByHint);
        }
    }

    void OnOutputCell(int row, int col, string text)
    {
        if (Current is not { } f || row >= f.Outputs.Count || _s.Rules is not { } pack) return;
        var again = false;
        switch (col)
        {
            case 0:
                if (!Enum.TryParse<OutputTarget>(text, out var t)) return;
                Commit(x => FormulaEdit.SetOutput(x, row, o => FormulaEdit.WithOutputTarget(o, t)));
                again = true;
                break;
            case 1:
                var h = pack.HolderByName(text);
                if (h == null) return;
                Commit(x => FormulaEdit.SetOutput(x, row, o => o with { HolderId = h.Id }));
                break;
            case 2:
                var p = pack.PropertyByName(text);
                if (p == null) return;
                Commit(x => FormulaEdit.SetOutput(x, row, o => o with { Property = p.Id }));
                break;
            case 3:
                if (!Enum.TryParse<OutputOp>(text, out var op)) return;
                Commit(x => FormulaEdit.SetOutput(x, row, o => o with { Op = op }));
                break;
            case 4:
                again = true;
                if (TryExpression(text, "Value", out var value)) Commit(x => SyncAfter(FormulaEdit.SetOutputValue(x, row, value)));
                break;
            case 5:
                again = true;
                var (names, nameErr) = FormulaEdit.ParseNameList(text);
                var ids = new List<string>();
                var refused = nameErr != null;
                if (nameErr != null) _err.Text = "Choose from: " + nameErr;
                foreach (var name in names ?? new List<string>())
                {
                    var q = pack.PropertyByName(name);
                    if (q == null) { _err.Text = $"Choose from: no property named \"{name}\"."; refused = true; break; }
                    if (!ids.Contains(q.Id)) ids.Add(q.Id);
                }
                if (refused) _goto.IsVisible = false;
                else Commit(x => FormulaEdit.SetOutputChoice(x, row, ids));
                break;
            case 6:
                again = true;
                if (TryExpression(text, "Pick by", out var pick)) Commit(x => SyncAfter(FormulaEdit.SetOutputPick(x, row, pick)));
                break;
        }
        if (again) Dispatcher.UIThread.Post(() => Run(RebuildOutputs));
    }

    // test hooks
    internal EditGrid Bindings => _bind;
    internal EditGrid Outputs => _out;
    internal Button RemoveUnusedButton => _removeUnused;
    internal Button AddOutputButton => _addOutput;
    internal Button RemoveOutputButton => _removeOutput;
}
