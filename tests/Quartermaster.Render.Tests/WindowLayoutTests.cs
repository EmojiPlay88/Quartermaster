using System.Drawing;
using Quartermaster.Render;

namespace Quartermaster.Render.Tests;

public class ScreenFitTests
{
    static readonly Rectangle P = new(0, 0, 1920, 1040);
    static readonly Rectangle Second = new(1920, 0, 1280, 984);

    [Fact]
    public void OnScreen_Kept()
        => Assert.Equal(new Rectangle(100, 100, 800, 600), ScreenFit.Fit(new Rectangle(100, 100, 800, 600), [P], P));

    [Fact]
    public void OnSecondScreen_Kept()
        => Assert.Equal(new Rectangle(2000, 50, 600, 400), ScreenFit.Fit(new Rectangle(2000, 50, 600, 400), [P, Second], P));

    [Fact]
    public void OffScreen_CentredOnPrimary()
    {
        var r = ScreenFit.Fit(new Rectangle(5000, 5000, 800, 600), [P], P);
        Assert.Equal(new Rectangle(560, 220, 800, 600), r);
    }

    [Fact]
    public void FormerSecondMonitorGone_CentredOnPrimary()
    {
        var r = ScreenFit.Fit(new Rectangle(2000, 50, 600, 400), [P], P);
        Assert.Equal(new Rectangle(660, 320, 600, 400), r);
    }

    [Fact]
    public void Oversize_ClampedToWorkingArea()
    {
        var r = ScreenFit.Fit(new Rectangle(-100, -100, 5000, 4000), [P], P);
        Assert.Equal(P, r);
    }

    [Fact]
    public void PartiallyVisible_AtLeast64_Kept()
        => Assert.Equal(new Rectangle(-736, 100, 800, 600), ScreenFit.Fit(new Rectangle(-736, 100, 800, 600), [P], P));

    [Fact]
    public void PartiallyVisible_Under64_Moved()
    {
        var r = ScreenFit.Fit(new Rectangle(-760, 100, 800, 600), [P], P);
        Assert.Equal(new Rectangle(560, 220, 800, 600), r);
    }

    [Fact]
    public void Empty_Saved_GetsDefaultCentredSize()
    {
        var r = ScreenFit.Fit(Rectangle.Empty, [P], P);
        Assert.True(r.Width >= 64 && r.Height >= 64);
        Assert.True(P.Contains(r));
    }

    [Fact]
    public void NoScreens_UsesPrimary()
        => Assert.Equal(new Rectangle(660, 320, 600, 400), ScreenFit.Fit(new Rectangle(9999, 9999, 600, 400), [], P));

    [Fact]
    public void OversizeOnSecond_ClampedToThatScreen()
    {
        var r = ScreenFit.Fit(new Rectangle(2000, 10, 3000, 3000), [P, Second], P);
        Assert.Equal(Second, r);
    }
}

public class WindowLayoutStoreTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "qm-layout-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Fact]
    public void Missing_ReturnsDefaults()
    {
        var s = new WindowLayoutStore(_root);
        Assert.Empty(s.LoadModule("abc"));
        Assert.Null(s.LoadMain());
    }

    [Fact]
    public void RoundTrip_Module()
    {
        var s = new WindowLayoutStore(_root);
        var d = new Dictionary<string, SavedWindow>
        {
            ["map:Main Map"] = new(true, new Rectangle(10, 20, 300, 400), false),
            ["chart:x"] = new(false, new Rectangle(0, 0, 0, 0), true),
        };
        s.SaveModule("abc", d);
        var back = new WindowLayoutStore(_root).LoadModule("abc");
        Assert.Equal(2, back.Count);
        Assert.Equal(d["map:Main Map"], back["map:Main Map"]);
        Assert.True(back["chart:x"].Maximized);
        Assert.True(File.Exists(Path.Combine(_root, "layout", "abc.json")));
    }

    [Fact]
    public void RoundTrip_Main()
    {
        var s = new WindowLayoutStore(_root);
        s.SaveMain(new SavedWindow(false, new Rectangle(1, 2, 3, 4), true));
        Assert.Equal(new SavedWindow(false, new Rectangle(1, 2, 3, 4), true), s.LoadMain());
        Assert.True(File.Exists(Path.Combine(_root, "layout.json")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json {{{")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"Windows\": 5}")]
    [InlineData("{\"Windows\": {\"a\": \"x\"}}")]
    public void Corrupt_FallsBackToDefaults(string text)
    {
        Directory.CreateDirectory(Path.Combine(_root, "layout"));
        File.WriteAllText(Path.Combine(_root, "layout", "bad.json"), text);
        File.WriteAllText(Path.Combine(_root, "layout.json"), text);
        var s = new WindowLayoutStore(_root);
        Assert.Empty(s.LoadModule("bad"));
        Assert.Null(s.LoadMain());
    }

    [Fact]
    public void BadModuleKey_DoesNotEscapeRoot()
    {
        var s = new WindowLayoutStore(_root);
        s.SaveModule("../../evil", new Dictionary<string, SavedWindow> { ["a"] = new(true, new Rectangle(0, 0, 9, 9), false) });
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_root)!, "evil*"));
    }

    [Fact]
    public void UnwritableRoot_DoesNotThrow()
    {
        var file = Path.Combine(Path.GetTempPath(), "qm-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "x");
        try
        {
            var s = new WindowLayoutStore(file); // root is a file: cannot create dirs
            s.SaveModule("abc", new Dictionary<string, SavedWindow>());
            s.SaveMain(new SavedWindow(false, new Rectangle(1, 1, 5, 5), false));
            Assert.Empty(s.LoadModule("abc"));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void HashFile_IsStableLowerHex_AndNullOnMissing()
    {
        var f = Path.Combine(Path.GetTempPath(), "qm-hash-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(f, "hello");
        try
        {
            Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", WindowLayoutStore.HashFile(f));
            Assert.Null(WindowLayoutStore.HashFile(f + ".nope"));
        }
        finally { File.Delete(f); }
    }
}

public class ScreenFitTitleAndDpiTests
{
    static readonly Rectangle P = new(0, 0, 1920, 1040);

    [Fact]
    public void TitleAboveTop_MovedDown()
    {
        var r = ScreenFit.Fit(new Rectangle(100, -500, 800, 600), [P], P);
        Assert.Equal(new Rectangle(100, 0, 800, 600), r);
    }

    [Fact]
    public void TitleBelowBottom_MovedUp()
    {
        // 70 px visible passes the 64 rule only if tall enough; make it exactly the title-strip case
        var r = ScreenFit.Fit(new Rectangle(100, 1040 - 64, 800, 600), [P], P);
        Assert.True(r.Y <= P.Bottom - ScreenFit.TitleHeight);
        Assert.Equal(800, r.Width);
    }

    [Fact]
    public void TitleMostlyOffLeft_NudgedSoStripVisible()
    {
        var r = ScreenFit.Fit(new Rectangle(-736, 100, 800, 600), [P], P);
        Assert.True(r.Right - P.Left >= 64);
        Assert.Equal(new Rectangle(-736, 100, 800, 600), r); // exactly 64 visible is allowed
    }

    [Fact]
    public void Restore_SameDpi_KeepsBounds()
    {
        var s = new ScreenInfo(P, 96);
        Assert.Equal(new Rectangle(100, 100, 800, 600), ScreenFit.Restore(new Rectangle(100, 100, 800, 600), 96, [s], s));
    }

    [Fact]
    public void Restore_SavedOn96_NowMonitorIs192_SizeDoubled()
    {
        var p = new ScreenInfo(new Rectangle(0, 0, 1920, 1040), 96);
        var hi = new ScreenInfo(new Rectangle(1920, 0, 3840, 2120), 192);
        var r = ScreenFit.Restore(new Rectangle(2000, 100, 800, 600), 96, [p, hi], p);
        Assert.Equal(new Rectangle(2000, 100, 1600, 1200), r);
    }

    [Fact]
    public void Restore_SavedOn192_NowMonitorIs96_SizeHalved()
    {
        var p = new ScreenInfo(new Rectangle(0, 0, 1920, 1040), 96);
        var r = ScreenFit.Restore(new Rectangle(100, 100, 1600, 1200), 192, [p], p);
        Assert.Equal(new Rectangle(100, 100, 800, 600), r);
    }

    [Fact]
    public void Restore_UnknownDpi_NoScaling()
    {
        var p = new ScreenInfo(new Rectangle(0, 0, 1920, 1040), 144);
        Assert.Equal(new Rectangle(100, 100, 800, 600), ScreenFit.Restore(new Rectangle(100, 100, 800, 600), 0, [p], p));
    }

    [Fact]
    public void Restore_MonitorGone_ScaledToPrimaryAndCentred()
    {
        var p = new ScreenInfo(new Rectangle(0, 0, 1920, 1040), 96);
        var r = ScreenFit.Restore(new Rectangle(5000, 100, 1600, 1200), 192, [p], p);
        Assert.Equal(new Rectangle(560, 220, 800, 600), r);
    }

    [Fact]
    public void SavedWindow_RoundTripsDpi()
    {
        var root = Path.Combine(Path.GetTempPath(), "qm-dpi-" + Guid.NewGuid().ToString("N"));
        try
        {
            var s = new WindowLayoutStore(root);
            s.SaveMain(new SavedWindow(false, new Rectangle(1, 2, 3, 4), false, 144));
            Assert.Equal(144, s.LoadMain()!.Value.Dpi);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
