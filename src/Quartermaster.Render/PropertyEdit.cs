using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>Workbench edits of a property (B6) that the grid cells share: pure, so every front end writes the same pack.</summary>
public static class PropertyEdit
{
    /// <summary>The property's Keyed by (<c>table:&lt;id&gt;</c>, <c>turn</c> or <c>hex</c>), trimmed with its case kept; blank is null (not keyed).</summary>
    public static PropertyDef WithKeyedBy(PropertyDef p, string? keyedBy) =>
        p with { KeyedBy = string.IsNullOrWhiteSpace(keyedBy) ? null : keyedBy.Trim() };
}
