using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Launcher;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 5b task 2: the setup chooser's Players page (Next &gt; / &lt; Back, OK checks) and Play offline starting with players.</summary>
public sealed class SetupChooserPlayersTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-chooser-players-" + Guid.NewGuid().ToString("N"));
    readonly FakeDialogs _dialogs = new();
    readonly List<Window> _windows = new();

    public void Dispose()
    {
        foreach (var w in _windows)
        {
            try
            {
                if (w is MainWindow m) m.ForceClose(); else w.Close();
            }
            catch (InvalidOperationException) { }
        }
        Dispatcher.UIThread.RunJobs();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Click(Button b)
    {
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    static void WaitFor(Func<bool> done, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(done());
    }

    SetupChooser Chooser(RulesPack? pack)
    {
        var c = new SetupChooser(null, Difficulty.SemiAuto, preselectNew: true, pack, _dialogs);
        _windows.Add(c);
        c.Show();
        Dispatcher.UIThread.RunJobs();
        return c;
    }

    [AvaloniaFact]
    public void Next_shows_players_page_and_Back_returns()
    {
        var c = Chooser(PlayersEditorTests.Pack("Red", "Blue"));
        Assert.Equal("_Next >", c.Next.Content);
        Assert.Equal("< _Back", c.Back.Content);
        Assert.False(c.OnPlayersPage);
        Assert.True(c.GamePanel.IsVisible);
        Assert.False(c.PlayersPanel.IsVisible);
        Assert.True(c.Next.IsVisible);
        Assert.False(c.Back.IsVisible);

        Click(c.Next);
        Assert.True(c.OnPlayersPage);
        Assert.False(c.GamePanel.IsVisible);
        Assert.True(c.PlayersPanel.IsVisible);
        Assert.False(c.Next.IsVisible);
        Assert.True(c.Back.IsVisible);
        Assert.Equal("PlayersEditor", c.Editor.Name);
        Assert.Single(c.Editor.NameBoxes);

        Click(c.Back);
        Assert.False(c.OnPlayersPage);
        Assert.True(c.GamePanel.IsVisible);
        Assert.False(c.PlayersPanel.IsVisible);
        Assert.True(c.Next.IsVisible);
        Assert.False(c.Back.IsVisible);
    }

    [AvaloniaFact]
    public void Ok_with_two_valid_players_returns_them()
    {
        var c = Chooser(PlayersEditorTests.Pack("Red", "Blue"));
        Click(c.Next);
        c.Editor.Model.FillTwoPlayersForTests();
        Dispatcher.UIThread.RunJobs();
        c.Editor.NameBoxes[0].Text = "Anna";
        c.Editor.NameBoxes[1].Text = "Ben";
        Click(c.Ok);
        Assert.Empty(_dialogs.Warnings);
        Assert.False(c.IsVisible);
        var players = c.Result!.Players!;
        Assert.Equal(new[] { "Anna", "Ben" }, players.Select(p => p.Name));
        Assert.Equal(new[] { "red" }, players[0].Sides);
        Assert.Equal(new[] { "blue" }, players[1].Sides);
        Assert.Equal(Difficulty.SemiAuto, c.Result.Difficulty);
        Assert.Null(c.Result.Setup);
    }

    [AvaloniaFact]
    public void Ok_with_problem_stays_on_players_page()
    {
        var c = Chooser(PlayersEditorTests.Pack("Red", "Blue"));
        Click(c.Next);
        Click(c.Editor.AddButton);   // Player 2 holds no side
        Click(c.Back);               // OK from the first page still checks the players
        Click(c.Ok);
        var (msg, caption) = Assert.Single(_dialogs.Warnings);
        Assert.Equal("Player 2 holds no side.", msg);
        Assert.Equal("Players", caption);
        Assert.True(c.IsVisible);
        Assert.True(c.OnPlayersPage);
        Assert.Null(c.Result);
    }

    [AvaloniaFact]
    public void Solo_returns_null_players()
    {
        var c = Chooser(PlayersEditorTests.Pack("Red", "Blue"));
        Click(c.Ok);
        Assert.False(c.IsVisible);
        Assert.Equal(new SetupChoice(null, Difficulty.SemiAuto), c.Result);
        Assert.Null(c.Result!.Players);
    }

    [AvaloniaFact]
    public void Players_page_disabled_without_two_sides()
    {
        var c = Chooser(PlayersEditorTests.Pack("Red"));
        Click(c.Next);
        Assert.True(c.OnPlayersPage);
        Assert.False(c.Editor.AddButton.IsEnabled);
        Assert.Equal("The rules pack has fewer than two sides: one player plays everything.", c.Editor.HintText.Text);
        Click(c.Ok);
        Assert.Empty(_dialogs.Warnings);
        Assert.Null(c.Result!.Players);
    }

    [AvaloniaFact]
    public void PlayOffline_new_game_starts_with_players()
    {
        var layout = Path.Combine(_tmp, "layout");
        Directory.CreateDirectory(layout);
        Directory.CreateDirectory(Path.Combine(_tmp, "mods"));
        var module = Path.Combine(_tmp, "mods", "Riverland-Demo.vmod");
        File.Copy(Demo("Riverland-Demo.vmod"), module);
        var store = new SettingsStore(layout);
        store.Load(null);
        var anna = new PlayerDef("p1", "Anna", new List<string> { "red" });
        var ben = new PlayerDef("p2", "Ben", new List<string> { "blue" });
        _dialogs.Setup = new SetupChoice(null, Difficulty.SemiAuto, new[] { anna, ben });

        var row = new ModuleRow(new ModuleEntry(Path.GetFullPath(module), new string('a', 64), 1, DateTime.UtcNow, "Riverland Demo", "1.0", null), false);
        var offline = new PlayOfflineWindow(row, new ModuleLibrary(store), new RecentGames(store, layout), store, _dialogs, Path.Combine(_tmp, "cache"));
        _windows.Add(offline);
        offline.Show();
        Dispatcher.UIThread.RunJobs();
        Click(offline.NewGameButton);
        WaitFor(() => offline.Result.IsCompleted, 60);
        var req = offline.Result.Result!;
        Assert.Equal(2, req.NewGame!.Players!.Count);

        var game = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, layout) { Width = 1024, Height = 768 };
        _windows.Add(game);
        game.Show();
        var start = game.StartFromLaunchAsync(req);
        WaitFor(() => start.IsCompleted && game.Session?.Game != null, 60);
        Assert.Equal(new[] { "Anna", "Ben" }, game.Session!.Game!.State.Players.Select(p => p.Name));
    }

    static string Demo(string file, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", file));
}
