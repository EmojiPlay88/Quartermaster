using System.Globalization;
using System.Text;

namespace Quartermaster.Render.Search;

/// <summary>
/// Rulebook query syntax: plain words (all must appear on the page) and "quoted phrases" (must appear exactly, ignoring case,
/// accents and runs of whitespace). An unclosed quote takes the rest of the query as a phrase.
/// </summary>
public static class SearchQuery
{
    /// <summary>Splits a query into words and phrases as typed (trimmed, inner whitespace collapsed, duplicates removed).</summary>
    public static (IReadOnlyList<string> Words, IReadOnlyList<string> Phrases) Parse(string? query)
    {
        var words = new List<string>();
        var phrases = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return (words, phrases);
        var q = query.Replace('“', '"').Replace('”', '"');
        var i = 0;
        while (i < q.Length)
        {
            if (q[i] == '"')
            {
                var close = q.IndexOf('"', i + 1);
                var body = close < 0 ? q[(i + 1)..] : q[(i + 1)..close];
                var phrase = Collapse(body);
                if (phrase.Length > 0)
                {
                    // A one-word "phrase" is just a word.
                    if (phrase.Contains(' ')) AddUnique(phrases, phrase); else AddUnique(words, phrase);
                }
                i = close < 0 ? q.Length : close + 1;
                continue;
            }
            if (char.IsWhiteSpace(q[i])) { i++; continue; }
            var start = i;
            while (i < q.Length && !char.IsWhiteSpace(q[i]) && q[i] != '"') i++;
            AddUnique(words, q[start..i]);
        }
        return (words, phrases);
    }

    /// <summary>The query's terms in matching form (lower-case, accents stripped); empty terms dropped.</summary>
    internal static IReadOnlyList<string> NormalisedTerms(string? query)
    {
        var (words, phrases) = Parse(query);
        var terms = new List<string>();
        foreach (var t in phrases.Concat(words))
        {
            var n = TextNormaliser.Normalise(t).Trim();
            if (n.Length > 0 && !terms.Contains(n)) terms.Add(n);
        }
        return terms;
    }

    private static string Collapse(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void AddUnique(List<string> list, string s)
    {
        if (s.Length > 0 && !list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(s);
    }
}

/// <summary>
/// Matching form of text: compatibility-decomposed, combining marks removed, lower-cased (invariant), every whitespace char a
/// single space, typographic quotes/dashes folded. <see cref="NormaliseWithMap"/> also returns, for each output char, the index
/// of the source char it came from, so hits can be marked in the original text.
/// </summary>
internal static class TextNormaliser
{
    public static string Normalise(string s) => NormaliseWithMap(s, collapseSpaces: true).Text;

    /// <summary>
    /// First ordinal occurrence of <paramref name="term"/> at or after <paramref name="from"/> that starts a word (the char before it
    /// is not a letter or digit): "die" finds "dies" but not "soldier". -1 when none.
    /// </summary>
    public static int IndexOfWordStart(string text, string term, int from)
    {
        if (term.Length == 0) return -1;
        var at = from < text.Length ? text.IndexOf(term, from, StringComparison.Ordinal) : -1;
        while (at >= 0)
        {
            if (at == 0 || !char.IsLetterOrDigit(text[at - 1])) return at;
            at = at + 1 < text.Length ? text.IndexOf(term, at + 1, StringComparison.Ordinal) : -1;
        }
        return -1;
    }

    public static (string Text, int[] Map) NormaliseWithMap(string s, bool collapseSpaces)
    {
        var sb = new StringBuilder(s.Length);
        var map = new List<int>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c < 0x80)
            {
                if (char.IsWhiteSpace(c)) { AppendSpace(sb, map, i, collapseSpaces); continue; }
                sb.Append(c is >= 'A' and <= 'Z' ? (char)(c + 32) : c);
                map.Add(i);
                continue;
            }
            if (char.IsWhiteSpace(c)) { AppendSpace(sb, map, i, collapseSpaces); continue; }
            if (c == '­') continue; // soft hyphen: invisible
            var folded = Fold(c);
            if (folded != '\0') { sb.Append(folded); map.Add(i); continue; }
            string unit;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) unit = s.Substring(i, 2);
            else unit = c.ToString();
            string decomposed;
            try { decomposed = unit.Normalize(NormalizationForm.FormKD); }
            catch (ArgumentException) { decomposed = unit; } // lone surrogate
            foreach (var d in decomposed)
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(d);
                if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
                if (char.IsWhiteSpace(d)) { AppendSpace(sb, map, i, collapseSpaces); continue; }
                sb.Append(char.ToLowerInvariant(d));
                map.Add(i);
            }
            if (unit.Length == 2) i++;
        }
        return (sb.ToString(), map.ToArray());
    }

    private static void AppendSpace(StringBuilder sb, List<int> map, int i, bool collapse)
    {
        if (collapse && sb.Length > 0 && sb[^1] == ' ') return;
        sb.Append(' ');
        map.Add(i);
    }

    private static char Fold(char c) => c switch
    {
        '‘' or '’' or '‚' or '′' => '\'',
        '“' or '”' or '„' or '″' => '"',
        '‐' or '‑' or '‒' or '–' or '—' or '−' => '-',
        _ => '\0',
    };
}
