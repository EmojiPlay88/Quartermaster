namespace Quartermaster.Render;

/// <summary>UI-free helpers for document-tab ordering and sizing.</summary>
public static class TabOrder
{
    /// <summary>Index after moving <paramref name="delta"/> steps (wrapping); -1 when there are no tabs.</summary>
    public static int Step(int count, int current, int delta)
    {
        if (count <= 0) return -1;
        if (current < 0) return delta >= 0 ? 0 : count - 1;
        return ((current + delta) % count + count) % count;
    }

    /// <summary>Index (in the list after removal) to activate once <paramref name="closed"/> is removed; -1 if none left.</summary>
    public static int AfterClose(int countBefore, int closed)
    {
        var left = countBefore - 1;
        return left <= 0 ? -1 : Math.Min(closed, left - 1);
    }

    public static List<T> Others<T>(IReadOnlyList<T> items, int keep)
    {
        var r = new List<T>();
        for (var i = 0; i < items.Count; i++) if (i != keep) r.Add(items[i]);
        return r;
    }

    public static int TabWidth(int count, int available, int max, int min)
        => count <= 0 ? max : Math.Clamp(available / count, min, max);
}
