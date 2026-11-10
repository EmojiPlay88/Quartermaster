using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Quartermaster.Render.Search;

namespace Quartermaster.Desktop.Workbench;

/// <summary>
/// Rulebook search results, under the PDF pane of the workbench (port of WinForms SearchPanel): one heading per document, then its
/// matching pages with the hits in bold; documents that cannot be searched are listed greyed. Double-click or Enter opens the page; the
/// context menu's "Use as rule ref" hands the page to the selected editor item, "Scan page 1" (not-searchable rows) reads page 1 with OCR.
/// Rows come from <see cref="SearchPanelModel"/>.
/// </summary>
public sealed class SearchPanel : UserControl
{
    public const double ExpandedHeight = 190;
    public const double HeaderHeight = 24;
    const double PageColumn = 44;
    const int HitLines = 2;

    readonly TextBlock _title = new() { Text = "Rulebook search", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 2, 0) };
    readonly Button _toggle = new() { Content = "▾", Width = 24, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center };
    readonly ListBox _list = new() { SelectionMode = SelectionMode.Single };
    readonly ContextMenu _menu = new();
    readonly MenuItem _useRef = new() { Header = "Use as rule ref" };
    readonly MenuItem _scanFirst = new() { Header = "Scan page 1" };
    IReadOnlyList<SearchRow> _rows = Array.Empty<SearchRow>();
    bool _collapsed;

    /// <summary>A hit row was opened (double-click or Enter): document entry and page.</summary>
    public event Action<string, int>? HitActivated;

    /// <summary>"Use as rule ref" chosen on a hit row: document entry and page.</summary>
    public event Action<string, int>? UseAsRuleRef;

    /// <summary>A "not searchable" row was opened (double-click or Enter): document entry.</summary>
    public event Action<string>? NotSearchableActivated;

    /// <summary>"Scan page 1" chosen on a "not searchable" row: document entry.</summary>
    public event Action<string>? ScanFirstPage;

    /// <summary>Collapsed or expanded by the arrow button; the host resizes the panel.</summary>
    public event Action? CollapsedChanged;

    /// <summary>Asked when the context menu opens: is there an editor item that can take a rule ref?</summary>
    public Func<bool>? CanUseRuleRef { get; set; }

    public SearchPanel()
    {
        var head = new DockPanel();
        DockPanel.SetDock(_toggle, Dock.Right);
        head.Children.Add(_toggle);
        head.Children.Add(_title);
        var headBox = new Border { Height = HeaderHeight, BorderThickness = new Thickness(0, 0, 0, 1), Child = head };
        headBox.Bind(Border.BackgroundProperty, headBox.GetResourceObservable("Classic.FaceBrush"));
        headBox.Bind(Border.BorderBrushProperty, headBox.GetResourceObservable("Classic.BorderBrush"));
        _title.Bind(TextBlock.ForegroundProperty, _title.GetResourceObservable("Classic.TextBrush"));

        _list.ItemTemplate = new FuncDataTemplate<SearchRow>((r, _) => r == null ? new Panel() : RowView(r), supportsRecycling: false);
        _list.Bind(BackgroundProperty, _list.GetResourceObservable("Classic.WindowBrush"));

        _menu.Items.Add(_useRef);
        _menu.Items.Add(_scanFirst);
        _list.ContextMenu = _menu;
        _menu.Opening += (_, e) => { if (!PrepareMenu()) e.Cancel = true; };

        var dock = new DockPanel();
        DockPanel.SetDock(headBox, Dock.Top);
        dock.Children.Add(headBox);
        dock.Children.Add(_list);
        Content = dock;
        Height = ExpandedHeight;

        _toggle.Click += (_, _) => Collapsed = !Collapsed;
        _list.DoubleTapped += (_, _) => Activate(_list.SelectedIndex);
        _list.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Activate(_list.SelectedIndex);
            e.Handled = true;
        };
        _list.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(_list).Properties.IsRightButtonPressed) return;
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } item)
                _list.SelectedIndex = _list.IndexFromContainer(item);
        }, RoutingStrategies.Tunnel);
        _useRef.Click += (_, _) => { if (SelectedHit is { } h) UseAsRuleRef?.Invoke(h.Entry, h.Page); };
        _scanFirst.Click += (_, _) => { if (SelectedNotSearchable is { } r) ScanFirstPage?.Invoke(r.Entry); };
    }

    /// <summary>Collapsed to the header line, or showing the results.</summary>
    public bool Collapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value) return;
            _collapsed = value;
            _list.IsVisible = !value;
            _toggle.Content = value ? "▸" : "▾";
            CollapsedChanged?.Invoke();
        }
    }

    /// <summary>The header's title line (status, the query and its page count).</summary>
    public string Title
    {
        get => _title.Text ?? "";
        set => _title.Text = value;
    }

    public IReadOnlyList<SearchRow> Rows => _rows;

    public void ShowRows(IReadOnlyList<SearchRow> rows)
    {
        _rows = rows ?? Array.Empty<SearchRow>();
        _list.ItemsSource = _rows.ToList();
        var first = _rows.ToList().FindIndex(r => r.Kind == SearchRowKind.Hit);
        _list.SelectedIndex = first;
        if (first >= 0) _list.ScrollIntoView(first);
        if (Collapsed && _rows.Count > 0) Collapsed = false;
    }

    SearchRow? SelectedHit => RowAt(_list.SelectedIndex) is { Kind: SearchRowKind.Hit } r ? r : null;

    SearchRow? SelectedNotSearchable => RowAt(_list.SelectedIndex) is { Kind: SearchRowKind.NotSearchable } r ? r : null;

    SearchRow? RowAt(int i) => i >= 0 && i < _rows.Count ? _rows[i] : null;

    void Activate(int index)
    {
        if (RowAt(index) is not { } row || !SearchPanelModel.Opens(row)) return;
        if (row.Kind == SearchRowKind.NotSearchable) NotSearchableActivated?.Invoke(row.Entry);
        else HitActivated?.Invoke(row.Entry, row.Page);
    }

    /// <summary>Sets the menu items for the selected row; false when the menu has nothing to offer (it then does not open).</summary>
    bool PrepareMenu()
    {
        var hit = SelectedHit;
        var noText = SelectedNotSearchable;
        _useRef.IsVisible = hit != null;
        _useRef.IsEnabled = hit != null && (CanUseRuleRef?.Invoke() ?? false);
        _scanFirst.IsVisible = noText != null;
        return hit != null || noText != null;
    }

    // ---------- rows ----------

    Control RowView(SearchRow row)
    {
        switch (row.Kind)
        {
            case SearchRowKind.Hit:
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions($"{PageColumn},*"), Margin = new Thickness(4, 2) };
                var page = new TextBlock { Text = row.Text };
                var snippet = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxLines = HitLines, TextTrimming = TextTrimming.CharacterEllipsis };
                foreach (var run in row.Runs)
                    snippet.Inlines!.Add(new Run(run.Text) { FontWeight = run.Bold ? FontWeight.Bold : FontWeight.Normal });
                Grid.SetColumn(snippet, 1);
                g.Children.Add(page);
                g.Children.Add(snippet);
                return g;
            }
            case SearchRowKind.Document:
            {
                var b = new Border { Margin = new Thickness(-4, -2), Padding = new Thickness(8, 2), Child = new TextBlock { Text = row.Text, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis } };
                b.Bind(Border.BackgroundProperty, b.GetResourceObservable("Classic.FaceBrush"));
                return b;
            }
            default:
            {
                var t = new TextBlock { Text = row.Text, Margin = new Thickness(4, 2), TextTrimming = TextTrimming.CharacterEllipsis };
                t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("Classic.GrayTextBrush"));
                return t;
            }
        }
    }

    // ---------- test access ----------

    internal ListBox List => _list;
    internal Button ToggleButton => _toggle;
    internal MenuItem UseRefItem => _useRef;
    internal MenuItem ScanFirstItem => _scanFirst;
    internal bool PrepareMenuForTests() => PrepareMenu();

    /// <summary>Selects the first hit row on the page (any page when 0), in <paramref name="entry"/> when given; false when there is none.</summary>
    internal bool SelectHit(int page, string? entry = null)
    {
        var i = _rows.ToList().FindIndex(r => r.Kind == SearchRowKind.Hit && (page == 0 || r.Page == page) && (entry == null || r.Entry == entry));
        if (i < 0) return false;
        _list.SelectedIndex = i;
        return true;
    }

    /// <summary>Opens the selected row as Enter does.</summary>
    internal void ActivateSelected() => Activate(_list.SelectedIndex);
}
