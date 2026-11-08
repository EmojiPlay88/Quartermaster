using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>The bindings and outputs grids of the formulas editor.</summary>
sealed partial class FormulasTab
{
    readonly DataGridView _bind = NewGrid();
    readonly DataGridView _out = NewGrid();
    readonly List<IReadOnlyList<TargetOption>> _targets = new();
    bool _rebuilding;

    internal static DataGridView NewGrid() => new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        EditMode = DataGridViewEditMode.EditOnEnter, BackgroundColor = SystemColors.AppWorkspace, RowHeadersWidth = 24,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = false,
    };

    internal static DataGridViewTextBoxColumn Col(string title, bool ro = false, float weight = 100) =>
        new() { HeaderText = title, ReadOnly = ro, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable, MinimumWidth = 50 };

    internal static DataGridViewComboBoxCell ComboCell(IEnumerable<string> items, string value)
    {
        var list = items.ToList();
        if (value.Length > 0 && !list.Contains(value)) list.Add(value);
        var c = new DataGridViewComboBoxCell { FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing };
        c.Items.AddRange(list.Cast<object>().ToArray());
        c.Value = value;
        return c;
    }

    Control BuildBindingsBox()
    {
        _bind.Columns.AddRange(Col("Variable", ro: true, weight: 60), Col("Kind", weight: 70), Col("Target", weight: 100), Col("Details", weight: 120), Col("Caption", weight: 70));
        _bind.CurrentCellDirtyStateChanged += (_, _) => { if (_bind.IsCurrentCellDirty && _bind.CurrentCell is DataGridViewComboBoxCell) _bind.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _bind.CellValueChanged += (_, e) => Run(() => OnBindingCell(e.RowIndex, e.ColumnIndex));
        _bind.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.ColumnIndex == 3 && e.RowIndex >= 0 && _s.Rules != null && Current is { } f && e.RowIndex < f.Bindings.Count)
                e.ToolTipText = DetailsHint(f.Bindings[e.RowIndex].Kind);
        };
        _bind.DataError += (_, e) => e.ThrowException = false;
        var bar = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
        var clean = new ToolStripButton("Remove unused bindings") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        bar.Items.Add(clean);
        clean.Click += (_, _) => Run(() => { Commit(FormulaEdit.RemoveUnused); RebuildBindings(); });
        var g = new GroupBox { Text = "Bindings (what each variable means)", Dock = DockStyle.Fill, Padding = new Padding(4) };
        g.Controls.Add(_bind);
        g.Controls.Add(bar);
        return g;
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
        _out.Columns.AddRange(Col("Target", weight: 60), Col("Holder", weight: 80), Col("Property", weight: 100), Col("Op", weight: 60));
        _out.CurrentCellDirtyStateChanged += (_, _) => { if (_out.IsCurrentCellDirty) _out.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _out.CellValueChanged += (_, e) => Run(() => OnOutputCell(e.RowIndex, e.ColumnIndex));
        _out.DataError += (_, e) => e.ThrowException = false;
        var bar = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
        var add = new ToolStripButton("Add output") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        var del = new ToolStripButton("Remove output") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        bar.Items.Add(add);
        bar.Items.Add(del);
        add.Click += (_, _) => Run(() => { Commit(FormulaEdit.AddOutput); RebuildOutputs(); });
        del.Click += (_, _) => Run(() => { if (_out.CurrentCell is { } c) { Commit(f => FormulaEdit.RemoveOutput(f, c.RowIndex)); RebuildOutputs(); } });
        var g = new GroupBox { Text = "Outputs (what the result changes)", Dock = DockStyle.Fill, Padding = new Padding(4) };
        g.Controls.Add(_out);
        g.Controls.Add(bar);
        return g;
    }

    // ---------- bindings ----------

    void RebuildBindings()
    {
        _rebuilding = true;
        try
        {
            _bind.SuspendLayout();
            _bind.Rows.Clear();
            _targets.Clear();
            _bind.Enabled = Current != null;
            if (Current is not { } f || _s.Rules is not { } pack) return;
            var unused = FormulaEdit.Unused(f).Select(b => b.Variable).ToHashSet(StringComparer.Ordinal);
            foreach (var b in f.Bindings)
            {
                var i = _bind.Rows.Add(unused.Contains(b.Variable) ? b.Variable + " (unused)" : b.Variable, "", "", FormulaEdit.FormatDetails(b), b.Caption ?? "");
                var row = _bind.Rows[i];
                row.Cells[1] = ComboCell(Enum.GetNames<BindingKind>(), b.Kind.ToString());
                var opts = FormulaEdit.TargetOptions(pack, b.Kind);
                _targets.Add(opts);
                if (opts.Count == 0 && b.Kind is not (BindingKind.Unit or BindingKind.Global or BindingKind.Holder or BindingKind.Table))
                {
                    row.Cells[2].ReadOnly = true;
                    row.Cells[2].Style.BackColor = SystemColors.Control;
                }
                else row.Cells[2] = ComboCell(opts.Select(o => o.Display), FormulaEdit.TargetDisplay(pack, b));
                var noDetails = b.Kind is BindingKind.Unit or BindingKind.Global or BindingKind.Holder or BindingKind.Location or BindingKind.Moved;
                row.Cells[3].ReadOnly = noDetails;
                if (noDetails) row.Cells[3].Style.BackColor = SystemColors.Control;
            }
        }
        finally
        {
            _bind.ResumeLayout();
            _rebuilding = false;
        }
    }

    void OnBindingCell(int row, int col)
    {
        if (_rebuilding || row < 0 || col < 1 || Current is not { } f || row >= f.Bindings.Count) return;
        var b = f.Bindings[row];
        var v = b.Variable;
        var text = Convert.ToString(_bind[col, row].Value) ?? "";
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
                if (err != null) { _err.Text = err; _goto.Visible = false; again = true; break; }
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
        if (again) BeginInvoke(new Action(() => Run(RebuildBindings))); // not inside the grid's own change event
    }

    // ---------- outputs ----------

    void RebuildOutputs()
    {
        _rebuilding = true;
        try
        {
            _out.SuspendLayout();
            _out.Rows.Clear();
            _out.Enabled = Current != null;
            if (Current is not { } f || _s.Rules is not { } pack) return;
            var holders = pack.Holders.Select(h => h.Name).ToList();
            foreach (var o in f.Outputs)
            {
                var i = _out.Rows.Add("", "", "", "");
                var r = _out.Rows[i];
                r.Cells[0] = ComboCell(Enum.GetNames<OutputTarget>(), o.Target.ToString());
                if (o.Target == OutputTarget.Holder)
                {
                    var hn = o.HolderId == null ? "" : pack.ById<HolderDef>(o.HolderId)?.Name ?? FormulaEdit.Missing;
                    r.Cells[1] = ComboCell(holders, hn);
                }
                else
                {
                    r.Cells[1].Value = o.Target == OutputTarget.Global ? HolderDef.GlobalId : "";
                    r.Cells[1].ReadOnly = true;
                    r.Cells[1].Style.BackColor = SystemColors.Control;
                }
                r.Cells[2] = ComboCell(pack.Properties.Select(p => p.Name), pack.ById<PropertyDef>(o.Property)?.Name ?? (o.Property.Length == 0 ? "" : FormulaEdit.Missing));
                r.Cells[3] = ComboCell(Enum.GetNames<OutputOp>(), o.Op.ToString());
            }
        }
        finally
        {
            _out.ResumeLayout();
            _rebuilding = false;
        }
    }

    void OnOutputCell(int row, int col)
    {
        if (_rebuilding || row < 0 || Current is not { } f || row >= f.Outputs.Count || _s.Rules is not { } pack) return;
        var text = Convert.ToString(_out[col, row].Value) ?? "";
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
        if (again) BeginInvoke(new Action(() => Run(RebuildOutputs)));
    }
}
