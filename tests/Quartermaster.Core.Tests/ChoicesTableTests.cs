using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Rules.Tables;
using Quartermaster.Vassal;
using static Quartermaster.Core.Tests.RunnerFx;

namespace Quartermaster.Core.Tests;

/// <summary>B4 choices from a table: an Input offering a table's row keys, filtered per counter. Invented Riverland data.</summary>
public class ChoicesTableTests
{
    private const string SizeFilter = "lookup(Cargo, row(), \"Min size\") <= Size";
    private static readonly IReadOnlySet<string> North = new HashSet<string> { "north" };

    private static RulesPack Pack(string? filter = SizeFilter, string table = "t-cargo") => new()
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
        },
        Tables = new()
        {
            new TableDef
            {
                Id = "t-cargo", Name = "Cargo", Kind = TableKind.TwoD, RowKeys = new() { "Grain", " Timber ", "Stone", "grain" },
                ColumnKeys = new() { "Min size" }, Cells = new() { new() { "1" }, new() { "2" }, new() { "3" }, new() { "1" } },
            },
            new TableDef
            {
                Id = "t-loads", Name = "Loads", Kind = TableKind.TwoD, RowKeys = new() { "1", "2", "3" },
                ColumnKeys = new() { "Min size" }, Cells = new() { new() { "1" }, new() { "2" }, new() { "3" } },
            },
        },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "f-cargo", Name = "Barge cargo", Text = "Cargo",
                Bindings = new() { new VariableBinding { Variable = "Cargo", Kind = BindingKind.Input, ChoicesTable = table, ChoicesFilter = filter } },
            },
        },
    };

    private static GameState State(double size = 2)
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
        s.Ledgers[LedgerKey.Piece("n1")] = new() { ["size"] = size };
        return s;
    }

    private static FormulaTrace Run(GameState s, RulesPack pack, string? typed, string? piece = "n1") =>
        new FormulaRunner(pack, Model, (n, _) => Enumerable.Repeat(1, n).ToList()).Run(s, new RunRequest("f-cargo", piece,
            typed == null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Cargo"] = typed },
            new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>()));

    private static IReadOnlyList<string>? Choices(GameState s, RulesPack pack, string? piece = "n1") =>
        InputChoices.For(s, pack, Model, pack.Formulas[0], "Cargo", piece);

    private static string? Reason(FormulaTrace t) => t.Variables.Single(v => v.Variable == "Cargo").MissingReason;

    [Fact]
    public void ChoicesTable_lists_filtered_keys()
    {
        var pack = Pack();
        Assert.Equal(new[] { "Grain", "Timber" }, Choices(State(), pack));   // normalised, a repeated key once
        var t = Run(State(), pack, " timber ");
        Assert.Null(t.Error);
        Assert.Null(Reason(t));
        Assert.Equal(ValueKind.Text, t.Result!.Value.Kind);
        Assert.Equal("Timber", t.Result.Value.Text);
    }

    [Fact]
    public void ChoicesTable_no_filter_all_keys()
    {
        var pack = Pack(filter: null);
        Assert.Equal(new[] { "Grain", "Timber", "Stone" }, Choices(State(), pack));
        Assert.Equal("Stone", Run(State(), pack, "Stone").Result!.Value.Text);
        // an Input without a choices table keeps today's choices
        var plain = pack with { Formulas = new() { pack.Formulas[0] with { Bindings = new() { new VariableBinding { Variable = "Cargo", Kind = BindingKind.Input, Choices = new() { "A", "B" } } } } } };
        Assert.Equal(new[] { "A", "B" }, Choices(State(), plain));
    }

    [Fact]
    public void ChoicesTable_no_piece_all_keys()
    {
        var pack = Pack();
        Assert.Equal(new[] { "Grain", "Timber", "Stone" }, Choices(State(), pack, piece: null));
        var t = Run(State(), pack, "Stone", piece: null);
        Assert.Null(t.Error);
        Assert.Equal("Stone", t.Result!.Value.Text);
    }

    [Fact]
    public void ChoicesTable_typed_not_listed_missing()
    {
        var t = Run(State(), Pack(), "Stone");
        Assert.Null(t.Result);
        Assert.Equal("Not one of the choices for this piece", Reason(t));
        Assert.Equal("Not one of the choices for this piece", Run(State(), Pack(), "Coal").Variables.Single().MissingReason);
        Assert.Equal("Waiting for input", Reason(Run(State(), Pack(), null)));
    }

    [Fact]
    public void ChoicesTable_none_left_missing()
    {
        var s = State(size: 0);
        Assert.Empty(Choices(s, Pack())!);
        Assert.Equal("No choice for this piece", Reason(Run(s, Pack(), "Grain")));
        Assert.Equal("No choice for this piece", Reason(Run(s, Pack(), null)));
    }

    [Fact]
    public void ChoicesFilter_error_missing()
    {
        var pack = Pack(filter: "lookup(Cargo, row(), \"Weight\") <= Size");
        var t = Run(State(), pack, "Grain");
        Assert.Null(t.Result);
        Assert.Equal("Choices filter: No column for Weight in Cargo", Reason(t));
    }

    [Fact]
    public void ChoicesFilter_hidden_row_kept_on_view()
    {
        // countof() over every piece reads data a player view cannot see: on a view each row is kept (the host decides)
        var pack = Pack(filter: SizeFilter + " + countof(\"\") * 0");
        var full = State();
        Assert.Equal(new[] { "Grain", "Timber" }, Choices(full, pack));
        Assert.Equal("Not one of the choices for this piece", Reason(Run(full, pack, "Stone")));
        var view = PlayerView.For(full, pack, Model, North);
        Assert.True(view.IsPlayerView);
        Assert.Equal(new[] { "Grain", "Timber", "Stone" }, Choices(view, pack));
        var t = Run(view, pack, "Stone");
        Assert.Null(Reason(t));
        Assert.Equal("Stone", t.Result!.Value.Text);
    }

    [Fact]
    public void ChoicesTable_numeric_keys_give_numbers()
    {
        var pack = Pack(filter: "row() <= Size", table: "t-loads");
        Assert.Equal(new[] { "1", "2" }, Choices(State(), pack));
        var t = Run(State(), pack, "2");
        Assert.Equal(ValueKind.Number, t.Result!.Value.Kind);
        Assert.Equal(2, t.Result.Value.Number);
        Assert.Equal("Not one of the choices for this piece", Reason(Run(State(), pack, "3")));
    }
}
