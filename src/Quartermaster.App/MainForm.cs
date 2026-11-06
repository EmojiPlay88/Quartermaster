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
    readonly MenuStrip _menu = new();
    readonly ToolStrip _toolbar = new() { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
    readonly StatusStrip _status = new() { RenderMode = ToolStripRenderMode.System };
    readonly ToolStripStatusLabel _sbModule = new("No module") { BorderSides = ToolStripStatusLabelBorderSides.Right, BorderStyle = Border3DStyle.Etched };
    readonly ToolStripStatusLabel _sbMessage = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel _sbLocation = new("-") { AutoSize = false, Width = 170, BorderSides = ToolStripStatusLabelBorderSides.Left, BorderStyle = Border3DStyle.Etched };
    readonly ToolStripStatusLabel _sbZoom = new("-") { AutoSize = false, Width = 50, BorderSides = ToolStripStatusLabelBorderSides.Left, BorderStyle = Border3DStyle.Etched };
    readonly ToolStripProgressBar _sbTiling = new() { Visible = false, AutoSize = false, Width = 100 };
    readonly ToolStripMenuItem _showGrid = new("Show &Grid") { Checked = true, CheckOnClick = true };
    readonly ToolStripMenuItem _viewTree = new("Module &Tree") { Checked = true, CheckOnClick = true };
    readonly List<ToolStripItem> _needsModule = new();
    readonly List<ToolStripItem> _needsGame = new();

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
        Controls.Add(_splitter);
        Controls.Add(_tree);
        Controls.Add(_status);
        Controls.Add(_toolbar);
        Controls.Add(_menu);
        MainMenuStrip = _menu;

        _tree.NodeActivated += OnNodeActivated;
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
        Item(window.DropDownItems, "&Cascade", () => LayoutMdi(MdiLayout.Cascade));
        Item(window.DropDownItems, "Tile &Horizontally", () => LayoutMdi(MdiLayout.TileHorizontal));
        Item(window.DropDownItems, "Tile &Vertically", () => LayoutMdi(MdiLayout.TileVertical));
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
        // Task 12: open Model.Maps[0] maximized in an MDI child.
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
        _diag.FormClosed += (_, _) => _diag = null;
        _diag.Show();
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
        // Map, chart and document windows arrive in later steps.
        SetStatus("Opens in a later step");
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
                Close();
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
