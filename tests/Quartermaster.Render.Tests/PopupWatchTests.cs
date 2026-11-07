using System.Drawing;
using Quartermaster.Render;
using Xunit;

namespace Quartermaster.Render.Tests;

public class PopupWatchTests
{
    static readonly Rectangle Popup = new(900, 300, 200, 120);

    [Fact]
    public void Cursor_inside_popup_keeps_it()
    {
        Assert.False(PopupWatch.ShouldHide(Popup, new Point(950, 350), cursorOverCanvas: false));
    }

    [Fact]
    public void Cursor_back_over_the_map_leaves_it_to_the_hover_logic()
    {
        Assert.False(PopupWatch.ShouldHide(Popup, new Point(500, 350), cursorOverCanvas: true));
    }

    [Fact]
    public void Cursor_off_popup_and_off_map_hides_it()
    {
        // e.g. the pointer left the popup's click-through area onto a docked panel past the map edge
        Assert.True(PopupWatch.ShouldHide(Popup, new Point(1150, 350), cursorOverCanvas: false));
    }

    [Fact]
    public void Popup_edge_counts_as_outside()
    {
        Assert.True(PopupWatch.ShouldHide(Popup, new Point(Popup.Right, Popup.Bottom), cursorOverCanvas: false));
    }
}
