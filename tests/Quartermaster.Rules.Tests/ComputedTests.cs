using System.Text.Json;
using Quartermaster.Rules.Expr;

namespace Quartermaster.Rules.Tests;

public class ComputedTests
{
    private static PropertyDef P(string name, string? expr = null, Visibility vis = Visibility.Public, PropertyScope scope = PropertyScope.Pieces)
        => new() { Id = "id" + name, Name = name, Expr = expr, Visibility = vis, Scope = scope };

    private static RulesPack Pack(params PropertyDef[] props) => new() { Properties = props.ToList() };

    private static string Error(string text) => Assert.Throws<ParseError>(() => Parser.Parse(text)).Detail;

    private static List<RulesIssue> Issues(RulesPack pack) => RulesValidator.Validate(pack).ToList();

    [Fact]
    public void Attached_parses_like_hex()
    {
        foreach (var (hex, att) in new[] { ("hexsum", "attachedsum"), ("hexcount", "attachedcount"), ("hexmax", "attachedmax"), ("hexmin", "attachedmin") })
        {
            foreach (var args in new[] { "", "Trucks", "Trucks, \"a\"", "\"a\"", "Trucks, Fuel", "1", "Trucks, \"a\", \"b\"", "\"a\", \"b\"" })
            {
                string Outcome(string fn) { try { Parser.Parse($"{fn}({args})"); return "ok"; } catch (ParseError e) { return e.Detail.Replace(fn, "FN"); } }
                Assert.Equal(Outcome(hex), Outcome(att));
            }
        }
        Assert.Equal("'attachedsum' takes 1 to 2 arguments, not 0", Error("attachedsum()"));
    }

    [Theory]
    [InlineData("attachedsum(Fuel) > 1")]
    [InlineData("attachedcount() > 1")]
    [InlineData("attachedmax(Fuel) > 1")]
    [InlineData("attachedmin(Fuel, \"\") > 1")]
    public void Attached_forbidden_in_phase_condition(string condition)
    {
        var pack = Pack(P("Fuel", scope: PropertyScope.Both)) with { Phases = new() { new PhaseDef { Id = "ph", Name = "Move", Condition = condition } } };
        var fn = condition[..condition.IndexOf('(')];
        Assert.Contains(Issues(pack), i => i.Severity == RulesIssueSeverity.Error && i.Message == $"Phase \"Move\": condition may not use {fn}()");
    }

    [Fact]
    public void Attached_count_is_count_aggregate()
    {
        Assert.True(ExprFacts.IsCountAggregate("attachedcount"));
        Assert.False(ExprFacts.IsCountAggregate("attachedsum"));
        foreach (var fn in new[] { "attachedsum", "attachedcount", "attachedmax", "attachedmin" })
        {
            Assert.True(ExprFacts.IsAggregate(fn));
            Assert.True(ExprFacts.IsAttachedAggregate(fn));
            Assert.True(ExprFacts.NeedsPiece(fn));
        }
        Assert.True(ExprFacts.NeedsPiece("hexsum"));
        Assert.False(ExprFacts.NeedsPiece("sum"));
        Assert.False(ExprFacts.IsGameLevel(Parser.Parse("attachedcount() > 1"), Pack()));
    }

    [Fact]
    public void Expr_round_trips_and_absent_keeps_hash()
    {
        var plain = Pack(P("Fuel"));
        var json = JsonSerializer.Serialize(plain.Properties[0], RulesPackIo.JsonOptions);
        Assert.DoesNotContain("expr", json);
        var withExpr = Pack(P("Fuel"), P("Double", "Fuel * 2"));
        Assert.Contains("\"expr\": \"Fuel * 2\"", JsonSerializer.Serialize(withExpr.Properties[1], RulesPackIo.JsonOptions));
        var path = Path.Combine(Path.GetTempPath(), "qm-expr-" + Guid.NewGuid().ToString("N") + ".qmrules");
        try
        {
            RulesPackIo.Save(withExpr with { Format = RulesPackIo.CurrentFormat }, path);
            var loaded = RulesPackIo.Load(path);
            Assert.Null(loaded.PropertyByName("Fuel")!.Expr);
            Assert.Equal("Fuel * 2", loaded.PropertyByName("Double")!.Expr);
        }
        finally { File.Delete(path); }
        // Blank text is no expression.
        Assert.False(PropertyFacts.IsComputed(P("X", "  ")));
        Assert.True(PropertyFacts.IsComputed(P("X", "1")));
    }

    [Fact]
    public void IsPublic_true_for_public_from_public()
    {
        var pack = Pack(P("Fuel"), P("Double", "Fuel * 2"), P("Hidden", vis: Visibility.Private));
        Assert.True(PropertyFacts.IsPublic(pack, pack.Properties[0]));
        Assert.True(PropertyFacts.IsPublic(pack, pack.Properties[1]));
        Assert.False(PropertyFacts.ReadsHidden(pack, pack.Properties[1]));
        Assert.False(PropertyFacts.IsPublic(pack, pack.Properties[2]));
        Assert.False(PropertyFacts.IsPublic(pack, null));
    }

    [Fact]
    public void IsPublic_false_when_reading_private()
    {
        var pack = Pack(P("Secret", vis: Visibility.Private), P("Shown", "Secret + 1"));
        Assert.True(PropertyFacts.ReadsHidden(pack, pack.Properties[1]));
        Assert.False(PropertyFacts.IsPublic(pack, pack.Properties[1]));
    }

    [Fact]
    public void IsPublic_false_for_holder_aggregate_and_unparsable()
    {
        var pack = Pack(P("Fuel"), P("H", "holder(Depot, Fuel)"), P("A", "sum(Fuel, \"\")"), P("C", "attachedcount()"), P("Bad", "Fuel +"));
        foreach (var p in pack.Properties.Skip(1))
        {
            Assert.True(PropertyFacts.ReadsHidden(pack, p), p.Name);
            Assert.False(PropertyFacts.IsPublic(pack, p), p.Name);
        }
    }

    [Fact]
    public void ReadsHidden_through_chain_and_cycle_safe()
    {
        var pack = Pack(P("Secret", vis: Visibility.Private), P("Mid", "Secret"), P("Top", "Mid + 1"), P("Clean", "1"),
            P("A", "B"), P("B", "A"));
        Assert.True(PropertyFacts.ReadsHidden(pack, pack.Properties[2]));
        Assert.False(PropertyFacts.IsPublic(pack, pack.Properties[2]));
        Assert.False(PropertyFacts.ReadsHidden(pack, pack.Properties[3]));
        Assert.True(PropertyFacts.ReadsHidden(pack, pack.Properties[4]));   // cycle: fail closed, no overflow
        Assert.False(PropertyFacts.IsPublic(pack, pack.Properties[5]));
    }

    [Fact]
    public void CycleFrom_reports_closed_path()
    {
        var pack = Pack(P("A", "B"), P("B", "A + 1"), P("C", "A"), P("Fuel"), P("S", "S"));
        Assert.Equal(new[] { "A", "B", "A" }, PropertyFacts.CycleFrom(pack, pack.Properties[0]));
        Assert.Equal(new[] { "B", "A", "B" }, PropertyFacts.CycleFrom(pack, pack.Properties[1]));
        Assert.Null(PropertyFacts.CycleFrom(pack, pack.Properties[2]));   // reaches a cycle but is not on it
        Assert.Null(PropertyFacts.CycleFrom(pack, pack.Properties[3]));
        Assert.Equal(new[] { "S", "S" }, PropertyFacts.CycleFrom(pack, pack.Properties[4]));
    }

    [Fact]
    public void Validator_expr_does_not_parse()
    {
        var issue = Assert.Single(Issues(Pack(P("Bad", "1 +"))), i => i.Severity == RulesIssueSeverity.Error);
        Assert.StartsWith("Property \"Bad\": the expression does not parse: ", issue.Message);
        Assert.Equal("idBad", issue.EntityId);
    }

    [Fact]
    public void Validator_expr_unknown_names()
    {
        var pack = Pack(P("Fuel"), P("Reach", "Fuell * 2 + lookup(Nowhere, Fuel) + holder(Depot, Fuel) + sum(Speed, \"Weight > 1\")"));
        var errors = Issues(pack).Where(i => i.Severity == RulesIssueSeverity.Error && i.EntityId == "idReach").Select(i => i.Message).ToList();
        Assert.Contains("Property \"Reach\": refers to unknown property \"Fuell\"", errors);
        Assert.Contains("Property \"Reach\": refers to unknown table \"Nowhere\"", errors);
        Assert.Contains("Property \"Reach\": refers to unknown holder \"Depot\"", errors);
        Assert.Contains("Property \"Reach\": sum() names unknown property \"Speed\"", errors);
        Assert.Contains("Property \"Reach\": the filter of sum() refers to unknown property \"Weight\"", errors);
        Assert.DoesNotContain(Issues(Pack(P("Fuel"), P("Fine", "Fuel * 2"))), i => i.Severity == RulesIssueSeverity.Error);
    }

    [Fact]
    public void Validator_cycle_text()
    {
        var errors = Issues(Pack(P("A", "B"), P("B", "A"))).Where(i => i.Severity == RulesIssueSeverity.Error).ToList();
        var e = Assert.Single(errors);   // reported once
        Assert.Equal("Property \"A\" is computed from itself: A → B → A", e.Message);
        Assert.Equal("idA", e.EntityId);
    }

    [Fact]
    public void Validator_output_writes_computed()
    {
        var pack = Pack(P("Fuel"), P("Double", "Fuel * 2")) with
        {
            Formulas = new()
            {
                new FormulaDef
                {
                    Id = "f1", Name = "Burn", Text = "1",
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "idFuel", Op = OutputOp.Set },
                                      new FormulaOutput { Target = OutputTarget.Unit, Property = "idDouble", Op = OutputOp.Set } },
                },
            },
        };
        Assert.Contains(Issues(pack), i => i.Severity == RulesIssueSeverity.Error && i.Message == "Formula \"Burn\": output 2 writes computed property \"Double\"");
        Assert.DoesNotContain(Issues(pack), i => i.Message.Contains("output 1 writes"));
    }

    [Fact]
    public void Validator_default_ignored_warning()
    {
        var pack = Pack(P("Fuel"), P("Double", "Fuel * 2") with { Default = 5 }, P("Sheet", "Fuel") with { SheetField = "Row" }, P("Fine", "Fuel"));
        var w = Issues(pack).Where(i => i.Severity == RulesIssueSeverity.Warning && i.Message.EndsWith("its Default and Sheet field are ignored")).ToList();
        Assert.Equal(new[] { "Property \"Double\" is computed; its Default and Sheet field are ignored", "Property \"Sheet\" is computed; its Default and Sheet field are ignored" },
            w.Select(i => i.Message));
    }

    [Fact]
    public void Validator_public_from_hidden_warning()
    {
        var pack = Pack(P("Secret", vis: Visibility.Private), P("Shown", "Secret + 1"), P("Quiet", "Secret", Visibility.Private), P("Ok", "1"));
        var w = Assert.Single(Issues(pack), i => i.Message.Contains("computed from hidden data"));
        Assert.Equal(RulesIssueSeverity.Warning, w.Severity);
        Assert.Equal("Property \"Shown\" is Public but is computed from hidden data; players who do not hold every side see it as Private", w.Message);
    }
}
