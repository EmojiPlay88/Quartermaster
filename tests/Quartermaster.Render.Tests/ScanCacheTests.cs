using System.Text;
using Quartermaster.Render.Ocr;

namespace Quartermaster.Render.Tests;

[Collection(AppDataRootEnv.Name)]
public class ScanCacheTests : IDisposable
{
    private readonly string? _oldRoot = Environment.GetEnvironmentVariable(AppDataRoot.EnvVar);
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-scancache-" + Guid.NewGuid().ToString("N"));

    public ScanCacheTests() => Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _tmp);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _oldRoot);
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    [Fact]
    public void Extract_NamesBySha_ReusesAndLeavesNoTemps()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.4 invented");
        var a = ScanCache.Extract(() => new MemoryStream(bytes), ".PDF");
        var b = ScanCache.Extract(() => new MemoryStream(bytes), "pdf");
        Assert.Equal(a, b);
        Assert.StartsWith(Path.Combine(_tmp, "scan-cache"), a);
        Assert.EndsWith(".pdf", a);
        Assert.Equal(64 + 4, Path.GetFileName(a).Length);
        Assert.Equal(bytes, File.ReadAllBytes(a));
        var other = ScanCache.Extract(() => new MemoryStream(Encoding.ASCII.GetBytes("other")), ".png");
        Assert.NotEqual(a, other);
        Assert.Empty(Directory.GetFiles(ScanCache.Folder, "*.tmp"));
    }

    [Fact]
    public void Prune_DropsOldFiles_ThenOldestUntilUnderCap()
    {
        Directory.CreateDirectory(ScanCache.Folder);
        var now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        string Make(string name, int bytes, int daysAgo)
        {
            var p = Path.Combine(ScanCache.Folder, name);
            File.WriteAllBytes(p, new byte[bytes]);
            var t = now.AddDays(-daysAgo);
            File.SetLastWriteTimeUtc(p, t);
            File.SetLastAccessTimeUtc(p, t);
            return p;
        }
        var stale = Make("a.pdf", 10, 40);
        var tmp = Make("x.tmp", 10, 2);
        var oldest = Make("b.pdf", 600, 20);
        var newer = Make("c.png", 600, 1);
        var deleted = ScanCache.Prune(now, TimeSpan.FromDays(30), 1000);
        Assert.Equal(3, deleted);
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(tmp));
        Assert.False(File.Exists(oldest)); // over the cap: least recently used goes first
        Assert.True(File.Exists(newer));
    }

    [Fact]
    public void Extract_FailureRemovesTemp()
    {
        Assert.Throws<IOException>(() => ScanCache.Extract(() => throw new IOException("locked"), ".pdf"));
        Assert.Empty(Directory.GetFiles(ScanCache.Folder, "*.tmp"));
    }
}
