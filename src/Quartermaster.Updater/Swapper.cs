using System.Text.Json;

namespace Quartermaster.Updater;

public enum ResumeOutcome { None, RolledBack, Completed }

/// <summary>
/// Journalled swap of an app folder's contents for a staged folder's contents.
/// Journal steps: "backup" (moving old files out), "install" (moving staged files in),
/// "done", "rolledback". The app folder itself is never moved, only its contents.
/// Cross-volume moves copy to "&lt;name&gt;.qm-partial", rename, then delete the source,
/// so a crash never leaves a half-copied entry that looks complete.
/// </summary>
public sealed class Swapper(string appDir, string stagedDir, string backupDir, string journalPath)
{
    const string PartialSuffix = ".qm-partial";

    public TimeSpan RetryWindow { get; set; } = TimeSpan.FromSeconds(10);

    sealed record Journal(string Step, string AppDir, string BackupDir);

    public void Apply()
    {
        if (Directory.Exists(backupDir)) Retry(() => Directory.Delete(backupDir, true));
        Directory.CreateDirectory(backupDir);
        WriteJournal("backup");
        foreach (var e in Entries(appDir)) MoveEntry(e, Path.Combine(backupDir, Path.GetFileName(e)), overwrite: false);
        WriteJournal("install");
        InstallStaged();
        WriteJournal("done");
    }

    public void Rollback()
    {
        var step = ReadJournal()?.Step;
        RestoreBackup(clearApp: step != "backup");
        WriteJournal("rolledback");
    }

    public ResumeOutcome Resume()
    {
        var step = ReadJournal()?.Step;
        switch (step)
        {
            case null: return ResumeOutcome.None;
            case "install":
                InstallStaged();
                WriteJournal("done");
                return ResumeOutcome.Completed;
            case "done": return ResumeOutcome.Completed;
            case "rolledback": return ResumeOutcome.RolledBack;
            default: // "backup" or anything unknown: old files are authoritative
                RestoreBackup(clearApp: false);
                WriteJournal("rolledback");
                return ResumeOutcome.RolledBack;
        }
    }

    public void DeleteJournal() { try { File.Delete(journalPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    void InstallStaged()
    {
        Directory.CreateDirectory(appDir);
        foreach (var e in Entries(appDir).Where(IsPartial).ToList()) Retry(() => Delete(e));
        foreach (var e in Entries(stagedDir))
        {
            var dest = Path.Combine(appDir, Path.GetFileName(e));
            if (Exists(dest)) Retry(() => Delete(e)); // completed earlier; source is leftover
            else MoveEntry(e, dest, overwrite: false);
        }
    }

    void RestoreBackup(bool clearApp)
    {
        Directory.CreateDirectory(appDir);
        if (clearApp) foreach (var e in Entries(appDir)) Retry(() => Delete(e));
        if (!Directory.Exists(backupDir)) return;
        foreach (var e in Entries(backupDir))
        {
            if (IsPartial(e)) { Retry(() => Delete(e)); continue; }
            MoveEntry(e, Path.Combine(appDir, Path.GetFileName(e)), overwrite: true);
        }
        foreach (var e in Entries(appDir).Where(IsPartial).ToList()) Retry(() => Delete(e));
    }

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

    void WriteJournal(string step)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(journalPath))!);
        var tmp = journalPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Journal(step, appDir, backupDir), JsonOpts));
        File.Move(tmp, journalPath, overwrite: true);
    }

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    Journal? ReadJournal()
    {
        if (!File.Exists(journalPath)) return null;
        try { return JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath), JsonOpts); }
        catch (JsonException) { return new Journal("backup", appDir, backupDir); } // unreadable: treat as earliest phase (restore)
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
