using Quartermaster.Render;

namespace Quartermaster.Render.Tests;

public class AutosaverTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-autosave-" + Guid.NewGuid().ToString("N"));

    public AutosaverTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    static readonly DateTimeOffset T0 = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Autosaver_SavesOnlyWhenDirtyAndElapsed()
    {
        var saved = new List<string>();
        var a = new Autosaver(() => "abc", p => { File.WriteAllText(p, "x"); saved.Add(p); }, _tmp);

        Assert.Null(a.Tick(T0, 5, dirty: false));
        Assert.Null(a.Tick(T0.AddMinutes(1), 5, dirty: true));           // interval not elapsed
        Assert.Empty(saved);

        var path = a.Tick(T0.AddMinutes(7), 5, dirty: true);             // dirty and elapsed
        Assert.NotNull(path);
        Assert.Equal(Path.Combine(_tmp, "autosave", "abc", "autosave-20261106-120700.qsave"), path);
        Assert.True(File.Exists(path));

        Assert.Null(a.Tick(T0.AddMinutes(8), 5, dirty: true));           // just saved
        Assert.Null(a.Tick(T0.AddMinutes(13), 5, dirty: false));         // clean game: nothing to save
        Assert.Null(a.Tick(T0.AddMinutes(14), 5, dirty: true));          // idle clock restarted by the clean tick
        Assert.NotNull(a.Tick(T0.AddMinutes(19), 5, dirty: true));
        Assert.Equal(2, saved.Count);
    }

    [Fact]
    public void Autosaver_Reset_RestartsTheInterval()
    {
        var saved = 0;
        var a = new Autosaver(() => "abc", p => { File.WriteAllText(p, "x"); saved++; }, _tmp);
        a.Tick(T0, 5, true);
        a.Reset(T0.AddMinutes(4));                       // user saved manually
        Assert.Null(a.Tick(T0.AddMinutes(6), 5, true));
        Assert.NotNull(a.Tick(T0.AddMinutes(9), 5, true));
        Assert.Equal(1, saved);
    }

    [Fact]
    public void Autosaver_IntervalZero_KeepsClockMoving()
    {
        var a = new Autosaver(() => "abc", p => File.WriteAllText(p, "x"), _tmp);
        Assert.Null(a.Tick(T0, 0, true));
        Assert.Null(a.Tick(T0.AddMinutes(60), 0, true));
        Assert.Null(a.Tick(T0.AddMinutes(61), 5, true));  // just enabled: must not fire at once
        Assert.NotNull(a.Tick(T0.AddMinutes(66), 5, true));
    }

    [Fact]
    public void Autosaver_NoModule_OrFailingSave_ReturnsNullWithoutThrowing()
    {
        var errors = new List<string>();
        var none = new Autosaver(() => null, _ => throw new InvalidOperationException(), _tmp);
        none.Tick(T0, 1, true);
        Assert.Null(none.Tick(T0.AddMinutes(9), 1, true));

        var bad = new Autosaver(() => "m", _ => throw new IOException("disk full"), _tmp, errors.Add);
        bad.Tick(T0, 1, true);
        Assert.Null(bad.Tick(T0.AddMinutes(2), 1, true));
        Assert.Single(errors);
        Assert.Contains("disk full", errors[0]);
    }

    [Fact]
    public void Autosaver_SaveNow_WritesUnlessOff()
    {
        var saved = new List<string>();
        var a = new Autosaver(() => "abc", p => { File.WriteAllText(p, "x"); saved.Add(p); }, _tmp);

        Assert.Null(a.SaveNow(T0, 0));                                       // autosave off
        Assert.Empty(saved);

        var path = a.SaveNow(T0.AddMinutes(1), 5);                           // immediately, not dirty/elapsed gated
        Assert.Equal(Path.Combine(_tmp, "autosave", "abc", "autosave-20261106-120100.qsave"), path);
        Assert.True(File.Exists(path));

        Assert.Null(a.Tick(T0.AddMinutes(3), 5, dirty: true));               // clock was reset by SaveNow
        Assert.NotNull(a.Tick(T0.AddMinutes(7), 5, dirty: true));

        for (var i = 0; i < 12; i++) a.SaveNow(T0.AddMinutes(10).AddSeconds(i), 5);
        Assert.Equal(10, Directory.GetFiles(Path.Combine(_tmp, "autosave", "abc")).Length);   // pruned

        var bad = new Autosaver(() => "m", _ => throw new IOException("disk full"), _tmp, _ => { });
        Assert.Null(bad.SaveNow(T0, 5));                                     // never throws
        Assert.Null(new Autosaver(() => null, _ => { }, _tmp).SaveNow(T0, 5));
    }

    [Fact]
    public void Autosaver_Prune_KeepsNewest10()
    {
        var dir = Path.Combine(_tmp, "d");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 14; i++) File.WriteAllText(Path.Combine(dir, $"autosave-20261106-1200{i:00}.qsave"), "x");
        File.WriteAllText(Path.Combine(dir, "mine.qsave"), "x");        // not ours: never touched

        Autosaver.Prune(dir);

        var left = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(11, left.Count);
        Assert.Contains("mine.qsave", left);
        Assert.DoesNotContain("autosave-20261106-120000.qsave", left);
        Assert.DoesNotContain("autosave-20261106-120003.qsave", left);
        Assert.Contains("autosave-20261106-120004.qsave", left);
        Assert.Contains("autosave-20261106-120013.qsave", left);
        Autosaver.Prune(Path.Combine(_tmp, "missing")); // tolerant
    }

    [Fact]
    public void IsAutosavePath_OnlyUnderAutosaveFolder()
    {
        var root = Path.Combine(_tmp, "root");
        Assert.True(Autosaver.IsAutosavePath(Path.Combine(root, "autosave", "abc", "autosave-20261106-120700.qsave"), root));
        Assert.True(Autosaver.IsAutosavePath(Path.Combine(root, "autosave", "abc", "..", "abc", "x.qsave"), root));
        Assert.True(Autosaver.IsAutosavePath(Path.Combine(root, "AUTOSAVE", "abc", "x.qsave"), root));
        Assert.False(Autosaver.IsAutosavePath(Path.Combine(root, "mine.qsave"), root));
        Assert.False(Autosaver.IsAutosavePath(Path.Combine(root, "autosave-evil", "x.qsave"), root));
        Assert.False(Autosaver.IsAutosavePath(Path.Combine(root, "autosave", "..", "x.qsave"), root));
        Assert.False(Autosaver.IsAutosavePath("", root));
        Assert.False(Autosaver.IsAutosavePath("bad\0path", root));
    }
}
