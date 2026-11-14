using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Quartermaster.Desktop.Tests;

/// <summary>Publishes the Desktop app once for the current OS's RID into a temp folder (slow: a real `dotnet publish`) and checks what self-update needs.</summary>
public sealed class PublishFixture : IDisposable
{
    public string Rid { get; }
    public string Dir { get; }
    public string RepoRoot { get; }
    public string Version { get; }
    public string HelperName => OperatingSystem.IsWindows() ? "Quartermaster.Updater.exe" : "Quartermaster.Updater";

    public PublishFixture()
    {
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        Rid = (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" + arch;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Quartermaster.slnx"))) d = d.Parent;
        RepoRoot = d?.FullName ?? throw new InvalidOperationException("repo root not found");
        var props = File.ReadAllText(Path.Combine(RepoRoot, "Directory.Build.props"));
        Version = System.Text.RegularExpressions.Regex.Match(props, "<Version>([^<]+)</Version>").Groups[1].Value;
        Dir = Path.Combine(Path.GetTempPath(), "qm-publish-" + Guid.NewGuid().ToString("N"));
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "publish", Path.Combine(RepoRoot, "src", "Quartermaster.Desktop"), "-c", "Release", "-r", Rid, "--self-contained", "-o", Dir, "--nologo", "--disable-build-servers", "-nodeReuse:false" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        // Lingering build-server children can keep the pipes open after publish exits, so wait on the process, not on pipe EOF.
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("dotnet publish failed: " + Tail(output) + Tail(err));
    }

    static string Tail(Task<string> t) => t.Wait(10000) ? t.Result : "";

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch { }
    }
}

[Trait("Category", "Publish")]
public class PublishLayoutTests : IClassFixture<PublishFixture>
{
    private readonly PublishFixture _f;
    public PublishLayoutTests(PublishFixture f) => _f = f;

    [Fact]
    public void Publish_HasHelperMarkerAndFileList()
    {
        Assert.True(File.Exists(Path.Combine(_f.Dir, _f.HelperName)), "helper missing");
        Assert.True(File.Exists(Path.Combine(_f.Dir, "quartermaster-package.json")));
        Assert.True(File.Exists(Path.Combine(_f.Dir, "quartermaster-files.txt")));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_f.Dir, "quartermaster-package.json")));
        Assert.Equal(_f.Version, doc.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void Publish_FileListMatchesFolder()
    {
        var listed = File.ReadAllLines(Path.Combine(_f.Dir, "quartermaster-files.txt")).Where(l => l.Length > 0).ToList();
        var actual = Directory.GetFiles(_f.Dir, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(_f.Dir, p).Replace(Path.DirectorySeparatorChar, '/')).ToList();
        Assert.DoesNotContain(listed, l => l.Contains((char)92));
        Assert.Contains("quartermaster-files.txt", listed);
        Assert.Equal(actual.OrderBy(x => x, StringComparer.Ordinal), listed.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal("quartermaster-files.txt", listed[^1]);
    }

    [Fact]
    public void Publish_MarkerKindPerRid()
    {
        var expected = _f.Rid.StartsWith("win") ? "win-zip" : _f.Rid.StartsWith("osx") ? "mac-app" : "linux-tar";
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_f.Dir, "quartermaster-package.json")));
        Assert.Equal(expected, doc.RootElement.GetProperty("package").GetString());
    }

    [Fact]
    public void Publish_Win_HasAppHostCopy()
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = Path.Combine(_f.Dir, "Quartermaster.exe");
        var copy = Path.Combine(_f.Dir, "Quartermaster.App.exe");
        Assert.True(File.Exists(copy));
        Assert.Equal(File.ReadAllBytes(host), File.ReadAllBytes(copy));

        var v = System.Version.Parse(_f.Version);
        var fvi = FileVersionInfo.GetVersionInfo(copy);
        Assert.Equal((v.Major, v.Minor, v.Build), (fvi.FileMajorPart, fvi.FileMinorPart, fvi.FileBuildPart));

        var psi = new ProcessStartInfo(copy, "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(30000));
        Assert.Equal(0, p.ExitCode);
        Assert.Equal("Quartermaster " + _f.Version, text.Trim());
    }
}
