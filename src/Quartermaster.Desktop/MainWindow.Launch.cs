using Avalonia.Controls;
using Quartermaster.Render;

namespace Quartermaster.Desktop;

/// <summary>
/// The game window's side of the launcher loop (WinForms MainForm StartGameForLaunch / BackToLauncher): starting the game the launcher
/// asked for, the library and recent-save bookkeeping, and File → Back to launcher.
/// </summary>
public partial class MainWindow
{
    MenuItem? _backToLauncherItem;
    bool _launcherAvailable;
    bool _backToLauncher;   // Back to launcher is closing the window; cleared when the close is cancelled
    ModuleLibrary? _library;
    RecentGames? _recent;

    /// <summary>True when the window was started by the launcher loop: File → Back to launcher shows and the launcher bookkeeping runs.</summary>
    internal bool LauncherAvailable
    {
        get => _launcherAvailable;
        set
        {
            _launcherAvailable = value;
            if (_backToLauncherItem != null) _backToLauncherItem.IsVisible = value;
        }
    }

    /// <summary>The Interface Tour runs on the first game open (set by the launcher loop only, never in tests or screenshot runs).</summary>
    internal bool AutoTour { get; set; }

    /// <summary>True when the window was closed through File → Back to launcher (not the close box or Exit).</summary>
    public bool ReturnToLauncher { get; private set; }

    /// <summary>The module last opened in this window (the launcher selects it after Back to launcher).</summary>
    public string? OpenedModulePath { get; private set; }

    ModuleLibrary Library => _library ??= new ModuleLibrary(SettingsStore);
    RecentGames Recent => _recent ??= new RecentGames(SettingsStore, _layoutRoot);

    /// <summary>
    /// Starts the game the launcher asked for (WinForms StartGameForLaunch): opens the module, loads the request's rules pack when it is
    /// set, exists and is not already loaded, then a new game (or setup), a saved game or the newest autosave; anything else shows the
    /// setup chooser. Then the module is marked played and an opened save goes to the recent list.
    /// </summary>
    public async Task StartFromLaunchAsync(LaunchRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (!OpenModule(r.ModulePath) || _session is not { } s) return;
        // Same guard as Play offline's New game: a remembered pack that moved (or is already loaded) is skipped silently.
        if (r.RulesPath is { Length: > 0 } rules && !string.Equals(s.RulesPath, rules, StringComparison.OrdinalIgnoreCase)
            && (File.Exists(rules) || Directory.Exists(rules)))
        {
            try { s.OpenRules(rules); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("launcher: rules pack not loaded: " + ex.Message); }
        }
        switch (r.Kind)
        {
            case LaunchKind.NewGame when r.NewGame is { } ng:
                var players = ng.Players is { Count: >= 2 } ? ng.Players : null;
                Guard(() => { if (ng.Setup is { } setup) s.LoadSetup(setup, ng.Difficulty, players); else s.StartNewGame(ng.Difficulty, players); });
                if (ng.Difficulty != SettingsStore.Current.LastDifficulty) SettingsStore.Update(x => x with { LastDifficulty = ng.Difficulty });
                break;
            case LaunchKind.OpenSave or LaunchKind.Continue when !string.IsNullOrEmpty(r.SavePath):
                Guard(() => { OpenGame(r.SavePath); NoteRecentSave(r.SavePath); });
                break;
            default:
                try { await ChooseAndStartAsync(preselectNew: false); }
                catch (Exception ex) when (IsUserError(ex)) { ShowError(ex.Message); }
                break;
        }
        NoteModuleOpened(r.ModulePath);
        MaybeAutoTour();
    }

    /// <summary>File → Back to launcher: closes the window, asking about unsaved changes as any close; a cancelled close forgets the request.
    /// Completes once the close has happened or was cancelled.</summary>
    internal async Task BackToLauncherAsync()
    {
        _backToLauncher = true;
        if (!_closeAsking) _closeQuestion = null;   // a stale, finished ask; one still open is waited for below
        Close();
        if (_closeQuestion is { } asking) await asking;
        if (!_closed && !_closeConfirmed) _backToLauncher = false;   // cancelled: the next close (box, Exit) ends the app
    }

    /// <summary>The launcher's "last played" (WinForms OpenModule bookkeeping); only in a window the launcher started.</summary>
    void NoteModuleOpened(string path)
    {
        if (LauncherAvailable) Guard(() => LaunchBookkeeping.ModuleOpened(Library, path, DateTimeOffset.UtcNow));
    }

    /// <summary>The launcher's recent saves (WinForms NoteRecentSave); autosaves never go there.</summary>
    void NoteRecentSave(string path)
    {
        if (!LauncherAvailable || Autosaver.IsAutosavePath(path, _layoutRoot)) return;
        Guard(() => LaunchBookkeeping.SaveUsed(Recent, _session?.Archive.Sha256, path));
    }
}
