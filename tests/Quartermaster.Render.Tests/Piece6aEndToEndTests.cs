using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>Piece 6a end to end: two players, private data, handover, filtered view, step back, save and load. Invented Red/Blue only.</summary>
public class Piece6aEndToEndTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-e2e6a-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    private const string SecretName = "Red Phantom Battery";
    private const string RedMap = "Red Command Post";
    private const double SecretStock = 424242;
    private const int SecretRoll = 8461;
    private const double SecretFuel = 71717;

    private static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });
    private static readonly PlayerDef Bob = new("p2", "Bob", new[] { "blue" });

    public Piece6aEndToEndTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static RulesPack Pack(string sha)
    {
        var p = PhaseFixture.Pack(sha);
        return p with
        {
            Format = 4,
            Properties = p.Properties.Concat(new[]
            {
                new PropertyDef { Id = "supply", Name = "Supply", Scope = PropertyScope.Holders, Visibility = Visibility.Private },
            }).Select(x => x.Id == "fuel" ? x with { Visibility = Visibility.Private } : x).ToList(),
            Holders = new() { new HolderDef { Id = "reddepot", Name = "Red Depot", SideId = "red" } },
            MapOwners = new() { new MapOwner { MapId = RedMap, SideId = "red" } },
            Formulas = p.Formulas.Concat(new[]
            {
                new FormulaDef
                {
                    Id = "f-secret", Name = "Secret Attack", Text = "R", PrivateRolls = true,
                    Bindings = new() { new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 10000 } },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Op = OutputOp.Set } },
                },
            }).ToList(),
            Phases = p.Phases.Select(x => x.Id == "ph-combat" ? x with { SideId = "blue" } : x).ToList(),
        };
    }

    private (ModuleSession S, PhaseController C, HistoryController H, HotSeat HS) Open(string name)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache-" + name), Path.Combine(_tmp, "root-" + name));
        _sessions.Add(s);
        s.SetRoller((n, sides) => Enumerable.Repeat(sides == 10000 ? SecretRoll : 3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto);
        s.NewRules("t");
        s.UpdateRules(_ => Pack(s.Archive.Sha256));
        Func<GameCommand, GameState, GameCommand> prepare = (c, _) => c;
        var phases = new PhaseController(s, prepare);
        var history = new HistoryController(s, phases, prepare);
        var hs = new HotSeat(s, phases);
        hs.Watch(history);
        return (s, phases, history, hs);
    }

    private static string Json(GameState? g) => JsonSerializer.Serialize(g, QuartermasterJson.Options);

    [Fact]
    public void TwoPlayers_HandoverFilterStepBackSaveLoad()
    {
        var (s, c, h, hs) = Open("a");
        var raised = new List<string>();
        hs.HandoverRequired += p => raised.Add(p.Id);

        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto, new[] { Alice, Bob });
        var game = s.Game!;
        var gameId = game.State.GameId;
        Assert.False(string.IsNullOrEmpty(gameId));
        Assert.Equal(new[] { "p1" }, raised);
        Assert.Empty(s.ViewSides!);

        // Red's secrets: a unit on a Red-owned map, a private fuel value, a private depot stock, a private roll.
        var type = PieceTypeParser.EncodeState(new[] { "piece;;;unit.svg;" + SecretName });
        var state = PieceTypeParser.EncodeState(new[] { "" });
        game.Execute(new AddPiece("secret1", type, state, RedMap, new PointD(10, 10), null, SecretName + " placed", Who: "Alice"));

        c.Start(); c.Advance();                         // Supply (red)
        hs.TakeOver(Alice);
        game.Execute(new LedgerDelta(LedgerKey.Piece(PhaseFixture.RedOne), "fuel", 40, SecretFuel, "manual", null, null, null, null, null, "Alice"));
        game.Execute(new LedgerDelta(LedgerKey.Holder("reddepot"), "supply", 0, SecretStock, "manual", null, null, null, null, null, "Alice"));
        var runner = new FormulaRunner(s.Rules!, s.Model, (n, _) => Enumerable.Repeat(SecretRoll, n).ToList());
        var trace = runner.Run(game.State, new RunRequest("f-secret", PhaseFixture.RedOne, new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<int>>()));
        Assert.Null(trace.Error);
        foreach (var cmd in runner.Commands(trace, null, "manual", null, "red")) game.Execute(cmd);

        // Alice sees her own data.
        hs.TakeOver(Alice);
        var aliceJson = Json(s.View);
        Assert.Contains(SecretName, aliceJson);
        Assert.Contains(SecretRoll.ToString(), aliceJson);

        // Phase change to Blue's Combat: handover to Bob, public-only until he continues.
        c.Advance(); c.Advance();
        Assert.Equal("p2", raised.Last());
        Assert.Equal("p2", hs.HandoverDue()?.Id);
        Assert.Empty(s.ViewSides!);
        AssertNoLeak(Json(s.View));

        hs.TakeOver(Bob);
        Assert.Equal(new[] { "blue" }, s.ViewSides!.ToArray());
        AssertNoLeak(Json(s.View));
        Assert.Contains(s.View!.Log, l => l.Text.Contains("Red rolled dice") || l.Text.Contains("Red ledger changed"));

        // Step back into Red's phase: Alice is due again.
        h.Undo();
        Assert.Equal("p1", hs.HandoverDue()?.Id);
        Assert.Equal("p1", raised.Last());
        Assert.Empty(s.ViewSides!);
        AssertNoLeak(Json(s.View));

        // Save and load keep players and game id; the view is public-only until someone takes over.
        var path = Path.Combine(_tmp, "g.qmsave");
        s.SaveNative(path);
        var (s2, _, _, hs2) = Open("b");
        s2.LoadNative(path);
        Assert.Equal(gameId, s2.Game!.State.GameId);
        Assert.Equal(new[] { "p1", "p2" }, s2.Game.State.Players.Select(p => p.Id).ToArray());
        Assert.Equal(new[] { "red" }, s2.Game.State.Players[0].Sides.ToArray());
        Assert.True(hs2.Active);
        Assert.Empty(s2.ViewSides!);
        AssertNoLeak(Json(s2.View));
    }

    private static void AssertNoLeak(string json)
    {
        Assert.DoesNotContain(SecretName, json);
        Assert.DoesNotContain(SecretRoll.ToString(), json);
        Assert.DoesNotContain(SecretStock.ToString(), json);
        Assert.DoesNotContain(SecretFuel.ToString(), json);
        Assert.DoesNotContain(RedMap, json);
    }
}
