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
    public List<(int Pid, string? Path)> Running = [];
    /// <summary>When set, processes are looked up by exact name here instead of <see cref="Running"/>.</summary>
    public Dictionary<string, List<(int Pid, string? Path)>>? ByName;
    public List<string> Queried = [];
    public IEnumerable<(int Pid, string? Path)> ProcessesNamed(string processName)
    {
        Queried.Add(processName);
        return ByName == null ? Running : ByName.GetValueOrDefault(processName) ?? [];
    }
    /// <summary>Per pid: its environment variables.</summary>
    public Dictionary<int, Dictionary<string, string>> Env = [];
    public IEnumerable<(int Pid, string Value)> ProcessesWithEnv(string name) =>
        Env.Where(e => e.Value.ContainsKey(name)).Select(e => (e.Key, e.Value[name]));
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
        t.WriteJournal("backup", version: "2.0.0");
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
        if (!OperatingSystem.IsWindows()) return; // literal C:\ paths
        var o = UpdateOptions.Parse(["--app", @"C:\a", "--staged", @"C:\s", "--pid", "7", "--version", "2.0.0", "--old", "1.0.0", "--marker", @"C:\d\started-2.0.0"]);
        Assert.NotNull(o);
        Assert.Equal(TimeSpan.FromSeconds(30), o!.MarkerTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), o.ExitTimeout);
        Assert.Equal(@"C:\d\swap-journal.json", o.JournalPath);
        Assert.Equal(@"C:\d\helper.log", o.LogPath);
        Assert.Null(UpdateOptions.Parse(["--app", @"C:\a"]));
    }

    [Fact]
    public void Main_RollbackFails_KeepsJournalAndDoesNotWipeApp()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        var host = new FakeHost();
        host.OnStart = (s, _) => { if (s.Args.StartsWith("--updated-from")) Directory.Delete(t.Backup, true); };
        var code = new UpdateRunner(Opts(t, markerSec: 1), host, _ => { }).Run();
        Assert.Equal(ExitCodes.RollbackFailed, code);
        Assert.True(File.Exists(t.Journal));
        Assert.Equal("new", File.ReadAllText(Path.Combine(t.App, "Quartermaster.App.exe")));
    }

    [Fact]
    public void Main_StaleDoneJournalFromOtherVersion_StillAppliesRequestedUpdate()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        t.WriteJournal("done", version: "1.5.0");
        var host = new FakeHost();
        host.OnStart = (s, _) => { if (s.Args.StartsWith("--updated-from")) File.WriteAllText(t.Marker, "ok"); };
        var code = new UpdateRunner(Opts(t), host, _ => { }).Run();
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal("new", File.ReadAllText(Path.Combine(t.App, "Quartermaster.App.exe")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(t.Backup, "Quartermaster.App.exe")));
    }

    [Fact]
    public void Main_OtherCopyRunningFromAppFolder_RefusedNothingChanged()
    {
        if (OperatingSystem.IsLinux()) return; // the upper-case match relies on a case-insensitive file system rule
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        var oldSnap = TempArea.Snapshot(t.App); var stagedSnap = TempArea.Snapshot(t.Staged);
        var host = new FakeHost();
        host.Running.Add((77, Path.Combine(t.App, "QUARTERMASTER.APP.EXE"))); // case-insensitive match
        var code = new UpdateRunner(Opts(t), host, _ => { }).Run();
        Assert.Equal(ExitCodes.Refused, code);
        Assert.Equal(oldSnap, TempArea.Snapshot(t.App));
        Assert.Equal(stagedSnap, TempArea.Snapshot(t.Staged));
        Assert.False(File.Exists(t.Journal));
        Assert.Equal("--update-refused 2.0.0", Assert.Single(host.Started).Args);
    }

    [Fact]
    public void OtherCopiesRunning_IgnoresSelfOtherFoldersAndUnreadablePaths()
    {
        using var t = new TempArea();
        var exe = Path.Combine(t.App, "Quartermaster.App.exe");
        var host = new FakeHost();
        host.Running.AddRange([(1, exe), (2, null), (3, Path.Combine(t.Root, "elsewhere", "Quartermaster.App.exe"))]);
        Assert.False(UpdateRunner.OtherCopiesRunning(host, [exe], selfPid: 1));
        host.Running.Add((4, exe));
        Assert.True(UpdateRunner.OtherCopiesRunning(host, [exe], selfPid: 1));
    }

    [Fact]
    public void OtherCopiesRunning_EitherExeName()
    {
        using var t = new TempArea();
        var a = Path.Combine(t.App, "Quartermaster.App.exe");
        var b = Path.Combine(t.App, "Quartermaster");
        var host = new FakeHost();
        host.Running.Add((5, b));
        Assert.True(UpdateRunner.OtherCopiesRunning(host, [a, b], selfPid: 1));
        host.Running.Clear();
        host.Running.Add((6, a));
        Assert.True(UpdateRunner.OtherCopiesRunning(host, [a, b], selfPid: 1));
        host.Running.Clear();
        Assert.False(UpdateRunner.OtherCopiesRunning(host, [a, b], selfPid: 1));
    }

    [Fact]
    public void OtherCopiesRunning_CaseRulePerOs()
    {
        using var t = new TempArea();
        var exe = Path.Combine(t.App, "Quartermaster");
        var host = new FakeHost();
        host.Running.Add((9, Path.Combine(t.App, "QUARTERMASTER")));
        Assert.True(UpdateRunner.OtherCopiesRunning(host, [exe], 1, StringComparison.OrdinalIgnoreCase));
        Assert.False(UpdateRunner.OtherCopiesRunning(host, [exe], 1, StringComparison.Ordinal));
        Assert.Equal(!OperatingSystem.IsLinux(), UpdateRunner.OtherCopiesRunning(host, [exe], 1));
    }

    [Fact]
    public void ParseArgs_ExeDefaultAndOverride()
    {
        string[] req = ["--app", "a", "--staged", "s", "--pid", "7", "--version", "2.0.0", "--old", "1.0.0", "--marker", Path.Combine("d", "started-2.0.0")];
        var o = UpdateOptions.Parse(req)!;
        Assert.Equal("Quartermaster.App.exe", o.Exe);
        Assert.Null(o.Unit);
        var p = UpdateOptions.Parse([.. req, "--exe", "Quartermaster", "--unit", "u"])!;
        Assert.Equal("Quartermaster", p.Exe);
        Assert.Equal("u", p.Unit);
    }

    [Fact]
    public void Main_RestartsConfiguredExe()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        var host = new FakeHost();
        host.OnStart = (s, _) => { if (s.Args.StartsWith("--updated-from")) File.WriteAllText(t.Marker, "ok"); };
        var o = new UpdateOptions
        {
            AppDir = t.App, StagedDir = t.Staged, Pid = 1, Version = "2.0.0", OldVersion = "1.0.0", MarkerPath = t.Marker,
            MarkerTimeout = TimeSpan.FromSeconds(2), ExitTimeout = TimeSpan.FromSeconds(5), BackupDir = t.Backup, JournalPath = t.Journal,
            Exe = "Quartermaster.exe",
        };
        Assert.Equal(ExitCodes.Ok, new UpdateRunner(o, host, _ => { }).Run());
        Assert.Equal(Path.Combine(t.App, "Quartermaster.exe"), Assert.Single(host.Started).Exe);
    }

    [Fact]
    public void Main_NoManifestInAppFolder_RefusedNothingChanged()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew(); t.SeedUnlisted();
        File.Delete(Path.Combine(t.App, TempArea.ManifestName));
        var oldSnap = TempArea.Snapshot(t.App);
        var host = new FakeHost();
        var code = new UpdateRunner(Opts(t), host, _ => { }).Run();
        Assert.Equal(ExitCodes.Refused, code);
        Assert.Equal(oldSnap, TempArea.Snapshot(t.App));
        Assert.False(Directory.Exists(t.Backup));
        Assert.Equal("--update-refused 2.0.0", Assert.Single(host.Started).Args);
    }

    [Fact]
    public void Main_UnlistedFiles_SurviveUpdateAndRollback()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew(); t.SeedUnlisted();
        var oldSnap = TempArea.Snapshot(t.App);
        var code = new UpdateRunner(Opts(t, markerSec: 1), new FakeHost(), _ => { }).Run(); // no marker: rolls back
        Assert.Equal(ExitCodes.RolledBack, code);
        Assert.Equal(oldSnap, TempArea.Snapshot(t.App));
    }

    [Fact]
    public void Main_LeftoverRolledBackJournalSameVersion_RetriesTheUpdate()
    {
        using var t = new TempArea(); t.SeedOld(); t.SeedNew();
        t.WriteJournal("rolledback", version: "2.0.0", stagedDir: t.Staged);
        var host = new FakeHost();
        host.OnStart = (s, _) => { if (s.Args.StartsWith("--updated-from")) File.WriteAllText(t.Marker, "ok"); };
        var code = new UpdateRunner(Opts(t), host, _ => { }).Run();
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal("new", File.ReadAllText(Path.Combine(t.App, "Quartermaster.App.exe")));
    }

    // ---------- unit mode (.app bundle / AppImage) ----------

    static UpdateOptions UnitOpts(UnitArea t, int markerSec = 2) => new()
    {
        AppDir = t.Root, // the bundle's parent, or the AppImage's folder
        Exe = t.IsFile ? Path.GetFileName(t.Unit) : Path.Combine(Path.GetFileName(t.Unit), UnitArea.Exe),
        Unit = t.Unit, StagedDir = t.Staged, Pid = 1, Version = "2.0.0", OldVersion = "1.0.0", MarkerPath = t.Marker,
        MarkerTimeout = TimeSpan.FromSeconds(markerSec), ExitTimeout = TimeSpan.FromSeconds(5), JournalPath = t.Journal,
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Main_UnitMode_NoMarker_RollsBack(bool file)
    {
        using var t = new UnitArea(file);
        var oldSnap = UnitArea.Snapshot(t.Unit);
        var host = new FakeHost();
        var code = new UpdateRunner(UnitOpts(t, markerSec: 1), host, _ => { }).Run();
        Assert.Equal(ExitCodes.RolledBack, code);
        Assert.Equal(oldSnap, UnitArea.Snapshot(t.Unit));
        Assert.Equal("", UnitArea.Snapshot(t.Backup));
        Assert.Equal(2, host.Started.Count);
        Assert.Equal("--update-failed 2.0.0", host.Started[1].Args);
        Assert.Contains(1000, host.Killed);
        Assert.False(File.Exists(t.Journal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Main_UnitMode_MarkerAppears_KeepsNewAndRestartsInsideUnit(bool file)
    {
        using var t = new UnitArea(file);
        var newSnap = UnitArea.Snapshot(t.Staged); var oldSnap = UnitArea.Snapshot(t.Unit);
        var host = new FakeHost();
        host.OnStart = (s, _) => { if (s.Args.StartsWith("--updated-from")) File.WriteAllText(t.Marker, "ok"); };
        var code = new UpdateRunner(UnitOpts(t), host, _ => { }).Run();
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Equal(newSnap, UnitArea.Snapshot(t.Unit));
        Assert.Equal(oldSnap, UnitArea.Snapshot(t.Backup));
        var s = Assert.Single(host.Started);
        Assert.Equal(file ? t.Unit : Path.Combine(t.Unit, UnitArea.Exe), s.Exe);
        Assert.Equal("--updated-from 1.0.0", s.Args);
        Assert.False(File.Exists(t.Journal));
    }

    [Fact]
    public void Main_UnitMode_StagedMissing_RefusedNothingChanged()
    {
        using var t = new UnitArea();
        var oldSnap = UnitArea.Snapshot(t.Unit);
        Directory.Delete(t.Staged, true);
        var host = new FakeHost();
        Assert.Equal(ExitCodes.Refused, new UpdateRunner(UnitOpts(t), host, _ => { }).Run());
        Assert.Equal(oldSnap, UnitArea.Snapshot(t.Unit));
        Assert.Equal("--update-refused 2.0.0", Assert.Single(host.Started).Args);
    }

    [Fact]
    public void Main_UnitMode_OtherAppImageRunning_RefusedNothingChanged()
    {
        using var t = new UnitArea(file: true);
        var host = new FakeHost();
        host.Env[77] = new() { ["APPIMAGE"] = Path.GetFullPath(t.Unit) }; // e.g. AppRun
        Assert.Equal(ExitCodes.Refused, new UpdateRunner(UnitOpts(t), host, _ => { }).Run());
        Assert.Equal("old-image", File.ReadAllText(t.Unit));
        Assert.False(File.Exists(t.Journal));
        Assert.Equal("--update-refused 2.0.0", Assert.Single(host.Started).Args);
    }

    [Fact]
    public void OtherUnitCopiesRunning_AppImage_ByAppImageEnvironment()
    {
        using var t = new UnitArea(file: true);
        var full = Path.GetFullPath(t.Unit);
        var host = new FakeHost();
        // A FUSE-like process named after the AppImage file, without APPIMAGE, is not a running copy.
        host.ByName = new() { ["Quartermaster.AppImage"] = [(50, t.Unit)] };
        host.Env[50] = new() { ["PATH"] = "/usr/bin" };
        host.Env[1] = new() { ["APPIMAGE"] = full };                                  // the exiting app (selfPid)
        host.Env[Environment.ProcessId] = new() { ["APPIMAGE"] = full };              // the helper itself inherits it
        host.Env[60] = new() { ["APPIMAGE"] = Path.Combine(t.Root, "Other.AppImage") }; // another AppImage
        Assert.False(UpdateRunner.OtherUnitCopiesRunning(host, t.Unit, "Quartermaster.AppImage", 1));
        host.Env[70] = new() { ["APPIMAGE"] = full };                                  // AppRun / Quartermaster from this unit
        Assert.True(UpdateRunner.OtherUnitCopiesRunning(host, t.Unit, "Quartermaster.AppImage", 1));
    }

    [Fact]
    public void OtherUnitCopiesRunning_AppImage_SymlinkedFolderStillMatches()
    {
        if (OperatingSystem.IsWindows()) return; // links need privileges there; ResolveLinks is the full path on Windows
        using var t = new UnitArea(file: true);
        var link = Path.Combine(Path.GetTempPath(), "qmlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateSymbolicLink(link, t.Root);
        try
        {
            var viaLink = Path.Combine(link, Path.GetFileName(t.Unit));
            Assert.Equal(UpdateRunner.ResolveLinks(t.Unit), UpdateRunner.ResolveLinks(viaLink));
            var host = new FakeHost();
            host.Env[70] = new() { ["APPIMAGE"] = UpdateRunner.ResolveLinks(t.Unit) }; // the runtime's realpath
            Assert.True(UpdateRunner.OtherUnitCopiesRunning(host, viaLink, "Quartermaster.AppImage", 1));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void ResolveLinks_PlainPath_IsTheFullPath()
    {
        using var t = new UnitArea(file: true);
        Assert.Equal(UpdateRunner.ResolveLinks(t.Unit), UpdateRunner.ResolveLinks(Path.Combine(t.Root, ".", Path.GetFileName(t.Unit))));
        if (OperatingSystem.IsWindows()) Assert.Equal(Path.GetFullPath(t.Unit), UpdateRunner.ResolveLinks(t.Unit));
    }

    [Fact]
    public void StartInfo_DropsAppImageVariablesKeepsOthers()
    {
        string[] vars = ["APPIMAGE", "APPDIR", "ARGV0", "OWD", "QM_TEST_KEEP"];
        var saved = vars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var v in vars) Environment.SetEnvironmentVariable(v, "x");
            var psi = RealProcessHost.StartInfo("app", "--updated-from 1.0.0", "dir");
            foreach (var v in RealProcessHost.AppImageVariables) Assert.False(psi.Environment.ContainsKey(v), v);
            Assert.Equal("x", psi.Environment["QM_TEST_KEEP"]);
            Assert.False(psi.UseShellExecute);
            Assert.Equal("dir", psi.WorkingDirectory);
            Assert.Equal("--updated-from 1.0.0", psi.Arguments);
        }
        finally { foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v); }
    }

    [Fact]
    public void OtherUnitCopiesRunning_AppBundle_OnlyPathsUnderTheUnit()
    {
        using var t = new UnitArea();
        var host = new FakeHost();
        host.Running.AddRange([(1, Path.Combine(t.Unit, UnitArea.Exe)), (2, null), (3, Path.Combine(t.Unit + "2", UnitArea.Exe)), (4, t.Unit)]);
        Assert.False(UpdateRunner.OtherUnitCopiesRunning(host, t.Unit, "Quartermaster", 1, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["Quartermaster"], host.Queried);
        host.Running.Add((5, Path.Combine(t.Unit, UnitArea.Exe)));
        Assert.True(UpdateRunner.OtherUnitCopiesRunning(host, t.Unit, "Quartermaster", 1, StringComparison.OrdinalIgnoreCase));
    }
}
