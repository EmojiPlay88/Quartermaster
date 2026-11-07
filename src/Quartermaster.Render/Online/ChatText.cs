using System.Text;

namespace Quartermaster.Render;

/// <summary>
/// Chat line text: control characters and line/paragraph separators become spaces, trimmed, at most <see cref="MaxLength"/> characters;
/// "{name}: {text}" when the sender is known.
/// </summary>
public static class ChatText
{
    public const int MaxLength = 500;

    public static string? Clean(string? text)
    {
        if (text == null) return null;
        var sb = new StringBuilder(Math.Min(text.Length, MaxLength * 2));
        foreach (var ch in text)
            sb.Append(char.IsControl(ch) || ch == (char)0x2028 || ch == (char)0x2029 || ch == (char)0x85 ? ' ' : ch);
        var t = sb.ToString().Trim();
        if (t.Length == 0) return null;
        return t.Length > MaxLength ? t[..MaxLength].TrimEnd() : t;
    }

    /// <summary>Longest player name.</summary>
    public const int MaxName = 40;

    /// <summary>
    /// A player name for comparing and display: NFKC-normalised, format characters (zero-width, bidi controls) removed, then as
    /// <see cref="Clean"/>, at most <see cref="MaxName"/> characters. Null when nothing is left (or for null).
    /// </summary>
    public static string? CleanName(string? name)
    {
        if (name == null) return null;
        string norm;
        try { norm = name.Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException) { norm = name; }   // invalid UTF-16 (lone surrogates): stripped below
        var sb = new StringBuilder(norm.Length);
        for (var i = 0; i < norm.Length; i++)
        {
            var pair = char.IsSurrogatePair(norm, i);
            if (!pair && char.IsSurrogate(norm[i])) continue;   // a lone surrogate
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(norm, i) != System.Globalization.UnicodeCategory.Format)
            {
                sb.Append(norm[i]);
                if (pair) sb.Append(norm[i + 1]);
            }
            if (pair) i++;
        }
        var clean = Clean(sb.ToString());
        if (clean == null) return null;
        if (clean.Length <= MaxName) return clean;
        var cut = char.IsHighSurrogate(clean[MaxName - 1]) ? MaxName - 1 : MaxName;   // never split a pair
        return clean[..cut].TrimEnd();
    }

    public static string Line(string? sender, string clean)
    {
        var name = Clean(sender);
        return name == null ? clean : $"{name}: {clean}";
    }
}
