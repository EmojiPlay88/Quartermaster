using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Quartermaster.Desktop.Shell;
using Quartermaster.Documents;
using Quartermaster.Render.Ocr;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Workbench;

/// <summary>
/// Scan pane and OCR of the workbench (port of WinForms WorkbenchWindow 587–756): Scan this page on the viewer's toolbar, Open scan…,
/// the Images part of the document lists, rule refs to module images and opened scans, and OCR import into the Tables tab. The scan pane
/// shares row 0 of <see cref="LeftPane"/> with the viewer and is built on first use; it is disposed when the window closes.
/// </summary>
public sealed partial class WorkbenchWindow
{
    readonly Button _scanPage = new() { Content = "Scan this page", IsEnabled = false };
    IReadOnlyList<ScanSource> _allModuleImages = Array.Empty<ScanSource>();
    IReadOnlyList<ScanSource> _moduleImages = Array.Empty<ScanSource>();   // the listed ones (≥ 64 KB unless "Show all images")
    bool _showAllImages;
    int _hiddenImages;
    ScanPane? _scan;
    ScanLoader? _scanLoader;
    IOcrRecognizer? _ownOcr;   // only when the window was given no engine: created on first use, disposed with the window

    /// <summary>The engine used when the window was given none (the main window always gives the app's engine).</summary>
    internal static Func<IOcrRecognizer> DefaultOcr = () => OcrEngines.Create();

    /// <summary>Called once from the constructor, after the tabs and the viewer exist.</summary>
    void WireScans()
    {
        _allModuleImages = ScanSessionModel.ModuleImages(_s.Archive.EntryNames);
        (_moduleImages, _hiddenImages) = ScanSessionModel.ModuleImages(_s.Archive.EntrySizes, _showAllImages);

        Viewer.AddToolItem(new Border { Width = 1, Margin = new Thickness(4, 2), Background = Brushes.Gray });
        Viewer.AddToolItem(_scanPage);
        ToolTip.SetTip(_scanPage, $"Render this page as an image ({ScanLoader.ScanDpi} DPI) to read a chart with OCR");
        _scanPage.Click += (_, _) => ScanGuard(() =>
        {
            if (Viewer.Document is not { Kind: DocumentKind.Pdf } doc) { _ = Warn("Open a PDF page first."); return; }
            _ = ShowScan(ScanSource.ModulePdf(doc.EntryName, doc.Title), Viewer.Page);
        });
        Viewer.DocumentOpened += () => _scanPage.IsEnabled = Viewer.Document?.Kind == DocumentKind.Pdf;

        OpenScanButton.IsEnabled = true;
        ToolTip.SetTip(OpenScanButton, "Open a scan or photo of a chart (image or PDF) to read it with OCR");
        OpenScanButton.Click += (_, _) => _ = ScanGuardAsync(ChooseScanFileAsync);
        Viewer.ImageDocumentChosen += i => ScanGuard(() => ChooseImage(i));
        RefreshDocLists();
    }

    /// <summary>The scan pane (built on first use: the OCR engine and the loader are only needed once a scan is shown).</summary>
    internal ScanPane Scan => EnsureScan();

    /// <summary>Whether the scan pane has been built.</summary>
    internal bool ScanCreated => _scan != null;

    /// <summary>The OCR engine the scan pane reads with: the one given to the window, else its own.</summary>
    internal IOcrRecognizer OcrEngine => _ocr ?? (_ownOcr ??= DefaultOcr());

    ScanPane EnsureScan()
    {
        if (_scan != null) return _scan;
        _scanLoader = new ScanLoader(_s.Archive.OpenEntry, _s.Archive.Sha256);
        var scan = new ScanPane(_s, ScanModel, _scanLoader, OcrEngine, _dialogs, OpenOcrLanguages) { IsVisible = false };
        _scan = scan;
        scan.DocumentChosen += i => ScanGuard(() =>
        {
            var pdfs = Viewer.PdfDocuments;
            if (i < pdfs.Count) { ShowPdf(); Viewer.Open(pdfs[i]); return; }
            ChooseImage(i - pdfs.Count);
        });
        scan.BackToPdf += () => ScanGuard(BackToPdf);
        scan.ImportRequested += (tsv, src, page) => _ = ScanGuardAsync(() => ImportScanAsync(tsv, src, page));
        Grid.SetRow(scan, 0);
        LeftPane.Children.Add(scan);
        scan.SetDocuments(Viewer.PdfDocuments.Select(d => d.Title).ToList(), ImageTitles());
        return scan;
    }

    void ScanGuard(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ScanFailed(ex); }
    }

    async Task ScanGuardAsync(Func<Task> a)
    {
        try { await a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ScanFailed(ex); }
    }

    void ScanFailed(Exception ex)
    {
        System.Diagnostics.Trace.WriteLine("workbench scan: " + ex);
        _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Scan failed: " + ex.Message, "");
    }

    // ---------- document lists ----------

    /// <summary>Module images then opened scans: the "Images" part of the document lists.</summary>
    IReadOnlyList<ScanSource> ImageSources => _moduleImages.Concat(ScanModel.Opened).ToList();

    string? ImagesToggle => ScanSessionModel.ImagesToggleText(_hiddenImages, _showAllImages);

    List<string> ImageTitles()
    {
        var titles = _moduleImages.Select(i => i.Title).Concat(ScanModel.Opened.Select(o => o.Title + " (opened)")).ToList();
        if (ImagesToggle is { } toggle) titles.Add(toggle);
        return titles;
    }

    void RefreshDocLists()
    {
        var titles = ImageTitles();
        Viewer.SetImageDocuments(titles);
        _scan?.SetDocuments(Viewer.PdfDocuments.Select(d => d.Title).ToList(), titles);
        if (_scan is { IsVisible: true, Source: { } src }) _scan.MarkSelected(DocIndex(src));
        foreach (var e in Editors) e.RefreshRuleRef();
    }

    /// <summary>An "Images" line was chosen: an image, or the last line that shows/hides the small module images.</summary>
    void ChooseImage(int i)
    {
        var list = ImageSources;
        if (i >= 0 && i < list.Count) { _ = ShowScan(list[i], 1); return; }
        if (i == list.Count && ImagesToggle != null) ToggleSmallImages();
    }

    /// <summary>"Show all images (N hidden)" / "Hide small images".</summary>
    void ToggleSmallImages()
    {
        _showAllImages = !_showAllImages;
        (_moduleImages, _hiddenImages) = ScanSessionModel.ModuleImages(_s.Archive.EntrySizes, _showAllImages);
        RefreshDocLists();
    }

    /// <summary>The source's line in the document lists (PDFs, then images), or -1.</summary>
    int DocIndex(ScanSource src)
    {
        var pdfs = Viewer.PdfDocuments;
        var pdfAt = src.Kind == ScanSourceKind.ModulePdf ? pdfs.ToList().FindIndex(d => d.EntryName == src.Key) : -1;
        if (pdfAt >= 0) return pdfAt;
        var at = ImageSources.ToList().FindIndex(x => x.Key == src.Key);
        return at >= 0 ? pdfs.Count + at : -1;
    }

    // ---------- opening ----------

    async Task ChooseScanFileAsync()
    {
        var path = await _dialogs.OpenFileAsync(this, "Open scan", ScanFilters());
        if (path == null || _closed) return;
        await OpenScanFile(path);
    }

    /// <summary>The Open scan filter (<see cref="ScanSessionModel.OpenScanFilter"/>, "name|patterns|…") as picker filters.</summary>
    static IReadOnlyList<FileFilter> ScanFilters()
    {
        var parts = ScanSessionModel.OpenScanFilter.Split('|');
        var list = new List<FileFilter>();
        for (var i = 0; i + 1 < parts.Length; i += 2)
            list.Add(new FileFilter(parts[i], parts[i + 1].Split(';').Select(p => p == "*.*" ? "*" : p).ToArray()));
        return list;
    }

    /// <summary>Opens an image or PDF file as a scan (session only); an opened PDF also joins the rulebook search for the session.</summary>
    internal async Task OpenScanFile(string path)
    {
        if (_closed) return;
        if (!File.Exists(path)) { await Warn($"'{path}' was not found."); return; }
        if (!ScanSessionModel.IsScanFile(path)) { await Warn("Choose an image (.png .jpg .jpeg .bmp .gif .tif .tiff) or a PDF."); return; }
        var before = ScanModel.Opened.Count;
        var src = ScanModel.AddFile(path);
        if (ScanModel.Opened.Count != before)
        {
            if (ScanModel.SameNameAs(src) is { } other)
                await Warn($"Another opened scan is also named \"{src.Title}\" (in {Path.GetDirectoryName(other.Key)}). Rule refs to opened scans keep only the file name (scan:{src.Title}), so they can't tell the two apart; rename one to keep their links separate.");
            if (_closed) return;
            RefreshDocLists();
            if (src.IsPdf) RestartScanIndex();
        }
        await ShowScan(src, 1);
    }

    /// <summary>How many times an opened PDF restarted the rulebook index (tests).</summary>
    internal int IndexRestarts { get; private set; }

    void RestartScanIndex()
    {
        IndexRestarts++;
        RestartIndex();
    }

    /// <summary>Switches the left pane to the scan pane showing the source (a PDF page or an image).</summary>
    Task ShowScan(ScanSource src, int page)
    {
        if (_closed) return Task.CompletedTask;
        var scan = EnsureScan();
        if (!scan.IsVisible) WidenForScan();
        Viewer.IsVisible = false;
        scan.IsVisible = true;
        scan.MarkSelected(DocIndex(src));
        return scan.ShowSource(src, page);
    }

    /// <summary>The scan pane holds the image and the OCR panel (≥ 280 px): give the left pane about 60 % of the window when it has less.</summary>
    void WidenForScan()
    {
        var cols = _split.ColumnDefinitions;
        double left = cols[0].ActualWidth, right = cols[2].ActualWidth, total = left + right;
        if (total > 0)
        {
            if (total < 400 || left >= total * 0.6) return;
        }
        else if (cols[0].Width.IsStar && cols[2].Width.IsStar && cols[0].Width.Value >= (cols[0].Width.Value + cols[2].Width.Value) * 0.6) return;
        cols[0].Width = new GridLength(60, GridUnitType.Star);
        cols[2].Width = new GridLength(40, GridUnitType.Star);
    }

    /// <summary>The viewer comes back (the scan pane's read is cancelled).</summary>
    void ShowPdf()
    {
        if (_scan is not { IsVisible: true } scan) return;
        scan.Ocr.CancelRead("Cancelled: back to the PDF.");
        scan.IsVisible = false;
        Viewer.IsVisible = true;
    }

    /// <summary>A rule ref opens a module document: the scan pane gives way.</summary>
    partial void BeforeShowDocument() => ShowPdf();

    /// <summary>Back to PDF: a scanned module page goes back to that page in the viewer.</summary>
    void BackToPdf()
    {
        if (_scan == null) return;
        var src = _scan.Source;
        var page = _scan.Page;
        ShowPdf();
        if (src is { Kind: ScanSourceKind.ModulePdf } && Viewer.PdfDocuments.FirstOrDefault(d => d.EntryName == src.Key) is { } doc) Viewer.Open(doc, page);
    }

    // ---------- import ----------

    async Task ImportScanAsync(string tsv, ScanSource src, int page)
    {
        if (_closed) return;
        var (entry, refPage) = ScanSessionModel.RuleRefFor(src, page);
        // The ref says "scan:{file}", so it can't point at a module entry; still tell the user when the names look the same.
        if (!src.InModule && _s.Archive.EntryNames.Any(e => string.Equals(Path.GetFileName(e), src.Title, StringComparison.OrdinalIgnoreCase)))
            await Warn($"The module also has a document named \"{src.Title}\". The imported table's rule ref points to the opened scan ({entry}), not to the module's copy.");
        if (_closed) return;
        await ImportTableAsync(tsv, entry, refPage);
    }

    // ---------- rule refs and search hooks ----------

    /// <summary>"Link to current page" while the scan pane shows a page: the scan's ref. False when no scan is shown.</summary>
    bool TryLinkScanPage(string label, out RuleRef? r)
    {
        r = null;
        if (_scan is not { IsVisible: true, Source: { } src }) return false;
        var (entry, page) = ScanSessionModel.RuleRefFor(src, _scan.Page);
        r = RefFor(label, entry, page);
        return true;
    }

    /// <summary>A rule ref to a module image or an opened scan: shown in the scan pane. False when it is neither.</summary>
    bool TryShowScanRule(RuleRef r)
    {
        var img = _allModuleImages.FirstOrDefault(i => i.RefEntry == r.DocumentEntry)
            ?? ScanModel.Opened.FirstOrDefault(o => string.Equals(o.RefEntry, r.DocumentEntry, StringComparison.OrdinalIgnoreCase));
        if (img == null) return false;
        ScanGuard(() => _ = ShowScan(img, Math.Max(1, r.Page)));
        return true;
    }

    /// <summary>A search hit in a PDF opened with Open scan… (keyed by its path): shown in the scan pane.</summary>
    bool TryShowOpenedScan(string key, int page)
    {
        if (ScanModel.OpenedByKey(key) is not { } src) return false;
        ScanGuard(() => _ = ShowScan(src, page));
        return true;
    }

    /// <summary>"Scan page 1" on a not-searchable search row: renders the document's first page in the scan pane.</summary>
    void ScanFirstPage(string entry)
    {
        if (_s.Model.Documents.FirstOrDefault(d => d.EntryName == entry && d.Kind == DocumentKind.Pdf) is { } doc)
        {
            ScanGuard(() => _ = ShowScan(ScanSource.ModulePdf(doc.EntryName, doc.Title), 1));
            return;
        }
        if (TryShowOpenedScan(entry, 1)) return;
        _ = Warn($"'{Path.GetFileName(entry)}' is not in this module.");
    }

    /// <summary>PDFs opened with Open scan…, for the rulebook search (session only).</summary>
    IEnumerable<(string Key, string Title, Func<Stream> Open)> OpenedScanPdfs() =>
        ScanModel.OpenedPdfs.Select(o =>
        {
            var path = o.Key;
            return (path, o.Title, (Func<Stream>)(() => File.OpenRead(path)));
        }).ToList();

    /// <summary>Documents a rule ref can point at besides the module's documents: module images and opened scans.</summary>
    IReadOnlyList<(string Entry, string Title)> ExtraRefDocs() =>
        _allModuleImages.Select(i => (i.RefEntry, i.Title)).Concat(ScanModel.Opened.Select(o => (o.RefEntry, o.Title))).ToList();

    internal IReadOnlyList<(string Key, string Title, Func<Stream> Open)> OpenedScanPdfsForTests => OpenedScanPdfs().ToList();
    internal IReadOnlyList<(string Entry, string Title)> ExtraRefDocsForTests => ExtraRefDocs();
    internal Button ScanPageButton => _scanPage;

    /// <summary>The window closed: the scan load and the OCR read are cancelled, the image pane lets go of the pixels and the bitmap is
    /// disposed; the loader and an engine the window made itself go too (an engine it was given belongs to the app).</summary>
    void OnScansClosed()
    {
        _scan?.Dispose();
        _scanLoader?.Dispose();
        (_ownOcr as IDisposable)?.Dispose();
        _ownOcr = null;
    }
}
