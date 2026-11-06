using System.Globalization;
using System.Text;
using Quartermaster.Rules.Expr;

namespace Quartermaster.Rules.Tables;

public enum TableKeyKind { Number, Range, AtLeast, AtMost, Text }

public readonly record struct TableKey(TableKeyKind Kind, double Lo, double Hi, string Text);

/// <summary>Parses table row/column keys and resolves lookups. Failures are <see cref="EvalError"/>.</summary>
public static class TableLookup
{
    public static string Normalise(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch == ' ' || (ch >= ' ' && ch <= ' ') || ch == ' ' || ch == '\t') sb.Append(' ');
            else sb.Append(ch);
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Dashes(string s) => s.Replace('–', '-').Replace('—', '-').Replace('−', '-');

    internal static bool TryNum(string s, out double d)
    {
        d = 0;
        s = Dashes(s).Trim();
        if (s.Length == 0) return false;
        if (!double.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out d)) return false;
        return !double.IsNaN(d) && !double.IsInfinity(d);
    }

    public static TableKey ParseKey(string raw)
    {
        var s = Normalise(raw);
        var d = Dashes(s);
        double a, b;

        if (d.StartsWith(">=", StringComparison.Ordinal) && TryNum(d[2..], out a)) return new(TableKeyKind.AtLeast, a, double.PositiveInfinity, s);
        if (d.StartsWith('≥') && TryNum(d[1..], out a)) return new(TableKeyKind.AtLeast, a, double.PositiveInfinity, s);
        if (d.StartsWith("<=", StringComparison.Ordinal) && TryNum(d[2..], out a)) return new(TableKeyKind.AtMost, double.NegativeInfinity, a, s);
        if (d.StartsWith('≤') && TryNum(d[1..], out a)) return new(TableKeyKind.AtMost, double.NegativeInfinity, a, s);
        if (d.EndsWith('+') && TryNum(d[..^1], out a)) return new(TableKeyKind.AtLeast, a, double.PositiveInfinity, s);
        if (d.EndsWith('-') && TryNum(d[..^1], out a)) return new(TableKeyKind.AtMost, double.NegativeInfinity, a, s);
        if (TryNum(d, out a)) return new(TableKeyKind.Number, a, a, s);

        for (int i = 1; i < d.Length - 1; i++)
        {
            if (d[i] != '-') continue;
            if (TryNum(d[..i], out a) && TryNum(d[(i + 1)..], out b))
                return new(TableKeyKind.Range, Math.Min(a, b), Math.Max(a, b), s);
        }
        return new(TableKeyKind.Text, 0, 0, s);
    }

    public static bool Matches(TableKey k, Value v)
    {
        if (k.Kind == TableKeyKind.Text)
        {
            if (v.Kind == ValueKind.Bool) return false;
            var vt = v.Kind == ValueKind.Number ? v.Display() : Normalise(v.Text);
            return string.Equals(k.Text, vt, StringComparison.OrdinalIgnoreCase);
        }
        double n;
        if (v.Kind == ValueKind.Number) n = v.Number;
        else if (v.Kind == ValueKind.Text)
        {
            var nt = Normalise(v.Text);
            if (k.Kind == TableKeyKind.Number && string.Equals(k.Text, nt, StringComparison.OrdinalIgnoreCase)) return true;
            if (!TryNum(nt, out n)) return false;
        }
        else return false;
        return k.Kind switch
        {
            TableKeyKind.Number => n == k.Lo,
            TableKeyKind.Range => n >= k.Lo && n <= k.Hi,
            TableKeyKind.AtLeast => n >= k.Lo,
            TableKeyKind.AtMost => n <= k.Hi,
            _ => false,
        };
    }

    private static int FindIndex(List<string> keys, Value v)
    {
        for (int i = 0; i < keys.Count; i++)
            if (Matches(ParseKey(keys[i]), v)) return i;
        return -1;
    }

    public static Value Lookup(TableDef t, Value row, Value? col)
    {
        var (r, c) = Locate(t, row, col);
        return CellAt(t, r, c);
    }

    /// <summary>The cell at the given indices, as a number when it parses, else as text.</summary>
    public static Value CellAt(TableDef t, int r, int c)
    {
        var cell = Normalise(r >= 0 && r < t.Cells.Count && c >= 0 && c < t.Cells[r].Count ? t.Cells[r][c] : "");
        return TryNum(cell, out var num) ? Value.Of(num) : Value.Of(cell);
    }

    /// <summary>Finds the matched (row, column) indices. Throws <see cref="EvalError"/> when nothing matches.</summary>
    public static (int Row, int Col) Locate(TableDef t, Value row, Value? col)
    {
        int c = 0;
        if (t.Kind == TableKind.TwoD)
        {
            if (col is null) throw new EvalError($"Table {t.Name} needs a column");
            c = FindIndex(t.ColumnKeys, col.Value);
            if (c < 0) throw new EvalError($"No column for {col.Value.Display()} in {t.Name}");
        }
        int r = FindIndex(t.RowKeys, row);
        if (r < 0) throw new EvalError($"No row for {row.Display()} in {t.Name}");
        return (r, c);
    }
}
