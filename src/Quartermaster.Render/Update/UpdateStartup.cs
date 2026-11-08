namespace Quartermaster.Render.Update;

/// <summary>A line the launcher shows once after an update ran, with an optional link (the release page).</summary>
public sealed record UpdateNotice(string Text, Uri? Link = null, string? LinkText = null);

/// <summary>What the app does at start when the updater helper launched it (<c>--updated-from</c> / <c>--update-failed</c>).</summary>
public static class UpdateStartup
{
    /// <summary>The helper's swap journal; it stays in the updates folder when a rollback failed.</summary>
    public const string JournalName = "swap-journal.json";

    /// <summary><c>--updated-from &lt;old&gt;</c>: the line the launcher shows, "Updated to {current}."</summary>
    public static UpdateNotice Updated(Version current) => new($"Updated to {AppVersion.Format(current)}.");

    /// <summary>
    /// Once the launcher is shown after <c>--updated-from</c>: writes the marker the helper waits for (so it does not roll back), then
    /// removes older downloads, markers and backups (<see cref="UpdateInstaller.CleanupAfterUpdate"/>). Never throws; a marker that cannot be
    /// written is reported through <paramref name="log"/>.
    /// </summary>
    public static void ConfirmStarted(string updatesRoot, string? appDir, Version current, Action<string>? log = null)
    {
        try
        {
            Directory.CreateDirectory(updatesRoot);
            File.WriteAllText(UpdateInstaller.MarkerPath(updatesRoot, current), DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("update: could not write the start marker: " + ex.Message);
        }
        UpdateInstaller.CleanupAfterUpdate(updatesRoot, appDir, current);
    }

    /// <summary><c>--update-refused &lt;version&gt;</c>: the helper refused before touching anything (another copy running, or no file list).</summary>
    public static UpdateNotice Refused(string attempted) =>
        new($"The update to {attempted} was not installed; nothing was changed. Close every other Quartermaster window and try again, or download it from the release page.",
            ReleasePage(attempted), "Release page");

    /// <summary>
    /// <c>--update-failed &lt;version&gt;</c>: the helper went back to the old version. When its journal is still there (the rollback itself
    /// failed), some files may be broken, so the notice also points at the release page.
    /// </summary>
    public static UpdateNotice Failed(string updatesRoot, string attempted, Version current)
    {
        var text = $"The update to {attempted} didn't start, so Quartermaster went back to {AppVersion.Format(current)}.";
        if (!File.Exists(Path.Combine(updatesRoot, JournalName))) return new UpdateNotice(text);
        return new UpdateNotice(text + " Some files may need a fresh download from the release page.", ReleasePage(attempted), "Release page");
    }

    /// <summary>The GitHub page of the release <c>quartermaster-v&lt;version&gt;</c>, or the releases list when the text is not a version.</summary>
    public static Uri ReleasePage(string version) =>
        Version.TryParse(version, out var v) && v.Build >= 0 && v.Revision < 0
            ? new Uri($"https://github.com/{GitHubFeed.Repo}/releases/tag/quartermaster-v{AppVersion.Format(v)}")
            : new Uri($"https://github.com/{GitHubFeed.Repo}/releases");
}
