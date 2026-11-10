using System.Drawing;
using Point = Avalonia.Point;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Quartermaster.Desktop.Documents;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 3b task 4: chart windows opened from the module tree. Headless, on an invented module with a generated chart image.</summary>
[Collection("MacFlag")]
public sealed class ChartWindowTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-chart-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();

    public ChartWindowTests() { ShortcutGestures.IsMac = false; Directory.CreateDirectory(_tmp); }

    public void Dispose()
    {
        foreach (var w in _windows) w.ForceClose();
        foreach (var f in _fixtures) f.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    string LayoutRoot => Path.Combine(_tmp, "layout");

    static void Pump() => Dispatcher.UIThread.RunJobs();

    MainWindow Game()
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            "<VASSAL.build.module.ChartWindow name=\"Charts\">" +
            "<VASSAL.build.widget.Chart chartName=\"Odds Table\" fileName=\"odds.png\"/>" +
            "<VASSAL.build.widget.Chart chartName=\"Turn Track\" fileName=\"turn.png\"/>" +
            "</VASSAL.build.module.ChartWindow></VASSAL.build.GameModule>");
        var fx = new FixtureModule().WithBuildFile(xml).AddImage("odds.png", 400, 200, new SKColor(0xC0, 0x60, 0x30)).AddImage("turn.png", 100, 300);
        _fixtures.Add(fx);
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, LayoutRoot) { Width = 1024, Height = 768 };
        w.ScreensProvider = () => new[] { new ScreenInfo(new Rectangle(0, 0, 1920, 1080), 96) };
        _windows.Add(w);
        w.Show();
        Assert.True(w.OpenModule(fx.Build()));
        w.StartSetup(null);
        Pump();
        return w;
    }

    static WindowTreeNode Node(ChartDef c) => new(c.Name, WindowNodeKind.Chart, c, Array.Empty<WindowTreeNode>());

    static ChartWindow Open(MainWindow w, int i = 0)
    {
        var c = w.Session!.Model.Charts[i];
        w.ModuleTree.Activate(Node(c));
        Pump();
        return w.ChartWindows[c.Name + "|" + c.FileName];
    }

    [AvaloniaFact]
    public void Tree_chart_node_opens_one_window_per_chart()
    {
        var w = Game();
        Assert.Equal(2, w.Session!.Model.Charts.Count);
        var a = Open(w, 0);
        var b = Open(w, 1);
        Assert.NotSame(a, b);
        Assert.Equal(2, w.ChartWindows.Count);
        Assert.Equal("Odds Table", a.Title);
        Assert.Equal("Turn Track", b.Title);
        Assert.Equal("Fit", a.FitButton.Content);
        Assert.Equal("100%", a.ActualButton.Content);
        Assert.Same(w, a.Owner);
        Assert.True(a.IsVisible);
    }

    [AvaloniaFact]
    public void Second_activation_activates_existing()
    {
        var w = Game();
        var a = Open(w);
        var again = Open(w);
        Assert.Same(a, again);
        Assert.Single(w.ChartWindows);
        a.Close();
        Pump();
        Assert.Empty(w.ChartWindows);
        Assert.NotSame(a, Open(w));   // closed: a new one opens
    }

    [AvaloniaFact]
    public void Fit_shows_whole_image_centered()
    {
        var w = Game();
        var c = Open(w);
        Pump();
        var size = c.Canvas.Bounds.Size;
        Assert.True(size.Width > 1 && size.Height > 1);
        // First layout fitted: 400x200 into the canvas.
        var expected = (float)Math.Min(size.Width / 400, size.Height / 200);
        Assert.Equal(expected, c.View.Zoom, 3);
        c.ActualSize();
        c.Fit();
        Assert.Equal(expected, c.View.Zoom, 3);
        // The image centre is at the canvas centre.
        var centre = c.View.ToScreen(new PointD(200, 100));
        Assert.Equal(size.Width / 2, centre.X, 2);
        Assert.Equal(size.Height / 2, centre.Y, 2);
    }

    [AvaloniaFact]
    public void Actual_size_is_zoom_1()
    {
        var w = Game();
        var c = Open(w);
        Pump();
        c.ActualButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal(1f, c.View.Zoom);
        var size = c.Canvas.Bounds.Size;
        var centre = c.View.ToScreen(new PointD(200, 100));
        Assert.Equal(size.Width / 2, centre.X, 2);
        Assert.Equal(size.Height / 2, centre.Y, 2);
        c.View.Zoom = 2f;
        c.FitButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.NotEqual(2f, c.View.Zoom);
    }

    [AvaloniaFact]
    public void Wheel_zooms_at_cursor()
    {
        var w = Game();
        var c = Open(w);
        Pump();
        c.ActualSize();
        var at = new Point(80, 60);
        var before = c.View.ToMap(new SKPoint((float)at.X, (float)at.Y));
        c.Wheel(at, 1);
        Assert.Equal(1.25f, c.View.Zoom, 3);
        var after = c.View.ToMap(new SKPoint((float)at.X, (float)at.Y));
        Assert.Equal(before.X, after.X, 2);
        Assert.Equal(before.Y, after.Y, 2);
        c.Wheel(at, -1);
        Assert.Equal(1f, c.View.Zoom, 3);
    }

    [AvaloniaFact]
    public void Drag_pans()
    {
        var w = Game();
        var c = Open(w);
        Pump();
        c.ActualSize();
        var pan = c.View.Pan;
        Assert.False(c.Panning);
        c.DragBegin(new Point(100, 100));
        Assert.True(c.Panning);
        Assert.NotNull(c.Canvas.Cursor);
        c.DragMove(new Point(130, 90));
        Assert.Equal(pan.X - 30, c.View.Pan.X, 2);
        Assert.Equal(pan.Y + 10, c.View.Pan.Y, 2);
        c.DragEnd();
        Assert.False(c.Panning);
        Assert.Null(c.Canvas.Cursor);
        c.DragMove(new Point(0, 0));   // no drag: nothing moves
        Assert.Equal(pan.X - 30, c.View.Pan.X, 2);
    }

    [AvaloniaFact]
    public void Windows_close_with_session()
    {
        var w = Game();
        var a = Open(w, 0);
        var b = Open(w, 1);
        var other = new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile());
        _fixtures.Add(other);
        Assert.True(w.OpenModule(other.Build()));   // replaces the session
        Pump();
        Assert.Empty(w.ChartWindows);
        Assert.False(a.IsVisible);
        Assert.False(b.IsVisible);
    }

    [AvaloniaFact]
    public void Bounds_remembered_under_chart_key()
    {
        var w = Game();
        var chart = w.Session!.Model.Charts[0];
        var sha = w.Session.Archive.Sha256;
        var c = Open(w);
        c.Position = new PixelPoint(210, 130);
        c.Width = 500;
        c.Height = 350;
        c.Close();
        Pump();
        var saved = new WindowLayoutStore(LayoutRoot).LoadModule(sha);
        var key = "chart:" + chart.Name + "|" + chart.FileName;
        Assert.True(saved.TryGetValue(key, out var sw));
        Assert.Equal(210, sw.Bounds.X);
        Assert.Equal(130, sw.Bounds.Y);
        Assert.Equal(500, sw.Bounds.Width);
        Assert.Equal(350, sw.Bounds.Height);
        // The other chart has no memory.
        Assert.False(saved.ContainsKey("chart:Turn Track|turn.png"));

        var again = Open(w);
        Assert.Equal(500, again.Width);
        Assert.Equal(350, again.Height);
        Assert.Equal(new PixelPoint(210, 130), again.Position);
    }

    [AvaloniaFact]
    public void Real_input_wheel_and_buttons()
    {
        var w = Game();
        var c = Open(w);
        Pump();
        c.ActualSize();
        var origin = c.Canvas.TranslatePoint(new Point(), c)!.Value;
        var at = origin + new Point(80, 60);

        // Wheel up zooms in at the cursor, down zooms out.
        var before = c.View.ToMap(new SKPoint(80, 60));
        c.MouseWheel(at, new Vector(0, 1));
        Assert.Equal(1.25f, c.View.Zoom, 3);
        var after = c.View.ToMap(new SKPoint(80, 60));
        Assert.Equal(before.X, after.X, 2);
        Assert.Equal(before.Y, after.Y, 2);
        c.MouseWheel(at, new Vector(0, -1));
        Assert.Equal(1f, c.View.Zoom, 3);

        // Left and middle drags pan; the cursor is back to default afterwards.
        foreach (var button in new[] { Avalonia.Input.MouseButton.Left, Avalonia.Input.MouseButton.Middle })
        {
            c.ActualSize();
            var pan = c.View.Pan;
            c.MouseDown(at, button);
            Assert.True(c.Panning);
            Assert.NotNull(c.Canvas.Cursor);
            c.MouseMove(at + new Point(20, -10));
            Assert.Equal(pan.X - 20, c.View.Pan.X, 2);
            Assert.Equal(pan.Y + 10, c.View.Pan.Y, 2);
            c.MouseUp(at + new Point(20, -10), button);
            Assert.False(c.Panning);
            Assert.Null(c.Canvas.Cursor);
        }

        // A right-button press and drag does not pan.
        c.ActualSize();
        var start = c.View.Pan;
        c.MouseDown(at, Avalonia.Input.MouseButton.Right);
        Assert.False(c.Panning);
        c.MouseMove(at + new Point(40, 40));
        c.MouseUp(at + new Point(40, 40), Avalonia.Input.MouseButton.Right);
        Assert.Equal(start, c.View.Pan);
        Assert.Null(c.Canvas.Cursor);
    }

    [AvaloniaFact]
    public void Device_zoom_follows_render_scaling()
    {
        Assert.Equal(2f, ChartWindow.DeviceZoom(1f, 2));
        Assert.Equal(0.5f, ChartWindow.DeviceZoom(0.25f, 2));
        Assert.Equal(1.5f, ChartWindow.DeviceZoom(1.5f, 1));
        var w = Game();
        var c = Open(w);
        c.SetRenderScaling(2);
        Pump();
        Assert.Equal(2, c.RenderScaling);
        c.ActualSize();   // still 1 image pixel per DIP
        Assert.Equal(1f, c.View.Zoom);
        c.Canvas.InvalidateVisual();
        Pump();
    }
}
