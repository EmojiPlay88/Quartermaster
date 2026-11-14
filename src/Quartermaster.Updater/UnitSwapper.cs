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
/// the backup exists. Every move goes through "&lt;target&gt;.qm-partial": a rename to it on the same volume, a copy to it across
/// volumes; then a same-folder rename to the target, then the source is deleted if still there. So the target only gets its real
/// name complete, and a crash never leaves a half-moved or half-copied unit under the real name. <see cref="ResumeOutcome"/> means the same as for <see cref="Swapper"/>.
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
                // A partial backup with the unit gone was reached by a rename, so it is complete; otherwise it is a copy in progress.
                if (!Exists(j.Backup) && !Exists(j.Unit) && Exists(j.Backup + PartialSuffix)) Retry(() => Rename(j.Backup + PartialSuffix, j.Backup));
                Retry(() => DeletePath(j.Backup + PartialSuffix));
                if (!Exists(j.Backup)) break; // the unit never left
                // The backup is complete (it only gets its name by a rename); the unit is gone or a half-deleted cross-volume source.
                Retry(() => { DeletePath(j.Unit); DeletePath(j.Unit + PartialSuffix); });
                Write(j = j with { Step = "rollback-restore" });
                Restore(j);
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

    /// <summary>Moves the staged unit in, finishing an interrupted move.</summary>
    void Install(string unit, string staged) => CompleteMove(staged, unit, $"'{unit}' is neither staged nor installed.");

    /// <summary>Moves the backup back after the unit was cleared, finishing an interrupted move.</summary>
    void Restore(Journal j) => CompleteMove(j.Backup, j.Unit, $"No backup found at '{j.Backup}' to restore '{j.Unit}' from.");

    /// <summary>
    /// Brings a <see cref="Move"/> of <paramref name="src"/> to <paramref name="dest"/> to its end, whether or not it started.
    /// <paramref name="dest"/> only ever gets its name by a rename of a complete partial, so its existence means the move finished
    /// (the source is a leftover); a partial with the source gone was reached by a rename, so it is complete too.
    /// </summary>
    void CompleteMove(string src, string dest, string missing)
    {
        var partial = dest + PartialSuffix;
        if (Exists(dest)) { Retry(() => { DeletePath(partial); DeletePath(src); }); return; }
        if (Exists(partial) && !Exists(src)) { Retry(() => Rename(partial, dest)); return; }
        if (!Exists(src)) throw new IOException(missing);
        Move(src, dest);
    }

    /// <summary>
    /// Moves a file or directory to <paramref name="dest"/> (which must not exist), always through "&lt;dest&gt;.qm-partial": a rename (or,
    /// across volumes, a copy) to the partial name, then a same-folder rename to <paramref name="dest"/>, then the source is deleted if still there.
    /// </summary>
    void Move(string src, string dest)
    {
        var partial = dest + PartialSuffix;
        Retry(() => DeletePath(partial));
        if (SameVolume(src, dest) && TryRename(src, partial)) { Retry(() => Rename(partial, dest)); return; }
        var isDir = Directory.Exists(src);
        Retry(() =>
        {
            DeletePath(partial);
            if (isDir) CopyTree(src, partial); else File.Copy(src, partial, true);
        });
        OnCopied?.Invoke(partial);
        Retry(() => Rename(partial, dest));
        Retry(() => DeletePath(src));
    }

    /// <summary>
    /// Renames, retrying on Windows (transient locks). Elsewhere one try: a failure with the source still there is a rename across
    /// mounts (EXDEV, the path roots match on Unix) and returns false at once so the caller copies instead.
    /// </summary>
    bool TryRename(string src, string dest)
    {
        if (OperatingSystem.IsWindows()) { Retry(() => Rename(src, dest)); return true; }
        try { Rename(src, dest); return true; }
        catch (IOException) when (Exists(src)) { return false; }
    }

    static void Rename(string src, string dest)
    {
        if (Directory.Exists(src)) Directory.Move(src, dest); else File.Move(src, dest);
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
