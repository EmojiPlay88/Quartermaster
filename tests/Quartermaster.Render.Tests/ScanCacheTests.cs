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
    public void Extract_FailureRemovesTemp()
    {
        Assert.Throws<IOException>(() => ScanCache.Extract(() => throw new IOException("locked"), ".pdf"));
        Assert.Empty(Directory.GetFiles(ScanCache.Folder, "*.tmp"));
    }
}
