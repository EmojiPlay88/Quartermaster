namespace Quartermaster.Rules.Tests;

/// <summary>B8: the validator's former id, alliance and reactor-ally checks, and the alliance gating of B7's filter warnings.</summary>
public class B8ValidatorTests
{
    private static SideDef S(string id, string name, string? alliance = null, params string[] former) =>
        new() { Id = id, Name = name, UnitFilter = "Strength > 0", AllianceId = alliance, FormerIds = former.Length == 0 ? null : former.ToList() };

    private static RulesPack Base(params SideDef[] sides) => RulesPackIo.CreateEmpty("Riverland", "abc123") with
    {
        Id = "riverland",
        Properties = new()
        {
            new PropertyDef { Id = "p-strength", Name = "Strength", Scope = PropertyScope.Pieces, Visibility = Visibility.Public },
            new PropertyDef { Id = "p-secret", Name = "Secret", Scope = PropertyScope.Pieces, Visibility = Visibility.Private },
        },
        Sides = sides.ToList(),
    };

    private static List<string> Messages(RulesPack p, RulesIssueSeverity sev) =>
        RulesValidator.Validate(p).Where(i => i.Severity == sev).Select(i => i.Message).ToList();

    [Fact]
    public void Former_id_equal_to_own_id_error()
    {
        var p = Base(S("north", "Northern League", null, "north"), S("south", "Southern Compact"));
        Assert.Contains("Side \"Northern League\": former id \"north\" is the id of side \"Northern League\"", Messages(p, RulesIssueSeverity.Error));
    }

    [Fact]
    public void Former_id_equal_to_other_side_id_error()
    {
        var p = Base(S("north", "Northern League", null, "south"), S("south", "Southern Compact"));
        Assert.Contains("Side \"Northern League\": former id \"south\" is the id of side \"Southern Compact\"", Messages(p, RulesIssueSeverity.Error));
    }

    [Fact]
    public void Former_id_listed_twice_reported_on_later_side()
    {
        var p = Base(S("north", "Northern League", null, "league"), S("south", "Southern Compact", null, "league"));
        var errs = Messages(p, RulesIssueSeverity.Error);
        Assert.Contains("Side \"Southern Compact\": former id \"league\" is also a former id of side \"Northern League\"", errs);
        Assert.DoesNotContain(errs, m => m.StartsWith("Side \"Northern League\": former id"));
    }

    [Fact]
    public void Alliance_with_no_other_side_warning()
    {
        var p = Base(S("north", "Northern League", "Upriver Pact"), S("south", "Southern Compact", "Other"), S("hills", "Hill Clans", "other"));
        var w = Messages(p, RulesIssueSeverity.Warning);
        Assert.Contains("Side \"Northern League\": alliance \"Upriver Pact\" has no other side", w);
        Assert.DoesNotContain(w, m => m.StartsWith("Side \"Southern Compact\": alliance"));
        Assert.DoesNotContain(w, m => m.StartsWith("Side \"Hill Clans\": alliance"));
    }

    [Fact]
    public void Every_side_in_one_alliance_warning()
    {
        var p = Base(S("north", "Northern League", "Upriver Pact"), S("south", "Southern Compact", " upriver pact"));
        Assert.Contains("Every side is in alliance \"Upriver Pact\", so nothing is hidden from any player", Messages(p, RulesIssueSeverity.Warning));
        var one = Base(S("north", "Northern League", "Upriver Pact"));
        Assert.DoesNotContain(Messages(one, RulesIssueSeverity.Warning), m => m.StartsWith("Every side"));
    }

    [Fact]
    public void Reacting_side_ally_of_phase_side_warning()
    {
        var p = Base(S("north", "Northern League", "Upriver Pact"), S("hills", "Hill Clans", "Upriver Pact"), S("south", "Southern Compact")) with
        {
            Phases = new() { new PhaseDef { Id = "ph1", Name = "March", SideId = "north", Reactors = new() { "hills", "south" } } },
        };
        var w = Messages(p, RulesIssueSeverity.Warning);
        Assert.Contains("Phase \"March\": reacting side \"Hill Clans\" is an ally of the phase's side, so it already acts in this phase", w);
        Assert.DoesNotContain(w, m => m.Contains("reacting side \"Southern Compact\""));
    }

    private static RulesPack WithFilters(params SideDef[] sides) => Base(sides) with
    {
        Tables = new() { new TableDef { Id = "t-sides", Name = "Sides", Kind = TableKind.OneD, RowKeys = new() { "Northern League" }, Cells = new() { new() { "1" } } } },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "f-bid", Name = "Bid", Text = "other",
                Bindings = new()
                {
                    new VariableBinding { Variable = "other", Kind = BindingKind.Input, ChoicesTable = "t-sides", ChoicesFilter = "Secret > 0" },
                    new VariableBinding { Variable = "pick", Kind = BindingKind.Piece, Property = "p-strength", PieceFilter = "Secret > 0" },
                },
            },
        },
    };

    private static bool HasFilterWarning(RulesPack p) =>
        Messages(p, RulesIssueSeverity.Warning).Any(m => m.Contains("choices filter reads values another side may not see") || m.Contains("piece filter reads values another side may not see"));

    [Fact]
    public void Filter_warnings_silent_when_all_sides_allied()
    {
        Assert.True(HasFilterWarning(WithFilters(S("north", "Northern League"), S("south", "Southern Compact"))));
        Assert.False(HasFilterWarning(WithFilters(S("north", "Northern League", "A"), S("south", "Southern Compact", "A"))));
        var w = Messages(WithFilters(S("north", "Northern League", "A"), S("south", "Southern Compact", "A")), RulesIssueSeverity.Warning);
        Assert.Contains(w, m => m.StartsWith("Every side is in alliance"));
        Assert.True(HasFilterWarning(WithFilters(S("north", "Northern League", "A"), S("hills", "Hill Clans", "A"), S("south", "Southern Compact"))));
    }

    [Fact]
    public void Valid_b8_pack_has_no_issues()
    {
        var p = Base(S("east", "East Shore", "Upriver Pact", "league"), S("hills", "Hill Clans", "Upriver Pact"), S("south", "Southern Compact")) with
        {
            Phases = new() { new PhaseDef { Id = "ph1", Name = "March", SideId = "east", Reactors = new() { "south" } } },
        };
        Assert.Empty(RulesValidator.Validate(p));
    }

    private static RulesPack OldFixture() => RulesPackIo.CreateEmpty("Riverland", "abc123") with
    {
        Id = "riverland",
        Properties = new()
        {
            new PropertyDef { Id = "p-strength", Name = "Strength", Scope = PropertyScope.Pieces, Visibility = Visibility.Public },
            new PropertyDef { Id = "p-secret", Name = "Secret", Scope = PropertyScope.Pieces, Visibility = Visibility.Private },
        },
        Sides = new()
        {
            new SideDef { Id = "north", Name = "Northern League", UnitFilter = "Strength > 0" },
            new SideDef { Id = "south", Name = "Southern Compact" },
            new SideDef { Id = "hills", Name = "Hill Clans", UnitFilter = "Strength > 1" },
        },
        Phases = new()
        {
            new PhaseDef { Id = "ph1", Name = "Toll" },
            new PhaseDef { Id = "ph2", Name = "March", SideId = "north", Reactors = new() { "south", "ghost", "north" } },
            new PhaseDef { Id = "ph3", Name = "Raid", SideId = "hills" },
        },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "f-pick", Name = "Pick", Text = "x",
                Bindings = new() { new VariableBinding { Variable = "x", Kind = BindingKind.Piece, PieceFilter = "Secret > 0" } },
            },
        },
    };

    [Fact]
    public void Old_fixture_issue_list_unchanged()
    {
        // Captured on the B7 merge head (before any B8 code).
        var expected = new[]
        {
            "Error|Formula \"Pick\": variable \"x\" has no property chosen",
            "Warning|Formula \"Pick\": variable \"x\": its piece filter reads values another side may not see; each player picks from what they can see",
            "Warning|Side \"Southern Compact\": unit filter is empty, so it matches every unit",
            "Error|Phase \"March\": reacting side \"ghost\" does not exist",
            "Warning|Phase \"March\" lists its own side as reacting",
        };
        Assert.Equal(expected, RulesValidator.Validate(OldFixture()).Select(i => $"{i.Severity}|{i.Message}").ToArray());
    }
}
