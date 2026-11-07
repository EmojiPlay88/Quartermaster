namespace Quartermaster.Render.Ocr;

/// <summary>An editable OCR grid: rebuilt from the words, tracks which cells the user edited.</summary>
public sealed class OcrGridState
{
    private readonly List<OcrWord> _words;
    private readonly bool _fixDigits;
    private List<double> _bounds;
    private readonly HashSet<(int, int)> _edited = new();

    public OcrGridState(IReadOnlyList<OcrWord> words, bool fixDigits)
    {
        _words = OcrResultModel.Clean(words);
        _fixDigits = fixDigits;
        _bounds = OcrResultModel.Boundaries(_words, null);
        Cells = Rebuild();
    }

    public string[][] Cells { get; private set; }
    public bool HasEdits => _edited.Count > 0;

    private string[][] Rebuild()
    {
        var g = _words.Count == 0 ? [] : OcrResultModel.Build(_words, _bounds);
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
    }

    public bool IsEdited(int r, int c) => _edited.Contains((r, c));

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

    public string ToTsv() => OcrResultModel.ToTsv(Cells);
}
