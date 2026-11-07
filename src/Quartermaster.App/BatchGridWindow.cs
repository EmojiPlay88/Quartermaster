using Quartermaster.Core;
using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// The batch grid of a multi-unit calculation run: one row per unit, one editable column per formula variable, then Result, Ledger and
/// Exceptions. The grid is virtual (a run can have ~1,200 units and a row costs a formula evaluation), so only the rows on screen are
/// asked for. Calculate all fills every row from the engine, Apply all applies the rows that match (wrong rows are skipped), Apply row
/// applies the current row. In semi-automatic mode a cheat sheet docked on the right follows the selected row.
/// </summary>
sealed class BatchGridWindow : Form
{
    readonly BatchModel _model;
    readonly ModuleSession _s;
    readonly string _runId;
    readonly Action<int>? _openRow;
    readonly BreakdownTip _tip = new();
    readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, VirtualMode = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        AutoGenerateColumns = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = false,
        BackgroundColor = SystemColors.Window, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
    };
    readonly Button _calcAll = new() { Text = "Calculate all", AutoSize = true };
    readonly Button _applyAll = new() { Text = "Apply all", AutoSize = true };
    readonly Button _applyRow = new() { Text = "Apply row", AutoSize = true };
    readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 8, 3, 3) };
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 120 };
    readonly CheatSheetWindow? _cheat;
    GameSession? _attached;
    int _varCount;
    int _shownRows = -1;

    public BatchGridWindow(BatchModel model, ModuleSession s, string runId, Action<int>? openRow = null)
    {
        _model = model;
        _s = s;
        _runId = runId;
        _openRow = openRow;
        Text = model.Title;
        ShowIcon = false;
        ClientSize = new Size(900, 520);

        BuildColumns();
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(6, 4, 6, 4), WrapContents = false };
        bar.Controls.AddRange(new Control[] { _calcAll, _applyAll, _applyRow, _status });

        // Later controls dock first: grid (fill), then splitter and cheat sheet on the right, the button bar across the bottom.
        Controls.Add(_grid);
        if (s.Game?.State.Difficulty == Difficulty.SemiAuto)
        {
            _cheat = new CheatSheetWindow { TopLevel = false, FormBorderStyle = FormBorderStyle.None, Dock = DockStyle.Right, Width = 300, Visible = true };
            Controls.Add(new Splitter { Dock = DockStyle.Right, Width = 4 });
            Controls.Add(_cheat);
        }
        Controls.Add(bar);

        _grid.CellValueNeeded += (_, e) => Safe(() => OnValueNeeded(e));
        _grid.CellValuePushed += (_, e) => Safe(() => OnValuePushed(e));
        _grid.CellFormatting += (_, e) => Safe(() => OnFormatting(e));
        _grid.CellToolTipTextNeeded += (_, e) => Safe(() => OnToolTip(e));
        _tip.Track(_grid, LedgerTip);
        _grid.ShowCellToolTips = true; // native tips stay on for the "doesn't match" hint; OnToolTip returns nothing for the Ledger column, so they never overlap the breakdown
        _grid.CurrentCellChanged += (_, _) => Safe(UpdateCheat);
        _grid.CellDoubleClick += (_, e) => Safe(() => { if (e.RowIndex >= 0 && e.ColumnIndex == 0) _openRow?.Invoke(e.RowIndex); });
        _grid.DataError += (_, e) => e.ThrowException = false;
        _grid.EditingControlShowing += (_, e) =>
        {
            if (e.Control is ComboBox cb) cb.DropDownStyle = ComboBoxStyle.DropDown;   // key columns stay typeable
        };
        _grid.CellValidating += (_, e) => Safe(() =>
        {
            if (e.ColumnIndex >= 1 && e.ColumnIndex <= _varCount && _grid.Columns[e.ColumnIndex] is DataGridViewComboBoxColumn combo
                && e.FormattedValue?.ToString() is { Length: > 0 } text && !combo.Items.Contains(text))
                combo.Items.Add(text);
        });
        _calcAll.Click += (_, _) => Safe(CalculateAll);
        _applyAll.Click += (_, _) => Safe(ApplyAll);
        _applyRow.Click += (_, _) => Safe(ApplyRow);
        _debounce.Tick += (_, _) => { _debounce.Stop(); Safe(Refresh_); };

        _s.GameReplaced += OnGameReplaced;
        Attach();
        Refresh_();
    }

    void BuildColumns()
    {
        _grid.Columns.Clear();
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Unit", ReadOnly = true, Width = 150, SortMode = DataGridViewColumnSortMode.NotSortable });
        foreach (var v in _model.Variables)
        {
            if (_model.ChoicesFor(v) is { Count: > 0 } keys)
            {
                // Key columns: a dropdown of the table keys. Typing stays allowed (editing control is a DropDown); the text still goes through the checked comparison.
                var col = new DataGridViewComboBoxColumn
                {
                    HeaderText = v, Width = 90, SortMode = DataGridViewColumnSortMode.NotSortable,
                    DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox, FlatStyle = FlatStyle.Standard,
                };
                col.Items.Add("");
                foreach (var k in keys) col.Items.Add(k);
                _grid.Columns.Add(col);
            }
            else _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = v, Width = 80, SortMode = DataGridViewColumnSortMode.NotSortable });
        }
        _varCount = _model.Variables.Count;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Result", ReadOnly = true, Width = 90, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Ledger", ReadOnly = true, Width = 200, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Exceptions", ReadOnly = true, Width = 160, SortMode = DataGridViewColumnSortMode.NotSortable });
    }

    /// <summary>The cheat sheet takes a share of the width that suits a narrow document area as well as a wide one.</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_cheat != null) _cheat.Width = Math.Clamp(ClientSize.Width * 2 / 5, 200, 380);
    }

    // ---- virtual grid callbacks (only visible rows are requested) ----

    int ResultCol => 1 + _varCount;

    void OnValueNeeded(DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _model.RowCount) { e.Value = ""; return; }
        var row = _model.Row(e.RowIndex);
        var c = e.ColumnIndex;
        if (c >= 1 && c <= _varCount && _grid.Columns[c] is DataGridViewComboBoxColumn combo && c - 1 < row.Cells.Count
            && row.Cells[c - 1] is { Length: > 0 } typed && !combo.Items.Contains(typed))
            combo.Items.Add(typed);   // a typed or calculated value that is not one of the listed keys must still display
        e.Value = c == 0 ? row.Label
            : c <= _varCount ? (c - 1 < row.Cells.Count ? row.Cells[c - 1] : "")
            : c == ResultCol ? (row.Applied && row.Result.Length == 0 ? "Applied" : row.Result)
            : c == ResultCol + 1 ? row.LedgerText
            : row.Exceptions;
    }

    void OnValuePushed(DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _model.RowCount || e.ColumnIndex < 1 || e.ColumnIndex > _varCount) return;
        _model.SetCell(e.RowIndex, _model.Variables[e.ColumnIndex - 1], e.Value?.ToString() ?? "");
        _grid.InvalidateRow(e.RowIndex);
        UpdateCheat();
    }

    void OnFormatting(DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _model.RowCount) return;
        var c = e.ColumnIndex;
        var row = _model.Row(e.RowIndex);
        if (c >= 1 && c <= _varCount && c - 1 < row.Wrong.Count && row.Wrong[c - 1])
            e.CellStyle!.BackColor = Color.MistyRose;
        else if (row.Applied)
            e.CellStyle!.ForeColor = SystemColors.GrayText;
        if (c == ResultCol + 2 && row.Exceptions.Length > 0 && !row.Applied) e.CellStyle!.ForeColor = Color.Firebrick;
    }

    /// <summary>History of the first ledger value this row would change (what happened before, never the pending result).</summary>
    Breakdown? LedgerTip(int row, int col)
    {
        if (col != ResultCol + 1 || row >= _model.RowCount || _s.Game == null) return null;
        var o = _model.OutputsOf(row);
        return o.Count == 0 ? null : LedgerBreakdown.For(_s.Game.State, _s.Rules, _s.Model, o[0].Key, o[0].PropertyId);
    }

    void OnToolTip(DataGridViewCellToolTipTextNeededEventArgs e)
    {
        var c = e.ColumnIndex;
        if (e.RowIndex < 0 || e.RowIndex >= _model.RowCount || c < 1 || c > _varCount) return;
        if (!_model.Row(e.RowIndex).Wrong[c - 1] || _s.Game == null) return;
        var v = _model.Variables[c - 1];
        e.ToolTipText = _model.ModelFor(e.RowIndex).Evaluate(_s.Game.State).Boxes.FirstOrDefault(b => b.Variable == v)?.Tooltip ?? "Doesn't match";
    }

    // ---- actions ----

    void CalculateAll()
    {
        if (!_grid.EndEdit()) return;
        using (WaitCursor())
        {
            _model.CalculateAll();
            _grid.Invalidate();
        }
        _status.Text = "Calculated " + _model.RowCount + " row(s).";
        UpdateCheat();
    }

    void ApplyAll()
    {
        if (!_grid.EndEdit()) return;
        var before = _model.RowCount;
        int n;
        using (WaitCursor()) n = _model.ApplyAll();
        // The run may be gone now (all applied): the debounced refresh closes the window.
        _status.Text = $"Applied {n} row(s); rows with a red cell were skipped.";
        _debounce.Stop();
        Refresh_();
        if (!IsDisposed && before > 0 && n == 0) _status.Text = "Nothing applied: check the red cells, or press Calculate all.";
    }

    void ApplyRow()
    {
        if (!_grid.EndEdit()) return;
        var r = _grid.CurrentCell?.RowIndex ?? -1;
        if (r < 0 || r >= _model.RowCount) { _status.Text = "Select a row first."; return; }
        var ok = _model.ApplyRow(r);
        _status.Text = ok ? "Row applied." : "That row does not match yet: fix the red cells or press Calculate all.";
        _debounce.Stop();
        Refresh_();
    }

    static IDisposable WaitCursor()
    {
        var old = Cursor.Current;
        Cursor.Current = Cursors.WaitCursor;
        return new Restore(old);
    }

    sealed class Restore(Cursor? c) : IDisposable { public void Dispose() => Cursor.Current = c; }

    // ---- refresh and lifetime ----

    void Attach()
    {
        if (_attached != null) _attached.Changed -= OnGameChanged;
        _attached = _s.Game;
        if (_attached != null) _attached.Changed += OnGameChanged;
    }

    void OnGameReplaced() { Attach(); QueueRefresh(); }
    void OnGameChanged(GameCommand _) => QueueRefresh();

    /// <summary>Game events may come from any thread and in bursts (Apply all): one debounced refresh on the UI thread.</summary>
    void QueueRefresh()
    {
        try
        {
            if (IsDisposed) return;
            if (InvokeRequired) { if (IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) { _debounce.Stop(); _debounce.Start(); } }); }
            else { _debounce.Stop(); _debounce.Start(); }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { /* closing */ }
    }

    bool RunExists => _s.Game?.State.Pending.Any(r => r != null && r.Id == _runId) == true;

    void Refresh_()
    {
        if (IsDisposed) return;
        if (!RunExists) { Close(); return; }       // finished, or the game was replaced
        _model.Invalidate();
        var n = _model.RowCount;
        if (n != _shownRows) { _shownRows = n; _grid.RowCount = n; }
        Text = _model.Title;
        _grid.Invalidate();
        UpdateCheat();
    }

    void UpdateCheat()
    {
        if (_cheat == null || _cheat.IsDisposed) return;
        var r = _grid.CurrentCell?.RowIndex ?? -1;
        if (r < 0 || r >= _model.RowCount || _s.Rules is not { } rules) { _cheat.Render(Array.Empty<CheatRow>()); return; }
        _model.Row(r); // makes sure the row has been evaluated (LastExpected)
        _cheat.Render(_model.ModelFor(r).LastExpected is { } exp ? CheatSheetModel.Build(exp, rules) : Array.Empty<CheatRow>());
    }

    void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("batch grid: " + ex.Message);
            if (!IsDisposed) _status.Text = "Something went wrong: " + ex.Message;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _s.GameReplaced -= OnGameReplaced;
            if (_attached != null) _attached.Changed -= OnGameChanged;
            _debounce.Dispose();
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- smoke hooks ----

    public int SmokeRowCount => _grid.RowCount;
    public CheatSheetWindow? CheatSheet => _cheat;
    public int SmokeWrongCells => Enumerable.Range(0, Math.Min(_model.RowCount, 5000)).Sum(i => _model.Row(i).Wrong.Count(w => w));
    public void SmokeCalculateAll() => _calcAll.PerformClick();
    public void SmokeApplyAll() => _applyAll.PerformClick();
    public void SmokeSetCell(int row, int col, string text) { _model.SetCell(row, _model.Variables[col - 1], text); _grid.InvalidateRow(row); }
    public void SmokeSelectRow(int row) { if (row >= 0 && row < _grid.RowCount) _grid.CurrentCell = _grid.Rows[row].Cells[0]; }
    public string SmokeCellText(int row, int col) => _grid.Rows[row].Cells[col].FormattedValue?.ToString() ?? "";

    public void SaveScreenshot(string path)
    {
        PerformLayout();
        Refresh_();
        Update();
        using var bmp = new Bitmap(Math.Max(Width, 1), Math.Max(Height, 1));
        DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Program.AppLog($"smoke: batch grid screenshot saved to {path} ({Width}x{Height}, state {WindowState}, grid {_grid.Width}x{_grid.Height})");
    }
}
