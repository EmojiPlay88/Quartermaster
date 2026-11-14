using System.Text.Json;

namespace Quartermaster.Updater;

/// <summary>
/// None: no journal. Completed: this update is installed. RolledBack: the old version was restored for this update.
/// Stale: a journal from some other update (or a finished rollback) was brought to a consistent folder and discarded; the requested
/// update has not been applied yet.
/// </summary>
public enum ResumeOutcome { None, RolledBack, Completed, Stale }

/// <summary>The app folder or the staged folder has no usable <see cref="Manifest.FileName"/>; nothing was touched.</summary>
public sealed class ManifestMissingException(string message) : Exception(message);

/// <summary>
/// <c>quartermaster-files.txt</c>: the release's own file list (one relative path per line, the list itself included), written at publish
/// time. The helper only ever moves, deletes or restores files named in it, so anything else in the app folder is left alone.
/// </summary>
public static class Manifest
{
    public const string FileName = "quartermaster-files.txt";

    /// <summary>The listed relative paths (OS separators, case-insensitively distinct, the list itself included); null when the list is missing, unreadable or names an unsafe path.</summary>
    public static List<string>? Read(string dir)
    {
        var path = Path.Combine(dir, FileName);
        string[] lines;
        try
        {
            if (!File.Exists(path)) return null;
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (Normalize(line) is not { } rel) return null;
            if (seen.Add(rel)) list.Add(rel);
        }
        if (seen.Add(FileName)) list.Add(FileName);
        return list;
    }

    /// <summary>A safe relative path with OS separators, or null (rooted, drive, stream, <c>..</c> or empty segments).</summary>
    public static string? Normalize(string line)
    {
        if (line.Contains(':') || line.Contains('\0') || line[0] is '/' or '\\' || Path.IsPathRooted(line)) return null;
        var parts = line.Split('/', '\\');
        if (parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.Trim() != p)) return null;
        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    /// <summary>Every folder that is a path prefix of a listed file ("listed subfolders"), deepest first.</summary>
    public static List<string> Folders(IEnumerable<string> files)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var d = Path.GetDirectoryName(f);
            while (!string.IsNullOrEmpty(d)) { set.Add(d); d = Path.GetDirectoryName(d); }
        }
        return set.OrderByDescending(d => d.Count(c => c == Path.DirectorySeparatorChar)).ThenBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>
/// Journalled swap of the release files in an app folder for a staged folder's files. Only paths listed in the app folder's
/// <see cref="Manifest"/> (the old release) and in the staged folder's (the new release) are moved, deleted or restored; any other
/// file in the app folder survives Apply, Rollback and Resume. Emptied folders are removed only when they are listed subfolders.
/// Journal steps: "backup" (moving old files out), "install" (moving staged files in), "done",
/// "rollback-clear" (removing the new files), "rollback-restore" (moving old files back), "rolledback".
/// The app folder itself is never moved. The journal records the paths and file lists it was written with; Resume and Rollback
/// always use those, not the constructor's. Cross-volume moves copy to "&lt;name&gt;.qm-partial", rename, then delete the source,
/// so a crash never leaves a half-copied file that looks complete.
/// </summary>
public sealed class Swapper(string appDir, string stagedDir, string backupDir, string journalPath) : ISwap
{
    const string PartialSuffix = ".qm-partial";

    public TimeSpan RetryWindow { get; set; } = TimeSpan.FromSeconds(10);
    public string? Version { get; set; }

    sealed record Journal(string Step, string AppDir, string BackupDir, string? StagedDir = null, string? Version = null,
        List<string>? OldFiles = null, List<string>? NewFiles = null);

    /// <summary>Throws <see cref="ManifestMissingException"/> before touching anything when either folder has no usable file list or the staged list names a missing file.</summary>
    public void Apply()
    {
        var oldFiles = Manifest.Read(appDir) ?? throw new ManifestMissingException($"'{appDir}' has no {Manifest.FileName}; this copy can't update itself.");
        var newFiles = Manifest.Read(stagedDir) ?? throw new ManifestMissingException($"The staged update '{stagedDir}' has no usable {Manifest.FileName}.");
        var missing = newFiles.FirstOrDefault(f => !File.Exists(Path.Combine(stagedDir, f)));
        if (missing != null) throw new ManifestMissingException($"The staged update lists '{missing}' but does not contain it.");

        if (Directory.Exists(backupDir)) Retry(() => Directory.Delete(backupDir, true));
        Directory.CreateDirectory(backupDir);
        var j = new Journal("backup", appDir, backupDir, stagedDir, Version, oldFiles, newFiles);
        Write(j);
        // A new release file whose name an unlisted file already has goes to the backup too, so a rollback puts it back.
        var moving = Union(oldFiles, newFiles);
        foreach (var rel in moving)
        {
            var src = Path.Combine(appDir, rel);
            if (File.Exists(src)) MoveFile(src, Path.Combine(backupDir, rel), overwrite: false);
        }
        RemoveEmptyFolders(appDir, moving);
        Write(j = j with { Step = "install" });
        InstallStaged(appDir, stagedDir, newFiles);
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
                if (!HasFiles(j.BackupDir))
                    throw new InvalidOperationException($"No backup found at '{j.BackupDir}'; refusing to clear '{j.AppDir}'.");
                var newFiles = j.NewFiles ?? Manifest.Read(j.AppDir) ?? (j.StagedDir != null ? Manifest.Read(j.StagedDir) : null)
                    ?? throw new InvalidOperationException($"The journal does not say which files are new; refusing to clear '{j.AppDir}'.");
                Write(j = j with { Step = "rollback-clear", NewFiles = newFiles });
                Directory.CreateDirectory(j.AppDir);
                foreach (var rel in newFiles)
                {
                    var p = Path.Combine(j.AppDir, rel);
                    Retry(() => { DeleteFile(p); DeleteFile(p + PartialSuffix); });
                }
                RemoveEmptyFolders(j.AppDir, newFiles);
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
                InstallStaged(j.AppDir, stagedDir, j.NewFiles);
                Write(j with { Step = "done" });
                return ResumeOutcome.Completed;
            case "done" when mine:
                return ResumeOutcome.Completed;
            case "done": // some other finished update: its result is the current app; just forget it
            case "rolledback": // a finished rollback (its journal could not be deleted): the old app is in place, so try again
                DeleteJournal();
                return ResumeOutcome.Stale;
            default: // backup, install (other update), rollback-*: bring the folder back to the old version
                Rollback();
                break;
        }
        if (mine) return ResumeOutcome.RolledBack;
        DeleteJournal();
        return ResumeOutcome.Stale;
    }

    public void DeleteJournal() { try { File.Delete(journalPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    /// <summary>Moves the listed staged files in (all staged files when the journal predates file lists).</summary>
    void InstallStaged(string app, string staged, List<string>? files)
    {
        Directory.CreateDirectory(app);
        files ??= Directory.Exists(staged)
            ? Directory.GetFiles(staged, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(staged, f)).Where(f => !IsPartial(f)).ToList()
            : [];
        foreach (var rel in files)
        {
            var src = Path.Combine(staged, rel);
            var dest = Path.Combine(app, rel);
            Retry(() => DeleteFile(dest + PartialSuffix));
            if (File.Exists(dest)) { if (File.Exists(src)) Retry(() => DeleteFile(src)); } // completed earlier; source is leftover
            else if (File.Exists(src)) MoveFile(src, dest, overwrite: false);
            else throw new IOException($"'{rel}' is neither staged nor installed.");
        }
    }

    /// <summary>Moves every file in the backup (only release files are ever put there) back to the same place in the app folder.</summary>
    void RestoreBackup(string app, string backup)
    {
        Directory.CreateDirectory(app);
        if (!Directory.Exists(backup)) return;
        foreach (var f in Directory.GetFiles(backup, "*", SearchOption.AllDirectories))
        {
            if (IsPartial(f)) { Retry(() => DeleteFile(f)); continue; }
            var dest = Path.Combine(app, Path.GetRelativePath(backup, f));
            Retry(() => DeleteFile(dest + PartialSuffix));
            MoveFile(f, dest, overwrite: true);
        }
    }

    /// <summary>Removes listed subfolders of <paramref name="files"/> that are now empty (deepest first); never any other folder.</summary>
    void RemoveEmptyFolders(string root, IEnumerable<string> files)
    {
        foreach (var d in Manifest.Folders(files))
        {
            var p = Path.Combine(root, d);
            try { if (Directory.Exists(p) && !Directory.EnumerateFileSystemEntries(p).Any()) Directory.Delete(p); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left in place; harmless */ }
        }
    }

    static List<string> Union(List<string> a, List<string> b)
    {
        var seen = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        return a.Concat(b.Where(seen.Add)).ToList();
    }

    static bool HasFiles(string dir) => Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(e => !IsPartial(e));
    static bool IsPartial(string p) => p.EndsWith(PartialSuffix, StringComparison.Ordinal);
    static void DeleteFile(string p) { if (File.Exists(p)) { File.SetAttributes(p, FileAttributes.Normal); File.Delete(p); } }

    void MoveFile(string src, string dest, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (overwrite && File.Exists(dest)) Retry(() => DeleteFile(dest));
        var sameVolume = string.Equals(Path.GetPathRoot(Path.GetFullPath(src)), Path.GetPathRoot(Path.GetFullPath(dest)), StringComparison.OrdinalIgnoreCase);
        if (sameVolume)
        {
            Retry(() => File.Move(src, dest));
            return;
        }
        var partial = dest + PartialSuffix;
        Retry(() => { DeleteFile(partial); File.Copy(src, partial, true); });
        Retry(() => File.Move(partial, dest));
        Retry(() => DeleteFile(src));
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
