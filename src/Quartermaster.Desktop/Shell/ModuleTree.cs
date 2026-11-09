using Avalonia.Controls;
using Avalonia.Input;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// Docked tree over the module's window tree (WinForms ModuleTreePanel, built from <see cref="ModuleTreeModel"/>). Double-click or Enter
/// raises <see cref="NodeActivated"/> for non-folder nodes. Maps the viewer may not see are left out; the tree rebuilds only when that
/// set changes (<see cref="RefreshVisibility"/>).
/// </summary>
public sealed class ModuleTree : UserControl
{
    readonly TreeView _tree = new() { Name = "ModuleTreeView" };
    ModuleModel? _model;
    Func<string, bool>? _canSeeMap;
    string _signature = "";
    IReadOnlyList<TreeItem> _items = Array.Empty<TreeItem>();

    public ModuleTree()
    {
        Name = "ModuleTree";
        Content = _tree;
        _tree.DoubleTapped += (_, e) =>
        {
            if (TagOf(_tree.SelectedItem) is { } n) { e.Handled = true; Activate(n); }
        };
        _tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && TagOf(_tree.SelectedItem) is { } n) { e.Handled = true; Activate(n); }
        };
    }

    /// <summary>Raised for a double-clicked or Enter-ed node that is not a folder.</summary>
    public event Action<WindowTreeNode>? NodeActivated;

    public TreeView Tree => _tree;

    /// <summary>Every node shown, depth first (tests and the smoke check).</summary>
    public IEnumerable<TreeItem> Nodes => _items.SelectMany(Flatten);

    /// <summary>Number of folders directly under the module root.</summary>
    public int TopLevelFolderCount => _items.Count > 0 ? _items[0].Children.Count : 0;

    static IEnumerable<TreeItem> Flatten(TreeItem i) => new[] { i }.Concat(i.Children.SelectMany(Flatten));

    static WindowTreeNode? TagOf(object? item) => (item as TreeViewItem)?.Tag is TreeItem { Tag: WindowTreeNode n } ? n : null;

    /// <summary>Activates a node like a double-click does (folders do nothing).</summary>
    public void Activate(WindowTreeNode n)
    {
        if (n.Kind != WindowNodeKind.Folder) NodeActivated?.Invoke(n);
    }

    public void Clear()
    {
        _tree.ItemsSource = null;
        _items = Array.Empty<TreeItem>();
        _model = null;
        _canSeeMap = null;
        _signature = "";
    }

    /// <summary>Fills the tree. <paramref name="canSeeMap"/> leaves out maps of sides the viewer does not hold.</summary>
    public void Load(ModuleModel model, Func<string, bool>? canSeeMap = null)
    {
        _model = model;
        _canSeeMap = canSeeMap;
        _signature = Signature();
        Rebuild();
    }

    /// <summary>Rebuilds the tree only when the set of hidden maps changed (a new viewer or a rules edit).</summary>
    public void RefreshVisibility()
    {
        if (_model == null) return;
        var sig = Signature();
        if (sig == _signature) return;
        _signature = sig;
        Rebuild();
    }

    string Signature() => _model == null ? "" : ModuleTreeModel.VisibilitySignature(_model, _canSeeMap);

    void Rebuild()
    {
        if (_model == null) return;
        _items = ModuleTreeModel.Build(_model, _canSeeMap);
        var nodes = _items.Select(Make).ToList();
        _tree.ItemsSource = nodes;
        if (nodes.Count > 0) nodes[0].IsExpanded = true;
    }

    static TreeViewItem Make(TreeItem item)
    {
        var tvi = new TreeViewItem { Header = item.Text, Tag = item, ItemsSource = item.Children.Select(Make).ToList() };
        if (!string.IsNullOrEmpty(item.Tooltip)) ToolTip.SetTip(tvi, item.Tooltip);
        return tvi;
    }
}
