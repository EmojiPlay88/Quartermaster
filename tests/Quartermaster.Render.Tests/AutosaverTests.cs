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
        Assert.Equal(Path.Combine(_tmp, "autosave", "abc", "autosave-20261106-120700000.qsave"), path);
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
        Assert.Equal(Path.Combine(_tmp, "autosave", "abc", "autosave-20261106-120100000.qsave"), path);
        Assert.True(File.Exists(path));

        Assert.Null(a.Tick(T0.AddMinutes(3), 5, dirty: true));               // clock was reset by SaveNow
        Assert.NotNull(a.Tick(T0.AddMinutes(7), 5, dirty: true));

        for (var i = 0; i < 12; i++) a.SaveNow(T0.AddMinutes(10).AddSeconds(i), 5);
        Assert.Equal(14, Directory.GetFiles(Path.Combine(_tmp, "autosave", "abc")).Length);   // all kept: under 20

        var bad = new Autosaver(() => "m", _ => throw new IOException("disk full"), _tmp, _ => { });
        Assert.Null(bad.SaveNow(T0, 5));                                     // never throws
        Assert.Null(new Autosaver(() => null, _ => { }, _tmp).SaveNow(T0, 5));
    }

    [Fact]
    public void Autosaver_Prune_KeepsNewest20_OldStyle()
    {
        var dir = Path.Combine(_tmp, "d");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 24; i++) File.WriteAllText(Path.Combine(dir, $"autosave-20261106-1200{i:00}.qsave"), "x");
        File.WriteAllText(Path.Combine(dir, "mine.qsave"), "x");        // not ours: never touched

        Autosaver.Prune(dir);

        var left = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(21, left.Count);
        Assert.Contains("mine.qsave", left);
        Assert.DoesNotContain("autosave-20261106-120000.qsave", left);
        Assert.DoesNotContain("autosave-20261106-120003.qsave", left);
        Assert.Contains("autosave-20261106-120004.qsave", left);
        Assert.Contains("autosave-20261106-120023.qsave", left);
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

    [Fact]
    public void Prune_Keeps20NewestPlusFirstOfEachTurn()
    {
        var dir = Path.Combine(_tmp, "t");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 35; i++)
        {
            var turn = i / 9 + 1; // 1..4
            File.WriteAllText(Path.Combine(dir, $"autosave-20261106-12{i:00}00000-t{turn}.qsave"), "x");
        }
        Autosaver.Prune(dir);
        var left = Directory.GetFiles(dir).Select(Path.GetFileName).ToHashSet();
        for (var i = 15; i < 35; i++) Assert.Contains($"autosave-20261106-12{i:00}00000-t{i / 9 + 1}.qsave", left);
        foreach (var i in new[] { 0, 9, 18 }) Assert.Contains($"autosave-20261106-12{i:00}00000-t{i / 9 + 1}.qsave", left); // first of turns 1-3 (turn 4 first, i=27, is recent)
        Assert.Equal(22, left.Count); // 15..34 recent + firsts of turns 1 and 2 (turn 3 first, i=18, is recent)
    }

    [Fact]
    public void Prune_OldStyleNames_OnlyRecent()
    {
        var dir = Path.Combine(_tmp, "o");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 25; i++) File.WriteAllText(Path.Combine(dir, $"autosave-20261106-1200{i:00}.qsave"), "x");
        Autosaver.Prune(dir);
        Assert.Equal(20, Directory.GetFiles(dir).Length);
        Assert.False(File.Exists(Path.Combine(dir, "autosave-20261106-120000.qsave")));
    }

    [Fact]
    public void Write_SameMillisecond_DoesNotOverwrite()
    {
        var a = new Autosaver(() => "abc", p => File.WriteAllText(p, "x"), _tmp);
        var p1 = a.SaveNow(T0, 5);
        var p2 = a.SaveNow(T0, 5);
        var p3 = a.SaveNow(T0, 5);
        Assert.Equal(3, new[] { p1, p2, p3 }.Distinct().Count());
        Assert.EndsWith("autosave-20261106-120000000-2.qsave", p2);
        Assert.EndsWith("autosave-20261106-120000000-3.qsave", p3);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_tmp, "autosave", "abc")).Length);
    }

    [Fact]
    public void FileName_HasTurnTag()
    {
        int? turn = 7;
        var a = new Autosaver(() => "abc", p => File.WriteAllText(p, "x"), _tmp, turn: () => turn);
        Assert.EndsWith("autosave-20261106-120000123-t7.qsave", a.SaveNow(T0.AddMilliseconds(123), 5));
        turn = null;
        Assert.EndsWith("autosave-20261106-120001000.qsave", a.SaveNow(T0.AddSeconds(1), 5));
    }

    [Fact]
    public void SaveForced_WritesWhenIntervalOff()
    {
        var a = new Autosaver(() => "abc", p => File.WriteAllText(p, "x"), _tmp);
        var path = a.SaveForced(T0);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Null(a.Tick(T0.AddMinutes(3), 5, dirty: true)); // interval reset by the forced save
        Assert.Null(new Autosaver(() => null, _ => { }, _tmp).SaveForced(T0));
        Assert.Null(new Autosaver(() => "m", _ => throw new IOException("x"), _tmp, _ => { }).SaveForced(T0));
    }
}
