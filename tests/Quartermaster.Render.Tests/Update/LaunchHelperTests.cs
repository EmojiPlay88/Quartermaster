using System.ComponentModel;
using System.Diagnostics;
using Quartermaster.Render;
using Quartermaster.Render.Update;

namespace Quartermaster.Render.Tests.Update;

/// <summary>Stage 6a task 6: the helper hand-over per install layout, the running-copies check over several exe names, the deb/rpm hand-off.</summary>
public class LaunchHelperTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-launch-" + Guid.NewGuid().ToString("N"));
    string Updates => Path.Combine(_tmp, "Updates");
    static readonly Version V = new(0, 2, 0);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (Exception) { } }

    StagedUpdate Staged()
    {
        var root = Path.Combine(Updates, "0.2.0");
        var staged = Path.Combine(root, "staged");
        Directory.CreateDirectory(staged);
        return new StagedUpdate(V, staged, root);
    }

    string App(string sub, bool helper = true, bool manifest = true)
    {
        var app = Directory.CreateDirectory(Path.Combine(_tmp, sub)).FullName;
        if (helper) File.WriteAllBytes(Path.Combine(app, UpdateInstaller.HelperName), [7, 7, 7]);
        if (manifest) File.WriteAllText(Path.Combine(app, UpdateInstaller.ManifestName), "TEST\n");
        return app;
    }

    static (ProcessStartInfo Psi, string[] Args) Launch(StagedUpdate s, InstallLayout l)
    {
        ProcessStartInfo? seen = null;
        UpdateInstaller.LaunchHelper(s, l, 4321, psi => { seen = psi; return null; });
        Assert.NotNull(seen);
        return (seen!, seen!.ArgumentList.ToArray());
    }

    string[] BaseArgs(string app, StagedUpdate s) =>
        ["--app", app, "--staged", s.StagedDir, "--pid", "4321", "--version", "0.2.0", "--old", AppVersion.Text, "--marker", UpdateInstaller.MarkerPath(Updates, V)];

    static Func<ProcessStartInfo, Process?> MustNotStart => _ => throw new InvalidOperationException("must not start");

    [Fact]
    public void HelperName_FollowsOs()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? "Quartermaster.Updater.exe" : "Quartermaster.Updater", UpdateInstaller.HelperName);
    }

    [Fact]
    public void WinZip_PassesWindowsExe_CopiesHelper_ClearsMarker()
    {
        var app = App("win");
        var s = Staged();
        var marker = UpdateInstaller.MarkerPath(Updates, V);
        File.WriteAllText(marker, "stale");
        var (psi, args) = Launch(s, new InstallLayout(PackageKind.WinZip, V, app, null, Path.Combine(app, "Quartermaster.exe")));
        var helper = Path.Combine(s.Root, "helper", UpdateInstaller.HelperName);
        Assert.Equal([7, 7, 7], File.ReadAllBytes(helper));
        Assert.Equal(helper, psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(Path.Combine(s.Root, "helper"), psi.WorkingDirectory);
        Assert.Equal([.. BaseArgs(app, s), "--exe", "Quartermaster.exe"], args);
        Assert.False(File.Exists(marker));
        if (!OperatingSystem.IsWindows())
            Assert.Equal((UnixFileMode)0x1ED, File.GetUnixFileMode(helper) & (UnixFileMode)0x1FF);
    }

    [Fact]
    public void LinuxTar_PassesPlainExe()
    {
        var app = App("tar");
        var s = Staged();
        var (_, args) = Launch(s, new InstallLayout(PackageKind.LinuxTar, V, app, null, Path.Combine(app, "Quartermaster")));
        Assert.Equal([.. BaseArgs(app, s), "--exe", "Quartermaster"], args);
    }

    [Fact]
    public void MacApp_SwapsTheBundle()
    {
        var macos = App(Path.Combine("Applications", "Quartermaster.app", "Contents", "MacOS"));
        var bundle = Path.Combine(_tmp, "Applications", "Quartermaster.app");
        var s = Staged();
        s = s with { StagedDir = Path.Combine(s.StagedDir, "Quartermaster.app") };
        var (_, args) = Launch(s, new InstallLayout(PackageKind.MacApp, V, macos, bundle, Path.Combine(macos, "Quartermaster")));
        Assert.Equal([.. BaseArgs(Path.Combine(_tmp, "Applications"), s), "--exe", "Quartermaster.app/Contents/MacOS/Quartermaster", "--unit", bundle], args);
    }

    [Fact]
    public void MacApp_WithoutFileList_Refused()
    {
        var macos = App(Path.Combine("A", "Quartermaster.app", "Contents", "MacOS"), manifest: false);
        var bundle = Path.Combine(_tmp, "A", "Quartermaster.app");
        var l = new InstallLayout(PackageKind.MacApp, V, macos, bundle, Path.Combine(macos, "Quartermaster"));
        var ex = Assert.Throws<UpdateRefused>(() => UpdateInstaller.LaunchHelper(Staged(), l, 1, MustNotStart));
        Assert.Equal(UpdateInstaller.NoManifestText, ex.Message);
    }

    [Fact]
    public void AppImage_SwapsTheFile_NoFileListNeeded()
    {
        var mount = App("mount", manifest: false);   // the mounted AppImage folder holds the helper
        var home = Directory.CreateDirectory(Path.Combine(_tmp, "home")).FullName;
        var img = Path.Combine(home, "Quartermaster-TEST.AppImage");
        File.WriteAllText(img, "TEST");
        var s = Staged();
        s = s with { StagedDir = Path.Combine(s.StagedDir, "Quartermaster.AppImage") };
        var (_, args) = Launch(s, new InstallLayout(PackageKind.LinuxAppImage, V, mount, img, img));
        Assert.Equal([.. BaseArgs(home, s), "--exe", "Quartermaster-TEST.AppImage", "--unit", img], args);
    }

    [Fact]
    public void Deb_IsNotSelfUpdating_Refused()
    {
        var app = App("deb");
        var l = new InstallLayout(PackageKind.LinuxDeb, V, app, null, Path.Combine(app, "Quartermaster"));
        Assert.Throws<UpdateRefused>(() => UpdateInstaller.LaunchHelper(Staged(), l, 1, MustNotStart));
        Assert.False(Directory.Exists(Path.Combine(Updates, "0.2.0", "helper")));
    }

    [Fact]
    public void HelperMissing_Refused()
    {
        var app = App("nohelper", helper: false);
        var l = new InstallLayout(PackageKind.WinZip, V, app, null, Path.Combine(app, "Quartermaster.exe"));
        var ex = Assert.Throws<UpdateRefused>(() => UpdateInstaller.LaunchHelper(Staged(), l, 1, MustNotStart));
        Assert.Equal("The updater helper is missing; download the new version from the release page.", ex.Message);
    }

    [Fact]
    public void RunningCopies_EitherExeName()
    {
        var app = Path.Combine(_tmp, "app");
        var exe = Path.Combine(app, "Quartermaster.exe");
        var classic = Path.Combine(app, "Quartermaster.App.exe");
        var asked = new List<string>();
        IEnumerable<(int, string?)> Lister(string name)
        {
            asked.Add(name);
            return name == "Quartermaster.App" ? [(7, classic)] : [(1, exe), (8, null)];
        }
        Assert.True(RunningCopies.OthersRunning([exe, classic], 1, Lister));
        Assert.Contains("Quartermaster", asked);
        Assert.Contains("Quartermaster.App", asked);
        Assert.False(RunningCopies.OthersRunning([exe], 1, Lister));          // only this process under that name
        Assert.True(RunningCopies.OthersRunning(classic, 1, Lister));         // the single-exe overload still works
        Assert.False(RunningCopies.OthersRunning([Path.Combine(_tmp, "elsewhere", "Quartermaster.exe")], 1, _ => [(2, exe)]));
    }

    [Fact]
    public void PackageHandOff_RunsXdgOpenWithThePath()
    {
        var path = Path.Combine(_tmp, "a b", "quartermaster_0.2.0_amd64.deb");
        ProcessStartInfo? seen = null;
        Assert.True(PackageHandOff.Open(path, psi => { seen = psi; return null; }));
        Assert.Equal("xdg-open", seen!.FileName);
        Assert.False(seen.UseShellExecute);
        Assert.Equal([path], seen.ArgumentList.ToArray());
        Assert.False(PackageHandOff.Open(path, _ => throw new Win32Exception(2, "TEST no xdg-open")));
    }

    [Fact]
    public void PackageHandOff_DropsAppImageVariables()
    {
        string[] vars = ["APPIMAGE", "APPDIR", "ARGV0", "OWD", "QM_TEST_KEEP"];
        var saved = vars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var v in vars) Environment.SetEnvironmentVariable(v, "x");
            ProcessStartInfo? seen = null;
            Assert.True(PackageHandOff.Open("p.deb", psi => { seen = psi; return null; }));
            foreach (var v in new[] { "APPIMAGE", "APPDIR", "ARGV0", "OWD" }) Assert.False(seen!.Environment.ContainsKey(v), v);
            Assert.Equal("x", seen!.Environment["QM_TEST_KEEP"]);
        }
        finally { foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v); }
    }

    /// <summary>A real, windowless process that exits at once with <paramref name="code"/>.</summary>
    internal static Process ExitWith(int code) => Process.Start(OperatingSystem.IsWindows()
        ? new ProcessStartInfo("cmd.exe", $"/c exit {code}") { UseShellExecute = false, CreateNoWindow = true }
        : new ProcessStartInfo("/bin/sh", ["-c", $"exit {code}"]) { UseShellExecute = false, CreateNoWindow = true })!;

    [Fact]
    public void PackageHandOff_NonZeroExit_IsFailure()
    {
        Assert.False(PackageHandOff.Open("p.deb", _ => ExitWith(3)));   // e.g. no handler for the package type
        Assert.True(PackageHandOff.Open("p.deb", _ => ExitWith(0)));
    }
}
