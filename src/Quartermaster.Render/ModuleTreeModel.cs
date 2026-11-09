using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>One node of a UI-neutral tree. <c>Tag</c> carries the domain object (a <see cref="WindowTreeNode"/>, <see cref="TableRef"/>, ...).</summary>
public sealed record TreeItem(string Text, string? Tooltip, object? Tag, IReadOnlyList<TreeItem> Children);

/// <summary>Module window tree as shown in the docked tree: the module root with its folders; maps the viewer may not see are left out.</summary>
public static class ModuleTreeModel
{
    /// <summary>A single root item (the module). <paramref name="canSeeMap"/> (hot-seat) leaves out maps of sides the player does not hold.</summary>
    public static IReadOnlyList<TreeItem> Build(ModuleModel m, Func<string, bool>? canSeeMap)
        => Convert(m.Tree, canSeeMap) is { } root ? [root] : [];

    /// <summary>Changes exactly when the set of hidden maps changes; "" without a visibility filter.</summary>
    public static string VisibilitySignature(ModuleModel m, Func<string, bool>? canSeeMap)
        => canSeeMap == null ? "" : string.Join("\u0001", m.Maps.Where(x => !canSeeMap(x.Id)).Select(x => x.Id));

    static TreeItem? Convert(WindowTreeNode n, Func<string, bool>? canSeeMap)
    {
        if (n.Ref is MapDef d && canSeeMap != null && !canSeeMap(d.Id)) return null;
        var kids = n.Children.Select(c => Convert(c, canSeeMap)).OfType<TreeItem>().ToList();
        return new TreeItem(n.Title, n.Kind.ToString(), n, kids);
    }
}
