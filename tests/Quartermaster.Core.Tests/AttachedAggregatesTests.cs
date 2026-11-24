using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>attachedsum, attachedcount, attachedmax and attachedmin: aggregates over the pieces on a piece's organisation chart. Invented data.</summary>
public class AttachedAggregatesTests
{
    private const string Unit = "piece;;;unit.svg;";

    /// <summary>Main Map, an ordinary "Holding Box" map, and two organisation displays (WidgetMap): "Red Chart" and "Blue Chart".</summary>
    private static readonly Lazy<ModuleModel> LazyModel = new(() =>
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            "<VASSAL.build.module.Map mapName=\"Holding Box\"/>" +
            "<VASSAL.build.module.WidgetMap mapName=\"Red Chart\"/>" +
            "<VASSAL.build.module.WidgetMap mapName=\"Blue Chart\"/></VASSAL.build.GameModule>");
        using var fx = new FixtureModule().WithBuildFile(xml).AddImage("board.png", 200, 100).AddSvg("unit.svg");
        using var arch = VmodArchive.Open(fx.Build());
        return ModuleModelBuilder.Build(arch, new DiagnosticLog());
    });

    private static ModuleModel Model => LazyModel.Value;

    private static RulesPack Pack(params FormulaDef[] formulas) => new()
    {
        Properties = new()
        {
            new PropertyDef { Id = "str", Name = "Strength", Scope = PropertyScope.Pieces, Default = 2 },
        },
        Phases = new() { new PhaseDef { Id = "P", Name = "Movement" } },
        Formulas = formulas.ToList(),
    };

    private static FormulaDef Formula(string text) => new() { Id = "f", Name = "Probe", Text = text };

    private static string SendTo(string map) => $"sendto;Go;K;{map};;0;0;;;0;0;1;1;;L;;;;";

    private static void Add(GameState s, string id, string name, double? str, string map, params string[] extraTraits)
    {
        var layers = extraTraits.Concat(new[] { Unit + name }).ToArray();
        s.Pieces[id] = new PieceInstance
        {
            Id = id, MapId = map, Pos = new PointD(60, 40), TraitStates = layers.Select(_ => "").ToList(),
            TypeString = PieceTypeParser.EncodeState(layers),
        };
        if (str != null) s.Ledgers[LedgerKey.Piece(id)] = new() { ["str"] = str.Value };
    }

    /// <summary>HQ on the main map (chart: Red Chart) with a stack mate; the chart holds HQ's three corps; Blue Chart holds an outsider.</summary>
    private static GameState Riverland()
    {
        var s = new GameState();
        Add(s, "hq", "Red HQ", 5, "Main Map", SendTo("Red Chart"));
        Add(s, "mate", "Red Mate", 100, "Main Map");   // stacked with HQ on the main map, not on its chart
        Add(s, "c1", "Alpha Corps", 3, "Red Chart");
        Add(s, "c2", "Bravo Corps", 4, "Red Chart");
        Add(s, "c3", "Charlie Corps", 7, "Red Chart");
        Add(s, "b1", "Blue Corps", 50, "Blue Chart");
        return s;
    }

    private static Value Eval(string expr, GameState s, RulesPack pack, PieceInstance? self)
    {
        var call = Assert.IsType<Call>(Parser.Parse(expr));
        var (property, filter) = ExprFacts.AggregateArgs(call);
        return PieceAggregates.Evaluate(call.Function, property, filter, self, new AggregateFilters(s, pack, Model));
    }

    private static FormulaTrace Run(GameState s, string text, string? piece = "hq")
    {
        var pack = Pack(Formula(text));
        return new FormulaRunner(pack, Model, (n, _) => Enumerable.Repeat(1, n).ToList())
            .Run(s, new RunRequest("f", piece, new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>()));
    }

    [Fact]
    public void Model_marks_widget_maps_as_org_displays()
    {
        Assert.True(Model.Maps.Single(m => m.Name == "Red Chart").IsOrgDisplay);
        Assert.False(Model.Maps.Single(m => m.Name == "Holding Box").IsOrgDisplay);
    }

    [Fact]
    public void Attachedsum_over_chart_pieces()
    {
        var s = Riverland();
        var pack = Pack();
        Assert.Equal(14, Eval("attachedsum(Strength)", s, pack, s.Pieces["hq"]).Number);
        Assert.Equal(11, Eval("attachedsum(Strength, \"Strength > 3\")", s, pack, s.Pieces["hq"]).Number);
        Assert.Equal(7, Eval("attachedmax(Strength)", s, pack, s.Pieces["hq"]).Number);
        Assert.Equal(3, Eval("attachedmin(Strength)", s, pack, s.Pieces["hq"]).Number);
        Assert.Equal(14, Run(s, "attachedsum(Strength)").Result!.Value.Number);
    }

    [Fact]
    public void Attached_counts_chart_not_stack()
    {
        var s = Riverland();
        var pack = Pack();
        Assert.Equal(3, Eval("attachedcount()", s, pack, s.Pieces["hq"]).Number);   // chart pieces, not HQ plus its stack mate
        Assert.Equal(2, Eval("attachedcount(\"Strength > 3\")", s, pack, s.Pieces["hq"]).Number);
        Assert.Equal(3, Run(s, "attachedcount()").Result!.Value.Number);
    }

    [Fact]
    public void Attached_includes_self_on_chart()
    {
        var s = Riverland();
        Add(s, "c4", "Delta Staff", 1, "Red Chart", SendTo("Red Chart"));   // sits on its own chart
        var pack = Pack();
        Assert.Equal(4, Eval("attachedcount()", s, pack, s.Pieces["c4"]).Number);
        Assert.Equal(15, Eval("attachedsum(Strength)", s, pack, s.Pieces["c4"]).Number);
    }

    [Fact]
    public void Attached_uses_first_org_display_sendto()
    {
        var s = Riverland();
        Add(s, "x", "Red Div", 1, "Main Map", SendTo("Holding Box"), SendTo("Blue Chart"), SendTo("Red Chart"));
        var pack = Pack();
        Assert.Equal("Blue Chart", PieceAggregates.FormationDisplay(s.Pieces["x"], Model)!.Id);
        Assert.Equal(1, Eval("attachedcount()", s, pack, s.Pieces["x"]).Number);   // Blue Chart, not the holding box or Red Chart
        Assert.Equal(50, Eval("attachedsum(Strength)", s, pack, s.Pieces["x"]).Number);
        Assert.Equal("Red Chart", PieceAggregates.FormationDisplay(s.Pieces["hq"], Model)!.Id);
    }

    [Fact]
    public void No_chart_error()
    {
        var s = Riverland();
        Assert.Equal("Red Mate has no organisation chart", Run(s, "attachedcount()", "mate").Error);
        Add(s, "box", "Red Box", 1, "Main Map", SendTo("Holding Box"));   // a SendTo to an ordinary map is no chart
        Assert.Equal("Red Box has no organisation chart", Run(s, "attachedsum(Strength)", "box").Error);
        Add(s, "ghost", "Red Ghost", 1, "Main Map", SendTo("Nowhere"));
        Assert.Equal("Red Ghost has no organisation chart", Run(s, "attachedcount()", "ghost").Error);
        Assert.Null(PieceAggregates.FormationDisplay(s.Pieces["mate"], Model));
    }

    [Fact]
    public void Attached_not_available_without_piece()
    {
        var s = Riverland();
        var pack = Pack();
        Assert.Equal("attachedcount() is not available here", Run(s, "attachedcount()", null).Error);
        Assert.Equal("attachedsum() is not available here", Run(s, "attachedsum(Strength)", null).Error);
        var ex = Assert.Throws<EvalError>(() => Eval("attachedcount()", s, pack, null));
        Assert.Equal("attachedcount() is not available here", ex.Message);
        Assert.False(PieceFilter.EvaluateGameLevel("attachedcount() >= 0", s, pack));
        var noModel = Assert.Throws<EvalError>(() => PieceAggregates.Evaluate("attachedcount", null, "", s.Pieces["hq"], new AggregateFilters(s, pack, null)));
        Assert.Equal("attachedcount() is not available here", noModel.Message);
    }

    [Fact]
    public void Attachedmax_empty_error()
    {
        var s = Riverland();
        Assert.Equal("No piece matches \"Strength > 99\"", Run(s, "attachedmax(Strength, \"Strength > 99\")").Error);
        Assert.Equal("No piece matches \"Strength > 99\"", Run(s, "attachedmin(Strength, \"Strength > 99\")").Error);
        s.Pieces.Remove("c1"); s.Pieces.Remove("c2"); s.Pieces.Remove("c3");   // an empty chart
        Assert.Equal("No piece matches \"\"", Run(s, "attachedmax(Strength)").Error);
        Assert.Equal(0, Run(s, "attachedcount()").Result!.Value.Number);
        Assert.Equal(0, Run(s, "attachedsum(Strength)").Result!.Value.Number);
    }

    [Fact]
    public void Attached_in_unit_filter_and_warning()
    {
        var s = Riverland();
        Add(s, "hq2", "Blue HQ", 1, "Main Map", SendTo("Blue Chart"));
        var formula = Formula("1") with { AppliesTo = "attachedcount() > 1" };
        var pack = Pack(formula);
        var phase = pack.Phases[0];
        Assert.Equal(new[] { "hq" }, Sequencer.UnitsFor(pack, s, phase, formula, null, Model).Select(p => p.Id));
        var filter = PieceFilter.Compile("attachedsum(Strength) == 50", s, pack, Model);
        Assert.Equal(new[] { "hq2" }, s.Pieces.Values.Where(filter).Select(p => p.Id));
        phase = phase with { Warnings = new() { new PhaseWarning { Condition = "attachedcount() > 2", Message = "{unit} is large" } } };
        var w = Decisions.Warnings(pack, s, phase, Model).Single();
        Assert.Equal(new[] { "hq" }, w.PieceIds);
        Assert.Equal(new[] { "Red HQ is large" }, w.UnitTexts);
    }

    [Fact]
    public void Attached_hidden_in_player_view_when_pieces_may_hide()
    {
        var pack = Pack(Formula("attachedcount()"));
        pack.Properties[0] = pack.Properties[0] with { Visibility = Visibility.Public };
        pack = pack with
        {
            Sides = new()
            {
                new SideDef { Id = "red", Name = "Red", UnitFilter = "startsWith(name(), \"Red\")" },
                new SideDef { Id = "blue", Name = "Blue", UnitFilter = "startsWith(name(), \"Blue\")" },
            },
            MapOwners = new() { new MapOwner { MapId = "Red Chart", SideId = "red" } },
        };
        var full = Riverland();
        var view = PlayerView.For(full, pack, Model, new HashSet<string> { "blue" });
        Assert.True(view.IsPlayerView);
        var t = new FormulaRunner(pack, Model, (n, _) => Enumerable.Repeat(1, n).ToList())
            .Run(view, new RunRequest("f", "hq", new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>()));
        Assert.Equal("attachedcount() reads data this player cannot see", t.Error);
        Assert.Equal(3, Run(full, "attachedcount()").Result!.Value.Number);
    }
}
