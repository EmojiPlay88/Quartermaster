using System.Text.Json;

namespace Quartermaster.Updater;

/// <summary>
/// None: no journal. Completed: this update is installed. RolledBack: the old version was restored for this update.
/// Stale: a journal from some other update was brought to a consistent folder and discarded; the requested update has not been applied yet.
/// </summary>
public enum ResumeOutcome { None, RolledBack, Completed, Stale }

/// <summary>
/// Journalled swap of an app folder's contents for a staged folder's contents.
/// Journal steps: "backup" (moving old files out), "install" (moving staged files in), "done",
/// "rollback-clear" (removing the new files), "rollback-restore" (moving old files back), "rolledback".
/// The app folder itself is never moved, only its contents. The journal records the paths it was
/// written with; Resume and Rollback always use those, not the constructor's.
/// Cross-volume moves copy to "&lt;name&gt;.qm-partial", rename, then delete the source,
/// so a crash never leaves a half-copied entry that looks complete.
/// </summary>
public sealed class Swapper(string appDir, string stagedDir, string backupDir, string journalPath)
{
    const string PartialSuffix = ".qm-partial";

    public TimeSpan RetryWindow { get; set; } = TimeSpan.FromSeconds(10);
    public string? Version { get; set; }

    sealed record Journal(string Step, string AppDir, string BackupDir, string? StagedDir = null, string? Version = null);

    public void Apply()
    {
        if (Directory.Exists(backupDir)) Retry(() => Directory.Delete(backupDir, true));
        Directory.CreateDirectory(backupDir);
        var j = new Journal("backup", appDir, backupDir, stagedDir, Version);
        Write(j);
        foreach (var e in Entries(appDir)) MoveEntry(e, Path.Combine(backupDir, Path.GetFileName(e)), overwrite: false);
        Write(j = j with { Step = "install" });
        InstallStaged(appDir, stagedDir);
        Write(j with { Step = "done" });
    }

    /// <summary>Restores the old version. Does nothing without a journal. Never clears the app folder unless a non-empty backup exists.</summary>
    public void Rollback()
    {
        var j = ReadJournal();
        if (j == null) return;
        switch (j.Step)
        {
            case "rolledback": return;
            case "backup": // new files never reached the app folder; old files are authoritative
            case "rollback-restore":
                break;
            default: // install, done, rollback-clear (and unknown): the app folder holds new files
                if (!HasEntries(j.BackupDir))
                    throw new InvalidOperationException($"No backup found at '{j.BackupDir}'; refusing to clear '{j.AppDir}'.");
                Write(j = j with { Step = "rollback-clear" });
                Directory.CreateDirectory(j.AppDir);
                foreach (var e in Entries(j.AppDir)) Retry(() => Delete(e));
                Write(j = j with { Step = "rollback-restore" });
                break;
        }
        RestoreBackup(j.AppDir, j.BackupDir);
        Write(j with { Step = "rolledback" });
    }

    public ResumeOutcome Resume()
    {
        var j = ReadJournal();
        if (j == null) return ResumeOutcome.None;
        var mine = Equals(j.Version, Version)
                   && (j.StagedDir == null || string.Equals(Path.GetFullPath(j.StagedDir), Path.GetFullPath(stagedDir), StringComparison.OrdinalIgnoreCase));
        switch (j.Step)
        {
            case "install" when mine:
                InstallStaged(j.AppDir, stagedDir);
                Write(j with { Step = "done" });
                return ResumeOutcome.Completed;
            case "done" when mine:
                return ResumeOutcome.Completed;
            case "done": // some other finished update: its result is the current app; just forget it
            case "rolledback":
                break;
            default: // backup, install (other update), rollback-*: bring the folder back to the old version
                Rollback();
                break;
        }
        if (mine) return ResumeOutcome.RolledBack;
        DeleteJournal();
        return ResumeOutcome.Stale;
    }

    public void DeleteJournal() { try { File.Delete(journalPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    void InstallStaged(string app, string staged)
    {
        Directory.CreateDirectory(app);
        foreach (var e in Entries(app).Where(IsPartial).ToList()) Retry(() => Delete(e));
        foreach (var e in Entries(staged))
        {
            var dest = Path.Combine(app, Path.GetFileName(e));
            if (Exists(dest)) Retry(() => Delete(e)); // completed earlier; source is leftover
            else MoveEntry(e, dest, overwrite: false);
        }
    }

    void RestoreBackup(string app, string backup)
    {
        Directory.CreateDirectory(app);
        if (Directory.Exists(backup))
            foreach (var e in Entries(backup))
            {
                if (IsPartial(e)) { Retry(() => Delete(e)); continue; }
                MoveEntry(e, Path.Combine(app, Path.GetFileName(e)), overwrite: true);
            }
        foreach (var e in Entries(app).Where(IsPartial).ToList()) Retry(() => Delete(e));
    }

    static bool HasEntries(string dir) => Directory.Exists(dir) && Entries(dir).Any(e => !IsPartial(e));
    static bool IsPartial(string p) => p.EndsWith(PartialSuffix, StringComparison.Ordinal);
    static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);
    static List<string> Entries(string dir) => Directory.Exists(dir) ? Directory.GetFileSystemEntries(dir).ToList() : [];
    static void Delete(string p) { if (Directory.Exists(p)) Directory.Delete(p, true); else if (File.Exists(p)) File.Delete(p); }

    void MoveEntry(string src, string dest, bool overwrite)
    {
        if (overwrite && Exists(dest)) Retry(() => Delete(dest));
        var sameVolume = string.Equals(Path.GetPathRoot(Path.GetFullPath(src)), Path.GetPathRoot(Path.GetFullPath(dest)), StringComparison.OrdinalIgnoreCase);
        if (sameVolume)
        {
            Retry(() => { if (Directory.Exists(src)) Directory.Move(src, dest); else File.Move(src, dest); });
            return;
        }
        var partial = dest + PartialSuffix;
        Retry(() => { Delete(partial); CopyEntry(src, partial); });
        Retry(() => { if (Directory.Exists(partial)) Directory.Move(partial, dest); else File.Move(partial, dest); });
        Retry(() => Delete(src));
    }

    static void CopyEntry(string src, string dest)
    {
        if (File.Exists(src)) { File.Copy(src, dest, true); return; }
        Directory.CreateDirectory(dest);
        foreach (var e in Directory.GetFileSystemEntries(src)) CopyEntry(e, Path.Combine(dest, Path.GetFileName(e)));
    }

    void Retry(Action action)
    {
        var deadline = DateTime.UtcNow + RetryWindow;
        while (true)
        {
            try { action(); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline) throw;
                Thread.Sleep(250);
            }
        }
    }

    void Write(Journal j)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(journalPath))!);
        var tmp = journalPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(j, JsonOpts));
        File.Move(tmp, journalPath, overwrite: true);
    }

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    Journal? ReadJournal()
    {
        if (!File.Exists(journalPath)) return null;
        try { return JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath), JsonOpts); }
        catch (JsonException) { return new Journal("backup", appDir, backupDir, stagedDir, Version); } // unreadable: earliest phase (restore, never clear)
    }
}

public static class Startup
{
    public static bool WaitForMarker(string markerPath, Func<bool> processAlive, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (File.Exists(markerPath)) return true;
            if (!processAlive()) return File.Exists(markerPath);
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(100);
        }
    }
}
