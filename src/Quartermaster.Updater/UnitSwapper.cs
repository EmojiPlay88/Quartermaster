using System.Text.Json;

namespace Quartermaster.Updater;

/// <summary>The staged unit (or the unit being replaced) is missing; nothing was touched.</summary>
public sealed class UnitMissingException(string message) : Exception(message);

/// <summary>The journalled swap both <see cref="Swapper"/> and <see cref="UnitSwapper"/> offer to <see cref="UpdateRunner"/>.</summary>
public interface ISwap
{
    void Apply();
    void Rollback();
    ResumeOutcome Resume();
    void DeleteJournal();
}

/// <summary>
/// Journalled swap of a whole unit: a macOS <c>.app</c> bundle (a directory) or a Linux AppImage (a file) is replaced as one piece.
/// The old unit is moved to <paramref name="backupPath"/> (see <see cref="BackupPathFor"/>), then the staged unit is moved in.
/// Journal steps: "backup" (moving the unit out), "install" (moving the staged unit in), "done",
/// "rollback-clear" (removing the new unit), "rollback-restore" (moving the backup back), "rolledback".
/// The journal records the paths it was written with; Resume and Rollback always use those. The unit is never deleted unless
/// the backup exists. Cross-volume moves copy to "&lt;target&gt;.qm-partial", rename it to the target, then delete the source,
/// so a crash never leaves a half-copied unit under the real name. <see cref="ResumeOutcome"/> means the same as for <see cref="Swapper"/>.
/// </summary>
public sealed class UnitSwapper(string unitPath, string stagedPath, string backupPath, string journalPath) : ISwap
{
    const string PartialSuffix = ".qm-partial";

    public TimeSpan RetryWindow { get; set; } = TimeSpan.FromSeconds(10);
    public string? Version { get; set; }

    /// <summary>Test seam: whether a rename can move <c>src</c> to <c>dest</c> (default: same path root, as <see cref="Swapper"/>).</summary>
    internal Func<string, string, bool> SameVolume { get; set; } = (src, dest) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(src)), Path.GetPathRoot(Path.GetFullPath(dest)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Test seam: called with the partial path once a cross-volume copy is complete, before it is renamed.</summary>
    internal Action<string>? OnCopied { get; set; }

    /// <summary><c>&lt;parent&gt;/.&lt;unitName&gt;.qm-backup-&lt;old&gt;</c>: hidden, beside the unit, and never ending in <c>.app</c>.</summary>
    public static string BackupPathFor(string unitPath, string oldVersion)
    {
        var full = Path.GetFullPath(unitPath).TrimEnd('\\', '/');
        return Path.Combine(Path.GetDirectoryName(full)!, "." + Path.GetFileName(full) + ".qm-backup-" + oldVersion);
    }

    sealed record Journal(string Step, string Unit, string Backup, string? Staged = null, string? Version = null);

    /// <summary>Throws <see cref="UnitMissingException"/> before touching anything when the staged unit or the unit is missing.</summary>
    public void Apply()
    {
        if (!Exists(stagedPath)) throw new UnitMissingException($"The staged update '{stagedPath}' is missing.");
        if (!Exists(unitPath)) throw new UnitMissingException($"'{unitPath}' is missing; this copy can't update itself.");

        Retry(() => { DeletePath(backupPath); DeletePath(backupPath + PartialSuffix); });
        var j = new Journal("backup", unitPath, backupPath, stagedPath, Version);
        Write(j);
        Move(unitPath, backupPath);
        Write(j = j with { Step = "install" });
        Install(unitPath, stagedPath);
        Write(j with { Step = "done" });
    }

    /// <summary>Restores the old unit. Does nothing without a journal. Never deletes the unit unless the backup exists.</summary>
    public void Rollback()
    {
        var j = ReadJournal();
        if (j == null) return;
        switch (j.Step)
        {
            case "rolledback": return;
            case "backup": // the staged unit never moved in
                Retry(() => DeletePath(j.Backup + PartialSuffix));
                if (Exists(j.Backup))
                {
                    // The backup is complete (renames are atomic); the unit is gone or a half-deleted cross-volume source.
                    Retry(() => { DeletePath(j.Unit); DeletePath(j.Unit + PartialSuffix); });
                    Move(j.Backup, j.Unit);
                }
                break;
            case "rollback-restore":
                Restore(j);
                break;
            default: // install, done, rollback-clear (and unknown): the unit may be the new one
                if (!Exists(j.Backup))
                    throw new InvalidOperationException($"No backup found at '{j.Backup}'; refusing to remove '{j.Unit}'.");
                Write(j = j with { Step = "rollback-clear" });
                Retry(() => { DeletePath(j.Unit); DeletePath(j.Unit + PartialSuffix); });
                Write(j = j with { Step = "rollback-restore" });
                Restore(j);
                break;
        }
        Write(j with { Step = "rolledback" });
    }

    public ResumeOutcome Resume()
    {
        var j = ReadJournal();
        if (j == null) return ResumeOutcome.None;
        var mine = Equals(j.Version, Version)
                   && (j.Staged == null || string.Equals(Path.GetFullPath(j.Staged), Path.GetFullPath(stagedPath), StringComparison.OrdinalIgnoreCase));
        switch (j.Step)
        {
            case "install" when mine:
                Install(j.Unit, stagedPath);
                Write(j with { Step = "done" });
                return ResumeOutcome.Completed;
            case "done" when mine:
                return ResumeOutcome.Completed;
            case "done": // some other finished update: its result is the current unit; just forget it
            case "rolledback": // a finished rollback (its journal could not be deleted): the old unit is in place, so try again
                DeleteJournal();
                return ResumeOutcome.Stale;
            default: // backup, install (other update), rollback-*: bring the unit back to the old version
                Rollback();
                break;
        }
        if (mine) return ResumeOutcome.RolledBack;
        DeleteJournal();
        return ResumeOutcome.Stale;
    }

    public void DeleteJournal() { try { File.Delete(journalPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    /// <summary>Moves the staged unit in; a unit already in place means an earlier move finished, so only staged leftovers go.</summary>
    void Install(string unit, string staged)
    {
        Retry(() => DeletePath(unit + PartialSuffix));
        if (Exists(unit)) { Retry(() => DeletePath(staged)); return; }
        if (!Exists(staged)) throw new IOException($"'{unit}' is neither staged nor installed.");
        Move(staged, unit);
    }

    /// <summary>Moves the backup back after the unit was cleared; a unit already in place means the restore finished.</summary>
    void Restore(Journal j)
    {
        Retry(() => DeletePath(j.Unit + PartialSuffix));
        if (Exists(j.Unit)) { Retry(() => { DeletePath(j.Backup); DeletePath(j.Backup + PartialSuffix); }); return; }
        if (!Exists(j.Backup)) throw new InvalidOperationException($"No backup found at '{j.Backup}' to restore '{j.Unit}' from.");
        Move(j.Backup, j.Unit);
    }

    /// <summary>Moves a file or directory to <paramref name="dest"/> (which must not exist). Across volumes: copy to the partial name, rename, delete the source.</summary>
    void Move(string src, string dest)
    {
        var isDir = Directory.Exists(src);
        if (SameVolume(src, dest))
        {
            if (!isDir) { Retry(() => File.Move(src, dest)); return; }
            try { Retry(() => Directory.Move(src, dest)); return; }
            catch (IOException) when (!OperatingSystem.IsWindows() && Directory.Exists(src) && !Exists(dest))
            {
                // Unix: a rename across mounts fails (EXDEV) even when the path roots match; fall back to copying.
            }
        }
        var partial = dest + PartialSuffix;
        Retry(() =>
        {
            DeletePath(partial);
            if (isDir) CopyTree(src, partial); else File.Copy(src, partial, true);
        });
        OnCopied?.Invoke(partial);
        if (isDir) Retry(() => Directory.Move(partial, dest)); else Retry(() => File.Move(partial, dest));
        Retry(() => DeletePath(src));
    }

    /// <summary>Copies a directory tree file by file (File.Copy keeps Unix modes); symbolic links are recreated, not followed.</summary>
    static void CopyTree(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var e in new DirectoryInfo(src).EnumerateFileSystemInfos())
        {
            var target = Path.Combine(dest, e.Name);
            if (e.LinkTarget is { } link)
            {
                if (e is DirectoryInfo) Directory.CreateSymbolicLink(target, link); else File.CreateSymbolicLink(target, link);
            }
            else if (e is DirectoryInfo) CopyTree(e.FullName, target);
            else File.Copy(e.FullName, target, true);
        }
    }

    static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    /// <summary>Deletes a file, a link or a directory tree (links are removed, never followed).</summary>
    static void DeletePath(string p)
    {
        FileAttributes attrs;
        try { attrs = File.GetAttributes(p); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }
        var link = (attrs & FileAttributes.ReparsePoint) != 0;
        if ((attrs & FileAttributes.Directory) == 0)
        {
            if (!link) File.SetAttributes(p, FileAttributes.Normal);
            File.Delete(p);
            return;
        }
        if (link) { Directory.Delete(p); return; }
        if (OperatingSystem.IsWindows()) // read-only files block Directory.Delete there
            foreach (var f in Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(p, true);
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
        var earliest = new Journal("backup", unitPath, backupPath, stagedPath, Version); // unreadable: earliest phase (restore, never clear)
        try { return JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath), JsonOpts) is { Step: not null, Unit: not null, Backup: not null } j ? j : earliest; }
        catch (JsonException) { return earliest; }
    }
}
