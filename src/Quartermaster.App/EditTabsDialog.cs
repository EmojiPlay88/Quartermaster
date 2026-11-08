using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>
/// Edits this player's personal Ledger tabs (stored on this PC per module, never in the rules pack): a list with Add / Remove / Move up / Move down
/// and, for the selected tab, its name, unit filter (parse errors shown inline), columns, "Include depots &amp; Global" and notes.
/// OK is refused while <see cref="LedgerTabsEdit.Problems"/> reports anything.
/// </summary>
sealed class EditTabsDialog : Form
{
    readonly List<UserLedgerTab> _tabs;
    readonly IReadOnlyList<PropertyDef> _props;
    readonly RulesPack? _pack;
    readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly Button _add = new() { Text = "Add", AutoSize = true };
    readonly Button _remove = new() { Text = "Remove", AutoSize = true };
    readonly Button _up = new() { Text = "Move up", AutoSize = true };
    readonly Button _down = new() { Text = "Move down", AutoSize = true };
    readonly TextBox _name = new() { Dock = DockStyle.Fill };
    readonly TextBox _filter = new() { Dock = DockStyle.Fill };
    readonly Label _filterError = new() { AutoSize = true, ForeColor = Color.Firebrick, Dock = DockStyle.Fill, MaximumSize = new Size(420, 0) };
    readonly CheckedListBox _cols = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    readonly Button _colUp = new() { Text = "Up", AutoSize = true };
    readonly Button _colDown = new() { Text = "Down", AutoSize = true };
    readonly CheckBox _holders = new() { Text = "Include depots && Global", AutoSize = true };
    readonly TextBox _notes = new() { Dock = DockStyle.Fill, Multiline = true, Height = 44, ScrollBars = ScrollBars.Vertical };
    readonly Label _problems = new() { AutoSize = true, ForeColor = Color.Firebrick, Dock = DockStyle.Fill, MaximumSize = new Size(640, 0) };
    readonly Button _ok = new() { Text = "OK", AutoSize = true };
    readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
    readonly TableLayoutPanel _editor = new() { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(6, 0, 0, 0) };
    bool _loading;

    /// <summary>The edited list once the dialog closed with OK.</summary>
    public IReadOnlyList<UserLedgerTab> Result => _tabs.ToList();

    /// <param name="addNew">True opens with a fresh tab added and selected (the "+" on the tab strip).</param>
    public EditTabsDialog(IReadOnlyList<UserLedgerTab> tabs, RulesPack? pack, int select, bool addNew)
    {
        _tabs = tabs.ToList();
        _pack = pack;
        _props = pack == null ? Array.Empty<PropertyDef>() : LedgerTable.Columns(pack);
        Text = "Edit Ledger tabs";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(720, 460);
        MinimumSize = new Size(620, 400);
        AcceptButton = _ok;
        CancelButton = _cancel;

        var left = new TableLayoutPanel { Dock = DockStyle.Left, Width = 200, RowCount = 2, ColumnCount = 1 };
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var listButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        listButtons.Controls.AddRange(new Control[] { _add, _remove, _up, _down });
        left.Controls.Add(_list, 0, 0);
        left.Controls.Add(listButtons, 0, 1);

        _editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string label, Control c, SizeType h = SizeType.AutoSize, float hv = 0)
        {
            _editor.RowStyles.Add(new RowStyle(h, hv));
            var r = _editor.RowCount++;
            if (label.Length > 0) _editor.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, 6, 3, 3) }, 0, r);
            _editor.Controls.Add(c, 1, r);
        }
        Row("Name:", _name);
        Row("Unit filter:", _filter);
        Row("", _filterError);
        Row("", new Label { Text = "Empty means all units. Example: Fuel < 5", AutoSize = true, ForeColor = SystemColors.GrayText });
        var colsPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        colsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        colsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var colButtons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        colButtons.Controls.AddRange(new Control[] { _colUp, _colDown });
        colsPanel.Controls.Add(_cols, 0, 0);
        colsPanel.Controls.Add(colButtons, 1, 0);
        Row("Columns:", colsPanel, SizeType.Percent, 100);
        Row("", new Label { Text = "None ticked means the same columns as the Units tab.", AutoSize = true, ForeColor = SystemColors.GrayText });
        Row("", _holders);
        Row("Notes:", _notes);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(6) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var okCancel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
        okCancel.Controls.AddRange(new Control[] { _ok, _cancel });
        bottom.Controls.Add(_problems, 0, 0);
        bottom.Controls.Add(okCancel, 1, 0);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        body.Controls.Add(_editor);
        body.Controls.Add(left);
        Controls.Add(body);
        Controls.Add(bottom);

        _list.SelectedIndexChanged += (_, _) => LoadSelected();
        _add.Click += (_, _) => AddTab();
        _remove.Click += (_, _) => RemoveTab();
        _up.Click += (_, _) => MoveTab(-1);
        _down.Click += (_, _) => MoveTab(1);
        _colUp.Click += (_, _) => MoveColumn(-1);
        _colDown.Click += (_, _) => MoveColumn(1);
        _name.TextChanged += (_, _) => Edit(t => t with { Name = _name.Text });
        _filter.TextChanged += (_, _) => Edit(t => t with { Filter = _filter.Text.Length == 0 ? null : _filter.Text });
        _holders.CheckedChanged += (_, _) => Edit(t => t with { IncludeHolders = _holders.Checked });
        _notes.TextChanged += (_, _) => Edit(t => t with { Notes = _notes.Text.Length == 0 ? null : _notes.Text });
        _cols.ItemCheck += (_, _) => BeginInvoke(() => Edit(t => t with { ColumnIds = CheckedIds() }));   // ItemCheck fires before the state changes
        _ok.Click += (_, _) => TryAccept();

        RefreshList();
        if (addNew) AddTab();
        else if (_tabs.Count > 0) _list.SelectedIndex = Math.Clamp(select, 0, _tabs.Count - 1);
        LoadSelected();
    }

    int Current => _list.SelectedIndex;

    void RefreshList()
    {
        _loading = true;
        var at = _list.SelectedIndex;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var t in _tabs) _list.Items.Add(string.IsNullOrWhiteSpace(t.Name) ? "(no name)" : "★ " + t.Name.Trim());
        _list.EndUpdate();
        if (_tabs.Count > 0) _list.SelectedIndex = Math.Clamp(at, 0, _tabs.Count - 1);
        _loading = false;
        UpdateProblems();
    }

    void LoadSelected()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var has = Current >= 0 && Current < _tabs.Count;
            _editor.Enabled = has;
            _remove.Enabled = has;
            _up.Enabled = has && Current > 0;
            _down.Enabled = has && Current < _tabs.Count - 1;
            var t = has ? _tabs[Current] : new UserLedgerTab("", null, Array.Empty<string>(), false, null);
            _name.Text = t.Name;
            _filter.Text = t.Filter ?? "";
            _holders.Checked = t.IncludeHolders;
            _notes.Text = t.Notes ?? "";
            _cols.BeginUpdate();
            _cols.Items.Clear();
            var ids = t.ColumnIds ?? Array.Empty<string>();
            foreach (var id in ids)
                if (_props.FirstOrDefault(p => p.Id == id) is { } p && !_cols.Items.Cast<ColumnItem>().Any(i => i.Def.Id == id)) _cols.Items.Add(new ColumnItem(p), true);
            foreach (var p in _props)
                if (!ids.Contains(p.Id)) _cols.Items.Add(new ColumnItem(p), false);
            _cols.EndUpdate();
            ShowFilterError();
        }
        finally { _loading = false; }
    }

    IReadOnlyList<string> CheckedIds()
    {
        var list = new List<string>();
        for (var i = 0; i < _cols.Items.Count; i++)
            if (_cols.GetItemChecked(i)) list.Add(((ColumnItem)_cols.Items[i]).Def.Id);
        // Columns that no longer exist in this rules pack stay stored (they come back if the property does).
        if (Current >= 0 && Current < _tabs.Count)
            list.AddRange(_tabs[Current].ColumnIds.Where(id => !_props.Any(p => p.Id == id)));
        return list;
    }

    void Edit(Func<UserLedgerTab, UserLedgerTab> edit)
    {
        if (_loading || IsDisposed || Current < 0 || Current >= _tabs.Count) return;
        _tabs[Current] = edit(_tabs[Current]);
        var label = string.IsNullOrWhiteSpace(_tabs[Current].Name) ? "(no name)" : "★ " + _tabs[Current].Name.Trim();
        if (!Equals(_list.Items[Current], label))
        {
            var at = Current;
            _loading = true;
            _list.Items[at] = label;
            if (_list.SelectedIndex != at) _list.SelectedIndex = at;
            _loading = false;
        }
        ShowFilterError();
        UpdateProblems();
    }

    void ShowFilterError()
    {
        var f = Current >= 0 && Current < _tabs.Count ? _tabs[Current].Filter : null;
        var err = LedgerTabs.FilterError(f);
        var text = err == null ? null : "Does not parse: " + err;
        if (text == null && _pack != null && LedgerTabs.FilterError(f, _pack) is { } perr) text = perr;
        _filterError.Text = text ?? "";
        _filterError.Visible = text != null;
    }

    void UpdateProblems()
    {
        var p = LedgerTabsEdit.Problems(_tabs, _pack);
        _problems.Text = string.Join(Environment.NewLine, p);
        _ok.Enabled = p.Count == 0;
    }

    void AddTab()
    {
        var n = 1;
        string name;
        do name = n == 1 ? "New tab" : "New tab " + n; while (_tabs.Any(t => string.Equals(t.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) && n++ < 1000);
        var at = Current < 0 ? _tabs.Count : Current + 1;
        _tabs.Insert(at, new UserLedgerTab(name, null, Array.Empty<string>(), false, null));
        RefreshList();
        _list.SelectedIndex = at;
        LoadSelected();
        _name.Focus();
        _name.SelectAll();
    }

    void RemoveTab()
    {
        if (Current < 0 || Current >= _tabs.Count) return;
        _tabs.RemoveAt(Current);
        RefreshList();
        LoadSelected();
    }

    void MoveTab(int delta)
    {
        var i = Current;
        var to = i + delta;
        if (i < 0 || to < 0 || to >= _tabs.Count) return;
        (_tabs[i], _tabs[to]) = (_tabs[to], _tabs[i]);
        RefreshList();
        _list.SelectedIndex = to;
        LoadSelected();
    }

    void MoveColumn(int delta)
    {
        var i = _cols.SelectedIndex;
        var to = i + delta;
        if (i < 0 || to < 0 || to >= _cols.Items.Count) return;
        _loading = true;
        var item = _cols.Items[i];
        var check = _cols.GetItemChecked(i);
        _cols.Items.RemoveAt(i);
        _cols.Items.Insert(to, item);
        _cols.SetItemChecked(to, check);
        _cols.SelectedIndex = to;
        _loading = false;
        Edit(t => t with { ColumnIds = CheckedIds() });
    }

    void TryAccept()
    {
        var p = LedgerTabsEdit.Problems(_tabs, _pack);
        if (p.Count > 0) { UpdateProblems(); return; }
        var clean = LedgerTabStore.Normalize(_tabs.ToList());
        _tabs.Clear();
        _tabs.AddRange(clean);
        DialogResult = DialogResult.OK;
        Close();
    }

    sealed record ColumnItem(PropertyDef Def)
    {
        public override string ToString() => Def.Name + (Def.Scope == PropertyScope.Holders ? "  (depots & Global)" : "");
    }
}
