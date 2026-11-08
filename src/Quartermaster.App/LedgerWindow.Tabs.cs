using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>
/// The Ledger's tab strip (Units, Depots &amp; Global, Everything, the player's ★ tabs, then "+"), the Side / Group by / Show counters toolbar and
/// the per-module preferences behind them (<see cref="LedgerTabStore"/>: on this PC only, never in the rules pack, save or turn file).
/// </summary>
sealed partial class LedgerWindow
{
    const string PlusTab = "+";
    readonly TabControl _tabStrip = new() { Dock = DockStyle.Top, ShowToolTips = true, Multiline = false };
    readonly Label _banner = new()
    {
        Dock = DockStyle.Top, Height = 24, Visible = false, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0),
        BackColor = Color.FromArgb(255, 236, 236), ForeColor = Color.Firebrick, AutoEllipsis = true,
    };
    readonly ToolStripLabel _sideLabel = new("Side:");
    readonly ToolStripComboBox _side = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 120, ToolTipText = "Show one side's units" };
    readonly ToolStripLabel _groupLabel = new("Group by:");
    readonly ToolStripComboBox _group = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 70, ToolTipText = "Group the units under bold header rows" };
    readonly ToolStripButton _showCounters = new("Show counters") { DisplayStyle = ToolStripItemDisplayStyle.Text, CheckOnClick = true, ToolTipText = "Show each unit's counter beside its name" };
    readonly ToolStripButton _editTabs = new("Edit tabs…") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Add, remove or change your own Ledger tabs (kept on this PC)" };
    readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 700 };
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

    void AddTabToolbar()
    {
        foreach (var g in GroupChoices) _group.Items.Add(g.ToString());
        _bar.Items.Add(_sideLabel);
        _bar.Items.Add(_side);
        _bar.Items.Add(_groupLabel);
        _bar.Items.Add(_group);
        _bar.Items.Add(_showCounters);
        _bar.Items.Add(_editTabs);
        _side.SelectedIndexChanged += (_, _) => Run(OnSidePicked);
        _group.SelectedIndexChanged += (_, _) => Run(OnGroupPicked);
        _showCounters.CheckedChanged += (_, _) => Run(OnCountersToggled);
        _editTabs.Click += (_, _) => Run(() => OpenEditTabs(false));
        _tabStrip.Selecting += (_, e) => Run(() => OnTabSelecting(e));
        _tabStrip.SelectedIndexChanged += (_, _) => Run(OnTabPicked);
        _grid.ColumnWidthChanged += (_, _) => Run(OnColumnWidthChanged);
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Run(SavePrefs); };
    }

    /// <summary>Loads this module's preferences and builds the strip, opening the tab used last.</summary>
    void InitTabs()
    {
        _prefs = LedgerTabStore.Load(PrefsRoot, ModuleSha);
        _syncing = true;
        try
        {
            _showCounters.Checked = _prefs.ShowCounters;
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
            _tabStrip.SuspendLayout();
            _tabStrip.TabPages.Clear();
            foreach (var t in _tabDefs)
                _tabStrip.TabPages.Add(new TabPage(TabTitle(t, null)) { Tag = t, ToolTipText = t.User?.Notes ?? "" });
            _tabStrip.TabPages.Add(new TabPage(PlusTab) { ToolTipText = "Add a tab of your own (kept on this PC)" });
            _tabStrip.SelectedIndex = Math.Max(0, _tabDefs.ToList().FindIndex(t => t.Key == _tab.Key));
            _tabStrip.ResumeLayout();
            _tabStrip.Height = _tabStrip.GetTabRect(0).Bottom + 3;   // header only: the grid below is the page
        }
        finally { _syncing = false; }
        UpdateTabTools();
    }

    static string TabTitle(LedgerTabDef t, int? count) => t.Kind switch
    {
        LedgerTabKind.User => "★ " + t.Title,
        LedgerTabKind.Everything => t.Title,
        _ => count is { } n ? $"{t.Title} ({n:N0})" : t.Title,
    };

    /// <summary>The fixed tabs' labels carry their row counts (with the current filter and side).</summary>
    void UpdateTabCounts(RulesPack? pack, GameSession? game)
    {
        foreach (TabPage page in _tabStrip.TabPages)
        {
            if (page.Tag is not LedgerTabDef t || t.Kind is not (LedgerTabKind.Units or LedgerTabKind.Holders)) continue;
            int? n = null;
            if (t.Key == _tab.Key) n = _result.Count;
            else if (pack != null && game != null && _s.View is { } view)
                n = LedgerTabs.Rows(t, view, pack, _s.Model, _filter.Text, t.Kind == LedgerTabKind.Units ? _prefs.Side : null, LedgerGrouping.None, _showEmpty.Checked).Count;
            var text = TabTitle(t, n);
            if (page.Text != text) page.Text = text;
        }
    }

    void ShowBanner(string? filterError)
    {
        var show = filterError != null;
        if (show) _banner.Text = $"The unit filter of \"{_tab.Title}\" does not parse, so this tab shows no rows: {filterError}   (Edit tabs… to fix it)";
        if (_banner.Visible != show) _banner.Visible = show;
    }

    void UpdateTabTools()
    {
        var unitTab = _tab.Kind is LedgerTabKind.Units or LedgerTabKind.User;
        _sideLabel.Visible = _side.Visible = _groupLabel.Visible = _group.Visible = unitTab;
    }

    void OnTabSelecting(TabControlCancelEventArgs e)
    {
        if (_syncing || e.TabPage == null || e.TabPage.Tag is LedgerTabDef) return;
        e.Cancel = true;   // "+" is a button, not a tab
        BeginInvoke(() => { if (!IsDisposed && !_closing) Run(() => OpenEditTabs(true)); });
    }

    void OnTabPicked()
    {
        if (_syncing || _tabStrip.SelectedTab?.Tag is not LedgerTabDef t || t.Key == _tab.Key) return;
        SwitchTo(t);
    }

    /// <summary>Selects a tab by key (no-op when it does not exist).</summary>
    void SelectTab(string key)
    {
        if (_tabDefs.FirstOrDefault(t => t.Key == key) is not { } t) return;
        _syncing = true;
        try { _tabStrip.SelectedIndex = _tabDefs.ToList().IndexOf(t); }
        finally { _syncing = false; }
        SwitchTo(t);
    }

    void SwitchTo(LedgerTabDef t)
    {
        _tab = t;
        _sortCol = -1;
        _prefs = _prefs with { LastTab = t.Key };
        UpdateTabTools();
        SavePrefs();
        _grid.ClearSelection();
        Reload();
        try { if (_grid.RowCount > 0) _grid.FirstDisplayedScrollingRowIndex = 0; } catch (InvalidOperationException) { }
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
        _prefs = _prefs with { ShowCounters = _showCounters.Checked };
        SavePrefs();
        Reload();
    }

    // ---------- column widths (per tab key; the Name column is stored without the picture's room) ----------

    void OnColumnWidthChanged()
    {
        if (_syncing || _rebuildingColumns || _grid.Columns.Count == 0) return;
        var widths = new List<int>();
        foreach (DataGridViewColumn c in _grid.Columns) widths.Add(c.Index == 0 ? Math.Max(20, c.Width - NameImagePad) : c.Width);
        var all = new Dictionary<string, IReadOnlyList<int>>(_prefs.ColumnWidths) { [_tab.Key] = widths };
        _prefs = _prefs with { ColumnWidths = all };
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void ApplyColumnWidths()
    {
        if (!_prefs.ColumnWidths.TryGetValue(_tab.Key, out var widths)) return;
        for (var i = 0; i < Math.Min(widths.Count, _grid.Columns.Count); i++)
        {
            var w = widths[i] + (i == 0 ? NameImagePad : 0);
            if (w >= 20 && w <= 2000) _grid.Columns[i].Width = w;
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
        if (_saveTimer.Enabled) SavePrefs();
    }

    /// <summary>Another Ledger window of this module saved: take its tab list (this window keeps its own tab, side and grouping).</summary>
    void OnStoreChanged(string sha)
    {
        if (_selfSave || !string.Equals(sha, ModuleSha, StringComparison.OrdinalIgnoreCase)) return;
        OnUi(() => Run(() =>
        {
            var fresh = LedgerTabStore.Load(PrefsRoot, ModuleSha);
            if (SameTabs(fresh.Tabs, _prefs.Tabs)) return;
            _prefs = _prefs with { Tabs = fresh.Tabs };
            RebuildTabStrip(_tab.Key);
            _colSignature = "";
            Reload();
        }));
    }

    static bool SameTabs(IReadOnlyList<UserLedgerTab> a, IReadOnlyList<UserLedgerTab> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Name == p.Second.Name && p.First.Filter == p.Second.Filter && p.First.IncludeHolders == p.Second.IncludeHolders
            && p.First.Notes == p.Second.Notes && p.First.ColumnIds.SequenceEqual(p.Second.ColumnIds));

    // ---------- Edit tabs ----------

    void OpenEditTabs(bool addNew)
    {
        if (Program.Smoke) return;   // no modal dialogs in smoke runs
        var userIndex = _tab.Kind == LedgerTabKind.User ? _prefs.Tabs.ToList().FindIndex(t => t.Name == _tab.Title) : 0;
        using var dlg = new EditTabsDialog(_prefs.Tabs, _s.Rules, Math.Max(0, userIndex), addNew);
        if (dlg.ShowDialog(this) != DialogResult.OK || IsDisposed) return;
        ApplyEditedTabs(dlg.Result, addNew);
    }

    /// <summary>Stores the edited list, rebuilds the strip and reloads; after "+" the newly added tab is opened.</summary>
    internal void ApplyEditedTabs(IReadOnlyList<UserLedgerTab> tabs, bool openNew)
    {
        var before = _prefs.Tabs.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clean = LedgerTabStore.Normalize(tabs);
        var key = _tab.Key;
        if (openNew && clean.FirstOrDefault(t => !before.Contains(t.Name)) is { } added) key = "user:" + added.Name;
        _prefs = _prefs with { Tabs = clean };
        RebuildTabStrip(key);
        _prefs = _prefs with { LastTab = _tab.Key };
        SavePrefs();
        _colSignature = "";
        Reload();
    }

    // ---------- smoke ----------

    public IReadOnlyList<string> TabTitlesForSmoke => _tabStrip.TabPages.Cast<TabPage>().Select(p => p.Text).ToList();
    public string CurrentTabKeyForSmoke => _tab.Key;
    public bool BannerShownForSmoke => _banner.Visible;
    public void SelectTabForSmoke(string key) => SelectTab(key);
}
