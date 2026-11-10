using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Quartermaster.Desktop.Workbench;
using Quartermaster.Render.Search;
using Quartermaster.Rules;

namespace Quartermaster.Desktop.Tests;

public sealed class RuleRefRowTests
{
    static (RuleRefRow Row, Window W) Make()
    {
        var row = new RuleRefRow();
        var w = new Window { Width = 500, Height = 60, Content = row };
        w.Show();
        return (row, w);
    }

    [AvaloniaFact]
    public void None_shows_none_text_not_link()
    {
        var (row, w) = Make();
        row.Show(null, null);
        Assert.Equal("(none)", row.Text);
        Assert.False(row.IsLink);
        Assert.False(row.IsGreyed);
        row.Show(new RuleRef { Label = "Combat", Page = 4 }, null);
        Assert.Equal("Combat, page 4", row.Text);
        Assert.True(row.IsLink);
        w.Close();
    }

    [AvaloniaFact]
    public void Missing_ref_is_greyed_and_not_clickable()
    {
        var (row, w) = Make();
        var clicks = 0;
        row.LinkClicked += () => clicks++;
        row.Show(new RuleRef(), _ => new RefDisplay("Combat (missing)", false, true));
        Assert.True(row.IsGreyed);
        Assert.False(row.IsLink);
        Click(row, w);
        Assert.Equal(0, clicks);
        w.Close();
    }

    [AvaloniaFact]
    public void Link_click_raises_event()
    {
        var (row, w) = Make();
        var clicks = 0;
        row.LinkClicked += () => clicks++;
        row.Show(new RuleRef { Label = "Combat", Page = 4 }, null);
        Click(row, w);
        Assert.Equal(1, clicks);
        w.Close();
    }

    [AvaloniaFact]
    public void Find_button_hidden_until_enabled()
    {
        var (row, w) = Make();
        Assert.False(row.FindButton.IsVisible);
        Assert.Equal("Find in rulebook", row.FindButton.Content);
        Assert.Equal("Link to current page", row.LinkButton.Content);
        row.FindButton.IsVisible = true;
        Assert.True(row.FindButton.IsVisible);
        w.Close();
    }

    static void Click(RuleRefRow row, Window w)
    {
        var link = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text != "Rule ref:");
        var p = link.TranslatePoint(new Point(link.Bounds.Width / 2, link.Bounds.Height / 2), w)!.Value;
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
    }
}
