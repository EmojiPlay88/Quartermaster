using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>
/// Alerts editor of the rules workbench: user-defined alerts (condition over units, message, severity, optional side, notes).
/// Edits go through UpdateRules; text fields commit 400 ms after the last keystroke. The "N matches now" line evaluates only the selected alert.
/// </summary>
sealed class AlertsTab : UserControl
{
    const string AnySide = "(any)";
    const string MissingText = "(missing)";

    readonly ModuleSession _s;
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    readonly Panel _right = new() { Dock = DockStyle.Fill, AutoScroll = true };
    readonly TextBox _name = new() { Dock = DockStyle.Fill };
    readonly TextBox _condition = new() { Dock = DockStyle.Fill };
    readonly Label _status = new() { AutoSize = true, Padding = new Padding(0, 3, 0, 0) };
    readonly TextBox _message = new() { Dock = DockStyle.Fill };
    readonly ComboBox _severity = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    readonly ComboBox _side = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    readonly TextBox _notes = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, WordWrap = true };
    readonly Label _issues = new() { AutoSize = true, ForeColor = Color.Firebrick };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 400 };
    readonly List<string> _sideIds = new();
    string? _selected;
    bool _selfEdit, _loading, _dirty;

    public AlertsTab(ModuleSession s)
    {
        _s = s;
        _list.Columns.Add("Alert", 150);
        _list.Columns.Add("Severity", 60);
        var bAdd = new Button { Text = "Add", Width = 50 };
        var bDel = new Button { Text = "Remove", Width = 56 };
        var bUp = new Button { Text = "Move Up", Width = 62 };
        var bDown = new Button { Text = "Move Down", Width = 74 };
        var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 62, WrapContents = true };
        btns.Controls.AddRange(new Control[] { bAdd, bDel, bUp, bDown });
        var box = new GroupBox { Text = "Alerts, in order", Dock = DockStyle.Fill, Padding = new Padding(4) };
        box.Controls.Add(_list);
        box.Controls.Add(btns);
        var left = new Panel { Dock = DockStyle.Left, Width = 250 };
        left.Controls.Add(box);

        _severity.Items.AddRange(Enum.GetNames<AlertSeverity>().Cast<object>().ToArray());

        var rows = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(4) };
        rows.MinimumSize = new Size(420, 0);
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _right.Controls.Add(rows);
        void AddRow(Control c, int height)
        {
            c.Dock = DockStyle.Top;
            c.Height = height;
            c.Margin = new Padding(0, 0, 0, 4);
            rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rows.Controls.Add(c, 0, rows.RowCount++);
        }
        rows.RowCount = 0;

        var nameRow = new TableLayoutPanel { ColumnCount = 2, RowCount = 1 };
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nameRow.Controls.Add(new Label { Text = "Name:", AutoSize = true, Padding = new Padding(0, 5, 2, 0) }, 0, 0);
        nameRow.Controls.Add(_name, 1, 0);
        AddRow(nameRow, 28);

        var condBox = new GroupBox { Text = "Condition (a unit filter, for example Fuel < 20 and Ammo < 10)", Padding = new Padding(4) };
        _status.Dock = DockStyle.Bottom;
        condBox.Controls.Add(_condition);
        condBox.Controls.Add(_status);
        AddRow(condBox, 74);

        var msgRow = new TableLayoutPanel { ColumnCount = 2, RowCount = 1 };
        msgRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        msgRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        msgRow.Controls.Add(new Label { Text = "Message ({unit} = unit name):", AutoSize = true, Padding = new Padding(0, 5, 2, 0) }, 0, 0);
        msgRow.Controls.Add(_message, 1, 0);
        AddRow(msgRow, 28);

        var pickRow = new FlowLayoutPanel { WrapContents = false };
        pickRow.Controls.Add(new Label { Text = "Severity:", AutoSize = true, Padding = new Padding(0, 5, 2, 0) });
        pickRow.Controls.Add(_severity);
        pickRow.Controls.Add(new Label { Text = "Side:", AutoSize = true, Padding = new Padding(10, 5, 2, 0) });
        pickRow.Controls.Add(_side);
        AddRow(pickRow, 30);

        var notesBox = new GroupBox { Text = "Notes (shown as a tooltip on the alert)", Padding = new Padding(4) };
        notesBox.Controls.Add(_notes);
        AddRow(notesBox, 80);

        AddRow(_issues, 40);

        Controls.Add(_right);
        Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
        Controls.Add(left);

        bAdd.Click += (_, _) => Run(AddAlert);
        bDel.Click += (_, _) => Run(RemoveAlert);
        bUp.Click += (_, _) => Run(() => Structural(p => AlertEdit.Move(p, SelectedIndex, -1)));
        bDown.Click += (_, _) => Run(() => Structural(p => AlertEdit.Move(p, SelectedIndex, 1)));
        _list.SelectedIndexChanged += (_, _) => Run(OnSelected);
        foreach (var t in new[] { _name, _condition, _message, _notes })
        {
            t.TextChanged += (_, _) => { if (!_loading) { _timer.Stop(); _timer.Start(); } };
            t.Leave += (_, _) => Run(Flush);
        }
        _condition.TextChanged += (_, _) => { if (!_loading) ShowStatus(); };
        _timer.Tick += (_, _) => Run(Flush);
        _severity.SelectionChangeCommitted += (_, _) => Run(() => { if (_severity.SelectedIndex >= 0) CommitAlert(a => a with { Severity = (AlertSeverity)_severity.SelectedIndex }); });
        _side.SelectionChangeCommitted += (_, _) => Run(OnSideChosen);

        VisibleChanged += (_, _) =>
        {
            if (!Visible) Run(Flush);
            else if (_dirty) Run(Reload);
            else Run(ShowStatus);
        };
        s.RulesChanged += OnRulesChanged;
        s.GameReplaced += OnGameReplaced;
        Reload();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _s.RulesChanged -= OnRulesChanged;
            _s.GameReplaced -= OnGameReplaced;
            if (_timer.Enabled) { try { Flush(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("alerts tab flush on close: " + ex.Message); } }
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------- plumbing ----------

    public string? SelectedAlertId => _selected;

    public void SelectAlert(string id)
    {
        foreach (ListViewItem it in _list.Items)
            if ((string?)it.Tag == id) { it.Selected = true; it.EnsureVisible(); return; }
    }

    void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("alerts tab: " + ex);
            _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Alert edit failed: " + ex.Message, "");
        }
    }

    void OnRulesChanged() => OnUi(() => { if (!_selfEdit) Reload(); });

    void OnGameReplaced() => OnUi(ShowStatus);

    void OnUi(Action a)
    {
        if (IsDisposed) return;
        if (!InvokeRequired) { Run(a); return; }
        if (!IsHandleCreated) { _dirty = true; return; }
        BeginInvoke(new Action(() => { if (!IsDisposed) Run(a); }));
    }

    AlertDef? Current => _selected == null ? null : _s.Rules?.Alerts.FirstOrDefault(a => a.Id == _selected);

    int SelectedIndex => _selected == null || _s.Rules is not { } p ? -1 : p.Alerts.FindIndex(x => x.Id == _selected);

    void CommitPack(Func<RulesPack, RulesPack> edit)
    {
        _selfEdit = true;
        try { _s.UpdateRules(edit); }
        finally { _selfEdit = false; }
    }

    void CommitAlert(Func<AlertDef, AlertDef> edit)
    {
        if (_selected is not { } id) return;
        CommitPack(p => AlertEdit.Update(p, id, edit));
        RefreshList();
        ShowStatus();
    }

    void Structural(Func<RulesPack, RulesPack> op)
    {
        Flush();
        if (_s.Rules is not { } pack) return;
        var next = op(pack);
        if (ReferenceEquals(next, pack)) return;
        CommitPack(_ => next);
        RefreshList();
    }

    // ---------- loading ----------

    void Reload()
    {
        _dirty = false;
        if (_timer.Enabled) Flush();
        var pack = _s.Rules;
        if (pack == null || pack.Alerts.All(a => a.Id != _selected)) _selected = pack?.Alerts.FirstOrDefault()?.Id;
        RefreshList();
        LoadAlert();
    }

    void RefreshList()
    {
        var prev = _loading;
        _loading = true;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var a in _s.Rules?.Alerts ?? new List<AlertDef>())
                _list.Items.Add(new ListViewItem(new[] { a.Name, a.Severity.ToString() }) { Tag = a.Id, Selected = a.Id == _selected });
        }
        finally { _list.EndUpdate(); _loading = prev; }
    }

    void LoadAlert()
    {
        var pack = _s.Rules;
        var a = Current;
        _loading = true;
        try
        {
            _right.Enabled = a != null;
            _name.Text = a?.Name ?? "";
            _condition.Text = a?.Condition ?? "";
            _message.Text = a?.Message ?? "";
            _notes.Text = a?.Notes ?? "";
            _severity.SelectedIndex = a == null ? -1 : (int)a.Severity;
            _side.Items.Clear();
            _sideIds.Clear();
            _side.Items.Add(AnySide);
            _sideIds.Add("");
            if (pack != null) foreach (var sd in pack.Sides) { _side.Items.Add(sd.Name); _sideIds.Add(sd.Id); }
            var idx = 0;
            if (a?.SideId is { } sid)
            {
                idx = _sideIds.IndexOf(sid);
                if (idx < 0) { _side.Items.Add(MissingText); _sideIds.Add(sid); idx = _sideIds.Count - 1; }
            }
            _side.SelectedIndex = a == null ? -1 : idx;
        }
        finally { _loading = false; }
        ShowStatus();
        ShowIssues();
    }

    /// <summary>The live count uses what is typed now, so it follows the text before the commit lands.</summary>
    void ShowStatus()
    {
        if (_s.Rules is not { } pack || Current is not { } a) { _status.Text = ""; return; }
        var probe = a with { Condition = _condition.Text };
        _status.Text = AlertEdit.MatchStatus(probe, _s.View, pack);   // counts only what this player may see
        _status.ForeColor = _status.Text.StartsWith("Does not parse", StringComparison.Ordinal) ? Color.Firebrick : SystemColors.ControlText;
    }

    void ShowIssues()
    {
        if (_s.Rules is not { } pack || Current is not { } a) { _issues.Text = ""; return; }
        try
        {
            var mine = RulesValidator.Validate(pack).Where(i => i.EntityId == a.Id).Select(i => i.Message).ToList();
            _issues.Text = string.Join(Environment.NewLine, mine);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("alerts validation failed: " + ex.Message);
            _issues.Text = "";
        }
    }

    // ---------- edits ----------

    void OnSelected()
    {
        if (_loading) return;
        Flush();
        var id = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as string : null;
        if (id == null || id == _selected) return;
        _selected = id;
        LoadAlert();
    }

    void Flush()
    {
        _timer.Stop();
        if (_loading || Current is not { } a) return;
        var (name, cond, msg, notes) = (_name.Text, _condition.Text, _message.Text, _notes.Text);
        if (a.Name == name && a.Condition == cond && a.Message == msg && (a.Notes ?? "") == notes) return;
        CommitAlert(x => x with { Name = name, Condition = cond, Message = msg, Notes = notes.Length == 0 ? null : notes });
        ShowIssues();
    }

    void OnSideChosen()
    {
        if (_loading || Current == null) return;
        var i = _side.SelectedIndex;
        if (i < 0 || i >= _sideIds.Count) return;
        var id = _sideIds[i];
        CommitAlert(x => x with { SideId = id.Length == 0 ? null : id });
        ShowIssues();
    }

    void AddAlert()
    {
        Flush();
        if (_s.Rules is not { } pack) return;
        var at = SelectedIndex < 0 ? pack.Alerts.Count : SelectedIndex + 1;
        var next = AlertEdit.Add(pack, at);
        _selected = next.Alerts[at].Id;
        CommitPack(_ => next);
        RefreshList();
        LoadAlert();
        _name.Focus();
        _name.SelectAll();
    }

    void RemoveAlert()
    {
        Flush();
        if (_s.Rules is not { } pack || SelectedIndex < 0) return;
        var i = SelectedIndex;
        var next = AlertEdit.Remove(pack, i);
        _selected = next.Alerts.Count == 0 ? null : next.Alerts[Math.Min(i, next.Alerts.Count - 1)].Id;
        CommitPack(_ => next);
        RefreshList();
        LoadAlert();
    }
}
