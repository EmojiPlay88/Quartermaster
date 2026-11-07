using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>What the settings window needs to know about the running app (kept out of the dialog so smoke and tests can stub it).</summary>
/// <param name="ToolbarItems">Toolbar buttons that can be hidden: id and caption.</param>
/// <param name="CacheRoot">Root of the per-module tile/document caches.</param>
/// <param name="OpenModuleCache">Cache folder of the module that is open now (its files are in use), or null.</param>
/// <param name="Notify">Shows a message to the user (logged instead in smoke mode).</param>
/// <param name="Commands">Commands whose shortcuts the Keyboard page edits.</param>
/// <param name="GameDifficulty">Difficulty of the open game, or null when no game is open.</param>
/// <param name="SetGameDifficulty">Switches the open game's difficulty (a logged command; takes effect from the next run).</param>
sealed record SettingsContext(IReadOnlyList<(string Id, string Text)> ToolbarItems, string CacheRoot, Func<string?> OpenModuleCache, Action<string> Notify, CommandRegistry? Commands = null,
    Func<Difficulty?>? GameDifficulty = null, Action<Difficulty>? SetGameDifficulty = null);

/// <summary>One page of the settings window: builds its controls, shows a <see cref="Settings"/> and writes its fields back into one.</summary>
abstract class SettingsTab
{
    public abstract string Title { get; }
    public abstract Control Build();
    public abstract void Load(Settings s);
    public abstract Settings Collect(Settings s);

    /// <summary>A reason the page cannot be applied as it stands, or null.</summary>
    public virtual string? Validate() => null;

    protected static TableLayoutPanel Table()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10), AutoScroll = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    protected static void Row(TableLayoutPanel t, string label, Control c)
    {
        var row = t.RowCount++;
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 6) }, 0, row);
        c.Margin = new Padding(3, 3, 3, 3);
        c.Anchor = AnchorStyles.Left;
        t.Controls.Add(c, 1, row);
    }

    protected static void Full(TableLayoutPanel t, Control c)
    {
        var row = t.RowCount++;
        c.Margin = new Padding(3, 4, 3, 4);
        t.Controls.Add(c, 0, row);
        t.SetColumnSpan(c, 2);
    }

    protected static CheckBox Check(string text) => new() { Text = text, AutoSize = true };

    protected static NumericUpDown Num(int min, int max) => new() { Minimum = min, Maximum = max, Width = 70, ThousandsSeparator = false };

    protected static void Set(NumericUpDown n, decimal v) => n.Value = Math.Clamp(v, n.Minimum, n.Maximum);
}

sealed class GeneralTab : SettingsTab
{
    readonly CheckBox _reopen = Check("Reopen the last module when Quartermaster starts");
    readonly NumericUpDown _autosave = Num(0, 240);
    readonly CheckBox _confirmDelete = Check("Ask before deleting pieces");
    readonly CheckBox _gm = Check("Game master mode (shows the counter palette)");

    public override string Title => "General";

    public override Control Build()
    {
        var t = Table();
        Full(t, _reopen);
        Row(t, "Autosave every (minutes, 0 = off):", _autosave);
        Full(t, _confirmDelete);
        Full(t, _gm);
        return t;
    }

    public override void Load(Settings s)
    {
        _reopen.Checked = s.ReopenLastModule;
        Set(_autosave, s.AutosaveMinutes);
        _confirmDelete.Checked = s.ConfirmDelete;
        _gm.Checked = s.GameMasterMode;
    }

    public override Settings Collect(Settings s) => s with
    {
        ReopenLastModule = _reopen.Checked, AutosaveMinutes = (int)_autosave.Value, ConfirmDelete = _confirmDelete.Checked, GameMasterMode = _gm.Checked,
    };
}

/// <summary>Settings -> Game: the difficulty of the open game (switchable mid-game) and the one new games start with.</summary>
sealed class GameTab(SettingsContext ctx) : SettingsTab
{
    static readonly (Difficulty D, string Name)[] Modes =
    {
        (Difficulty.Hardcore, "Hardcore"), (Difficulty.SemiAuto, "Semi-automatic"), (Difficulty.FullyAuto, "Fully automatic"),
    };
    readonly ComboBox _difficulty = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    readonly Label _hint = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 6) };

    public override string Title => "Game";

    public override Control Build()
    {
        foreach (var m in Modes) _difficulty.Items.Add(m.Name);
        var t = Table();
        Row(t, "Difficulty:", _difficulty);
        Full(t, _hint);
        return t;
    }

    public override void Load(Settings s)
    {
        var open = ctx.GameDifficulty?.Invoke();
        Select(open ?? s.LastDifficulty);
        _difficulty.Enabled = open != null;
        _hint.Text = open != null ? "A change takes effect from the next calculation." : "Open a game first";
    }

    void Select(Difficulty d) => _difficulty.SelectedIndex = Math.Max(0, Array.FindIndex(Modes, m => m.D == d));

    Difficulty Chosen => _difficulty.SelectedIndex >= 0 ? Modes[_difficulty.SelectedIndex].D : Difficulty.SemiAuto;

    public override Settings Collect(Settings s) => _difficulty.Enabled ? s with { LastDifficulty = Chosen } : s;

    /// <summary>Called after the settings were applied: switches the open game to the chosen difficulty.</summary>
    public void AfterApply()
    {
        if (_difficulty.Enabled && ctx.GameDifficulty?.Invoke() != null) ctx.SetGameDifficulty?.Invoke(Chosen);
    }
}

sealed class DisplayTab : SettingsTab
{
    readonly ComboBox _grid = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    readonly NumericUpDown _label = Num(6, 20);
    readonly Panel _swatch = new() { Width = 40, Height = 20, BorderStyle = BorderStyle.FixedSingle };
    readonly NumericUpDown _tabMin = Num(40, 400), _tabMax = Num(60, 600);
    readonly CheckBox _alertBar = Check("Show alert bar");
    string _color = "#FFD700";

    public override string Title => "Display";

    public override Control Build()
    {
        _grid.Items.AddRange(new object[] { "Module default", "Always show", "Never show" });
        var pick = new Button { Text = "Choose...", AutoSize = true };
        pick.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { Color = _swatch.BackColor, FullOpen = true };
            if (dlg.ShowDialog() == DialogResult.OK) SetColor($"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}");
        };
        var colour = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        colour.Controls.AddRange(new Control[] { _swatch, pick });
        var t = Table();
        Row(t, "Grid:", _grid);
        Row(t, "Piece label size (8 = as the module says):", _label);
        Row(t, "Selection highlight colour:", colour);
        Row(t, "Document tab minimum width:", _tabMin);
        Row(t, "Document tab maximum width:", _tabMax);
        Full(t, _alertBar);
        return t;
    }

    void SetColor(string hex)
    {
        _color = hex;
        if (SettingsStore.TryParseColor(hex, out var c)) _swatch.BackColor = Color.FromArgb(c.R, c.G, c.B);
    }

    public override void Load(Settings s)
    {
        _grid.SelectedIndex = (int)s.GridDefault;
        Set(_label, s.LabelFontSize);
        SetColor(s.HighlightColor);
        Set(_tabMin, s.TabMinWidth);
        Set(_tabMax, s.TabMaxWidth);
        _alertBar.Checked = s.AlertBarVisible;
    }

    public override Settings Collect(Settings s)
    {
        var min = (int)_tabMin.Value;
        return s with
        {
            GridDefault = (GridMode)Math.Max(0, _grid.SelectedIndex), LabelFontSize = (int)_label.Value, HighlightColor = _color,
            TabMinWidth = min, TabMaxWidth = Math.Max(min, (int)_tabMax.Value), AlertBarVisible = _alertBar.Checked,
        };
    }
}

sealed class MapTab : SettingsTab
{
    readonly NumericUpDown _zoom = new() { Minimum = 1.05m, Maximum = 2m, DecimalPlaces = 2, Increment = 0.05m, Width = 70 };
    readonly CheckBox _snap = Check("Snap pieces to the grid when dropped");
    readonly NumericUpDown _drag = Num(0, 50), _hover = Num(0, 5000);

    public override string Title => "Map";

    public override Control Build()
    {
        _hover.Increment = 50;
        var t = Table();
        Row(t, "Mouse wheel zoom step per notch (x):", _zoom);
        Full(t, _snap);
        Row(t, "Drag starts after (pixels):", _drag);
        Row(t, "Stack popup delay (ms):", _hover);
        return t;
    }

    public override void Load(Settings s)
    {
        Set(_zoom, (decimal)s.WheelZoomStep);
        _snap.Checked = s.SnapOnDrop;
        Set(_drag, s.DragThresholdPx);
        Set(_hover, s.HoverDelayMs);
    }

    public override Settings Collect(Settings s) => s with
    {
        WheelZoomStep = (double)_zoom.Value, SnapOnDrop = _snap.Checked, DragThresholdPx = (int)_drag.Value, HoverDelayMs = (int)_hover.Value,
    };
}

sealed class DiceLogTab : SettingsTab
{
    readonly CheckBox _physical = Check("Physical dice: ask me for each die instead of rolling");
    readonly CheckBox _lastRoll = Check("Show the last roll in the status bar");
    readonly CheckBox _moves = Check("Show move, place and delete rows in the log panel");
    readonly TextBox _format = new() { Width = 120 };
    readonly Label _preview = new() { AutoSize = true };

    public override string Title => "Dice & Log";

    public override Control Build()
    {
        _format.TextChanged += (_, _) => UpdatePreview();
        var t = Table();
        Full(t, _physical);
        Full(t, _lastRoll);
        Full(t, _moves);
        Row(t, "Log time format:", _format);
        Row(t, "Preview:", _preview);
        return t;
    }

    void UpdatePreview() =>
        _preview.Text = SettingsStore.IsValidTimeFormat(_format.Text) ? DateTime.Now.ToString(_format.Text, CultureInfo.InvariantCulture) : "(not a valid time format)";

    public override void Load(Settings s)
    {
        _physical.Checked = s.PhysicalDiceDefault;
        _lastRoll.Checked = s.ShowLastRollInStatus;
        _moves.Checked = s.LogShowMoves;
        _format.Text = s.LogTimeFormat;
        UpdatePreview();
    }

    public override Settings Collect(Settings s) => s with
    {
        PhysicalDiceDefault = _physical.Checked, ShowLastRollInStatus = _lastRoll.Checked, LogShowMoves = _moves.Checked,
        LogTimeFormat = SettingsStore.IsValidTimeFormat(_format.Text) ? _format.Text : s.LogTimeFormat,
    };
}

sealed class ToolbarTab(IReadOnlyList<(string Id, string Text)> items) : SettingsTab
{
    readonly CheckedListBox _list = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };

    public override string Title => "Toolbar";

    public override Control Build()
    {
        foreach (var (_, text) in items) _list.Items.Add(text);
        var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        p.Controls.Add(_list);
        p.Controls.Add(new Label { Text = "Tick the buttons to show in the toolbar:", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 6) });
        return p;
    }

    public override void Load(Settings s)
    {
        for (var i = 0; i < items.Count; i++) _list.SetItemChecked(i, s.IsToolbarVisible(items[i].Id));
    }

    public override Settings Collect(Settings s)
    {
        var map = new Dictionary<string, bool>(s.ToolbarVisible);
        for (var i = 0; i < items.Count; i++) map[items[i].Id] = _list.GetItemChecked(i);
        return s with { ToolbarVisible = map };
    }
}


sealed class FoldersTab(SettingsContext ctx) : SettingsTab
{
    readonly Label _sizes = new() { AutoSize = true, Text = "Calculating sizes..." };
    readonly Button _tiles = new() { Text = "Clear tile cache", AutoSize = true };
    readonly Button _docs = new() { Text = "Clear document cache", AutoSize = true };
    bool _started;

    public override string Title => "Folders";

    /// <summary>True once the sizes have been computed and shown (smoke waits for it).</summary>
    public bool Loaded { get; private set; }

    public override Control Build()
    {
        _tiles.Click += (_, _) => Clear("tiles");
        _docs.Click += (_, _) => Clear("docs");
        var t = Table();
        Full(t, new Label { Text = "Cache folder:", AutoSize = true });
        Full(t, new TextBox { Text = ctx.CacheRoot, ReadOnly = true, Width = 440 });
        Full(t, _sizes);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.AddRange(new Control[] { _tiles, _docs });
        Full(t, row);
        Full(t, new Label { Text = "The module that is open keeps its cache while it is open.", AutoSize = true });
        // No handle exists while the dialog is built; start when the page is first shown.
        _sizes.VisibleChanged += (_, _) => { if (_sizes.Visible && !_started) Refresh(); };
        _sizes.HandleCreated += (_, _) => { if (_sizes.Visible && !_started) Refresh(); };
        return t;
    }

    /// <summary>Runs work on a worker thread and posts done back to the UI thread (skipped when the dialog is gone).</summary>
    void OffUi<T>(Func<T> work, Action<T> done)
    {
        var sync = SynchronizationContext.Current;
        _ = Task.Run(work).ContinueWith(r =>
        {
            void Back(object? _)
            {
                if (_sizes.IsDisposed) return;
                if (r.IsFaulted)
                {
                    _tiles.Enabled = _docs.Enabled = true;
                    _sizes.Text = "The folder could not be read: " + r.Exception?.GetBaseException().Message;
                    return;
                }
                done(r.Result);
            }
            if (sync != null) sync.Post(Back, null); else Back(null);
        });
    }

    void Refresh()
    {
        _started = true;
        _sizes.Text = "Calculating sizes...";
        OffUi(() => CacheFolders.Sizes(ctx.CacheRoot), r =>
        {
            _sizes.Text = $"Tile cache: {Mb(r.Tiles)}     Document cache: {Mb(r.Docs)}";
            Loaded = true;
        });
    }

    static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) + " MB";

    void Clear(string kind)
    {
        var open = ctx.OpenModuleCache();
        _tiles.Enabled = _docs.Enabled = false;
        _sizes.Text = "Clearing...";
        OffUi(() => CacheFolders.Clear(ctx.CacheRoot, kind, open), r =>
        {
            _tiles.Enabled = _docs.Enabled = true;
            ctx.Notify($"Cleared {Mb(r.Freed)} of cached {(kind == "tiles" ? "tiles" : "documents")}." +
                (r.SkippedOpen ? "\nThe open module's files are in use and were left alone; close the module to clear them." : ""));
            Refresh();
        });
    }

    public override void Load(Settings s) { }
    public override Settings Collect(Settings s) => s;
}
