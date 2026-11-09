using Quartermaster.Core;
using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// Counter pictures in the Ledger: a 64×48 image at the left of each unit row's Name cell (rows 52 px while Show counters is on), drawn lazily for
/// the visible rows (<see cref="CounterCellImages"/>), a big preview on hover (<see cref="CounterPreviewTip"/>) and double-click to centre the
/// piece on its map. Pictures come only from the player's view; holder and group rows have none.
/// </summary>
sealed partial class LedgerWindow
{
    static readonly Size ImageSize = new(CounterImages.RowSize.Width, CounterImages.RowSize.Height);
    const int CounterRowHeight = LedgerViewRules.CounterRowHeight;
    const int ImageLeft = 2;
    int _compactRowHeight;
    CounterCellImages? _counterCells;
    CounterPreviewTip? _preview;

    bool CountersOn => _showCounters.Checked;
    int RowHeight => LedgerViewRules.RowHeight(CountersOn, _compactRowHeight);
    int NameImagePad => CountersOn ? ImageSize.Width + ImageLeft + 6 : 0;

    void InitCounters()
    {
        _compactRowHeight = _grid.RowTemplate.Height;
        _counterCells = new CounterCellImages(_s.Counters, ImageSize, _grid, () => _grid.Invalidate());
        _preview = new CounterPreviewTip(_s.Counters);
        _grid.CellPainting += (_, e) => Run(() => PaintCounter(e));
        _grid.CellMouseMove += (_, e) => Run(() => HoverCounter(e));
        _grid.CellMouseLeave += (_, _) => _preview?.Hide();
        _grid.MouseLeave += (_, _) => _preview?.Hide();
        _grid.Scroll += (_, _) => _preview?.Hide();
        _grid.MouseDown += (_, _) => _preview?.Hide();
        _grid.CellMouseDoubleClick += (_, e) => Run(() => DoubleClickCounter(e));
        // Only unit rows need room for a picture: group headers and holder rows stay compact.
        _grid.RowHeightInfoNeeded += (_, e) =>
        {
            if (!CountersOn || e.RowIndex < 0 || e.RowIndex >= _rows.Count) return;
            e.Height = _rows[e.RowIndex].PieceId != null ? CounterRowHeight : _compactRowHeight + (_rows[e.RowIndex].IsGroupHeader ? 4 : 0);
            e.MinimumHeight = Math.Min(e.MinimumHeight, e.Height);
        };
        Deactivate += (_, _) => _preview?.Hide();
    }

    void DisposeCounters()
    {
        _preview?.Dispose();
        _counterCells?.Dispose();
    }

    void ResetCounterImages()
    {
        _counterCells?.Reset();   // the viewed state changed: pictures may differ
        _preview?.Hide();
    }

    /// <summary>The viewed piece behind a unit row, or null (holder, group header, hidden or gone).</summary>
    PieceInstance? RowPiece(int row)
    {
        if (row < 0 || row >= _rows.Count || _rows[row].PieceId is not { } id) return null;
        return _s.View?.Pieces.TryGetValue(id, out var p) == true ? p : null;
    }

    Rectangle ImageBox(Rectangle cell) =>
        new(cell.Left + ImageLeft, cell.Top + (cell.Height - ImageSize.Height) / 2, ImageSize.Width, ImageSize.Height);

    void PaintCounter(DataGridViewCellPaintingEventArgs e)
    {
        if (!CountersOn || e.ColumnIndex != 0 || e.RowIndex < 0 || e.RowIndex >= _rows.Count || e.Graphics == null) return;
        if (RowPiece(e.RowIndex) is not { } piece) return;
        e.Paint(e.CellBounds, DataGridViewPaintParts.All);
        e.Handled = true;
        var first = Math.Max(0, _grid.FirstDisplayedScrollingRowIndex);
        _counterCells!.SetVisible(first, first + Math.Max(1, _grid.DisplayedRowCount(true)) - 1);
        var box = ImageBox(e.CellBounds);
        if (_counterCells.Get(piece, e.RowIndex) is { } bmp)
            e.Graphics.DrawImage(bmp, box.Left, box.Top, bmp.Width, bmp.Height);
        else
        {
            using var pen = new Pen(Color.Silver) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            e.Graphics.DrawRectangle(pen, box.Left, box.Top, box.Width - 1, box.Height - 1);
        }
    }

    /// <summary>True when a cell-relative mouse position is over the picture of a unit row.</summary>
    bool OverImage(int row, int col, Point inCell)
    {
        if (!CountersOn || col != 0 || RowPiece(row) == null) return false;
        var cell = _grid.GetCellDisplayRectangle(0, row, false);
        var box = ImageBox(new Rectangle(0, 0, cell.Width, cell.Height));
        return box.Contains(inCell);
    }

    void HoverCounter(DataGridViewCellMouseEventArgs e)
    {
        if (_preview == null) return;
        if (!OverImage(e.RowIndex, e.ColumnIndex, e.Location) || RowPiece(e.RowIndex) is not { } piece) { _preview.Hide(); return; }
        _preview.Show(piece, _rows[e.RowIndex].Row.Name, Cursor.Position);
    }

    void DoubleClickCounter(DataGridViewCellMouseEventArgs e)
    {
        if (_centre == null || !OverImage(e.RowIndex, e.ColumnIndex, e.Location) || _rows[e.RowIndex].PieceId is not { } id) return;
        _preview?.Hide();
        _centre(id);
    }

    // ---------- smoke ----------

    /// <summary>Number of visible unit rows whose picture is ready (smoke waits for them before the screenshot).</summary>
    public int ReadyCounterRowsForSmoke()
    {
        if (_counterCells == null || !CountersOn) return 0;
        var n = 0;
        var first = Math.Max(0, _grid.FirstDisplayedScrollingRowIndex);
        var last = Math.Min(_rows.Count - 1, first + _grid.DisplayedRowCount(true));
        for (var i = first; i <= last; i++)
            if (RowPiece(i) is { } p && _s.Counters.TryGetCached(p, CounterImages.RowSize) != null) n++;
        return n;
    }

    public int VisibleUnitRowsForSmoke()
    {
        var first = Math.Max(0, _grid.FirstDisplayedScrollingRowIndex);
        var last = Math.Min(_rows.Count - 1, first + _grid.DisplayedRowCount(true));
        var n = 0;
        for (var i = first; i <= last; i++) if (RowPiece(i) != null) n++;
        return n;
    }

    /// <summary>Shows the hover preview for the first unit row, as if the mouse rested on its picture; returns the tip, or null.</summary>
    public CounterPreviewTip? ShowPreviewForSmoke()
    {
        for (var i = 0; i < _rows.Count; i++)
            if (RowPiece(i) is { } p)
            {
                var cell = _grid.GetCellDisplayRectangle(0, i, false);
                var at = _grid.PointToScreen(new Point(cell.Left + ImageLeft + ImageSize.Width / 2, cell.Top + cell.Height / 2));
                _preview?.Show(p, _rows[i].Row.Name, at);
                return _preview;
            }
        return null;
    }
}
