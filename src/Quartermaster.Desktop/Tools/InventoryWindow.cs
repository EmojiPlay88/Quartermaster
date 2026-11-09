using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Map;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tools;

/// <summary>
/// Counts placed pieces by name, map, marker or ledger property (WinForms InventoryWindow). Double-click a group to step through its pieces;
/// hover a Total to see the pieces that add up to it. Everything shown comes from <see cref="ModuleSession.View"/>.
/// </summary>
public sealed class InventoryWindow : Window
{
    const double GroupWidth = 220, CountWidth = 70, TotalWidth = 100;

    static readonly (string Label, InventoryKey Key)[] Keys =
    {
        ("Piece name", InventoryKey.Name), ("Map", InventoryKey.Map), ("Marker", InventoryKey.Marker), ("Ledger property", InventoryKey.LedgerProperty),
    };

    readonly ModuleSession _s;
    readonly Action<string> _centre;
    readonly ComboBox _key = new() { Width = 120, Name = "InventoryKey" };
    readonly ComboBox _keyName = new() { Width = 150, Name = "InventoryKeyName" };
    readonly TextBox _filter = new() { Width = 140, Name = "InventoryFilter" };
    readonly ListBox _list = new() { SelectionMode = SelectionMode.Single, Name = "InventoryList" };
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly Dictionary<string, int> _cycle = new(StringComparer.OrdinalIgnoreCase);
    readonly BreakdownTip _tip = new();
    List<InventoryRow> _rows = new();
    GameSession? _game;
    string? _tipGroup;
    bool _closing;
    bool _loadingCombos;

    internal InventoryWindow(ModuleSession s, Action<string> centre)
    {
        _s = s;
        _centre = centre;
        Title = "Inventory";
        Width = 520;
        Height = 440;
        ShowInTaskbar = false;

        foreach (var k in Keys) _key.Items.Add(k.Label);
        _key.SelectedIndex = 0;
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
        bar.Children.Add(Label("Group by:", 0));
        bar.Children.Add(_key);
        bar.Children.Add(_keyName);
        bar.Children.Add(Label("Filter:", 8));
        bar.Children.Add(_filter);
        foreach (var c in new Control[] { _key, _keyName, _filter }) c.Margin = new Thickness(0, 0, 4, 0);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions($"{GroupWidth},{CountWidth},{TotalWidth}") };
        for (var i = 0; i < 3; i++)
        {
            var cell = new Border
            {
                BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(4, 2),
                Child = new TextBlock { Text = new[] { "Group", "Count", "Total" }[i], FontWeight = FontWeight.Bold, HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right },
            };
            cell.Bind(Border.BorderBrushProperty, cell.GetResourceObservable("Classic.BorderBrush"));
            cell.Bind(Border.BackgroundProperty, cell.GetResourceObservable("Classic.FaceBrush"));
            Grid.SetColumn(cell, i);
            header.Children.Add(cell);
        }
        _list.ItemTemplate = new FuncDataTemplate<InventoryRow>((r, _) => r == null ? new Panel() : RowControl(r), supportsRecycling: false);
        _list.DoubleTapped += (_, e) => { e.Handled = true; Run(CycleSelected); };
        _list.PointerExited += (_, _) => HideTip();
        _list.PointerWheelChanged += (_, _) => HideTip();

        var table = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        table.Children.Add(header);
        table.Children.Add(_list);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(table);
        Content = root;

        _key.SelectionChanged += (_, _) => Run(() => { _cycle.Clear(); FillKeyNames(); Reload(); });
        _keyName.SelectionChanged += (_, _) => Run(() => { if (!_loadingCombos) { _cycle.Clear(); Reload(); } });
        _filter.TextChanged += (_, _) => { _cycle.Clear(); Kick(); };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Run(() => { FillKeyNames(); Reload(); }); };
        Deactivated += (_, _) => HideTip();

        s.RulesChanged += OnRulesChanged;
        s.GameReplaced += OnGameReplaced;
        s.ViewChanged += OnViewChanged;
        HookGame();
        Run(() => { FillKeyNames(); Reload(); });
    }

    static TextBlock Label(string text, double left) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 3, 0) };

    Control RowControl(InventoryRow r)
    {
        var showTotal = CurrentKey == InventoryKey.LedgerProperty;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{GroupWidth},{CountWidth},{TotalWidth}") };
        grid.Children.Add(Cell(r.Group, 0, HorizontalAlignment.Left));
        grid.Children.Add(Cell(r.Count.ToString(CultureInfo.InvariantCulture), 1, HorizontalAlignment.Right));
        var total = Cell(showTotal && r.Total is { } t ? LedgerTable.FormatNumber(t) : "", 2, HorizontalAlignment.Right);
        total.Background = Brushes.Transparent;
        total.PointerEntered += (_, e) => Run(() => ShowTip(r.Group, e.GetPosition(this)));
        total.PointerExited += (_, _) => HideTip();
        grid.Children.Add(total);
        return grid;
    }

    static Border Cell(string text, int col, HorizontalAlignment align)
    {
        var b = new Border { Padding = new Thickness(4, 1), Child = new TextBlock { Text = text, HorizontalAlignment = align, TextTrimming = TextTrimming.CharacterEllipsis } };
        Grid.SetColumn(b, col);
        return b;
    }

    InventoryKey CurrentKey => _key.SelectedIndex >= 0 ? Keys[_key.SelectedIndex].Key : InventoryKey.Name;

    void Kick() { _debounce.Stop(); _debounce.Start(); }

    void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("inventory window failed: " + ex);
            _s.Log.Add(DiagnosticSeverity.Error, "inventory", "The inventory window failed: " + ex.Message);
        }
    }

    void OnUi(Action a) => Dispatcher.UIThread.Post(() => { if (!_closing) a(); });

    void HookGame()
    {
        if (_game != null) { _game.Changed -= OnGameChanged; _game.Rewound -= OnRewound; }
        _game = _s.Game;
        if (_game != null) { _game.Changed += OnGameChanged; _game.Rewound += OnRewound; }
    }

    void OnGameChanged(GameCommand _) => OnUi(Kick);
    void OnRewound() => OnUi(Kick);
    void OnGameReplaced() => OnUi(() => { HookGame(); _cycle.Clear(); Kick(); });
    void OnRulesChanged() => OnUi(Kick);
    void OnViewChanged() => OnUi(Kick);

    protected override void OnClosed(EventArgs e)
    {
        Unhook();
        base.OnClosed(e);
    }

    void Unhook()
    {
        _closing = true;
        _debounce.Stop();
        HideTip();
        _s.RulesChanged -= OnRulesChanged;
        _s.GameReplaced -= OnGameReplaced;
        _s.ViewChanged -= OnViewChanged;
        if (_game != null) { _game.Changed -= OnGameChanged; _game.Rewound -= OnRewound; }
        _game = null;
    }

    // ---------- data ----------

    /// <summary>Repopulates the key-name combo for the current key, keeping the selection when it is still offered.</summary>
    void FillKeyNames()
    {
        var names = InventoryRules.KeyNames(_s.Rules, CurrentKey, _s.Model, _s.View);
        var keep = _keyName.SelectedItem as string;
        _loadingCombos = true;
        try
        {
            _keyName.Items.Clear();
            foreach (var n in names) _keyName.Items.Add(n);
            var at = keep == null ? -1 : names.ToList().IndexOf(keep);
            _keyName.SelectedIndex = at >= 0 ? at : names.Count > 0 ? 0 : -1;
            _keyName.IsEnabled = names.Count > 0;
        }
        finally { _loadingCombos = false; }
    }

    void Reload()
    {
        _rows = _s.View is not { } view ? new() : InventoryQuery.Run(view, _s.Model, _s.Rules, CurrentKey, _keyName.SelectedItem as string, _filter.Text ?? "").ToList();
        var sel = _list.SelectedItem is InventoryRow cur ? cur.Group : null;
        HideTip();
        _list.ItemsSource = _rows;
        if (sel != null) _list.SelectedItem = _rows.FirstOrDefault(r => string.Equals(r.Group, sel, StringComparison.OrdinalIgnoreCase));
    }

    void ShowTip(string group, Point at)
    {
        if (group == _tipGroup) return;
        HideTip();
        _tipGroup = group;
        if (CurrentKey != InventoryKey.LedgerProperty || _s.View is not { } view || _s.Rules is not { } pack) return;
        var row = _rows.FirstOrDefault(r => string.Equals(r.Group, group, StringComparison.OrdinalIgnoreCase));
        if (row == null || InventoryRules.TotalBreakdown(row, _keyName.SelectedItem as string ?? "", view, pack, _s.ViewFiltered) is not { } tip) return;
        _tip.Show(this, new Point(at.X + 12, at.Y + 18), tip);
    }

    void HideTip()
    {
        _tip.Hide();
        _tipGroup = null;
    }

    void CycleSelected()
    {
        if (_list.SelectedItem is not InventoryRow r || r.PieceIds.Count == 0) return;
        var next = _cycle.TryGetValue(r.Group, out var at) ? (at + 1) % r.PieceIds.Count : 0;
        _cycle[r.Group] = next;
        _centre(r.PieceIds[next]);
    }

    // ---------- what tests read and drive ----------

    internal IReadOnlyList<InventoryRow> Rows => _rows;
    internal string? TipText => _tip.Text;
    internal bool DebouncePending => _debounce.IsEnabled;
    internal ComboBox KeyBox => _key;
    internal ComboBox KeyNameBox => _keyName;
    internal TextBox FilterBox => _filter;
    internal ListBox List => _list;

    /// <summary>Groups the pieces by <paramref name="key"/> (the first combo), as choosing it would.</summary>
    internal void SelectKey(InventoryKey key) => _key.SelectedIndex = Array.FindIndex(Keys, k => k.Key == key);

    /// <summary>Reloads now, skipping the 300 ms pause.</summary>
    internal void Flush()
    {
        _debounce.Stop();
        Run(() => { FillKeyNames(); Reload(); });
    }

    /// <summary>What a double-click on the selected group does.</summary>
    internal void ActivateSelected() => Run(CycleSelected);

    /// <summary>Hovers the Total cell of a group.</summary>
    internal void HoverTotal(string group) => Run(() => ShowTip(group, new Point(20, 60)));

    internal void Select(int index) => _list.SelectedIndex = index;
}
