using System.Xml;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>MDI shell: menus, toolbar, status bar, module tree, diagnostics, module/game lifecycle.</summary>
sealed partial class MainForm : Form
{
    readonly StartupOptions _opts;
    readonly SplitContainer _leftSplit = new() { Dock = DockStyle.Left, Width = 220, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.None };
    readonly ModuleTreePanel _tree = new() { Dock = DockStyle.Fill };
    readonly TablesTreePanel _tablesTree = new() { Dock = DockStyle.Fill };
    readonly WindowLayoutStore _layoutStore = new(LayoutRoot);
    double _leftFraction = WindowLayoutStore.DefaultLeftSplit;
    bool _leftSplitReady, _applyingSplit;
    readonly Splitter _splitter = new() { Dock = DockStyle.Left, Width = 4 };
    readonly DocumentTabStrip _tabs = new();
    readonly Panel _logHost = new() { Dock = DockStyle.Bottom, Height = 170, Visible = false };
    readonly Splitter _logSplitter = new() { Dock = DockStyle.Bottom, Height = 4, Visible = false };
    readonly ToolStripMenuItem _viewLog = new("&Log");
    readonly LogDock _logDock;
    readonly Panel _paletteHost = new() { Dock = DockStyle.Fill, Visible = false };
    readonly Splitter _paletteSplitter = new() { Dock = DockStyle.Right, Width = 4, Visible = false };
    readonly PaletteDock _paletteDock;
    readonly ToolStripMenuItem _viewPalette = new("Counter &palette") { Visible = false };
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
    readonly List<ToolStripItem> _needsRules = new();
    readonly Dictionary<string, MapWindow> _maps = new();
    readonly Dictionary<string, Form> _charts = new();
    readonly Dictionary<string, Form> _docs = new();
    readonly Dictionary<string, Form> _ledgers = new();
    readonly Dictionary<string, Form> _benches = new();

    readonly WindowManager _wm;
    readonly ToolStripMenuItem _floatingMenu = new("&Floating windows") { Visible = false };
    readonly ToolStripMenuItem _popOutActive = new("&Pop Out Active Window");
    ModuleSession? _session;
    ToolStripMenuItem? _toolsMenu;
    DiceTools? _dice;
    DiagnosticsWindow? _diag;
    string? _savePath;
    bool _recoveredAutosave; // the open game came from an autosave file; Save goes to Save As
    bool _dirty;
    bool _noticePending;
    bool _startupDone;
    int _problemCount;
    System.Windows.Forms.Timer? _smokeTimer;

    public ModuleSession? Session => _session;
    public bool GridVisible => _showGrid.Checked;
    public event Action? GridVisibleChanged;

    readonly LaunchRequest? _launch;
    bool _backToLauncher;

    /// <summary>True when the window was closed through File -> Back to launcher (not the close box or Exit).</summary>
    public bool ReturnToLauncher { get; private set; }

    /// <summary>The module last opened in this window (the launcher selects it after Back to launcher).</summary>
    public string? OpenedModulePath { get; private set; }

    public MainForm(StartupOptions opts) : this(opts, null) { }

    public MainForm(StartupOptions opts, LaunchRequest? launch)
    {
        _opts = opts;
        _launch = launch;
        IsMdiContainer = true;
        Text = "Quartermaster";
        ClientSize = new Size(1024, 700);
        StartPosition = FormStartPosition.CenterScreen;
        _wm = new WindowManager(this, _tabs, _layoutStore);
        _wm.FloatingChanged += RebuildFloatingMenu;
        _logDock = new LogDock(this, _wm, _logHost, _logSplitter, _viewLog, CentreOnPiece, Guard);
        _paletteDock = new PaletteDock(this, _wm, _paletteHost, _paletteSplitter, Guard);
        InitPhases();
        InitOutliner();
        InitAlertBar();
        RegisterCommands();
        InitLos();
        InitHotSeat();
        _cmdItems["view.log"] = _viewLog;
        BuildMenu();
        BuildToolbar();
        BuildStatus();

        // Dock order: the last control added is docked first (menu on top, then toolbar, status, left panel).
        Controls.Add(_tabs); // added first so it docks after the tree and spans only the MDI area
        Controls.Add(_logSplitter); // the log docks at the bottom of the MDI area, beside the tree
        Controls.Add(_logHost);
        BuildRightStack(); // phase panel on top, counter palette (game master mode) below, in one right-hand column
        Controls.Add(_rightSplitter);
        Controls.Add(_rightHost);
        Controls.Add(_splitter);
        _leftSplit.Panel1.Controls.Add(_tree);
        _leftSplit.Panel2.Controls.Add(_tablesTree);
        Controls.Add(_leftSplit);
        Controls.Add(_status);
        Controls.Add(_toolbar);
        Controls.Add(_alertBar);
        Controls.Add(_strip); // the phase tracker sits directly under the toolbar
        Controls.Add(_menu);
        // Explicit z-order (highest index docks first): menu, toolbar, then the phase strip, so the strip sits under the toolbar.
        FixTopOrder();
        MainMenuStrip = _menu;

        _tree.NodeActivated += OnNodeActivated;
        _tablesTree.TableActivated += id => Guard(() => ShowWorkbenchItem(w => w.ShowTable(id)));
        _tablesTree.FormulaActivated += id => Guard(() => ShowWorkbenchItem(w => w.ShowFormula(id)));
        _leftSplit.SplitterMoved += (_, _) => RememberLeftSplit();
        _leftSplit.SizeChanged += (_, _) => ApplyLeftSplit();
        MdiChildActivate += (_, _) => { KeepMaximized(); SyncTabs(null); NoteActiveMap(ActiveMdiChild as MapWindow); };
        _tabs.TabActivated += f => { if (!f.IsDisposed) f.Activate(); };
        _tabs.TabCloseRequested += CloseTab;
        _tabs.TabPopOutRequested += (f, at) => Guard(() => _wm.PopOut(f, at));
        _tabs.TabsCloseRequested += fs => { foreach (var f in fs) if (!f.IsDisposed) f.Close(); };
        _showGrid.CheckedChanged += (_, _) => GridVisibleChanged?.Invoke();
        _viewLog.Click += (_, _) => Guard(_logDock.Toggle);
        _viewTree.CheckedChanged += (_, _) => _leftSplit.Visible = _splitter.Visible = _viewTree.Checked;
        _floatKeys = new FloatKeyFilter(this);
        Application.AddMessageFilter(_floatKeys);
        InitSettings();
        UpdateUi();
        UpdateTitle();
        RestoreMainBounds();
    }

    /// <summary>Layout files live here; QUARTERMASTER_LAYOUT_ROOT redirects them (used by smoke runs).</summary>
    static string LayoutRoot =>
        Environment.GetEnvironmentVariable("QUARTERMASTER_LAYOUT_ROOT") is { Length: > 0 } r ? r
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quartermaster");

    void ApplyLeftSplit()
    {
        if (!_leftSplitReady || _leftSplit.Height < 100) return;
        var d = (int)Math.Round(_leftSplit.Height * _leftFraction);
        d = Math.Clamp(d, _leftSplit.Panel1MinSize, Math.Max(_leftSplit.Panel1MinSize, _leftSplit.Height - _leftSplit.Panel2MinSize - _leftSplit.SplitterWidth));
        if (_leftSplit.SplitterDistance == d) return;
        _applyingSplit = true;
        try { _leftSplit.SplitterDistance = d; }
        finally { _applyingSplit = false; }
    }

    void RememberLeftSplit()
    {
        if (!_leftSplitReady || _applyingSplit || _leftSplit.Height < 100) return;
        _leftFraction = Math.Clamp((double)_leftSplit.SplitterDistance / _leftSplit.Height, WindowLayoutStore.MinLeftSplit, WindowLayoutStore.MaxLeftSplit);
    }

    /// <summary>Opens the workbench and runs the action on it (a no-op without a module).</summary>
    void ShowWorkbenchItem(Action<WorkbenchWindow> act)
    {
        ShowWorkbench();
        if (_benches.TryGetValue("workbench", out var w) && !w.IsDisposed && w is WorkbenchWindow wb)
        {
            wb.Activate();
            act(wb);
        }
    }

    void RestoreMainBounds()
    {
        if (_wm.LoadMain() is not { } m) return;
        WindowManager.ApplyBounds(this, m.Bounds, m.Dpi);
        if (m.Maximized) WindowState = FormWindowState.Maximized;
    }

    // ---------- construction ----------

    ToolStripMenuItem Item(ToolStripItemCollection into, string text, Action act, string? cmd = null, bool needsModule = false, bool needsGame = false)
    {
        var mi = new ToolStripMenuItem(text);
        if (cmd != null) _cmdItems[cmd] = mi;
        mi.Click += (_, _) => Guard(act);
        into.Add(mi);
        if (needsModule) _needsModule.Add(mi);
        if (needsGame) _needsGame.Add(mi);
        return mi;
    }

    ToolStripButton Tool(string id, string text, Action act, bool needsModule = false, bool needsGame = false)
    {
        var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, Name = id };
        b.Click += (_, _) => Guard(act);
        _toolbar.Items.Add(b);
        if (needsModule) _needsModule.Add(b);
        if (needsGame) _needsGame.Add(b);
        return b;
    }

    void BuildMenu()
    {
        var file = new ToolStripMenuItem("&File");
        Item(file.DropDownItems, "&Open Module...", OpenModuleDialog, "file.openModule");
        file.DropDownItems.Add(new ToolStripSeparator());
        Item(file.DropDownItems, "&New Game", NewGame, "file.newGame", needsModule: true);
        Item(file.DropDownItems, "Load &Setup...", LoadSetupDialog, needsModule: true);
        Item(file.DropDownItems, "Open &Game...", OpenGameDialog, "file.openGame", needsModule: true);
        Item(file.DropDownItems, "Open Auto&save...", OpenAutosaveDialog, needsModule: true);
        file.DropDownItems.Add(new ToolStripSeparator());
        Item(file.DropDownItems, "&Save Game", () => SaveGame(false), "file.saveGame", needsGame: true);
        Item(file.DropDownItems, "Save Game &As...", () => SaveGame(true), "file.saveGameAs", needsGame: true);
        file.DropDownItems.Add(new ToolStripSeparator());
        Item(file.DropDownItems, "New &Rules Pack...", NewRulesPack, needsModule: true);
        Item(file.DropDownItems, "Open Rules &Pack...", OpenRulesDialog, needsModule: true);
        _needsRules.Add(Item(file.DropDownItems, "Save Rules Pac&k", () => SaveRulesPack(false)));
        _needsRules.Add(Item(file.DropDownItems, "Save Rules Pack A&s...", () => SaveRulesPack(true)));
        file.DropDownItems.Add(new ToolStripSeparator());
        Item(file.DropDownItems, "&Back to launcher", BackToLauncher).Visible = Program.LauncherHook != null;
        Item(file.DropDownItems, "E&xit", Close);

        var editMenu = BuildEditMenu();

        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.Add(_viewTree);
        Item(view.DropDownItems, "&Diagnostics", ShowDiagnostics, "view.diagnostics", needsModule: true);
        view.DropDownItems.Add(_showGrid);
        _needsGame.Add(_viewLog);
        view.DropDownItems.Add(_viewLog);
        view.DropDownItems.Add(_viewPhases);
        _needsGame.Add(_viewOutliner);
        view.DropDownItems.Add(_viewOutliner);
        _needsRules.Add(Item(view.DropDownItems, "&Ledger", ShowLedger, "view.ledger"));
        Item(view.DropDownItems, "&Overview", ShowOverview, needsGame: true);
        _viewPalette.Click += (_, _) => Guard(TogglePalette);
        _needsGame.Add(_viewPalette);
        view.DropDownItems.Add(_viewPalette);

        var tools = _toolsMenu = new ToolStripMenuItem("&Tools");
        Item(tools.DropDownItems, "&Rules Workbench", ShowWorkbench, "tools.workbench", needsModule: true);
        Item(tools.DropDownItems, "&Inventory", ShowInventory, needsGame: true);
        Item(tools.DropDownItems, "&Notes", ShowNotes, needsGame: true);

        var window = new ToolStripMenuItem("&Window");
        Item(window.DropDownItems, "&Cascade", () => Arrange(MdiLayout.Cascade));
        Item(window.DropDownItems, "Tile &Horizontally", () => Arrange(MdiLayout.TileHorizontal));
        Item(window.DropDownItems, "Tile &Vertically", () => Arrange(MdiLayout.TileVertical));
        Item(window.DropDownItems, "Close &All", CloseAllWindows);
        window.DropDownItems.Add(new ToolStripSeparator());
        _popOutActive.Click += (_, _) => Guard(() => { if (ActiveMdiChild is { } a) _wm.PopOut(a); });
        window.DropDownItems.Add(_popOutActive);
        window.DropDownItems.Add(_floatingMenu);
        window.DropDownItems.Add(new ToolStripSeparator());
        window.DropDownOpening += (_, _) => _popOutActive.Enabled = ActiveMdiChild is { IsDisposed: false };

        var help = new ToolStripMenuItem("&Help");
        Item(help.DropDownItems, "Interface &Tour", ShowTour, null, needsGame: true);
        Item(help.DropDownItems, "&About", () => Info("Quartermaster\nA VASSAL module reader and engine.", "About Quartermaster"));

        _menu.Items.AddRange(new ToolStripItem[] { file, editMenu, view, tools, window, help });
        _menu.MdiWindowListItem = window;
    }

    void BuildToolbar()
    {
        Tool("tool.openModule", "Open Module", OpenModuleDialog);
        Tool("tool.newGame", "New Game", NewGame, needsModule: true);
        Tool("tool.loadSetup", "Load Setup", LoadSetupDialog, needsModule: true);
        Tool("tool.openGame", "Open Game", OpenGameDialog, needsModule: true);
        Tool("tool.saveGame", "Save Game", () => SaveGame(false), needsGame: true);
        _toolbar.Items.Add(new ToolStripSeparator());
        Tool("tool.diagnostics", "Diagnostics", ShowDiagnostics, needsModule: true);
        Tool("tool.notes", "Notes", ShowNotes, needsGame: true);
        Tool("tool.overview", "Overview", ShowOverview, needsGame: true);
        Tool("tool.inventory", "Inventory", ShowInventory, needsGame: true);
        Tool("tool.palette", "Palette", TogglePalette, needsGame: true); // shown in game master mode only (ApplySettings)
        Tool("tool.los", "LOS", () => { if (_handlers.TryGetValue("tool.los", out var h)) h(); }, needsGame: true);
        BuildHistoryToolbar();
        _dice = new DiceTools(this, () => _session, Guard, _toolbar, _toolsMenu!, _sbMessage, _needsGame);
        _dice.Actions = () => Actions;
        SetCommandHandler("dice.1d6", () => _dice!.RollSpec(new DiceSpec(1, 6, 0), "Roll"));
        SetCommandHandler("dice.2d6", () => _dice!.RollSpec(new DiceSpec(2, 6, 0), "Roll"));
        SetCommandHandler("turn.next", NextTurnCommand);
        _toolsMenu!.DropDownItems.Add(new ToolStripSeparator());
        Item(_toolsMenu.DropDownItems, "&Settings...", ShowSettings, "tools.settings");
    }

    void BuildStatus()
    {
        _sbMessage.Click += (_, _) =>
        {
            if (_problemCount > 0) ShowDiagnostics();
        };
        _status.Items.AddRange(new ToolStripItem[] { _sbModule, _sbMessage, _sbTiling, _sbPlayer, _dice!.TurnLabel, _sbLocation, _sbZoom });
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
        FlushNotes(); // pending note text becomes a command first, so the dirty check below counts it
        if (!_dirty || Program.Smoke) return true;
        return MessageBox.Show(this, "The current game has unsaved changes. Discard them?", "Quartermaster",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    void UpdateUi()
    {
        foreach (var i in _needsModule) i.Enabled = _session != null;
        foreach (var i in _needsGame) i.Enabled = _session?.Game != null;
        foreach (var i in _needsRules) i.Enabled = _session?.Rules != null;
        _dice?.Refresh();
        UpdatePhaseUi();
        RefreshHistoryUi();
        UpdatePlayerButton();
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
                else if (_recoveredAutosave) t += " — (recovered autosave)";
                if (_dirty) t += "*";
            }
            if (_session.Rules is { } r) t += $" — rules: {r.Name}{(_session.RulesDirty ? "*" : "")}";
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
    bool OpenModule(string path, bool fromArgs, LaunchRequest? launch = null)
    {
        if (!ConfirmDiscard() || !ConfirmRulesSaved()) return false;
        ModuleSession next;
        string? moduleHash;
        var prevWait = Application.UseWaitCursor;
        Application.UseWaitCursor = true;
        Cursor.Current = Cursors.WaitCursor;
        try
        {
            next = ModuleSession.Open(path);
            moduleHash = WindowLayoutStore.HashFile(path);
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
        _wm.BeginModule(moduleHash);
        _savePath = null;
        _dirty = false;
        _session.Log.Added += OnLogAdded;
        ReportSettingsProblems(_session);
        _session.GameReplaced += OnGameReplaced;
        _session.RulesChanged += OnRulesChanged;
        _session.ViewChanged += OnViewChanged;
        var opened = _session;
        _tree.Load(_session.Model, id => opened.CanSeeMap(id));
        _tablesTree.Bind(_session);
        _logDock.Create(_session);
        _logDock.Panel?.ApplySettings(_settings.Current);
        _paletteDock.Create(_session);
        _paletteDock.SetGameMaster(GameMaster);
        CreatePhases(_session);
        CreateOutliner(_session);
        _sbModule.Text = $"{_session.Model.Name} {_session.Model.Version}".Trim();
        _sbLocation.Text = "-";
        _sbZoom.Text = "-";
        RefreshProblemNotice();
        UpdateUi();
        UpdateTitle();

        if (launch != null) Guard(() => StartGameForLaunch(_session, launch));
        else Guard(() => StartGameFor(_session, fromArgs));
        if (fromArgs) Guard(() => LoadRulesFromArgs(_session));
        OpenMainMap();
        if (_opts.FloatMainMap && Program.Smoke && FirstMap() is { } fm) _wm.PopOut(fm);
        MaybeAutoTour();
        OpenedModulePath = path;
        RememberModule(path);
        if (!Program.Smoke || _opts.SmokeLauncher) Guard(() => LaunchBookkeeping.ModuleOpened(_library, path, DateTimeOffset.UtcNow));
        return true;
    }

    /// <summary>Starts the game the launcher asked for: a new game (or setup), a saved game, or the newest autosave.</summary>
    void StartGameForLaunch(ModuleSession s, LaunchRequest launch)
    {
        // Same guard as Play offline's New game: a remembered pack that moved (or is already loaded) is skipped silently.
        if (launch.RulesPath is { Length: > 0 } rules && !string.Equals(s.RulesPath, rules, StringComparison.OrdinalIgnoreCase)
            && (File.Exists(rules) || Directory.Exists(rules)))
        {
            try { s.OpenRules(rules); } catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("launcher: rules pack not loaded: " + ex.Message); }
        }
        switch (launch.Kind)
        {
            case LaunchKind.NewGame when launch.NewGame is { } ng:
                var players = ng.Players is { Count: >= 2 } ? ng.Players : null;
                if (ng.Setup is { } setup) s.LoadSetup(setup, ng.Difficulty, players); else s.StartNewGame(ng.Difficulty, players);
                if (ng.Difficulty != _settings.Current.LastDifficulty) _settings.Update(cur => cur with { LastDifficulty = ng.Difficulty });
                break;
            case LaunchKind.OpenSave or LaunchKind.Continue when !string.IsNullOrEmpty(launch.SavePath):
                LoadSavedGame(s, launch.SavePath);
                break;
            default:
                ChooseAndStart(s, preselectNew: false);
                break;
        }
    }

    /// <summary>Loads a native save into the session and tracks it as the current file (autosaves are never a Save target).</summary>
    void LoadSavedGame(ModuleSession s, string path)
    {
        s.LoadNative(path);
        _recoveredAutosave = Autosaver.IsAutosavePath(path, LayoutRoot);
        _savePath = _recoveredAutosave ? null : path;
        _dirty = false;
        if (!_recoveredAutosave) NoteRecentSave(path);
        UpdateTitle();
    }

    void NoteRecentSave(string path)
    {
        if (Program.Smoke) return;
        Guard(() => LaunchBookkeeping.SaveUsed(_recent, _session?.Archive.Sha256, path));
    }

    void BackToLauncher()
    {
        _backToLauncher = true;
        try { Close(); }
        finally { _backToLauncher = false; }
    }

    void StartGameFor(ModuleSession s, bool fromArgs)
    {
        if (fromArgs && _opts.SetupIndex is { } idx)
        {
            if (idx < s.Model.Setups.Count) s.LoadSetup(s.Model.Setups[idx], _opts.Difficulty);
            else
            {
                Program.AppLog($"--setup {idx} is out of range (module has {s.Model.Setups.Count}); starting a new game.");
                if (Program.Smoke) Program.ExitCode = 1;
                s.StartNewGame(_opts.Difficulty);
            }
        }
        else if (fromArgs && _opts.NewGame) s.StartNewGame(_opts.Difficulty);
        else if (Program.Smoke)
        {
            Program.AppLog("Smoke mode: setup chooser skipped; starting a new game.");
            s.StartNewGame(_opts.Difficulty ?? Difficulty.SemiAuto);
        }
        else ChooseAndStart(s, preselectNew: false);
    }

    MapWindow? FirstMap() => _maps.Values.FirstOrDefault(w => !w.IsDisposed);

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
        var w = new MapWindow(_session, map) { Host = this };
        _maps[map.Id] = w;
        w.FormClosed += (_, _) => { if (_maps.TryGetValue(map.Id, out var cur) && cur == w) _maps.Remove(map.Id); };
        ShowDoc(w, "map:" + map.Id);
        return w;
    }

    void CloseSession()
    {
        _tour?.Close();
        _wm.EndModule(); // layout is written before the windows go away
        _logDock.Destroy();
        _paletteDock.Destroy();
        DestroyOutliner();
        DestroyPhases();
        CloseAllWindows();
        if (_session == null) return;
        _session.Log.Added -= OnLogAdded;
        _session.GameReplaced -= OnGameReplaced;
        _session.RulesChanged -= OnRulesChanged;
        _session.ViewChanged -= OnViewChanged;
        _session.Dispose();
        _session = null;
        _tree.Clear();
        _tablesTree.Bind(null);
    }

    // ---------- log panel (see LogDock) ----------

    /// <summary>Opens or activates the piece's map and scrolls it to the piece (used by double-clicking a log row).</summary>
    public void CentreOnPiece(string pieceId)
    {
        if (_session?.View is not { } seen || !seen.Pieces.TryGetValue(pieceId, out var piece)) return;   // only pieces this player may see
        var map = _session.Model.Maps.FirstOrDefault(m => m.Id == piece.MapId) ?? _session.Model.Maps.FirstOrDefault(m => m.Name == piece.MapId);
        if (map == null || OpenMap(map, false) is not { } w) return;
        w.Canvas.CentreOn(piece.Pos);
        w.Canvas.SelectOnly(pieceId);
    }

    void CloseAllWindows()
    {
        foreach (var c in MdiChildren) c.Close();
        _wm.CloseFloating();
    }

    void OnGameReplaced()
    {
        _tour?.Close();
        if (_session?.Game is { } g)
        {
            g.Changed += _ => { _dirty = true; Interlocked.Increment(ref _changeSeq); UpdateTitle(); _dice?.Refresh(); RequestHistoryRefresh(); };
            g.Rewound += () => { _dirty = true; Interlocked.Increment(ref _changeSeq); UpdateTitle(); _dice?.Refresh(); RequestHistoryRefresh(); };
        }
        _dirty = false;
        _autosavedSeq = _changeSeq;
        _savePath = null;
        _recoveredAutosave = false;
        UpdateUi();
        UpdateTitle();
        ReconcilePhases();
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
        _diag = new DiagnosticsWindow(_session.Log);
        _diag.FormClosed += (_, _) => { _diag = null; };
        ShowDoc(_diag, "diag");
    }

    // ---------- game commands ----------

    void NewGame()
    {
        if (_session == null || !ConfirmDiscard()) return;
        if (Program.Smoke) { _session.StartNewGame(_opts.Difficulty ?? Difficulty.SemiAuto); return; }
        ChooseAndStart(_session, preselectNew: true);
    }

    /// <summary>Shows the setup chooser (with the difficulty choice), remembers the difficulty, and starts the chosen game.</summary>
    void ChooseAndStart(ModuleSession s, bool preselectNew, SetupDef? preselect = null)
    {
        using var dlg = new SetupChooserDialog(s.Model, GameMode.Offline, _settings.Current.LastDifficulty, s.Rules, preselectNew, preselect);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Request is not { } req) return;
        if (req.Difficulty != _settings.Current.LastDifficulty) _settings.Update(cur => cur with { LastDifficulty = req.Difficulty });
        // The players are part of the game's start (not an undoable command); two or more bring up the first handover screen.
        if (req.Setup is { } setup) s.LoadSetup(setup, req.Difficulty, req.Players); else s.StartNewGame(req.Difficulty, req.Players);
    }

    void LoadSetupDialog()
    {
        if (_session == null || !ConfirmDiscard()) return;
        ChooseAndStart(_session, preselectNew: false);
    }

    void OpenGameDialog() => OpenGameCore(autosaves: false);

    void OpenAutosaveDialog() => OpenGameCore(autosaves: true);

    void OpenGameCore(bool autosaves)
    {
        if (_session == null || Program.Smoke || !ConfirmDiscard()) return;
        using var dlg = new OpenFileDialog
        {
            Filter = "Quartermaster saves (*.qsave)|*.qsave|All files (*.*)|*.*",
            Title = autosaves ? "Open Autosave" : "Open Game",
        };
        if (autosaves)
        {
            var root = Autosaver.AutosaveRoot(LayoutRoot);
            var mine = _session.Archive.Sha256 is { Length: > 0 } sha ? Path.Combine(root, sha) : null;
            dlg.InitialDirectory = mine != null && Directory.Exists(mine) ? mine : Directory.Exists(root) ? root : "";
        }
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        // A file inside the autosave folder is pruned automatically, so it must never become the target of Save.
        LoadSavedGame(_session, dlg.FileName);
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
            if (Autosaver.IsAutosavePath(path, LayoutRoot))
            {
                ShowError("Saves inside the autosave folder are deleted automatically. Choose another folder.");
                return;
            }
        }
        _session.SaveNative(path);
        _savePath = path;
        _recoveredAutosave = false;
        _dirty = false;
        _autosavedSeq = _changeSeq;
        _autosaver?.Reset(DateTimeOffset.UtcNow);
        NoteRecentSave(path);
        UpdateTitle();
    }

    /// <summary>The player's view changed (raised on the thread that made the change): the module tree hides maps the player can't see.</summary>
    void OnViewChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(() => { if (!IsDisposed) { Guard(_tree.RefreshVisibility); UpdatePlayerButton(); } });
        else { Guard(_tree.RefreshVisibility); UpdatePlayerButton(); }
    }

    // ---------- rules packs ----------

    void OnRulesChanged()
    {
        if (IsDisposed) return;
        UpdateUi();
        UpdateTitle();
        ReconcilePhases();
    }

    void LoadRulesFromArgs(ModuleSession s)
    {
        if (_opts.RulesPath != null) s.OpenRules(_opts.RulesPath);
        else if (_opts.NewRulesName != null) s.NewRules(_opts.NewRulesName);
    }

    /// <summary>Offers to save an edited rules pack. Returns false when the user cancels (or cancels the save).</summary>
    bool ConfirmRulesSaved()
    {
        if (_session is not { RulesDirty: true } || Program.Smoke) return true;
        var r = MessageBox.Show(this, $"Save changes to the rules pack '{_session.Rules?.Name}'?", "Quartermaster",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.No) return true;
        try { return SaveRulesPack(false); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError(ex.Message);
            return false;
        }
    }

    void NewRulesPack()
    {
        if (_session == null || Program.Smoke || !ConfirmRulesSaved()) return;
        var name = InputDialog.Ask(this, "New Rules Pack", "Name of the new rules pack:", "Rules");
        if (!string.IsNullOrWhiteSpace(name)) _session.NewRules(name.Trim());
    }

    void OpenRulesDialog()
    {
        if (_session == null || Program.Smoke || !ConfirmRulesSaved()) return;
        using var dlg = new OpenFileDialog { Filter = "Rules packs (*.qrules)|*.qrules|All files (*.*)|*.*", Title = "Open Rules Pack" };
        if (dlg.ShowDialog(this) == DialogResult.OK) _session.OpenRules(dlg.FileName);
    }

    /// <summary>Saves the pack; returns false when the user cancelled the file dialog.</summary>
    bool SaveRulesPack(bool saveAs)
    {
        if (_session?.Rules == null) return true;
        string? path = null;
        if (saveAs || _session.RulesPath == null)
        {
            if (Program.Smoke) return true;
            using var dlg = new SaveFileDialog { Filter = "Rules packs (*.qrules)|*.qrules", DefaultExt = "qrules", Title = "Save Rules Pack", FileName = _session.Rules.Name };
            if (dlg.ShowDialog(this) != DialogResult.OK) return false;
            path = dlg.FileName;
        }
        _session.SaveRules(path);
        return true;
    }

    /// <summary>Opens (or activates) a module document and jumps to a 1-based page.</summary>
    public void OpenDocumentAtPage(string documentEntry, int page)
    {
        if (_session == null) return;
        var doc = _session.Model.Documents.FirstOrDefault(d => d.EntryName == documentEntry);
        if (doc == null)
        {
            ShowError($"The rule reference points to '{documentEntry}', which is not in this module.");
            return;
        }
        Guard(() =>
        {
            OpenChild(_docs, doc.EntryName, () => new PdfWindow(_session, doc, page));
            if (_docs.TryGetValue(doc.EntryName, out var w) && !w.IsDisposed && w is PdfWindow pdf)
            {
                pdf.GoToPage(page);
                pdf.Activate();
            }
        });
    }


    /// <summary>Opens (or activates) the ledger window; needs a loaded rules pack.</summary>
    void ShowLedger()
    {
        if (_session?.Rules == null) return;
        var s = _session;
        OpenChild(_ledgers, "ledger", () => new LedgerWindow(s, () => Actions));
    }

    /// <summary>Opens (or activates) the single rules workbench of this session.</summary>
    void ShowWorkbench()
    {
        if (_session == null) return;
        var s = _session;
        OpenChild(_benches, "workbench", () => new WorkbenchWindow(s, () => Guard(NewRulesPack), () => Guard(OpenRulesDialog)));
    }



    void OnNodeActivated(WindowTreeNode n)
    {
        if (_session == null) return;
        if (n.Kind == WindowNodeKind.Setup && n.Ref is SetupDef s)
        {
            if (!ConfirmDiscard()) return;
            // Same difficulty choice as File > Load Setup (default = last used); this setup is preselected.
            if (Program.Smoke) Guard(() => _session.LoadSetup(s, _opts.Difficulty ?? _settings.Current.LastDifficulty));
            else Guard(() => ChooseAndStart(_session, preselectNew: false, preselect: s));
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
        open[key] = w;
        w.FormClosed += (_, _) => { if (open.TryGetValue(key, out var cur) && cur == w) open.Remove(key); };
        ShowDoc(w, (ReferenceEquals(open, _charts) ? "chart:" : ReferenceEquals(open, _ledgers) ? "ledger:" : ReferenceEquals(open, _benches) ? "bench:" : "doc:") + key);
    }

    /// <summary>Shows a new document window: floating at its remembered bounds, otherwise as a maximized MDI tab.</summary>
    void ShowDoc(Form w, string layoutKey)
    {
        _wm.Register(w, layoutKey);
        if (_wm.Saved(layoutKey) is { Floating: true } sv) { _wm.ShowFloating(w, sv); return; }
        PrepareDock(w);
        w.Show();
    }

    /// <summary>Makes a window an MDI child that fills the document area (browser feel).</summary>
    public void PrepareDock(Form w)
    {
        w.MdiParent = this;
        w.Size = new Size(720, 520);
        _maximizeDocs = true;
        w.WindowState = FormWindowState.Maximized;
    }

    public bool IsActiveDocument(Form w) => ReferenceEquals(ActiveMdiChild, w) || ReferenceEquals(ActiveForm, w);

    void RebuildFloatingMenu()
    {
        _floatingMenu.DropDownItems.Clear();
        foreach (var f in _wm.Floating)
        {
            var form = f;
            var sub = new ToolStripMenuItem(_wm.TitleOf(form));
            sub.DropDownItems.Add("&Activate", null, (_, _) => { if (!form.IsDisposed) form.Activate(); });
            sub.DropDownItems.Add("&Dock", null, (_, _) => Guard(() => _wm.Dock(form)));
            _floatingMenu.DropDownItems.Add(sub);
        }
        _floatingMenu.Visible = _floatingMenu.DropDownItems.Count > 0;
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

    public void SyncTabs(Form? closing) =>
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
        if (TryDispatchShortcut(keyData, ActiveMdiChild ?? this, Control.FromChildHandle(msg.HWnd))) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------- startup / shutdown ----------

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _leftFraction = _layoutStore.LoadLeftSplit();
        _leftSplitReady = true;
        ApplyLeftSplit();
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
        ReopenLastModuleIfWanted();
        if (_launch != null && !_startupDone)
        {
            _startupDone = true;
            BeginInvoke(new Action(() =>
            {
                if (!IsDisposed) OpenModule(_launch.ModulePath, fromArgs: false, _launch);
            }));
        }
        else if (_opts.ModulePath != null && !_startupDone)
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
        if (!e.Cancel && (!ConfirmDiscard() || !ConfirmRulesSaved())) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_backToLauncher) ReturnToLauncher = true; // the close really completed
        _shuttingDown = true;   // the handover screen goes without bringing hidden windows back
        if (_floatKeys != null) Application.RemoveMessageFilter(_floatKeys);
        _smokeTimer?.Dispose();
        _autosaveTimer?.Dispose();
        _handoverRetry?.Dispose();
        _dice?.Dispose();
        RememberLeftSplit();
        _layoutStore.SaveLeftSplit(_leftFraction);
        _wm.SaveMain(new SavedWindow(false, WindowState == FormWindowState.Normal ? Bounds : RestoreBounds, WindowState == FormWindowState.Maximized, DeviceDpi));
        CloseSession();
        base.OnFormClosed(e);
    }
}
