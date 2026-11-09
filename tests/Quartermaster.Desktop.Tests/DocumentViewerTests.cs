using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Desktop.Documents;
using Quartermaster.Desktop.Input;
using Quartermaster.Documents;
using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 3b task 6: the document viewer, headless, on an invented module with invented PDF, text and HTML documents.</summary>
[Collection("MacFlag")]
public sealed class DocumentViewerTests : IDisposable
{
    const string Term = "zone of control";

    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-docview-" + Guid.NewGuid().ToString("N"));
    readonly List<Window> _windows = new();
    readonly List<DocumentViewer> _viewers = new();
    readonly List<ModuleSession> _sessions = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();

    public DocumentViewerTests() { ShortcutGestures.IsMac = false; Directory.CreateDirectory(_tmp); }

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        foreach (var v in _viewers) v.Dispose();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>An invented PDF: one page per entry, each line of text 14 pt below the last.</summary>
    static byte[] Pdf(PageSize size, params string[][] pages)
    {
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var pg = b.AddPage(size);
            double y = pg.PageSize.Height - 60;
            foreach (var l in lines)
            {
                pg.AddText(l, 12, new PdfPoint(40, y), font);
                y -= 14;
            }
        }
        return b.Build();
    }

    static string Help(string title, string file) =>
        $"<VASSAL.build.module.documentation.HelpFile title=\"{title}\" fileName=\"{file}\" fileType=\"archive\"/>";

    ModuleSession Session()
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            "<VASSAL.build.module.Documentation>" +
            Help("Rules", "rules.pdf") + Help("Notes", "docs/notes.txt") + Help("Guide", "guide.html") +
            Help("Broken", "broken.pdf") + Help("Big", "big.pdf") + Help("Gone", "gone.pdf") +
            "</VASSAL.build.module.Documentation></VASSAL.build.GameModule>");
        var fx = new FixtureModule().WithBuildFile(xml)
            .AddFile("rules.pdf", Pdf(PageSize.A4,
                new[] { "Introduction to the invented game" },
                new[] { "Movement costs one point per hex.", "A unit entering an enemy zone of control stops." },
                new[] { "Supply is traced to a friendly road." }))
            .AddFile("docs/notes.txt", Encoding.UTF8.GetBytes("Designer notes\r\n  column one\tcolumn two\r\n"))
            .AddFile("guide.html", Encoding.UTF8.GetBytes("<html><body>Invented guide</body></html>"))
            .AddFile("broken.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 this is not really a pdf"))
            .AddFile("big.pdf", Pdf(PageSize.A3, new[] { "A large invented page" }));
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), null);
        _sessions.Add(s);
        return s;
    }

    (Window W, DocumentViewer V, ModuleSession S) Host(double scaling = 1)
    {
        var s = Session();
        var v = new DocumentViewer(s, toolbar: false, _dialogs);
        _viewers.Add(v);
        var w = new Window { Width = 800, Height = 600, Content = v };
        _windows.Add(w);
        if (scaling != 1) w.SetRenderScaling(scaling);
        w.Show();
        Pump();
        return (w, v, s);
    }

    static DocumentDef Doc(ModuleSession s, string title) => s.Model.Documents.Single(d => d.Title == title);

    static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>Runs the dispatcher (timers included) for a few milliseconds.</summary>
    static void Slice(int ms = 10)
    {
        Pump();
        using var slice = new CancellationTokenSource(ms);
        Dispatcher.UIThread.MainLoop(slice.Token);
    }

    /// <summary>Pumps until the latest open/render has landed (debounce included).</summary>
    static void Settle(DocumentViewer v, int timeoutMs = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            Pump();
            if (v.Rendering.IsCompleted && !v.Busy) { Pump(); if (v.Rendering.IsCompleted && !v.Busy) return; }
            Slice(10);
        }
        throw new TimeoutException("viewer did not settle: " + v.StateForTests);
    }

    static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static IEnumerable<T> All<T>(Visual root) where T : Visual =>
        root.GetVisualDescendantsSafe().OfType<T>();

    [AvaloniaFact]
    public void Opens_pdf_at_page_with_page_count()
    {
        var (_, v, s) = Host();
        v.Open(Doc(s, "Rules"), 2);
        Settle(v);
        Assert.Equal(2, v.Page);
        Assert.Equal(3, v.PageCount);
        Assert.Same(Doc(s, "Rules"), v.Document);
        Assert.True(v.NavigationBar.IsVisible);
        Assert.Equal(2m, v.PageBox.Value);
        Assert.Equal(1m, v.PageBox.Minimum);
        Assert.Equal(3m, v.PageBox.Maximum);
        Assert.Equal("of 3", v.OfLabel.Text);
        Assert.Equal("Page:", v.PageLabel.Text);
        Assert.Equal("<", v.PrevButton.Content);
        Assert.Equal(">", v.NextButton.Content);
        Assert.Equal("Previous page", ToolTip.GetTip(v.PrevButton));
        Assert.Equal("Next page", ToolTip.GetTip(v.NextButton));
        Assert.Equal("Zoom out", v.ZoomOutButton.Content);
        Assert.Equal("Zoom in", v.ZoomInButton.Content);
        Assert.Equal("Fit width", v.FitWidthButton.Content);
        Assert.Equal("Fit page", v.FitPageButton.Content);
        Assert.Equal($"{Math.Round(v.Zoom * 100)} %", v.ZoomLabel.Text);
        Assert.Equal(2, v.PageView.ShownPage);
        Assert.NotNull(v.PageView.Bitmap);
        Assert.Null(v.SearchTerm);
    }

    [AvaloniaFact]
    public void Next_previous_and_page_box_clamp()
    {
        var (_, v, s) = Host();
        v.Open(Doc(s, "Rules"), 2);
        Settle(v);
        Click(v.NextButton);
        Assert.Equal(3, v.Page);
        Click(v.NextButton);
        Assert.Equal(3, v.Page);
        Click(v.PrevButton);
        Assert.Equal(2, v.Page);
        v.PageBox.Value = 10;   // the box clamps to its maximum
        Assert.Equal(3, v.Page);
        Assert.Equal(3m, v.PageBox.Value);
        v.GoToPage(0);
        Assert.Equal(1, v.Page);
        Click(v.PrevButton);
        Assert.Equal(1, v.Page);
        v.GoToPage(99);
        Assert.Equal(3, v.Page);
        Assert.Equal(3m, v.PageBox.Value);
        Settle(v);
        Assert.Equal(3, v.PageView.ShownPage);
    }

    [AvaloniaFact]
    public void PageUp_PageDown_Home_End()
    {
        var (w, v, s) = Host();
        v.Open(Doc(s, "Rules"));
        Settle(v);
        v.PageView.Focus();
        Pump();
        w.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
        Assert.Equal(2, v.Page);
        w.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
        Assert.Equal(3, v.Page);
        w.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
        Assert.Equal(3, v.Page);
        w.KeyPress(Key.PageUp, RawInputModifiers.None, PhysicalKey.PageUp, null);
        Assert.Equal(2, v.Page);
        w.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
        Assert.Equal(1, v.Page);
        w.KeyPress(Key.PageUp, RawInputModifiers.None, PhysicalKey.PageUp, null);
        Assert.Equal(1, v.Page);
    }

    [AvaloniaFact]
    public void Plain_page_move_clears_search()
    {
        var (_, v, s) = Host();
        v.Open(Doc(s, "Rules"), 2, "  " + Term + " ");
        Assert.Equal(Term, v.SearchTerm);
        v.GoToPage(3);
        Assert.Null(v.SearchTerm);
        v.GoToPage(2, Term);
        Assert.Equal(Term, v.SearchTerm);
        Settle(v);
        Click(v.NextButton);
        Assert.Null(v.SearchTerm);
        v.GoToPage(1, "   ");
        Assert.Null(v.SearchTerm);
    }

    [AvaloniaFact]
    public void Fit_width_then_zoom_steps_and_limits()
    {
        var (w, v, s) = Host();
        v.Open(Doc(s, "Rules"));
        Settle(v);
        Assert.True(v.FitWidth);
        var fit = v.Zoom;
        Assert.InRange(fit, 0.5, 2);
        Assert.Equal(v.FitWidthZoom()!.Value, fit, 6);
        Assert.Equal($"{Math.Round(fit * 100)} %", v.ZoomLabel.Text);

        Click(v.ZoomInButton);
        Assert.False(v.FitWidth);
        Assert.Equal(fit * 1.25, v.Zoom, 6);
        Click(v.ZoomOutButton);
        Assert.Equal(fit, v.Zoom, 6);
        for (var i = 0; i < 40; i++) Click(v.ZoomInButton);
        Assert.Equal(8.0, v.Zoom, 6);
        Assert.Equal("800 %", v.ZoomLabel.Text);
        for (var i = 0; i < 60; i++) Click(v.ZoomOutButton);
        Assert.Equal(0.1, v.Zoom, 6);
        Assert.Equal("10 %", v.ZoomLabel.Text);

        // Keys: Primary+Plus / Minus zoom, Primary+0 fits the width.
        v.PageView.Focus();
        w.KeyPress(Key.OemPlus, RawInputModifiers.Control, PhysicalKey.Equal, null);
        Assert.Equal(0.125, v.Zoom, 6);
        w.KeyPress(Key.OemMinus, RawInputModifiers.Control, PhysicalKey.Minus, null);
        Assert.Equal(0.1, v.Zoom, 6);
        w.KeyPress(Key.D0, RawInputModifiers.Control, PhysicalKey.Digit0, null);
        Assert.True(v.FitWidth);
        Assert.Equal(fit, v.Zoom, 6);

        // Fit page: the whole page fits; resizes keep the fit mode.
        Click(v.FitPageButton);
        Assert.False(v.FitWidth);
        Assert.True(v.FitPage);
        var page = v.Zoom;
        Assert.True(page < fit);
        w.Height = 400;
        Pump();
        Assert.True(v.FitPage);
        Assert.True(v.Zoom < page);
        Click(v.FitWidthButton);
        w.Width = 600;
        Pump();
        Assert.True(v.FitWidth);
        Assert.True(v.Zoom < fit);
        Assert.Equal(v.FitWidthZoom()!.Value, v.Zoom, 6);

        // Primary+wheel zooms; a plain wheel scrolls (zoom unchanged).
        var at = v.PageView.TranslatePoint(new Point(20, 20), w)!.Value;
        var before = v.Zoom;
        w.MouseWheel(at, new Vector(0, 1), RawInputModifiers.Control);
        Assert.Equal(before * 1.25, v.Zoom, 6);
        w.MouseWheel(at, new Vector(0, -1), RawInputModifiers.Control);
        Assert.Equal(before, v.Zoom, 6);
        w.MouseWheel(at, new Vector(0, -1));
        Assert.Equal(before, v.Zoom, 6);
        Assert.True(v.Scroller.Offset.Y > 0);
    }

    [AvaloniaFact]
    public void Render_dpi_follows_render_scaling()
    {
        var (w, v, s) = Host(scaling: 2);
        Assert.Equal(2, w.RenderScaling);
        v.Open(Doc(s, "Rules"));
        Settle(v);
        Assert.Equal(144 * v.Zoom, v.LastRequestedDpi, 6);
        var size = v.PageView.Bitmap!.PixelSize;
        Assert.InRange(size.Width, 595 * v.Zoom * 2 - 2, 595 * v.Zoom * 2 + 2);
        Click(v.ZoomInButton);
        Settle(v);
        Assert.Equal(144 * v.Zoom, v.LastRequestedDpi, 6);
    }

    [AvaloniaFact]
    public void Zoom_800_on_large_page_stays_under_cap()
    {
        var (_, v, s) = Host(scaling: 2);
        v.Open(Doc(s, "Big"));
        Settle(v);
        v.Zoom = 8;
        Assert.False(v.FitWidth);
        Settle(v, 60000);
        Assert.Equal(8 * 144, v.LastRequestedDpi, 6);
        var px = v.PageView.Bitmap!.PixelSize;
        Assert.True((long)px.Width * px.Height <= PdfPages.MaxPixels, $"{px.Width} x {px.Height}");
        Assert.True((long)px.Width * px.Height > PdfPages.MaxPixels * 9 / 10, $"{px.Width} x {px.Height}");
    }

    [AvaloniaFact]
    public void Stale_render_is_dropped_and_disposed()
    {
        var (_, v, s) = Host();
        var pending = new List<(int Page, TaskCompletionSource<SKBitmap> Tcs)>();
        v.RenderOverride = (page, dpi, ct) =>
        {
            var tcs = new TaskCompletionSource<SKBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pending) pending.Add((page, tcs));
            return tcs.Task;
        };
        v.Open(Doc(s, "Rules"));
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until) { Pump(); lock (pending) if (pending.Count == 1) break; Slice(5); }
        Assert.Single(pending);
        v.GoToPage(2);
        Pump();
        Assert.Equal(2, pending.Count);
        Assert.Equal(1, pending[0].Page);
        Assert.Equal(2, pending[1].Page);

        var fresh = new SKBitmap(30, 40);
        fresh.Erase(SKColors.White);
        var stale = new SKBitmap(10, 10);
        stale.Erase(SKColors.White);
        pending[1].Tcs.SetResult(fresh);
        Settle(v);
        pending[0].Tcs.SetResult(stale);   // arrives after the newer page: dropped
        until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until && v.StaleDropped == 0) { Pump(); Slice(5); }
        Assert.Equal(1, v.StaleDropped);
        Assert.Equal(IntPtr.Zero, stale.Handle);   // disposed
        Assert.Equal(2, v.PageView.ShownPage);
        Assert.Equal(new PixelSize(30, 40), v.PageView.Bitmap!.PixelSize);
    }

    [AvaloniaFact]
    public void Search_term_draws_highlights_on_hit_page_only()
    {
        var (w, v, s) = Host();
        v.Open(Doc(s, "Rules"), 2, Term);
        Settle(v);
        var boxes = v.PageView.Highlights;
        Assert.Single(boxes);
        var box = boxes[0];
        Assert.InRange(box.X, 0.0, 1.0);
        Assert.InRange(box.Y, 0.0, 0.3);
        Assert.Equal(Color.FromArgb(0x66, 0xFF, 0xFF, 0x00), DocumentViewer.HighlightColor);

        // The box centre is drawn yellow over the white page.
        var centre = v.PageView.TranslatePoint(new Point((box.X + box.W / 2) * v.PageView.Bounds.Width,
            (box.Y + box.H / 2) * v.PageView.Bounds.Height), w)!.Value;
        using (var frame = w.CaptureRenderedFrame()!)
        {
            var px = PixelAt(frame, (int)centre.X, (int)centre.Y);
            Assert.True(px.Blue < px.Red - 60 && px.Blue < px.Green - 60, px.ToString());
        }

        v.GoToPage(3, Term);
        Settle(v);
        Assert.Empty(v.PageView.Highlights);
        v.GoToPage(1, Term);
        Settle(v);
        Assert.Empty(v.PageView.Highlights);
        v.GoToPage(2);   // no term: no boxes
        Settle(v);
        Assert.Empty(v.PageView.Highlights);
    }

    static SKColor PixelAt(Avalonia.Media.Imaging.Bitmap frame, int x, int y)
    {
        var buf = new byte[4];
        unsafe
        {
            fixed (byte* p = buf)
                frame.CopyPixels(new PixelRect(x, y, 1, 1), (IntPtr)p, 4, 4);
        }
        return new SKColor(buf[0], buf[1], buf[2], buf[3]);   // headless frames are Rgba8888
    }

    [AvaloniaFact]
    public void Text_document_shows_text_read_only()
    {
        var (_, v, s) = Host();
        v.Open(Doc(s, "Notes"));
        Settle(v);
        Assert.Equal(0, v.PageCount);
        Assert.False(v.NavigationBar.IsVisible);
        var box = Assert.IsType<TextBox>(v.Body);
        Assert.True(box.IsReadOnly);
        Assert.Equal("Designer notes\r\n  column one\tcolumn two\r\n", box.Text);
        Assert.Equal(TextWrapping.NoWrap, box.TextWrapping);
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, ScrollViewer.GetHorizontalScrollBarVisibility(box));
        Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, ScrollViewer.GetVerticalScrollBarVisibility(box));
        Assert.Contains("Consolas", box.FontFamily.ToString());
    }

    [AvaloniaFact]
    public void Html_document_opens_browser_once_and_shows_button()
    {
        var (_, v, s) = Host();
        v.Open(Doc(s, "Guide"));
        Settle(v);
        var path = Assert.Single(_dialogs.Browsed);
        Assert.EndsWith("guide.html", path);
        Assert.True(File.Exists(path));
        Assert.Equal(0, v.PageCount);
        Assert.False(v.NavigationBar.IsVisible);
        Assert.Contains("This document opens in your web browser.", v.BodyTexts);
        var button = Assert.Single(All<Button>(v.Body!), b => (b.Content as string) == "Open in browser");
        Click(button);
        Assert.Equal(2, _dialogs.Browsed.Count);
        Assert.Equal(path, _dialogs.Browsed[1]);
    }

    [AvaloniaFact]
    public void Corrupt_pdf_shows_message_and_logs_once()
    {
        var (_, v, s) = Host();
        var before = s.Log.Items.Count;
        v.Open(Doc(s, "Broken"));
        Settle(v);
        Pump();
        var msg = Assert.Single(v.BodyTexts);
        Assert.StartsWith("This document could not be opened: ", msg);
        Assert.True(msg.Length > "This document could not be opened: ".Length);
        var logged = s.Log.Items.Skip(before).ToList();
        var d = Assert.Single(logged);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal("document", d.Category);
        Assert.Equal(msg, d.Message);
        Assert.Equal("Broken", d.Context);
        Assert.Equal(0, v.PageCount);

        // A missing entry fails the same way, synchronously.
        v.Open(Doc(s, "Gone"));
        Settle(v);
        Assert.StartsWith("This document could not be opened: ", Assert.Single(v.BodyTexts));
        Assert.Equal(2, s.Log.Items.Count - before);
        Assert.Equal("Gone", s.Log.Items[^1].Context);
    }

    [AvaloniaFact]
    public void Close_during_render_is_quiet()
    {
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) { lock (unobserved) unobserved.Add(e.Exception); }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var (w, v, s) = Host(scaling: 2);
            v.Zoom = 8;   // a slow render: 800 % of an A3 page at scale 2 (capped at 60 megapixels)
            v.Open(Doc(s, "Big"));
            var until = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < until && !v.RenderInFlight) { Pump(); Thread.Sleep(1); }
            Assert.True(v.RenderInFlight);
            v.Zoom = 4;   // a pending debounced render too
            var sw = System.Diagnostics.Stopwatch.StartNew();
            w.Close();
            Pump();
            Assert.True(sw.ElapsedMilliseconds < 2500, sw.ElapsedMilliseconds + " ms");
            Assert.True(v.Released);
            for (var i = 0; i < 30; i++) { Pump(); Slice(10); }
            Assert.Null(v.PageView.Bitmap);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            lock (unobserved) Assert.Empty(unobserved);
        }
        finally { TaskScheduler.UnobservedTaskException -= OnUnobserved; }
    }

    [AvaloniaFact]
    public void Reattach_reads_the_pdf_again_at_the_same_page()
    {
        var (w, v, s) = Host();
        v.Open(Doc(s, "Rules"), 2, Term);
        Settle(v);
        w.Content = null;   // detached: everything released
        Pump();
        Assert.True(v.Released);
        Assert.Null(v.PageView.Bitmap);
        Assert.Equal(2, v.Page);
        w.Content = v;
        Pump();
        Settle(v);
        Assert.False(v.Released);
        Assert.Equal(3, v.PageCount);
        Assert.Equal(2, v.PageView.ShownPage);
        Assert.NotNull(v.PageView.Bitmap);
        Assert.Equal(Term, v.SearchTerm);
        Assert.Single(v.PageView.Highlights);
    }

    [AvaloniaFact]
    public void Page_changed_and_document_opened_events()
    {
        var (_, v, s) = Host();
        var pages = new List<int>();
        var opened = 0;
        v.PageChanged += pages.Add;
        v.DocumentOpened += () => opened++;
        v.Open(Doc(s, "Rules"));
        Assert.Equal(1, opened);
        Assert.Empty(pages);
        Settle(v);
        v.GoToPage(2);
        Assert.Equal(new[] { 2 }, pages);
        v.GoToPage(2);
        Assert.Equal(new[] { 2 }, pages);
        v.Open(Doc(s, "Rules"), 3);   // same document: a page move
        Assert.Equal(1, opened);
        Assert.Equal(new[] { 2, 3 }, pages);
        v.Open(Doc(s, "Notes"));
        Assert.Equal(2, opened);
        Assert.Equal(new[] { 2, 3, 1 }, pages);
        v.Open(Doc(s, "Rules"), 9);   // clamped to the page count once the PDF is read
        Settle(v);
        Assert.Equal(3, opened);
        Assert.Equal(3, v.Page);
        Assert.Equal(3, pages[^1]);
    }
}

static class VisualExtensions
{
    public static IEnumerable<Visual> GetVisualDescendantsSafe(this Visual root)
    {
        foreach (var c in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root)) yield return c;
    }
}
