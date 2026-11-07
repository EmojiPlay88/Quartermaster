namespace Quartermaster.Render.Ocr;

/// <summary>Pure conversion of OCR word boxes into lines, rows, columns and TSV.</summary>
public static class OcrResultModel
{
    internal static List<OcrWord> Clean(IReadOnlyList<OcrWord> words) =>
        words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();

    internal static List<List<OcrWord>> Lines(IReadOnlyList<OcrWord> words)
    {
        var sorted = Clean(words).OrderBy(w => w.Y + w.H / 2).ThenBy(w => w.X).ToList();
        var lines = new List<List<OcrWord>>();
        double top = 0, bottom = 0;
        foreach (var w in sorted)
        {
            if (lines.Count > 0)
            {
                double overlap = Math.Min(bottom, w.Y + w.H) - Math.Max(top, w.Y);
                double minH = Math.Min(w.H, bottom - top);
                if (overlap > 0 && overlap >= 0.5 * minH)
                {
                    lines[^1].Add(w);
                    top = Math.Min(top, w.Y);
                    bottom = Math.Max(bottom, w.Y + w.H);
                    continue;
                }
            }
            lines.Add(new List<OcrWord> { w });
            top = w.Y;
            bottom = w.Y + w.H;
        }
        foreach (var l in lines) l.Sort((a, b) => a.X.CompareTo(b.X));
        return lines;
    }

    /// <summary>X ranges not covered by any word, between the leftmost and rightmost word.</summary>
    internal static List<(double Start, double End)> SharedGaps(IReadOnlyList<OcrWord> words)
    {
        var ws = Clean(words).OrderBy(w => w.X).ToList();
        var gaps = new List<(double, double)>();
        if (ws.Count == 0) return gaps;
        double reach = ws[0].X + ws[0].W;
        foreach (var w in ws.Skip(1))
        {
            if (w.X > reach) gaps.Add((reach, w.X));
            reach = Math.Max(reach, w.X + w.W);
        }
        return gaps;
    }

    internal static double MedianHeight(IReadOnlyList<OcrWord> words)
    {
        var hs = Clean(words).Select(w => w.H).OrderBy(h => h).ToList();
        if (hs.Count == 0) return 0;
        return hs.Count % 2 == 1 ? hs[hs.Count / 2] : (hs[hs.Count / 2 - 1] + hs[hs.Count / 2]) / 2;
    }

    /// <summary>Column boundaries (x midpoints of gaps): automatic, or the N-1 widest gaps when forced.</summary>
    internal static List<double> Boundaries(IReadOnlyList<OcrWord> words, int? columns)
    {
        var gaps = SharedGaps(words);
        IEnumerable<(double Start, double End)> chosen;
        if (columns is int n)
            chosen = n <= 1 ? [] : gaps.OrderByDescending(g => g.End - g.Start).Take(n - 1);
        else
        {
            double min = 1.2 * MedianHeight(words);
            chosen = gaps.Where(g => g.End - g.Start > min);
        }
        return chosen.Select(g => (g.Start + g.End) / 2).OrderBy(x => x).ToList();
    }

    internal static string[][] Build(IReadOnlyList<OcrWord> words, IReadOnlyList<double> boundaries)
    {
        var lines = Lines(words);
        int cols = boundaries.Count + 1;
        var grid = new string[lines.Count][];
        for (int r = 0; r < lines.Count; r++)
        {
            var parts = new List<string>?[cols];
            foreach (var w in lines[r])
            {
                double cx = w.X + w.W / 2;
                int c = 0;
                while (c < boundaries.Count && cx > boundaries[c]) c++;
                (parts[c] ??= new List<string>()).Add(w.Text.Trim());
            }
            grid[r] = parts.Select(p => p == null ? "" : string.Join(" ", p)).ToArray();
        }
        return grid;
    }

    public static string[][] BuildGrid(IReadOnlyList<OcrWord> words, int? columns = null)
    {
        if (Clean(words).Count == 0) return [];
        return Build(words, Boundaries(words, columns));
    }

    public static string ToTsv(IReadOnlyList<IReadOnlyList<string>> grid)
    {
        static string Cell(string? s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        return string.Join("\n", grid.Select(row => string.Join("\t", row.Select(Cell))));
    }

    public static string ToText(IReadOnlyList<OcrWord> words) =>
        string.Join("\n", Lines(words).Select(l => string.Join(" ", l.Select(w => w.Text.Trim()))));
}
