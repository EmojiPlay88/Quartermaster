namespace Quartermaster.Render;

/// <summary>Ordering of lazily rendered thumbnails: the visible range first, then by distance from it (ties go to the later item).</summary>
public static class ThumbQueue
{
    public static IReadOnlyList<int> Order(int count, int first, int last)
    {
        if (count <= 0) return Array.Empty<int>();
        first = Math.Clamp(first, 0, count - 1);
        last = Math.Clamp(Math.Max(last, first), first, count - 1);
        return Enumerable.Range(0, count)
            .OrderBy(i => i < first ? first - i : i > last ? i - last : 0)
            .ThenBy(i => i < first ? 1 : 0)
            .ThenBy(i => i)
            .ToList();
    }
}
