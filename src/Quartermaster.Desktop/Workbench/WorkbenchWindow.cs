using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Desktop.Documents;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Render.Ocr;
using Quartermaster.Render.Search;
using Quartermaster.Render.Workbench;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Workbench;

/// <summary>
/// Rules Workbench (port of WinForms WorkbenchWindow): the module's rulebook on the left as a reference the user reads, the user's own editors
/// on the right (six tabs), the problems list at the bottom. Nothing is ever read out of the documents; every table and formula is typed or
/// pasted by the user. The rulebook search (WorkbenchWindow.Search.cs) and the scan pane (WorkbenchWindow.Scans.cs) plug into the
/// <see cref="LeftPane"/> and <see cref="ToolbarRow"/>.
/// </summary>
public sealed partial class WorkbenchWindow : Window
{
    const string Caption = "Rules Workbench";
    static readonly IBrush ReadOnlyBrush = new SolidColorBrush(Color.FromRgb(255, 240, 200));
    static readonly string[] IssueHeaderTexts = { "Severity", "Item", "Problem" };
    static readonly double[] IssueColumnWidths = { 70, 140 };

    /// <summary>Prunes the scan cache and the other modules' search caches (once per window, in the background). Tests replace it.</summary>
    internal static Action<string> PruneCaches = sha =>
    {
        try { ScanCache.Prune(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("scan cache prune: " + ex.Message); }
        try { RulebookIndex.PruneCache(sha); }   // other modules' search caches unused for 90 days
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("search cache prune: " + ex.Message); }
    };

    readonly ModuleSession _s;
    readonly IDialogs _dialogs;
    readonly Func<Task> _newPack, _openPack;
    /// <summary>The OCR engine the scan pane uses (WorkbenchWindow.Scans.cs); null = the platform's choice.</summary>
    readonly IOcrRecognizer? _ocr;

    /// <summary>Opens Settings on the OCR languages page (the OCR panel's "More languages…"); null when the window was made without a main window.</summary>
    internal Func<Task>? OpenOcrLanguages { get; }

    readonly TabItem _tablesPage, _formulasPage, _propsPage, _phasesPage, _alertsPage, _visibilityPage;
    readonly Grid _split = new() { ColumnDefinitions = new ColumnDefinitions("40*,4,60*") };
    readonly ListBox _issues = new() { SelectionMode = SelectionMode.Single };
    readonly DockPanel _main = new();
    readonly StackPanel _prompt = new() { Margin = new Thickness(20) };
    readonly TextBlock _promptText = new() { Text = WorkbenchRules.NoPackText };
    readonly Button _bNew = new() { Content = "New rules pack…" }, _bOpen = new() { Content = "Open…" };
    readonly Border _readOnly;
    readonly TextBlock _readOnlyText = new() { Text = WorkbenchRules.ReadOnlyText, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _statusText = new() { Margin = new Thickness(4, 2), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly DispatcherTimer _validateTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    IReadOnlyList<IssueRow> _issueRows = Array.Empty<IssueRow>();
    bool _closed;

    // shared with WorkbenchWindow.Search.cs and WorkbenchWindow.Scans.cs
    internal readonly ScanSessionModel ScanModel = new();

    /// <summary>The left pane: row 0 the viewer (the scan pane goes there too), row 1 a splitter, row 2 the search panel's slot.</summary>
    internal Grid LeftPane { get; } = new() { RowDefinitions = new RowDefinitions("*,Auto,Auto") };

    /// <summary>The toolbar over the panes: "Rulebooks:", the search box and button, a separator, "Open scan…".</summary>
    internal StackPanel ToolbarRow { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(4, 2) };
    internal TextBox SearchBox { get; } = new() { Width = 260, IsEnabled = false };
    internal Button SearchButton { get; } = new() { Content = "Search", IsEnabled = false };
    internal Button OpenScanButton { get; } = new() { Content = "Open scan…", IsEnabled = false };

    /// <summary>Row 1 of <see cref="LeftPane"/>: the splitter over the search panel slot (row 2). Shown only while the slot holds a visible
    /// control (<see cref="UpdateSearchSplitter"/>; the search panel calls it again when it collapses or expands).</summary>
    internal GridSplitter SearchSplitter { get; } = new() { ResizeDirection = GridResizeDirection.Rows, Height = 4, IsVisible = false };

    /// <summary>Shows the search splitter while row 2 of the left pane holds a visible control, hides it otherwise.</summary>
    internal void UpdateSearchSplitter() =>
        SearchSplitter.IsVisible = LeftPane.Children.Any(c => Grid.GetRow(c) == 2 && c.IsVisible);

    public WorkbenchWindow(ModuleSession s, IDialogs dialogs, Func<Task> newPack, Func<Task> openPack, IOcrRecognizer? ocr = null, Func<Task>? openOcrLanguages = null)
    {
        OpenOcrLanguages = openOcrLanguages;
        _s = s;
        _dialogs = dialogs;
        _newPack = newPack;
        _openPack = openPack;
        _ocr = ocr;
        Title = Caption;
        ShowInTaskbar = false;
        Width = 1000;
        Height = 640;

        Viewer = new DocumentViewer(s, toolbar: true, dialogs);
        Tables = new TablesTab(s, dialogs, LinkCurrentPage, ShowRule) { DialogOwner = this, MakeRuleRef = RefFor };
        Formulas = new FormulasTab(s, dialogs, LinkCurrentPage, ShowRule);
        Properties = new PropertiesTab(s, dialogs);
        Phases = new PhasesTab(s, dialogs, LinkCurrentPage, ShowRule);
        Alerts = new AlertsTab(s, dialogs);
        Visibility = new VisibilityTab(s);
        _tablesPage = Page("Tables", Tables);
        _formulasPage = Page("Formulas", Formulas);
        _propsPage = Page("Properties & Holders", Properties);
        _phasesPage = Page("Phases", Phases);
        _alertsPage = Page("Alerts", Alerts);
        _visibilityPage = Page("Visibility", Visibility);
        Tabs = new TabControl();
        foreach (var page in new[] { _tablesPage, _formulasPage, _propsPage, _phasesPage, _alertsPage, _visibilityPage }) Tabs.Items.Add(page);
        Tabs.SelectedIndex = 0;

        // left | splitter | tabs
        LeftPane.Children.Add(Viewer);
        Grid.SetRow(SearchSplitter, 1);
        LeftPane.Children.Add(SearchSplitter);
        LeftPane.Children.CollectionChanged += (_, _) => UpdateSearchSplitter();
        Grid.SetColumn(LeftPane, 0);
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(Tabs, 2);
        _split.Children.Add(LeftPane);
        _split.Children.Add(splitter);
        _split.Children.Add(Tabs);

        // toolbar: the search and scan parts are switched on by their files
        SearchBox.PlaceholderText = $"Search the rulebooks ({ShortcutGestures.Display("Ctrl+Shift+F")})";
        ToolbarRow.Children.Add(new TextBlock { Text = "Rulebooks:", VerticalAlignment = VerticalAlignment.Center });
        ToolbarRow.Children.Add(SearchBox);
        ToolbarRow.Children.Add(SearchButton);
        ToolbarRow.Children.Add(new Border { Width = 1, Margin = new Thickness(4, 2), Background = Brushes.Gray });
        ToolbarRow.Children.Add(OpenScanButton);

        _readOnly = new Border { Height = 24, Padding = new Thickness(8, 0), Background = ReadOnlyBrush, Child = _readOnlyText, IsVisible = false };

        // problems list (Severity 70, Item 140, Problem)
        var issuesBox = BuildIssues();
        var body = new Grid { RowDefinitions = new RowDefinitions("*,4,110") };
        var bodySplit = new GridSplitter { ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(bodySplit, 1);
        Grid.SetRow(issuesBox, 2);
        body.Children.Add(_split);
        body.Children.Add(bodySplit);
        body.Children.Add(issuesBox);

        DockPanel.SetDock(ToolbarRow, Dock.Top);
        DockPanel.SetDock(_readOnly, Dock.Top);
        _main.Children.Add(ToolbarRow);
        _main.Children.Add(_readOnly);
        _main.Children.Add(body);

        // no-pack prompt
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(_bNew);
        buttons.Children.Add(_bOpen);
        _prompt.Children.Add(_promptText);
        _prompt.Children.Add(buttons);
        _bNew.Click += (_, _) => Run(_newPack);
        _bOpen.Click += (_, _) => Run(_openPack);

        var status = new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Brushes.Gray, Child = _statusText };
        var root = new DockPanel();
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        var host = new Panel();
        host.Children.Add(_main);
        host.Children.Add(_prompt);
        root.Children.Add(host);
        Content = root;

        var sha = s.Archive.Sha256;
        var prune = PruneCaches;
        _ = Task.Run(() => prune(sha));

        Viewer.EnableUsedOnPage();
        Viewer.PageChanged += _ => Guard(RefreshUsed);
        Viewer.DocumentOpened += () => Guard(RefreshUsed);
        Viewer.UsedItemActivated += (kind, id) => Guard(() =>
        {
            switch (kind)
            {
                case "Formula": ShowFormula(id); break;
                case "Table": ShowTable(id); break;
                case "Phase": ShowPhase(id); break;
            }
        });

        WireSearch();
        WireScans();
        // After the search and scan parts are wired: Describe is theirs (WorkbenchWindow.Search.cs), and the editors show refs through it.
        foreach (var e in Editors) { e.DescribeRef = Describe; e.RefreshRuleRef(); }

        _validateTimer.Tick += (_, _) => { _validateTimer.Stop(); Guard(Validate); };
        _s.RulesChanged += OnRulesChanged;
        Closing += (_, _) => CommitPending();
        Closed += (_, _) => OnClosedOnce();

        Apply();
        RefreshUsed();
    }

    static TabItem Page(string header, Control content) => new() { Header = header, Content = content };

    Control BuildIssues()
    {
        ColumnDefinitions Columns()
        {
            var d = new ColumnDefinitions();
            foreach (var w in IssueColumnWidths) d.Add(new ColumnDefinition(w, GridUnitType.Pixel));
            d.Add(new ColumnDefinition(1, GridUnitType.Star));
            return d;
        }
        var header = new Grid { ColumnDefinitions = Columns() };
        for (var i = 0; i < IssueHeaderTexts.Length; i++)
        {
            var cell = new Border { BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(4, 2), Child = new TextBlock { Text = IssueHeaderTexts[i], FontWeight = FontWeight.Bold } };
            cell.Bind(Border.BorderBrushProperty, cell.GetResourceObservable("Classic.BorderBrush"));
            cell.Bind(Border.BackgroundProperty, cell.GetResourceObservable("Classic.FaceBrush"));
            Grid.SetColumn(cell, i);
            header.Children.Add(cell);
        }
        _issues.ItemTemplate = new FuncDataTemplate<IssueRow>((r, _) =>
        {
            var g = new Grid { ColumnDefinitions = Columns() };
            if (r == null) return g;
            var texts = new[] { r.Severity, r.Item, r.Problem };
            for (var i = 0; i < texts.Length; i++)
            {
                var t = new TextBlock { Text = texts[i], Margin = new Thickness(4, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(t, i);
                g.Children.Add(t);
            }
            return g;
        }, supportsRecycling: false);
        _issues.DoubleTapped += (_, _) => GoToIssue(_issues.SelectedIndex);
        var d = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        d.Children.Add(header);
        d.Children.Add(_issues);
        return d;
    }

    // ---------- public surface ----------

    public DocumentViewer Viewer { get; }
    public TabControl Tabs { get; }
    public TablesTab Tables { get; }
    public FormulasTab Formulas { get; }
    public PropertiesTab Properties { get; }
    public PhasesTab Phases { get; }
    public AlertsTab Alerts { get; }
    public VisibilityTab Visibility { get; }

    /// <summary>The problems list as shown (one "No problems found." row without an issue when the pack is clean).</summary>
    public IReadOnlyList<IssueRow> IssueRows => _issueRows;

    public string StatusText => _statusText.Text ?? "";
    public bool PromptShown => _prompt.IsVisible;
    public bool ReadOnlyShown => _main.IsVisible && _readOnly.IsVisible;

    /// <summary>Commits what is being typed in the shown tab (notes, text, an open cell edit) into the pack: before a save, a tab switch,
    /// closing, and the problems navigation. Hidden tabs committed theirs when they were hidden.</summary>
    public void CommitPending()
    {
        switch (Shown)
        {
            case TablesTab t: t.CommitPending(); break;
            case FormulasTab f: f.CommitPending(); break;
            case PropertiesTab p: p.CommitPending(); break;
            case PhasesTab ph: ph.CommitPending(); break;
            case AlertsTab a: a.CommitPending(); break;
            case VisibilityTab v: v.CommitPending(); break;
        }
    }

    Control? Shown => (Tabs.SelectedItem as TabItem)?.Content as Control;

    void Select(TabItem page)
    {
        if (ReferenceEquals(Tabs.SelectedItem, page)) return;
        CommitPending();
        Tabs.SelectedItem = page;
    }

    // ---------- pack state ----------

    void OnRulesChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) OnRulesChangedUi();
        else Dispatcher.UIThread.Post(OnRulesChangedUi);
    }

    void OnRulesChangedUi()
    {
        if (_closed) return;
        Guard(() =>
        {
            Apply(validate: false);
            RefreshUsed();
            _validateTimer.Stop();
            _validateTimer.Start();   // debounced
        });
    }

    /// <summary>An online guest reads the host's pack but never edits it (the session refuses edits too): the editors are switched off.</summary>
    void ApplyReadOnly()
    {
        var ro = _s.IsGuest;
        _readOnly.IsVisible = ro;
        foreach (var page in new[] { _tablesPage, _formulasPage, _propsPage, _phasesPage, _alertsPage, _visibilityPage })
            if (page.Content is Control c) c.IsEnabled = !ro;
    }

    void Apply(bool validate = true)
    {
        var has = _s.Rules != null;
        _main.IsVisible = has;
        _prompt.IsVisible = !has && !_s.IsGuest;
        ApplyReadOnly();
        Title = WorkbenchRules.Title(_s.Rules, _s.IsGuest);
        if (has && validate) Validate();
    }

    void Validate()
    {
        if (_closed || _s.Rules is not { } pack) return;
        IReadOnlyList<IssueRow> rows;
        try { rows = WorkbenchRules.Issues(pack, _s.Model.Maps.Select(m => m.Id).ToList()); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("workbench validation failed: " + ex);
            return;
        }
        _issueRows = rows;
        _issues.ItemsSource = rows.ToList();
    }

    /// <summary>Opens the problem's item: its tab, then the item in it. Out of range or the "No problems found." row: no-op.</summary>
    public void GoToIssue(int index)
    {
        if (index < 0 || index >= _issueRows.Count || _issueRows[index].Issue is not { } issue) return;
        CommitPending();
        if (_s.Rules is not { } p || WorkbenchRules.IssueTarget(p, issue.EntityId) is not { } target) return;
        switch (target.Tab)
        {
            case WorkbenchTab.Tables: ShowTable(target.Id); break;
            case WorkbenchTab.Formulas: ShowFormula(target.Id); break;
            case WorkbenchTab.Phases: ShowPhase(target.Id); break;
            case WorkbenchTab.Alerts: Select(_alertsPage); Alerts.SelectAlert(target.Id); break;
            case WorkbenchTab.Properties:
                Select(_propsPage);
                if (p.ById<HolderDef>(target.Id) != null) Properties.SelectHolder(target.Id); else Properties.SelectProperty(target.Id);
                break;
            case WorkbenchTab.Visibility: Select(_visibilityPage); break;
        }
    }

    // ---------- navigation (tables tree, Used on this page, problems) ----------

    /// <summary>Shows the Tables tab with the table selected. Unknown id: no-op. Works before the window is shown.</summary>
    public void ShowTable(string id)
    {
        if (_closed || _s.Rules?.ById<TableDef>(id) == null) return;
        Select(_tablesPage);
        Tables.SelectTable(id);
    }

    /// <summary>Shows the Formulas tab with the formula selected. Unknown id: no-op. Works before the window is shown.</summary>
    public void ShowFormula(string id)
    {
        if (_closed || _s.Rules?.ById<FormulaDef>(id) == null) return;
        Select(_formulasPage);
        Formulas.SelectFormula(id);
    }

    /// <summary>Shows the Phases tab with the phase selected. Unknown id: no-op.</summary>
    public void ShowPhase(string id)
    {
        if (_closed || _s.Rules?.Phases.Any(p => p.Id == id) != true) return;
        Select(_phasesPage);
        Phases.SelectPhase(id);
    }

    /// <summary>Imports an OCR grid into the Tables tab (brought to the front first), then shows the table. Null when nothing was imported.</summary>
    internal async Task<string?> ImportTableAsync(string tsv, string? ruleRefEntry, int? page)
    {
        Select(_tablesPage);
        var id = await Tables.ImportGridAsync(tsv, ruleRefEntry, page);
        if (id == null || _closed) return id;
        ShowTable(id);
        RefreshUsed();
        return id;
    }

    // ---------- rule references ----------

    /// <summary>The editors with rule refs (Tables, Formulas, Phases).</summary>
    internal IEnumerable<IRuleRefEditor> Editors => new IRuleRefEditor[] { Tables, Formulas, Phases };

    /// <summary>The editor of the shown tab when it has items with rule refs (Tables, Formulas, Phases).</summary>
    internal IRuleRefEditor? ActiveEditor => Shown as IRuleRefEditor;

    /// <summary>Creates or reuses a RuleRef for the scan page or the document page shown on the left. Null (with a message) when nothing is open.</summary>
    public RuleRef? LinkCurrentPage(string label)
    {
        if (TryLinkScanPage(label, out var scanRef)) return scanRef;
        if (Viewer.Document is not { } doc)
        {
            _ = Warn(WorkbenchRules.OpenDocumentFirstText);
            return null;
        }
        return RefFor(label, doc.EntryName, Viewer.Page);
    }

    /// <summary>Creates or reuses the RuleRef for a document page (the one path "Link to current page", scan import and "Use as rule ref" share).</summary>
    internal RuleRef? RefFor(string label, string entry, int page)
    {
        if (_s.Rules == null) return null;
        // The pack is shared with other players: a rule ref never carries a path on this computer.
        if (!ScanSessionModel.IsAllowedRefEntry(entry, _s.Model.Documents.Select(d => d.EntryName).Concat(_s.Archive.EntryNames)))
        {
            System.Diagnostics.Trace.WriteLine("workbench: refused a rule ref to a local path or unsafe entry");
            _ = Warn(WorkbenchRules.RefNotAllowedText);
            return null;
        }
        RuleRef? made = null;
        _s.UpdateRules(p =>
        {
            var (next, r) = WorkbenchRules.MakeOrReuseRef(p, label, entry, page);
            made = r;
            return next;
        });
        return made;
    }

    /// <summary>A rule ref was clicked: a module document opens in the viewer at the page; an image or opened scan in the scan pane.</summary>
    void ShowRule(RuleRef r)
    {
        var doc = _s.Model.Documents.FirstOrDefault(d => d.EntryName == r.DocumentEntry);
        if (doc != null)
        {
            BeforeShowDocument();
            Viewer.Open(doc, r.Page);
            return;
        }
        if (TryShowScanRule(r)) return;
        _ = Warn(WorkbenchRules.MissingRefText(r.DocumentEntry));
    }

    /// <summary>Before the viewer shows a module document (a rule ref): the scan pane, if shown, gives way (WorkbenchWindow.Scans.cs).</summary>
    partial void BeforeShowDocument();

    void RefreshUsed()
    {
        if (_closed) return;
        Viewer.SetUsedOnPage(SearchPanelModel.UsedOnPageRows(_s.Rules, Viewer.Document?.EntryName, Viewer.Page));
    }

    // ---------- plumbing ----------

    internal Task Warn(string text) => _dialogs.ShowInfoAsync(this, text, Caption);

    internal void SetStatus(string text) => _statusText.Text = text;

    void Guard(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("workbench: " + ex);
            _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Rules Workbench: " + ex.Message, "");
        }
    }

    async void Run(Func<Task> a)
    {
        try { await a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("workbench: " + ex);
            _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Rules Workbench: " + ex.Message, "");
        }
    }

    void OnClosedOnce()
    {
        if (_closed) return;
        _closed = true;
        _validateTimer.Stop();
        _s.RulesChanged -= OnRulesChanged;
        Guard(OnSearchClosed);
        Guard(OnScansClosed);
        ScanModel.CancelRead();
        Viewer.Dispose();
    }

    // ---------- test access ----------

    internal bool MainShown => _main.IsVisible;
    internal string PromptText => _promptText.Text ?? "";
    internal string ReadOnlyText => _readOnlyText.Text ?? "";
    internal Button NewPackButton => _bNew;
    internal Button OpenPackButton => _bOpen;
    internal ListBox IssueList => _issues;
    internal IReadOnlyList<string> IssueHeaders => IssueHeaderTexts;
    internal IReadOnlyList<double> IssueWidths => IssueColumnWidths;
    internal bool ValidatePending => _validateTimer.IsEnabled;
}
