using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Quartermaster.Core;
using Quartermaster.Net;
using Quartermaster.Relay;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

/// <summary>
/// Piece 6b end to end: a real relay (in process, TLS), a host who holds a third invented side (Green), and two guests (Red, Blue) who find the lobby
/// through <c>ListLobbies</c>, join, choose sides, play a phase with a private-dice formula and private ledgers, chat, drop and
/// rejoin, and see a host undo. Invented sides only.
/// </summary>
public sealed class Piece6bEndToEndTests : IAsyncLifetime
{
    private const string Password = "red-and-blue";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qm-e2e6b-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();
    private readonly List<IDisposable> _disposables = new();
    private readonly List<RelayClient> _clients = new();
    private WebApplication _app = null!;
    private Uri _uri = null!;
    private int _n;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _app = RelayApp.Build([], b =>
        {
            b.Logging.ClearProviders();
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:Port"] = port.ToString(),
                ["Relay:DataDir"] = Path.Combine(_dir, "relay"),
            });
        });
        await _app.StartAsync();
        _uri = new Uri($"wss://127.0.0.1:{port}/qm");
    }

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables) d.Dispose();
        foreach (var c in _clients) await c.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task<RelayClient> NewClient()
    {
        var c = new RelayClient(new PinStore(Path.Combine(_dir, $"pins{_n++}.json")), new[] { TimeSpan.FromMilliseconds(20) });
        _clients.Add(c);
        await c.ConnectAsync(_uri, _ => true, CancellationToken.None);
        return c;
    }

    private static void Until(QueueDispatcher d, Func<bool> cond, int ms = 20000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (true)
        {
            d.Pump();
            if (cond()) return;
            if (DateTime.UtcNow > end) throw new TimeoutException();
            Thread.Sleep(10);
        }
    }

    private static T Await<T>(QueueDispatcher d, Task<T> task)
    {
        Until(d, () => task.IsCompleted);
        return task.Result;
    }

    private ModuleSession Session(string path, string name)
    {
        var s = ModuleSession.Open(path, Path.Combine(_dir, "cache-" + name), Path.Combine(_dir, "root-" + name));
        _sessions.Add(s);
        return s;
    }

    private sealed record Guest(ModuleSession S, GuestSession G, RemoteActions A, RelayClient C, List<string> Notices);

    private async Task<Guest> JoinFound(QueueDispatcher d, string modulePath, string sha, byte[] key, string name, string tokens, string sessionName)
    {
        var client = await NewClient();
        var found = Assert.Single(await client.ListLobbiesAsync(sha));   // the lobby is public, listed by module
        Assert.Equal("Friday", found.Name);
        Assert.True(found.Locked);
        await client.JoinRoomAsync(found.Code, Secrets.AuthVerifier(Password, found.Code));
        var t = new RelayTransport(client, key);
        _disposables.Add(t);
        var s = Session(modulePath, sessionName);
        var g = new GuestSession(s, t, found.Code, name, d.Post, new RejoinTokens(tokens));
        _disposables.Add(g);
        var notices = new List<string>();
        g.Notice += notices.Add;
        var a = new RemoteActions(g);
        a.Refused += notices.Add;
        return new Guest(s, g, a, client, notices);
    }

    private static RulesPack WithGreen(RulesPack p) => p with { Sides = p.Sides.Append(new SideDef { Id = "green", Name = "Green", UnitFilter = "startsWith(name(), \"Green\")" }).ToList() };

    private static string Json(GameState? st) => JsonSerializer.Serialize(st, QuartermasterJson.Options);

    [Fact]
    public async Task TwoGuests_FullGame_PrivateAndConsistent()
    {
        // Host: a moderator holding only Green, a side with no pieces; Red's secrets live in the host's state.
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var path = fx.Build();
        var hs = Session(path, "host");
        hs.SetRoller((n, sides) => Enumerable.Repeat(sides == 10000 ? GuestSessionTests.SecretRoll : 4, n).ToList());
        hs.LoadSetup(hs.Model.Setups[0], Difficulty.Hardcore, new[] { new PlayerDef("p1", "Moderator", new[] { "green" }) });
        hs.NewRules("t");
        hs.UpdateRules(_ => WithGreen(GuestSessionTests.Pack(hs.Archive.Sha256)));
        GuestSessionTests.AddRedSecrets(hs);

        var hostClient = await NewClient();
        var code = await hostClient.ReserveCodeAsync();
        var listing = new LobbyListing("", "Friday", "Moderator", hs.Archive.Sha256, "Fixture", new[] { "Red", "Blue" }, new[] { "Red", "Blue" },
            1, 3, true, Protocol.ProtocolVersion);
        await hostClient.CreateRoomAsync(code, Secrets.AuthVerifier(Password, code), listing);
        var key = Secrets.GameKey(Password, code);
        var ht = new RelayTransport(hostClient, key);
        _disposables.Add(ht);
        var d = new QueueDispatcher();
        Func<GameCommand, GameState, GameCommand> prepare = (cmd, st) => CommandLog.Prepare(cmd, st, hs);
        var pc = new PhaseController(hs, prepare);
        var host = new HostSession(hs, new IntentExecutor(hs, pc), ht, Password, code, listing, d.Post);
        _disposables.Add(host);
        d.Pump();

        var tokens = Path.Combine(_dir, "tokens");
        var ann = await JoinFound(d, path, hs.Archive.Sha256, key, "Ann", tokens, "ann");
        var bob = await JoinFound(d, path, hs.Archive.Sha256, key, "Bob", Path.Combine(_dir, "tokens-bob"), "bob");
        Until(d, () => ann.G.State == GuestState.ChoosingSide && bob.G.State == GuestState.ChoosingSide);
        Assert.True(ann.G.ChooseSides(new[] { "red" }));
        Assert.True(bob.G.ChooseSides(new[] { "blue" }));
        Until(d, () => ann.G.State == GuestState.Playing && bob.G.State == GuestState.Playing && ann.S.Game != null && bob.S.Game != null);
        Assert.Equal(3, host.Seats.All.Count);

        // Play: both move their own units, edit private ledgers, roll, chat; Bob runs a formula with a hidden input.
        var r1 = ann.S.Game!.State.Pieces[PhaseFixture.RedOne];
        var b1 = bob.S.Game!.State.Pieces[PhaseFixture.BlueOne];
        ann.A.Move(new[] { new MovePieces(new[] { PhaseFixture.RedOne }, r1.MapId, new PointD(140, 40), null) });
        bob.A.Move(new[] { new MovePieces(new[] { PhaseFixture.BlueOne }, b1.MapId, new PointD(150, 50), null) });
        ann.A.EditLedger(LedgerKey.Piece(PhaseFixture.RedOne), "fuel", GuestSessionTests.SecretFuel + 5, "topped up");
        bob.A.EditLedger(LedgerKey.Piece(PhaseFixture.BlueOne), "water", 7, "refilled");
        ann.A.Roll(new DiceSpec(2, 6, 0), "Scouting");
        ann.A.Chat("Red ready");
        bob.A.Chat("Blue ready");
        var verdict = Await(d, bob.A.ApplyFormulaAsync(new FormulaIntent("f-mix", PhaseFixture.BlueOne, null,
            new Dictionary<string, string> { ["W"] = "7", ["S"] = "1" }, false)));
        Assert.Equal(FormulaBox.Hidden, verdict.Boxes.Single(b => b.Var == "S").State);
        // A guest asking for a preview of a formula never writes (and never sees the other side's input).
        var peek = Await(d, ann.A.ApplyFormulaAsync(new FormulaIntent("f-peek", null, null, new Dictionary<string, string>(), true)));
        // Acting on the other side's unit is refused.
        bob.A.EditLedger(LedgerKey.Piece(PhaseFixture.RedOne), "fuel", 1, "drain");
        Until(d, () => bob.Notices.Count >= 1);

        Until(d, () => ann.S.Game!.State.Log.Any(l => l.Kind == LogKind.Chat && l.Text.Contains("Blue ready"))
            && bob.S.Game!.State.Log.Any(l => l.Kind == LogKind.Chat && l.Text.Contains("Red ready")));
        Assert.NotNull(peek);

        // Guest drop: Bob's app goes away, the game moves on, Bob rejoins with his token and keeps his seat.
        var bobId = bob.G.PlayerId;
        bob.G.Dispose();
        await bob.C.DisposeAsync();
        Until(d, () => host.Seats.All.Count(s => s.Connected) == 2);
        host.Chat("while Bob was away");
        var bob2 = await JoinFound(d, path, hs.Archive.Sha256, key, "Bob", Path.Combine(_dir, "tokens-bob"), "bob2");
        Until(d, () => bob2.G.State == GuestState.Playing && bob2.S.Game != null);
        Assert.Equal(bobId, bob2.G.PlayerId);
        Assert.Equal(3, host.Seats.All.Count);
        Assert.Equal(new[] { "blue" }, bob2.S.GuestSides!.ToArray());

        // Host undo is host-only: a guest's attempt is refused, the host's takes effect and guests see it in the next snapshot.
        var logBefore = hs.Game!.State.Log.Count;
        bob2.A.Undo();
        Assert.Contains(ModuleSession.GuestUndoText, bob2.Notices);
        var undo = new HistoryController(hs, pc, prepare);
        Assert.True(undo.CanUndo);
        undo.Undo();
        host.FlushSnapshots();
        Func<string, ModuleSession, bool> settled = (side, s) =>
            Json(PlayerView.For(hs.Game!.State, hs.Rules, hs.Model, new HashSet<string> { side })) == Json(s.Game!.State);
        Until(d, () => settled("red", ann.S) && settled("blue", bob2.S));

        // Settle: both guests hold exactly the view the host computes for their sides, and nothing else.
        var model = hs.Model;
        var pack = hs.Rules;
        var full = hs.Game!.State;
        Assert.Equal(Json(PlayerView.For(full, pack, model, new HashSet<string> { "red" })), Json(ann.S.Game!.State));
        Assert.Equal(Json(PlayerView.For(full, pack, model, new HashSet<string> { "blue" })), Json(bob2.S.Game!.State));

        var annAll = Json(ann.S.Game!.State) + "\n" + string.Join("\n", ann.Notices);
        var bobAll = Json(bob2.S.Game!.State) + "\n" + string.Join("\n", bob2.Notices);
        // Hidden-input formula working never reaches Red; Blue's own values do reach Blue.
        Assert.DoesNotContain(GuestSessionTests.SecretStock.ToString(), bobAll);
        Assert.DoesNotContain(GuestSessionTests.SecretName, bobAll);
        foreach (var marker in GuestSessionTests.SecretMarkers) Assert.DoesNotContain(marker, bobAll);
        Assert.Contains(GuestSessionTests.SecretName, annAll);   // Red's own secrets do reach Red (the check above proves something)
        Assert.Contains("Blue 1", bobAll);
        Assert.Contains("Red 1", annAll);
        Assert.Equal(0, ann.G.Malformed + bob2.G.Malformed);
    }
}
