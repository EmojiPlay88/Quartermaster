using System.Drawing;

namespace Quartermaster.Render.Ocr;

/// <summary>An empty grid cell inside occupied row and column bands, with the image box (px) to re-read it from.</summary>
public readonly record struct EmptyCell(int Row, int Col, RectangleF Box);

/// <summary>An editable OCR grid: rebuilt from the words, tracks which cells the user edited and which a second pass re-read.</summary>
public sealed class OcrGridState
{
    private readonly List<OcrWord> _words;
    private readonly bool _fixDigits;
    private List<double> _bounds;
    private readonly HashSet<(int, int)> _edited = new();
    private readonly List<OcrWord> _extra = new();
    private readonly HashSet<(int, int)> _reread = new();

    public OcrGridState(IReadOnlyList<OcrWord> words, bool fixDigits)
    {
        _words = OcrResultModel.Clean(words);
        _fixDigits = fixDigits;
        _bounds = OcrResultModel.Boundaries(_words, null);
        Cells = Rebuild();
    }

    public string[][] Cells { get; private set; }
    public bool HasEdits => _edited.Count > 0;

    /// <summary>Median word height in image px (the padding unit of the empty-cell re-read).</summary>
    public double MedianWordHeight => OcrResultModel.MedianHeight(_words);

    /// <summary>The words the grid is built from, including accepted re-reads.</summary>
    public IReadOnlyList<OcrWord> Words => _words;

    private string[][] Rebuild()
    {
        _reread.Clear();
        var extra = _extra.Count == 0 ? null : new HashSet<OcrWord>(_extra);
        var g = _words.Count == 0 ? [] : OcrResultModel.Build(_words, _bounds,
            extra == null ? null : (w, r, c) => { if (extra.Contains(w)) _reread.Add((r, c)); });
        if (_fixDigits)
            foreach (var row in g)
                for (int c = 0; c < row.Length; c++) row[c] = OcrCleanup.Fix(row[c]);
        return g;
    }

    private void Reset(List<double> bounds)
    {
        _bounds = bounds;
        _edited.Clear();
        Cells = Rebuild();
    }

    public void Edit(int r, int c, string text)
    {
        if (r < 0 || r >= Cells.Length || c < 0 || c >= Cells[r].Length) return;
        Cells[r][c] = text ?? "";
        _edited.Add((r, c));
        _reread.Remove((r, c));
    }

    public bool IsEdited(int r, int c) => _edited.Contains((r, c));

    /// <summary>The cell was filled by the second pass over empty cells (not by the user).</summary>
    public bool IsReread(int r, int c) => _reread.Contains((r, c));

    public void SetColumns(int? n) => Reset(OcrResultModel.Boundaries(_words, n));

    public void MergeColumns(int left)
    {
        if (left < 0 || left >= _bounds.Count) return;
        var b = new List<double>(_bounds);
        b.RemoveAt(left);
        Reset(b);
    }

    public void SplitColumn(int col)
    {
        if (col < 0 || col > _bounds.Count) return;
        double lo = col == 0 ? double.NegativeInfinity : _bounds[col - 1];
        double hi = col == _bounds.Count ? double.PositiveInfinity : _bounds[col];
        var cand = OcrResultModel.SharedGaps(_words)
            .Where(g => (g.Start + g.End) / 2 > lo && (g.Start + g.End) / 2 < hi)
            .OrderByDescending(g => g.End - g.Start)
            .ToList();
        if (cand.Count == 0) return;
        var b = new List<double>(_bounds) { (cand[0].Start + cand[0].End) / 2 };
        b.Sort();
        Reset(b);
    }

    /// <summary>
    /// Empty cells whose row has words and whose column has words in some row. The box spans the row's line band (a little taller)
    /// and the column's word extent (a little wider, never past the column boundaries), so ruled lines in the gaps stay out.
    /// </summary>
    public IReadOnlyList<EmptyCell> EmptyCells()
    {
        var result = new List<EmptyCell>();
        if (_words.Count == 0) return result;
        var lines = OcrResultModel.Lines(_words);
        int cols = _bounds.Count + 1;
        var colLo = Enumerable.Repeat(double.PositiveInfinity, cols).ToArray();
        var colHi = Enumerable.Repeat(double.NegativeInfinity, cols).ToArray();
        var filled = new HashSet<(int, int)>();
        OcrResultModel.Build(_words, _bounds, (w, r, c) =>
        {
            colLo[c] = Math.Min(colLo[c], w.X);
            colHi[c] = Math.Max(colHi[c], w.X + w.W);
            filled.Add((r, c));
        });
        double h = OcrResultModel.MedianHeight(_words);
        for (int r = 0; r < lines.Count && r < Cells.Length; r++)
        {
            double top = lines[r].Min(w => w.Y) - 0.2 * h, bottom = lines[r].Max(w => w.Y + w.H) + 0.2 * h;
            for (int c = 0; c < cols; c++)
            {
                if (filled.Contains((r, c)) || double.IsInfinity(colLo[c])) continue;
                if (c < Cells[r].Length && Cells[r][c].Length > 0) continue;
                double lo = colLo[c] - 0.3 * h, hi = colHi[c] + 0.3 * h;
                if (c > 0) lo = Math.Max(lo, _bounds[c - 1]);
                if (c < _bounds.Count) hi = Math.Min(hi, _bounds[c]);
                if (hi <= lo || bottom <= top) continue;
                result.Add(new EmptyCell(r, c, new RectangleF((float)lo, (float)top, (float)(hi - lo), (float)(bottom - top))));
            }
        }
        return result;
    }

    /// <summary>
    /// Adds the second-pass readings (aligned with <paramref name="cells"/>; null = nothing read) that
    /// <see cref="OcrCleanup.AcceptsCellReread"/> accepts, and rebuilds the grid with the same columns. Not applied once the user
    /// has edited the grid. Returns how many were accepted.
    /// </summary>
    public int AddRereads(IReadOnlyList<EmptyCell> cells, IReadOnlyList<OcrWord?> found)
    {
        if (HasEdits) return 0;
        int n = 0;
        for (int i = 0; i < cells.Count && i < found.Count; i++)
        {
            if (found[i] is not { } w || !OcrCleanup.AcceptsCellReread(w.Text)) continue;
            var word = w with { Text = new string(w.Text.Where(ch => !char.IsWhiteSpace(ch)).ToArray()) };
            _words.Add(word);
            _extra.Add(word);
            n++;
        }
        if (n > 0) Cells = Rebuild();
        return n;
    }

    public string ToTsv() => OcrResultModel.ToTsv(Cells);
}
