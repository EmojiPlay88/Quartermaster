using Quartermaster.Render.Workbench;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

public class WorkbenchRulesTests
{
    static RulesPack Pack(params object[] items)
    {
        var p = new RulesPack { Id = "p", Name = "Demo" };
        foreach (var i in items)
            p = i switch
            {
                PropertyDef x => p with { Properties = p.Properties.Append(x).ToList() },
                HolderDef x => p with { Holders = p.Holders.Append(x).ToList() },
                TableDef x => p with { Tables = p.Tables.Append(x).ToList() },
                FormulaDef x => p with { Formulas = p.Formulas.Append(x).ToList() },
                PhaseDef x => p with { Phases = p.Phases.Append(x).ToList() },
                AlertDef x => p with { Alerts = p.Alerts.Append(x).ToList() },
                MapOwner x => p with { MapOwners = p.MapOwners.Append(x).ToList() },
                RuleRef x => p with { RuleRefs = p.RuleRefs.Append(x).ToList() },
                _ => throw new ArgumentException(),
            };
        return p;
    }

    [Fact]
    public void Title_variants()
    {
        Assert.Equal("Rules Workbench", WorkbenchRules.Title(null, false));
        Assert.Equal("Rules Workbench", WorkbenchRules.Title(null, true));
        Assert.Equal("Rules Workbench — Demo", WorkbenchRules.Title(Pack(), false));
        Assert.Equal("Rules Workbench — Demo (read-only)", WorkbenchRules.Title(Pack(), true));
    }

    [Fact]
    public void Issues_empty_pack_gives_no_problems_row()
    {
        var rows = WorkbenchRules.Issues(Pack(), []);
        var r = Assert.Single(rows);
        Assert.Equal(("", "", "No problems found."), (r.Severity, r.Item, r.Problem));
        Assert.Null(r.Issue);
    }

    [Fact]
    public void Issues_rows_carry_severity_item_problem()
    {
        var pack = Pack(new PropertyDef { Id = "a", Name = "Dup" }, new PropertyDef { Id = "b", Name = "Dup" });
        var rows = WorkbenchRules.Issues(pack, []);
        Assert.NotEmpty(rows);
        var direct = RulesValidator.Validate(pack, []);
        Assert.Equal(direct.Count, rows.Count);
        Assert.Equal(direct[0].Severity.ToString(), rows[0].Severity);
        Assert.Equal(direct[0].EntityName, rows[0].Item);
        Assert.Equal(direct[0].Message, rows[0].Problem);
    }

    [Fact]
    public void Issue_target_order_and_map_owner_goes_to_visibility()
    {
        var pack = Pack(new TableDef { Id = "t" }, new FormulaDef { Id = "f" }, new PhaseDef { Id = "ph" }, new AlertDef { Id = "al" },
            new PropertyDef { Id = "pr" }, new HolderDef { Id = "h" }, new MapOwner { MapId = "m1", SideId = "s" });
        Assert.Equal((WorkbenchTab.Tables, "t"), WorkbenchRules.IssueTarget(pack, "t"));
        Assert.Equal((WorkbenchTab.Formulas, "f"), WorkbenchRules.IssueTarget(pack, "f"));
        Assert.Equal((WorkbenchTab.Phases, "ph"), WorkbenchRules.IssueTarget(pack, "ph"));
        Assert.Equal((WorkbenchTab.Alerts, "al"), WorkbenchRules.IssueTarget(pack, "al"));
        Assert.Equal((WorkbenchTab.Properties, "pr"), WorkbenchRules.IssueTarget(pack, "pr"));
        Assert.Equal((WorkbenchTab.Properties, "h"), WorkbenchRules.IssueTarget(pack, "h"));
        Assert.Equal((WorkbenchTab.Visibility, "m1"), WorkbenchRules.IssueTarget(pack, "m1"));
        Assert.Null(WorkbenchRules.IssueTarget(pack, "nope"));
    }

    [Fact]
    public void Make_ref_reuses_same_entry_and_page()
    {
        var existing = new RuleRef { Id = "r1", Label = "Old", DocumentEntry = "docs/a.pdf", Page = 3 };
        var pack = Pack(existing);
        var (p2, r) = WorkbenchRules.MakeOrReuseRef(pack, "New", "docs/a.pdf", 3);
        Assert.Same(existing, r);
        Assert.Same(pack, p2);
        var (p3, r3) = WorkbenchRules.MakeOrReuseRef(pack, "New", "docs/a.pdf", 4);
        Assert.NotSame(existing, r3);
        Assert.Equal(2, p3.RuleRefs.Count);
    }

    [Fact]
    public void Make_ref_new_has_label_entry_page()
    {
        var (p, r) = WorkbenchRules.MakeOrReuseRef(Pack(), "Combat", "docs/a.pdf", 7);
        Assert.Equal(("Combat", "docs/a.pdf", 7), (r.Label, r.DocumentEntry, r.Page));
        Assert.Matches("^[0-9a-f]{32}$", r.Id);
        Assert.Same(r, Assert.Single(p.RuleRefs));
    }

    [Fact]
    public void Missing_ref_text_with_and_without_scan_hint()
    {
        Assert.EndsWith(" (open it with Open scan… to see it).", WorkbenchRules.MissingRefText("a.pdf"));
        Assert.EndsWith(" (open it with Open scan… to see it).", WorkbenchRules.MissingRefText("page.png"));
        Assert.Equal("The reference points to 'notes', which is not in this module.", WorkbenchRules.MissingRefText("notes"));
    }

    [Fact]
    public void Unique_name_skips_taken_case_insensitive()
    {
        Assert.Equal("Property 1", WorkbenchRules.UniqueName([], "Property"));
        Assert.Equal("Property 3", WorkbenchRules.UniqueName(["Property 2", "x"], "Property"));
        Assert.Equal("Property 3", WorkbenchRules.UniqueName(["PROPERTY 2", "property 1"], "Property"));
    }

    [Fact]
    public void Delete_question_lists_15_then_ellipsis()
    {
        Assert.Equal("Delete the holder \"H\"?", WorkbenchRules.DeleteQuestion("holder", "H", []));
        var users = Enumerable.Range(1, 16).Select(i => new FormulaDef { Id = "f" + i, Name = "F" + i }).ToList();
        var t = WorkbenchRules.DeleteQuestion("property", "P", users);
        Assert.StartsWith("Delete the property \"P\"?\n\nThese formulas use it and will report errors until you fix them:\n  F1\n  F2", t);
        Assert.Contains("\n  F15\n  …", t);
        Assert.DoesNotContain("F16", t);
        Assert.DoesNotContain("…", WorkbenchRules.DeleteQuestion("property", "P", users.Take(15).ToList()));
    }
}
