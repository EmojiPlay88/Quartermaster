using System.Text.RegularExpressions;

namespace Quartermaster.Render;

/// <summary>The "what now" banner text for the current phase view.</summary>
public static class Banner
{
    private const int MaxSentence = 120;
    private static readonly Regex Sentence = new(@"^.*?[.!?](?=\s|$)", RegexOptions.Singleline | RegexOptions.Compiled);

    public static (string Text, string? Tooltip) For(PhaseView v)
    {
        if (v.NoPhases) return ("No phases defined — free play.", null);
        if (v.BeforeStart) return ("Press Start game ▶ to begin turn 1.", null);
        if (v.PlayingText is { Length: > 0 } playing) return ("Now: " + playing, null);   // hot-seat: another side's phase

        var phase = v.Current?.Name is { Length: > 0 } n ? n : v.StageText ?? "Phase";
        var instructions = v.Current?.Instructions?.Trim() ?? "";
        var text = v.SideName is { Length: > 0 } side ? $"Now: {side} player — {phase}" : $"Now: {phase}";
        var first = FirstSentence(instructions);
        if (first.Length > 0) text += ": " + first;
        var left = v.Decisions?.Count ?? 0;
        if (left > 0) text += $" · {left} decisions left";
        return (text, instructions.Length > 0 ? instructions : null);
    }

    private static string FirstSentence(string instructions)
    {
        var flat = Regex.Replace(instructions, @"\s+", " ").Trim();
        if (flat.Length == 0) return "";
        var m = Sentence.Match(flat);
        var s = m.Success ? m.Value : flat;
        return s.Length > MaxSentence ? s[..(MaxSentence - 1)] + "…" : s;
    }
}
