using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Quartermaster.Core;
using Quartermaster.Vassal;
using Quartermaster.Desktop.Map;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;
using CoreLedger = Quartermaster.Core.Ledger;

namespace Quartermaster.Desktop.Ledgers;

/// <summary>
/// The Ledger (WinForms LedgerWindow): holders and units with their ledger values, one column per property, split into tabs (Units, Depots &amp;
/// Global, Everything and the player's own tabs, see LedgerWindow.Tabs.cs). Unit rows can show their counter (LedgerWindow.Counters.cs). Value
/// cells are editable (with a required note). The rows are virtualised: only those on screen are built. Rows, values and pictures come only
/// from the player's view (<see cref="ModuleSession.View"/>). Owned by the main window, not modal, one per module.
/// </summary>
public sealed partial class LedgerWindow : Window
{
    const int FixedCols = LedgerViewRules.FixedCols;   // Name, Kind
    const double NameWidth = 180, KindWidth = 140, ValueWidth = 90, MinColumnWidth = 20;
    static readonly IBrush GroupBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE3, 0xE3));     // SystemColors.ControlLight
    static readonly IBrush NegativeBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00));  // Color.Red

    readonly ModuleSession _s;
    readonly IDialogs _dialogs;
    readonly Func<IActionPort?>? _actions;
    readonly Action<string>? _centre;
    readonly WrapPanel _bar = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };   // wraps onto a second line when narrow
    readonly TextBox _filter = new() { Width = 160, Name = "LedgerFilter" };
    readonly Button _export = Plain("Export CSV…", "Save the current tab's rows and columns as a CSV file");
    readonly ToggleButton _showEmpty = Toggle("Show units without values", null);
    readonly Button _refresh = Plain("Refresh", null);
    readonly Grid _header = new();
    readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple, Name = "LedgerRows", Padding = default };
    readonly DockPanel _table = new() { LastChildFill = true };
    readonly ScrollViewer _hscroll;
    readonly BreakdownTip _tip = new();
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly List<LedgerRowView> _live = new();
    IReadOnlyList<PropertyDef> _cols = Array.Empty<PropertyDef>();
    List<LedgerRowEx> _rows = new();
    List<RowItem> _items = new();
    LedgerTabResult _result = new(Array.Empty<PropertyDef>(), Array.Empty<LedgerRowEx>(), 0, null);
    double[] _widths = { NameWidth, KindWidth };
    string _colSignature = "";
    int _sortCol = -1;
    bool _sortAsc = true;
    GameSession? _game;
    bool _closing;
    Editing? _editing;
    (int Row, int Col) _currentCell = (-1, -1);
    int _reloads;

    /// <summary>One row of the list: a fresh object per reload, so the list always rebuilds what it shows.</summary>
    internal sealed record RowItem(int Index, LedgerRowEx Data);

    sealed record Editing(LedgerRowView Row, int Col, TextBox Box);

    internal LedgerWindow(ModuleSession s, IDialogs dialogs, Func<IActionPort?>? actions = null, Action<string>? centreOnPiece = null)
    {
        _s = s;
        _dialogs = dialogs;
        _actions = actions;
        _centre = centreOnPiece;
        Title = "Ledger";
        Width = 820;
        Height = 560;
        ShowInTaskbar = false;
        Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0)), new Setter(MinHeightProperty, 0d) },
        });

        ToolTip.SetTip(_filter, "Filter by name or kind");
        _bar.Children.Add(Label("Filter:"));
        _bar.Children.Add(_filter);
        _bar.Children.Add(Sep());
        AddTabToolbar();
        _bar.Children.Add(Sep());
        _bar.Children.Add(_export);
        _bar.Children.Add(_showEmpty);
        _bar.Children.Add(_refresh);

        _list.ItemTemplate = new FuncDataTemplate<RowItem>((r, _) => r == null ? new Panel() : new LedgerRowView(this, r), supportsRecycling: false);
        _list.KeyDown += (_, e) => Run(() => OnListKey(e));
        _list.AddHandler(TextInputEvent, (_, e) => Run(() => OnListText(e)), RoutingStrategies.Bubble);
        _list.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => { _tip.Hide(); HidePreview(); }, RoutingStrategies.Bubble, handledEventsToo: true);
        _list.AddHandler(PointerPressedEvent, (_, _) => HidePreview(), RoutingStrategies.Tunnel, handledEventsToo: true);
        _list.PointerExited += (_, _) => { _tip.Hide(); HidePreview(); };
        DockPanel.SetDock(_header, Dock.Top);
        _table.Children.Add(_header);
        _table.Children.Add(_list);
        _hscroll = new ScrollViewer
        {
            Content = _table, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _hscroll.ScrollChanged += (_, _) => { _tip.Hide(); HidePreview(); };

        var root = new DockPanel();
        var top = new StackPanel();
        top.Children.Add(_bar);
        top.Children.Add(_tabStrip);
        top.Children.Add(_banner);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_hscroll);
        Content = root;
        InitCounters();

        _filter.TextChanged += (_, _) => Kick();
        _showEmpty.IsCheckedChanged += (_, _) => Kick();
        _refresh.Click += (_, _) => Run(Reload);
        _export.Click += (_, _) => RunAsync(ExportCsvAsync);
        _debounce.Tick += (_, _) => { _debounce.Stop(); Run(Reload); };
        Deactivated += (_, _) => { _tip.Hide(); HidePreview(); };

        s.RulesChanged += OnRulesChanged;
        s.GameReplaced += OnGameReplaced;
        s.ViewChanged += OnViewChanged;
        LedgerTabStore.Changed += OnStoreChanged;
        HookGame();
        Run(InitTabs);
        Run(Reload);
    }

    static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 3, 0) };

    static Border Sep()
    {
        var b = new Border { Width = 1, Margin = new Thickness(4, 3), VerticalAlignment = VerticalAlignment.Stretch };
        b.Bind(Border.BackgroundProperty, b.GetResourceObservable("Classic.BorderBrush"));
        return b;
    }

    static Button Plain(string text, string? tip)
    {
        var b = new Button { Content = text, Focusable = false, Margin = new Thickness(1, 0) };
        if (tip != null) ToolTip.SetTip(b, tip);
        return b;
    }

    static ToggleButton Toggle(string text, string? tip)
    {
        var b = new ToggleButton { Content = text, Focusable = false, Margin = new Thickness(1, 0) };
        if (tip != null) ToolTip.SetTip(b, tip);
        return b;
    }

    void Kick() { _debounce.Stop(); _debounce.Start(); }

    void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("ledger window failed: " + ex);
            _s.Log.Add(DiagnosticSeverity.Error, "ledger", "The ledger window failed: " + ex.Message);
        }
    }

    async void RunAsync(Func<Task> a)
    {
        try { await a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("ledger window failed: " + ex);
            _s.Log.Add(DiagnosticSeverity.Error, "ledger", "The ledger window failed: " + ex.Message);
        }
    }

    /// <summary>Session and game events may come from any thread: the work runs on the UI thread, never after the window closed.</summary>
    void OnUi(Action a) => Dispatcher.UIThread.Post(() => { if (!_closing) Run(a); });

    void HookGame()
    {
        if (_game != null) { _game.Changed -= OnGameChanged; _game.Rewound -= OnRewound; }
        _game = _s.Game;
        if (_game != null) { _game.Changed += OnGameChanged; _game.Rewound += OnRewound; }
    }

    void OnGameChanged(GameCommand _) => OnUi(Kick);
    void OnRewound() => OnUi(Kick);
    void OnGameReplaced() => OnUi(() => { HookGame(); Kick(); });
    void OnRulesChanged() => OnUi(() => { RefreshSides(); Kick(); });
    void OnViewChanged() => OnUi(Kick);   // debounced: a burst of changes reloads once

    protected override void OnClosed(EventArgs e)
    {
        Run(FlushPrefs);
        _closing = true;
        _debounce.Stop();
        _saveTimer.Stop();
        _s.RulesChanged -= OnRulesChanged;
        _s.GameReplaced -= OnGameReplaced;
        _s.ViewChanged -= OnViewChanged;
        LedgerTabStore.Changed -= OnStoreChanged;
        if (_game != null) { _game.Changed -= OnGameChanged; _game.Rewound -= OnRewound; }
        _game = null;
        _tip.Hide();
        StopRendering();
        base.OnClosed(e);
    }

    // ---------- what the main window, Task 5 and tests read ----------

    /// <summary>The key of the tab shown ("units", "holders", "all", "user:{name}").</summary>
    public string CurrentTabKey => _tab.Key;

    /// <summary>The rows shown, in display order (group headers included).</summary>
    internal IReadOnlyList<LedgerRowEx> Rows => _rows;

    /// <summary>The column headers: Name, Kind, then the properties.</summary>
    internal IReadOnlyList<string> Headers => new[] { "Name", "Kind" }.Concat(_cols.Select(c => c.Name)).ToList();

    /// <summary>A header's tooltip (the property's notes; none for Name and Kind).</summary>
    internal string? HeaderTip(int col) => col < _header.Children.Count ? ToolTip.GetTip(_header.Children[col]) as string : null;

    internal IReadOnlyList<double> ColumnWidths => _widths;
    internal TextBox FilterBox => _filter;
    internal ToggleButton ShowEmptyButton => _showEmpty;
    internal Button ExportButton => _export;
    internal Button RefreshButton => _refresh;
    internal string? TipText => _tip.Text;
    internal Control? TipControl => _tip.Shown;
    internal TextBox? Editor => _editing?.Box;
    /// <summary>Number of reloads so far (tests).</summary>
    internal int Reloads => _reloads;
    /// <summary>The row controls built now (virtualised: only those on screen).</summary>
    internal int LiveRowCount => _live.Count;

    /// <summary>The text a cell shows (from the rows, whether built or not).</summary>
    internal string CellText(int row, int col) => row < 0 || row >= _rows.Count ? "" : TextOf(_rows[row], col);

    /// <summary>The control of a row on screen (null when it is not built).</summary>
    internal Control? RowControl(int row) => Live(row);

    /// <summary>A cell's bounds in window coordinates (the row must be on screen).</summary>
    internal Rect CellBounds(int row, int col) => BoundsIn(Need(row).Cell(col));

    /// <summary>True when a value cell shows its text in red (negative values).</summary>
    internal bool CellIsRed(int row, int col) => ReferenceEquals(Need(row).TextBlock(col)?.Foreground, NegativeBrush);

    /// <summary>True when the row is drawn as a bold group header.</summary>
    internal bool IsBoldRow(int row) => Need(row).TextBlock(0)?.FontWeight == FontWeight.Bold;

    internal double RowHeightOf(int row) => Need(row).Bounds.Height;

    /// <summary>Runs a pending (debounced) reload now (tests).</summary>
    internal void Flush()
    {
        Dispatcher.UIThread.RunJobs();
        if (_debounce.IsEnabled) { _debounce.Stop(); Run(Reload); }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Brings a row on screen (tests).</summary>
    internal void ScrollToRow(int row)
    {
        if (row >= 0 && row < _items.Count) _list.ScrollIntoView(_items[row]);
        _list.UpdateLayout();
    }

    /// <summary>The keys of the selected rows.</summary>
    public IReadOnlyCollection<string> SelectedRowKeys => SelectedKeys();

    LedgerRowView? Live(int row) => _live.FirstOrDefault(r => r.Index == row);
    LedgerRowView Need(int row) => Live(row) ?? throw new InvalidOperationException("Row is not on screen.");
    Rect BoundsIn(Control c) => new(c.TranslatePoint(default, this) ?? default, c.Bounds.Size);

    // ---------- data ----------

    /// <summary>Reads the rows of the current tab again from the viewed state (keeps the selection and the first row shown).</summary>
    public void Reload()
    {
        if (_closing) return;
        if (_editing != null) { Kick(); return; }   // never pull the cell out from under the user
        _reloads++;
        var pack = _s.Rules;
        var game = _s.Game;
        var keepKeys = SelectedKeys();
        var firstKey = FirstVisibleKey();

        _result = pack == null || game == null || _s.View is not { } view
            ? new LedgerTabResult(pack == null ? Array.Empty<PropertyDef>() : LedgerTabs.ColumnsFor(_tab, pack), Array.Empty<LedgerRowEx>(), 0, null)
            : LedgerTabs.Rows(_tab, view, pack, _s.Model, _filter.Text ?? "", SideFilter, Grouping, _showEmpty.IsChecked == true);
        _cols = _result.Columns;
        UpdateTabCounts(pack, game);
        ShowBanner(_result.FilterError);

        var sig = _tab.Key + "\u0002" + CountersOn + "\u0002" + string.Join("|", _cols.Select(c => c.Id + "\u0001" + c.Name + "\u0001" + c.Notes));
        if (sig != _colSignature) { _colSignature = sig; RebuildColumns(); }

        _rows = _result.Rows.ToList();
        ApplySort();
        _tip.Hide();
        HidePreview();   // the viewed state changed: pictures may differ
        ShowRows(keepKeys, firstKey);
    }

    void ShowRows(HashSet<string> keepKeys, string? firstKey)
    {
        _items = _rows.Select((r, i) => new RowItem(i, r)).ToList();
        _list.ItemsSource = _items;
        Restore(keepKeys, firstKey);
    }

    static string TextOf(LedgerRowEx r, int col) => col switch
    {
        0 => r.Row.Name,
        1 => r.Row.Kind,
        _ when col - FixedCols < r.Row.Values.Count && r.Row.Values[col - FixedCols] is { } v => LedgerTable.FormatNumber(v),
        _ => "",
    };

    bool _rebuildingColumns;

    /// <summary>The header row and the column widths: Name (with the picture's room), Kind, then one right-aligned column per property.</summary>
    void RebuildColumns()
    {
        _rebuildingColumns = true;
        try
        {
            _widths = new[] { NameWidth + NameImagePad, KindWidth }.Concat(_cols.Select(_ => ValueWidth)).ToArray();
            ApplyColumnWidths();
            if (_sortCol >= _widths.Length) _sortCol = -1;
            _header.Children.Clear();
            _header.ColumnDefinitions = Columns(_widths);
            var names = Headers;
            for (var c = 0; c < names.Count; c++) _header.Children.Add(HeaderCell(c, names[c], c >= FixedCols ? _cols[c - FixedCols].Notes : null));
            UpdateSortGlyphs();
            _table.Width = _widths.Sum() + 24;
        }
        finally { _rebuildingColumns = false; }
    }

    internal static ColumnDefinitions Columns(IReadOnlyList<double> widths)
    {
        var d = new ColumnDefinitions();
        foreach (var w in widths) d.Add(new ColumnDefinition(w, GridUnitType.Pixel));
        return d;
    }

    Control HeaderCell(int col, string text, string? notes)
    {
        var label = new TextBlock { Text = text, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var glyph = new TextBlock { Text = "", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), FontSize = 9, Tag = "glyph" };
        var grip = new Border
        {
            Width = 5, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Right, Cursor = new Cursor(StandardCursorType.SizeWestEast),
        };
        var content = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(glyph, Dock.Right);
        content.Children.Add(glyph);
        content.Children.Add(label);
        var inner = new Border { Padding = new Thickness(4, 2), Child = content, Background = Brushes.Transparent };
        var cell = new Border { BorderThickness = new Thickness(0, 0, 1, 1), Child = new Panel { Children = { inner, grip } } };
        cell.Bind(Border.BorderBrushProperty, cell.GetResourceObservable("Classic.BorderBrush"));
        cell.Bind(Border.BackgroundProperty, cell.GetResourceObservable("Classic.FaceBrush"));
        if (!string.IsNullOrEmpty(notes)) ToolTip.SetTip(cell, notes);
        Grid.SetColumn(cell, col);
        // A click on the header sorts by that column; dragging its right edge resizes it (WinForms DataGridView).
        inner.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) Run(() => SortBy(col)); };
        double? dragFrom = null;
        double startWidth = 0;
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            dragFrom = e.GetPosition(this).X;
            startWidth = _widths[col];
            e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) => { if (dragFrom is { } x0) Run(() => SetColumnWidth(col, startWidth + e.GetPosition(this).X - x0)); };
        grip.PointerReleased += (_, e) => { dragFrom = null; e.Pointer.Capture(null); e.Handled = true; };
        grip.PointerCaptureLost += (_, _) => dragFrom = null;
        return cell;
    }

    /// <summary>Sets a column's width as dragging its header edge does (at least 20 px); remembered per tab.</summary>
    internal void SetColumnWidth(int col, double width)
    {
        if (col < 0 || col >= _widths.Length) return;
        width = Math.Max(MinColumnWidth, Math.Round(width));
        if (_widths[col] == width) return;
        _widths[col] = width;
        _header.ColumnDefinitions[col].Width = new GridLength(width, GridUnitType.Pixel);
        foreach (var r in _live) r.ApplyWidths();
        _table.Width = _widths.Sum() + 24;
        OnColumnWidthChanged();
    }

    HashSet<string> SelectedKeys()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in _list.SelectedItems ?? Array.Empty<object>())
            if (o is RowItem r) set.Add(r.Data.Row.Key);
        return set;
    }

    string? FirstVisibleKey()
    {
        if (_live.Count == 0) return null;
        var first = _live.Where(r => r.Index < _rows.Count && r.Bounds.Height > 0 && IsOnScreen(r)).Select(r => r.Index).DefaultIfEmpty(-1).Min();
        return first >= 0 && first < _rows.Count ? _rows[first].Row.Key : null;
    }

    bool IsOnScreen(LedgerRowView r)
    {
        var p = r.TranslatePoint(default, _list);
        return p is { } at && at.Y + r.Bounds.Height > 0;
    }

    /// <summary>Selects the rows of <paramref name="keys"/> and puts the row of <paramref name="firstKey"/> at the top of the list.</summary>
    void Restore(HashSet<string> keys, string? firstKey)
    {
        if (_items.Count == 0) return;
        try
        {
            _list.SelectedItems?.Clear();
            foreach (var it in _items) if (keys.Contains(it.Data.Row.Key)) _list.SelectedItems?.Add(it);
            if (firstKey != null)
            {
                var at = _items.FindIndex(r => r.Data.Row.Key == firstKey);
                if (at >= 0) ScrollToTop(at);
            }
        }
        catch (InvalidOperationException) { }
        catch (ArgumentOutOfRangeException) { }
    }

    /// <summary>Scrolls so the row is the first one shown (WinForms FirstDisplayedScrollingRowIndex).</summary>
    void ScrollToTop(int row)
    {
        if (_list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } sv) { _list.ScrollIntoView(_items[row]); return; }
        double y = 0;
        for (var i = 0; i < row && i < _rows.Count; i++) y += RowHeightFor(_rows[i]);
        _list.UpdateLayout();
        sv.Offset = new Vector(sv.Offset.X, y);
    }

    void SortBy(int col)
    {
        if (col < 0 || col >= _widths.Length) return;
        if (_sortCol == col) _sortAsc = !_sortAsc; else { _sortCol = col; _sortAsc = true; }
        var keys = SelectedKeys();
        var first = FirstVisibleKey();
        ApplySort();
        ShowRows(keys, first);
    }

    /// <summary>Sorts by the chosen column within each group: group header rows stay where they are and keep their rows under them.</summary>
    void ApplySort()
    {
        UpdateSortGlyphs();
        if (_sortCol < 0 || _sortCol >= _widths.Length) return;
        _rows = LedgerViewRules.Sort(_rows, _sortCol, _sortAsc).ToList();
    }

    void UpdateSortGlyphs()
    {
        for (var c = 0; c < _header.Children.Count; c++)
            foreach (var g in _header.Children[c].GetLogicalDescendants().OfType<TextBlock>().Where(t => Equals(t.Tag, "glyph")))
                g.Text = c == _sortCol ? (_sortAsc ? "▲" : "▼") : "";
    }

    /// <summary>The sort glyph of a header ("▲", "▼" or "") (tests).</summary>
    internal string SortGlyph(int col) =>
        col < _header.Children.Count ? _header.Children[col].GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => Equals(t.Tag, "glyph"))?.Text ?? "" : "";

    /// <summary>Clicks a column header (tests).</summary>
    internal void ClickHeader(int col) => Run(() => SortBy(col));

    Breakdown? BreakdownFor(int rowIndex, int colIndex)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count || colIndex < FixedCols || colIndex - FixedCols >= _cols.Count || _rows[rowIndex].IsGroupHeader) return null;
        if (_s.Game is not { } game) return null;
        return LedgerBreakdown.For(_s.View ?? game.State, _s.Rules, _s.Model, _rows[rowIndex].Row.Key, _cols[colIndex - FixedCols].Id);
    }

    /// <summary>Hovering a value cell shows its breakdown under the cursor (WinForms BreakdownTip.Track: 12 px right, 18 px down).</summary>
    void HoverCell(int row, int col, Point inWindow)
    {
        if (_closing || BreakdownFor(row, col) is not { } b) { _tip.Hide(); return; }
        if (_tip.Text == LedgerBreakdown.Format(b)) return;
        _tip.Show(this, new Point(inWindow.X + 12, inWindow.Y + 18), b);
    }

    // ---------- manual edit ----------

    /// <summary>The value cell F2 or a keystroke edits: the last clicked cell (a value column), else the selected row's first value.</summary>
    (int Row, int Col)? EditTarget()
    {
        var row = _currentCell.Row >= 0 && _currentCell.Row < _rows.Count ? _currentCell.Row : _list.SelectedIndex;
        var col = _currentCell.Col >= FixedCols ? _currentCell.Col : FixedCols;
        return row < 0 ? null : (row, col);
    }

    void OnListKey(KeyEventArgs e)
    {
        if (e.Key != Key.F2 || _editing != null || EditTarget() is not { } at) return;
        BeginEdit(at.Row, at.Col);
        e.Handled = true;
    }

    /// <summary>Typing on the grid starts editing the current value cell with the typed text (WinForms EditOnKeystrokeOrF2).</summary>
    void OnListText(TextInputEventArgs e)
    {
        if (_editing != null || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl) || EditTarget() is not { } at) return;
        if (BeginEdit(at.Row, at.Col, e.Text)) e.Handled = true;
    }

    /// <summary>Starts editing a value cell (double-click or F2, as the WinForms grid); holder, group and fixed cells are not editable.</summary>
    internal bool BeginEdit(int row, int col, string? typed = null)
    {
        if (_closing || _editing != null || row < 0 || row >= _rows.Count || col < FixedCols || col - FixedCols >= _cols.Count) return false;
        if (_rows[row].IsGroupHeader) return false;
        ScrollToRow(row);
        if (Live(row) is not { } view) return false;
        var box = view.BeginEdit(col, typed ?? CellText(row, col));
        if (box == null) return false;
        _editing = new Editing(view, col, box);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; EndEdit(true); }
            else if (e.Key == Key.Escape) { e.Handled = true; EndEdit(false); }
        };
        box.LostFocus += (_, _) => EndEdit(true);
        box.Focus();
        if (typed == null) box.SelectAll();
        else box.CaretIndex = typed.Length;   // the keystroke replaces the value, caret after it
        return true;
    }

    /// <summary>Ends the edit: an empty entry cancels; a commit asks for the note once the edit has finished.</summary>
    internal void EndEdit(bool commit)
    {
        if (_editing is not { } ed) return;
        _editing = null;
        var text = (ed.Box.Text ?? "").Trim();
        ed.Row.EndEdit(ed.Col);
        _list.Focus();
        if (!commit || text.Length == 0) return;   // empty input cancels
        var data = ed.Row.Data;
        if (data.IsGroupHeader || ed.Col - FixedCols >= _cols.Count) return;
        var key = data.Row.Key;
        var prop = _cols[ed.Col - FixedCols];
        Dispatcher.UIThread.Post(() => { if (!_closing) RunAsync(() => CommitManualAsync(key, prop, text)); });
    }

    async Task CommitManualAsync(string key, PropertyDef prop, string text)
    {
        var game = _s.Game;
        var check = LedgerViewRules.CheckManualEdit(_s, key, prop.Id, game == null ? null : CoreLedger.Get(game.State, key, prop.Id), text, prop.Name);
        if (check.Error != null) await WarnAsync(check.Error);
        if (_closing) return;
        if (check.Reload) Reload();
        if (!check.Ok || check.Unchanged || game == null) return;   // unchanged: no history entry
        var after = check.Value;
        var note = await _dialogs.AskTextAsync(this, "Manual change", check.NotePrompt!, "");
        if (_closing) return;
        if (string.IsNullOrWhiteSpace(note)) { Reload(); return; }
        if (_actions?.Invoke() is { } port) { port.EditLedger(key, prop.Id, after, note); return; }
        var before = CoreLedger.GetOrDefault(game.State, key, prop);
        game.Execute(CommandLog.Stamp(new LedgerDelta(key, prop.Id, before, after, "manual", null, null, note.Trim(), null)));
    }

    Task WarnAsync(string message) => _dialogs.ShowWarningAsync(this, message, "Ledger");

    // ---------- export ----------

    /// <summary>Export CSV…: the current tab's rows as shown (sorted, group headers left out), UTF-8 with a byte-order mark.</summary>
    internal async Task ExportCsvAsync()
    {
        var path = await _dialogs.SaveFileAsync(this, "Save As", new FileFilter("CSV files (*.csv)", "*.csv"), "csv", LedgerViewRules.CsvFileName(_tab.Title));
        if (path == null || _closing) return;
        var shown = _result with { Rows = _rows };
        try { File.WriteAllText(path, LedgerTabs.ToCsv(shown), new UTF8Encoding(true)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await WarnAsync("Could not save the file: " + ex.Message); }
    }

    // ---------- Show in Ledger and holder rows (used by Task 5) ----------

    /// <summary>Selects and scrolls to the row of a holder (used by the alert bar). Moves to the Depots &amp; Global tab and clears the filter when
    /// the row is not shown.</summary>
    public void SelectHolder(string holderId)
    {
        Run(() =>
        {
            var key = LedgerKey.Holder(holderId);
            if (_rows.FindIndex(r => r.Row.Key == key) < 0)
            {
                if (_tab.Kind is not (LedgerTabKind.Holders or LedgerTabKind.Everything)) SelectTab("holders");
                if (!string.IsNullOrEmpty(_filter.Text)) _filter.Text = "";
                _debounce.Stop();
                Reload();
            }
            Restore(new HashSet<string>(StringComparer.Ordinal) { key }, key);
        });
    }

    /// <summary>The plan of the last <see cref="ShowPieces"/> (its row keys and message).</summary>
    public LedgerJumpPlan? LastJump { get; private set; }

    /// <summary>
    /// Show in Ledger: moves to the tab that shows the counters' rows (<see cref="LedgerJump"/>), clearing the text filter when it hides them,
    /// then selects those rows and scrolls to the first one shown. Only rows of the player's view. Returns null when every counter was shown,
    /// else the message that tells what was not (or why nothing could be shown; LedgerJump.Plan's Message is null exactly when none is missing); <see cref="LastJump"/> has the whole plan.
    /// </summary>
    public string? ShowPieces(IReadOnlyList<string> pieceIds)
    {
        if (_s.Rules is not { } pack || _s.Game == null || _s.View is not { } view)
        {
            LastJump = new LedgerJumpPlan(null, false, Array.Empty<string>(), pieceIds.Count, "The Ledger needs a game with a rules pack.");
            return LastJump.Message;
        }
        if (_editing != null)
        {
            LastJump = new LedgerJumpPlan(null, false, Array.Empty<string>(), pieceIds.Count, "Finish editing the Ledger cell first.");
            return LastJump.Message;
        }
        var plan = LedgerJump.Plan(pieceIds, _tabDefs, _tab.Key, view, pack, _s.Model, _filter.Text ?? "", _prefs.Side, _showEmpty.IsChecked == true, _s.CanSee);
        LastJump = plan;
        if (plan.TabKey == null) return plan.Message;
        if (plan.ClearFilter && !string.IsNullOrEmpty(_filter.Text)) _filter.Text = "";
        if (plan.TabKey != _tab.Key) SelectTab(plan.TabKey);   // reloads
        else Reload();
        _debounce.Stop();   // the cleared filter's pending reload is done
        var keys = plan.RowKeys.ToHashSet(StringComparer.Ordinal);
        var first = _rows.FindIndex(r => keys.Contains(r.Row.Key));
        if (first < 0) return plan.Message;
        _currentCell = (first, 0);   // WinForms: keyboard focus on the first row (CurrentCell), which also scrolls it into view
        _list.Focus();
        Restore(keys, _rows[first].Row.Key);
        return plan.Message;
    }

    // ---------- rows ----------

    /// <summary>One row on screen: a cell per column, the counter picture at the left of the Name cell (unit rows, Show counters on).</summary>
    internal sealed partial class LedgerRowView : Grid
    {
        readonly LedgerWindow _w;
        readonly Border[] _cells;
        readonly TextBlock[] _texts;
        public int Index { get; }
        public LedgerRowEx Data { get; }

        public LedgerRowView(LedgerWindow w, RowItem item)
        {
            _w = w;
            Index = item.Index;
            Data = item.Data;
            Height = w.RowHeightFor(Data);
            ColumnDefinitions = Columns(w._widths);
            var n = w._widths.Length;
            _cells = new Border[n];
            _texts = new TextBlock[n];
            if (Data.IsGroupHeader) Background = GroupBrush;
            for (var c = 0; c < n; c++)
            {
                var col = c;
                var text = new TextBlock
                {
                    Text = TextOf(Data, c), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = c >= FixedCols ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    FontWeight = Data.IsGroupHeader ? FontWeight.Bold : FontWeight.Normal,
                };
                if (c >= FixedCols && c - FixedCols < Data.Row.Values.Count && Data.Row.Values[c - FixedCols] is < 0) text.Foreground = NegativeBrush;
                var cell = new Border { BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(4, 0), Background = Brushes.Transparent };
                cell.Bind(Border.BorderBrushProperty, cell.GetResourceObservable("Classic.BorderBrush"));
                cell.Child = c == 0 ? NameCell(text) : text;
                SetColumn(cell, c);
                Children.Add(cell);
                _cells[c] = cell;
                _texts[c] = text;
                cell.PointerPressed += (_, _) => _w._currentCell = (Index, col);
                if (c >= FixedCols)
                {
                    cell.PointerEntered += (_, e) => _w.Run(() => _w.HoverCell(Index, col, e.GetPosition(_w)));
                    cell.PointerMoved += (_, e) => _w.Run(() => _w.HoverCell(Index, col, e.GetPosition(_w)));
                    cell.PointerExited += (_, _) => _w._tip.Hide();
                    cell.DoubleTapped += (_, e) => { e.Handled = true; _w.Run(() => _w.BeginEdit(Index, col)); };
                }
            }
            AttachedToVisualTree += (_, _) => { if (!_w._live.Contains(this)) _w._live.Add(this); UpdatePicture(); _w.PrioritiseShown(); };
            DetachedFromVisualTree += (_, _) => { _w._live.Remove(this); _w.PrioritiseShown(); };
        }

        public Control Cell(int col) => _cells[col];
        public TextBlock? TextBlock(int col) => col >= 0 && col < _texts.Length ? _texts[col] : null;

        public void ApplyWidths()
        {
            for (var c = 0; c < ColumnDefinitions.Count && c < _w._widths.Length; c++)
                ColumnDefinitions[c].Width = new GridLength(_w._widths[c], GridUnitType.Pixel);
        }

        public TextBox? BeginEdit(int col, string text)
        {
            if (col < FixedCols || col >= _cells.Length) return null;
            var box = new TextBox { Text = text, Padding = new Thickness(2, 0), VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Right, MinHeight = 0 };
            _cells[col].Child = box;
            return box;
        }

        public void EndEdit(int col)
        {
            if (col >= 0 && col < _cells.Length) _cells[col].Child = _texts[col];
        }
    }
}
