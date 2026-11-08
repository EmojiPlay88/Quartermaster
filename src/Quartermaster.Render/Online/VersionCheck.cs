namespace Quartermaster.Render;

/// <summary>Gentle app-version mismatch warnings (never a block; the network protocol version is checked separately).</summary>
public static class VersionCheck
{
    /// <summary>Null when the versions match or either is unknown; else a one-line warning.</summary>
    public static string? VersionWarning(string? mine, string? theirs, string who) =>
        Differs(mine, theirs) ? $"{who} runs {theirs}, you run {mine} — update first if anything misbehaves." : null;

    /// <summary>Same, worded for a turn file.</summary>
    public static string? TurnFileVersionWarning(string? mine, string? theirs) =>
        Differs(mine, theirs) ? $"This turn file was made with {theirs}, you run {mine} — update first if anything misbehaves." : null;

    static bool Differs(string? mine, string? theirs) =>
        !string.IsNullOrEmpty(mine) && !string.IsNullOrEmpty(theirs) && !string.Equals(mine, theirs, StringComparison.Ordinal);
}
