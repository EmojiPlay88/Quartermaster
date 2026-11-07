using Quartermaster.Ocr;
using Quartermaster.Render;
using Quartermaster.Render.Ocr;
using Quartermaster.Render.Search;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>
/// Rules Workbench: the module's rulebook on the left as a reference the user reads, the user's own editors on the right.
/// Nothing is ever read out of the documents; every table and formula is typed or pasted by the user.
/// </summary>
sealed class WorkbenchWindow : Form, WindowManager.IReparentAware
{
    readonly ModuleSession _s;
    readonly PdfViewerControl _viewer;
    readonly SplitContainer _split = new() { Dock = DockStyle.Fill };
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    readonly TabPage _tablesPage = new("Tables"), _formulasPage = new("Formulas"), _propsPage = new("Properties & Holders"), _phasesPage = new("Phases"), _alertsPage = new("Alerts"), _visibilityPage = new("Visibility");
    readonly VisibilityTab _visibilityTab;
    readonly TablesTab _tables;
    readonly FormulasTab _formulas;
    readonly PropertiesTab _props;
    readonly PhasesTab _phasesTab;
    readonly AlertsTab _alertsTab;
    readonly ListView _issues = new()
    {
        Dock = DockStyle.Bottom, Height = 110, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
    };
    readonly Panel _main = new() { Dock = DockStyle.Fill };
    readonly Panel _prompt = new() { Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer _validateTimer = new() { Interval = 300 };
    bool _splitSet;

    // rulebook search
    readonly SearchPanel _search = new() { Dock = DockStyle.Bottom, Height = SearchPanel.ExpandedHeight };
    readonly Splitter _searchSplit = new() { Dock = DockStyle.Bottom, Height = 4 };
    readonly ToolStrip _toolbar = new() { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System, Dock = DockStyle.Top };
    readonly ToolStripTextBox _searchBox = new() { AutoSize = false, Width = 260 };
    readonly ToolStripButton _searchBtn = new("Search") { DisplayStyle = ToolStripItemDisplayStyle.Text, Enabled = false };
    readonly StatusStrip _status = new() { SizingGrip = false };
    readonly ToolStripStatusLabel _statusText = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly CancellationTokenSource _indexCts = new();
    readonly List<(string Entry, string Title)> _moduleDocs;
    RulebookIndex? _index;
    Task? _indexTask;
    string? _pendingQuery;
    string _lastQuery = "";
    int _searchHeight = SearchPanel.ExpandedHeight;
    int _indexGen;
    CancellationTokenSource? _indexBuildCts;

    // scans / OCR
    readonly ScanSessionModel _scanModel = new();
    readonly ScanPane _scan;
    readonly IReadOnlyList<ScanSource> _allModuleImages;
    IReadOnlyList<ScanSource> _moduleImages = Array.Empty<ScanSource>(); // the listed ones (≥ 64 KB unless "Show all images")
    bool _showAllImages;
    int _hiddenImages;
    readonly ToolStripButton _scanPage = new("Scan this page") { DisplayStyle = ToolStripItemDisplayStyle.Text, Enabled = false };
    readonly ToolStripButton _openScan = new("Open scan…") { DisplayStyle = ToolStripItemDisplayStyle.Text };

    public WorkbenchWindow(ModuleSession s, Action newPack, Action openPack)
    {
        _s = s;
        ShowIcon = false;
        Size = new Size(1000, 640);

        _viewer = new PdfViewerControl(s, toolbar: true, allowWeb: !Program.Smoke) { Dock = DockStyle.Fill };
        _tables = new TablesTab(s, LinkCurrentPage, ShowRule) { Dock = DockStyle.Fill };
        _tablesPage.Controls.Add(_tables);
        _formulas = new FormulasTab(s, LinkCurrentPage, ShowRule) { Dock = DockStyle.Fill };
        _formulasPage.Controls.Add(_formulas);
        _props = new PropertiesTab(s) { Dock = DockStyle.Fill };
        _propsPage.Controls.Add(_props);
        _phasesTab = new PhasesTab(s, LinkCurrentPage, ShowRule) { Dock = DockStyle.Fill };
        _phasesPage.Controls.Add(_phasesTab);
        _alertsTab = new AlertsTab(s) { Dock = DockStyle.Fill };
        _alertsPage.Controls.Add(_alertsTab);
        _visibilityTab = new VisibilityTab(s) { Dock = DockStyle.Fill };
        _visibilityPage.Controls.Add(_visibilityTab);
        _tabs.TabPages.AddRange(new[] { _tablesPage, _formulasPage, _propsPage, _phasesPage, _alertsPage, _visibilityPage });
        _scan = new ScanPane(s, _scanModel, new OcrService()) { Dock = DockStyle.Fill, Visible = false };
        _split.Panel1.Controls.Add(_viewer);
        _split.Panel1.Controls.Add(_scan);
        _split.Panel1.Controls.Add(_searchSplit);
        _split.Panel1.Controls.Add(_search);
        _split.Panel2.Controls.Add(_tabs);

        _issues.Columns.Add("Severity", 70);
        _issues.Columns.Add("Item", 140);
        _issues.Columns.Add("Problem", 600);
        _issues.DoubleClick += (_, _) => GoToIssue();
        _main.Controls.Add(_split);
        _main.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
        _main.Controls.Add(_issues);
        _main.Controls.Add(_toolbar);
        _toolbar.Items.Add(new ToolStripLabel("Rulebooks:"));
        _toolbar.Items.Add(_searchBox);
        _toolbar.Items.Add(_searchBtn);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(_openScan);
        _searchBox.TextBox.PlaceholderText = "Search the rulebooks (Ctrl+Shift+F)";
        _status.Items.Add(_statusText);

        var msg = new Label { Text = "No rules pack is loaded. A rules pack holds the tables and formulas you write yourself.", AutoSize = true, Left = 20, Top = 20 };
        var bNew = new Button { Text = "New rules pack…", AutoSize = true, Left = 20, Top = 56 };
        var bOpen = new Button { Text = "Open…", AutoSize = true, Left = 140, Top = 56 };
        bNew.Click += (_, _) => newPack();
        bOpen.Click += (_, _) => openPack();
        _prompt.Controls.AddRange(new Control[] { msg, bNew, bOpen });

        Controls.Add(_main);
        Controls.Add(_prompt);
        Controls.Add(_status);

        _moduleDocs = s.Model.Documents.Select(d => (d.EntryName, d.Title)).ToList();
        _allModuleImages = ScanSessionModel.ModuleImages(s.Archive.EntryNames);
        (_moduleImages, _hiddenImages) = ScanSessionModel.ModuleImages(s.Archive.EntrySizes, _showAllImages);
        _ = Task.Run(() =>
        {
            try { ScanCache.Prune(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.AppLog("scan cache prune: " + ex.Message); }
        });
        WireSearch();
        WireScans();

        _validateTimer.Tick += (_, _) => { _validateTimer.Stop(); Validate2(); };
        _s.RulesChanged += OnRulesChanged;
        Shown += (_, _) => { SetSplit(); TryPending(); };
        FormClosed += (_, _) => { _indexCts.Cancel(); _scanModel.CancelRead(); _s.RulesChanged -= OnRulesChanged; _validateTimer.Dispose(); };

        if (Program.Smoke && s.Model.Documents.FirstOrDefault(d => d.Kind == Quartermaster.Vassal.DocumentKind.Pdf) is { } doc) _viewer.Open(doc);
        Apply();
        RefreshUsed();
        StartIndex();
    }

    public PdfViewerControl Viewer => _viewer;

    void SetSplit()
    {
        if (_splitSet || _split.Width < 300) return;
        _splitSet = true;
        _split.SplitterDistance = _split.Width * 40 / 100;
    }

    void WindowManager.IReparentAware.BeforeReparent() => _viewer.BeforeReparent();

    void WindowManager.IReparentAware.AfterReparent() => _viewer.AfterReparent();

    // ---------- pack state ----------

    void OnRulesChanged() => OnUi(() =>
    {
        Apply(validate: false);
        RefreshUsed();
        _validateTimer.Stop();
        _validateTimer.Start(); // debounced
    });

    void OnUi(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(new Action(() => { if (!IsDisposed) a(); }));
        else a();
    }

    readonly Label _readOnly = new()
    {
        Dock = DockStyle.Top, Height = 24, Visible = false, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 8, 0),
        BackColor = Color.FromArgb(255, 240, 200), Text = "Read-only: the host's rules pack for this online game. Only the host can change it.",
    };

    /// <summary>An online guest reads the host's pack but never edits it (the session refuses edits too): the editors are switched off.</summary>
    void ApplyReadOnly()
    {
        var ro = _s.IsGuest;
        if (_readOnly.Parent == null) { _main.Controls.Add(_readOnly); _readOnly.SendToBack(); }
        _readOnly.Visible = ro;
        foreach (TabPage page in _tabs.TabPages)
            foreach (Control c in page.Controls) c.Enabled = !ro;
    }

    void Apply(bool validate = true)
    {
        var has = _s.Rules != null;
        _main.Visible = has;
        _prompt.Visible = !has && !_s.IsGuest;
        ApplyReadOnly();
        Text = has ? $"Rules Workbench — {_s.Rules!.Name}" + (_s.IsGuest ? " (read-only)" : "") : "Rules Workbench";
        if (has && validate) Validate2();
    }

    void Validate2()
    {
        if (IsDisposed || _s.Rules is not { } pack) return;
        IReadOnlyList<RulesIssue> list;
        try { list = RulesValidator.Validate(pack, _s.Model.Maps.Select(m => m.Id).ToList()); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("workbench validation failed: " + ex);
            return;
        }
        _issues.BeginUpdate();
        try
        {
            _issues.Items.Clear();
            foreach (var i in list)
            {
                var it = new ListViewItem(new[] { i.Severity.ToString(), i.EntityName, i.Message }) { Tag = i };
                _issues.Items.Add(it);
            }
            if (list.Count == 0) _issues.Items.Add(new ListViewItem(new[] { "", "", "No problems found." }));
        }
        finally { _issues.EndUpdate(); }
    }

    public int IssueCountForSmoke => _issues.Items.Cast<ListViewItem>().Count(i => i.Tag is RulesIssue);

    void GoToIssue()
    {
        if (_issues.SelectedItems.Count == 0 || _issues.SelectedItems[0].Tag is not RulesIssue i || _s.Rules is not { } p) return;
        if (p.ById<TableDef>(i.EntityId) != null) { _tabs.SelectedTab = _tablesPage; _tables.SelectTable(i.EntityId); }
        else if (p.ById<FormulaDef>(i.EntityId) != null) { _tabs.SelectedTab = _formulasPage; _formulas.SelectFormula(i.EntityId); }
        else if (p.ById<PhaseDef>(i.EntityId) != null) ShowPhase(i.EntityId);
        else if (p.ById<AlertDef>(i.EntityId) != null) { _tabs.SelectedTab = _alertsPage; _alertsTab.SelectAlert(i.EntityId); }
        else if (p.ById<PropertyDef>(i.EntityId) != null) { _tabs.SelectedTab = _propsPage; _props.SelectProperty(i.EntityId); }
        else if (p.ById<HolderDef>(i.EntityId) != null) { _tabs.SelectedTab = _propsPage; _props.SelectHolder(i.EntityId); }
        else if (p.MapOwners.Any(o => o.MapId == i.EntityId)) _tabs.SelectedTab = _visibilityPage;
    }

    // ---------- navigation from the tables tree ----------

    string? _pendingTable, _pendingFormula;

    /// <summary>Shows the Tables tab with the table selected. Unknown id: no-op. Deferred until the tab has the table.</summary>
    public void ShowTable(string tableId)
    {
        if (IsDisposed || _s.Rules?.ById<TableDef>(tableId) == null) return;
        _pendingTable = tableId;
        _pendingTries = 0;
        _tabs.SelectedTab = _tablesPage;
        TryPending();
    }

    /// <summary>Shows the Formulas tab with the formula selected. Unknown id: no-op. Deferred until the tab has the formula.</summary>
    public void ShowFormula(string formulaId)
    {
        if (IsDisposed || _s.Rules?.ById<FormulaDef>(formulaId) == null) return;
        _pendingFormula = formulaId;
        _pendingTries = 0;
        _tabs.SelectedTab = _formulasPage;
        TryPending();
    }

    /// <summary>Shows the Phases tab with the phase selected. Unknown id: no-op.</summary>
    public void ShowPhase(string phaseId)
    {
        if (IsDisposed || _s.Rules?.Phases.Any(p => p.Id == phaseId) != true) return;
        _tabs.SelectedTab = _phasesPage;
        _phasesTab.SelectPhase(phaseId);
    }

    int _pendingTries;

    void TryPending()
    {
        if (IsDisposed) return;
        if (_pendingTable is { } t)
        {
            _tables.SelectTable(t);
            if (_tables.SelectedTableId == t) _pendingTable = null;
        }
        if (_pendingFormula is { } f)
        {
            _formulas.SelectFormula(f);
            if (_formulas.SelectedFormulaId == f) _pendingFormula = null;
        }
        if (_pendingTable == null && _pendingFormula == null) { _pendingTries = 0; return; }
        // A tab that has not loaded yet (no handle) picks the item up on a later pass; give up after a few.
        if (++_pendingTries > 5 || !IsHandleCreated) return;
        BeginInvoke(new Action(TryPending));
    }

    // ---------- rule references ----------

    /// <summary>Creates or reuses a RuleRef for the document and page shown in the viewer. Null (with a message) when nothing is open.</summary>
    public RuleRef? LinkCurrentPage(string label)
    {
        if (_scan.Visible && _scan.Source is { } src)
        {
            var (entry, page) = ScanSessionModel.RuleRefFor(src, _scan.Page);
            return RefFor(label, entry, page);
        }
        if (_viewer.Document is not { } doc)
        {
            Warn("Open a document in the left pane first, then go to the page to link.");
            return null;
        }
        return RefFor(label, doc.EntryName, _viewer.Page);
    }

    /// <summary>Creates or reuses the RuleRef for a document page (the one path "Link to current page" and "Use as rule ref" share).</summary>
    RuleRef? RefFor(string label, string entry, int page)
    {
        if (_s.Rules == null) return null;
        RuleRef? made = null;
        _s.UpdateRules(p =>
        {
            var existing = p.RuleRefs.FirstOrDefault(r => r.DocumentEntry == entry && r.Page == page);
            if (existing != null) { made = existing; return p; }
            made = new RuleRef { Id = Guid.NewGuid().ToString("N"), Label = label, DocumentEntry = entry, Page = page };
            return p with { RuleRefs = p.RuleRefs.Append(made).ToList() };
        });
        return made;
    }

    void ShowRule(RuleRef r)
    {
        var doc = _s.Model.Documents.FirstOrDefault(d => d.EntryName == r.DocumentEntry);
        if (doc != null) { ShowPdf(); _viewer.Open(doc, r.Page); return; }
        var img = _allModuleImages.FirstOrDefault(i => i.RefEntry == r.DocumentEntry)
            ?? _scanModel.Opened.FirstOrDefault(o => string.Equals(o.RefEntry, r.DocumentEntry, StringComparison.OrdinalIgnoreCase));
        if (img != null) { _ = ShowScan(img, Math.Max(1, r.Page)); return; }
        Warn($"The reference points to '{r.DocumentEntry}', which is not in this module" + (ScanSessionModel.IsScanFile(r.DocumentEntry) ? " (open it with Open scan… to see it)." : "."));
    }

    // ---------- rulebook search ----------

    IEnumerable<IRuleRefEditor> Editors => new IRuleRefEditor[] { _tables, _formulas, _phasesTab };

    /// <summary>The editor of the shown tab when it has items with rule refs (Tables, Formulas, Phases).</summary>
    IRuleRefEditor? ActiveEditor =>
        _tabs.SelectedTab == _formulasPage ? _formulas : _tabs.SelectedTab == _tablesPage ? _tables : _tabs.SelectedTab == _phasesPage ? _phasesTab : null;

    void WireSearch()
    {
        foreach (var e in Editors)
        {
            e.DescribeRef = r => SearchPanelModel.DescribeRef(r, RefDocs, _index?.Documents);
            e.FindInRulebook = q => { FocusSearchBox(select: false); RunSearch(q); };
            e.RefreshRuleRef();
        }
        _search.Collapsed = true;
        _search.Title = "Rulebook search: type in the box above (Ctrl+Shift+F)";
        _search.CollapsedChanged += () =>
        {
            if (_search.Collapsed) _search.Height = SearchPanel.HeaderHeight;
            _searchSplit.Enabled = !_search.Collapsed;
            FitSearch();
        };
        _search.Height = SearchPanel.HeaderHeight;
        _searchSplit.Enabled = false;
        _searchSplit.SplitterMoved += (_, _) => { if (!_search.Collapsed) _searchHeight = _search.Height; };
        _split.Panel1.Resize += (_, _) => FitSearch();
        _search.HitActivated += (entry, page) => Guard(() => OpenHit(entry, page));
        _search.UseAsRuleRef += (entry, page) => Guard(() => UseAsRuleRef(entry, page));
        _search.CanUseRuleRef = () => !_s.IsGuest && _s.Rules != null && ActiveEditor?.CanUseRuleRef == true;
        _searchBtn.Click += (_, _) => Guard(() => RunSearch(_searchBox.Text));
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            Guard(() => RunSearch(_searchBox.Text));
        };
        _viewer.EnableUsedOnPage();
        _viewer.PageChanged += _ => RefreshUsed();
        _viewer.DocumentOpened += RefreshUsed;
        _viewer.DocumentOpened += () => _scanPage.Enabled = _viewer.Document?.Kind == DocumentKind.Pdf;
        _viewer.UsedItemActivated += (kind, id) => Guard(() =>
        {
            switch (kind)
            {
                case "Formula": ShowFormula(id); break;
                case "Table": ShowTable(id); break;
                case "Phase": ShowPhase(id); break;
            }
        });
    }

    /// <summary>The expanded results panel keeps its height (or the one the user dragged) but never takes more than half the PDF pane.</summary>
    void FitSearch()
    {
        if (_search.Collapsed) return;
        var max = Math.Max(SearchPanel.HeaderHeight * 3, _split.Panel1.ClientSize.Height / 2);
        _search.Height = Math.Min(Math.Max(_searchHeight, SearchPanel.HeaderHeight * 3), max);
    }

    void Guard(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("workbench search: " + ex);
            _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Rulebook search failed: " + ex.Message, "");
        }
    }

    /// <summary>Indexes the module's PDFs in the background (cached per document); the status bar shows progress.</summary>
    void StartIndex()
    {
        var pdfs = _s.Model.Documents.Where(d => d.Kind == DocumentKind.Pdf).GroupBy(d => d.EntryName).Select(g => g.First())
            .Select(d => (d.EntryName, d.Title, (Func<Stream>)(() => _s.Archive.OpenEntry(d.EntryName)))).ToList();
        // PDFs opened with "Open scan…" are searchable for this session too (keyed by their full path).
        foreach (var o in _scanModel.OpenedPdfs)
        {
            var path = o.Key;
            pdfs.Add((path, o.Title, () => File.OpenRead(path)));
        }
        // A rebuild supersedes the running build (the old index stays usable until the new one is ready). Each build's source is
        // disposed once its build has finished, so a running Task.Run never sees a disposed token source.
        try { _indexBuildCts?.Cancel(); }
        catch (ObjectDisposedException) { } // that build already finished
        _indexBuildCts = null;
        var gen = ++_indexGen;
        if (pdfs.Count == 0)
        {
            _index = RulebookIndex.Empty;
            OnIndexReady("This module has no PDF rulebooks to search.");
            return;
        }
        var last = -1;
        _statusText.Text = "Indexing rulebooks… 0 %";
        var progress = new Progress<double>(f =>
        {
            if (IsDisposed || gen != _indexGen) return;
            var pct = (int)Math.Clamp(f * 100, 0, 100);
            if (pct == last) return;
            last = pct;
            _statusText.Text = $"Indexing rulebooks… {pct} %";
        });
        var cts = _indexBuildCts = CancellationTokenSource.CreateLinkedTokenSource(_indexCts.Token);
        _indexTask = BuildIndexAsync(pdfs, progress, gen, cts.Token);
        _indexTask.ContinueWith(_ => cts.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    async Task BuildIndexAsync(IReadOnlyList<(string, string, Func<Stream>)> pdfs, IProgress<double> progress, int gen, CancellationToken ct)
    {
        RulebookIndex ix;
        string? failed = null;
        try { ix = await RulebookIndex.BuildAsync(_s.Archive.Sha256, pdfs, progress, ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("rulebook index failed: " + ex);
            ix = RulebookIndex.Empty;
            failed = "The rulebooks could not be indexed: " + ex.Message;
        }
        if (IsDisposed || ct.IsCancellationRequested || gen != _indexGen) return;
        _index = ix;
        var bad = ix.Documents.Count(d => !d.Searchable);
        OnIndexReady(failed ?? $"Rulebooks indexed: {ix.Documents.Count - bad} searchable" + (bad > 0 ? $", {bad} {SearchPanelModel.NotSearchableText}" : "") + ".");
    }

    void OnIndexReady(string status)
    {
        _statusText.Text = status;
        _searchBtn.Enabled = true;
        foreach (var e in Editors) e.RefreshRuleRef();
        if (_pendingQuery is { } q) RunSearch(q);
    }

    /// <summary>Searches the rulebooks and shows the results; before the index is ready the query waits and runs when it is.</summary>
    public void RunSearch(string? query)
    {
        query = (query ?? "").Trim();
        if (_searchBox.Text != query) _searchBox.Text = query;
        if (query.Length == 0) return;
        if (_index == null)
        {
            _pendingQuery = query;
            _search.Title = $"Rulebook search: {query} (runs when indexing finishes)";
            return;
        }
        _pendingQuery = null;
        _lastQuery = query;
        var rows = SearchPanelModel.Build(_index, query);
        var pages = rows.Count(r => r.Kind == SearchRowKind.Hit);
        _search.Title = $"Rulebook search: {query} ({pages} page{(pages == 1 ? "" : "s")})";
        _search.ShowRows(rows);
    }

    void FocusSearchBox(bool select)
    {
        if (!_main.Visible) return;
        _searchBox.Focus();
        if (select) _searchBox.SelectAll();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Shift | Keys.F))
        {
            FocusSearchBox(select: true);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void OpenHit(string entry, int page)
    {
        var doc = _s.Model.Documents.FirstOrDefault(d => d.EntryName == entry);
        if (doc == null)
        {
            // A PDF opened with "Open scan…": shown as a rendered page in the scan pane.
            if (_scanModel.Opened.FirstOrDefault(o => o.Key == entry) is { } src) { _ = ShowScan(src, page); return; }
            Warn($"'{entry}' is not in this module.");
            return;
        }
        ShowPdf();
        _viewer.Open(doc, page, SearchPanelModel.ViewerSearchTerm(_lastQuery));
    }

    void UseAsRuleRef(string entry, int page)
    {
        if (_s.IsGuest || ActiveEditor is not { CanUseRuleRef: true } editor)
        {
            Warn("Select a formula, table or phase first, then use the result as its rule ref.");
            return;
        }
        editor.UseRuleRef(label => RefFor(label, entry, page));
        RefreshUsed();
    }

    void RefreshUsed()
    {
        if (IsDisposed) return;
        _viewer.SetUsedOnPage(SearchPanelModel.UsedOnPageRows(_s.Rules, _viewer.Document?.EntryName, _viewer.Page));
    }

    void Warn(string text)
    {
        if (Program.Smoke) { Program.AppLog("workbench: " + text); return; }
        MessageBox.Show(this, text, "Rules Workbench", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---------- scans / OCR ----------

    /// <summary>Module images then opened scans: the "Images" part of the document lists.</summary>
    IReadOnlyList<ScanSource> ImageSources => _moduleImages.Concat(_scanModel.Opened).ToList();

    /// <summary>Documents a rule ref can point at: the module's documents, its images and the scans opened this session.</summary>
    IReadOnlyList<(string Entry, string Title)> RefDocs =>
        _moduleDocs.Concat(_allModuleImages.Select(i => (i.RefEntry, i.Title))).Concat(_scanModel.Opened.Select(o => (o.RefEntry, o.Title))).ToList();

    void WireScans()
    {
        _viewer.AddToolItem(new ToolStripSeparator());
        _viewer.AddToolItem(_scanPage);
        _scanPage.ToolTipText = $"Render this page as an image ({ScanPane.ScanDpi} DPI) to read a chart with OCR";
        _scanPage.Click += (_, _) => ScanGuard(() =>
        {
            if (_viewer.Document is not { Kind: DocumentKind.Pdf } doc) { Warn("Open a PDF page first."); return; }
            _ = ShowScan(ScanSource.ModulePdf(doc.EntryName, doc.Title), _viewer.Page);
        });
        _openScan.ToolTipText = "Open a scan or photo of a chart (image or PDF) to read it with OCR";
        _openScan.Click += (_, _) => ScanGuard(ChooseScanFile);
        _viewer.ImageDocumentChosen += i => ScanGuard(() => ChooseImage(i));
        _scan.DocumentChosen += i => ScanGuard(() =>
        {
            var pdfs = _viewer.PdfDocuments;
            if (i < pdfs.Count) { ShowPdf(); _viewer.Open(pdfs[i]); return; }
            ChooseImage(i - pdfs.Count);
        });
        _scan.BackToPdf += () => ScanGuard(BackToPdf);
        _scan.ImportRequested += (tsv, src, page) => ScanGuard(() => ImportScan(tsv, src, page));
        _tables.MakeRuleRef = RefFor;
        RefreshDocLists();
    }

    void ScanGuard(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("workbench scan: " + ex);
            _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Scan failed: " + ex.Message, "");
        }
    }

    string? ImagesToggle => ScanSessionModel.ImagesToggleText(_hiddenImages, _showAllImages);

    /// <summary>An "Images" line was chosen: an image, or the last line that shows/hides the small module images.</summary>
    void ChooseImage(int i)
    {
        var list = ImageSources;
        if (i >= 0 && i < list.Count) { _ = ShowScan(list[i], 1); return; }
        if (i == list.Count && ImagesToggle != null) ToggleSmallImages();
    }

    /// <summary>"Show all images (N hidden)" / "Hide small images".</summary>
    public void ToggleSmallImages()
    {
        _showAllImages = !_showAllImages;
        (_moduleImages, _hiddenImages) = ScanSessionModel.ModuleImages(_s.Archive.EntrySizes, _showAllImages);
        RefreshDocLists();
        if (_scan.Visible && _scan.Source is { } src) _scan.MarkSelected(DocIndex(src));
    }

    /// <summary>The source's line in the document lists (PDFs, then images), or -1.</summary>
    int DocIndex(ScanSource src)
    {
        var pdfAt = src.Kind == ScanSourceKind.ModulePdf ? _viewer.PdfDocuments.ToList().FindIndex(d => d.EntryName == src.Key) : -1;
        if (pdfAt >= 0) return pdfAt;
        var at = ImageSources.ToList().FindIndex(x => x.Key == src.Key);
        return at >= 0 ? _viewer.PdfDocuments.Count + at : -1;
    }

    /// <summary>Smoke: the listed module images and how many small ones are hidden.</summary>
    public (int Listed, int Hidden, int All) ImageListForSmoke => (_moduleImages.Count, _hiddenImages, _allModuleImages.Count);

    void RefreshDocLists()
    {
        var titles = _moduleImages.Select(i => i.Title).Concat(_scanModel.Opened.Select(o => o.Title + " (opened)")).ToList();
        if (ImagesToggle is { } toggle) titles.Add(toggle);
        _viewer.SetImageDocuments(titles);
        _scan.SetDocuments(_viewer.PdfDocuments.Select(d => d.Title).ToList(), titles);
        foreach (var e in Editors) e.RefreshRuleRef();
    }

    void ChooseScanFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Open scan", Filter = ScanSessionModel.OpenScanFilter, CheckFileExists = true, Multiselect = false,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _ = OpenScanFile(dlg.FileName);
    }

    /// <summary>Opens an image or PDF file as a scan (session only); an opened PDF is also added to the rulebook search for the session.</summary>
    public Task OpenScanFile(string path)
    {
        if (!File.Exists(path)) { Warn($"'{path}' was not found."); return Task.CompletedTask; }
        if (!ScanSessionModel.IsScanFile(path)) { Warn("Choose an image (.png .jpg .jpeg .bmp .gif .tif .tiff) or a PDF."); return Task.CompletedTask; }
        var before = _scanModel.Opened.Count;
        var src = _scanModel.AddFile(path);
        if (_scanModel.Opened.Count != before)
        {
            RefreshDocLists();
            if (src.IsPdf) StartIndex();
        }
        return ShowScan(src, 1);
    }

    /// <summary>Switches the left pane to the scan pane showing the source (a PDF page or an image).</summary>
    public Task ShowScan(ScanSource src, int page)
    {
        if (!_scan.Visible) WidenForScan();
        _viewer.Visible = false;
        _scan.Visible = true;
        _scan.MarkSelected(DocIndex(src));
        return _scan.ShowSource(src, page);
    }

    /// <summary>The scan pane holds the image and the OCR panel (≥ 280 px): give the left pane about 60 % of the window when it has less.</summary>
    void WidenForScan()
    {
        SetSplit();
        var want = _split.Width * 60 / 100;
        if (_split.Width < 400 || _split.SplitterDistance >= want) return;
        try { _split.SplitterDistance = want; }
        catch (InvalidOperationException) { }
    }

    void ShowPdf()
    {
        if (!_scan.Visible) return;
        _scan.Ocr.CancelRead("Cancelled: back to the PDF.");
        _scan.Visible = false;
        _viewer.Visible = true;
    }

    /// <summary>Back to PDF: a scanned module page goes back to that page in the viewer.</summary>
    void BackToPdf()
    {
        var src = _scan.Source;
        var page = _scan.Page;
        ShowPdf();
        if (src is { Kind: ScanSourceKind.ModulePdf } && _viewer.PdfDocuments.FirstOrDefault(d => d.EntryName == src.Key) is { } doc) _viewer.Open(doc, page);
    }

    void ImportScan(string tsv, ScanSource src, int page)
    {
        var (entry, refPage) = ScanSessionModel.RuleRefFor(src, page);
        // The ref says "scan:{file}", so it can't point at a module entry; still tell the user when the names look the same.
        if (!src.InModule && _s.Archive.EntryNames.Any(e => string.Equals(Path.GetFileName(e), src.Title, StringComparison.OrdinalIgnoreCase)))
            Warn($"The module also has a document named \"{src.Title}\". The imported table's rule ref points to the opened scan ({entry}), not to the module's copy.");
        _tabs.SelectedTab = _tablesPage;
        var id = _tables.ImportGrid(tsv, entry, refPage);
        if (id == null) return;
        ShowTable(id);
        RefreshUsed();
    }

    // ---------- smoke ----------

    static void Pump(Func<bool> done, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(15); }
        Application.DoEvents();
    }

    /// <summary>
    /// Smoke (--smoke-ocr): draws an invented table to scratch, opens it with the Open scan… path, selects the table region, reads it,
    /// checks the grid, imports it into a new table (the Paste table dialog confirms itself in smoke runs) and checks the table and its
    /// rule ref; then Scan this page on the module PDF, Back to PDF, and back to the image (its region must come back).
    /// Returns (problem, skipped note); both null when all is well.
    /// </summary>
    public (string? Problem, string? Skipped) OcrSmoke()
    {
        if (_s.Rules == null) return ("no rules pack is loaded", null);
        var (path, region, expected) = SmokeOcrTable.Write(AppDataRoot.Sub("smoke-ocr"));
        Program.AppLog($"smoke: invented table image written to {path}");
        var open = OpenScanFile(path);
        Pump(() => open.IsCompleted && _scan.Loading.IsCompleted, 20);
        if (_scan.Image == null) return ($"the scan did not open ({_scan.Pane.Message})", null);
        if (!_scan.Visible || _viewer.Visible) return ("the scan pane is not shown", null);
        if (_scan.Source?.RefEntry != ScanSessionModel.ScanRefPrefix + Path.GetFileName(path)) return ("the opened scan is not the shown source", null);
        Program.AppLog($"smoke: scan shown {_scan.Image.Width}×{_scan.Image.Height}; OCR panel width {_scan.Ocr.Width}");
        if (_scan.Ocr.Width < OcrPanel.MinPanelWidth) return ($"the OCR panel is narrower than {OcrPanel.MinPanelWidth} px", null);
        _scan.Pane.CommitRegion(region); // the same path a finished drag takes
        if (_scanModel.GetRegion(_scan.Source.Key, 1, _scan.Image.Size) != region) return ("the region was not remembered", null);

        if (OcrService.Languages().Count == 0)
        {
            _scan.Ocr.ClickReadForSmoke();
            Pump(() => _scan.Ocr.LastRead.IsCompleted, 10);
            return (_scan.Ocr.StatusText == OcrService.NoLanguageMessage ? null : "the no-language message was not shown: " + _scan.Ocr.StatusText,
                "no OCR language is installed; Read text showed the install message, the read/import steps were skipped");
        }

        _scan.Ocr.ShowGridTab();
        _scan.Ocr.ClickReadForSmoke();
        Pump(() => _scan.Ocr.LastRead.IsCompleted, 60);
        Program.AppLog("smoke: ocr status: " + _scan.Ocr.StatusText);
        var cells = _scan.Ocr.Cells;
        Program.AppLog("smoke: ocr grid: " + string.Join(" / ", cells.Select(r => string.Join(" | ", r))));
        if (cells.Length != expected.Length) return ($"the grid has {cells.Length} rows, expected {expected.Length}", null);
        int digitCells = 0, exact = 0, keys = 0, reread = 0;
        for (var r = 0; r < expected.Length; r++)
            for (var c = 0; c < expected[r].Length; c++)
            {
                var got = c < cells[r].Length ? cells[r][c] : "";
                if (r == 0) { if (got != expected[r][c]) return ($"header cell c{c} reads '{got}', expected '{expected[r][c]}'", null); continue; }
                if (c == 0)
                {
                    // Single-digit row keys: the first pass skips lone characters, the second pass over empty cells finds them.
                    if (got == expected[r][c]) keys++;
                    if (_scan.Ocr.IsReread(r, c)) reread++;
                    continue;
                }
                digitCells++;
                if (got == expected[r][c]) exact++;
            }
        Program.AppLog($"smoke: ocr digit cells {exact}/{digitCells} exact; single-digit row keys {keys}/{expected.Length - 1} ({reread} by the empty-cell re-read)");
        if (exact < 0.95 * digitCells) return ($"only {exact}/{digitCells} digit cells read exactly", null);
        if (keys < expected.Length - 2) return ($"only {keys}/{expected.Length - 1} single-digit row keys were recovered", null);
        if (reread == 0) return ("no row key came from the empty-cell re-read (or it is not marked)", null);

        var before = _s.Rules.Tables.Select(t => t.Id).ToHashSet();
        if (!_scan.Ocr.ClickImportForSmoke()) return ("Import into table… is not enabled", null);
        Application.DoEvents();
        var made = _s.Rules!.Tables.FirstOrDefault(t => !before.Contains(t.Id));
        if (made == null) return ("no table was created by the import", null);
        Program.AppLog($"smoke: imported table '{made.Name}' {made.Kind}: cols [{string.Join(", ", made.ColumnKeys)}] rows [{string.Join(", ", made.RowKeys)}]");
        if (made.Kind != Quartermaster.Rules.TableKind.TwoD) return ("the imported table is not two-dimensional", null);
        if (!made.ColumnKeys.SequenceEqual(expected[0].Skip(1))) return ("the imported column keys differ from the header row", null);
        if (!made.RowKeys.SequenceEqual(cells.Skip(1).Select(r => r[0]))) return ("the imported row keys differ from the grid's first column", null);
        for (var r = 1; r < cells.Length; r++)
            if (!made.Cells[r - 1].SequenceEqual(cells[r].Skip(1).Take(made.ColumnKeys.Count))) return ($"imported row {r} differs from the grid", null);
        if (_tables.SelectedTableId != made.Id) return ("the imported table is not selected in the Tables tab", null);
        var rr = made.RuleRefId == null ? null : _s.Rules.ById<RuleRef>(made.RuleRefId);
        if (rr == null || rr.DocumentEntry != ScanSessionModel.ScanRefPrefix + Path.GetFileName(path) || rr.Page != 0) return ("the imported table's rule ref is not the scan file (no page)", null);
        var shown = SearchPanelModel.DescribeRef(rr, RefDocs, _index?.Documents);
        Program.AppLog($"smoke: imported table rule ref reads '{shown.Text}' (link {shown.IsLink})");
        if (!shown.IsLink) return ("the imported table's rule ref is not a link", null);

        var imageSrc = _scan.Source!;
        var (listed, hidden, all) = ImageListForSmoke;
        Program.AppLog($"smoke: module images listed {listed}, hidden {hidden}, all {all}");
        if (listed + hidden != all) return ("the image list does not add up", null);
        if (hidden > 0)
        {
            ToggleSmallImages();
            if (ImageListForSmoke.Listed != all) return ("Show all images did not list every module image", null);
            ToggleSmallImages();
        }
        if (_viewer.PdfDocuments.FirstOrDefault() is { } pdf)
        {
            BackToPdf();
            _viewer.Open(pdf, 1);
            if (!_scanPage.Enabled) return ("Scan this page is disabled on a PDF", null);
            _scanPage.PerformClick();
            Pump(() => _scan.Loading.IsCompleted, 30);
            if (_scan.Image == null || _scan.Source?.Kind != ScanSourceKind.ModulePdf) return ($"Scan this page did not render the PDF page ({_scan.Pane.Message})", null);
            Program.AppLog($"smoke: scanned PDF page 1 at {ScanPane.ScanDpi} DPI: {_scan.Image.Width}×{_scan.Image.Height}");
            if (_scan.Image.Width < 1000) return ("the rendered page is smaller than 200 DPI would give", null);
            var cached = Directory.GetFiles(ScanCache.Folder, "*.pdf").FirstOrDefault();
            if (cached == null) return ("the module PDF was not copied to the scan cache", null);
            // Page flips reuse the extracted file (one extraction per entry per session).
            var flip = _scan.ShowSource(_scan.Source!, 2);
            Pump(() => flip.IsCompleted, 30);
            var back1 = _scan.ShowSource(_scan.Source!, 1);
            Pump(() => back1.IsCompleted, 30);
            Program.AppLog($"smoke: module entries extracted this session: {_scan.ExtractCount} (after two page flips); page {_scan.Page}");
            if (_scan.ExtractCount != 1 || _scan.Page != 1 || _scan.Image == null) return ("page flips re-extracted the module PDF or did not render", null);
            BackToPdf();
            if (!_viewer.Visible || _scan.Visible) return ("Back to PDF did not show the PDF viewer", null);

            // A PDF opened with Open scan… joins the rulebook search for the session, and its hits open in the scan pane.
            var opened = Path.Combine(AppDataRoot.Sub("smoke-ocr"), "opened-rules.pdf");
            File.Copy(cached, opened, true);
            var o = OpenScanFile(opened);
            Pump(() => o.IsCompleted && _scan.Loading.IsCompleted, 30);
            if (_scan.Source is not { Kind: ScanSourceKind.FilePdf } fp || _scan.Image == null) return ($"the opened PDF was not shown as a scan ({_scan.Pane.Message})", null);
            Pump(() => _indexTask is { IsCompleted: true } && _index?.Documents.Any(d => d.Entry == fp.Key) == true, 60);
            var hit = _index?.Search("supply").FirstOrDefault(h => h.Entry == fp.Key && h.Page == 2);
            Program.AppLog($"smoke: opened PDF indexed: {_index?.Documents.Count} document(s); hit on its page 2: {hit != null}");
            if (hit == null) return ("the opened PDF is not searchable", null);
            OpenHit(hit.Entry, hit.Page);
            Pump(() => _scan.Loading.IsCompleted, 30);
            if (_scan.Source?.Key != fp.Key || _scan.Page != 2 || _scan.Image == null) return ("a search hit in the opened PDF did not show its page in the scan pane", null);
        }
        var back = ShowScan(imageSrc, 1);
        Pump(() => back.IsCompleted && _scan.Loading.IsCompleted, 20);
        if (_scan.Pane.SelectedRegion != region) return ("the image's region was not restored", null);
        _scan.Ocr.ShowGridTab();
        return (null, null);
    }

    // ---------- smoke (screenshot) ----------

    /// <summary>Smoke: shows the named tab (tables, formulas, properties, phases); unknown or empty keeps the Tables tab.</summary>
    public void SelectTabForSmoke(string? name)
    {
        _tabs.SelectedTab = name?.ToLowerInvariant() switch { "formulas" => _formulasPage, "properties" => _propsPage, "phases" => _phasesPage, "alerts" => _alertsPage, "visibility" => _visibilityPage, _ => _tablesPage };
        if (_tabs.SelectedTab == _formulasPage && _formulas.SelectedFormulaId == null) Program.AppLog("smoke: no formula to show");
    }

    public string? SelectedTableForSmoke => _tables.SelectedTableId;

    /// <summary>
    /// Smoke (--smoke-search): queues a search before the index is ready, waits for the index, opens the hit on page 2, uses it as
    /// the first formula's rule ref, and checks "Used on this page" and the rule-ref link. Returns a problem, or null when all is well.
    /// </summary>
    public string? SearchSmoke()
    {
        const string term = "supply";
        RunSearch(term); // queued when the index is still building
        var queued = _pendingQuery != null;
        var until = DateTime.UtcNow.AddSeconds(60);
        while ((_indexTask is { IsCompleted: false } || _index == null) && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(20); }
        Application.DoEvents();
        if (_index == null) return "the rulebook index was not ready after 60 s";
        Program.AppLog($"smoke: rulebook index ready ({_index.Documents.Count} document(s); status '{_statusText.Text}'; query queued {queued})");
        var doc = _index.Documents.FirstOrDefault(d => d.Searchable);
        if (doc == null) return "no searchable rulebook in the module";
        if (_search.Rows.Count == 0) RunSearch(term);
        var hits = _search.Rows.Where(r => r.Kind == SearchRowKind.Hit).ToList();
        Program.AppLog("smoke: search rows: " + string.Join(" | ", _search.Rows.Select(r => r.Kind == SearchRowKind.Hit ? r.Text + " " + string.Concat(r.Runs.Select(x => x.Bold ? "[" + x.Text + "]" : x.Text)) : r.Text)));
        if (hits.Count == 0) return "no hits for '" + term + "'";
        if (!hits.Any(h => h.Runs.Any(x => x.Bold))) return "no hit is marked in its snippet";
        if (!_search.SelectHitForSmoke(2)) return "no hit on page 2";
        _search.ActivateSelectedForSmoke();
        if (_viewer.Document?.EntryName != doc.Entry || _viewer.Page != 2) return $"the hit did not open page 2 (shows {_viewer.Document?.EntryName} p. {_viewer.Page})";
        if (_viewer.SearchTerm != term) return "the viewer was not asked to highlight the term";
        _tabs.SelectedTab = _formulasPage;
        if (_s.Rules?.Formulas.FirstOrDefault() is not { } f0) return "the rules pack has no formula";
        _formulas.SelectFormula(f0.Id);
        if (!_search.UseSelectedForSmoke()) return "Use as rule ref was not available with a formula selected";
        Application.DoEvents();
        var f = _s.Rules!.ById<FormulaDef>(f0.Id);
        var r = f?.RuleRefId == null ? null : _s.Rules.ById<RuleRef>(f.RuleRefId);
        if (r == null || r.DocumentEntry != doc.Entry || r.Page != 2) return "the formula's rule ref does not point at the hit page";
        var used = _viewer.UsedOnPageLines;
        Program.AppLog("smoke: used on this page: " + string.Join(" | ", used));
        if (!used.Contains("Formula: " + f0.Name)) return "Used on this page does not list the formula";
        var shown = SearchPanelModel.DescribeRef(r, _moduleDocs, _index.Documents);
        Program.AppLog($"smoke: formula rule ref reads '{shown.Text}' (link {shown.IsLink})");
        if (!shown.IsLink) return "the rule ref is not a link";
        var missing = SearchPanelModel.DescribeRef(r with { Page = 99 }, _moduleDocs, _index.Documents);
        if (!missing.Missing || missing.IsLink) return "a ref past the last page is not shown as missing";
        return null;
    }

    public void SaveScreenshot(string path)
    {
        SetSplit();
        Update();
        using var bmp = new Bitmap(Math.Max(Width, 1), Math.Max(Height, 1));
        DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Program.AppLog("smoke: workbench screenshot saved to " + path);
    }
}
