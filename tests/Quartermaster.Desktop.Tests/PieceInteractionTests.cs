using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Map;
using Quartermaster.Render;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Desktop.Tests;

/// <summary>Piece interaction on the MapView as in the WinForms MapCanvas: select, rubber band, drag/snap/stack, Esc, undo/redo. Headless
/// pointer and key events on the Riverland demo. The add-to-selection modifier and shortcuts read ShortcutGestures.IsMac: MacFlag collection.</summary>
[Collection("MacFlag")]
public sealed class PieceInteractionTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    const int W = 800, H = 600;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-pieces-" + Guid.NewGuid().ToString("N"));
    ModuleSession? _s;

    public void Dispose()
    {
        ShortcutGestures.IsMac = _wasMac;
        _s?.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Demo(string file, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", file));

    ModuleSession S => _s!;
    GameState St => _s!.Game!.State;

    (MapView View, Window Window, Dictionary<string, TileSet> Tiles) Open(bool mac = false, double scaling = 1, bool rules = false)
    {
        ShortcutGestures.IsMac = mac;
        _s = ModuleSession.Open(Demo("Riverland-Demo.vmod"), Path.Combine(_tmp, "cache"), null);
        _s.StartNewGame(Difficulty.FullyAuto);
        if (rules) _s.OpenRules(Demo("Riverland-Demo.qrules"));
        var map = _s.Model.Maps[0];
        var view = new MapView(_s, map);
        var tiles = new Dictionary<string, TileSet>();
        foreach (var b in map.Boards)
        {
            var ts = _s.Tiler.EnsureTilesAsync(b, null, CancellationToken.None).GetAwaiter().GetResult();
            tiles[BoardTiler.TileKey(b)] = ts;
            view.SetTiles(BoardTiler.TileKey(b), ts);
        }
        var window = new Window { Width = W, Height = H, Content = view };
        if (scaling != 1) window.SetRenderScaling(scaling);
        window.Show();
        return (view, window, tiles);
    }

    /// <summary>Pieces alone in their stack on the view's map, whose centre is inside the view and hit-tests to themselves.</summary>
    List<(string Id, Point At)> LonePieces(MapView view, Func<string, bool>? where = null)
    {
        var st = S.View!;
        var result = new List<(string, Point)>();
        foreach (var stack in st.StacksOn(view.Map.Id))
        {
            if (stack.PieceIds.Count != 1) continue;
            var id = stack.PieceIds[0];
            if (where != null && !where(id)) continue;
            var sp = view.View.ToScreen(stack.Pos);
            if (sp.X < 20 || sp.Y < 20 || sp.X > W - 20 || sp.Y > H - 20) continue;
            if (HitTester.PieceAt(st, view.Map, S.Model, S.Images, stack.Pos) != id) continue;
            result.Add((id, new Point(sp.X, sp.Y)));
        }
        return result;
    }

    static void Click(Window w, Point p, RawInputModifiers mods = RawInputModifiers.None)
    {
        w.MouseDown(p, MouseButton.Left, mods);
        w.MouseUp(p, MouseButton.Left, mods);
    }

    static void Drag(Window w, Point from, Point to, bool release = true)
    {
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2), RawInputModifiers.LeftMouseButton);
        w.MouseMove(to, RawInputModifiers.LeftMouseButton);
        if (release) w.MouseUp(to, MouseButton.Left);
    }

    /// <summary>A point far enough from <paramref name="from"/> that hits no piece.</summary>
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

    [AvaloniaFact]
    public void Click_selects_piece()
    {
        var (view, window, _) = Open();
        var pieces = LonePieces(view);
        Assert.True(pieces.Count >= 2, "demo has no lone pieces in view");
        var changed = 0;
        view.SelectionChanged += (_, _) => changed++;

        Click(window, pieces[0].At);
        Assert.Equal(new[] { pieces[0].Id }, view.SelectedPieceIds);
        Click(window, pieces[1].At);
        Assert.Equal(new[] { pieces[1].Id }, view.SelectedPieceIds);
        Assert.True(changed >= 2, "SelectionChanged not raised");

        // Ctrl+click adds (Windows/Linux); a click on empty map clears.
        Click(window, pieces[0].At, RawInputModifiers.Control);
        Assert.Equal(new[] { pieces[0].Id, pieces[1].Id }.OrderBy(x => x), view.SelectedPieceIds.OrderBy(x => x));
        Click(window, EmptySpotNear(view, pieces[0].At));
        Assert.Empty(view.SelectedPieceIds);
        window.Close();
    }

    [AvaloniaFact]
    public void Rubber_band_selects()
    {
        var (view, window, _) = Open();
        var a = new Point(3, 3);
        var b = new Point(W - 3, H - 3);
        Assert.Null(HitTester.PieceAt(S.View!, view.Map, S.Model, S.Images, view.ScreenToMap(a)));
        var expected = SelectionRect.PieceIdsIn(S.View!, view.Map, SelectionRect.FromCorners(view.ScreenToMap(a), view.ScreenToMap(b)), S.Images);
        Assert.True(expected.Count > 1);

        Drag(window, a, b);
        window.Close();

        Assert.Equal(expected.OrderBy(x => x), view.SelectedPieceIds.OrderBy(x => x));
    }

    [AvaloniaFact]
    public void Drag_moves_and_snaps()
    {
        var (view, window, _) = Open();
        var (id, at) = LonePieces(view)[0];
        var to = EmptySpotNear(view, at);
        var before = St;
        var grab = view.ScreenToMap(at);
        var offset = new PointD(before.Pieces[id].Pos.X - grab.X, before.Pieces[id].Pos.Y - grab.Y);
        var expected = DropResolver.ResolveFormation(before, view.Map, view.ScreenToMap(to), offset, id, new[] { id }, S.Images, snap: true);

        var log = S.Game!.Log.Count;
        Drag(window, at, to);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.Close();

        // The view's own drop: one move, the drag ended normally, the moved piece stays selected.
        var p = St.Pieces[id];
        Assert.Equal(log + 1, S.Game!.Log.Count);
        Assert.IsType<MovePieces>(S.Game.Log[^1]);
        Assert.Equal(new[] { id }, view.SelectedPieceIds);
        Assert.Equal(expected[0].Pos, p.Pos);
        Assert.Equal(view.Map.Id, p.MapId);
        Assert.NotEqual(view.ScreenToMap(to), p.Pos); // snapped to the grid, not the raw cursor point
        Assert.False(view.IsDragging);
    }

    [AvaloniaFact]
    public void Drop_on_piece_stacks()
    {
        var (view, window, _) = Open();
        var pieces = LonePieces(view);
        var (a, atA) = pieces[0];
        var (b, atB) = pieces[1];

        Drag(window, atA, atB);
        window.Close();

        Assert.NotNull(St.Pieces[a].StackId);
        Assert.Equal(St.Pieces[b].StackId, St.Pieces[a].StackId);
        Assert.Equal(2, St.Stacks[St.Pieces[b].StackId!].PieceIds.Count);
    }

    [AvaloniaFact]
    public void Esc_cancels_drag()
    {
        var (view, window, _) = Open();
        var (id, at) = LonePieces(view)[0];
        var to = EmptySpotNear(view, at);
        var pos = St.Pieces[id].Pos;
        var log = St.Log.Count;

        Drag(window, at, to, release: false);
        Assert.True(view.IsDragging);
        window.KeyPress(Key.Escape, RawInputModifiers.LeftMouseButton, PhysicalKey.Escape, null);
        Assert.False(view.IsDragging);
        window.MouseUp(to, MouseButton.Left);
        window.Close();

        Assert.Equal(pos, St.Pieces[id].Pos);
        Assert.Equal(log, St.Log.Count);
    }

    [AvaloniaFact]
    public void Zoom_is_ignored_mid_drag()
    {
        var (view, window, _) = Open();
        var (id, at) = LonePieces(view)[0];
        var to = EmptySpotNear(view, at);
        var zoom = view.View.Zoom;
        var pan = view.View.Pan;

        Drag(window, at, to, release: false);
        Assert.True(view.IsDragging);
        window.MouseWheel(to, new Vector(0, 1));
        Assert.Equal(zoom, view.View.Zoom);
        Assert.Equal(pan, view.View.Pan);
        Assert.True(view.IsDragging);
        window.MouseUp(to, MouseButton.Left);
        window.Close();
        Assert.NotEqual(view.ScreenToMap(at), St.Pieces[id].Pos);
    }

    [AvaloniaFact]
    public void Scaling_change_mid_drag_cancels_it()
    {
        var (view, window, _) = Open();
        var (id, at) = LonePieces(view)[0];
        var to = EmptySpotNear(view, at);
        var pos = St.Pieces[id].Pos;

        Drag(window, at, to, release: false);
        Assert.True(view.IsDragging);
        window.SetRenderScaling(2);   // the snapshot was taken for the old device resolution
        using (window.CaptureRenderedFrame()) { }
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(view.IsDragging);
        window.MouseUp(to, MouseButton.Left);
        window.Close();
        Assert.Equal(pos, St.Pieces[id].Pos);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Undo_redo_move(bool mac)
    {
        var (view, window, _) = Open(mac);
        var phases = new PhaseController(S, (c, st) => CommandLog.Prepare(c, st, S));
        var history = new HistoryController(S, phases, (c, st) => CommandLog.Prepare(c, st, S));
        var actions = new LocalActions(S, phases, history);
        view.Actions = () => actions;
        var router = new CommandRouter();
        HistoryCommands.Register(router, S, history, () => actions);
        router.Attach(window);

        var (id, at) = LonePieces(view)[0];
        var to = EmptySpotNear(view, at);
        var start = St.Pieces[id].Pos;
        Drag(window, at, to);
        var moved = St.Pieces[id].Pos;
        Assert.NotEqual(start, moved);

        var cmd = mac ? RawInputModifiers.Meta : RawInputModifiers.Control;
        var other = mac ? RawInputModifiers.Control : RawInputModifiers.Meta;
        window.KeyPress(Key.Z, other, PhysicalKey.Z, "z");          // the other platform's modifier does nothing
        Assert.Equal(moved, St.Pieces[id].Pos);
        window.KeyPress(Key.Z, cmd, PhysicalKey.Z, "z");
        Assert.Equal(start, St.Pieces[id].Pos);
        window.KeyPress(Key.Y, cmd, PhysicalKey.Y, "y");
        Assert.Equal(moved, St.Pieces[id].Pos);
        window.KeyPress(Key.Z, cmd, PhysicalKey.Z, "z");
        Assert.Equal(start, St.Pieces[id].Pos);
        window.KeyPress(Key.Z, cmd | RawInputModifiers.Shift, PhysicalKey.Z, "Z");   // edit.redoAlt
        Assert.Equal(moved, St.Pieces[id].Pos);
        window.Close();
    }

    [AvaloniaFact]
    public void Mac_multiselect_uses_meta()
    {
        var (view, window, _) = Open(mac: true);
        var pieces = LonePieces(view);
        Assert.True(pieces.Count >= 3);

        Click(window, pieces[0].At);
        Click(window, pieces[1].At, RawInputModifiers.Meta);
        Assert.Equal(new[] { pieces[0].Id, pieces[1].Id }.OrderBy(x => x), view.SelectedPieceIds.OrderBy(x => x));
        Click(window, pieces[2].At, RawInputModifiers.Control);
        Assert.Equal(new[] { pieces[2].Id }, view.SelectedPieceIds);
        window.Close();
    }

    [AvaloniaFact]
    public void Move_locked_piece_does_not_move()
    {
        var (view, window, _) = Open();
        var (id, at) = LonePieces(view)[0];
        // Wrap the demo counter in a move-locked Immobile trait (VASSAL "immob;;V"), as a module would define it.
        var p = St.Pieces[id];
        p.TypeString = PieceTypeParser.EncodeState(new[] { "immob;;V", p.TypeString });
        p.TraitStates.Insert(0, "");
        Assert.False(PieceActions.CanMove(p));
        var pos = p.Pos;
        var log = St.Log.Count;

        Drag(window, at, EmptySpotNear(view, at));
        window.Close();

        Assert.False(view.IsDragging);
        Assert.Equal(pos, St.Pieces[id].Pos);
        Assert.Equal(log, St.Log.Count);
    }

    [AvaloniaFact]
    public void Other_sides_piece_moves_in_hot_seat()
    {
        // Parity with WinForms: the map never filters drags by side; only move-locked pieces and the action port do.
        var (view, window, _) = Open(rules: true);
        S.SetViewSides(new HashSet<string> { "north" });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var theirs = LonePieces(view, id => S.CanSee(id) && !S.CanAct(id));
        Assert.NotEmpty(theirs);
        var (id, at) = theirs[0];
        var pos = St.Pieces[id].Pos;

        Drag(window, at, EmptySpotNear(view, at));
        window.Close();

        Assert.NotEqual(pos, St.Pieces[id].Pos);
    }

    [AvaloniaFact]
    public void A_burst_of_view_changes_queues_one_refresh()
    {
        var (view, window, _) = Open(rules: true);
        S.SetViewSides(new HashSet<string> { "north" });   // hot-seat: view refreshes only matter with view sides
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var runs = view.ViewRefreshRuns;
        for (var i = 0; i < 10; i++) S.Game!.Execute(new AddLogLine(LogKind.Chat, "line " + i));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(runs + 1, view.ViewRefreshRuns);

        // Solo (no view sides now or before): nothing is queued at all, as WinForms.
        S.SetViewSides(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        runs = view.ViewRefreshRuns;
        for (var i = 0; i < 10; i++) S.Game!.Execute(new AddLogLine(LogKind.Chat, "solo " + i));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.Close();
        Assert.Equal(runs, view.ViewRefreshRuns);
    }

    [AvaloniaFact]
    public void Game_change_during_drag_cancels_it()
    {
        // WinForms MapWindow.OnGameChanged -> MapCanvas.PruneSelection: the drag snapshot is stale, so the drag is cancelled.
        var (view, window, _) = Open();
        var (id, at) = LonePieces(view)[0];
        var to = EmptySpotNear(view, at);
        var pos = St.Pieces[id].Pos;
        var log = S.Game!.Log.Count;

        Drag(window, at, to, release: false);
        Assert.True(view.IsDragging);
        S.Game.Execute(new AddLogLine(LogKind.Chat, "from elsewhere"));
        Assert.False(view.IsDragging);
        window.MouseUp(to, MouseButton.Left);
        window.Close();

        Assert.Equal(pos, St.Pieces[id].Pos);
        Assert.Equal(log + 1, S.Game.Log.Count);   // only the other command; nothing from the drag
        Assert.IsType<AddLogLine>(S.Game.Log[^1]);
    }

    [AvaloniaFact]
    public void Undo_during_drag_cancels_it()
    {
        var (view, window, _) = Open();
        var phases = new PhaseController(S, (c, st) => CommandLog.Prepare(c, st, S));
        var history = new HistoryController(S, phases, (c, st) => CommandLog.Prepare(c, st, S));
        var pieces = LonePieces(view);
        var (a, atA) = pieces[0];
        Drag(window, atA, EmptySpotNear(view, atA));
        var (b, atB) = LonePieces(view).First(x => x.Id != a);
        var posB = St.Pieces[b].Pos;

        Drag(window, atB, EmptySpotNear(view, atB), release: false);
        Assert.True(view.IsDragging);
        history.Undo();
        Assert.False(view.IsDragging);
        window.MouseUp(atB, MouseButton.Left);
        window.Close();
        Assert.Equal(posB, St.Pieces[b].Pos);
        Assert.False(history.CanUndo);
    }

    [AvaloniaFact]
    public void View_sides_change_during_drag_cancels_it()
    {
        var (view, window, _) = Open(rules: true);
        var (id, at) = LonePieces(view)[0];
        var to = EmptySpotNear(view, at);
        var pos = St.Pieces[id].Pos;

        Drag(window, at, to, release: false);
        Assert.True(view.IsDragging);
        S.SetViewSides(new HashSet<string> { "north" });   // hot-seat hand-over
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(view.IsDragging);
        window.MouseUp(to, MouseButton.Left);
        window.Close();
        Assert.Equal(pos, St.Pieces[id].Pos);
    }

    [AvaloniaFact]
    public void Drag_frame_is_sharp_at_scale_2()
    {
        const float k = 2;
        var (view, window, tiles) = Open(scaling: k);
        view.ZoomCentered(1f);
        view.Snap = false;
        // Whole DIPs for the cursor: the ghost then lands on whole device pixels. The drop spot must be clear of other pieces.
        var (id, at, to) = LonePieces(view).Select(x => (x.Id, x.At, To: new Point(Math.Round(x.At.X) + 61, Math.Round(x.At.Y) + 37)))
            .First(x => x.To.X < W - 40 && x.To.Y < H - 40 && HitTester.PieceAt(S.View!, view.Map, S.Model, S.Images, view.ScreenToMap(x.To)) == null);
        var pc = St.Pieces[id];

        Drag(window, at, to, release: false);
        Assert.True(view.IsDragging);
        using var frame = window.CaptureRenderedFrame();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.MouseUp(to, MouseButton.Left);
        window.Close();
        var actual = TestHelpers.Pixels(frame!);
        Assert.Equal(((int)(W * k), (int)(H * k)), (actual.Width, actual.Height));

        // Expected: the scene natively at 2x without the piece, plus the ghost rendered at zoom 2 placed at the cursor.
        var pieces = new PieceRenderer(S.Images);
        using var sprite = pieces.RenderSprite(pc, view.View.Zoom * k, PieceDrawFlags.Ghost);
        using var expected = TestHelpers.ReferenceBitmap((int)(W * k), (int)(H * k));
        using (var c = new SKCanvas(expected))
        {
            c.Save();
            c.Scale(k);
            new MapRenderer(S.Images, pieces).Draw(c, view.Map, tiles, S.View!, S.Model, view.View, new SKSize(W, H),
                new MapDrawOptions(false, new HashSet<string> { id }, null, new HashSet<string> { id }, DeviceScale: k));
            c.Restore();
            c.DrawImage(sprite.Image, (float)(to.X * k) - sprite.Half, (float)(to.Y * k) - sprite.Half, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }
        var bounds = sprite.Bounds;

        // Compare the piece's interior (the landing outline runs along its edge).
        var z = view.View.Zoom * k;
        var inner = SKRectI.Round(new SKRect((float)(to.X * k + (bounds.Left + 3) * z), (float)(to.Y * k + (bounds.Top + 3) * z),
            (float)(to.X * k + (bounds.Right - 3) * z), (float)(to.Y * k + (bounds.Bottom - 3) * z)));
        Assert.True(inner.Width > 20 && inner.Height > 20, $"piece too small to compare: {inner}");
        int diff = 0, total = 0;
        for (var y = inner.Top; y < inner.Bottom; y++)
            for (var x = inner.Left; x < inner.Right; x++)
            {
                total++;
                var a = actual.Px[y * actual.Width + x];
                var e = expected.GetPixel(x, y);
                if (Math.Abs(a.Red - e.Red) > 2 || Math.Abs(a.Green - e.Green) > 2 || Math.Abs(a.Blue - e.Blue) > 2) diff++;
            }
        Assert.True(diff == 0, $"{diff} of {total} ghost pixels differ from the native 2x render (blurry drag sprite?)");

        // And the rest of the frame (away from the ghost) is the native 2x snapshot.
        var far = new SKRectI(0, 0, (int)(W * k), 40);
        for (var y = far.Top; y < far.Bottom; y++)
            for (var x = far.Left; x < far.Right; x++)
            {
                var a = actual.Px[y * actual.Width + x];
                var e = expected.GetPixel(x, y);
                Assert.True(Math.Abs(a.Red - e.Red) <= 2 && Math.Abs(a.Green - e.Green) <= 2 && Math.Abs(a.Blue - e.Blue) <= 2, $"snapshot pixel {x},{y} differs");
            }
    }
}
