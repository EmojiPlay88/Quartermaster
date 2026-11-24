using System.Text.Json;
using Quartermaster.Rules.Tables;

namespace Quartermaster.Rules.Tests;

public class CellOverrideTests
{
    private static TableDef Two() => new() { Id = "t2", Name = "Units", Kind = TableKind.TwoD, RowKeys = { "Raft A" }, ColumnKeys = { "Speed", "Load  Cap" }, Cells = { new() { "3", "4" } } };
    private static TableDef One() => new() { Id = "t1", Name = "Costs", Kind = TableKind.OneD, RowKeys = { "Raft A" }, ColumnKeys = { "Cost" }, Cells = { new() { "5" } } };

    private static PropertyDef P(string name, string? table = null, string? col = null, Visibility vis = Visibility.Public, PropertyScope scope = PropertyScope.Pieces)
        => new() { Id = "id" + name, Name = name, OverridesTable = table, OverridesColumn = col, Visibility = vis, Scope = scope };

    private static RulesPack Pack(params PropertyDef[] props) => new() { Properties = props.ToList(), Tables = { Two(), One() } };

    private static List<RulesIssue> Issues(RulesPack pack) => RulesValidator.Validate(pack).ToList();

    [Fact]
    public void Fields_round_trip_and_absent_keep_bytes()
    {
        var plain = P("Speed");
        var json = JsonSerializer.Serialize(plain, RulesPackIo.JsonOptions);
        Assert.DoesNotContain("overridesTable", json);
        Assert.DoesNotContain("overridesColumn", json);
        Assert.Contains("\"overridesTable\": \"t2\"", JsonSerializer.Serialize(P("S", "t2", "Speed"), RulesPackIo.JsonOptions));

        var pack = (Pack(P("Plain"), P("Spd", "t2", "Speed"), P("Blank", "  ", "  "), P("OneD", "t1", " ")) with { Format = RulesPackIo.CurrentFormat });
        var path = Path.Combine(Path.GetTempPath(), "qm-ovr-" + Guid.NewGuid().ToString("N") + ".qmrules");
        try
        {
            RulesPackIo.Save(pack, path);
            var l = RulesPackIo.Load(path);
            Assert.Null(l.PropertyByName("Plain")!.OverridesTable);
            Assert.Equal("t2", l.PropertyByName("Spd")!.OverridesTable);
            Assert.Equal("Speed", l.PropertyByName("Spd")!.OverridesColumn);
            Assert.Null(l.PropertyByName("Blank")!.OverridesTable);
            Assert.Null(l.PropertyByName("Blank")!.OverridesColumn);
            Assert.Equal("t1", l.PropertyByName("OneD")!.OverridesTable);
            Assert.Null(l.PropertyByName("OneD")!.OverridesColumn);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Validator_missing_table()
    {
        var pack = Pack(P("Spd", "gone", "Speed"));
        Assert.Contains(Issues(pack), i => i.Severity == RulesIssueSeverity.Error && i.Message == "Property \"Spd\" overrides a table that no longer exists");
    }

    [Fact]
    public void Validator_missing_column()
    {
        var pack = Pack(P("Spd", "t2", "Nope"), P("Cap", "t2", "load cap"), P("Cap2", "t2", " Load Cap "), P("Cost", "t1", "anything"), P("None", "t2"));
        var issues = Issues(pack);
        Assert.Contains(issues, i => i.Severity == RulesIssueSeverity.Error && i.Message == "Property \"Spd\" overrides column \"Nope\", which table \"Units\" does not have");
        Assert.DoesNotContain(issues, i => i.Message.Contains("\"Cost\" overrides column"));
        Assert.Contains(issues, i => i.Message.StartsWith("Property \"None\" overrides column"));
        // Keys compare as table keys (whitespace-normalised, case-sensitive).
        Assert.Contains(issues, i => i.Message.StartsWith("Property \"Cap\" overrides column"));
        Assert.DoesNotContain(issues, i => i.Message.StartsWith("Property \"Cap2\" overrides column"));
    }

    [Fact]
    public void Validator_holder_scope()
    {
        var pack = Pack(P("H", "t2", "Speed", scope: PropertyScope.Holders), P("B", "t2", "Speed", scope: PropertyScope.Both), P("Pc", "t2", "Speed"));
        var issues = Issues(pack);
        Assert.Contains(issues, i => i.Severity == RulesIssueSeverity.Error && i.Message == "Property \"H\" overrides a table but is not a piece property");
        Assert.DoesNotContain(issues, i => i.Message.Contains("\"B\" overrides a table but"));
        Assert.DoesNotContain(issues, i => i.Message.Contains("\"Pc\" overrides a table but"));
    }

    [Fact]
    public void Validator_duplicate_warning()
    {
        var pack = Pack(P("A", "t2", "Speed"), P("B", "t2", "Speed"), P("C", "t2", "Load Cap"), P("D", "t1"), P("E", "t1"));
        var issues = Issues(pack);
        Assert.Contains(issues, i => i.Severity == RulesIssueSeverity.Warning && i.Message == "Properties \"A\" and \"B\" both override Units, column Speed; \"A\" wins");
        Assert.Contains(issues, i => i.Severity == RulesIssueSeverity.Warning && i.Message == "Properties \"D\" and \"E\" both override Costs; \"D\" wins");
        Assert.Equal(2, issues.Count(i => i.Message.Contains("both override")));
    }

    [Fact]
    public void For_orders_by_pack_and_ignores_column_on_1d()
    {
        var pack = Pack(P("A", "t2", "Speed"), P("B", "t1", "x"), P("C", "t2", "Load Cap"), P("D", "t2", "Speed"), P("E", "t1"));
        Assert.Equal(new[] { "A", "D" }, CellOverrideFacts.For(pack, "t2", "Speed").Select(p => p.Name));
        Assert.Equal(new[] { "C" }, CellOverrideFacts.For(pack, "t2", " Load   Cap").Select(p => p.Name));
        Assert.Empty(CellOverrideFacts.For(pack, "t2", null));
        Assert.Equal(new[] { "B", "E" }, CellOverrideFacts.For(pack, "t1", "whatever").Select(p => p.Name));
        Assert.Equal(new[] { "B", "E" }, CellOverrideFacts.For(pack, "t1", null).Select(p => p.Name));
        Assert.Empty(CellOverrideFacts.For(pack, "nope", "Speed"));
    }

    [Fact]
    public void HiddenOverride_true_for_private()
    {
        var pack = Pack(P("A", "t2", "Speed"), P("B", "t1", vis: Visibility.Private));
        Assert.False(CellOverrideFacts.HiddenOverride(pack, "t2"));
        Assert.True(CellOverrideFacts.HiddenOverride(pack, "t1"));
        Assert.False(CellOverrideFacts.HiddenOverride(pack, "other"));
    }

    [Fact]
    public void For_and_HiddenOverride_skip_holder_scope()
    {
        var pack = Pack(P("H", "t2", "Speed", scope: PropertyScope.Holders), P("B", "t2", "Speed", scope: PropertyScope.Both),
            P("Hp", "t1", vis: Visibility.Private, scope: PropertyScope.Holders));
        Assert.Equal(new[] { "B" }, CellOverrideFacts.For(pack, "t2", "Speed").Select(p => p.Name));
        Assert.Empty(CellOverrideFacts.For(pack, "t1", null));
        Assert.False(CellOverrideFacts.HiddenOverride(pack, "t1"));
        Assert.DoesNotContain(Issues(pack), i => i.Message.Contains("both override"));
    }
}
