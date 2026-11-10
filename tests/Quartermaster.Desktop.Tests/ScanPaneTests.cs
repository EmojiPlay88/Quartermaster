using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Workbench;
using Quartermaster.Documents;
using Quartermaster.Documents.Tests;
using Quartermaster.Render;
using Quartermaster.Render.Ocr;
using Quartermaster.TestFixtures;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using SDRect = System.Drawing.Rectangle;

namespace Quartermaster.Desktop.Tests;

/// <summary>Invented scan files and a scripted OCR engine for the scan pane tests (no real scans, no real OCR).</summary>
internal static class ScanKit
{
    /// <summary>The invented table's cells in image pixels (what a user would drag around).</summary>
    public static SDRect TableRegion => new(InventedTables.Margin, InventedTables.Margin, InventedTables.Cols * InventedTables.CellW, InventedTables.Rows * InventedTables.CellH);

    public static string[][] Expected()
    {
        var (img, expected) = InventedTables.Table(bordered: true);
        img.Dispose();
        return expected;
    }

    /// <summary>The invented bordered table as a PNG file.</summary>
    public static string WritePng(string dir, string name = "invented-chart.png")
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        var (img, _) = InventedTables.Table(bordered: true);
        using (img)
        using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    /// <summary>A PNG of random noise (large when encoded: listed as a module image ≥ 64 KB) or a small plain one.</summary>
    public static byte[] Png(int w, int h, bool noise)
    {
        using var bmp = new SKBitmap(w, h);
        var rng = new Random(11);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                bmp.SetPixel(x, y, noise ? new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)) : SKColors.White);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static byte[] Pdf(params string[] pages)
    {
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages) b.AddPage(PageSize.A4).AddText(text, 12, new PdfPoint(40, 700), font);
        return b.Build();
    }

    /// <summary>A two-page invented PDF file.</summary>
    public static string WritePdf(string dir, string name = "invented-scan.pdf")
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, Pdf("Invented scan page one", "Invented scan page two"));
        return path;
    }

    /// <summary>Runs the dispatcher loop in short slices until the condition holds or the time is up.</summary>
    public static bool WaitFor(Func<bool> condition, int ms = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            using var slice = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    public static void Click(Button b) { b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
}

/// <summary>
/// Scripted OCR engine: a Block read returns the invented table's numbers laid out over the image it is given, as if the region were the
/// table's cells (<see cref="ScanKit.TableRegion"/>); a single-line read returns nothing. <see cref="Gate"/> holds reads.
/// </summary>
internal sealed class TableOcr : IOcrRecognizer, IDisposable
{
    readonly string[][] _expected = ScanKit.Expected();
    public string Name => "Fake";
    public int MaxImageDimension => 10000;
    public IReadOnlyList<string> Languages => ["eng"];
    public string? DefaultLanguage => "eng";
    public string UnavailableMessage => "Fake OCR has no data.";
    public TaskCompletionSource? Gate { get; set; }
    public int Reads;
    public bool Disposed;

    public async Task<IReadOnlyList<OcrWord>> RecognizeAsync(SKBitmap gray, OcrLayout layout, string language, CancellationToken ct)
    {
        Interlocked.Increment(ref Reads);
        if (Gate is { } g) await g.Task.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (layout != OcrLayout.Block) return [];
        double tw = InventedTables.Cols * InventedTables.CellW, th = InventedTables.Rows * InventedTables.CellH;
        double sx = gray.Width / tw, sy = gray.Height / th;
        var words = new List<OcrWord>();
        for (var r = 0; r < InventedTables.Rows; r++)
            for (var c = 0; c < InventedTables.Cols; c++)
                words.Add(new OcrWord(_expected[r][c], (c * InventedTables.CellW + 70) * sx, (r * InventedTables.CellH + 20) * sy, 60 * sx, 30 * sy));
        return words;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Port stage 4a task 12: the scan pane (toolbar, page controls, regions per page, read cancellation, load failures).</summary>
[Collection("MacFlag")]
public sealed class ScanPaneTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-scanpane-" + Guid.NewGuid().ToString("N"));
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly FixtureModule _fx;
    readonly ModuleSession _s;
    readonly ScanSessionModel _model = new();
    readonly ScanLoader _loader;
    readonly TableOcr _ocr = new();
    readonly FakeDialogs _dialogs = new();
    readonly ScanPane _pane;
    readonly Window _win;

    public ScanPaneTests()
    {
        ShortcutGestures.IsMac = false;
        Directory.CreateDirectory(_tmp);
        _fx = PhaseFixture.Module();
        _s = ModuleSession.Open(_fx.Build(), Path.Combine(_tmp, "cache"), null);
        _loader = new ScanLoader(_s.Archive.OpenEntry, _s.Archive.Sha256);
        _pane = new ScanPane(_s, _model, _loader, _ocr, _dialogs);
        _win = new Window { Width = 1000, Height = 600, Content = _pane };
        _win.Show();
        Dispatcher.UIThread.RunJobs();
    }

    public void Dispose()
    {
        _win.Close();
        _pane.Dispose();
        _loader.Dispose();
        _s.Dispose();
        _fx.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    void Show(ScanSource src, int page = 1)
    {
        var t = _pane.ShowSource(src, page);
        Assert.True(ScanKit.WaitFor(() => t.IsCompleted && _pane.Loading.IsCompleted), "the scan did not load");
    }

    bool PageControlsShown => _pane.PageLabel.IsVisible && _pane.PageBox.IsVisible && _pane.PageOf.IsVisible && _pane.PrevButton.IsVisible && _pane.NextButton.IsVisible;

    [AvaloniaFact]
    public void Toolbar_texts_and_tips()
    {
        Assert.Equal("Back to PDF", _pane.BackButton.Content);
        Assert.Equal("Page:", _pane.PageLabel.Text);
        Assert.Equal(("<", ">"), ((string)_pane.PrevButton.Content!, (string)_pane.NextButton.Content!));
        Assert.Equal(("Previous page", "Next page"), ((string)ToolTip.GetTip(_pane.PrevButton)!, (string)ToolTip.GetTip(_pane.NextButton)!));
        Assert.Equal(("−", "+"), ((string)_pane.ZoomOutButton.Content!, (string)_pane.ZoomInButton.Content!));
        Assert.Equal("Zoom out (Ctrl+wheel)", ToolTip.GetTip(_pane.ZoomOutButton));
        Assert.Equal("Zoom in (Ctrl+wheel)", ToolTip.GetTip(_pane.ZoomInButton));
        Assert.Equal(new[] { "Fit", "25 %", "50 %", "75 %", "100 %", "150 %", "200 %", "300 %", "400 %" }, _pane.ZoomBox.Items.Cast<string>());
        Assert.Equal("Open a scan, choose an image, or use Scan this page on a PDF.", _pane.Pane.Message);
        Assert.False(PageControlsShown);

        ShortcutGestures.IsMac = true;
        using var mac = new ScanPane(_s, _model, _loader, _ocr, _dialogs);
        Assert.Equal("Zoom in (Cmd+wheel)", ToolTip.GetTip(mac.ZoomInButton));
    }

    [AvaloniaFact]
    public void Pdf_page_shows_with_page_controls()
    {
        var src = ScanSource.File(ScanKit.WritePdf(_tmp));
        Show(src);
        Assert.NotNull(_pane.Image);
        Assert.Same(_pane.Image, _pane.Pane.Image);
        Assert.Null(_pane.Pane.Message);
        Assert.True(_pane.Image!.Width > 1500);   // A4 at 200 DPI
        Assert.True(PageControlsShown);
        Assert.Equal("of 2", _pane.PageOf.Text);
        Assert.Equal(1, _pane.PageBox.Value);
        Assert.False(_pane.PrevButton.IsEnabled);
        Assert.True(_pane.NextButton.IsEnabled);
        Assert.Equal(src, _pane.Source);
        Assert.Equal(0, _pane.ZoomBox.SelectedIndex);   // Fit
    }

    [AvaloniaFact]
    public void Image_hides_page_controls()
    {
        Show(ScanSource.File(ScanKit.WritePdf(_tmp)));
        Assert.True(PageControlsShown);
        Show(ScanSource.File(ScanKit.WritePng(_tmp)));
        Assert.False(PageControlsShown);
        Assert.Equal((1320, 400), (_pane.Image!.Width, _pane.Image.Height));
        Assert.Equal(1, _pane.Page);
        Assert.Null(_pane.Note);
    }

    [AvaloniaFact]
    public void Next_previous_clamp()
    {
        Show(ScanSource.File(ScanKit.WritePdf(_tmp)));
        ScanKit.Click(_pane.NextButton);
        Assert.True(ScanKit.WaitFor(() => _pane.Loading.IsCompleted && _pane.Page == 2 && _pane.Image != null));
        Assert.False(_pane.NextButton.IsEnabled);
        Assert.True(_pane.PrevButton.IsEnabled);
        _pane.PageBox.Value = 7;   // beyond the last page: clamped
        Assert.True(ScanKit.WaitFor(() => _pane.Loading.IsCompleted));
        Assert.Equal(2, _pane.Page);
        Assert.Equal(2, _pane.PageBox.Value);
        ScanKit.Click(_pane.PrevButton);
        Assert.True(ScanKit.WaitFor(() => _pane.Loading.IsCompleted && _pane.Page == 1 && _pane.Image != null));
        Assert.False(_pane.PrevButton.IsEnabled);
        // a page asked for past the end renders the last page
        Show(_pane.Source!, 9);
        Assert.Equal(2, _pane.Page);
        Assert.Equal("of 2", _pane.PageOf.Text);
    }

    [AvaloniaFact]
    public void Region_remembered_per_page()
    {
        var src = ScanSource.File(ScanKit.WritePdf(_tmp));
        Show(src);
        var region = new SDRect(100, 120, 300, 200);
        _pane.Pane.CommitRegion(region);
        Assert.Equal(region, _model.GetRegion(src.Key, 1, new System.Drawing.Size(_pane.Image!.Width, _pane.Image.Height)));
        Show(src, 2);
        Assert.Null(_pane.Pane.SelectedRegion);
        Show(src, 1);
        Assert.Equal(region, _pane.Pane.SelectedRegion);
        Assert.Equal(region, _pane.Ocr.Target!()!.Region);
    }

    [AvaloniaFact]
    public async Task Region_change_cancels_read()
    {
        Show(ScanSource.File(ScanKit.WritePng(_tmp)));
        _ocr.Gate = new TaskCompletionSource();
        var read = _pane.Ocr.ReadTextAsync();
        Assert.True(_pane.Ocr.IsReading);
        _pane.Pane.CommitRegion(ScanKit.TableRegion);
        Assert.False(_pane.Ocr.IsReading);
        Assert.Equal("Cancelled: the region changed.", _pane.Ocr.StatusText);
        await read;
        Assert.False(_pane.Ocr.HasGrid);

        // a new image cancels a running read too, and the shown bitmap stays alive until the new one replaces it
        var shown = _pane.Image!;
        read = _pane.Ocr.ReadTextAsync();
        Assert.True(_pane.Ocr.IsReading);
        var next = _pane.ShowSource(ScanSource.File(ScanKit.WritePng(_tmp, "other.png")));
        Assert.Equal("Cancelled: another image was opened.", _pane.Ocr.StatusText);
        Assert.False(_pane.Ocr.IsReading);
        Assert.Same(shown, _pane.Image);
        Assert.NotEqual(IntPtr.Zero, shown.GetPixels());
        Assert.Null(_pane.Ocr.Target!());   // nothing to read while loading
        Assert.True(ScanKit.WaitFor(() => next.IsCompleted));
        Assert.NotSame(shown, _pane.Image);
        Assert.Equal(IntPtr.Zero, shown.Handle);   // the replaced bitmap was disposed
        await read;
    }

    [AvaloniaFact]
    public void Load_failure_message_shown()
    {
        Show(ScanSource.File(Path.Combine(_tmp, "missing.png")));
        Assert.Equal("missing.png was not found.", _pane.Pane.Message);
        Assert.Null(_pane.Image);
        Assert.Null(_pane.Ocr.Target!());

        var bad = Path.Combine(_tmp, "broken.png");
        File.WriteAllBytes(bad, [1, 2, 3, 4, 5]);
        Show(ScanSource.File(bad));
        Assert.False(string.IsNullOrEmpty(_pane.Pane.Message));

        var badPdf = Path.Combine(_tmp, "broken.pdf");
        File.WriteAllBytes(badPdf, [1, 2, 3, 4, 5]);
        Show(ScanSource.File(badPdf));
        Assert.False(string.IsNullOrEmpty(_pane.Pane.Message));
        Assert.Null(_pane.Image);
        Assert.False(PageControlsShown && _pane.NextButton.IsEnabled);

        // a failure that is not about the file is logged
        var before = _s.Log.Items.Count;
        _pane.LoadOverride = (_, _, _) => throw new InvalidOperationException("invented failure");
        Show(ScanSource.File(ScanKit.WritePng(_tmp)));
        Assert.Equal("This file can't be opened as an image.", _pane.Pane.Message);
        Assert.Contains(_s.Log.Items.Skip(before), e => e.Message == "Scan failed: invented failure");
    }

    [AvaloniaFact]
    public void Back_to_pdf_event()
    {
        var backs = 0;
        _pane.BackToPdf += () => backs++;
        ScanKit.Click(_pane.BackButton);
        Assert.Equal(1, backs);
    }

    [AvaloniaFact]
    public void Document_list_maps_to_host_indexes_and_skips_separator()
    {
        var chosen = new List<int>();
        _pane.DocumentChosen += chosen.Add;
        _pane.SetDocuments(["Rules", "Charts"], ["map.png", "scan.png (opened)"]);
        Assert.Equal(new[] { "Rules", "Charts", Documents.DocumentViewer.ImagesSeparator, "map.png", "scan.png (opened)" }, _pane.DocsBox.Items.Cast<string>());
        _pane.MarkSelected(3);
        Assert.Equal(4, _pane.DocsBox.SelectedIndex);
        Assert.Empty(chosen);
        _pane.DocsBox.SelectedIndex = 2;   // the separator: back to the marked document
        Assert.Equal(4, _pane.DocsBox.SelectedIndex);
        Assert.Empty(chosen);
        _pane.DocsBox.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { 1 }, chosen);
    }

    [AvaloniaFact]
    public void Zoom_buttons_and_list()
    {
        Show(ScanSource.File(ScanKit.WritePng(_tmp)));
        _pane.ZoomBox.SelectedIndex = 4;   // 100 %
        Assert.Equal(1.0, _pane.Pane.Zoom, 3);
        ScanKit.Click(_pane.ZoomInButton);
        Assert.Equal(1.5, _pane.Pane.Zoom, 3);
        Assert.Equal(5, _pane.ZoomBox.SelectedIndex);
        ScanKit.Click(_pane.ZoomOutButton);
        Assert.Equal(4, _pane.ZoomBox.SelectedIndex);
        _pane.Pane.SetZoom(1.23);
        Assert.Equal(-1, _pane.ZoomBox.SelectedIndex);
        Assert.Equal("123 %", _pane.ZoomBox.PlaceholderText);
        _pane.ZoomBox.SelectedIndex = 0;
        Assert.True(_pane.Pane.FitMode);
    }

    [AvaloniaFact]
    public void Dispose_clears_the_image_pane_and_disposes_the_bitmap()
    {
        Show(ScanSource.File(ScanKit.WritePng(_tmp)));
        var bmp = _pane.Image!;
        _pane.Dispose();
        Assert.Null(_pane.Pane.Image);
        Assert.Null(_pane.Image);
        Assert.Equal(IntPtr.Zero, bmp.Handle);
        _pane.Dispose();   // twice is harmless
    }
}
