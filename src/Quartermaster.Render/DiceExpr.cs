using System.Globalization;
using System.Text.RegularExpressions;

namespace Quartermaster.Render;

public sealed record DiceSpec(int Count, int Sides, int Modifier);

/// <summary>Parses and formats dice expressions such as 2d6 or 3d10+2. Never throws.</summary>
public static partial class DiceExpr
{
    public const string FormatError = "Dice must look like 2d6 or 3d10+2";

    [GeneratedRegex(@"^\s*(\d*)\s*[dD]\s*(\d+)\s*([+-]\s*\d+)?\s*$")]
    private static partial Regex Pattern();

    public static (DiceSpec? Spec, string? Error) Parse(string? text)
    {
        var m = Pattern().Match(text ?? "");
        if (!m.Success) return (null, FormatError);
        var count = 1;
        if (m.Groups[1].Length > 0 && !TryInt(m.Groups[1].Value, out count)) count = int.MaxValue;
        if (count is < 1 or > 100) return (null, "Use 1–100 dice");
        if (!TryInt(m.Groups[2].Value, out var sides) || sides is < 2 or > 1000) return (null, "Dice need 2–1000 sides");
        var mod = 0;
        if (m.Groups[3].Success)
        {
            var raw = m.Groups[3].Value.Replace(" ", "").Replace("\t", "");
            if (!TryInt(raw, out mod) || mod is < -1000 or > 1000) return (null, "Modifier must be between -1000 and 1000");
        }
        return (new DiceSpec(count, sides, mod), null);
    }

    public static string Format(DiceSpec s)
    {
        var t = $"{s.Count.ToString(CultureInfo.InvariantCulture)}d{s.Sides.ToString(CultureInfo.InvariantCulture)}";
        if (s.Modifier > 0) t += "+" + s.Modifier.ToString(CultureInfo.InvariantCulture);
        else if (s.Modifier < 0) t += "-" + (-(long)s.Modifier).ToString(CultureInfo.InvariantCulture);
        return t;
    }

    static bool TryInt(string s, out int v) => int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v);
}
