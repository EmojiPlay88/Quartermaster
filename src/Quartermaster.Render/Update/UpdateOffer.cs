namespace Quartermaster.Render.Update;

/// <summary>
/// The update offer's texts and rules (as in the WinForms <c>LauncherForm</c>, <c>AppUpdates</c>, <c>WhatsNewDialog</c> and <c>UpdateDialog</c>):
/// the bar, the automatic check, the manual check's answers, What's new and the download status.
/// </summary>
public static class UpdateOffer
{
    public const string NoNotes = "(This release has no notes.)";
    public const string Unpacking = "Checking and unpacking…";
    public const string Cancelling = "Cancelling…";
    public const string NotInstalledFormat = "The update was not installed; nothing was changed.\n\n{0}";

    public static readonly IReadOnlyList<string> BarActions = ["What's new", "Update now", "Later", "Skip this version"];

    public static string BarText(UpdateInfo i) => $"Quartermaster {AppVersion.Format(i.Version)} is available.";

    /// <summary>The automatic check runs once per run, when switched on and the last check is a day old (or never ran).</summary>
    public static bool AutoCheckDue(bool checkUpdatesOnStart, bool checkedThisRun, DateTime? lastUtc, DateTime nowUtc) =>
        checkUpdatesOnStart && !checkedThisRun && UpdateChecker.Due(lastUtc, nowUtc);

    /// <summary>The manual check's message for UpToDate and Failed; empty for the other outcomes.</summary>
    public static string ManualText(CheckResult r) => r.Outcome switch
    {
        CheckOutcome.UpToDate => $"Quartermaster {AppVersion.Text} is up to date.",
        CheckOutcome.Failed => "Could not check for updates.\n\n" + r.Error,
        _ => "",
    };

    public static string SkippedQuestion(UpdateInfo i) =>
        $"Quartermaster {AppVersion.Format(i.Version)} is available, but you chose to skip it. Offer it again?";

    public static string WhatsNewTitle(UpdateInfo i) => $"What's new in Quartermaster {AppVersion.Format(i.Version)}";

    /// <summary>"Downloading {zip} ({MB:0.0} MB)…" in the current culture, as WinForms.</summary>
    public static string DownloadStatus(string zipName, long bytes) => $"Downloading {zipName} ({bytes / 1024.0 / 1024.0:0.0} MB)…";
}
