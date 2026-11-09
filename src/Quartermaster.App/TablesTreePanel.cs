using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>
/// Docked tree over the loaded rules pack: root folders "Tables" and "Formulas", items sorted by name.
/// Refreshes (debounced) when the pack changes; double-click or Enter raises the activation events.
/// </summary>
sealed class TablesTreePanel : Panel
{
    const string TablesKey = "tables", FormulasKey = "formulas";

    readonly TreeView _tree = new()
    {
        Dock = DockStyle.Fill,
        HideSelection = false,
        ShowNodeToolTips = true,
    };
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 200 };
    ModuleSession? _session;
    bool _tablesOpen = true, _formulasOpen = true;

    public event Action<string>? TableActivated;
    public event Action<string>? FormulaActivated;

    public TablesTreePanel()
    {
        Controls.Add(_tree);
        _debounce.Tick += (_, _) => { _debounce.Stop(); Guarded(Refresh_); };
        _tree.NodeMouseDoubleClick += (_, e) => Guarded(() => Activate(e.Node));
        _tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || _tree.SelectedNode is not { } n) return;
            e.Handled = e.SuppressKeyPress = true;
            Guarded(() => Activate(n));
        };
        _tree.AfterExpand += (_, e) => Remember(e.Node, true);
        _tree.AfterCollapse += (_, e) => Remember(e.Node, false);
        Refresh_();
    }

    /// <summary>Rules-pack table and formula counts currently shown (for the smoke check).</summary>
    public (int Tables, int Formulas) CountsForSmoke
        => (FindRoot(TablesKey)?.Nodes.Count ?? 0, FindRoot(FormulasKey)?.Nodes.Count ?? 0);

    /// <summary>Attaches to a session (or detaches with null) and shows its rules pack.</summary>
    public void Bind(ModuleSession? session)
    {
        if (_session != null) _session.RulesChanged -= OnRulesChanged;
        _debounce.Stop();
        _session = session;
        if (_session != null) _session.RulesChanged += OnRulesChanged;
        Refresh_();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_session != null) _session.RulesChanged -= OnRulesChanged;
            _session = null;
            _debounce.Dispose();
        }
        base.Dispose(disposing);
    }

    void OnRulesChanged()
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired) BeginInvoke(new Action(() => { if (!IsDisposed) Restart(); }));
            else Restart();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { /* closing */ }
    }

    void Restart() { _debounce.Stop(); _debounce.Start(); }

    void Guarded(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("tables tree: " + ex);
        }
    }

    void Activate(TreeNode? n)
    {
        if (Item(n) is not { } it) return;
        if (it.Kind == TablesKey) TableActivated?.Invoke(it.Id);
        else if (it.Kind == FormulasKey) FormulaActivated?.Invoke(it.Id);
    }

    static (string Kind, string Id)? Item(TreeNode? n) => n?.Tag is ValueTuple<string, string> v ? (v.Item1, v.Item2) : null;

    void Remember(TreeNode? n, bool open)
    {
        if (n?.Tag is not string k) return;
        if (k == TablesKey) _tablesOpen = open; else if (k == FormulasKey) _formulasOpen = open;
    }

    TreeNode? FindRoot(string key) => _tree.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Tag is string k && k == key);

    void Refresh_()
    {
        var selected = Item(_tree.SelectedNode);
        var pack = _session?.Rules;
        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();
            if (pack == null)
            {
                _tree.Nodes.Add("(no rules pack)").ForeColor = SystemColors.GrayText;
                return;
            }
            TreeNode? tables = null, formulas = null;
            foreach (var root in TablesTreeModel.Build(pack))
            {
                var key = root.Tag as string;
                var rn = _tree.Nodes.Add(root.Text);
                rn.Tag = key;
                if (key == TablesKey) tables = rn; else formulas = rn;
                foreach (var c in root.Children)
                {
                    var n = rn.Nodes.Add(c.Text);
                    n.Tag = c.Tag switch { TableRef t => (TablesKey, t.Id), FormulaRef f => (FormulasKey, f.Id), _ => null };
                    if (c.Tooltip != null) n.ToolTipText = c.Tooltip;
                }
            }
            if (_tablesOpen) tables?.Expand();
            if (_formulasOpen) formulas?.Expand();
            if (selected is { } sel)
            {
                var again = FindRoot(sel.Kind)?.Nodes.Cast<TreeNode>().FirstOrDefault(n => Item(n)?.Id == sel.Id);
                if (again != null) _tree.SelectedNode = again;
            }
        }
        finally { _tree.EndUpdate(); }
    }
}
