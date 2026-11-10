using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Desktop.Input;

namespace Quartermaster.Desktop.Workbench;

/// <summary>EditableCombo: free text plus a drop-down of suggestions (a WinForms DropDown-style combo).</summary>
public enum EditColumnKind { Text, Combo, Check, EditableCombo }

/// <param name="Weight">Relative width (star sizing); ignored when <paramref name="Width"/> is set.</param>
/// <param name="Items">The choices of a Combo column (a cell can override them with <see cref="EditGrid.SetCellItems"/>).</param>
/// <param name="Width">Fixed width in pixels. When any column has one the grid scrolls horizontally (star columns share what is left, at least 40 px each).</param>
public sealed record EditColumn(string Header, EditColumnKind Kind = EditColumnKind.Text, bool ReadOnly = false, double Weight = 100, IReadOnlyList<string>? Items = null, double? Width = null);

/// <summary>
/// A small editable grid in the classic dense look, behaving like a WinForms DataGridView: cells are drawn as text blocks in one body grid with a single
/// floating editor (text box or combo box) placed over the edited cell. F2, typing or double-click starts an edit; Enter commits and moves down, Tab
/// commits and moves right, Escape cancels, arrows move. Check cells hold "True"/"False" and toggle on click or Space. Callers bind with
/// <see cref="SetColumns"/> + <see cref="SetRows"/> and react to <see cref="CellCommitted"/>; a commit that leaves the text unchanged raises nothing.
/// <see cref="SetRows"/> / <see cref="SetColumns"/> drop an edit in progress without committing it.
/// </summary>
public sealed class EditGrid : UserControl
{
    public static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0xD2));
    static readonly IBrush SelectedBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xE8, 0xFF));
    static readonly IBrush CurrentBorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
    static readonly IBrush GridLineBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
    static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
    internal static readonly IBrush ReadOnlyBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));   // SystemColors.Control, as WinForms greys read-only cells
    const double StarMinWidth = 40;
    public const double RowHeight = 22;

    sealed class CellState
    {
        public string Text = "";
        public IReadOnlyList<string>? Items;
        public bool ReadOnly;
        public string? Error;
        public string? Tip;
        public IBrush? Tint;
        public TextBlock Block = null!;
    }

    /// <summary>The editor of an <see cref="EditColumnKind.EditableCombo"/> cell: a text box with a drop-down button listing the suggestions.</summary>
    sealed class EditableEditor : Grid
    {
        public readonly TextBox Box = new() { MinHeight = 0, Height = RowHeight, Padding = new Thickness(3, 0), BorderThickness = new Thickness(1), VerticalContentAlignment = VerticalAlignment.Center, AcceptsReturn = false };
        public readonly Button Drop = new() { Content = "▾", Focusable = false, MinHeight = 0, Height = RowHeight, Width = 18, Padding = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center };
        public readonly ListBox List = new() { MaxHeight = 160, MinWidth = 120, Focusable = false };
        public readonly Avalonia.Controls.Primitives.Popup Pop = new() { IsLightDismissEnabled = true, Placement = Avalonia.Controls.PlacementMode.Bottom };
        public EditableEditor()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto");
            SetColumn(Drop, 1);
            Children.Add(Box);
            Children.Add(Drop);
            Pop.PlacementTarget = this;
            Pop.Child = new Border { BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, Background = Brushes.White, Child = List };
            Children.Add(Pop);
        }
    }

    readonly Grid _header = new() { Height = RowHeight, HorizontalAlignment = HorizontalAlignment.Left };
    readonly List<Border> _headerCells = new();
    Border _headerBox = null!;
    readonly Grid _body = new() { Focusable = false, Background = GridLineBrush };   // shows through the 1px cell margins as grid lines
    readonly Border _curBox = new() { BorderThickness = new Thickness(1), BorderBrush = CurrentBorderBrush, IsHitTestVisible = false, ZIndex = 5, IsVisible = false };
    readonly ScrollViewer _scroll;
    IReadOnlyList<EditColumn> _cols = [];
    CellState[][] _rows = [];
    (int Row, int Col)? _cur;
    readonly SortedSet<int> _sel = new();
    int _anchor;
    bool _multi;

    // editing
    Control? _editor;
    (int Row, int Col) _editCell;
    bool _editing;

    public EditGrid()
    {
        Focusable = true;
        var root = new DockPanel { LastChildFill = true };
        var headerBox = _headerBox = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = _header, ClipToBounds = true };
        headerBox.Bind(Border.BorderBrushProperty, headerBox.GetResourceObservable("Classic.BorderBrush"));
        DockPanel.SetDock(headerBox, Dock.Top);
        _scroll = new ScrollViewer
        {
            Content = _body,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,   // Auto once a column has a fixed width
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        root.Children.Add(headerBox);
        root.Children.Add(_scroll);
        var frame = new Border { BorderThickness = new Thickness(1), Background = Brushes.White, Child = root };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("Classic.BorderBrush"));
        Content = frame;
        _scroll.ScrollChanged += (_, _) => SyncHeaderWidth();
        _scroll.SizeChanged += (_, _) => SyncHeaderWidth();
        _body.PointerPressed += OnBodyPressed;
        AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Bubble);
        AddHandler(TextInputEvent, OnGridTextInput, RoutingStrategies.Bubble);
    }

    bool _fixedCols;

    void SyncHeaderWidth()
    {
        var bar = Math.Max(0, _scroll.Bounds.Width - _scroll.Viewport.Width);
        _headerBox.Margin = new Thickness(0, 0, bar, 0);
        _header.RenderTransform = new TranslateTransform(-_scroll.Offset.X, 0);
        if (!_fixedCols || _scroll.Viewport.Width <= 0) return;
        double fixedSum = 0;
        var stars = 0;
        foreach (var c in _cols) { if (c.Width is { } w) fixedSum += w; else stars++; }
        var needed = fixedSum + stars * StarMinWidth;
        var width = stars == 0 ? needed : Math.Max(_scroll.Viewport.Width, needed);
        if (double.IsNaN(_body.Width) || Math.Abs(_body.Width - width) > 0.5) { _body.Width = width; _header.Width = width; }
    }

    static ColumnDefinition Def(EditColumn c) =>
        c.Width is { } w ? new ColumnDefinition(w, GridUnitType.Pixel) : new ColumnDefinition(Math.Max(0.01, c.Weight), GridUnitType.Star) { MinWidth = StarMinWidth };

    // ---------- data ----------

    public bool MultiSelect
    {
        get => _multi;
        set { _multi = value; if (!value && _cur is { } c) { _sel.Clear(); _sel.Add(c.Row); } PaintAll(); }
    }

    public int RowCount => _rows.Length;
    public bool IsEditing => _editing;
    public (int Row, int Col)? CurrentCell => _cur;
    public event Action<int, int, string>? CellCommitted;
    public event Action<int, int>? CurrentCellChanged;

    public IReadOnlyList<int> SelectedRows =>
        _multi ? _sel.ToList() : _cur is { } c ? [c.Row] : [];

    public string Cell(int row, int col) => _rows[row][col].Text;

    void Check(int row, int col)
    {
        if (row < 0 || row >= _rows.Length || col < 0 || col >= _cols.Count) throw new ArgumentOutOfRangeException(row < 0 || row >= _rows.Length ? nameof(row) : nameof(col));
    }

    public void SetColumns(IReadOnlyList<EditColumn> columns)
    {
        var wasEditing = _editing;
        EndEdit(false, false);
        _cols = columns.ToList();
        _fixedCols = _cols.Any(c => c.Width != null);
        _scroll.HorizontalScrollBarVisibility = _fixedCols ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _body.Width = double.NaN;
        _header.Width = double.NaN;
        _header.Children.Clear();
        _header.ColumnDefinitions.Clear();
        _headerCells.Clear();
        _body.Children.Clear();
        _body.Children.Add(_curBox);
        _body.ColumnDefinitions.Clear();
        _body.RowDefinitions.Clear();
        _rows = [];
        _cur = null;
        _sel.Clear();
        for (var c = 0; c < _cols.Count; c++)
        {
            _header.ColumnDefinitions.Add(Def(_cols[c]));
            _body.ColumnDefinitions.Add(Def(_cols[c]));
            var cell = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(4, 0), Background = Brushes.Transparent };
            cell.Bind(Border.BorderBrushProperty, cell.GetResourceObservable("Classic.BorderBrush"));
            cell.Child = new TextBlock { Text = _cols[c].Header, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(cell, c);
            _header.Children.Add(cell);
            _headerCells.Add(cell);
        }
        _header.Bind(Panel.BackgroundProperty, _header.GetResourceObservable("Classic.FaceBrush"));
        SetRows([]);
        SyncHeaderWidth();
        if (wasEditing) Focus();
    }

    public void SetRows(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var wasEditing = _editing;
        EndEdit(false, false);   // a stale in-progress edit never commits into the replaced data
        var old = _cur;
        var reuse = rows.Count == _rows.Length && rows.Count > 0;
        if (!reuse)
        {
            _body.Children.Clear();
            _body.Children.Add(_curBox);
            _body.RowDefinitions.Clear();
            _rows = new CellState[rows.Count][];
        }
        for (var r = 0; r < rows.Count; r++)
        {
            if (!reuse)
            {
                _body.RowDefinitions.Add(new RowDefinition(RowHeight, GridUnitType.Pixel));
                _rows[r] = new CellState[_cols.Count];
            }
            for (var c = 0; c < _cols.Count; c++)
            {
                var text = c < rows[r].Count ? rows[r][c] ?? "" : "";
                if (_cols[c].Kind == EditColumnKind.Check) text = IsTrue(text) ? "True" : "False";
                CellState cell;
                if (reuse)
                {
                    cell = _rows[r][c];
                    cell.Text = text; cell.Items = null; cell.Error = null; cell.Tip = null; cell.Tint = null; cell.ReadOnly = _cols[c].ReadOnly;
                    ToolTip.SetTip(cell.Block, null);
                }
                else
                {
                    var block = new TextBlock
                    {
                        VerticalAlignment = VerticalAlignment.Stretch, TextTrimming = TextTrimming.CharacterEllipsis, Padding = new Thickness(4, 3, 4, 0),
                        Margin = new Thickness(0, 0, 1, 1), Background = Brushes.White,
                    };
                    Grid.SetRow(block, r);
                    Grid.SetColumn(block, c);
                    _body.Children.Add(block);
                    cell = _rows[r][c] = new CellState { Text = text, Block = block, ReadOnly = _cols[c].ReadOnly };
                }
                Show(cell, c);
            }
        }
        if (old is { } o && o.Row < _rows.Length && o.Col < _cols.Count) _cur = o;
        else _cur = _rows.Length > 0 && _cols.Count > 0 ? (0, 0) : null;
        _sel.Clear();
        if (_cur is { } k) { _sel.Add(k.Row); _anchor = k.Row; }
        PaintAll();
        if (wasEditing) Focus();   // the editor held the keyboard focus; keep typing working
        if (_cur != old && _cur is { } n) CurrentCellChanged?.Invoke(n.Row, n.Col);
    }

    static bool IsTrue(string s) => string.Equals(s, "True", StringComparison.OrdinalIgnoreCase);

    void Show(CellState cell, int col)
    {
        if (_cols[col].Kind == EditColumnKind.Check)
        {
            cell.Block.Text = IsTrue(cell.Text) ? "☑" : "☐";
            cell.Block.HorizontalAlignment = HorizontalAlignment.Center;
        }
        else cell.Block.Text = cell.Text;
    }

    public void SetCellItems(int row, int col, IReadOnlyList<string> items) { Check(row, col); _rows[row][col].Items = items; }

    public void SetCellReadOnly(int row, int col, bool readOnly)
    {
        Check(row, col);
        if (_editing) { if (_editCell == (row, col) && readOnly) EndEdit(false, false); else EndEdit(true, false); }
        if (row >= _rows.Length || col >= _cols.Count) return;
        _rows[row][col].ReadOnly = readOnly;
        Paint(row, col);
    }

    public void SetCellError(int row, int col, string? error)
    {
        Check(row, col);
        _rows[row][col].Error = string.IsNullOrEmpty(error) ? null : error;
        ApplyTip(_rows[row][col]);
        Paint(row, col);
    }

    /// <summary>A hint tooltip for a cell (an error text set with <see cref="SetCellError"/> wins while it is set); null clears.</summary>
    public void SetCellToolTip(int row, int col, string? tip)
    {
        Check(row, col);
        _rows[row][col].Tip = string.IsNullOrEmpty(tip) ? null : tip;
        ApplyTip(_rows[row][col]);
    }

    static void ApplyTip(CellState c) => ToolTip.SetTip(c.Block, c.Error ?? c.Tip);

    public void SetCellTint(int row, int col, IBrush? brush)
    {
        Check(row, col);
        _rows[row][col].Tint = brush;
        Paint(row, col);
    }

    public void SetHeaderToolTip(int col, string? tip)
    {
        if (col < 0 || col >= _headerCells.Count) throw new ArgumentOutOfRangeException(nameof(col));
        ToolTip.SetTip(_headerCells[col], string.IsNullOrEmpty(tip) ? null : tip);
    }

    public void SetHeaderTint(int col, IBrush? brush)
    {
        if (col < 0 || col >= _headerCells.Count) throw new ArgumentOutOfRangeException(nameof(col));
        _headerCells[col].Background = brush ?? Brushes.Transparent;
    }

    // ---------- painting ----------

    void PaintAll()
    {
        PlaceCurrentBox();
        for (var r = 0; r < _rows.Length; r++) PaintRow(r);
    }

    void PlaceCurrentBox()
    {
        if (_cur is { } k) { Grid.SetRow(_curBox, k.Row); Grid.SetColumn(_curBox, k.Col); _curBox.IsVisible = true; }
        else _curBox.IsVisible = false;
    }

    void PaintRow(int r)
    {
        if (r < 0 || r >= _rows.Length) return;
        for (var c = 0; c < _cols.Count; c++) Paint(r, c);
    }

    // Precedence: error, tint, current cell (DataGridView highlight), selected row (multi-select), read-only grey, white.
    void Paint(int r, int c)
    {
        var cell = _rows[r][c];
        var current = _cur is { } k && k.Row == r && k.Col == c;
        IBrush bg;
        var plain = cell.Error == null && cell.Tint == null;
        if (cell.Error != null) bg = ErrorBrush;
        else if (cell.Tint != null) bg = cell.Tint;
        else if (current) bg = HighlightBrush;
        else if (_multi && _sel.Contains(r)) bg = SelectedBrush;
        else if (cell.ReadOnly) bg = ReadOnlyBrush;
        else bg = Brushes.White;
        cell.Block.Background = bg;
        cell.Block.Foreground = current && plain ? Brushes.White : null;
    }

    // test hooks
    internal IBrush? CellBackground(int row, int col) => _rows[row][col].Block.Background;
    internal object? CellToolTip(int row, int col) => ToolTip.GetTip(_rows[row][col].Block);
    internal string CellShown(int row, int col) => _rows[row][col].Block.Text ?? "";
    internal Control? Editor => _editor;
    internal TextBox? EditorText => (_editor as EditableEditor)?.Box;
    internal ListBox? EditorList => (_editor as EditableEditor)?.List;
    internal Control CellControl(int row, int col) => _rows[row][col].Block;
    internal IBrush? HeaderBackground(int col) => _headerCells[col].Background;
    internal object? HeaderToolTip(int col) => ToolTip.GetTip(_headerCells[col]);

    // ---------- selection ----------

    public void SelectCell(int row, int col)
    {
        Check(row, col);
        EndEdit(true, false);
        if (row >= _rows.Length || col >= _cols.Count) return;
        MoveTo(row, col, extend: false, keepSel: false);
    }

    void MoveTo(int row, int col, bool extend, bool keepSel)
    {
        var old = _cur;
        var oldSel = _multi ? _sel.ToArray() : [];
        _cur = (row, col);
        if (extend && _multi)
        {
            _sel.Clear();
            for (var r = Math.Min(_anchor, row); r <= Math.Max(_anchor, row); r++) _sel.Add(r);
        }
        else if (!keepSel)
        {
            _sel.Clear();
            _sel.Add(row);
            _anchor = row;
        }
        PlaceCurrentBox();
        var touched = new HashSet<int>(oldSel) { row };
        foreach (var r in _sel) touched.Add(r);
        if (old is { } o) touched.Add(o.Row);
        foreach (var r in touched) PaintRow(r);
        _rows[row][col].Block.BringIntoView();
        if (old != _cur) CurrentCellChanged?.Invoke(row, col);
    }

    bool PrimaryDown(KeyModifiers m) => (m & (ShortcutGestures.IsMac ? KeyModifiers.Meta : KeyModifiers.Control)) != 0;

    void OnBodyPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_rows.Length == 0 || _cols.Count == 0) return;
        var p = e.GetCurrentPoint(_body);
        if (!p.Properties.IsLeftButtonPressed && !p.Properties.IsRightButtonPressed) return;
        var row = (int)(p.Position.Y / RowHeight);
        var col = -1;
        double x = 0;
        for (var c = 0; c < _body.ColumnDefinitions.Count; c++)
        {
            x += _body.ColumnDefinitions[c].ActualWidth;
            if (p.Position.X < x) { col = c; break; }
        }
        if (row < 0 || row >= _rows.Length || col < 0) return;
        if (_editing) { var same = _editCell == (row, col); if (same) return; EndEdit(true, false); if (row >= _rows.Length || col >= _cols.Count) return; }
        Focus();
        var m = e.KeyModifiers;
        var wasCurrent = _cur == (row, col);
        if (_multi && (m & KeyModifiers.Shift) != 0) MoveTo(row, col, extend: true, keepSel: false);
        else if (_multi && PrimaryDown(m))
        {
            if (!_sel.Remove(row)) _sel.Add(row);
            _anchor = row;
            MoveTo(row, col, extend: false, keepSel: true);
        }
        else MoveTo(row, col, extend: false, keepSel: false);
        if (!p.Properties.IsLeftButtonPressed) return;
        var kind = _cols[col].Kind;
        if (kind == EditColumnKind.Check && !_rows[row][col].ReadOnly) Toggle(row, col);
        else if (e.ClickCount >= 2 || ((kind == EditColumnKind.Combo || kind == EditColumnKind.EditableCombo) && wasCurrent)) BeginEdit(row, col);
        e.Handled = true;
    }

    // ---------- keyboard ----------

    void OnGridTextInput(object? sender, TextInputEventArgs e)
    {
        if (_editing || _cur is not { } c || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if (_cols[c.Col].Kind == EditColumnKind.Check) return;
        var combo = _cols[c.Col].Kind == EditColumnKind.Combo;
        BeginEdit(c.Row, c.Col, combo ? null : e.Text);
        if (combo && _editor is ComboBox cb && cb.ItemsSource is IEnumerable<string> items
            && items.FirstOrDefault(i => i.StartsWith(e.Text, StringComparison.CurrentCultureIgnoreCase)) is { } hit)
            cb.SelectedItem = hit;   // typed first letters pick the first matching choice (committed with Enter or when the list closes)
        e.Handled = true;
    }

    void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (_editing || _cur is not { } c || e.Handled) return;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        int r = c.Row, k = c.Col;
        switch (e.Key)
        {
            case Key.F2: BeginEdit(r, k); break;
            case Key.Space when _cols[k].Kind == EditColumnKind.Check:
                if (!_rows[r][k].ReadOnly) Toggle(r, k);
                break;
            case Key.Enter when r + 1 < _rows.Length: MoveTo(r + 1, k, false, false); break;
            case Key.Down when r + 1 < _rows.Length: MoveTo(r + 1, k, shift, false); break;
            case Key.Up when r > 0: MoveTo(r - 1, k, shift, false); break;
            case Key.Right when k + 1 < _cols.Count: MoveTo(r, k + 1, false, true); break;
            case Key.Left when k > 0: MoveTo(r, k - 1, false, true); break;
            case Key.Tab:
                if (!shift && r == _rows.Length - 1 && k == _cols.Count - 1) return;   // leave the grid
                if (shift && r == 0 && k == 0) return;
                Tab(r, k, shift);
                break;
            case Key.Home: MoveTo(r, 0, false, true); break;
            case Key.End: MoveTo(r, _cols.Count - 1, false, true); break;
            case Key.PageDown: MoveTo(Math.Min(_rows.Length - 1, r + 10), k, shift, false); break;
            case Key.PageUp: MoveTo(Math.Max(0, r - 10), k, shift, false); break;
            default: return;
        }
        e.Handled = true;
    }

    void Tab(int r, int k, bool back)
    {
        if (!back) { if (k + 1 < _cols.Count) MoveTo(r, k + 1, false, true); else if (r + 1 < _rows.Length) MoveTo(r + 1, 0, false, false); }
        else { if (k > 0) MoveTo(r, k - 1, false, true); else if (r > 0) MoveTo(r - 1, _cols.Count - 1, false, false); }
    }

    // ---------- editing ----------

    /// <summary>Commits the edit in progress (a combo takes its selected choice). Returns false when nothing was being edited.</summary>
    public bool CommitEdit()
    {
        if (!_editing) return false;
        if (_editor is ComboBox cb)
        {
            var s = cb.SelectedItem as string;
            var (r, c) = _editCell;
            EndEdit(false, true);
            if (s != null && r < _rows.Length && c < _cols.Count) Commit(r, c, s);
        }
        else EndEdit(true, true);
        return true;
    }

    /// <summary>Drops the edit in progress without committing.</summary>
    public void CancelEdit() => EndEdit(false, true);

    void Toggle(int row, int col) => Commit(row, col, IsTrue(_rows[row][col].Text) ? "False" : "True");

    void Commit(int row, int col, string text)
    {
        var cell = _rows[row][col];
        if (cell.Text == text) return;
        cell.Text = text;
        Show(cell, col);
        CellCommitted?.Invoke(row, col, text);
    }

    public void BeginEdit(int row, int col, string? initialText = null)
    {
        Check(row, col);
        var cell = _rows[row][col];
        if (cell.ReadOnly || _cols[col].Kind == EditColumnKind.Check) return;
        EndEdit(true, false);
        if (row >= _rows.Length) return;
        if (_cur != (row, col)) MoveTo(row, col, false, false);
        Control ed;
        if (_cols[col].Kind == EditColumnKind.Combo)
        {
            var items = (cell.Items ?? _cols[col].Items ?? []).ToList();
            if (cell.Text.Length > 0 && !items.Contains(cell.Text)) items.Add(cell.Text);   // a value outside the list still shows (WinForms ComboCell)
            var box = new ComboBox { ItemsSource = items, MinHeight = 0, Height = RowHeight, Padding = new Thickness(3, 0), VerticalContentAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
            box.SelectedItem = items.FirstOrDefault(i => i == cell.Text);
            // Arrowing through the open list changes the selection; only closing the list (Enter, click) or a selection made while it is shut commits.
            box.SelectionChanged += (_, _) =>
            {
                if (!_editing || _editor != box || box.IsDropDownOpen || box.SelectedItem is not string s) return;
                EndEdit(false, true);
                Commit(row, col, s);
            };
            box.PropertyChanged += (_, e) =>
            {
                if (e.Property != ComboBox.IsDropDownOpenProperty || e.NewValue is not false) return;
                if (!_editing || _editor != box) return;
                var s = box.SelectedItem as string;
                EndEdit(false, true);
                if (s != null && row < _rows.Length && col < _cols.Count) Commit(row, col, s);
            };
            box.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape && _editing && _editor == box) { EndEdit(false, true); e.Handled = true; } }, RoutingStrategies.Tunnel);
            box.LostFocus += (_, _) => { if (_editing && _editor == box && !box.IsDropDownOpen) EndEdit(false, false); };
            ed = box;
        }
        else if (_cols[col].Kind == EditColumnKind.EditableCombo)
        {
            var items = (cell.Items ?? _cols[col].Items ?? []).ToList();
            var ee = new EditableEditor();
            ee.Box.Text = initialText ?? cell.Text;
            ee.Box.BorderBrush = CurrentBorderBrush;
            ee.List.ItemsSource = items;
            ee.Box.KeyDown += (s, e) =>
            {
                if (_editing && _editor == ee && (e.Key == Key.F4 || (e.Key == Key.Down && (e.KeyModifiers & KeyModifiers.Alt) != 0))) { ee.Pop.IsOpen = items.Count > 0; e.Handled = true; return; }
                OnEditorKeyDown(s, e);
            };
            ee.Box.LostFocus += (_, _) => { if (_editing && _editor == ee && !ee.Pop.IsOpen) EndEdit(true, false); };
            ee.Drop.Click += (_, _) => { if (_editing && _editor == ee) { ee.Pop.IsOpen = !ee.Pop.IsOpen && items.Count > 0; ee.Box.Focus(); } };
            ee.List.SelectionChanged += (_, _) =>
            {
                if (!_editing || _editor != ee || ee.List.SelectedItem is not string s) return;
                ee.Pop.IsOpen = false;
                EndEdit(false, true);
                if (row < _rows.Length && col < _cols.Count) Commit(row, col, s);
            };
            ed = ee;
        }
        else
        {
            var text = initialText ?? cell.Text;
            var tb = new TextBox
            {
                Text = text, MinHeight = 0, Height = RowHeight, Padding = new Thickness(3, 0), BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center, AcceptsReturn = false,
            };
            tb.BorderBrush = CurrentBorderBrush;
            tb.KeyDown += OnEditorKeyDown;
            tb.LostFocus += (_, _) => { if (_editing && _editor == tb) EndEdit(true, false); };
            ed = tb;
        }
        _editing = true;
        _editor = ed;
        _editCell = (row, col);
        Grid.SetRow(ed, row);
        Grid.SetColumn(ed, col);
        ed.ZIndex = 10;
        _body.Children.Add(ed);
        if (ed is ComboBox cb) { cb.Focus(); cb.IsDropDownOpen = true; }
        else if (ed is EditableEditor eed) { eed.Box.Focus(); eed.Box.CaretIndex = eed.Box.Text?.Length ?? 0; if (initialText == null) eed.Box.SelectAll(); }
        else if (ed is TextBox t) { t.Focus(); t.CaretIndex = t.Text?.Length ?? 0; if (initialText == null) t.SelectAll(); }
    }

    void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_editing) return;
        var (r, c) = _editCell;
        switch (e.Key)
        {
            case Key.Enter:
                EndEdit(true, true);
                if (r + 1 < _rows.Length && c < _cols.Count) MoveTo(r + 1, c, false, false);
                break;
            case Key.Tab:
                var back = (e.KeyModifiers & KeyModifiers.Shift) != 0;
                EndEdit(true, true);
                if (r >= _rows.Length || c >= _cols.Count) break;
                if ((!back && r == _rows.Length - 1 && c == _cols.Count - 1) || (back && r == 0 && c == 0)) return;   // focus leaves the grid
                Tab(r, c, back);
                break;
            case Key.Down:
            case Key.Up:
                var nr = e.Key == Key.Down ? r + 1 : r - 1;
                EndEdit(true, true);
                if (nr >= 0 && nr < _rows.Length && c < _cols.Count) MoveTo(nr, c, false, false);
                break;
            case Key.Escape: EndEdit(false, true); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Ends the edit: commits the editor's text when <paramref name="commit"/>, drops it otherwise.</summary>
    void EndEdit(bool commit, bool refocus)
    {
        if (!_editing) return;
        var ed = _editor;
        var (r, c) = _editCell;
        var text = commit ? ed is TextBox tb ? tb.Text ?? "" : ed is EditableEditor ee ? ee.Box.Text ?? "" : null : null;
        _editing = false;
        _editor = null;
        if (ed != null) _body.Children.Remove(ed);
        if (refocus) Focus();
        if (text != null && r < _rows.Length && c < _cols.Count) Commit(r, c, text);
    }
}
