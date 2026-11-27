using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

/// <summary>B8 task 5: PhaseEdit.SetSideAlliance / SetSideFormerIds / MergeSide. Invented Riverland data.</summary>
public class PhaseEditSidesTests
{
    static RulesPack Pack() => new()
    {
        Sides = new()
        {
            new SideDef { Id = "north", Name = "Northern League", UnitFilter = "startsWith(name(), \"N\")", FormerIds = new() { "league" } },
            new SideDef { Id = "south", Name = "Southern Compact", UnitFilter = "startsWith(name(), \"S\")" },
            new SideDef { Id = "east", Name = "East Shore", UnitFilter = "startsWith(name(), \"E\")", FormerIds = new() { "shore" } },
        },
        Phases = new()
        {
            new PhaseDef
            {
                Id = "p1", Name = "Supply", SideId = "east", Reactors = new() { "south", "north" },
                OnStart = new() { new StartEntry { FormulaId = "f", AskSideId = "east" } },
                OnEnd = new() { new StartEntry { FormulaId = "f", AskSideId = "east" } },
            },
            new PhaseDef { Id = "p2", Name = "Move", SideId = "south", Reactors = new() { "east" } },
        },
        Holders = new() { new HolderDef { Id = "h", Name = "Depot", SideId = "east" } },
        MapOwners = new() { new MapOwner { MapId = "m", SideId = "east" } },
        Alerts = new() { new AlertDef { Id = "a", Name = "Alert", Condition = "true", Message = "x", SideId = "east" } },
        Victory = new() { new VictoryCondition { Id = "v", Condition = "true", SideId = "east" } },
    };

    static SideDef Side(RulesPack p, string id) => p.Sides.Single(s => s.Id == id);

    [Fact]
    public void Set_alliance_trims_blank_null()
    {
        var p = Pack();
        var (a, err) = PhaseEdit.SetSideAlliance(p, "north", "  Upriver Pact ");
        Assert.Null(err);
        Assert.Equal("Upriver Pact", Side(a, "north").AllianceId);
        Assert.Null(PhaseEdit.SetSideAlliance(a, "north", "   ").Pack.Sides[0].AllianceId);
        Assert.Null(PhaseEdit.SetSideAlliance(a, "north", null).Pack.Sides[0].AllianceId);
        Assert.Equal(p.Sides[1], Side(a, "south"));
    }

    [Fact]
    public void Set_former_ids_splits_trims_dedupes_none_null()
    {
        var p = Pack();
        var (a, err) = PhaseEdit.SetSideFormerIds(p, "south", " old , ,older,old,, OLD ");
        Assert.Null(err);
        Assert.Equal(new[] { "old", "older", "OLD" }, Side(a, "south").FormerIds);
        Assert.Null(PhaseEdit.SetSideFormerIds(a, "south", " , ").Pack.Sides[1].FormerIds);
        Assert.Null(PhaseEdit.SetSideFormerIds(a, "south", null).Pack.Sides[1].FormerIds);
        Assert.Equal(new[] { "league" }, Side(a, "north").FormerIds);
    }

    [Fact]
    public void Set_former_ids_maps_dangling_references()
    {
        var p = Pack() with { Holders = new() { new HolderDef { Id = "h", Name = "Depot", SideId = "west" } } };
        var (a, err) = PhaseEdit.SetSideFormerIds(p, "south", "west");
        Assert.Null(err);
        Assert.Equal("south", a.Holders.Single().SideId);
        Assert.Same(p.Holders, PhaseEdit.SetSideFormerIds(p, "south", "older").Pack.Holders);   // nothing named: references untouched
    }

    [Fact]
    public void Merge_moves_references_and_former_ids()
    {
        var (m, err) = PhaseEdit.MergeSide(Pack(), "east", "south");
        Assert.Null(err);
        Assert.DoesNotContain(m.Sides, s => s.Id == "east");
        Assert.Equal(new[] { "east", "shore" }, Side(m, "south").FormerIds);
        var p1 = m.Phases[0];
        Assert.Equal("south", p1.SideId);
        Assert.Equal(new[] { "north" }, p1.Reactors);   // own side dropped
        Assert.Equal("south", p1.OnStart![0].AskSideId);
        Assert.Equal("south", p1.OnEnd![0].AskSideId);
        Assert.Equal("south", m.Phases[1].SideId);
        Assert.Null(m.Phases[1].Reactors);
        Assert.Equal("south", m.Holders[0].SideId);
        Assert.Equal("south", m.MapOwners[0].SideId);
        Assert.Equal("south", m.Alerts[0].SideId);
        Assert.Equal("south", m.Victory![0].SideId);
    }

    [Fact]
    public void Merge_combines_filters_or_blank()
    {
        var (m, _) = PhaseEdit.MergeSide(Pack(), "east", "south");
        Assert.Equal("(startsWith(name(), \"S\")) or (startsWith(name(), \"E\"))", Side(m, "south").UnitFilter);
        var p = PhaseEdit.SetSideFilter(Pack(), "south", " ").Pack;
        Assert.Null(Side(PhaseEdit.MergeSide(p, "east", "south").Pack, "south").UnitFilter);
        p = PhaseEdit.SetSideFilter(Pack(), "east", null).Pack;
        Assert.Null(Side(PhaseEdit.MergeSide(p, "east", "south").Pack, "south").UnitFilter);
    }

    [Fact]
    public void Merge_into_itself_error()
    {
        var p = Pack();
        var (r, err) = PhaseEdit.MergeSide(p, "east", "east");
        Assert.Equal("A side cannot be merged into itself", err);
        Assert.Same(p, r);
    }

    [Fact]
    public void Merge_unknown_name_error()
    {
        var p = Pack();
        var (r, err) = PhaseEdit.MergeSide(p, "east", "nowhere");
        Assert.Equal("No side is named \"nowhere\"", err);
        Assert.Same(p, r);
    }

    [Fact]
    public void Merged_pack_validates_clean()
    {
        var (m, _) = PhaseEdit.MergeSide(Pack(), "east", "south");
        Assert.DoesNotContain(RulesValidator.Validate(m), i => i.Message.Contains("former id") || i.Message.Contains("unknown side", StringComparison.OrdinalIgnoreCase)
            || i.Message.Contains("no longer exists", StringComparison.OrdinalIgnoreCase));
    }
}
