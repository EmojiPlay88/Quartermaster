using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>
/// The outliner: the open "Now" decisions on top, then a tree-list of folders / groups / pieces with name, unit count, the pack's
/// outliner properties (aggregated per group) and alert icons. Numbers explain themselves in a <see cref="BreakdownTip"/>; column headers show
/// the property notes. Refreshes are debounced 250 ms and the alerts are evaluated here. Hosted docked on the right or in an
/// <see cref="OutlinerFloatForm"/>; the same instance moves between them.
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
    readonly ToolStrip _bar = new() { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
    readonly ToolStripComboBox _grouping = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 160 };
    readonly ToolStripButton _pop = new("Pop out") { Alignment = ToolStripItemAlignment.Right };
    readonly Label _nowLabel = new() { Text = "Now", Dock = DockStyle.Top, Height = 16, Padding = new Padding(3, 2, 0, 0), Font = new Font("Tahoma", 8.25f, FontStyle.Bold) };
    readonly ListBox _now = new() { Dock = DockStyle.Top, Height = 70, IntegralHeight = false, HorizontalScrollbar = true };
    readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.Fixed3D,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing, EnableHeadersVisualStyles = true,
        CellBorderStyle = DataGridViewCellBorderStyle.None, ShowCellToolTips = false,
    };
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 250 };
    readonly BreakdownTip _tip = new();
    readonly ToolTip _headerTip = new() { ShowAlways = true };
    readonly HashSet<string> _open = new(StringComparer.Ordinal);
    readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    readonly List<(OutlinerNode Node, int Depth)> _rows = new();
    IReadOnlyList<Decision> _decisions = Array.Empty<Decision>();
    OutlinerTree _tree = new(Array.Empty<OutlinerColumn>(), Array.Empty<OutlinerNode>());
    OutlinerGrouping _mode;
    GameSession? _game;
    bool _loading, _closing;
    int _headerCol = -1;

    public OutlinerPanel(ModuleSession s, Func<PhaseController?> phases, OutlinerGrouping grouping, Action<OutlinerGrouping> saveGrouping,
        Action<string> centreOnPiece, Action<MapDef> openMap, Action<Decision> jump)
    {
        _s = s; _phases = phases; _mode = grouping; _saveGrouping = saveGrouping; _centre = centreOnPiece; _openMap = openMap; _jump = jump;
        Name = "OutlinerPanel";
        Font = new Font("Tahoma", 8.25f);

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

        _now.DoubleClick += (_, _) => Guard(JumpSelected);
        _now.Click += (_, _) => Guard(JumpSelected);
        _grid.CellDoubleClick += (_, e) => Guard(() => Activate(e.RowIndex, e.ColumnIndex));
        _grid.CellMouseClick += (_, e) => Guard(() => { if (e.ColumnIndex == NameCol && e.RowIndex >= 0 && e.X < 14 + 12 * Depth(e.RowIndex)) Toggle(e.RowIndex); });
        _grid.KeyDown += (_, e) => Guard(() =>
        {
            if (_grid.CurrentRow is not { } r) return;
            if (e.KeyCode == Keys.Right && !IsOpen(r.Index)) { Toggle(r.Index); e.Handled = true; }
            else if (e.KeyCode == Keys.Left && IsOpen(r.Index)) { Toggle(r.Index); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { Activate(r.Index, NameCol); e.Handled = true; }
        });
        _grid.CellPainting += OnPaint;
        _grid.CellMouseMove += OnHeaderHover;
        _grid.MouseLeave += (_, _) => { _headerTip.Hide(_grid); _headerCol = -1; };
        _tip.Track(_grid, NumberTip);

        _debounce.Tick += (_, _) => { _debounce.Stop(); Guard(Reload); };
        Controls.Add(_grid);
        Controls.Add(_now);
        Controls.Add(_nowLabel);
        Controls.Add(_bar);

        _s.GameReplaced += OnGameReplaced;
        _s.RulesChanged += OnRulesChanged;
        HookGame();
        Reload();
    }

    /// <summary>Raised by the Pop out / Dock button; the host moves the panel.</summary>
    public event Action? PopToggleRequested;

    public void SetFloating(bool floating) => _pop.Text = floating ? "Dock" : "Pop out";

    /// <summary>Smoke: the flattened visible rows' names and indents.</summary>
    public int VisibleRowCount => _rows.Count;
    public int ValueColumnCount => _tree.Columns.Count;
    public int NowCount => _decisions.Count;

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

    /// <summary>Smoke: recompute now instead of waiting for the debounce.</summary>
    public void Flush() { _debounce.Stop(); Guard(Reload); }

    /// <summary>Smoke: the breakdown the tooltip would show for a cell (null = none).</summary>
    public Breakdown? BreakdownFor(int row, int col) => NumberTip(row, col);

    // ---------- refresh ----------

    void OnUi(Action a)
    {
        if (IsDisposed || _closing || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(() => { if (!IsDisposed && !_closing) a(); }); else a();
    }

    void Kick() { _debounce.Stop(); _debounce.Start(); }

    void HookGame()
    {
        if (_game != null) { _game.Changed -= OnGameChanged; _game.Rewound -= OnRewound; }
        _game = _s.Game;
        if (_game != null) { _game.Changed += OnGameChanged; _game.Rewound += OnRewound; }
    }

    void OnGameChanged(GameCommand _) => OnUi(Kick);
    void OnRewound() => OnUi(Kick);
    void OnGameReplaced() => OnUi(() => { HookGame(); Kick(); });
    void OnRulesChanged() => OnUi(Kick);

    void Reload()
    {
        if (_closing || IsDisposed) return;
        var game = _s.Game;
        if (game == null)
        {
            _tree = new OutlinerTree(Array.Empty<OutlinerColumn>(), Array.Empty<OutlinerNode>());
            _decisions = Array.Empty<Decision>();
            Rebuild();
            return;
        }
        var state = game.State;
        PhaseView? view = null;
        try { view = _phases()?.View(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("outliner: " + ex.Message); }
        var pack = _s.Rules;
        IReadOnlyList<AlertHit> alerts = Array.Empty<AlertHit>();
        try
        {
            var side = pack != null && view?.Current != null ? Sequencer.ActingSide(pack, view.Current)?.Id : null;
            alerts = AlertEngine.Evaluate(state, pack, _s.Model, view?.Current, side);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("outliner alerts: " + ex.Message); }
        try { _tree = OutlinerModel.Build(state, pack, _s.Model, _mode, alerts); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("outliner: " + ex.Message);
            _tree = new OutlinerTree(Array.Empty<OutlinerColumn>(), Array.Empty<OutlinerNode>());
        }
        _decisions = view?.Decisions ?? Array.Empty<Decision>();
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
            _nowLabel.Text = _decisions.Count == 0 ? "Now" : $"Now — {_decisions.Count}";

            _rows.Clear();
            void Add(OutlinerNode n, int depth)
            {
                _rows.Add((n, depth));
                if (n.Children.Count > 0 && _open.Contains(n.Key)) foreach (var c in n.Children) Add(c, depth + 1);
            }
            foreach (var r in _tree.Roots) Add(r, 0);

            var first = _grid.FirstDisplayedScrollingRowIndex;
            var cur = _grid.CurrentRow?.Index is { } ci && ci >= 0 && ci < _rows.Count ? _rows[ci].Node.Key : null;
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            _grid.Columns.Clear();
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "", Width = 26, SortMode = DataGridViewColumnSortMode.NotSortable, Resizable = DataGridViewTriState.False });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", Width = 190, SortMode = DataGridViewColumnSortMode.NotSortable });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Units", Width = 44, SortMode = DataGridViewColumnSortMode.NotSortable, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } });
            foreach (var c in _tree.Columns)
                _grid.Columns.Add(new DataGridViewTextBoxColumn
                {
                    HeaderText = c.Name, Width = 60, SortMode = DataGridViewColumnSortMode.NotSortable,
                    DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
                });
            if (_rows.Count > 0) _grid.Rows.Add(_rows.Count);
            for (var i = 0; i < _rows.Count; i++)
            {
                var n = _rows[i].Node;
                var row = _grid.Rows[i];
                row.Cells[NameCol].Value = n.Name;
                row.Cells[UnitsCol].Value = n.Units.ToString(CultureInfo.InvariantCulture);
                for (var c = 0; c < _tree.Columns.Count && c < n.Values.Count; c++)
                    row.Cells[FirstValueCol + c].Value = n.Values[c] is { } v ? v.ToString("0.##", CultureInfo.InvariantCulture) : "";
                if (n.Kind != OutlinerNodeKind.Piece) row.DefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
                if (cur != null && n.Key == cur) { _grid.CurrentCell = row.Cells[NameCol]; }
            }
            if (first >= 0 && first < _grid.RowCount) { try { _grid.FirstDisplayedScrollingRowIndex = first; } catch (InvalidOperationException) { } }
            _grid.ResumeLayout();
        }
        finally { _loading = false; }
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

    void JumpSelected()
    {
        var i = _now.SelectedIndex;
        if (i >= 0 && i < _decisions.Count) _jump(_decisions[i]);
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
            return LedgerBreakdown.For(game.State, _s.Rules, _s.Model, LedgerKey.Piece(node.PieceId), column.PropertyId);
        var parts = new List<(string, double)>();
        foreach (var leaf in Flatten(new[] { node }).Where(x => x.Kind == OutlinerNodeKind.Piece))
            if (ci < leaf.Values.Count && leaf.Values[ci] is { } v) parts.Add((leaf.Name, v));
        var how = column.Aggregate switch { OutlinerAggregate.Min => "lowest", OutlinerAggregate.Average => "average", _ => "total" };
        return LedgerBreakdown.ForTotal($"{column.Name} {how} {node.Values[ci]!.Value.ToString("0.##", CultureInfo.InvariantCulture)}  ({node.Name})", parts);
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
            _debounce.Stop();
            _s.GameReplaced -= OnGameReplaced;
            _s.RulesChanged -= OnRulesChanged;
            if (_game != null) { _game.Changed -= OnGameChanged; _game.Rewound -= OnRewound; }
            _game = null;
            _debounce.Dispose();
            _tip.Dispose();
            _headerTip.Dispose();
        }
        base.Dispose(disposing);
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
