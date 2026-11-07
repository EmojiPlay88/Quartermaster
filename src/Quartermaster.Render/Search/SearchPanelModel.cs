using Quartermaster.Rules;

namespace Quartermaster.Render.Search;

/// <summary>A piece of snippet text, drawn bold when it is a hit.</summary>
public sealed record TextRun(string Text, bool Bold);

public enum SearchRowKind
{
    /// <summary>A document heading: title, how many pages match and the "Probably defined on page N" hint.</summary>
    Document,
    /// <summary>A matching page: <see cref="SearchRow.Page"/> and the snippet as runs.</summary>
    Hit,
    /// <summary>A document whose text could not be read (shown greyed).</summary>
    NotSearchable,
    /// <summary>A plain line ("No pages match …").</summary>
    Message,
}

/// <summary>One line of the results panel. <see cref="Runs"/> is the snippet for a hit, else one run holding <see cref="Text"/>.</summary>
public sealed record SearchRow(SearchRowKind Kind, string Entry, string Title, int Page, string Text, IReadOnlyList<TextRun> Runs);

/// <summary>How an item's rule ref reads in an editor; <see cref="IsLink"/> when clicking it should open the page.</summary>
public sealed record RefDisplay(string Text, bool IsLink, bool Missing);

/// <summary>
/// The rulebook results panel and rule-ref links as plain data: grouping hits per document, snippet runs with the hits bold,
/// the text the editors' "Find in rulebook" searches, and how a rule ref reads.
/// </summary>
public static class SearchPanelModel
{
    public const int MaxHits = 200;
    public const string NotSearchableText = "not searchable";
    public const string None = "(none)";
    /// <summary>A snippet is cut so its first hit starts within this many characters (it must show on a narrow panel).</summary>
    public const int DisplayLead = 40;

    public static string DefinedHint(int page) => $"Probably defined on page {page}";

    /// <summary>
    /// The results for a query: per document (index order) a heading and its matching pages in page order; every document that is
    /// not searchable is listed greyed "not searchable". An empty query gives no rows.
    /// </summary>
    public static IReadOnlyList<SearchRow> Build(RulebookIndex index, string? query)
    {
        var rows = new List<SearchRow>();
        if (index == null || string.IsNullOrWhiteSpace(query)) return rows;
        var hits = index.Search(query, MaxHits);
        var term = GlossaryTerm(query);
        var byDoc = hits.GroupBy(h => h.Entry).ToDictionary(g => g.Key, g => g.OrderBy(h => h.Page).ToList());
        if (hits.Count == 0) rows.Add(Message($"No pages match {query.Trim()}."));
        foreach (var doc in index.Documents)
        {
            if (!doc.Searchable)
            {
                var t = $"{doc.Title} — {NotSearchableText}";
                rows.Add(new SearchRow(SearchRowKind.NotSearchable, doc.Entry, doc.Title, 0, t, new[] { new TextRun(t, false) }));
                continue;
            }
            if (!byDoc.TryGetValue(doc.Entry, out var list)) continue;
            var head = $"{doc.Title} — {list.Count} page{(list.Count == 1 ? "" : "s")}";
            if (term != null && index.ProbablyDefinedOn(term, doc.Entry) is { } p) head += " · " + DefinedHint(p);
            rows.Add(new SearchRow(SearchRowKind.Document, doc.Entry, doc.Title, 0, head, new[] { new TextRun(head, true) }));
            foreach (var h in list)
                rows.Add(new SearchRow(SearchRowKind.Hit, h.Entry, h.Title, h.Page, "p. " + h.Page, Runs(h.Snippet, h.Marks, DisplayLead)));
        }
        if (hits.Count >= MaxHits) rows.Add(Message($"Showing the first {MaxHits} pages."));
        return rows;
    }

    private static SearchRow Message(string text) => new(SearchRowKind.Message, "", "", 0, text, new[] { new TextRun(text, false) });

    /// <summary>
    /// Splits a snippet into plain and bold runs (marks clamped, sorted and merged). With <paramref name="maxLead"/>, text before
    /// the first mark is cut (at a space where possible, marked "…") so the first hit starts within that many characters.
    /// </summary>
    public static IReadOnlyList<TextRun> Runs(string snippet, IReadOnlyList<(int Start, int Length)> marks, int? maxLead = null)
    {
        snippet ??= "";
        var ranges = new List<(int S, int E)>();
        foreach (var (s0, l) in marks ?? Array.Empty<(int, int)>())
        {
            var s = Math.Clamp(s0, 0, snippet.Length);
            var e = Math.Clamp(s0 + Math.Max(l, 0), 0, snippet.Length);
            if (e > s) ranges.Add((s, e));
        }
        ranges.Sort();
        var merged = new List<(int S, int E)>();
        foreach (var r in ranges)
        {
            if (merged.Count > 0 && r.S <= merged[^1].E) merged[^1] = (merged[^1].S, Math.Max(merged[^1].E, r.E));
            else merged.Add(r);
        }

        var cut = 0;
        var prefix = "";
        if (maxLead is { } lead && merged.Count > 0 && merged[0].S > lead)
        {
            cut = merged[0].S - lead;
            var space = snippet.IndexOf(' ', cut);
            if (space >= 0 && space < merged[0].S) cut = space + 1;
            if (char.IsLowSurrogate(snippet[cut])) cut++;
            prefix = "…";
        }

        var runs = new List<TextRun>();
        var at = cut;
        foreach (var (s, e) in merged)
        {
            if (s > at) runs.Add(new TextRun(snippet[at..s], false));
            runs.Add(new TextRun(snippet[s..e], true));
            at = e;
        }
        if (at < snippet.Length || runs.Count == 0) runs.Add(new TextRun(snippet[at..], false));
        if (prefix.Length > 0)
        {
            if (!runs[0].Bold) runs[0] = runs[0] with { Text = prefix + runs[0].Text.TrimStart('…') };
            else runs.Insert(0, new TextRun(prefix, false));
        }
        return runs;
    }

    /// <summary>The single word or phrase of a query (for the glossary hint); null when the query has several terms or none.</summary>
    public static string? GlossaryTerm(string? query)
    {
        var (words, phrases) = SearchQuery.Parse(query);
        return words.Count + phrases.Count == 1 ? (phrases.Count == 1 ? phrases[0] : words[0]) : null;
    }

    /// <summary>The term handed to the PDF viewer to highlight (<c>#search=</c>): the first phrase, else the first word; null for none.</summary>
    public static string? ViewerSearchTerm(string? query)
    {
        var (words, phrases) = SearchQuery.Parse(query);
        return phrases.Count > 0 ? phrases[0] : words.Count > 0 ? words[0] : null;
    }

    /// <summary>PDF viewer URL fragment: <c>#page=N</c>, plus <c>&amp;search=term</c> (Edge's viewer highlights it; others ignore it).</summary>
    public static string ViewerFragment(int page, string? search) =>
        "#page=" + page + (string.IsNullOrWhiteSpace(search) ? "" : "&search=" + Uri.EscapeDataString(search.Trim()));

    /// <summary>
    /// What "Find in rulebook" searches: the selected text (whitespace collapsed; several words become a "quoted phrase"), else the
    /// item's name as plain words. Empty when there is neither.
    /// </summary>
    public static string FindText(string? selection, string? itemName)
    {
        var sel = Collapse((selection ?? "").Replace('"', ' ').Replace('“', ' ').Replace('”', ' '));
        if (sel.Length > 0 && sel.Length <= 120) return sel.Contains(' ') ? "\"" + sel + "\"" : sel;
        return Collapse((itemName ?? "").Replace('"', ' '));
    }

    private static string Collapse(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// How a rule ref reads in an editor. <paramref name="moduleDocs"/> are the module's documents; <paramref name="index"/> is
    /// the rulebook index (null while it is still being built). A ref to a document that is not in the module, could not be opened,
    /// or a page past its end is "(missing)" and not a link.
    /// </summary>
    public static RefDisplay DescribeRef(RuleRef? r, IReadOnlyList<(string Entry, string Title)> moduleDocs, IReadOnlyList<IndexedDocument>? index)
    {
        if (r == null) return new RefDisplay(None, false, false);
        (string Entry, string Title)? inModule = null;
        foreach (var d in moduleDocs ?? Array.Empty<(string, string)>())
            if (d.Entry == r.DocumentEntry) { inModule = d; break; }
        string text;
        bool missing;
        if (inModule is not { } m)
        {
            text = Plain(r, r.DocumentEntry);
            missing = true;
        }
        else if (index?.Any(d => d.Entry == r.DocumentEntry) == true)
        {
            text = RuleRefLinks.Describe(r, index);
            missing = text.EndsWith(" " + RuleRefLinks.Missing, StringComparison.Ordinal);
            if (missing) text = text[..^(RuleRefLinks.Missing.Length + 1)];
        }
        else
        {
            // Not indexed (yet, or not a PDF): the module has it, so it opens.
            text = Plain(r, string.IsNullOrWhiteSpace(m.Title) ? r.DocumentEntry : m.Title);
            missing = r.Page < 1;
        }
        return missing ? new RefDisplay(text + " " + RuleRefLinks.Missing, false, true) : new RefDisplay(text, true, false);
    }

    private static string Plain(RuleRef r, string name) =>
        (string.IsNullOrWhiteSpace(r.Label) ? "" : r.Label + " — ") + $"{name}, p. {r.Page}";

    /// <summary>"Used on this page" lines: "Formula: Name" etc., in <see cref="RuleRefLinks.UsedOnPage"/> order.</summary>
    public static IReadOnlyList<(string Kind, string Id, string Text)> UsedOnPageRows(RulesPack? pack, string? entry, int page)
    {
        if (pack == null || string.IsNullOrEmpty(entry)) return Array.Empty<(string, string, string)>();
        return RuleRefLinks.UsedOnPage(pack, entry, page).Select(u => (u.Kind, u.Id, $"{u.Kind}: {u.Name}")).ToList();
    }
}
