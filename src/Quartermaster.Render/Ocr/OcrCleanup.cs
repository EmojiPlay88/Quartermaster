using System.Text.RegularExpressions;

namespace Quartermaster.Render.Ocr;

/// <summary>Fixes common OCR digit mistakes, only in cells that look numeric.</summary>
public static class OcrCleanup
{
    private const string D = @"[\dOolIS]";
    private static readonly Regex Plain = new(@"^[+\-–—]?[\dOolIS.,]+%?$", RegexOptions.Compiled);
    private static readonly Regex Range = new(@"^[\dOolIS.,]+[\-–—][\dOolIS.,]+$", RegexOptions.Compiled);
    private static readonly Regex Dice = new($@"^{D}*[dD]{D}+([+\-–—]{D}+)?$", RegexOptions.Compiled);

    private static string Strip(string s) =>
        new string(s.Where(ch => ch is not (' ' or ' ' or ' ' or ' ' or ' ' or ' ')).ToArray()).Trim();

    public static bool IsNumericLooking(string cell)
    {
        if (string.IsNullOrEmpty(cell)) return false;
        var t = Strip(cell);
        if (t.Length == 0 || !t.Any(char.IsAsciiDigit)) return false;
        return Plain.IsMatch(t) || Range.IsMatch(t) || Dice.IsMatch(t);
    }

    /// <summary>
    /// Whether a second-pass reading of an empty cell is believable: 1–3 characters (spaces ignored) that look numeric,
    /// or a single letter A–Z.
    /// </summary>
    public static bool AcceptsCellReread(string? text)
    {
        var t = new string((text ?? "").Where(ch => !char.IsWhiteSpace(ch)).ToArray());
        if (t.Length is < 1 or > 3) return false;
        return IsNumericLooking(t) || (t.Length == 1 && t[0] is >= 'A' and <= 'Z');
    }

    public static string Fix(string cell)
    {
        if (!IsNumericLooking(cell)) return cell;
        var t = Strip(cell);
        var chars = t.Select(ch => ch switch
        {
            'O' or 'o' => '0',
            'l' or 'I' => '1',
            'S' => '5',
            '–' or '—' => '-',
            _ => ch,
        }).ToArray();
        return new string(chars);
    }
}
