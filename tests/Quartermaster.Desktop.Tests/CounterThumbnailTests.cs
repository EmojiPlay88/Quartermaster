using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 3a task 3: the shared counter thumbnail cache (WinForms CounterCellImages / PaletteThumbs). Headless only, on the invented
/// phase fixture and the Riverland demo. The palette case opens a main window, so the class runs in the MacFlag collection.</summary>
[Collection("MacFlag")]
public sealed class CounterThumbnailTests : IDisposable
{
    static readonly PixelSize Row = new(64, 48);
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-thumbs-" + Guid.NewGuid().ToString("N"));
    readonly List<ModuleSession> _sessions = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly List<MainWindow> _windows = new();
    readonly List<CounterThumbnails> _thumbs = new();

    public CounterThumbnailTests() => ShortcutGestures.IsMac = false;

    public void Dispose()
    {
        foreach (var t in _thumbs) t.Dispose();
        foreach (var w in _windows) w.ForceClose();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static void PumpUntil(Func<bool> done, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            Pump();
            if (done()) return;
            Assert.True(DateTime.UtcNow < until, "timed out");
            using var slice = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
    }

    static string Demo(string file, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", file));

    /// <summary>The phase fixture (Red 1, Red 2, Blue 1 on Main Map) with its rules pack; <paramref name="redMap"/> makes Main Map Red's.</summary>
    ModuleSession Session(bool redMap = false)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto);
        s.NewRules("t");
        s.UpdateRules(_ => PhaseFixture.Pack(s.Archive.Sha256) with
        {
            MapOwners = redMap ? new List<MapOwner> { new() { MapId = s.Model.Maps[0].Id, SideId = "red" } } : new List<MapOwner>(),
        });
        return s;
    }

    CounterThumbnails Thumbs(ModuleSession s, int cap = 600)
    {
        var t = new CounterThumbnails(s, cap);
        _thumbs.Add(t);
        return t;
    }

    static bool Alive(Bitmap b)
    {
        try { _ = b.PixelSize; return true; }
        catch (ObjectDisposedException) { return false; }
    }

    [AvaloniaFact]
    public void Thumbnail_renders_off_ui_thread_and_reports_ready()
    {
        var s = Session();
        var t = Thumbs(s);
        var ui = Environment.CurrentManagedThreadId;
        var ready = new List<(string Id, int Thread)>();
        Assert.Null(t.Get(PhaseFixture.RedOne, Row, id => ready.Add((id, Environment.CurrentManagedThreadId))));
        PumpUntil(() => ready.Count > 0);

        var (readyId, thread) = Assert.Single(ready);
        Assert.Equal(PhaseFixture.RedOne, readyId);
        Assert.Equal(ui, thread);   // reported on the UI thread
        Assert.NotNull(t.LastWorkerThread);
        Assert.NotEqual(ui, t.LastWorkerThread);   // drawn on a worker
        var bmp = t.Get(PhaseFixture.RedOne, Row, _ => Assert.Fail("already cached"));
        Assert.NotNull(bmp);
        Assert.Equal(Row, bmp!.PixelSize);
        Assert.Equal(0, t.PendingCount);
    }

    [AvaloniaFact]
    public void Thumbnail_cache_caps_and_keeps_visible()
    {
        var s = Session();
        var t = Thumbs(s, cap: 2);
        var ids = new[] { PhaseFixture.RedOne, PhaseFixture.RedTwo, PhaseFixture.BlueOne };
        // Red 2's type differs from Red 1's only by name, so each piece is its own entry.
        t.Prioritise(new[] { PhaseFixture.BlueOne });
        var ready = new List<string>();
        t.Hold = true;   // queue all three before the worker picks
        foreach (var id in ids) Assert.Null(t.Get(id, Row, ready.Add));
        Assert.Equal(3, t.PendingCount);
        t.Hold = false;
        PumpUntil(() => ready.Count == 3);
        Assert.Equal(PhaseFixture.BlueOne, ready[0]);   // the visible piece is drawn first

        // Red 1 is shown on screen; then a fourth entry pushes the cache over its cap.
        // 3 entries, cap 2: Blue 1 stays because it is visible, the oldest other one (Red 1) went.
        Assert.Equal(2, t.Count);
        Assert.False(t.Has(PhaseFixture.RedOne, Row));
        Assert.True(t.Has(PhaseFixture.RedTwo, Row));
        Assert.True(t.Has(PhaseFixture.BlueOne, Row));

        var redOne = t.Get(PhaseFixture.RedOne, Row, null);   // back from CounterImages' own cache, or drawn again
        if (redOne == null) { PumpUntil(() => t.Has(PhaseFixture.RedOne, Row)); redOne = t.Get(PhaseFixture.RedOne, Row, null)!; }
        Assert.Equal(2, t.Count);
        Assert.True(t.Has(PhaseFixture.BlueOne, Row));
        t.Shown = () => new[] { redOne };
        var spare = new Bitmap(new MemoryStream(PngOf(redOne)));
        t.Remember("test:spare", spare);
        t.Remember("test:newest", new Bitmap(new MemoryStream(PngOf(redOne))));
        Assert.True(t.Count <= 2 + 1);   // the visible entry may stay above the cap
        Assert.NotNull(t.Get(PhaseFixture.BlueOne, Row, null));   // visible: never evicted
        Assert.True(Alive(redOne), "a thumbnail still on screen was disposed");
        Assert.False(Alive(spare), "an evicted thumbnail nobody shows should be disposed");
    }

    static byte[] PngOf(Bitmap b)
    {
        using var m = new MemoryStream();
        b.Save(m, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        return m.ToArray();
    }

    [AvaloniaFact]
    public void Thumbnail_none_for_hidden_piece()
    {
        var s = Session(redMap: true);
        s.SetViewSides(new HashSet<string> { "blue" });
        Assert.False(s.View!.Pieces.ContainsKey(PhaseFixture.RedOne));
        var t = Thumbs(s);
        var ready = new List<string>();
        Assert.Null(t.Get(PhaseFixture.RedOne, Row, ready.Add));
        Assert.Null(t.Get("no-such-piece", Row, ready.Add));
        Assert.Equal(0, t.PendingCount);
        for (var i = 0; i < 20; i++) { Pump(); Thread.Sleep(5); }
        Assert.Empty(ready);
        Assert.Null(t.Get(PhaseFixture.RedOne, Row, ready.Add));   // and stays so
        Assert.Equal(0, t.Count);
    }

    [AvaloniaFact]
    public void Palette_uses_shared_cache()
    {
        var w = new MainWindow(new FakeDialogs(), Path.Combine(_tmp, "cache-w"), null) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        w.OpenModule(Demo("Riverland-Demo.vmod"));
        w.StartSetup(null);
        var palette = w.Palette;
        PumpUntil(() => palette.ThumbImages.Count > 0 && palette.ThumbImages.All(i => i.Source != null));
        var thumbs = Assert.IsType<CounterThumbnails>(palette.Thumbnails);
        for (var i = 0; i < palette.ListedSlots.Count; i++)
            Assert.Same(palette.ThumbImages[i].Source, thumbs.Peek(PaletteModel.Key(palette.ListedSlots[i])));
        Assert.Equal(3000, thumbs.Cap);   // the palette keeps its own cap
        Assert.Equal(PaletteDock.ThumbSize, ((Bitmap)palette.ThumbImages[0].Source!).PixelSize);
    }
}
