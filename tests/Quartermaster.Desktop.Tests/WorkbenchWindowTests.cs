using Avalonia;
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
using Quartermaster.Render.Workbench;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 4a task 10: the Rules Workbench window, Tools > Rules Workbench, its shortcut and the tables-tree activation.
/// Headless, fake dialogs, an invented module with an invented PDF and an invented rules pack.</summary>
[Collection("MacFlag")]
public sealed class WorkbenchWindowTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly Action<string> _prune = WorkbenchWindow.PruneCaches;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-workbench-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _mains = new();
    readonly List<Window> _windows = new();
    readonly List<ModuleSession> _sessions = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();
    readonly List<string> _pruned = new();

    public WorkbenchWindowTests()
    {
        ShortcutGestures.IsMac = false;
        Directory.CreateDirectory(_tmp);
        WorkbenchWindow.PruneCaches = sha => { lock (_pruned) _pruned.Add(sha); };   // never the real cache folders
    }

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        foreach (var w in _mains) w.ForceClose();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        WorkbenchWindow.PruneCaches = _prune;
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static void Click(Button b) { b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }

    /// <summary>Runs the dispatcher loop (RunJobs alone runs no timers) in short slices until the condition holds or the time is up.</summary>
    static bool WaitFor(Func<bool> condition, int ms = 3000)
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

    string LayoutRoot => Path.Combine(_tmp, "layout");

    static byte[] Pdf(params string[] pages)
    {
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages) b.AddPage(PageSize.A4).AddText(text, 12, new PdfPoint(40, 700), font);
        return b.Build();
    }

    /// <summary>Built once: the same bytes every time, so every build of the module has the same sha (layout memory is per module).</summary>
    static readonly Lazy<byte[]> RulesPdf = new(() => Pdf("Invented page one", "Invented weather page", "Invented page three"));

    /// <summary>The phase fixture plus an invented three-page PDF rulebook "rules.pdf".</summary>
    string ModulePath()
    {
        var fx = PhaseFixture.Module().EditBuildFile(x => x.Replace("</VASSAL.build.GameModule>",
                "<VASSAL.build.module.Documentation><VASSAL.build.module.documentation.HelpFile title=\"Rules\" fileName=\"rules.pdf\" fileType=\"archive\"/>"
                + "</VASSAL.build.module.Documentation></VASSAL.build.GameModule>"))
            .AddFile("rules.pdf", RulesPdf.Value);
        _fixtures.Add(fx);
        return fx.Build();
    }

    static RulesPack Pack(string sha)
    {
        var p = PhaseFixture.Pack(sha);
        return p with
        {
            Tables = p.Tables.Select(t => t with { Notes = "first line\r\nsecond line" })
                .Append(new TableDef { Id = "t-b", Name = "Second table", Kind = TableKind.OneD, RowKeys = new() { "A" }, ColumnKeys = new() { "Value" }, Cells = new() { new() { "1" } } })
                .ToList(),
            Formulas = p.Formulas.Select(f => f.Id == "f-weather" ? f with { RuleRefId = "r-rules", Notes = "rolled\r\nonce" } : f)
                .Append(new FormulaDef { Id = "f-broken", Name = "Broken", Text = "1 +" }).ToList(),
            Phases = p.Phases.Select(ph => ph.Id == "ph-end" ? ph with { RuleRefId = "r-gone", Notes = "end\r\nof turn" } : ph).ToList(),
            Alerts = new() { new AlertDef { Id = "a-low", Name = "Low fuel", Condition = "Fuel < 5", Message = "Low", Notes = "a\r\nb" } },
            Holders = new() { new HolderDef { Id = "h-depot", Name = "Depot" } },
            RuleRefs = new()
            {
                new RuleRef { Id = "r-rules", Label = "Weather roll", DocumentEntry = "rules.pdf", Page = 2 },
                new RuleRef { Id = "r-gone", Label = "End", DocumentEntry = "gone.pdf", Page = 1 },
            },
        };
    }

    MainWindow Main(bool pack = true, bool mac = false)
    {
        ShortcutGestures.IsMac = mac;
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, LayoutRoot) { Width = 1200, Height = 800 };
        w.ScreensProvider = () => new[] { new ScreenInfo(new System.Drawing.Rectangle(0, 0, 1920, 1080), 96) };
        _mains.Add(w);
        w.Show();
        Assert.True(w.OpenModule(ModulePath()));
        if (pack) OpenPack(w.Session!);
        Pump();
        return w;
    }

    string OpenPack(ModuleSession s, string name = "pack")
    {
        var path = Path.Combine(_tmp, name + "-" + Guid.NewGuid().ToString("N") + ".qrules");
        RulesPackIo.Save(Pack(s.Archive.Sha256), path);
        s.OpenRules(path);
        return path;
    }

    WorkbenchWindow Bench(MainWindow w)
    {
        w.ClickMenu("&Rules Workbench");
        Pump();
        return Assert.IsType<WorkbenchWindow>(w.WorkbenchTool);
    }

    /// <summary>A workbench on its own session (no main window), not shown unless asked.</summary>
    (WorkbenchWindow W, ModuleSession S) Standalone(bool pack = true, bool show = true, Func<Task>? newPack = null, Func<Task>? openPack = null)
    {
        var s = ModuleSession.Open(ModulePath(), Path.Combine(_tmp, "cache" + _sessions.Count), null);
        _sessions.Add(s);
        if (pack) OpenPack(s);
        var wb = new WorkbenchWindow(s, _dialogs, newPack ?? (() => Task.CompletedTask), openPack ?? (() => Task.CompletedTask));
        _windows.Add(wb);
        if (show) { wb.Show(); Pump(); }
        return (wb, s);
    }

    static object? Shown(WorkbenchWindow wb) => (wb.Tabs.SelectedItem as TabItem)?.Content;

    // ---------- opening ----------

    [AvaloniaFact]
    public void Tools_menu_opens_one_workbench_per_session()
    {
        var w = Main();
        Assert.Equal("&Rules Workbench", w.MenuLabels()["&Tools"][0]);
        Assert.Equal(new KeyGesture(Key.W, KeyModifiers.Control | KeyModifiers.Shift), w.FindMenuItem("&Rules Workbench")!.InputGesture);
        var wb = Bench(w);
        Assert.Equal((1000d, 640d), (wb.Width, wb.Height));
        Assert.Same(w, wb.Owner);
        Assert.Equal(new[] { "Tables", "Formulas", "Properties & Holders", "Phases", "Alerts", "Visibility" },
            wb.Tabs.Items.Cast<TabItem>().Select(t => (string)t.Header!));
        Assert.Same(wb.Tables, Shown(wb));
        Assert.Same(wb, Bench(w));   // the menu again brings the same window to the front
        Assert.Single(_pruned);
        Assert.Equal(w.Session!.Archive.Sha256, _pruned[0]);
    }

    [AvaloniaFact]
    public void Shortcut_primary_shift_w_opens_workbench()
    {
        var w = Main();
        w.KeyPress(Key.W, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.W, null);
        Pump();
        Assert.NotNull(w.WorkbenchTool);

        var mac = Main(mac: true);
        Assert.Equal(new KeyGesture(Key.W, KeyModifiers.Meta | KeyModifiers.Shift), mac.FindMenuItem("&Rules Workbench")!.InputGesture);
        mac.KeyPress(Key.W, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.W, null);
        Pump();
        Assert.Null(mac.WorkbenchTool);
        mac.KeyPress(Key.W, RawInputModifiers.Meta | RawInputModifiers.Shift, PhysicalKey.W, null);
        Pump();
        Assert.NotNull(mac.WorkbenchTool);
    }

    [AvaloniaFact]
    public void Refused_while_replaying()
    {
        var w = Main();
        typeof(ModuleSession).GetProperty("IsReplaying")!.SetValue(w.Session, true);
        w.Commands.Execute("tools.workbench");
        Pump();
        Assert.Null(w.WorkbenchTool);
        Assert.Equal(ModuleSession.ReplayingText, w.StatusText);
        w.SetStatus("");
        w.ClickMenu("&Rules Workbench");
        Assert.Null(w.WorkbenchTool);
        Assert.Equal(ModuleSession.ReplayingText, w.StatusText);

        // No module: nothing happens.
        var empty = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, LayoutRoot);
        _mains.Add(empty);
        empty.Show();
        Assert.False(empty.FindMenuItem("&Rules Workbench")!.IsEnabled);
        Assert.False(empty.Commands.Execute("tools.workbench"));
        Assert.Null(empty.WorkbenchTool);
    }

    [AvaloniaFact]
    public void Tree_table_and_formula_activation_open_at_item()
    {
        var w = Main();
        w.TablesTree.Refresh();
        var roots = w.TablesTree.Tree.ItemsSource!.Cast<TreeViewItem>().ToList();
        TreeViewItem Kid(object tag) => roots.SelectMany(r => r.ItemsSource!.Cast<TreeViewItem>()).Single(k => Equals(((TreeItem)k.Tag!).Tag, tag));
        void Activate(TreeViewItem item)
        {
            w.TablesTree.Tree.SelectedItem = item;
            w.TablesTree.Tree.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Pump();
        }

        Activate(Kid(new TableRef("t-b")));
        var wb = Assert.IsType<WorkbenchWindow>(w.WorkbenchTool);
        Assert.Same(wb.Tables, Shown(wb));
        Assert.Equal("t-b", wb.Tables.SelectedTableId);

        Activate(Kid(new FormulaRef("f-water")));
        Assert.Same(wb, w.WorkbenchTool);
        Assert.Same(wb.Formulas, Shown(wb));
        Assert.Equal("f-water", wb.Formulas.SelectedFormulaId);
    }

    [AvaloniaFact]
    public void Show_table_before_shown_selects_it()
    {
        var (wb, _) = Standalone(show: false);
        wb.ShowFormula("f-water");
        wb.ShowTable("nope");   // unknown: no-op
        wb.ShowTable("t-b");
        wb.ShowPhase("nope");
        wb.Show();
        Pump();
        Assert.Same(wb.Tables, Shown(wb));
        Assert.Equal("t-b", wb.Tables.SelectedTableId);
        wb.ShowFormula("nope");
        Assert.Same(wb.Tables, Shown(wb));
        wb.ShowPhase("ph-combat");
        Pump();
        Assert.Same(wb.Phases, Shown(wb));
        Assert.Equal("ph-combat", wb.Phases.SelectedPhaseId);
        wb.ShowFormula("f-water");
        Pump();
        Assert.Equal("f-water", wb.Formulas.SelectedFormulaId);
    }

    // ---------- pack state ----------

    [AvaloniaFact]
    public void No_pack_prompt_buttons_run_flows()
    {
        var w = Main(pack: false);
        var wb = Bench(w);
        Assert.True(wb.PromptShown);
        Assert.False(wb.MainShown);
        Assert.Equal(WorkbenchRules.NoPackText, wb.PromptText);
        Assert.Equal("New rules pack…", wb.NewPackButton.Content);
        Assert.Equal("Open…", wb.OpenPackButton.Content);
        Assert.Equal("Rules Workbench", wb.Title);

        _dialogs.Texts.Enqueue("Invented rules");
        Click(wb.NewPackButton);
        Assert.Equal("Invented rules", w.Session!.Rules!.Name);
        Assert.False(wb.PromptShown);
        Assert.True(wb.MainShown);
        Assert.Equal("Rules Workbench — Invented rules", wb.Title);

        var path = Path.Combine(_tmp, "opened.qrules");
        RulesPackIo.Save(Pack(w.Session.Archive.Sha256), path);
        _dialogs.YesNoCancel.Enqueue(false);   // don't save the new pack
        _dialogs.OpenFiles.Enqueue(path);
        Click(wb.OpenPackButton);
        Assert.Equal("Phase fixture", w.Session.Rules!.Name);
        Assert.Equal("Rules Workbench — Phase fixture", wb.Title);
        Assert.Contains("Open Rules Pack", _dialogs.PickerTitles);
    }

    [AvaloniaFact]
    public void Pack_closed_shows_prompt()
    {
        var (wb, s) = Standalone();
        Assert.False(wb.PromptShown);
        Assert.True(wb.MainShown);
        typeof(ModuleSession).GetProperty("Rules")!.SetValue(s, null);
        RaiseRulesChanged(s);
        Pump();
        Assert.True(wb.PromptShown);
        Assert.False(wb.MainShown);
        Assert.Equal("Rules Workbench", wb.Title);

        // A guest never sees the prompt; with the host's pack the tabs are read-only under the banner.
        var (g, gs) = Standalone(pack: false);
        gs.EnterGuestMode();
        RaiseRulesChanged(gs);
        Pump();
        Assert.False(g.PromptShown);
        gs.LoadGuestRules(Pack(gs.Archive.Sha256));
        Pump();
        Assert.True(g.MainShown);
        Assert.True(g.ReadOnlyShown);
        Assert.Equal(WorkbenchRules.ReadOnlyText, g.ReadOnlyText);
        Assert.Equal("Rules Workbench — Phase fixture (read-only)", g.Title);
        Assert.False(g.Tables.IsEnabled);
        Assert.False(g.Visibility.IsEnabled);
        Assert.False(wb.ReadOnlyShown);
        Assert.True(wb.Tables.IsEnabled);
    }

    static void RaiseRulesChanged(ModuleSession s)
    {
        var f = typeof(ModuleSession).GetField("RulesChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        ((Action?)f.GetValue(s))?.Invoke();
    }

    [AvaloniaFact]
    public void Pack_replaced_rebinds_tabs()
    {
        var (wb, s) = Standalone();
        wb.ShowTable("t-b");
        Pump();
        wb.Tables.Grid.BeginEdit(0, 1, "zzz");   // an edit open while the pack is replaced is dropped, never applied to the new pack
        var other = PhaseFixture.Pack(s.Archive.Sha256) with
        {
            Name = "Other",
            Tables = new() { new TableDef { Id = "t-x", Name = "Other table", Kind = TableKind.OneD, RowKeys = new() { "Q" }, ColumnKeys = new() { "Value" }, Cells = new() { new() { "9" } } } },
        };
        var path = Path.Combine(_tmp, "other.qrules");
        RulesPackIo.Save(other, path);
        s.OpenRules(path);
        Pump();
        Assert.Equal("Rules Workbench — Other", wb.Title);
        Assert.Equal("t-x", wb.Tables.SelectedTableId);
        Assert.Equal("9", wb.Tables.Grid.Cell(0, 1));
        Assert.Equal("9", s.Rules!.Tables[0].Cells[0][0]);
        Assert.False(s.RulesDirty);
        // Tabs not shown pick the new pack up when they are.
        wb.Tabs.SelectedItem = wb.Tabs.Items.Cast<TabItem>().Single(t => t.Content == wb.Alerts);
        Pump();
        Assert.Empty(wb.Alerts.Rows);
        wb.Tabs.SelectedItem = wb.Tabs.Items.Cast<TabItem>().Single(t => t.Content == wb.Formulas);
        Pump();
        Assert.Equal(s.Rules.Formulas[0].Id, wb.Formulas.SelectedFormulaId);   // the new pack's first formula (the old selection is not in it)
    }

    [AvaloniaFact]
    public void Title_follows_pack_name()
    {
        var (wb, s) = Standalone();
        Assert.Equal("Rules Workbench — Phase fixture", wb.Title);
        s.UpdateRules(p => p with { Name = "Renamed" });
        Pump();
        Assert.Equal("Rules Workbench — Renamed", wb.Title);
    }

    [AvaloniaFact]
    public void Issues_debounced_and_go_to_issue_selects_tab_and_item()
    {
        var (wb, s) = Standalone();
        int Find(string item) => wb.IssueRows.ToList().FindIndex(r => r.Item == item);
        Assert.True(Find("Broken") >= 0);
        var row = wb.IssueRows[Find("Broken")];
        Assert.Equal("Error", row.Severity);
        Assert.Equal(new[] { "Severity", "Item", "Problem" }, wb.IssueHeaders);
        Assert.Equal(new[] { 70d, 140d }, wb.IssueWidths);

        s.UpdateRules(p => p with { Alerts = p.Alerts.Select(a => a with { Condition = "" }).ToList() });
        Pump();
        Assert.True(wb.ValidatePending);
        Assert.True(Find("Low fuel") < 0);   // not before the 300 ms pause
        Assert.True(WaitFor(() => Find("Low fuel") >= 0));
        Assert.False(wb.ValidatePending);

        wb.GoToIssue(Find("Low fuel"));
        Pump();
        Assert.Same(wb.Alerts, Shown(wb));
        Assert.Equal("a-low", wb.Alerts.SelectedAlertId);

        wb.GoToIssue(Find("Broken"));
        Pump();
        Assert.Same(wb.Formulas, Shown(wb));
        Assert.Equal("f-broken", wb.Formulas.SelectedFormulaId);

        // Double-clicking a row does the same.
        wb.IssueList.SelectedIndex = Find("Low fuel");
        wb.IssueList.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));
        Pump();
        Assert.Same(wb.Alerts, Shown(wb));

        // Fixed: one "No problems found." row without an issue.
        s.UpdateRules(p => p with { Formulas = p.Formulas.Where(f => f.Id != "f-broken").ToList(), Alerts = new() });
        Assert.True(WaitFor(() => wb.IssueRows.All(r => r.Issue == null || r.Severity != "Error")));
        if (wb.IssueRows.Count == 1 && wb.IssueRows[0].Issue == null) Assert.Equal(WorkbenchRules.NoProblemsText, wb.IssueRows[0].Problem);
        wb.GoToIssue(99);   // out of range: no-op
    }

    // ---------- rule references ----------

    [AvaloniaFact]
    public void Link_current_page_without_document_warns()
    {
        var (wb, s) = Standalone();
        Assert.Null(wb.LinkCurrentPage("Label"));
        Assert.Equal((WorkbenchRules.OpenDocumentFirstText, "Rules Workbench"), _dialogs.Infos[^1]);

        wb.Viewer.Open(s.Model.Documents.Single(d => d.EntryName == "rules.pdf"), 3);
        Pump();
        var r = wb.LinkCurrentPage("Label");
        Assert.NotNull(r);
        Assert.Equal(("rules.pdf", 3, "Label"), (r!.DocumentEntry, r.Page, r.Label));
        Assert.Same(r, s.Rules!.ById<RuleRef>(r.Id));
        Assert.Equal(r.Id, wb.LinkCurrentPage("Other")!.Id);   // the same page reuses the ref

        // A path on this computer is never a rule ref.
        Assert.Null(wb.RefFor("x", Path.Combine(_tmp, "local.pdf"), 1));
        Assert.Equal((WorkbenchRules.RefNotAllowedText, "Rules Workbench"), _dialogs.Infos[^1]);
        // The tabs make refs through the same path (scan import).
        Assert.NotNull(wb.Tables.MakeRuleRef);
    }

    [AvaloniaFact]
    public void Rule_ref_to_module_document_opens_viewer_page()
    {
        var (wb, _) = Standalone();
        wb.ShowFormula("f-weather");
        Pump();
        Assert.True(wb.Formulas.RefRow.IsLink);
        wb.Formulas.RefRow.ClickLink();
        Pump();
        Assert.Equal("rules.pdf", wb.Viewer.Document?.EntryName);
        Assert.Equal(2, wb.Viewer.Page);
    }

    [AvaloniaFact]
    public void Rule_ref_to_missing_entry_warns()
    {
        var (wb, _) = Standalone();
        wb.ShowPhase("ph-end");
        Pump();
        wb.Phases.RefRow.ClickLink();
        Pump();
        Assert.Equal((WorkbenchRules.MissingRefText("gone.pdf"), "Rules Workbench"), _dialogs.Infos[^1]);
        Assert.Null(wb.Viewer.Document);
    }

    [AvaloniaFact]
    public void Used_on_page_activates_formula()
    {
        var (wb, s) = Standalone();
        Assert.True(wb.Viewer.UsedPanel!.IsVisible);
        wb.Viewer.Open(s.Model.Documents.Single(d => d.EntryName == "rules.pdf"), 1);
        Pump();
        Assert.Empty(wb.Viewer.UsedOnPageLines);
        wb.Viewer.GoToPage(2);
        Pump();
        Assert.Equal(new[] { "Formula: Weather roll" }, wb.Viewer.UsedOnPageLines);
        // A rules change refreshes it.
        s.UpdateRules(p => p with { Tables = p.Tables.Select(t => t.Id == "t-b" ? t with { RuleRefId = "r-rules" } : t).ToList() });
        Pump();
        Assert.Contains("Table: Second table", wb.Viewer.UsedOnPageLines);

        Assert.Same(wb.Tables, Shown(wb));
        wb.Viewer.UsedList!.SelectedIndex = wb.Viewer.UsedOnPageLines.ToList().IndexOf("Formula: Weather roll");
        wb.Viewer.UsedList.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Pump();
        Assert.Same(wb.Formulas, Shown(wb));
        Assert.Equal("f-weather", wb.Formulas.SelectedFormulaId);
    }

    // ---------- pack integrity ----------

    [AvaloniaFact]
    public void Opening_workbench_leaves_pack_unchanged()
    {
        var w = Main();
        var s = w.Session!;
        var before = s.Rules;
        var wb = Bench(w);
        foreach (var tab in wb.Tabs.Items.Cast<TabItem>())
        {
            wb.Tabs.SelectedItem = tab;
            Pump();
        }
        foreach (var id in new[] { "t-cost", "t-b", "t-cost" }) { wb.ShowTable(id); Pump(); }
        foreach (var id in new[] { "f-weather", "f-water", "f-broken", "f-weather" }) { wb.ShowFormula(id); Pump(); }
        foreach (var id in new[] { "ph-weather", "ph-end", "ph-combat" }) { wb.ShowPhase(id); Pump(); }
        for (var i = 0; i < wb.IssueRows.Count; i++) { wb.GoToIssue(i); Pump(); }
        wb.Viewer.Open(s.Model.Documents[0], 2);
        Pump();
        wb.Close();
        Pump();
        Assert.False(s.RulesDirty);
        Assert.Same(before, s.Rules);
    }

    [AvaloniaFact]
    public void Closes_with_session_and_remembers_bounds()
    {
        var w = Main();
        var sha = w.Session!.Archive.Sha256;
        var wb = Bench(w);
        var closed = false;
        wb.Closed += (_, _) => closed = true;
        wb.Position = new PixelPoint(150, 90);
        wb.Width = 900;
        wb.Height = 600;
        Assert.True(w.OpenModule(ModulePath()));   // the session goes: the workbench closes with it
        Pump();
        Assert.True(closed);
        Assert.Null(w.WorkbenchTool);
        var saved = new WindowLayoutStore(LayoutRoot).LoadModule(sha);
        Assert.True(saved.TryGetValue("bench:workbench", out var sw));
        Assert.Equal((150, 90, 900, 600), (sw.Bounds.X, sw.Bounds.Y, sw.Bounds.Width, sw.Bounds.Height));

        // Reopened: the remembered bounds.
        OpenPack(w.Session!);
        var again = Bench(w);
        Assert.NotSame(wb, again);
        Assert.Equal((900d, 600d), (again.Width, again.Height));
        Assert.Equal(new PixelPoint(150, 90), again.Position);
    }

    // ---------- cross-task: pending edits, import owner, list clicks ----------

    [AvaloniaFact]
    public void Pending_edit_in_each_tab_survives_save_rules_pack()
    {
        var w = Main();
        var s = w.Session!;
        var path = s.RulesPath!;
        var wb = Bench(w);

        void Save()
        {
            w.ClickMenu("Save Rules Pac&k");
            Assert.True(WaitFor(() => !s.RulesDirty));
        }
        RulesPack Reloaded() => RulesPackIo.Load(path);

        wb.ShowTable("t-b");
        Pump();
        wb.Tables.Notes.Text = "typed table note";
        Save();
        Assert.Equal("typed table note", Reloaded().ById<TableDef>("t-b")!.Notes);
        wb.Tables.Grid.BeginEdit(0, 1, "42");
        Save();
        Assert.Equal("42", Reloaded().ById<TableDef>("t-b")!.Cells[0][0]);

        wb.ShowFormula("f-water");
        Pump();
        wb.Formulas.Text.Text = "W - 2";
        Save();
        Assert.Equal("W - 2", Reloaded().ById<FormulaDef>("f-water")!.Text);

        wb.ShowPhase("ph-combat");
        Pump();
        wb.Phases.Notes.Text = "typed phase note";
        Save();
        Assert.Equal("typed phase note", Reloaded().Phases.Single(p => p.Id == "ph-combat").Notes);

        wb.Tabs.SelectedItem = wb.Tabs.Items.Cast<TabItem>().Single(t => t.Content == wb.Properties);
        Pump();
        wb.Properties.HolderGrid.BeginEdit(1, 1, "typed holder note");
        Save();
        Assert.Equal("typed holder note", Reloaded().Holders.Single(h => h.Id == "h-depot").Notes);

        wb.Tabs.SelectedItem = wb.Tabs.Items.Cast<TabItem>().Single(t => t.Content == wb.Alerts);
        Pump();
        wb.Alerts.NotesBox.Text = "typed alert note";
        Save();
        Assert.Equal("typed alert note", Reloaded().Alerts.Single().Notes);

        // Visibility edits are combo choices: they reach the pack at once; the tab still flushes on save without harm.
        wb.Tabs.SelectedItem = wb.Tabs.Items.Cast<TabItem>().Single(t => t.Content == wb.Visibility);
        Pump();
        w.ClickMenu("Save Rules Pac&k");
        Pump();
        Assert.False(s.RulesDirty);

        // Save Game (Ctrl+S) commits a pending edit into the pack first too.
        w.StartSetup(0);
        Pump();
        wb.ShowTable("t-b");
        Pump();
        wb.Tables.Notes.Text = "before game save";
        _dialogs.SaveFiles.Enqueue(Path.Combine(_tmp, "game.qsave"));
        w.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, null);
        Assert.True(WaitFor(() => File.Exists(Path.Combine(_tmp, "game.qsave"))));
        Assert.Equal("before game save", s.Rules!.ById<TableDef>("t-b")!.Notes);
    }

    [AvaloniaFact]
    public void Pending_edit_survives_tab_switch_and_close()
    {
        var w = Main();
        var s = w.Session!;
        var wb = Bench(w);
        wb.ShowTable("t-b");
        Pump();
        wb.Tables.Notes.Text = "switched away";
        wb.ShowFormula("f-water");
        Pump();
        Assert.Equal("switched away", s.Rules!.ById<TableDef>("t-b")!.Notes);
        wb.Formulas.Text.Text = "W - 3";
        wb.Close();
        Pump();
        Assert.Equal("W - 3", s.Rules!.ById<FormulaDef>("f-water")!.Text);
    }

    [AvaloniaFact]
    public async Task Import_works_while_another_tab_is_shown()
    {
        var (wb, s) = Standalone();
        wb.ShowFormula("f-water");
        Pump();
        Assert.Same(wb.Formulas, Shown(wb));
        _dialogs.ImportTargetAnswer = false;
        _dialogs.Texts.Enqueue("Scanned");
        _dialogs.PasteAnswer = Quartermaster.Rules.Tables.TablePaste.Parse("k\tv\n1\t2", true, true);
        var id = await wb.Tables.ImportGridAsync("k\tv\n1\t2", "rules.pdf", 2);
        Assert.NotNull(id);
        Assert.Equal("Scanned", s.Rules!.ById<TableDef>(id!)!.Name);

        // Through the window: the Tables tab comes to the front with the new table.
        wb.ShowFormula("f-water");
        Pump();
        _dialogs.Texts.Enqueue("Scanned 2");
        var id2 = await wb.ImportTableAsync("k\tv\n1\t2", "rules.pdf", 3);
        Pump();
        Assert.NotNull(id2);
        Assert.Same(wb.Tables, Shown(wb));
        Assert.Equal(id2, wb.Tables.SelectedTableId);
        var r = s.Rules!.ById<RuleRef>(s.Rules.ById<TableDef>(id2!)!.RuleRefId!);
        Assert.Equal(("rules.pdf", 3), (r!.DocumentEntry, r.Page));
    }

    static void ClickAt(Window w, Control c)
    {
        var at = c.TranslatePoint(new Point(5, Math.Min(5, c.Bounds.Height / 2)), w)!.Value;
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        Pump();
    }

    static ListBoxItem Item(ListBox list, int i) => (ListBoxItem)list.ContainerFromIndex(i)!;

    [AvaloniaFact]
    public void List_click_with_pending_edit_selects_clicked_item()
    {
        var (wb, s) = Standalone();
        wb.ShowTable("t-cost");
        Pump();
        wb.Tables.Notes.Focus();
        Pump();
        wb.KeyTextInput("!");
        Pump();
        var tb = s.Rules!.Tables.FindIndex(t => t.Id == "t-b");
        ClickAt(wb, Item(wb.Tables.List, tb));
        Assert.Equal("t-b", wb.Tables.SelectedTableId);
        Assert.Equal(tb, wb.Tables.List.SelectedIndex);
        Assert.Contains("!", s.Rules!.ById<TableDef>("t-cost")!.Notes);   // the typed note reached its own table

        wb.ShowFormula("f-weather");
        Pump();
        wb.Formulas.Text.Focus();
        Pump();
        wb.Formulas.Text.CaretIndex = wb.Formulas.Text.Text!.Length;
        wb.KeyTextInput(" + 1");
        Pump();
        var fw = s.Rules!.Formulas.FindIndex(f => f.Id == "f-water");
        ClickAt(wb, Item(wb.Formulas.List, fw));
        Assert.Equal("f-water", wb.Formulas.SelectedFormulaId);
        Assert.Equal(fw, wb.Formulas.List.SelectedIndex);
        Assert.Equal("R + 1", s.Rules!.ById<FormulaDef>("f-weather")!.Text);
    }
}
