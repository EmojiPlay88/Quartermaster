using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Shell;
using Quartermaster.Rules;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 5b task 2: the players editor control (one row per player, a check box per side). Invented sides only.</summary>
public sealed class PlayersEditorTests : IDisposable
{
    readonly List<Window> _windows = new();

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        Dispatcher.UIThread.RunJobs();
    }

    internal static RulesPack Pack(params string[] sides) => new()
    {
        Sides = sides.Select(s => new SideDef { Id = s.ToLowerInvariant(), Name = s }).ToList(),
    };

    PlayersEditor Show(RulesPack? pack, IReadOnlyList<PlayerDef>? players = null)
    {
        var e = new PlayersEditor(pack, players ?? Array.Empty<PlayerDef>());
        var w = new Window { Width = 500, Height = 300, Content = e };
        _windows.Add(w);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return e;
    }

    static void Click(Button b)
    {
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Add_and_remove_rows_follow_model()
    {
        var e = Show(Pack("Red", "Blue", "Green"));
        Assert.Equal("PlayersEditor", e.Name);
        Assert.Equal("_Add player", e.AddButton.Content);
        Assert.Equal($"Each side is held by exactly one player (at most {Players.MaxPlayers} players).", e.HintText.Text);
        var box = Assert.Single(e.NameBoxes);
        Assert.Equal("Player 1", box.Text);
        Assert.Equal(130, box.Width);
        Assert.Equal(new[] { "Red", "Blue", "Green" }, e.SideBoxes[0].Select(c => (string?)c.Content));
        Assert.All(e.SideBoxes[0], c => Assert.True(c.IsChecked));
        Assert.Equal("Remove", e.RemoveButtons[0].Content);
        Assert.False(e.RemoveButtons[0].IsEnabled);
        Assert.True(e.AddButton.IsEnabled);
        Assert.Empty(e.Result);

        Click(e.AddButton);
        Assert.Equal(2, e.NameBoxes.Count);
        Assert.Equal(2, e.Model.Rows.Count);
        Assert.Equal("Player 2", e.NameBoxes[1].Text);
        Assert.All(e.SideBoxes[1], c => Assert.False(c.IsChecked));
        Assert.All(e.RemoveButtons, b => Assert.True(b.IsEnabled));

        // Typing a name reaches the model at once (not through the posted TextChanged).
        e.NameBoxes[0].Text = "Anna";
        Assert.Equal("Anna", e.Model.Rows[0].Name);
        e.NameBoxes[1].Text = null;
        Assert.Equal("", e.Model.Rows[1].Name);
        e.NameBoxes[1].Text = "Ben";

        // A change made on the model shows in the editor.
        e.Model.Add();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, e.NameBoxes.Count);
        Assert.Equal("Player 3", e.NameBoxes[2].Text);

        Click(e.RemoveButtons[0]);
        Assert.Equal(new[] { "Ben", "Player 3" }, e.Model.Rows.Select(r => r.Name));
        Assert.Equal(new[] { "Ben", "Player 3" }, e.NameBoxes.Select(b => b.Text));
        Click(e.RemoveButtons[1]);
        Assert.Single(e.NameBoxes);
        Assert.False(e.RemoveButtons[0].IsEnabled);
        Assert.Empty(e.Result);   // one player = solo
    }

    [AvaloniaFact]
    public void Ticking_side_unticks_other_row()
    {
        var e = Show(Pack("Red", "Blue"));
        Click(e.AddButton);
        e.SideBoxes[1][0].IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(e.SideBoxes[0][0].IsChecked);
        Assert.True(e.SideBoxes[0][1].IsChecked);
        Assert.False(e.Model.Rows[0].Held[0]);
        Assert.True(e.Model.Rows[1].Held[0]);
        e.NameBoxes[0].Text = "Anna";
        e.NameBoxes[1].Text = "Ben";
        Assert.Null(e.Problem);
        var r = e.Result;
        Assert.Equal(new[] { "Anna", "Ben" }, r.Select(p => p.Name));
        Assert.Equal(new[] { "blue" }, r[0].Sides);
        Assert.Equal(new[] { "red" }, r[1].Sides);

        e.SideBoxes[1][0].IsChecked = false;   // Ben holds nothing now
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Ben holds no side.", e.Problem);
    }

    [AvaloniaFact]
    public void Add_disabled_without_two_sides()
    {
        var one = Show(new RulesPack { Sides = new() { new SideDef { Id = "red", Name = " " } } });
        Assert.False(one.AddButton.IsEnabled);
        Assert.Equal("The rules pack has fewer than two sides: one player plays everything.", one.HintText.Text);
        Assert.Equal("red", Assert.Single(one.SideBoxes[0]).Content);   // a side without a name shows its id

        var none = Show(null);
        Assert.False(none.AddButton.IsEnabled);
        Assert.Equal("Open a rules pack to set up players.", none.HintText.Text);
        Assert.Empty(none.SideBoxes[0]);
        Assert.Empty(none.Result);
        Assert.Null(none.Problem);
    }
}
