using Avalonia.Controls;
using Avalonia.Threading;
using Quartermaster.Desktop.Preferences;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop;

/// <summary>
/// The settings store and the live application of settings (WinForms MainForm.Settings): one <c>settings.json</c> under the layout root,
/// Tools > Settings, and every setting that can change while the app runs applied at once, each part guarded so one failure cannot
/// block the rest.
/// </summary>
public partial class MainWindow
{
    readonly DiagnosticLog _settingsDiag = new();
    /// <summary>Menu items and toolbar buttons that show a command's shortcut (gesture or tooltip), refreshed when shortcuts change.</summary>
    readonly List<(Control Control, string Cmd)> _shortcutControls = new();
    GridMode? _appliedGrid;
    Action<Settings>? _onSettingsChanged;
    bool _settingsOpen;

    /// <summary>The settings of this window (<c>settings.json</c> under the layout root, the WinForms file).</summary>
    public SettingsStore SettingsStore { get; }

    /// <summary>Game master mode: the saved setting (the Avalonia app has no --gm flag).</summary>
    public bool GameMaster => SettingsStore.Current.GameMasterMode;

    /// <summary>Raised after settings were applied to the main window.</summary>
    public event Action<Settings>? SettingsApplied;

    /// <summary>Tests: receives the built settings window instead of it being shown.</summary>
    internal Func<SettingsWindow, Task>? ShowSettingsHook { get; set; }

    /// <summary>Loads the store (a bad file falls back to defaults, with its problems logged) and the shortcut overrides, before the menus are built.</summary>
    void LoadSettings()
    {
        try { SettingsStore.Load(_settingsDiag); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("settings: " + ex.Message); }
        foreach (var d in _settingsDiag.Items) System.Diagnostics.Trace.WriteLine("settings: " + d.Message);
        _router.SetOverrides(SettingsStore.Current.ShortcutOverrides);
    }

    /// <summary>Applies the loaded settings and follows the store from now on (changes from another thread are posted to the UI thread).</summary>
    void StartSettings()
    {
        _onSettingsChanged = s =>
        {
            if (Dispatcher.UIThread.CheckAccess()) ApplySettings(s);
            else Dispatcher.UIThread.Post(() => { if (_onSettingsChanged != null) ApplySettings(s); });
        };
        SettingsStore.Changed += _onSettingsChanged;
        ApplySettings(SettingsStore.Current);
    }

    void StopSettings()
    {
        if (_onSettingsChanged != null) SettingsStore.Changed -= _onSettingsChanged;
        _onSettingsChanged = null;
    }

    /// <summary>Copies the problems found while loading settings into a freshly opened module's log (View > Diagnostics).</summary>
    void ReportSettingsProblems(ModuleSession s)
    {
        foreach (var d in _settingsDiag.Items) s.Log.AddOnce("settings:" + d.Context + d.Message, d.Severity, d.Category, d.Message, d.Context);
    }

    /// <summary>Every setting that can change while the app runs, in the WinForms MainForm.ApplySettings order; the map views last.</summary>
    void ApplySettings(Settings s)
    {
        Step(() => ApplyShortcuts(s));
        Step(() => Maps.SetTabWidths(s.TabMinWidth, s.TabMaxWidth));
        Step(() =>
        {
            // Only a change of the default sets View > Show Grid; the user's own choice stays otherwise.
            if (_appliedGrid == s.GridDefault) return;
            _appliedGrid = s.GridDefault;
            _showGrid.IsChecked = s.GridDefault == GridMode.Always;
        });
        Step(() => { foreach (var b in ToolbarHost.Buttons) ToolbarHost.SetVisible(b.Id, s.IsToolbarVisible(b.Id)); });
        Step(() => _dice.Core.ShowLastRoll = s.ShowLastRollInStatus);
        Step(() => _log?.ApplySettings(s));
        Step(UpdatePaletteUi);
        Step(UpdateAlertBarVisible);
        Step(() => SettingsApplied?.Invoke(s));
        Step(() => Maps.ApplySettings(s));   // WinForms: each map window on SettingsApplied
    }

    static void Step(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("apply settings: " + ex.Message); }
    }

    /// <summary>The overrides reach the key dispatch, the menu gestures and the toolbar tooltips (reasons and history captions re-applied after).</summary>
    void ApplyShortcuts(Settings s)
    {
        _router.SetOverrides(s.ShortcutOverrides);
        foreach (var (c, cmd) in _shortcutControls)
        {
            if (c is MenuItem mi) mi.InputGesture = _router.Gesture(cmd);
            else ToolTip.SetTip(c, BaseTip(c));
        }
        ApplyGates();
        RefreshDice();
        RefreshHistoryUi();
    }

    /// <summary>Remembers a control that shows <paramref name="cmd"/>'s shortcut.</summary>
    void ShowsShortcut(Control c, string cmd) => _shortcutControls.Add((c, cmd));

    /// <summary>A toolbar button's own tooltip: its text, with the shortcut of its command when it has one ("1d6 (F5)").</summary>
    object? BaseTip(Control c)
    {
        var text = ToolbarHost.BaseTip(c);
        if (text == null) return null;
        foreach (var (sc, cmd) in _shortcutControls)
            if (ReferenceEquals(sc, c)) return Tip(text, cmd);
        return text;
    }

    SettingsContext SettingsCtx() => new(
        ToolbarIds.All.Where(t => ToolbarHost.Find(t.Id) != null).ToList(),
        _cacheRoot,
        () => _session?.CacheDir,
        m => _dialogs.ShowInfoAsync(this, m, "Settings"),
        _dialogs,
        () => _session?.Game?.State.Difficulty,
        d =>
        {
            try
            {
                if (_session?.IsGuest == true) ShowError(IntentExecutor.HostOnlyText);
                else _phases?.SetDifficulty(d);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("set difficulty: " + ex.Message); }
        });

    /// <summary>Tools > Settings (Ctrl+,): the settings window over the store; afterwards a failed save is shown, as WinForms.</summary>
    public async Task ShowSettingsAsync()
    {
        if (_settingsOpen) return;
        _settingsOpen = true;
        try
        {
            SettingsWindow? win = null;
            var keyboard = new KeyboardPage(_router.Registry, _dialogs, () => win);
            win = new SettingsWindow(SettingsStore.Current, SettingsStore.Save, SettingsWindow.StandardPages(SettingsCtx(), keyboard), () => SettingsStore.Current, _dialogs);
            if (ShowSettingsHook is { } hook) await hook(win);
            else await win.ShowDialog(this);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("settings: " + ex);
            ShowError(ex.Message);
            return;
        }
        finally { _settingsOpen = false; }
        if (SettingsStore.LastSaveError is { } err) ShowError("The settings could not be saved: " + err);
    }
}
