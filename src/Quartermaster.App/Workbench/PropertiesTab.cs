using Quartermaster.Vassal;
using System.Globalization;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>Properties and holders of the rules workbench. Ids are fixed at creation; formulas refer to them, so renaming never breaks a formula.</summary>
sealed class PropertiesTab : UserControl
{
    static readonly Color Pink = Color.FromArgb(255, 200, 210);

    readonly ModuleSession _s;
    readonly DataGridView _props = new(), _holders = new();
    readonly Label _status = new() { Dock = DockStyle.Bottom, AutoSize = true, ForeColor = Color.Firebrick };
    readonly List<string> _propIds = new(), _holderIds = new();
    IReadOnlyList<string>? _fields;
    bool _rebuilding, _selfEdit, _dirty;

    public PropertiesTab(ModuleSession s)
    {
        _s = s;
        _props.Columns.Add(Col("Name", 90));
        var scope = new DataGridViewComboBoxColumn { HeaderText = "Scope", FlatStyle = FlatStyle.Flat, FillWeight = 60, DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing, SortMode = DataGridViewColumnSortMode.NotSortable };
        scope.Items.AddRange(Enum.GetNames<PropertyScope>().Cast<object>().ToArray());
        _props.Columns.Add(scope);
        _props.Columns.Add(Col("Default", 50));
        _props.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "SheetField", FlatStyle = FlatStyle.Flat, FillWeight = 80, DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing, SortMode = DataGridViewColumnSortMode.NotSortable });
        _props.Columns.Add(Col("AppliesTo", 120));
        var outliner = new DataGridViewComboBoxColumn { HeaderText = "Outliner", FlatStyle = FlatStyle.Flat, FillWeight = 60, DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing, SortMode = DataGridViewColumnSortMode.NotSortable };
        outliner.Items.AddRange(Enum.GetNames<OutlinerAggregate>().Cast<object>().ToArray());
        _props.Columns.Add(outliner);
        _props.Columns.Add(Col("Notes", 160));
        _holders.Columns.Add(Col("Name", 80));
        _holders.Columns.Add(Col("Notes", 200));
        foreach (var g in new[] { _props, _holders })
        {
            g.Dock = DockStyle.Fill; g.AllowUserToAddRows = false; g.AllowUserToDeleteRows = false; g.AllowUserToResizeRows = false;
            g.BackgroundColor = SystemColors.AppWorkspace; g.RowHeadersWidth = 24; g.MultiSelect = false;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            g.DataError += (_, e) => e.ThrowException = false;
        }
        _props.CurrentCellDirtyStateChanged += (_, _) => { if (_props.IsCurrentCellDirty && _props.CurrentCell is DataGridViewComboBoxCell && _props.CurrentCell.ColumnIndex is 1 or 5) _props.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _props.EditingControlShowing += (_, e) =>
        {
            if (_props.CurrentCell?.ColumnIndex == 3 && e.Control is ComboBox cb)
            {
                cb.DropDownStyle = ComboBoxStyle.DropDown; // free text plus suggestions
                cb.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
                cb.AutoCompleteSource = AutoCompleteSource.ListItems;
            }
        };
        _props.CellValidating += (_, e) =>
        {
            if (e.ColumnIndex == 3 && e.RowIndex >= 0 && _props[3, e.RowIndex] is DataGridViewComboBoxCell c && e.FormattedValue is string t && t.Length > 0 && !c.Items.Contains(t))
                c.Items.Add(t);
        };
        _props.CellValueChanged += (_, e) => Run(() => OnPropertyCell(e.RowIndex, e.ColumnIndex));
        _holders.CellValueChanged += (_, e) => Run(() => OnHolderCell(e.RowIndex, e.ColumnIndex));
        _props.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.ColumnIndex == 4 && e.RowIndex >= 0 && FormulaEdit.SyntaxProblem(Convert.ToString(_props[4, e.RowIndex].Value)) is { } p) e.ToolTipText = p;
            else if (e.ColumnIndex == 3) e.ToolTipText = "A property-sheet row name from the module's pieces; seeds the property from that row.";
            else if (e.ColumnIndex == 5) e.ToolTipText = "How the outliner totals this property per group: None hides it; Sum, Min or Average show a column.";
        };
        _props.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex == 4 && e.RowIndex >= 0 && FormulaEdit.SyntaxProblem(Convert.ToString(e.Value)) != null) e.CellStyle!.BackColor = Pink;
        };

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(Box("Properties", _props, Bar(AddProperty, DeleteProperty, "Add property", "Delete property")));
        split.Panel2.Controls.Add(Box("Holders (the built-in \"global\" holder always exists)", _holders, Bar(AddHolder, DeleteHolder, "Add holder", "Delete holder")));
        Controls.Add(split);
        Controls.Add(_status);
        HandleCreated += (_, _) => { if (split.Height > 100) split.SplitterDistance = split.Height * 60 / 100; };

        VisibleChanged += (_, _) => { if (Visible && _dirty) Run(Rebuild); };
        s.RulesChanged += OnRulesChanged;
        s.GameReplaced += OnGameReplaced;
        Rebuild();
    }

    static DataGridViewTextBoxColumn Col(string title, float weight) =>
        new() { HeaderText = title, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable, MinimumWidth = 50 };

    static Control Box(string title, DataGridView grid, ToolStrip bar)
    {
        var g = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(4) };
        g.Controls.Add(grid);
        g.Controls.Add(bar);
        return g;
    }

    ToolStrip Bar(Action add, Action del, string addText, string delText)
    {
        var bar = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
        var a = new ToolStripButton(addText) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        var d = new ToolStripButton(delText) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        a.Click += (_, _) => Run(add);
        d.Click += (_, _) => Run(del);
        bar.Items.Add(a);
        bar.Items.Add(d);
        return bar;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _s.RulesChanged -= OnRulesChanged;
            _s.GameReplaced -= OnGameReplaced;
        }
        base.Dispose(disposing);
    }

    public void SelectProperty(string id)
    {
        var i = _propIds.IndexOf(id);
        if (i >= 0) { _props.ClearSelection(); _props.CurrentCell = _props[0, i]; }
    }

    public void SelectHolder(string id)
    {
        var i = _holderIds.IndexOf(id);
        if (i >= 0) { _holders.ClearSelection(); _holders.CurrentCell = _holders[0, i + 1]; }
    }

    void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("properties tab: " + ex);
            _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Property edit failed: " + ex.Message, "");
        }
    }

    void OnRulesChanged() => OnUi(() => { if (!_selfEdit) Rebuild(); });

    void OnGameReplaced() => OnUi(() => { _fields = null; Rebuild(); });

    void OnUi(Action a)
    {
        if (IsDisposed) return;
        if (!InvokeRequired) { Run(a); return; } // direct even before the handle exists
        if (!IsHandleCreated) { _dirty = true; return; }
        BeginInvoke(new Action(() => { if (!IsDisposed) Run(a); }));
    }

    void Commit(Func<RulesPack, RulesPack> edit)
    {
        _selfEdit = true;
        try { _s.UpdateRules(edit); }
        finally { _selfEdit = false; }
    }

    // ---------- build ----------

    void Rebuild()
    {
        _dirty = false;
        var pack = _s.Rules;
        _fields ??= SafeFields();
        _rebuilding = true;
        try
        {
            _props.Rows.Clear(); _holders.Rows.Clear(); _propIds.Clear(); _holderIds.Clear();
            _props.Enabled = _holders.Enabled = pack != null;
            if (pack == null) return;
            foreach (var p in pack.Properties)
            {
                var i = _props.Rows.Add(p.Name, p.Scope.ToString(), p.Default.ToString("R", CultureInfo.InvariantCulture), "", p.AppliesTo ?? "", p.Outliner.ToString(), p.Notes ?? "");
                _props.Rows[i].Cells[3] = SheetCell(p.SheetField ?? "");
                _propIds.Add(p.Id);
            }
            var gi = _holders.Rows.Add(HolderDef.GlobalId, "Built in; always exists.");
            _holders.Rows[gi].ReadOnly = true;
            _holders.Rows[gi].DefaultCellStyle.BackColor = SystemColors.Control;
            foreach (var h in pack.Holders)
            {
                _holders.Rows.Add(h.Name, h.Notes ?? "");
                _holderIds.Add(h.Id);
            }
        }
        finally { _rebuilding = false; }
    }

    IReadOnlyList<string> SafeFields()
    {
        try { return SheetFieldCatalog.Collect(_s.Model, _s.View); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("sheet field scan failed: " + ex);
            return Array.Empty<string>();
        }
    }

    DataGridViewComboBoxCell SheetCell(string value)
    {
        var items = new List<string> { "" };
        items.AddRange(_fields ?? Array.Empty<string>());
        if (value.Length > 0 && !items.Contains(value)) items.Add(value);
        var c = new DataGridViewComboBoxCell { FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing };
        c.Items.AddRange(items.Cast<object>().ToArray());
        c.Value = value;
        return c;
    }

    // ---------- edits ----------

    void OnPropertyCell(int row, int col)
    {
        if (_rebuilding || row < 0 || row >= _propIds.Count) return;
        var id = _propIds[row];
        var text = Convert.ToString(_props[col, row].Value) ?? "";
        _status.Text = "";
        switch (col)
        {
            case 0:
                if (NameRules.Problem(_s.Rules!.Properties.Where(x => x.Id != id).Select(x => x.Name), text, null) is { } bad)
                {
                    _status.Text = bad + " The name was not changed.";
                    BeginInvoke(new Action(() => Run(Rebuild)));
                    return;
                }
                Commit(p => Edit(p, id, x => x with { Name = text.Trim() }));
                break;
            case 1:
                if (!Enum.TryParse<PropertyScope>(text, out var sc)) return;
                Commit(p => Edit(p, id, x => x with { Scope = sc }));
                break;
            case 2:
                if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
                {
                    _status.Text = "The default must be a number (use a dot for decimals).";
                    BeginInvoke(new Action(() => Run(Rebuild)));
                    return;
                }
                Commit(p => Edit(p, id, x => x with { Default = d }));
                break;
            case 3: Commit(p => Edit(p, id, x => x with { SheetField = text.Trim().Length == 0 ? null : text.Trim() })); break;
            case 4:
                Commit(p => Edit(p, id, x => x with { AppliesTo = text.Trim().Length == 0 ? null : text }));
                if (FormulaEdit.SyntaxProblem(text) is { } problem) _status.Text = "Applies to: " + problem;
                _props.InvalidateRow(row);
                break;
            case 5:
                if (!Enum.TryParse<OutlinerAggregate>(text, out var oa)) return;
                Commit(p => Edit(p, id, x => x with { Outliner = oa }));
                break;
            case 6: Commit(p => Edit(p, id, x => x with { Notes = text.Length == 0 ? null : text })); break;
        }
    }

    static RulesPack Edit(RulesPack p, string id, Func<PropertyDef, PropertyDef> f) =>
        p with { Properties = p.Properties.Select(x => x.Id == id ? f(x) : x).ToList() };

    void OnHolderCell(int row, int col)
    {
        if (_rebuilding || row < 1 || row - 1 >= _holderIds.Count) return;
        var id = _holderIds[row - 1];
        var text = Convert.ToString(_holders[col, row].Value) ?? "";
        if (col == 0 && NameRules.Problem(_s.Rules!.Holders.Where(x => x.Id != id).Select(x => x.Name).Append(HolderDef.GlobalId), text, null) is { } bad)
        {
            _status.Text = bad + " The name was not changed.";
            BeginInvoke(new Action(() => Run(Rebuild)));
            return;
        }
        Commit(p => p with
        {
            Holders = p.Holders.Select(h => h.Id != id ? h : col == 0 ? h with { Name = text.Trim() } : h with { Notes = text.Length == 0 ? null : text }).ToList(),
        });
    }

    static string Unique(IEnumerable<string> names, string stem)
    {
        var set = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = set.Count + 1; ; n++) if (!set.Contains($"{stem} {n}")) return $"{stem} {n}";
    }

    void AddProperty()
    {
        if (_s.Rules is not { } pack) return;
        var p = new PropertyDef { Id = Guid.NewGuid().ToString("N"), Name = Unique(pack.Properties.Select(x => x.Name), "Property"), Scope = PropertyScope.Pieces };
        Commit(x => x with { Properties = x.Properties.Append(p).ToList() });
        Rebuild();
        SelectProperty(p.Id);
    }

    void AddHolder()
    {
        if (_s.Rules is not { } pack) return;
        var h = new HolderDef { Id = Guid.NewGuid().ToString("N"), Name = Unique(pack.Holders.Select(x => x.Name), "Holder") };
        Commit(x => x with { Holders = x.Holders.Append(h).ToList() });
        Rebuild();
        SelectHolder(h.Id);
    }

    void DeleteProperty()
    {
        if (_s.Rules is not { } pack || _props.CurrentCell is not { } c || c.RowIndex < 0 || c.RowIndex >= _propIds.Count) return;
        var prop = pack.ById<PropertyDef>(_propIds[c.RowIndex]);
        if (prop == null) return;
        if (!Confirm("property", prop.Name, FormulaEdit.FormulasUsingProperty(pack, prop.Id))) return;
        Commit(p => p with { Properties = p.Properties.Where(x => x.Id != prop.Id).ToList() });
        Rebuild();
    }

    void DeleteHolder()
    {
        if (_s.Rules is not { } pack || _holders.CurrentCell is not { } c || c.RowIndex < 1 || c.RowIndex - 1 >= _holderIds.Count) return;
        var holder = pack.ById<HolderDef>(_holderIds[c.RowIndex - 1]);
        if (holder == null) return;
        if (!Confirm("holder", holder.Name, FormulaEdit.FormulasUsingHolder(pack, holder.Id))) return;
        Commit(p => p with { Holders = p.Holders.Where(x => x.Id != holder.Id).ToList() });
        Rebuild();
    }

    bool Confirm(string kind, string name, IReadOnlyList<FormulaDef> users)
    {
        var text = $"Delete the {kind} \"{name}\"?";
        if (users.Count > 0)
            text += $"\n\nThese formulas use it and will report errors until you fix them:\n  " + string.Join("\n  ", users.Take(15).Select(f => f.Name)) + (users.Count > 15 ? "\n  …" : "");
        return MessageBox.Show(this, text, "Delete " + kind, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }
}
