using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Render;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 5b task 4: the status-bar player switcher (WinForms MainForm.HotSeat _sbPlayer). Invented two-player TEST game
/// (Anna Red, Ben Blue); headless, fake dialogs, temp roots.</summary>
public sealed class PlayerSwitcherTests : IDisposable
{
    readonly HotSeatGames _games = new("qm-switcher-");

    public void Dispose() => _games.Dispose();

    static void Click(MenuItem mi)
    {
        mi.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        HotSeatGames.Pump();
    }

    [AvaloniaFact]
    public void Hidden_in_solo_game()
    {
        var w = _games.NewWindow();
        Assert.False(w.PlayerButton.IsVisible);   // no module
        Assert.Equal("status.player", w.PlayerButton.Name);
        Assert.Equal(HandoverText.PlayerTip, ToolTip.GetTip(w.PlayerButton));
        Assert.Equal(new[] { HandoverText.NoPlayers }, w.PlayerMenuItems().Select(i => (string)i.Header!));
        Assert.False(w.PlayerMenuItems().Single().IsEnabled);

        var solo = _games.Game(players: false);
        Assert.False(solo.HotSeat!.Active);
        Assert.False(solo.PlayerButton.IsVisible);

        // Two players, then back to solo: shown, then hidden again.
        var g = _games.Game();
        Assert.True(g.PlayerButton.IsVisible);
        g.Session!.Game!.Execute(new SetPlayers(Array.Empty<PlayerDef>()));
        HotSeatGames.Pump();
        Assert.False(g.PlayerButton.IsVisible);
    }

    [AvaloniaFact]
    public void Shows_current_player()
    {
        var w = _games.Game(atScreen: false);
        Assert.True(w.PlayerButton.IsVisible);
        Assert.Equal("Player: —", w.PlayerButtonText);   // nobody has taken the screen yet
        HotSeatGames.Continue(w);
        Assert.Equal("Player: Anna", w.PlayerButtonText);
        HotSeatGames.ToCombat(w);
        Assert.Equal("Player: Anna", w.PlayerButtonText);   // Ben is asked for, Anna is still the current player
        HotSeatGames.Continue(w);
        Assert.Equal("Player: Ben", w.PlayerButtonText);
    }

    [AvaloniaFact]
    public void Menu_lists_players_with_sides_and_checks_current()
    {
        var w = _games.Game();
        var items = w.PlayerMenuItems();
        Assert.Equal(new[] { "Anna (Red)", "Ben (Blue)" }, items.Select(i => (string)i.Header!));
        Assert.Equal(new[] { true, false }, items.Select(i => i.IsChecked));
        Assert.All(items, i => Assert.Equal(MenuItemToggleType.CheckBox, i.ToggleType));
        Assert.All(items, i => Assert.True(i.IsEnabled));
        // The flyout is filled the same way each time it opens.
        var flyout = Assert.IsType<MenuFlyout>(w.PlayerButton.Flyout);
        w.PlayerButton.Flyout!.ShowAt(w.PlayerButton);
        HotSeatGames.Pump();
        Assert.Equal(new[] { "Anna (Red)", "Ben (Blue)" }, flyout.Items.OfType<MenuItem>().Select(i => (string)i.Header!));
        w.PlayerButton.Flyout.Hide();
        HotSeatGames.Pump();

        HotSeatGames.ToCombat(w);
        HotSeatGames.Continue(w);
        Assert.Equal(new[] { false, true }, w.PlayerMenuItems().Select(i => i.IsChecked));
    }

    [AvaloniaFact]
    public void Switch_shows_handover_first_with_empty_view()
    {
        var w = _games.Game();
        var s = w.Session!;
        Assert.NotNull(FormulaWindows.Show(w, s, "f-water", PhaseFixture.RedOne, null));
        HotSeatGames.Pump();
        Assert.Single(FormulaWindows.OpenFor(s));

        // The current player chosen: the handover screen still comes first.
        Click(w.PlayerMenuItems().Single(i => (string)i.Header! == "Anna (Red)"));
        Assert.True(w.HandoverShowing);
        Assert.Empty(s.ViewSides!);
        Assert.Empty(FormulaWindows.OpenFor(s));
        Assert.Equal("Pass to Anna (Red)", w.Handover!.Message);
        Assert.False(w.IsVisible);
        HotSeatGames.Continue(w);
        Assert.Equal(new[] { "red" }, s.ViewSides!.ToArray());
        Assert.Equal("Player: Anna", w.PlayerButtonText);

        // Another player: the leaving player's view goes at once, the next one's only after Continue.
        Click(w.PlayerMenuItems().Single(i => (string)i.Header! == "Ben (Blue)"));
        Assert.True(w.HandoverShowing);
        Assert.Empty(s.ViewSides!);
        Assert.Equal("Pass to Ben (Blue)", w.Handover!.Message);
        Assert.Equal(PlayersOf(w).Anna.Id, w.HotSeat!.Current?.Id);
        HotSeatGames.Continue(w);
        Assert.Equal(new[] { "blue" }, s.ViewSides!.ToArray());
        Assert.Equal(PlayersOf(w).Ben.Id, w.HotSeat!.Current?.Id);
        Assert.Equal("Player: Ben", w.PlayerButtonText);
        Assert.Equal("Player: Ben", w.StatusText);
    }

    [AvaloniaFact]
    public void Renamed_player_updates_button()
    {
        var w = _games.Game();
        w.Session!.Game!.Execute(new SetPlayers(new[] { HotSeatGames.Anna with { Name = "Annie" }, HotSeatGames.Ben }));
        HotSeatGames.Pump();
        Assert.Equal("Player: Annie", w.PlayerButtonText);
        Assert.Equal("Annie (Red)", (string)w.PlayerMenuItems()[0].Header!);
    }

    static (PlayerDef Anna, PlayerDef Ben) PlayersOf(MainWindow _) => (HotSeatGames.Anna, HotSeatGames.Ben);
}
