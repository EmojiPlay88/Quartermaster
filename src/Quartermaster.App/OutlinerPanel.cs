using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>
/// The outliner: the open "Now" decisions on top, then a tree-list of folders / groups / pieces with name, unit count, the pack's
/// outliner properties (aggregated per group) and alert icons. Numbers explain themselves in a <see cref="BreakdownTip"/>; column headers show
/// the property notes. It does not evaluate alerts itself: the owner feeds it the alert bar's (debounced) results through
/// <see cref="AlertsChanged"/>, which is also its refresh signal after every game change. The grid is virtual (values come from the row list on
/// demand) and its columns are only rebuilt when the set of outliner properties changes, so user column widths survive refreshes. Hosted docked
/// on the right or in an <see cref="OutlinerFloatForm"/>; the same instance moves between them.
/// </summary>
sealed class OutlinerPanel : UserControl
{
    const int IconCol = 0, NameCol = 1, UnitsCol = 2, FirstValueCol = 3;
    readonly ModuleSession _s;
    readonly Func<PhaseController?> _phases;
    readonly Action<OutlinerGrouping> _saveGrouping;
    readonly Action<string> _centre;
    readonly Action<MapDef> _openMap;
    readonly Action<Decision> _jump;
    readonly Font _font = new("Tahoma", 8.25f);
    readonly Font _nowFont = new("Tahoma", 8.25f, FontStyle.Bold);
    readonly ToolStrip _bar = new() { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
    readonly ToolStripComboBox _grouping = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 160 };
    readonly ToolStripButton _pop = new("Pop out") { Alignment = ToolStripItemAlignment.Right };
    readonly Label _nowLabel = new() { Text = "Now", Dock = DockStyle.Top, Height = 16, Padding = new Padding(3, 2, 0, 0) };
    readonly ListBox _now = new() { Dock = DockStyle.Top, Height = 70, IntegralHeight = false, HorizontalScrollbar = true };
    readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.Fixed3D,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, EnableHeadersVisualStyles = true,
        CellBorderStyle = DataGridViewCellBorderStyle.None, ShowCellToolTips = false, VirtualMode = true,
    };
    readonly BreakdownTip _tip = new();
    readonly ToolTip _headerTip = new() { ShowAlways = true };
    readonly HashSet<string> _open = new(StringComparer.Ordinal);
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    readonly List<(OutlinerNode Node, int Depth)> _rows = new();
    readonly List<string> _shownKeys = new();
    readonly Dictionary<string, int> _widths = new(StringComparer.Ordinal);
    IReadOnlyList<Decision> _decisions = Array.Empty<Decision>();
    IReadOnlyList<AlertHit> _alerts;
    string? _columnSet;
    OutlinerTree _tree = new(Array.Empty<OutlinerColumn>(), Array.Empty<OutlinerNode>());
    OutlinerGrouping _mode;
    bool _loading, _closing, _dirty, _noPhases = true;
    Font? _bold;
    int _headerCol = -1;

    /// <param name="alerts">The latest evaluated alerts (the alert bar's); later results arrive through <see cref="AlertsChanged"/>.</param>
    public OutlinerPanel(ModuleSession s, Func<PhaseController?> phases, IReadOnlyList<AlertHit> alerts, OutlinerGrouping grouping,
        Action<OutlinerGrouping> saveGrouping, Action<string> centreOnPiece, Action<MapDef> openMap, Action<Decision> jump)
    {
        _s = s; _phases = phases; _alerts = alerts; _mode = grouping; _saveGrouping = saveGrouping; _centre = centreOnPiece; _openMap = openMap; _jump = jump;
        Name = "OutlinerPanel";
        Font = _font;
        _nowLabel.Font = _nowFont;

        _grouping.Items.AddRange(new object[] { "By map / org display", "By side" });
        _grouping.SelectedIndex = grouping == OutlinerGrouping.BySide ? 1 : 0;
        _grouping.SelectedIndexChanged += (_, _) => Guard(() =>
        {
            if (_loading) return;
            _mode = _grouping.SelectedIndex == 1 ? OutlinerGrouping.BySide : OutlinerGrouping.OrgDisplays;
            _saveGrouping(_mode);
            Reload();
        });
        _bar.Items.Add(new ToolStripLabel("Group:"));
        _bar.Items.Add(_grouping);
        _pop.Click += (_, _) => PopToggleRequested?.Invoke();
        _bar.Items.Add(_pop);

        _now.MouseDoubleClick += (_, e) => Guard(() =>
        {
            var i = _now.IndexFromPoint(e.Location);
            if (i >= 0 && i < _decisions.Count) _jump(_decisions[i]);
        });
        VisibleChanged += (_, _) => { if (Visible && _dirty) Guard(Reload); };
        _grid.CellDoubleClick += (_, e) => Guard(() => Activate(e.RowIndex, e.ColumnIndex));
        _grid.CellMouseClick += (_, e) => Guard(() => { if (e.ColumnIndex == NameCol && e.RowIndex >= 0 && e.X < 14 + 12 * Depth(e.RowIndex)) Toggle(e.RowIndex); });
        _grid.KeyDown += (_, e) => Guard(() =>
        {
            if (_grid.CurrentRow is not { } r) return;
            if (e.KeyCode == Keys.Right && !IsOpen(r.Index)) { Toggle(r.Index); e.Handled = true; }
            else if (e.KeyCode == Keys.Left && IsOpen(r.Index)) { Toggle(r.Index); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { Activate(r.Index, NameCol); e.Handled = true; }
        });
        _grid.CellValueNeeded += (_, e) => e.Value = ValueAt(e.RowIndex, e.ColumnIndex);
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.RowIndex < _rows.Count && _rows[e.RowIndex].Node.Kind != OutlinerNodeKind.Piece && e.CellStyle != null)
                e.CellStyle.Font = _bold ??= new Font(Font, FontStyle.Bold);
        };
        _grid.CellPainting += OnPaint;
        _grid.CellMouseMove += OnHeaderHover;
        _grid.MouseLeave += (_, _) => { _headerTip.Hide(_grid); _headerCol = -1; };
        _tip.Track(_grid, NumberTip);

        Controls.Add(_grid);
        Controls.Add(_now);
        Controls.Add(_nowLabel);
        Controls.Add(_bar);

        Reload();
    }

    /// <summary>Raised by the Pop out / Dock button; the host moves the panel.</summary>
    public event Action? PopToggleRequested;

    public void SetFloating(bool floating) => _pop.Text = floating ? "Dock" : "Pop out";

    /// <summary>Smoke: the flattened visible rows' names and indents.</summary>
    public int VisibleRowCount => _rows.Count;
    public int ValueColumnCount => _tree.Columns.Count;
    public int NowCount => _decisions.Count;
    public bool NowVisible => _now.Visible;
    public string? CellText(int row, int col) => ValueAt(row, col);
    /// <summary>Smoke: column widths, to check that user widths survive refreshes.</summary>
    public int ColumnWidth(int col) => col >= 0 && col < _grid.ColumnCount ? _grid.Columns[col].Width : -1;
    public void SetColumnWidth(int col, int width) { if (col >= 0 && col < _grid.ColumnCount) _grid.Columns[col].Width = width; }

    /// <summary>Smoke: opens every folder and group.</summary>
    public void ExpandAll()
    {
        void Walk(IEnumerable<OutlinerNode> ns) { foreach (var n in ns) { if (n.Children.Count > 0) { _open.Add(n.Key); Walk(n.Children); } } }
        Walk(_tree.Roots);
        Rebuild();
    }

    public void SaveScreenshot(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var bmp = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
        DrawToBitmap(bmp, new Rectangle(Point.Empty, Size));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>Smoke: recompute now (with the latest alerts) instead of waiting for the next alert update.</summary>
    public void Flush() => Guard(Reload);

    /// <summary>
    /// The owner's alert hub published a fresh evaluation (after any game change, rewind, rules change or game replacement, already debounced):
    /// keep the hits and rebuild the tree, or just mark it stale while the panel is hidden.
    /// </summary>
    public void AlertsChanged(IReadOnlyList<AlertHit> hits)
    {
        if (IsDisposed || _closing) return;
        _alerts = hits ?? Array.Empty<AlertHit>();
        _dirty = true;
        if (Visible) Guard(Reload);
    }

    /// <summary>Smoke: the breakdown the tooltip would show for a cell (null = none).</summary>
    public Breakdown? BreakdownFor(int row, int col) => NumberTip(row, col);

    // ---------- refresh ----------

    void Reload()
    {
        if (_closing || IsDisposed) return;
        if (!Visible) { _dirty = true; return; }
        _dirty = false;
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
        try { view = _phases()?.View(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("outliner: " + ex.Message); }
        try { _tree = OutlinerModel.Build(state, _s.Rules, _s.Model, _mode, _alerts); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("outliner: " + ex.Message);
            _tree = new OutlinerTree(Array.Empty<OutlinerColumn>(), Array.Empty<OutlinerNode>());
        }
        _decisions = view?.Decisions ?? Array.Empty<Decision>();
        _noPhases = view == null || view.NoPhases;
        // New folders start open, groups start closed; what the user opened or closed is remembered by key.
        foreach (var n in Flatten(_tree.Roots))
            if (_seen.Add(n.Key) && n.Kind == OutlinerNodeKind.Folder) _open.Add(n.Key);
        Rebuild();
    }

    static IEnumerable<OutlinerNode> Flatten(IEnumerable<OutlinerNode> ns)
    {
        foreach (var n in ns)
        {
            yield return n;
            foreach (var c in Flatten(n.Children)) yield return c;
        }
    }

    void Rebuild()
    {
        _loading = true;
        try
        {
            _now.BeginUpdate();
            _now.Items.Clear();
            foreach (var d in _decisions) _now.Items.Add(d.Text);
            if (_decisions.Count == 0) _now.Items.Add("(nothing waiting)");
            _now.EndUpdate();
            _now.Visible = _nowLabel.Visible = !_noPhases;
            _nowLabel.Text = _decisions.Count == 0 ? "Now" : $"Now — {_decisions.Count}";

            _rows.Clear();
            void Add(OutlinerNode n, int depth)
            {
                _rows.Add((n, depth));
                if (n.Children.Count > 0 && _open.Contains(n.Key)) foreach (var c in n.Children) Add(c, depth + 1);
            }
            foreach (var r in _tree.Roots) Add(r, 0);

            var first = _grid.FirstDisplayedScrollingRowIndex;
            var cur = _grid.CurrentCell is { RowIndex: var ci } && ci >= 0 && ci < _shownKeys.Count ? _shownKeys[ci] : null;
            var curCol = _grid.CurrentCell?.ColumnIndex ?? NameCol;
            EnsureColumns();
            // Virtual grid: only the row count changes here; the visible cells ask ValueAt for their text when they repaint.
            _grid.RowCount = 0;
            _grid.RowCount = _rows.Count;
            _shownKeys.Clear();
            foreach (var r in _rows) _shownKeys.Add(r.Node.Key);
            if (cur != null && _shownKeys.IndexOf(cur) is var at and >= 0 && curCol >= 0 && curCol < _grid.ColumnCount)
                _grid.CurrentCell = _grid.Rows[at].Cells[curCol];
            if (first >= 0 && first < _grid.RowCount) { try { _grid.FirstDisplayedScrollingRowIndex = first; } catch (InvalidOperationException) { } }
            _grid.Invalidate();
        }
        finally { _loading = false; }
    }

    /// <summary>Rebuilds the columns only when the set of outliner properties changed; widths the user set are kept per property id.</summary>
    void EnsureColumns()
    {
        var set = string.Join("\u001f", _tree.Columns.Select(c => c.PropertyId));
        if (_grid.ColumnCount > 0 && set == _columnSet)
        {
            for (var i = 0; i < _tree.Columns.Count; i++) _grid.Columns[FirstValueCol + i].HeaderText = _tree.Columns[i].Name;
            return;
        }
        foreach (DataGridViewColumn c in _grid.Columns) if (!string.IsNullOrEmpty(c.Name)) _widths[c.Name] = c.Width;
        _columnSet = set;
        _grid.Columns.Clear();
        DataGridViewTextBoxColumn Col(string id, string header, int width, bool right) => new()
        {
            Name = id, HeaderText = header, Width = _widths.TryGetValue(id, out var w) ? w : width, SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = right ? new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } : new DataGridViewCellStyle(),
        };
        var icon = Col("#icons", "", 26, false);
        icon.Resizable = DataGridViewTriState.False;
        _grid.Columns.Add(icon);
        _grid.Columns.Add(Col("#name", "Name", 190, false));
        _grid.Columns.Add(Col("#units", "Units", 44, true));
        foreach (var c in _tree.Columns) _grid.Columns.Add(Col("p:" + c.PropertyId, c.Name, 60, true));
    }

    /// <summary>The text of one cell, straight from the row list (the grid is virtual).</summary>
    string? ValueAt(int row, int col)
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

    // ---------- tree behaviour ----------

    int Depth(int row) => row >= 0 && row < _rows.Count ? _rows[row].Depth : 0;
    bool IsOpen(int row) => row >= 0 && row < _rows.Count && _open.Contains(_rows[row].Node.Key);

    void Toggle(int row)
    {
        if (row < 0 || row >= _rows.Count || _rows[row].Node.Children.Count == 0) return;
        var k = _rows[row].Node.Key;
        if (!_open.Add(k)) _open.Remove(k);
        Rebuild();
    }

    void Activate(int row, int col)
    {
        if (row < 0 || row >= _rows.Count) return;
        var n = _rows[row].Node;
        if (n.Kind == OutlinerNodeKind.Piece && n.PieceId != null) { _centre(n.PieceId); return; }
        if (n.Kind == OutlinerNodeKind.Group && n.Key.StartsWith("map:", StringComparison.Ordinal))
        {
            var id = n.Key["map:".Length..];
            var map = _s.Model.Maps.FirstOrDefault(m => m.Id == id) ?? _s.Model.Maps.FirstOrDefault(m => m.Name == id);
            if (map != null && map.IsOrgDisplay) { _openMap(map); return; }
        }
        if (n.Children.Count > 0) Toggle(row);
    }

    static Color SeverityColor(AlertSeverity s) => s switch
    {
        AlertSeverity.Critical => Color.Firebrick,
        AlertSeverity.Warning => Color.DarkOrange,
        _ => Color.SteelBlue,
    };

    void OnPaint(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _rows.Count || e.Graphics == null) return;
        try
        {
            var (n, depth) = _rows[e.RowIndex];
            if (e.ColumnIndex == IconCol)
            {
                e.Paint(e.CellBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground);
                var x = e.CellBounds.Left + 4;
                foreach (var sev in n.Alerts.Take(2))
                {
                    using var b = new SolidBrush(SeverityColor(sev));
                    e.Graphics.FillEllipse(b, x, e.CellBounds.Top + (e.CellBounds.Height - 8) / 2, 8, 8);
                    x += 10;
                }
                e.Handled = true;
            }
            else if (e.ColumnIndex == NameCol)
            {
                e.Paint(e.CellBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground | DataGridViewPaintParts.Border);
                var selected = (e.State & DataGridViewElementStates.Selected) != 0;
                var fore = selected ? SystemColors.HighlightText : SystemColors.WindowText;
                var x = e.CellBounds.Left + 2 + depth * 12;
                if (n.Children.Count > 0)
                    TextRenderer.DrawText(e.Graphics, _open.Contains(n.Key) ? "−" : "+", Font, new Rectangle(x, e.CellBounds.Top, 12, e.CellBounds.Height), fore,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
                var f = e.CellStyle?.Font ?? Font;
                TextRenderer.DrawText(e.Graphics, n.Name, f, new Rectangle(x + 14, e.CellBounds.Top, Math.Max(0, e.CellBounds.Right - x - 14), e.CellBounds.Height), fore,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.Left);
                e.Handled = true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* a missed paint is harmless */ }
    }

    // ---------- tooltips ----------

    Breakdown? NumberTip(int row, int col)
    {
        if (row < 0 || row >= _rows.Count || col < FirstValueCol) return null;
        var ci = col - FirstValueCol;
        if (ci >= _tree.Columns.Count || _s.Game is not { } game) return null;
        var node = _rows[row].Node;
        var column = _tree.Columns[ci];
        if (ci >= node.Values.Count || node.Values[ci] == null) return null;
        if (node.Kind == OutlinerNodeKind.Piece && node.PieceId != null)
            return LedgerBreakdown.For(_s.View ?? game.State, _s.Rules, _s.Model, LedgerKey.Piece(node.PieceId), column.PropertyId);
        var parts = new List<(string, double)>();
        foreach (var leaf in Flatten(new[] { node }).Where(x => x.Kind == OutlinerNodeKind.Piece))
            if (ci < leaf.Values.Count && leaf.Values[ci] is { } v) parts.Add((leaf.Name, v));
        var how = column.Aggregate switch { OutlinerAggregate.Min => "lowest", OutlinerAggregate.Average => "average", _ => "total" };
        return LedgerBreakdown.ForTotal(_s.VisibleOnly($"{column.Name} {how} {node.Values[ci]!.Value.ToString("0.##", CultureInfo.InvariantCulture)}  ({node.Name})"), parts);
    }

    void OnHeaderHover(object? sender, DataGridViewCellMouseEventArgs e)
    {
        // Cell-mouse-move only fires on cells; headers (RowIndex -1) are reported too.
        if (e.RowIndex >= 0) { if (_headerCol != -1) { _headerTip.Hide(_grid); _headerCol = -1; } return; }
        if (e.ColumnIndex == _headerCol) return;
        _headerCol = e.ColumnIndex;
        var ci = e.ColumnIndex - FirstValueCol;
        var notes = ci >= 0 && ci < _tree.Columns.Count ? _tree.Columns[ci].Notes : null;
        if (string.IsNullOrWhiteSpace(notes)) { _headerTip.Hide(_grid); return; }
        var p = _grid.PointToClient(Cursor.Position);
        _headerTip.Show(notes, _grid, p.X + 12, p.Y + 18, 8000);
    }

    void Guard(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("outliner failed: " + ex);
            _s.Log.Add(DiagnosticSeverity.Error, "outliner", "The outliner failed: " + ex.Message);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing = true;
            _tip.Dispose();
            _headerTip.Dispose();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            // The fonts outlive the child controls that use them; free them last.
            _bold?.Dispose();
            _nowFont.Dispose();
            _font.Dispose();
        }
    }
}

/// <summary>Top-level window for a popped-out <see cref="OutlinerPanel"/>; closing it returns the panel to the main window.</summary>
sealed class OutlinerFloatForm : Form, WindowManager.ICustomDock
{
    readonly Action _dockBack;
    bool _closingForReal;

    public OutlinerFloatForm(Action dockBack)
    {
        _dockBack = dockBack;
        Text = "Outliner";
        ShowIcon = false;
    }

    public void CloseForReal() { _closingForReal = true; Close(); }
    public void DockBack() => _dockBack();

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closingForReal && !e.Cancel && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; BeginInvoke(_dockBack); return; }
        base.OnFormClosing(e);
    }
}
