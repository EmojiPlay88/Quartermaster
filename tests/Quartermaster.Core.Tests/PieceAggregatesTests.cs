using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>Piece sums (sum, countof, maxof, minof and the hex* ones) in every evaluation context. Invented Riverland-style data.</summary>
public class PieceAggregatesTests
{
    private const string GridPrefix = "VASSAL.build.module.map.boardPicker.board.";

    /// <summary>Two maps with the same square grid (so the same location names): "Main Map" and "Side Map".</summary>
    private static readonly Lazy<ModuleModel> LazyTwoMaps = new(() =>
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("y0=\"25\"/>",
            $"y0=\"25\"><{GridPrefix}mapgrid.SquareGridNumbering first=\"H\" hType=\"N\" vType=\"N\" sep=\"\" hLeading=\"1\" vLeading=\"1\" hOff=\"1\" vOff=\"1\"/></{GridPrefix}SquareGrid>");
        const string end = "</VASSAL.build.module.Map>";
        var from = xml.IndexOf("<VASSAL.build.module.Map ", StringComparison.Ordinal);
        var to = xml.IndexOf(end, StringComparison.Ordinal) + end.Length;
        var second = xml[from..to].Replace("mapName=\"Main Map\"", "mapName=\"Side Map\"");
        xml = xml.Insert(to, second);
        using var fx = new FixtureModule().WithBuildFile(xml).AddImage("board.png", 200, 100).AddSvg("unit.svg");
        using var arch = VmodArchive.Open(fx.Build());
        return ModuleModelBuilder.Build(arch, new DiagnosticLog());
    });

    private static ModuleModel Model => LazyTwoMaps.Value;

    private static RulesPack Pack(params FormulaDef[] formulas) => new()
    {
        Properties = new()
        {
            new PropertyDef { Id = "str", Name = "Strength", Scope = PropertyScope.Pieces, Default = 2 },
            new PropertyDef { Id = "trucks", Name = "Trucks", Scope = PropertyScope.Pieces, Default = 0 },
            new PropertyDef { Id = "flag", Name = "Flag", Scope = PropertyScope.Holders, Default = 0 },
        },
        Phases = new() { new PhaseDef { Id = "P", Name = "Movement" } },
        Formulas = formulas.ToList(),
    };

    private static FormulaDef Formula(string text) => new() { Id = "f", Name = "Probe", Text = text };

    private static void Add(GameState s, string id, string name, double? str, string map = "Main Map", double x = 60, double y = 40)
    {
        s.Pieces[id] = new PieceInstance
        {
            Id = id, MapId = map, Pos = new PointD(x, y), TraitStates = new() { "" },
            TypeString = SequenceEncoder.Encode(new[] { "piece;;;unit.svg;" + name }, '\t'),
        };
        if (str != null) s.Ledgers[LedgerKey.Piece(id)] = new() { ["str"] = str.Value };
    }

    /// <summary>Riverland: two Red units stacked at (60,40), one Red unit in the next square, one Blue unit at the same spot on the side map.</summary>
    private static GameState Riverland()
    {
        var s = new GameState();
        Add(s, "r1", "Red Rifles", 3);
        Add(s, "r2", "Red Guns", 4, x: 62, y: 42);
        Add(s, "r3", "Red Scouts", 5, x: 160, y: 40);
        Add(s, "b1", "Blue Rifles", 10, map: "Side Map");
        return s;
    }

    private static Value Eval(string expr, GameState s, RulesPack pack, PieceInstance? self = null)
    {
        var call = Assert.IsType<Call>(Parser.Parse(expr));
        var (property, filter) = ExprFacts.AggregateArgs(call);
        return PieceAggregates.Evaluate(call.Function, property, filter, s, pack, Model, self, t => PieceFilter.Compile(t, s, pack, Model));
    }

    private static FormulaTrace Run(GameState s, string text, string? piece = "r1")
    {
        var pack = Pack(Formula(text));
        return new FormulaRunner(pack, Model, (n, _) => Enumerable.Repeat(1, n).ToList())
            .Run(s, new RunRequest("f", piece, new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>()));
    }

    [Fact]
    public void Sum_over_filter()
    {
        var s = Riverland();
        var pack = Pack();
        Assert.Equal(12, Eval("sum(Strength, \"startsWith(name(), \\\"Red\\\")\")", s, pack).Number);
        Assert.True(PieceFilter.EvaluateGameLevel("sum(Strength, \"startsWith(name(), \\\"Red\\\")\") == 12", s, pack));
        Assert.Equal(0, Eval("sum(Strength, \"name() == \\\"Nobody\\\"\")", s, pack).Number);
    }

    [Fact]
    public void Sum_uses_defaults()
    {
        var s = Riverland();
        Add(s, "r4", "Red Militia", null);   // no ledger row: Strength reads its Default 2
        var pack = Pack();
        Assert.Equal(14, Eval("sum(Strength, \"startsWith(name(), \\\"Red\\\")\")", s, pack).Number);
        Assert.Equal(2, Eval("minof(Strength, \"\")", s, pack).Number);
        Assert.Equal(10, Eval("maxof(Strength, \"\")", s, pack).Number);
        Assert.Equal(0, Eval("sum(Trucks, \"\")", s, pack).Number);
    }

    [Fact]
    public void Countof_empty_filter_counts_all()
    {
        var s = Riverland();
        var pack = Pack();
        Assert.Equal(4, Eval("countof(\"\")", s, pack).Number);
        Assert.Equal(2, Eval("countof(\"Strength > 4\")", s, pack).Number);   // r3 (5) and b1 (10)
        Assert.Equal(0, Eval("countof(\"name() == \\\"Nobody\\\"\")", s, pack).Number);
    }

    [Fact]
    public void Maxof_no_match_error()
    {
        var s = Riverland();
        var t = Run(s, "maxof(Strength, \"name() == \\\"Nobody\\\"\")");
        Assert.Equal("No piece matches \"name() == \"Nobody\"\"", t.Error);
        var min = Run(s, "minof(Strength, \"Strength > 99\")");
        Assert.Equal("No piece matches \"Strength > 99\"", min.Error);
        var hex = Run(s, "hexmax(Strength, \"Strength > 99\")");
        Assert.Equal("No piece matches \"Strength > 99\"", hex.Error);
    }

    [Fact]
    public void Hexsum_same_map_same_hex_only()
    {
        var s = Riverland();
        var pack = Pack();
        var self = s.Pieces["r1"];
        Assert.Equal(PieceInfo.Location(self, Model), PieceInfo.Location(s.Pieces["b1"], Model));   // same location name, other map
        Assert.NotEqual(PieceInfo.Location(self, Model), PieceInfo.Location(s.Pieces["r3"], Model));
        Assert.Equal(7, Eval("hexsum(Strength)", s, pack, self).Number);   // r1 3 + r2 4; not r3 (next square), not b1 (side map)
        Assert.Equal(4, Eval("hexsum(Strength, \"name() != \\\"Red Rifles\\\"\")", s, pack, self).Number);
        Assert.Equal(4, Eval("hexmax(Strength)", s, pack, self).Number);
        Assert.Equal(3, Eval("hexmin(Strength)", s, pack, self).Number);
        Assert.Equal(10, Eval("hexsum(Strength)", s, pack, s.Pieces["b1"]).Number);
        Assert.Equal(7, Run(s, "hexsum(Strength)").Result!.Value.Number);
    }

    [Fact]
    public void Hexcount_includes_self()
    {
        var s = Riverland();
        Assert.Equal(2, Run(s, "hexcount()").Result!.Value.Number);
        Assert.Equal(1, Run(s, "hexcount()", "r3").Result!.Value.Number);
        Assert.Equal(1, Run(s, "hexcount(\"name() != \\\"Red Rifles\\\"\")").Result!.Value.Number);
    }

    [Fact]
    public void Hex_piece_off_grid_error()
    {
        var s = Riverland();
        Add(s, "r5", "Red Reserve", 1, x: 900, y: 900);   // off the board
        Assert.Equal("Red Reserve is not in a hex", Run(s, "hexcount()", "r5").Error);
        Add(s, "x1", "", 1, map: "Nowhere");   // nameless piece on no map: labelled as the log labels it
        Assert.Equal("piece is not in a hex", Run(s, "hexsum(Strength)", "x1").Error);
    }

    [Fact]
    public void Hex_not_available_in_phase_condition()
    {
        var pack = Pack();
        var s = Riverland();
        var at = new PhasePosition(1, 1, "P", 0);
        pack.Phases[0] = pack.Phases[0] with { Condition = "hexcount() > 0" };
        Assert.True(PhaseConditions.Runs(pack, s, pack.Phases[0], at, out var err));
        Assert.Equal("hexcount() is not available here", err);
        pack.Phases[0] = pack.Phases[0] with { Condition = "hexsum(Strength, \"\") > 0" };
        PhaseConditions.Runs(pack, s, pack.Phases[0], at, out err);
        Assert.Equal("hexsum() is not available here", err);
        // Whole-game aggregates work there.
        pack.Phases[0] = pack.Phases[0] with { Condition = "countof(\"startsWith(name(), \\\"Blue\\\")\") == 0" };
        Assert.False(PhaseConditions.Runs(pack, s, pack.Phases[0], at, out err));
        Assert.Null(err);
        pack.Phases[0] = pack.Phases[0] with { Condition = "sum(Strength, \"\") == 22" };
        Assert.True(PhaseConditions.Runs(pack, s, pack.Phases[0], at, out err));
        Assert.Null(err);
        // A game-level context (no piece) refuses the hex ones too.
        Assert.False(PieceFilter.EvaluateGameLevel("hexcount() >= 0", s, pack));
        Assert.True(PieceFilter.EvaluateGameLevel("countof(\"\") == 4", s, pack));
    }

    [Fact]
    public void Aggregate_in_filter_compiles_once()
    {
        var s = Riverland();
        var pack = Pack();
        PieceAggregates.FilterCompiles = 0;
        var strongest = PieceFilter.Compile("Strength == maxof(Strength, \"startsWith(name(), \\\"Red\\\")\")", s, pack, Model);
        Assert.Equal(new[] { "r3" }, s.Pieces.Values.Where(strongest).Select(p => p.Id));
        Assert.Equal(1, PieceAggregates.FilterCompiles);
        PieceAggregates.FilterCompiles = 0;
        var stacked = PieceFilter.Compile("hexcount(\"Strength > 0\") > 1", s, pack, Model);
        Assert.Equal(new[] { "r1", "r2" }, s.Pieces.Values.Where(stacked).Select(p => p.Id));
        Assert.Equal(1, PieceAggregates.FilterCompiles);
    }

    [Fact]
    public void Aggregate_in_warning_and_alert()
    {
        var phase = new PhaseDef
        {
            Id = "P", Name = "Movement",
            Warnings = new()
            {
                new PhaseWarning { Condition = "hexcount() > 1", Message = "{unit} is stacked" },
                new PhaseWarning { Condition = "countof(\"startsWith(name(), \\\"Red\\\")\") > 2", Message = "Red is crowded" },
            },
        };
        var pack = Pack();
        pack.Phases[0] = phase;
        pack.Alerts.AddRange(new[]
        {
            new AlertDef { Id = "a1", Name = "Overstacked", Condition = "hexsum(Strength) > 6 and hexcount() > 1", Message = "{unit} overstacked" },
            new AlertDef { Id = "a2", Name = "Big army", Condition = "sum(Strength, \"\") > 20", Message = "Big army" },
        });
        var s = Riverland();
        s.Position = new PhasePosition(1, 1, "P", 0);
        var w = Decisions.Warnings(pack, s, phase, Model);
        Assert.Equal(2, w.Count);
        Assert.Equal(new[] { "r1", "r2" }, w[0].PieceIds);
        Assert.Equal(new[] { "Red Rifles is stacked", "Red Guns is stacked" }, w[0].UnitTexts);
        Assert.Empty(w[1].PieceIds);   // whole-game: one game-level hit
        Assert.Equal(new[] { "Red is crowded" }, w[1].UnitTexts);
        var hits = AlertEngine.Evaluate(s, pack, Model, phase, null);
        var over = hits.Single(h => h.Id == "a1");
        Assert.Equal(new[] { "r1", "r2" }, over.Matches.Select(m => m.PieceId));
        var big = hits.Single(h => h.Id == "a2");
        Assert.Single(big.Matches);
        Assert.Null(big.Matches[0].PieceId);
    }

    [Fact]
    public void Formula_aggregate_in_text()
    {
        var s = Riverland();
        var t = Run(s, "sum(Strength, \"startsWith(name(), \\\"Red\\\")\") * 2 + hexcount() + countof(\"\")");
        Assert.Null(t.Error);
        Assert.Equal(12 * 2 + 2 + 4, t.Result!.Value.Number);
        // Run without a piece: whole-game aggregates still work, the hex ones are not available.
        Assert.Equal(12, Run(s, "sum(Strength, \"startsWith(name(), \\\"Red\\\")\")", null).Result!.Value.Number);
        Assert.Equal("hexcount() is not available here", Run(s, "hexcount()", null).Error);
    }

    [Fact]
    public void Game_level_counts_whole_game_aggregates_only()
    {
        var pack = Pack();
        Assert.True(ExprFacts.IsGameLevel(Parser.Parse("sum(Strength, \"\") > 3 and countof(\"Strength > 1\") > 0"), pack));
        Assert.True(ExprFacts.IsGameLevel(Parser.Parse("maxof(Strength, \"\") > minof(Strength, \"\")"), pack));
        Assert.False(ExprFacts.IsGameLevel(Parser.Parse("hexcount() > 1"), pack));
        Assert.False(ExprFacts.IsGameLevel(Parser.Parse("hexsum(Strength) > 1 or Flag == 0"), pack));
    }
}
