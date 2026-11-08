using System.ComponentModel;
using System.Diagnostics;
using Quartermaster.Render;
using Quartermaster.Render.Update;

namespace Quartermaster.App;

/// <summary>
/// The updater's state for this run of the app and the Update now flow. Updates are only offered and applied from the launcher
/// (never while a game window is open, never without the user clicking Update now, never elevated).
/// </summary>
static class AppUpdates
{
    static readonly object Gate = new();
    static IUpdateFeed? _feed;

    /// <summary>GitHub through a non-redirecting client (every redirect hop is checked against the host allow-list), or the test folder feed.</summary>
    public static IUpdateFeed Feed
    {
        get
        {
            lock (Gate) return _feed ??= UpdateFeeds.Default(GitHubFeed.CreateClient());
        }
    }

    public static string UpdatesRoot => AppDataRoot.Sub("Updates");

    /// <summary>The folder the running app exe is in (the folder the helper swaps).</summary>
    public static string AppDir => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>The update the last check found this run (the launcher shows it again after Back to launcher).</summary>
    public static UpdateInfo? Offered { get; set; }

    /// <summary>Later was clicked: the bar stays hidden until the app starts again.</summary>
    public static bool Later { get; set; }

    /// <summary>The automatic check already ran this run (a failed one is not retried until the next start).</summary>
    public static bool CheckedThisRun { get; set; }

    /// <summary>Help → Check for updates… went back to the launcher: it checks as soon as it opens.</summary>
    public static bool CheckOnNextLauncher { get; set; }

    /// <summary>"Updated to 0.2.0." / "The update to 0.2.0 didn't start…": shown once by the first launcher.</summary>
    public static UpdateNotice? Notice { get; set; }

    static string? _updatedFrom, _updateFailed, _updateRefused;
    static bool _confirmed;

    /// <summary>--screenshot-update-bar &lt;png&gt;.</summary>
    public static string? ScreenshotBarPath { get; private set; }

    /// <summary>Takes the updater's command-line options (<c>--updated-from</c>, <c>--update-failed</c>, <c>--update-refused</c>, <c>--screenshot-update-bar</c>).</summary>
    public static void Arg(string name, string value)
    {
        switch (name)
        {
            case "--updated-from": _updatedFrom = value; break;
            case "--update-failed": _updateFailed = value; break;
            case "--update-refused": _updateRefused = value; break;
            case "--screenshot-update-bar": ScreenshotBarPath = value; break;
        }
    }

    /// <summary>
    /// Run at start, after the arguments: sets the notice the launcher shows ("Updated to …" / "The update … didn't start …"). The start
    /// marker the helper waits for is NOT written here but in <see cref="OnLauncherShown"/>, so a version that fails while starting up
    /// is still rolled back.
    /// </summary>
    public static void OnStart()
    {
        if (_updatedFrom != null)
        {
            Notice = UpdateStartup.Updated(AppVersion.Current);
            Program.AppLog($"update: started {AppVersion.Text} after updating from {_updatedFrom}");
        }
        else if (_updateRefused != null)
        {
            Notice = UpdateStartup.Refused(_updateRefused);
            Program.AppLog("update: " + Notice.Text);
        }
        else if (_updateFailed != null)
        {
            // The helper deletes its journal right after starting us when the rollback went fine: give it a moment.
            var journal = Path.Combine(UpdatesRoot, UpdateStartup.JournalName);
            for (var i = 0; i < 20 && File.Exists(journal); i++) Thread.Sleep(100);
            Notice = UpdateStartup.Failed(UpdatesRoot, _updateFailed, AppVersion.Current);
            Program.AppLog("update: " + Notice.Text);
        }
    }

    /// <summary>
    /// The launcher's Shown event: the first time after <c>--updated-from</c>, writes the start marker (the app really is up) and then
    /// removes older downloads and backups.
    /// </summary>
    public static void OnLauncherShown()
    {
        if (_updatedFrom == null || _confirmed) return;
        _confirmed = true;
        UpdateStartup.ConfirmStarted(UpdatesRoot, AppDir, AppVersion.Current, Program.AppLog);
        Program.AppLog($"update: start of {AppVersion.Text} confirmed");
    }

    /// <summary>True while another Quartermaster process runs from this exe (it may hold a game the swap would break).</summary>
    static bool OtherCopiesRunning() =>
        Environment.ProcessPath is { } exe && RunningCopies.OthersRunning(exe, Environment.ProcessId);

    /// <summary>True while a game window is open (the launcher never updates then).</summary>
    public static bool GameOpen => Application.OpenForms.OfType<MainForm>().Any(f => !f.IsDisposed);

    public static void OpenPage(IWin32Window? owner, Uri url)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Program.AppLog("update: could not open " + url + ": " + ex.Message);
            MessageBox.Show(owner, "Could not open the web browser. The page is:\n\n" + url.AbsoluteUri, "Quartermaster", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    /// <summary>
    /// Update now: refuses while a game is open; offers the release page when the app folder is not writable or the helper is missing; else
    /// downloads with progress, starts the helper and ends the app. Any problem leaves everything as it was and says so plainly.
    /// </summary>
    public static void UpdateNow(IWin32Window owner, UpdateInfo info)
    {
        if (GameOpen)
        {
            MessageBox.Show(owner, "Close the game and use the launcher to update.", "Quartermaster", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (OtherCopiesRunning())
        {
            MessageBox.Show(owner, RunningCopies.CloseOthersText, "Quartermaster", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var appDir = AppDir;
        if (!UpdateInstaller.AppFolderWritable(appDir))
        {
            OfferPage(owner, info, "Quartermaster can't update itself in this folder. Open the download page?");
            return;
        }
        if (!File.Exists(Path.Combine(appDir, UpdateInstaller.HelperExe)))
        {
            OfferPage(owner, info, $"Quartermaster can't update itself: {UpdateInstaller.HelperExe} is missing from its folder. Open the download page?");
            return;
        }
        if (!UpdateInstaller.CanSelfUpdate(appDir))
        {
            OfferPage(owner, info, UpdateInstaller.NoManifestText + " Open the download page?");
            return;
        }

        using var dlg = new UpdateDialog(Feed, info, UpdatesRoot);
        var r = dlg.ShowDialog(owner);
        if (r == DialogResult.Abort)
        {
            MessageBox.Show(owner, "The update was not installed; nothing was changed.\n\n" + dlg.Error, "Quartermaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (r != DialogResult.OK || dlg.Staged is not { } staged) return;
        if (GameOpen || OtherCopiesRunning()) // the download took a while: check again right before handing over
        {
            MessageBox.Show(owner, GameOpen ? "Close the game and use the launcher to update." : RunningCopies.CloseOthersText,
                "Quartermaster", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            UpdateInstaller.LaunchHelper(staged, appDir, Environment.ProcessId);
        }
        catch (Exception ex) when (ex is UpdateRefused or IOException or UnauthorizedAccessException or Win32Exception)
        {
            Program.AppLog("update: could not start the helper: " + ex.Message);
            MessageBox.Show(owner, "The update was not installed; nothing was changed.\n\n" + ex.Message, "Quartermaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Program.AppLog($"update: helper started for {AppVersion.Format(info.Version)}; closing so it can apply the update");
        Application.Exit();
    }

    static void OfferPage(IWin32Window owner, UpdateInfo info, string question)
    {
        if (MessageBox.Show(owner, question, "Quartermaster", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            OpenPage(owner, info.PageUrl);
    }
}
