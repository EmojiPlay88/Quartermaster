using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>Docked tree over <see cref="ModuleModel.Tree"/>. Double-click or Enter raises <see cref="NodeActivated"/> for non-folder nodes.</summary>
sealed class ModuleTreePanel : Panel
{
    readonly TreeView _tree = new()
    {
        Dock = DockStyle.Fill,
        HideSelection = false,
        ShowNodeToolTips = true,
    };

    public event Action<WindowTreeNode>? NodeActivated;

    public ModuleTreePanel()
    {
        Controls.Add(_tree);
        _tree.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Node?.Tag is WindowTreeNode n) Activate(n);
        };
        _tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && _tree.SelectedNode?.Tag is WindowTreeNode n)
            {
                e.Handled = e.SuppressKeyPress = true;
                Activate(n);
            }
        };
    }

    public int TopLevelFolderCount => _tree.Nodes.Count > 0 ? _tree.Nodes[0].Nodes.Count : 0;

    void Activate(WindowTreeNode n)
    {
        if (n.Kind != WindowNodeKind.Folder) NodeActivated?.Invoke(n);
    }

    public void Clear() => _tree.Nodes.Clear();

    public void Load(ModuleModel model)
    {
        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();
            var root = Add(_tree.Nodes, model.Tree);
            root.Expand();
        }
        finally { _tree.EndUpdate(); }
    }

    static TreeNode Add(TreeNodeCollection into, WindowTreeNode n)
    {
        var tn = into.Add(n.Title);
        tn.Tag = n;
        tn.ToolTipText = n.Kind.ToString();
        foreach (var c in n.Children) Add(tn.Nodes, c);
        return tn;
    }
}
