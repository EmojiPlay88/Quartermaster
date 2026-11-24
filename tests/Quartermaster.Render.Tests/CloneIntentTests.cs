using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>
/// The Clone command copies the source's stored ledger values, locally in one undoable action and for a guest only from a piece it may act on
/// (spec #20). Invented Red/Blue data only.
/// </summary>
public class CloneIntentTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-clone-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    private const string RedMap = "Red Command Post";
    private static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });
    private static readonly PlayerDef Bob = new("p2", "Bob", new[] { "blue" });
    private static readonly IReadOnlySet<string> Red = new HashSet<string> { "red" };
    private static readonly IReadOnlySet<string> Blue = new HashSet<string> { "blue" };

    public CloneIntentTests() => Directory.CreateDirectory(_tmp);

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
            MapOwners = new() { new MapOwner { MapId = RedMap, SideId = "red" } },
            Sides = new()
            {
                new SideDef { Id = "red", Name = "Red", UnitFilter = "startsWith(name(), \"Red\") or marker(\"Side\") == \"Red\"" },
                new SideDef { Id = "blue", Name = "Blue", UnitFilter = "startsWith(name(), \"Blue\") or marker(\"Side\") == \"Blue\"" },
            },
        };
    }

    private (ModuleSession S, PhaseController C, HistoryController H, LocalActions Port, IntentExecutor X) Open()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(4, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto, new[] { Alice, Bob });
        s.NewRules("t");
        s.UpdateRules(_ => Pack(s.Archive.Sha256));
        Func<GameCommand, GameState, GameCommand> prepare = (cmd, st) => CommandLog.Prepare(cmd, st, s);
        var c = new PhaseController(s, prepare);
        var h = new HistoryController(s, c, prepare);
        return (s, c, h, new LocalActions(s, c, h, prepare), new IntentExecutor(s, c));
    }

    private static string FlagType(string name = "TEST Flag") =>
        PieceTypeParser.EncodeState(new[] { "clone;Clone;C", "mark;Side", "piece;;;unit.svg;" + name });

    private static string FlagState(string side) => PieceTypeParser.EncodeState(new[] { "", side, "" });

    private static void AddFlag(ModuleSession s, string id, string side, double fuel, double water, string map = "Main Map", string name = "TEST Flag")
    {
        var g = s.Game!;
        g.Execute(new AddPiece(id, FlagType(name), FlagState(side), map, new PointD(150, 50), null));
        g.Execute(new LedgerDelta(LedgerKey.Piece(id), "fuel", 0, fuel, "manual", null, null, "set", null));
        g.Execute(new LedgerDelta(LedgerKey.Piece(id), "water", 10, water, "manual", null, null, "set", null));
    }

    /// <summary>The commands of the piece's Clone menu item (the new piece gets <paramref name="newId"/>).</summary>
    private static IReadOnlyList<GameCommand> CloneCommands(ModuleSession s, string sourceId, string newId) =>
        PieceActions.For(s.Game!.State.Pieces[sourceId], s.Model, s.Game.State, () => newId).Single(a => a.Label == "Clone").Build(s.Game.State);

    private static string Json(GameState? st) => JsonSerializer.Serialize(st, QuartermasterJson.Options);

    private static double? Value(ModuleSession s, string id, string prop) => Ledger.Get(s.Game!.State, LedgerKey.Piece(id), prop);

    [Fact]
    public void Local_clone_copies_in_one_action()
    {
        var (s, _, h, port, _) = Open();
        var g = s.Game!;
        AddFlag(s, "rf", "Red", 17, 3);
        var commands = g.Log.Count;
        port.PieceCommands(CloneCommands(s, "rf", "c1"));

        Assert.Equal(17, Value(s, "c1", "fuel"));
        Assert.Equal(3, Value(s, "c1", "water"));
        var seed = Assert.IsType<SeedLedger>(g.Log[commands + 1]);
        Assert.Equal("rf", seed.CloneOf);
        Assert.Equal("rf", Assert.IsType<AddPiece>(g.Log[commands]).CloneOf);

        // One undo takes the clone and its values; the source keeps its own.
        h.Undo();
        Assert.False(g.State.Pieces.ContainsKey("c1"));
        Assert.Null(Value(s, "c1", "fuel"));
        Assert.Equal(17, Value(s, "rf", "fuel"));
        Assert.Equal(3, Value(s, "rf", "water"));

        // The single-command path copies too.
        port.PieceCommand(CloneCommands(s, "rf", "c2").Single());
        Assert.Equal(17, Value(s, "c2", "fuel"));
        h.Undo();
        Assert.False(g.State.Pieces.ContainsKey("c2"));
        Assert.Equal(17, Value(s, "rf", "fuel"));
    }

    [Fact]
    public void Undo_redo_of_clone_keeps_copied_values()
    {
        var (s, _, h, port, _) = Open();
        var g = s.Game!;
        AddFlag(s, "rf", "Red", 17, 3);
        port.PieceCommands(CloneCommands(s, "rf", "c1"));
        g.Execute(new LedgerDelta(LedgerKey.Piece("rf"), "fuel", 17, 99, "manual", null, null, "refuelled", null));   // the source changes after the clone

        h.Undo();
        h.Undo();
        Assert.False(g.State.Pieces.ContainsKey("c1"));
        Assert.Equal(17, Value(s, "rf", "fuel"));
        h.Redo();
        Assert.Equal(17, Value(s, "c1", "fuel"));
        Assert.Equal(3, Value(s, "c1", "water"));
        h.Redo();
        Assert.Equal(99, Value(s, "rf", "fuel"));
        Assert.Equal(17, Value(s, "c1", "fuel"));
        Assert.Equal(3, Value(s, "c1", "water"));
    }

    [Fact]
    public void Guest_clone_of_unheld_piece_refused()
    {
        var (s, _, _, _, x) = Open();
        var g = s.Game!;
        AddFlag(s, "rf", "Red", 61717, 3);                               // Red's, on the shared map: Blue sees it, may not act on it
        AddFlag(s, "rh", "Red", 62727, 4, RedMap);                       // Red's, on Red's own map: Blue cannot see it
        AddFlag(s, "bf", "Blue", 23, 5);
        var before = Json(g.State);
        var commands = g.Log.Count;

        // A Blue counter of the same type claiming to be a copy of Red's counter: it would carry Red's private fuel.
        var steal = new AddPiece("c1", FlagType(), FlagState("Blue"), "Main Map", new PointD(160, 50), null, CloneOf: "rf");
        var r = x.Execute("p2", Blue, new PieceCommandIntent(steal));
        Assert.False(r.Ok);
        Assert.Equal("That piece " + ModuleSession.OtherSideText + ".", r.Message);

        var hidden = x.Execute("p2", Blue, new PieceCommandIntent(steal with { CloneOf = "rh" }));
        Assert.False(hidden.Ok);
        Assert.Equal(IntentExecutor.GoneText, hidden.Message);   // as if the hidden piece did not exist

        // Its own counter, but a different type: refused.
        var other = new AddPiece("c1", FlagType("TEST Banner"), FlagState("Blue"), "Main Map", new PointD(160, 50), null, CloneOf: "bf");
        var t = x.Execute("p2", Blue, new PieceCommandIntent(other));
        Assert.False(t.Ok);
        Assert.Equal("That piece can't be placed.", t.Message);

        Assert.Equal(commands, g.Log.Count);   // no piece, no seeds
        Assert.Equal(before, Json(g.State));
        Assert.DoesNotContain("61717", Json(PlayerView.For(g.State, s.Rules, s.Model, Blue)));

        // Without CloneOf the same counter is an ordinary new Blue piece, seeded as new, with none of Red's values.
        Assert.True(x.Execute("p2", Blue, new PieceCommandIntent(steal with { CloneOf = null })).Ok);
        Assert.Null(Value(s, "c1", "fuel"));
        Assert.DoesNotContain("61717", Json(PlayerView.For(g.State, s.Rules, s.Model, Blue)));
    }

    [Fact]
    public void Guest_clone_of_own_piece_copies()
    {
        var (s, _, h, _, x) = Open();
        var g = s.Game!;
        AddFlag(s, "bf", "Blue", 52525, 5);
        var commands = g.Log.Count;

        var add = Assert.IsType<AddPiece>(Assert.Single(CloneCommands(s, "bf", "c1")));
        var r = x.Execute("p2", Blue, new PieceCommandIntent(add));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(52525, Value(s, "c1", "fuel"));
        Assert.Equal(5, Value(s, "c1", "water"));
        Assert.Equal("bf", Assert.IsType<SeedLedger>(g.Log[commands + 1]).CloneOf);

        Assert.Equal(52525, Ledger.Get(PlayerView.For(g.State, s.Rules, s.Model, Blue), LedgerKey.Piece("c1"), "fuel"));
        Assert.DoesNotContain("52525", Json(PlayerView.For(g.State, s.Rules, s.Model, Red)));

        h.Undo();   // the host undoes the guest's clone as one action
        Assert.False(g.State.Pieces.ContainsKey("c1"));
        Assert.Equal(52525, Value(s, "bf", "fuel"));
    }
}
