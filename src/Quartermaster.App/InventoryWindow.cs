using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>MDI child counting placed pieces by name, map, marker or ledger property. Double-click a group to step through its pieces.</summary>
sealed class InventoryWindow : Form
{
    static readonly (string Label, InventoryKey Key)[] Keys =
    {
        ("Piece name", InventoryKey.Name), ("Map", InventoryKey.Map), ("Marker", InventoryKey.Marker), ("Ledger property", InventoryKey.LedgerProperty),
    };

    readonly ModuleSession _s;
    readonly Action<string> _centre;
    readonly ComboBox _key = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    readonly ComboBox _keyName = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    readonly TextBox _filter = new() { Width = 140 };
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false };
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };
    readonly Dictionary<string, int> _cycle = new(StringComparer.OrdinalIgnoreCase);
    List<InventoryRow> _rows = new();
    GameSession? _game;
    bool _closing;
    bool _loadingCombos;

    public InventoryWindow(ModuleSession s, Action<string> centre)
    {
        _s = s;
        _centre = centre;
        Text = "Inventory";
        Size = new Size(520, 440);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(4) };
        bar.Controls.Add(new Label { Text = "Group by:", AutoSize = true, Margin = new Padding(0, 6, 3, 0) });
        bar.Controls.Add(_key);
        bar.Controls.Add(_keyName);
        bar.Controls.Add(new Label { Text = "Filter:", AutoSize = true, Margin = new Padding(8, 6, 3, 0) });
        bar.Controls.Add(_filter);
        foreach (var k in Keys) _key.Items.Add(k.Label);
        _key.SelectedIndex = 0;

        _list.Columns.Add("Group", 220);
        _list.Columns.Add("Count", 70, HorizontalAlignment.Right);
        _list.Columns.Add("Total", 100, HorizontalAlignment.Right);
        Controls.Add(_list);
        Controls.Add(bar);

        _key.SelectedIndexChanged += (_, _) => Run(() => { _cycle.Clear(); FillKeyNames(); Reload(); });
        _keyName.SelectedIndexChanged += (_, _) => Run(() => { if (!_loadingCombos) { _cycle.Clear(); Reload(); } });
        _filter.TextChanged += (_, _) => { _cycle.Clear(); Kick(); };
        _list.DoubleClick += (_, _) => Run(CycleSelected);
        _debounce.Tick += (_, _) => { _debounce.Stop(); Run(() => { FillKeyNames(); Reload(); }); };

        s.RulesChanged += OnRulesChanged;
        s.GameReplaced += OnGameReplaced;
        HookGame();
        Run(() => { FillKeyNames(); Reload(); });
    }

    InventoryKey CurrentKey => _key.SelectedIndex >= 0 ? Keys[_key.SelectedIndex].Key : InventoryKey.Name;

    void Kick() { _debounce.Stop(); _debounce.Start(); }

    void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("inventory window failed: " + ex);
            _s.Log.Add(DiagnosticSeverity.Error, "inventory", "The inventory window failed: " + ex.Message);
        }
    }

    void OnUi(Action a)
    {
        if (IsDisposed || _closing || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(() => { if (!IsDisposed && !_closing) a(); }); else a();
    }

    void HookGame()
    {
        if (_game != null) _game.Changed -= OnGameChanged;
        _game = _s.Game;
        if (_game != null) _game.Changed += OnGameChanged;
    }

    void OnGameChanged(GameCommand _) => OnUi(Kick);
    void OnGameReplaced() => OnUi(() => { HookGame(); _cycle.Clear(); Kick(); });
    void OnRulesChanged() => OnUi(Kick);

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        Unhook();
    }

    void Unhook()
    {
        _closing = true;
        _debounce.Stop();
        _s.RulesChanged -= OnRulesChanged;
        _s.GameReplaced -= OnGameReplaced;
        if (_game != null) _game.Changed -= OnGameChanged;
        _game = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Unhook(); _debounce.Dispose(); }
        base.Dispose(disposing);
    }

    // ---------- data ----------

    /// <summary>Repopulates the key-name combo for the current key, keeping the selection when it is still offered.</summary>
    void FillKeyNames()
    {
        IReadOnlyList<string> names = CurrentKey switch
        {
            InventoryKey.Marker => _s.Game is { } g ? InventoryQuery.MarkerKeys(g.State) : Array.Empty<string>(),
            InventoryKey.LedgerProperty => _s.Rules?.Properties.Where(p => p.Scope is PropertyScope.Pieces or PropertyScope.Both).Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>(),
            _ => Array.Empty<string>(),
        };
        var keep = _keyName.SelectedItem as string;
        _loadingCombos = true;
        try
        {
            _keyName.BeginUpdate();
            _keyName.Items.Clear();
            foreach (var n in names) _keyName.Items.Add(n);
            _keyName.EndUpdate();
            var at = keep == null ? -1 : _keyName.Items.IndexOf(keep);
            _keyName.SelectedIndex = at >= 0 ? at : _keyName.Items.Count > 0 ? 0 : -1;
            _keyName.Enabled = names.Count > 0;
        }
        finally { _loadingCombos = false; }
    }

    void Reload()
    {
        if (IsDisposed) return;
        _rows = _s.Game == null ? new() : InventoryQuery.Run(_s.Game.State, _s.Model, _s.Rules, CurrentKey, _keyName.SelectedItem as string, _filter.Text).ToList();
        var showTotal = CurrentKey == InventoryKey.LedgerProperty;
        var sel = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Text : null;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in _rows)
        {
            var it = new ListViewItem(r.Group);
            it.SubItems.Add(r.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            it.SubItems.Add(showTotal && r.Total is { } t ? LedgerTable.FormatNumber(t) : "");
            _list.Items.Add(it);
            if (sel != null && string.Equals(sel, r.Group, StringComparison.OrdinalIgnoreCase)) it.Selected = true;
        }
        _list.EndUpdate();
    }

    void CycleSelected()
    {
        if (_list.SelectedIndices.Count == 0) return;
        var i = _list.SelectedIndices[0];
        if (i < 0 || i >= _rows.Count || _rows[i].PieceIds.Count == 0) return;
        var r = _rows[i];
        var next = _cycle.TryGetValue(r.Group, out var at) ? (at + 1) % r.PieceIds.Count : 0;
        _cycle[r.Group] = next;
        _centre(r.PieceIds[next]);
    }

    // ---------- smoke ----------

    public int RowCountForSmoke => _rows.Count;

    public void SaveScreenshot(string path)
    {
        Update();
        using var bmp = new Bitmap(Math.Max(Width, 1), Math.Max(Height, 1));
        DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Program.AppLog("smoke: inventory screenshot saved to " + path);
    }
}
