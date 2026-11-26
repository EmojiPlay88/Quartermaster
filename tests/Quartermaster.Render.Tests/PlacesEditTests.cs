using Quartermaster.Render.Workbench;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

/// <summary>B6 Workbench edit helpers: Keyed by, output Key, and the issues list checked against the module. Invented Riverland names.</summary>
public class PlacesEditTests
{
    [Fact]
    public void WithKeyedBy_blank_null()
    {
        var p = new PropertyDef { Id = "p", Name = "Ford depth", KeyedBy = "hex" };
        Assert.Null(PropertyEdit.WithKeyedBy(p, "  ").KeyedBy);
        Assert.Null(PropertyEdit.WithKeyedBy(p, null).KeyedBy);
        Assert.Equal("table:Hull types", PropertyEdit.WithKeyedBy(p, " table:Hull types ").KeyedBy);   // trimmed, case kept
        Assert.Equal("Turn", PropertyEdit.WithKeyedBy(p, "Turn").KeyedBy);
        Assert.Equal("Ford depth", PropertyEdit.WithKeyedBy(p, "turn").Name);
    }

    [Fact]
    public void WithKey_blank_null()
    {
        var o = new FormulaOutput { Target = OutputTarget.Global, Property = "Ford depth", Key = "hex()" };
        Assert.Null(FormulaEdit.WithKey(o, " ").Key);
        Assert.Null(FormulaEdit.WithKey(o, null).Key);
        Assert.Equal("turn() + 2", FormulaEdit.WithKey(o, " turn() + 2 ").Key);
    }

    [Fact]
    public void WithOutputAction_clears_key()
    {
        var o = new FormulaOutput { Target = OutputTarget.Global, Property = "Ford depth", Key = "hex()" };
        Assert.Equal("hex()", FormulaEdit.WithOutputAction(o, OutputAction.Write).Key);
        Assert.Null(FormulaEdit.WithOutputAction(o, OutputAction.Remove).Key);
        Assert.Null(FormulaEdit.WithOutputAction(o, OutputAction.Place).Key);
    }

    [Fact]
    public void WorkbenchRules_issues_with_model_checks_hex_constants()
    {
        var pack = PlacesFixture.Pack() with
        {
            Formulas = new() { new FormulaDef { Id = "f", Name = "Probe", Text = "keyed(FordDepth, \"zzz\")" } },
        };
        const string text = "Formula \"Probe\": keyed() key \"zzz\" is not a hex of any board";
        var with = WorkbenchRules.Issues(pack, [], null, PlacesFixture.Model);
        Assert.Contains(with, r => r.Problem.Contains(text));
        Assert.DoesNotContain(WorkbenchRules.Issues(pack, [], null), r => r.Problem.Contains("is not a hex"));
        var good = pack with { Formulas = new() { new FormulaDef { Id = "f", Name = "Probe", Text = "keyed(FordDepth, \"0412\")" } } };
        Assert.DoesNotContain(WorkbenchRules.Issues(good, [], null, PlacesFixture.Model), r => r.Problem.Contains("is not a hex"));
    }
}
