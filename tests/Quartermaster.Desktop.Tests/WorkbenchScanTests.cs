using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Workbench;
using Quartermaster.Documents;
using Quartermaster.Render;
using Quartermaster.Render.Ocr;
using Quartermaster.Render.Workbench;
using Quartermaster.Rules;
using Quartermaster.Rules.Tables;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Desktop.Tests;

/// <summary>
/// Port stage 4a task 12: the scan pane inside the Rules Workbench — Scan this page, Open scan…, the Images list, OCR import into a table
/// with a scan rule ref, opened PDFs for the search, Back to PDF and a quiet close. Invented module, invented files, scripted OCR.
/// </summary>
[Collection("MacFlag")]
public sealed class WorkbenchScanTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly Action<string> _prune = WorkbenchWindow.PruneCaches;
    readonly Func<Action<string>, IOcrRecognizer> _createOcr = MainWindow.CreateOcr;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-wbscan-" + Guid.NewGuid().ToString("N"));
    readonly List<Window> _windows = new();
    readonly List<MainWindow> _mains = new();
    readonly List<ModuleSession> _sessions = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();
    readonly TableOcr _ocr = new();

    public WorkbenchScanTests()
    {
        ShortcutGestures.IsMac = false;
        Directory.CreateDirectory(_tmp);
        WorkbenchWindow.PruneCaches = _ => { };   // never the real cache folders
    }

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        foreach (var w in _mains) w.ForceClose();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        WorkbenchWindow.PruneCaches = _prune;
        MainWindow.CreateOcr = _createOcr;
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static readonly Lazy<byte[]> RulesPdf = new(() => ScanKit.Pdf("Invented page one", "Invented weather page", "Invented page three"));
    static readonly Lazy<byte[]> BigPng = new(() => ScanKit.Png(200, 200, noise: true));
    static readonly Lazy<byte[]> SmallPng = new(() => ScanKit.Png(20, 20, noise: false));

    /// <summary>The phase fixture plus an invented PDF rulebook, a text document, a large module image and a small one named like the
    /// invented chart file.</summary>
    string ModulePath()
    {
        var fx = PhaseFixture.Module().EditBuildFile(x => x.Replace("</VASSAL.build.GameModule>",
                "<VASSAL.build.module.Documentation><VASSAL.build.module.documentation.HelpFile title=\"Rules\" fileName=\"rules.pdf\" fileType=\"archive\"/>"
                + "</VASSAL.build.module.Documentation></VASSAL.build.GameModule>"))
            .AddFile("rules.pdf", RulesPdf.Value)
            .AddFile("readme.txt", Encoding.UTF8.GetBytes("Invented notes."))
            .AddFile("images/big-map.png", BigPng.Value)
            .AddFile("images/invented-chart.png", SmallPng.Value);
        _fixtures.Add(fx);
        return fx.Build();
    }

    (WorkbenchWindow W, ModuleSession S) Bench(bool pack = true)
    {
        var s = ModuleSession.Open(ModulePath(), Path.Combine(_tmp, "cache" + _sessions.Count), null);
        _sessions.Add(s);
        if (pack)
        {
            var path = Path.Combine(_tmp, "pack-" + Guid.NewGuid().ToString("N") + ".qrules");
            RulesPackIo.Save(PhaseFixture.Pack(s.Archive.Sha256), path);
            s.OpenRules(path);
        }
        var wb = new WorkbenchWindow(s, _dialogs, () => Task.CompletedTask, () => Task.CompletedTask, _ocr);
        _windows.Add(wb);
        wb.Show();
        Pump();
        return (wb, s);
    }

    static DocumentDef Rules(ModuleSession s) => s.Model.Documents.First(d => d.EntryName == "rules.pdf");

    static void Loaded(WorkbenchWindow wb, Task? t = null) =>
        Assert.True(ScanKit.WaitFor(() => (t?.IsCompleted ?? true) && wb.Scan.Loading.IsCompleted && wb.Scan.Image != null), "the scan did not load: " + wb.Scan.Pane.Message);

    static bool ScanShown(WorkbenchWindow wb) => wb.ScanCreated && wb.Scan.IsVisible && !wb.Viewer.IsVisible;

    static double LeftShare(WorkbenchWindow wb) => wb.LeftPane.Bounds.Width / (wb.LeftPane.Bounds.Width + wb.Tabs.Bounds.Width);

    [AvaloniaFact]
    public void Scan_this_page_shows_scan_pane_at_viewer_page()
    {
        var (wb, s) = Bench();
        Assert.Equal("Scan this page", wb.ScanPageButton.Content);
        Assert.Equal("Render this page as an image (200 DPI) to read a chart with OCR", ToolTip.GetTip(wb.ScanPageButton));
        Assert.Contains(wb.ScanPageButton, wb.Viewer.ToolRow!.Children);
        Assert.Equal("Open a scan or photo of a chart (image or PDF) to read it with OCR", ToolTip.GetTip(wb.OpenScanButton));
        Assert.True(wb.OpenScanButton.IsEnabled);
        Assert.False(wb.ScanCreated);   // nothing built until a scan is shown
        Assert.True(LeftShare(wb) < 0.45);

        wb.Viewer.Open(Rules(s), 2);
        Pump();
        Assert.True(wb.ScanPageButton.IsEnabled);
        ScanKit.Click(wb.ScanPageButton);
        Loaded(wb);
        Assert.True(ScanShown(wb));
        Assert.Equal(ScanSource.ModulePdf("rules.pdf", "Rules"), wb.Scan.Source);
        Assert.Equal(2, wb.Scan.Page);
        Assert.Equal("of 3", wb.Scan.PageOf.Text);
        Assert.Equal(0, wb.Scan.DocsBox.SelectedIndex);   // the rulebook is marked in the scan pane's list
        Assert.True(ScanKit.WaitFor(() => LeftShare(wb) > 0.55));   // the left pane widened to about 60 %
        Assert.True(wb.Scan.Ocr.Bounds.Width >= OcrPanel.MinPanelWidth);

        // Link to current page links the scanned page
        var r = wb.LinkCurrentPage("Weather chart");
        Assert.Equal(("rules.pdf", 2), (r!.DocumentEntry, r.Page));
    }

    [AvaloniaFact]
    public void Scan_this_page_disabled_for_non_pdf()
    {
        var (wb, s) = Bench();
        Assert.False(wb.ScanPageButton.IsEnabled);   // nothing open
        wb.Viewer.Open(s.Model.Documents.First(d => d.EntryName == "readme.txt"));
        Pump();
        Assert.False(wb.ScanPageButton.IsEnabled);
        wb.Viewer.Open(Rules(s));
        Pump();
        Assert.True(wb.ScanPageButton.IsEnabled);
        wb.Viewer.Open(s.Model.Documents.First(d => d.EntryName == "readme.txt"));
        Pump();
        Assert.False(wb.ScanPageButton.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Open_scan_image_reads_and_imports_table_with_scan_rule_ref()
    {
        var (wb, s) = Bench();
        var png = ScanKit.WritePng(Path.Combine(_tmp, "files"));
        _dialogs.OpenFiles.Enqueue(png);
        ScanKit.Click(wb.OpenScanButton);
        Loaded(wb);
        Assert.Equal("Open scan", _dialogs.PickerTitles[^1]);
        Assert.True(ScanShown(wb));
        Assert.Equal("scan:invented-chart.png", wb.Scan.Source!.RefEntry);
        Assert.Contains("invented-chart.png (opened)", wb.Viewer.DocumentList!.Items.Cast<string>());
        Assert.Equal("invented-chart.png (opened)", wb.Scan.DocsBox.SelectedItem);

        // region, read, grid (the WinForms OcrSmoke flow)
        wb.Scan.Pane.CommitRegion(ScanKit.TableRegion);
        Assert.Equal(ScanKit.TableRegion, wb.ScanModel.GetRegion(wb.Scan.Source.Key, 1, new System.Drawing.Size(wb.Scan.Image!.Width, wb.Scan.Image.Height)));
        wb.Scan.Ocr.ShowGridTab();
        await wb.Scan.Ocr.ReadTextAsync();
        var expected = ScanKit.Expected();
        Assert.Equal(expected, wb.Scan.Ocr.Cells);

        // import into a new table
        var tsv = string.Join("\n", expected.Select(r => string.Join("\t", r)));
        _dialogs.ImportTargetAnswer = false;
        _dialogs.Texts.Enqueue("Invented chart");
        _dialogs.PasteAnswer = TablePaste.Parse(tsv, false, false, true);
        ScanKit.Click(wb.Scan.Ocr.ImportButton);
        Assert.True(ScanKit.WaitFor(() => s.Rules!.Tables.Any(t => t.Name == "Invented chart")));
        var table = s.Rules!.Tables.Single(t => t.Name == "Invented chart");
        var rr = s.Rules.RuleRefs.Single(r => r.Id == table.RuleRefId);
        Assert.Equal(("scan:invented-chart.png", 0), (rr.DocumentEntry, rr.Page));
        Assert.True(_dialogs.PastePrompts[^1].TsvOnly);
        Assert.Same(wb.Tables, (wb.Tabs.SelectedItem as TabItem)?.Content);
        Assert.Equal(table.Id, wb.Tables.SelectedTableId);
        // the module has an image of the same name: the user is told the ref points to the opened scan
        Assert.Contains(_dialogs.Infos, i => i.Message == "The module also has a document named \"invented-chart.png\". The imported table's rule ref points to the opened scan (scan:invented-chart.png), not to the module's copy."
            && i.Caption == "Rules Workbench");
    }

    [AvaloniaFact]
    public void Opened_pdf_joins_search()
    {
        var (wb, _) = Bench();
        var pdf = ScanKit.WritePdf(Path.Combine(_tmp, "files"));
        var open = wb.OpenScanFile(pdf);
        Loaded(wb, open);
        Assert.Equal(1, wb.IndexRestarts);
        var opened = Assert.Single(wb.OpenedScanPdfsForTests);
        Assert.Equal((Path.GetFullPath(pdf), "invented-scan.pdf"), (opened.Key, opened.Title));
        using (var st = opened.Open()) Assert.Equal(new FileInfo(pdf).Length, st.Length);
        Assert.Equal(1, wb.Scan.Page);
        Assert.Equal("of 2", wb.Scan.PageOf.Text);
        Assert.Contains(("scan:invented-scan.pdf", "invented-scan.pdf"), wb.ExtraRefDocsForTests);
        Assert.Contains(("images/big-map.png", "images/big-map.png"), wb.ExtraRefDocsForTests);
        Assert.Contains(("images/invented-chart.png", "images/invented-chart.png"), wb.ExtraRefDocsForTests);   // small images can be refs too

        // opening it again shows it without a second index build; an opened image never rebuilds the index
        Loaded(wb, wb.OpenScanFile(pdf));
        Loaded(wb, wb.OpenScanFile(ScanKit.WritePng(Path.Combine(_tmp, "files"))));
        Assert.Equal(1, wb.IndexRestarts);
        Assert.Single(wb.OpenedScanPdfsForTests);
    }

    [AvaloniaFact]
    public void Images_list_toggle_shows_small_images()
    {
        var (wb, s) = Bench();
        var (shown, hidden) = ScanSessionModel.ModuleImages(s.Archive.EntrySizes, false);
        Assert.Contains(shown, i => i.Key == "images/big-map.png");
        Assert.DoesNotContain(shown, i => i.Key == "images/invented-chart.png");
        var list = wb.Viewer.DocumentList!;
        var items = list.Items.Cast<string>().ToList();
        var toggle = $"Show all images ({hidden} hidden)";
        Assert.Equal(new[] { "Rules", Documents.DocumentViewer.ImagesSeparator }.Concat(shown.Select(i => i.Title)).Append(toggle), items);

        list.SelectedIndex = items.IndexOf(toggle);
        Pump();
        items = list.Items.Cast<string>().ToList();
        Assert.Contains("images/invented-chart.png", items);
        Assert.Equal("Hide small images", items[^1]);
        Assert.False(wb.ScanCreated && wb.Scan.IsVisible);

        // choosing an image shows it in the scan pane; its list has the same lines
        list.SelectedIndex = items.IndexOf("images/big-map.png");
        Loaded(wb);
        Assert.True(ScanShown(wb));
        Assert.Equal(ScanSource.ModuleImage("images/big-map.png"), wb.Scan.Source);
        Assert.Equal(items, wb.Scan.DocsBox.Items.Cast<string>());
        Assert.Equal("images/big-map.png", wb.Scan.DocsBox.SelectedItem);

        // the toggle in the scan pane's list hides the small images again and keeps the shown image marked
        wb.Scan.DocsBox.SelectedIndex = items.IndexOf("Hide small images");
        Pump();
        Assert.Equal(toggle, wb.Scan.DocsBox.Items.Cast<string>().Last());
        Assert.Equal("images/big-map.png", wb.Scan.DocsBox.SelectedItem);

        // the PDF in the scan pane's list goes back to the viewer
        wb.Scan.DocsBox.SelectedIndex = 0;
        Pump();
        Assert.True(wb.Viewer.IsVisible);
        Assert.False(wb.Scan.IsVisible);
        Assert.Equal("rules.pdf", wb.Viewer.Document?.EntryName);
    }

    [AvaloniaFact]
    public void Rule_ref_to_opened_scan_reopens_it()
    {
        var (wb, s) = Bench();
        var png = ScanKit.WritePng(Path.Combine(_tmp, "files"));
        Loaded(wb, wb.OpenScanFile(png));
        var r = wb.LinkCurrentPage("Invented chart")!;
        Assert.Equal(("scan:invented-chart.png", 0), (r.DocumentEntry, r.Page));
        var tableId = s.Rules!.Tables[0].Id;
        s.UpdateRules(p => p with { Tables = p.Tables.Select(t => t.Id == tableId ? t with { RuleRefId = r.Id } : t).ToList() });
        ScanKit.Click(wb.Scan.BackButton);
        Assert.False(wb.Scan.IsVisible);

        wb.ShowTable(tableId);
        Pump();
        Assert.True(wb.Tables.RefRow.IsLink);
        wb.Tables.RefRow.ClickLink();
        Loaded(wb);
        Assert.True(ScanShown(wb));
        Assert.Equal(Path.GetFullPath(png), wb.Scan.Source!.Key);

        // a ref to a module image opens it; a module document hides the scan pane again
        var img = wb.RefFor("Map", "images/big-map.png", 0)!;
        s.UpdateRules(p => p with { Tables = p.Tables.Select(t => t.Id == tableId ? t with { RuleRefId = img.Id } : t).ToList() });
        wb.Tables.RefRow.ClickLink();
        Loaded(wb);
        Assert.Equal("images/big-map.png", wb.Scan.Source!.Key);
        var doc = wb.RefFor("Rules", "rules.pdf", 3)!;
        s.UpdateRules(p => p with { Tables = p.Tables.Select(t => t.Id == tableId ? t with { RuleRefId = doc.Id } : t).ToList() });
        wb.Tables.RefRow.ClickLink();
        Pump();
        Assert.True(wb.Viewer.IsVisible);
        Assert.False(wb.Scan.IsVisible);
        Assert.Equal(3, wb.Viewer.Page);

        // a ref to a scan that is not open this session: the Open scan hint
        var other = wb.RefFor("Other", "scan:not-opened.png", 0)!;
        s.UpdateRules(p => p with { Tables = p.Tables.Select(t => t.Id == tableId ? t with { RuleRefId = other.Id } : t).ToList() });
        Assert.False(wb.Tables.RefRow.IsLink);   // a missing scan is greyed with the hint in the row itself, not clickable
        Assert.True(wb.Tables.RefRow.IsGreyed);
        Assert.Contains("open it with Open scan…", wb.Tables.RefRow.Text);
    }

    [AvaloniaFact]
    public void Back_to_pdf_returns_to_scanned_page()
    {
        var (wb, s) = Bench();
        wb.Viewer.Open(Rules(s), 2);
        Pump();
        ScanKit.Click(wb.ScanPageButton);
        Loaded(wb);
        ScanKit.Click(wb.Scan.NextButton);
        Assert.True(ScanKit.WaitFor(() => wb.Scan.Page == 3 && wb.Scan.Loading.IsCompleted));
        _ocr.Gate = new TaskCompletionSource();
        var read = wb.Scan.Ocr.ReadTextAsync();
        Assert.True(wb.Scan.Ocr.IsReading);
        ScanKit.Click(wb.Scan.BackButton);
        Assert.False(wb.Scan.Ocr.IsReading);
        Assert.Equal("Cancelled: back to the PDF.", wb.Scan.Ocr.StatusText);
        Assert.True(wb.Viewer.IsVisible);
        Assert.False(wb.Scan.IsVisible);
        Assert.Equal(("rules.pdf", 3), (wb.Viewer.Document!.EntryName, wb.Viewer.Page));
        Assert.True(ScanKit.WaitFor(() => read.IsCompleted));

        // Back to PDF from an opened image leaves the viewer as it was
        Loaded(wb, wb.OpenScanFile(ScanKit.WritePng(Path.Combine(_tmp, "files"))));
        ScanKit.Click(wb.Scan.BackButton);
        Assert.Equal(("rules.pdf", 3), (wb.Viewer.Document!.EntryName, wb.Viewer.Page));
    }

    [AvaloniaFact]
    public async Task Same_name_scan_warns()
    {
        var (wb, _) = Bench();
        var a = ScanKit.WritePng(Path.Combine(_tmp, "a"), "chart.png");
        var b = ScanKit.WritePng(Path.Combine(_tmp, "b"), "chart.png");
        Loaded(wb, wb.OpenScanFile(a));
        Assert.Empty(_dialogs.Infos);
        Loaded(wb, wb.OpenScanFile(b));
        Assert.Equal($"Another opened scan is also named \"chart.png\" (in {Path.GetDirectoryName(Path.GetFullPath(a))}). Rule refs to opened scans keep only the file name (scan:chart.png), so they can't tell the two apart; rename one to keep their links separate.",
            Assert.Single(_dialogs.Infos).Message);
        Loaded(wb, wb.OpenScanFile(b));   // already open: no second warning
        Assert.Single(_dialogs.Infos);

        var missing = Path.Combine(_tmp, "missing.png");
        await wb.OpenScanFile(missing);
        Assert.Equal($"'{missing}' was not found.", _dialogs.Infos[^1].Message);
        var txt = Path.Combine(_tmp, "notes.txt");
        File.WriteAllText(txt, "x");
        await wb.OpenScanFile(txt);
        Assert.Equal("Choose an image (.png .jpg .jpeg .bmp .gif .tif .tiff) or a PDF.", _dialogs.Infos[^1].Message);
        Assert.Equal(2, wb.ScanModel.Opened.Count);
    }

    [AvaloniaFact]
    public async Task Close_during_scan_load_and_read_is_quiet()
    {
        var unobserved = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) => { lock (unobserved) unobserved.Add(e.Exception); };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            // 1. closed while a scan loads: the late bitmap is disposed, nothing is shown or logged
            var (wb, s) = Bench();
            var gate = new TaskCompletionSource<ScanImage>();
            wb.Scan.LoadOverride = (_, _, ct) => gate.Task.WaitAsync(Timeout.InfiniteTimeSpan, CancellationToken.None);
            var load = wb.OpenScanFile(ScanKit.WritePng(Path.Combine(_tmp, "files")));
            Pump();
            Assert.False(load.IsCompleted);
            var sw = Stopwatch.StartNew();
            wb.Close();
            Pump();
            Assert.True(sw.ElapsedMilliseconds < 2000);
            var late = new SKBitmap(10, 10);
            gate.SetResult(new ScanImage(late, 1, 1, null));
            Assert.True(ScanKit.WaitFor(() => load.IsCompleted));
            Assert.Equal(IntPtr.Zero, late.Handle);
            Assert.Null(wb.Scan.Image);
            Assert.Null(wb.Scan.Pane.Image);
            Assert.DoesNotContain(s.Log.Items, e => e.Message.StartsWith("Scan failed", StringComparison.Ordinal));

            // 2. closed during an OCR read: the read ends quietly, the bitmap is released once
            var (wb2, s2) = Bench();
            Loaded(wb2, wb2.OpenScanFile(ScanKit.WritePng(Path.Combine(_tmp, "files2"))));
            var bmp = wb2.Scan.Image!;
            _ocr.Gate = new TaskCompletionSource();
            var read = wb2.Scan.Ocr.ReadTextAsync();
            Assert.True(wb2.Scan.Ocr.IsReading);
            sw.Restart();
            wb2.Close();
            Pump();
            Assert.True(sw.ElapsedMilliseconds < 2000);
            Assert.True(ScanKit.WaitFor(() => read.IsCompleted));
            await read;
            Assert.Null(wb2.Scan.Pane.Image);
            Assert.Equal(IntPtr.Zero, bmp.Handle);
            Assert.False(wb2.Scan.Ocr.HasGrid);
            _ocr.Gate.SetResult();
            Assert.DoesNotContain(s2.Log.Items, e => e.Message.StartsWith("Scan failed", StringComparison.Ordinal));
            Assert.False(_ocr.Disposed);   // the engine is the app's, not the window's

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Pump();
            lock (unobserved) Assert.Empty(unobserved);
        }
        finally { TaskScheduler.UnobservedTaskException -= handler; }
    }

    [AvaloniaFact]
    public void Main_window_shares_one_ocr_engine_and_disposes_it_at_exit()
    {
        var engines = new List<TableOcr>();
        var logs = new List<Action<string>>();
        MainWindow.CreateOcr = log => { logs.Add(log); var e = new TableOcr(); engines.Add(e); return e; };
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache-main"), null, Path.Combine(_tmp, "layout")) { Width = 1200, Height = 800 };
        _mains.Add(w);
        w.Show();
        Assert.True(w.OpenModule(ModulePath()));
        Pump();
        Assert.Empty(engines);   // created on first Workbench open
        w.ShowWorkbench();
        Pump();
        var wb = w.WorkbenchTool!;
        var engine = Assert.Single(engines);
        Assert.Same(engine, wb.OcrEngine);
        logs[0]("OCR: invented engine line.");
        Assert.Contains(w.Session!.Log.Items, e => e is { Severity: DiagnosticSeverity.Info, Category: "ocr", Message: "OCR: invented engine line." });
        wb.Close();
        Pump();
        w.ShowWorkbench();
        Pump();
        Assert.Single(engines);
        Assert.Same(engine, w.WorkbenchTool!.OcrEngine);
        Assert.False(engine.Disposed);   // closing a workbench keeps the app's engine
        w.ForceClose();
        Pump();
        Assert.True(engine.Disposed);
    }
}
