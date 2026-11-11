using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Desktop.Input;
using Quartermaster.Render.Search;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Workbench;

/// <summary>Rulebook search of the workbench (port of WinForms WorkbenchWindow 405-505): the background index, the results panel
/// under the viewer, hits opening the viewer with highlights, "Use as rule ref" and the editors' "Find in rulebook".</summary>
public sealed partial class WorkbenchWindow
{
    readonly SearchPanel _search = new();
    RulebookSearchSession? _session;
    RulebookIndex? _readyIndex;          // the index the editors and the pending query last saw
    string? _pendingQuery;
    string _lastQuery = "";
    double _searchHeight = SearchPanel.ExpandedHeight;

    /// <summary>Called once from the constructor, after the tabs and the viewer exist.</summary>
    void WireSearch()
    {
        foreach (var e in Editors)
        {
            e.FindInRulebook = q => { FocusSearchBox(select: false); RunSearch(q); };
            e.RefreshRuleRef();
        }

        _search.Collapsed = true;
        _search.Title = IdleTitle;
        Grid.SetRow(_search, 2);
        LeftPane.Children.Add(_search);
        _search.Height = SearchPanel.HeaderHeight;
        SearchSplitter.IsEnabled = false;
        _search.CollapsedChanged += () =>
        {
            SearchSplitter.IsEnabled = !_search.Collapsed;
            UpdateSearchSplitter();
            FitSearch();
        };
        SearchSplitter.DragCompleted += (_, _) => OnSearchSplitterDragged();
        LeftPane.GetObservable(Visual.BoundsProperty).Subscribe(new BoundsObserver(FitSearch));

        _search.HitActivated += (entry, page) => SearchGuard(() => OpenHit(entry, page));
        _search.UseAsRuleRef += (entry, page) => SearchGuard(() => UseHitAsRuleRef(entry, page));
        _search.NotSearchableActivated += entry => SearchGuard(() => OpenNotSearchable(entry));
        _search.ScanFirstPage += entry => SearchGuard(() => ScanFirstPage(entry));
        _search.CanUseRuleRef = () => !_s.IsGuest && _s.Rules != null && ActiveEditor?.CanUseRuleRef == true;

        SearchBox.IsEnabled = true;
        SearchButton.Click += (_, _) => SearchGuard(() => RunSearch(SearchBox.Text));
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            SearchGuard(() => RunSearch(SearchBox.Text));
        };
        // Primary+Shift+F (Cmd on macOS): tunnelled, so the focused text box does not swallow it.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (ShortcutGestures.Parse("Ctrl+Shift+F") is { } g && g.Matches(e))
            {
                e.Handled = true;
                FocusSearchBox(select: true);
            }
        }, RoutingStrategies.Tunnel);

        var pdfs = _s.Model.Documents.Where(d => d.Kind == DocumentKind.Pdf).GroupBy(d => d.EntryName).Select(g => g.First())
            .Select(d => (d.EntryName, d.Title, (Func<Stream>)(() => _s.Archive.OpenEntry(d.EntryName)))).ToList();
        _session = new RulebookSearchSession(_s.Archive.Sha256, pdfs);
        _session.Changed += OnSessionChanged;
        SessionChangedUi();
    }

    static string IdleTitle => $"Rulebook search: type in the box above ({ShortcutGestures.Display("Ctrl+Shift+F")})";

    /// <summary>Rebuilds the rulebook index (a PDF was opened as a scan: opened PDFs join the search for this session).</summary>
    void RestartIndex()
    {
        if (_closed || _session == null) return;
        _session.Rebuild(OpenedScanPdfs().ToList());
    }

    /// <summary>The window closed: running index builds are cancelled.</summary>
    void OnSearchClosed()
    {
        var s = _session;
        _session = null;
        if (s == null) return;
        s.Changed -= OnSessionChanged;
        s.Dispose();
    }

    /// <summary>How the editors show a rule ref.</summary>
    Func<RuleRef?, RefDisplay>? Describe => _describe ??= r => SearchPanelModel.DescribeRef(r, RefDocs, _session?.Index?.Documents);
    Func<RuleRef?, RefDisplay>? _describe;

    /// <summary>Documents a rule ref can point at: the module's documents, then its images and the scans opened this session.</summary>
    IReadOnlyList<(string Entry, string Title)> RefDocs =>
        _s.Model.Documents.Select(d => (d.EntryName, d.Title)).Concat(ExtraRefDocs()).ToList();

    // ---------- the index ----------

    void OnSessionChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) SessionChangedUi();
        else Dispatcher.UIThread.Post(SessionChangedUi);
    }

    void SessionChangedUi()
    {
        if (_closed || _session is not { } session) return;
        SearchGuard(() =>
        {
            SetStatus(session.Status);
            var ix = session.Index;
            SearchButton.IsEnabled = ix != null;
            if (ix == null || ReferenceEquals(ix, _readyIndex)) return;
            _readyIndex = ix;
            foreach (var e in Editors) e.RefreshRuleRef();
            if (_pendingQuery is { } q) RunSearch(q);
        });
    }

    // ---------- searching ----------

    /// <summary>Searches the rulebooks and shows the results; before the index is ready the query waits and runs when it is.</summary>
    public void RunSearch(string? query)
    {
        query = (query ?? "").Trim();
        if (SearchBox.Text != query) SearchBox.Text = query;
        if (query.Length == 0 || _session == null) return;
        var result = _session.Run(query);
        _search.Title = result.Title;
        if (result.Pending)
        {
            _pendingQuery = query;
            return;
        }
        _pendingQuery = null;
        _lastQuery = query;
        _search.ShowRows(result.Rows ?? Array.Empty<SearchRow>());
    }

    void FocusSearchBox(bool select)
    {
        if (!_main.IsVisible) return;
        SearchBox.Focus();
        if (select) SearchBox.SelectAll();
    }

    void OpenHit(string entry, int page)
    {
        var doc = _s.Model.Documents.FirstOrDefault(d => d.EntryName == entry);
        if (doc == null)
        {
            // A PDF opened with "Open scan…": shown as a rendered page in the scan pane.
            if (TryShowOpenedScan(entry, page)) return;
            _ = Warn($"'{Path.GetFileName(entry)}' is not in this module.");
            return;
        }
        BeforeShowDocument();
        Viewer.Open(doc, page, SearchPanelModel.ViewerSearchTerm(_lastQuery));
    }

    /// <summary>A "not searchable" row was opened: a module PDF opens on page 1 with a hint to scan it; an opened PDF shows page 1 as a scan.</summary>
    void OpenNotSearchable(string entry)
    {
        if (_s.Model.Documents.FirstOrDefault(d => d.EntryName == entry) is { } doc)
        {
            BeforeShowDocument();
            Viewer.Open(doc, 1);
            SetStatus(SearchPanelModel.NoTextStatus);
            return;
        }
        if (TryShowOpenedScan(entry, 1)) return;
        _ = Warn($"'{Path.GetFileName(entry)}' is not in this module.");
    }

    void UseHitAsRuleRef(string entry, int page)
    {
        if (_s.IsGuest || ActiveEditor is not { CanUseRuleRef: true } editor)
        {
            _ = Warn("Select a formula, table or phase first, then use the result as its rule ref.");
            return;
        }
        // A hit in a PDF opened with Open scan… is keyed by its full path: the ref stores scan:{file name}, never the path.
        var (refEntry, refPage) = _s.Model.Documents.Any(d => d.EntryName == entry) ? (entry, page) : ScanModel.RefForSearchHit(entry, page);
        editor.UseRuleRef(label => RefFor(label, refEntry, refPage));
        RefreshUsed();
    }

    void SearchGuard(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("workbench search: " + ex);
            _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Rulebook search failed: " + ex.Message, "");
        }
    }

    // ---------- the results panel's height ----------

    /// <summary>The expanded results panel keeps its height (or the one the user dragged) but never takes more than half the left pane.</summary>
    void FitSearch()
    {
        if (_search.Collapsed)
        {
            _search.Height = SearchPanel.HeaderHeight;
            return;
        }
        var max = Math.Max(SearchPanel.HeaderHeight * 3, LeftPane.Bounds.Height / 2);
        _search.Height = Math.Min(Math.Max(_searchHeight, SearchPanel.HeaderHeight * 3), max);
    }

    /// <summary>The splitter turned the (auto) row into a fixed size: that is the height the user wants; the row goes back to auto.</summary>
    internal void OnSearchSplitterDragged()
    {
        var row = LeftPane.RowDefinitions[2];
        if (!_search.Collapsed && row.Height.IsAbsolute) _searchHeight = row.Height.Value;
        row.Height = GridLength.Auto;
        FitSearch();
    }

    internal SearchPanel SearchPanelForTests => _search;
    internal string SearchStatusForTests => _session?.Status ?? "";
    internal RulebookIndex? SearchIndexForTests => _session?.Index;

    sealed class BoundsObserver(Action fit) : IObserver<Rect>
    {
        public void OnNext(Rect value) => fit();
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
