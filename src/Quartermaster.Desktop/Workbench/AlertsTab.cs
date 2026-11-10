using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Workbench;

/// <summary>
/// Alerts editor of the rules workbench (port of WinForms AlertsTab): user-defined alerts (condition over units, message, severity, optional side, notes).
/// Edits go through UpdateRules; text fields commit 400 ms after the last keystroke (or when they lose focus). The "N matches now" line evaluates only the
/// selected alert, over what the current viewer may see.
/// </summary>
public sealed class AlertsTab : UserControl
{
    const string AnySide = "(any)";
    const string MissingText = "(missing)";

    internal sealed record AlertRow(string Name, string Severity);

    readonly ModuleSession _s;
    readonly IDialogs _dialogs;
    readonly ListBox _list = new();
    readonly Control _right;
    readonly TextBox _name = new();
    readonly TextBox _condition = new();
    readonly TextBlock _status = new() { Margin = new Thickness(0, 3, 0, 0) };
    readonly TextBox _message = new();
    readonly ComboBox _severity = new() { Width = 120 };
    readonly ComboBox _side = new() { Width = 200 };
    readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 66 };
    readonly TextBlock _issues = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
    readonly Button _bAdd = new() { Content = "Add", Width = 50 }, _bDel = new() { Content = "Remove", Width = 56 },
        _bUp = new() { Content = "Move Up", Width = 62 }, _bDown = new() { Content = "Move Down", Width = 74 };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly List<string> _alertIds = new();
    readonly List<string> _sideIds = new();
    string? _selected;
    bool _selfEdit, _loading;

    public AlertsTab(ModuleSession s, IDialogs dialogs)
    {
        _s = s;
        _dialogs = dialogs;

        _list.ItemTemplate = new FuncDataTemplate<AlertRow>((row, _) =>
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("150,60") };
            g.Children.Add(new TextBlock { Text = row?.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            var sev = new TextBlock { Text = row?.Severity, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(sev, 1);
            g.Children.Add(sev);
            return g;
        });
        _severity.ItemsSource = Enum.GetNames<AlertSeverity>();

        // ----- left: the list -----
        var btns = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var b in new[] { _bAdd, _bDel, _bUp, _bDown }) { b.Margin = new Thickness(0, 0, 2, 2); btns.Children.Add(b); }
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("150,60"), Margin = new Thickness(4, 0, 0, 2) };
        header.Children.Add(new TextBlock { Text = "Alert", FontWeight = FontWeight.SemiBold });
        var sevHead = new TextBlock { Text = "Severity", FontWeight = FontWeight.SemiBold };
        Grid.SetColumn(sevHead, 1);
        header.Children.Add(sevHead);
        var leftInner = new DockPanel();
        Dock(leftInner, header, Avalonia.Controls.Dock.Top);
        Dock(leftInner, btns, Avalonia.Controls.Dock.Bottom);
        leftInner.Children.Add(_list);
        var left = new DockPanel { Width = 250, Margin = new Thickness(0, 0, 4, 0) };
        left.Children.Add(Box("Alerts, in order", leftInner));

        // ----- right: fields of the selected alert -----
        var rows = new StackPanel { Margin = new Thickness(4), Spacing = 4 };
        var nameRow = new DockPanel();
        Dock(nameRow, new TextBlock { Text = "Name:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) }, Avalonia.Controls.Dock.Left);
        nameRow.Children.Add(_name);
        rows.Children.Add(nameRow);

        var condInner = new StackPanel();
        condInner.Children.Add(_condition);
        condInner.Children.Add(_status);
        rows.Children.Add(Box("Condition (a unit filter, for example Fuel < 20 and Ammo < 10)", condInner));

        var msgRow = new DockPanel();
        Dock(msgRow, new TextBlock { Text = "Message ({unit} = unit name):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) }, Avalonia.Controls.Dock.Left);
        msgRow.Children.Add(_message);
        rows.Children.Add(msgRow);

        var pick = new StackPanel { Orientation = Orientation.Horizontal };
        pick.Children.Add(new TextBlock { Text = "Severity:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        pick.Children.Add(_severity);
        pick.Children.Add(new TextBlock { Text = "Side:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) });
        pick.Children.Add(_side);
        rows.Children.Add(pick);

        rows.Children.Add(Box("Notes (shown as a tooltip on the alert)", _notes));
        rows.Children.Add(_issues);
        _right = new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        var dock = new DockPanel { Margin = new Thickness(4) };
        Dock(dock, left, Avalonia.Controls.Dock.Left);
        dock.Children.Add(_right);
        Content = dock;

        // ----- events -----
        _bAdd.Click += (_, _) => Run(AddAlert);
        _bDel.Click += (_, _) => Run(RemoveAlert);
        _bUp.Click += (_, _) => Run(() => Structural(p => AlertEdit.Move(p, SelectedIndex, -1)));
        _bDown.Click += (_, _) => Run(() => Structural(p => AlertEdit.Move(p, SelectedIndex, 1)));
        _list.SelectionChanged += (_, _) => Run(OnSelected);
        foreach (var t in new[] { _name, _condition, _message, _notes })
        {
            t.TextChanged += (_, _) => { if (!_loading) Kick(); };
            t.LostFocus += (_, _) => Run(Flush);
        }
        _condition.TextChanged += (_, _) => { if (!_loading) ShowStatus(); };
        _timer.Tick += (_, _) => Run(Flush);
        _severity.SelectionChanged += (_, _) => Run(OnSeverityChosen);
        _side.SelectionChanged += (_, _) => Run(OnSideChosen);

        AttachedToVisualTree += (_, _) =>
        {
            _s.RulesChanged -= OnRulesChanged; _s.RulesChanged += OnRulesChanged;
            _s.GameReplaced -= OnGameReplaced; _s.GameReplaced += OnGameReplaced;
            _s.ViewChanged -= OnViewChanged; _s.ViewChanged += OnViewChanged;
            Run(Reload);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Run(Flush);   // a hidden tab is detached: what was typed must reach the pack first
            _s.RulesChanged -= OnRulesChanged;
            _s.GameReplaced -= OnGameReplaced;
            _s.ViewChanged -= OnViewChanged;
            _timer.Stop();
        };
        Reload();
    }

    // ---------- layout helpers ----------

    static void Dock(DockPanel p, Control c, Dock d) { DockPanel.SetDock(c, d); p.Children.Add(c); }

    static Control Box(string title, Control content)
    {
        var d = new DockPanel();
        Dock(d, new TextBlock { Text = title, Margin = new Thickness(2, 0, 0, 2) }, Avalonia.Controls.Dock.Top);
        d.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Padding = new Thickness(4), Child = content });
        return d;
    }

    // ---------- plumbing ----------

    public string? SelectedAlertId => _selected;

    /// <summary>Selects an alert by id (no-op when unknown).</summary>
    public void SelectAlert(string id)
    {
        var i = _alertIds.IndexOf(id);
        if (i < 0) return;
        _list.SelectedIndex = i;
        _list.ScrollIntoView(i);
    }

    /// <summary>Commits what is being typed into the pack; call before saving.</summary>
    public void CommitPending() => Run(Flush);

    Window? Owner => TopLevel.GetTopLevel(this) as Window;

    void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Failed(ex); }
    }

    /// <summary>Logs the failure and redraws from the pack, so a refused edit (guest, replay) never leaves text the pack does not hold.</summary>
    void Failed(Exception ex)
    {
        _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Alert edit failed: " + ex.Message, "");
        try { Reload(); }
        catch (Exception ex2) when (ex2 is not OutOfMemoryException) { _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Alerts tab reload failed: " + ex2.Message, ""); }
    }

    void Kick() { _timer.Stop(); _timer.Start(); }

    void OnUi(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) Run(a);
        else Dispatcher.UIThread.Post(() => Run(a));
    }

    void OnRulesChanged() => OnUi(() => { if (!_selfEdit) Reload(); });

    void OnGameReplaced() => OnUi(ShowStatus);

    // Hot-seat and every game change: the count follows what the viewer may see. A tab's own edits refresh it themselves.
    void OnViewChanged() => OnUi(() => { if (!_selfEdit) ShowStatus(); });

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
        _timer.Stop();
        var pack = _s.Rules;
        if (pack == null || pack.Alerts.All(a => a.Id != _selected)) _selected = pack?.Alerts.FirstOrDefault()?.Id;
        RefreshList();
        LoadAlert();
    }

    void RefreshList()
    {
        var prev = _loading;
        _loading = true;
        try
        {
            _alertIds.Clear();
            var rows = new List<AlertRow>();
            foreach (var a in _s.Rules?.Alerts ?? new List<AlertDef>()) { rows.Add(new AlertRow(a.Name, a.Severity.ToString())); _alertIds.Add(a.Id); }
            _list.ItemsSource = rows;
            _list.SelectedIndex = _selected == null ? -1 : _alertIds.IndexOf(_selected);
        }
        finally { _loading = prev; }
    }

    void LoadAlert()
    {
        var pack = _s.Rules;
        var a = Current;
        var prev = _loading;
        _loading = true;
        try
        {
            _right.IsEnabled = a != null;
            _name.Text = a?.Name ?? "";
            _condition.Text = a?.Condition ?? "";
            _message.Text = a?.Message ?? "";
            _notes.Text = a?.Notes ?? "";
            _severity.SelectedIndex = a == null ? -1 : (int)a.Severity;
            var names = new List<string> { AnySide };
            _sideIds.Clear();
            _sideIds.Add("");
            if (pack != null) foreach (var sd in pack.Sides) { names.Add(sd.Name); _sideIds.Add(sd.Id); }
            var idx = 0;
            if (a?.SideId is { } sid)
            {
                idx = _sideIds.IndexOf(sid);
                if (idx < 0) { names.Add(MissingText); _sideIds.Add(sid); idx = _sideIds.Count - 1; }
            }
            _side.ItemsSource = names;
            _side.SelectedIndex = a == null ? -1 : idx;
        }
        finally { _loading = prev; }
        ShowStatus();
        ShowIssues();
    }

    /// <summary>The live count uses what is typed now, so it follows the text before the commit lands; it counts only what this player may see.</summary>
    void ShowStatus()
    {
        if (_s.Rules is not { } pack || Current is not { } a) { _status.Text = ""; return; }
        var probe = a with { Condition = _condition.Text ?? "" };
        _status.Text = AlertEdit.MatchStatus(probe, _s.View, pack);
        _status.Foreground = _status.Text.StartsWith("Does not parse", StringComparison.Ordinal) ? Brushes.Firebrick : null;
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
            _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Alerts validation failed: " + ex.Message, "");
            _issues.Text = "";
        }
    }

    // ---------- edits ----------

    void OnSelected()
    {
        if (_loading) return;
        var i = _list.SelectedIndex;
        var id = i >= 0 && i < _alertIds.Count ? _alertIds[i] : null;   // read first: flushing refreshes the list, which re-selects the old alert
        Flush();
        if (id == null || id == _selected) return;
        if (_s.Rules?.Alerts.All(a => a.Id != id) != false) return;
        _selected = id;
        RefreshList();
        LoadAlert();
    }

    static string Norm(string s) => s.Replace("\r\n", "\n");

    void Flush()
    {
        _timer.Stop();
        if (_loading || Current is not { } a) return;
        var (name, cond, msg, notes) = (_name.Text ?? "", _condition.Text ?? "", _message.Text ?? "", _notes.Text ?? "");
        // a box may hand back LF for a stored CRLF: that is no edit
        if (a.Name == name && a.Condition == cond && a.Message == msg && Norm(a.Notes ?? "") == Norm(notes)) return;
        CommitAlert(x => x with { Name = name, Condition = cond, Message = msg, Notes = notes.Length == 0 ? null : notes });
        ShowIssues();
    }

    void OnSeverityChosen()
    {
        if (_loading || Current == null || _severity.SelectedIndex < 0) return;
        var sev = (AlertSeverity)_severity.SelectedIndex;
        if (Current.Severity == sev) return;
        CommitAlert(a => a with { Severity = sev });
    }

    void OnSideChosen()
    {
        if (_loading || Current == null) return;
        var i = _side.SelectedIndex;
        if (i < 0 || i >= _sideIds.Count) return;
        var id = _sideIds[i];
        if ((Current.SideId ?? "") == id) return;
        CommitAlert(x => x with { SideId = id.Length == 0 ? null : id });
        ShowIssues();
    }

    void AddAlert()
    {
        Flush();
        if (_s.Rules is not { } pack) return;
        var at = SelectedIndex < 0 ? pack.Alerts.Count : SelectedIndex + 1;
        var next = AlertEdit.Add(pack, at);
        var id = next.Alerts[at].Id;
        CommitPack(_ => next);
        _selected = id;
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
        var sel = next.Alerts.Count == 0 ? null : next.Alerts[Math.Min(i, next.Alerts.Count - 1)].Id;
        CommitPack(_ => next);
        _selected = sel;
        RefreshList();
        LoadAlert();
    }

    // test hooks
    internal ListBox AlertList => _list;
    internal IReadOnlyList<AlertRow> Rows => ((IEnumerable<AlertRow>?)_list.ItemsSource)?.ToList() ?? new List<AlertRow>();
    internal TextBox NameBox => _name;
    internal TextBox ConditionBox => _condition;
    internal TextBox MessageBox => _message;
    internal TextBox NotesBox => _notes;
    internal ComboBox SeverityBox => _severity;
    internal ComboBox SideBox => _side;
    internal TextBlock MatchStatusText => _status;
    internal TextBlock IssuesText => _issues;
    internal Button AddButton => _bAdd;
    internal Button RemoveButton => _bDel;
    internal Button UpButton => _bUp;
    internal Button DownButton => _bDown;
}
