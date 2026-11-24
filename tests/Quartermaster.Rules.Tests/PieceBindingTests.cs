using System.Text.Json;

namespace Quartermaster.Rules.Tests;

public class PieceBindingTests
{
    private static PropertyDef P(string id, string name, Visibility vis = Visibility.Public, PropertyScope scope = PropertyScope.Pieces, string? expr = null)
        => new() { Id = id, Name = name, Visibility = vis, Scope = scope, Expr = expr };

    private static VariableBinding Pb(string variable, string? property, string? filter = null)
        => new() { Variable = variable, Kind = BindingKind.Piece, Property = property, PieceFilter = filter };

    private static RulesPack PackWith(VariableBinding b, params PropertyDef[] props)
        => new() { Properties = props.ToList(), Formulas = new() { new FormulaDef { Id = "f1", Name = "Compare", Text = "M", Bindings = new() { b } } } };

    private static List<string> Errors(RulesPack pack) =>
        RulesValidator.Validate(pack).Where(i => i.Severity == RulesIssueSeverity.Error).Select(i => i.Message).ToList();

    [Fact]
    public void PieceFilter_round_trips_and_absent_keeps_bytes()
    {
        var plain = new VariableBinding { Variable = "A", Kind = BindingKind.Input };
        Assert.DoesNotContain("pieceFilter", JsonSerializer.Serialize(plain, RulesPackIo.JsonOptions));
        var pack = PackWith(Pb("M", "m", "Morale"), P("m", "Morale"));
        Assert.Contains("\"pieceFilter\": \"Morale\"", JsonSerializer.Serialize(pack.Formulas[0].Bindings[0], RulesPackIo.JsonOptions));
        var path = Path.Combine(Path.GetTempPath(), "qm-piece-" + Guid.NewGuid().ToString("N") + ".qmrules");
        try
        {
            RulesPackIo.Save(pack with { Format = RulesPackIo.CurrentFormat }, path);
            Assert.Equal("Morale", RulesPackIo.Load(path).Formulas[0].Bindings[0].PieceFilter);
            var blank = PackWith(Pb("M", "m", "  "), P("m", "Morale"));
            RulesPackIo.Save(blank with { Format = RulesPackIo.CurrentFormat }, path);
            var b = RulesPackIo.Load(path).Formulas[0].Bindings[0];
            Assert.Equal(BindingKind.Piece, b.Kind);
            Assert.Null(b.PieceFilter);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Validator_unknown_property()
    {
        var errors = Errors(PackWith(Pb("M", "nope"), P("m", "Morale")));
        Assert.Contains("Formula \"Compare\": variable \"M\" reads unknown property \"nope\"", errors);
    }

    [Fact]
    public void Validator_holder_property()
    {
        var errors = Errors(PackWith(Pb("M", "h"), P("h", "Supply", scope: PropertyScope.Holders)));
        Assert.Contains("Formula \"Compare\": variable \"M\" reads holder property \"Supply\"", errors);
        Assert.DoesNotContain(Errors(PackWith(Pb("M", "b"), P("b", "Both", scope: PropertyScope.Both))), e => e.Contains("holder property"));
    }

    [Fact]
    public void Validator_filter_does_not_parse()
    {
        var errors = Errors(PackWith(Pb("M", "m", "Morale >"), P("m", "Morale")));
        Assert.Contains(errors, e => e.StartsWith("Formula \"Compare\": variable \"M\" \"which pieces\" filter does not parse: "));
        Assert.DoesNotContain(Errors(PackWith(Pb("M", "m", "Morale > 1"), P("m", "Morale"))), e => e.Contains("filter does not parse"));
    }

    [Fact]
    public void Hidden_when_property_not_public()
    {
        var props = new[]
        {
            P("priv", "Secret", Visibility.Private),
            P("pub", "Open"),
            P("calc", "Calc", expr: "Secret * 2"),   // Public, computed from a Private property
        };
        var f = new FormulaDef
        {
            Id = "f", Name = "F", Text = "A",
            Bindings = new() { Pb("A", "priv"), Pb("B", "pub"), Pb("C", "calc"), Pb("D", "gone"),
                new VariableBinding { Variable = "E", Kind = BindingKind.Unit, Property = "priv" } },
        };
        var pack = new RulesPack { Properties = props.ToList(), Formulas = new() { f } };
        Assert.Equal(new[] { "A", "C", "D" }, FormulaVariables.PieceBindingsHidden(pack, f));
        var open = f with { Bindings = new() { Pb("B", "pub") } };
        Assert.Empty(FormulaVariables.PieceBindingsHidden(pack, open));
    }
}
