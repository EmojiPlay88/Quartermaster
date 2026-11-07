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

    public static string Line(string? sender, string clean)
    {
        var name = Clean(sender);
        return name == null ? clean : $"{name}: {clean}";
    }
}
