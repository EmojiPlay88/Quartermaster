using Quartermaster.Rules;

namespace Quartermaster.Rules.Tests;

public class FormulaVariablesTests
{
    private static VariableBinding In(string v) => new() { Variable = v, Kind = BindingKind.Input };
    private static FormulaDef F(string text, params VariableBinding[] b) => new() { Id = "f", Name = "F", Text = text, Bindings = b.ToList() };

    [Fact]
    public void Used_unguarded_formula_unchanged()
    {
        var t = new VariableBinding { Variable = "T", Kind = BindingKind.Table, TableId = "t", RowExpr = "R", ColExpr = "C" };
        Assert.Equal(new[] { "A", "T", "R", "C" }, FormulaVariables.Used(F("A + T", In("A"), t, In("R"), In("C"))));
    }

    [Fact]
    public void Used_includes_precondition_variables()
    {
        var f = F("A", In("A"), In("P")) with { Precondition = "P > 0" };
        Assert.Equal(new[] { "A", "P" }, FormulaVariables.Used(f));
    }

    [Fact]
    public void Used_includes_when_of_used_bindings_transitively_not_of_unused()
    {
        var a = In("A") with { When = "W > 0" };
        var w = In("W") with { When = "V > 0" };
        var unusedB = In("U") with { When = "Z > 0" };
        Assert.Equal(new[] { "A", "W", "V" }, FormulaVariables.Used(F("A", a, w, In("V"), unusedB, In("Z"))));
    }

    [Fact]
    public void Used_follows_table_expressions_of_guard_only_bindings()
    {
        var t = new VariableBinding { Variable = "T", Kind = BindingKind.Table, TableId = "t", RowExpr = "R" };
        var f = F("A", In("A"), t, In("R")) with { Precondition = "T > 0" };
        Assert.Equal(new[] { "A", "T", "R" }, FormulaVariables.Used(f));
    }
}
