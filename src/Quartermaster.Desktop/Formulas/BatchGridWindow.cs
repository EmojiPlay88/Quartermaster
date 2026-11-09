using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Map;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Formulas;

/// <summary>
/// The batch grid of a multi-unit calculation run (WinForms BatchGridWindow): one row per unit, one editable column per formula variable, then
/// Result, Ledger and Exceptions. The list is virtualised (a run can have ~1,200 units and a row costs a formula evaluation), so only the rows
/// on screen are built. Calculate all fills every row from the engine, Apply all applies the rows that match (wrong rows are skipped), Apply row
/// applies the selected row. In semi-automatic mode a cheat sheet docked on the right follows the selected row. Closes itself when its run is
/// no longer pending. Open through <see cref="FormulaWindows.ShowBatch"/>.
/// </summary>
public sealed class BatchGridWindow : Window
{
    static readonly IBrush WrongBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xE4, 0xE1));   // MistyRose
    static readonly IBrush ExceptionBrush = new SolidColorBrush(Color.FromRgb(0xB2, 0x22, 0x22));   // Firebrick
    static readonly IBrush AppliedBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));   // GrayText
    static readonly IBrush PlaceholderBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0));   // Silver
    /// <summary>The counter picture in the Unit cell (WinForms ImageSize).</summary>
    static readonly PixelSize ImageSize = new(64, 48);
    // WinForms: Unit is 150 + picture + 8 wide (the picture is painted in the cell's left padding), rows are 52 high.
    const double UnitWidth = 150 + 64 + 8, ResultWidth = 90, LedgerWidth = 200, ExceptionsWidth = 160, RowHeight = 52;

    readonly MainWindow _host;
    readonly ModuleSession _s;
    readonly BatchModel _model;
    readonly string _runId;
    readonly Action<int>? _openRow;
    readonly ListBox _list = new() { SelectionMode = SelectionMode.Single };
    readonly Button _calcAll = Btn("Calculate all");
    readonly Button _applyAll = Btn("Apply all");
    readonly Button _applyRow = Btn("Apply row");
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly BreakdownTip _tip = new();
    readonly CounterThumbnails _thumbs;
    readonly CounterPreview _preview;
    readonly Action<string> _pictureReady;
    readonly CheatSheetView? _cheat;
    readonly Control? _cheatBody;
    readonly ColumnDefinition? _cheatColumn;
    readonly List<BatchRow> _rows = new();
    readonly IReadOnlyList<string> _headers;
    readonly double[] _widths;
    GameSession? _game;
    int _shownRows = -1;
    bool _closing;
    bool _sending;
    bool _updating;

    internal BatchGridWindow(MainWindow host, ModuleSession s, IActionPort port, string runId, Action<int>? openRow)
    {
        _host = host;
        _s = s;
        _runId = runId;
        _openRow = openRow;
        _model = new BatchModel(s, port, runId);
        _thumbs = new CounterThumbnails(s) { Shown = () => _rows.Select(r => r.Picture) };
        _preview = new CounterPreview(s);
        _pictureReady = id => Safe(() => { foreach (var r in _rows.Where(r => r.PieceId == id).ToList()) r.UpdatePicture(); });
        Title = _model.Title;
        Width = 900;
        Height = 520;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var vars = _model.Variables;
        _headers = new[] { FormulaWindowRules.BatchUnit }.Concat(vars)
            .Concat(new[] { FormulaWindowRules.BatchResult, FormulaWindowRules.BatchLedger, FormulaWindowRules.BatchExceptions }).ToList();
        _widths = new[] { UnitWidth }.Concat(vars.Select(v => _model.ChoicesFor(v) is { Count: > 0 } ? 90d : 80d))
            .Concat(new[] { ResultWidth, LedgerWidth, ExceptionsWidth }).ToArray();

        var header = new Grid { ColumnDefinitions = Columns(_widths) };
        for (var c = 0; c < _headers.Count; c++)
        {
            var cell = new Border { BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(4, 2), Child = new TextBlock { Text = _headers[c], FontWeight = FontWeight.Bold } };
            cell.Bind(Border.BorderBrushProperty, cell.GetResourceObservable("Classic.BorderBrush"));
            cell.Bind(Border.BackgroundProperty, cell.GetResourceObservable("Classic.FaceBrush"));
            Grid.SetColumn(cell, c);
            header.Children.Add(cell);
        }
        _list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<int>((i, _) => new BatchRow(this, i), supportsRecycling: false);
        _list.SelectionChanged += (_, _) => Safe(UpdateCheat);
        var table = new DockPanel { LastChildFill = true, Width = _widths.Sum() + 24 };
        DockPanel.SetDock(header, Dock.Top);
        table.Children.Add(header);
        table.Children.Add(_list);
        var scroller = new ScrollViewer
        {
            Content = table, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6, 4) };
        bar.Children.Add(_calcAll);
        bar.Children.Add(_applyAll);
        bar.Children.Add(_applyRow);
        bar.Children.Add(_status);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*") };
        Grid.SetColumn(scroller, 0);
        body.Children.Add(scroller);
        if (s.Game?.State.Difficulty == Difficulty.SemiAuto)
        {
            // The cheat sheet is a window class; its content is docked here (300 wide, resizable by the splitter) and the window itself never shown.
            _cheat = new CheatSheetView();
            _cheatBody = (Control)_cheat.Content!;
            _cheat.Content = null;
            body.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            _cheatColumn = new ColumnDefinition(300, GridUnitType.Pixel) { MinWidth = 200 };
            body.ColumnDefinitions.Add(_cheatColumn);
            var splitter = new GridSplitter { Width = 4, ResizeDirection = GridResizeDirection.Columns, HorizontalAlignment = HorizontalAlignment.Stretch };
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(_cheatBody, 2);
            body.Children.Add(splitter);
            body.Children.Add(_cheatBody);
        }
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(body);
        Content = root;

        _calcAll.Click += (_, _) => Safe(CalculateAll);
        _applyAll.Click += (_, _) => Safe(ApplyAll);
        _applyRow.Click += (_, _) => Safe(ApplyRow);
        _debounce.Tick += (_, _) => { _debounce.Stop(); Safe(Refresh); };
        Deactivated += (_, _) => { _tip.Hide(); _preview.Hide(); };
        // WinForms: scrolling or a click hides the counter preview.
        _list.AddHandler(PointerPressedEvent, (_, _) => _preview.Hide(), RoutingStrategies.Tunnel, handledEventsToo: true);
        _list.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => _preview.Hide(), RoutingStrategies.Bubble, handledEventsToo: true);
        scroller.ScrollChanged += (_, _) => _preview.Hide();
        _list.PointerExited += (_, _) => _preview.Hide();

        _s.GameReplaced += OnGameReplaced;
        _s.ViewChanged += QueueRefresh;   // a new viewer: other sides' rows turn hidden
        HookGame();
        Opened += (_, _) =>
        {
            if (_cheatColumn != null) _cheatColumn.Width = new GridLength(Math.Clamp(Bounds.Width * 2 / 5, 200, 380));
            Refresh();
        };
        Refresh();
    }

    static Button Btn(string text) => new() { Content = text, MinWidth = 85, HorizontalContentAlignment = HorizontalAlignment.Center };

    static ColumnDefinitions Columns(double[] widths)
    {
        var d = new ColumnDefinitions();
        foreach (var w in widths) d.Add(new ColumnDefinition(w, GridUnitType.Pixel));
        return d;
    }

    // ---- what the registry and tests read ----

    internal ModuleSession Session => _s;
    internal string Key => FormulaWindows.BatchKey(_runId);
    internal string RunId => _runId;
    internal string StatusText => _status.Text ?? "";
    internal IReadOnlyList<string> Headers => _headers;
    internal CheatSheetView? CheatSheet => _cheat;
    internal int RowCount => _model.RowCount;
    internal CounterThumbnails Thumbnails => _thumbs;
    internal CounterPreview Preview => _preview;
    internal double UnitColumnWidth => _widths[0];
    internal double RowHeightOf(int row) => Need(row).Bounds.Height;
    /// <summary>The counter picture of a row on screen; null while it is drawn, or when the unit is hidden.</summary>
    internal Avalonia.Media.Imaging.Bitmap? PictureOf(int row) => Live(row)?.Picture as Avalonia.Media.Imaging.Bitmap;
    internal bool PlaceholderShown(int row) => Need(row).PlaceholderShown;
    internal Rect PictureBounds(int row) => BoundsIn(Need(row).PictureBox);
    internal Rect UnitCellBounds(int row) => BoundsIn(Need(row).UnitCell);
    internal double UnitTextLeft(int row) => BoundsIn(Need(row).UnitText).X;
    BatchRow Need(int row) => Live(row) ?? throw new InvalidOperationException("Row is not on screen.");
    Rect BoundsIn(Control c) => new(c.TranslatePoint(default, this) ?? default, c.Bounds.Size);
    int VarCount => _model.Variables.Count;
    int ResultCol => 1 + VarCount;

    /// <summary>True when the variable column is a dropdown of table keys.</summary>
    internal bool IsChoiceColumn(int col) => col >= 1 && col <= VarCount && _model.ChoicesFor(_model.Variables[col - 1]) is { Count: > 0 };

    BatchRow? Live(int row) => _rows.FirstOrDefault(r => r.Index == row);

    /// <summary>The text shown in a cell (read from the row control; rows not on screen are read from the model).</summary>
    internal string CellText(int row, int col) => Live(row)?.TextOf(col) ?? ModelText(row, col);

    string ModelText(int row, int col)
    {
        var r = _model.Row(row);
        return col == 0 ? FormulaWindowRules.CellOf(r, FormulaWindowRules.BatchUnit).Text
            : col <= VarCount ? FormulaWindowRules.VariableCell(r, col - 1).Text
            : col == ResultCol ? FormulaWindowRules.CellOf(r, FormulaWindowRules.BatchResult).Text
            : col == ResultCol + 1 ? FormulaWindowRules.CellOf(r, FormulaWindowRules.BatchLedger).Text
            : FormulaWindowRules.CellOf(r, FormulaWindowRules.BatchExceptions).Text;
    }

    internal bool CellWrong(int row, int col) => FormulaWindowRules.VariableCell(_model.Row(row), col - 1).Tone == CellTone.Wrong;

    /// <summary>Types into a variable cell as the user would.</summary>
    internal void TypeCell(int row, int col, string text) => (Live(row) ?? throw new InvalidOperationException("Row is not on screen.")).Type(col, text);

    internal void SelectRow(int row) => _list.SelectedIndex = row;

    /// <summary>Centre of the row's Unit cell in window coordinates (tests double-click there).</summary>
    internal Point UnitCellCenter(int row)
    {
        var cell = (Live(row) ?? throw new InvalidOperationException("Row is not on screen.")).UnitCell;
        return cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), this) ?? default;
    }

    // ---- rows ----

    sealed class BatchRow : Grid
    {
        readonly BatchGridWindow _w;
        readonly Border[] _boxes;
        readonly Control[] _content;
        readonly Image _picture = new() { Width = ImageSize.Width, Height = ImageSize.Height, Stretch = Stretch.None, IsHitTestVisible = false };
        readonly Rectangle _placeholder = new()
        {
            Width = ImageSize.Width, Height = ImageSize.Height, Stroke = PlaceholderBrush, StrokeThickness = 1,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 1, 1 }, IsVisible = false, IsHitTestVisible = false,
        };
        readonly Border _pictureBox;
        public int Index { get; }
        public string? PieceId { get; private set; }
        public Control UnitCell => _boxes[0];
        public Control UnitText => _content[0];
        public Control PictureBox => _pictureBox;
        public object? Picture => _picture.Source;
        public bool PlaceholderShown => _placeholder.IsVisible;

        public BatchRow(BatchGridWindow w, int index)
        {
            _w = w;
            Index = index;
            Height = RowHeight;
            _pictureBox = new Border
            {
                Width = ImageSize.Width, Height = ImageSize.Height, Margin = new Thickness(2, 0, 0, 0), Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Child = new Panel { Children = { _placeholder, _picture } },
            };
            // Hovering the picture shows the big counter preview (WinForms OnHoverImage); leaving it hides it.
            _pictureBox.PointerEntered += (_, e) => _w.Safe(() => _w.ShowPreview(this, e.GetPosition(_pictureBox)));
            _pictureBox.PointerMoved += (_, e) => _w.Safe(() => _w.ShowPreview(this, e.GetPosition(_pictureBox)));
            _pictureBox.PointerExited += (_, _) => _w._preview.Hide();
            ColumnDefinitions = Columns(w._widths);
            var n = w._widths.Length;
            _boxes = new Border[n];
            _content = new Control[n];
            for (var c = 0; c < n; c++)
            {
                var col = c;
                Control inner;
                if (c >= 1 && c <= w.VarCount)
                {
                    var v = w._model.Variables[c - 1];
                    if (w._model.ChoicesFor(v) is { Count: > 0 } keys)
                    {
                        var combo = new ComboBox { IsEditable = true, ItemsSource = new[] { "" }.Concat(keys).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(2, 0) };
                        combo.PropertyChanged += (_, e) => { if (e.Property == ComboBox.TextProperty) Edited(col, combo.Text ?? ""); };
                        inner = combo;
                    }
                    else
                    {
                        var box = new TextBox { Padding = new Thickness(2, 0), VerticalContentAlignment = VerticalAlignment.Center };
                        box.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Edited(col, box.Text ?? ""); };
                        inner = box;
                    }
                }
                else inner = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                var cell = new Border { BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(4, 0) };
                if (c == 0)
                {
                    // The unit's counter in the cell's left padding (WinForms paints it at Left + 2, centred vertically), the name after it.
                    cell.Padding = default;
                    inner.Margin = new Thickness(ImageSize.Width + 8 + 4, 0, 4, 0);
                    cell.Child = new Panel { Children = { _pictureBox, inner } };
                }
                else cell.Child = inner;
                cell.Bind(Border.BorderBrushProperty, cell.GetResourceObservable("Classic.BorderBrush"));
                SetColumn(cell, c);
                Children.Add(cell);
                _boxes[c] = cell;
                _content[c] = inner;
            }
            // The Unit cell opens the unit dialog on double-click (WinForms: CellDoubleClick on column 0).
            _boxes[0].DoubleTapped += (_, e) => { _w.Safe(() => _w._openRow?.Invoke(Index)); e.Handled = true; };
            var ledger = _boxes[w.ResultCol + 1];
            ledger.PointerEntered += (_, _) => _w.Safe(() => _w.ShowLedgerTip(Index, ledger));
            ledger.PointerExited += (_, _) => _w._tip.Hide();
            GotFocus += (_, _) => { if (_w._list.SelectedIndex != Index) _w._list.SelectedIndex = Index; };
            AttachedToVisualTree += (_, _) => { if (!_w._rows.Contains(this)) _w._rows.Add(this); Update(); _w.PrioritiseShown(); };
            DetachedFromVisualTree += (_, _) => { _w._rows.Remove(this); _w.PrioritiseShown(); };
        }

        public string TextOf(int col) => _content[col] switch
        {
            TextBlock t => t.Text ?? "",
            TextBox b => b.Text ?? "",
            ComboBox c => c.Text ?? "",
            _ => "",
        };

        public void Type(int col, string text)
        {
            switch (_content[col])
            {
                case TextBox b: b.Text = text; break;
                case ComboBox c: c.Text = text; break;
                default: throw new ArgumentException("Not an editable cell.", nameof(col));
            }
        }

        void Edited(int col, string text)
        {
            if (_w._updating || _w._closing) return;
            _w.Safe(() =>
            {
                if (Index >= _w._model.RowCount) return;
                _w._model.SetCell(Index, _w._model.Variables[col - 1], text);
                Update();
                _w.UpdateCheat();
            });
        }

        /// <summary>Redraws the row from the model: texts, and the red / grey / firebrick tones.</summary>
        public void Update()
        {
            if (Index >= _w._model.RowCount) return;
            var row = _w._model.Row(Index);
            var was = _w._updating;
            _w._updating = true;
            try
            {
                for (var c = 0; c < _content.Length; c++)
                {
                    BatchCell cell = c == 0 ? FormulaWindowRules.CellOf(row, FormulaWindowRules.BatchUnit)
                        : c <= _w.VarCount ? FormulaWindowRules.VariableCell(row, c - 1)
                        : c == _w.ResultCol ? FormulaWindowRules.CellOf(row, FormulaWindowRules.BatchResult)
                        : c == _w.ResultCol + 1 ? FormulaWindowRules.CellOf(row, FormulaWindowRules.BatchLedger)
                        : FormulaWindowRules.CellOf(row, FormulaWindowRules.BatchExceptions);
                    var wrong = cell.Tone == CellTone.Wrong;
                    var fore = cell.Tone == CellTone.Exception ? ExceptionBrush : cell.Tone == CellTone.Applied ? AppliedBrush : null;
                    switch (_content[c])
                    {
                        case TextBlock t:
                            if (t.Text != cell.Text) t.Text = cell.Text;
                            Style(t, fore);
                            break;
                        case TextBox b:
                            if ((b.Text ?? "") != cell.Text) b.Text = cell.Text;
                            Style(b, fore);
                            ToolTip.SetTip(b, wrong ? _w.WrongTip(Index, c) : null);
                            break;
                        case ComboBox cb:
                            if ((cb.Text ?? "") != cell.Text) cb.Text = cell.Text;
                            Style(cb, fore);
                            ToolTip.SetTip(cb, wrong ? _w.WrongTip(Index, c) : null);
                            break;
                    }
                    _boxes[c].Background = wrong ? WrongBrush : Brushes.Transparent;   // transparent, not null: the cell must take clicks
                }
                PieceId = row.PieceId;
                UpdatePicture();
            }
            finally { _w._updating = was; }
        }

        /// <summary>The unit's counter from the viewed state only: a dotted placeholder until the worker has drawn it, nothing when hidden.</summary>
        public void UpdatePicture()
        {
            var id = PieceId;
            var drawable = id != null && _w._s.View?.Pieces.TryGetValue(id, out var p) == true && p != null;
            var bmp = drawable ? _w._thumbs.Get(id!, ImageSize, _w._pictureReady) : null;
            if (!ReferenceEquals(_picture.Source, bmp)) _picture.Source = bmp;
            _placeholder.IsVisible = drawable && bmp == null;
        }

        static void Style(Control c, IBrush? fore)
        {
            if (fore != null) c.SetValue(c is TextBlock ? TextBlock.ForegroundProperty : TemplatedControl.ForegroundProperty, fore);
            else c.ClearValue(c is TextBlock ? TextBlock.ForegroundProperty : TemplatedControl.ForegroundProperty);
        }
    }

    string? WrongTip(int row, int col)
    {
        if (_s.Game == null) return FormulaWindowRules.DoesntMatchTip;
        var v = _model.Variables[col - 1];
        return _model.ModelFor(row).Evaluate(_s.Game.State).Boxes.FirstOrDefault(b => b.Variable == v)?.Tooltip ?? FormulaWindowRules.DoesntMatchTip;
    }

    /// <summary>History of the first ledger value this row would change (what happened before, never the pending result).</summary>
    void ShowLedgerTip(int row, Control anchor)
    {
        if (row >= _model.RowCount || _s.Game == null) return;
        var o = _model.OutputsOf(row);
        if (o.Count == 0) return;
        if (LedgerBreakdown.For(_s.View ?? _s.Game.State, _s.Rules, _s.Model, o[0].Key, o[0].PropertyId) is { } b)
            _tip.Show(anchor, new Point(0, anchor.Bounds.Height), b);
    }

    /// <summary>The rows on screen are drawn first and kept in the thumbnail cache.</summary>
    void PrioritiseShown() => _thumbs.Prioritise(_rows.Select(r => r.PieceId).OfType<string>());

    /// <summary>The big preview of the row's counter at the pointer (WinForms OnHoverImage); hidden for hidden or gone units.</summary>
    void ShowPreview(BatchRow r, Point at)
    {
        if (_closing || r.Index >= _model.RowCount) { _preview.Hide(); return; }
        var row = _model.Row(r.Index);
        if (row.PieceId == null || _s.View?.Pieces.TryGetValue(row.PieceId, out var p) != true || p == null) { _preview.Hide(); return; }
        _preview.Show(r.PictureBox, at, row.PieceId, row.Label);
    }

    /// <summary>Stops the counter preview and thumbnail workers and waits briefly for renders in flight, so the session can be disposed.
    /// Called on close, and again by <see cref="FormulaWindows.CloseAllFor"/> before the session goes away. Safe to call twice.</summary>
    internal void StopRendering()
    {
        _preview.Dispose();
        _thumbs.Dispose();
    }

    // ---- actions ----

    void CalculateAll()
    {
        if (_model.HostCalculates) { SendToHost(_model.OpenRows(), calculate: true, preview: true); return; }
        _model.CalculateAll();
        UpdateRows();
        _status.Text = FormulaWindowRules.CalculatedText(_model.RowCount);
        UpdateCheat();
    }

    void ApplyAll()
    {
        if (_model.HostCalculates) { SendToHost(_model.OpenRows(), calculate: false); return; }
        var before = _model.RowCount;
        var n = _model.ApplyAll();
        // The run may be gone now (all applied): the refresh closes the window.
        _status.Text = FormulaWindowRules.AppliedRowsText(n);
        _debounce.Stop();
        Refresh();
        if (!_closing && before > 0 && n == 0) _status.Text = FormulaWindowRules.NothingAppliedText;
    }

    void ApplyRow()
    {
        var r = _list.SelectedIndex;
        if (r < 0 || r >= _model.RowCount) { _status.Text = FormulaWindowRules.SelectRowText; return; }
        if (_model.HostCalculates) { SendToHost(new[] { r }, calculate: false); return; }
        var ok = _model.ApplyRow(r);
        _status.Text = ok ? FormulaWindowRules.RowAppliedText : FormulaWindowRules.RowNotMatchingText;
        _debounce.Stop();
        Refresh();
    }

    /// <summary>Online guest: the rows go to the host as formula intents, awaited without blocking the UI; the grid shows its verdicts.</summary>
    async void SendToHost(IReadOnlyList<int> rows, bool calculate, bool preview = false)
    {
        if (_sending || _closing) return;
        _sending = true;
        _calcAll.IsEnabled = _applyAll.IsEnabled = _applyRow.IsEnabled = false;
        _status.Text = FormulaWindowRules.AskingHostText;
        try
        {
            var (applied, message) = await _model.SendRowsAsync(rows, calculate, preview);
            if (_closing) return;
            _status.Text = (preview ? FormulaWindowRules.HostCalculatedText(rows.Count) : FormulaWindowRules.HostAppliedText(applied, rows.Count))
                + (message != null ? " " + message : "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _s.Log.Add(DiagnosticSeverity.Warning, "batch", "batch grid: formula intents failed: " + ex.Message);
            if (!_closing) _status.Text = GuestSession.NoAnswerText;
        }
        finally
        {
            _sending = false;
            if (!_closing) _calcAll.IsEnabled = _applyAll.IsEnabled = _applyRow.IsEnabled = true;
        }
        if (!_closing) { _debounce.Stop(); Safe(Refresh); }
    }

    // ---- refresh and lifetime ----

    void HookGame()
    {
        if (_game != null) { _game.Changed -= OnGameChanged; _game.Rewound -= QueueRefresh; }
        _game = _s.Game;
        if (_game != null) { _game.Changed += OnGameChanged; _game.Rewound += QueueRefresh; }
    }

    void OnGameReplaced() { HookGame(); QueueRefresh(); }
    void OnGameChanged(GameCommand _) => QueueRefresh();

    /// <summary>Game events may come from any thread and in bursts (Apply all): one debounced refresh on the UI thread.</summary>
    void QueueRefresh() => Dispatcher.UIThread.Post(() => { if (!_closing) { _debounce.Stop(); _debounce.Start(); } });

    bool RunExists => _s.Game?.State.Pending.Any(r => r != null && r.Id == _runId) == true;

    void Refresh()
    {
        if (_closing) return;
        if (!RunExists) { Close(); return; }       // finished, or the game was replaced
        _model.Invalidate();
        _tip.Hide();
        _preview.Hide();   // the viewed state may have changed (pictures follow in UpdateRows)
        var n = _model.RowCount;
        if (n != _shownRows)
        {
            _shownRows = n;
            var keep = _list.SelectedIndex;
            _list.ItemsSource = Enumerable.Range(0, n).ToList();
            if (keep >= 0 && keep < n) _list.SelectedIndex = keep;
        }
        Title = _model.Title;
        UpdateRows();
        UpdateCheat();
    }

    void UpdateRows()
    {
        foreach (var r in _rows.ToList()) r.Update();
    }

    void UpdateCheat()
    {
        if (_cheat == null) return;
        var r = _list.SelectedIndex;
        if (r < 0 || r >= _model.RowCount || _s.Rules is not { } rules) { _cheat.Render(Array.Empty<CheatRow>()); return; }
        _model.Row(r); // makes sure the row has been evaluated (LastExpected)
        var m = _model.ModelFor(r);
        _cheat.Render(m.LastExpected is { } exp ? CheatSheetModel.Build(exp, rules, m.Hidden) : Array.Empty<CheatRow>());
    }

    void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _s.Log.Add(DiagnosticSeverity.Error, "batch", "batch grid: " + ex.Message);
            if (!_closing) _status.Text = FormulaWindowRules.WentWrongText(ex.Message);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        _s.GameReplaced -= OnGameReplaced;
        _s.ViewChanged -= QueueRefresh;
        if (_game != null) { _game.Changed -= OnGameChanged; _game.Rewound -= QueueRefresh; }
        _game = null;
        _debounce.Stop();
        _tip.Hide();
        StopRendering();
        FormulaWindows.ClosedBatch(this);
        base.OnClosed(e);
    }
}
