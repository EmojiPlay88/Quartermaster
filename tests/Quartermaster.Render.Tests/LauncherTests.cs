using System.Security.Cryptography;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

public class LauncherTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-launcher-" + Guid.NewGuid().ToString("N"));

    public LauncherTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    string Module(string name, string content = "a")
    {
        var p = Path.Combine(_tmp, name + ".vmod");
        File.WriteAllText(p, content);
        return p;
    }

    static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    SettingsStore NewStore() => new(Path.Combine(_tmp, "root"));

    SettingsStore LoadedStore()
    {
        var s = NewStore();
        s.Load(null);
        return s;
    }

    [Fact]
    public void Add_Remove_Persist()
    {
        var lib = new ModuleLibrary(NewStore());
        var a = Module("a"); var b = Module("b");
        lib.Add(a); lib.Add(b); lib.Add(a);
        Assert.Equal(2, lib.Rows.Count);
        var again = new ModuleLibrary(LoadedStore());
        Assert.Equal(2, again.Rows.Count);
        again.Remove(a);
        Assert.Single(again.Rows);
        Assert.True(File.Exists(a));
        Assert.Single(new ModuleLibrary(LoadedStore()).Rows);
    }

    [Fact]
    public void MissingFile_Greyed()
    {
        var lib = new ModuleLibrary(NewStore());
        var a = Module("a");
        lib.Add(a);
        Assert.False(lib.Rows[0].Missing);
        File.Delete(a);
        Assert.True(lib.Rows[0].Missing);
    }

    [Fact]
    public async Task Refresh_RehashOnlyWhenChanged()
    {
        var lib = new ModuleLibrary(NewStore());
        var count = 0;
        lib.Hasher = p => { Interlocked.Increment(ref count); return Sha(p); };
        var a = Module("a");
        lib.Add(a);
        await lib.RefreshAsync(null, CancellationToken.None);
        Assert.Equal(1, count);
        Assert.Equal(Sha(a), lib.Rows[0].Entry.Sha256);
        await lib.RefreshAsync(null, CancellationToken.None);
        Assert.Equal(1, count);
    }

    string WriteAutosave(string sha, string name, int turn, string? phase, DateTime? when = null)
    {
        var dir = Path.Combine(_tmp, "root", "autosave", sha);
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        var ph = phase == null ? "null" : "\"" + phase + "\"";
        File.WriteAllText(p, $"{{\"format\":7,\"moduleSha256\":\"{sha}\",\"snapshot\":{{\"turn\":{{\"turn\":{turn},\"phase\":{ph}}},\"pieces\":{{}}}},\"log\":[]}}");
        if (when != null) File.SetLastWriteTimeUtc(p, when.Value);
        return p;
    }

    [Fact]
    public async Task ModuleReplaced_RehashesAndMovesContinue()
    {
        var store = NewStore();
        var lib = new ModuleLibrary(store);
        var recent = new RecentGames(store, store.Root);
        var a = Module("a", "one");
        lib.Add(a);
        await lib.RefreshAsync(null, CancellationToken.None);
        var oldSha = lib.Rows[0].Entry.Sha256!;
        WriteAutosave(oldSha, "autosave-20260101-000000000-t3.qsave", 3, "Move");
        Assert.NotNull(recent.Continue(oldSha));
        File.WriteAllText(a, "two, longer");
        await lib.RefreshAsync(null, CancellationToken.None);
        var newSha = lib.Rows[0].Entry.Sha256!;
        Assert.NotEqual(oldSha, newSha);
        Assert.Equal(Sha(a), newSha);
        Assert.Null(recent.Continue(newSha));
    }

    [Fact]
    public void RulesPack_PerSha_ViaRulesMemory()
    {
        var store = NewStore();
        var lib = new ModuleLibrary(store);
        var e1 = new ModuleEntry("x", "sha1", 1, DateTime.UtcNow, "t", "", null);
        var e2 = e1 with { Sha256 = "sha2" };
        Assert.Null(lib.RulesPackFor(e1));
        lib.SetRulesPack(e1, "C:\\packs\\one.qrules");
        Assert.Equal("C:\\packs\\one.qrules", lib.RulesPackFor(e1));
        Assert.Equal("C:\\packs\\one.qrules", RulesMemory.LastFor(store.Root, "sha1"));
        Assert.Null(lib.RulesPackFor(e2));
        Assert.Null(lib.RulesPackFor(e1 with { Sha256 = null }));
    }

    [Fact]
    public void RecentSaves_Dedupe_Max10()
    {
        var store = NewStore();
        var recent = new RecentGames(store, store.Root);
        for (var i = 0; i < 12; i++) recent.Add("s", Path.Combine(_tmp, $"g{i}.qsave"));
        recent.Add("s", Path.Combine(_tmp, "g5.qsave"));
        File.WriteAllText(Path.Combine(_tmp, "g5.qsave"), "x");
        var list = recent.For("s");
        Assert.Equal(10, list.Count);
        Assert.Equal(Path.Combine(_tmp, "g5.qsave"), list[0].Path);
        Assert.False(list[0].Missing);
        Assert.True(list[1].Missing);
        Assert.Single(list, r => r.Path.EndsWith("g5.qsave"));
        Assert.Equal(Path.Combine(_tmp, "g11.qsave"), list[1].Path);
        Assert.Empty(recent.For("other"));
        Assert.Equal(10, new RecentGames(LoadedStore(), store.Root).For("s").Count);
    }

    [Fact]
    public void Continue_NewestAutosave_WithTurnPhase()
    {
        var store = NewStore();
        var recent = new RecentGames(store, store.Root);
        Assert.Null(recent.Continue("none"));
        WriteAutosave("s", "autosave-20260101-000000000-t1.qsave", 1, null, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newest = WriteAutosave("s", "autosave-20260102-000000000-t4.qsave", 4, "Combat", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var c = recent.Continue("s")!;
        Assert.Equal(newest, c.Path);
        Assert.Equal(4, c.Turn);
        Assert.Equal("Combat", c.Phase);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), c.At);
        var bad = Path.Combine(_tmp, "root", "autosave", "s", "autosave-20260103-000000000.qsave");
        File.WriteAllText(bad, "garbage");
        File.SetLastWriteTimeUtc(bad, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));
        var c2 = recent.Continue("s")!;
        Assert.Equal(bad, c2.Path);
        Assert.Equal(0, c2.Turn);
        Assert.Null(c2.Phase);
    }

    [Fact]
    public void Continue_ReadsRealNativeSave()
    {
        var store = NewStore();
        var dir = Path.Combine(_tmp, "root", "autosave", "realsha");
        Directory.CreateDirectory(dir);
        var session = new GameSession(new GameState { Turn = new TurnState(2, "X") }, "realsha");
        NativeSave.Write(Path.Combine(dir, "autosave-20260101-000000000-t2.qsave"), session);
        var c = new RecentGames(store, store.Root).Continue("realsha")!;
        Assert.Equal(2, c.Turn);
        Assert.Equal("X", c.Phase);
    }

    [Fact]
    public void Continue_ReadsTurnPastBufferBoundary()
    {
        var store = NewStore();
        var dir = Path.Combine(_tmp, "root", "autosave", "big");
        Directory.CreateDirectory(dir);
        var pad = new string('x', 100_000);
        File.WriteAllText(Path.Combine(dir, "autosave-1.qsave"), "{\"format\":7,\"notes\":\"" + pad + "\",\"snapshot\":{\"turn\":{\"turn\":9,\"phase\":\"Z\"}}}");
        var c = new RecentGames(store, store.Root).Continue("big")!;
        Assert.Equal(9, c.Turn);
        Assert.Equal("Z", c.Phase);
    }

    [Fact]
    public void RecentGames_NullSha_Empty_AndKeysCaseInsensitive()
    {
        var store = NewStore();
        var recent = new RecentGames(store, store.Root);
        Assert.Empty(recent.For(null));
        Assert.Empty(recent.For(""));
        recent.Add("ABCD", "a.qsave");
        recent.Add("abcd", "b.qsave");
        Assert.Equal(2, recent.For("AbCd").Count);
        Assert.Single(store.Current.RecentSaves);
    }

    [Fact]
    public async Task ConcurrentUpdates_LoseNothing()
    {
        var store = NewStore();
        var recent = new RecentGames(store, store.Root);
        var lib = new ModuleLibrary(store);
        var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 10; i++)
            {
                recent.Add("sha" + t, $"t{t}-{i}.qsave");
                lib.Add(Module($"m{t}_{i}"));
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(80, lib.Rows.Count);
        for (var t = 0; t < 8; t++) Assert.Equal(10, recent.For("sha" + t).Count);
        var back = LoadedStore();
        Assert.Equal(80, back.Current.Modules.Count);
        Assert.Equal(8, back.Current.RecentSaves.Count);
    }

    sealed class SyncProgress<T>(Action<T> a) : IProgress<T> { public void Report(T v) => a(v); }

    [Fact]
    public async Task Library_HashesOffThread_ReportsProgress()
    {
        var lib = new ModuleLibrary(NewStore());
        var pool = new List<bool>();
        lib.Hasher = p => { lock (pool) pool.Add(Thread.CurrentThread.IsThreadPoolThread); return Sha(p); };
        lib.Add(Module("a")); lib.Add(Module("b"));
        var rows = new List<ModuleRow>();
        await lib.RefreshAsync(new SyncProgress<ModuleRow>(rows.Add), CancellationToken.None);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.NotNull(r.Entry.Sha256));
        Assert.Equal(2, pool.Count);
        Assert.All(pool, Assert.True);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lib.RefreshAsync(null, cts.Token));
    }

    [Fact]
    public void Rows_SortedByLastPlayedThenTitle()
    {
        var lib = new ModuleLibrary(NewStore());
        var a = Module("alpha"); var b = Module("bravo"); var c = Module("charlie");
        lib.Add(c); lib.Add(b); lib.Add(a);
        Assert.Equal(new[] { "alpha", "bravo", "charlie" }, lib.Rows.Select(r => r.Entry.Title));
        lib.MarkPlayed(b, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        lib.MarkPlayed(c, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(new[] { "charlie", "bravo", "alpha" }, lib.Rows.Select(r => r.Entry.Title));
    }

    [Fact]
    public void Settings_NewFields_RoundTrip()
    {
        Assert.True(new Settings().ReopenLastModule);
        Assert.Empty(new Settings().Modules);
        var store = NewStore();
        var t = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var e = new ModuleEntry("C:\\m\\a.vmod", "abc", 123, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), "A", "1.2", t);
        var e2 = new ModuleEntry("C:\\m\\b.vmod", null, 0, DateTime.MinValue, "B", "", null);
        store.Save(new Settings
        {
            ReopenLastModule = false, Modules = new[] { e, e2 },
            RecentSaves = new Dictionary<string, IReadOnlyList<string>> { ["abc"] = new[] { "x.qsave", "y.qsave" } },
        });
        var back = NewStore().Load(new DiagnosticLog());
        Assert.False(back.ReopenLastModule);
        Assert.Equal(new[] { e, e2 }, back.Modules);
        Assert.Equal(new[] { "x.qsave", "y.qsave" }, back.RecentSaves["abc"]);
        Directory.CreateDirectory(Path.Combine(_tmp, "r2"));
        File.WriteAllText(Path.Combine(_tmp, "r2", "settings.json"), "{\"Modules\":[{\"Path\":1},{\"Path\":\"ok.vmod\"}],\"RecentSaves\":{\"a\":[\"p\",3]}}");
        var tolerant = new SettingsStore(Path.Combine(_tmp, "r2")).Load(new DiagnosticLog());
        Assert.True(tolerant.ReopenLastModule);
        Assert.Equal("ok.vmod", Assert.Single(tolerant.Modules).Path);
        Assert.Equal(new[] { "p" }, tolerant.RecentSaves["a"]);
    }

    static NewGameRequest Req(GameMode m, string? lobby = null, string? pw = null, IReadOnlyList<PlayerDef>? players = null) =>
        new(m, null, Difficulty.SemiAuto, players, lobby, pw);

    [Fact]
    public void Validate_Offline_RejectsLobbyFields()
    {
        Assert.Null(NewGameRules.Validate(Req(GameMode.Offline), null));
        Assert.NotNull(NewGameRules.Validate(Req(GameMode.Offline, lobby: "x"), null));
        Assert.NotNull(NewGameRules.Validate(Req(GameMode.Offline, pw: "x"), null));
    }

    [Fact]
    public void Validate_Online_LobbyNameRules()
    {
        Assert.Null(NewGameRules.Validate(Req(GameMode.Online, lobby: "  Table 1 "), null));
        Assert.NotNull(NewGameRules.Validate(Req(GameMode.Online), null));
        Assert.NotNull(NewGameRules.Validate(Req(GameMode.Online, lobby: "   "), null));
        Assert.Null(NewGameRules.Validate(Req(GameMode.Online, lobby: new string('a', 40)), null));
        Assert.NotNull(NewGameRules.Validate(Req(GameMode.Online, lobby: new string('a', 41)), null));
    }

    [Fact]
    public void Validate_Players_UsesPlayersValidate()
    {
        var pack = new Quartermaster.Rules.RulesPack { Sides = { new() { Id = "a", Name = "A" }, new() { Id = "b", Name = "B" } } };
        var bad = new[] { new PlayerDef("1", "X", new[] { "a" }), new PlayerDef("2", "Y", new[] { "a" }) };
        var good = new[] { new PlayerDef("1", "X", new[] { "a" }), new PlayerDef("2", "Y", new[] { "b" }) };
        Assert.Equal(Players.Validate(bad, pack), NewGameRules.Validate(Req(GameMode.Offline, players: bad), pack));
        Assert.NotNull(NewGameRules.Validate(Req(GameMode.Offline, players: bad), pack));
        Assert.Null(NewGameRules.Validate(Req(GameMode.Offline, players: good), pack));
        Assert.Null(NewGameRules.Validate(Req(GameMode.Offline, players: bad), null));
    }

    [Theory]
    [InlineData(null, false, StartupAction.Launcher)]
    [InlineData("m.vmod", false, StartupAction.DirectModule)]
    [InlineData("m.vmod", true, StartupAction.DirectModule)]
    [InlineData(null, true, StartupAction.DirectModule)]
    [InlineData("", false, StartupAction.Launcher)]
    public void StartupDecision_Table(string? module, bool smoke, StartupAction expected)
        => Assert.Equal(expected, StartupDecision.For(module, smoke));

    [Fact]
    public void ReturnToLauncher_UpdatesLastPlayed()
    {
        var store = NewStore();
        var lib = new ModuleLibrary(store);
        var recent = new RecentGames(store, Path.Combine(_tmp, "root"));
        var a = Module("a");
        lib.Add(a);
        var save = Path.Combine(_tmp, "g.qsave");
        File.WriteAllText(save, "{}");
        var when = new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
        LaunchBookkeeping.ModuleOpened(lib, a, when);
        LaunchBookkeeping.SaveUsed(recent, "abc123", save);
        Assert.Equal(when, lib.Rows[0].Entry.LastPlayed);
        var reloaded = LoadedStore();
        Assert.Equal(when, new ModuleLibrary(reloaded).Rows[0].Entry.LastPlayed);
        Assert.Equal(save, new RecentGames(reloaded, Path.Combine(_tmp, "root")).For("abc123")[0].Path);
        // Blank hash or path is ignored, never throws.
        LaunchBookkeeping.SaveUsed(recent, null, save);
        LaunchBookkeeping.ModuleOpened(lib, "", when);
    }
}
