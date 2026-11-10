using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Workbench;
using SkiaSharp;
using SDRect = System.Drawing.Rectangle;

namespace Quartermaster.Desktop.Tests;

/// <summary>ImagePane: region drag, handles, zoom and fit, message. Headless only; the wheel reads ShortcutGestures.IsMac, so MacFlag.</summary>
[Collection("MacFlag")]
public sealed class ImagePaneTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    const int W = 400, H = 300;

    public void Dispose() => ShortcutGestures.IsMac = _wasMac;

    static SKBitmap Bmp(int w, int h)
    {
        var b = new SKBitmap(w, h);
        using var c = new SKCanvas(b);
        c.Clear(SKColors.White);
        using var p = new SKPaint { Color = SKColors.Red };
        c.DrawRect(0, 0, w / 4f, h / 4f, p);
        return b;
    }

    static (ImagePane Pane, Window Window) Make(SKBitmap? image, bool mac = false)
    {
        ShortcutGestures.IsMac = mac;
        var pane = new ImagePane();
        var window = new Window { Width = W, Height = H, Content = pane };
        window.Show();
        pane.SetImage(image, null);
        return (pane, window);
    }

    static void Drag(Window w, Point from, Point to, MouseButton b = MouseButton.Left)
    {
        w.MouseDown(from, b);
        w.MouseMove(to);
        w.MouseUp(to, b);
    }

    [AvaloniaFact]
    public void Drag_selects_region_in_image_pixels()
    {
        using var bmp = Bmp(200, 100);
        var (pane, w) = Make(bmp);
        pane.SetZoom(1); // 200x100 centred in 400x300: origin (100, 100)
        var raised = new List<SDRect?>();
        pane.SelectionChanged += raised.Add;

        Drag(w, new Point(120, 110), new Point(220, 160));
        w.Close();

        Assert.Equal(new SDRect(20, 10, 100, 50), pane.SelectedRegion);
        Assert.Equal([new SDRect(20, 10, 100, 50)], raised);
    }

    [AvaloniaFact]
    public void Tiny_drag_selects_nothing()
    {
        using var bmp = Bmp(200, 100);
        var (pane, w) = Make(bmp);
        pane.SetZoom(1);
        var raised = 0;
        pane.SelectionChanged += _ => raised++;

        Drag(w, new Point(150, 150), new Point(152, 151));
        w.Close();

        Assert.Null(pane.SelectedRegion);
        Assert.Equal(0, raised);
    }

    [AvaloniaFact]
    public void Handle_drag_resizes_and_move_moves()
    {
        using var bmp = Bmp(200, 100);
        var (pane, w) = Make(bmp);
        pane.SetZoom(1);
        pane.CommitRegion(new SDRect(20, 20, 100, 50)); // view x 120..220, y 120..170
        var raised = new List<SDRect?>();
        pane.SelectionChanged += raised.Add;

        Drag(w, new Point(220, 145), new Point(240, 145)); // east handle, 20 px out
        Assert.Equal(new SDRect(20, 20, 120, 50), pane.SelectedRegion);

        Drag(w, new Point(170, 145), new Point(180, 150)); // inside: move by (10, 5)
        w.Close();

        Assert.Equal(new SDRect(30, 25, 120, 50), pane.SelectedRegion);
        Assert.Equal(2, raised.Count);
    }

    [AvaloniaFact]
    public void Primary_wheel_zooms_at_cursor_within_limits()
    {
        using var bmp = Bmp(800, 600);
        var (pane, w) = Make(bmp);
        pane.SetZoom(1);
        var zooms = 0;
        pane.ZoomChanged += () => zooms++;

        w.MouseWheel(new Point(200, 150), new Vector(0, 1), RawInputModifiers.Control);
        Assert.Equal(1.5, pane.Zoom, 6);
        Assert.False(pane.FitMode);
        Assert.Equal(1, zooms);
        // The image point under the cursor stayed (about) there: a drag from the cursor starts at image (400, 300)
        // (SetZoom(1) from fit kept the view centre, image (400, 300), in place; that point is under the cursor).
        Drag(w, new Point(200, 150), new Point(260, 210));
        Assert.NotNull(pane.SelectedRegion);
        Assert.InRange(pane.SelectedRegion!.Value.X, 399, 401);
        Assert.InRange(pane.SelectedRegion!.Value.Y, 299, 301);
        Assert.InRange(pane.SelectedRegion!.Value.Width, 39, 41);

        for (int i = 0; i < 12; i++) w.MouseWheel(new Point(200, 150), new Vector(0, 1), RawInputModifiers.Control);
        Assert.Equal(4.0, pane.Zoom, 6);
        for (int i = 0; i < 12; i++) w.MouseWheel(new Point(200, 150), new Vector(0, -1), RawInputModifiers.Control);
        Assert.Equal(0.25, pane.Zoom, 6);
        pane.SetZoom(100);
        Assert.Equal(4.0, pane.Zoom, 6);
        w.Close();
    }

    [AvaloniaFact]
    public void Plain_wheel_scrolls_and_does_not_zoom()
    {
        using var bmp = Bmp(800, 600);
        var (pane, w) = Make(bmp);
        pane.SetZoom(1);
        w.MouseWheel(new Point(200, 150), new Vector(0, -1)); // down: the view moves down the image
        Drag(w, new Point(10, 10), new Point(60, 60));
        w.Close();

        Assert.Equal(1.0, pane.Zoom, 6);
        Assert.NotNull(pane.SelectedRegion);
        Assert.True(pane.SelectedRegion!.Value.Y > 10, "the view did not scroll");
    }

    [AvaloniaFact]
    public void Mac_cmd_wheel_zooms()
    {
        using var bmp = Bmp(800, 600);
        var (pane, w) = Make(bmp, mac: true);
        pane.SetZoom(1);
        w.MouseWheel(new Point(200, 150), new Vector(0, 1), RawInputModifiers.Control); // Ctrl is not the primary key on a Mac
        Assert.Equal(1.0, pane.Zoom, 6);
        w.MouseWheel(new Point(200, 150), new Vector(0, 1), RawInputModifiers.Meta);
        w.Close();
        Assert.Equal(1.5, pane.Zoom, 6);
    }

    [AvaloniaFact]
    public void Fit_shows_whole_image()
    {
        using var bmp = Bmp(800, 600);
        var (pane, w) = Make(bmp);
        Assert.True(pane.FitMode);
        Assert.Equal(0.5, pane.Zoom, 6); // 800x600 into 400x300
        pane.SetZoom(2);
        Assert.False(pane.FitMode);
        pane.FitToWindow();
        Assert.True(pane.FitMode);
        Assert.Equal(0.5, pane.Zoom, 6);
        // In fit mode a drag over the whole view selects the whole image.
        Drag(w, new Point(0, 0), new Point(399, 299));
        w.Close();
        Assert.Equal(new SDRect(0, 0, 798, 598), pane.SelectedRegion);
    }

    [AvaloniaFact]
    public void Message_shown_without_image()
    {
        var (pane, w) = Make(null);
        pane.Message = "Opening Chart.png…";
        Assert.Null(pane.Image);
        Assert.Equal("Opening Chart.png…", pane.Message);
        using var frame = w.CaptureRenderedFrame()!;
        var (width, _, px) = TestHelpers.Pixels(frame);
        bool dark = false;
        for (int y = 12; y < 40 && !dark; y++)
            for (int x = 12; x < 200; x++)
                if (px[y * width + x].Red < 80) { dark = true; break; }
        w.Close();
        Assert.True(dark, "the message text was not drawn");
    }

    [AvaloniaFact]
    public void Commit_region_raises_selection_changed()
    {
        using var bmp = Bmp(200, 100);
        var (pane, w) = Make(bmp);
        var raised = new List<SDRect?>();
        pane.SelectionChanged += raised.Add;
        pane.CommitRegion(new SDRect(-10, 10, 100, 500)); // clipped to the image
        pane.CommitRegion(null);
        w.Close();
        Assert.Equal<SDRect?>([new SDRect(0, 10, 90, 90), null], raised);
        Assert.Null(pane.SelectedRegion);
    }

    [AvaloniaFact]
    public void Set_region_does_not_raise()
    {
        using var bmp = Bmp(200, 100);
        var (pane, w) = Make(bmp);
        var raised = 0;
        pane.SelectionChanged += _ => raised++;
        pane.SetRegion(new SDRect(1, 2, 30, 40));
        w.Close();
        Assert.Equal(0, raised);
        Assert.Equal(new SDRect(1, 2, 30, 40), pane.SelectedRegion);
    }

    [AvaloniaFact]
    public void Lost_capture_abandons_the_gesture()
    {
        using var bmp = Bmp(200, 100);
        var (pane, w) = Make(bmp);
        pane.SetZoom(1);
        var raised = 0;
        pane.SelectionChanged += _ => raised++;

        w.MouseDown(new Point(120, 110), MouseButton.Left);
        w.MouseMove(new Point(220, 160));
        pane.RaiseEvent(new PointerCaptureLostEventArgs(pane, new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true)));
        w.MouseMove(new Point(260, 190)); // no button is down any more
        w.MouseUp(new Point(260, 190), MouseButton.Left);
        w.Close();

        Assert.Null(pane.SelectedRegion);
        Assert.Equal(0, raised);
    }

    [AvaloniaFact]
    public void Scroll_extents_follow_zoom()
    {
        using var bmp = Bmp(800, 600);
        var (pane, w) = Make(bmp);
        Assert.False(pane.HasHorizontalScrollBar || pane.HasVerticalScrollBar); // fit: nothing to scroll
        Assert.Equal(new System.Drawing.Size(400, 300), pane.ScrollExtent);

        pane.SetZoom(1);
        Assert.Equal(new System.Drawing.Size(800, 600), pane.ScrollExtent);
        Assert.True(pane.HasHorizontalScrollBar && pane.HasVerticalScrollBar);

        pane.SetZoom(2);
        Assert.Equal(new System.Drawing.Size(1600, 1200), pane.ScrollExtent);

        var bars = pane.Children.OfType<Avalonia.Controls.Primitives.ScrollBar>().ToList();
        var h = bars.Single(b => b.Orientation == Avalonia.Layout.Orientation.Horizontal);
        var v = bars.Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        Assert.Equal(1600 - (W - 16), h.Maximum, 3); // the vertical bar takes 16 px of the view width
        Assert.Equal(1200 - (H - 16), v.Maximum, 3);
        h.Value = 100;
        v.Value = 50;
        Assert.Equal(new System.Drawing.Point(100, 50), pane.ScrollOffset);

        pane.SetZoom(0.25);
        Assert.False(pane.HasHorizontalScrollBar || pane.HasVerticalScrollBar);
        w.Close();
    }
}
