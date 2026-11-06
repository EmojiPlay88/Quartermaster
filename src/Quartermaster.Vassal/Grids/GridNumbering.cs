// Ported from VASSAL RegularGridNumbering.java, SquareGridNumbering.java, HexGridNumbering.java (LGPL-2.1).
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Quartermaster.Vassal;

/// <summary>Java arithmetic semantics (Math.round, int casts) used by the ported grid code.</summary>
internal static class JavaMath
{
    /// <summary>(int) cast: truncation toward zero, saturating, NaN to 0.</summary>
    public static int ToInt(double d)
    {
        if (double.IsNaN(d)) return 0;
        if (d >= int.MaxValue) return int.MaxValue;
        if (d <= int.MinValue) return int.MinValue;
        return (int)d;
    }

    /// <summary>Math.round: floor(d + 0.5).</summary>
    public static int Round(double d) => ToInt(Math.Floor(d + 0.5));

    public static int Floor(double d) => ToInt(Math.Floor(d));
}

internal static class GridAttr
{
    public static double Double(ModuleNode n, string name, double def, DiagnosticLog? log)
    {
        if (!n.Attributes.TryGetValue(name, out var s)) return def;
        if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)) return v;
        Bad(n, name, s, log);
        return def;
    }

    public static int Int(ModuleNode n, string name, int def, DiagnosticLog? log)
    {
        if (!n.Attributes.TryGetValue(name, out var s)) return def;
        if (int.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v)) return v;
        Bad(n, name, s, log);
        return def;
    }

    public static bool Bool(ModuleNode n, string name, bool def) =>
        n.Attributes.TryGetValue(name, out var s) ? string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) : def;

    public static RgbColor Color(ModuleNode n, DiagnosticLog? log)
    {
        if (!n.Attributes.TryGetValue("color", out var s)) return new RgbColor(0, 0, 0);
        var c = RgbColor.Parse(s);
        if (c == null) { Bad(n, "color", s, log); return new RgbColor(0, 0, 0); }
        return c.Value;
    }

    public static char Char(ModuleNode n, string name, char def) =>
        n.Attributes.TryGetValue(name, out var s) && s.Length > 0 ? s[0] : def;

    private static void Bad(ModuleNode n, string name, string value, DiagnosticLog? log) =>
        log?.AddOnce($"grid-attr:{n.ShortName}:{name}:{value}", DiagnosticSeverity.Warning, "Grid",
            $"Malformed {n.ShortName} attribute {name}='{value}'; default used.", n.ClassName);
}

/// <summary>Integer polygon with java.awt.Polygon.contains semantics. Path format "x,y;x,y;...".</summary>
internal sealed class Polygon
{
    public readonly List<int> Xs = new();
    public readonly List<int> Ys = new();

    public static Polygon Parse(string? path)
    {
        var p = new Polygon();
        foreach (var pt in SequenceEncoder.Decode(path, ';'))
        {
            var xy = SequenceEncoder.Decode(pt, ',');
            if (xy.Count >= 2
                && int.TryParse(xy[0].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var x)
                && int.TryParse(xy[1].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var y))
            {
                p.Xs.Add(x);
                p.Ys.Add(y);
            }
        }
        return p;
    }

    public int Count => Xs.Count;

    public (int X, int Y, int W, int H) Bounds
    {
        get
        {
            if (Count == 0) return (0, 0, 0, 0);
            int minx = Xs.Min(), maxx = Xs.Max(), miny = Ys.Min(), maxy = Ys.Max();
            return (minx, miny, maxx - minx, maxy - miny);
        }
    }

    public bool Contains(double x, double y) => Contains(Xs, Ys, x, y);

    public static bool Contains(IReadOnlyList<int> xs, IReadOnlyList<int> ys, double x, double y)
    {
        int n = xs.Count;
        if (n <= 2) return false;
        int minx = int.MaxValue, maxx = int.MinValue, miny = int.MaxValue, maxy = int.MinValue;
        for (int i = 0; i < n; i++)
        {
            minx = Math.Min(minx, xs[i]); maxx = Math.Max(maxx, xs[i]);
            miny = Math.Min(miny, ys[i]); maxy = Math.Max(maxy, ys[i]);
        }
        if (!(x >= minx && y >= miny && x < (long)minx + (maxx - minx) && y < (long)miny + (maxy - miny))) return false;

        int hits = 0;
        int lastx = xs[n - 1], lasty = ys[n - 1];
        for (int i = 0; i < n; i++)
        {
            int curx = xs[i], cury = ys[i];
            if (cury != lasty)
            {
                int leftx;
                bool skip = false;
                if (curx < lastx) { if (x >= lastx) skip = true; leftx = curx; }
                else { if (x >= curx) skip = true; leftx = lastx; }
                if (!skip)
                {
                    double test1, test2;
                    if (cury < lasty)
                    {
                        if (y < cury || y >= lasty) skip = true;
                        test1 = x - curx; test2 = y - cury;
                    }
                    else
                    {
                        if (y < lasty || y >= cury) skip = true;
                        test1 = x - lastx; test2 = y - lasty;
                    }
                    if (!skip)
                    {
                        if (x < leftx) hits++;
                        else if (test1 < (test2 / (lasty - cury) * (lastx - curx))) hits++;
                    }
                }
            }
            lastx = curx; lasty = cury;
        }
        return (hits & 1) != 0;
    }
}

/// <summary>Minimal port of VASSAL FormattedString for "$name$" templates.</summary>
internal static class Format
{
    /// <summary>Splits on '$': even indexes are literals, odd indexes are variable names (a trailing unterminated segment is literal).</summary>
    public static List<string> Split(string format) => new(format.Split('$'));

    public static bool IsVariable(List<string> parts, int i) => i % 2 == 1 && i < parts.Count - 1;

    public static string Expand(string format, Func<string, string> value)
    {
        var parts = Split(format);
        var sb = new StringBuilder();
        for (int i = 0; i < parts.Count; i++)
        {
            if (IsVariable(parts, i)) sb.Append(value(parts[i]));
            else sb.Append(i % 2 == 1 ? "$" + parts[i] : parts[i]);
        }
        return sb.ToString();
    }
}

/// <summary>Ported from RegularGridNumbering (shared naming/parsing). Subclasses supply the geometry.</summary>
internal abstract class GridNumbering
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string AlphabeticMatch = "-?(?:A+|B+|C+|D+|E+|F+|G+|H+|I+|J+|K+|L+|M+|N+|O+|P+|Q+|R+|S+|T+|U+|V+|W+|X+|Y+|Z+)";

    protected char First = 'H';
    protected string Sep = "";
    protected char HType = 'N';
    protected char VType = 'N';
    protected int HLeading = 1;
    protected int VLeading = 1;
    protected int HOff = 1;
    protected int VOff = 1;
    protected bool HDescending;
    protected bool VDescending;
    protected string LocationFormat = "$gridLocation$";
    protected SizeD Container;

    public bool Visible { get; protected set; }

    protected GridNumbering(ModuleNode n, SizeD container, DiagnosticLog log)
    {
        Container = container;
        First = GridAttr.Char(n, "first", First);
        Sep = n.Attr("sep", Sep);
        HType = GridAttr.Char(n, "hType", HType);
        VType = GridAttr.Char(n, "vType", VType);
        HLeading = GridAttr.Int(n, "hLeading", HLeading, log);
        VLeading = GridAttr.Int(n, "vLeading", VLeading, log);
        HOff = GridAttr.Int(n, "hOff", HOff, log);
        VOff = GridAttr.Int(n, "vOff", VOff, log);
        HDescending = GridAttr.Bool(n, "hDescend", false);
        VDescending = GridAttr.Bool(n, "vDescend", false);
        Visible = GridAttr.Bool(n, "visible", false);
        LocationFormat = n.Attr("locationFormat", LocationFormat);
    }

    public abstract int GetRow(int x, int y);
    public abstract int GetColumn(int x, int y);
    public abstract (int X, int Y) GetCenterPoint(int col, int row);

    protected string GetName(int row, int column)
    {
        var rowName = GetName(row + VOff, VType, VLeading);
        var colName = GetName(column + HOff, HType, HLeading);
        return First == 'H' ? colName + Sep + rowName : rowName + Sep + colName;
    }

    public string LocationName(int x, int y)
    {
        int row = GetRow(x, y), col = GetColumn(x, y);
        return Format.Expand(LocationFormat, v => v switch
        {
            "gridLocation" => GetName(row, col),
            "row" => GetName(row + VOff, VType, VLeading),
            "column" => GetName(col + HOff, HType, HLeading),
            _ => "",
        });
    }

    /// <summary>VASSAL getLocation; null for unparsable names.</summary>
    public (int X, int Y)? GetLocation(string location)
    {
        var parts = Format.Split(LocationFormat);
        var regex = new StringBuilder();
        int colGroup = 0, rowGroup = 0, groups = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            var token = parts[i];
            if (token.Length == 0) continue;
            if (!Format.IsVariable(parts, i)) regex.Append(Regex.Escape(i % 2 == 1 ? "$" + token : token));
            else if (token == "gridLocation")
            {
                if (First == 'H')
                {
                    regex.Append('(').Append(MatchingPattern(HType, HLeading)).Append(')');
                    colGroup = ++groups;
                    if (Sep.Length > 0) regex.Append(Regex.Escape(Sep));
                    regex.Append('(').Append(MatchingPattern(VType, VLeading)).Append(')');
                    rowGroup = ++groups;
                }
                else
                {
                    regex.Append('(').Append(MatchingPattern(VType, VLeading)).Append(')');
                    rowGroup = ++groups;
                    regex.Append(Regex.Escape(Sep)).Append('(').Append(MatchingPattern(HType, HLeading)).Append(')');
                    colGroup = ++groups;
                }
            }
            else if (token == "row")
            {
                regex.Append('(').Append(MatchingPattern(VType, VLeading)).Append(')');
                rowGroup = ++groups;
            }
            else if (token == "column")
            {
                regex.Append('(').Append(MatchingPattern(HType, HLeading)).Append(')');
                colGroup = ++groups;
            }
        }
        if (regex.Length == 0 || colGroup == 0 || rowGroup == 0) return null;

        Match m;
        try { m = Regex.Match(location ?? "", "^(?:" + regex + ")$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
        catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException) { return null; }
        if (!m.Success) return null;

        var row = ParseName(m.Groups[rowGroup].Value, VType);
        var col = ParseName(m.Groups[colGroup].Value, HType);
        if (row == null || col == null) return null;
        return GetCenterPoint(col.Value - HOff, row.Value - VOff);
    }

    private static string MatchingPattern(char type, int leading) =>
        type == 'A' ? AlphabeticMatch : "-?[0-9]{" + (Math.Max(leading, 0) + 1) + ",}";

    private static int? ParseName(string name, char type)
    {
        if (type == 'A')
        {
            int value = 0, index = 0;
            bool negative = false;
            if (name.StartsWith('-')) { negative = true; index++; }
            while (index < name.Length && name[index] >= 'A' && name[index] <= 'Z')
            {
                if (index < name.Length - 1) value += 26;
                else value += Alphabet.IndexOf(name[index]);
                index++;
            }
            return negative ? -value : value;
        }
        return int.TryParse(name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    protected static string GetName(int rowOrColumn, char type, int leading)
    {
        var val = new StringBuilder(rowOrColumn < 0 ? "-" : "");
        long n = Math.Abs((long)rowOrColumn);
        if (type == 'A')
        {
            do
            {
                val.Append(Alphabet[(int)(n % 26)]);
                n -= 26;
            } while (n >= 0);
            return val.ToString();
        }
        while (leading > 0 && n < Math.Pow(10.0, leading--)) val.Append('0');
        return val.Append(n).ToString();
    }
}

internal sealed class SquareGridNumbering : GridNumbering
{
    private readonly SquareGrid _grid;

    public SquareGridNumbering(ModuleNode n, SquareGrid grid, SizeD container, DiagnosticLog log) : base(n, container, log) => _grid = grid;

    private int MaxRows => JavaMath.ToInt(Container.H / _grid.Dy + 0.5);
    private int MaxColumns => JavaMath.ToInt(Container.W / _grid.Dx + 0.5);

    public override int GetColumn(int x, int y)
    {
        int col = JavaMath.Floor((x - _grid.OriginX) / _grid.Dx + 0.5);
        return HDescending ? MaxColumns - col : col;
    }

    public override int GetRow(int x, int y)
    {
        int row = JavaMath.ToInt((y - _grid.OriginY) / _grid.Dy + 0.5);
        return VDescending ? MaxRows - row : row;
    }

    public override (int X, int Y) GetCenterPoint(int col, int row)
    {
        if (VDescending) row = MaxRows - row;
        if (HDescending) col = MaxColumns - col;
        return (JavaMath.ToInt(col * _grid.Dx + _grid.OriginX), JavaMath.ToInt(row * _grid.Dy + _grid.OriginY));
    }
}

internal sealed class HexGridNumbering : GridNumbering
{
    private readonly HexGrid _grid;
    private readonly bool _stagger;

    public HexGridNumbering(ModuleNode n, HexGrid grid, SizeD container, DiagnosticLog log) : base(n, container, log)
    {
        _grid = grid;
        _stagger = GridAttr.Bool(n, "stagger", true);
    }

    private int MaxRows => JavaMath.Floor(Container.H / _grid.HexWidth + 0.5);
    private int MaxColumns => JavaMath.Floor(Container.W / _grid.HexSize + 0.5);

    public override (int X, int Y) GetCenterPoint(int col, int row)
    {
        if (_stagger && col % 2 != 0)
        {
            if (_grid.Sideways) row += HDescending ? 1 : -1;
            else row += VDescending ? 1 : -1;
        }

        if (_grid.Sideways)
        {
            if (VDescending) col = MaxRows - col;
            if (HDescending) row = MaxColumns - row;
        }
        else
        {
            if (HDescending) col = MaxColumns - col;
            if (VDescending) row = MaxRows - row;
        }

        int px = JavaMath.ToInt(col * _grid.HexWidth) + _grid.OriginX;
        int py = col % 2 == 0
            ? JavaMath.ToInt(row * _grid.HexSize)
            : JavaMath.ToInt(row * _grid.HexSize + _grid.HexSize / 2);
        py += _grid.OriginY;
        return _grid.Sideways ? (py, px) : (px, py);
    }

    public override int GetColumn(int x, int y)
    {
        int c = _grid.RawColumn(x, y);
        if (VDescending && _grid.Sideways) c = MaxRows - c;
        if (HDescending && !_grid.Sideways) c = MaxColumns - c;
        return c;
    }

    public override int GetRow(int x, int y)
    {
        int ny = _grid.RawRow(x, y);
        if (VDescending && !_grid.Sideways) ny = MaxRows - ny;
        if (HDescending && _grid.Sideways) ny = MaxColumns - ny;
        if (_stagger && _grid.RawColumn(x, y) % 2 != 0)
        {
            if (_grid.Sideways) ny += HDescending ? -1 : 1;
            else ny += VDescending ? -1 : 1;
        }
        return ny;
    }
}
