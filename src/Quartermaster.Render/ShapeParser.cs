// Ported from VASSAL NonRectangular.java (shape spec parsing) (LGPL-2.1).
using System.Collections.Concurrent;
using System.Globalization;
using SkiaSharp;

namespace Quartermaster.Render;

/// <summary>Parses a nonRect2 trait's ShapeSpec into a path in piece-centre-relative coordinates.</summary>
public static class ShapeParser
{
    private static readonly ConcurrentDictionary<string, SKPath?> Cache = new(StringComparer.Ordinal);

    /// <summary>The shape, or null when the spec is empty or unparsable. Results are cached; do not mutate the returned path.</summary>
    public static SKPath? TryParse(string spec) => Cache.GetOrAdd(spec ?? "", Build);

    private static SKPath? Build(string spec)
    {
        if (spec.Length == 0) return null;
        try
        {
            var path = spec.StartsWith('d') ? ParseNew(spec.Substring(1)) : ParseLegacy(spec);
            return path != null && !path.IsEmpty ? path : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) // any malformed spec (incl. OverflowException) is "unparsable"
        {
            return null;
        }
    }

    private static float F(string s) => (float)double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static SKPath? ParseNew(string spec)
    {
        var path = new SKPathBuilder();
        var t = spec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var i = 0;
        string Next() => i < t.Length ? t[i++] : throw new FormatException("Truncated shape spec.");
        while (i < t.Length)
        {
            switch (t[i++])
            {
                case "M": path.MoveTo(F(Next()), F(Next())); break;
                case "L": path.LineTo(F(Next()), F(Next())); break;
                case "Q": path.QuadTo(F(Next()), F(Next()), F(Next()), F(Next())); break;
                case "C": path.CubicTo(F(Next()), F(Next()), F(Next()), F(Next()), F(Next()), F(Next())); break;
                case "Z": path.Close(); break;
                case "N": i = t.Length; break; // image name runs to the end of the spec
            }
        }
        return path.Detach();
    }

    private static SKPath? ParseLegacy(string spec)
    {
        var t = spec.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length == 0) return null;
        var path = new SKPathBuilder();
        var i = 0;
        string Next() => i < t.Length ? t[i++] : throw new FormatException("Truncated shape spec.");
        while (i < t.Length)
        {
            var tok = t[i++];
            switch (tok[0])
            {
                case 'c': path.Close(); break;
                case 'm': path.MoveTo(int.Parse(Next(), CultureInfo.InvariantCulture), int.Parse(Next(), CultureInfo.InvariantCulture)); break;
                case 'l': path.LineTo(int.Parse(Next(), CultureInfo.InvariantCulture), int.Parse(Next(), CultureInfo.InvariantCulture)); break;
            }
        }
        return path.Detach();
    }
}
