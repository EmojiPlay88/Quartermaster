using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>
/// Visibility editor of the rules workbench: which properties are private, which side owns each holder ledger and each map or
/// org display, and which formulas roll private dice. Every change commits at once through UpdateRules.
/// </summary>
sealed class VisibilityTab : UserControl
{
    const string NoOwner = "(none)";
    const string MissingText = "(missing)";

    readonly ModuleSession _s;
    readonly DataGridView _props = MakeGrid();
    readonly DataGridView _holders = MakeGrid();
    readonly DataGridView _maps = MakeGrid();
    readonly CheckedListBox _formulas = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    readonly Label _issues = new() { Dock = DockStyle.Bottom, AutoSize = false, Height = 44, ForeColor = Color.Firebrick };
    readonly List<string> _formulaIds = new();
    readonly List<string> _sideIds = new();
    bool _selfEdit, _loading, _dirty;

    public VisibilityTab(ModuleSession s)
    {
        _s = s;
        _props.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Property", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _props.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "Visibility", Width = 110, FlatStyle = FlatStyle.Standard, DataSource = new[] { nameof(Visibility.Private), nameof(Visibility.Public) } });

        foreach (var g in new[] { _holders, _maps })
        {
            g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = g == _holders ? "Holder" : "Map or org display", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            g.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "Owner side", Width = 160, FlatStyle = FlatStyle.Standard });
        }

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(4) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        table.Controls.Add(Boxed("Properties (private: only the owning side's players see the value)", _props), 0, 0);
        table.Controls.Add(Boxed("Holders (owner side; none = public)", _holders), 1, 0);
        table.Controls.Add(Boxed("Maps and org displays (owner side; none = public)", _maps), 0, 1);
        table.Controls.Add(Boxed("Formulas (tick \"Private dice\" to show rolls only to the rolling side)", _formulas), 1, 1);
        Controls.Add(table);
        Controls.Add(_issues);

        _props.CurrentCellDirtyStateChanged += (_, _) => { if (_props.IsCurrentCellDirty) _props.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _holders.CurrentCellDirtyStateChanged += (_, _) => { if (_holders.IsCurrentCellDirty) _holders.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _maps.CurrentCellDirtyStateChanged += (_, _) => { if (_maps.IsCurrentCellDirty) _maps.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _props.CellValueChanged += (_, e) => Run(() => OnPropChanged(e.RowIndex, e.ColumnIndex));
        _holders.CellValueChanged += (_, e) => Run(() => OnOwnerChanged(_holders, e.RowIndex, e.ColumnIndex, VisibilityEdit.SetHolderOwner));
        _maps.CellValueChanged += (_, e) => Run(() => OnOwnerChanged(_maps, e.RowIndex, e.ColumnIndex, VisibilityEdit.SetMapOwner));
        _formulas.ItemCheck += (_, e) => Run(() => OnFormulaCheck(e));

        VisibleChanged += (_, _) => { if (Visible && _dirty) Run(Reload); };
        s.RulesChanged += OnRulesChanged;
        Reload();
    }

    static DataGridView MakeGrid() => new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = false,
        BackgroundColor = SystemColors.Window, EditMode = DataGridViewEditMode.EditOnEnter,
    };

    static GroupBox Boxed(string title, Control c)
    {
        var g = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(4) };
        g.Controls.Add(c);
        return g;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _s.RulesChanged -= OnRulesChanged;
        base.Dispose(disposing);
    }

    // ---------- plumbing ----------

    void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("visibility tab: " + ex);
            _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Visibility edit failed: " + ex.Message, "");
        }
    }

    void OnRulesChanged()
    {
        if (IsDisposed) return;
        void Go() { if (!_selfEdit) Run(Reload); }
        if (!InvokeRequired) { Go(); return; }
        if (!IsHandleCreated) { _dirty = true; return; }
        BeginInvoke(new Action(() => { if (!IsDisposed) Go(); }));
    }

    void Commit(Func<RulesPack, RulesPack> edit)
    {
        _selfEdit = true;
        try { _s.UpdateRules(edit); }
        finally { _selfEdit = false; }
        ShowIssues();
    }

    // ---------- loading ----------

    void Reload()
    {
        _dirty = false;
        _loading = true;
        try
        {
            var pack = _s.Rules;
            _sideIds.Clear();
            var sideNames = new List<string> { NoOwner };
            if (pack != null) foreach (var sd in pack.Sides) { _sideIds.Add(sd.Id); sideNames.Add(sd.Name); }

            _props.Rows.Clear();
            foreach (var p in pack?.Properties ?? new List<PropertyDef>())
            {
                var i = _props.Rows.Add(p.Name, p.Visibility.ToString());
                _props.Rows[i].Tag = p.Id;
            }

            FillOwnerGrid(_holders, (pack?.Holders ?? new()).Select(h => (h.Id, h.Name, h.SideId)), sideNames);

            var maps = _s.Model.Maps.Select(m => (m.Id, m.IsOrgDisplay ? m.Name + " (org display)" : m.Name, pack == null ? null : VisibilityEdit.MapOwnerOf(pack, m.Id)));
            // owners naming a map the module lacks stay visible so the user can clear them
            var extra = (pack?.MapOwners ?? new()).Where(o => _s.Model.Maps.All(m => m.Id != o.MapId)).Select(o => (o.MapId, o.MapId + " (not in module)", (string?)o.SideId));
            FillOwnerGrid(_maps, maps.Concat(extra), sideNames);

            _formulas.Items.Clear();
            _formulaIds.Clear();
            foreach (var f in pack?.Formulas ?? new List<FormulaDef>())
            {
                _formulaIds.Add(f.Id);
                _formulas.Items.Add(f.Name + "  [Private dice]", f.PrivateRolls);
            }
        }
        finally { _loading = false; }
        ShowIssues();
    }

    void FillOwnerGrid(DataGridView grid, IEnumerable<(string Id, string Name, string? SideId)> rows, List<string> sideNames)
    {
        grid.Rows.Clear();
        foreach (var (id, name, side) in rows)
        {
            var names = new List<string>(sideNames);
            var shown = NoOwner;
            if (!string.IsNullOrEmpty(side))
            {
                var k = _sideIds.IndexOf(side);
                if (k >= 0) shown = sideNames[k + 1];
                else { names.Add(MissingText); shown = MissingText; }
            }
            var cell = new DataGridViewComboBoxCell { FlatStyle = FlatStyle.Standard, DataSource = names, Value = shown };
            var r = new DataGridViewRow();
            r.Cells.Add(new DataGridViewTextBoxCell { Value = name });
            r.Cells.Add(cell);
            r.Tag = id;
            grid.Rows.Add(r);
        }
    }

    void ShowIssues()
    {
        if (_s.Rules is not { } pack) { _issues.Text = ""; return; }
        try
        {
            var ids = _s.Model.Maps.Select(m => m.Id).ToList();
            var mine = RulesValidator.Validate(pack, ids).Where(i => i.Message.StartsWith("Map owner", StringComparison.Ordinal) || (i.Message.StartsWith("Holder", StringComparison.Ordinal) && i.Message.Contains("side", StringComparison.Ordinal))).Select(i => i.Message);
            _issues.Text = string.Join("  |  ", mine);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("visibility validation failed: " + ex.Message);
            _issues.Text = "";
        }
    }

    // ---------- edits ----------

    void OnPropChanged(int row, int col)
    {
        if (_loading || row < 0 || col != 1 || _props.Rows[row].Tag is not string id) return;
        if (!Enum.TryParse<Visibility>(_props.Rows[row].Cells[1].Value as string, out var v)) return;
        Commit(p => VisibilityEdit.SetPropertyVisibility(p, id, v));
    }

    void OnOwnerChanged(DataGridView grid, int row, int col, Func<RulesPack, string, string?, RulesPack> set)
    {
        if (_loading || row < 0 || col != 1 || grid.Rows[row].Tag is not string id) return;
        var shown = grid.Rows[row].Cells[1].Value as string;
        var names = (grid.Rows[row].Cells[1] as DataGridViewComboBoxCell)?.DataSource as List<string>;
        var k = shown == null || names == null ? -1 : names.IndexOf(shown);
        if (shown == MissingText) return; // leave a dangling owner until the user picks something real
        string? side = null;
        if (shown != null && shown != NoOwner && k > 0 && k - 1 < _sideIds.Count) side = _sideIds[k - 1];
        Commit(p => set(p, id, side));
    }

    void OnFormulaCheck(ItemCheckEventArgs e)
    {
        if (_loading || e.Index < 0 || e.Index >= _formulaIds.Count) return;
        var id = _formulaIds[e.Index];
        var on = e.NewValue == CheckState.Checked;
        Commit(p => VisibilityEdit.SetPrivateRolls(p, id, on));
    }
}
