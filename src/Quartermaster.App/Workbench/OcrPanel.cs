using Quartermaster.Ocr;
using Quartermaster.Render.Ocr;

namespace Quartermaster.App;

/// <summary>What a read works on: the shown image, the region (null = whole image) and where it came from.</summary>
sealed record OcrTarget(Bitmap Image, Rectangle? Region, ScanSource Source, int Page);

/// <summary>
/// OCR panel beside the scan: Read text (Windows OCR, off the UI thread, one read at a time, Cancel), language, the Text box and an
/// editable Grid with Columns / Merge / Split, Import into table… and Copy text. OCR output is only imported through the
/// Paste table dialog the host opens.
/// </summary>
sealed class OcrPanel : UserControl
{
    public const int MinPanelWidth = 280;
    public const string ReplaceEditsQuestion = "Replace your edits with a new reading?";
    const string RebuildQuestion = "Rebuild the grid from the reading? Your edits to the grid will be lost.";
    static readonly Color EditedBack = Color.FromArgb(255, 248, 200);
    static readonly Color RereadBack = Color.FromArgb(214, 234, 255);

    readonly OcrService _service;
    readonly ScanSessionModel _model;
    readonly Button _read = new() { Text = "Read text", AutoSize = true };
    readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    readonly ComboBox _lang = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    readonly CheckBox _fix = new() { Text = "Fix common OCR digit mistakes", Checked = true, AutoSize = true };
    readonly Label _status = new() { Dock = DockStyle.Top, Height = 34, AutoEllipsis = true, Padding = new Padding(2, 2, 2, 0) };
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    readonly TabPage _textPage = new("Text"), _gridPage = new("Grid");
    readonly TextBox _text = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, AcceptsReturn = true, AcceptsTab = true };
    readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        RowHeadersWidth = 28, SelectionMode = DataGridViewSelectionMode.CellSelect, EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
        BackgroundColor = SystemColors.AppWorkspace, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
    };
    readonly ComboBox _columns = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
    readonly Button _merge = new() { Text = "Merge", AutoSize = true };
    readonly Button _split = new() { Text = "Split", AutoSize = true };
    readonly Button _import = new() { Text = "Import into table…", AutoSize = true, Enabled = false };
    readonly Button _copy = new() { Text = "Copy text", AutoSize = true, Enabled = false };

    IReadOnlyList<OcrWord>? _words;
    OcrGridState? _state;
    ScanSource? _readSource;
    int _readPage;
    bool _filling, _syncing;
    Task _lastRead = Task.CompletedTask;

    public OcrPanel(OcrService service, ScanSessionModel model)
    {
        _service = service;
        _model = model;
        MinimumSize = new Size(MinPanelWidth, 0);

        var row1 = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(2, 2, 0, 0) };
        row1.Controls.Add(_read);
        row1.Controls.Add(_cancel);
        row1.Controls.Add(new Label { Text = "Language:", AutoSize = true, Padding = new Padding(6, 7, 0, 0) });
        row1.Controls.Add(_lang);
        var row2 = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(2, 0, 0, 0) };
        row2.Controls.Add(_fix);

        _columns.Items.Add("Auto");
        for (var i = 1; i <= 16; i++) _columns.Items.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _columns.SelectedIndex = 0;
        var gridBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
        gridBar.Controls.Add(new Label { Text = "Columns:", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        gridBar.Controls.Add(_columns);
        gridBar.Controls.Add(_merge);
        gridBar.Controls.Add(_split);
        _gridPage.Controls.Add(_grid);
        _gridPage.Controls.Add(gridBar);
        _textPage.Controls.Add(_text);
        _tabs.TabPages.Add(_gridPage);
        _tabs.TabPages.Add(_textPage);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(2) };
        bottom.Controls.Add(_import);
        bottom.Controls.Add(_copy);

        Controls.Add(_tabs);
        Controls.Add(bottom);
        Controls.Add(_status);
        Controls.Add(row2);
        Controls.Add(row1);

        var langs = OcrService.Languages();
        foreach (var l in langs) _lang.Items.Add(l);
        var def = OcrService.DefaultLanguage();
        if (def != null) _lang.SelectedItem = langs.FirstOrDefault(l => string.Equals(l, def, StringComparison.OrdinalIgnoreCase)) ?? def;
        if (_lang.SelectedIndex < 0 && _lang.Items.Count > 0) _lang.SelectedIndex = 0;
        _lang.Enabled = langs.Count > 0;
        SetStatus(langs.Count == 0 ? OcrService.NoLanguageMessage : "Drag a region on the image (or read the whole image), then Read text.", langs.Count == 0);

        _read.Click += (_, _) => _lastRead = ReadTextAsync();
        _cancel.Click += (_, _) => CancelRead("Cancelled.");
        _fix.CheckedChanged += (_, _) => Guard(OnFixChanged);
        _columns.SelectionChangeCommitted += (_, _) => Guard(OnColumnsChosen);
        _merge.Click += (_, _) => Guard(Merge);
        _split.Click += (_, _) => Guard(Split);
        _import.Click += (_, _) => Guard(Import);
        _copy.Click += (_, _) => Guard(Copy);
        _grid.CellValueChanged += (_, e) => Guard(() => OnCellEdited(e.RowIndex, e.ColumnIndex));
        _grid.CellFormatting += OnCellFormatting;
        _grid.DataError += (_, e) => e.ThrowException = false;
        _tabs.SelectedIndexChanged += (_, _) => UpdateButtons();
        _text.TextChanged += (_, _) => UpdateButtons();
        UpdateButtons();
    }

    /// <summary>Supplies the image/region to read; null when nothing is shown.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<OcrTarget?>? Target { get; set; }

    /// <summary>Import into table…: TSV of the grid, the source and page it was read from.</summary>
    public event Action<string, ScanSource, int>? ImportRequested;

    public bool IsReading => _model.IsReading;
    public string StatusText => _status.Text;
    public string[][] Cells => _state?.Cells ?? [];
    public bool IsReread(int r, int c) => _state?.IsReread(r, c) == true;
    public Task LastRead => _lastRead;
    public bool HasGrid => _state != null && _state.Cells.Length > 0;

    void Guard(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("ocr panel: " + ex);
            SetStatus("Something went wrong: " + ex.Message, true);
        }
    }

    void SetStatus(string text, bool problem = false)
    {
        _status.Text = text;
        _status.ForeColor = problem ? Color.Firebrick : SystemColors.ControlText;
    }

    bool Confirm(string question)
    {
        if (Program.Smoke) { Program.AppLog("smoke: ocr confirm '" + question + "' answered yes"); return true; }
        return MessageBox.Show(FindForm(), question, "Read text", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    void UpdateButtons()
    {
        var reading = _model.IsReading;
        _read.Enabled = !reading;
        _cancel.Enabled = reading;
        var grid = HasGrid;
        _merge.Enabled = _split.Enabled = _columns.Enabled = grid && !reading;
        _import.Enabled = grid && !reading;
        _copy.Enabled = _tabs.SelectedTab == _gridPage ? grid : _text.TextLength > 0;
    }

    // ---------- reading ----------

    /// <summary>Read text: the grid is replaced after a confirm when the user edited it.</summary>
    public async Task ReadTextAsync()
    {
        if (_model.IsReading) return;
        if (_state?.HasEdits == true && !Confirm(ReplaceEditsQuestion)) return;
        OcrTarget? target;
        try { target = Target?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("ocr target: " + ex); target = null; }
        if (target == null) { SetStatus("Open a scan first: Open scan…, Scan this page, or an image from the document list.", true); return; }
        var lang = _lang.SelectedItem as string ?? "";
        var ticket = _model.BeginRead();
        UpdateButtons();
        SetStatus(target.Region is { } r ? $"Reading text in the region ({r.Width}×{r.Height} px)…" : "Reading text in the whole image…");
        try
        {
            // The service copies the region before its first await, so the shown bitmap is not touched off the UI thread.
            var words = await _service.ReadAsync(target.Image, target.Region, lang, ticket.Token);
            if (IsDisposed || !_model.IsCurrent(ticket)) return; // stale: a newer read, a cancel, or another image
            ShowReading(words, target.Source, target.Page);
            var n = words.Count(w => !string.IsNullOrWhiteSpace(w.Text));
            int recovered = 0, stillEmpty = 0;
            // Second pass: cells left empty inside the table (lone characters are often skipped) are re-read one by one.
            var empty = _state!.EmptyCells();
            if (empty.Count > 0)
            {
                SetStatus($"Re-reading {empty.Count} empty cell{(empty.Count == 1 ? "" : "s")}…");
                // Still current here, so the bitmap is the one shown; ReadCellsAsync copies the cells before its first await.
                var found = await _service.ReadCellsAsync(target.Image, empty.Select(c => c.Box).ToList(), _state.MedianWordHeight, lang, ticket.Token);
                if (IsDisposed || !_model.IsCurrent(ticket)) return;
                recovered = _state.AddRereads(empty, found);
                if (recovered > 0) FillGrid();
                stillEmpty = _state.EmptyCells().Count;
            }
            ReportReading(n, recovered, stillEmpty);
        }
        catch (OperationCanceledException) { /* the canceller has set the status */ }
        catch (OcrUnavailableException ex) { if (_model.IsCurrent(ticket)) SetStatus(ex.Message, true); }
        catch (InvalidDataException ex) { if (_model.IsCurrent(ticket)) SetStatus(ex.Message, true); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("ocr read failed: " + ex);
            if (_model.IsCurrent(ticket)) SetStatus("Reading failed: " + ex.Message, true);
        }
        finally
        {
            _model.EndRead(ticket);
            if (!IsDisposed) UpdateButtons();
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

    public const string SomeEmptyText = "Some cells came back empty (single characters are often missed): type them in.";

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
        _text.Text = OcrResultModel.ToText(words).Replace("\n", "\r\n");
        _state = NewState();
        FillGrid();
    }

    OcrGridState NewState()
    {
        var s = new OcrGridState(_words ?? [], _fix.Checked);
        if (_columns.SelectedIndex > 0) s.SetColumns(_columns.SelectedIndex);
        return s;
    }

    // ---------- grid ----------

    void FillGrid()
    {
        _filling = true;
        try
        {
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            _grid.Columns.Clear();
            var cells = Cells;
            var width = cells.Length == 0 ? 0 : cells.Max(r => r.Length);
            for (var c = 0; c < width; c++)
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = (c + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    SortMode = DataGridViewColumnSortMode.NotSortable, Width = 64,
                });
            foreach (var row in cells)
                _grid.Rows.Add(Enumerable.Range(0, width).Select(c => (object)(c < row.Length ? row[c] : "")).ToArray());
        }
        finally
        {
            _grid.ResumeLayout();
            _filling = false;
        }
        UpdateButtons();
    }

    void OnCellEdited(int r, int c)
    {
        if (_filling || _state == null || r < 0 || c < 0) return;
        _state.Edit(r, c, Convert.ToString(_grid[c, r].Value) ?? "");
        _grid.InvalidateCell(c, r);
    }

    void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (_state == null || e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (_state.IsEdited(e.RowIndex, e.ColumnIndex)) e.CellStyle!.BackColor = EditedBack;
        else if (_state.IsReread(e.RowIndex, e.ColumnIndex)) e.CellStyle!.BackColor = RereadBack;
    }

    /// <summary>Rebuild steps drop edits: confirm first.</summary>
    bool MayRebuild() => _state?.HasEdits != true || Confirm(RebuildQuestion);

    void OnFixChanged()
    {
        if (_syncing || _words == null) return;
        if (!MayRebuild())
        {
            _syncing = true;
            _fix.Checked = !_fix.Checked;
            _syncing = false;
            return;
        }
        _state = NewState();
        FillGrid();
    }

    int _columnsShown;

    void OnColumnsChosen()
    {
        if (_state == null) { _columnsShown = _columns.SelectedIndex; return; }
        if (!MayRebuild()) { _columns.SelectedIndex = _columnsShown; return; }
        _columnsShown = _columns.SelectedIndex;
        _state.SetColumns(_columns.SelectedIndex <= 0 ? null : _columns.SelectedIndex);
        FillGrid();
        var got = Cells.Length == 0 ? 0 : Cells.Max(r => r.Length);
        if (_columns.SelectedIndex > 0 && got < _columns.SelectedIndex)
            SetStatus($"Only {got} column{(got == 1 ? "" : "s")} could be found: there are not enough gaps between the words.");
    }

    int SelectedColumn => _grid.CurrentCell?.ColumnIndex ?? -1;

    void Merge()
    {
        if (_state == null) return;
        var c = SelectedColumn;
        var width = _grid.Columns.Count;
        if (c < 0 || c >= width - 1) { SetStatus("Select a cell in a column that has a column to its right, then Merge joins the two."); return; }
        if (!MayRebuild()) return;
        _state.MergeColumns(c);
        FillGrid();
        SetStatus($"Columns {c + 1} and {c + 2} merged.");
    }

    void Split()
    {
        if (_state == null) return;
        var c = SelectedColumn;
        if (c < 0) { SetStatus("Select a cell in the column to split."); return; }
        if (!MayRebuild()) return;
        var before = _grid.Columns.Count;
        _state.SplitColumn(c);
        FillGrid();
        SetStatus(_grid.Columns.Count > before ? $"Column {c + 1} split in two." : "There is no gap between the words to split this column at.");
    }

    // ---------- import / copy ----------

    void Import()
    {
        if (_state == null || _readSource == null || !HasGrid) { SetStatus("Read text first."); return; }
        if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
        ImportRequested?.Invoke(_state.ToTsv(), _readSource, _readPage);
    }

    /// <summary>Copy text: the Text box, or the grid as TSV when the Grid tab is shown.</summary>
    void Copy()
    {
        if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
        var text = _tabs.SelectedTab == _gridPage ? _state?.ToTsv() ?? "" : _text.Text;
        if (text.Length == 0) return;
        try
        {
            Clipboard.SetText(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            SetStatus(_tabs.SelectedTab == _gridPage ? "The grid was copied (tab-separated)." : "The text was copied.");
        }
        catch (System.Runtime.InteropServices.ExternalException) { SetStatus("The clipboard is busy; try again.", true); }
    }

    // ---------- smoke ----------

    public void ShowGridTab() => _tabs.SelectedTab = _gridPage;

    /// <summary>Smoke: clicks Read text through the button.</summary>
    public void ClickReadForSmoke() => _lastRead = ReadTextAsync();

    /// <summary>Smoke: clicks Import into table…; false when the button is disabled.</summary>
    public bool ClickImportForSmoke()
    {
        if (!_import.Enabled) return false;
        Guard(Import);
        return true;
    }
}
