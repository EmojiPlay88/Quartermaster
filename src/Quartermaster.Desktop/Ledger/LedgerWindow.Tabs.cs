using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Desktop.Ledgers;

/// <summary>
/// The Ledger's tab strip (Units, Depots &amp; Global, Everything, the player's ★ tabs, then "+"), the Side / Group by / Show counters toolbar and
/// the per-module preferences behind them (<see cref="LedgerTabStore"/>: on this computer only, never in the rules pack, save or turn file).
/// Edit tabs… and "+" open the tab editor (<see cref="EditTabsDialog"/>, through <see cref="IDialogs.EditLedgerTabsAsync"/>).
/// </summary>
public sealed partial class LedgerWindow
{
    const string PlusTab = "+";
    const string PlusTip = "Add a tab of your own (kept on this PC)";
    const string EditTabsTip = "Add, remove or change your own Ledger tabs (kept on this PC)";
    static readonly IBrush BannerBack = new SolidColorBrush(Color.FromRgb(255, 236, 236));
    static readonly IBrush BannerText = new SolidColorBrush(Color.FromRgb(0xB2, 0x22, 0x22));   // Firebrick

    readonly TabStrip _tabStrip = new() { Name = "LedgerTabs", Margin = new Thickness(2, 0, 2, 0) };
    readonly TextBlock _bannerText = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = BannerText };
    Border _banner = null!;   // built in AddTabToolbar
    readonly TextBlock _sideLabel = Label("Side:");
    readonly ComboBox _side = new() { Width = 120, Name = "LedgerSide" };
    readonly TextBlock _groupLabel = Label("Group by:");
    readonly ComboBox _group = new() { Width = 70, Name = "LedgerGroup" };
    readonly ToggleButton _showCounters = Toggle("Show counters", "Show each unit's counter beside its name");
    readonly Button _editTabs = Plain("Edit tabs…", EditTabsTip);
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    readonly List<string?> _sideIds = new();
    static readonly LedgerGrouping[] GroupChoices = { LedgerGrouping.Map, LedgerGrouping.Side, LedgerGrouping.None };
    LedgerPrefs _prefs = LedgerPrefs.Default;
    IReadOnlyList<LedgerTabDef> _tabDefs = LedgerTabs.Tabs(Array.Empty<UserLedgerTab>());
    LedgerTabDef _tab = LedgerTabs.Tabs(Array.Empty<UserLedgerTab>())[0];
    string _sidesSignature = "\u0000";
    bool _syncing;     // true while the code itself sets the strip or the combos
    bool _selfSave;    // true while this window's own Save raises LedgerTabStore.Changed

    string ModuleSha => _s.Archive.Sha256;
    static string PrefsRoot => AppDataRoot.Path;

    string? SideFilter => _tab.Kind is LedgerTabKind.Units or LedgerTabKind.User ? _prefs.Side : null;
    LedgerGrouping Grouping => _tab.Kind is LedgerTabKind.Units or LedgerTabKind.User ? _prefs.Grouping : LedgerGrouping.None;

    // ---------- what tests and Task 5 read ----------

    internal IReadOnlyList<string> TabKeys => _tabDefs.Select(t => t.Key).ToList();
    internal IReadOnlyList<string> TabTitles => _tabStrip.Items.OfType<TabStripItem>().Select(t => t.Content as string ?? "").ToList();
    internal TabStripItem PlusTabItem => _tabStrip.Items.OfType<TabStripItem>().Last();
    internal string? TabTip(int index) => _tabStrip.Items.OfType<TabStripItem>().ElementAtOrDefault(index) is { } t ? ToolTip.GetTip(t) as string : null;
    internal ComboBox SideBox => _side;
    internal ComboBox GroupBox => _group;
    internal ToggleButton CountersButton => _showCounters;
    internal Button EditTabsButton => _editTabs;
    internal bool SideAndGroupShown => _side.IsVisible && _group.IsVisible && _sideLabel.IsVisible && _groupLabel.IsVisible;
    internal bool BannerShown => _banner.IsVisible;
    internal string BannerTextShown => _bannerText.Text ?? "";
    internal LedgerPrefs Prefs => _prefs;
    /// <summary>The toolbar's texts in order (labels, buttons; the filter box and combos as "[...]").</summary>
    internal IReadOnlyList<string> ToolbarTexts => _bar.Children.Select(c => c switch
    {
        TextBlock t => t.Text ?? "",
        ContentControl b => b.Content as string ?? "",
        TextBox => "[filter]",
        ComboBox => "[combo]",
        _ => "|",
    }).ToList();

    /// <summary>Selects a tab by its key, as clicking it (no-op when there is none).</summary>
    public void SelectTab(string key)
    {
        if (_tabDefs.FirstOrDefault(t => t.Key == key) is not { } t) return;
        _syncing = true;
        try { _tabStrip.SelectedIndex = _tabDefs.ToList().IndexOf(t); }
        finally { _syncing = false; }
        SwitchTo(t);
    }

    void AddTabToolbar()
    {
        _banner = new Border { Height = 24, IsVisible = false, Padding = new Thickness(6, 0), Background = BannerBack, Child = _bannerText };
        foreach (var g in GroupChoices) _group.Items.Add(g.ToString());
        ToolTip.SetTip(_side, "Show one side's units");
        ToolTip.SetTip(_group, "Group the units under bold header rows");
        _bar.Children.Add(_sideLabel);
        _bar.Children.Add(_side);
        _bar.Children.Add(_groupLabel);
        _bar.Children.Add(_group);
        _bar.Children.Add(_showCounters);
        _bar.Children.Add(_editTabs);
        _editTabs.Click += (_, _) => RunAsync(() => OpenEditTabsAsync(false));
        _side.SelectionChanged += (_, _) => Run(OnSidePicked);
        _group.SelectionChanged += (_, _) => Run(OnGroupPicked);
        _showCounters.IsCheckedChanged += (_, _) => Run(OnCountersToggled);
        _tabStrip.SelectionChanged += (_, _) => Run(OnTabPicked);
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Run(SavePrefs); };
    }

    /// <summary>Loads this module's preferences and builds the strip, opening the tab used last.</summary>
    void InitTabs()
    {
        _prefs = LedgerTabStore.Load(PrefsRoot, ModuleSha);
        _syncing = true;
        try
        {
            _showCounters.IsChecked = _prefs.ShowCounters;
            _group.SelectedIndex = Math.Max(0, Array.IndexOf(GroupChoices, _prefs.Grouping));
        }
        finally { _syncing = false; }
        RefreshSides();
        RebuildTabStrip(_prefs.LastTab);
    }

    /// <summary>The Side dropdown: All sides, then the rules pack's sides (rows still come only from the player's view).</summary>
    void RefreshSides()
    {
        var sides = _s.Rules?.Sides ?? new List<SideDef>();
        var sig = string.Join("|", sides.Select(s => s.Id + "\u0001" + s.Name));
        if (sig == _sidesSignature) return;
        _sidesSignature = sig;
        _syncing = true;
        try
        {
            _side.Items.Clear();
            _sideIds.Clear();
            _side.Items.Add("All sides");
            _sideIds.Add(null);
            foreach (var s in sides) { _side.Items.Add(s.Name); _sideIds.Add(s.Id); }
            var at = _sideIds.IndexOf(_prefs.Side);
            if (at < 0) { at = 0; _prefs = _prefs with { Side = null }; }
            _side.SelectedIndex = at;
        }
        finally { _syncing = false; }
    }

    void RebuildTabStrip(string? selectKey)
    {
        _tabDefs = LedgerTabs.Tabs(_prefs.Tabs);
        _tab = _tabDefs.FirstOrDefault(t => t.Key == selectKey) ?? _tabDefs[0];
        _syncing = true;
        try
        {
            _tabStrip.Items.Clear();
            foreach (var t in _tabDefs)
            {
                var item = new TabStripItem { Content = LedgerViewRules.TabTitle(t, null), Tag = t };
                if (!string.IsNullOrEmpty(t.User?.Notes)) ToolTip.SetTip(item, t.User!.Notes);
                _tabStrip.Items.Add(item);
            }
            var plus = new TabStripItem { Content = PlusTab };   // opens the tab editor with a fresh tab
            ToolTip.SetTip(plus, PlusTip);
            _tabStrip.Items.Add(plus);
            _tabStrip.SelectedIndex = Math.Max(0, _tabDefs.ToList().FindIndex(t => t.Key == _tab.Key));
        }
        finally { _syncing = false; }
        UpdateTabTools();
    }

    /// <summary>The fixed tabs' labels carry their row counts (with the current filter and side).</summary>
    void UpdateTabCounts(RulesPack? pack, GameSession? game)
    {
        foreach (var page in _tabStrip.Items.OfType<TabStripItem>())
        {
            if (page.Tag is not LedgerTabDef t || t.Kind is not (LedgerTabKind.Units or LedgerTabKind.Holders)) continue;
            int? n = null;
            if (t.Key == _tab.Key) n = _result.Count;
            else if (pack != null && game != null && _s.View is { } view)
                n = LedgerTabs.Rows(t, view, pack, _s.Model, _filter.Text ?? "", t.Kind == LedgerTabKind.Units ? _prefs.Side : null, LedgerGrouping.None, _showEmpty.IsChecked == true).Count;
            var text = LedgerViewRules.TabTitle(t, n);
            if (page.Content as string != text) page.Content = text;
        }
    }

    void ShowBanner(string? filterError)
    {
        var show = filterError != null;
        if (show) _bannerText.Text = LedgerViewRules.FilterBanner(_tab.Title, filterError!);
        if (_banner.IsVisible != show) _banner.IsVisible = show;
    }

    void UpdateTabTools()
    {
        var unitTab = LedgerViewRules.SideAndGroupApply(_tab);
        _sideLabel.IsVisible = _side.IsVisible = _groupLabel.IsVisible = _group.IsVisible = unitTab;
    }

    void OnTabPicked()
    {
        if (_syncing) return;
        if (_tabStrip.SelectedItem is not TabStripItem { Tag: LedgerTabDef t })
        {
            // "+" is a button, not a tab: put the selection back, then open the editor with a new tab.
            _syncing = true;
            try { _tabStrip.SelectedIndex = Math.Max(0, _tabDefs.ToList().FindIndex(d => d.Key == _tab.Key)); }
            finally { _syncing = false; }
            Dispatcher.UIThread.Post(() => { if (!_closing) RunAsync(() => OpenEditTabsAsync(true)); });
            return;
        }
        if (t.Key == _tab.Key) return;
        SwitchTo(t);
    }

    void SwitchTo(LedgerTabDef t)
    {
        _tab = t;
        _sortCol = -1;
        _prefs = _prefs with { LastTab = t.Key };
        UpdateTabTools();
        SavePrefs();
        _list.SelectedItems?.Clear();
        _debounce.Stop();
        Reload();
        if (_items.Count > 0) ScrollToTop(0);
    }

    void OnSidePicked()
    {
        if (_syncing || _side.SelectedIndex < 0 || _side.SelectedIndex >= _sideIds.Count) return;
        _prefs = _prefs with { Side = _sideIds[_side.SelectedIndex] };
        SavePrefs();
        Reload();
    }

    void OnGroupPicked()
    {
        if (_syncing || _group.SelectedIndex < 0) return;
        _prefs = _prefs with { Grouping = GroupChoices[_group.SelectedIndex] };
        SavePrefs();
        Reload();
    }

    void OnCountersToggled()
    {
        if (_syncing) return;
        _prefs = _prefs with { ShowCounters = _showCounters.IsChecked == true };
        SavePrefs();
        Reload();
    }

    // ---------- column widths (per tab key; the Name column is stored without the picture's room) ----------

    void OnColumnWidthChanged()
    {
        if (_syncing || _rebuildingColumns || _widths.Length == 0) return;
        var widths = _widths.Select((w, i) => i == 0 ? Math.Max(20, (int)Math.Round(w) - NameImagePad) : (int)Math.Round(w)).ToList();
        var all = new Dictionary<string, IReadOnlyList<int>>(_prefs.ColumnWidths) { [_tab.Key] = widths };
        _prefs = _prefs with { ColumnWidths = all };
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void ApplyColumnWidths()
    {
        if (!_prefs.ColumnWidths.TryGetValue(_tab.Key, out var widths)) return;
        for (var i = 0; i < Math.Min(widths.Count, _widths.Length); i++)
        {
            var w = widths[i] + (i == 0 ? NameImagePad : 0);
            if (w >= 20 && w <= 2000) _widths[i] = w;
        }
    }

    // ---------- saving and other windows ----------

    void SavePrefs()
    {
        _saveTimer.Stop();
        _selfSave = true;
        try { LedgerTabStore.Save(PrefsRoot, ModuleSha, _prefs); }
        finally { _selfSave = false; }
    }

    /// <summary>Writes a pending column-width change when the window closes.</summary>
    void FlushPrefs()
    {
        if (_saveTimer.IsEnabled) SavePrefs();
    }

    /// <summary>Another Ledger window of this module saved: take its tab list (this window keeps its own tab, side and grouping).</summary>
    void OnStoreChanged(string sha)
    {
        if (_selfSave || !string.Equals(sha, ModuleSha, StringComparison.OrdinalIgnoreCase)) return;
        OnUi(() =>
        {
            var fresh = LedgerTabStore.Load(PrefsRoot, ModuleSha);
            if (LedgerViewRules.SameTabs(fresh.Tabs, _prefs.Tabs)) return;
            _prefs = _prefs with { Tabs = fresh.Tabs };
            RebuildTabStrip(_tab.Key);
            _colSignature = "";
            Reload();
        });
    }

    // ---------- Edit tabs ----------

    bool _editingTabs;   // the tab editor is open (a second click on Edit tabs… or "+" opens nothing)

    /// <summary>Edit tabs… / "+": the tab editor at the current user tab (or with a fresh tab when <paramref name="addNew"/>); OK applies the list.</summary>
    async Task OpenEditTabsAsync(bool addNew)
    {
        if (_editingTabs || _closing) return;
        var userIndex = _tab.Kind == LedgerTabKind.User ? _prefs.Tabs.ToList().FindIndex(t => t.Name == _tab.Title) : 0;
        _editingTabs = true;
        IReadOnlyList<UserLedgerTab>? result;
        try { result = await _dialogs.EditLedgerTabsAsync(this, _prefs.Tabs, _s.Rules, Math.Max(0, userIndex), addNew); }
        finally { _editingTabs = false; }
        if (result == null || _closing) return;
        ApplyEditedTabs(result, addNew);
    }

    /// <summary>Stores an edited tab list, rebuilds the strip and reloads; after "+" the newly added tab is opened.</summary>
    internal void ApplyEditedTabs(IReadOnlyList<UserLedgerTab> tabs, bool openNew)
    {
        var clean = LedgerTabStore.Normalize(tabs);
        var key = LedgerViewRules.TabToOpenAfterEdit(_prefs.Tabs, clean, _tab.Key, openNew);
        _prefs = _prefs with { Tabs = clean };
        RebuildTabStrip(key);
        _prefs = _prefs with { LastTab = _tab.Key };
        SavePrefs();
        _colSignature = "";
        Reload();
    }
}
