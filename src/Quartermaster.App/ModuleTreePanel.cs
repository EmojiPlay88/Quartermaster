using Quartermaster.Render;
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

    ModuleModel? _model;
    Func<string, bool>? _canSeeMap;
    string _hiddenSig = "";

    public void Clear()
    {
        _tree.Nodes.Clear();
        _model = null;
        _canSeeMap = null;
        _hiddenSig = "";
    }

    /// <summary>Fills the tree. <paramref name="canSeeMap"/> (hot-seat) leaves out maps of sides the player does not hold.</summary>
    public void Load(ModuleModel model, Func<string, bool>? canSeeMap = null)
    {
        _model = model;
        _canSeeMap = canSeeMap;
        _hiddenSig = HiddenSignature();
        Rebuild();
    }

    /// <summary>Rebuilds the tree only when the set of hidden maps changed (a new viewer or a rules edit).</summary>
    public void RefreshVisibility()
    {
        if (_model == null) return;
        var sig = HiddenSignature();
        if (sig == _hiddenSig) return;
        _hiddenSig = sig;
        Rebuild();
    }

    string HiddenSignature() => _model == null ? "" : ModuleTreeModel.VisibilitySignature(_model, _canSeeMap);

    void Rebuild()
    {
        if (_model == null) return;
        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();
            var root = ModuleTreeModel.Build(_model, _canSeeMap).Select(i => Add(_tree.Nodes, i)).FirstOrDefault();
            root?.Expand();
        }
        finally { _tree.EndUpdate(); }
    }

    static TreeNode Add(TreeNodeCollection into, TreeItem item)
    {
        var tn = into.Add(item.Text);
        tn.Tag = item.Tag;
        tn.ToolTipText = item.Tooltip ?? "";
        foreach (var c in item.Children) Add(tn.Nodes, c);
        return tn;
    }
}
