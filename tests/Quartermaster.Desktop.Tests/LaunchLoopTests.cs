using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Launcher;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Vassal;
using Xunit;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 5a task 3: the launcher ↔ game window loop and File > Back to launcher, headless, with temp roots and a temp module copy.</summary>
public sealed class LaunchLoopTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-loop-" + Guid.NewGuid().ToString("N"));
    readonly string _layout;
    readonly string _cache;
    readonly string _module;
    readonly FakeDialogs _dialogs = new();
    readonly List<LauncherWindow> _launchers = new();
    readonly List<string?> _justPlayed = new();
    readonly List<MainWindow> _games = new();
    readonly List<Window> _mainWindows = new();
    int _shutdowns;

    public LaunchLoopTests()
    {
        _layout = Path.Combine(_tmp, "layout");
        _cache = Path.Combine(_layout, "cache");
        Directory.CreateDirectory(_layout);
        Directory.CreateDirectory(Path.Combine(_tmp, "mods"));
        _module = Path.Combine(_tmp, "mods", "Riverland-Demo.vmod");
        File.Copy(Demo("Riverland-Demo.vmod"), _module);
        new ModuleLibrary(Store()).Add(_module);
    }

    public void Dispose()
    {
        foreach (var g in _games) { try { g.ForceClose(); } catch (InvalidOperationException) { } }
        foreach (var l in _launchers) { try { l.Close(); } catch (InvalidOperationException) { } }
        Dispatcher.UIThread.RunJobs();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Demo(string file, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", file));

    SettingsStore Store()
    {
        var s = new SettingsStore(_layout);
        s.Load(null);
        return s;
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static void PumpUntil(Func<bool> done, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            Pump();
            Thread.Sleep(10);
        }
        Pump();
    }

    /// <summary>Starts the loop with factories over the temp roots; returns once the first launcher is up and has hashed the library.</summary>
    Task Start()
    {
        var run = Loop().RunAsync();
        PumpUntil(() => _launchers.Count == 1 && _launchers[0].IsVisible && _launchers[0].RefreshTask.IsCompleted);
        return run;
    }

    LaunchLoop Loop() => new LaunchLoop(
            jp =>
            {
                _justPlayed.Add(jp);
                var l = new LauncherWindow(Store(), _layout, _dialogs, jp);
                _launchers.Add(l);
                return l;
            },
            () =>
            {
                var g = new MainWindow(_dialogs, _cache, null, _layout) { Width = 1024, Height = 768 };
                _games.Add(g);
                return g;
            },
            _mainWindows.Add,
            () => _shutdowns++);

    static StartupArgs Args(params string[] a) => StartupArgs.Parse(a, _ => { });

    /// <summary>A direct start (module argument): the loop opens the game window first with the command line; returns once its game is up.</summary>
    (Task Run, MainWindow Game) StartDirect()
    {
        var run = Loop().RunAsync(w => w.ApplyStartup(Args(_module, "--new")));
        PumpUntil(() => _games.Count == 1 && _games[0].Session?.Game != null);
        return (run, _games[0]);
    }

    LaunchRequest NewGame() => new(_module, null, LaunchKind.NewGame,
        new NewGameRequest(GameMode.Offline, null, Difficulty.FullyAuto, null, null, null), null);

    /// <summary>Starts the loop and plays a new game from the launcher; returns the game window once its game is up.</summary>
    (Task Run, MainWindow Game) StartGame()
    {
        var run = Start();
        _launchers[0].Choose(NewGame());
        PumpUntil(() => _games.Count == 1 && _games[0].Session?.Game != null);
        return (run, _games[0]);
    }

    static void MoveAPiece(MainWindow w)
    {
        var g = w.Session!.Game!;
        var p = g.State.Pieces.Values.First(x => x.MapId == w.Session.Model.Maps[0].Id);
        w.Actions!.Move(new[] { new MovePieces(new[] { p.Id }, p.MapId, new PointD(p.Pos.X + 40, p.Pos.Y + 40), null) });
    }

    [AvaloniaFact]
    public void Closing_launcher_ends_loop()
    {
        var run = Start();
        Assert.Same(_launchers[0], Assert.Single(_mainWindows));
        _launchers[0].Close();
        PumpUntil(() => run.IsCompleted);
        Assert.Equal(1, _shutdowns);
        Assert.Empty(_games);
        Assert.Single(_launchers);
    }

    [AvaloniaFact]
    public void New_game_request_opens_game_and_marks_played()
    {
        var (run, g) = StartGame();
        Assert.False(_launchers[0].IsVisible);   // one window at a time
        Assert.True(g.IsVisible);
        Assert.Same(g, _mainWindows[^1]);
        Assert.True(g.LauncherAvailable);
        Assert.True(g.AutoTour);
        Assert.Equal(Difficulty.FullyAuto, g.Session!.Game!.State.Difficulty);
        Assert.Equal(_module, g.OpenedModulePath);
        PumpUntil(() => Store().Current.LastDifficulty == Difficulty.FullyAuto);
        var row = Assert.Single(new ModuleLibrary(Store()).Rows);
        Assert.NotNull(row.Entry.LastPlayed);
        Assert.False(run.IsCompleted);
        Assert.Equal(0, _shutdowns);
    }

    [AvaloniaFact]
    public void Back_to_launcher_reopens_launcher_with_just_played_selected()
    {
        var store = Store();
        store.Update(s => s with { ReopenLastModule = false });   // selected because it was just played, not because of the setting
        var (run, g) = StartGame();
        Assert.True(g.FindMenuItem("&Back to launcher")!.IsVisible);
        var back = g.BackToLauncherAsync();
        PumpUntil(() => back.IsCompleted && _launchers.Count == 2 && _launchers[1].IsVisible && _launchers[1].RefreshTask.IsCompleted);
        Assert.Empty(_dialogs.Questions);
        Assert.True(g.ReturnToLauncher);
        Assert.False(g.IsVisible);
        Assert.Equal(new[] { null, _module }, _justPlayed);
        Assert.Equal(Path.GetFullPath(_module), _launchers[1].SelectedRow!.Entry.Path, ignoreCase: true);
        Assert.Same(_launchers[1], _mainWindows[^1]);
        Assert.Equal(0, _shutdowns);

        _launchers[1].Close();
        PumpUntil(() => run.IsCompleted);
        Assert.Equal(1, _shutdowns);
        Assert.Single(_games);
    }

    [AvaloniaFact]
    public void Back_to_launcher_respects_discard_question()
    {
        var (run, g) = StartGame();
        MoveAPiece(g);
        Pump();
        Assert.True(g.HasUnsavedChanges);
        _dialogs.Answer = false;
        var back = g.BackToLauncherAsync();
        PumpUntil(() => back.IsCompleted);
        Assert.Equal(new[] { ("The current game has unsaved changes. Discard them?", "Quartermaster") }, _dialogs.Questions);
        Assert.True(g.IsVisible);
        Assert.False(g.ReturnToLauncher);
        Assert.Single(_launchers);
        Assert.False(run.IsCompleted);

        // The cancelled Back to launcher is forgotten: closing the window now ends the app.
        _dialogs.Answer = true;
        g.Close();
        PumpUntil(() => run.IsCompleted);
        Assert.False(g.ReturnToLauncher);
        Assert.Single(_launchers);
        Assert.Equal(1, _shutdowns);
    }

    [AvaloniaFact]
    public void Closing_game_window_ends_loop()
    {
        var (run, g) = StartGame();
        g.Close();
        PumpUntil(() => run.IsCompleted);
        Assert.False(g.ReturnToLauncher);
        Assert.Single(_launchers);
        Assert.Equal(1, _shutdowns);
    }

    [AvaloniaFact]
    public void Open_save_request_loads_save_and_adds_recent()
    {
        var save = Path.Combine(_tmp, "game 1.qsave");
        string sha;
        using (var s = ModuleSession.Open(_module, Path.Combine(_tmp, "cache-save"), null))
        {
            s.StartNewGame(Difficulty.SemiAuto);
            s.SaveNative(save);
            sha = s.Archive.Sha256!;
        }
        var run = Start();
        _launchers[0].Choose(new LaunchRequest(_module, null, LaunchKind.OpenSave, null, save));
        PumpUntil(() => _games.Count == 1 && _games[0].Session?.Game != null);
        var g = _games[0];
        Assert.Equal(save, g.SavePath);
        Assert.Equal(Difficulty.SemiAuto, g.Session!.Game!.State.Difficulty);
        Assert.Equal(0, _dialogs.SetupChooserShown);
        Assert.Equal(save, Assert.Single(new RecentGames(Store(), _layout).For(sha)).Path);
        Assert.False(run.IsCompleted);
    }

    [AvaloniaFact]
    public void Back_to_launcher_menu_hidden_without_launcher()
    {
        var w = new MainWindow(_dialogs, _cache, null, _layout);
        _games.Add(w);
        w.Show();
        var item = w.FindMenuItem("&Back to launcher");
        Assert.NotNull(item);
        Assert.False(w.LauncherAvailable);
        Assert.False(w.AutoTour);
        Assert.False(item!.IsVisible);
        Assert.Equal(new[] { "&Back to launcher", "E&xit" }, w.MenuLabels()["&File"].TakeLast(2));
        w.LauncherAvailable = true;
        Assert.True(item.IsVisible);
        w.LauncherAvailable = false;
        Assert.False(item.IsVisible);
    }

    [AvaloniaFact]
    public void Module_argument_skips_launcher()
    {
        static StartupArgs P(params string[] a) => Args(a);
        Assert.True(App.UsesLauncher(P()));
        Assert.False(App.UsesLauncher(P("x.vmod")));
        Assert.False(App.UsesLauncher(P("x.vmod", "--new")));
        Assert.False(App.UsesLauncher(P("x.vmod", "--screenshot-main", "out.png")));
        Assert.False(App.UsesLauncher(P("--screenshot-main", "out.png")));
        // The direct start: the game window comes first, no launcher is shown.
        var (run, w) = StartDirect();
        Assert.Empty(_launchers);
        Assert.Same(w, Assert.Single(_mainWindows));
        Assert.Equal(_module, w.OpenedModulePath);
        Assert.False(run.IsCompleted);
    }

    /// <summary>WinForms parity (Program.cs LauncherHook ??= ShowLauncher): a game window opened from the command line also has Back to launcher.</summary>
    [AvaloniaFact]
    public void Direct_start_back_to_launcher_opens_launcher_with_module_selected()
    {
        Store().Update(s => s with { ReopenLastModule = false });
        var (run, g) = StartDirect();
        Assert.True(g.LauncherAvailable);
        Assert.True(g.AutoTour);
        Assert.True(g.FindMenuItem("&Back to launcher")!.IsVisible);
        Assert.NotNull(Assert.Single(new ModuleLibrary(Store()).Rows).Entry.LastPlayed);   // the direct start is bookkept too

        var back = g.BackToLauncherAsync();
        PumpUntil(() => back.IsCompleted && _launchers.Count == 1 && _launchers[0].IsVisible && _launchers[0].RefreshTask.IsCompleted);
        Assert.True(g.ReturnToLauncher);
        Assert.Equal(new string?[] { _module }, _justPlayed);
        Assert.Equal(Path.GetFullPath(_module), _launchers[0].SelectedRow!.Entry.Path, ignoreCase: true);
        Assert.Same(_launchers[0], _mainWindows[^1]);
        Assert.Equal(0, _shutdowns);

        _launchers[0].Close();
        PumpUntil(() => run.IsCompleted);
        Assert.Equal(1, _shutdowns);
    }

    [AvaloniaFact]
    public void Direct_start_closing_game_window_ends_app()
    {
        var (run, g) = StartDirect();
        g.Close();
        PumpUntil(() => run.IsCompleted);
        Assert.False(g.ReturnToLauncher);
        Assert.Empty(_launchers);
        Assert.Equal(1, _shutdowns);
    }
}
