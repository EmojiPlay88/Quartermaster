using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render.Ocr;
using SkiaSharp;

namespace Quartermaster.Desktop.Workbench;

/// <summary>What a read works on: the shown image, the region (null = whole image) and where it came from.</summary>
public sealed record OcrTarget(SKBitmap Image, System.Drawing.Rectangle? Region, ScanSource Source, int Page);

/// <summary>
/// OCR panel beside the scan (port of the WinForms OcrPanel): Read text (off the UI thread, one read at a time, Cancel), language,
/// the Text box and an editable Grid with Columns / Merge / Split, Import into table… and Copy text. OCR output is only imported
/// through the Paste table dialog the host opens.
/// </summary>
public sealed class OcrPanel : UserControl
{
    public const int MinPanelWidth = 280;
    public const string ReplaceEditsQuestion = "Replace your edits with a new reading?";
    public const string SomeEmptyText = "Some cells came back empty (single characters are often missed): type them in.";
    const string RebuildQuestion = "Rebuild the grid from the reading? Your edits to the grid will be lost.";
    const string Caption = "Read text";
    static readonly IBrush EditedBack = new SolidColorBrush(Color.FromRgb(255, 248, 200));
    static readonly IBrush RereadBack = new SolidColorBrush(Color.FromRgb(214, 234, 255));
    static readonly IBrush ProblemText = new SolidColorBrush(Color.FromRgb(0xB2, 0x22, 0x22));   // Firebrick

    readonly OcrReader _reader;
    readonly ScanSessionModel _model;
    readonly IDialogs _dialogs;
    readonly IReadOnlyList<string> _langs;

    public Button ReadButton { get; } = new() { Content = "Read text" };
    public Button CancelButton { get; } = new() { Content = "Cancel", IsEnabled = false };
    public ComboBox LanguageBox { get; } = new() { Width = 90 };
    public CheckBox FixBox { get; } = new() { Content = "Fix common OCR digit mistakes", IsChecked = true };
    public ComboBox ColumnsBox { get; } = new() { Width = 70 };
    public Button MergeButton { get; } = new() { Content = "Merge" };
    public Button SplitButton { get; } = new() { Content = "Split" };
    public Button ImportButton { get; } = new() { Content = "Import into table…", IsEnabled = false };
    public Button CopyButton { get; } = new() { Content = "Copy text", IsEnabled = false };
    public TabControl Tabs { get; } = new();
    public TextBox TextBox { get; } = new()
    {
        AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
        [ScrollViewer.HorizontalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto, [ScrollViewer.VerticalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto,
    };
    public EditGrid Grid { get; } = new();
    internal bool StatusIsProblem { get; private set; }

    readonly TextBlock _status = new() { Height = 34, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 2, 0) };
    readonly TabItem _textPage = new() { Header = "Text" }, _gridPage = new() { Header = "Grid" };

    IReadOnlyList<OcrWord>? _words;
    OcrGridState? _state;
    ScanSource? _readSource;
    int _readPage;
    bool _filling, _syncing, _asking;
    int _columnsShown;
    Task _lastRead = Task.CompletedTask;

    public OcrPanel(IOcrRecognizer engine, ScanSessionModel model, IDialogs dialogs)
    {
        _reader = new OcrReader(engine);
        _model = model;
        _dialogs = dialogs;
        MinWidth = MinPanelWidth;

        var row1 = new WrapPanel { Margin = new Thickness(2, 2, 0, 0) };
        Add(row1, ReadButton); Add(row1, CancelButton);
        row1.Children.Add(new TextBlock { Text = "Language:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) });
        Add(row1, LanguageBox);
        var row2 = new WrapPanel { Margin = new Thickness(2, 2, 0, 0) };
        row2.Children.Add(FixBox);

        ColumnsBox.Items.Add("Auto");
        for (var i = 1; i <= 16; i++) ColumnsBox.Items.Add(i.ToString(CultureInfo.InvariantCulture));
        ColumnsBox.SelectedIndex = 0;
        var gridBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        gridBar.Children.Add(new TextBlock { Text = "Columns:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        Add(gridBar, ColumnsBox); Add(gridBar, MergeButton); Add(gridBar, SplitButton);
        var gridDock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(gridBar, Dock.Top);
        gridDock.Children.Add(gridBar);
        gridDock.Children.Add(Grid);
        _gridPage.Content = gridDock;
        _textPage.Content = TextBox;
        Tabs.Items.Add(_gridPage);
        Tabs.Items.Add(_textPage);

        var bottom = new WrapPanel { Margin = new Thickness(2) };
        Add(bottom, ImportButton); Add(bottom, CopyButton);

        var root = new DockPanel { LastChildFill = true };
        var top = new StackPanel();
        top.Children.Add(row1);
        top.Children.Add(row2);
        top.Children.Add(_status);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(Tabs);
        Content = root;

        _langs = engine.Languages;
        foreach (var l in _langs) LanguageBox.Items.Add(l);
        var def = engine.DefaultLanguage;
        if (def != null) LanguageBox.SelectedItem = _langs.FirstOrDefault(l => string.Equals(l, def, StringComparison.OrdinalIgnoreCase)) ?? def;
        if (LanguageBox.SelectedIndex < 0 && LanguageBox.Items.Count > 0) LanguageBox.SelectedIndex = 0;
        LanguageBox.IsEnabled = _langs.Count > 0;
        SetStatus(_langs.Count == 0 ? engine.UnavailableMessage : "Drag a region on the image (or read the whole image), then Read text.", _langs.Count == 0);

        ReadButton.Click += (_, _) => _lastRead = ReadTextAsync();
        CancelButton.Click += (_, _) => CancelRead("Cancelled.");
        FixBox.IsCheckedChanged += (_, _) => _ = Guard(OnFixChangedAsync);
        ColumnsBox.SelectionChanged += (_, _) => _ = Guard(OnColumnsChosenAsync);
        MergeButton.Click += (_, _) => _ = Guard(MergeAsync);
        SplitButton.Click += (_, _) => _ = Guard(SplitAsync);
        ImportButton.Click += (_, _) => _ = Guard(() => { Import(); return Task.CompletedTask; });
        CopyButton.Click += (_, _) => _ = Guard(CopyAsync);
        Grid.CellCommitted += (r, c, t) => OnCellEdited(r, c, t);
        Tabs.SelectionChanged += (_, _) => UpdateButtons();
        TextBox.GetObservable(TextBox.TextProperty).Subscribe(new ActionObserver(UpdateButtons));
        UpdateButtons();
    }

    static void Add(Panel p, Control c) { c.Margin = new Thickness(0, 0, 4, 2); p.Children.Add(c); }

    sealed class ActionObserver(Action a) : IObserver<string?>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(string? value) => a();
    }

    /// <summary>Supplies the image/region to read; null when nothing is shown.</summary>
    public Func<OcrTarget?>? Target { get; set; }

    /// <summary>Import into table…: TSV of the grid, the source and page it was read from.</summary>
    public event Action<string, ScanSource, int>? ImportRequested;

    public bool IsReading => _model.IsReading;
    public string StatusText => _status.Text ?? "";
    public string[][] Cells => _state?.Cells ?? [];
    public bool IsReread(int r, int c) => _state?.IsReread(r, c) == true;
    public Task LastRead => _lastRead;
    public bool HasGrid => _state != null && _state.Cells.Length > 0;

    Window Owner => (TopLevel.GetTopLevel(this) as Window)!;

    async Task Guard(Func<Task> a)
    {
        try { await a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("ocr panel: " + ex);
            SetStatus("Something went wrong: " + ex.Message, true);
        }
    }

    void SetStatus(string text, bool problem = false)
    {
        _status.Text = text;
        StatusIsProblem = problem;
        if (problem) _status.Foreground = ProblemText; else _status.ClearValue(TextBlock.ForegroundProperty);
    }

    async Task<bool> ConfirmAsync(string question) => await _dialogs.AskYesNoAsync(Owner, question, Caption, defaultNo: true);

    void UpdateButtons()
    {
        var reading = _model.IsReading;
        ReadButton.IsEnabled = !reading && _langs.Count > 0;
        CancelButton.IsEnabled = reading;
        var grid = HasGrid;
        MergeButton.IsEnabled = SplitButton.IsEnabled = ColumnsBox.IsEnabled = grid && !reading;
        ImportButton.IsEnabled = grid && !reading;
        CopyButton.IsEnabled = Tabs.SelectedItem == _gridPage ? grid : !string.IsNullOrEmpty(TextBox.Text);
    }

    // ---------- reading ----------

    /// <summary>Read text: the grid is replaced after a confirm when the user edited it.</summary>
    public async Task ReadTextAsync()
    {
        if (_model.IsReading || _asking) return;
        Grid.CommitEdit();
        if (_state?.HasEdits == true)
        {
            _asking = true;
            try { if (!await ConfirmAsync(ReplaceEditsQuestion)) return; }
            finally { _asking = false; }
            if (_model.IsReading) return;
        }
        OcrTarget? target;
        try { target = Target?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("ocr target: " + ex); target = null; }
        if (target == null) { SetStatus("Open a scan first: Open scan…, Scan this page, or an image from the document list.", true); return; }
        var lang = LanguageBox.SelectedItem as string ?? "";
        var ticket = _model.BeginRead();
        UpdateButtons();
        SetStatus(target.Region is { } r ? $"Reading text in the region ({r.Width}×{r.Height} px)…" : "Reading text in the whole image…");
        try
        {
            SKRectI? region = target.Region is { } g ? new SKRectI(g.Left, g.Top, g.Right, g.Bottom) : null;
            // The reader copies the region before its first await, so the shown bitmap is not touched off the UI thread.
            var words = await _reader.ReadAsync(target.Image, region, lang, ticket.Token);
            if (!_model.IsCurrent(ticket)) return; // stale: a newer read, a cancel, or another image
            ShowReading(words, target.Source, target.Page);
            var n = words.Count(w => !string.IsNullOrWhiteSpace(w.Text));
            int recovered = 0, stillEmpty = 0;
            // Second pass: cells left empty inside the table (lone characters are often skipped) are re-read one by one.
            var empty = _state!.EmptyCells();
            if (empty.Count > 0)
            {
                SetStatus($"Re-reading {empty.Count} empty cell{(empty.Count == 1 ? "" : "s")}…");
                var found = await _reader.ReadCellsAsync(target.Image, empty.Select(c => new SKRect(c.Box.Left, c.Box.Top, c.Box.Right, c.Box.Bottom)).ToList(),
                    _state.MedianWordHeight, lang, ticket.Token);
                if (!_model.IsCurrent(ticket)) return;
                recovered = _state.AddRereads(empty, found);
                if (recovered > 0) FillGrid();
                stillEmpty = _state.EmptyCells().Count;
            }
            ReportReading(n, recovered, stillEmpty);
        }
        catch (OperationCanceledException) { /* the canceller has set the status */ }
        catch (ObjectDisposedException) { /* the engine was shut down (the app is closing): nothing to report */ }
        catch (Quartermaster.Render.Ocr.OcrUnavailableException ex) { if (_model.IsCurrent(ticket)) SetStatus(ex.Message, true); }
        catch (InvalidDataException ex) { if (_model.IsCurrent(ticket)) SetStatus(ex.Message, true); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("ocr read failed: " + ex);
            if (_model.IsCurrent(ticket)) SetStatus("Reading failed: " + ex.Message, true);
        }
        finally
        {
            _model.EndRead(ticket);
            UpdateButtons();
        }
    }

    void ReportReading(int words, int recovered, int stillEmpty)
    {
        if (words == 0) { SetStatus("No text was found."); return; }
        var text = $"Read {words} word{(words == 1 ? "" : "s")}; {Cells.Length} row{(Cells.Length == 1 ? "" : "s")}.";
        if (recovered > 0) text += $" {recovered} empty cell{(recovered == 1 ? " was" : "s were")} re-read (tinted blue).";
        if (stillEmpty > 0) text += " " + SomeEmptyText;
        else text += " Check the grid, then Import into table…";
        SetStatus(text);
    }

    /// <summary>Shows a note about the loaded image (e.g. a multi-page TIFF) in the status line.</summary>
    public void ShowNote(string note) => SetStatus(note);

    /// <summary>Cancels the running read (Cancel, another image or region); its result is dropped.</summary>
    public void CancelRead(string? status = null)
    {
        if (!_model.IsReading) return;
        _model.CancelRead();
        if (status != null) SetStatus(status);
        UpdateButtons();
    }

    void ShowReading(IReadOnlyList<OcrWord> words, ScanSource source, int page)
    {
        _words = words;
        _readSource = source;
        _readPage = page;
        TextBox.Text = OcrResultModel.ToText(words);
        _state = NewState();
        FillGrid();
    }

    OcrGridState NewState()
    {
        var s = new OcrGridState(_words ?? [], FixBox.IsChecked == true);
        if (ColumnsBox.SelectedIndex > 0) s.SetColumns(ColumnsBox.SelectedIndex);
        return s;
    }

    // ---------- grid ----------

    void FillGrid()
    {
        _filling = true;
        try
        {
            var cells = Cells;
            var width = cells.Length == 0 ? 0 : cells.Max(r => r.Length);
            Grid.SetColumns(Enumerable.Range(0, width)
                .Select(c => new EditColumn((c + 1).ToString(CultureInfo.InvariantCulture), Width: 64)).ToList());
            Grid.SetRows(cells.Select(row => (IReadOnlyList<string>)Enumerable.Range(0, width).Select(c => c < row.Length ? row[c] : "").ToList()).ToList());
            for (var r = 0; r < cells.Length; r++)
                for (var c = 0; c < width; c++) Tint(r, c);
        }
        finally { _filling = false; }
        UpdateButtons();
    }

    // SetRows resets per-cell state, so the tints are applied after every fill.
    void Tint(int r, int c) =>
        Grid.SetCellTint(r, c, _state!.IsEdited(r, c) ? EditedBack : _state.IsReread(r, c) ? RereadBack : null);

    void OnCellEdited(int r, int c, string text)
    {
        if (_filling || _state == null || r < 0 || c < 0) return;
        _state.Edit(r, c, text);
        Tint(r, c);
    }

    /// <summary>Rebuild steps drop edits: confirm first.</summary>
    async Task<bool> MayRebuildAsync()
    {
        if (_state?.HasEdits != true) return true;
        var state = _state;
        if (!await ConfirmAsync(RebuildQuestion)) return false;
        return ReferenceEquals(state, _state) && !_model.IsReading; // the grid was replaced or a read started while the question was open
    }

    async Task OnFixChangedAsync()
    {
        if (_syncing || _words == null) return;
        Grid.CommitEdit();
        if (!await MayRebuildAsync())
        {
            _syncing = true;
            FixBox.IsChecked = FixBox.IsChecked != true;
            _syncing = false;
            return;
        }
        _state?.SetFixDigits(FixBox.IsChecked == true); // keeps the re-read cells and the columns
        FillGrid();
    }

    async Task OnColumnsChosenAsync()
    {
        if (_syncing) return;
        if (_state == null) { _columnsShown = ColumnsBox.SelectedIndex; return; }
        Grid.CommitEdit();
        if (!await MayRebuildAsync())
        {
            _syncing = true;
            ColumnsBox.SelectedIndex = _columnsShown;
            _syncing = false;
            return;
        }
        _columnsShown = ColumnsBox.SelectedIndex;
        _state.SetColumns(ColumnsBox.SelectedIndex <= 0 ? null : ColumnsBox.SelectedIndex);
        FillGrid();
        var got = Cells.Length == 0 ? 0 : Cells.Max(r => r.Length);
        if (ColumnsBox.SelectedIndex > 0 && got < ColumnsBox.SelectedIndex)
            SetStatus($"Only {got} column{(got == 1 ? "" : "s")} could be found: there are not enough gaps between the words.");
    }

    int SelectedColumn => Grid.CurrentCell?.Col ?? -1;

    async Task MergeAsync()
    {
        if (_state == null) return;
        Grid.CommitEdit();
        var c = SelectedColumn;
        var width = Cells.Length == 0 ? 0 : Cells.Max(r => r.Length);
        if (c < 0 || c >= width - 1) { SetStatus("Select a cell in a column that has a column to its right, then Merge joins the two."); return; }
        if (!await MayRebuildAsync() || SelectedColumn != c) return;
        _state.MergeColumns(c);
        FillGrid();
        SetStatus($"Columns {c + 1} and {c + 2} merged.");
    }

    async Task SplitAsync()
    {
        if (_state == null) return;
        Grid.CommitEdit();
        var c = SelectedColumn;
        if (c < 0) { SetStatus("Select a cell in the column to split."); return; }
        if (!await MayRebuildAsync() || SelectedColumn != c) return;
        var before = Cells.Length == 0 ? 0 : Cells.Max(r => r.Length);
        _state.SplitColumn(c);
        FillGrid();
        var after = Cells.Length == 0 ? 0 : Cells.Max(r => r.Length);
        SetStatus(after > before ? $"Column {c + 1} split in two." : "There is no gap between the words to split this column at.");
    }

    // ---------- import / copy ----------

    void Import()
    {
        if (_state == null || _readSource == null || !HasGrid) { SetStatus("Read text first."); return; }
        Grid.CommitEdit();
        ImportRequested?.Invoke(_state.ToTsv(), _readSource, _readPage);
    }

    /// <summary>Copy text: the Text box, or the grid as TSV when the Grid tab is shown.</summary>
    async Task CopyAsync()
    {
        Grid.CommitEdit();
        var grid = Tabs.SelectedItem == _gridPage;
        var text = grid ? _state?.ToTsv() ?? "" : TextBox.Text ?? "";
        if (text.Length == 0) return;
        try
        {
            await _dialogs.SetClipboardTextAsync(Owner, text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
            SetStatus(grid ? "The grid was copied (tab-separated)." : "The text was copied.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("ocr copy: " + ex.Message);
            SetStatus("The clipboard is busy; try again.", true);
        }
    }

    public void ShowGridTab() => Tabs.SelectedItem = _gridPage;

    /// <summary>Smoke: clicks Read text through the button.</summary>
    internal void ClickReadForSmoke() => _lastRead = ReadTextAsync();
}
