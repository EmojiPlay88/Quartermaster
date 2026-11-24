using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>A piece property overriding a table cell for the piece whose name is the row (invented Riverland rafts).</summary>
public class CellOverrideRunTests
{
    private static RulesPack Pack(params PropertyDef[] extra)
    {
        var props = new List<PropertyDef>
        {
            new() { Id = "fs", Name = "Fast Speed", Scope = PropertyScope.Pieces, Default = 99, OverridesTable = "tu", OverridesColumn = "Speed" },
            new() { Id = "ch", Name = "Cheap", Scope = PropertyScope.Pieces, Default = 98, OverridesTable = "tc" },
        };
        props.AddRange(extra);
        return new RulesPack
        {
            Properties = props,
            Tables = new()
            {
                new TableDef
                {
                    Id = "tu", Name = "Units", Kind = TableKind.TwoD,
                    RowKeys = new() { "Raft A", "Raft B", "12.0" }, ColumnKeys = new() { "Speed", "Load" },
                    Cells = new() { new() { "3", "4" }, new() { "5", "6" }, new() { "1", "2" } },
                },
                new TableDef
                {
                    Id = "tc", Name = "Costs", Kind = TableKind.OneD,
                    RowKeys = new() { "Raft A" }, ColumnKeys = new() { "Cost" }, Cells = new() { new() { "5" } },
                },
            },
            Formulas = new()
            {
                F("f-bind", "S", new VariableBinding { Variable = "S", Kind = BindingKind.Table, TableId = "tu", RowExpr = "name()", ColExpr = "\"Speed\"" }),
                F("f-bind1", "C", new VariableBinding { Variable = "C", Kind = BindingKind.Table, TableId = "tc", RowExpr = "name()" }),
                F("f-inline", "lookup(Units, name(), \"Speed\")"),
                F("f-a", "lookup(Units, \"Raft A\", \"Speed\")"),
                F("f-b", "lookup(Units, \"Raft B\", \"Speed\")"),
                F("f-load", "lookup(Units, name(), \"Load\")"),
            },
        };
    }

    private static FormulaDef F(string id, string text, params VariableBinding[] b) =>
        new() { Id = id, Name = id, Text = text, Bindings = b.ToList() };

    private static PieceInstance Piece(string id, string name) =>
        new() { Id = id, TypeString = $"piece;;;unit.svg;{name}", MapId = "Main Map", Pos = new PointD(60, 40), TraitStates = new() { "" } };

    /// <summary>u1 "Raft A" with Fast Speed 7 and Cheap 2; u2 "Raft B" with Fast Speed 8; u3 "Raft A" with no values.</summary>
    private static GameState State()
    {
        var s = new GameState();
        s.Pieces["u1"] = Piece("u1", "Raft A");
        s.Pieces["u2"] = Piece("u2", "Raft B");
        s.Pieces["u3"] = Piece("u3", "Raft A");
        s.Ledgers[LedgerKey.Piece("u1")] = new() { ["fs"] = 7, ["ch"] = 2 };
        s.Ledgers[LedgerKey.Piece("u2")] = new() { ["fs"] = 8 };
        return s;
    }

    private static FormulaTrace Run(RulesPack pack, GameState s, string formula, string piece)
    {
        var t = RunnerFx.Runner(pack).Run(s, RunnerFx.Req(formula, piece));
        Assert.Null(t.Error);
        return t;
    }

    private static double Result(RulesPack pack, GameState s, string formula, string piece) => Run(pack, s, formula, piece).Result!.Value.Number;

    [Fact]
    public void Binding_uses_override_when_piece_has_value()
    {
        var (pack, s) = (Pack(), State());
        Assert.Equal(7, Result(pack, s, "f-bind", "u1"));
        Assert.Equal(8, Result(pack, s, "f-bind", "u2"));
        Assert.Equal(2, Result(pack, s, "f-bind1", "u1"));   // 1-D: any column
    }

    [Fact]
    public void Inline_lookup_uses_override()
    {
        var (pack, s) = (Pack(), State());
        Assert.Equal(7, Result(pack, s, "f-inline", "u1"));
        Assert.Equal(7, Result(pack, s, "f-a", "u1"));
    }

    [Fact]
    public void Lookup_of_other_row_uses_cell()
    {
        var (pack, s) = (Pack(), State());
        Assert.Equal(5, Result(pack, s, "f-b", "u1"));   // Raft A evaluating Raft B's row: B's printed cell, not A's 7 (nor B's own 8)
        Assert.Equal(3, Result(pack, s, "f-a", "u2"));
    }

    [Fact]
    public void No_value_uses_cell_not_default()
    {
        var (pack, s) = (Pack(), State());
        Assert.Equal(3, Result(pack, s, "f-bind", "u3"));
        Assert.Equal(3, Result(pack, s, "f-inline", "u3"));
        Assert.Equal(5, Result(pack, s, "f-bind1", "u3"));
        Assert.Equal("Units, row Raft A, column Speed", Run(pack, s, "f-bind", "u3").Variables.Single().Source);
    }

    [Fact]
    public void Game_level_lookup_uses_cell()
    {
        var (pack, s) = (Pack(), State());
        Assert.True(PieceFilter.EvaluateGameLevel("lookup(Units, \"Raft A\", \"Speed\") == 3", s, pack, RunnerFx.Model));
        Assert.Equal(3, Result(pack, s, "f-a", "u2"));
        var noPiece = RunnerFx.Runner(pack).Run(s, RunnerFx.Req("f-a", piece: null));
        Assert.Equal(3, noPiece.Result!.Value.Number);
    }

    [Fact]
    public void Row_match_uses_table_key_normalisation()
    {
        var (pack, s) = (Pack(), State());
        s.Pieces["u4"] = Piece("u4", "raft  a");
        s.Ledgers[LedgerKey.Piece("u4")] = new() { ["fs"] = 11 };
        Assert.Equal(11, Result(pack, s, "f-bind", "u4"));
        Assert.Equal(11, Result(pack, s, "f-inline", "u4"));
        s.Pieces["u5"] = Piece("u5", "12");
        s.Ledgers[LedgerKey.Piece("u5")] = new() { ["fs"] = 13 };
        Assert.Equal(13, Result(pack, s, "f-inline", "u5"));   // name "12" locates key "12.0"
        s.Pieces["u6"] = Piece("u6", "Raft A2");
        s.Ledgers[LedgerKey.Piece("u6")] = new() { ["fs"] = 14 };
        Assert.Equal(3, Result(pack, s, "f-a", "u6"));
    }

    [Fact]
    public void Other_column_uses_cell()
    {
        var (pack, s) = (Pack(), State());
        Assert.Equal(4, Result(pack, s, "f-load", "u1"));
    }

    [Fact]
    public void Computed_override_applies()
    {
        var boost = new PropertyDef
        {
            Id = "bo", Name = "Boost", Scope = PropertyScope.Pieces, Expr = "2 * 5", AppliesTo = "name() == \"Raft B\"",
            OverridesTable = "tu", OverridesColumn = "Speed",
        };
        var (pack, s) = (Pack(boost), State());
        s.Ledgers.Remove(LedgerKey.Piece("u2"));
        Assert.Equal(10, Result(pack, s, "f-bind", "u2"));   // computed and applying
        Assert.Equal(7, Result(pack, s, "f-bind", "u1"));    // stored Fast Speed comes first in pack order
        Assert.Equal(3, Result(pack, s, "f-bind", "u3"));    // Boost does not apply to a Raft A: the cell
    }

    [Fact]
    public void Unit_filter_sees_override()
    {
        var (pack, s) = (Pack(), State());
        Assert.True(PieceFilter.Matches("lookup(Units, name(), \"Speed\") == 7", s.Pieces["u1"], s, RunnerFx.Model, pack));
        Assert.True(PieceFilter.Matches("lookup(Units, name(), \"Speed\") == 3", s.Pieces["u3"], s, RunnerFx.Model, pack));
        Assert.True(PieceFilter.Matches("lookup(Units, \"Raft B\", \"Speed\") == 5", s.Pieces["u1"], s, RunnerFx.Model, pack));
    }

    [Fact]
    public void Working_source_names_override()
    {
        var (pack, s) = (Pack(), State());
        var v = Run(pack, s, "f-bind", "u1").Variables.Single();
        Assert.Equal("Units, row Raft A, column Speed: Fast Speed of Raft A (printed 3)", v.Source);
        Assert.Equal(new TableCell("tu", 0, 0), v.Cell);
        Assert.Equal("Costs, row Raft A: Cheap of Raft A (printed 5)", Run(pack, s, "f-bind1", "u1").Variables.Single().Source);
    }

    [Fact]
    public void Breakdown_line()
    {
        var (pack, s) = (Pack(), State());
        var two = LedgerBreakdown.For(s, pack, null, LedgerKey.Piece("u1"), "fs");
        Assert.Contains(two.Lines, l => l.Label == "overrides Units, column Speed");
        var one = LedgerBreakdown.For(s, pack, null, LedgerKey.Piece("u1"), "ch");
        Assert.Contains(one.Lines, l => l.Label == "overrides Costs");
    }

    [Fact]
    public void Hidden_private_override_in_view_is_missing()
    {
        var pack = Pack() with { Sides = new() { new SideDef { Id = "red", Name = "Red", UnitFilter = "name() == \"Raft A\"" } } };
        pack.Properties[0] = pack.Properties[0] with { Visibility = Visibility.Private };
        var s = State();   // u3 (Raft A, red) has no Fast Speed: in a view of another side that may be hidden, not unset
        s.IsPlayerView = true;
        s.ViewSides = new() { "blue" };
        var bind = RunnerFx.Runner(pack).Run(s, RunnerFx.Req("f-bind", "u3"));
        Assert.Null(bind.Result);
        Assert.False(string.IsNullOrEmpty(bind.Variables.Single().MissingReason));
        Assert.Null(RunnerFx.Runner(pack).Run(s, RunnerFx.Req("f-inline", "u3")).Result);
        Assert.Equal(5, Result(pack, s, "f-b", "u3"));   // another row: the cell, nothing hidden
        s.ViewSides = new() { "red" };
        Assert.Equal(3, Result(pack, s, "f-inline", "u3"));   // own side: no value, the cell
    }
}
