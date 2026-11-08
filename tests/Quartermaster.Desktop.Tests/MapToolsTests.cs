using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Map;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Desktop.Tests;

/// <summary>Map tools (stage 2a task 6): line of sight, hide counters, last-moved highlight and the map toolbar. Headless, on the Riverland
/// demo. Key handling reads ShortcutGestures.IsMac, so the class is in the MacFlag collection.</summary>
[Collection("MacFlag")]
public sealed class MapToolsTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    const int W = 800, H = 600;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-tools-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    ModuleSession? _s;

    public void Dispose()
    {
        foreach (var w in _windows) w.ForceClose();
        ShortcutGestures.IsMac = _wasMac;
        _s?.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Demo(string file, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", file));

    ModuleSession S => _s!;
    GameState St => _s!.Game!.State;

    (MapView View, Window Window, List<string> Messages) Open(double scaling = 1)
    {
        ShortcutGestures.IsMac = false;
        _s = ModuleSession.Open(Demo("Riverland-Demo.vmod"), Path.Combine(_tmp, "cache"), null);
        _s.StartNewGame(Difficulty.FullyAuto);
        var map = _s.Model.Maps[0];
        var view = new MapView(_s, map);
        foreach (var b in map.Boards)
            view.SetTiles(BoardTiler.TileKey(b), _s.Tiler.EnsureTilesAsync(b, null, CancellationToken.None).GetAwaiter().GetResult());
        var messages = new List<string>();
        view.StatusMessage += messages.Add;
        var window = new Window { Width = W, Height = H, Content = view };
        if (scaling != 1) window.SetRenderScaling(scaling);
        window.Show();
        view.Focus();
        return (view, window, messages);
    }

    List<(string Id, Point At)> LonePieces(MapView view)
    {
        var st = S.View!;
        var result = new List<(string, Point)>();
        foreach (var stack in st.StacksOn(view.Map.Id))
        {
            if (stack.PieceIds.Count != 1) continue;
            var id = stack.PieceIds[0];
            var sp = view.View.ToScreen(stack.Pos);
            if (sp.X < 20 || sp.Y < 20 || sp.X > W - 20 || sp.Y > H - 20) continue;
            if (HitTester.PieceAt(st, view.Map, S.Model, S.Images, stack.Pos) != id) continue;
            result.Add((id, new Point(sp.X, sp.Y)));
        }
        return result;
    }

    Point EmptySpotNear(MapView view, Point from)
    {
        foreach (var (dx, dy) in new[] { (90, 0), (-90, 0), (0, 90), (0, -90), (90, 90), (-90, -90), (90, -90), (-90, 90) })
        {
            var p = new Point(from.X + dx, from.Y + dy);
            if (p.X < 10 || p.Y < 10 || p.X > W - 10 || p.Y > H - 10) continue;
            if (HitTester.PieceAt(S.View!, view.Map, S.Model, S.Images, view.ScreenToMap(p)) == null) return p;
        }
        throw new InvalidOperationException("no empty spot");
    }

    static void Drag(Window w, Point from, Point to, bool release = true)
    {
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2), RawInputModifiers.LeftMouseButton);
        w.MouseMove(to, RawInputModifiers.LeftMouseButton);
        if (release) w.MouseUp(to, MouseButton.Left);
    }

    static void Key(Window w, Key key, string text) => w.KeyPress(key, RawInputModifiers.None, (PhysicalKey)Enum.Parse(typeof(PhysicalKey), key.ToString()), text);

    /// <summary>The window's frame as pixels (headless, never shown).</summary>
    static (int Width, int Height, SKColor[] Px) Frame(Window w)
    {
        using var f = w.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame");
        return TestHelpers.Pixels(f);
    }

    static int Differing((int Width, int Height, SKColor[] Px) a, (int Width, int Height, SKColor[] Px) b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        var n = 0;
        for (var i = 0; i < a.Px.Length; i++) if (a.Px[i] != b.Px[i]) n++;
        return n;
    }

    /// <summary>Two screen points on the map, a few hexes apart, with the LosTool's own answer for their map points.</summary>
    (Point A, Point B, string Expected) LosPoints(MapView view)
    {
        var a = new Point(300, 250);
        var b = new Point(520, 330);
        var tool = new LosTool(view.Map);
        tool.Begin(view.ScreenToMap(a));
        tool.Update(view.ScreenToMap(b));
        return (a, b, tool.Status()!);
    }

    [AvaloniaFact]
    public void L_starts_los_and_drag_measures()
    {
        var (view, window, messages) = Open();
        var (a, b, expected) = LosPoints(view);
        Assert.Matches(new Regex(@"^.+ → .+: \d+ (hexes|squares|px)$"), expected);
        var plain = Frame(window);

        Assert.False(view.LosEnabled);
        view.ToggleLos();
        Assert.True(view.LosEnabled);
        Assert.NotNull(view.Cursor);   // the cross hair

        window.MouseDown(a, MouseButton.Left);
        window.MouseMove(b, RawInputModifiers.LeftMouseButton);
        Assert.Equal(expected, view.LosStatus);
        Assert.Equal(expected, messages[^1]);   // the status bar message follows the drag
        window.MouseUp(b, MouseButton.Left);
        Assert.Equal(expected, view.LosStatus);   // the line stays after release
        Assert.True(view.HasLosLine);
        Assert.Empty(view.SelectedPieceIds);       // the tool consumed the click: no selection, no rubber band

        // Drawn in the view's own picture.
        Assert.True(Differing(plain, Frame(window)) > 50);

        // A new press starts a new line.
        window.MouseDown(b, MouseButton.Left);
        window.MouseUp(b, MouseButton.Left);
        Assert.NotEqual(expected, view.LosStatus);
    }

    [AvaloniaFact]
    public void Los_measures_hexes_at_scale_2()
    {
        // At render scale 2 and zoom 2 the pointer is in DIPs; the same map points give the same measurement as at zoom 1.
        var (view, window, _) = Open(scaling: 2);
        var (a, b, expected) = LosPoints(view);
        var mapA = view.ScreenToMap(a);
        var mapB = view.ScreenToMap(b);
        view.ToggleLos();
        view.ZoomCentered(2f);
        var a2 = view.View.ToScreen(mapA);
        var b2 = view.View.ToScreen(mapB);
        Assert.True(a2.X > 0 && a2.Y > 0 && b2.X < W && b2.Y < H, "points inside the view at zoom 2");

        window.MouseDown(new Point(a2.X, a2.Y), MouseButton.Left);
        window.MouseMove(new Point(b2.X, b2.Y), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(b2.X, b2.Y), MouseButton.Left);

        Assert.Equal(expected, view.LosStatus);
        // The device-resolution frame draws the line without throwing and differs from the plain map.
        var withLine = Frame(window);
        view.LosEnabled = false;
        Assert.Null(view.LosStatus);
        Assert.True(Differing(withLine, Frame(window)) > 50);
    }

    [AvaloniaFact]
    public void Esc_cancels_los()
    {
        var (view, window, messages) = Open();
        var (a, b, _) = LosPoints(view);
        view.ToggleLos();
        Drag(window, a, b);
        Assert.True(view.HasLosLine);

        Key(window, Avalonia.Input.Key.Escape, null!);
        Assert.False(view.HasLosLine);
        Assert.Null(view.LosStatus);
        Assert.True(view.LosEnabled);             // the mode stays on, only the line goes
        Assert.Equal("", messages[^1]);

        // Right-click cancels too, and the press is swallowed (no context menu).
        Drag(window, a, b);
        Assert.True(view.HasLosLine);
        window.MouseDown(a, MouseButton.Right);
        window.MouseUp(a, MouseButton.Right);
        Assert.False(view.HasLosLine);
        Assert.Null(view.OpenMenu);

        // Esc with no line leaves the key alone (and cancels a piece drag only outside the tool).
        view.LosEnabled = false;
        Assert.Null(view.Cursor);
    }

    [AvaloniaFact]
    public void H_hides_and_shows_counters()
    {
        var (view, window, messages) = Open();
        var (id, at) = LonePieces(view)[0];
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Assert.Equal(new[] { id }, view.SelectedPieceIds);
        var shown = Frame(window);
        var raised = new List<bool>();
        view.HideCountersChanged += raised.Add;

        view.ToggleHideCounters();
        Assert.True(view.HideCounters);
        Assert.Empty(view.SelectedPieceIds);       // WinForms clears the selection
        Assert.Equal("Counters hidden — press H to show", messages[^1]);
        var hidden = Frame(window);
        Assert.True(Differing(shown, hidden) > 200);

        // The board alone, as the shared renderer draws it without any counter.
        var reference = new MapRenderer(S.Images, new PieceRenderer(S.Images));
        using (var bmp = new SKBitmap(W, H))
        using (var canvas = new SKCanvas(bmp))
        {
            var tiles = new Dictionary<string, TileSet>();
            foreach (var b in view.Map.Boards) tiles[BoardTiler.TileKey(b)] = S.Tiler.EnsureTilesAsync(b, null, CancellationToken.None).GetAwaiter().GetResult();
            reference.Draw(canvas, view.Map, tiles, St, S.Model, view.View, new SKSize(W, H), new MapDrawOptions(false, new HashSet<string>(), null, HideAllPieces: true));
            // No counter pixels remain in the hidden frame at the piece's place: it equals the reference there.
            var px = hidden.Px[(int)at.Y * hidden.Width + (int)at.X];
            var r = bmp.GetPixel((int)at.X, (int)at.Y);
            Assert.True(Math.Abs(px.Red - r.Red) < 6 && Math.Abs(px.Green - r.Green) < 6 && Math.Abs(px.Blue - r.Blue) < 6, $"{px} vs {r}");
        }

        view.ToggleHideCounters();
        Assert.False(view.HideCounters);
        Assert.Equal("Counters shown", messages[^1]);
        Assert.Equal(new[] { true, false }, raised);
        Assert.True(Differing(hidden, Frame(window)) > 200);
    }

    [AvaloniaFact]
    public void Last_moved_highlights_after_move()
    {
        var (view, window, _) = Open();
        var (id, at) = LonePieces(view)[0];
        view.HighlightLastMoved = true;
        Assert.Empty(view.LastMovedIds);   // nothing moved yet

        Drag(window, at, EmptySpotNear(view, at));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { id }, view.LastMovedIds);
        view.SelectOnly(null);   // the moved piece stays selected, and the selection outline has the same colour
        var on = Frame(window);

        view.HighlightLastMoved = false;
        Assert.Empty(view.LastMovedIds);
        var gone = Differing(on, Frame(window));
        Assert.True(gone > 20, "differing pixels: " + gone);   // the outline is gone

        // Undo takes the highlight away with the move.
        view.HighlightLastMoved = true;
        Assert.Equal(new[] { id }, view.LastMovedIds);
        S.Game!.Undo();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(view.LastMovedIds);
    }

    [AvaloniaFact]
    public void Toolbar_buttons_toggle_state()
    {
        ShortcutGestures.IsMac = false;
        var dialogs = new FakeDialogs();
        var w = new MainWindow(dialogs, Path.Combine(_tmp, "cache"), null) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        Assert.True(w.OpenModule(Demo("Riverland-Demo.vmod")));
        w.StartSetup(null);
        Dispatcher.UIThread.RunJobs();
        var view = w.MapTabs.ActiveView!;
        var bar = w.MapTabs.ToolbarOf(view)!;   // the toolbar sits at the top of the map's own tab
        Assert.True(bar.IsVisible);
        Assert.Same(((Control)view.Parent!), bar.Parent);
        _s = w.Session;

        // Labels and order as WinForms (Save map as picture… waits for stage 3).
        Assert.Equal("Line of sight", bar.LosButton.Content);
        Assert.Equal("Last moved", bar.LastMovedButton.Content);
        Assert.Equal("Hide counters", bar.HideButton.Content);
        Assert.Equal("Centre on selected", bar.CentreButton.Content);
        Assert.Equal(new[] { "25%", "50%", "100%", "200%", "Fit" }, bar.ZoomItems.Select(i => (string)i.Header!));
        Assert.Equal(MapToolbar.HideTip, ToolTip.GetTip(bar.HideButton));
        Assert.False(bar.LosButton.Focusable);

        // Buttons drive the view...
        Click(bar.LosButton);
        Assert.True(view.LosEnabled);
        Click(bar.HideButton);
        Assert.True(view.HideCounters);
        Click(bar.LastMovedButton);
        Assert.True(view.HighlightLastMoved);

        // ...and follow it when the keyboard changes the state (L and H with the map focused).
        view.Focus();
        Key(w, Avalonia.Input.Key.L, "l");
        Assert.False(view.LosEnabled);
        Assert.False(bar.LosButton.IsChecked);
        Key(w, Avalonia.Input.Key.H, "h");
        Assert.False(view.HideCounters);
        Assert.False(bar.HideButton.IsChecked);

        // Zoom presets act on the view.
        bar.ZoomItems[3].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(2f, view.View.Zoom);
        bar.ZoomItems[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1f, view.View.Zoom);

        // Shortcuts do not fire while typing in a text box.
        Assert.False(ShortcutGate.ShouldDispatch(w.Commands.Shortcut("tool.los"), textFocused: true, mapFocused: false));

        // The toolbar follows the tab: switching away shows the other view's state, and the state is per view.
        if (w.MapTabs.Views.Count > 1)
        {
            w.MapTabs.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.NotSame(view, w.MapTabs.ActiveView);
            Assert.False(w.MapTabs.ToolbarOf(w.MapTabs.ActiveView!)!.HideButton.IsChecked);
        }
    }

    /// <summary>Headless screenshot of a map tab with the toolbar (Line of sight on) when QM_SHOT_DIR names a folder; otherwise just renders.</summary>
    [AvaloniaFact]
    public void Map_tab_with_toolbar_renders()
    {
        ShortcutGestures.IsMac = false;
        var w = new MainWindow(new FakeDialogs(), Path.Combine(_tmp, "cache"), null) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        w.OpenModule(Demo("Riverland-Demo.vmod"));
        w.StartSetup(null);
        PumpTiles(w);
        var view = w.MapTabs.ActiveView!;
        view.LosEnabled = true;
        var a = new Point(300, 250);
        view.ZoomToFit();
        w.MouseDown(w.MapTabs.TranslatePoint(new Point(200, 200), w) ?? default, MouseButton.Left);
        w.MouseUp(w.MapTabs.TranslatePoint(new Point(200, 200), w) ?? default, MouseButton.Left);
        if (Environment.GetEnvironmentVariable("QM_SHOT_DIR") is { Length: > 0 } dir)
            TestHelpers.CaptureToPng(w, Path.Combine(dir, "map-toolbar.png"));
        else using (w.CaptureRenderedFrame()) { }
    }

    static void PumpTiles(MainWindow w)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (w.MapTabs.ActiveView?.Preparing != null && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Dispatcher.UIThread.RunJobs();
    }

    static void Click(ToggleButton b)
    {
        b.IsChecked = !b.IsChecked;   // what a click does; IsCheckedChanged drives the view
        Dispatcher.UIThread.RunJobs();
    }
}
