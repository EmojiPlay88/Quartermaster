using System.Drawing;
using Quartermaster.Render;

namespace Quartermaster.Render.Tests;

public class PanelDockStateTests
{
    static PanelDockState Make() => new("phases", () => new RectangleI(1, 2, 3, 4));

    [Fact]
    public void Dock_state_toggle_popout_dockback()
    {
        var s = Make(); var changes = 0; s.Changed += () => changes++;
        Assert.True(s.Wanted); Assert.False(s.Floating);
        s.Toggle(); Assert.False(s.Wanted);
        s.Toggle(); Assert.True(s.Wanted);
        s.PopOut(); Assert.True(s.Floating); Assert.Equal(new RectangleI(1, 2, 3, 4), s.FloatBounds);
        s.Wanted = false; s.Toggle(); // floating: docks back and wants
        Assert.False(s.Floating); Assert.True(s.Wanted);
        Assert.True(changes > 0);
        s.PopOut(); s.DockBack(); Assert.False(s.Floating);
    }

    [Fact]
    public void Reveal_shows_hidden_panel_in_last_place()
    {
        var s = Make(); s.Toggle(); Assert.False(s.Wanted);
        s.Reveal(); Assert.True(s.Wanted); Assert.False(s.Floating);
        s.PopOut(); s.Wanted = false; s.Reveal(); Assert.True(s.Wanted); Assert.True(s.Floating);
    }

    [Fact]
    public void Dock_state_roundtrips_saved_window()
    {
        var s = Make();
        Assert.Null(s.Save());
        s.Load(new SavedWindow(true, new Rectangle(10, 20, 300, 400), false, 144));
        Assert.True(s.Floating); Assert.Equal(new RectangleI(10, 20, 300, 400), s.FloatBounds);
        Assert.Equal(new SavedWindow(true, new Rectangle(10, 20, 300, 400), false, 144), s.Save());
        s.DockBack();
        Assert.False(s.Save()!.Value.Floating);
    }

    [Fact]
    public void Default_float_rects_match_winforms()
    {
        var main = new RectangleI(100, 50, 1200, 800); // right 1300, bottom 850
        Assert.Equal(new RectangleI(960, 170, 320, 520), PanelDockState.PhasesDefault(main));
        Assert.Equal(new RectangleI(140, 530, 1120, 260), PanelDockState.LogDefault(main));
        Assert.Equal(480, PanelDockState.LogDefault(new RectangleI(0, 0, 400, 600)).W);
        Assert.Equal(new RectangleI(840, 150, 440, 520), PanelDockState.OutlinerDefault(main));   // WinForms OutlinerDock: (right - 460, top + 100, 440, 520)
    }
}
