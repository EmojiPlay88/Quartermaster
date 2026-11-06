namespace Quartermaster.Rules.Tables;

/// <summary>Pure edit operations on a table; each returns a new TableDef and never mutates its input.</summary>
public static class TableEdit
{
    public const string ValueColumn = "Value";

    public static TableDef New(string name, TableKind kind) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        Kind = kind,
        RowKeys = new() { "" },
        ColumnKeys = new() { kind == TableKind.TwoD ? "" : ValueColumn },
        Cells = new() { new() { "" } },
    };

    private static bool Single(TableKind k) => k != TableKind.TwoD;

    private static List<List<string>> CopyCells(TableDef t, int rows, int cols)
    {
        var res = new List<List<string>>(rows);
        for (int r = 0; r < rows; r++)
        {
            var row = new List<string>(cols);
            var src = r < t.Cells.Count ? t.Cells[r] : null;
            for (int c = 0; c < cols; c++) row.Add(src != null && c < src.Count ? src[c] : "");
            res.Add(row);
        }
        return res;
    }

    /// <summary>Normalised copy: Cells is exactly RowKeys x ColumnKeys.</summary>
    private static TableDef Make(TableDef t, List<string> rows, List<string> cols, List<List<string>> cells) =>
        t with { RowKeys = rows, ColumnKeys = cols, Cells = cells };

    public static TableDef AddRow(TableDef t, string key = "")
    {
        var rows = new List<string>(t.RowKeys) { key };
        var cells = CopyCells(t, t.RowKeys.Count, t.ColumnKeys.Count);
        cells.Add(Enumerable.Repeat("", t.ColumnKeys.Count).ToList());
        return Make(t, rows, new(t.ColumnKeys), cells);
    }

    public static TableDef RemoveRow(TableDef t, int index)
    {
        if (index < 0 || index >= t.RowKeys.Count) return t;
        var rows = new List<string>(t.RowKeys); rows.RemoveAt(index);
        var cells = CopyCells(t, t.RowKeys.Count, t.ColumnKeys.Count); cells.RemoveAt(index);
        return Make(t, rows, new(t.ColumnKeys), cells);
    }

    public static TableDef AddColumn(TableDef t, string key = "")
    {
        if (Single(t.Kind)) return t;
        var cols = new List<string>(t.ColumnKeys) { key };
        return Make(t, new(t.RowKeys), cols, CopyCells(t, t.RowKeys.Count, cols.Count));
    }

    public static TableDef RemoveColumn(TableDef t, int index)
    {
        if (Single(t.Kind) || index < 0 || index >= t.ColumnKeys.Count || t.ColumnKeys.Count <= 1) return t;
        var cols = new List<string>(t.ColumnKeys); cols.RemoveAt(index);
        var cells = CopyCells(t, t.RowKeys.Count, t.ColumnKeys.Count);
        foreach (var r in cells) r.RemoveAt(index);
        return Make(t, new(t.RowKeys), cols, cells);
    }

    public static TableDef SetCell(TableDef t, int row, int col, string text)
    {
        if (row < 0 || row >= t.RowKeys.Count || col < 0 || col >= t.ColumnKeys.Count) return t;
        var cells = CopyCells(t, t.RowKeys.Count, t.ColumnKeys.Count);
        cells[row][col] = text ?? "";
        return Make(t, new(t.RowKeys), new(t.ColumnKeys), cells);
    }

    public static TableDef SetRowKey(TableDef t, int row, string key)
    {
        if (row < 0 || row >= t.RowKeys.Count) return t;
        var rows = new List<string>(t.RowKeys) { [row] = key ?? "" };
        return Make(t, rows, new(t.ColumnKeys), CopyCells(t, rows.Count, t.ColumnKeys.Count));
    }

    public static TableDef SetColumnKey(TableDef t, int col, string key)
    {
        if (Single(t.Kind) || col < 0 || col >= t.ColumnKeys.Count) return t;
        var cols = new List<string>(t.ColumnKeys) { [col] = key ?? "" };
        return Make(t, new(t.RowKeys), cols, CopyCells(t, t.RowKeys.Count, cols.Count));
    }

    /// <summary>Replaces all column keys (TwoD only); cells are resized, keeping what still fits.</summary>
    public static TableDef SetColumnKeys(TableDef t, IReadOnlyList<string> keys)
    {
        if (Single(t.Kind) || keys.Count == 0) return t;
        var cols = keys.ToList();
        return Make(t, new(t.RowKeys), cols, CopyCells(t, t.RowKeys.Count, cols.Count));
    }

    /// <summary>OneD/Dice to TwoD keeps the value column as the first column; TwoD to OneD/Dice keeps only the first column.</summary>
    public static TableDef ChangeKind(TableDef t, TableKind kind)
    {
        if (kind == t.Kind) return t;
        if (kind == TableKind.TwoD)
            return Make(t with { Kind = kind }, new(t.RowKeys), new(t.ColumnKeys.Count == 0 ? new List<string> { ValueColumn } : t.ColumnKeys),
                CopyCells(t, t.RowKeys.Count, Math.Max(1, t.ColumnKeys.Count)));
        return Make(t with { Kind = kind }, new(t.RowKeys), new() { ValueColumn }, CopyCells(t, t.RowKeys.Count, 1));
    }

    public static bool ChangeLosesData(TableDef t, TableKind kind) =>
        kind != TableKind.TwoD && t.Kind == TableKind.TwoD && t.ColumnKeys.Count > 1;

    /// <summary>Replaces rows, columns and cells with a pasted grid. OneD/Dice tables take the first pasted column only.</summary>
    public static TableDef ApplyPaste(TableDef t, PastedGrid g)
    {
        int rows = g.Cells.Count;
        int width = rows == 0 ? 0 : g.Cells.Max(r => r.Count);
        var keys = new List<string>();
        for (int r = 0; r < rows; r++) keys.Add(r < g.RowKeys.Count ? g.RowKeys[r] : "");
        List<string> cols;
        if (Single(t.Kind)) { cols = new() { ValueColumn }; width = 1; }
        else
        {
            width = Math.Max(Math.Max(width, g.ColumnKeys.Count), 1);
            cols = Enumerable.Range(0, width).Select(c => c < g.ColumnKeys.Count ? g.ColumnKeys[c] : "").ToList();
        }
        var cells = g.Cells.Select(r => Enumerable.Range(0, width).Select(c => c < r.Count ? r[c] : "").ToList()).ToList();
        return Make(t, keys, cols, cells);
    }
}
