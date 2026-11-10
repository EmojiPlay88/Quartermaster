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
        ]);
        _bind.CellCommitted += (r, c, text) => Run(() => OnBindingCell(r, c, text));
        _removeUnused.Click += (_, _) => Run(() => { FlushPending(final: false); Commit(FormulaEdit.RemoveUnused); RebuildBindings(); });
        var bar = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        bar.Children.Add(_removeUnused);
        return Group("Bindings (what each variable means)", _bind, bar);
    }

    static string DetailsHint(BindingKind k) => k switch
    {
        BindingKind.Input => "A range such as 0..20, or a comma-separated list of choices.",
        BindingKind.Roll => "Dice, such as 2d6.",
        BindingKind.Table => "row: expression | col: expression (the expressions may use other variables).",
        _ => "",
    };

    Control BuildOutputsBox()
    {
        _out.SetColumns(
        [
            new EditColumn("Target", EditColumnKind.Combo, Weight: 60, Items: Enum.GetNames<OutputTarget>()),
            new EditColumn("Holder", EditColumnKind.Combo, Weight: 80),
            new EditColumn("Property", EditColumnKind.Combo, Weight: 100),
            new EditColumn("Op", EditColumnKind.Combo, Weight: 60, Items: Enum.GetNames<OutputOp>()),
        ]);
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
            rows.Add([unused.Contains(b.Variable) ? b.Variable + " (unused)" : b.Variable, b.Kind.ToString(), FormulaEdit.TargetDisplay(pack, b), FormulaEdit.FormatDetails(b), b.Caption ?? ""]);
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
        }
        if (again) Dispatcher.UIThread.Post(() => Run(RebuildBindings));   // not inside the grid's own commit
    }

    // ---------- outputs ----------

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
            rows.Add([o.Target.ToString(), holder, prop, o.Op.ToString()]);
        }
        _out.SetRows(rows);
        for (var r = 0; r < f.Outputs.Count; r++)
        {
            if (f.Outputs[r].Target == OutputTarget.Holder) _out.SetCellItems(r, 1, holders);
            else _out.SetCellReadOnly(r, 1, true);
            _out.SetCellItems(r, 2, props);
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
