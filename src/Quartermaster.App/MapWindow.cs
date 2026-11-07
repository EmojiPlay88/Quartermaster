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
        Canvas.Phases = () => Host?.Phases;

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
            mf.SetZoomText($"{Math.Round(Canvas.View.Zoom * 100)}%");
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

    static string NewId() => "c" + Guid.NewGuid().ToString("N");

    void OnContext(string? pieceId, Point at)
    {
        var game = _s.Game;
        if (game == null) return;
        var menu = new ContextMenuStrip();
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        if (pieceId != null && game.State.Pieces.TryGetValue(pieceId, out var piece))
        {
            // Hot-seat: a piece drawn as its mask (or not shown at all) must not reveal its real commands.
            if (_s.ViewSides != null && (_s.View?.Pieces.TryGetValue(pieceId, out var seen) != true || seen!.TypeString != piece.TypeString))
                menu.Items.Add(new ToolStripMenuItem("(no commands)") { Enabled = false });
            else BuildPieceMenu(menu, piece, game);
        }
        else
            BuildMapMenu(menu, game);
        if (menu.Items.Count > 0) menu.Show(Canvas, at);
    }

    void BuildPieceMenu(ContextMenuStrip menu, PieceInstance piece, GameSession game)
    {
        var actions = PieceActions.For(piece, _s.Model, game.State, NewId);
        foreach (var group in ActionMenu.Group(actions.Where(a => a.Group != "error")))
        {
            if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
            foreach (var a in group)
            {
                var action = a;
                var item = new ToolStripMenuItem(a.Label) { Enabled = a.Enabled, ToolTipText = a.DisabledReason };
                item.Click += (_, _) => Run(() => RunAction(action, piece));
                menu.Items.Add(item);
            }
        }
        AddCalculateMenu(menu, piece, game);
        if (Host?.GameMaster == true)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Delete counter", null, (_, _) => Run(() => DeleteCounters(piece, game))));
        }
        if (menu.Items.Count == 0) menu.Items.Add(new ToolStripMenuItem("(no commands)") { Enabled = false });
    }

    /// <summary>"Calculate" submenu: one entry per formula that applies to the piece. Hidden without a rules pack.</summary>
    void AddCalculateMenu(ContextMenuStrip menu, PieceInstance piece, GameSession game)
    {
        if (_s.Rules is not { } rules || _s.Runner == null || !_s.CanAct(piece.Id)) return;
        IReadOnlyList<Quartermaster.Rules.FormulaDef> formulas;
        try { formulas = PieceFilter.FormulasFor(rules, piece, game.State, _s.Model); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Could not list formulas for a piece: " + ex.Message);
            return;
        }
        if (formulas.Count == 0) return;
        if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
        var calc = new ToolStripMenuItem("Calculate");
        foreach (var f in formulas)
        {
            var id = f.Id;
            calc.DropDownItems.Add(new ToolStripMenuItem(f.Name, null, (_, _) => Run(() => ShowFormula(id, piece.Id))));
        }
        menu.Items.Add(calc);
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

    void Execute(IEnumerable<GameCommand> commands)
    {
        if (_s.Game is not { } game) return;
        var phases = Host?.Phases;
        var gesture = new List<MovePieces>();
        void FlushMoves()
        {
            if (gesture.Count == 0) return;
            var moves = gesture.ToList();
            gesture.Clear();
            phases!.ExecuteMove(moves);
        }
        using var scope = game.BeginAction();   // one undoable action per call; label comes from the first command
        foreach (var c in commands)
        {
            // Move commands (Send to, keyboard moves) are move gestures: consecutive ones go through the phase controller as one call.
            if (phases != null && c is MovePieces m) { gesture.Add(m); continue; }
            FlushMoves();
            game.Execute(CommandLog.Prepare(c, game.State, _s));
        }
        FlushMoves();
    }

    void RunAction(PieceAction action, PieceInstance clicked)
    {
        if (_s.Game is not { } game) return;
        if (action.Group == "sheet") { EditSheet(action, clicked, game); return; }

        // The action applies to every selected piece that offers the same command (the clicked piece always does).
        var targets = game.State.Pieces.Values.Where(p => p.Id == clicked.Id || Canvas.Selected.Contains(p.Id)).ToList();
        string? input = null;
        if (action.BuildWithInput != null)
        {
            input = InputDialog.Ask(this, action.Label, action.PromptText is { Length: > 0 } t ? t : action.Label);
            if (input == null) return;
        }
        var batches = new List<GameCommand[]>();
        var deleting = new List<string>();
        foreach (var p in targets)
        {
            var mine = p.Id == clicked.Id
                ? action
                : PieceActions.For(p, _s.Model, game.State, NewId).FirstOrDefault(a => a.Label == action.Label && a.Group == action.Group && a.Enabled);
            if (mine == null) continue;
            if (mine.IsDelete) deleting.Add(p.Id);
            batches.Add((input != null && mine.BuildWithInput != null ? mine.BuildWithInput(game.State, input) : mine.Build(game.State)).ToArray());
        }
        if (!ConfirmDeletes(game.State, deleting, clicked)) return;
        foreach (var b in batches) Execute(b);
    }

    /// <summary>Game master: removes the clicked counter, or the whole selection when the clicked counter is selected (after the usual confirmation).</summary>
    void DeleteCounters(PieceInstance clicked, GameSession game)
    {
        // The selection is the target only when the clicked counter is part of it; otherwise just the clicked counter.
        var ids = Canvas.Selected.Contains(clicked.Id)
            ? game.State.Pieces.Keys.Where(Canvas.Selected.Contains).ToList()
            : new List<string> { clicked.Id };
        if (!ConfirmDeletes(game.State, ids, clicked)) return;
        Execute(ids.Select(id => new RemovePiece(id)));
    }

    /// <summary>Settings "ask before deleting": one Yes/No for the pieces a trait Delete action would remove. Always true when the setting is off or in smoke mode.</summary>
    bool ConfirmDeletes(GameState st, List<string> ids, PieceInstance clicked)
    {
        if (ids.Count == 0 || Program.Smoke || Host?.CurrentSettings.ConfirmDelete == false) return true;
        var what = ids.Count == 1
            ? (st.Pieces.TryGetValue(ids[0], out var p) ? PieceInfo.Name(p) : clicked.Id) is { Length: > 0 } n ? n : "this piece"
            : ids.Count + " pieces";
        return MessageBox.Show(this, $"Delete {what}?", "Quartermaster", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    /// <summary>Ledger value text for a sheet row that a rules-pack property manages; null when no pack is loaded or no property is bound to the row.</summary>
    string? ManagedSheetValue(PieceInstance piece, GameSession game, string rowName)
    {
        if (_s.Rules is not { } rules) return null;
        var prop = rules.Properties.FirstOrDefault(p => p.Scope != Quartermaster.Rules.PropertyScope.Holders
            && string.Equals(p.SheetField, rowName, StringComparison.OrdinalIgnoreCase)
            && PieceFilter.Matches(p.AppliesTo, piece, game.State, _s.Model, rules));
        if (prop == null) return null;
        if (_s.View is not { } view || (_s.ViewSides != null && Ledger.Get(view, LedgerKey.Piece(piece.Id), prop.Id) == null
            && Ledger.Get(game.State, LedgerKey.Piece(piece.Id), prop.Id) != null)) return ModuleSession.HiddenText;
        return Ledger.GetOrDefault(view,LedgerKey.Piece(piece.Id), prop).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    void EditSheet(PieceAction action, PieceInstance piece, GameSession game)
    {
        var traits = PieceTypeCache.For(_s.Images).Get(piece.TypeString).Traits;
        var index = -1;
        for (var i = 0; i < traits.Count; i++)
            if (traits[i] is PropertySheetDef d && d.MenuName == action.Label) { index = i; break; }
        if (index < 0) return;
        var rows = PieceActions.ReadSheet(piece, index);
        using var dlg = new PropertySheetDialog(action.Label, rows, name => ManagedSheetValue(piece, game, name));
        if (dlg.ShowDialog(this) == DialogResult.OK) game.Execute(PieceActions.WriteSheet(piece, index, dlg.Values));
    }
}
