using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Map;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>B6 task 5: a hex's place values on the map: the status bar's place text and the hover tooltip over an empty hex. Headless on the
/// invented Riverland places module. Pointer presses read ShortcutGestures.IsMac: MacFlag collection.</summary>
[Collection("MacFlag")]
public sealed class MapPlaceHoverTests : IDisposable
{
    const string Global = "h:global";
    const string Hex = "0203";
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-placehover-" + Guid.NewGuid().ToString("N"));
    readonly List<FixtureModule> _fixtures = new();
    readonly List<MainWindow> _windows = new();
    ModuleSession? _s;

    public void Dispose()
    {
        foreach (var w in _windows) w.ForceClose();
        ShortcutGestures.IsMac = _wasMac;
        _s?.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

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

    static void Put(ModuleSession s, string prop, string hex, double v) =>
        s.Game!.Execute(new LedgerDelta(Global, prop, 0, v, "manual", null, null, "test", null) { ValueKey = hex });

    /// <summary>A started game with place values on the hex (and a private one).</summary>
    ModuleSession Session()
    {
        ShortcutGestures.IsMac = false;
        var fx = PlacesFixture.Module();
        _fixtures.Add(fx);
        _s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), null);
        _s.StartNewGame(Difficulty.FullyAuto);
        _s.NewRules("t");
        _s.UpdateRules(_ => PlacesFixture.Pack());
        Put(_s, "p-ford", Hex, 3);
        Put(_s, "p-mines", Hex, 2);
        return _s;
    }

    (MapView View, Window Window) Map(ModuleSession s)
    {
        var view = new MapView(s, s.Model.Maps.Single(m => m.Name == "North")) { HoverDelayMs = 100 };
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        Pump();
        return (view, window);
    }

    static Point ScreenOf(MapView view, ModuleSession s, string hex)
    {
        var board = s.Model.Maps.Single(m => m.Name == "North").Boards.Single(b => b.Name == "Riverside");
        var local = board.Grid!.LocationToPoint(hex)!.Value;
        var sp = view.View.ToScreen(new PointD(board.Offset.X + local.X, board.Offset.Y + local.Y));
        return new Point(sp.X, sp.Y);
    }

    const string FullText = "0203: FordDepth 3 (Flooded) · Mines 2";

    [AvaloniaFact]
    public void Status_bar_shows_place_text()
    {
        var s = Session();
        var w = new MainWindow(new FakeDialogs(), Path.Combine(_tmp, "wcache"), null, Path.Combine(_tmp, "layout")) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        Assert.True(w.OpenModule(_fixtures[0].Build()));
        w.StartSetup(null);
        w.Session!.NewRules("t");
        w.Session.UpdateRules(_ => PlacesFixture.Pack());
        Put(w.Session, "p-ford", Hex, 3);
        Put(w.Session, "p-mines", Hex, 2);
        Pump();
        var view = w.MapTabs.Views.Single(v => v.Map.Name == "North");
        view.CentreOn(new PointD(200, 200));
        Pump();

        w.MouseMove(view.TranslatePoint(ScreenOf(view, w.Session, Hex), w)!.Value);
        Pump();
        Assert.EndsWith("  0203  ·  FordDepth 3 (Flooded) · Mines 2", w.LocationText);

        // A hex without place values: the status is as before.
        var plain = view.TranslatePoint(ScreenOf(view, w.Session, "0305"), w)!.Value;
        w.MouseMove(plain);
        Pump();
        Assert.EndsWith("  0305", w.LocationText);
        Assert.DoesNotContain("·", w.LocationText);

        // The viewer's state: a player view leaves the Private place out.
        w.Session.SetViewSides(new HashSet<string> { "blue" });
        Pump();
        w.MouseMove(view.TranslatePoint(ScreenOf(view, w.Session, Hex), w)!.Value + new Point(1, 0));
        Pump();
        Assert.EndsWith("  0203  ·  FordDepth 3 (Flooded)", w.LocationText);
    }

    [AvaloniaFact]
    public void Hover_tooltip_on_empty_hex_with_place()
    {
        var s = Session();
        var (view, window) = Map(s);
        var at = ScreenOf(view, s, Hex);
        window.MouseMove(at);
        Pump();
        Assert.Null(view.PlaceTip);                                  // not at once
        Assert.True(WaitFor(() => view.PlaceTip != null), "the tooltip did not open");
        Assert.Equal(FullText, view.PlaceTip);
        Assert.Equal(FullText, ToolTip.GetTip(view));
        Assert.True(ToolTip.GetIsOpen(view));

        window.MouseMove(ScreenOf(view, s, "0305"));                 // another hex, none there: gone
        Assert.Null(view.PlaceTip);
        Assert.False(ToolTip.GetIsOpen(view));

        window.MouseMove(at);
        Assert.True(WaitFor(() => view.PlaceTip != null));
        window.MouseDown(at, MouseButton.Left);                      // a press clears it
        window.MouseUp(at, MouseButton.Left);
        Assert.Null(view.PlaceTip);

        window.MouseMove(at + new Point(2, 1));
        Assert.True(WaitFor(() => view.PlaceTip != null));
        Put(s, "p-ford", Hex, 1);                                    // a game change clears it, and the next tooltip reads the new value
        Assert.True(WaitFor(() => view.PlaceTip == null));
        window.MouseMove(at);
        Assert.True(WaitFor(() => view.PlaceTip == "0203: FordDepth 1 (Shallow) · Mines 2"));
    }

    [AvaloniaFact]
    public void No_tooltip_over_stack()
    {
        var s = Session();
        var (view, window) = Map(s);
        var board = s.Model.Maps.Single(m => m.Name == "North").Boards.Single(b => b.Name == "Riverside");
        var local = board.Grid!.LocationToPoint(Hex)!.Value;
        s.Game!.Execute(new AddPiece("u1", PieceTypeParser.EncodeState(new[] { "mark;Side", "piece;;;unit.svg;Red Barge" }),
            PieceTypeParser.EncodeState(new[] { "Red", "" }), view.Map.Id, new PointD(board.Offset.X + local.X, board.Offset.Y + local.Y), null));
        Pump();
        var at = ScreenOf(view, s, Hex);
        Assert.NotNull(HitTester.PieceAt(s.View!, view.Map, s.Model, s.Images, view.ScreenToMap(at)));
        window.MouseMove(at);
        Assert.True(WaitFor(() => view.Popup != null), "the counter popup did not open");   // the stack's own hover wins
        Assert.Null(view.PlaceTip);
        Pump();
        Assert.False(WaitFor(() => view.PlaceTip != null, 400));
    }

    [AvaloniaFact]
    public void No_tooltip_without_places()
    {
        var s = Session();
        s.UpdateRules(p => p with { Properties = p.Properties.Where(x => !KeyedFacts.IsPlace(x)).ToList() });
        var (view, window) = Map(s);
        window.MouseMove(ScreenOf(view, s, Hex));
        Assert.False(WaitFor(() => view.PlaceTip != null, 400));
    }
}
