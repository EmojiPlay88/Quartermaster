using Quartermaster.Ocr;
using Quartermaster.Render;
using Quartermaster.Render.Ocr;

namespace Quartermaster.App;

/// <summary>
/// The workbench's Image mode: a toolbar (Back to PDF, document list, page, zoom), the <see cref="ImagePane"/> and the
/// <see cref="OcrPanel"/> on a vertical splitter. Shows a module image, a file opened with Open scan…, or a PDF page rendered
/// at 200 DPI (Scan this page). Regions are remembered per (source, page) in <see cref="ScanSessionModel"/>.
/// </summary>
sealed class ScanPane : UserControl
{
    public const int ScanDpi = 200;

    readonly ModuleSession _s;
    readonly ScanSessionModel _model;
    readonly ToolStrip _bar = new() { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
    readonly ToolStripButton _back = new("Back to PDF") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    readonly ToolStripComboBox _docs = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 190 };
    readonly ToolStripLabel _pageLabel = new("Page:");
    readonly NumericUpDown _pageBox = new() { Minimum = 1, Maximum = 9999, Value = 1, Width = 56 };
    readonly ToolStripControlHost _pageHost;
    readonly ToolStripLabel _pageOf = new("");
    readonly ToolStripButton _prev = new("<") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Previous page" };
    readonly ToolStripButton _next = new(">") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Next page" };
    readonly ToolStripButton _zoomOut = new("−") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Zoom out (Ctrl+wheel)" };
    readonly ToolStripComboBox _zoom = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 64 };
    readonly ToolStripButton _zoomIn = new("+") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Zoom in (Ctrl+wheel)" };
    readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.Panel2 };
    readonly ImagePane _pane = new() { Dock = DockStyle.Fill };
    readonly OcrPanel _ocr;
    readonly List<int> _docMap = new(); // combo index → host's document index (-1 = separator)
    Bitmap? _image;
    int _loadGen, _pageCount = 1;
    bool _syncing, _splitSet;
    Task _load = Task.CompletedTask;

    public ScanPane(ModuleSession s, ScanSessionModel model, OcrService ocr)
    {
        _s = s;
        _model = model;
        _ocr = new OcrPanel(ocr, model) { Dock = DockStyle.Fill };
        _pageHost = new ToolStripControlHost(_pageBox);
        _bar.Items.AddRange(new ToolStripItem[] { _back, new ToolStripSeparator(), _docs, _pageLabel, _pageHost, _pageOf, _prev, _next, new ToolStripSeparator(), _zoomOut, _zoom, _zoomIn });
        _zoom.Items.Add("Fit");
        foreach (var z in ScanSessionModel.ZoomSteps) _zoom.Items.Add(z + " %");
        _zoom.SelectedIndex = 0;

        _split.Panel1.Controls.Add(_pane);
        _split.Panel2.Controls.Add(_ocr);
        Controls.Add(_split);
        Controls.Add(_bar);

        _back.Click += (_, _) => BackToPdf?.Invoke();
        _docs.SelectedIndexChanged += (_, _) => OnDocChosen();
        _pageBox.ValueChanged += (_, _) => { if (!_syncing) GoToPage((int)_pageBox.Value); };
        _prev.Click += (_, _) => GoToPage(Page - 1);
        _next.Click += (_, _) => GoToPage(Page + 1);
        _zoomIn.Click += (_, _) => _pane.SetZoom(ScanSessionModel.StepZoom(_pane.Zoom, +1));
        _zoomOut.Click += (_, _) => _pane.SetZoom(ScanSessionModel.StepZoom(_pane.Zoom, -1));
        _zoom.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing) return;
            if (_zoom.SelectedIndex <= 0) _pane.FitToWindow();
            else _pane.SetZoom(ScanSessionModel.ZoomSteps[_zoom.SelectedIndex - 1] / 100.0);
        };
        _pane.ZoomChanged += SyncZoom;
        _pane.SelectionChanged += OnRegionChanged;
        _ocr.Target = () => _image != null && Source != null && _load.IsCompleted
            ? new OcrTarget(_image, _pane.SelectedRegion, Source, Page) : null;
        _ocr.ImportRequested += (tsv, src, page) => ImportRequested?.Invoke(tsv, src, page);
        _pane.Message = "Open a scan, choose an image, or use Scan this page on a PDF.";
        Resize += (_, _) => SetSplit();
        SyncPageControls();
    }

    public ScanSource? Source { get; private set; }
    public int Page { get; private set; } = 1;
    public Bitmap? Image => _image;
    public OcrPanel Ocr => _ocr;
    public ImagePane Pane => _pane;
    /// <summary>The current load (image decode or page render); completed when idle.</summary>
    public Task Loading => _load;

    public event Action? BackToPdf;
    /// <summary>The user picked a document in the list: the host's document index (PDFs first, then images).</summary>
    public event Action<int>? DocumentChosen;
    public event Action<string, ScanSource, int>? ImportRequested;

    void SetSplit()
    {
        if (_splitSet || _split.Width < OcrPanel.MinPanelWidth + 160) return;
        _splitSet = true;
        // The OCR panel's 280 px minimum is set only once the container is wide enough (setting it earlier throws).
        try
        {
            _split.SplitterDistance = Math.Max(_split.Panel1MinSize, _split.Width - Math.Max(OcrPanel.MinPanelWidth, _split.Width * 42 / 100));
            _split.Panel2MinSize = OcrPanel.MinPanelWidth;
        }
        catch (InvalidOperationException) { }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetSplit();
    }

    /// <summary>The document list: the same items as the PDF viewer's (PDFs, then an "Images" separator and the images).</summary>
    public void SetDocuments(IReadOnlyList<string> pdfTitles, IReadOnlyList<string> imageTitles)
    {
        _syncing = true;
        try
        {
            _docs.Items.Clear();
            _docMap.Clear();
            for (var i = 0; i < pdfTitles.Count; i++) { _docs.Items.Add(pdfTitles[i]); _docMap.Add(i); }
            if (imageTitles.Count > 0) { _docs.Items.Add(PdfViewerControl.ImagesSeparator); _docMap.Add(-1); }
            for (var i = 0; i < imageTitles.Count; i++) { _docs.Items.Add(imageTitles[i]); _docMap.Add(pdfTitles.Count + i); }
        }
        finally { _syncing = false; }
    }

    /// <summary>Selects the host's document index in the list without raising <see cref="DocumentChosen"/>.</summary>
    public void SelectDocument(int hostIndex)
    {
        _syncing = true;
        try { _docs.SelectedIndex = _docMap.IndexOf(hostIndex); }
        finally { _syncing = false; }
    }

    int _selectedDoc = -1;

    void OnDocChosen()
    {
        if (_syncing) return;
        var i = _docs.SelectedIndex;
        if (i < 0 || i >= _docMap.Count) return;
        if (_docMap[i] < 0) { SelectDocument(_selectedDoc); return; } // the separator is not a document
        _selectedDoc = _docMap[i];
        DocumentChosen?.Invoke(_docMap[i]);
    }

    public void MarkSelected(int hostIndex)
    {
        _selectedDoc = hostIndex;
        SelectDocument(hostIndex);
    }

    // ---------- loading ----------

    /// <summary>Shows a source (PDF page rendered at 200 DPI, or the image); a running read is cancelled.</summary>
    public Task ShowSource(ScanSource source, int page = 1)
    {
        _ocr.CancelRead("Cancelled: another image was opened.");
        page = source.IsPdf ? Math.Max(1, page) : 1;
        if (Source == source && Page == page && _image != null && _load.IsCompleted) return _load;
        Source = source;
        Page = page;
        SyncPageControls();
        _load = LoadAsync(source, page, ++_loadGen);
        return _load;
    }

    void GoToPage(int page)
    {
        if (Source is not { IsPdf: true } src) return;
        page = Math.Clamp(page, 1, Math.Max(1, _pageCount));
        if (page == Page && _image != null) { SyncPageControls(); return; }
        ShowSource(src, page);
    }

    void SyncPageControls()
    {
        var pdf = Source?.IsPdf == true;
        _pageLabel.Visible = _pageHost.Visible = _pageOf.Visible = _prev.Visible = _next.Visible = pdf;
        _syncing = true;
        try
        {
            _pageBox.Maximum = Math.Max(1, Math.Max(_pageCount, Page));
            _pageBox.Value = Math.Clamp(Page, 1, (int)_pageBox.Maximum);
        }
        finally { _syncing = false; }
        _pageOf.Text = pdf ? $"of {_pageCount}" : "";
        _prev.Enabled = pdf && Page > 1;
        _next.Enabled = pdf && Page < _pageCount;
    }

    async Task LoadAsync(ScanSource src, int page, int gen)
    {
        _pane.SetImage(null, null);
        _pane.Message = src.IsPdf ? $"Rendering page {page} of {src.Title}…" : $"Opening {src.Title}…";
        SwapImage(null);
        Bitmap? bmp = null;
        string? note = null;
        int pages = 1;
        try
        {
            var path = await Task.Run(() => PathFor(src));
            if (src.IsPdf)
            {
                pages = await PdfPageRender.PageCountAsync(path);
                if (page > pages) page = pages;
                bmp = await PdfPageRender.RenderAsync(path, page, ScanDpi);
            }
            else
            {
                (bmp, var count) = await Task.Run(() => { var b = ImageLoad.Load(path, out var n); return (b, n); });
                if (count > 1) note = $"This TIFF has {count} pages; only the first is shown.";
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            bmp?.Dispose();
            if (gen != _loadGen || IsDisposed) return;
            var msg = ex switch
            {
                InvalidDataException => ex.Message,
                FileNotFoundException or DirectoryNotFoundException => $"{src.Title} was not found.",
                ArgumentOutOfRangeException when src.IsPdf => $"{src.Title} has no page {page}.",
                _ when src.IsPdf => PdfPageRender.CantOpenMessage,
                _ => "This file can't be opened as an image.",
            };
            if (ex is not (InvalidDataException or FileNotFoundException)) Program.AppLog("scan load failed: " + ex);
            _pane.Message = msg;
            return;
        }
        if (gen != _loadGen || IsDisposed) { bmp.Dispose(); return; } // a newer source or page was asked for
        _pageCount = Math.Max(1, pages);
        Page = page;
        SyncPageControls();
        SwapImage(bmp);
        _pane.Message = null;
        _pane.SetImage(bmp, _model.GetRegion(src.Key, page, bmp.Size));
        SyncZoom();
        if (note != null) Program.AppLog("scan: " + note);
        _note = note;
        _pane.Invalidate();
    }

    string? _note;

    /// <summary>A note about the shown image (multi-page TIFF), null when none.</summary>
    public string? Note => _note;

    /// <summary>The file to load: module entries are copied to the scan cache, opened files are used in place.</summary>
    string PathFor(ScanSource src)
    {
        if (!src.InModule) return src.Key;
        var ext = Path.GetExtension(src.Key);
        return ScanCache.Extract(() => _s.Archive.OpenEntry(src.Key), ext);
    }

    void SwapImage(Bitmap? next)
    {
        var old = _image;
        _image = next;
        if (!ReferenceEquals(old, next)) old?.Dispose();
    }

    void OnRegionChanged(Rectangle? region)
    {
        if (Source == null || _image == null) return;
        _ocr.CancelRead("Cancelled: the region changed.");
        _model.SetRegion(Source.Key, Page, _image.Size, region);
    }

    void SyncZoom()
    {
        _syncing = true;
        try
        {
            if (_pane.FitMode) _zoom.SelectedIndex = 0;
            else
            {
                var pct = (int)Math.Round(_pane.Zoom * 100);
                var i = ScanSessionModel.ZoomSteps.ToList().IndexOf(pct);
                if (i >= 0) _zoom.SelectedIndex = i + 1;
                else { _zoom.SelectedIndex = -1; _zoom.Text = pct + " %"; }
            }
        }
        finally { _syncing = false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loadGen++;
            _model.CancelRead();
            _pane.SetImage(null, null);
            SwapImage(null);
        }
        base.Dispose(disposing);
    }
}
