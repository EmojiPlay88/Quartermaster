using System.Globalization;

namespace Quartermaster.Render;

/// <summary>The Play offline screen's texts and its launch request (as in the WinForms <c>PlayOfflineForm</c>).</summary>
public static class PlayOfflineModel
{
    public const string SaveMissingText = "That saved game's file was not found.";

    /// <summary>"Continue: (no autosave yet)" or "Continue: autosave {when} (turn N, Phase)".</summary>
    public static string ContinueText(ContinueInfo? c)
    {
        if (c == null) return "Continue: (no autosave yet)";
        var when = c.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        var detail = c.Turn > 0 ? $"turn {c.Turn}" : "";
        if (!string.IsNullOrEmpty(c.Phase)) detail = detail.Length > 0 ? detail + ", " + c.Phase : c.Phase;
        return detail.Length > 0 ? $"Continue: autosave {when} ({detail})" : $"Continue: autosave {when}";
    }

    /// <summary>"Play offline – {title or file name}".</summary>
    public static string Title(ModuleRow r) => "Play offline – " + LauncherModel.TitleText(r);

    /// <summary>The launch request; an empty rules path becomes none.</summary>
    public static LaunchRequest Finish(ModuleRow r, string? rulesPath, LaunchKind kind, NewGameRequest? ng, string? savePath) =>
        new(r.Entry.Path, string.IsNullOrEmpty(rulesPath) ? null : rulesPath, kind, ng, savePath);
}
