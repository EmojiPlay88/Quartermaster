using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Core;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Preferences;

/// <summary>Settings -> Game: the difficulty of the open game (switchable mid-game) and the one new games start with, and the open game's Players.</summary>
public sealed class GamePage : SettingsPage
{
    static readonly (Difficulty D, string Name)[] Modes =
    {
        (Difficulty.Hardcore, "Hardcore"), (Difficulty.SemiAuto, "Semi-automatic"), (Difficulty.FullyAuto, "Fully automatic"),
    };

    readonly SettingsContext _ctx;
    internal readonly ComboBox DifficultyBox = new() { Width = 160, ItemsSource = Modes.Select(m => m.Name).ToArray() };
    internal readonly TextBlock Hint = new() { Foreground = Brushes.Gray };
    /// <summary>The WinForms GroupBox "Players" (440×210): its title and its frame, holding the editor or <see cref="NoGameText"/>. It keeps the
    /// height but stretches to the page width (a fixed 440 px cut off the Remove buttons of a row with several sides).</summary>
    internal readonly TextBlock PlayersTitle = new() { Text = "Players", Margin = new Thickness(6, 0, 0, 2) };
    internal readonly Border PlayersBox = new() { Height = 210, BorderThickness = new Thickness(1), Padding = new Thickness(6) };
    internal readonly TextBlock NoGameText = new() { Text = "Open a game first", Foreground = Brushes.Gray };

    /// <summary>The open game's players (null without a game).</summary>
    internal PlayersEditor? Editor { get; private set; }

    public GamePage(SettingsContext ctx)
    {
        _ctx = ctx;
        var t = Table();
        Row(t, "Difficulty:", DifficultyBox);
        Full(t, Hint);
        PlayersBox.Bind(Border.BorderBrushProperty, PlayersBox.GetResourceObservable("Classic.BorderBrush"));
        var group = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        group.Children.Add(PlayersTitle);
        group.Children.Add(PlayersBox);
        Full(t, group);
        Content = Host(t);
        PlayersBox.Child = NoGameText;
    }

    public override string Title => "Game";

    public override void Load(Settings s)
    {
        var open = _ctx.GameDifficulty?.Invoke();
        DifficultyBox.SelectedIndex = Math.Max(0, Array.FindIndex(Modes, m => m.D == (open ?? s.LastDifficulty)));
        DifficultyBox.IsEnabled = open != null;
        Hint.Text = open != null ? "A change takes effect from the next calculation." : "Open a game first";
        LoadPlayers();
    }

    void LoadPlayers()
    {
        Editor = null;
        if (_ctx.GamePlayers?.Invoke() is not { } game) { PlayersBox.Child = NoGameText; return; }
        Editor = new PlayersEditor(game.Pack, game.Players);
        PlayersBox.Child = Editor;
    }

    /// <summary>A player list that cannot be used keeps the settings window open.</summary>
    public override string? Validate() => Editor?.Problem;

    Difficulty Chosen => DifficultyBox.SelectedIndex >= 0 ? Modes[DifficultyBox.SelectedIndex].D : Difficulty.SemiAuto;

    public override Settings Collect(Settings s) => DifficultyBox.IsEnabled ? s with { LastDifficulty = Chosen } : s;

    /// <summary>Switches the open game to the chosen difficulty, and gives it the edited player list when that differs.</summary>
    public override void AfterApply()
    {
        if (DifficultyBox.IsEnabled && _ctx.GameDifficulty?.Invoke() != null) _ctx.SetGameDifficulty?.Invoke(Chosen);
        if (Editor != null && _ctx.GamePlayers?.Invoke() is { } game && !PlayersEditModel.Same(game.Players, Editor.Result))
            _ctx.SetGamePlayers?.Invoke(Editor.Result);
    }
}
