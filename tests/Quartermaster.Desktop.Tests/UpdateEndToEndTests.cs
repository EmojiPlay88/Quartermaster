using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Quartermaster.Desktop.Tests;

/// <summary>Publishes only the updater helper for the current OS's RID into a temp folder (a real `dotnet publish`).</summary>
public sealed class UpdaterPublishFixture : IDisposable
{
    public string Dir { get; }
    public string HelperPath => Path.Combine(Dir, OperatingSystem.IsWindows() ? "Quartermaster.Updater.exe" : "Quartermaster.Updater");

    public UpdaterPublishFixture()
    {
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var rid = (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" + arch;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Quartermaster.slnx"))) d = d.Parent;
        var repo = d?.FullName ?? throw new InvalidOperationException("repo root not found");
        Dir = Path.Combine(Path.GetTempPath(), "qm-updater-publish-" + Guid.NewGuid().ToString("N"));
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "publish", Path.Combine(repo, "src", "Quartermaster.Updater"), "-c", "Release", "-r", rid, "--self-contained", "-o", Dir, "--nologo", "--disable-build-servers", "-nodeReuse:false" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        // Wait on the process, not on pipe EOF: lingering build-server children can keep the pipes open.
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("dotnet publish failed: " + Tail(output) + Tail(err));
        if (!File.Exists(HelperPath)) throw new InvalidOperationException("helper missing after publish");
    }

    static string Tail(Task<string> t) => t.Wait(10000) ? t.Result : "";

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch { }
    }
}

/// <summary>The real published helper swaps fake app folders / units and confirms the restart through the marker file.</summary>
[Trait("Category", "Publish")]
public class UpdateEndToEndTests : IClassFixture<UpdaterPublishFixture>, IDisposable
{
    const string MarkerVar = "QM_TEST_MARKER";
    readonly UpdaterPublishFixture _f;
    readonly string _root = Path.Combine(Path.GetTempPath(), "qm-e2e-" + Guid.NewGuid().ToString("N"));

    public UpdateEndToEndTests(UpdaterPublishFixture f) { _f = f; Directory.CreateDirectory(_root); }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    static string ScriptName => OperatingSystem.IsWindows() ? "app.cmd" : "app.sh";

    static void WriteScript(string path)
    {
        if (OperatingSystem.IsWindows())
            File.WriteAllText(path, "@echo off\r\necho started> \"%" + MarkerVar + "%\"\r\n");
        else
        {
            File.WriteAllText(path, "#!/bin/sh\necho started > \"$" + MarkerVar + "\"\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>A folder with the fake app script (at <paramref name="scriptRel"/>), a version file and quartermaster-files.txt.</summary>
    static void MakeRelease(string dir, string version, string scriptRel)
    {
        Directory.CreateDirectory(dir);
        WriteScript(Path.Combine(dir, scriptRel));
        File.WriteAllText(Path.Combine(dir, "version-" + version + ".txt"), version);
        File.WriteAllLines(Path.Combine(dir, "quartermaster-files.txt"), new[] { scriptRel, "version-" + version + ".txt", "quartermaster-files.txt" });
    }

    /// <summary>A pid that is certainly not alive: a process that was started and has exited.</summary>
    static int DeadPid()
    {
        var psi = new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.Id;
    }

    (int Exit, string Marker) RunHelper(string app, string staged, string exe, string? unit)
    {
        var markerDir = Path.Combine(_root, "marker");
        Directory.CreateDirectory(markerDir);
        var marker = Path.Combine(markerDir, "started.flag");
        var psi = new ProcessStartInfo(_f.HelperPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        void Arg(string k, string v) { psi.ArgumentList.Add(k); psi.ArgumentList.Add(v); }
        Arg("--app", app); Arg("--staged", staged); Arg("--pid", DeadPid().ToString());
        Arg("--version", "0.0.2"); Arg("--old", "0.0.1"); Arg("--marker", marker); Arg("--exe", exe);
        Arg("--marker-timeout", "30"); Arg("--exit-timeout", "5");
        if (unit != null) Arg("--unit", unit);
        psi.Environment[MarkerVar] = marker;
        using var p = Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60000)) { try { p.Kill(true); } catch { } throw new TimeoutException("helper did not finish"); }
        return (p.ExitCode, marker);
    }

    string HelperLog()
    {
        var path = Path.Combine(_root, "marker", "helper.log");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    [Fact]
    public void Update_FolderKind_RealHelper_SwapsAndConfirms()
    {
        var app = Path.Combine(_root, "Quartermaster");
        var staged = Path.Combine(_root, "staged");
        MakeRelease(app, "0.0.1", ScriptName);
        MakeRelease(staged, "0.0.2", ScriptName);

        var (exit, marker) = RunHelper(app, staged, ScriptName, null);

        Assert.True(exit == 0, "exit " + exit + Environment.NewLine + HelperLog());
        Assert.True(File.Exists(Path.Combine(app, "version-0.0.2.txt")));
        Assert.False(File.Exists(Path.Combine(app, "version-0.0.1.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "Quartermaster.backup-0.0.1", "version-0.0.1.txt")));
        Assert.True(File.Exists(marker), "the restarted fake app wrote the marker");
        Assert.False(File.Exists(Path.Combine(_root, "marker", "swap-journal.json")), "journal removed");
    }

    [Fact]
    public void Update_UnitKind_RealHelper_SwapsDirectoryUnit()
    {
        if (OperatingSystem.IsWindows()) return;
        var unit = Path.Combine(_root, "Quartermaster.app");
        var staged = Path.Combine(_root, "staged.app");
        MakeRelease(unit, "0.0.1", ScriptName);
        MakeRelease(staged, "0.0.2", ScriptName);

        var (exit, marker) = RunHelper(_root, staged, Path.Combine("Quartermaster.app", ScriptName), unit);

        Assert.True(exit == 0, "exit " + exit + Environment.NewLine + HelperLog());
        Assert.True(File.Exists(Path.Combine(unit, "version-0.0.2.txt")));
        Assert.False(File.Exists(Path.Combine(unit, "version-0.0.1.txt")));
        Assert.True(File.Exists(Path.Combine(_root, ".Quartermaster.app.qm-backup-0.0.1", "version-0.0.1.txt")));
        Assert.True(File.Exists(marker));
        Assert.False(File.Exists(Path.Combine(_root, "marker", "swap-journal.json")));
    }
}
