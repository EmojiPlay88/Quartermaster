using System.Text.Json;
using System.Text.RegularExpressions;
using Quartermaster.Render.Update;

namespace Quartermaster.Render.Tests.Update;

/// <summary>Feeds the zip that CI built (build/macos/make-dmg.sh) through the real updater staging code.</summary>
public class MacBundleZipTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-maczip-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (Exception) { } }

    [Fact]
    public async Task Download_MacApp_FromRealBundleZip()
    {
        // The zip only exists on the macOS CI job (it sets QM_MAC_APP_ZIP); everywhere else, and on a plain local run, this is a no-op.
        var zip = Environment.GetEnvironmentVariable("QM_MAC_APP_ZIP");
        if (string.IsNullOrWhiteSpace(zip)) return;
        zip = Path.GetFullPath(zip);
        var zipName = Path.GetFileName(zip);
        var m = Regex.Match(zipName, @"^Quartermaster-(\d+\.\d+\.\d+)-osx-arm64\.zip$");
        Assert.True(m.Success, $"unexpected zip name {zipName}");
        var version = m.Groups[1].Value;

        var feed = Path.Combine(_tmp, "feed");
        var updates = Path.Combine(_tmp, "Updates");
        Directory.CreateDirectory(feed);
        File.Copy(zip, Path.Combine(feed, zipName));
        File.Copy(zip + ".sha256", Path.Combine(feed, zipName + ".sha256"));
        var release = new
        {
            tag_name = "quartermaster-v" + version,
            draft = false,
            prerelease = false,
            body = "TEST",
            assets = new[]
            {
                new { name = zipName, browser_download_url = zipName, size = new FileInfo(zip).Length },
                new { name = zipName + ".sha256", browser_download_url = zipName + ".sha256", size = new FileInfo(zip + ".sha256").Length },
            },
        };
        var releaseJson = JsonSerializer.Serialize(release);
        File.WriteAllText(Path.Combine(feed, "release.json"), releaseJson);

        var info = UpdateChecker.Parse(releaseJson, PackageKind.MacApp);
        Assert.NotNull(info);
        var staged = await UpdateInstaller.Download(new FolderFeed(feed), info!, updates, null, default);

        Assert.EndsWith("Quartermaster.app", staged.StagedDir);
        var macos = Path.Combine(staged.StagedDir, "Contents", "MacOS");
        if (!OperatingSystem.IsWindows())
            foreach (var exe in new[] { "Quartermaster", "Quartermaster.Updater" })
                Assert.True(File.GetUnixFileMode(Path.Combine(macos, exe)).HasFlag(UnixFileMode.UserExecute), exe + " lost its execute bit");
        var pkg = PackageFile.Read(macos);
        Assert.NotNull(pkg);
        Assert.Equal(PackageKind.MacApp, pkg!.Kind);
        Assert.Equal(Version.Parse(version), pkg.Version);
    }
}
