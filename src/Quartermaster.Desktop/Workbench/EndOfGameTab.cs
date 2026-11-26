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
/// End-of-game editor of the rules workbench (B5 #73): the last turn and stage, and the victory conditions (condition, winning side, message, the
/// phases it is checked in). Edits go through UpdateRules; text fields commit 400 ms after the last keystroke (or when they lose focus), as the Alerts
/// tab does. The live line evaluates only the selected condition, over what the current viewer may see, and never on a guest.
/// </summary>
public sealed class EndOfGameTab : UserControl
{
    const string NoWinner = "(no winner)";
    const string MissingText = "(missing)";

    internal sealed record VictoryRow(string Text);

    readonly ModuleSession _s;
    readonly CheckBox _lastTurnCheck = new() { Content = "The game ends after turn" };
    readonly NumericUpDown _lastTurn = new() { Minimum = 1, Maximum = GameEndEdit.MaxTurn, Value = 1, Increment = 1, FormatString = "0", Width = 100 };
    readonly CheckBox _lastStageCheck = new() { Content = "Last stage" };
    readonly NumericUpDown _lastStage = new() { Minimum = 1, Maximum = GameEndEdit.MaxTurn, Value = 1, Increment = 1, FormatString = "0", Width = 100 };
    readonly ListBox _list = new();
    readonly Control _right;
    readonly TextBox _condition = new();
    readonly TextBlock _live = new() { Margin = new Thickness(0, 3, 0, 0) };
    readonly ComboBox _side = new() { Width = 220 };
    readonly TextBox _message = new();
    readonly TextBox _checkedIn = new();
    readonly TextBlock _checkedInError = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _issues = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
    readonly Button _bAdd = new() { Content = "Add", Width = 50 }, _bDel = new() { Content = "Remove", Width = 56 },
        _bUp = new() { Content = "Move Up", Width = 62 }, _bDown = new() { Content = "Move Down", Width = 74 };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly List<string> _victoryIds = new();
    readonly List<string> _sideIds = new();
    string? _selected;
    bool _selfEdit, _loading;

    public EndOfGameTab(ModuleSession s, IDialogs dialogs)
    {
        _s = s;
        _ = dialogs;
        _list.ItemTemplate = new FuncDataTemplate<VictoryRow>((row, _) => new TextBlock { Text = row?.Text, TextTrimming = TextTrimming.CharacterEllipsis });

        // ----- the end of the turn count -----
        var turnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        turnRow.Children.Add(_lastTurnCheck);
        turnRow.Children.Add(_lastTurn);
        var stageRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        stageRow.Children.Add(_lastStageCheck);
        stageRow.Children.Add(_lastStage);
        var top = new StackPanel { Spacing = 4, Margin = new Thickness(4) };
        top.Children.Add(turnRow);
        top.Children.Add(stageRow);

        // ----- left: the victory conditions -----
        var btns = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var b in new[] { _bAdd, _bDel, _bUp, _bDown }) { b.Margin = new Thickness(0, 0, 2, 2); btns.Children.Add(b); }
        var leftInner = new DockPanel();
        Dock(leftInner, btns, Avalonia.Controls.Dock.Bottom);
        leftInner.Children.Add(_list);
        var left = new DockPanel { Width = 250, Margin = new Thickness(0, 0, 4, 0) };
        left.Children.Add(Box("Victory conditions, in order", leftInner));

        // ----- right: the selected condition -----
        var rows = new StackPanel { Margin = new Thickness(4), Spacing = 4 };
        var condInner = new StackPanel();
        condInner.Children.Add(_condition);
        condInner.Children.Add(_live);
        rows.Children.Add(Box("Condition (true ends the game, for example Score >= 10)", condInner));
        rows.Children.Add(Labelled("Side:", _side));
        rows.Children.Add(Labelled("Message:", _message));
        var inner = new StackPanel();
        inner.Children.Add(_checkedIn);
        inner.Children.Add(_checkedInError);
        rows.Children.Add(Box("Checked in (phase names, comma-separated; blank = every phase)", inner));
        rows.Children.Add(_issues);
        _right = new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        var lower = new DockPanel { Margin = new Thickness(4) };
        Dock(lower, left, Avalonia.Controls.Dock.Left);
        lower.Children.Add(_right);
        var dock = new DockPanel();
        Dock(dock, top, Avalonia.Controls.Dock.Top);
        dock.Children.Add(lower);
        Content = dock;

        // ----- events -----
        _lastTurnCheck.IsCheckedChanged += (_, _) => Run(OnLastTurn);
        _lastTurn.ValueChanged += (_, _) => Run(OnLastTurn);
        _lastStageCheck.IsCheckedChanged += (_, _) => Run(OnLastStage);
        _lastStage.ValueChanged += (_, _) => Run(OnLastStage);
        _bAdd.Click += (_, _) => Run(AddVictory);
        _bDel.Click += (_, _) => Run(RemoveVictory);
        _bUp.Click += (_, _) => Run(() => Structural(p => GameEndEdit.MoveVictory(p, SelectedIndex, -1)));
        _bDown.Click += (_, _) => Run(() => Structural(p => GameEndEdit.MoveVictory(p, SelectedIndex, 1)));
        _list.SelectionChanged += (_, _) => Run(OnSelected);
        foreach (var t in new[] { _condition, _message, _checkedIn })
        {
            t.OnTextSet(() => { if (!_loading) Kick(); });
            t.LostFocus += (_, _) => Run(Flush);
        }
        _condition.OnTextSet(() => { if (!_loading) ShowLive(); });
        _timer.Tick += (_, _) => Run(Flush);
        _side.SelectionChanged += (_, _) => Run(OnSideChosen);

        AttachedToVisualTree += (_, _) =>
        {
            _s.RulesChanged -= OnRulesChanged; _s.RulesChanged += OnRulesChanged;
            _s.GameReplaced -= OnGameChanged; _s.GameReplaced += OnGameChanged;
            _s.ViewChanged -= OnGameChanged; _s.ViewChanged += OnGameChanged;
            Run(Reload);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Run(Flush);   // a hidden tab is detached: what was typed must reach the pack first
            _s.RulesChanged -= OnRulesChanged;
            _s.GameReplaced -= OnGameChanged;
            _s.ViewChanged -= OnGameChanged;
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

    static Control Labelled(string label, Control c)
    {
        var d = new DockPanel();
        Dock(d, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) }, Avalonia.Controls.Dock.Left);
        if (c is TextBox) d.Children.Add(c);
        else { c.HorizontalAlignment = HorizontalAlignment.Left; d.Children.Add(c); }
        return d;
    }

    // ---------- plumbing ----------

    public string? SelectedVictoryId => _selected;

    /// <summary>Selects a victory condition by id (no-op when unknown).</summary>
    public void SelectVictory(string id)
    {
        var i = _victoryIds.IndexOf(id);
        if (i < 0) return;
        _list.SelectedIndex = i;
        _list.ScrollIntoView(i);
    }

    /// <summary>Commits what is being typed into the pack; call before saving.</summary>
    public void CommitPending() => Run(Flush);

    void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Failed(ex); }
    }

    /// <summary>Logs the failure and redraws from the pack, so a refused edit (guest, replay) never leaves text the pack does not hold.</summary>
    void Failed(Exception ex)
    {
        _s.Log.Add(DiagnosticSeverity.Warning, "rules", "End of game edit failed: " + ex.Message, "");
        try { Reload(); }
        catch (Exception ex2) when (ex2 is not OutOfMemoryException) { _s.Log.Add(DiagnosticSeverity.Warning, "rules", "End of game tab reload failed: " + ex2.Message, ""); }
    }

    void Kick() { _timer.Stop(); _timer.Start(); }

    void OnUi(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) Run(a);
        else Dispatcher.UIThread.Post(() => Run(a));
    }

    void OnRulesChanged() => OnUi(() => { if (!_selfEdit) Reload(); });

    void OnGameChanged() => OnUi(() => { if (!_selfEdit) ShowLive(); });

    VictoryCondition? Current => _selected == null ? null : _s.Rules?.Victory?.FirstOrDefault(v => v.Id == _selected);

    int SelectedIndex => _selected == null || _s.Rules?.Victory is not { } list ? -1 : list.FindIndex(x => x.Id == _selected);

    void CommitPack(Func<RulesPack, RulesPack> edit)
    {
        _selfEdit = true;
        try { _s.UpdateRules(edit); }
        finally { _selfEdit = false; }
    }

    void CommitVictory(Func<VictoryCondition, VictoryCondition> edit)
    {
        if (_selected is not { } id) return;
        CommitPack(p => GameEndEdit.UpdateVictory(p, id, edit));
        RefreshList();
        ShowLive();
        ShowIssues();
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
        if (pack?.Victory == null || pack.Victory.All(v => v.Id != _selected)) _selected = pack?.Victory?.FirstOrDefault()?.Id;
        var prev = _loading;
        _loading = true;
        try
        {
            var hasTurn = pack?.LastTurn != null;
            _lastTurnCheck.IsChecked = hasTurn;
            _lastTurn.IsEnabled = hasTurn;
            _lastTurn.Value = pack?.LastTurn ?? 1;
            var hasStage = pack?.LastStage != null;
            _lastStageCheck.IsChecked = hasStage;
            _lastStage.IsEnabled = hasStage;
            _lastStage.Value = pack?.LastStage ?? 1;
            _lastTurnCheck.IsEnabled = _lastStageCheck.IsEnabled = pack != null;
        }
        finally { _loading = prev; }
        RefreshList();
        LoadVictory();
    }

    static string Summary(VictoryCondition v, int n)
    {
        var c = (v.Condition ?? "").Trim();
        return $"{n}. " + (c.Length == 0 ? "(no condition)" : c);
    }

    void RefreshList()
    {
        var prev = _loading;
        _loading = true;
        try
        {
            _victoryIds.Clear();
            var rows = new List<VictoryRow>();
            var n = 0;
            foreach (var v in _s.Rules?.Victory ?? new List<VictoryCondition>()) { rows.Add(new VictoryRow(Summary(v, ++n))); _victoryIds.Add(v.Id); }
            _list.ItemsSource = rows;
            _list.SelectedIndex = _selected == null ? -1 : _victoryIds.IndexOf(_selected);
        }
        finally { _loading = prev; }
    }

    string PhaseNames(VictoryCondition? v) =>
        v?.PhaseIds == null ? "" : string.Join(", ", v.PhaseIds.Select(id => _s.Rules?.Phases.FirstOrDefault(p => p.Id == id)?.Name ?? id));

    void LoadVictory()
    {
        var pack = _s.Rules;
        var v = Current;
        var prev = _loading;
        _loading = true;
        try
        {
            _right.IsEnabled = v != null;
            _condition.Text = v?.Condition ?? "";
            _message.Text = v?.Message ?? "";
            _checkedIn.Text = PhaseNames(v);
            _checkedInError.Text = "";
            var names = new List<string> { NoWinner };
            _sideIds.Clear();
            _sideIds.Add("");
            if (pack != null) foreach (var sd in pack.Sides) { names.Add(sd.Name); _sideIds.Add(sd.Id); }
            var idx = 0;
            if (v?.SideId is { } sid)
            {
                idx = _sideIds.IndexOf(sid);
                if (idx < 0) { names.Add(MissingText); _sideIds.Add(sid); idx = _sideIds.Count - 1; }
            }
            _side.ItemsSource = names;
            _side.SelectedIndex = v == null ? -1 : idx;
        }
        finally { _loading = prev; }
        ShowLive();
        ShowIssues();
    }

    /// <summary>The live line uses what is typed now; it is evaluated on the host or in solo play only, over what the viewer may see.</summary>
    void ShowLive()
    {
        if (_s.IsGuest || _s.Rules is not { } pack || Current is not { } v) { _live.Text = ""; return; }
        var probe = v with { Condition = _condition.Text ?? "" };
        _live.Text = GameEndEdit.ConditionStatus(probe, _s.View, pack, _s.Model);
        if (_live.Text.StartsWith("Does not parse", StringComparison.Ordinal) || _live.Text.StartsWith("failed", StringComparison.Ordinal)) _live.Foreground = Brushes.Firebrick;
        else _live.ClearValue(TextBlock.ForegroundProperty);
    }

    void ShowIssues()
    {
        if (_s.Rules is not { } pack) { _issues.Text = ""; return; }
        try
        {
            var ids = pack.Victory?.Select(x => x.Id).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
            var mine = RulesValidator.Validate(pack)
                .Where(i => (_selected != null && i.EntityId == _selected)
                    || (i.EntityId == pack.Id && (i.Message.StartsWith("The last ", StringComparison.Ordinal) || i.Message.StartsWith("A last stage ", StringComparison.Ordinal)
                        || i.Message.StartsWith("Victory condition ", StringComparison.Ordinal))))
                .Select(i => i.Message);
            _issues.Text = string.Join(Environment.NewLine, mine);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _s.Log.Add(DiagnosticSeverity.Warning, "rules", "End of game validation failed: " + ex.Message, "");
            _issues.Text = "";
        }
    }

    // ---------- edits ----------

    void OnLastTurn()
    {
        if (_loading) return;
        var on = _lastTurnCheck.IsChecked == true;
        _lastTurn.IsEnabled = on;
        var v = on ? (int?)(int)(_lastTurn.Value ?? 1) : null;
        if (_s.Rules?.LastTurn == v) return;
        CommitPack(p => GameEndEdit.WithLastTurn(p, v));
        ShowIssues();
    }

    void OnLastStage()
    {
        if (_loading) return;
        var on = _lastStageCheck.IsChecked == true;
        _lastStage.IsEnabled = on;
        var v = on ? (int?)(int)(_lastStage.Value ?? 1) : null;
        if (_s.Rules?.LastStage == v) return;
        CommitPack(p => GameEndEdit.WithLastStage(p, v));
        ShowIssues();
    }

    void OnSelected()
    {
        if (_loading) return;
        var i = _list.SelectedIndex;
        var id = i >= 0 && i < _victoryIds.Count ? _victoryIds[i] : null;   // read first: flushing refreshes the list, which re-selects the old row
        Flush();
        if (id == null || id == _selected) return;
        if (_s.Rules?.Victory?.All(v => v.Id != id) != false) return;
        _selected = id;
        RefreshList();
        LoadVictory();
    }

    void Flush()
    {
        _timer.Stop();
        if (_loading || Current is not { } v || _s.Rules is not { } pack) return;
        var (cond, msg, where) = (_condition.Text ?? "", _message.Text ?? "", _checkedIn.Text ?? "");
        // the phase names: every name must be a phase of the pack, else the text stays as typed and the pack keeps its old list
        List<string>? ids = v.PhaseIds;
        if (where.Trim() != PhaseNames(v).Trim())
        {
            var parsed = new List<string>();
            string? unknown = null;
            foreach (var name in where.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var ph = pack.Phases.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (ph == null) { unknown = name; break; }
                parsed.Add(ph.Id);
            }
            if (unknown != null) _checkedInError.Text = $"No phase named \"{unknown}\"";
            else { _checkedInError.Text = ""; ids = parsed; }
        }
        if (v.Condition == cond && (v.Message ?? "") == msg && ReferenceEquals(ids, v.PhaseIds)) return;
        CommitVictory(x => x with { Condition = cond, Message = msg, PhaseIds = ids });
    }

    void OnSideChosen()
    {
        if (_loading || Current == null) return;
        var i = _side.SelectedIndex;
        if (i < 0 || i >= _sideIds.Count) return;
        var id = _sideIds[i];
        if ((Current.SideId ?? "") == id) return;
        CommitVictory(x => x with { SideId = id.Length == 0 ? null : id });
    }

    void AddVictory()
    {
        Flush();
        if (_s.Rules is not { } pack) return;
        var next = GameEndEdit.AddVictory(pack);
        var id = next.Victory![^1].Id;
        CommitPack(_ => next);
        _selected = id;
        RefreshList();
        LoadVictory();
        _condition.Focus();
    }

    void RemoveVictory()
    {
        Flush();
        if (_s.Rules is not { } pack || SelectedIndex < 0) return;
        var i = SelectedIndex;
        var next = GameEndEdit.RemoveVictory(pack, i);
        var sel = next.Victory == null ? null : next.Victory[Math.Min(i, next.Victory.Count - 1)].Id;
        CommitPack(_ => next);
        _selected = sel;
        RefreshList();
        LoadVictory();
    }

    // test hooks
    internal CheckBox LastTurnCheck => _lastTurnCheck;
    internal NumericUpDown LastTurnBox => _lastTurn;
    internal CheckBox LastStageCheck => _lastStageCheck;
    internal NumericUpDown LastStageBox => _lastStage;
    internal ListBox VictoryList => _list;
    internal IReadOnlyList<VictoryRow> Rows => ((IEnumerable<VictoryRow>?)_list.ItemsSource)?.ToList() ?? new List<VictoryRow>();
    internal TextBox ConditionBox => _condition;
    internal TextBlock LiveText => _live;
    internal ComboBox SideBox => _side;
    internal TextBox MessageBox => _message;
    internal TextBox CheckedInBox => _checkedIn;
    internal TextBlock CheckedInError => _checkedInError;
    internal TextBlock IssuesText => _issues;
    internal Button AddButton => _bAdd;
    internal Button RemoveButton => _bDel;
    internal Button UpButton => _bUp;
    internal Button DownButton => _bDown;
}
