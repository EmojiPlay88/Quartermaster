using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Preferences;
using Quartermaster.Documents;
using Quartermaster.Render;
using Xunit;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 4c task 4: Settings > OCR languages (download / cancel / remove with progress) and Settings opening on a page.
/// Headless only; a stub HTTP handler and an invented catalog, never the real network or profile.</summary>
[Collection("MacFlag")]
public sealed class OcrLanguagesPageTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-ocrlang-" + Guid.NewGuid().ToString("N"));
    readonly FakeDialogs _dialogs = new();
    readonly StubHandler _http = new();
    readonly List<OcrLanguageService> _services = new();
    readonly List<Window> _windows = new();
    readonly List<MainWindow> _mains = new();

    static readonly byte[] DeuData = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray();
    static readonly byte[] FraData = Enumerable.Range(0, 2000).Select(i => (byte)(i * 13)).ToArray();

    static readonly OcrLanguageEntry[] Catalog =
    {
        new("eng", "English", 4113088, "7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2", true),
        new("deu", "German", DeuData.Length, Sha(DeuData), false),
        new("fra", "French", 2_621_440, Sha(FraData), false),   // the size is only shown, never downloaded in full
    };

    public OcrLanguagesPageTests()
    {
        ShortcutGestures.IsMac = false;
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        foreach (var w in _windows) { try { w.Close(); } catch (Exception) { } }
        foreach (var w in _mains) w.ForceClose();
        foreach (var s in _services) s.Dispose();
        _http.Release();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    string Folder => Path.Combine(_tmp, "OcrLanguages");

    OcrLanguageService Service()
    {
        var s = new OcrLanguageService(Folder, _http, null, Catalog);
        _services.Add(s);
        return s;
    }

    SettingsContext Ctx(OcrLanguageService? langs) =>
        new(Array.Empty<(string, string)>(), Path.Combine(_tmp, "cache"), () => null, _ => Task.CompletedTask, _dialogs, OcrLanguages: langs);

    sealed class StubPage(string title) : SettingsPage
    {
        public override string Title => title;
        public override void Load(Settings s) { }
        public override Settings Collect(Settings s) => s;
    }

    /// <summary>A settings window with the standard pages and the OCR page selected.</summary>
    (SettingsWindow Win, OcrLanguagesPage Page) Open(OcrLanguageService svc, Action<Settings>? apply = null)
    {
        var w = new SettingsWindow(new Settings(), apply ?? (_ => { }), SettingsWindow.StandardPages(Ctx(svc), new StubPage("Keyboard")), null, _dialogs);
        _windows.Add(w);
        w.Show();
        Assert.True(w.SelectPage(OcrLanguagesPage.PageKey));
        var page = Assert.IsType<OcrLanguagesPage>(w.Pages[w.SelectedPage]);
        Assert.Same(w.Pages[^1], page);   // the new page is last (existing page indexes hold)
        WaitFor(() => page.StatusesLoaded);
        return (w, page);
    }

    static void Click(Button b)
    {
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    static void WaitFor(Func<bool> done, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(done());
    }

    static string StatusOf(OcrLanguagesPage p, string code) => p.RowsForTest.Single(r => r.Code == code).Status;

    // ---------- the page ----------

    [AvaloniaFact]
    public void Page_lists_catalog_with_status_and_size()
    {
        var (_, page) = Open(Service());
        Assert.Equal("OCR languages", page.Title);
        Assert.Equal("ocr-languages", page.Key);
        Assert.Equal(new[] { "Name", "Code", "Size", "Status" }, page.ColumnTitles);
        Assert.Equal(new[]
        {
            ("English", "eng", "3.9 MB", "Bundled"),
            ("German", "deu", "0.0 MB", "Not installed"),
            ("French", "fra", "2.5 MB", "Not installed"),
        }, page.RowsForTest.Select(r => (r.Name, r.Code, r.Size, r.Status)).ToArray());
        Assert.Equal(SelectionMode.Multiple, page.List.SelectionMode & SelectionMode.Multiple);
        Assert.Empty(_http.Requests);   // nothing touches the network without a click
    }

    [AvaloniaFact]
    public void Download_selected_rows_queues_and_shows_progress()
    {
        _http.Gated["deu"] = DeuData;
        var svc = Service();
        var (_, page) = Open(svc);
        page.SelectCodes("deu", "fra");
        _http.Missing.Add("fra");
        Assert.True(page.DownloadButton.IsEnabled);
        Click(page.DownloadButton);
        WaitFor(() => StatusOf(page, "deu") == "Downloading 50%");
        Assert.Equal("Queued", StatusOf(page, "fra"));
        Assert.True(page.StopButton.IsEnabled);
        _http.Release();
        WaitFor(() => StatusOf(page, "deu") == "Installed");
        WaitFor(() => svc.Idle.IsCompleted);
        WaitFor(() => StatusOf(page, "fra") == "Not installed");
        Assert.True(File.Exists(Path.Combine(Folder, "deu.traineddata")));
        Assert.All(_http.Requests, u => Assert.StartsWith("https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/", u));
    }

    [AvaloniaFact]
    public void Remove_is_disabled_for_bundled_and_not_installed()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(Path.Combine(Folder, "deu.traineddata"), DeuData);
        var svc = Service();
        var (_, page) = Open(svc);
        Assert.Equal("Installed", StatusOf(page, "deu"));
        page.SelectCodes("eng");
        Assert.False(page.RemoveButton.IsEnabled);
        Assert.False(page.DownloadButton.IsEnabled);
        page.SelectCodes("fra");
        Assert.False(page.RemoveButton.IsEnabled);
        Assert.True(page.DownloadButton.IsEnabled);
        page.SelectCodes("deu");
        Assert.True(page.RemoveButton.IsEnabled);
        Assert.False(page.DownloadButton.IsEnabled);
        Click(page.RemoveButton);
        WaitFor(() => StatusOf(page, "deu") == "Not installed");
        Assert.False(File.Exists(Path.Combine(Folder, "deu.traineddata")));
        Assert.False(page.RemoveButton.IsEnabled);
        Assert.Empty(_http.Requests);
    }

    [AvaloniaFact]
    public void Filter_box_narrows_rows()
    {
        var (_, page) = Open(Service());
        page.FilterBox.Text = "germ";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "German" }, page.VisibleRowsForTest.Select(r => r.Name).ToArray());
        page.FilterBox.Text = "FRA";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { "French" }, page.VisibleRowsForTest.Select(r => r.Name).ToArray());
        page.FilterBox.Text = "";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, page.VisibleRowsForTest.Count);
    }

    [AvaloniaFact]
    public void Source_line_is_verbatim()
    {
        var (_, page) = Open(Service());
        Assert.Equal("Downloads from github.com/tesseract-ocr/tessdata_fast (tag 4.1.0, Apache-2.0). Nothing is downloaded unless you click Download.",
            page.SourceLine.Text);
        if (OperatingSystem.IsWindows())
            Assert.Equal("Windows OCR languages are added in Windows Settings.", page.WindowsLine?.Text);
        else
            Assert.Null(page.WindowsLine);
    }

    [AvaloniaFact]
    public void Errors_show_inline()
    {
        _http.Missing.Add("deu");
        var svc = Service();
        var (_, page) = Open(svc);
        page.SelectCodes("deu");
        Click(page.DownloadButton);
        WaitFor(() => svc.Idle.IsCompleted);
        WaitFor(() => page.ErrorLine.Text?.Contains("404", StringComparison.Ordinal) == true);
        Assert.Contains("German", page.ErrorLine.Text);
        Assert.Equal("Not installed", StatusOf(page, "deu"));
    }

    [AvaloniaFact]
    public void Ok_cancel_and_restore_defaults_do_not_touch_languages()
    {
        Directory.CreateDirectory(Folder);
        var deu = Path.Combine(Folder, "deu.traineddata");
        File.WriteAllBytes(deu, DeuData);
        var applied = 0;
        var svc = Service();
        var (w, page) = Open(svc, _ => applied++);
        Click(w.RestoreButton);
        Click(w.ApplyButton);
        Assert.Equal(1, applied);
        Assert.Equal("Installed", StatusOf(page, "deu"));
        Click(w.OkButton);
        Assert.Equal(2, applied);
        Assert.False(w.IsVisible);
        var (w2, _) = Open(svc);
        Click(w2.CancelButton);
        Assert.False(w2.IsVisible);
        Assert.True(File.Exists(deu));
        Assert.Equal(new[] { "deu" }, svc.InstalledCodes());
        Assert.Empty(_http.Requests);
        Assert.Empty(_dialogs.Questions);   // nothing running: no close question
    }

    [AvaloniaFact]
    public void Closing_during_download_asks_and_cancels_on_yes()
    {
        _http.Gated["deu"] = DeuData;
        var svc = Service();
        var (w, page) = Open(svc);
        page.SelectCodes("deu");
        Click(page.DownloadButton);
        WaitFor(() => StatusOf(page, "deu").StartsWith("Downloading", StringComparison.Ordinal));

        _dialogs.Answer = false;
        w.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.IsVisible);
        var (message, _) = Assert.Single(_dialogs.Questions);
        Assert.Equal("A language download is still running. Stop it and close Settings?", message);
        Assert.True(Assert.Single(_dialogs.YesNoDefaultNo));
        Assert.False(svc.Idle.IsCompleted);

        _dialogs.Answer = true;
        Click(w.CancelButton);
        WaitFor(() => !w.IsVisible);
        Assert.Equal(2, _dialogs.Questions.Count);
        WaitFor(() => svc.Idle.IsCompleted);
        Assert.False(File.Exists(Path.Combine(Folder, "deu.traineddata")));
    }

    // ---------- the main window ----------

    MainWindow NewMain()
    {
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, Path.Combine(_tmp, "layout")) { Width = 1024, Height = 768 };
        _mains.Add(w);
        w.Show();
        return w;
    }

    [AvaloniaFact]
    public void Show_settings_on_page_opens_ocr_page()
    {
        var w = NewMain();
        var shown = new List<SettingsWindow>();
        w.ShowSettingsHook = win => { shown.Add(win); return Task.CompletedTask; };
        w.ShowSettingsAsync("ocr-languages").GetAwaiter().GetResult();
        var win = Assert.Single(shown);
        var page = Assert.IsType<OcrLanguagesPage>(win.Pages[win.SelectedPage]);
        Assert.Same(w.OcrLanguages, page.Service);
        Assert.Same(win.Pages[^1], page);
        w.ShowSettingsAsync().GetAwaiter().GetResult();   // the parameterless call still opens on the first page
        Assert.Equal(2, shown.Count);
        Assert.Equal(0, shown[1].SelectedPage);
    }

    [AvaloniaFact]
    public void Second_call_while_open_switches_page_not_window()
    {
        var w = NewMain();
        var shown = new List<SettingsWindow>();
        var gate = new TaskCompletionSource();
        w.ShowSettingsHook = win => { shown.Add(win); return gate.Task; };
        var first = w.ShowSettingsAsync();
        Dispatcher.UIThread.RunJobs();
        var win = Assert.Single(shown);
        Assert.Equal(0, win.SelectedPage);
        var second = w.ShowSettingsAsync("ocr-languages");
        Dispatcher.UIThread.RunJobs();
        Assert.True(second.IsCompleted);
        Assert.Single(shown);
        Assert.IsType<OcrLanguagesPage>(win.Pages[win.SelectedPage]);
        gate.SetResult();
        WaitFor(() => first.IsCompleted);
        w.ShowSettingsAsync().GetAwaiter().GetResult();
        Assert.Equal(2, shown.Count);   // closed: the next call opens a new window
    }

    // ---------- stub network ----------

    /// <summary>Answers every request without a network: 404 for <see cref="Missing"/>, otherwise <see cref="Gated"/> data whose first half comes
    /// after a short pause and whose second half waits for <see cref="Release"/>.</summary>
    sealed class StubHandler : HttpMessageHandler
    {
        public readonly ConcurrentQueue<string> Requests = new();
        public readonly ConcurrentDictionary<string, byte[]> Gated = new();
        public readonly ConcurrentBag<string> Missing = new();
        readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Enqueue(url);
            var code = Path.GetFileNameWithoutExtension(request.RequestUri.AbsolutePath);
            if (Missing.Contains(code) || !Gated.TryGetValue(code, out var data))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { ReasonPhrase = "Not Found", Content = new ByteArrayContent([]) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new GatedStream(data, _gate.Task)) });
        }
    }

    sealed class GatedStream(byte[] data, Task gate) : Stream
    {
        int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos == 0) await Task.Delay(250, ct);
            else if (_pos < data.Length) await gate.WaitAsync(ct);
            else return 0;
            var end = _pos == 0 ? data.Length / 2 : data.Length;
            var n = Math.Min(buffer.Length, end - _pos);
            data.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
