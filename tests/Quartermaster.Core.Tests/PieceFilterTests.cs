using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class PieceFilterTests
{
    private static PieceInstance Marked()
    {
        var type = SequenceEncoder.Encode(new[] { "mark;" + SequenceEncoder.Encode(new[] { "Side" }, ','), "piece;;;unit.svg;Rifle" }, '\t');
        return new PieceInstance { Id = "m1", TypeString = type, MapId = "Main Map", TraitStates = new() { "Red", "" } };
    }

    private static bool M(string? expr, PieceInstance p, GameState s) =>
        PieceFilter.Matches(expr, p, s, RunnerFx.Model, RunnerFx.Pack());

    [Fact]
    public void PieceFilter_MarkerAndName()
    {
        var s = RunnerFx.State();
        var p = Marked();
        s.Pieces[p.Id] = p;
        Assert.True(M(null, p, s));
        Assert.True(M("", p, s));
        Assert.True(M("marker(\"Side\") == \"Red\"", p, s));
        Assert.False(M("marker(\"Side\") == \"Blue\"", p, s));
        Assert.True(M("name() == \"Rifle\"", p, s));
        Assert.False(M("has(Fuel)", p, s));
        s.Ledgers[LedgerKey.Piece("m1")] = new() { ["fuel"] = 5 };
        Assert.True(M("has(Fuel) and Fuel > 3", p, s));
        Assert.False(M("Supply > 3", p, s));
    }

    [Fact]
    public void PieceFilter_BadExpression_False()
    {
        var s = RunnerFx.State();
        var p = Marked();
        Assert.False(M("((", p, s));
        Assert.False(M("marker(\"Nope\") == \"x\"", p, s));
        Assert.False(M("Missing + 1", p, s));
    }

    [Fact]
    public void FormulasFor_OrderedAndFiltered()
    {
        var pack = RunnerFx.Pack();
        pack.Formulas.Add(new FormulaDef { Id = "a", Name = "Alpha", Text = "1", Bindings = new() { new VariableBinding { Variable = "X", Kind = BindingKind.Unit, Property = "fuel" } } });
        pack.Formulas.Add(new FormulaDef { Id = "g", Name = "Global only", Text = "1" });
        pack.Formulas.Add(new FormulaDef { Id = "r", Name = "Red only", Text = "1", AppliesTo = "marker(\"Side\") == \"Red\"",
            Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Op = OutputOp.Add } } });
        var s = RunnerFx.State();
        var red = Marked();
        var names = PieceFilter.FormulasFor(pack, red, s, RunnerFx.Model).Select(f => f.Name).ToList();
        Assert.Equal(new[] { "Alpha", "Fuel Use", "Red only" }, names);
        var plain = s.Pieces["u1"];
        Assert.Equal(new[] { "Alpha", "Fuel Use" }, PieceFilter.FormulasFor(pack, plain, s, RunnerFx.Model).Select(f => f.Name).ToList());
    }
}
