using System.Text.Json;
using System.Text.RegularExpressions;
using Quartermaster.Render.Update;

namespace Quartermaster.Render.Tests.Update;

/// <summary>Feeds the Windows zip and the Linux tar.gz that CI built through the real updater staging code (extract, marker, file list).</summary>
public class PackageArchiveTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-pkgarchive-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (Exception) { } }

    [Fact]
    public async Task Download_WinZip_FromRealPackage()
    {
        // The zip only exists on the Windows CI job (it sets QM_WIN_ZIP); everywhere else, and on a plain local run, this is a no-op.
        var zip = Environment.GetEnvironmentVariable("QM_WIN_ZIP");
        if (string.IsNullOrWhiteSpace(zip)) return;
        // The zip serves both the portable and the installed layout: stage it as each.
        foreach (var kind in new[] { PackageKind.WinZip, PackageKind.WinSetup })
        {
            var staged = await Stage(zip, @"^Quartermaster-(\d+\.\d+\.\d+)-win-x64\.zip$", kind, kind.ToString());
            Assert.True(File.Exists(Path.Combine(staged.Dir, "Quartermaster.exe")), "Quartermaster.exe missing from the staged update");
            Assert.True(File.Exists(Path.Combine(staged.Dir, "Quartermaster.App.exe")), "Quartermaster.App.exe missing from the staged update");
            var pkg = PackageFile.Read(staged.Dir);
            Assert.NotNull(pkg);
            Assert.Equal(PackageKind.WinZip, pkg!.Kind);
            Assert.Equal(Version.Parse(staged.Version), pkg.Version);
        }
    }

    [Fact]
    public async Task Download_LinuxTar_FromRealPackage()
    {
        // The tar.gz only exists on the Linux CI job (it sets QM_LINUX_TAR); everywhere else, and on a plain local run, this is a no-op.
        var tar = Environment.GetEnvironmentVariable("QM_LINUX_TAR");
        if (string.IsNullOrWhiteSpace(tar)) return;
        var staged = await Stage(tar, @"^Quartermaster-(\d+\.\d+\.\d+)-linux-x64\.tar\.gz$", PackageKind.LinuxTar, "tar");
        var pkg = PackageFile.Read(staged.Dir);
        Assert.NotNull(pkg);
        Assert.Equal(PackageKind.LinuxTar, pkg!.Kind);
        Assert.Equal(Version.Parse(staged.Version), pkg.Version);
        if (!OperatingSystem.IsWindows())
            foreach (var exe in new[] { "Quartermaster", "Quartermaster.Updater" })
                Assert.True(File.GetUnixFileMode(Path.Combine(staged.Dir, exe)).HasFlag(UnixFileMode.UserExecute), exe + " lost its execute bit");
    }

    async Task<(string Dir, string Version)> Stage(string archive, string namePattern, PackageKind kind, string tag)
    {
        archive = Path.GetFullPath(archive);
        var name = Path.GetFileName(archive);
        var m = Regex.Match(name, namePattern);
        Assert.True(m.Success, $"unexpected package name {name}");
        var version = m.Groups[1].Value;

        var feed = Path.Combine(_tmp, tag, "feed");
        var updates = Path.Combine(_tmp, tag, "Updates");
        Directory.CreateDirectory(feed);
        File.Copy(archive, Path.Combine(feed, name));
        File.Copy(archive + ".sha256", Path.Combine(feed, name + ".sha256"));
        var release = new
        {
            tag_name = "quartermaster-v" + version,
            draft = false,
            prerelease = false,
            body = "TEST",
            assets = new[]
            {
                new { name, browser_download_url = name, size = new FileInfo(archive).Length },
                new { name = name + ".sha256", browser_download_url = name + ".sha256", size = new FileInfo(archive + ".sha256").Length },
            },
        };
        var releaseJson = JsonSerializer.Serialize(release);
        File.WriteAllText(Path.Combine(feed, "release.json"), releaseJson);

        var info = UpdateChecker.Parse(releaseJson, kind);
        Assert.NotNull(info);
        // Download runs the real UpdateStaging: safe extract, marker kind and version, the app executable and the file list.
        var staged = await UpdateInstaller.Download(new FolderFeed(feed), info!, updates, null, default);
        Assert.True(File.Exists(Path.Combine(staged.StagedDir, UpdateInstaller.ManifestName)), "file list missing from the staged update");
        return (staged.StagedDir, version);
    }
}
