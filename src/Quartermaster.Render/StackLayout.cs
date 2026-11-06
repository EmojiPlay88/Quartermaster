using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render;

public static class StackLayout
{
    /// <summary>Per-piece offsets from the stack position, bottom piece first (index 0 is always the origin).</summary>
    public static IReadOnlyList<SKPoint> Offsets(int count, bool expanded, StackMetricsDef m)
    {
        var result = new SKPoint[Math.Max(count, 0)];
        if (m.Disabled) return result;
        var dx = expanded ? m.ExSepX : m.UnexSepX;
        var dy = expanded ? m.ExSepY : m.UnexSepY;
        for (var i = 1; i < result.Length; i++) result[i] = new SKPoint(i * dx, -i * dy);
        return result;
    }
}
