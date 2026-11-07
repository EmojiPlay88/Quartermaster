using Quartermaster.Core;
using Quartermaster.Net;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>The Online lobby's logic and the online game window's model parts. Invented Red/Blue sides only; temp folders only.</summary>
public class OnlineLobbyModelTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-lobby-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();
    private const string Sha = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    public OnlineLobbyModelTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static RulesPack Pack(params (string Id, string Name)[] sides) =>
        RulesPackIo.CreateEmpty("t", Sha) with { Sides = sides.Select(s => new SideDef { Id = s.Id, Name = s.Name }).ToList() };

    private static LobbyListing Listing(string code, string name = "Evening game", string sha = Sha, int players = 1, int max = 2,
        IReadOnlyList<string>? free = null, bool locked = false, int version = Protocol.ProtocolVersion) =>
        new(code, name, "Alice", sha, "Fixture", new[] { "Red", "Blue" }, free ?? new[] { "Blue" }, players, max, locked, version);

    private ModuleSession Open()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        return s;
    }

    // ---- relay address ----

    [Theory]
    [InlineData("relay.example.org", "wss://relay.example.org:47820/qm")]
    [InlineData("  relay.example.org:5000 ", "wss://relay.example.org:5000/qm")]
    [InlineData("wss://relay.example.org", "wss://relay.example.org:47820/qm")]
    [InlineData("wss://relay.example.org:443", "wss://relay.example.org/qm")]
    [InlineData("https://relay.example.org:9000/custom", "wss://relay.example.org:9000/custom")]
    [InlineData("127.0.0.1:47821", "wss://127.0.0.1:47821/qm")]
    [InlineData("[::1]:6000", "wss://[::1]:6000/qm")]
    public void ParseRelay_ReadsWhatPeopleType(string text, string expected)
    {
        var uri = OnlineLobbyModel.ParseRelay(text, out var error);
        Assert.Null(error);
        Assert.Equal(expected, uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("", OnlineLobbyModel.NoAddressText)]
    [InlineData("   ", OnlineLobbyModel.NoAddressText)]
    [InlineData("ws://relay.example.org", OnlineLobbyModel.InsecureText)]
    [InlineData("http://relay.example.org", OnlineLobbyModel.InsecureText)]
    [InlineData("ftp://relay.example.org", OnlineLobbyModel.BadAddressText)]
    [InlineData("not a host", OnlineLobbyModel.BadAddressText)]
    public void ParseRelay_RefusesWithAReason(string text, string reason)
    {
        Assert.Null(OnlineLobbyModel.ParseRelay(text, out var error));
        Assert.Equal(reason, error);
    }

    // ---- the table ----

    [Fact]
    public void Rows_OnlyThisModule_OwnLobbyHidden()
    {
        var rows = OnlineLobbyModel.Rows(new[]
        {
            Listing("AAAAAA", "Ours"),
            Listing("BBBBBB", "Theirs"),
            Listing("CCCCCC", "Other module", sha: new string('1', 64)),
            null,
        }, Sha.ToLowerInvariant(), new HashSet<string> { "AAAAAA" });
        var row = Assert.Single(rows);
        Assert.Equal("Theirs", row.Lobby);
        Assert.Equal("Alice", row.Host);
        Assert.Equal("Blue", row.FreeSides);
        Assert.Equal("1/2", row.Players);
        Assert.Null(row.CannotJoin);
    }

    [Fact]
    public void Rows_LockedFullAndOtherVersions()
    {
        var rows = OnlineLobbyModel.Rows(new[]
        {
            Listing("AAAAAA", "Full", players: 2, max: 2),
            Listing("BBBBBB", "No side", free: Array.Empty<string>()),
            Listing("CCCCCC", "Newer", version: 2),
            Listing("DDDDDD", "Locked", locked: true),
        }, Sha);
        Assert.Equal("Locked", rows[0].Lobby);   // joinable first
        Assert.True(rows[0].Locked);
        Assert.Null(rows[0].CannotJoin);
        Assert.Equal(OnlineLobbyModel.FullText, rows.Single(r => r.Lobby == "Full").CannotJoin);
        Assert.Equal(OnlineLobbyModel.FullText, rows.Single(r => r.Lobby == "No side").CannotJoin);
        Assert.Equal("—", rows.Single(r => r.Lobby == "No side").FreeSides);
        Assert.Equal(Protocol.MismatchMessage(2), rows.Single(r => r.Lobby == "Newer").CannotJoin);
    }

    [Fact]
    public void Rows_ControlCharactersAndLongNamesAreCleaned()
    {
        var row = Assert.Single(OnlineLobbyModel.Rows(new[] { Listing("AAAAAA", "Line\none" + new string('x', 80)) }, Sha));
        Assert.DoesNotContain('\n', row.Lobby);
        Assert.Equal(NewGameRules.MaxLobbyName, row.Lobby.Length);
    }

    [Fact]
    public void Rows_UntrustedListingsAreBounded()
    {
        var many = Enumerable.Range(0, 40).Select(i => "Side" + i).ToList();
        var row = Assert.Single(OnlineLobbyModel.Rows(new[] { Listing("AAAAAA", free: many, max: 6) }, Sha));
        Assert.Equal(OnlineLobbyModel.MaxFreeSidesShown, row.FreeSides.Split(", ").Count(x => x.StartsWith("Side", StringComparison.Ordinal)));
        Assert.EndsWith("…", row.FreeSides);
        var flood = Enumerable.Range(0, 500).Select(i => Listing("AAAAAA", "L" + i));
        Assert.Equal(OnlineLobbyModel.MaxRows, OnlineLobbyModel.Rows(flood, Sha).Count);
        Assert.Equal("a b", OnlineLobbyModel.CleanText("a\u0007b"));
        Assert.Equal(5, OnlineLobbyModel.CleanText(new string('x', 99), 5).Length);
    }

    [Fact]
    public void CertificateTrust_CanBeRepointed_AndNeverThrows()
    {
        var t = new CertificateTrust();
        var check = new CertificateCheck("relay:1", "AA", null);
        Assert.False(t.Check(check));                 // nobody asked yet: no
        t.Ask = _ => true;
        Assert.True(t.Check(check));
        t.Ask = _ => throw new InvalidOperationException("window gone");
        Assert.False(t.Check(check));
        t.Ask = null!;
        Assert.False(t.Check(check));
    }

    [Fact]
    public void GuestSaveText_NamesTheMenuItem() => Assert.Contains("Export Viewer Copy…", ModuleSession.GuestSaveText);

    [Fact]
    public void HostedCodes_AreRemembered_AndForgotten()
    {
        OnlineLobbyModel.MarkHosted("zz9-zz8");
        Assert.Contains("ZZ9ZZ8", OnlineLobbyModel.HostedCodes);
        OnlineLobbyModel.UnmarkHosted("ZZ9ZZ8");
        Assert.DoesNotContain("ZZ9ZZ8", OnlineLobbyModel.HostedCodes);
    }

    [Fact]
    public void Title_IsTheExactCopy() => Assert.Equal("Online lobby — Fixture", OnlineLobbyModel.Title("Fixture"));

    // ---- hosting ----

    [Fact]
    public void ValidateHostSides_NeedsOwnAndOpenSides()
    {
        var pack = Pack(("red", "Red"), ("blue", "Blue"));
        Assert.Null(OnlineLobbyModel.ValidateHostSides(pack, new[] { "red" }));
        Assert.Equal(OnlineLobbyModel.NoOwnSideText, OnlineLobbyModel.ValidateHostSides(pack, Array.Empty<string>()));
        Assert.Equal(OnlineLobbyModel.NoOpenSideText, OnlineLobbyModel.ValidateHostSides(pack, new[] { "red", "blue" }));
        Assert.Equal(Seats.NotASideText, OnlineLobbyModel.ValidateHostSides(pack, new[] { "green" }));
        Assert.Equal(OnlineLobbyModel.NeedsPackText, OnlineLobbyModel.ValidateHostSides(Pack(("red", "Red")), new[] { "red" }));
        Assert.Equal(OnlineLobbyModel.NeedsPackText, OnlineLobbyModel.ValidateHostSides(null, new[] { "red" }));
    }

    [Fact]
    public void Draft_OpenSeatsAreTheOtherSides()
    {
        var pack = Pack(("red", "Red"), ("blue", "Blue"), ("green", ""));
        var d = OnlineLobbyModel.Draft("abc-234", "  Friday\tnight  ", "Alice", Sha, "Fixture", pack, new[] { "red" }, locked: true);
        Assert.Equal("ABC234", d.Code);
        Assert.Equal("Friday night", d.Name);
        Assert.Equal(new[] { "Red", "Blue", "green" }, d.Sides);
        Assert.Equal(new[] { "Blue", "green" }, d.FreeSides);
        Assert.Equal(1, d.Players);
        Assert.Equal(3, d.MaxPlayers);
        Assert.True(d.Locked);
        Assert.Equal(Protocol.ProtocolVersion, d.Version);
    }

    [Fact]
    public void CheckSave_OtherModuleRefused_ViewerCopyRefused()
    {
        var s = Open();
        s.LoadSetup(s.Model.Setups[0], Difficulty.SemiAuto);
        var save = Path.Combine(_tmp, "game.qsave");
        s.SaveNative(save);
        Assert.Null(OnlineLobbyModel.CheckSave(save, s.Archive.Sha256));
        Assert.Equal(OnlineLobbyModel.DifferentModuleText, OnlineLobbyModel.CheckSave(save, new string('0', 64)));
        Assert.Equal("This save belongs to a different module", OnlineLobbyModel.DifferentModuleText);
        Assert.StartsWith("That saved game could not be read", OnlineLobbyModel.CheckSave(Path.Combine(_tmp, "missing.qsave"), s.Archive.Sha256));

        s.EnterGuestMode();
        s.ApplySnapshot(new GameState());
        var viewer = Path.Combine(_tmp, "viewer.qsave");
        s.ExportViewerCopy(viewer);
        Assert.Equal(OnlineLobbyModel.ViewerCopyText, OnlineLobbyModel.CheckSave(viewer, s.Archive.Sha256));
    }

    [Fact]
    public void SuggestedHostSides_FromTheSavesFirstPlayer()
    {
        var s = Open();
        s.LoadSetup(s.Model.Setups[0], Difficulty.SemiAuto, new[] { new PlayerDef("p1", "A", new[] { "blue" }), new PlayerDef("p2", "B", new[] { "red" }) });
        var save = Path.Combine(_tmp, "two.qsave");
        s.SaveNative(save);
        var pack = Pack(("red", "Red"), ("blue", "Blue"));
        Assert.Equal(new[] { "blue" }, OnlineLobbyModel.SuggestedHostSides(save, pack));
        Assert.Equal(new[] { "red" }, OnlineLobbyModel.SuggestedHostSides(null, pack));
    }

    // ---- status, banner, certificate ----

    [Fact]
    public void StatusText_IsTheExactCopy()
    {
        Assert.Equal("Online: ABC234 · 3 players", OnlineLobbyModel.StatusText("ABC234", 3));
        Assert.Equal("Online: ABC234 · 1 players — reconnecting…", OnlineLobbyModel.StatusText("ABC234", 0, "reconnecting…"));
    }

    [Fact]
    public void GuestBanner_HostGone_IsTheExactCopy()
    {
        Assert.Equal("Host disconnected — waiting for the host to return.", OnlineLobbyModel.GuestBanner(GuestState.Paused, GuestSession.HostGoneText));
        Assert.Equal(GuestSession.RelayLostText, OnlineLobbyModel.GuestBanner(GuestState.Paused, GuestSession.RelayLostText));
        Assert.Equal(GuestSession.RejoiningText, OnlineLobbyModel.GuestBanner(GuestState.Joining, GuestSession.RejoiningText));
        Assert.Null(OnlineLobbyModel.GuestBanner(GuestState.Playing, null));
        Assert.Null(OnlineLobbyModel.GuestBanner(GuestState.ChoosingSide, null));
    }

    [Fact]
    public void CertificatePrompt_NewAndChanged()
    {
        var fresh = OnlineLobbyModel.CertificatePrompt(new CertificateCheck("relay:47820", "AB12CD34", null));
        Assert.False(fresh.Changed);
        Assert.Contains("AB12 CD34", fresh.Message);
        var changed = OnlineLobbyModel.CertificatePrompt(new CertificateCheck("relay:47820", "AB12CD34", "FFFF0000"));
        Assert.True(changed.Changed);
        Assert.Equal("The relay's certificate has changed", changed.Title);
        Assert.Contains("FFFF 0000", changed.Message);
        Assert.Contains("AB12 CD34", changed.Message);
    }

    [Fact]
    public void OnlineLaunch_ToStringNeverShowsThePassword()
    {
        var o = new OnlineLaunch(new Uri("wss://relay:47820/qm"), "Alice", "hunter2-secret", "Lobby", new[] { "red" }, "ABC234");
        Assert.DoesNotContain("hunter2-secret", o.ToString());
        var req = new LaunchRequest("m.vmod", null, LaunchKind.JoinOnline, null, null, o);
        Assert.DoesNotContain("hunter2-secret", req.ToString());
    }

    // ---- guest limits in the session ----

    [Fact]
    public void Guest_RulesAreReadOnly()
    {
        var s = Open();
        s.NewRules("mine");
        s.EnterGuestMode();
        s.LoadGuestRules(PhaseFixture.Pack(s.Archive.Sha256));
        Assert.Equal(ModuleSession.GuestRulesText, Assert.Throws<InvalidOperationException>(() => s.UpdateRules(p => p with { Name = "x" })).Message);
        Assert.Throws<InvalidOperationException>(() => s.NewRules("other"));
        Assert.Throws<InvalidOperationException>(() => s.SaveRules(Path.Combine(_tmp, "p.qrules")));
        Assert.Throws<InvalidOperationException>(() => s.OpenRules(Path.Combine(_tmp, "p.qrules")));
        Assert.False(File.Exists(Path.Combine(_tmp, "p.qrules")));
    }

    [Fact]
    public void LogFilter_KnowsChatLines() => Assert.Equal(LogKind.Chat, LogFilter.DisplayKind(LogKind.Chat));

    [Fact]
    public void Settings_RelayAndName_RoundTrip()
    {
        var dir = Path.Combine(_tmp, "settings");
        var store = new SettingsStore(dir);
        store.Load(null);
        store.Update(s => s with { RelayAddress = "relay.example.org:47820", PlayerName = "Alice" });
        var back = new SettingsStore(dir).Load(new DiagnosticLog());
        Assert.Equal("relay.example.org:47820", back.RelayAddress);
        Assert.Equal("Alice", back.PlayerName);
    }
}

/// <summary>The window's switchable action port and hot-seat detaching for online play.</summary>
public class OnlineWiringTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-wiring-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public OnlineWiringTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private (ModuleSession S, PhaseController C, HistoryController H) Make()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto);
        s.NewRules("t");
        s.UpdateRules(_ => PhaseFixture.Pack(s.Archive.Sha256));
        Func<GameCommand, GameState, GameCommand> prepare = (c, _) => c;
        var phases = new PhaseController(s, prepare);
        return (s, phases, new HistoryController(s, phases, prepare));
    }

    [Fact]
    public void ActionPortSwitch_ForwardsToTheCurrentPort()
    {
        var (s, c, h) = Make();
        var local = new LocalActions(s, c, h);
        var sw = new ActionPortSwitch(local);
        Assert.False(sw.IsRemote);
        var before = s.Game!.State.Log.Count;
        sw.Chat("hello");
        Assert.Equal(before + 1, s.Game!.State.Log.Count);
        var remote = new RemoteActions(new GuestSession(s, new MemoryRelay().Guest("m1"), "ABC234", "Bob", a => a(),
            new RejoinTokens(Path.Combine(_tmp, "tokens"))));
        sw.Inner = remote;
        Assert.True(sw.IsRemote);
        Assert.Same(remote, sw.Inner);
        Assert.Throws<ArgumentNullException>(() => sw.Inner = null!);
    }

    [Fact]
    public void HotSeat_Detach_StopsChangingTheView()
    {
        var (s, c, h) = Make();
        var hs = new HotSeat(s, c);
        hs.Watch(h);
        var raised = 0;
        hs.HandoverRequired += _ => raised++;
        hs.Detach();
        s.SetViewSides(new HashSet<string> { "red" });   // what an online session would set
        s.Game!.Execute(new SetPlayers(new[] { new PlayerDef("p1", "A", new[] { "red" }), new PlayerDef("p2", "B", new[] { "blue" }) }));
        c.Start();
        c.Advance();
        Assert.Equal(0, raised);
        Assert.True(s.ViewSides!.SetEquals(new[] { "red" }));
        hs.Detach();   // twice is fine
    }
}
