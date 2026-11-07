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
