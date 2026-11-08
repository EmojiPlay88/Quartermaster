using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Map;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>Hover popup (WinForms CounterDetailPopup), ledger breakdown tips, hover-stack highlight and double-click stack expand on the
/// MapView, as the WinForms MapCanvas. Headless on the Riverland demo. Pointer presses read ShortcutGestures.IsMac: MacFlag collection.</summary>
[Collection("MacFlag")]
public sealed class HoverTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    const int W = 800, H = 600;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-hover-" + Guid.NewGuid().ToString("N"));
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

    (MapView View, Window Window) Open(bool rules = false, int hoverDelayMs = 150)
    {
        ShortcutGestures.IsMac = false;
        _s = ModuleSession.Open(Demo("Riverland-Demo.vmod"), Path.Combine(_tmp, "cache"), null);
        _s.StartNewGame(Difficulty.FullyAuto);
        if (rules) _s.OpenRules(Demo("Riverland-Demo.qrules"));
        var view = new MapView(_s, _s.Model.Maps[0]) { HoverDelayMs = hoverDelayMs };
        var window = new Window { Width = W, Height = H, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (view, window);
    }

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

    Point EmptySpot(MapView view)
    {
        for (var y = 30; y < H - 30; y += 20)
            for (var x = 30; x < W - 30; x += 20)
                if (HitTester.PieceAt(S.View!, view.Map, S.Model, S.Images, view.ScreenToMap(new Point(x, y))) == null) return new Point(x, y);
        throw new InvalidOperationException("no empty spot");
    }

    bool IsUnit(string id)
    {
        var p = S.View!.Pieces[id];
        return PieceDetails.For(p, new PieceRenderer(S.Images).Types.Get(p.TypeString)).Properties.Contains(("Kind", "Unit"));
    }

    void SetLedger(string id, string prop, double value, string note)
    {
        var key = LedgerKey.Piece(id);
        S.Game!.Execute(new LedgerDelta(key, prop, Ledger.Get(St, key, prop) ?? 0, value, "manual", null, null, note, null));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Runs the dispatcher loop (RunJobs alone runs no timers) in short slices until the condition holds or the time is up.</summary>
    static bool WaitFor(Func<bool> condition, int ms = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            using var slice = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    static void Pump(int ms) => WaitFor(() => false, ms);

    CounterPopup Hover(MapView view, Window window, Point at)
    {
        window.MouseMove(at);
        Assert.True(WaitFor(() => view.Popup != null), "the popup did not open");
        return view.Popup!;
    }

    static Rect BoundsIn(Visual v, Visual root) =>
        new(v.TranslatePoint(new Point(0, 0), root) ?? throw new InvalidOperationException("not in the tree"), v.Bounds.Size);

    [AvaloniaFact]
    public void Hover_shows_popup_after_delay()
    {
        var (view, window) = Open(hoverDelayMs: 400);
        var (id, at) = LonePieces(view)[0];
        window.MouseMove(at);
        Assert.Equal(St.Pieces[id].StackId, view.HoverStackId);
        Pump(150);
        Assert.Null(view.Popup);                                   // not before the delay
        Assert.True(WaitFor(() => view.Popup != null));
        var popup = view.Popup!;
        Assert.IsType<OverlayLayer>(popup.GetVisualParent());      // an overlay of the window, never a window of its own
        Assert.Equal(id, popup.Layout.Details[0].PieceId);
        Assert.False(popup.Interactive);                           // no rules pack: no ledger lines

        window.MouseMove(EmptySpot(view));                         // off the counter: gone at once (nothing to hover on it)
        Assert.Null(view.Popup);
        Assert.Null(view.HoverStackId);
        Assert.Null(popup.GetVisualParent());
        window.Close();
    }

    [AvaloniaFact]
    public void Popup_lists_properties_and_ledger_lines()
    {
        var (view, window) = Open(rules: true);
        var (id, at) = LonePieces(view, IsUnit)[0];
        SetLedger(id, "fuel", 6, "starting load");
        SetLedger(id, "readiness", 3, "drill");

        var popup = Hover(view, window, at);
        var p = St.Pieces[id];
        var detail = PieceDetails.For(p, new PieceRenderer(S.Images).Types.Get(p.TypeString), St, S.Rules);
        var lines = popup.Layout.Lines;
        Assert.Equal(detail.Name, lines[0]);
        foreach (var (k, v) in detail.Properties) Assert.Contains($"{k}: {v}", lines);
        Assert.Contains("Fuel: 6", lines);
        Assert.Contains("Readiness: 3", lines);
        Assert.True(popup.Interactive);
        Assert.Equal(detail.Ledger.Count, popup.Layout.HotLines.Count);   // one hoverable line per ledger value (the game seeds some)
        Assert.Equal(detail.Ledger.Select(l => l.Name), popup.Layout.HotLines.Select(h => h.Tip.Title.Split(' ')[0]));
        Assert.Equal((double)popup.Layout.Width, popup.Bounds.Width);
        Assert.Equal((double)popup.Layout.Height, popup.Bounds.Height);
        window.Close();
    }

    [AvaloniaFact]
    public void Popup_hides_private_values_of_other_side()
    {
        var (view, window) = Open(rules: true);
        S.SetViewSides(new HashSet<string> { "north" });
        Dispatcher.UIThread.RunJobs();
        var (theirs, theirsAt) = LonePieces(view, id => IsUnit(id) && S.CanSee(id) && !S.CanAct(id))[0];
        var (mine, mineAt) = LonePieces(view, id => IsUnit(id) && S.CanAct(id))[0];
        foreach (var id in new[] { theirs, mine })
        {
            SetLedger(id, "fuel", 6, "starting load");
            SetLedger(id, "readiness", 3, "drill");
        }
        S.SetViewSides(new HashSet<string> { "north" });   // view refresh after the edits
        Dispatcher.UIThread.RunJobs();

        // The other side's unit: its public Fuel shows, its private Readiness does not (neither as a line nor in a breakdown).
        var popup = Hover(view, window, theirsAt);
        Assert.Equal(theirs, popup.Layout.Details[0].PieceId);
        Assert.Contains("Fuel: 6", popup.Layout.Lines);
        Assert.DoesNotContain(popup.Layout.Lines, l => l.Contains("Readiness"));
        Assert.DoesNotContain(popup.Layout.HotLines, h => LedgerBreakdown.Format(h.Tip).Contains("Readiness") || LedgerBreakdown.Format(h.Tip).Contains("drill"));
        // Exactly what the WinForms popup shows this viewer: the same shared layout over ModuleSession.View.
        var stack = S.View!.Stacks[S.View.Pieces[theirs].StackId!];
        Assert.Equal(CounterPopupLayout.Build(S, S.View, stack, new PieceRenderer(S.Images)).Lines, popup.Layout.Lines);

        window.MouseMove(EmptySpot(view));
        Assert.True(WaitFor(() => view.Popup == null));
        // The viewer's own unit shows its private value.
        popup = Hover(view, window, mineAt);
        Assert.Contains("Readiness: 3", popup.Layout.Lines);

        // Solo (every side visible): the other unit's Readiness shows again.
        window.MouseMove(EmptySpot(view));
        Assert.True(WaitFor(() => view.Popup == null));
        S.SetViewSides(null);
        Dispatcher.UIThread.RunJobs();
        popup = Hover(view, window, theirsAt);
        Assert.Contains("Readiness: 3", popup.Layout.Lines);
        window.Close();
    }

    [AvaloniaFact]
    public void Ledger_line_hover_shows_breakdown()
    {
        var (view, window) = Open(rules: true);
        var (id, at) = LonePieces(view, IsUnit)[0];
        SetLedger(id, "fuel", 6, "starting load");
        SetLedger(id, "fuel", 4, "forced march");

        var popup = Hover(view, window, at);
        var line = popup.Layout.HotLines.Single(h => h.Tip.Title.StartsWith("Fuel ", StringComparison.Ordinal));
        var onLine = popup.TranslatePoint(new Point(line.X + 2, line.Top + 2), window)!.Value;
        window.MouseMove(onLine);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(popup, view.Popup);                            // the popup stays while the pointer is on it
        var expected = LedgerBreakdown.Format(LedgerBreakdown.For(S.View!, S.Rules, S.Model, LedgerKey.Piece(id), "fuel"));
        Assert.Equal(expected, view.Tip.Text);
        Assert.Contains("forced march", view.Tip.Text);
        var tip = Assert.IsAssignableFrom<Control>(view.Tip.Shown);
        Assert.IsType<OverlayLayer>(tip.GetVisualParent());
        var tipAt = BoundsIn(tip, window);
        var lineRight = popup.TranslatePoint(new Point(line.X + line.Width + 6, line.Top), window)!.Value;
        Assert.Equal(lineRight.X, tipAt.X, 0.5);                   // beside the line, as WinForms (rect.Right + 6, rect.Top)
        Assert.Equal(lineRight.Y, tipAt.Y, 0.5);

        // Off the line but still on the popup: the tip goes, the popup stays.
        var popupRect = BoundsIn(popup, window);
        window.MouseMove(new Point(popupRect.Right - 3, popupRect.Bottom - 3));
        Dispatcher.UIThread.RunJobs();
        Assert.Null(view.Tip.Text);
        Assert.Same(popup, view.Popup);

        // Back on the line, then away onto the map: everything closes.
        window.MouseMove(onLine);
        Assert.NotNull(view.Tip.Text);
        window.MouseMove(EmptySpot(view));
        Assert.True(WaitFor(() => view.Popup == null));
        Assert.Null(view.Tip.Text);

        // Click-through: a press on the popup away from its lines reaches the map, which closes the popup (WinForms HTTRANSPARENT).
        popup = Hover(view, window, at);
        popupRect = BoundsIn(popup, window);
        var offLines = new Point(popupRect.Right - 3, popupRect.Bottom - 3);
        window.MouseMove(offLines);
        Assert.Same(popup, view.Popup);
        window.MouseDown(offLines, MouseButton.Left);
        Assert.Null(view.Popup);
        window.MouseUp(offLines, MouseButton.Left);
        window.Close();
    }

    [AvaloniaFact]
    public void Hover_highlights_stack()
    {
        var (view, window) = Open(hoverDelayMs: 60000);
        var (id, at) = LonePieces(view)[0];
        var empty = EmptySpot(view);
        window.MouseMove(empty);
        var before = TestHelpers.Pixels(window.CaptureRenderedFrame()!);
        window.MouseMove(at);
        Assert.Equal(St.Pieces[id].StackId, view.HoverStackId);
        var hovered = TestHelpers.Pixels(window.CaptureRenderedFrame()!);
        var changed = Enumerable.Range(0, before.Px.Length).Where(i => before.Px[i] != hovered.Px[i]).ToList();
        Assert.NotEmpty(changed);                                  // the outline is drawn...
        foreach (var i in changed)                                 // ...around the hovered counter only
            Assert.True(Math.Abs(i % W - at.X) < 80 && Math.Abs(i / W - at.Y) < 80, $"pixel ({i % W},{i / W}) changed");
        window.MouseMove(empty);
        Assert.Null(view.HoverStackId);
        var after = TestHelpers.Pixels(window.CaptureRenderedFrame()!);
        Assert.Equal(before.Px, after.Px);
        window.Close();
    }

    [AvaloniaFact]
    public void Double_click_expands_and_collapses_stack()
    {
        var (view, window) = Open(hoverDelayMs: 60000);
        var (id, at) = LonePieces(view)[0];
        var stackId = St.Pieces[id].StackId!;
        window.MouseDown(at, MouseButton.Left); window.MouseUp(at, MouseButton.Left);
        Assert.False(St.Stacks[stackId].Expanded);                 // a single click does not toggle

        window.MouseDown(at, MouseButton.Left); window.MouseUp(at, MouseButton.Left);
        Assert.True(St.Stacks[stackId].Expanded);

        Pump(700);                                                 // past the double-click time: the next click starts afresh
        window.MouseDown(at, MouseButton.Left); window.MouseUp(at, MouseButton.Left);
        Assert.True(St.Stacks[stackId].Expanded);
        window.MouseDown(at, MouseButton.Left); window.MouseUp(at, MouseButton.Left);
        Assert.False(St.Stacks[stackId].Expanded);
        window.Close();
    }

    [AvaloniaFact]
    public void Popup_not_under_cursor()
    {
        var (view, window) = Open();
        var (id, _) = LonePieces(view)[0];
        var pos = St.Stacks[St.Pieces[id].StackId!].Pos;
        foreach (var target in new[] { new Point(40, 40), new Point(W - 40, H - 40), new Point(W - 40, 40), new Point(40, H - 40) })
        {
            // Pan so the counter sits at the target point.
            view.View.Pan = new SkiaSharp.SKPoint((float)(pos.X - target.X / view.View.Zoom), (float)(pos.Y - target.Y / view.View.Zoom));
            view.InvalidateVisual();
            var sp = view.View.ToScreen(pos);
            var at = new Point(sp.X, sp.Y);
            var popup = Hover(view, window, at);
            var r = BoundsIn(popup, window);
            Assert.False(r.Contains(at), $"popup {r} covers the cursor {at}");
            var placed = PopupPlacement.Place(new System.Drawing.Point((int)Math.Round(at.X), (int)Math.Round(at.Y)),
                new System.Drawing.Size(popup.Layout.Width, popup.Layout.Height), new System.Drawing.Rectangle(0, 0, W, H));
            Assert.Equal(new Point(placed.X, placed.Y), r.Position);
            if (at.X > W / 2.0) Assert.True(r.Right <= at.X); else Assert.True(r.X >= at.X);
            window.MouseMove(EmptySpot(view));
            Assert.True(WaitFor(() => view.Popup == null));
        }
        window.Close();
    }

    [AvaloniaFact]
    public void Popup_hides_on_press_and_wheel()
    {
        var (view, window) = Open();
        var (_, at) = LonePieces(view)[0];
        Hover(view, window, at);
        window.MouseDown(at, MouseButton.Left);
        Assert.Null(view.Popup);
        window.MouseUp(at, MouseButton.Left);
        Pump(700);

        var nudge = new Point(at.X + 1, at.Y);
        window.MouseMove(EmptySpot(view));
        Hover(view, window, nudge);
        window.MouseWheel(nudge, new Vector(0, 1));
        Assert.Null(view.Popup);
        window.Close();
    }
}
