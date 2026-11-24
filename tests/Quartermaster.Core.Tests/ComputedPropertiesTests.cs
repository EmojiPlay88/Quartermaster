using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>Computed properties (<see cref="PropertyDef.Expr"/>) read through <see cref="PropertyValues"/> at every rules read site. Invented data.</summary>
public class ComputedPropertiesTests
{
    private static ModuleModel Model => RunnerFx.Model;

    private static RulesPack Pack(params PropertyDef[] extra)
    {
        var pack = new RulesPack
        {
            Properties = new()
            {
                new PropertyDef { Id = "str", Name = "Strength", Scope = PropertyScope.Pieces, Default = 2 },
                new PropertyDef { Id = "mor", Name = "Morale", Scope = PropertyScope.Pieces, Default = 1 },
                new PropertyDef { Id = "coh", Name = "Cohesion", Scope = PropertyScope.Pieces, Expr = "Strength + Morale" },
                new PropertyDef { Id = "dbl", Name = "Double", Scope = PropertyScope.Pieces, Expr = "Cohesion * 2" },
                new PropertyDef { Id = "supply", Name = "Supply", Scope = PropertyScope.Holders },
                new PropertyDef { Id = "stock", Name = "Stock", Scope = PropertyScope.Holders, Expr = "Supply * 10" },
            },
            Holders = new() { new HolderDef { Id = "depot", Name = "Depot" } },
            Phases = new() { new PhaseDef { Id = "P", Name = "Supply Phase", Condition = "Stock > 25" } },
        };
        pack.Properties.AddRange(extra);
        return pack;
    }

    private static PropertyDef Prop(RulesPack pack, string id) => pack.Properties.Single(p => p.Id == id);

    private static void Replace(RulesPack pack, string id, Func<PropertyDef, PropertyDef> change)
    {
        var i = pack.Properties.FindIndex(p => p.Id == id);
        pack.Properties[i] = change(pack.Properties[i]);
    }

    /// <summary>u1 "Rifle" (Strength 3, Morale 4) and u2 "Scout" (no values: Defaults 2 and 1); Supply 9 at Depot, 2 globally.</summary>
    private static GameState State()
    {
        var s = new GameState();
        s.Pieces["u1"] = new PieceInstance { Id = "u1", TypeString = "piece;;;unit.svg;Rifle", MapId = "Main Map", Pos = new PointD(60, 40), TraitStates = new() { "" } };
        s.Pieces["u2"] = new PieceInstance { Id = "u2", TypeString = "piece;;;unit.svg;Scout", MapId = "Main Map", Pos = new PointD(60, 40), TraitStates = new() { "" } };
        s.Ledgers[LedgerKey.Piece("u1")] = new() { ["str"] = 3, ["mor"] = 4 };
        s.Ledgers[LedgerKey.Holder("depot")] = new() { ["supply"] = 9 };
        s.Ledgers[LedgerKey.Holder(HolderDef.GlobalId)] = new() { ["supply"] = 2 };
        return s;
    }

    private static double? Get(GameState s, RulesPack pack, string key, string prop) => PropertyValues.Get(s, pack, Model, key, Prop(pack, prop));

    [Fact]
    public void Computed_piece_value_from_two_stored()
    {
        var (s, pack) = (State(), Pack());
        Assert.Equal(7, Get(s, pack, LedgerKey.Piece("u1"), "coh"));
        Assert.Equal(3, Get(s, pack, LedgerKey.Piece("u2"), "coh"));   // Defaults 2 + 1
        Assert.Equal(7, PropertyValues.GetOrDefault(s, pack, Model, LedgerKey.Piece("u1"), Prop(pack, "coh")));
        Assert.Equal(3, PropertyValues.Get(s, pack, Model, LedgerKey.Piece("u1"), Prop(pack, "str")));   // stored: the ledger value
        Assert.Null(Get(s, pack, LedgerKey.Piece("gone"), "coh"));
    }

    [Fact]
    public void Computed_reads_other_computed()
    {
        var (s, pack) = (State(), Pack());
        Assert.Equal(14, Get(s, pack, LedgerKey.Piece("u1"), "dbl"));
    }

    [Fact]
    public void Computed_holder_reads_its_own_holder()
    {
        var (s, pack) = (State(), Pack());
        Assert.Equal(90, Get(s, pack, LedgerKey.Holder("depot"), "stock"));
        Assert.Equal(20, Get(s, pack, LedgerKey.Holder(HolderDef.GlobalId), "stock"));
        Assert.True(PieceFilter.EvaluateGameLevel("holder(Depot, Stock) == 90 and Stock == 20", s, pack, Model));
        // Both scope: evaluated for the piece on a piece key (Supply is the global one there), for the holder on a holder key
        var both = Pack(new PropertyDef { Id = "reach", Name = "Reach", Scope = PropertyScope.Both, Expr = "Supply + 1" });
        Assert.Equal(3, Get(s, both, LedgerKey.Piece("u1"), "reach"));
        Assert.Equal(10, Get(s, both, LedgerKey.Holder("depot"), "reach"));
        var unit = Pack(new PropertyDef { Id = "bad", Name = "Bad", Scope = PropertyScope.Holders, Expr = "Strength + 1" });
        var ex = Assert.Throws<EvalError>(() => Get(s, unit, LedgerKey.Holder("depot"), "bad"));
        Assert.Equal("Bad: This formula needs a unit", ex.Message);
    }

    [Fact]
    public void Computed_global_in_phase_condition()
    {
        var (s, pack) = (State(), Pack());
        var at = new PhasePosition(1, 1, "P", 0);
        Assert.False(PhaseConditions.Runs(pack, s, pack.Phases[0], at, out var error, Model));   // Stock 20
        Assert.Null(error);
        s.Ledgers[LedgerKey.Holder(HolderDef.GlobalId)]["supply"] = 3;
        Assert.True(PhaseConditions.Runs(pack, s, pack.Phases[0], at, out error, Model));   // Stock 30
        Assert.Null(error);
    }

    [Fact]
    public void Computed_in_unit_filter_and_binding()
    {
        var (s, pack) = (State(), Pack());
        Assert.True(PieceFilter.Matches("Cohesion > 5 and has(Cohesion)", s.Pieces["u1"], s, Model, pack));
        Assert.False(PieceFilter.Matches("Cohesion > 5", s.Pieces["u2"], s, Model, pack));
        Assert.True(PieceFilter.Matches("Stock == 20", s.Pieces["u2"], s, Model, pack));   // a global computed from a piece
        pack.Formulas.Add(new FormulaDef
        {
            Id = "f", Name = "Probe", Text = "C + G + H",
            Bindings = new()
            {
                new VariableBinding { Variable = "C", Kind = BindingKind.Unit, Property = "coh" },
                new VariableBinding { Variable = "G", Kind = BindingKind.Global, Property = "stock" },
                new VariableBinding { Variable = "H", Kind = BindingKind.Holder, HolderId = "depot", Property = "stock" },
            },
        });
        var t = Runner(pack).Run(s, Request("f", "u1"));
        Assert.Null(t.Error);
        Assert.Equal(7 + 20 + 90, t.Result!.Value.Number);
    }

    [Fact]
    public void Computed_in_sum()
    {
        var (s, pack) = (State(), Pack());
        Assert.True(PieceFilter.EvaluateGameLevel("sum(Cohesion, \"\") == 10 and maxof(Double, \"\") == 14", s, pack, Model));
        Assert.True(PieceFilter.EvaluateGameLevel("countof(\"Cohesion > 5\") == 1", s, pack, Model));
    }

    [Fact]
    public void Computed_not_seeded_and_not_stored()
    {
        var s = State();
        s.Pieces["u3"] = new PieceInstance
        {
            Id = "u3", MapId = "Main Map", Pos = new PointD(60, 40), TraitStates = new() { "8", "" },
            TypeString = PieceTypeParser.EncodeState(new[] { "propertysheet;0Cohesion;Sheet;", "piece;;;unit.svg;Sheeted" }),
        };
        var pack = Pack();
        Replace(pack, "coh", d => d with { SheetField = "Cohesion", Default = 50 });
        Assert.Null(LedgerSeeder.BuildSeeds(s, pack, _ => true, Model));
        Assert.Null(Ledger.Get(s, LedgerKey.Piece("u3"), "coh"));
        Assert.Equal(3, Get(s, pack, LedgerKey.Piece("u3"), "coh"));   // Defaults of its inputs, never its own Default
    }

    [Fact]
    public void Computed_error_is_named()
    {
        var s = State();
        s.Ledgers[LedgerKey.Piece("u1")]["mor"] = 0;
        var pack = Pack(new PropertyDef { Id = "ratio", Name = "Ratio", Scope = PropertyScope.Pieces, Expr = "Strength / Morale" });
        var ex = Assert.Throws<EvalError>(() => Get(s, pack, LedgerKey.Piece("u1"), "ratio"));
        Assert.Equal("Ratio: Division by zero", ex.Message);
        Assert.False(ex.MissingValue);
        Assert.False(PieceFilter.Matches("Ratio > 0", s.Pieces["u1"], s, Model, pack));

        // a missing input stays a missing value (a filter reads it as no match), named once by the innermost computed property
        var narrow = Pack(
            new PropertyDef { Id = "arm", Name = "Armour", Scope = PropertyScope.Pieces, AppliesTo = "name() == \"Tank\"" },
            new PropertyDef { Id = "prot", Name = "Protection", Scope = PropertyScope.Pieces, Expr = "Armour + 1" },
            new PropertyDef { Id = "outer", Name = "Outer", Scope = PropertyScope.Pieces, Expr = "Protection * 2" });
        var miss = Assert.Throws<EvalError>(() => Get(s, narrow, LedgerKey.Piece("u1"), "outer"));
        Assert.Equal("Protection: Armour has no value on this piece", miss.Message);
        Assert.True(miss.MissingValue);
    }

    [Fact]
    public void Computed_cycle_through_applies_to_is_error_not_overflow()
    {
        var s = State();
        // Alpha applies where Beta > 0, and Beta reads Alpha
        var pack = Pack(
            new PropertyDef { Id = "a", Name = "Alpha", Scope = PropertyScope.Pieces, Expr = "1", AppliesTo = "Beta > 0" },
            new PropertyDef { Id = "b", Name = "Beta", Scope = PropertyScope.Pieces, Expr = "Alpha + 1" });
        Assert.Null(Get(s, pack, LedgerKey.Piece("u1"), "a"));
        Assert.Throws<EvalError>(() => Get(s, pack, LedgerKey.Piece("u1"), "b"));
        Assert.False(PieceFilter.Matches("Alpha > 0 or Beta > 0", s.Pieces["u1"], s, Model, pack));
        Assert.False(PieceFilter.Matches("has(Alpha)", s.Pieces["u1"], s, Model, pack));

        // a direct cycle a broken pack slipped through
        var loop = Pack(
            new PropertyDef { Id = "x", Name = "Ex", Scope = PropertyScope.Pieces, Expr = "Why + 1" },
            new PropertyDef { Id = "y", Name = "Why", Scope = PropertyScope.Pieces, Expr = "Ex + 1" });
        var ex = Assert.Throws<EvalError>(() => Get(s, loop, LedgerKey.Piece("u1"), "x"));
        Assert.Equal("Ex is computed from itself", ex.Message);

        // depth > 16 without a cycle
        var chain = Enumerable.Range(0, 20).Select(i => new PropertyDef
        {
            Id = "c" + i, Name = "Step" + i, Scope = PropertyScope.Pieces, Expr = i == 19 ? "1" : $"Step{i + 1} + 1",
        }).ToArray();
        var deep = Pack(chain);
        var dx = Assert.Throws<EvalError>(() => Get(s, deep, LedgerKey.Piece("u1"), "c0"));
        Assert.EndsWith("is computed from itself", dx.Message);
        Assert.Equal(4, Get(s, deep, LedgerKey.Piece("u1"), "c16"));   // a short chain still works
    }

    [Fact]
    public void Computed_from_private_in_player_view_is_missing()
    {
        var pack = Pack();
        Replace(pack, "str", d => d with { Visibility = Visibility.Private });
        var full = State();
        full.Ledgers[LedgerKey.Piece("u1")].Remove("str");
        Assert.Equal(6, Get(full, pack, LedgerKey.Piece("u1"), "coh"));   // full state: the Default of Strength

        // a player's view of another side's piece: its Private Strength was removed
        var view = State();
        view.Ledgers[LedgerKey.Piece("u1")].Remove("str");
        view.IsPlayerView = true;
        view.ViewSides = new() { "blue" };
        var ex = Assert.Throws<EvalError>(() => Get(view, pack, LedgerKey.Piece("u1"), "coh"));
        Assert.True(ex.MissingValue);
        Assert.Equal("Cohesion: Strength has no value on this piece", ex.Message);
        Assert.False(PieceFilter.Matches("Cohesion >= 0", view.Pieces["u1"], view, Model, pack));
        Assert.Throws<EvalError>(() => PropertyValues.GetOrDefault(view, pack, Model, LedgerKey.Piece("u1"), Prop(pack, "coh")));
    }

    [Fact]
    public void Run_refuses_output_to_computed()
    {
        var (s, pack) = (State(), Pack());
        pack.Formulas.Add(new FormulaDef
        {
            Id = "f", Name = "Rally", Text = "1",
            Outputs = new()
            {
                new FormulaOutput { Target = OutputTarget.Unit, Property = "str", Op = OutputOp.Add },
                new FormulaOutput { Target = OutputTarget.Unit, Property = "coh", Op = OutputOp.Add },
            },
        });
        var runner = Runner(pack);
        var t = runner.Run(s, Request("f", "u1"));
        Assert.Equal("Formula \"Rally\": output 2 writes computed property \"Cohesion\"", t.Error);
        Assert.Empty(t.Outputs);
        Assert.Empty(runner.Commands(t, null));
    }

    [Fact]
    public void LedgerTable_shows_computed_and_error_cells()
    {
        var s = State();
        s.Ledgers[LedgerKey.Piece("u2")] = new() { ["mor"] = 0 };
        var pack = Pack(new PropertyDef { Id = "ratio", Name = "Ratio", Scope = PropertyScope.Pieces, Expr = "Strength / Morale" });
        var cols = LedgerTable.Columns(pack);
        int Col(string id) => cols.ToList().FindIndex(c => c.Id == id);
        var rows = LedgerTable.Rows(s, pack, Model, "");
        var rifle = rows.Single(r => r.Name == "Rifle");
        var scout = rows.Single(r => r.Name == "Scout");
        var depot = rows.Single(r => r.Name == "Depot");
        Assert.Equal(7, rifle.Values[Col("coh")]);
        Assert.Equal(0.75, rifle.Values[Col("ratio")]);
        Assert.Null(rifle.Errors);
        Assert.Null(scout.Values[Col("ratio")]);
        Assert.Equal("Ratio: Division by zero", scout.Errors![Col("ratio")]);
        Assert.Equal(90, depot.Values[Col("stock")]);
        Assert.Null(depot.Values[Col("coh")]);   // a unit property on a holder row
        Assert.Null(depot.Errors);
    }

    [Fact]
    public void Computed_breakdown_shows_expression_not_history()
    {
        var (s, pack) = (State(), Pack());
        var b = LedgerBreakdown.For(s, pack, Model, LedgerKey.Piece("u1"), "coh");
        Assert.Equal("Cohesion 7  (Rifle)", b.Title);
        Assert.Equal(new[] { "= Strength + Morale" }, b.Lines.Select(l => l.Label));
    }

    private static FormulaRunner Runner(RulesPack pack) => new(pack, Model, (n, _) => Enumerable.Repeat(1, n).ToList());

    private static RunRequest Request(string formula, string? piece) =>
        new(formula, piece, new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>());
}
