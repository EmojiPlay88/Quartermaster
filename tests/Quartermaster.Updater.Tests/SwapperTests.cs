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

    public void SeedOld() { Put(App, "Quartermaster.App.exe", "old"); Put(App, "a.dll", "old-a"); Put(App, "sub/b.dll", "old-b"); }
    public void SeedNew() { Put(Staged, "Quartermaster.App.exe", "new"); Put(Staged, "a.dll", "new-a"); Put(Staged, "sub/c.dll", "new-c"); }

    public Swapper Swapper() => new(App, Staged, Backup, Journal);

    public static string Snapshot(string dir) =>
        string.Join("|", Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(dir, f) + "=" + File.ReadAllText(f))
                .Order(StringComparer.Ordinal)
            : []);

    public void WriteJournal(string step) =>
        File.WriteAllText(Journal, JsonSerializer.Serialize(new { step, appDir = App, backupDir = Backup }));

    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}

public class SwapperTests
{
    [Fact]
    public void Apply_MovesStagedIn_BackupHasOld()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        t.Swapper().Apply();
        Assert.Equal("Quartermaster.App.exe=new|a.dll=new-a|sub\\c.dll=new-c".Split('|').Order(StringComparer.Ordinal).Aggregate((x, y) => x + "|" + y), TempArea.Snapshot(t.App));
        Assert.Equal("Quartermaster.App.exe=old|a.dll=old-a|sub\\b.dll=old-b".Split('|').Order(StringComparer.Ordinal).Aggregate((x, y) => x + "|" + y), TempArea.Snapshot(t.Backup));
        Assert.Contains("\"done\"", File.ReadAllText(t.Journal));
    }

    [Fact]
    public void Apply_FileLockedThenFreed_Retries()
    {
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
}
