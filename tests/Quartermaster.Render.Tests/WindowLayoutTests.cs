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
