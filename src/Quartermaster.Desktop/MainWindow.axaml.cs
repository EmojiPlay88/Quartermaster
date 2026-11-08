using System.Xml;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Map;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop;

/// <summary>
/// The main window shell, ported from WinForms MainForm (stage 1): File / Edit / View menus, one tab per visible map, the counter
/// palette, open/save of native games, the unsaved-changes prompt and the command line. Every dialog goes through <see cref="IDialogs"/>.
/// The public methods (<see cref="OpenModule"/>, <see cref="StartSetup"/>, <see cref="SaveGame"/>, <see cref="OpenGame"/>) act at once,
/// without questions; the menu commands ask first, as WinForms.
/// </summary>
public partial class MainWindow : Window
{
    const string Caption = "Quartermaster";
    const string UnsavedText = "The current game has unsaved changes. Discard them?";
    const string AbandonReplayText = "Abandon this replay?";
    /// <summary>WinForms MainForm.History.FloorTip.</summary>
    internal const string FloorTip = "Undo stops here: this game was loaded from a save without undo history.";
    static readonly FileFilter ModuleFilter = new("VASSAL modules (*.vmod)", "*.vmod");
    static readonly FileFilter SaveFilter = new("Quartermaster saves (*.qsave)", "*.qsave");
    static readonly FileFilter AllFiles = new("All files (*.*)", "*");

    readonly IDialogs _dialogs;
    readonly string _cacheRoot;
    readonly string? _rulesRoot;
    readonly CommandRouter _router = new();
    readonly List<MenuItem> _needsModule = new();
    readonly List<MenuItem> _needsGame = new();
    readonly List<(string Id, MenuItem Item)> _gated = new();   // menu items whose state comes from CommandAvailability
    static readonly FileFilter RulesFilter = new("Rules packs (*.qrules)", "*.qrules");
    readonly MenuItem _showGrid;
    MenuItem? _editUndo, _editRedo;

    ModuleSession? _session;
    PhaseController? _phases;
    HistoryController? _history;
    ActionPortSwitch? _actions;
    GameSession? _hookedGame;
    string? _savePath;
    bool _recoveredAutosave;   // the open game came from an autosave file; Save goes to Save As
    bool _dirty;
    bool _historyRefreshQueued;
    bool _closeConfirmed, _closeAsking;
    Difficulty _lastDifficulty = Difficulty.SemiAuto;

    /// <summary>The app's window: real dialogs, the default cache and remembered-rules folders.</summary>
    public MainWindow() : this(new Dialogs(), ModuleSession.DefaultCacheRoot, RulesMemory.DefaultRoot) { }

    /// <param name="dialogs">Every dialog the window shows (tests pass a fake).</param>
    /// <param name="cacheRoot">Where module caches (tiles) go (<see cref="ModuleSession.Open(string, string, string?)"/>).</param>
    /// <param name="rulesRoot">The remembered-rules index; null disables remembering.</param>
    public MainWindow(IDialogs dialogs, string cacheRoot, string? rulesRoot)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _cacheRoot = cacheRoot;
        _rulesRoot = rulesRoot;
        InitializeComponent();
        _showGrid = new MenuItem { Header = MenuText.FromWinForms("Show &Grid"), ToggleType = MenuItemToggleType.CheckBox };
        _router.Guard = Guard;
        RegisterCommands();
        BuildMenu();
        _router.Attach(this);

        Maps.StatusChanged += (view, loc, zoom) =>
        {
            if (!ReferenceEquals(view, Maps.ActiveView)) return;
            if (loc != null) SbLocation.Text = loc;
            SbZoom.Text = zoom;
        };
        Maps.DropMessage += SetStatus;
        Maps.TilingProgress += (view, f) => { if (ReferenceEquals(view, Maps.ActiveView)) SetTilingProgress(f); };
        Maps.ActiveViewChanged += () => SetTilingProgress(Maps.ActiveView?.Preparing);
        UpdateUi();
        UpdateTitle();
    }

    // ---------- public surface ----------

    /// <summary>The open module (null before one is opened).</summary>
    public ModuleSession? Session => _session;

    /// <summary>The action port every game change goes through (WinForms MainForm.Actions): <see cref="LocalActions"/> behind a switch.</summary>
    public IActionPort? Actions => _actions;

    /// <summary>Undo / redo history of the open module's game.</summary>
    public HistoryController? History => _history;

    /// <summary>Command ids (CommandDefaults) with their shortcuts and handlers; the window's KeyDown goes through it.</summary>
    public CommandRouter Commands => _router;

    /// <summary>The map tabs.</summary>
    public MapTabs MapTabs => Maps;

    /// <summary>The counter palette.</summary>
    public PaletteDock Palette => PaletteHost;

    /// <summary>True when closing would ask (WinForms ConfirmDiscard): a savable game changed since it was started, opened or saved.</summary>
    public bool HasUnsavedChanges => _dirty && _session is { Game: not null, CanSave: true };

    /// <summary>The current file of the game (null until saved or opened, or for a recovered autosave).</summary>
    public string? SavePath => _savePath;

    /// <summary>Status bar texts.</summary>
    public string StatusText => SbMessage.Text ?? "";
    public string LocationText => SbLocation.Text ?? "";
    public string ZoomText => SbZoom.Text ?? "";
    public string ModuleText => SbModule.Text ?? "";
    internal bool StatusTilingVisible => SbTiling.IsVisible;

    /// <summary>Opens a module (no game yet) and shows its maps and palette. On failure shows the WinForms message and keeps everything as
    /// it was. No question is asked: the caller decides about unsaved changes.</summary>
    public bool OpenModule(string path)
    {
        ModuleSession next;
        try
        {
            next = ModuleSession.Open(path, _cacheRoot, _rulesRoot);
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException or UnauthorizedAccessException)
        {
            ShowError($"Could not open '{path}':\n{ex.Message}");
            return false;
        }

        CloseSession();
        _session = next;
        _savePath = null;
        _recoveredAutosave = false;
        _dirty = false;
        next.GameReplaced += OnGameReplacedAnyThread;
        next.ViewChanged += OnViewChangedAnyThread;
        next.RulesChanged += OnRulesChangedAnyThread;
        CreatePhases(next);
        Maps.Bind(next, () => _actions);
        PaletteHost.Bind(next);
        SbModule.Text = $"{next.Model.Name} {next.Model.Version}".Trim();
        SbLocation.Text = "-";
        SbZoom.Text = "-";
        SetStatus("");
        HookGame();
        UpdateUi();
        UpdateTitle();
        return true;
    }

    /// <summary>
    /// Starts a game: the module's setup at <paramref name="index"/>, or a new game from the module for null (command line --new).
    /// An index out of range starts a new game and logs a warning, as WinForms --setup.
    /// </summary>
    public void StartSetup(int? index, Difficulty? difficulty = null)
    {
        var s = _session ?? throw new InvalidOperationException("Open a module first.");
        if (index is not { } idx) s.StartNewGame(difficulty);
        else if (idx >= 0 && idx < s.Model.Setups.Count) s.LoadSetup(s.Model.Setups[idx], difficulty);
        else
        {
            s.Log.Add(DiagnosticSeverity.Warning, "startup", $"--setup {idx} is out of range (module has {s.Model.Setups.Count}); starting a new game.");
            s.StartNewGame(difficulty);
        }
    }

    /// <summary>Saves the game to <paramref name="path"/> (native .qsave, the WinForms format) and makes it the current file.</summary>
    public void SaveGame(string path)
    {
        var s = _session ?? throw new InvalidOperationException("There is no game to save.");
        s.SaveNative(path);
        _savePath = path;
        _recoveredAutosave = false;
        _dirty = false;
        UpdateTitle();
    }

    /// <summary>Loads a native save (WinForms or Desktop) into the open module and tracks it as the current file (autosaves never are).</summary>
    public void OpenGame(string path)
    {
        var s = _session ?? throw new InvalidOperationException("Open a module first.");
        s.LoadNative(path);
        _recoveredAutosave = Autosaver.IsAutosavePath(path, AppDataRoot.Path);
        _savePath = _recoveredAutosave ? null : path;
        _dirty = false;
        UpdateTitle();
    }

    /// <summary>
    /// The command line, as WinForms StartGameFor: opens the module, then the setup (--setup), a new game (--new), or the setup chooser.
    /// </summary>
    public void ApplyStartup(StartupArgs args)
    {
        if (args.ModulePath is not { } path || !OpenModule(path)) return;
        Guard(() => LoadRulesFromArgs(args));   // as WinForms: the pack is in place before the first game starts
        if (args.SetupIndex is { } idx) Guard(() => StartSetup(idx));
        else if (args.NewGame) Guard(() => StartSetup(null));
        else RunAsync(() => ChooseAndStartAsync(preselectNew: false));
    }

    /// <summary>The menu bar in WinForms notation: top menu label → its item labels (separators skipped), in order.</summary>
    public OrderedDictionary<string, IReadOnlyList<string>> MenuLabels()
    {
        var result = new OrderedDictionary<string, IReadOnlyList<string>>();
        foreach (var top in MainMenu.Items.OfType<MenuItem>())
            result[MenuText.ToWinForms((string)top.Header!)] = top.Items.OfType<MenuItem>().Select(i => MenuText.ToWinForms((string)i.Header!)).ToList();
        return result;
    }

    /// <summary>The menu item with this WinForms label (exact, or the first that starts with it when <paramref name="prefix"/>), any depth.</summary>
    public MenuItem? FindMenuItem(string label, bool prefix = false)
    {
        static IEnumerable<MenuItem> All(ItemsControl c) => c.Items.OfType<MenuItem>().SelectMany(i => new[] { i }.Concat(All(i)));
        return All(MainMenu).FirstOrDefault(i => i.Header is string h && (prefix ? MenuText.ToWinForms(h).StartsWith(label, StringComparison.Ordinal) : MenuText.ToWinForms(h) == label));
    }

    /// <summary>Clicks a menu item as the user would (tests).</summary>
    internal void ClickMenu(string label)
    {
        var mi = FindMenuItem(label) ?? throw new ArgumentException("no menu item " + label);
        if (!mi.IsEffectivelyEnabled) return;
        // As Avalonia's DefaultMenuInteractionHandler.Click: a check item flips first, then Click is raised.
        if (mi.ToggleType == MenuItemToggleType.CheckBox && mi.ItemCount == 0) mi.IsChecked = !mi.IsChecked;
        mi.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    /// <summary>Closes without asking (tests' cleanup).</summary>
    internal void ForceClose()
    {
        _closeConfirmed = true;
        Close();
    }

    void LoadRulesFromArgs(StartupArgs args)
    {
        if (_session is not { } s) return;
        if (args.RulesPath != null) s.OpenRules(args.RulesPath);
        else if (args.NewRulesName != null) s.NewRules(args.NewRulesName);
    }

    // ---------- menus and commands ----------

    void RegisterCommands()
    {
        _router.SetHandler("file.openModule", () => RunAsync(OpenModuleDialogAsync));
        _router.SetHandler("file.newGame", () => Available(Avail("file.newGame"), () => RunAsync(NewGameAsync)));
        _router.SetHandler("file.openGame", () => Available(Avail("file.openGame"), () => RunAsync(OpenGameDialogAsync)));
        _router.SetHandler("file.saveGame", () => Available(Avail("file.saveGame"), () => RunAsync(() => SaveGameAsync(false))));
        _router.SetHandler("file.saveGameAs", () => Available(Avail("file.saveGameAs"), () => RunAsync(() => SaveGameAsync(true))));
    }

    /// <summary>Whether a gated command is available now (<see cref="CommandAvailability"/>, the WinForms rules).</summary>
    bool Avail(string id) => CommandAvailability.For(_session).TryGetValue(id, out var a) && a.Enabled;

    /// <summary>Runs <paramref name="act"/> when the command is available (its menu item enabled); otherwise lets the key press through.</summary>
    static bool Available(bool enabled, Action act)
    {
        if (!enabled) return false;
        act();
        return true;
    }

    MenuItem Item(MenuItem into, string label, Action act, string? cmd = null, bool needsModule = false, bool needsGame = false, string? gate = null)
    {
        var mi = new MenuItem { Header = MenuText.FromWinForms(label) };
        if (cmd != null)
        {
            mi.InputGesture = _router.Gesture(cmd);   // display only: the CommandRouter dispatches the key (no HotKey, so no double fire)
            mi.Click += (_, _) => _router.Execute(cmd);
        }
        else mi.Click += (_, _) => Guard(act);
        into.Items.Add(mi);
        if (needsModule) _needsModule.Add(mi);
        if (needsGame) _needsGame.Add(mi);
        gate ??= cmd;
        if (gate != null && CommandAvailability.Ids.Contains(gate)) _gated.Add((gate, mi));
        return mi;
    }

    static MenuItem Top(string label) => new() { Header = MenuText.FromWinForms(label) };

    void BuildMenu()
    {
        var file = Top("&File");
        Item(file, "&Open Module...", () => { }, "file.openModule");
        file.Items.Add(new Separator());
        Item(file, "&New Game", () => { }, "file.newGame");
        Item(file, "Load &Setup...", () => RunAsync(LoadSetupAsync), gate: "file.loadSetup");
        Item(file, "Open &Game...", () => { }, "file.openGame");
        file.Items.Add(new Separator());
        Item(file, "&Save Game", () => { }, "file.saveGame");
        Item(file, "Save Game &As...", () => { }, "file.saveGameAs");
        file.Items.Add(new Separator());
        Item(file, "New &Rules Pack...", NewRulesPack, gate: "file.newRules");
        Item(file, "Open Rules &Pack...", OpenRulesPack, gate: "file.openRules");
        Item(file, "Save Rules Pac&k", () => SaveRulesPack(false), gate: "file.saveRules");
        Item(file, "Save Rules Pack A&s...", () => SaveRulesPack(true), gate: "file.saveRulesAs");
        file.Items.Add(new Separator());
        Item(file, "E&xit", Close);

        // Edit (MainForm.History): Undo / Redo, captions follow the history ("&Undo Move …").
        var edit = Top("&Edit");
        _editUndo = Item(edit, "&Undo", () => { }, "edit.undo");
        _editRedo = Item(edit, "&Redo", () => { }, "edit.redo");
        edit.SubmenuOpened += (_, _) => RefreshHistoryUi();

        // View: MainForm "Show &Grid", the map context menu's "Zoom to Fit" / "Zoom 100%" and the map toolbar's "Zoom" presets.
        var view = Top("&View");
        // Avalonia's menu flips a CheckBox item's IsChecked itself on click (WinForms CheckOnClick); follow the flag.
        _showGrid.PropertyChanged += (_, e) => { if (e.Property == MenuItem.IsCheckedProperty) Maps.ShowGrid = _showGrid.IsChecked; };
        view.Items.Add(_showGrid);
        view.Items.Add(new Separator());
        Item(view, "Zoom to Fit", () => Maps.ActiveView?.ZoomToFit(), needsModule: true);
        Item(view, "Zoom 100%", () => Maps.ActiveView?.ZoomCentered(1f), needsModule: true);
        var zoom = Item(view, "Zoom", () => { }, needsModule: true);
        foreach (var pct in new[] { 25, 50, 100, 200 })
        {
            var f = pct / 100f;
            Item(zoom, pct + "%", () => Maps.ActiveView?.ZoomCentered(f));
        }
        Item(zoom, "Fit", () => Maps.ActiveView?.ZoomToFit());

        MainMenu.Items.Add(file);
        MainMenu.Items.Add(edit);
        MainMenu.Items.Add(view);
        RefreshHistoryUi();
    }

    void UpdateUi()
    {
        foreach (var i in _needsModule) i.IsEnabled = _session != null;
        foreach (var i in _needsGame) i.IsEnabled = _session?.Game != null;
        ApplyGates();
        var palette = _session?.Game != null;
        PaletteHost.IsVisible = PaletteSplitter.IsVisible = palette;
        Body.ColumnDefinitions[1].Width = new GridLength(palette ? 4 : 0);
        Body.ColumnDefinitions[2].Width = new GridLength(palette ? 220 : 0);
        RefreshHistoryUi();
    }

    /// <summary>Enabled state and the reason tooltip of every gated menu item (WinForms ApplyGates); other tooltips are left alone.</summary>
    void ApplyGates()
    {
        var av = CommandAvailability.For(_session);
        foreach (var (id, mi) in _gated)
        {
            var (enabled, reason) = av[id];
            mi.IsEnabled = enabled;
            if (!CommandAvailability.OwnsTooltip(id)) continue;
            if (reason != null) ToolTip.SetTip(mi, reason);
            else if (CommandAvailability.IsOwnText(ToolTip.GetTip(mi) as string)) ToolTip.SetTip(mi, null);
            ToolTip.SetShowOnDisabled(mi, true);
        }
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
                if (_dirty && _session.CanSave) t += "*";   // a guest or a viewer copy has nothing to save
                if (_session.IsGuest) t += " — online guest";
            }
            if (_session.Rules is { } r) t += $" — rules: {r.Name}{(_session.RulesDirty ? "*" : "")}";
        }
        Title = t;
    }

    /// <summary>Caption and enabled state of Undo / Redo (WinForms RefreshHistoryUi).</summary>
    void RefreshHistoryUi()
    {
        try
        {
            var s = _session;
            var guest = s?.IsGuest == true;
            var replaying = s?.IsReplaying == true;   // the sender's turn cannot be undone; nothing may change it mid-replay
            var h = s == null || guest || replaying ? null : _history;
            var undo = s != null && h != null && HistoryCommands.CanUndo(s, h);
            var redo = s != null && h != null && HistoryCommands.CanRedo(s, h);
            // Why an item is off (WinForms MainForm.History.RefreshHistoryUi).
            var why = guest ? ModuleSession.GuestUndoText : replaying ? ModuleSession.ReplayingText
                : h?.UndoBlockedBySentTurn == true ? HistoryController.AlreadySentText : h?.AtFloorFromOldSave == true ? FloorTip : null;
            Set(_editUndo, h?.UndoText ?? "Undo", undo, !undo ? why : null);
            Set(_editRedo, h?.RedoText ?? "Redo", redo, guest || replaying ? why : null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("refresh history ui: " + ex.Message);
        }

        static void Set(MenuItem? mi, string text, bool enabled, string? tip)
        {
            if (mi == null) return;
            ToolTip.SetTip(mi, tip);
            ToolTip.SetShowOnDisabled(mi, true);
            // "&" + the action label with its own ampersands escaped, so it cannot become a mnemonic (WinForms Mnemonic).
            mi.Header = MenuText.FromWinForms("&" + text.Replace("&", "&&", StringComparison.Ordinal));
            mi.IsEnabled = enabled;
        }
    }

    /// <summary>Coalesces refreshes asked for by game changes (many commands can arrive in one burst).</summary>
    void RequestHistoryRefresh()
    {
        if (_historyRefreshQueued) return;
        _historyRefreshQueued = true;
        Dispatcher.UIThread.Post(() => { _historyRefreshQueued = false; RefreshHistoryUi(); });
    }

    // ---------- menu flows (ask first, as WinForms) ----------

    async Task OpenModuleDialogAsync()
    {
        var path = await _dialogs.OpenFileAsync(this, "Open Module", new[] { ModuleFilter, AllFiles });
        if (path == null || !await ConfirmDiscardAsync() || !ConfirmRulesSaved()) return;
        if (OpenModule(path)) await ChooseAndStartAsync(preselectNew: false);
    }

    async Task NewGameAsync()
    {
        if (_session == null || !await ConfirmDiscardAsync()) return;
        await ChooseAndStartAsync(preselectNew: true);
    }

    async Task LoadSetupAsync()
    {
        if (_session == null || !await ConfirmDiscardAsync()) return;
        await ChooseAndStartAsync(preselectNew: false);
    }

    /// <summary>Shows the setup chooser, remembers the difficulty and starts the chosen game.</summary>
    async Task ChooseAndStartAsync(bool preselectNew)
    {
        if (_session is not { } s) return;
        var choice = await _dialogs.ChooseSetupAsync(this, s.Model, preselectNew, _lastDifficulty);
        if (choice == null || !ReferenceEquals(s, _session)) return;
        _lastDifficulty = choice.Difficulty;
        if (choice.Setup is { } setup) s.LoadSetup(setup, choice.Difficulty); else s.StartNewGame(choice.Difficulty);
    }

    async Task OpenGameDialogAsync()
    {
        if (_session is not { } s || !await ConfirmDiscardAsync()) return;
        var path = await _dialogs.OpenFileAsync(this, "Open Game", new[] { SaveFilter, AllFiles });
        if (path == null || !ReferenceEquals(s, _session)) return;   // the module changed while the picker was open
        OpenGame(path);
    }

    async Task SaveGameAsync(bool saveAs)
    {
        if (_session?.Game == null) return;
        if (!_session.CanSave)
        {
            if (_session.IsReplaying) SetStatus(ModuleSession.ReplayingText);
            return;
        }
        var path = _savePath;
        if (saveAs || path == null)
        {
            path = await _dialogs.SaveFileAsync(this, "Save Game", SaveFilter, "qsave");
            if (path == null) return;
            if (Autosaver.IsAutosavePath(path, AppDataRoot.Path))
            {
                ShowError("Saves inside the autosave folder are deleted automatically. Choose another folder.");
                return;
            }
        }
        SaveGame(path);
    }

    // ---------- rules packs ----------

    RulesPackFlow? RulesFlow() => _session == null ? null : new RulesPackFlow(_session, new RulesUi(this), null);

    void NewRulesPack() => RulesFlow()?.New();
    void OpenRulesPack() => RulesFlow()?.Open();

    void SaveRulesPack(bool saveAs)
    {
        if (_session?.Rules == null) return;
        RulesFlow()!.Save(saveAs);
    }

    /// <summary>Offers to save an edited rules pack; false when the user cancels (WinForms ConfirmRulesSaved).</summary>
    bool ConfirmRulesSaved() => _session is not { RulesDirty: true } || RulesFlow()!.ConfirmSaved();

    /// <summary>
    /// <see cref="IRulesPackUi"/> over <see cref="IDialogs"/>. The flow is synchronous (shared with WinForms) and the dialogs are not,
    /// so each answer is waited for with <see cref="Wait{T}"/>, which keeps the UI running (a modal loop) while a real dialog is open.
    /// </summary>
    sealed class RulesUi : IRulesPackUi
    {
        readonly MainWindow _w;
        public RulesUi(MainWindow w) => _w = w;

        public bool? AskSaveChanges(string packName) =>
            Wait(_w._dialogs.AskYesNoCancelAsync(_w, $"Save changes to the rules pack '{packName}'?", Caption));
        public string? PickOpen() => Wait(_w._dialogs.OpenFileAsync(_w, "Open Rules Pack", new[] { RulesFilter, AllFiles }));
        public string? PickSave(string suggestedName) => Wait(_w._dialogs.SaveFileAsync(_w, "Save Rules Pack", RulesFilter, "qrules"));
        public string? AskName(string title, string prompt, string def) => Wait(_w._dialogs.AskTextAsync(_w, title, prompt, def));
        public void Warn(string text) => Wait(_w._dialogs.ShowWarningAsync(_w, text, Caption).ContinueWith(_ => 0, TaskScheduler.Default));
    }

    /// <summary>The result of a dialog task. A task that is already done (tests' fakes) is read at once; otherwise a nested UI loop runs until it ends.</summary>
    static T Wait<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }

    /// <summary>The question to ask before the game goes away, or null when nothing would be lost (WinForms ConfirmDiscard).</summary>
    string? DiscardQuestion()
    {
        if (_session?.IsGuest == true) return null;   // a guest's game is the host's
        if (_session?.IsReplaying == true) return AbandonReplayText;   // a half-replayed turn is never saved
        if (_session is { Game: not null, CanSave: false }) return null;   // a viewer copy: never saved
        return _dirty && _session?.Game != null ? UnsavedText : null;
    }

    async Task<bool> ConfirmDiscardAsync() =>
        DiscardQuestion() is not { } q || await _dialogs.AskYesNoAsync(this, q, Caption);

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!e.Cancel && !_closeConfirmed && (DiscardQuestion() is not null || _session is { RulesDirty: true }))
        {
            // Dialogs are asynchronous: keep the window, ask, and close again when everything is answered.
            e.Cancel = true;
            if (!_closeAsking) AskThenClose(DiscardQuestion());
        }
        base.OnClosing(e);
    }

    async void AskThenClose(string? question)
    {
        _closeAsking = true;
        bool yes;
        // WinForms: ConfirmDiscard, then ConfirmRulesSaved.
        try { yes = (question == null || await _dialogs.AskYesNoAsync(this, question, Caption)) && ConfirmRulesSaved(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("close question: " + ex); yes = false; }
        finally { _closeAsking = false; }
        if (!yes) return;
        _closeConfirmed = true;
        Dispatcher.UIThread.Post(Close);
    }

    protected override void OnClosed(EventArgs e)
    {
        _router.Detach(this);
        CloseSession();
        base.OnClosed(e);
    }

    // ---------- session wiring ----------

    /// <summary>Phase and history controllers and the action port, exactly as WinForms MainForm.CreatePhases (without the panels).</summary>
    void CreatePhases(ModuleSession s)
    {
        var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        c.IsOwnerThread = () => Dispatcher.UIThread.CheckAccess();   // game changes run on the UI thread
        _phases = c;
        _history = new HistoryController(s, c, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        _history.Rewound += RequestHistoryRefresh;
        _actions = new ActionPortSwitch(new LocalActions(s, c, _history));
        HistoryCommands.Register(_router, s, _history, () => _actions, SetStatus);
    }

    void CloseSession()
    {
        Maps.Bind(null, null);
        PaletteHost.Bind(null);
        UnhookGame();
        if (_history != null) _history.Rewound -= RequestHistoryRefresh;
        _history = null;
        _phases = null;
        _actions = null;
        if (_session == null) return;
        _session.GameReplaced -= OnGameReplacedAnyThread;
        _session.ViewChanged -= OnViewChangedAnyThread;
        _session.RulesChanged -= OnRulesChangedAnyThread;
        _session.Dispose();
        _session = null;
        _savePath = null;
        _dirty = false;
        SbModule.Text = "No module";
        SbLocation.Text = "-";
        SbZoom.Text = "-";
        SetTilingProgress(null);
    }

    void HookGame()
    {
        UnhookGame();
        _hookedGame = _session?.Game;
        if (_hookedGame == null) return;
        _hookedGame.Changed += OnGameChanged;
        _hookedGame.Rewound += OnGameRewound;
    }

    void UnhookGame()
    {
        if (_hookedGame == null) return;
        _hookedGame.Changed -= OnGameChanged;
        _hookedGame.Rewound -= OnGameRewound;
        _hookedGame = null;
    }

    void OnGameChanged(GameCommand _) => OnUi(MarkDirty);
    void OnGameRewound() => OnUi(MarkDirty);

    void MarkDirty()
    {
        _dirty = true;
        UpdateTitle();
        RequestHistoryRefresh();
    }

    /// <summary>WinForms OnGameReplaced: a new game (setup, new, opened) starts clean, without a file.</summary>
    void OnGameReplacedAnyThread() => OnUi(() =>
    {
        HookGame();
        _dirty = false;
        _savePath = null;
        _recoveredAutosave = false;
        UpdateUi();
        UpdateTitle();
    });

    /// <summary>The player's view changed: maps another side owns lose their tab.</summary>
    void OnViewChangedAnyThread() => Dispatcher.UIThread.Post(() => Guard(Maps.Rebuild));

    void OnRulesChangedAnyThread() => OnUi(() => { UpdateUi(); UpdateTitle(); });

    static void OnUi(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a(); else Dispatcher.UIThread.Post(a);
    }

    // ---------- status bar ----------

    public void SetStatus(string text) => SbMessage.Text = text;

    void SetTilingProgress(double? fraction)
    {
        SbTiling.IsVisible = fraction != null;
        if (fraction != null) SbTiling.Value = Math.Clamp(fraction.Value * 100, 0, 100);
    }

    // ---------- errors ----------

    static bool IsUserError(Exception ex) =>
        ex is InvalidDataException or XmlException or IOException or UnauthorizedAccessException or InvalidOperationException;

    /// <summary>WinForms MainForm.Guard: expected failures (bad files, wrong module, ...) become a warning box.</summary>
    void Guard(Action a)
    {
        try { a(); }
        catch (Exception ex) when (IsUserError(ex)) { ShowError(ex.Message); }
    }

    async void RunAsync(Func<Task> f)
    {
        try { await f(); }
        catch (Exception ex) when (IsUserError(ex)) { ShowError(ex.Message); }
    }

    async void ShowError(string message)
    {
        try { await _dialogs.ShowWarningAsync(this, message, Caption); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("error box: " + ex); }
    }
}
