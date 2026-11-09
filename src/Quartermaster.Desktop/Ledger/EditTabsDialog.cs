using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Desktop.Ledgers;

/// <summary>
/// "Edit Ledger tabs" (WinForms EditTabsDialog): this player's personal Ledger tabs (stored on this computer per module, never in the rules
/// pack): a list with Add / Remove / Move up / Move down and, for the selected tab, its name, unit filter (parse errors shown under it),
/// columns (tick and order them with Up / Down), "Include depots &amp; Global" and notes. OK is refused while <see cref="LedgerTabsEdit.Problems"/>
/// reports anything; it closes with the normalised list (<see cref="LedgerTabStore.Normalize"/>), Cancel with null.
/// </summary>
sealed class EditTabsDialog : Window
{
    static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xB2, 0x22, 0x22));   // Firebrick

    readonly List<UserLedgerTab> _tabs;
    readonly IReadOnlyList<PropertyDef> _props;
    readonly RulesPack? _pack;
    readonly ListBox _list = new() { Name = "TabList" };
    readonly Button _add = Btn("Add");
    readonly Button _remove = Btn("Remove");
    readonly Button _up = Btn("Move up");
    readonly Button _down = Btn("Move down");
    readonly TextBox _name = new() { Name = "TabName" };
    readonly TextBox _filter = new() { Name = "TabFilter" };
    readonly TextBlock _filterError = new() { Foreground = ErrorBrush, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
    readonly ListBox _cols = new() { Name = "TabColumns" };
    readonly Button _colUp = Btn("Up");
    readonly Button _colDown = Btn("Down");
    readonly CheckBox _holders = new() { Content = "Include depots & Global" };
    readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 44 };
    readonly TextBlock _problems = new() { Foreground = ErrorBrush, TextWrapping = TextWrapping.Wrap, MaxWidth = 640, VerticalAlignment = VerticalAlignment.Center };
    readonly Button _ok = new() { Content = "OK", MinWidth = 75, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
    readonly Button _cancel = new() { Content = "Cancel", MinWidth = 75, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
    readonly Grid _editor = new() { Margin = new Thickness(6, 0, 0, 0), ColumnDefinitions = new ColumnDefinitions("Auto,*") };
    readonly List<string> _fieldLabels = new();
    readonly List<string> _hints = new();
    bool _loading;

    /// <summary>The edited list once OK closed the dialog (null before, or after Cancel).</summary>
    public IReadOnlyList<UserLedgerTab>? Answer { get; private set; }

    /// <param name="select">The user tab selected at first (clamped).</param>
    /// <param name="addNew">True opens with a fresh tab added and selected (the "+" on the tab strip).</param>
    public EditTabsDialog(IReadOnlyList<UserLedgerTab> tabs, RulesPack? pack, int select, bool addNew)
    {
        _tabs = tabs.ToList();
        _pack = pack;
        _props = pack == null ? Array.Empty<PropertyDef>() : LedgerTable.Columns(pack);
        Title = "Edit Ledger tabs";
        Width = 720;
        Height = 460;
        MinWidth = 620;
        MinHeight = 400;
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var listButtons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var b in new[] { _add, _remove, _up, _down }) listButtons.Children.Add(b);
        var left = new DockPanel { Width = 200 };
        DockPanel.SetDock(listButtons, Dock.Bottom);
        left.Children.Add(listButtons);
        left.Children.Add(_list);

        Row("Name:", _name);
        Row("Unit filter:", _filter);
        Row("", _filterError);
        Row("", Hint("Empty means all units. Example: Fuel < 5"));
        var colButtons = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };
        colButtons.Children.Add(_colUp);
        colButtons.Children.Add(_colDown);
        var colsPanel = new DockPanel();
        DockPanel.SetDock(colButtons, Dock.Right);
        colsPanel.Children.Add(colButtons);
        colsPanel.Children.Add(_cols);
        Row("Columns:", colsPanel, star: true);
        Row("", Hint("None ticked means the same columns as the Units tab."));
        Row("", _holders);
        Row("Notes:", _notes);

        var okCancel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        okCancel.Children.Add(_ok);
        okCancel.Children.Add(_cancel);
        var bottom = new Grid { Margin = new Thickness(6), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(okCancel, 1);
        bottom.Children.Add(_problems);
        bottom.Children.Add(okCancel);

        var body = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(left, Dock.Left);
        body.Children.Add(left);
        body.Children.Add(_editor);
        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(body);
        Content = root;

        _list.SelectionChanged += (_, _) => LoadSelected();
        _add.Click += (_, _) => AddTab();
        _remove.Click += (_, _) => RemoveTab();
        _up.Click += (_, _) => MoveTab(-1);
        _down.Click += (_, _) => MoveTab(1);
        _colUp.Click += (_, _) => MoveColumn(-1);
        _colDown.Click += (_, _) => MoveColumn(1);
        // Text changes are taken as they happen (the property, not the posted TextChanged), so a load never writes into another tab.
        OnText(_name, () => Edit(t => t with { Name = _name.Text ?? "" }));
        OnText(_filter, () => Edit(t => t with { Filter = string.IsNullOrEmpty(_filter.Text) ? null : _filter.Text }));
        _holders.IsCheckedChanged += (_, _) => Edit(t => t with { IncludeHolders = _holders.IsChecked == true });
        OnText(_notes, () => Edit(t => t with { Notes = string.IsNullOrEmpty(_notes.Text) ? null : _notes.Text }));
        _ok.Click += (_, _) => TryAccept();
        _cancel.Click += (_, _) => Close(null);

        RefreshList();
        if (addNew) AddTab();
        else if (_tabs.Count > 0) _list.SelectedIndex = Math.Clamp(select, 0, _tabs.Count - 1);
        LoadSelected();
    }

    // ---------- what tests read ----------

    internal IReadOnlyList<UserLedgerTab> Tabs => _tabs.ToList();
    internal IReadOnlyList<string> ListItems => _list.Items.Cast<string>().ToList();
    internal int SelectedIndex => _list.SelectedIndex;
    internal Button AddButton => _add;
    internal Button RemoveButton => _remove;
    internal Button UpButton => _up;
    internal Button DownButton => _down;
    internal Button ColUpButton => _colUp;
    internal Button ColDownButton => _colDown;
    internal Button OkButton => _ok;
    internal Button CancelButton => _cancel;
    internal TextBox NameBox => _name;
    internal TextBox FilterBox => _filter;
    internal TextBox NotesBox => _notes;
    internal CheckBox HoldersBox => _holders;
    internal ListBox ColumnList => _cols;
    internal IReadOnlyList<(string Text, bool Checked)> ColumnItems => ColumnBoxes.Select(b => (b.Content as string ?? "", b.IsChecked == true)).ToList();
    internal void SetColumnChecked(int index, bool value) => ColumnBoxes[index].IsChecked = value;
    internal bool FilterErrorShown => _filterError.IsVisible;
    internal string FilterErrorText => _filterError.Text ?? "";
    internal string ProblemsText => _problems.Text ?? "";
    internal IReadOnlyList<string> FieldLabels => _fieldLabels;
    internal IReadOnlyList<string> Hints => _hints;

    // ---------- layout ----------

    static void OnText(TextBox box, Action changed) =>
        box.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) changed(); };

    static Button Btn(string text) => new() { Content = text, Margin = new Thickness(0, 0, 4, 4), HorizontalContentAlignment = HorizontalAlignment.Center };

    TextBlock Hint(string text)
    {
        _hints.Add(text);
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2) };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("Classic.GrayTextBrush"));
        return t;
    }

    void Row(string label, Control c, bool star = false)
    {
        var r = _editor.RowDefinitions.Count;
        _editor.RowDefinitions.Add(new RowDefinition(star ? new GridLength(1, GridUnitType.Star) : GridLength.Auto));
        if (label.Length > 0)
        {
            _fieldLabels.Add(label);
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(3, 6, 6, 3) };
            Grid.SetRow(l, r);
            _editor.Children.Add(l);
        }
        c.Margin = new Thickness(0, 2);
        Grid.SetRow(c, r);
        Grid.SetColumn(c, 1);
        _editor.Children.Add(c);
    }

    // ---------- editing ----------

    int Current => _list.SelectedIndex;
    bool HasCurrent => Current >= 0 && Current < _tabs.Count;
    List<CheckBox> ColumnBoxes => _cols.Items.OfType<CheckBox>().ToList();

    void RefreshList()
    {
        _loading = true;
        try
        {
            var at = _list.SelectedIndex;
            _list.Items.Clear();
            foreach (var t in _tabs) _list.Items.Add(LedgerViewRules.ListLabel(t));
            if (_tabs.Count > 0) _list.SelectedIndex = Math.Clamp(at, 0, _tabs.Count - 1);
        }
        finally { _loading = false; }
        UpdateProblems();
    }

    void LoadSelected()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var has = HasCurrent;
            _editor.IsEnabled = has;
            _remove.IsEnabled = has;
            _up.IsEnabled = has && Current > 0;
            _down.IsEnabled = has && Current < _tabs.Count - 1;
            var t = has ? _tabs[Current] : new UserLedgerTab("", null, Array.Empty<string>(), false, null);
            _name.Text = t.Name;
            _filter.Text = t.Filter ?? "";
            _holders.IsChecked = t.IncludeHolders;
            _notes.Text = t.Notes ?? "";
            _cols.Items.Clear();
            var ids = t.ColumnIds ?? Array.Empty<string>();
            var added = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids)
                if (_props.FirstOrDefault(p => p.Id == id) is { } p && added.Add(id)) _cols.Items.Add(ColumnBox(p, true));
            foreach (var p in _props)
                if (!ids.Contains(p.Id)) _cols.Items.Add(ColumnBox(p, false));
            ShowFilterError();
        }
        finally { _loading = false; }
    }

    CheckBox ColumnBox(PropertyDef p, bool check)
    {
        var box = new CheckBox { Content = p.Name + (p.Scope == PropertyScope.Holders ? LedgerViewRules.HolderColumnSuffix : ""), IsChecked = check, Tag = p };
        box.IsCheckedChanged += (_, _) => Edit(t => t with { ColumnIds = CheckedIds() });
        box.Click += (_, _) => _cols.SelectedItem = box;   // WinForms CheckOnClick: the clicked row is also the selected one (for Up / Down)
        return box;
    }

    IReadOnlyList<string> CheckedIds()
    {
        var list = ColumnBoxes.Where(b => b.IsChecked == true).Select(b => ((PropertyDef)b.Tag!).Id).ToList();
        // Columns that no longer exist in this rules pack stay stored (they come back if the property does).
        if (HasCurrent) list.AddRange(_tabs[Current].ColumnIds.Where(id => !_props.Any(p => p.Id == id)));
        return list;
    }

    void Edit(Func<UserLedgerTab, UserLedgerTab> edit)
    {
        if (_loading || !HasCurrent) return;
        var at = Current;
        _tabs[at] = edit(_tabs[at]);
        var label = LedgerViewRules.ListLabel(_tabs[at]);
        if (!Equals(_list.Items[at], label))
        {
            _loading = true;
            try
            {
                _list.Items[at] = label;
                if (_list.SelectedIndex != at) _list.SelectedIndex = at;
            }
            finally { _loading = false; }
        }
        ShowFilterError();
        UpdateProblems();
    }

    void ShowFilterError()
    {
        var f = HasCurrent ? _tabs[Current].Filter : null;
        var err = LedgerTabs.FilterError(f);
        var text = err == null ? null : "Does not parse: " + err;
        if (text == null && _pack != null && LedgerTabs.FilterError(f, _pack) is { } perr) text = perr;
        _filterError.Text = text ?? "";
        _filterError.IsVisible = text != null;
    }

    void UpdateProblems()
    {
        var p = LedgerTabsEdit.Problems(_tabs, _pack);
        _problems.Text = string.Join(Environment.NewLine, p);
        _ok.IsEnabled = p.Count == 0;
    }

    void AddTab()
    {
        var name = LedgerViewRules.NewTabName(_tabs);
        var at = Current < 0 ? _tabs.Count : Current + 1;
        _tabs.Insert(at, new UserLedgerTab(name, null, Array.Empty<string>(), false, null));
        RefreshList();
        _loading = true;
        try { _list.SelectedIndex = at; }
        finally { _loading = false; }
        LoadSelected();
        _name.Focus();
        _name.SelectAll();
    }

    void RemoveTab()
    {
        if (!HasCurrent) return;
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
        _loading = true;
        try { _list.SelectedIndex = to; }
        finally { _loading = false; }
        LoadSelected();
    }

    void MoveColumn(int delta)
    {
        var i = _cols.SelectedIndex;
        var to = i + delta;
        if (i < 0 || to < 0 || to >= _cols.Items.Count) return;
        _loading = true;
        try
        {
            var item = _cols.Items[i];
            _cols.Items.RemoveAt(i);
            _cols.Items.Insert(to, item);
            _cols.SelectedIndex = to;
        }
        finally { _loading = false; }
        Edit(t => t with { ColumnIds = CheckedIds() });
    }

    void TryAccept()
    {
        var p = LedgerTabsEdit.Problems(_tabs, _pack);
        if (p.Count > 0) { UpdateProblems(); return; }
        Answer = LedgerTabStore.Normalize(_tabs.ToList());
        Close(Answer);
    }
}
