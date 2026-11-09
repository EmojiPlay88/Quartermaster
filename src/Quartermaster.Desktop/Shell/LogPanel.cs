using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// The game log (WinForms LogPanel): one row per <see cref="LogEntry"/> of the player's view (other sides' private lines redacted),
/// filterable by kind and text, copyable and exportable. The list is virtualised (a 10,000-row log stays fast), refreshes are debounced
/// 150 ms, and the view follows new rows unless the user scrolled up. Docks at the bottom or pops out into a floating window (the Pop out / Dock button).
/// </summary>
public sealed class LogPanel : UserControl
{
    const int TimeW = 64, KindW = 60, PhaseW = 100;
    static readonly FileFilter ExportFilter = new("Log files (*.txt;*.html)", "*.txt", "*.html");

    readonly ModuleSession _s;
    readonly Action<string> _centre;
    readonly IDialogs _dialogs;
    readonly Func<Window?> _owner;
    readonly Dictionary<string, ToggleButton> _kindButtons = new();
    readonly TextBox _search = new() { Width = 140, Name = "LogSearch" };
    readonly Button _copy = new() { Content = "Copy" };
    readonly Button _export = new() { Content = "Export log…" };
    readonly Button _pop = new() { Content = "Pop out", Name = "LogPop" };
    readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple, Name = "LogList" };
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly DockPanel _root = new();
    GameSession? _attached;
    IReadOnlyList<LogEntry> _rows = Array.Empty<LogEntry>();
    bool _showMoves = true;
    string _timeFormat = "HH:mm:ss";
    ChatBox? _chat;
    bool _disposed;

    public LogPanel(ModuleSession s, Action<string> centreOnPiece, IDialogs dialogs, Func<Window?> owner)
    {
        _s = s;
        _centre = centreOnPiece;
        _dialogs = dialogs;
        _owner = owner;
        Name = "LogPanel";

        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
        foreach (var kind in LogFilter.AllKinds)
        {
            var b = new ToggleButton { Content = LogFilter.KindLabel(kind), IsChecked = true, Margin = new Thickness(0, 0, 2, 0), Tag = kind };
            b.IsCheckedChanged += (_, _) => Schedule();
            _kindButtons[kind] = b;
            bar.Children.Add(b);
        }
        bar.Children.Add(new TextBlock { Text = "Search:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
        _search.TextChanged += (_, _) => Schedule();
        bar.Children.Add(_search);
        _copy.Margin = new Thickness(8, 0, 2, 0);
        _copy.Click += (_, _) => _ = CopyAsync();
        _export.Click += (_, _) => _ = ExportAsync();
        bar.Children.Add(_copy);
        bar.Children.Add(_export);
        _pop.Margin = new Thickness(8, 0, 0, 0);
        _pop.Click += (_, _) => PopToggleRequested?.Invoke();
        bar.Children.Add(_pop);
        DockPanel.SetDock(bar, Dock.Top);

        // Column header, as the WinForms details view.
        var header = Row("Time", "Kind", "Turn · Phase", "Text", bold: false);
        header.Margin = new Thickness(4, 0);
        DockPanel.SetDock(header, Dock.Top);

        _list.ItemTemplate = new FuncDataTemplate<LogEntry>((r, _) =>
        {
            if (r == null) return new TextBlock();
            var c = LogPanelRules.Cells(r, _timeFormat);
            return Row(c.Time, c.Kind, c.TurnPhase, c.Text, bold: false);
        });
        _list.DoubleTapped += (_, _) => Guard(CentreSelected);
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.C && e.KeyModifiers.HasFlag(ShortcutGestures.IsMac ? KeyModifiers.Meta : KeyModifiers.Control))
            {
                e.Handled = true;
                _ = CopyAsync();
            }
        };

        _root.Children.Add(bar);
        _root.Children.Add(header);
        _root.Children.Add(_list);
        Content = _root;

        _debounce.Tick += (_, _) => { _debounce.Stop(); Guard(Reload); };
        _s.GameReplaced += OnGameReplaced;
        _s.ViewChanged += Schedule;   // also a new viewer
        Attach();
        Reload();
    }

    /// <summary>Raised by the Pop out / Dock button; the host moves the panel.</summary>
    public event Action? PopToggleRequested;

    /// <summary>The Pop out / Dock button.</summary>
    public Button PopButton => _pop;

    /// <summary>Follows where the panel is: the button offers "Dock" while floating, "Pop out" while docked.</summary>
    public void SetFloating(bool floating) => _pop.Content = floating ? "Dock" : "Pop out";

    static Grid Row(string time, string kind, string phase, string text, bool bold)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions($"{TimeW},{KindW},{PhaseW},*") };
        void Cell(int col, string t)
        {
            var tb = new TextBlock { Text = t, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal, Margin = new Thickness(0, 0, 4, 0) };
            Grid.SetColumn(tb, col);
            g.Children.Add(tb);
        }
        Cell(0, time); Cell(1, kind); Cell(2, phase); Cell(3, text);
        return g;
    }

    // ---------- state shown ----------

    /// <summary>Settings: whether move/place/delete rows are listed (display only) and the time column format.</summary>
    public void ApplySettings(Settings s)
    {
        _showMoves = s.LogShowMoves;
        _timeFormat = s.LogTimeFormat;
        Reload();
    }

    /// <summary>The listed rows (after the kind, search and show-moves filters).</summary>
    public IReadOnlyList<LogEntry> Rows => _rows;

    /// <summary>The kind toggle buttons by kind.</summary>
    public IReadOnlyDictionary<string, ToggleButton> KindButtons => _kindButtons;

    public TextBox Search => _search;
    public ListBox List => _list;
    public Button CopyButton => _copy;
    public Button ExportButton => _export;

    /// <summary>The chat line under the log while the game is online; null otherwise.</summary>
    public ChatBox? Chat => _chat;

    /// <summary>Shows the chat line (online) with what sends a line, or removes it (null).</summary>
    public void SetChat(Action<string>? send)
    {
        if (_chat != null) { _root.Children.Remove(_chat); _chat = null; }
        if (send == null) return;
        _chat = new ChatBox(send);
        DockPanel.SetDock(_chat, Dock.Bottom);
        _root.Children.Insert(0, _chat);   // docks first: the list fills what is left above it
    }

    void Attach()
    {
        if (_attached != null) { _attached.Changed -= OnGameChanged; _attached.Rewound -= Schedule; }
        _attached = _s.Game;
        if (_attached != null) { _attached.Changed += OnGameChanged; _attached.Rewound += Schedule; }
    }

    void OnGameReplaced() { OnUi(Attach); Schedule(); }
    void OnGameChanged(GameCommand _) => Schedule();

    static void OnUi(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a(); else Dispatcher.UIThread.Post(a);
    }

    /// <summary>Schedules a debounced reload on the UI thread (events may arrive from any thread).</summary>
    void Schedule() => OnUi(() => { if (_disposed) return; _debounce.Stop(); _debounce.Start(); });

    /// <summary>Recomputes the visible rows now (tests do not wait for the debounce).</summary>
    public void Reload()
    {
        var log = _s.View?.Log ?? (IReadOnlyList<LogEntry>)Array.Empty<LogEntry>();   // the player's view: other sides' private lines redacted
        var follow = IsAtBottom();
        var selected = _list.Selection.SelectedItems.OfType<LogEntry>().Select(r => r.Seq).ToHashSet();   // by Seq: row indexes change
        var kinds = _kindButtons.Where(k => k.Value.IsChecked == true).Select(k => k.Key).ToHashSet();
        var rows = LogPanelRules.Visible(log, kinds, _search.Text, _showMoves);
        _rows = rows;
        _list.ItemsSource = rows;
        if (selected.Count > 0)
            for (var i = 0; i < rows.Count; i++)
                if (selected.Contains(rows[i].Seq)) _list.Selection.Select(i);
        if (follow && rows.Count > 0) _list.ScrollIntoView(rows.Count - 1);
    }

    /// <summary>True when the last row is in view (or the list is empty): new rows then scroll into view; a user who scrolled up is left alone.</summary>
    bool IsAtBottom()
    {
        if (_rows.Count == 0 || _list.Scroll is not ScrollViewer sv || sv.Viewport.Height <= 0) return true;
        return sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 1;
    }

    // ---------- actions ----------

    /// <summary>The selected rows in list order, or all listed rows when none is selected.</summary>
    public IReadOnlyList<LogEntry> SelectedOrAll()
    {
        var sel = _list.Selection.SelectedItems.OfType<LogEntry>().OrderBy(r => r.Seq).ToList();
        return sel.Count > 0 ? sel : _rows;
    }

    /// <summary>The text Copy puts on the clipboard: the selected rows (all when none) as the text export.</summary>
    public string CopyText() => LogFilter.ToText(SelectedOrAll());

    /// <summary>Copy: never throws (a clipboard failure is logged as a warning, as WinForms Guard).</summary>
    public async Task CopyAsync()
    {
        try
        {
            var text = CopyText();
            if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } cb) await cb.SetDataAsync(DataTransferFactory(text));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Warn(ex); }
    }

    static DataTransfer DataTransferFactory(string text)
    {
        var t = new DataTransfer();
        t.Add(DataTransferItem.CreateText(text));
        return t;
    }

    /// <summary>Export log…: asks for a file (text or HTML by extension, "game-log" suggested) and writes the listed rows.</summary>
    public async Task ExportAsync()
    {
        try
        {
            if (_owner() is not { } owner) return;
            var path = await _dialogs.SaveFileAsync(owner, LogPanelRules.ExportTitle, ExportFilter, "txt", LogPanelRules.ExportFileName);
            if (path == null) return;
            LogPanelRules.Export(path, _rows);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Warn(ex); }   // a locked or read-only path: warned in the diagnostics log, as WinForms
    }

    void CentreSelected()
    {
        if (_list.SelectedItem is LogEntry { PieceId: { Length: > 0 } id }) _centre(id);
    }

    void Guard(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Warn(ex); }
    }

    void Warn(Exception ex) => _s.Log.Add(DiagnosticSeverity.Warning, "log", "The log panel could not complete an action: " + ex.Message);

    /// <summary>Detaches from the session (the panel is going away).</summary>
    public void Detach()
    {
        _disposed = true;
        _debounce.Stop();
        _s.GameReplaced -= OnGameReplaced;
        _s.ViewChanged -= Schedule;
        if (_attached != null) { _attached.Changed -= OnGameChanged; _attached.Rewound -= Schedule; _attached = null; }
    }
}
