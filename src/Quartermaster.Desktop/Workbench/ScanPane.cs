using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Desktop.Documents;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Shell;
using Quartermaster.Documents;
using Quartermaster.Render;
using Quartermaster.Render.Ocr;
using Quartermaster.Vassal;
using SkiaSharp;
using SDRect = System.Drawing.Rectangle;
using SDSize = System.Drawing.Size;

namespace Quartermaster.Desktop.Workbench;

/// <summary>
/// The workbench's Image mode (port of WinForms ScanPane): a toolbar (Back to PDF, document list, page, zoom), the <see cref="ImagePane"/>
/// and the <see cref="OcrPanel"/> beside it. Shows a module image, a file opened with Open scan…, or a PDF page rendered at
/// <see cref="ScanLoader.ScanDpi"/> DPI (Scan this page). Regions are remembered per (source, page) in <see cref="ScanSessionModel"/>.
/// <para>Bitmaps: the pane owns the shown bitmap (<see cref="Image"/>). It stays alive until a newer load replaces it (or the pane is
/// disposed), and is disposed on the UI thread after the image pane has switched to the new one; the OCR reader touches it only before
/// its first await, and every read is cancelled before a new target is loaded. A load that is no longer wanted disposes its bitmap.</para>
/// </summary>
public sealed class ScanPane : UserControl, IDisposable
{
    const string StartMessage = "Open a scan, choose an image, or use Scan this page on a PDF.";

    readonly ModuleSession _s;
    readonly ScanSessionModel _model;
    readonly ScanLoader _loader;
    readonly ImagePane _pane = new();
    readonly OcrPanel _ocr;
    readonly List<int> _docMap = new();   // list index → host's document index (-1 = separator)
    SKBitmap? _image;
    int _loadGen, _pageCount = 1, _selectedDoc = -1;
    bool _syncing, _disposed;
    Task _load = Task.CompletedTask;
    CancellationTokenSource? _loadCts;

    internal Button BackButton { get; } = new() { Content = "Back to PDF" };
    internal ComboBox DocsBox { get; } = new() { Width = 190, VerticalAlignment = VerticalAlignment.Center };
    internal TextBlock PageLabel { get; } = new() { Text = "Page:", VerticalAlignment = VerticalAlignment.Center };
    internal NumericUpDown PageBox { get; } = new()
    {
        Minimum = 1, Maximum = 9999, Value = 1, Increment = 1, FormatString = "0", Width = 56, ShowButtonSpinner = false,
        VerticalAlignment = VerticalAlignment.Center,
    };
    internal TextBlock PageOf { get; } = new() { Text = "", VerticalAlignment = VerticalAlignment.Center };
    internal Button PrevButton { get; } = new() { Content = "<" };
    internal Button NextButton { get; } = new() { Content = ">" };
    internal Button ZoomOutButton { get; } = new() { Content = "−" };
    internal ComboBox ZoomBox { get; } = new() { Width = 64, VerticalAlignment = VerticalAlignment.Center };
    internal Button ZoomInButton { get; } = new() { Content = "+" };

    /// <summary>Test seam: replaces <see cref="ScanLoader.LoadAsync"/>.</summary>
    internal Func<ScanSource, int, CancellationToken, Task<ScanImage>>? LoadOverride { get; set; }

    public ScanPane(ModuleSession s, ScanSessionModel model, ScanLoader loader, IOcrRecognizer ocr, IDialogs dialogs)
    {
        _s = s;
        _model = model;
        _loader = loader;
        _ocr = new OcrPanel(ocr, model, dialogs);

        var primary = ShortcutGestures.IsMac ? "Cmd" : "Ctrl";
        ToolTip.SetTip(PrevButton, "Previous page");
        ToolTip.SetTip(NextButton, "Next page");
        ToolTip.SetTip(ZoomOutButton, $"Zoom out ({primary}+wheel)");
        ToolTip.SetTip(ZoomInButton, $"Zoom in ({primary}+wheel)");
        ZoomBox.Items.Add("Fit");
        foreach (var z in ScanSessionModel.ZoomSteps) ZoomBox.Items.Add(z + " %");
        ZoomBox.SelectedIndex = 0;

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(4, 2) };
        bar.Children.Add(BackButton);
        bar.Children.Add(Separator());
        bar.Children.Add(DocsBox);
        bar.Children.Add(PageLabel);
        bar.Children.Add(PageBox);
        bar.Children.Add(PageOf);
        bar.Children.Add(PrevButton);
        bar.Children.Add(NextButton);
        bar.Children.Add(Separator());
        bar.Children.Add(ZoomOutButton);
        bar.Children.Add(ZoomBox);
        bar.Children.Add(ZoomInButton);

        // image | splitter | OCR panel (about 42 %, never under its minimum width)
        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("58*,4,42*") };
        split.ColumnDefinitions[0].MinWidth = 160;
        split.ColumnDefinitions[2].MinWidth = OcrPanel.MinPanelWidth;
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(_ocr, 2);
        split.Children.Add(_pane);
        split.Children.Add(splitter);
        split.Children.Add(_ocr);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(split);
        Content = root;

        BackButton.Click += (_, _) => BackToPdf?.Invoke();
        DocsBox.SelectionChanged += (_, _) => OnDocChosen();
        PageBox.ValueChanged += (_, e) => { if (!_syncing && e.NewValue is { } v) GoToPage((int)v); };
        PrevButton.Click += (_, _) => GoToPage(Page - 1);
        NextButton.Click += (_, _) => GoToPage(Page + 1);
        ZoomInButton.Click += (_, _) => _pane.SetZoom(ScanSessionModel.StepZoom(_pane.Zoom, +1));
        ZoomOutButton.Click += (_, _) => _pane.SetZoom(ScanSessionModel.StepZoom(_pane.Zoom, -1));
        ZoomBox.SelectionChanged += (_, _) =>
        {
            if (_syncing) return;
            if (ZoomBox.SelectedIndex <= 0) _pane.FitToWindow();
            else _pane.SetZoom(ScanSessionModel.ZoomSteps[ZoomBox.SelectedIndex - 1] / 100.0);
        };
        _pane.ZoomChanged += SyncZoom;
        _pane.SelectionChanged += OnRegionChanged;
        _ocr.Target = () => !_disposed && _image != null && Source != null && _load.IsCompleted
            ? new OcrTarget(_image, _pane.SelectedRegion, Source, Page) : null;
        _ocr.ImportRequested += (tsv, src, page) => ImportRequested?.Invoke(tsv, src, page);
        _pane.Message = StartMessage;
        SyncPageControls();
    }

    static Border Separator() => new() { Width = 1, Margin = new Thickness(4, 2), Background = Brushes.Gray };

    public ScanSource? Source { get; private set; }
    public int Page { get; private set; } = 1;
    /// <summary>The shown bitmap (owned by the pane); null when nothing is shown.</summary>
    public SKBitmap? Image => _image;
    public OcrPanel Ocr => _ocr;
    public ImagePane Pane => _pane;
    /// <summary>The current load (image decode or page render); completed when idle.</summary>
    public Task Loading => _load;
    /// <summary>A note about the shown image (multi-page TIFF), null when none.</summary>
    public string? Note { get; private set; }

    public event Action? BackToPdf;
    /// <summary>The user picked a document in the list: the host's document index (PDFs first, then images).</summary>
    public event Action<int>? DocumentChosen;
    public event Action<string, ScanSource, int>? ImportRequested;

    // ---------- document list ----------

    /// <summary>The document list: the same items as the PDF viewer's (PDFs, then an "Images" separator and the images).</summary>
    public void SetDocuments(IReadOnlyList<string> pdfTitles, IReadOnlyList<string> imageTitles)
    {
        _syncing = true;
        try
        {
            DocsBox.Items.Clear();
            _docMap.Clear();
            for (var i = 0; i < pdfTitles.Count; i++) { DocsBox.Items.Add(pdfTitles[i]); _docMap.Add(i); }
            if (imageTitles.Count > 0) { DocsBox.Items.Add(DocumentViewer.ImagesSeparator); _docMap.Add(-1); }
            for (var i = 0; i < imageTitles.Count; i++) { DocsBox.Items.Add(imageTitles[i]); _docMap.Add(pdfTitles.Count + i); }
        }
        finally { _syncing = false; }
    }

    /// <summary>Selects the host's document index in the list without raising <see cref="DocumentChosen"/>.</summary>
    void SelectDocument(int hostIndex)
    {
        _syncing = true;
        try { DocsBox.SelectedIndex = _docMap.IndexOf(hostIndex); }
        finally { _syncing = false; }
    }

    void OnDocChosen()
    {
        if (_syncing) return;
        var i = DocsBox.SelectedIndex;
        if (i < 0 || i >= _docMap.Count) return;
        if (_docMap[i] < 0) { SelectDocument(_selectedDoc); return; }   // the separator is not a document
        var host = _selectedDoc = _docMap[i];
        // After the list's own selection change has finished: the host may refill this list (the Images toggle).
        Dispatcher.UIThread.Post(() => { if (!_disposed) DocumentChosen?.Invoke(host); });
    }

    /// <summary>Marks the host's document index as the shown one (no <see cref="DocumentChosen"/>).</summary>
    public void MarkSelected(int hostIndex)
    {
        _selectedDoc = hostIndex;
        SelectDocument(hostIndex);
    }

    // ---------- loading ----------

    /// <summary>
    /// Shows a source (a PDF page rendered at 200 DPI, or the image); a running read is cancelled. A superseded load is cancelled and its
    /// result dropped; the shown bitmap stays alive until the new one replaces it.
    /// </summary>
    public Task ShowSource(ScanSource source, int page = 1)
    {
        if (_disposed) return Task.CompletedTask;
        _ocr.CancelRead("Cancelled: another image was opened.");
        page = source.IsPdf ? Math.Max(1, page) : 1;
        if (Source == source && Page == page && _image != null && _load.IsCompleted) return _load;
        if (Source?.Key != source.Key) _pageCount = 1;   // unknown until the new document is open
        Source = source;
        Page = page;
        SyncPageControls();
        try { _loadCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        var cts = _loadCts = new CancellationTokenSource();
        _load = LoadAsync(source, page, ++_loadGen, cts);
        return _load;
    }

    void GoToPage(int page)
    {
        if (Source is not { IsPdf: true } src) { SyncPageControls(); return; }
        page = Math.Clamp(page, 1, Math.Max(1, _pageCount));
        if (page == Page && _image != null) { SyncPageControls(); return; }
        _ = ShowSource(src, page);
    }

    void SyncPageControls()
    {
        var pdf = Source?.IsPdf == true;
        PageLabel.IsVisible = PageBox.IsVisible = PageOf.IsVisible = PrevButton.IsVisible = NextButton.IsVisible = pdf;
        _syncing = true;
        try
        {
            PageBox.Maximum = Math.Max(1, Math.Max(_pageCount, Page));
            PageBox.Value = Math.Clamp(Page, 1, (int)PageBox.Maximum);
        }
        finally { _syncing = false; }
        PageOf.Text = pdf ? $"of {_pageCount}" : "";
        PrevButton.IsEnabled = pdf && Page > 1;
        NextButton.IsEnabled = pdf && Page < _pageCount;
    }

    async Task LoadAsync(ScanSource src, int page, int gen, CancellationTokenSource cts)
    {
        try { await LoadCoreAsync(src, page, gen, cts.Token); }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) _loadCts = null;
            cts.Dispose();
        }
    }

    async Task LoadCoreAsync(ScanSource src, int page, int gen, CancellationToken ct)
    {
        _pane.SetImage(null, null);
        _pane.Message = src.IsPdf ? $"Rendering page {page} of {src.Title}…" : $"Opening {src.Title}…";
        ScanImage loaded;
        try
        {
            loaded = LoadOverride is { } over ? await over(src, page, ct) : await _loader.LoadAsync(src, page, ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (gen != _loadGen || _disposed || ex is OperationCanceledException) return;
            if (ex is not (InvalidDataException or FileNotFoundException or DirectoryNotFoundException))
            {
                System.Diagnostics.Trace.WriteLine("scan load failed: " + ex);
                _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Scan failed: " + ex.Message, "");
            }
            _pane.Message = ScanLoader.FailureMessage(src, page, ex);
            Note = null;
            SwapImage(null);
            SyncPageControls();
            return;
        }
        if (gen != _loadGen || _disposed) { loaded.Bitmap.Dispose(); return; }   // a newer source or page was asked for
        var bmp = loaded.Bitmap;
        _pageCount = Math.Max(1, loaded.PageCount);
        Page = loaded.Page;
        SyncPageControls();
        _pane.Message = null;
        _pane.SetImage(bmp, _model.GetRegion(src.Key, Page, new SDSize(bmp.Width, bmp.Height)));
        SwapImage(bmp);   // the old bitmap goes only now, after the image pane has let go of it
        SyncZoom();
        Note = loaded.Note;
        if (loaded.Note is { } note)
        {
            System.Diagnostics.Trace.WriteLine("scan: " + note);
            _ocr.ShowNote(note);
        }
    }

    void SwapImage(SKBitmap? next)
    {
        var old = _image;
        _image = next;
        if (!ReferenceEquals(old, next)) old?.Dispose();
    }

    void OnRegionChanged(SDRect? region)
    {
        if (Source == null || _image == null || !_load.IsCompleted) return;
        _ocr.CancelRead("Cancelled: the region changed.");
        _model.SetRegion(Source.Key, Page, new SDSize(_image.Width, _image.Height), region);
    }

    void SyncZoom()
    {
        _syncing = true;
        try
        {
            if (_pane.FitMode) ZoomBox.SelectedIndex = 0;
            else
            {
                var pct = (int)Math.Round(_pane.Zoom * 100);
                var i = ScanSessionModel.ZoomSteps.ToList().IndexOf(pct);
                ZoomBox.SelectedIndex = i >= 0 ? i + 1 : -1;
                ZoomBox.PlaceholderText = i >= 0 ? null : pct + " %";
            }
        }
        finally { _syncing = false; }
    }

    /// <summary>Cancels the load and the read, lets the image pane go of the pixels (native memory, not GC-tracked) and disposes the bitmap.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loadGen++;
        try { _loadCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        _ocr.CancelRead();
        _model.CancelRead();
        _pane.SetImage(null, null);
        SwapImage(null);
    }
}
