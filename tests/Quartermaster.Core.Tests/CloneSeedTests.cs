using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>The Clone command copies the source counter's stored ledger values (spec #20). Invented Red/Blue data only.</summary>
public class CloneSeedTests
{
    private const string Main = "Main Map";
    private static readonly IReadOnlySet<string> Red = new HashSet<string> { "red" };
    private static readonly IReadOnlySet<string> Blue = new HashSet<string> { "blue" };

    private static RulesPack Pack(string redFilter = "marker(\"Side\") == \"Red\"", string blueFilter = "marker(\"Side\") == \"Blue\"") => new()
    {
        Format = 4,
        Sides = new()
        {
            new SideDef { Id = "red", Name = "Red", UnitFilter = redFilter },
            new SideDef { Id = "blue", Name = "Blue", UnitFilter = blueFilter },
        },
        Properties = new()
        {
            new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, Visibility = Visibility.Private },
            new PropertyDef { Id = "morale", Name = "Morale", Scope = PropertyScope.Pieces, Visibility = Visibility.Public },
            new PropertyDef { Id = "rank", Name = "Rank", Scope = PropertyScope.Pieces, SheetField = "Rank", CloneAs = CloneValue.Default },
            new PropertyDef { Id = "team", Name = "Team", Scope = PropertyScope.Pieces, Default = 1, CloneAs = CloneValue.Default },
            new PropertyDef { Id = "pace", Name = "Pace", Scope = PropertyScope.Both, Visibility = Visibility.Private },
            new PropertyDef { Id = "speed", Name = "Speed", Scope = PropertyScope.Pieces, Expr = "Fuel * 2" },
            new PropertyDef { Id = "supply", Name = "Supply", Scope = PropertyScope.Holders },
        },
    };

    private static string Type(string name) =>
        PieceTypeParser.EncodeState(new[] { "propertysheet;0Rank;Sheet;", "mark;Side", "piece;;;unit.svg;" + name });

    private static string State(string rank, string side) => PieceTypeParser.EncodeState(new[] { rank, side, "" });

    private static GameState NewGame(string side = "Red")
    {
        var s = new GameState { GameId = "g1" };
        s.MapOrder.Add(Main);
        s.Apply(new AddPiece("r1", Type("TEST Rifle"), State("3", side), Main, new PointD(50, 50), null, "TEST Rifle placed"));
        s.Apply(new SeedLedger([new LedgerSeed("p:r1", "rank", 3)]));
        Set(s, "p:r1", "fuel", 4242);
        Set(s, "p:r1", "morale", 7);
        Set(s, "p:r1", "rank", 9);       // promoted since it was placed
        Set(s, "p:r1", "pace", 5);
        Set(s, "p:r1", "speed", 111);    // a stale stored value of a computed property: never copied
        Set(s, "p:r1", "supply", 222);   // a holder property: never copied
        return s;
    }

    private static void Set(GameState s, string key, string prop, double after) =>
        s.Apply(new LedgerDelta(key, prop, 0, after, "manual", null, null, "set", null));

    /// <summary>The clone as the VASSAL Clone action adds it: same type and state, onto the source's stack.</summary>
    private static AddPiece CloneOf(GameState s, string sourceId, string newId)
    {
        var src = s.Pieces[sourceId];
        return new AddPiece(newId, src.TypeString, PieceTypeParser.EncodeState(src.TraitStates), src.MapId, src.Pos, src.StackId, CloneOf: sourceId);
    }

    private static string Json(GameState s) => JsonSerializer.Serialize(s, QuartermasterJson.Options);

    [Fact]
    public void Clone_copies_stored_values()
    {
        var s = NewGame();
        var pack = Pack();
        s.Apply(CloneOf(s, "r1", "c1"));
        var seeds = LedgerSeeder.BuildCloneSeeds(s, pack, RunnerFx.Model, "c1", "r1");
        Assert.NotNull(seeds);
        Assert.Equal("r1", seeds!.CloneOf);
        Assert.Equal(new[] { ("p:c1", "fuel", 4242.0), ("p:c1", "morale", 7.0), ("p:c1", "pace", 5.0) },
            seeds.Seeds.Select(x => (x.Key, x.PropertyId, x.Value)).ToArray());   // pack order
        s.Apply(seeds);
        Assert.Equal(4242, Ledger.Get(s, "p:c1", "fuel"));
        Assert.Equal(7, Ledger.Get(s, "p:c1", "morale"));
        Assert.Equal(5, Ledger.Get(s, "p:c1", "pace"));
        Assert.Equal(4242, Ledger.Get(s, "p:r1", "fuel"));   // the source keeps its values

        // A source with nothing stored, a missing source and a missing clone give nothing.
        s.Apply(new AddPiece("r2", Type("TEST Rifle"), State("3", "Red"), Main, new PointD(60, 60), null));
        s.Apply(CloneOf(s, "r2", "c2"));
        Assert.Null(LedgerSeeder.BuildCloneSeeds(s, pack, RunnerFx.Model, "c2", "r2"));
        Assert.Null(LedgerSeeder.BuildCloneSeeds(s, pack, RunnerFx.Model, "c2", "nope"));
        Assert.Null(LedgerSeeder.BuildCloneSeeds(s, pack, RunnerFx.Model, "nope", "r1"));
    }

    [Fact]
    public void CloneAs_default_seeds_as_new_counter()
    {
        var s = NewGame();
        var pack = Pack();
        s.Apply(CloneOf(s, "r1", "c1"));
        var seeds = LedgerSeeder.BuildCloneSeeds(s, pack, RunnerFx.Model, "c1", "r1")!;
        Assert.DoesNotContain(seeds.Seeds, x => x.PropertyId is "rank" or "team");
        s.Apply(seeds);
        // The normal seeding that follows gives a Default property what a new counter gets: its sheet value, else its Default.
        var normal = LedgerSeeder.BuildSeeds(s, pack, _ => true, RunnerFx.Model);
        s.Apply(normal!);
        Assert.Equal(3, Ledger.Get(s, "p:c1", "rank"));   // the sheet's 3, not the source's 9
        Assert.Null(Ledger.Get(s, "p:c1", "team"));
        Assert.Equal(1, Ledger.GetOrDefault(s, "p:c1", pack.Properties.Single(p => p.Id == "team")));
        Assert.Equal(4242, Ledger.Get(s, "p:c1", "fuel"));   // the copy ran first; seeding never overwrites it
    }

    [Fact]
    public void Computed_and_holder_properties_not_copied()
    {
        var s = NewGame();
        s.Apply(CloneOf(s, "r1", "c1"));
        var seeds = LedgerSeeder.BuildCloneSeeds(s, Pack(), RunnerFx.Model, "c1", "r1")!;
        Assert.DoesNotContain(seeds.Seeds, x => x.PropertyId is "speed" or "supply");
    }

    [Fact]
    public void Clone_changing_side_copies_nothing()
    {
        // The side is read from Team, which a clone does not copy: the source (Team 2) is Blue, its clone (Team 1, the Default) is Red.
        var pack = Pack("Team == 1", "Team == 2");
        var s = NewGame();
        Set(s, "p:r1", "team", 2);
        s.Apply(CloneOf(s, "r1", "c1"));
        var log = new DiagnosticLog();
        Assert.Null(LedgerSeeder.BuildCloneSeeds(s, pack, RunnerFx.Model, "c1", "r1", log));
        var d = Assert.Single(log.Items);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal("rules", d.Category);
        Assert.Equal("Clone of \"TEST Rifle\" belongs to another side, so its values were not copied", d.Message);
        Assert.Null(Ledger.Get(s, "p:c1", "fuel"));   // the check ran on a copy of the state

        // The same side with the copied values: copied, no diagnostic.
        var same = Pack("Team == 1 and Fuel < 0", "Team >= 1");
        var log2 = new DiagnosticLog();
        Assert.NotNull(LedgerSeeder.BuildCloneSeeds(s, same, RunnerFx.Model, "c1", "r1", log2));
        Assert.Empty(log2.Items);
    }

    [Fact]
    public void Clone_side_judged_after_normal_seeding()
    {
        // Team (a Default property) is seeded from the sheet right after the copy: the clone's side is judged with that seed. The source
        // printed Team 1 but now stores 2 (Blue); a clone would read the pack Default 2 before seeding (Blue) but the sheet's 1 after it (Red).
        var pack = Pack("Team == 1", "Team == 2") with
        {
            Properties = new()
            {
                new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, Visibility = Visibility.Private },
                new PropertyDef { Id = "team", Name = "Team", Scope = PropertyScope.Pieces, Default = 2, SheetField = "Team", CloneAs = CloneValue.Default },
            },
        };
        var type = PieceTypeParser.EncodeState(new[] { "propertysheet;0Team;Sheet;", "piece;;;unit.svg;TEST Scout" });
        var s = new GameState { GameId = "g1" };
        s.MapOrder.Add(Main);
        s.Apply(new AddPiece("s1", type, PieceTypeParser.EncodeState(new[] { "1", "" }), Main, new PointD(50, 50), null));
        Set(s, "p:s1", "team", 2);
        Set(s, "p:s1", "fuel", 5151);
        Assert.Equal("blue", Ownership.SideOfPiece(s.Pieces["s1"], s, pack));
        s.Apply(CloneOf(s, "s1", "c1"));

        var log = new DiagnosticLog();
        Assert.Null(LedgerSeeder.BuildCloneSeeds(s, pack, RunnerFx.Model, "c1", "s1", log));
        Assert.Equal("Clone of \"TEST Scout\" belongs to another side, so its values were not copied", Assert.Single(log.Items).Message);
        s.Apply(LedgerSeeder.BuildSeeds(s, pack, _ => true, RunnerFx.Model)!);
        Assert.Equal("red", Ownership.SideOfPiece(s.Pieces["c1"], s, pack));
        Assert.Null(Ledger.Get(s, "p:c1", "fuel"));
        Assert.DoesNotContain("5151", Json(PlayerView.For(s, pack, RunnerFx.Model, Red)));
    }

    [Fact]
    public void Clone_action_sets_CloneOf()
    {
        var s = new GameState();
        var type = PieceTypeParser.EncodeState(new[] { "clone;Clone;C", "piece;;;unit.svg;TEST Rifle" });
        s.Apply(new AddPiece("a", type, "", Main, new PointD(100, 100), null));
        var n = 0;
        var act = PieceActions.For(s.Pieces["a"], RunnerFx.Model, s, () => "new" + ++n).Single(x => x.Label == "Clone");
        var add = Assert.IsType<AddPiece>(Assert.Single(act.Build(s)));
        Assert.Equal("a", add.CloneOf);
        Assert.Equal("new1", add.PieceId);
    }

    [Fact]
    public void AddPiece_and_SeedLedger_without_CloneOf_serialise_unchanged()
    {
        var o = QuartermasterJson.Options;
        // Captured from the serializer before CloneOf existed.
        Assert.Equal("{\"$t\":\"addPiece\",\"pieceId\":\"c1\",\"typeString\":\"t\",\"stateString\":\"s\",\"mapId\":\"m1\",\"pos\":{\"x\":1,\"y\":2},"
            + "\"stackOnto\":\"st1\",\"logText\":\"Clone placed\",\"at\":null,\"who\":null}",
            JsonSerializer.Serialize<GameCommand>(new AddPiece("c1", "t", "s", "m1", new PointD(1, 2), "st1", "Clone placed"), o));
        Assert.Equal("{\"$t\":\"seedLedger\",\"seeds\":[{\"key\":\"p:c1\",\"propertyId\":\"fuel\",\"value\":7}]}",
            JsonSerializer.Serialize<GameCommand>(new SeedLedger([new LedgerSeed("p:c1", "fuel", 7)]), o));

        var add = JsonSerializer.Serialize<GameCommand>(new AddPiece("c1", "t", "s", "m1", new PointD(1, 2), null, CloneOf: "r1"), o);
        Assert.Contains("\"cloneOf\":\"r1\"", add);
        Assert.Equal("r1", Assert.IsType<AddPiece>(JsonSerializer.Deserialize<GameCommand>(add, o)).CloneOf);
        var seed = JsonSerializer.Serialize<GameCommand>(new SeedLedger([new LedgerSeed("p:c1", "fuel", 7)], CloneOf: "r1"), o);
        Assert.Contains("\"cloneOf\":\"r1\"", seed);
        Assert.Equal("r1", Assert.IsType<SeedLedger>(JsonSerializer.Deserialize<GameCommand>(seed, o)).CloneOf);

        // A clone seed is its own undoable step (it must replay on redo); a plain seed folds into the action before it.
        Assert.True(GameSession.IsFolding(new SeedLedger([])));
        Assert.False(GameSession.IsFolding(new SeedLedger([], CloneOf: "r1")));
    }

    [Fact]
    public void Clone_values_hidden_from_other_side()
    {
        // A Blue player (hot-seat) clones a Red counter it can see: the clone stays Red, so its private values stay Red's.
        var pack = Pack();
        var s = NewGame();
        s.Apply(new AddPiece("b1", Type("TEST Lancer"), State("1", "Blue"), Main, new PointD(90, 90), null));
        Set(s, "p:b1", "fuel", 3131);
        Assert.DoesNotContain("4242", Json(PlayerView.For(s, pack, RunnerFx.Model, Blue)));   // the source is hidden from Blue

        s.Apply(CloneOf(s, "r1", "c1") with { LogText = "TEST Rifle cloned", Who = "blue player" });
        s.Apply(LedgerSeeder.BuildCloneSeeds(s, pack, RunnerFx.Model, "c1", "r1")!);
        Assert.Equal("red", Ownership.SideOfPiece(s.Pieces["c1"], s, pack));

        var blue = PlayerView.For(s, pack, RunnerFx.Model, Blue);
        var json = Json(blue);
        Assert.DoesNotContain("4242", json);   // not in Ledgers, LedgerHistory or the log
        Assert.DoesNotContain("\"p:c1\":{\"fuel\"", json);
        Assert.Null(Ledger.Get(blue, "p:c1", "fuel"));
        Assert.Null(Ledger.Get(blue, "p:c1", "pace"));
        Assert.DoesNotContain(blue.LedgerHistory, e => e.Key == "p:c1" && e.PropertyId is "fuel" or "pace");
        Assert.Equal(7, Ledger.Get(blue, "p:c1", "morale"));   // Public: Blue saw it on the source too
        Assert.Equal(3131, Ledger.Get(blue, "p:b1", "fuel"));

        var red = PlayerView.For(s, pack, RunnerFx.Model, Red);
        Assert.Equal(4242, Ledger.Get(red, "p:c1", "fuel"));
        Assert.Equal(5, Ledger.Get(red, "p:c1", "pace"));
        Assert.DoesNotContain("3131", Json(red));
    }
}
