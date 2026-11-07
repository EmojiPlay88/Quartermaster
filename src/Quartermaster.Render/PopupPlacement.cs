using System.Drawing;

namespace Quartermaster.Render;

/// <summary>
/// Pure placement rule for a hover popup next to the cursor: below-right by default, flipped to the left and/or above when it would run off
/// the working area, and never with the cursor inside its bounds (a click right after the hover must land on what is under the cursor, not on
/// the popup).
/// </summary>
public static class PopupPlacement
{
    public const int Offset = 16;

    public static Point Place(Point cursor, Size size, Rectangle area)
    {
        var (x, xClear) = Axis(cursor.X, size.Width, area.Left, area.Right);
        var (y, yClear) = Axis(cursor.Y, size.Height, area.Top, area.Bottom);
        if (!xClear && !yClear)
        {
            // Too big to sit beside the cursor on either axis: put it on the roomier vertical side, even if that runs off the screen.
            y = area.Bottom - cursor.Y >= cursor.Y - area.Top ? cursor.Y + Offset : cursor.Y - Offset - size.Height;
        }
        return new Point(x, y);
    }

    /// <summary>One axis: after the cursor if it fits, else before it if that fits, else clamped to the area (Clear = false, may cover the cursor).</summary>
    static (int Pos, bool Clear) Axis(int c, int len, int lo, int hi)
    {
        if (c + Offset + len <= hi) return (Math.Max(lo, c + Offset), true);
        if (c - Offset - len >= lo) return (c - Offset - len, true);
        var p = Math.Max(lo, Math.Min(c + Offset, hi - len));
        var clear = p > c || p + len <= c;
        return (p, clear);
    }
}
