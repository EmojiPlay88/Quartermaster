using System.Globalization;
using System.Text;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>
/// MDI child listing holders and units with ledger values, one column per property, split into tabs (Units, Depots &amp; Global, Everything and
/// the player's own tabs, see LedgerWindow.Tabs.cs). Unit rows can show their counter (LedgerWindow.Counters.cs). Cells are editable (with a required note).
/// Rows and pictures come only from the player's view.
/// </summary>
sealed partial class LedgerWindow : Form
{
    const int FixedCols = 2; // Name, Kind
    readonly ModuleSession _s;
    readonly Func<IActionPort?>? _actions;
    readonly Action<string>? _centre;
    readonly ToolStrip _bar = new() { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System, LayoutStyle = ToolStripLayoutStyle.Flow };   // wraps onto a second line when narrow
    readonly ToolStripTextBox _filter = new() { AutoSize = false, Width = 160, ToolTipText = "Filter by name or kind" };
    readonly ToolStripButton _export = new("Export CSV…") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Save the current tab's rows and columns as a CSV file" };
    readonly ToolStripButton _showEmpty = new("Show units without values") { DisplayStyle = ToolStripItemDisplayStyle.Text, CheckOnClick = true };
    readonly ToolStripButton _refresh = new("Refresh") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    readonly DataGridView _grid = new();
    readonly BreakdownTip _tip = new();
    readonly ToolTip _headerTip = new() { ShowAlways = true, UseAnimation = false, UseFading = false, AutoPopDelay = 30000 };
    (int Col, bool Shown) _headerAt = (-1, false);
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 200 };
    Font? _boldFont;
    IReadOnlyList<PropertyDef> _cols = Array.Empty<PropertyDef>();
    List<LedgerRowEx> _rows = new();
    LedgerTabResult _result = new(Array.Empty<PropertyDef>(), Array.Empty<LedgerRowEx>(), 0, null);
    string _colSignature = "";
    int _sortCol = -1;
    bool _sortAsc = true;
    GameSession? _game;
    bool _closing;

    public LedgerWindow(ModuleSession s, Func<IActionPort?>? actions = null, Action<string>? centreOnPiece = null)
    {
        _s = s;
        _actions = actions;
        _centre = centreOnPiece;
        Text = "Ledger";
        Size = new Size(820, 560);
        _bar.Items.Add(new ToolStripLabel("Filter:"));
        _bar.Items.Add(_filter);
        _bar.Items.Add(new ToolStripSeparator());
        AddTabToolbar();
        _bar.Items.Add(new ToolStripSeparator());
        _bar.Items.Add(_export);
        _bar.Items.Add(_showEmpty);
        _bar.Items.Add(_refresh);

        _grid.Dock = DockStyle.Fill;
        _grid.VirtualMode = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.MultiSelect = true;   // Show in Ledger selects the rows of several counters
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        _grid.BackgroundColor = SystemColors.AppWorkspace;
        _grid.CellValueNeeded += OnCellValueNeeded;
        _grid.CellValuePushed += OnCellValuePushed;
        _grid.CellFormatting += OnCellFormatting;
        _grid.CellBeginEdit += (_, e) => { if (e.RowIndex < 0 || e.RowIndex >= _rows.Count || _rows[e.RowIndex].IsGroupHeader) e.Cancel = true; };
        _tip.Track(_grid, BreakdownFor);
        // The breakdown tip turns the grid's own tooltips off, so property notes on the column headers get their own tooltip.
        _grid.CellMouseMove += (_, e) => Run(() => HeaderTip(e.RowIndex, e.ColumnIndex));
        _grid.MouseLeave += (_, _) => HideHeaderTip();
        _grid.Scroll += (_, _) => HideHeaderTip();
        _grid.ColumnHeaderMouseClick += (_, e) => Run(() => SortBy(e.ColumnIndex));
        _grid.DataError += (_, e) => e.ThrowException = false;
        InitCounters();

        Controls.Add(_grid);
        Controls.Add(_banner);
        Controls.Add(_tabStrip);
        Controls.Add(_bar);

        _filter.TextChanged += (_, _) => Kick();
        _showEmpty.CheckedChanged += (_, _) => Kick();
        _refresh.Click += (_, _) => Run(Reload);
        _export.Click += (_, _) => Run(ExportCsv);
        _debounce.Tick += (_, _) => { _debounce.Stop(); Run(Reload); };

        s.RulesChanged += OnRulesChanged;
        s.GameReplaced += OnGameReplaced;
        s.ViewChanged += OnViewChanged;
        LedgerTabStore.Changed += OnStoreChanged;
        HookGame();
        Run(InitTabs);
        Run(Reload);
    }

    void Kick() { _debounce.Stop(); _debounce.Start(); }

    void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("ledger window failed: " + ex);
            _s.Log.Add(DiagnosticSeverity.Error, "ledger", "The ledger window failed: " + ex.Message);
        }
    }

    void OnUi(Action a)
    {
        if (IsDisposed || _closing || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(() => { if (!IsDisposed && !_closing) a(); }); else a();
    }

    void HookGame()
    {
        if (_game != null) _game.Changed -= OnGameChanged;
        if (_game != null) _game.Rewound -= OnRewound;
        _game = _s.Game;
        if (_game != null) _game.Changed += OnGameChanged;
        if (_game != null) _game.Rewound += OnRewound;
    }

    void OnGameChanged(GameCommand _) => OnUi(Kick);
    void OnRewound() => OnUi(Kick);
    void OnGameReplaced() => OnUi(() => { HookGame(); Kick(); });
    void OnRulesChanged() => OnUi(() => { Run(RefreshSides); Kick(); });
    void OnViewChanged() => OnUi(Kick);   // debounced: a burst of changes reloads once

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        Run(FlushPrefs);
        Unhook();
    }

    void Unhook()
    {
        _closing = true;
        _debounce.Stop();
        _s.RulesChanged -= OnRulesChanged;
        _s.GameReplaced -= OnGameReplaced;
        _s.ViewChanged -= OnViewChanged;
        LedgerTabStore.Changed -= OnStoreChanged;
        if (_game != null) _game.Changed -= OnGameChanged;
        if (_game != null) _game.Rewound -= OnRewound;
        _game = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Unhook();
            _debounce.Dispose();
            _saveTimer.Dispose();
            _tip.Dispose();
            _headerTip.Dispose();
            DisposeCounters();
            _boldFont?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------- data ----------

    void Reload()
    {
        if (IsDisposed) return;
        if (_grid.IsCurrentCellInEditMode) { Kick(); return; } // never pull the cell out from under the user
        var pack = _s.Rules;
        var game = _s.Game;
        var keepKeys = SelectedKeys();
        var firstKey = FirstVisibleKey();

        var side = SideFilter;
        var group = Grouping;
        _result = pack == null || game == null || _s.View is not { } view
            ? new LedgerTabResult(pack == null ? Array.Empty<PropertyDef>() : LedgerTabs.ColumnsFor(_tab, pack), Array.Empty<LedgerRowEx>(), 0, null)
            : LedgerTabs.Rows(_tab, view, pack, _s.Model, _filter.Text, side, group, _showEmpty.Checked);
        _cols = _result.Columns;
        UpdateTabCounts(pack, game);
        ShowBanner(_result.FilterError);

        var sig = _tab.Key + "\u0002" + _showCounters.Checked + "\u0002" + string.Join("|", _cols.Select(c => c.Id + "\u0001" + c.Name));
        if (sig != _colSignature) { _colSignature = sig; RebuildColumns(); }

        _rows = _result.Rows.ToList();
        ApplySort();
        ResetCounterImages();
        _grid.RowTemplate.Height = RowHeight;
        _grid.RowCount = 0;
        _grid.RowCount = _rows.Count;
        Restore(keepKeys, firstKey);
        _grid.Invalidate();
    }

    void HeaderTip(int row, int col)
    {
        if (row >= 0 || col < 0 || col >= _grid.Columns.Count || _grid.Columns[col].ToolTipText is not { Length: > 0 } text) { HideHeaderTip(); return; }
        if (_headerAt == (col, true)) return;
        _headerAt = (col, true);
        var p = _grid.PointToClient(Cursor.Position);
        _headerTip.Show(text, _grid, p.X + 12, p.Y + 18);
    }

    void HideHeaderTip()
    {
        if (!_headerAt.Shown) return;
        _headerAt = (-1, false);
        try { _headerTip.Hide(_grid); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    bool _rebuildingColumns;

    void RebuildColumns()
    {
        _rebuildingColumns = true;
        try
        {
            _grid.Columns.Clear();
            var pad = NameImagePad;
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = "Name", ReadOnly = true, Width = 180 + pad, SortMode = DataGridViewColumnSortMode.Programmatic,
                DefaultCellStyle = new DataGridViewCellStyle { Padding = new Padding(pad, 0, 0, 0) },   // the counter picture is painted in the padding
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Kind", ReadOnly = true, Width = 140, SortMode = DataGridViewColumnSortMode.Programmatic });
            foreach (var c in _cols)
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = c.Name, ToolTipText = c.Notes ?? "", Width = 90, SortMode = DataGridViewColumnSortMode.Programmatic,
                    DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
                });
            ApplyColumnWidths();
            if (_sortCol >= _grid.Columns.Count) _sortCol = -1;
        }
        finally { _rebuildingColumns = false; }
    }

    HashSet<string> SelectedKeys()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (DataGridViewRow r in _grid.SelectedRows)
            if (r.Index >= 0 && r.Index < _rows.Count) set.Add(_rows[r.Index].Row.Key);
        return set;
    }

    string? FirstVisibleKey()
    {
        try
        {
            var i = _grid.FirstDisplayedScrollingRowIndex;
            return i >= 0 && i < _rows.Count ? _rows[i].Row.Key : null;
        }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Selects and scrolls to the row of a holder (used by the alert bar). Moves to the Depots &amp; Global tab and clears the filter when the row is not shown.</summary>
    public void SelectHolder(string holderId)
    {
        try
        {
            var key = LedgerKey.Holder(holderId);
            if (_rows.FindIndex(r => r.Row.Key == key) < 0)
            {
                if (_tab.Kind is not (LedgerTabKind.Holders or LedgerTabKind.Everything)) SelectTab("holders");
                if (_filter.Text.Length > 0) _filter.Text = "";
                Reload();
            }
            Restore(new HashSet<string> { key }, key);
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Show in Ledger: moves to the tab that shows the counters' rows (see <see cref="LedgerJump"/>), clearing the text filter when it hides them,
    /// then selects those rows and scrolls to the first one shown. Only rows of the player's view; returns the plan (its message tells what was not found).
    /// </summary>
    public LedgerJumpPlan ShowPieces(IReadOnlyCollection<string> pieceIds)
    {
        if (_s.Rules is not { } pack || _s.Game == null || _s.View is not { } view)
            return new LedgerJumpPlan(null, false, Array.Empty<string>(), pieceIds.Count, "The Ledger needs a game with a rules pack.");
        if (_grid.IsCurrentCellInEditMode)
            return new LedgerJumpPlan(null, false, Array.Empty<string>(), pieceIds.Count, "Finish editing the Ledger cell first.");
        var plan = LedgerJump.Plan(pieceIds, _tabDefs, _tab.Key, view, pack, _s.Model, _filter.Text, _prefs.Side, _showEmpty.Checked, _s.CanSee);
        if (plan.TabKey == null) return plan;
        if (plan.ClearFilter && _filter.Text.Length > 0) _filter.Text = "";
        if (plan.TabKey != _tab.Key) SelectTab(plan.TabKey);   // reloads
        else Reload();
        _debounce.Stop();   // the cleared filter's pending reload is done
        var keys = plan.RowKeys.ToHashSet(StringComparer.Ordinal);
        var first = _rows.FindIndex(r => keys.Contains(r.Row.Key));
        if (first < 0) return plan;
        try { _grid.CurrentCell = _grid.Rows[first].Cells[0]; }   // keyboard focus on the first row; also scrolls it into view
        catch (InvalidOperationException) { }
        Restore(keys, _rows[first].Row.Key);
        return plan;
    }

    /// <summary>Smoke: the keys of the selected rows.</summary>
    public IReadOnlyCollection<string> SelectedKeysForSmoke => SelectedKeys();

    void Restore(HashSet<string> keys, string? firstKey)
    {
        if (_rows.Count == 0) return;
        try
        {
            _grid.ClearSelection();
            for (var i = 0; i < _rows.Count; i++)
                if (keys.Contains(_rows[i].Row.Key)) _grid.Rows[i].Selected = true;
            if (firstKey != null)
            {
                var at = _rows.FindIndex(r => r.Row.Key == firstKey);
                if (at >= 0 && at < _grid.RowCount) _grid.FirstDisplayedScrollingRowIndex = at;
            }
        }
        catch (InvalidOperationException) { }
        catch (ArgumentOutOfRangeException) { }
    }

    void SortBy(int col)
    {
        if (col < 0 || col >= _grid.Columns.Count) return;
        if (_sortCol == col) _sortAsc = !_sortAsc; else { _sortCol = col; _sortAsc = true; }
        var keys = SelectedKeys();
        var first = FirstVisibleKey();
        ApplySort();
        _grid.Invalidate();
        Restore(keys, first);
    }

    /// <summary>Sorts by the chosen column within each group: group header rows stay where they are and keep their rows under them.</summary>
    void ApplySort()
    {
        foreach (DataGridViewColumn c in _grid.Columns) c.HeaderCell.SortGlyphDirection = SortOrder.None;
        if (_sortCol < 0 || _sortCol >= _grid.Columns.Count) return;
        _grid.Columns[_sortCol].HeaderCell.SortGlyphDirection = _sortAsc ? SortOrder.Ascending : SortOrder.Descending;
        var output = new List<LedgerRowEx>(_rows.Count);
        var segment = new List<LedgerRowEx>();
        void Flush() { output.AddRange(SortSegment(segment)); segment.Clear(); }
        foreach (var r in _rows)
        {
            if (r.IsGroupHeader) { Flush(); output.Add(r); }
            else segment.Add(r);
        }
        Flush();
        _rows = output;
    }

    List<LedgerRowEx> SortSegment(List<LedgerRowEx> rows)
    {
        var sign = _sortAsc ? 1 : -1;
        if (_sortCol >= FixedCols)
        {
            var vi = _sortCol - FixedCols;
            double? V(LedgerRowEx r) => vi < r.Row.Values.Count ? r.Row.Values[vi] : null;
            // Blank values always sort last; a stable order keeps ties in their natural order.
            return rows.Select((r, i) => (r, i)).OrderBy(x => V(x.r) == null ? 1 : 0)
                .ThenBy(x => (V(x.r) ?? 0) * sign).ThenBy(x => x.i).Select(x => x.r).ToList();
        }
        Func<LedgerRowEx, string> sel = _sortCol == 0 ? r => r.Row.Name : r => r.Row.Kind;
        return (_sortAsc ? rows.OrderBy(sel, StringComparer.CurrentCultureIgnoreCase) : rows.OrderByDescending(sel, StringComparer.CurrentCultureIgnoreCase)).ToList();
    }

    static string Fmt(double v) => LedgerTable.FormatNumber(v);

    void OnCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _rows.Count) return;
        var r = _rows[e.RowIndex].Row;
        e.Value = e.ColumnIndex switch
        {
            0 => r.Name,
            1 => r.Kind,
            _ when e.ColumnIndex - FixedCols < r.Values.Count && r.Values[e.ColumnIndex - FixedCols] is { } v => Fmt(v),
            _ => "",
        };
    }

    void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _rows.Count || e.CellStyle == null) return;
        var row = _rows[e.RowIndex];
        if (row.IsGroupHeader)
        {
            e.CellStyle.Font = _boldFont ??= new Font(_grid.Font, FontStyle.Bold);
            e.CellStyle.BackColor = SystemColors.ControlLight;
            e.CellStyle.SelectionBackColor = SystemColors.ControlDark;
            if (e.ColumnIndex == 0) e.CellStyle.Padding = Padding.Empty;
            return;
        }
        if (e.ColumnIndex < FixedCols) return;
        var i = e.ColumnIndex - FixedCols;
        if (i < row.Row.Values.Count && row.Row.Values[i] is < 0)
        {
            e.CellStyle.ForeColor = Color.Red;
            e.CellStyle.SelectionForeColor = Color.Red;
        }
    }

    Breakdown? BreakdownFor(int rowIndex, int colIndex)
    {
        if (rowIndex >= _rows.Count || colIndex < FixedCols || colIndex - FixedCols >= _cols.Count || _rows[rowIndex].IsGroupHeader) return null;
        if (_s.Game is not { } game) return null;
        return LedgerBreakdown.For(_s.View ?? game.State, _s.Rules, _s.Model, _rows[rowIndex].Row.Key, _cols[colIndex - FixedCols].Id);
    }

    // ---------- manual edit ----------

    void OnCellValuePushed(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _rows.Count || e.ColumnIndex < FixedCols || e.ColumnIndex - FixedCols >= _cols.Count) return;
        if (_rows[e.RowIndex].IsGroupHeader) return;
        var key = _rows[e.RowIndex].Row.Key;
        var prop = _cols[e.ColumnIndex - FixedCols];
        var text = (e.Value as string ?? "").Trim();
        if (text.Length == 0) return; // empty input cancels
        // The commit happens inside the grid's own edit-end; ask for the note once that has finished.
        BeginInvoke(() => { if (!IsDisposed && !_closing) Run(() => CommitManual(key, prop, text)); });
    }

    /// <summary>Invariant first (no thousands separators), then the current culture; null when not a finite number.</summary>
    internal static double? ParseNumber(string text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)) return v;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out v) && double.IsFinite(v)) return v;
        return null;
    }

    void CommitManual(string key, PropertyDef prop, string text)
    {
        if (ParseNumber(text) is not { } after)
        {
            Warn($"'{text}' is not a number.");
            return;
        }
        if (_s.Game is not { } game) return;
        if (!_s.CanEditLedger(key)) { Warn("That value " + ModuleSession.OtherSideText + "."); Reload(); return; }
        if (Ledger.Get(game.State, key, prop.Id) is { } cur && cur == after) { Reload(); return; } // unchanged: no history entry
        var note = InputDialog.Ask(this, "Manual change", $"Why is {prop.Name} being set to {Fmt(after)}? (required)");
        if (string.IsNullOrWhiteSpace(note)) { Reload(); return; }
        if (_actions?.Invoke() is { } port) { port.EditLedger(key, prop.Id, after, note); return; }
        var before = Ledger.GetOrDefault(game.State, key, prop);
        game.Execute(CommandLog.Stamp(new LedgerDelta(key, prop.Id, before, after, "manual", null, null, note.Trim(), null)));
    }

    void Warn(string message)
    {
        if (Program.Smoke) { Program.AppLog("ledger: " + message); return; }
        MessageBox.Show(this, message, "Ledger", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ---------- export ----------

    void ExportCsv()
    {
        if (Program.Smoke) return;
        var safe = new string(_tab.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsWhiteSpace(c) ? '-' : c).ToArray()).ToLowerInvariant();
        using var dlg = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*", DefaultExt = "csv", FileName = "ledger-" + safe + ".csv" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        // The rows as shown (sorted), group headers left out.
        var shown = _result with { Rows = _rows };
        try { File.WriteAllText(dlg.FileName, LedgerTabs.ToCsv(shown), new UTF8Encoding(true)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warn("Could not save the file: " + ex.Message); }
    }

    // ---------- smoke ----------

    public int RowCountForSmoke => _rows.Count;

    /// <summary>Smoke: pumps messages and draws the window once into a scratch bitmap. A window kept off-screen is never painted by the system, so this is what makes its lazy parts (counter pictures) get requested and drawn.</summary>
    public void PaintForSmoke()
    {
        Application.DoEvents();
        using var scratch = new Bitmap(Math.Max(Width, 1), Math.Max(Height, 1));
        DrawToBitmap(scratch, new Rectangle(0, 0, scratch.Width, scratch.Height));
    }

    public void SaveScreenshot(string path)
    {
        Update();
        using var bmp = new Bitmap(Math.Max(Width, 1), Math.Max(Height, 1));
        DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Program.AppLog("smoke: ledger screenshot saved to " + path);
    }
}
