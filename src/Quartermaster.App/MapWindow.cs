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
        Canvas.Guarded = Run;

        Canvas.ContextRequested += OnContext;
        Canvas.StatusChanged += (loc, zoom) =>
        {
            if (Host is { } mf && mf.IsActiveDocument(this)) { mf.SetLocationText(loc); mf.SetZoomText(zoom); }
        };
        s.GameReplaced += OnGameReplaced;
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
        _attached = _s.Game;
        if (_attached != null) _attached.Changed += OnGameChanged;
    }

    void OnGameReplaced()
    {
        Attach();
        OnUi(Canvas.PruneSelection);
    }

    void OnGameChanged(GameCommand _) => OnUi(Canvas.PruneSelection);

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
        }
        _ = PrepareTilesAsync();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (Host is { } mf)
        {
            mf.SetZoomText($"{Math.Round(Canvas.View.Zoom * 100)}%");
            mf.SetLocationText("-");
        }
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
        if (_attached != null) _attached.Changed -= OnGameChanged;
        if (Host is { } mf)
        {
            mf.GridVisibleChanged -= OnGridVisibleChanged;
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
            BuildPieceMenu(menu, piece, game);
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
        if (menu.Items.Count == 0) menu.Items.Add(new ToolStripMenuItem("(no commands)") { Enabled = false });
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
        foreach (var c in commands) _s.Game?.Execute(c);
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
        foreach (var p in targets)
        {
            var mine = p.Id == clicked.Id
                ? action
                : PieceActions.For(p, _s.Model, game.State, NewId).FirstOrDefault(a => a.Label == action.Label && a.Group == action.Group && a.Enabled);
            if (mine == null) continue;
            Execute(input != null && mine.BuildWithInput != null ? mine.BuildWithInput(game.State, input) : mine.Build(game.State));
        }
    }

    void EditSheet(PieceAction action, PieceInstance piece, GameSession game)
    {
        var traits = PieceTypeCache.For(_s.Images).Get(piece.TypeString).Traits;
        var index = -1;
        for (var i = 0; i < traits.Count; i++)
            if (traits[i] is PropertySheetDef d && d.MenuName == action.Label) { index = i; break; }
        if (index < 0) return;
        var rows = PieceActions.ReadSheet(piece, index);
        using var dlg = new PropertySheetDialog(action.Label, rows);
        if (dlg.ShowDialog(this) == DialogResult.OK) game.Execute(PieceActions.WriteSheet(piece, index, dlg.Values));
    }
}
