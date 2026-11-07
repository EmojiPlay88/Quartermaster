using Quartermaster.Core;

namespace Quartermaster.App;

/// <summary>
/// One shared tooltip for every ledger breakdown: monospace text (so the amounts line up) in the classic tooltip colours.
/// Use <see cref="Show"/> on hover and <see cref="Hide"/> on leave. Never throws.
/// </summary>
sealed class BreakdownTip : IDisposable
{
    const int Pad = 4;
    readonly ToolTip _tip = new() { OwnerDraw = true, UseAnimation = false, UseFading = false, ShowAlways = true, AutoPopDelay = 30000 };
    Font? _font;
    Control? _shownOn;

    public BreakdownTip()
    {
        _tip.Popup += (_, e) =>
        {
            try { e.ToolTipSize = Measure(_tip.GetToolTip(e.AssociatedControl!) ?? ""); } catch (Exception) { /* keep the default size */ }
        };
        _tip.Draw += (_, e) =>
        {
            try
            {
                e.DrawBackground();
                e.DrawBorder();
                TextRenderer.DrawText(e.Graphics, e.ToolTipText, Mono, new Rectangle(Pad, Pad, e.Bounds.Width - Pad, e.Bounds.Height - Pad),
                    SystemColors.InfoText, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.Top);
            }
            catch (Exception) { /* a missed paint is harmless */ }
        };
    }

    /// <summary>Consolas where installed, else Lucida Console, else the generic monospace face.</summary>
    Font Mono => _font ??= MakeFont();

    static Font MakeFont()
    {
        foreach (var name in new[] { "Consolas", "Lucida Console" })
        {
            var f = new Font(name, 8.25f);
            if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
            f.Dispose();
        }
        return new Font(System.Drawing.FontFamily.GenericMonospace, 8.25f);
    }

    Size Measure(string text)
    {
        var s = TextRenderer.MeasureText(text, Mono, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        return new Size(s.Width + Pad * 2, s.Height + Pad * 2);
    }

    /// <summary>Shows the breakdown with its top-left near <paramref name="at"/> (owner client coordinates).</summary>
    public void Show(Control owner, Point at, Breakdown b)
    {
        try
        {
            if (owner.IsDisposed) return;
            _tip.Show(LedgerBreakdown.Format(b), owner, at.X, at.Y);
            _shownOn = owner;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    public void Hide()
    {
        try { if (_shownOn is { IsDisposed: false } o) _tip.Hide(o); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        _shownOn = null;
    }

    /// <summary>
    /// Shows <paramref name="provider"/>'s breakdown while the pointer rests on a grid cell (null = nothing to show) and hides it on
    /// leave, scroll or click. Turns the grid's own plain-text tooltips off.
    /// </summary>
    public void Track(DataGridView grid, Func<int, int, Breakdown?> provider)
    {
        (int Row, int Col) at = (-1, -1);
        grid.ShowCellToolTips = false;
        grid.CellMouseMove += (_, e) =>
        {
            if ((e.RowIndex, e.ColumnIndex) == at) return;
            at = (e.RowIndex, e.ColumnIndex);
            Hide();
            try
            {
                if (e.RowIndex >= 0 && e.ColumnIndex >= 0 && provider(e.RowIndex, e.ColumnIndex) is { } b)
                {
                    var p = grid.PointToClient(Cursor.Position);
                    Show(grid, new Point(p.X + 12, p.Y + 18), b);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        };
        void Reset(object? s, EventArgs e) { at = (-1, -1); Hide(); }
        grid.CellMouseLeave += Reset;
        grid.MouseLeave += Reset;
        grid.Scroll += Reset;
        grid.MouseDown += Reset;
    }

    public void Dispose()
    {
        _tip.Dispose();
        _font?.Dispose();
    }
}
