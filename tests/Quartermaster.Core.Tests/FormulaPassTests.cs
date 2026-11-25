using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>#31: a formula's expressions are parsed once per record, and a pass shares one side map and one aggregate state. Invented data.</summary>
public class FormulaPassTests
{
    private static readonly FormulaDef Boost = new()
    {
        Id = "f-boost", Name = "Boost", Text = "S + B",
        Precondition = "S >= 0",
        Bindings = new()
        {
            new VariableBinding { Variable = "S", Kind = BindingKind.Unit, Property = "strength" },
            new VariableBinding { Variable = "B", Kind = BindingKind.Table, TableId = "t-bonus", RowExpr = "\"Clear\"", When = "S > -1" },
        },
        Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "strength", Op = OutputOp.Set, Expr = "S + B + 0" } },
    };

    private static RulesPack Pack(params FormulaDef[] extra) => new()
    {
        Format = 4,
        Sides = new()
        {
            new SideDef { Id = "red", Name = "Red", UnitFilter = "marker(\"Side\") == \"Red\"" },
            new SideDef { Id = "blue", Name = "Blue", UnitFilter = "marker(\"Side\") == \"Blue\"" },
        },
        Properties = new() { new PropertyDef { Id = "strength", Name = "Strength", Scope = PropertyScope.Pieces } },
        Tables = new()
        {
            new TableDef
            {
                Id = "t-bonus", Name = "TestBonus", Kind = TableKind.OneD,
                RowKeys = new() { "Clear", "Sand" }, ColumnKeys = new() { "Value" },
                Cells = new() { new() { "2" }, new() { "3" } },
            },
        },
        Formulas = new[]
        {
            Boost with { },
            new FormulaDef
            {
                Id = "f-side", Name = "Side bonus", Text = "if(side() == \"Red\", 1, 2)",
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "strength", Op = OutputOp.Add } },
            },
            new FormulaDef
            {
                Id = "f-tally", Name = "Tally", Text = "sum(Strength, \"side() == \\\"Red\\\"\") + 1",
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "strength", Op = OutputOp.Set } },
            },
        }.Concat(extra).ToList(),
    };

    private static GameState S()
    {
        var s = new GameState { GameId = "g" };
        foreach (var (id, side) in new[] { ("u1", "Red"), ("u2", "Red"), ("u3", "Red"), ("b1", "Blue") })
            s.Apply(new AddPiece(id, PieceTypeParser.EncodeState(new[] { "mark;Side", "piece;;;unit.svg;Boat " + id }), PieceTypeParser.EncodeState(new[] { side, "" }),
                "Main Map", new PointD(50, 50), null, id + " placed"));
        return s;
    }

    private static FormulaRunner Runner(RulesPack pack) => new(pack, RunnerFx.Model, (n, _) => Enumerable.Repeat(1, n).ToList());

    private static FormulaTrace Apply(FormulaRunner runner, GameState s, string formula, string piece)
    {
        var none = new Dictionary<string, string>();
        var t = runner.Run(s, new RunRequest(formula, piece, none, none, new Dictionary<string, IReadOnlyList<int>>(), null, true));
        Assert.Null(t.Error);
        foreach (var c in runner.Commands(t, null)) s.Apply(c);
        runner.AfterApply();
        return t;
    }

    private static double Strength(GameState s, string id) => Ledger.Get(s, LedgerKey.Piece(id), "strength") ?? 0;

    [Fact]
    public void Pass_parses_formula_once()
    {
        var pack = Pack();
        var runner = Runner(pack);
        var s = S();
        FormulaRunner.Parses = 0;
        using (runner.BeginPass(s))
            foreach (var id in new[] { "u1", "u2", "u3" }) Apply(runner, s, "f-boost", id);
        // text, precondition, the output's value expression, the table binding's row and "Only when": each distinct text once
        Assert.Equal(5, FormulaRunner.Parses);
        Assert.Equal(2, Strength(s, "u3"));
        Apply(runner, s, "f-boost", "u1");   // outside a pass too: the record's parse is kept
        Assert.Equal(5, FormulaRunner.Parses);
        Assert.Equal(4, Strength(s, "u1"));
    }

    [Fact]
    public void Pass_builds_side_map_once()
    {
        var pack = Pack();
        var runner = Runner(pack);
        var s = S();
        Ownership.SideMapBuilds = 0;
        foreach (var id in new[] { "u1", "u2", "b1" }) Apply(runner, s, "f-side", id);
        Assert.Equal(3, Ownership.SideMapBuilds);   // without a pass: one per run
        Ownership.SideMapBuilds = 0;
        using (runner.BeginPass(s))
        {
            foreach (var id in new[] { "u1", "u2", "b1" }) Apply(runner, s, "f-side", id);
            foreach (var id in new[] { "u1", "u2" }) Apply(runner, s, "f-tally", id);   // the aggregate's filter reads side() too
        }
        Assert.Equal(1, Ownership.SideMapBuilds);
        // f-side: Red +1, Blue +2 (twice); then Tally sums the Red pieces: u1 = 2 + 2 + 0 + 1, u2 = 5 + 2 + 0 + 1
        Assert.Equal(new[] { 5.0, 8.0, 0.0, 4.0 }, new[] { "u1", "u2", "u3", "b1" }.Select(id => Strength(s, id)).ToArray());
        Ownership.SideMapBuilds = 0;
        Apply(runner, s, "f-side", "u1");           // the pass is closed: its side map is gone
        Assert.Equal(1, Ownership.SideMapBuilds);
    }

    [Fact]
    public void Pass_sees_earlier_units_writes()
    {
        var pack = Pack();
        var runner = Runner(pack);
        var s = S();
        using (runner.BeginPass(s))
        {
            Apply(runner, s, "f-tally", "u1");   // 0 + 1
            Apply(runner, s, "f-tally", "u2");   // reads u1's 1: 1 + 1
            Apply(runner, s, "f-tally", "u3");   // 1 + 2 + 1
        }
        Assert.Equal(new[] { 1.0, 2.0, 4.0 }, new[] { "u1", "u2", "u3" }.Select(id => Strength(s, id)).ToArray());

        // A pass on another state instance is not used for this one.
        var other = S();
        using (runner.BeginPass(other))
            Assert.Equal(8, Apply(runner, s, "f-tally", "u1").Result!.Value.Number);   // 1 + 2 + 4 + 1, read from s
        Assert.Equal(8, Strength(s, "u1"));
    }

    [Fact]
    public void Edited_formula_record_reparsed()
    {
        var pack = Pack();
        var s = S();
        Apply(Runner(pack), s, "f-boost", "u1");
        FormulaRunner.Parses = 0;
        var edited = pack with { Formulas = pack.Formulas.Select(f => f.Id == "f-boost" ? f with { Text = "S + B + 1" } : f).ToList() };
        var t = Apply(Runner(edited), s, "f-boost", "u1");
        Assert.Equal(5, FormulaRunner.Parses);   // a new record: its own cache, every text parsed again (the new text among them)
        Assert.Equal(5, t.Result!.Value.Number);       // 2 + 2 + 1
        Assert.Equal(4, Strength(s, "u1"));
        Apply(Runner(pack), s, "f-boost", "u1");  // the old record keeps its cache
        Assert.Equal(5, FormulaRunner.Parses);
        Assert.Equal(6, Strength(s, "u1"));
    }

    // ---- B3: piece commands inside a pass ----

    private static RulesPack FlagPack() => new()
    {
        Format = 4,
        Sides = new()
        {
            new SideDef { Id = "red", Name = "Red", UnitFilter = "layer(\"Flag\") == 1" },
            new SideDef { Id = "blue", Name = "Blue", UnitFilter = "layer(\"Flag\") == 2" },
        },
        Properties = new() { new PropertyDef { Id = "strength", Name = "Strength", Scope = PropertyScope.Pieces } },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "f-turn", Name = "Turn coat", Text = "1",
                Bindings = new()
                {
                    new VariableBinding { Variable = "T", Kind = BindingKind.Piece, Property = "strength" },
                    new VariableBinding { Variable = "V", Kind = BindingKind.Piece, Property = "strength" },
                },
                Outputs = new()
                {
                    new FormulaOutput { Action = OutputAction.SetLayer, Target = OutputTarget.Piece, PieceVariable = "T", Layer = "Flag", Expr = "2" },
                    new FormulaOutput { Action = OutputAction.Remove, Target = OutputTarget.Piece, PieceVariable = "V" },
                },
            },
            new FormulaDef
            {
                Id = "f-check", Name = "Check", Text = "if(side() == \"Blue\", 10, 0) + hexcount()",
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "strength", Op = OutputOp.Set } },
            },
            new FormulaDef
            {
                Id = "f-plus", Name = "Plus", Text = "1",
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "strength", Op = OutputOp.Add } },
            },
        },
    };

    private static GameState Flags()
    {
        var s = new GameState { GameId = "g" };
        void Add(string id, double x, bool flag)
        {
            var traits = flag ? new[] { EffectFx.Flag, "piece;;;unit.svg;Boat " + id } : new[] { "piece;;;unit.svg;Boat " + id };
            var states = flag ? new[] { "1", "" } : new[] { "" };
            s.Apply(new AddPiece(id, PieceTypeParser.EncodeState(traits), PieceTypeParser.EncodeState(states), "Main Map", new PointD(x, 40), null, id + " placed"));
        }
        Add("u1", 60, true);
        Add("u2", 160, true);
        Add("x1", 160, false);
        return s;
    }

    /// <summary>One unit of a Fully automatic entry: run, execute the commands, then tell the runner what was executed.</summary>
    private static FormulaTrace Unit(FormulaRunner runner, GameState s, string formula, string piece, Dictionary<string, string>? typed = null)
    {
        var none = new Dictionary<string, string>();
        var t = runner.Run(s, new RunRequest(formula, piece, typed ?? none, none, new Dictionary<string, IReadOnlyList<int>>(), null, true));
        Assert.Null(t.Error);
        var cmds = runner.Commands(t, null);
        foreach (var c in cmds) s.Apply(c);
        runner.AfterApply(cmds);
        return t;
    }

    private static string Label(GameState s, string id) => $"Boat {id} ({PieceInfo.Location(s.Pieces[id], EffectFx.Model)})";

    [Fact]
    public void Pass_refreshes_after_piece_commands()
    {
        var pack = FlagPack();
        var runner = new FormulaRunner(pack, EffectFx.Model, (n, _) => Enumerable.Repeat(1, n).ToList());
        var s = Flags();
        FormulaRunner.PassRefreshes = 0;
        using (runner.BeginPass(s))
        {
            Assert.Equal(1, Unit(runner, s, "f-check", "u1").Result!.Value.Number);   // Red, alone in its hex; the pass indexes the hexes
            // unit 1's run turns u2 Blue and removes x1 from u2's hex
            Unit(runner, s, "f-turn", "u1", new() { ["T"] = Label(s, "u2"), ["V"] = Label(s, "x1") });
            Assert.Equal(1, FormulaRunner.PassRefreshes);
            Assert.False(s.Pieces.ContainsKey("x1"));
            // unit 2 sees both: side() is Blue and its hex holds only itself
            Assert.Equal(11, Unit(runner, s, "f-check", "u2").Result!.Value.Number);
        }
        Assert.Equal(11, Ledger.Get(s, LedgerKey.Piece("u2"), "strength"));
    }

    [Fact]
    public void Pass_not_refreshed_by_ledger_only_commands()
    {
        var pack = FlagPack();
        var runner = new FormulaRunner(pack, EffectFx.Model, (n, _) => Enumerable.Repeat(1, n).ToList());
        var s = Flags();
        FormulaRunner.PassRefreshes = 0;
        Ownership.SideMapBuilds = 0;
        using (runner.BeginPass(s))
        {
            foreach (var id in new[] { "u1", "u2", "x1" }) Unit(runner, s, "f-plus", id);
            foreach (var id in new[] { "u1", "u2" }) Unit(runner, s, "f-check", id);
        }
        Assert.Equal(0, FormulaRunner.PassRefreshes);
        Assert.Equal(1, Ownership.SideMapBuilds);
        runner.AfterApply(new GameCommand[] { new RemovePiece("x1") });   // no pass open: nothing to refresh
        Assert.Equal(0, FormulaRunner.PassRefreshes);
    }
}
