using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Desktop.Preferences;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 5b task 2: Settings > Game > Players over the main window's open game. Headless, invented sides Red/Blue.</summary>
public sealed class GamePagePlayersTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-gamepage-players-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();

    public void Dispose()
    {
        foreach (var w in _windows)
        {
            if (w.Session is { } s) FormulaWindows.CloseAllFor(s);
            w.ForceClose();
        }
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>A main window with a new game of the minimal fixture and an invented pack with sides Red and Blue.</summary>
    MainWindow Game()
    {
        var fx = new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile());
        _fixtures.Add(fx);
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, Path.Combine(_tmp, "layout")) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        Assert.True(w.OpenModule(fx.Build()));
        var s = w.Session!;
        s.NewRules("Invented");
        s.UpdateRules(p => p with { Sides = PlayersEditorTests.Pack("Red", "Blue").Sides });
        w.StartSetup(null);
        Pump();
        Assert.NotNull(s.Game);
        return w;
    }

    static GamePage Page(SettingsWindow win) => win.Pages.OfType<GamePage>().Single();

    SettingsWindow Settings(MainWindow w)
    {
        SettingsWindow? shown = null;
        w.ShowSettingsHook = win => { shown = win; return Task.CompletedTask; };
        w.ShowSettingsAsync("game").GetAwaiter().GetResult();
        Pump();
        return shown!;
    }

    [AvaloniaFact]
    public void No_game_shows_no_game_text()
    {
        var ctx = new SettingsContext(Array.Empty<(string, string)>(), _tmp, () => null, _ => Task.CompletedTask, _dialogs,
            GamePlayers: () => null);
        var p = new GamePage(ctx);
        p.Load(new Settings());
        Assert.Null(p.Editor);
        Assert.Equal("Players", p.PlayersTitle.Text);
        Assert.Equal((440d, 210d), (p.PlayersBox.Width, p.PlayersBox.Height));
        Assert.Equal("Open a game first", p.NoGameText.Text);
        Assert.Same(p.NoGameText, p.PlayersBox.Child);
        Assert.Null(p.Validate());

        // Without the callback at all (an older caller), the same.
        var bare = new GamePage(new SettingsContext(Array.Empty<(string, string)>(), _tmp, () => null, _ => Task.CompletedTask, _dialogs));
        bare.Load(new Settings());
        Assert.Null(bare.Editor);
        Assert.Same(bare.NoGameText, bare.PlayersBox.Child);
    }

    [AvaloniaFact]
    public void Apply_sets_players_on_open_game()
    {
        var w = Game();
        var g = w.Session!.Game!;
        var win = Settings(w);
        var page = Page(win);
        var editor = page.Editor!;
        Assert.Same(editor, page.PlayersBox.Child);
        Assert.Single(editor.NameBoxes);

        // A problem keeps the window open (Settings' warning) and changes nothing.
        page.Editor!.AddButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.Equal("Player 2 holds no side.", page.Validate());
        Assert.False(win.CommitAsync().GetAwaiter().GetResult());
        Assert.Equal(("Player 2 holds no side.", "Settings"), Assert.Single(_dialogs.Warnings));
        Assert.Empty(g.State.Players);

        editor.Model.FillTwoPlayersForTests();
        editor.NameBoxes[0].Text = "Anna";
        editor.NameBoxes[1].Text = "Ben";
        Pump();
        Assert.True(win.CommitAsync().GetAwaiter().GetResult());
        Pump();
        Assert.Same(g, w.Session!.Game);
        Assert.Equal(new[] { "Anna", "Ben" }, g.State.Players.Select(p => p.Name));
        Assert.Single(g.Log.OfType<SetPlayers>());
        Assert.Single(_dialogs.Warnings);
    }

    [AvaloniaFact]
    public void Apply_same_list_executes_nothing()
    {
        var w = Game();
        var g = w.Session!.Game!;
        var before = g.Log.Count;
        var win = Settings(w);
        Assert.NotNull(Page(win).Editor);
        Assert.True(win.CommitAsync().GetAwaiter().GetResult());
        Pump();
        Assert.Equal(before, g.Log.Count);
        Assert.Empty(g.State.Players);
        Assert.Empty(_dialogs.Warnings);
    }

    [AvaloniaFact]
    public void Guest_refused_with_HostOnlyText()
    {
        var w = Game();
        var s = w.Session!;
        s.EnterGuestMode();
        s.ApplySnapshot(new GameState());
        Pump();
        var g = s.Game!;
        var win = Settings(w);
        var editor = Page(win).Editor!;
        editor.Model.FillTwoPlayersForTests();
        Pump();
        Assert.True(win.CommitAsync().GetAwaiter().GetResult());
        Pump();
        // One refusal from the difficulty (a guest's AfterApply always asks to set it, as WinForms) and one from the players.
        Assert.Equal(2, _dialogs.Warnings.Count(x => x == (IntentExecutor.HostOnlyText, "Quartermaster")));
        Assert.Equal(2, _dialogs.Warnings.Count);
        Assert.Empty(g.State.Players);
        Assert.Empty(g.Log.OfType<SetPlayers>());
    }
}
