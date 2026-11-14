using System.Text.Json;

namespace Quartermaster.Updater.Tests;

/// <summary>A unit beside its future backup (root/Quartermaster.app or root/Quartermaster.AppImage) and a staged unit in root/staged.</summary>
public sealed class UnitArea : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "qmunit-" + Guid.NewGuid().ToString("N"));
    public string Unit { get; }
    public string Staged { get; }
    public string Backup => UnitSwapper.BackupPathFor(Unit, "1.0.0");
    public string Journal => Path.Combine(Root, "data", "swap-journal.json");
    public string Marker => Path.Combine(Root, "data", "started-2.0.0");
    public bool IsFile { get; }

    public static string Exe = Path.Combine("Contents", "MacOS", "Quartermaster");
    public static string Plist = Path.Combine("Contents", "Info.plist");
    public static string Extra = Path.Combine("Contents", "Resources", "new.dat");

    public UnitArea(bool file = false)
    {
        IsFile = file;
        var name = file ? "Quartermaster.AppImage" : "Quartermaster.app";
        Unit = Path.Combine(Root, name);
        Staged = Path.Combine(Root, "staged", name);
        Directory.CreateDirectory(Path.GetDirectoryName(Journal)!);
        Directory.CreateDirectory(Path.GetDirectoryName(Staged)!);
        if (file)
        {
            File.WriteAllText(Unit, "old-image");
            File.WriteAllText(Staged, "new-image");
        }
        else
        {
            Put(Unit, Exe, "old"); Put(Unit, Plist, "old-plist");
            Put(Staged, Exe, "new"); Put(Staged, Plist, "new-plist"); Put(Staged, Extra, "extra");
        }
    }

    public static void Put(string dir, string rel, string text)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    public UnitSwapper Swapper() => new(Unit, Staged, Backup, Journal) { Version = "2.0.0" };

    /// <summary>"rel=text|..." for a directory, "=text" for a file, "" when missing.</summary>
    public static string Snapshot(string path) =>
        File.Exists(path) ? "=" + File.ReadAllText(path)
        : Directory.Exists(path)
            ? string.Join("|", Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(path, f) + "=" + File.ReadAllText(f)).Order(StringComparer.Ordinal))
            : "";

    public void WriteJournal(string step, string? version = "2.0.0", string? staged = null) =>
        File.WriteAllText(Journal, JsonSerializer.Serialize(new { step, unit = Unit, backup = Backup, staged = staged ?? Staged, version }));

    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}

public class UnitSwapperTests
{
    [Fact]
    public void UnitSwap_Dir_Apply_ReplacesAndKeepsBackup()
    {
        using var t = new UnitArea();
        var oldSnap = UnitArea.Snapshot(t.Unit); var newSnap = UnitArea.Snapshot(t.Staged);
        t.Swapper().Apply();
        Assert.Equal(newSnap, UnitArea.Snapshot(t.Unit));
        Assert.Equal(oldSnap, UnitArea.Snapshot(t.Backup));
        Assert.False(Directory.Exists(t.Staged));
        Assert.Contains("\"done\"", File.ReadAllText(t.Journal));
    }

    [Fact]
    public void UnitSwap_File_Apply_ReplacesAppImage()
    {
        using var t = new UnitArea(file: true);
        t.Swapper().Apply();
        Assert.Equal("new-image", File.ReadAllText(t.Unit));
        Assert.Equal("old-image", File.ReadAllText(t.Backup));
        Assert.False(File.Exists(t.Staged));
        Assert.Contains("\"done\"", File.ReadAllText(t.Journal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnitSwap_Rollback_RestoresOld(bool file)
    {
        using var t = new UnitArea(file);
        var oldSnap = UnitArea.Snapshot(t.Unit);
        var s = t.Swapper();
        s.Apply();
        s.Rollback();
        Assert.Equal(oldSnap, UnitArea.Snapshot(t.Unit));
        Assert.Equal("", UnitArea.Snapshot(t.Backup));
        Assert.Contains("\"rolledback\"", File.ReadAllText(t.Journal));
    }

    [Fact]
    public void UnitSwap_Rollback_NoBackup_RefusesAndKeepsUnit()
    {
        using var t = new UnitArea();
        var s = t.Swapper();
        s.Apply();
        var newSnap = UnitArea.Snapshot(t.Unit);
        Directory.Delete(t.Backup, true);
        Assert.Throws<InvalidOperationException>(s.Rollback);
        Assert.Equal(newSnap, UnitArea.Snapshot(t.Unit));
    }

    [Fact]
    public void UnitSwap_ResumeAfterBackupStep_RestoresOld()
    {
        // Crash right after the unit went to the backup, before the staged unit moved in.
        using var t = new UnitArea();
        var oldSnap = UnitArea.Snapshot(t.Unit);
        t.WriteJournal("backup");
        Directory.Move(t.Unit, t.Backup);
        Assert.Equal(ResumeOutcome.RolledBack, t.Swapper().Resume());
        Assert.Equal(oldSnap, UnitArea.Snapshot(t.Unit));
        Assert.False(Directory.Exists(t.Backup));
    }

    [Fact]
    public void UnitSwap_ResumeAfterBackupStep_CrossVolumeHalfDeletedSource_RestoresFromBackup()
    {
        // Crash while deleting the source after a cross-volume copy: the backup is complete, the unit is partial.
        using var t = new UnitArea();
        var oldSnap = UnitArea.Snapshot(t.Unit);
        t.WriteJournal("backup");
        CopyTree(t.Unit, t.Backup);
        File.Delete(Path.Combine(t.Unit, UnitArea.Exe));
        Assert.Equal(ResumeOutcome.RolledBack, t.Swapper().Resume());
        Assert.Equal(oldSnap, UnitArea.Snapshot(t.Unit));
    }

    [Fact]
    public void UnitSwap_ResumeAfterBackupStep_PartialBackupOnly_KeepsUnit()
    {
        // Crash during a cross-volume copy to the backup: the unit is untouched, the partial copy goes.
        using var t = new UnitArea();
        var oldSnap = UnitArea.Snapshot(t.Unit);
        t.WriteJournal("backup");
        UnitArea.Put(t.Backup + ".qm-partial", UnitArea.Plist, "half");
        Assert.Equal(ResumeOutcome.RolledBack, t.Swapper().Resume());
        Assert.Equal(oldSnap, UnitArea.Snapshot(t.Unit));
        Assert.False(Directory.Exists(t.Backup + ".qm-partial"));
    }

    [Fact]
    public void UnitSwap_ResumeInstallMine_Completes()
    {
        using var t = new UnitArea();
        var oldSnap = UnitArea.Snapshot(t.Unit); var newSnap = UnitArea.Snapshot(t.Staged);
        t.WriteJournal("install");
        Directory.Move(t.Unit, t.Backup);
        UnitArea.Put(t.Unit + ".qm-partial", UnitArea.Plist, "half"); // interrupted cross-volume install
        Assert.Equal(ResumeOutcome.Completed, t.Swapper().Resume());
        Assert.Equal(newSnap, UnitArea.Snapshot(t.Unit));
        Assert.Equal(oldSnap, UnitArea.Snapshot(t.Backup));
        Assert.False(Directory.Exists(t.Unit + ".qm-partial"));
        Assert.Contains("\"done\"", File.ReadAllText(t.Journal));
    }

    static void MoveAny(string src, string dest) { if (Directory.Exists(src)) Directory.Move(src, dest); else File.Move(src, dest); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnitSwap_ResumeInstall_StagedRenamedToPartial_Completes(bool file)
    {
        // Crash after the first rename of the install (staged -> unit.qm-partial), before partial -> unit.
        using var t = new UnitArea(file);
        var oldSnap = UnitArea.Snapshot(t.Unit); var newSnap = UnitArea.Snapshot(t.Staged);
        t.WriteJournal("install");
        MoveAny(t.Unit, t.Backup);
        MoveAny(t.Staged, t.Unit + ".qm-partial");
        Assert.Equal(ResumeOutcome.Completed, t.Swapper().Resume());
        Assert.Equal(newSnap, UnitArea.Snapshot(t.Unit));
        Assert.Equal(oldSnap, UnitArea.Snapshot(t.Backup));
        Assert.Equal("", UnitArea.Snapshot(t.Unit + ".qm-partial"));
    }

    [Fact]
    public void UnitSwap_ResumeInstall_TruncatedPartialCopy_KeepsStagedAndRecopies()
    {
        // Crash mid-copy of an AppImage to the partial name (what File.Move does across mounts on Unix): the unit is missing,
        // the partial is truncated, the staged file is intact and must not be deleted before the unit is complete.
        using var t = new UnitArea(file: true);
        t.WriteJournal("install");
        File.Move(t.Unit, t.Backup);
        File.WriteAllText(t.Unit + ".qm-partial", "new-im");
        Assert.Equal(ResumeOutcome.Completed, t.Swapper().Resume());
        Assert.Equal("new-image", File.ReadAllText(t.Unit));
        Assert.False(File.Exists(t.Unit + ".qm-partial"));
        Assert.Equal("old-image", File.ReadAllText(t.Backup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnitSwap_Rollback_TruncatedPartialInstall_RestoresOld(bool file)
    {
        using var t = new UnitArea(file);
        var oldSnap = UnitArea.Snapshot(t.Unit);
        t.WriteJournal("install");
        MoveAny(t.Unit, t.Backup);
        UnitArea.Put(Path.GetDirectoryName(t.Unit)!, Path.GetFileName(t.Unit) + ".qm-partial", "half");
        t.Swapper().Rollback();
        Assert.Equal(oldSnap, UnitArea.Snapshot(t.Unit));
        Assert.Equal("", UnitArea.Snapshot(t.Unit + ".qm-partial"));
        Assert.Equal("", UnitArea.Snapshot(t.Backup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnitSwap_ResumeAfterBackupStep_UnitRenamedToPartial_RestoresOld(bool file)
    {
        // Crash after unit -> backup.qm-partial, before partial -> backup: the partial is the whole old unit.
        using var t = new UnitArea(file);
        var oldSnap = UnitArea.Snapshot(t.Unit);
        t.WriteJournal("backup");
        MoveAny(t.Unit, t.Backup + ".qm-partial");
        Assert.Equal(ResumeOutcome.RolledBack, t.Swapper().Resume());
        Assert.Equal(oldSnap, UnitArea.Snapshot(t.Unit));
        Assert.Equal("", UnitArea.Snapshot(t.Backup + ".qm-partial"));
        Assert.Equal("", UnitArea.Snapshot(t.Backup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnitSwap_ResumeRollbackRestore_BackupRenamedToPartial_RestoresOld(bool file)
    {
        // Crash during a restore after backup -> unit.qm-partial, before partial -> unit.
        using var t = new UnitArea(file);
        var oldSnap = UnitArea.Snapshot(t.Unit);
        t.WriteJournal("rollback-restore");
        MoveAny(t.Unit, t.Unit + ".qm-partial");
        Assert.Equal(ResumeOutcome.RolledBack, t.Swapper().Resume());
        Assert.Equal(oldSnap, UnitArea.Snapshot(t.Unit));
        Assert.Equal("", UnitArea.Snapshot(t.Unit + ".qm-partial"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnitSwap_SameVolumeMove_RenamesWithoutCopyOrLeftovers(bool file)
    {
        // Same volume: renames only (no copy), and no partial is left behind.
        using var t = new UnitArea(file);
        var oldSnap = UnitArea.Snapshot(t.Unit); var newSnap = UnitArea.Snapshot(t.Staged);
        var s = t.Swapper();
        var copied = new List<string>();
        s.OnCopied = copied.Add;
        s.Apply();
        Assert.Empty(copied);
        Assert.Equal(newSnap, UnitArea.Snapshot(t.Unit));
        Assert.Equal(oldSnap, UnitArea.Snapshot(t.Backup));
        Assert.Equal("", UnitArea.Snapshot(t.Unit + ".qm-partial"));
        Assert.Equal("", UnitArea.Snapshot(t.Backup + ".qm-partial"));
    }

    [Fact]
    public void UnitSwap_ResumeInstallOther_RollsBackStale()
    {
        using var t = new UnitArea();
        var oldSnap = UnitArea.Snapshot(t.Unit);
        t.WriteJournal("install", version: "1.5.0");
        Directory.Move(t.Unit, t.Backup);
        Assert.Equal(ResumeOutcome.Stale, t.Swapper().Resume());
        Assert.Equal(oldSnap, UnitArea.Snapshot(t.Unit));
        Assert.False(File.Exists(t.Journal));
    }

    [Fact]
    public void UnitSwap_UnreadableJournal_RestoresNeverClears()
    {
        using var t = new UnitArea();
        var oldSnap = UnitArea.Snapshot(t.Unit);
        File.WriteAllText(t.Journal, "{not json");
        Assert.Equal(ResumeOutcome.RolledBack, t.Swapper().Resume());
        Assert.Equal(oldSnap, UnitArea.Snapshot(t.Unit));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnitSwap_CrossVolumeMove_UsesPartialName(bool file)
    {
        using var t = new UnitArea(file);
        var oldSnap = UnitArea.Snapshot(t.Unit); var newSnap = UnitArea.Snapshot(t.Staged);
        var seen = new List<string>();
        var s = t.Swapper();
        s.SameVolume = (_, _) => false;
        s.OnCopied = p => seen.Add(p);
        s.Apply();
        Assert.Equal([t.Backup + ".qm-partial", t.Unit + ".qm-partial"], seen);
        Assert.Equal(newSnap, UnitArea.Snapshot(t.Unit));
        Assert.Equal(oldSnap, UnitArea.Snapshot(t.Backup));
        Assert.Equal("", UnitArea.Snapshot(t.Staged));
        Assert.Equal("", UnitArea.Snapshot(t.Unit + ".qm-partial"));
        Assert.Equal("", UnitArea.Snapshot(t.Backup + ".qm-partial"));
        s.Rollback();
        Assert.Equal(oldSnap, UnitArea.Snapshot(t.Unit));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnitSwap_StagedMissing_RefusedNothingChanged(bool file)
    {
        using var t = new UnitArea(file);
        var oldSnap = UnitArea.Snapshot(t.Unit);
        if (file) File.Delete(t.Staged); else Directory.Delete(t.Staged, true);
        Assert.Throws<UnitMissingException>(t.Swapper().Apply);
        Assert.Equal(oldSnap, UnitArea.Snapshot(t.Unit));
        Assert.Equal("", UnitArea.Snapshot(t.Backup));
        Assert.False(File.Exists(t.Journal));
    }

    [Fact]
    public void UnitSwap_BackupNameHasNoAppSuffix()
    {
        var parent = Path.Combine(Path.GetTempPath(), "apps");
        var b = UnitSwapper.BackupPathFor(Path.Combine(parent, "Quartermaster.app") + Path.DirectorySeparatorChar, "1.0.0");
        Assert.Equal(Path.Combine(parent, ".Quartermaster.app.qm-backup-1.0.0"), b);
        Assert.False(b.EndsWith(".app", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.Combine(parent, ".Quartermaster.AppImage.qm-backup-1.0.0"), UnitSwapper.BackupPathFor(Path.Combine(parent, "Quartermaster.AppImage"), "1.0.0"));
    }

    static void CopyTree(string src, string dest)
    {
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var d = Path.Combine(dest, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(d)!);
            File.Copy(f, d);
        }
    }
}
