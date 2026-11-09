using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Desktop.Phases;

/// <summary>
/// The phase panel (WinForms PhasePanel): current phase name and instructions, the rule reference (shown disabled until the rulebook viewer
/// exists), matching warnings (with Acknowledge for decision warnings), "Decisions left" (checklist, pending runs, decision warnings) and the
/// Next Phase button. Refreshes debounced on game / rules / view changes. Hosted docked at the right of the main window.
/// </summary>
public sealed class PhasePanel : UserControl
{
    /// <summary>Tooltip of the rule reference link until the rulebook viewer exists.</summary>
    public const string RuleViewerTip = "Rulebook viewer comes in a later version";

    readonly ModuleSession _s;
    readonly PhaseController _c;
    readonly Func<IActionPort> _actions;
    readonly Action<string> _centre;
    readonly PhaseWatch _watch;

    readonly TextBlock _title = new() { FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2) };
    readonly TextBox _instructions = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _ref = new() { IsVisible = false, IsEnabled = false, TextDecorations = Avalonia.Media.TextDecorations.Underline, Margin = new Thickness(0, 2) };
    readonly TextBlock _warnCaption = new() { Text = "Warnings", VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 4, 0, 0) };
    readonly TreeView _warnings = new();
    readonly Button _ack = new() { Content = "Acknowledge", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, IsEnabled = false };
    readonly TextBlock _decCaption = new() { Text = "Decisions left", VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 4, 0, 0) };
    readonly StackPanel _checklist = new();
    readonly ListBox _open = new();
    readonly Button _next = new() { Content = PhaseTexts.NextPhaseText, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
    readonly TextBlock _count = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2) };
    readonly Button _pop = new() { Content = "Pop out", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
    readonly TextBlock _none = new() { Text = PhaseTexts.NoPhasesText, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(4) };
    readonly Grid _body = new();

    IReadOnlyList<Decision> _openRows = Array.Empty<Decision>();
    PhaseView? _view;
    bool _updating;
    string? _nextTip;

    public PhasePanel(ModuleSession s, PhaseController c, Func<IActionPort> actions, Action<string> centre)
    {
        _s = s; _c = c; _actions = actions; _centre = centre;
        Name = "PhasePanel";
        MinWidth = 180;

        _body.RowDefinitions = new RowDefinitions("Auto,2*,Auto,Auto,*,Auto,Auto,*,*,Auto,Auto");
        void Row(Control ctl, int row) { Grid.SetRow(ctl, row); _body.Children.Add(ctl); }
        Row(_title, 0);
        Row(_instructions, 1);
        Row(_ref, 2);
        Row(_warnCaption, 3);
        Row(_warnings, 4);
        Row(_ack, 5);
        Row(_decCaption, 6);
        Row(new ScrollViewer { Content = _checklist, Name = "ChecklistScroll" }, 7);
        Row(_open, 8);
        Row(_next, 9);
        Row(_count, 10);
        ToolTip.SetTip(_ref, RuleViewerTip);
        ToolTip.SetShowOnDisabled(_ref, true);
        ToolTip.SetShowOnDisabled(_next, true);

        var root = new DockPanel { Margin = new Thickness(4) };
        DockPanel.SetDock(_pop, Dock.Bottom);
        root.Children.Add(_pop);
        root.Children.Add(_body);
        var host = new Grid();
        host.Children.Add(root);
        host.Children.Add(_none);
        Content = host;
        _none.VerticalAlignment = VerticalAlignment.Top;

        _pop.Click += (_, _) => PopToggleRequested?.Invoke();
        _next.Click += (_, _) => NextRequested?.Invoke();
        _ack.Click += (_, _) => AckSelected();
        _warnings.SelectionChanged += (_, _) => UpdateAckEnabled();
        _warnings.DoubleTapped += (_, _) => { if (_warnings.SelectedItem is TreeViewItem { Tag: string id }) _centre(id); };
        _open.DoubleTapped += (_, _) => OpenSelected();
        _watch = new PhaseWatch(s, Reload);
        Reload();
    }

    /// <summary>Raised by the Next Phase / Start game button; the host runs the phase flow.</summary>
    public event Action? NextRequested;

    /// <summary>Raised when an open decision (not a checklist item) is double-clicked.</summary>
    public event Action<Decision>? DecisionActivated;

    /// <summary>Raised by the Pop out / Dock button; the host moves the panel.</summary>
    public event Action? PopToggleRequested;

    /// <summary>The Pop out / Dock button.</summary>
    public Button PopButton => _pop;

    public void SetFloating(bool floating) => _pop.Content = floating ? "Dock" : "Pop out";

    /// <summary>Refreshes at once and cancels the pending debounced refresh.</summary>
    public void Flush() => _watch.Flush();

    /// <summary>Asks for a refresh (debounced); callable from any thread.</summary>
    public void RequestRefresh() => _watch.Request();

    /// <summary>How many times the panel has re-read the game (tests).</summary>
    public int ReloadCount { get; private set; }

    /// <summary>Highlights a checklist item (the Next Phase dialog jumps to it).</summary>
    public void SelectChecklist(int index)
    {
        if (index < 0 || index >= _checklist.Children.Count) return;
        _checklist.Children[index].Focus();
        _checklist.Children[index].BringIntoView();
    }

    public void Detach() => _watch.Dispose();

    // ---------- reading (tests) ----------

    public string NextText => _next.Content as string ?? "";
    public bool NextEnabled => _next.IsEnabled;
    public string? NextTip => _nextTip;
    public string TitleText => _title.Text ?? "";
    public string InstructionsText => _instructions.Text ?? "";
    public string CountText => _count.Text ?? "";
    public bool CountRed => _count.Foreground is ISolidColorBrush { Color: var c } && c == Colors.Red;
    public bool AckEnabled => _ack.IsEnabled;
    public string NoPhasesLabelText => _none.Text ?? "";
    public bool ContentVisible => _body.IsVisible;
    public string? RuleText => _ref.IsVisible ? _ref.Text : null;
    public IReadOnlyList<string> ChecklistTexts => _checklist.Children.OfType<CheckBox>().Select(b => b.Content as string ?? "").ToList();
    public IReadOnlyList<string> WarningTexts => _warnings.Items.OfType<TreeViewItem>().Select(i => i.Header as string ?? "").ToList();
    public IReadOnlyList<string> OpenDecisionTexts => _open.Items.OfType<string>().ToList();
    public bool IsChecked(int i) => _checklist.Children[i] is CheckBox { IsChecked: true };

    /// <summary>Ticks or unticks a checklist item as the user would (tests).</summary>
    public void SetChecked(int i, bool on) => ((CheckBox)_checklist.Children[i]).IsChecked = on;

    public void SelectWarning(int i) => _warnings.SelectedItem = _warnings.Items[i];

    public void ClickAcknowledge() { if (_ack.IsEnabled) _ack.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }

    /// <summary>Double-clicks the open decision at this row (tests).</summary>
    public void ActivateOpen(int i) { _open.SelectedIndex = i; OpenSelected(); }

    // ---------- rendering ----------

    public void Reload()
    {
        ReloadCount++;
        _updating = true;
        try
        {
            var v = _c.View();
            _view = v;
            var show = !v.NoPhases;
            _none.IsVisible = !show;
            _body.IsVisible = show;
            // Hot-seat: during another side's phase only that side's player may advance.
            // A received turn replaying: nothing here may change the game until it is done.
            var replaying = _s.IsReplaying;
            var text = PhaseTexts.Panel(v, _s.ActorSide(), replaying, _s.Rules);
            _next.IsEnabled = text.NextEnabled;
            _nextTip = show ? (replaying ? ModuleSession.ReplayingText : v.PlayingText) : null;
            ToolTip.SetTip(_next, _nextTip);
            _checklist.IsEnabled = !replaying;
            _next.Content = text.NextText;
            if (!show) { _count.Text = text.CountText; return; }
            _title.Text = text.Title;
            // Hot-seat: another side's phase shows none of its instructions, notes or rule reference.
            var mine = v.PlayingText == null;
            _instructions.Text = mine ? (v.Current?.Instructions ?? "").Replace("\r\n", "\n") : "";
            ToolTip.SetTip(_title, mine && !string.IsNullOrWhiteSpace(v.Current?.Notes) ? v.Current!.Notes : null);
            _ref.IsVisible = text.RuleText != null;
            _ref.Text = text.RuleText ?? "";
            ShowWarnings(v, text.Warnings);
            ShowDecisions(v, text.ChecklistHidden);
            _count.Text = text.CountText;
            _count.Foreground = text.CountRed ? Brushes.Red : null;
            _count.IsVisible = !v.BeforeStart;
        }
        finally { _updating = false; }
    }

    void ShowWarnings(PhaseView v, IReadOnlyList<WarningLine> lines)
    {
        var open = _warnings.Items.OfType<TreeViewItem>().Where(n => n.IsExpanded && n.Tag is int).Select(n => (int)n.Tag!).ToHashSet();
        var selIndex = (_warnings.SelectedItem as TreeViewItem)?.Tag as int?;
        _warnings.SelectedItem = null;
        _warnings.Items.Clear();
        TreeViewItem? reselect = null;
        for (var k = 0; k < v.Warnings.Count; k++)
        {
            var w = v.Warnings[k];
            var node = new TreeViewItem { Header = lines[k].Text, Tag = w.Index, IsExpanded = open.Contains(w.Index) };
            for (var i = 0; i < w.UnitTexts.Count; i++)
                node.Items.Add(new TreeViewItem { Header = w.UnitTexts[i], Tag = i < w.PieceIds.Count ? w.PieceIds[i] : null });
            _warnings.Items.Add(node);
            if (selIndex == w.Index) reselect = node;
        }
        if (reselect != null) _warnings.SelectedItem = reselect;
        UpdateAckEnabled();
    }

    void ShowDecisions(PhaseView v, bool checklistHidden)
    {
        // Hot-seat: another side's phase shows none of its decisions (its checklist included).
        var items = checklistHidden ? new List<string>() : v.Current?.Checklist ?? new List<string>();
        var ticked = _s.View?.Ticked;
        var boxes = _checklist.Children.OfType<CheckBox>().ToList();
        if (boxes.Count == items.Count && boxes.Count == _checklist.Children.Count && boxes.Select(b => b.Content as string).SequenceEqual(items.Select(t => t ?? "")))
        {
            // The same items: update the boxes in place (Reload holds _updating, so this ticks nothing) and keep the keyboard focus on the one
            // just ticked, as the WinForms CheckedListBox does.
            for (var i = 0; i < boxes.Count; i++) boxes[i].IsChecked = ticked?.Contains(i) == true;
        }
        else
        {
            ShowChecklist(items, ticked);
        }
        _openRows = v.Decisions.Where(d => d.Kind != DecisionKind.Checklist).ToList();
        var sel = _open.SelectedIndex;
        _open.ItemsSource = _openRows.Select(d => d.Text).ToList();
        if (sel >= 0 && sel < _openRows.Count) _open.SelectedIndex = sel;
    }

    void ShowChecklist(List<string> items, IReadOnlyCollection<int>? ticked)
    {
        _checklist.Children.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            var index = i;
            var box = new CheckBox { Content = items[i] ?? "", IsChecked = ticked?.Contains(i) == true };
            box.IsCheckedChanged += (_, _) => OnItemCheck(index, box.IsChecked == true);
            _checklist.Children.Add(box);
        }
    }

    // ---------- actions ----------

    WarningHit? SelectedWarning()
    {
        if (_warnings.SelectedItem is not TreeViewItem n) return null;
        if (n.Parent is TreeViewItem p) n = p;
        return n.Tag is int index ? _view?.Warnings.FirstOrDefault(w => w.Index == index) : null;
    }

    void UpdateAckEnabled() => _ack.IsEnabled = PhaseTexts.CanAck(SelectedWarning(), _s.IsReplaying);

    void AckSelected()
    {
        if (SelectedWarning() is { IsDecision: true, Acknowledged: false } w) _actions().Ack(w.Index);
        _watch.Flush();
    }

    void OnItemCheck(int index, bool on)
    {
        if (_updating) return;
        _actions().Tick(index, on);
        _watch.Request();
    }

    void OpenSelected()
    {
        var i = _open.SelectedIndex;
        if (i < 0 || i >= _openRows.Count) return;
        DecisionActivated?.Invoke(_openRows[i]);
    }
}
