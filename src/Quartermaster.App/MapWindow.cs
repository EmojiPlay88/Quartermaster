using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>MDI child showing one map (or organisation display): canvas, tile preparation, menus and game commands.</summary>
sealed class MapWindow : Form
{
    readonly ModuleSession _s;
    readonly CancellationTokenSource _cts = new();
    GameSession? _attached;
    bool _tilingStarted;

    public MapWindow(ModuleSession s, MapDef map)
    {
        _s = s;
        Map = map;
        Text = map.Name;
        ShowIcon = false;
        Canvas = new MapCanvas(s, map) { Dock = DockStyle.Fill, Preparing = map.Boards.Count > 0 ? 0 : null };
        Controls.Add(Canvas);
        Controls.Add(BuildToolbar());
        Canvas.Guarded = Run;
        Canvas.Actions = () => Host?.Actions;

        Canvas.ContextRequested += OnContext;
        Canvas.DropMessage += m => Host?.SetStatus(m);
        Canvas.StatusChanged += (loc, zoom) =>
        {
            if (Host is { } mf && mf.IsActiveDocument(this)) { mf.SetLocationText(loc); mf.SetZoomText(zoom); }
        };
        s.GameReplaced += OnGameReplaced;
        s.ViewChanged += OnViewChanged;
        Attach();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public MapDef Map { get; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public MapCanvas Canvas { get; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool TilingDone { get; private set; }
    public event Action? TilingCompleted;
    /// <summary>The main window; set before Show. Unlike MdiParent it stays valid while the window is floating.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public MainForm? Host { get; set; }

    // ---------- game wiring ----------

    void Attach()
    {
        if (_attached != null) _attached.Changed -= OnGameChanged;
        if (_attached != null) _attached.Rewound -= OnRewound;
        _attached = _s.Game;
        if (_attached != null) _attached.Changed += OnGameChanged;
        if (_attached != null) _attached.Rewound += OnRewound;
    }

    void OnGameReplaced()
    {
        Attach();
        OnUi(() => { Canvas.PruneSelection(); Canvas.RefreshLastMoved(); });
    }

    void OnGameChanged(GameCommand _) => OnUi(() => { Canvas.PruneSelection(); Canvas.RefreshLastMoved(); });
    void OnRewound() => OnUi(() => { Canvas.PruneSelection(); Canvas.RefreshLastMoved(); });

    IReadOnlySet<string>? _lastSides;
    bool _viewQueued;

    /// <summary>
    /// Hot-seat only: a new viewer or a rules edit can hide what is selected or hovered. Coalesced into one UI-thread refresh. In solo
    /// (no view sides now or before) it does nothing, so rules edits never prune the selection or cancel a drag.
    /// </summary>
    void OnViewChanged()
    {
        var sides = _s.ViewSides;
        if (sides == null && _lastSides == null) return;
        _lastSides = sides;
        if (_viewQueued || IsDisposed || !IsHandleCreated) return;
        _viewQueued = true;
        try
        {
            BeginInvoke(() =>
            {
                _viewQueued = false;
                if (IsDisposed) return;
                Run(() => { Canvas.PruneSelection(); Canvas.RefreshLastMoved(); });
            });
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { _viewQueued = false; }
    }

    void OnUi(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(a); else a();
    }

    // ---------- lifecycle ----------

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        StartOnce();
    }

    // Shown does not fire if the window is moved between MDI and desktop (hidden/re-shown) before its first Shown event,
    // so the first time the window becomes visible by any route also starts it.
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && IsHandleCreated) StartOnce();
    }

    void StartOnce()
    {
        if (_tilingStarted) return;
        _tilingStarted = true;
        if (Host is { } mf)
        {
            mf.GridVisibleChanged += OnGridVisibleChanged;
            Canvas.ShowGrid = mf.GridVisible;
            mf.SettingsApplied += OnSettingsApplied;
            Canvas.ApplySettings(mf.CurrentSettings);
        }
        _ = PrepareTilesAsync();
    }

    ToolStrip BuildToolbar()
    {
        var bar = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
        var los = new ToolStripButton("Line of sight") { CheckOnClick = true, ToolTipText = "Drag to measure range between two locations (Esc or right-click cancels)" };
        los.Click += (_, _) => Canvas.LosEnabled = los.Checked;
        Canvas.LosChanged += () => los.Checked = Canvas.LosEnabled;
        bar.Items.Add(los);
        var last = new ToolStripButton("Last moved") { CheckOnClick = true, ToolTipText = "Outline the counters of the most recent move on this map" };
        last.Click += (_, _) => Canvas.HighlightLastMoved = last.Checked;
        bar.Items.Add(last);
        var hide = new ToolStripButton("Hide counters") { CheckOnClick = true, ToolTipText = "Show only the board (H)" };
        hide.Click += (_, _) => Canvas.HideCounters = hide.Checked;
        Canvas.HideCountersChanged += v => hide.Checked = v;
        bar.Items.Add(hide);
        bar.Items.Add(new ToolStripButton("Centre on selected", null, (_, _) => Run(Canvas.CentreOnSelection)) { ToolTipText = "Scroll to the selected counters" });
        var zoom = new ToolStripDropDownButton("Zoom") { ToolTipText = "Zoom presets" };
        foreach (var pct in new[] { 25, 50, 100, 200 })
        {
            var f = pct / 100f;
            zoom.DropDownItems.Add(new ToolStripMenuItem(pct + "%", null, (_, _) => Run(() => Canvas.ZoomPreset(f))));
        }
        zoom.DropDownItems.Add(new ToolStripMenuItem("Fit", null, (_, _) => Run(Canvas.ZoomFit)));
        bar.Items.Add(zoom);
        bar.Items.Add(new ToolStripButton("Save map as picture…", null, (_, _) => Run(() => _ = SaveAsPictureAsync())) { ToolTipText = "Save the view or the whole map as a PNG" });
        return bar;
    }

    async Task SaveAsPictureAsync()
    {
        try { await MapExport.ShowAsync(this, _s, Canvas, m => Host?.SetStatus(m)); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("map export failed: " + ex);
            if (!IsDisposed) MessageBox.Show(this, "The picture could not be saved: " + ex.Message, "Save map as picture", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>The view.hideCounters command: flips counter visibility of this window.</summary>
    public void ToggleHideCounters() => Canvas.HideCounters = !Canvas.HideCounters;

    /// <summary>The tool.los command: flips the line-of-sight mode of this window.</summary>
    public void ToggleLos() => Canvas.LosEnabled = !Canvas.LosEnabled;

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Host?.NoteActiveMap(this);
        if (Host is { } mf)
        {
            mf.SetZoomText(MapStatusText.Zoom(Canvas.View.Zoom));
            mf.SetLocationText("-");
        }
    }

    void OnSettingsApplied(Settings s)
    {
        if (!IsDisposed) Canvas.ApplySettings(s);
    }

    void OnGridVisibleChanged()
    {
        if (Host is { } mf) Canvas.ShowGrid = mf.GridVisible;
        Canvas.Invalidate();
    }

    bool _closed;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_closed) { base.OnFormClosed(e); return; }
        _closed = true;
        _cts.Cancel();
        _s.GameReplaced -= OnGameReplaced;
        _s.ViewChanged -= OnViewChanged;
        if (_attached != null) _attached.Changed -= OnGameChanged;
        if (_attached != null) _attached.Rewound -= OnRewound;
        if (Host is { } mf)
        {
            mf.GridVisibleChanged -= OnGridVisibleChanged;
            mf.SettingsApplied -= OnSettingsApplied;
            mf.SetTilingProgress(null);
        }
        Canvas.HidePopup();
        base.OnFormClosed(e);
        _cts.Dispose();
    }

    // ---------- tiling ----------

    async Task PrepareTilesAsync()
    {
        var boards = Map.Boards;
        try
        {
            for (var i = 0; i < boards.Count; i++)
            {
                var index = i;
                var progress = new Progress<double>(p => ReportProgress((index + p) / boards.Count));
                TileSet ts;
                try
                {
                    ts = await _s.Tiler.EnsureTilesAsync(boards[i], progress, _cts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    _s.Log.Add(DiagnosticSeverity.Error, "tiles", $"Board '{boards[i].Name}' could not be prepared: {ex.Message}", Map.Name);
                    ts = new TileSet("", Math.Max(boards[i].Width, 1), Math.Max(boards[i].Height, 1), 0, isPlaceholder: true);
                }
                if (IsDisposed) return;
                Canvas.SetTiles(BoardTiler.TileKey(boards[i]), ts);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (IsDisposed) return;
        Canvas.Preparing = null;
        TilingDone = true;
        if (Host is { } mf) mf.SetTilingProgress(null);
        Canvas.Invalidate();
        TilingCompleted?.Invoke();
    }

    void ReportProgress(double fraction)
    {
        if (IsDisposed || TilingDone) return;
        Canvas.Preparing = fraction;
        if (Host is { } mf) mf.SetTilingProgress(fraction);
        Canvas.Invalidate();
    }

    // ---------- menus ----------

    void OnContext(string? pieceId, Point at)
    {
        var game = _s.Game;
        if (game == null) return;
        var menu = new ContextMenuStrip();
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        if (pieceId != null && game.State.Pieces.ContainsKey(pieceId))
        {
            // The clicked counter first, then the selection (which repeats it when it is selected).
            var ids = new List<string> { pieceId };
            ids.AddRange(Canvas.Selected);
            AddEntries(menu.Items, PieceMenuModel.Build(_s, Map.Id, ids, Host?.Actions, PieceHooks()));
        }
        else
            BuildMapMenu(menu, game);
        if (menu.Items.Count > 0) menu.Show(Canvas, at);
    }

    /// <summary>The dialogs and windows the shared counter menu opens from this window.</summary>
    PieceMenuHooks PieceHooks()
    {
        var host = Host;
        return new PieceMenuHooks
        {
            GameMaster = host?.GameMaster == true,
            ShowInLedger = host == null ? null : ids => host.ShowInLedger(ids),
            ShowFormula = ShowFormula,
            AskInput = (title, prompt) => InputDialog.Ask(this, title, prompt),
            // Settings "ask before deleting": always yes when the setting is off or in smoke mode.
            ConfirmDelete = question => Program.Smoke || Host?.CurrentSettings.ConfirmDelete == false
                || MessageBox.Show(this, question, "Quartermaster", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes,
            EditSheet = EditSheet,
        };
    }

    /// <summary>Turns shared menu entries into WinForms items: same texts, tooltips, separators, submenus and shortcut texts.</summary>
    void AddEntries(ToolStripItemCollection items, IReadOnlyList<MenuEntry> entries)
    {
        foreach (var e in entries)
        {
            if (e.IsSeparator) { items.Add(new ToolStripSeparator()); continue; }
            var item = new ToolStripMenuItem(e.Label) { Enabled = e.Enabled, ToolTipText = e.DisabledReason };
            if (e.CommandId != null && Host is { } host) item.ShortcutKeyDisplayString = host.ShortcutText(e.CommandId);
            if (e.Children.Count > 0) AddEntries(item.DropDownItems, e.Children);
            if (e.Run is { } run) item.Click += (_, _) => Run(run);
            items.Add(item);
        }
    }

    void ShowFormula(string formulaId, string pieceId)
    {
        FormulaDialog.Show(_s, formulaId, pieceId, this, Host);
    }

    void BuildMapMenu(ContextMenuStrip menu, GameSession game)
    {
        var unmoved = PieceActions.ForMap(Map)[0];
        menu.Items.Add(new ToolStripMenuItem("Mark All Unmoved", null, (_, _) => Run(() => Execute(unmoved.Build(game.State)))));
        var grid = new ToolStripMenuItem("Show Grid") { Checked = Canvas.ShowGrid, CheckOnClick = true };
        grid.CheckedChanged += (_, _) => { Canvas.ShowGrid = grid.Checked; Canvas.Invalidate(); };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(grid);
        menu.Items.Add(new ToolStripMenuItem("Zoom to Fit", null, (_, _) => Canvas.ZoomFit()));
        menu.Items.Add(new ToolStripMenuItem("Zoom 100%", null, (_, _) => Canvas.ZoomTo100()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Hide counters", null, (_, _) => ToggleHideCounters()) { Checked = Canvas.HideCounters });
        menu.Items.Add(new ToolStripMenuItem("Centre on selected", null, (_, _) => Canvas.CentreOnSelection()));
        menu.Items.Add(new ToolStripMenuItem("Save map as picture…", null, (_, _) => Run(() => _ = SaveAsPictureAsync())));
    }

    void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("map command failed: " + ex);
            _s.Log.Add(DiagnosticSeverity.Error, "command", "A command failed: " + ex.Message, Map.Name);
        }
    }

    void Execute(IEnumerable<GameCommand> commands) => PieceMenuModel.Execute(_s, Host?.Actions, commands);

    void EditSheet(PieceAction action, string pieceId)
    {
        if (_s.Game is not { } game || !game.State.Pieces.TryGetValue(pieceId, out var piece)) return;
        var index = PieceMenuModel.SheetIndex(_s, piece, action.Label);
        if (index < 0) return;
        var rows = PieceActions.ReadSheet(piece, index);
        using var dlg = new PropertySheetDialog(action.Label, rows, name => PieceMenuModel.ManagedSheetValue(_s, piece, name));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var write = PieceActions.WriteSheet(piece, index, dlg.Values);
        if (Host?.Actions is { } port) port.PieceCommand(write);
        else game.Execute(write);
    }
}
