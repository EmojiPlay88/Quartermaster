using System.Xml;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>MDI shell: menus, toolbar, status bar, module tree, diagnostics, module/game lifecycle.</summary>
sealed class MainForm : Form
{
    readonly StartupOptions _opts;
    readonly ModuleTreePanel _tree = new() { Dock = DockStyle.Left, Width = 220 };
    readonly Splitter _splitter = new() { Dock = DockStyle.Left, Width = 4 };
    readonly DocumentTabStrip _tabs = new();
    readonly MenuStrip _menu = new();
    readonly ToolStrip _toolbar = new() { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
    readonly StatusStrip _status = new() { RenderMode = ToolStripRenderMode.System };
    readonly ToolStripStatusLabel _sbModule = new("No module") { BorderSides = ToolStripStatusLabelBorderSides.Right, BorderStyle = Border3DStyle.Etched };
    readonly ToolStripStatusLabel _sbMessage = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel _sbLocation = new("-") { AutoSize = false, Width = 170, BorderSides = ToolStripStatusLabelBorderSides.Left, BorderStyle = Border3DStyle.Etched };
    readonly ToolStripStatusLabel _sbZoom = new("-") { AutoSize = false, Width = 50, BorderSides = ToolStripStatusLabelBorderSides.Left, BorderStyle = Border3DStyle.Etched };
    readonly ToolStripProgressBar _sbTiling = new() { Visible = false, AutoSize = false, Width = 100 };
    readonly ToolStripMenuItem _showGrid = new("Show &Grid") { Checked = false, CheckOnClick = true };
    readonly ToolStripMenuItem _viewTree = new("Module &Tree") { Checked = true, CheckOnClick = true };
    readonly List<ToolStripItem> _needsModule = new();
    readonly List<ToolStripItem> _needsGame = new();
    readonly Dictionary<string, MapWindow> _maps = new();
    readonly Dictionary<string, Form> _charts = new();
    readonly Dictionary<string, Form> _docs = new();

    ModuleSession? _session;
    DiagnosticsWindow? _diag;
    string? _savePath;
    bool _dirty;
    bool _noticePending;
    bool _startupDone;
    int _problemCount;
    System.Windows.Forms.Timer? _smokeTimer;

    public ModuleSession? Session => _session;
    public bool GridVisible => _showGrid.Checked;
    public event Action? GridVisibleChanged;

    public MainForm(StartupOptions opts)
    {
        _opts = opts;
        IsMdiContainer = true;
        Text = "Quartermaster";
        ClientSize = new Size(1024, 700);
        StartPosition = FormStartPosition.CenterScreen;
        BuildMenu();
        BuildToolbar();
        BuildStatus();

        // Dock order: the last control added is docked first (menu on top, then toolbar, status, left panel).
        Controls.Add(_tabs); // added first so it docks after the tree and spans only the MDI area
        Controls.Add(_splitter);
        Controls.Add(_tree);
        Controls.Add(_status);
        Controls.Add(_toolbar);
        Controls.Add(_menu);
        MainMenuStrip = _menu;

        _tree.NodeActivated += OnNodeActivated;
        MdiChildActivate += (_, _) => { KeepMaximized(); SyncTabs(null); };
        _tabs.TabActivated += f => { if (!f.IsDisposed) f.Activate(); };
        _tabs.TabCloseRequested += CloseTab;
        _tabs.TabsCloseRequested += fs => { foreach (var f in fs) if (!f.IsDisposed) f.Close(); };
        _showGrid.CheckedChanged += (_, _) => GridVisibleChanged?.Invoke();
        _viewTree.CheckedChanged += (_, _) => _tree.Visible = _splitter.Visible = _viewTree.Checked;
        UpdateUi();
        UpdateTitle();
    }

    // ---------- construction ----------

    ToolStripMenuItem Item(ToolStripItemCollection into, string text, Action act, Keys keys = Keys.None, bool needsModule = false, bool needsGame = false)
    {
        var mi = new ToolStripMenuItem(text) { ShortcutKeys = keys };
        mi.Click += (_, _) => Guard(act);
        into.Add(mi);
        if (needsModule) _needsModule.Add(mi);
        if (needsGame) _needsGame.Add(mi);
        return mi;
    }

    void Tool(string text, Action act, bool needsModule = false, bool needsGame = false)
    {
        var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        b.Click += (_, _) => Guard(act);
        _toolbar.Items.Add(b);
        if (needsModule) _needsModule.Add(b);
        if (needsGame) _needsGame.Add(b);
    }

    void BuildMenu()
    {
        var file = new ToolStripMenuItem("&File");
        Item(file.DropDownItems, "&Open Module...", OpenModuleDialog, Keys.Control | Keys.O);
        file.DropDownItems.Add(new ToolStripSeparator());
        Item(file.DropDownItems, "&New Game", NewGame, Keys.Control | Keys.N, needsModule: true);
        Item(file.DropDownItems, "Load &Setup...", LoadSetupDialog, needsModule: true);
        Item(file.DropDownItems, "Open &Game...", OpenGameDialog, Keys.Control | Keys.G, needsModule: true);
        file.DropDownItems.Add(new ToolStripSeparator());
        Item(file.DropDownItems, "&Save Game", () => SaveGame(false), Keys.Control | Keys.S, needsGame: true);
        Item(file.DropDownItems, "Save Game &As...", () => SaveGame(true), Keys.Control | Keys.Shift | Keys.S, needsGame: true);
        file.DropDownItems.Add(new ToolStripSeparator());
        Item(file.DropDownItems, "E&xit", Close);

        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.Add(_viewTree);
        Item(view.DropDownItems, "&Diagnostics", ShowDiagnostics, Keys.Control | Keys.D, needsModule: true);
        view.DropDownItems.Add(_showGrid);

        var window = new ToolStripMenuItem("&Window");
        Item(window.DropDownItems, "&Cascade", () => Arrange(MdiLayout.Cascade));
        Item(window.DropDownItems, "Tile &Horizontally", () => Arrange(MdiLayout.TileHorizontal));
        Item(window.DropDownItems, "Tile &Vertically", () => Arrange(MdiLayout.TileVertical));
        Item(window.DropDownItems, "Close &All", CloseAllWindows);
        window.DropDownItems.Add(new ToolStripSeparator());

        var help = new ToolStripMenuItem("&Help");
        Item(help.DropDownItems, "&About", () => Info("Quartermaster\nA VASSAL module reader and engine.", "About Quartermaster"));

        _menu.Items.AddRange(new ToolStripItem[] { file, view, window, help });
        _menu.MdiWindowListItem = window;
    }

    void BuildToolbar()
    {
        Tool("Open Module", OpenModuleDialog);
        Tool("New Game", NewGame, needsModule: true);
        Tool("Load Setup", LoadSetupDialog, needsModule: true);
        Tool("Open Game", OpenGameDialog, needsModule: true);
        Tool("Save Game", () => SaveGame(false), needsGame: true);
        _toolbar.Items.Add(new ToolStripSeparator());
        Tool("Diagnostics", ShowDiagnostics, needsModule: true);
    }

    void BuildStatus()
    {
        _sbMessage.Click += (_, _) =>
        {
            if (_problemCount > 0) ShowDiagnostics();
        };
        _status.Items.AddRange(new ToolStripItem[] { _sbModule, _sbMessage, _sbTiling, _sbLocation, _sbZoom });
    }

    // ---------- public hooks for map windows (Task 12) ----------

    public void SetLocationText(string text) => _sbLocation.Text = text;
    public void SetZoomText(string text) => _sbZoom.Text = text;
    public void SetStatus(string text) => _sbMessage.Text = text;

    /// <summary>Shows tiling progress 0..1; null hides the bar.</summary>
    public void SetTilingProgress(double? fraction)
    {
        _sbTiling.Visible = fraction != null;
        if (fraction != null) _sbTiling.Value = (int)Math.Clamp(fraction.Value * 100, 0, 100);
    }

    // ---------- helpers ----------

    void Guard(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError(ex.Message);
        }
    }

    void ShowError(string message)
    {
        if (Program.Smoke)
        {
            Program.AppLog("error: " + message);
            Program.ExitCode = 1;
            return;
        }
        MessageBox.Show(this, message, "Quartermaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    void Info(string message, string caption)
    {
        if (Program.Smoke) { Program.AppLog(caption + ": " + message); return; }
        MessageBox.Show(this, message, caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    bool ConfirmDiscard()
    {
        if (!_dirty || Program.Smoke) return true;
        return MessageBox.Show(this, "The current game has unsaved changes. Discard them?", "Quartermaster",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    void UpdateUi()
    {
        foreach (var i in _needsModule) i.Enabled = _session != null;
        foreach (var i in _needsGame) i.Enabled = _session?.Game != null;
    }

    void UpdateTitle()
    {
        var t = "Quartermaster";
        if (_session != null)
        {
            t += $": {_session.Model.Name} {_session.Model.Version}".TrimEnd();
            if (_session.Game != null)
            {
                if (_savePath != null) t += " — " + Path.GetFileNameWithoutExtension(_savePath);
                if (_dirty) t += "*";
            }
        }
        Text = t;
    }

    // ---------- module lifecycle ----------

    void OpenModuleDialog()
    {
        if (Program.Smoke) return;
        using var dlg = new OpenFileDialog { Filter = "VASSAL modules (*.vmod)|*.vmod|All files (*.*)|*.*", Title = "Open Module" };
        if (dlg.ShowDialog(this) == DialogResult.OK) OpenModule(dlg.FileName, fromArgs: false);
    }

    /// <summary>Opens a module and starts a game. Returns false (leaving everything as it was) on failure.</summary>
    bool OpenModule(string path, bool fromArgs)
    {
        if (!ConfirmDiscard()) return false;
        ModuleSession next;
        var prevWait = Application.UseWaitCursor;
        Application.UseWaitCursor = true;
        Cursor.Current = Cursors.WaitCursor;
        try
        {
            next = ModuleSession.Open(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException or UnauthorizedAccessException)
        {
            ShowError($"Could not open '{path}':\n{ex.Message}");
            return false;
        }
        finally
        {
            Application.UseWaitCursor = prevWait;
        }

        CloseSession();
        _session = next;
        _savePath = null;
        _dirty = false;
        _session.Log.Added += OnLogAdded;
        _session.GameReplaced += OnGameReplaced;
        _tree.Load(_session.Model);
        _sbModule.Text = $"{_session.Model.Name} {_session.Model.Version}".Trim();
        _sbLocation.Text = "-";
        _sbZoom.Text = "-";
        RefreshProblemNotice();
        UpdateUi();
        UpdateTitle();

        Guard(() => StartGameFor(_session, fromArgs));
        OpenMainMap();
        return true;
    }

    void StartGameFor(ModuleSession s, bool fromArgs)
    {
        if (fromArgs && _opts.SetupIndex is { } idx)
        {
            if (idx < s.Model.Setups.Count) s.LoadSetup(s.Model.Setups[idx]);
            else
            {
                Program.AppLog($"--setup {idx} is out of range (module has {s.Model.Setups.Count}); starting a new game.");
                if (Program.Smoke) Program.ExitCode = 1;
                s.StartNewGame();
            }
        }
        else if (fromArgs && _opts.NewGame) s.StartNewGame();
        else if (Program.Smoke)
        {
            Program.AppLog("Smoke mode: setup chooser skipped; starting a new game.");
            s.StartNewGame();
        }
        else
        {
            using var dlg = new SetupChooserDialog(s.Model);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (dlg.Selected is { } setup) s.LoadSetup(setup); else s.StartNewGame();
        }
    }

    void OpenMainMap()
    {
        var map = _session?.Model.Maps.FirstOrDefault(x => !x.IsOrgDisplay) ?? _session?.Model.Maps.FirstOrDefault();
        if (map != null) Guard(() => OpenMap(map, maximized: true));
    }

    /// <summary>Opens a window for the map, or activates it when already open.</summary>
    MapWindow? OpenMap(MapDef map, bool maximized)
    {
        if (_session == null) return null;
        if (_maps.TryGetValue(map.Id, out var existing) && !existing.IsDisposed)
        {
            existing.Activate();
            return existing;
        }
        var w = new MapWindow(_session, map) { MdiParent = this, Size = new Size(720, 520) };
        _maps[map.Id] = w;
        w.FormClosed += (_, _) => { if (_maps.TryGetValue(map.Id, out var cur) && cur == w) _maps.Remove(map.Id); };
        w.FormClosed += (_, _) => SyncTabs(w);
        if (maximized) { _maximizeDocs = true; w.WindowState = FormWindowState.Maximized; }
        w.Show();
        return w;
    }

    void CloseSession()
    {
        CloseAllWindows();
        if (_session == null) return;
        _session.Log.Added -= OnLogAdded;
        _session.GameReplaced -= OnGameReplaced;
        _session.Dispose();
        _session = null;
        _tree.Clear();
    }

    void CloseAllWindows()
    {
        foreach (var c in MdiChildren) c.Close();
    }

    void OnGameReplaced()
    {
        if (_session?.Game is { } g) g.Changed += _ => { _dirty = true; UpdateTitle(); };
        _dirty = false;
        _savePath = null;
        UpdateUi();
        UpdateTitle();
    }

    // Log.Added can fire from any thread, and an exception here would propagate into the logger.
    void OnLogAdded(Diagnostic d)
    {
        try
        {
            if (d.Severity < DiagnosticSeverity.Warning || _noticePending || !IsHandleCreated || IsDisposed) return;
            _noticePending = true;
            BeginInvoke(new Action(() =>
            {
                _noticePending = false;
                RefreshProblemNotice();
            }));
        }
        catch (Exception) { /* shutting down */ }
    }

    void RefreshProblemNotice()
    {
        _problemCount = _session?.Log.Items.Count(d => d.Severity >= DiagnosticSeverity.Warning) ?? 0;
        _sbMessage.Text = _problemCount > 0
            ? $"{_problemCount} warning(s)/error(s) - click here to view diagnostics"
            : "";
        _sbMessage.IsLink = _problemCount > 0;
    }

    void ShowDiagnostics()
    {
        if (_session == null) return;
        if (_diag is { IsDisposed: false })
        {
            _diag.Activate();
            return;
        }
        _diag = new DiagnosticsWindow(_session.Log) { MdiParent = this, Size = new Size(760, 320) };
        _diag.FormClosed += (_, _) => { _diag = null; };
        var d = _diag;
        d.FormClosed += (_, _) => SyncTabs(d);
        _maximizeDocs = true;
        d.WindowState = FormWindowState.Maximized;
        d.Show();
    }

    // ---------- game commands ----------

    void NewGame()
    {
        if (_session == null || !ConfirmDiscard()) return;
        _session.StartNewGame();
    }

    void LoadSetupDialog()
    {
        if (_session == null || !ConfirmDiscard()) return;
        using var dlg = new SetupChooserDialog(_session.Model);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (dlg.Selected is { } s) _session.LoadSetup(s); else _session.StartNewGame();
    }

    void OpenGameDialog()
    {
        if (_session == null || Program.Smoke || !ConfirmDiscard()) return;
        using var dlg = new OpenFileDialog { Filter = "Quartermaster saves (*.qsave)|*.qsave|All files (*.*)|*.*", Title = "Open Game" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _session.LoadNative(dlg.FileName);
        _savePath = dlg.FileName;
        _dirty = false;
        UpdateTitle();
    }

    void SaveGame(bool saveAs)
    {
        if (_session?.Game == null) return;
        var path = _savePath;
        if (saveAs || path == null)
        {
            if (Program.Smoke) return;
            using var dlg = new SaveFileDialog { Filter = "Quartermaster saves (*.qsave)|*.qsave", DefaultExt = "qsave", Title = "Save Game" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            path = dlg.FileName;
        }
        _session.SaveNative(path);
        _savePath = path;
        _dirty = false;
        UpdateTitle();
    }

    void OnNodeActivated(WindowTreeNode n)
    {
        if (_session == null) return;
        if (n.Kind == WindowNodeKind.Setup && n.Ref is SetupDef s)
        {
            if (!ConfirmDiscard()) return;
            Guard(() => _session.LoadSetup(s));
            return;
        }
        if (n.Kind is WindowNodeKind.Map or WindowNodeKind.OrgDisplay && n.Ref is MapDef map)
        {
            Guard(() => OpenMap(map, maximized: true));
            return;
        }
        if (n.Kind == WindowNodeKind.Chart && n.Ref is ChartDef chart)
        {
            Guard(() => OpenChild(_charts, chart.Name + "|" + chart.FileName, () => new ChartWindow(_session, chart)));
            return;
        }
        if (n.Kind == WindowNodeKind.Document && n.Ref is DocumentDef doc)
        {
            if (Program.Smoke && doc.Kind != DocumentKind.Text) return; // no WebView2 windows in smoke mode
            Guard(() => OpenChild(_docs, doc.EntryName, () => new PdfWindow(_session, doc)));
        }
    }

    void OpenChild(Dictionary<string, Form> open, string key, Func<Form> create)
    {
        if (open.TryGetValue(key, out var existing) && !existing.IsDisposed)
        {
            existing.Activate();
            return;
        }
        var w = create();
        w.MdiParent = this;
        w.Size = new Size(720, 520);
        open[key] = w;
        w.FormClosed += (_, _) => { if (open.TryGetValue(key, out var cur) && cur == w) open.Remove(key); SyncTabs(w); };
        _maximizeDocs = true;
        w.WindowState = FormWindowState.Maximized;
        w.Show();
    }

    // ---------- document tabs ----------

    bool _maximizeDocs = true; // browser feel: documents fill the MDI area until the user cascades/tiles

    void Arrange(MdiLayout layout)
    {
        _maximizeDocs = false;
        foreach (var c in MdiChildren) if (c.WindowState == FormWindowState.Maximized) c.WindowState = FormWindowState.Normal;
        LayoutMdi(layout);
    }

    void KeepMaximized()
    {
        if (_maximizeDocs && ActiveMdiChild is { IsDisposed: false, WindowState: not FormWindowState.Maximized } a) a.WindowState = FormWindowState.Maximized;
    }

    void SyncTabs(Form? closing) =>
        _tabs.Sync(MdiChildren.Where(c => c != closing && !c.IsDisposed), ActiveMdiChild != closing ? ActiveMdiChild : null);

    /// <summary>Closes a tab's window; if it was the active one, activates its neighbour in tab order.</summary>
    void CloseTab(Form f)
    {
        if (f.IsDisposed) return;
        var wasActive = ReferenceEquals(f, ActiveMdiChild);
        var idx = _tabs.Tabs.ToList().IndexOf(f);
        var count = _tabs.Tabs.Count;
        f.Close();
        if (!wasActive || !f.IsDisposed) return;
        var next = TabOrder.AfterClose(count, idx);
        if (next >= 0 && next < _tabs.Tabs.Count && !_tabs.Tabs[next].IsDisposed) _tabs.Tabs[next].Activate();
    }

    void StepTab(int delta)
    {
        var tabs = _tabs.Tabs;
        var i = TabOrder.Step(tabs.Count, ActiveMdiChild == null ? -1 : tabs.ToList().IndexOf(ActiveMdiChild), delta);
        if (i >= 0) tabs[i].Activate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.Tab: StepTab(1); return true;
            case Keys.Control | Keys.Shift | Keys.Tab: StepTab(-1); return true;
            case Keys.Control | Keys.W:
                if (ActiveMdiChild is { } a) CloseTab(a);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------- startup / shutdown ----------

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_opts.SmokeSeconds is { } secs)
        {
            _smokeTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1, secs) * 1000 };
            _smokeTimer.Tick += (_, _) =>
            {
                _smokeTimer!.Stop();
                FinishSmoke();
            };
            _smokeTimer.Start();
        }
        if (_opts.ModulePath != null && !_startupDone)
        {
            _startupDone = true;
            BeginInvoke(new Action(() =>
            {
                if (!OpenModule(_opts.ModulePath, fromArgs: true) && Program.Smoke) Program.ExitCode = 1;
            }));
        }
    }

    // ---------- smoke: wait for tiling, optional screenshot ----------

    DateTime? _smokeWaitStart;
    readonly DateTime _smokeStartedAt = DateTime.UtcNow;

    bool _smokeExtrasOpened;

    /// <summary>Opens the first chart and first text document so the tab strip shows several tabs (no PDF/WebView2 in smoke).</summary>
    void OpenSmokeDocuments()
    {
        if (_session == null) return;
        if (_session.Model.Charts.FirstOrDefault() is { } c) OnNodeActivated(new WindowTreeNode(c.Name, WindowNodeKind.Chart, c, []));
        if (_session.Model.Documents.FirstOrDefault(d => d.Kind == DocumentKind.Text) is { } d)
            OnNodeActivated(new WindowTreeNode(d.Title, WindowNodeKind.Document, d, []));
        if (_maps.Values.FirstOrDefault(w => !w.IsDisposed) is { } m) m.Activate(); // tab strip shows all tabs, map on top
        Update();
        Program.AppLog($"smoke: strip bounds {_tabs.Bounds} visible {_tabs.Visible}; mdi states {string.Join(',', MdiChildren.Select(m => m.WindowState))}");
        Program.AppLog($"smoke: {_tabs.Tabs.Count} document tab(s): " + string.Join(" | ", _tabs.Tabs.Select(t => t.Text)));
    }

    void FinishSmoke()
    {
        var pending = _maps.Values.Any(w => !w.IsDisposed && !w.TilingDone);
        _smokeWaitStart ??= DateTime.UtcNow;
        if (pending && DateTime.UtcNow - _smokeWaitStart < TimeSpan.FromSeconds(60))
        {
            _smokeTimer!.Interval = 200;
            _smokeTimer.Start();
            return;
        }
        if (!_smokeExtrasOpened) { _smokeExtrasOpened = true; Guard(OpenSmokeDocuments); }
        if (pending) Program.AppLog("smoke: tiling did not finish within 60 s.");
        else Program.AppLog($"smoke: tiling finished {(DateTime.UtcNow - _smokeStartedAt).TotalSeconds:F1} s after window start.");
        Program.AppLog($"smoke: peak working set {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024)} MB");
        if (_opts.Screenshot != null) Guard(() => SaveScreenshot(_opts.Screenshot));
        Close();
    }

    /// <summary>Main form via DrawToBitmap, with each visible map canvas re-rendered by Skia on top of its screen area.</summary>
    void SaveScreenshot(string path)
    {
        Update();
        using var shot = new Bitmap(Math.Max(Width, 1), Math.Max(Height, 1));
        DrawToBitmap(shot, new Rectangle(0, 0, shot.Width, shot.Height));
        using (var g = Graphics.FromImage(shot))
        {
            if (_tabs.Visible)
            {
                using var tb = new Bitmap(_tabs.Width, _tabs.Height);
                _tabs.DrawToBitmap(tb, new Rectangle(Point.Empty, tb.Size));
                g.DrawImageUnscaled(tb, _tabs.PointToScreen(Point.Empty).X - Bounds.X, _tabs.PointToScreen(Point.Empty).Y - Bounds.Y);
            }
            var active = ActiveMdiChild as MapWindow ?? _maps.Values.FirstOrDefault(w => !w.IsDisposed);
            if (active != null && active.Visible)
            {
                using var snap = active.Canvas.Snapshot();
                using var bmp = SkiaSharp.Views.Desktop.Extensions.ToBitmap(snap);
                var origin = new Point(active.Canvas.PointToScreen(Point.Empty).X - Bounds.X, active.Canvas.PointToScreen(Point.Empty).Y - Bounds.Y);
                g.DrawImageUnscaled(bmp, origin);
            }
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        shot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Program.AppLog("smoke: screenshot saved to " + path);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!e.Cancel && !ConfirmDiscard()) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _smokeTimer?.Dispose();
        CloseSession();
        base.OnFormClosed(e);
    }
}
