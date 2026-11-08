using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

public sealed class LedgerTabStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qm-ledgertabs-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static UserLedgerTab Tab(string name, string? filter = null) =>
        new(name, filter, new[] { "a", "b" }, true, "TEST note");

    [Fact]
    public void Load_Missing_Default()
    {
        var p = LedgerTabStore.Load(_root, "sha1");
        Assert.Empty(p.Tabs);
        Assert.Null(p.LastTab);
        Assert.True(p.ShowCounters);
        Assert.Null(p.Side);
        Assert.Equal(LedgerGrouping.Map, p.Grouping);
        Assert.Empty(p.ColumnWidths);
    }

    [Fact]
    public void SaveLoad_RoundTrip()
    {
        var prefs = new LedgerPrefs(new[] { Tab("Supply TEST", "x > 1") }, "user:Supply TEST", false, "blue", LedgerGrouping.Side,
            new Dictionary<string, IReadOnlyList<int>> { ["units"] = new[] { 80, 120 }, ["user:Supply TEST"] = new[] { 50 } });
        LedgerTabStore.Save(_root, "sha1", prefs);
        var p = LedgerTabStore.Load(_root, "sha1");
        var t = Assert.Single(p.Tabs);
        Assert.Equal("Supply TEST", t.Name);
        Assert.Equal("x > 1", t.Filter);
        Assert.Equal(new[] { "a", "b" }, t.ColumnIds);
        Assert.True(t.IncludeHolders);
        Assert.Equal("TEST note", t.Notes);
        Assert.Equal("user:Supply TEST", p.LastTab);
        Assert.False(p.ShowCounters);
        Assert.Equal("blue", p.Side);
        Assert.Equal(LedgerGrouping.Side, p.Grouping);
        Assert.Equal(new[] { 80, 120 }, p.ColumnWidths["units"]);
        Assert.Equal(new[] { 50 }, p.ColumnWidths["user:Supply TEST"]);
    }

    [Fact]
    public void Save_KeepsOtherModules()
    {
        LedgerTabStore.Save(_root, "A", LedgerPrefs.Default with { Tabs = new[] { Tab("One") } });
        LedgerTabStore.Save(_root, "B", LedgerPrefs.Default with { Tabs = new[] { Tab("Two") } });
        Assert.Equal("One", Assert.Single(LedgerTabStore.Load(_root, "A").Tabs).Name);
        Assert.Equal("Two", Assert.Single(LedgerTabStore.Load(_root, "B").Tabs).Name);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"sha1\": 5}")]
    [InlineData("{\"sha1\": {\"tabs\": 7, \"showCounters\": \"x\", \"grouping\": 3, \"columnWidths\": []}}")]
    public void Load_CorruptJson_Default(string json)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "ledger-tabs.json"), json);
        var p = LedgerTabStore.Load(_root, "sha1");
        Assert.Empty(p.Tabs);
        Assert.True(p.ShowCounters);
        Assert.Equal(LedgerGrouping.Map, p.Grouping);
    }

    [Theory]
    [InlineData("{\"A\": {\"tabs\": [{\"name\": \"One TEST\"}]}, oops")]
    [InlineData("[1,2]")]
    public void UnparseableFile_CopiedToBadOnce_BeforeSaveOverwritesIt(string json)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "ledger-tabs.json");
        File.WriteAllText(path, json);
        LedgerTabStore.Load(_root, "A");
        LedgerTabStore.Save(_root, "B", LedgerPrefs.Default with { Tabs = new[] { Tab("Two") } });
        LedgerTabStore.Save(_root, "B", LedgerPrefs.Default with { Tabs = new[] { Tab("Three") } });
        var bad = Assert.Single(Directory.GetFiles(_root, "ledger-tabs.json.bad-*"));
        Assert.Matches(@"ledger-tabs\.json\.bad-\d{14}$", bad);
        Assert.Equal(json, File.ReadAllText(bad)); // the original bytes survive
        Assert.Equal("Three", Assert.Single(LedgerTabStore.Load(_root, "B").Tabs).Name);
    }

    [Fact]
    public void ParseableFile_NoBadCopy()
    {
        LedgerTabStore.Save(_root, "A", LedgerPrefs.Default with { Tabs = new[] { Tab("One") } });
        LedgerTabStore.Save(_root, "B", LedgerPrefs.Default with { Tabs = new[] { Tab("Two") } });
        Assert.Empty(Directory.GetFiles(_root, "ledger-tabs.json.bad-*"));
    }

    [Fact]
    public void Save_FileLocked_LeavesItAlone()
    {
        LedgerTabStore.Save(_root, "A", LedgerPrefs.Default with { Tabs = new[] { Tab("One") } });
        var path = Path.Combine(_root, "ledger-tabs.json");
        var before = File.ReadAllText(path);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            LedgerTabStore.Save(_root, "B", LedgerPrefs.Default with { Tabs = new[] { Tab("Two") } });
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_root, "ledger-tabs.json.bad-*"));
        Assert.Equal("One", Assert.Single(LedgerTabStore.Load(_root, "A").Tabs).Name);
    }

    [Fact]
    public void Load_DuplicateNames_FirstWins()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "ledger-tabs.json"),
            "{\"sha1\":{\"tabs\":[{\"name\":\" Foo \",\"filter\":\"first\"},{\"name\":\"foo\",\"filter\":\"second\"},{\"name\":\"  \"},{\"name\":\"Bar\"}]}}");
        var p = LedgerTabStore.Load(_root, "sha1");
        Assert.Equal(new[] { "Foo", "Bar" }, p.Tabs.Select(t => t.Name));
        Assert.Equal("first", p.Tabs[0].Filter);
    }

    [Fact]
    public void Load_UnknownFields_Ignored()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "ledger-tabs.json"),
            "{\"future\":1,\"sha1\":{\"zzz\":true,\"showCounters\":false,\"tabs\":[{\"name\":\"T\",\"extra\":1}]}}");
        var p = LedgerTabStore.Load(_root, "sha1");
        Assert.False(p.ShowCounters);
        Assert.Equal("T", Assert.Single(p.Tabs).Name);
    }

    [Fact]
    public void Save_RaisesChanged()
    {
        var seen = new List<string>();
        void H(string s) => seen.Add(s);
        LedgerTabStore.Changed += H;
        try { LedgerTabStore.Save(_root, "shaX", LedgerPrefs.Default); }
        finally { LedgerTabStore.Changed -= H; }
        Assert.Equal(new[] { "shaX" }, seen);
    }

    [Fact]
    public void Save_DoesNotTouchRulesPackHash()
    {
        var pack = RulesPackIo.CreateEmpty("TEST pack", "sha1");
        var zip = Path.Combine(_root, "t.qrules");
        Directory.CreateDirectory(_root);
        RulesPackIo.Save(pack, zip);
        var before = RulesPackIo.ComputeSha256(RulesPackIo.Load(zip));
        LedgerTabStore.Save(_root, "sha1", LedgerPrefs.Default with { Tabs = new[] { Tab("One") } });
        Assert.Equal(before, RulesPackIo.ComputeSha256(RulesPackIo.Load(zip)));
    }

    [Fact]
    public void Normalize_TrimsDropsBlankDedups()
    {
        var r = LedgerTabStore.Normalize(new[] { Tab(" A "), Tab("a"), Tab(""), Tab("B") });
        Assert.Equal(new[] { "A", "B" }, r.Select(t => t.Name));
    }
}
