using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>
/// B7: an Input's table choices are judged on the chooser's view (<see cref="RunRequest.PickSides"/>), as a Piece binding's candidates
/// are: in hot-seat and for a guest's intent the accepted values are exactly the listed ones, never "excluded by a value you cannot see".
/// Solo and hook runs (no PickSides) judge on the full state. The view is built at most once per run. Invented Riverland data.
/// </summary>
public class ChooserViewTests
{
    private static readonly IReadOnlySet<string> North = new HashSet<string> { "north" };

    /// <summary>A board without a grid: a place key is the hex name as written ("Ford", "Bank", "Marsh").</summary>
    private static readonly Lazy<ModuleModel> LazyModel = new(() =>
    {
        var xml = System.Text.RegularExpressions.Regex.Replace(FixtureModule.MinimalBuildFile(),
            @"<VASSAL\.build\.module\.map\.boardPicker\.board\.SquareGrid[^>]*/>", "");
        using var fx = new FixtureModule().WithBuildFile(xml).AddImage("board.png", 200, 100).AddSvg("unit.svg");
        using var arch = VmodArchive.Open(fx.Build());
        return ModuleModelBuilder.Build(arch, new DiagnosticLog());
    });

    private static ModuleModel Model => LazyModel.Value;

    /// <summary>"Mines" is a Private place (hidden from every side's view); "Reserve" a Private value of the Southern depot.</summary>
    private const string MinesFilter = "keyed(Mines, row()) == 0";

    private static RulesPack Pack(string? filter = MinesFilter, string? autoValue = null, bool pick = false)
    {
        var bindings = new List<VariableBinding>
        {
            new() { Variable = "Ford", Kind = BindingKind.Input, ChoicesTable = "t-places", ChoicesFilter = filter, AutoValue = autoValue },
        };
        if (pick) bindings.Add(new VariableBinding { Variable = "T", Kind = BindingKind.Piece, Property = "size", PieceFilter = "name() == \"North Barge\" or holder(SouthDepot, Reserve) > 0" });
        return new RulesPack
        {
            Format = 4,
            Sides = new()
            {
                new SideDef { Id = "north", Name = "Northern League", UnitFilter = "startsWith(name(), \"North\")" },
                new SideDef { Id = "south", Name = "Southern Compact", UnitFilter = "startsWith(name(), \"South\")" },
            },
            Properties = new()
            {
                new PropertyDef { Id = "size", Name = "Size", Scope = PropertyScope.Pieces, Default = 1, Visibility = Visibility.Public },
                new PropertyDef { Id = "mines", Name = "Mines", Scope = PropertyScope.Holders, KeyedBy = "hex", Visibility = Visibility.Private },
                new PropertyDef { Id = "reserve", Name = "Reserve", Scope = PropertyScope.Holders, Visibility = Visibility.Private },
            },
            Holders = new() { new HolderDef { Id = "southdepot", Name = "SouthDepot", SideId = "south" } },
            Tables = new()
            {
                new TableDef
                {
                    Id = "t-places", Name = "Places", Kind = TableKind.OneD, RowKeys = new() { "Ford", "Bank", "Marsh" },
                    ColumnKeys = new() { "Value" }, Cells = new() { new() { "1" }, new() { "2" }, new() { "3" } },
                },
            },
            Formulas = new()
            {
                new FormulaDef
                {
                    Id = "f-cross", Name = "Crossing", Text = pick ? "lookup(Places, Ford) + T" : "lookup(Places, Ford)", Bindings = bindings,
                },
            },
        };
    }

    /// <summary>A North barge (n1) and a South raft (s1); mines lie at the Ford (5), none at the Bank; the Southern reserve is 4.</summary>
    private static GameState State()
    {
        var s = new GameState();
        s.Pieces["n1"] = new PieceInstance
        {
            Id = "n1", MapId = "Main Map", Pos = new PointD(60, 40), TypeString = PieceTypeParser.EncodeState(new[] { "piece;;;unit.svg;North Barge" }),
            TraitStates = new() { "" },
        };
        s.Pieces["s1"] = new PieceInstance
        {
            Id = "s1", MapId = "Main Map", Pos = new PointD(160, 40), TypeString = PieceTypeParser.EncodeState(new[] { "piece;;;unit.svg;South Raft" }),
            TraitStates = new() { "" },
        };
        s.Apply(new LedgerDelta(LedgerKey.Holder(HolderDef.GlobalId), "mines", 0, 5, "manual", null, null, null, null) { ValueKey = "Ford" });
        s.Ledgers[LedgerKey.Holder("southdepot")] = new() { ["reserve"] = 4 };
        return s;
    }

    private static FormulaTrace Run(RulesPack pack, GameState s, string? ford, IReadOnlySet<string>? pickSides, bool auto = false,
        Dictionary<string, string>? more = null)
    {
        var typed = new Dictionary<string, string>(more ?? new Dictionary<string, string>());
        if (ford != null) typed["Ford"] = ford;
        return new FormulaRunner(pack, Model, (n, _) => Enumerable.Repeat(1, n).ToList()).Run(s, new RunRequest("f-cross", "n1", typed,
            new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>(), UseAutoValues: auto, PickSides: pickSides));
    }

    private static string? Reason(FormulaTrace t) => t.Variables.Single(v => v.Variable == "Ford").MissingReason;

    [Fact]
    public void Hotseat_choice_excluded_only_by_hidden_value_accepted()
    {
        var pack = Pack();
        var full = State();
        // The full state excludes the Ford (mines there); the North player's own list keeps it (the mines are hidden from them).
        Assert.Equal(new[] { "Bank", "Marsh" }, InputChoices.For(full, pack, Model, pack.Formulas[0], "Ford", "n1"));
        Assert.Equal(new[] { "Ford", "Bank", "Marsh" },
            InputChoices.For(PlayerView.For(full, pack, Model, North), pack, Model, pack.Formulas[0], "Ford", "n1"));
        var t = Run(pack, full, "Ford", North);
        Assert.Null(Reason(t));
        Assert.Null(t.Error);
        Assert.Equal(1, t.Result!.Value.Number);
    }

    [Fact]
    public void Hotseat_choice_not_listed_refused()
    {
        // A row the North player's own list leaves out (by a value they see), and a key not in the table: the same neutral text.
        var pack = Pack(filter: "row() != \"Marsh\" and " + MinesFilter);
        Assert.Equal(new[] { "Ford", "Bank" },
            InputChoices.For(PlayerView.For(State(), pack, Model, North), pack, Model, pack.Formulas[0], "Ford", "n1"));
        Assert.Equal(InputChoices.NotOneText, Reason(Run(pack, State(), "Marsh", North)));
        Assert.Equal(InputChoices.NotOneText, Reason(Run(pack, State(), "Swamp", North)));
        Assert.Null(Reason(Run(pack, State(), "Bank", North)));
    }

    [Fact]
    public void Hotseat_no_choice_in_own_list()
    {
        // Every row left out of the North player's own list: no choice, whatever is typed.
        var pack = Pack(filter: "row() == \"Swamp\" and " + MinesFilter);
        Assert.Equal(InputChoices.NoChoiceText, Reason(Run(pack, State(), "Ford", North)));
    }

    [Fact]
    public void Solo_choices_filter_on_full_state()
    {
        var pack = Pack();
        Assert.Equal(InputChoices.NotOneText, Reason(Run(pack, State(), "Ford", null)));
        Assert.Null(Reason(Run(pack, State(), "Bank", null)));
    }

    [Fact]
    public void Hook_run_choices_unchanged()
    {
        // A hook run (auto values, no PickSides) judges the choices on the full state, as before.
        Assert.Equal(InputChoices.NotOneText, Reason(Run(Pack(autoValue: "\"Ford\""), State(), null, null, auto: true)));
        var bank = Run(Pack(autoValue: "\"Bank\""), State(), null, null, auto: true);
        Assert.Null(Reason(bank));
        Assert.Equal(2, bank.Result!.Value.Number);
    }

    [Fact]
    public void One_view_per_run()
    {
        var pack = Pack(pick: true);
        var full = State();
        var label = PieceChoices.For(full, pack, Model, pack.Formulas[0].Bindings[1], North).Single().Label;   // only the own barge: the reserve is hidden
        PieceChoices.ViewBuilds = 0;
        var t = Run(pack, full, "Ford", North, more: new() { ["T"] = label });
        Assert.Null(t.Error);
        Assert.Null(Reason(t));
        Assert.Equal(2, t.Result!.Value.Number);   // Ford 1 + the barge's size 1
        Assert.Equal(1, PieceChoices.ViewBuilds);
    }

    [Fact]
    public void No_view_without_filter_or_pick()
    {
        PieceChoices.ViewBuilds = 0;
        var t = Run(Pack(filter: null), State(), "Ford", North);
        Assert.Null(Reason(t));
        Assert.Equal(0, PieceChoices.ViewBuilds);
        // Without PickSides nothing is built either, filter or not.
        Run(Pack(), State(), "Bank", null);
        Assert.Equal(0, PieceChoices.ViewBuilds);
    }
}
