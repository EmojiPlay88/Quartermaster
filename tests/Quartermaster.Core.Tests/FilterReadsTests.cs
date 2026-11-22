using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>side(), phaseside(), map(), holder() and bare global names in filters, formulas and phase conditions. Invented data.</summary>
public class FilterReadsTests
{
    private static PieceInstance Marked(string id = "m1", string side = "Red", string map = "Main Map")
    {
        var type = SequenceEncoder.Encode(new[] { "mark;" + SequenceEncoder.Encode(new[] { "Side" }, ','), "piece;;;unit.svg;Rifle" }, '\t');
        return new PieceInstance { Id = id, TypeString = type, MapId = map, TraitStates = new() { side, "" } };
    }

    /// <summary>Sides North (Red marker) and South (Blue marker); holder Rail owned by North; global Weather; piece Grit; Both Morale.</summary>
    private static RulesPack Pack(string? northFilter = "marker(\"Side\") == \"Red\"", params PropertyDef[] extra)
    {
        var pack = new RulesPack
        {
            Properties = new()
            {
                new PropertyDef { Id = "weather", Name = "Weather", Scope = PropertyScope.Holders, Default = 1, Visibility = Visibility.Public },
                new PropertyDef { Id = "tonnage", Name = "Tonnage", Scope = PropertyScope.Holders, Default = 4, Visibility = Visibility.Private },
                new PropertyDef { Id = "grit", Name = "Grit", Scope = PropertyScope.Pieces, Default = 5, Visibility = Visibility.Public },
                new PropertyDef { Id = "morale", Name = "Morale", Scope = PropertyScope.Both, Default = 3, Visibility = Visibility.Public },
            },
            Holders = new() { new HolderDef { Id = "rail", Name = "Rail", SideId = "north" } },
            Sides = new()
            {
                new SideDef { Id = "north", Name = "North", UnitFilter = northFilter },
                new SideDef { Id = "south", Name = "South", UnitFilter = "marker(\"Side\") == \"Blue\"" },
            },
            Phases = new()
            {
                new PhaseDef { Id = "P1", Name = "North Move", SideId = "north" },
                new PhaseDef { Id = "P2", Name = "South Move", SideId = "south" },
                new PhaseDef { Id = "P3", Name = "Weather" },
            },
        };
        pack.Properties.AddRange(extra);
        return pack;
    }

    private static GameState State(params PieceInstance[] pieces)
    {
        var s = new GameState();
        foreach (var p in pieces) s.Pieces[p.Id] = p;
        return s;
    }

    private static bool M(string expr, PieceInstance p, GameState s, RulesPack pack) => PieceFilter.Matches(expr, p, s, RunnerFx.Model, pack);

    private static void At(GameState s, RulesPack pack, string phaseId) =>
        s.Position = new PhasePosition(1, 1, phaseId, pack.Phases.FindIndex(x => x.Id == phaseId));

    [Fact]
    public void Filter_reads_side_and_map()
    {
        var pack = Pack();
        var red = Marked("r", "Red");
        var blue = Marked("b", "Blue", "Side Map");
        var grey = Marked("g", "Grey");
        var s = State(red, blue, grey);
        Assert.True(M("side() == \"North\"", red, s, pack));
        Assert.True(M("side() == \"South\"", blue, s, pack));
        Assert.True(M("side() == \"\"", grey, s, pack));
        Assert.True(M("map() == \"Main Map\"", red, s, pack));
        Assert.True(M("map() == \"Side Map\"", blue, s, pack));
        Assert.False(M("map() == \"Main Map\"", blue, s, pack));
    }

    [Fact]
    public void Filter_reads_phaseside()
    {
        var pack = Pack();
        var p = Marked();
        var s = State(p);
        Assert.True(M("phaseside() == \"\"", p, s, pack));
        At(s, pack, "P1");
        Assert.True(M("phaseside() == \"North\"", p, s, pack));
        Assert.True(M("side() == phaseside()", p, s, pack));
        At(s, pack, "P3");
        Assert.True(M("phaseside() == \"\"", p, s, pack));
        var bare = new RulesPack();
        Assert.True(M("phaseside() == \"\"", p, State(p), bare));
    }

    [Fact]
    public void Filter_reads_global_by_name()
    {
        var pack = Pack();
        var p = Marked();
        var s = State(p);
        Assert.True(M("Weather == 1", p, s, pack));
        Assert.False(M("Weather == 2", p, s, pack));
        s.Ledgers[LedgerKey.Holder(HolderDef.GlobalId)] = new() { ["weather"] = 2 };
        Assert.True(M("Weather == 2", p, s, pack));
    }

    [Fact]
    public void Both_scope_reads_piece_not_global()
    {
        var pack = Pack();
        var p = Marked();
        var s = State(p);
        s.Ledgers[LedgerKey.Holder(HolderDef.GlobalId)] = new() { ["morale"] = 9 };
        Assert.True(M("Morale == 3", p, s, pack));
        s.Ledgers[LedgerKey.Piece("m1")] = new() { ["morale"] = 7 };
        Assert.True(M("Morale == 7", p, s, pack));
        Assert.True(M("Grit == 5", p, s, pack));
    }

    [Fact]
    public void Filter_reads_holder_value()
    {
        var pack = Pack();
        var p = Marked();
        var s = State(p);
        Assert.True(M("holder(Rail, Tonnage) == 4", p, s, pack));
        s.Ledgers[LedgerKey.Holder("rail")] = new() { ["tonnage"] = 11 };
        Assert.True(M("holder(Rail, Tonnage) == 11", p, s, pack));
        Assert.False(M("holder(Depot, Tonnage) == 4", p, s, pack));
        Assert.False(M("holder(Depot, Tonnage) != 4", p, s, pack));
        Assert.False(M("holder(Rail, Nothing) == 0", p, s, pack));
    }

    [Fact]
    public void Side_in_unit_filter_does_not_recurse()
    {
        var pack = Pack("side() == \"North\"");
        var p = Marked();
        var s = State(p);
        Assert.Null(Ownership.SideOfPiece(p, s, pack));
        Assert.Equal("south", Ownership.SideOfPiece(Marked("b", "Blue"), s, pack));
        Assert.True(M("side() == \"\"", p, s, pack));
    }

    [Fact]
    public void Side_via_applies_to_does_not_recurse()
    {
        var loyal = new PropertyDef { Id = "loyal", Name = "Loyal", Scope = PropertyScope.Pieces, Default = 1, AppliesTo = "side() == \"North\"" };
        var pack = Pack("Loyal == 1", loyal);
        var p = Marked();
        var s = State(p);
        Assert.Null(Ownership.SideOfPiece(p, s, pack));
        Assert.True(M("side() == \"\"", p, s, pack));
        Assert.False(M("Loyal == 1", p, s, pack));
    }

    [Fact]
    public void Formula_reads_side_and_holder()
    {
        var pack = Pack();
        pack.Formulas.Add(new FormulaDef { Id = "f1", Name = "Haul", Text = "if(side() == phaseside(), 1, 0) + holder(Rail, Tonnage)" });
        pack.Formulas.Add(new FormulaDef { Id = "f2", Name = "Where", Text = "if(map() == \"Main Map\", 10, 20)" });
        var p = Marked();
        var s = State(p);
        s.Ledgers[LedgerKey.Holder("rail")] = new() { ["tonnage"] = 6 };
        At(s, pack, "P1");
        var runner = RunnerFx.Runner(pack);
        var t = runner.Run(s, RunnerFx.Req("f1", "m1"));
        Assert.Null(t.Error);
        Assert.Equal(7, t.Result!.Value.Number);
        At(s, pack, "P2");
        Assert.Equal(6, runner.Run(s, RunnerFx.Req("f1", "m1")).Result!.Value.Number);
        Assert.Equal(10, runner.Run(s, RunnerFx.Req("f2", "m1")).Result!.Value.Number);
        Assert.Equal("This formula needs a unit", runner.Run(s, RunnerFx.Req("f2", null)).Error);
        Assert.Equal("This formula needs a unit", runner.Run(s, RunnerFx.Req("f1", null)).Error);
    }

    [Fact]
    public void Hidden_private_holder_in_player_view_is_not_defaulted()
    {
        var pack = Pack();
        var p = Marked();
        var s = State(p);
        Assert.True(M("holder(Rail, Tonnage) == 4", p, s, pack));
        s.IsPlayerView = true;
        Assert.False(M("holder(Rail, Tonnage) == 4", p, s, pack));
        Assert.False(M("holder(Rail, Tonnage) != 4", p, s, pack));
        s.Ledgers[LedgerKey.Holder("rail")] = new() { ["tonnage"] = 8 };
        Assert.True(M("holder(Rail, Tonnage) == 8", p, s, pack));
    }

    [Fact]
    public void Other_sides_stripped_holder_is_not_defaulted_in_player_view()
    {
        var pub = new PropertyDef { Id = "pub", Name = "Pub", Scope = PropertyScope.Holders, Default = 2, Visibility = Visibility.Public };
        var pack = Pack(extra: pub);
        pack.Holders.Add(new HolderDef { Id = "camp", Name = "Camp", SideId = "south" });
        var p = Marked("b", "Blue");
        var s = State(p);
        s.Ledgers[LedgerKey.Holder("rail")] = new() { ["pub"] = 9 };
        s.Ledgers[LedgerKey.Holder("camp")] = new() { ["tonnage"] = 5 };
        var south = PlayerView.For(s, pack, RunnerFx.Model, new HashSet<string> { "south" });
        var vp = south.Pieces["b"];
        Assert.False(M("holder(Rail, Pub) == 2", vp, south, pack));
        Assert.False(M("holder(Rail, Pub) != 2", vp, south, pack));
        Assert.True(M("holder(Camp, Tonnage) == 5", vp, south, pack));
        Assert.True(M("holder(Camp, Pub) == 2", vp, south, pack));
        var north = PlayerView.For(s, pack, RunnerFx.Model, new HashSet<string> { "north" });
        Assert.True(M("holder(Rail, Pub) == 9", vp, north, pack));
    }

    [Fact]
    public void Holder_rejects_piece_property()
    {
        var pack = Pack();
        var p = Marked();
        var s = State(p);
        Assert.False(M("holder(Rail, Grit) == 5", p, s, pack));
        Assert.False(M("holder(Rail, Grit) != 5", p, s, pack));
        Assert.True(M("holder(Rail, Morale) == 3", p, s, pack));
        pack.Formulas.Add(new FormulaDef { Id = "f1", Name = "Bad", Text = "holder(Rail, Grit)" });
        Assert.Equal("Grit is not a holder property", RunnerFx.Runner(pack).Run(s, RunnerFx.Req("f1", "m1")).Error);
    }

    [Fact]
    public void Condition_phaseside_is_candidate_phase()
    {
        var pack = Pack();
        pack.Phases[1] = pack.Phases[1] with { Condition = "phaseside() == \"South\"" };
        pack.Phases[2] = pack.Phases[2] with { Condition = "phaseside() == \"South\"" };
        var s = new GameState();
        Assert.True(PhaseConditions.Runs(pack, s, pack.Phases[1], new PhasePosition(1, 1, "P2", 1), out var err));
        Assert.Null(err);
        var step = PhaseConditions.Next(pack, s, new PhasePosition(1, 1, "P1", 0))!;
        Assert.Equal("P2", step.To.PhaseId);
        var after = PhaseConditions.Next(pack, s, new PhasePosition(1, 1, "P2", 1))!;
        Assert.Equal("P1", after.To.PhaseId);
        Assert.Single(after.Skipped);
    }
}
