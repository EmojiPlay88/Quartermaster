namespace Quartermaster.Updater.Tests;

sealed class FakeHost : IProcessHost
{
    public bool OldAlive = false;
    public List<(string Exe, string Args)> Started = [];
    public List<int> Killed = [];
    public Action<(string Exe, string Args), int>? OnStart;
    public HashSet<int> Dead = [];
    int _next = 1000;
    public bool IsAlive(int pid) => pid == 1 ? OldAlive : !Dead.Contains(pid) && !Killed.Contains(pid);
    public int Start(string exe, string args, string workDir)
    {
        Started.Add((exe, args));
        var pid = _next++;
        OnStart?.Invoke((exe, args), pid);
        return pid;
    }
    public void Kill(int pid) => Killed.Add(pid);
}

public class RunnerTests
{
    static UpdateOptions Opts(TempArea t, int exitSec = 5, int markerSec = 2) => new()
    {
        AppDir = t.App, StagedDir = t.Staged, Pid = 1, Version = "2.0.0", OldVersion = "1.0.0",
        MarkerPath = t.Marker, MarkerTimeout = TimeSpan.FromSeconds(markerSec), ExitTimeout = TimeSpan.FromSeconds(exitSec),
        BackupDir = t.Backup, JournalPath = t.Journal,
    };

    [Fact]
    public void Main_PidNeverExits_AbortsNothingChanged()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        var oldSnap = TempArea.Snapshot(t.App); var stagedSnap = TempArea.Snapshot(t.Staged);
        var host = new FakeHost { OldAlive = true };
        var code = new UpdateRunner(Opts(t, exitSec: 1), host, _ => { }).Run();
        Assert.Equal(ExitCodes.PidTimeout, code);
        Assert.Equal(oldSnap, TempArea.Snapshot(t.App));
        Assert.Equal(stagedSnap, TempArea.Snapshot(t.Staged));
        Assert.Empty(host.Started);
        Assert.False(File.Exists(t.Journal));
    }

    [Fact]
    public void Main_MarkerAppears_KeepsNewAndDeletesJournal()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        var host = new FakeHost();
        host.OnStart = (s, _) => { if (s.Args.StartsWith("--updated-from")) File.WriteAllText(t.Marker, "ok"); };
        var code = new UpdateRunner(Opts(t), host, _ => { }).Run();
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal("new", File.ReadAllText(Path.Combine(t.App, "Quartermaster.App.exe")));
        Assert.False(File.Exists(t.Journal));
        var s = Assert.Single(host.Started);
        Assert.Equal(Path.Combine(t.App, "Quartermaster.App.exe"), s.Exe);
        Assert.Equal("--updated-from 1.0.0", s.Args);
    }

    [Fact]
    public void Main_NoMarker_RollsBackAndRestartsOld()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        var oldSnap = TempArea.Snapshot(t.App);
        var host = new FakeHost();
        var code = new UpdateRunner(Opts(t, markerSec: 1), host, _ => { }).Run();
        Assert.Equal(ExitCodes.RolledBack, code);
        Assert.Equal(oldSnap, TempArea.Snapshot(t.App));
        Assert.Equal(2, host.Started.Count);
        Assert.Equal("--update-failed 2.0.0", host.Started[1].Args);
        Assert.Contains(1000, host.Killed);
        Assert.False(File.Exists(t.Journal));
    }

    [Fact]
    public void Main_NewProcessDiesImmediately_RollsBackWithoutWaitingFullTimeout()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        var host = new FakeHost();
        host.OnStart = (s, pid) => { if (s.Args.StartsWith("--updated-from")) host.Dead.Add(pid); };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var code = new UpdateRunner(Opts(t, markerSec: 30), host, _ => { }).Run();
        Assert.Equal(ExitCodes.RolledBack, code);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Main_CrashedJournalInBackup_RollsBackAndRestartsOld()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        var oldSnap = TempArea.Snapshot(t.App);
        t.WriteJournal("backup");
        Directory.CreateDirectory(t.Backup);
        File.Move(Path.Combine(t.App, "a.dll"), Path.Combine(t.Backup, "a.dll"));
        var host = new FakeHost();
        var code = new UpdateRunner(Opts(t), host, _ => { }).Run();
        Assert.Equal(ExitCodes.RolledBack, code);
        Assert.Equal(oldSnap, TempArea.Snapshot(t.App));
        Assert.Equal("--update-failed 2.0.0", Assert.Single(host.Started).Args);
    }

    [Fact]
    public void ChooseBackupDir_WritableParent_Sibling()
    {
        using var t = new TempArea();
        var d = UpdateRunner.ChooseBackupDir(t.App, "1.0.0", Path.GetDirectoryName(t.Marker)!);
        Assert.Equal(Path.Combine(t.Root, "Quartermaster.backup-1.0.0"), d);
    }

    [Fact]
    public void ChooseBackupDir_UnwritableParent_FallsBackToMarkerFolder()
    {
        using var t = new TempArea();
        var missingParentApp = Path.Combine(t.Root, "nonexistent", "app");
        var d = UpdateRunner.ChooseBackupDir(missingParentApp, "1.0.0", Path.GetDirectoryName(t.Marker)!);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(t.Marker)!, "backup-1.0.0"), d);
    }

    [Fact]
    public void ParseArgs_RequiredAndDefaults()
    {
        var o = UpdateOptions.Parse(["--app", @"C:\a", "--staged", @"C:\s", "--pid", "7", "--version", "2.0.0", "--old", "1.0.0", "--marker", @"C:\d\started-2.0.0"]);
        Assert.NotNull(o);
        Assert.Equal(TimeSpan.FromSeconds(30), o!.MarkerTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), o.ExitTimeout);
        Assert.Equal(@"C:\d\swap-journal.json", o.JournalPath);
        Assert.Equal(@"C:\d\helper.log", o.LogPath);
        Assert.Null(UpdateOptions.Parse(["--app", @"C:\a"]));
    }
}
