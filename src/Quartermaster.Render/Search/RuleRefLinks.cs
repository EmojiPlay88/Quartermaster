using Quartermaster.Rules;

namespace Quartermaster.Render.Search;

/// <summary>Rule refs as links both ways: what points at a page ("Used on this page"), and how a ref reads.</summary>
public static class RuleRefLinks
{
    public const string Missing = "(missing)";

    /// <summary>Formulas, then tables, then phases (pack order) whose rule ref points at this document page.</summary>
    public static IReadOnlyList<(string Kind, string Id, string Name)> UsedOnPage(RulesPack pack, string entry, int page)
    {
        var list = new List<(string Kind, string Id, string Name)>();
        if (pack == null) return list;
        bool Points(string? refId) =>
            !string.IsNullOrEmpty(refId) && pack.ById<RuleRef>(refId) is { } r && r.DocumentEntry == entry && r.Page == page;
        foreach (var f in pack.Formulas) if (Points(f.RuleRefId)) list.Add(("Formula", f.Id, f.Name));
        foreach (var t in pack.Tables) if (Points(t.RuleRefId)) list.Add(("Table", t.Id, t.Name));
        foreach (var p in pack.Phases) if (Points(p.RuleRefId)) list.Add(("Phase", p.Id, p.Name));
        return list;
    }

    /// <summary>
    /// "Label — Title, p. N"; ends with " (missing)" when the document is not indexed, could not be opened (empty SHA), or the
    /// page is past its last page (or below 1). A document that opened but whose page count is unknown (PdfPig could not parse
    /// it) is not called missing.
    /// </summary>
    public static string Describe(RuleRef r, IReadOnlyList<IndexedDocument> docs)
    {
        var doc = docs?.FirstOrDefault(d => d.Entry == r.DocumentEntry);
        var name = doc != null && !string.IsNullOrWhiteSpace(doc.Title) ? doc.Title : r.DocumentEntry;
        var text = (string.IsNullOrWhiteSpace(r.Label) ? "" : r.Label + " — ") + $"{name}, p. {r.Page}";
        var missing = doc == null || doc.Sha256.Length == 0 || r.Page < 1 || (doc.Pages.Count > 0 && r.Page > doc.Pages.Count);
        return missing ? text + " " + Missing : text;
    }
}
