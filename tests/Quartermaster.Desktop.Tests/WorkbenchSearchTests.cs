using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Shell;
using Quartermaster.Desktop.Workbench;
using Quartermaster.Render;
using Quartermaster.Render.Search;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 4a task 11: rulebook search in the Rules Workbench over an invented two-page rulebook (headless, fake dialogs).</summary>
[Collection("MacFlag")]
public sealed class WorkbenchSearchTests : IDisposable
{
    const string Entry = "invented-rules.pdf";

    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly Action<string> _prune = WorkbenchWindow.PruneCaches;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-wb-search-" + Guid.NewGuid().ToString("N"));
    readonly string? _oldRoot = Environment.GetEnvironmentVariable(AppDataRoot.EnvVar);
    readonly List<Window> _windows = new();
    readonly List<ModuleSession> _sessions = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();

    public WorkbenchSearchTests()
    {
        ShortcutGestures.IsMac = false;
        Directory.CreateDirectory(_tmp);
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _tmp);   // the index cache lives in the temp folder
        WorkbenchWindow.PruneCaches = _ => { };
    }

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        WorkbenchWindow.PruneCaches = _prune;
        ShortcutGestures.IsMac = _wasMac;
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _oldRoot);
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static bool WaitFor(Func<bool> condition, int ms = 10000)
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

    /// <summary>An invented rulebook: a contents page that mentions supply, and the page that explains it.</summary>
    static byte[] Rulebook(int extraPages = 0)
    {
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        void Page(params string[] lines)
        {
            var pg = b.AddPage(PageSize.A4);
            double y = 800;
            foreach (var l in lines) { pg.AddText(l, 10, new PdfPoint(40, y), font); y -= 14; }
        }
        Page("Invented Rules - Contents", "Supply and movement are covered on the next page of this invented booklet.");
        Page("Supply", "Each unit traces supply to a depot at the start of its turn.", "A unit out of supply moves at half speed.");
        for (var i = 0; i < extraPages; i++) Page($"Filler page {i}", "Invented filler text about nothing in particular, repeated for bulk. " + string.Concat(Enumerable.Repeat("lorem ipsum ", 40)));
        return b.Build();
    }

    string ModulePath(int extraPages = 0)
    {
        var fx = PhaseFixture.Module().EditBuildFile(x => x.Replace("</VASSAL.build.GameModule>",
                "<VASSAL.build.module.Documentation><VASSAL.build.module.documentation.HelpFile title=\"Invented rules\" fileName=\"" + Entry + "\" fileType=\"archive\"/>"
                + "</VASSAL.build.module.Documentation></VASSAL.build.GameModule>"))
            .AddFile(Entry, Rulebook(extraPages));
        _fixtures.Add(fx);
        return fx.Build();
    }

    (WorkbenchWindow W, ModuleSession S) Bench(bool waitIndex = true, int extraPages = 0, bool mac = false)
    {
        ShortcutGestures.IsMac = mac;
        var s = ModuleSession.Open(ModulePath(extraPages), Path.Combine(_tmp, "cache" + _sessions.Count), null);
        _sessions.Add(s);
        var path = Path.Combine(_tmp, "pack-" + Guid.NewGuid().ToString("N") + ".qrules");
        RulesPackIo.Save(PhaseFixture.Pack(s.Archive.Sha256), path);
        s.OpenRules(path);
        var wb = new WorkbenchWindow(s, _dialogs, () => Task.CompletedTask, () => Task.CompletedTask);
        _windows.Add(wb);
        wb.Show();
        Pump();
        if (waitIndex) Assert.True(WaitFor(() => wb.SearchIndexForTests != null), "the index was not built");
        return (wb, s);
    }

    static void Search(WorkbenchWindow wb, string q)
    {
        wb.SearchBox.Text = q;
        wb.SearchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    [AvaloniaFact]
    public void Search_supply_lists_pages_and_title()
    {
        var (wb, _) = Bench();
        var panel = wb.SearchPanelForTests;
        Assert.True(panel.Collapsed);
        Assert.Equal("Rulebook search: type in the box above (Ctrl+Shift+F)", panel.Title);
        Assert.Equal("Search the rulebooks (Ctrl+Shift+F)", wb.SearchBox.PlaceholderText);
        Assert.True(WaitFor(() => wb.SearchButton.IsEnabled));
        Assert.Equal("Rulebooks indexed: 1 searchable.", wb.StatusText);

        Search(wb, "supply");
        Assert.Equal("Rulebook search: supply (2 pages)", panel.Title);
        Assert.False(panel.Collapsed);
        Assert.Equal(new[] { SearchRowKind.Document, SearchRowKind.Hit, SearchRowKind.Hit }, panel.Rows.Select(r => r.Kind));
        Assert.Equal(new[] { 1, 2 }, panel.Rows.Where(r => r.Kind == SearchRowKind.Hit).Select(r => r.Page));
        Assert.True(wb.SearchSplitter.IsVisible);
        Assert.True(wb.SearchSplitter.IsEnabled);

        Search(wb, "zzzqq");
        Assert.Equal("Rulebook search: zzzqq (0 pages)", panel.Title);
        Search(wb, "supply");
        panel.Collapsed = true;
        Assert.False(wb.SearchSplitter.IsEnabled);
        Assert.Equal(SearchPanel.HeaderHeight, panel.Height);
    }

    [AvaloniaFact]
    public void Enter_in_the_box_runs_the_search()
    {
        var (wb, _) = Bench();
        wb.SearchBox.Text = "supply";
        wb.SearchBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Pump();
        Assert.Equal("Rulebook search: supply (2 pages)", wb.SearchPanelForTests.Title);
    }

    [AvaloniaFact]
    public void Query_before_the_index_is_ready_runs_when_it_is()
    {
        var (wb, _) = Bench(waitIndex: false, extraPages: 150);
        wb.RunSearch("supply");
        Assert.True(wb.SearchPanelForTests.Title is "Rulebook search: supply (runs when indexing finishes)" or "Rulebook search: supply (2 pages)");
        Assert.True(WaitFor(() => wb.SearchPanelForTests.Title == "Rulebook search: supply (2 pages)"));
    }

    [AvaloniaFact]
    public void Hit_opens_viewer_page_with_highlights()
    {
        var (wb, _) = Bench();
        Search(wb, "supply");
        var panel = wb.SearchPanelForTests;
        Assert.True(panel.SelectHit(2, Entry));
        panel.ActivateSelected();
        Pump();
        Assert.Equal(Entry, wb.Viewer.Document?.EntryName);
        Assert.Equal(2, wb.Viewer.Page);
        Assert.Equal(SearchPanelModel.ViewerSearchTerm("supply"), wb.Viewer.SearchTerm);
        Assert.Equal("supply", wb.Viewer.SearchTerm);
    }

    [AvaloniaFact]
    public void Use_as_rule_ref_sets_selected_formula_ref()
    {
        var (wb, s) = Bench();
        Search(wb, "supply");
        var panel = wb.SearchPanelForTests;
        Assert.True(panel.SelectHit(2, Entry));

        // No editor item selected (Properties tab): disabled.
        wb.Tabs.SelectedItem = wb.Tabs.Items.Cast<TabItem>().Single(t => (string?)t.Header == "Alerts");
        Pump();
        Assert.False(panel.CanUseRuleRef!());

        wb.ShowFormula("f-water");
        Pump();
        Assert.True(panel.CanUseRuleRef!());
        Assert.True(panel.PrepareMenuForTests());
        Assert.True(panel.UseRefItem.IsEnabled);
        panel.UseRefItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump();

        var f = s.Rules!.Formulas.Single(x => x.Id == "f-water");
        Assert.NotNull(f.RuleRefId);
        var r = s.Rules.RuleRefs.Single(x => x.Id == f.RuleRefId);
        Assert.Equal((Entry, 2), (r.DocumentEntry, r.Page));
    }

    [AvaloniaFact]
    public void Find_in_rulebook_runs_search()
    {
        var (wb, _) = Bench();
        Assert.NotNull(wb.Formulas.FindInRulebook);
        wb.Formulas.FindInRulebook!("supply");
        Pump();
        Assert.Equal("supply", wb.SearchBox.Text);
        Assert.Equal("Rulebook search: supply (2 pages)", wb.SearchPanelForTests.Title);
        Assert.Equal(3, wb.SearchPanelForTests.Rows.Count);
    }

    [AvaloniaFact]
    public void Search_shortcut_is_cmd_shift_f_on_mac()
    {
        var (wb, _) = Bench(mac: true);
        Assert.Equal("Search the rulebooks (⇧⌘F)", wb.SearchBox.PlaceholderText);
        Assert.Equal("Rulebook search: type in the box above (⇧⌘F)", wb.SearchPanelForTests.Title);
        wb.SearchBox.Text = "supply";
        wb.Tabs.Focus();
        Pump();

        wb.KeyPress(Key.F, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.F, null);   // Ctrl is not the primary key on mac
        Pump();
        Assert.False(wb.SearchBox.IsFocused);

        wb.KeyPress(Key.F, RawInputModifiers.Meta | RawInputModifiers.Shift, PhysicalKey.F, null);
        Pump();
        Assert.True(wb.SearchBox.IsFocused);
        Assert.Equal((0, 6), (Math.Min(wb.SearchBox.SelectionStart, wb.SearchBox.SelectionEnd), Math.Max(wb.SearchBox.SelectionStart, wb.SearchBox.SelectionEnd)));
    }

    [AvaloniaFact]
    public void Search_shortcut_is_ctrl_shift_f_elsewhere()
    {
        var (wb, _) = Bench();
        wb.SearchBox.Text = "supply";
        wb.Tabs.Focus();
        Pump();
        wb.KeyPress(Key.F, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.F, null);
        Pump();
        Assert.True(wb.SearchBox.IsFocused);
    }

    [AvaloniaFact]
    public void Panel_height_never_exceeds_half_the_left_pane()
    {
        var (wb, _) = Bench();
        Search(wb, "supply");
        wb.LeftPane.Measure(new Avalonia.Size(400, 300));
        wb.Height = 300;
        wb.UpdateLayout();
        Pump();
        var half = wb.LeftPane.Bounds.Height / 2;
        Assert.True(wb.SearchPanelForTests.Height <= Math.Max(SearchPanel.HeaderHeight * 3, half) + 0.5,
            $"panel {wb.SearchPanelForTests.Height} left pane {wb.LeftPane.Bounds.Height}");
        wb.Height = 1000;
        wb.UpdateLayout();
        Pump();
        Assert.Equal(SearchPanel.ExpandedHeight, wb.SearchPanelForTests.Height);   // the user's height comes back when there is room
    }

    [AvaloniaFact]
    public void Dragged_splitter_height_is_kept()
    {
        var (wb, _) = Bench();
        wb.Height = 1000;
        Search(wb, "supply");
        wb.UpdateLayout();
        wb.LeftPane.RowDefinitions[2].Height = new Avalonia.Controls.GridLength(120);   // what the splitter leaves behind
        wb.OnSearchSplitterDragged();
        Assert.Equal(120d, wb.SearchPanelForTests.Height);
        Assert.True(wb.LeftPane.RowDefinitions[2].Height.IsAuto);
    }

    [AvaloniaFact]
    public void Close_during_index_build_is_quiet()
    {
        var unobserved = new List<Exception>();
        void OnUnobserved(object? o, UnobservedTaskExceptionEventArgs e) { lock (unobserved) unobserved.Add(e.Exception); }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var (wb, s) = Bench(waitIndex: false, extraPages: 400);
            wb.RunSearch("supply");
            wb.Close();
            Pump();
            Thread.Sleep(300);
            Pump();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Pump();
            Assert.DoesNotContain(s.Log.Items, e => e.Message.Contains("Rulebook search failed"));
            lock (unobserved) Assert.Empty(unobserved);
        }
        finally { TaskScheduler.UnobservedTaskException -= OnUnobserved; }
    }
}
