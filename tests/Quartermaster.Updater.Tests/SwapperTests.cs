using System.Text.Json;

namespace Quartermaster.Updater.Tests;

public sealed class TempArea : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "qmupd-" + Guid.NewGuid().ToString("N"));
    public string App => Path.Combine(Root, "app");
    public string Staged => Path.Combine(Root, "staged");
    public string Backup => Path.Combine(Root, "backup");
    public string Journal => Path.Combine(Root, "data", "swap-journal.json");
    public string Marker => Path.Combine(Root, "data", "started-2.0.0");

    public TempArea()
    {
        Directory.CreateDirectory(App);
        Directory.CreateDirectory(Staged);
        Directory.CreateDirectory(Path.GetDirectoryName(Journal)!);
    }

    public void Put(string dir, string rel, string text)
    {
        var p = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    public const string ManifestName = "quartermaster-files.txt";

    public void SeedOld()
    {
        Put(App, "Quartermaster.App.exe", "old"); Put(App, "a.dll", "old-a"); Put(App, "sub/b.dll", "old-b");
        PutList(App, OldList);
    }

    public void SeedNew()
    {
        Put(Staged, "Quartermaster.App.exe", "new"); Put(Staged, "a.dll", "new-a"); Put(Staged, "sub/c.dll", "new-c");
        PutList(Staged, NewList);
    }

    /// <summary>Writes a release file list; ';' in <paramref name="list"/> separates lines.</summary>
    public void PutList(string dir, string list) => Put(dir, ManifestName, list.Replace(';', '\n') + "\n");

    public const string OldList = "Quartermaster.App.exe;a.dll;sub/b.dll;" + ManifestName;
    public const string NewList = "Quartermaster.App.exe;a.dll;sub/c.dll;" + ManifestName;

    /// <summary>Files the user put in the app folder (not in any release list).</summary>
    public void SeedUnlisted() { Put(App, "notes TEST.txt", "mine"); Put(App, "sub/mine TEST.txt", "mine-sub"); Put(App, "own/deep TEST.txt", "mine-own"); }

    public static string Expected(params string[] entries) => string.Join("|", entries.Order(StringComparer.Ordinal));

    public Swapper Swapper() => new(App, Staged, Backup, Journal);

    public static string Snapshot(string dir) =>
        string.Join("|", Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(dir, f) + "=" + File.ReadAllText(f).TrimEnd('\n').Replace('\n', ';'))
                .Order(StringComparer.Ordinal)
            : []);

    public void WriteJournal(string step, string? version = null, string? backupDir = null, string? stagedDir = null) =>
        File.WriteAllText(Journal, JsonSerializer.Serialize(new { step, appDir = App, backupDir = backupDir ?? Backup, stagedDir, version }));

    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}

public class SwapperTests
{
    [Fact]
    public void Apply_MovesStagedIn_BackupHasOld()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        t.Swapper().Apply();
        Assert.Equal(TempArea.Expected("Quartermaster.App.exe=new", "a.dll=new-a", "sub\\c.dll=new-c", TempArea.ManifestName + "=" + TempArea.NewList), TempArea.Snapshot(t.App));
        Assert.Equal(TempArea.Expected("Quartermaster.App.exe=old", "a.dll=old-a", "sub\\b.dll=old-b", TempArea.ManifestName + "=" + TempArea.OldList), TempArea.Snapshot(t.Backup));
        Assert.Contains("\"done\"", File.ReadAllText(t.Journal));
    }

    // ---------- only listed files are moved ----------

    [Fact]
    public void Apply_UnlistedFilesStayInAppFolder()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew(); t.SeedUnlisted();
        t.Swapper().Apply();
        Assert.Equal("mine", File.ReadAllText(Path.Combine(t.App, "notes TEST.txt")));
        Assert.Equal("mine-sub", File.ReadAllText(Path.Combine(t.App, "sub", "mine TEST.txt")));
        Assert.Equal("mine-own", File.ReadAllText(Path.Combine(t.App, "own", "deep TEST.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(t.App, "Quartermaster.App.exe")));
        Assert.False(File.Exists(Path.Combine(t.App, "sub", "b.dll")));
        Assert.DoesNotContain("TEST", TempArea.Snapshot(t.Backup));
    }

    [Fact]
    public void Apply_EmptiedListedSubfolderRemoved_UnlistedEmptyFolderKept()
    {
        using var t = new TempArea(); t.SeedOld();
        t.Put(t.App, "old-only/x.dll", "old-x");
        t.PutList(t.App, TempArea.OldList + ";old-only/x.dll");
        Directory.CreateDirectory(Path.Combine(t.App, "empty TEST"));
        t.SeedNew();
        t.Swapper().Apply();
        Assert.False(Directory.Exists(Path.Combine(t.App, "old-only")));
        Assert.True(Directory.Exists(Path.Combine(t.App, "empty TEST")));
    }

    [Fact]
    public void Rollback_UnlistedFilesSurvive_OldRestored()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew(); t.SeedUnlisted();
        var before = TempArea.Snapshot(t.App);
        var s = t.Swapper(); s.Apply(); s.Rollback();
        Assert.Equal(before, TempArea.Snapshot(t.App));
    }

    [Fact]
    public void Resume_AfterCrashInBackup_UnlistedFilesSurvive()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew(); t.SeedUnlisted();
        var before = TempArea.Snapshot(t.App);
        t.WriteJournal("backup");
        Directory.CreateDirectory(t.Backup);
        File.Move(Path.Combine(t.App, "a.dll"), Path.Combine(t.Backup, "a.dll"));
        Assert.Equal(ResumeOutcome.RolledBack, t.Swapper().Resume());
        Assert.Equal(before, TempArea.Snapshot(t.App));
    }

    [Fact]
    public void Resume_AfterCrashInInstall_UnlistedFilesSurvive()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew(); t.SeedUnlisted();
        var s = t.Swapper(); s.Apply();
        // crash with one file not yet installed: put it back in the staged folder and rewind the journal step
        File.Move(Path.Combine(t.App, "sub", "c.dll"), Path.Combine(t.Staged, "sub", "c.dll"));
        File.WriteAllText(t.Journal, File.ReadAllText(t.Journal).Replace("\"done\"", "\"install\""));
        Assert.Equal(ResumeOutcome.Completed, t.Swapper().Resume());
        Assert.Equal("new-c", File.ReadAllText(Path.Combine(t.App, "sub", "c.dll")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(t.App, "notes TEST.txt")));
        Assert.Equal("mine-sub", File.ReadAllText(Path.Combine(t.App, "sub", "mine TEST.txt")));
        Assert.Equal("mine-own", File.ReadAllText(Path.Combine(t.App, "own", "deep TEST.txt")));
    }

    [Fact]
    public void Resume_AfterCrashInRollbackClear_UnlistedFilesSurvive()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew(); t.SeedUnlisted();
        var before = TempArea.Snapshot(t.App);
        t.Swapper().Apply();
        File.WriteAllText(t.Journal, File.ReadAllText(t.Journal).Replace("\"done\"", "\"rollback-clear\""));
        File.Delete(Path.Combine(t.App, "a.dll")); // crash after one new file was removed
        Assert.Equal(ResumeOutcome.RolledBack, t.Swapper().Resume());
        Assert.Equal(before, TempArea.Snapshot(t.App));
    }

    [Fact]
    public void Apply_NewFileNamedLikeUnlistedFile_RollbackPutsUserFileBack()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        t.Put(t.App, "sub/c.dll", "user copy TEST"); // not in the old list, but the new release ships sub/c.dll
        var before = TempArea.Snapshot(t.App);
        var s = t.Swapper(); s.Apply();
        Assert.Equal("new-c", File.ReadAllText(Path.Combine(t.App, "sub", "c.dll")));
        s.Rollback();
        Assert.Equal(before, TempArea.Snapshot(t.App));
    }

    [Fact]
    public void Apply_NoManifestInAppFolder_Refused_NothingTouched()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew(); t.SeedUnlisted();
        File.Delete(Path.Combine(t.App, TempArea.ManifestName));
        var before = TempArea.Snapshot(t.App); var staged = TempArea.Snapshot(t.Staged);
        Assert.Throws<ManifestMissingException>(() => t.Swapper().Apply());
        Assert.Equal(before, TempArea.Snapshot(t.App));
        Assert.Equal(staged, TempArea.Snapshot(t.Staged));
        Assert.False(File.Exists(t.Journal));
        Assert.False(Directory.Exists(t.Backup));
    }

    [Theory]
    [InlineData("..\\outside.dll")]
    [InlineData("C:\\Windows\\x.dll")]
    [InlineData("/rooted.dll")]
    [InlineData("a//b.dll")]
    public void Apply_UnsafeManifestEntry_Refused(string entry)
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        t.PutList(t.App, TempArea.OldList + ";" + entry);
        var before = TempArea.Snapshot(t.App);
        Assert.Throws<ManifestMissingException>(() => t.Swapper().Apply());
        Assert.Equal(before, TempArea.Snapshot(t.App));
    }

    [Fact]
    public void Apply_StagedListNamesMissingFile_Refused_NothingTouched()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        t.PutList(t.Staged, TempArea.NewList + ";gone.dll");
        var before = TempArea.Snapshot(t.App);
        Assert.Throws<ManifestMissingException>(() => t.Swapper().Apply());
        Assert.Equal(before, TempArea.Snapshot(t.App));
    }

    [Fact]
    public void Resume_LeftoverRolledBackJournalSameVersion_StaleSoTheUpdateIsTriedAgain()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        t.WriteJournal("rolledback", version: "2.0.0");
        var s = t.Swapper(); s.Version = "2.0.0";
        Assert.Equal(ResumeOutcome.Stale, s.Resume());
        Assert.False(File.Exists(t.Journal));
    }

    [Fact]
    public void Apply_FileLockedThenFreed_Retries()
    {
        if (!OperatingSystem.IsWindows()) return; // FileShare.None blocks moves only on Windows
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        var fs = new FileStream(Path.Combine(t.App, "a.dll"), FileMode.Open, FileAccess.Read, FileShare.None);
        _ = Task.Run(async () => { await Task.Delay(1000); fs.Dispose(); });
        t.Swapper().Apply();
        Assert.Equal("new-a", File.ReadAllText(Path.Combine(t.App, "a.dll")));
        Assert.Equal("old-a", File.ReadAllText(Path.Combine(t.Backup, "a.dll")));
    }

    [Fact]
    public void Apply_FileStaysLocked_ThrowsAfterRetryWindow()
    {
        if (!OperatingSystem.IsWindows()) return; // FileShare.None blocks moves only on Windows
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        using var fs = new FileStream(Path.Combine(t.App, "a.dll"), FileMode.Open, FileAccess.Read, FileShare.None);
        var s = t.Swapper(); s.RetryWindow = TimeSpan.FromMilliseconds(500);
        Assert.ThrowsAny<IOException>(() => s.Apply());
    }

    [Fact]
    public void Rollback_RestoresOld()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        var before = TempArea.Snapshot(t.App);
        var s = t.Swapper(); s.Apply(); s.Rollback();
        Assert.Equal(before, TempArea.Snapshot(t.App));
    }

    [Fact]
    public void Resume_AfterCrashInBackup_RollsBack()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        var before = TempArea.Snapshot(t.App);
        t.WriteJournal("backup");
        Directory.CreateDirectory(t.Backup);
        File.Move(Path.Combine(t.App, "a.dll"), Path.Combine(t.Backup, "a.dll")); // crash after one file moved
        Assert.Equal(ResumeOutcome.RolledBack, t.Swapper().Resume());
        Assert.Equal(before, TempArea.Snapshot(t.App));
    }

    [Fact]
    public void Resume_AfterCrashInInstall_FinishesOrRollsBack()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        var newSnap = TempArea.Snapshot(t.Staged);
        t.WriteJournal("install");
        Directory.CreateDirectory(t.Backup);
        foreach (var f in Directory.GetFiles(t.App)) File.Move(f, Path.Combine(t.Backup, Path.GetFileName(f)));
        Directory.Move(Path.Combine(t.App, "sub"), Path.Combine(t.Backup, "sub"));
        File.Move(Path.Combine(t.Staged, "a.dll"), Path.Combine(t.App, "a.dll")); // one file already installed
        Assert.Equal(ResumeOutcome.Completed, t.Swapper().Resume());
        Assert.Equal(newSnap, TempArea.Snapshot(t.App));
    }

    [Fact]
    public void Resume_NoJournal_None()
    {
        using var t = new TempArea();
        Assert.Equal(ResumeOutcome.None, t.Swapper().Resume());
    }

    [Fact]
    public void WaitForMarker_Appears_True()
    {
        using var t = new TempArea();
        _ = Task.Run(async () => { await Task.Delay(300); File.WriteAllText(t.Marker, "x"); });
        Assert.True(Startup.WaitForMarker(t.Marker, () => true, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void WaitForMarker_ProcessDies_False()
    {
        using var t = new TempArea();
        Assert.False(Startup.WaitForMarker(t.Marker, () => false, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void WaitForMarker_Timeout_False()
    {
        using var t = new TempArea();
        Assert.False(Startup.WaitForMarker(t.Marker, () => true, TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public void Rollback_NoJournal_LeavesAppUntouched()
    {
        using var t = new TempArea(); t.SeedOld();
        var before = TempArea.Snapshot(t.App);
        t.Swapper().Rollback();
        Assert.Equal(before, TempArea.Snapshot(t.App));
    }

    [Fact]
    public void Rollback_DoneJournalButBackupMissing_Throws_AppUntouched()
    {
        using var t = new TempArea();
        t.Put(t.App, "Quartermaster.App.exe", "current");
        t.WriteJournal("done");
        Assert.Throws<InvalidOperationException>(() => t.Swapper().Rollback());
        Assert.Equal("Quartermaster.App.exe=current", TempArea.Snapshot(t.App));
    }

    [Fact]
    public void Apply_StaleBackupLocked_AppUntouched()
    {
        if (!OperatingSystem.IsWindows()) return; // FileShare.None blocks moves only on Windows
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        t.Put(t.Backup, "stale.dll", "stale");
        var before = TempArea.Snapshot(t.App);
        using var fs = new FileStream(Path.Combine(t.Backup, "stale.dll"), FileMode.Open, FileAccess.Read, FileShare.None);
        var s = t.Swapper(); s.RetryWindow = TimeSpan.FromMilliseconds(300);
        Assert.ThrowsAny<IOException>(() => s.Apply());
        s.Rollback();
        Assert.Equal(before, TempArea.Snapshot(t.App));
    }

    [Theory]
    [InlineData("rollback-restore")]
    [InlineData("rollback-clear")]
    public void Resume_AfterCrashInRollback_RestoresOld(string step)
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        var before = TempArea.Snapshot(t.App);
        t.Swapper().Apply();
        t.WriteJournal(step);
        if (step == "rollback-restore")
        {
            // new files cleared, one old file already moved back
            foreach (var e in Directory.GetFileSystemEntries(t.App)) { if (Directory.Exists(e)) Directory.Delete(e, true); else File.Delete(e); }
            File.Move(Path.Combine(t.Backup, "a.dll"), Path.Combine(t.App, "a.dll"));
        }
        Assert.Equal(ResumeOutcome.RolledBack, t.Swapper().Resume());
        Assert.Equal(before, TempArea.Snapshot(t.App));
        t.Swapper().Rollback(); // a second rollback must not delete the restored files
        Assert.Equal(before, TempArea.Snapshot(t.App));
    }

    [Fact]
    public void Resume_UsesJournalPaths_NotConstructorBackup()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        var before = TempArea.Snapshot(t.App);
        var realBackup = Path.Combine(t.Root, "real-backup");
        new Swapper(t.App, t.Staged, realBackup, t.Journal).Apply();
        // a later helper run computes a different backup path; the journal's path must win
        new Swapper(t.App, t.Staged, t.Backup, t.Journal).Rollback();
        Assert.Equal(before, TempArea.Snapshot(t.App));
    }
}
