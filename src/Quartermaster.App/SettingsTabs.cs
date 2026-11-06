using System.Globalization;
using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>What the settings window needs to know about the running app (kept out of the dialog so smoke and tests can stub it).</summary>
/// <param name="ToolbarItems">Toolbar buttons that can be hidden: id and caption.</param>
/// <param name="CacheRoot">Root of the per-module tile/document caches.</param>
/// <param name="OpenModuleCache">Cache folder of the module that is open now (its files are in use), or null.</param>
/// <param name="Notify">Shows a message to the user (logged instead in smoke mode).</param>
sealed record SettingsContext(IReadOnlyList<(string Id, string Text)> ToolbarItems, string CacheRoot, Func<string?> OpenModuleCache, Action<string> Notify);

/// <summary>One page of the settings window: builds its controls, shows a <see cref="Settings"/> and writes its fields back into one.</summary>
abstract class SettingsTab
{
    public abstract string Title { get; }
    public abstract Control Build();
    public abstract void Load(Settings s);
    public abstract Settings Collect(Settings s);

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
    readonly CheckBox _gm = Check("Game master mode (show hidden pieces and notes)");

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

sealed class DisplayTab : SettingsTab
{
    readonly ComboBox _grid = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    readonly NumericUpDown _label = Num(6, 20);
    readonly Panel _swatch = new() { Width = 40, Height = 20, BorderStyle = BorderStyle.FixedSingle };
    readonly NumericUpDown _tabMin = Num(40, 400), _tabMax = Num(60, 600);
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
    }

    public override Settings Collect(Settings s)
    {
        var min = (int)_tabMin.Value;
        return s with
        {
            GridDefault = (GridMode)Math.Max(0, _grid.SelectedIndex), LabelFontSize = (int)_label.Value, HighlightColor = _color,
            TabMinWidth = min, TabMaxWidth = Math.Max(min, (int)_tabMax.Value),
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

/// <summary>Placeholder: the shortcut editor arrives with the keyboard task. Existing overrides pass through untouched.</summary>
sealed class KeyboardTab : SettingsTab
{
    public override string Title => "Keyboard";

    /// <summary>The page body; the shortcut editor replaces its content.</summary>
    public Panel Host { get; } = new() { Dock = DockStyle.Fill, Padding = new Padding(10) };

    public override Control Build()
    {
        if (Host.Controls.Count == 0) Host.Controls.Add(new Label { Text = "Shortcuts — coming next", AutoSize = true });
        return Host;
    }

    public override void Load(Settings s) { }
    public override Settings Collect(Settings s) => s;
}

sealed class FoldersTab(SettingsContext ctx) : SettingsTab
{
    readonly Label _sizes = new() { AutoSize = true, Text = "Calculating sizes..." };

    public override string Title => "Folders";

    public override Control Build()
    {
        var tiles = new Button { Text = "Clear tile cache", AutoSize = true };
        var docs = new Button { Text = "Clear document cache", AutoSize = true };
        tiles.Click += (_, _) => Clear("tiles");
        docs.Click += (_, _) => Clear("docs");
        var t = Table();
        Full(t, new Label { Text = "Cache folder:", AutoSize = true });
        Full(t, new TextBox { Text = ctx.CacheRoot, ReadOnly = true, Width = 440 });
        Full(t, _sizes);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.AddRange(new Control[] { tiles, docs });
        Full(t, row);
        Full(t, new Label { Text = "The module that is open keeps its cache while it is open.", AutoSize = true });
        Refresh();
        return t;
    }

    void Refresh()
    {
        _ = Task.Run(() => CacheFolders.Sizes(ctx.CacheRoot)).ContinueWith(t =>
        {
            if (t.IsFaulted || _sizes.IsDisposed || !_sizes.IsHandleCreated) return;
            try
            {
                _sizes.BeginInvoke(() =>
                {
                    if (!_sizes.IsDisposed) _sizes.Text = $"Tile cache: {Mb(t.Result.Tiles)}     Document cache: {Mb(t.Result.Docs)}";
                });
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { /* dialog closed meanwhile */ }
        });
    }

    static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) + " MB";

    void Clear(string kind)
    {
        var open = ctx.OpenModuleCache();
        var (freed, skippedOpen) = CacheFolders.Clear(ctx.CacheRoot, kind, open);
        ctx.Notify($"Cleared {Mb(freed)} of cached {(kind == "tiles" ? "tiles" : "documents")}." +
            (skippedOpen ? "\nThe open module's files are in use and were left alone; close the module to clear them." : ""));
        _sizes.Text = "Calculating sizes...";
        Refresh();
    }

    public override void Load(Settings s) { }
    public override Settings Collect(Settings s) => s;
}

/// <summary>Sizes and clearing of the per-module caches (<c>&lt;root&gt;/&lt;module sha&gt;/tiles</c> and <c>/docs</c>).</summary>
static class CacheFolders
{
    public static (long Tiles, long Docs) Sizes(string root)
    {
        long tiles = 0, docs = 0;
        try
        {
            foreach (var m in Directory.EnumerateDirectories(root))
            {
                tiles += Size(Path.Combine(m, "tiles"));
                docs += Size(Path.Combine(m, "docs"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (tiles, docs);
    }

    static long Size(string dir)
    {
        long n = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { n += f.Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return n;
    }

    /// <summary>Deletes <paramref name="kind"/> under every module folder except <paramref name="openModuleDir"/>. Returns bytes freed and whether the open module was skipped.</summary>
    public static (long Freed, bool SkippedOpen) Clear(string root, string kind, string? openModuleDir)
    {
        long freed = 0;
        var skipped = false;
        try
        {
            foreach (var m in Directory.EnumerateDirectories(root))
            {
                var sub = Path.Combine(m, kind);
                if (!Directory.Exists(sub)) continue;
                if (openModuleDir != null && string.Equals(Path.GetFullPath(m).TrimEnd('\\', '/'), Path.GetFullPath(openModuleDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                {
                    skipped = true;
                    continue;
                }
                var size = Size(sub);
                try { Directory.Delete(sub, true); freed += size; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use elsewhere: keep */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (freed, skipped);
    }
}
