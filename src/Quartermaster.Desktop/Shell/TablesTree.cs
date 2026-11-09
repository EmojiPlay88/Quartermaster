using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// Docked tree over the loaded rules pack (WinForms TablesTreePanel, built from <see cref="TablesTreeModel"/>): root folders "Tables" and
/// "Formulas", items sorted by name. Refreshes (debounced 200 ms) when the pack changes. Activating an item raises the events; the main
/// window ignores them until the table and formula windows exist.
/// </summary>
public sealed class TablesTree : UserControl
{
    const string TablesKey = TablesTreeModel.TablesKey, FormulasKey = TablesTreeModel.FormulasKey;

    readonly TreeView _tree = new() { Name = "TablesTreeView" };
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(200) };
    ModuleSession? _session;
    bool _tablesOpen = true, _formulasOpen = true;
    IReadOnlyList<TreeItem> _items = Array.Empty<TreeItem>();

    public TablesTree()
    {
        Name = "TablesTree";
        Content = _tree;
        _debounce.Tick += (_, _) => { _debounce.Stop(); Guarded(Refresh); };
        _tree.DoubleTapped += (_, e) => { e.Handled = true; Guarded(() => Activate(_tree.SelectedItem as TreeViewItem)); };
        _tree.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Guarded(() => Activate(_tree.SelectedItem as TreeViewItem));
        };
        Refresh();
    }

    public event Action<string>? TableActivated;
    public event Action<string>? FormulaActivated;

    public TreeView Tree => _tree;

    /// <summary>The roots shown now, with their items as children.</summary>
    public IReadOnlyList<TreeItem> Roots => _items;

    /// <summary>Rules-pack table and formula counts currently shown.</summary>
    public (int Tables, int Formulas) Counts
        => (_items.FirstOrDefault(i => Equals(i.Tag, TablesKey))?.Children.Count ?? 0,
            _items.FirstOrDefault(i => Equals(i.Tag, FormulasKey))?.Children.Count ?? 0);

    /// <summary>Attaches to a session (or detaches with null) and shows its rules pack.</summary>
    public void Bind(ModuleSession? session)
    {
        if (_session != null) _session.RulesChanged -= OnRulesChanged;
        _debounce.Stop();
        _session = session;
        if (_session != null) _session.RulesChanged += OnRulesChanged;
        Refresh();
    }

    /// <summary>Raised on any thread; the refresh is debounced onto the UI thread.</summary>
    void OnRulesChanged() => Dispatcher.UIThread.Post(() => { _debounce.Stop(); _debounce.Start(); });

    static void Guarded(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("tables tree: " + ex); }
    }

    void Activate(TreeViewItem? n)
    {
        if (n?.Tag is not TreeItem it) return;
        switch (it.Tag)
        {
            case TableRef t: TableActivated?.Invoke(t.Id); break;
            case FormulaRef f: FormulaActivated?.Invoke(f.Id); break;
        }
    }

    /// <summary>Rebuilds the tree now (the debounce calls this).</summary>
    public void Refresh()
    {
        var selected = (_tree.SelectedItem as TreeViewItem)?.Tag is TreeItem s ? IdOf(s) : null;
        var pack = _session?.Rules;
        _items = TablesTreeModel.Build(pack);
        var roots = new List<TreeViewItem>();
        TreeViewItem? again = null;
        foreach (var root in _items)
        {
            var rootItem = root;
            var tvi = Make(root);
            if (pack == null) tvi.Foreground = Brushes.Gray;   // "(no rules pack)"
            if (Equals(root.Tag, TablesKey)) tvi.IsExpanded = _tablesOpen;
            else if (Equals(root.Tag, FormulasKey)) tvi.IsExpanded = _formulasOpen;
            tvi.PropertyChanged += (_, e) =>
            {
                if (e.Property == TreeViewItem.IsExpandedProperty) Remember(rootItem, tvi.IsExpanded);
            };
            if (selected != null && tvi.ItemsSource is IEnumerable<TreeViewItem> kids)
                again ??= kids.FirstOrDefault(k => k.Tag is TreeItem ti && IdOf(ti) == selected);
            roots.Add(tvi);
        }
        _tree.ItemsSource = roots;
        if (again != null) _tree.SelectedItem = again;
    }

    static string? IdOf(TreeItem i) => i.Tag switch { TableRef t => "t:" + t.Id, FormulaRef f => "f:" + f.Id, _ => null };

    void Remember(TreeItem root, bool open)
    {
        if (Equals(root.Tag, TablesKey)) _tablesOpen = open;
        else if (Equals(root.Tag, FormulasKey)) _formulasOpen = open;
    }

    static TreeViewItem Make(TreeItem item)
    {
        var tvi = new TreeViewItem { Header = item.Text, Tag = item, ItemsSource = item.Children.Select(Make).ToList() };
        if (!string.IsNullOrEmpty(item.Tooltip)) ToolTip.SetTip(tvi, item.Tooltip);
        return tvi;
    }
}
