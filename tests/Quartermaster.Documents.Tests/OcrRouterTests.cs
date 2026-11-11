using System.Net;
using Quartermaster.Render.Ocr;
using SkiaSharp;

namespace Quartermaster.Documents.Tests;

/// <summary>
/// A temp downloads folder and a fake catalog whose "deu" entry is the bundled English data under another code (same bytes and SHA-256),
/// so a real Tesseract read works without a real download. The stub handler serves that file; nothing touches the network.
/// </summary>
internal sealed class OcrDownloadsFixture : IDisposable
{
    public static readonly string BundledEng = Path.Combine(AppContext.BaseDirectory, "tessdata", "eng.traineddata");
    public const string Code = "deu";

    readonly string _root = Path.Combine(Path.GetTempPath(), "qm-ocrdl-" + Guid.NewGuid().ToString("N"));
    public string Folder => Path.Combine(_root, "OcrLanguages");
    public string Root => _root;
    public readonly OcrLanguageEntry[] Catalog;

    public OcrDownloadsFixture()
    {
        Directory.CreateDirectory(Folder);
        var eng = OcrLanguageCatalog.Find("eng")!;
        Catalog = [eng, new OcrLanguageEntry(Code, "German", eng.Bytes, eng.Sha256, false)];
    }

    public OcrLanguageService Service() => new(Folder, new Serve(), null, Catalog);

    /// <summary>Puts a verified copy in the downloads folder directly (as if downloaded in an earlier session).</summary>
    public void Install(string code = Code) => File.Copy(BundledEng, Path.Combine(Folder, code + ".traineddata"), true);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    sealed class Serve : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(BundledEng)) });
    }
}

public class OcrRouterTests : IDisposable
{
    readonly OcrDownloadsFixture _fx = new();
    public void Dispose() => _fx.Dispose();

    sealed class FakeWindows(params string[] languages) : IOcrRecognizer, IDisposable
    {
        public readonly List<string> Reads = [];
        public bool Disposed;
        public string Name => "Windows";
        public int MaxImageDimension => 2600;
        public IReadOnlyList<string> Languages => languages;
        public string? DefaultLanguage => languages.LastOrDefault();
        public string UnavailableMessage => "No Windows OCR language.";
        public Task<IReadOnlyList<OcrWord>> RecognizeAsync(SKBitmap gray, OcrLayout layout, string language, CancellationToken ct)
        {
            lock (Reads) Reads.Add(language);
            return Task.FromResult<IReadOnlyList<OcrWord>>([new OcrWord("win", 0, 0, 1, 1)]);
        }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task Windows_language_goes_to_windows_downloaded_to_tesseract()
    {
        _fx.Install();
        using var svc = _fx.Service();
        var win = new FakeWindows("en-US", "de-DE");
        var router = new OcrRouter(win, new TesseractRecognizer(null, svc));
        Assert.Equal("Windows", router.Name);
        Assert.Equal("de-DE", router.DefaultLanguage);
        Assert.Equal(2600, router.MaxImageDimension);
        Assert.Equal(["en-US", "de-DE", "deu"], router.Languages);

        using var img = InventedTables.Line("Alpha Bravo Charlie");
        var w = await router.RecognizeAsync(img, OcrLayout.Block, "de-DE", CancellationToken.None);
        Assert.Equal("win", Assert.Single(w).Text);
        var t = await router.RecognizeAsync(img, OcrLayout.Block, "deu", CancellationToken.None);
        Assert.Contains(t, x => x.Text.Contains("Bravo"));
        Assert.Equal(["de-DE"], win.Reads);

        router.Dispose();
        Assert.True(win.Disposed);
    }

    [Fact]
    public void Bundled_eng_is_not_listed()
    {
        using var svc = _fx.Service();
        using var router = new OcrRouter(new FakeWindows("en-US", "de-DE"), new TesseractRecognizer(null, svc));
        Assert.Equal(["en-US", "de-DE"], router.Languages);
        Assert.DoesNotContain("eng", router.Languages);
        Assert.Equal("English (United States)", router.DisplayName("en-US"));
        Assert.Equal("German (Germany)", router.DisplayName("de-DE"));
    }

    [Fact]
    public void Clashing_display_names_get_engine_suffix()
    {
        _fx.Install();
        using var svc = _fx.Service();
        using var router = new OcrRouter(new FakeWindows("en-US", "de"), new TesseractRecognizer(null, svc));
        Assert.Equal("German (Windows)", router.DisplayName("de"));
        Assert.Equal("German (Tesseract)", router.DisplayName("deu"));
        Assert.Equal("English (United States)", router.DisplayName("en-US"));
    }

    [Fact]
    public async Task Router_raises_LanguagesChanged_from_tesseract()
    {
        using var svc = _fx.Service();
        using var router = new OcrRouter(new FakeWindows("en-US", "de-DE"), new TesseractRecognizer(null, svc));
        using var raised = new ManualResetEventSlim();
        IOcrLanguageList list = router;
        list.LanguagesChanged += raised.Set;
        svc.Enqueue([OcrDownloadsFixture.Code]);
        await svc.Idle.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(raised.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(["en-US", "de-DE", "deu"], router.Languages);
        Assert.Equal("German", router.DisplayName("deu"));              // no clash with "German (Germany)": no suffix
        Assert.Equal("German (Germany)", router.DisplayName("de-DE"));
    }
}
