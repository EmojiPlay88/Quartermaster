using System.Diagnostics;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Quartermaster.Render;
using Quartermaster.Render.Update;

namespace Quartermaster.Render.Tests;

/// <summary>
/// Download, verify and stage an update from a local folder feed. The fixture "app exe" is a copy of a real assembly built by this solution
/// (Quartermaster.Render.dll), so its Win32 FileVersion is the solution's &lt;Version&gt; and the production FileVersionInfo check runs unchanged.
/// </summary>
public class UpdateInstallerTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-upd-inst-" + Guid.NewGuid().ToString("N"));
    string Feed => Path.Combine(_tmp, "feed");
    string Updates => Path.Combine(_tmp, "Updates");

    public UpdateInstallerTests()
    {
        Directory.CreateDirectory(Feed);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (Exception) { }
    }

    static readonly Version V = AppVersion.Current; // the fixture exe is stamped with this
    static string Ver => AppVersion.Text;
    static string ZipName => $"Quartermaster-{Ver}-win-x64.zip";
    static byte[] StampedExe => File.ReadAllBytes(typeof(AppVersion).Assembly.Location);

    /// <summary>Writes the zip (entries name → bytes) and its .sha256 into the feed; returns the matching UpdateInfo.</summary>
    UpdateInfo Publish(IEnumerable<(string Name, byte[] Data)> entries, Version? claim = null, string? shaOverride = null, long sizeDelta = 0)
    {
        var zipPath = Path.Combine(Feed, ZipName);
        using (var fs = File.Create(zipPath))
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            foreach (var (name, data) in entries)
            {
                using var s = z.CreateEntry(name).Open();
                s.Write(data);
            }
        var bytes = File.ReadAllBytes(zipPath);
        var hex = shaOverride ?? Convert.ToHexString(SHA256.HashData(bytes)); // upper case: the check is case-insensitive
        File.WriteAllText(Path.Combine(Feed, ZipName + ".sha256"), hex + "  " + ZipName + "\n");
        var v = claim ?? V;
        return new UpdateInfo(v, "quartermaster-v" + v, "TEST", new Uri(ZipName, UriKind.Relative), new Uri(ZipName + ".sha256", UriKind.Relative),
            bytes.Length + sizeDelta, new Uri("https://github.com/EmojiPlay88/Quartermaster/releases"));
    }

    static (string, byte[])[] GoodEntries() =>
    [
        ("Quartermaster.App.exe", StampedExe),
        ("Quartermaster.Updater.exe", [1, 2, 3]),
        ("runtimes/win-x64/native/TEST.dll", [4, 5]),
        ("readme.txt", "TEST"u8.ToArray()),
    ];

    string VersionDir => Path.Combine(Updates, Ver);

    [Fact]
    public async Task Download_GoodZip_Staged()
    {
        var u = Publish(GoodEntries());
        var progress = new List<double>();
        var staged = await UpdateInstaller.Download(new FolderFeed(Feed), u, Updates, new SyncProgress(progress.Add), default);
        Assert.Equal(V, staged.Version);
        Assert.Equal(VersionDir, staged.Root);
        Assert.Equal(Path.Combine(VersionDir, "staged"), staged.StagedDir);
        Assert.True(File.Exists(Path.Combine(staged.StagedDir, "Quartermaster.App.exe")));
        Assert.True(File.Exists(Path.Combine(staged.StagedDir, "runtimes", "win-x64", "native", "TEST.dll")));
        Assert.Equal("TEST", File.ReadAllText(Path.Combine(staged.StagedDir, "readme.txt")));
        Assert.True(File.Exists(Path.Combine(VersionDir, ZipName)));
        Assert.False(Directory.EnumerateFiles(VersionDir, "*.part").Any());
        Assert.NotEmpty(progress);
        Assert.Equal(1.0, progress[^1], 3);
    }

    [Fact]
    public async Task Download_Again_ReplacesOldStage()
    {
        var u = Publish(GoodEntries());
        var s1 = await UpdateInstaller.Download(new FolderFeed(Feed), u, Updates, null, default);
        File.WriteAllText(Path.Combine(s1.StagedDir, "stale.txt"), "x");
        var s2 = await UpdateInstaller.Download(new FolderFeed(Feed), u, Updates, null, default);
        Assert.False(File.Exists(Path.Combine(s2.StagedDir, "stale.txt")));
    }

    [Fact]
    public async Task Download_WrongHash_Refused_NoStagedDir()
    {
        var u = Publish(GoodEntries(), shaOverride: new string('a', 64));
        var ex = await Assert.ThrowsAsync<UpdateRefused>(() => UpdateInstaller.Download(new FolderFeed(Feed), u, Updates, null, default));
        Assert.Contains("SHA-256", ex.Message);
        Assert.False(Directory.Exists(VersionDir));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nothex")]
    [InlineData("abcd")]
    public async Task Download_MalformedShaFile_Refused(string sha)
    {
        var u = Publish(GoodEntries(), shaOverride: sha);
        await Assert.ThrowsAsync<UpdateRefused>(() => UpdateInstaller.Download(new FolderFeed(Feed), u, Updates, null, default));
        Assert.False(Directory.Exists(VersionDir));
    }

    [Theory]
    [InlineData(10)]   // file shorter than the release says (truncated)
    [InlineData(-10)]  // file longer than the release says
    public async Task Download_SizeMismatch_Refused(long delta)
    {
        var u = Publish(GoodEntries(), sizeDelta: delta);
        var ex = await Assert.ThrowsAsync<UpdateRefused>(() => UpdateInstaller.Download(new FolderFeed(Feed), u, Updates, null, default));
        Assert.Contains("size", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(VersionDir));
    }

    [Theory]
    [InlineData("..\\x.txt")]
    [InlineData("../x.txt")]
    [InlineData("sub/../../x.txt")]
    [InlineData("/x.txt")]
    [InlineData("\\x.txt")]
    [InlineData("C:/x.txt")]
    [InlineData("C:x.txt")]
    public async Task Download_ZipSlip_Refused(string evil)
    {
        var u = Publish(GoodEntries().Append((evil, "TEST"u8.ToArray())));
        var ex = await Assert.ThrowsAsync<UpdateRefused>(() => UpdateInstaller.Download(new FolderFeed(Feed), u, Updates, null, default));
        Assert.Contains(evil, ex.Message);
        Assert.False(Directory.Exists(VersionDir));
        Assert.False(File.Exists(Path.Combine(Updates, "x.txt")));
        Assert.False(File.Exists(Path.Combine(_tmp, "x.txt")));
    }

    [Fact]
    public async Task Download_NoExe_Refused()
    {
        var u = Publish(GoodEntries().Where(e => e.Item1 != "Quartermaster.App.exe"));
        var ex = await Assert.ThrowsAsync<UpdateRefused>(() => UpdateInstaller.Download(new FolderFeed(Feed), u, Updates, null, default));
        Assert.Contains("Quartermaster.App.exe", ex.Message);
        Assert.False(Directory.Exists(VersionDir));
    }

    [Fact]
    public async Task Download_ExeInSubfolderOnly_Refused()
    {
        var u = Publish(GoodEntries().Select(e => ("Quartermaster/" + e.Item1, e.Item2)));
        await Assert.ThrowsAsync<UpdateRefused>(() => UpdateInstaller.Download(new FolderFeed(Feed), u, Updates, null, default));
    }

    [Fact]
    public async Task Download_ExeFileVersionMismatch_Refused()
    {
        var claim = new Version(V.Major, V.Minor + 1, 0);
        var u = Publish(GoodEntries(), claim: claim);
        var ex = await Assert.ThrowsAsync<UpdateRefused>(() => UpdateInstaller.Download(new FolderFeed(Feed), u, Updates, null, default));
        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(Updates, claim.ToString(3))));
    }

    [Fact]
    public async Task Download_ExeWithoutVersionResource_Refused()
    {
        var u = Publish(GoodEntries().Select(e => e.Item1 == "Quartermaster.App.exe" ? (e.Item1, "MZ not really"u8.ToArray()) : e));
        await Assert.ThrowsAsync<UpdateRefused>(() => UpdateInstaller.Download(new FolderFeed(Feed), u, Updates, null, default));
        Assert.False(Directory.Exists(VersionDir));
    }

    [Fact]
    public async Task Download_NotAZip_Refused()
    {
        File.WriteAllText(Path.Combine(Feed, ZipName), "TEST not a zip");
        var bytes = File.ReadAllBytes(Path.Combine(Feed, ZipName));
        File.WriteAllText(Path.Combine(Feed, ZipName + ".sha256"), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var u = new UpdateInfo(V, "t", "", new Uri(ZipName, UriKind.Relative), new Uri(ZipName + ".sha256", UriKind.Relative), bytes.Length, new Uri("https://github.com/"));
        await Assert.ThrowsAsync<UpdateRefused>(() => UpdateInstaller.Download(new FolderFeed(Feed), u, Updates, null, default));
        Assert.False(Directory.Exists(VersionDir));
    }

    /// <summary>A feed whose zip stream cancels the token after the first chunk.</summary>
    sealed class CancellingFeed(FolderFeed inner, CancellationTokenSource cts) : IUpdateFeed
    {
        public Task<string?> LatestJson(CancellationToken ct) => inner.LatestJson(ct);

        public async Task<Stream> Open(Uri asset, CancellationToken ct)
        {
            var s = await inner.Open(asset, ct);
            return asset.OriginalString.EndsWith(".zip") ? new CancelAfterFirstRead(s, cts) : s;
        }
    }

    sealed class CancelAfterFirstRead(Stream s, CancellationTokenSource cts) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) { var n = s.Read(buffer, offset, Math.Min(count, 1024)); cts.Cancel(); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await s.ReadAsync(buffer[..Math.Min(buffer.Length, 1024)], CancellationToken.None);
            cts.Cancel();
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) s.Dispose(); base.Dispose(disposing); }
    }

    [Fact]
    public async Task Download_CancelledMidway_PartialFilesGone()
    {
        var u = Publish(GoodEntries());
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateInstaller.Download(new CancellingFeed(new FolderFeed(Feed), cts), u, Updates, null, cts.Token));
        Assert.False(Directory.Exists(VersionDir));
    }

    // ---------- AppFolderWritable ----------

    [Fact]
    public void AppFolderWritable_NormalDir_True_MissingDir_False()
    {
        var app = Path.Combine(_tmp, "app");
        Directory.CreateDirectory(app);
        Assert.True(UpdateInstaller.AppFolderWritable(app));
        Assert.Empty(Directory.EnumerateFileSystemEntries(app)); // probe left nothing behind
        Assert.False(UpdateInstaller.AppFolderWritable(Path.Combine(_tmp, "nope")));
    }

    [Fact]
    public void AppFolderWritable_ReadOnlyDir_False()
    {
        if (!OperatingSystem.IsWindows()) return; // ACL-based read-only folder; the app ships for Windows
        var app = Path.Combine(_tmp, "ro");
        var di = Directory.CreateDirectory(app);
        var me = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(me, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.WriteData | FileSystemRights.AppendData,
            AccessControlType.Deny);
        var acl = di.GetAccessControl();
        acl.AddAccessRule(deny);
        di.SetAccessControl(acl);
        try
        {
            Assert.False(UpdateInstaller.AppFolderWritable(app));
        }
        finally
        {
            acl = di.GetAccessControl();
            acl.RemoveAccessRule(deny);
            di.SetAccessControl(acl);
        }
    }

    // ---------- LaunchHelper ----------

    StagedUpdate FakeStaged()
    {
        var root = Path.Combine(Updates, "0.2.0");
        var staged = Path.Combine(root, "staged");
        Directory.CreateDirectory(staged);
        return new StagedUpdate(new Version(0, 2, 0), staged, root);
    }

    [Fact]
    public void LaunchHelper_MissingHelper_Refused()
    {
        var app = Directory.CreateDirectory(Path.Combine(_tmp, "app")).FullName;
        var ex = Assert.Throws<UpdateRefused>(() => UpdateInstaller.LaunchHelper(FakeStaged(), app, 1234, _ => throw new InvalidOperationException("must not start")));
        Assert.Equal("The updater helper is missing; download the new version from the release page.", ex.Message);
    }

    [Fact]
    public void LaunchHelper_CopiesHelper_StartsWithArgs_ClearsStaleMarker()
    {
        var app = Directory.CreateDirectory(Path.Combine(_tmp, "app")).FullName;
        File.WriteAllBytes(Path.Combine(app, "Quartermaster.Updater.exe"), [7, 7, 7]);
        var s = FakeStaged();
        var marker = UpdateInstaller.MarkerPath(Updates, s.Version);
        Assert.Equal(Path.Combine(Updates, "started-0.2.0"), marker);
        File.WriteAllText(marker, "stale");
        ProcessStartInfo? seen = null;
        UpdateInstaller.LaunchHelper(s, app, 4321, psi => { seen = psi; return null; });
        var helper = Path.Combine(s.Root, "helper", "Quartermaster.Updater.exe");
        Assert.Equal([7, 7, 7], File.ReadAllBytes(helper));
        Assert.NotNull(seen);
        Assert.Equal(helper, seen!.FileName);
        Assert.False(seen.UseShellExecute);
        Assert.Equal(Path.Combine(s.Root, "helper"), seen.WorkingDirectory);
        Assert.Equal(new[] { "--app", app, "--staged", s.StagedDir, "--pid", "4321", "--version", "0.2.0", "--old", AppVersion.Text, "--marker", marker }, seen.ArgumentList.ToArray());
        Assert.False(File.Exists(marker));
    }

    // ---------- CleanupOld ----------

    [Fact]
    public void CleanupOld_DeletesOtherVersionsOnly()
    {
        foreach (var d in new[] { "0.1.0", "0.2.0", "0.3.0", "notes" }) Directory.CreateDirectory(Path.Combine(Updates, d, "staged"));
        File.WriteAllText(Path.Combine(Updates, "started-0.1.0"), "");
        File.WriteAllText(Path.Combine(Updates, "started-0.2.0"), "");
        UpdateInstaller.CleanupOld(Updates, new Version(0, 2, 0));
        Assert.False(Directory.Exists(Path.Combine(Updates, "0.1.0")));
        Assert.False(Directory.Exists(Path.Combine(Updates, "0.3.0")));
        Assert.True(Directory.Exists(Path.Combine(Updates, "0.2.0")));
        Assert.True(Directory.Exists(Path.Combine(Updates, "notes"))); // not ours: left alone
        Assert.False(File.Exists(Path.Combine(Updates, "started-0.1.0")));
        UpdateInstaller.CleanupOld(Path.Combine(_tmp, "missing"), new Version(0, 2, 0)); // no folder: no throw
    }

    sealed class SyncProgress(Action<double> a) : IProgress<double>
    {
        public void Report(double value) => a(value);
    }
}
