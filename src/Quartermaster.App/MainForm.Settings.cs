using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>Settings store wiring, live application of settings and the autosave timer.</summary>
sealed partial class MainForm
{
    readonly SettingsStore _settings = new(LayoutRoot);
    readonly DiagnosticLog _settingsDiag = new();
    Autosaver? _autosaver;
    System.Windows.Forms.Timer? _autosaveTimer;
    int _changeSeq, _autosavedSeq; // game changes so far / the value when the game was last saved or autosaved
    GridMode? _appliedGrid;

    public Settings CurrentSettings => _settings.Current;

    /// <summary>Game master mode: the saved setting, or --gm for this run only.</summary>
    public bool GameMaster => _opts.GameMaster || _settings.Current.GameMasterMode;

    /// <summary>Raised after settings were applied to the main window; map windows re-read their options.</summary>
    public event Action<Settings>? SettingsApplied;

    void InitSettings()
    {
        try { _settings.Load(_settingsDiag); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("settings: " + ex.Message); }
        foreach (var d in _settingsDiag.Items) Program.AppLog("settings: " + d.Message);
        _settings.Changed += s =>
        {
            if (IsDisposed) return;
            if (InvokeRequired && IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) ApplySettings(s); });
            else ApplySettings(s);
        };
        _autosaver = new Autosaver(() => _session?.Archive.Sha256, p => _session!.SaveNative(p), LayoutRoot, OnAutosaveError);
        _autosaveTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _autosaveTimer.Tick += (_, _) => Guard(AutosaveTick);
        _autosaveTimer.Start();
        ApplySettings(_settings.Current);
    }

    /// <summary>Copies the diagnostics found while loading settings into a freshly opened module's log so they show in View, Diagnostics.</summary>
    void ReportSettingsProblems(ModuleSession s)
    {
        foreach (var d in _settingsDiag.Items) s.Log.AddOnce("settings:" + d.Context + d.Message, d.Severity, d.Category, d.Message, d.Context);
    }

    void OnAutosaveError(string message)
    {
        Program.AppLog(message);
        _session?.Log.AddOnce("autosave:failed", DiagnosticSeverity.Warning, "autosave", message);
    }

    void AutosaveTick()
    {
        if (_autosaver == null) return;
        var seq = _changeSeq;
        var dirty = _session?.Game != null && seq != _autosavedSeq;
        var path = _autosaver.Tick(DateTimeOffset.UtcNow, _settings.Current.AutosaveMinutes, dirty);
        if (path == null) return;
        _autosavedSeq = seq; // the user's save path and the title's dirty marker are deliberately untouched
        Program.AppLog("autosaved to " + path);
    }

    /// <summary>Every setting that can change while the app runs. Each part is guarded so one failure cannot block the rest.</summary>
    void ApplySettings(Settings s)
    {
        Step(() => ApplyShortcuts(s));
        Step(() => { _tabs.SetWidths(s.TabMinWidth, s.TabMaxWidth); });
        Step(() =>
        {
            if (_appliedGrid != s.GridDefault)
            {
                _appliedGrid = s.GridDefault;
                _showGrid.Checked = s.GridDefault == GridMode.Always;
            }
        });
        Step(() =>
        {
            foreach (ToolStripItem i in _toolbar.Items)
                if (i is not ToolStripSeparator && i.Text is { Length: > 0 } text) i.Visible = s.IsToolbarVisible(text);
        });
        Step(() =>
        {
            if (_dice == null) return;
            _dice.PhysicalDice = s.PhysicalDiceDefault;
            _dice.ShowLastRoll = s.ShowLastRollInStatus;
        });
        Step(() => _logDock.Panel?.ApplySettings(s));
        Step(() => _paletteDock.SetGameMaster(GameMaster));
        Step(() => SettingsApplied?.Invoke(s));
    }

    static void Step(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("apply settings: " + ex.Message); }
    }

    SettingsContext SettingsCtx() => new(
        _toolbar.Items.Cast<ToolStripItem>().Where(i => i is not ToolStripSeparator && i.Text is { Length: > 0 }).Select(i => (i.Text!, i.Text!)).ToList(),
        ModuleSession.DefaultCacheRoot,
        () => _session?.CacheDir,
        m => Info(m, "Settings"), _commands);

    void ShowSettings()
    {
        if (Program.Smoke) return;
        using var dlg = new SettingsDialog(_settings.Current, s => _settings.Save(s), SettingsCtx(), () => _settings.Current);
        dlg.ShowDialog(this);
        if (_settings.LastSaveError is { } err) ShowError("The settings could not be saved: " + err);
    }

    /// <summary>Reopens the last module at startup when asked to (never in smoke mode, never when a module came from the command line).</summary>
    void ReopenLastModuleIfWanted()
    {
        var s = _settings.Current;
        if (Program.Smoke || _opts.ModulePath != null || !s.ReopenLastModule || string.IsNullOrEmpty(s.LastModulePath) || !File.Exists(s.LastModulePath)) return;
        var path = s.LastModulePath;
        BeginInvoke(() => { if (!IsDisposed) Guard(() => OpenModule(path, fromArgs: false)); });
    }

    void RememberModule(string path)
    {
        if (Program.Smoke || string.Equals(_settings.Current.LastModulePath, path, StringComparison.OrdinalIgnoreCase)) return;
        _settings.Save(_settings.Current with { LastModulePath = path });
    }

    // ---------- smoke ----------

    /// <summary>Smoke: opens the settings window on the General tab, checks it reads back what it was given, saves a screenshot, and checks that a store change reaches the map canvas.</summary>
    void SmokeSettings()
    {
        var shot = _opts.ScreenshotSettings;
        if (shot == null) return;
        using (var dlg = new SettingsDialog(_settings.Current, _ => { }, SettingsCtx()))
        {
            dlg.Show(this);
            var ti = Array.IndexOf(new[] { "general", "display", "map", "dice", "toolbar", "keyboard", "folders" }, (_opts.SettingsTab ?? "general").ToLowerInvariant());
            dlg.SelectTab(Math.Max(0, ti));
            dlg.Update();
            if (ti == 6)
            {
                var until = DateTime.UtcNow.AddSeconds(10);
                while (!dlg.FoldersLoadedForSmoke && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(20); }
                Program.AppLog($"smoke: folders sizes loaded {dlg.FoldersLoadedForSmoke}");
                if (!dlg.FoldersLoadedForSmoke) Program.ExitCode = 1;
                dlg.Update();
            }
            var back = dlg.CollectForSmoke();
            var cur = _settings.Current;
            var same = back with { ToolbarVisible = cur.ToolbarVisible, ShortcutOverrides = cur.ShortcutOverrides } == cur;
            Program.AppLog($"smoke: settings dialog round trip {(same ? "ok" : "FAILED")}");
            if (!same) Program.ExitCode = 1;
            dlg.SaveScreenshot(shot);
            dlg.Close();
        }

        if (FirstMap() is not { } map)
        {
            Program.AppLog("smoke: --screenshot-settings given but no map is open for the live-apply check.");
            Program.ExitCode = 1;
            return;
        }
        var before = _settings.Current;
        var raised = 0;
        void Count(Settings _) => raised++;
        _settings.Changed += Count;
        try { _settings.Save(before with { WheelZoomStep = 1.5 }); }
        finally { _settings.Changed -= Count; }
        var live = raised == 1 && Math.Abs(map.Canvas.ZoomStepForSmoke - 1.5) < 1e-9;
        _settings.Save(before);
        Program.AppLog($"smoke: settings live apply {(live ? "ok" : "FAILED")} (Changed raised {raised}x, canvas zoom step {map.Canvas.ZoomStepForSmoke})");
        if (!live) Program.ExitCode = 1;
    }
}
