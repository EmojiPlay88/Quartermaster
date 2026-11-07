namespace Quartermaster.Render;

/// <summary>Where the alert bar puts its chips. Pure: widths in, positions out, so the order and overflow rules are testable.</summary>
public static class ChipLayout
{
    /// <summary>X of each placed chip (in input order, left to right), how many did not fit, the X of the "+N" chip (-1 when Overflow is 0) and where the banner must end.</summary>
    public sealed record Result(IReadOnlyList<int> Xs, int Overflow, int OverflowX, int BannerRight);

    /// <summary>
    /// The chips form one group, right-aligned against <paramref name="right"/>, reading in input order left to right (Critical first);
    /// the "+N" chip, when needed, is the rightmost. The group never leaves less than <paramref name="minBanner"/> for the banner, so
    /// trailing chips are dropped into the overflow instead.
    /// </summary>
    public static Result Place(IReadOnlyList<int> widths, int overflowWidth, int right, int gap, int minBanner)
    {
        var fit = 0;
        var used = 0; // each chip counts its width plus one gap
        for (var i = 0; i < widths.Count; i++)
        {
            var next = used + widths[i] + gap;
            if (right - next < minBanner) break;
            used = next;
            fit++;
        }
        var overflow = widths.Count - fit;
        if (overflow > 0)
        {
            while (fit > 0 && right - (used + overflowWidth + gap) < minBanner) { fit--; used -= widths[fit] + gap; }
            overflow = widths.Count - fit;
            used += overflowWidth + gap;
        }
        var x = right - used + gap;
        var xs = new List<int>();
        for (var i = 0; i < fit; i++) { xs.Add(x); x += widths[i] + gap; }
        return new Result(xs, overflow, overflow > 0 ? x : -1, right - used);
    }
}
