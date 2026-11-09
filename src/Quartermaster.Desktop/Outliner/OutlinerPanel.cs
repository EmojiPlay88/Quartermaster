using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Core;
using Quartermaster.Desktop.Map;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Outliner;

/// <summary>
/// The outliner (WinForms OutlinerPanel): the open "Now" decisions on top, then a tree-list of folders / groups / pieces with name, unit count,
/// the pack's outliner properties (aggregated per group) and alert dots. Numbers explain themselves in a <see cref="BreakdownTip"/>; column headers
/// show the property notes. It does not evaluate alerts itself: the owner feeds it the alert hub's (already debounced) results through
/// <see cref="AlertsChanged"/>, which is also its refresh signal after every game change. While the panel is not showing, a refresh is only
/// remembered and done when it shows again. Column widths are kept per column id across refreshes. Hosted docked at the right or in a floating
/// window; the same instance moves between them.
/// </summary>
public sealed class OutlinerPanel : UserControl
{
    const int IconCol = 0, NameCol = 1, UnitsCol = 2, FirstValueCol = 3;
    /// <summary>Counter picture size in the Name column (WinForms CounterCellImages).</summary>
    public static readonly PixelSize ImageSize = new(24, 18);
    const double RowHeight = 20;   // ImageSize.Height + 2, WinForms RowTemplate.Height

    sealed record Row(OutlinerNode Node, int Depth, int Index);

    readonly ModuleSession _s;
    readonly Func<PhaseController?> _phases;
    readonly Action<string> _centre;
    readonly Action<MapDef> _openMap;
    readonly Action<Decision> _jump;
    readonly CounterThumbnails _thumbs;
    readonly BreakdownTip _tip = new();
    readonly Action<string> _pictureReady;

    readonly ComboBox _grouping = new() { Width = 170, ItemsSource = new[] { "By map / org display", "By side" }, SelectedIndex = 0 };
    readonly Button _pop = new() { Content = "Pop out", Margin = new Thickness(4, 0, 0, 0) };
    readonly TextBlock _nowLabel = new() { Text = OutlinerViewRules.NowText, FontWeight = FontWeight.Bold, Margin = new Thickness(3, 2, 0, 0) };
    readonly ListBox _now = new() { Height = 70 };
    readonly Grid _header = new() { Height = RowHeight + 2 };
    readonly Border _headerClip = new() { ClipToBounds = true };
    // No auto-scroll to the selection: a refresh restores the selection and must leave the list where the user scrolled it (WinForms keeps
    // FirstDisplayedScrollingRowIndex). The selection deferred-scroll would otherwise run after the offset was restored. Keyboard moves still
    // scroll (the items panel brings the next row into view itself; OutlinerTests.Outliner_keyboard_moves_still_scroll).
    readonly ListBox _list = new() { SelectionMode = SelectionMode.Single, Padding = new Thickness(0), AutoScrollToSelectedItem = false };

    readonly HashSet<string> _open = new(StringComparer.Ordinal);
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    readonly Dictionary<string, double> _widths = new(StringComparer.Ordinal);
    readonly HashSet<Grid> _liveRows = new();
    readonly Dictionary<Image, string> _liveImages = new();
    readonly TranslateTransform _headerShift = new();
    List<Row> _rows = new();
    IReadOnlyList<Decision> _decisions = Array.Empty<Decision>();
    IReadOnlyList<AlertHit> _alerts;
    OutlinerTree _tree = new(Array.Empty<OutlinerColumn>(), Array.Empty<OutlinerNode>());
    OutlinerGrouping _mode = OutlinerGrouping.OrgDisplays;
    bool _loading, _closing, _dirty = true, _noPhases = true;

    /// <param name="alerts">The latest evaluated alerts (the alert hub's); later results arrive through <see cref="AlertsChanged"/>.</param>
    public OutlinerPanel(ModuleSession s, Func<PhaseController?> phases, IReadOnlyList<AlertHit> alerts, Action<string> centreOnPiece,
        Action<MapDef> openMap, Action<Decision> jump)
    {
        _s = s; _phases = phases; _alerts = alerts; _centre = centreOnPiece; _openMap = openMap; _jump = jump;
        Name = "OutlinerPanel";
        MinWidth = 180;
        _thumbs = new CounterThumbnails(s) { Shown = () => _liveImages.Keys.Select(i => (object?)i.Source) };
        _pictureReady = OnPictureReady;

        var bar = new DockPanel { Margin = new Thickness(2) };
        DockPanel.SetDock(_pop, Dock.Right);
        bar.Children.Add(_pop);
        var groupBox = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        groupBox.Children.Add(new TextBlock { Text = "Group:", VerticalAlignment = VerticalAlignment.Center });
        groupBox.Children.Add(_grouping);
        bar.Children.Add(groupBox);

        _now.Margin = new Thickness(2, 0);
        _headerClip.Child = _header;
        _header.RenderTransform = _headerShift;

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(_nowLabel, Dock.Top);
        DockPanel.SetDock(_now, Dock.Top);
        DockPanel.SetDock(_headerClip, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_nowLabel);
        root.Children.Add(_now);
        root.Children.Add(_headerClip);
        root.Children.Add(_list);
        Content = root;

        _list.ItemTemplate = new FuncDataTemplate<Row>((row, _) => row == null ? new Panel() : BuildRow(row), supportsRecycling: false);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        _list.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            if (e.Source is ScrollViewer sv) _headerShift.X = -sv.Offset.X;
        });

        _grouping.SelectionChanged += (_, _) => Guard(() =>
        {
            if (_loading) return;
            _mode = _grouping.SelectedIndex == 1 ? OutlinerGrouping.BySide : OutlinerGrouping.OrgDisplays;
            Reload();
        });
        _pop.Click += (_, _) => PopToggleRequested?.Invoke();
        _now.DoubleTapped += (_, e) => Guard(() =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } item && item.Parent is Panel p)
                ActivateNow(p.Children.IndexOf(item));
        });
        _list.DoubleTapped += (_, e) => Guard(() =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is Row r) ActivateRow(r.Index);
        });
        _list.KeyDown += (_, e) => Guard(() =>
        {
            if (_list.SelectedItem is not Row r) return;
            if (e.Key == Key.Right && !IsOpen(r.Index)) { Toggle(r.Index); e.Handled = true; }
            else if (e.Key == Key.Left && IsOpen(r.Index)) { Toggle(r.Index); e.Handled = true; }
            else if (e.Key == Key.Enter) { ActivateRow(r.Index); e.Handled = true; }
        });
        AttachedToVisualTree += (_, _) => ShownChanged();
        DetachedFromVisualTree += (_, _) => _tip.Hide();
        Reload();
    }

    /// <summary>Raised by the Pop out / Dock button; the host moves the panel.</summary>
    public event Action? PopToggleRequested;

    public void SetFloating(bool floating) => _pop.Content = floating ? "Dock" : "Pop out";

    /// <summary>The Pop out / Dock button.</summary>
    public Button PopButton => _pop;

    /// <summary>The grouping combo box.</summary>
    public ComboBox GroupingBox => _grouping;

    /// <summary>The current grouping (the combo box's).</summary>
    public OutlinerGrouping Grouping
    {
        get => _mode;
        set => _grouping.SelectedIndex = value == OutlinerGrouping.BySide ? 1 : 0;
    }

    /// <summary>The picture cache (tests).</summary>
    internal CounterThumbnails Thumbnails => _thumbs;

    /// <summary>How many times the tree was rebuilt (tests: reload deferred while hidden).</summary>
    public int ReloadCount { get; private set; }

    /// <summary>A refresh is waiting for the panel to show.</summary>
    public bool Stale => _dirty;

    // ---------- reading (tests) ----------

    public int VisibleRowCount => _rows.Count;
    public int ValueColumnCount => _tree.Columns.Count;
    public OutlinerTree Tree => _tree;
    public IReadOnlyList<OutlinerNode> RowNodes => _rows.Select(r => r.Node).ToList();
    public int DepthOf(int row) => row >= 0 && row < _rows.Count ? _rows[row].Depth : 0;
    public int NowCount => _decisions.Count;
    public bool NowVisible => _now.IsVisible;
    public string NowLabelText => _nowLabel.Text ?? "";
    public IReadOnlyList<string> NowTexts => _now.Items.OfType<string>().ToList();
    public IReadOnlyList<string> HeaderTexts => _header.Children.OfType<Border>().Select(b => (b.Child as TextBlock)?.Text ?? "").ToList();
    public string? HeaderNotes(int col) => _header.Children.OfType<Border>().FirstOrDefault(b => Grid.GetColumn(b) == col) is { } b ? ToolTip.GetTip(b) as string : null;
    public ListBox RowList => _list;
    public IReadOnlyList<AlertSeverity> AlertsAt(int row) => row >= 0 && row < _rows.Count ? _rows[row].Node.Alerts : Array.Empty<AlertSeverity>();

    /// <summary>Text of one cell, straight from the row list.</summary>
    public string? CellText(int row, int col)
    {
        if (row < 0 || row >= _rows.Count || col < 0) return null;
        var n = _rows[row].Node;
        if (col == IconCol) return null;
        if (col == NameCol) return n.Name;
        if (col == UnitsCol) return n.Units.ToString(CultureInfo.InvariantCulture);
        var ci = col - FirstValueCol;
        if (ci >= _tree.Columns.Count) return null;
        return ci < n.Values.Count && n.Values[ci] is { } v ? v.ToString("0.##", CultureInfo.InvariantCulture) : "";
    }

    /// <summary>The counter picture shown in a piece row, when it has one and it has been drawn.</summary>
    public Bitmap? PictureAt(int row)
    {
        if (row < 0 || row >= _rows.Count) return null;
        var id = _rows[row].Node.PieceId;
        if (id == null || _s.View?.Pieces.ContainsKey(id) != true) return null;
        return _thumbs.Get(id, ImageSize, null);
    }

    /// <summary>True when the row shows a picture placeholder (a piece this player may see).</summary>
    public bool HasPicture(int row) => row >= 0 && row < _rows.Count && _rows[row].Node.PieceId is { } id && _s.View?.Pieces.ContainsKey(id) == true;

    /// <summary>The picture frame of a row on screen (tests; null when the row is not built or has no picture).</summary>
    internal Border? PictureFrameAt(int row) =>
        row >= 0 && row < _rows.Count && _rows[row].Node.PieceId is { } id
            ? _liveImages.Where(kv => kv.Value == id).Select(kv => kv.Key.Parent as Border).FirstOrDefault(b => b != null)
            : null;

    public double ColumnWidth(int col) => ColumnId(col) is { } id ? WidthOf(id, DefaultWidth(col)) : -1;

    public void SetColumnWidth(int col, double width)
    {
        if (ColumnId(col) is not { } id) return;
        _widths[id] = Math.Max(20, width);
        ApplyWidths();
    }

    public bool IsOpen(int row) => row >= 0 && row < _rows.Count && _open.Contains(_rows[row].Node.Key);

    // ---------- refresh ----------

    /// <summary>The host showed or hid the panel (docked column or floating window): a refresh put off while hidden is done now.</summary>
    public void ShownChanged()
    {
        if (_closing) return;
        if (IsEffectivelyVisible) { if (_dirty) Guard(Reload); }
        else _tip.Hide();
    }

    /// <summary>Smoke / tests: rebuild now (with the latest alerts) instead of waiting for the next alert update.</summary>
    public void Flush() => Guard(Reload);

    /// <summary>
    /// The owner's alert hub published a fresh evaluation (after any game change, rewind, rules change, viewer change or game replacement,
    /// already debounced): keep the hits and rebuild the tree, or just mark it stale while the panel is hidden.
    /// </summary>
    public void AlertsChanged(IReadOnlyList<AlertHit> hits)
    {
        if (_closing) return;
        _alerts = hits ?? Array.Empty<AlertHit>();
        _dirty = true;
        if (IsEffectivelyVisible) Guard(Reload);
    }

    /// <summary>Lets go of the pictures and the tip (the game is closing).</summary>
    public void Detach()
    {
        _closing = true;
        _tip.Hide();
        _thumbs.Dispose();
    }

    void Reload()
    {
        if (_closing) return;
        if (!IsEffectivelyVisible) { _dirty = true; return; }
        _dirty = false;
        ReloadCount++;
        var game = _s.Game;
        if (game == null)
        {
            _tree = new OutlinerTree(Array.Empty<OutlinerColumn>(), Array.Empty<OutlinerNode>());
            _decisions = Array.Empty<Decision>();
            _noPhases = true;
            Rebuild();
            return;
        }
        var state = _s.View ?? game.State;   // what this player may see
        PhaseView? view = null;
        try { view = _phases()?.View(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("outliner: " + ex.Message); }
        try { _tree = OutlinerModel.Build(state, _s.Rules, _s.Model, _mode, _alerts); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("outliner: " + ex.Message);
            _tree = new OutlinerTree(Array.Empty<OutlinerColumn>(), Array.Empty<OutlinerNode>());
        }
        _decisions = view == null ? Array.Empty<Decision>() : OutlinerViewRules.NowList(view);
        _noPhases = view == null || view.NoPhases;
        // New folders start open, groups start closed; what the user opened or closed is remembered by key.
        foreach (var n in OutlinerViewRules.AllNodes(_tree.Roots))
            if (_seen.Add(n.Key) && OutlinerViewRules.StartsExpanded(n)) _open.Add(n.Key);
        Rebuild();
    }

    void Rebuild()
    {
        _loading = true;
        try
        {
            _tip.Hide();
            var items = _decisions.Select(d => d.Text).ToList();
            if (_decisions.Count == 0) items.Add(OutlinerViewRules.NothingWaitingText);
            _now.ItemsSource = items;
            _now.IsVisible = _nowLabel.IsVisible = !_noPhases;
            _nowLabel.Text = OutlinerViewRules.NowLabel(_decisions.Count);

            var cur = _list.SelectedItem is Row sel ? sel.Node.Key : null;
            var scroller = _list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            var offset = scroller?.Offset;   // WinForms keeps FirstDisplayedScrollingRowIndex across a refresh
            var flat = OutlinerViewRules.Flatten(_tree, _open);
            _rows = flat.Select((r, i) => new Row(r.Node, r.Depth, i)).ToList();
            BuildHeader();
            _list.ItemsSource = _rows;
            if (cur != null)
            {
                var at = _rows.FindIndex(r => r.Node.Key == cur);
                if (at >= 0) _list.SelectedIndex = at;   // no auto-scroll to it (see _list): the rows stay where the user scrolled them
            }
            if (scroller != null && offset is { } o)
            {
                _list.UpdateLayout();   // the new rows' extent first, so the offset is not clamped
                scroller.Offset = o;
            }
        }
        finally { _loading = false; }
    }

    // ---------- columns ----------

    string? ColumnId(int col)
    {
        if (col == IconCol) return "#icons";
        if (col == NameCol) return "#name";
        if (col == UnitsCol) return "#units";
        var ci = col - FirstValueCol;
        return ci >= 0 && ci < _tree.Columns.Count ? "p:" + _tree.Columns[ci].PropertyId : null;
    }

    static double DefaultWidth(int col) => col == IconCol ? 26 : col == NameCol ? 190 : col == UnitsCol ? 44 : 60;

    double WidthOf(string id, double def) => _widths.TryGetValue(id, out var w) ? w : def;

    int ColumnCount => FirstValueCol + _tree.Columns.Count;

    ColumnDefinitions Definitions()
    {
        var defs = new ColumnDefinitions();
        for (var c = 0; c < ColumnCount; c++) defs.Add(new ColumnDefinition(WidthOf(ColumnId(c)!, DefaultWidth(c)), GridUnitType.Pixel));
        return defs;
    }

    /// <summary>Header cells ("" / Name / Units / property names, with the notes as tooltip) and their resize grips.</summary>
    void BuildHeader()
    {
        _header.Children.Clear();
        _header.ColumnDefinitions = Definitions();
        for (var c = 0; c < ColumnCount; c++)
        {
            var text = c == IconCol ? "" : c == NameCol ? "Name" : c == UnitsCol ? "Units" : _tree.Columns[c - FirstValueCol].Name;
            var cell = new Border
            {
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(3, 0),
                Child = new TextBlock
                {
                    Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = c >= UnitsCol ? TextAlignment.Right : TextAlignment.Left,
                },
            };
            Grid.SetColumn(cell, c);
            var notes = c >= FirstValueCol ? _tree.Columns[c - FirstValueCol].Notes : null;
            if (!string.IsNullOrWhiteSpace(notes)) ToolTip.SetTip(cell, notes);
            if (c != IconCol)
            {
                var col = c;
                var grip = new Thumb { Width = 5, HorizontalAlignment = HorizontalAlignment.Right, Cursor = new Cursor(StandardCursorType.SizeWestEast), Opacity = 0 };
                grip.DragDelta += (_, e) => SetColumnWidth(col, ColumnWidth(col) + e.Vector.X);
                _header.Children.Add(cell);
                _header.Children.Add(grip);
                Grid.SetColumn(grip, c);
                continue;
            }
            _header.Children.Add(cell);
        }
    }

    void ApplyWidths()
    {
        for (var c = 0; c < ColumnCount && c < _header.ColumnDefinitions.Count; c++)
            _header.ColumnDefinitions[c].Width = new GridLength(WidthOf(ColumnId(c)!, DefaultWidth(c)));
        foreach (var g in _liveRows)
            for (var c = 0; c < ColumnCount && c < g.ColumnDefinitions.Count; c++)
                g.ColumnDefinitions[c].Width = new GridLength(WidthOf(ColumnId(c)!, DefaultWidth(c)));
    }

    // ---------- rows ----------

    Control BuildRow(Row row)
    {
        var n = row.Node;
        var g = new Grid { Height = RowHeight, ColumnDefinitions = Definitions(), Background = Brushes.Transparent };
        var bold = n.Kind != OutlinerNodeKind.Piece;

        // Alert dots (at most two, worst first).
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var sev in n.Alerts.Take(2))
        {
            var (r, gr, b) = OutlinerViewRules.SeverityColour(sev);
            dots.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(Color.FromRgb(r, gr, b)) });
        }
        g.Children.Add(dots);

        var name = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2 + row.Depth * 12, 0, 0, 0) };
        var toggle = new TextBlock
        {
            Width = 12, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent,
            Text = n.Children.Count == 0 ? "" : _open.Contains(n.Key) ? "−" : "+", Margin = new Thickness(0, 0, 2, 0),
        };
        if (n.Children.Count > 0)
            toggle.PointerPressed += (_, e) => { if (e.GetCurrentPoint(toggle).Properties.IsLeftButtonPressed) { Guard(() => Toggle(row.Index)); e.Handled = true; } };
        name.Children.Add(toggle);
        if (n.Kind == OutlinerNodeKind.Piece && n.PieceId is { } pid && _s.View?.Pieces.ContainsKey(pid) == true)
            name.Children.Add(BuildPicture(pid));
        name.Children.Add(new TextBlock
        {
            Text = n.Name, VerticalAlignment = VerticalAlignment.Center, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var nameCell = new Border { ClipToBounds = true, Child = name };
        Grid.SetColumn(nameCell, NameCol);
        g.Children.Add(nameCell);

        for (var c = UnitsCol; c < ColumnCount; c++)
        {
            var col = c;
            var cell = new TextBlock
            {
                Text = CellText(row.Index, c), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0), FontWeight = bold ? FontWeight.Bold : FontWeight.Normal, Background = Brushes.Transparent,
            };
            Grid.SetColumn(cell, c);
            if (c >= FirstValueCol)
            {
                cell.PointerEntered += (_, e) => Guard(() => ShowTip(row.Index, col, cell, e.GetPosition(cell)));
                cell.PointerExited += (_, _) => _tip.Hide();
            }
            g.Children.Add(cell);
        }
        g.AttachedToVisualTree += (_, _) => _liveRows.Add(g);
        g.DetachedFromVisualTree += (_, _) => _liveRows.Remove(g);
        return g;
    }

    /// <summary>A 24x18 frame with the piece's counter, drawn on the shared worker; empty (dotted) until it is ready.</summary>
    Control BuildPicture(string pieceId)
    {
        var img = new Image { Width = ImageSize.Width, Height = ImageSize.Height, Stretch = Stretch.None };
        var frame = new Border
        {
            Width = ImageSize.Width, Height = ImageSize.Height, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = Brushes.Silver, BorderThickness = new Thickness(0), Child = img,
        };
        void Load()
        {
            if (_closing) return;
            var bmp = _thumbs.Get(pieceId, ImageSize, _pictureReady);   // one handler per panel: the cache keeps one waiter per piece
            img.Source = bmp;
            frame.BorderThickness = new Thickness(bmp == null ? 1 : 0);
        }
        img.AttachedToVisualTree += (_, _) =>
        {
            _liveImages[img] = pieceId;
            _thumbs.Prioritise(_liveImages.Values);
            Load();
        };
        img.DetachedFromVisualTree += (_, _) =>
        {
            _liveImages.Remove(img);
            _thumbs.Prioritise(_liveImages.Values);
        };
        return frame;
    }

    /// <summary>A piece's picture was drawn (UI thread): every row on screen showing that piece gets it and loses the placeholder border.</summary>
    void OnPictureReady(string id)
    {
        if (_closing) return;
        foreach (var (img, pid) in _liveImages.ToList())
        {
            if (pid != id || _thumbs.Get(id, ImageSize, null) is not { } bmp) continue;
            img.Source = bmp;
            if (img.Parent is Border frame) frame.BorderThickness = new Thickness(0);
        }
    }

    // ---------- tree behaviour ----------

    public void Toggle(int row)
    {
        if (row < 0 || row >= _rows.Count || _rows[row].Node.Children.Count == 0) return;
        var k = _rows[row].Node.Key;
        if (!_open.Add(k)) _open.Remove(k);
        _list.SelectedIndex = -1;
        Rebuild();
        // Keep the toggled row selected, as the grid's current cell stays.
        var at = _rows.FindIndex(r => r.Node.Key == k);
        if (at >= 0) _list.SelectedIndex = at;
    }

    /// <summary>Opens every folder and group.</summary>
    public void ExpandAll()
    {
        foreach (var n in OutlinerViewRules.AllNodes(_tree.Roots)) if (n.Children.Count > 0) _open.Add(n.Key);
        Rebuild();
    }

    /// <summary>Double-click / Enter on a row: pieces centre on the map, org-display groups open their window, anything else toggles.</summary>
    public void ActivateRow(int row)
    {
        if (row < 0 || row >= _rows.Count) return;
        var n = _rows[row].Node;
        switch (OutlinerViewRules.Activate(n, _s.Model))
        {
            case OutlinerActivation.CentrePiece: _centre(n.PieceId!); break;
            case OutlinerActivation.OpenMap: _openMap(OutlinerViewRules.OrgDisplayFor(n, _s.Model)!); break;
            default: if (n.Children.Count > 0) Toggle(row); break;
        }
    }

    /// <summary>Double-click on a "Now" entry: takes the user to that decision.</summary>
    public void ActivateNow(int i)
    {
        if (i >= 0 && i < _decisions.Count) _jump(_decisions[i]);
    }

    // ---------- tooltips ----------

    /// <summary>The breakdown the tooltip shows for a cell (null = none).</summary>
    public Breakdown? BreakdownFor(int row, int col)
    {
        if (row < 0 || row >= _rows.Count || col < FirstValueCol) return null;
        var ci = col - FirstValueCol;
        if (ci >= _tree.Columns.Count || _s.Game is not { } game) return null;
        var node = _rows[row].Node;
        var column = _tree.Columns[ci];
        if (ci >= node.Values.Count || node.Values[ci] == null) return null;
        if (node.Kind == OutlinerNodeKind.Piece && node.PieceId != null)
            return LedgerBreakdown.For(_s.View ?? game.State, _s.Rules, _s.Model, LedgerKey.Piece(node.PieceId), column.PropertyId);
        return OutlinerViewRules.AggregateBreakdown(column, node, ci, _s.ViewFiltered);
    }

    /// <summary>The text the tip shows now (null when hidden).</summary>
    public string? TipText => _tip.Text;

    internal void ShowTip(int row, int col, Visual owner, Point at)
    {
        if (BreakdownFor(row, col) is { } b) _tip.Show(owner, new Point(at.X + 12, at.Y + 18), b);
        else _tip.Hide();
    }

    /// <summary>Shows the tip of a cell at the pointer (tests; the cell's own hover does the same).</summary>
    internal void HoverCell(int row, int col)
    {
        var owner = (Visual?)_list.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => _liveRows.Contains(g)) ?? this;
        ShowTip(row, col, owner, default);
    }

    internal void HideTip() => _tip.Hide();

    void Guard(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("outliner failed: " + ex);
            _s.Log.Add(DiagnosticSeverity.Error, "outliner", "The outliner failed: " + ex.Message);
        }
    }
}
