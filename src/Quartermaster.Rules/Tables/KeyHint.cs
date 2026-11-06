using System.Globalization;

namespace Quartermaster.Rules.Tables;

/// <summary>Plain-language reading of a table key, shown as a tooltip while editing.</summary>
public static class KeyHint
{
    public static string Describe(TableKey k)
    {
        if (k.Kind == TableKeyKind.Text && k.Text.Length == 0) return "(empty key)";
        return k.Kind switch
        {
            TableKeyKind.Number => N(k.Lo),
            TableKeyKind.Range => $"{N(k.Lo)} to {N(k.Hi)}",
            TableKeyKind.AtLeast => $"at least {N(k.Lo)}",
            TableKeyKind.AtMost => $"at most {N(k.Hi)}",
            _ => $"text \"{k.Text}\"",
        };
    }

    private static string N(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
}
