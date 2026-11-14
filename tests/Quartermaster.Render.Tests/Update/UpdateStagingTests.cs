using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Quartermaster.Render;
using Quartermaster.Render.Update;

namespace Quartermaster.Render.Tests.Update;

/// <summary>Staging per package kind from a local folder feed. Unix-only checks return early on Windows (the repo's skip pattern).</summary>
public class UpdateStagingTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-upd-stg-" + Guid.NewGuid().ToString("N"));
    string Feed => Path.Combine(_tmp, "feed");
    string Updates => Path.Combine(_tmp, "Updates");
    static readonly Version V = new(0, 2, 0);
    string VersionDir => Path.Combine(Updates, "0.2.0");
    static readonly byte[] Manifest = "quartermaster-files.txt\n"u8.ToArray();
    const UnixFileMode Rwxr = (UnixFileMode)0x1ED; // 0755
    const UnixFileMode Rw = (UnixFileMode)0x1A4;   // 0644

    public UpdateStagingTests() => Directory.CreateDirectory(Feed);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (Exception) { } }

    UpdateInfo PublishBytes(PackageKind kind, byte[] bytes, Version? version = null)
    {
        var v = version ?? V;
        var name = UpdateChecker.AssetName(v, kind);
        File.WriteAllBytes(Path.Combine(Feed, name), bytes);
        File.WriteAllText(Path.Combine(Feed, name + ".sha256"), Convert.ToHexString(SHA256.HashData(bytes)) + "  " + name + "\n");
        return new UpdateInfo(v, "quartermaster-v" + v, "TEST", new Uri(name, UriKind.Relative), new Uri(name + ".sha256", UriKind.Relative),
            bytes.Length, new Uri("https://github.com/EmojiPlay88/Quartermaster/releases"), kind, name);
    }

    static byte[] Zip(IEnumerable<(string Name, byte[] Data)> entries, Action<ZipArchiveEntry>? tweak = null)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
            {
                var e = z.CreateEntry(name);
                using (var s = e.Open()) s.Write(data);
                tweak?.Invoke(e);
            }
        return ms.ToArray();
    }

    static byte[] Tar(Action<TarWriter> fill)
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tw = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: false))
            fill(tw);
        return ms.ToArray();
    }

    static void AddFile(TarWriter tw, string name, byte[] data, UnixFileMode mode = Rw) =>
        tw.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data), Mode = mode });

    static byte[] Marker(string version, string kind) => System.Text.Encoding.UTF8.GetBytes($"{{\"version\":\"{version}\",\"package\":\"{kind}\"}}");

    static byte[] GoodTar(string version = "0.2.0") => Tar(tw =>
    {
        AddFile(tw, "quartermaster-package.json", Marker(version, "linux-tar"));
        AddFile(tw, "quartermaster-files.txt", Manifest);
        AddFile(tw, "Quartermaster.App", [1, 2, 3], Rwxr);
    });

    Task<StagedUpdate> Run(UpdateInfo u) => UpdateInstaller.Download(new FolderFeed(Feed), u, Updates, null, default);

    [Fact]
    public async Task Download_LinuxTar_Stages()
    {
        var s = await Run(PublishBytes(PackageKind.LinuxTar, GoodTar()));
        Assert.Equal(Path.Combine(VersionDir, "staged"), s.StagedDir);
        Assert.True(File.Exists(Path.Combine(s.StagedDir, "Quartermaster.App")));
        Assert.True(File.Exists(Path.Combine(s.StagedDir, "quartermaster-files.txt")));
        Assert.True(File.Exists(Path.Combine(VersionDir, UpdateChecker.AssetName(V, PackageKind.LinuxTar))));
    }

    [Fact]
    public async Task Download_LinuxTar_VersionMismatch_Refused()
    {
        var u = PublishBytes(PackageKind.LinuxTar, GoodTar("0.1.9"));
        var ex = await Assert.ThrowsAsync<UpdateRefused>(() => Run(u));
        Assert.Equal("The update is version 0.1.9, but the release is 0.2.0; it was not installed.", ex.Message);
        Assert.False(Directory.Exists(VersionDir));
    }

    [Fact]
    public async Task Download_LinuxTar_NoMarker_Refused()
    {
        var u = PublishBytes(PackageKind.LinuxTar, Tar(tw => AddFile(tw, "quartermaster-files.txt", Manifest)));
        var ex = await Assert.ThrowsAsync<UpdateRefused>(() => Run(u));
        Assert.Equal("The update has no package marker (quartermaster-package.json); it was not installed.", ex.Message);
        Assert.False(Directory.Exists(VersionDir));
    }

    [Fact]
    public async Task Extract_TarGz_SymlinkEntry_Refused()
    {
        var u = PublishBytes(PackageKind.LinuxTar, Tar(tw =>
        {
            AddFile(tw, "quartermaster-package.json", Marker("0.2.0", "linux-tar"));
            tw.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "evil") { LinkName = "/etc/passwd" });
        }));
        var ex = await Assert.ThrowsAsync<UpdateRefused>(() => Run(u));
        Assert.Equal("The update contains a link or special file ('evil'); it was not installed.", ex.Message);
        Assert.False(Directory.Exists(VersionDir));
    }

    [Fact]
    public async Task Extract_TarGz_DotDot_Refused()
    {
        var u = PublishBytes(PackageKind.LinuxTar, Tar(tw => AddFile(tw, "../evil.txt", [1])));
        var ex = await Assert.ThrowsAsync<UpdateRefused>(() => Run(u));
        Assert.Contains("unsafe file path", ex.Message);
        Assert.False(File.Exists(Path.Combine(VersionDir, "evil.txt")));
        Assert.False(Directory.Exists(VersionDir));
    }

    [Fact]
    public async Task Extract_TarGz_HardLinkEntry_Refused()
    {
        var u = PublishBytes(PackageKind.LinuxTar, Tar(tw =>
        {
            AddFile(tw, "quartermaster-package.json", Marker("0.2.0", "linux-tar"));
            tw.WriteEntry(new PaxTarEntry(TarEntryType.HardLink, "hard") { LinkName = "quartermaster-package.json" });
        }));
        var ex = await Assert.ThrowsAsync<UpdateRefused>(() => Run(u));
        Assert.Equal("The update contains a link or special file ('hard'); it was not installed.", ex.Message);
        Assert.False(Directory.Exists(VersionDir));
    }

    [Fact]
    public async Task Extract_TarGz_AbsolutePath_Refused()
    {
        var u = PublishBytes(PackageKind.LinuxTar, Tar(tw => AddFile(tw, "/etc/evil.txt", [1])));
        var ex = await Assert.ThrowsAsync<UpdateRefused>(() => Run(u));
        Assert.Equal("The update contains an unsafe file path ('/etc/evil.txt'); it was not installed.", ex.Message);
        Assert.False(Directory.Exists(VersionDir));
    }

    [Fact]
    public async Task Extract_TarGz_DuplicateEntry_Refused()
    {
        var u = PublishBytes(PackageKind.LinuxTar, Tar(tw =>
        {
            AddFile(tw, "quartermaster-package.json", Marker("0.2.0", "linux-tar"));
            AddFile(tw, "quartermaster-files.txt", Manifest);
            AddFile(tw, "Quartermaster.App", [1]);
            AddFile(tw, "Quartermaster.App", [2]);
        }));
        var ex = await Assert.ThrowsAsync<UpdateRefused>(() => Run(u));
        Assert.Equal("The update lists 'Quartermaster.App' twice; it was not installed.", ex.Message);
        Assert.False(Directory.Exists(VersionDir));
    }

    [Fact]
    public async Task Extract_TarGz_KeepsExecutableMode()
    {
        if (OperatingSystem.IsWindows()) return;
        var s = await Run(PublishBytes(PackageKind.LinuxTar, GoodTar()));
        Assert.Equal(Rwxr, File.GetUnixFileMode(Path.Combine(s.StagedDir, "Quartermaster.App")));
    }

    static (string, byte[])[] MacEntries(string kind) =>
    [
        ("Quartermaster.app/Contents/MacOS/quartermaster-package.json", Marker("0.2.0", kind)),
        ("Quartermaster.app/Contents/MacOS/quartermaster-files.txt", Manifest),
        ("Quartermaster.app/Contents/MacOS/Quartermaster.App", [1, 2]),
    ];

    [Fact]
    public async Task Extract_Zip_KeepsExecutableMode()
    {
        if (OperatingSystem.IsWindows()) return;
        var zip = Zip(MacEntries("mac-app"), e => e.ExternalAttributes = (int)Rwxr << 16);
        var s = await Run(PublishBytes(PackageKind.MacApp, zip));
        Assert.Equal(Rwxr, File.GetUnixFileMode(Path.Combine(s.StagedDir, "Contents", "MacOS", "Quartermaster.App")));
    }

    [Fact]
    public async Task Download_MacApp_StagedDirIsBundle()
    {
        var s = await Run(PublishBytes(PackageKind.MacApp, Zip(MacEntries("mac-app"))));
        Assert.Equal(Path.Combine(VersionDir, "staged", "Quartermaster.app"), s.StagedDir);
        Assert.True(File.Exists(Path.Combine(s.StagedDir, "Contents", "MacOS", "Quartermaster.App")));
    }

    [Fact]
    public async Task Download_MacApp_WrongKind_Refused()
    {
        var u = PublishBytes(PackageKind.MacApp, Zip(MacEntries("linux-tar")));
        var ex = await Assert.ThrowsAsync<UpdateRefused>(() => Run(u));
        Assert.Equal("The update is a linux-tar package, but this copy is mac-app; it was not installed.", ex.Message);
        Assert.False(Directory.Exists(VersionDir));
    }

    [Fact]
    public async Task Download_AppImage_SavedExecutable()
    {
        var s = await Run(PublishBytes(PackageKind.LinuxAppImage, [7, 7, 7]));
        Assert.Equal(Path.Combine(VersionDir, "staged", "Quartermaster.AppImage"), s.StagedDir);
        Assert.Equal(new byte[] { 7, 7, 7 }, File.ReadAllBytes(s.StagedDir));
        if (!OperatingSystem.IsWindows()) Assert.Equal(Rwxr, File.GetUnixFileMode(s.StagedDir));
    }

    [Fact]
    public async Task Download_Deb_KeptAsFile()
    {
        var s = await Run(PublishBytes(PackageKind.LinuxDeb, [9, 9]));
        Assert.Equal(Path.Combine(VersionDir, UpdateChecker.AssetName(V, PackageKind.LinuxDeb)), s.StagedDir);
        Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(s.StagedDir));
    }

    [Fact]
    public async Task Download_WinZip_MarkerVersionUsedWhenPresent()
    {
        // The "exe" is not a real executable: with a marker, its FileVersion is not consulted.
        (string, byte[])[] Entries(string v) =>
        [
            ("Quartermaster.App.exe", [1, 2, 3]),
            ("quartermaster-package.json", Marker(v, "win-zip")),
            ("quartermaster-files.txt", Manifest),
        ];
        var s = await Run(PublishBytes(PackageKind.WinZip, Zip(Entries("0.2.0"))));
        Assert.Equal(Path.Combine(VersionDir, "staged"), s.StagedDir);
        var ex = await Assert.ThrowsAsync<UpdateRefused>(() => Run(PublishBytes(PackageKind.WinZip, Zip(Entries("0.1.0")))));
        Assert.Contains("is version 0.1.0, but the release is 0.2.0", ex.Message);
    }

    [Fact]
    public async Task Download_WinZip_MarkerWithoutAppExe_Refused()
    {
        // The 0.1.0 switch-over and the WinForms restart need Quartermaster.App.exe even when the marker is fine.
        var zip = Zip(
        [
            ("Quartermaster.exe", [1, 2, 3]),
            ("quartermaster-package.json", Marker("0.2.0", "win-zip")),
            ("quartermaster-files.txt", Manifest),
        ]);
        var ex = await Assert.ThrowsAsync<UpdateRefused>(() => Run(PublishBytes(PackageKind.WinZip, zip)));
        Assert.Equal("The update does not contain Quartermaster.App.exe; it was not installed.", ex.Message);
        Assert.False(Directory.Exists(VersionDir));
    }

    [Fact]
    public async Task Download_WinZip_NoMarker_FileVersionStillChecked()
    {
        if (!OperatingSystem.IsWindows()) return;
        var zip = Zip(
        [
            ("Quartermaster.App.exe", File.ReadAllBytes(typeof(AppVersion).Assembly.Location)),
            ("quartermaster-files.txt", Manifest),
        ]);
        // The exe carries the solution's version; the release claims another one.
        var ex = await Assert.ThrowsAsync<UpdateRefused>(() => Run(PublishBytes(PackageKind.WinZip, zip, new Version(9, 9, 9))));
        Assert.Contains("Quartermaster.App.exe in the update is version", ex.Message);
        await Run(PublishBytes(PackageKind.WinZip, zip, AppVersion.Current));
    }
}
