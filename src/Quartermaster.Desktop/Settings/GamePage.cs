using Avalonia.Controls;
using Avalonia.Media;
using Quartermaster.Core;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Preferences;

/// <summary>Settings -> Game: the difficulty of the open game (switchable mid-game) and the one new games start with. Players comes with multiplayer (stage 5).</summary>
public sealed class GamePage : SettingsPage
{
    static readonly (Difficulty D, string Name)[] Modes =
    {
        (Difficulty.Hardcore, "Hardcore"), (Difficulty.SemiAuto, "Semi-automatic"), (Difficulty.FullyAuto, "Fully automatic"),
    };

    readonly SettingsContext _ctx;
    internal readonly ComboBox DifficultyBox = new() { Width = 160, ItemsSource = Modes.Select(m => m.Name).ToArray() };
    internal readonly TextBlock Hint = new() { Foreground = Brushes.Gray };
    internal readonly TextBlock PlayersNote = new() { Text = "Players: comes with multiplayer (stage 5)", Foreground = Brushes.Gray };

    public GamePage(SettingsContext ctx)
    {
        _ctx = ctx;
        var t = Table();
        Row(t, "Difficulty:", DifficultyBox);
        Full(t, Hint);
        Full(t, PlayersNote);
        Content = Host(t);
    }

    public override string Title => "Game";

    public override void Load(Settings s)
    {
        var open = _ctx.GameDifficulty?.Invoke();
        DifficultyBox.SelectedIndex = Math.Max(0, Array.FindIndex(Modes, m => m.D == (open ?? s.LastDifficulty)));
        DifficultyBox.IsEnabled = open != null;
        Hint.Text = open != null ? "A change takes effect from the next calculation." : "Open a game first";
    }

    Difficulty Chosen => DifficultyBox.SelectedIndex >= 0 ? Modes[DifficultyBox.SelectedIndex].D : Difficulty.SemiAuto;

    public override Settings Collect(Settings s) => DifficultyBox.IsEnabled ? s with { LastDifficulty = Chosen } : s;

    /// <summary>Switches the open game to the chosen difficulty.</summary>
    public override void AfterApply()
    {
        if (DifficultyBox.IsEnabled && _ctx.GameDifficulty?.Invoke() != null) _ctx.SetGameDifficulty?.Invoke(Chosen);
    }
}
