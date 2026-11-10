using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Workbench;
using Quartermaster.Render;
using Quartermaster.Render.Search;
using Quartermaster.Render.Workbench;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 4a task 13: the rulebook search and the scan pane together — a PDF opened with Open scan… is searched, its hits open in the
/// scan pane at the page, and "Use as rule ref" on such a hit gives a scan:{file} ref that reopens it.</summary>
[Collection("MacFlag")]
public sealed class WorkbenchSearchScanTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly Action<string> _prune = WorkbenchWindow.PruneCaches;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-wbsearchscan-" + Guid.NewGuid().ToString("N"));
    readonly List<Window> _windows = new();
    readonly List<ModuleSession> _sessions = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();
    readonly TableOcr _ocr = new();

    public WorkbenchSearchScanTests()
    {
        ShortcutGestures.IsMac = false;
        Directory.CreateDirectory(_tmp);
        WorkbenchWindow.PruneCaches = _ => { };
    }

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        WorkbenchWindow.PruneCaches = _prune;
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    [AvaloniaFact]
    public void Search_hit_in_opened_scan_pdf_opens_scan_pane_and_ref_reopens_it()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), null);
        _sessions.Add(s);
        var packPath = Path.Combine(_tmp, "pack.qrules");
        RulesPackIo.Save(PhaseFixture.Pack(s.Archive.Sha256), packPath);
        s.OpenRules(packPath);
        var wb = new WorkbenchWindow(s, _dialogs, () => Task.CompletedTask, () => Task.CompletedTask, _ocr);
        _windows.Add(wb);
        wb.Show();
        Pump();

        var dir = Path.Combine(_tmp, "files");
        Directory.CreateDirectory(dir);
        var pdf = Path.Combine(dir, "invented-scan.pdf");
        File.WriteAllBytes(pdf, ScanKit.Pdf("Invented intro page", "Invented zebra ration page"));

        // Open scan… through the button and the file dialog, as a user would.
        _dialogs.OpenFiles.Enqueue(pdf);
        ScanKit.Click(wb.OpenScanButton);
        Assert.True(ScanKit.WaitFor(() => wb.ScanCreated && wb.Scan.Loading.IsCompleted && wb.Scan.Image != null), "the scan did not load");
        Assert.True(ScanKit.WaitFor(() => wb.IndexRestarts == 1 && wb.SearchIndexForTests != null && wb.SearchButton.IsEnabled && wb.StatusText == "Rulebooks indexed: 1 searchable.", 15000), "the index was not rebuilt");

        // Back to the module's own PDF view so the hit has to bring the scan pane back.
        ScanKit.Click(wb.Scan.BackButton);
        Assert.False(wb.Scan.IsVisible);

        wb.RunSearch("zebra");
        Assert.True(ScanKit.WaitFor(() => wb.SearchPanelForTests.Rows.Any(r => r.Kind == SearchRowKind.Hit), 10000), "no hit: " + wb.SearchPanelForTests.Title);
        var hit = wb.SearchPanelForTests.Rows.First(r => r.Kind == SearchRowKind.Hit);
        Assert.Equal(2, hit.Page);
        Assert.True(wb.SearchPanelForTests.SelectHit(2));
        wb.SearchPanelForTests.ActivateSelected();
        Assert.True(ScanKit.WaitFor(() => wb.Scan.IsVisible && wb.Scan.Image != null && wb.Scan.Loading.IsCompleted));
        Assert.Equal(Path.GetFullPath(pdf), wb.Scan.Source!.Key);
        Assert.Equal(2, wb.Scan.Page);

        // Use as rule ref on that hit: scan:{file}, never the path.
        var table = s.Rules!.Tables[0];
        wb.ShowTable(table.Id);
        Pump();
        Assert.True(wb.SearchPanelForTests.SelectHit(2));
        Assert.True(wb.SearchPanelForTests.CanUseRuleRef!());
        Assert.True(wb.SearchPanelForTests.PrepareMenuForTests());
        wb.SearchPanelForTests.UseRefItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump();
        var rid = s.Rules!.Tables.Single(t => t.Id == table.Id).RuleRefId;
        var r = s.Rules.RuleRefs.Single(x => x.Id == rid);
        Assert.Equal(("scan:invented-scan.pdf", 2), (r.DocumentEntry, r.Page));

        // It reopens the scan at the page.
        ScanKit.Click(wb.Scan.BackButton);
        Assert.False(wb.Scan.IsVisible);
        Pump();
        Assert.True(wb.Tables.RefRow.IsLink);
        wb.Tables.RefRow.ClickLink();
        Assert.True(ScanKit.WaitFor(() => wb.Scan.IsVisible && wb.Scan.Image != null && wb.Scan.Loading.IsCompleted));
        Assert.Equal(Path.GetFullPath(pdf), wb.Scan.Source!.Key);
        Assert.Equal(2, wb.Scan.Page);
    }
}
