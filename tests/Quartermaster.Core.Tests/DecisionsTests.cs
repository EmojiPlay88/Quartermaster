using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class DecisionsTests
{
    private static GameState State(params (string id, string name, double fuel)[] units)
    {
        var s = new GameState();
        foreach (var (id, name, fuel) in units)
        {
            s.Pieces[id] = new PieceInstance
            {
                Id = id, MapId = "Main Map", TraitStates = new() { "" },
                TypeString = SequenceEncoder.Encode(new[] { "piece;;;unit.svg;" + name }, '\t'),
            };
            s.Ledgers[LedgerKey.Piece(id)] = new() { ["fuel"] = fuel };
        }
        return s;
    }

    private static RulesPack Pack(PhaseDef phase) => new()
    {
        Properties = new() { new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces } },
        Sides = new() { new SideDef { Id = "red", Name = "Red", UnitFilter = "startsWith(name(), \"Red\")" } },
        Phases = new() { phase },
    };

    private static PhaseDef Phase(params PhaseWarning[] w) => new()
    {
        Id = "P", Name = "Phase P", Checklist = new() { "Do A", "Do B", "Do C" }, Warnings = w.ToList(),
    };

    private static PendingRun Run(string id, string name, int n, int applied, bool needs = false) =>
        new(id, "f", name, "start",
            Enumerable.Range(0, n).Select(i => new PendingUnit("u" + i, "Unit " + i, null, i < applied)).ToList(), needs);

    [Fact]
    public void Compute_OrderAndTexts()
    {
        var ph = Phase(
            new PhaseWarning { Condition = "Fuel <= 0", Message = "Out of fuel {unit}", IsDecision = true });
        var pk = Pack(ph);
        var s = State(("a", "Red One", 0), ("b", "Red Two", 0), ("c", "Red Three", 5));
        s.Position = new PhasePosition(1, 1, "P", 0);
        s.Ticked.Add(1);
        s.Pending = new()
        {
            Run("r1", "Daily water", 3, 1),
            new PendingRun("r2", "f", "Fuel use", "move", new[] { new PendingUnit("a", "Red One", 4, false) }, false),
            Run("r3", "Ask", 0, 0, needs: true),
        };
        var d = Decisions.Compute(pk, s);
        Assert.Equal(new[]
        {
            "Do A", "Do C",
            "Daily water — 1 of 3 applied", "Fuel use — Red One", "Ask — needs input",
            "Out of fuel — 2 units",
        }, d.Select(x => x.Text));
        Assert.Equal(new[] { DecisionKind.Checklist, DecisionKind.Checklist, DecisionKind.Pending, DecisionKind.Pending, DecisionKind.Pending, DecisionKind.Warning }, d.Select(x => x.Kind));
        Assert.Equal(new[] { 0, 2, -1, -1, -1, 0 }, d.Select(x => x.Index));
        Assert.Equal("r1", d[2].RunId);
        Assert.Null(d[0].RunId);
        Assert.Equal(new[] { "a", "b" }, d[5].PieceIds);
    }

    [Fact]
    public void Warnings_PerUnitTexts_TokenReplaced()
    {
        var ph = Phase(
            new PhaseWarning { Condition = "Fuel <= 0", Message = "{unit} is dry" },
            new PhaseWarning { Condition = "Fuel <= 0", Message = "Dry" });
        var pk = Pack(ph);
        var s = State(("a", "Red One", 0), ("b", "Red Two", 3));
        var w = Decisions.Warnings(pk, s, ph);
        Assert.Equal(2, w.Count);
        Assert.Equal(new[] { "Red One is dry" }, w[0].UnitTexts);
        Assert.Equal(new[] { "Red One" }, w[1].UnitTexts);
        Assert.Equal(new[] { "a" }, w[0].PieceIds);
        Assert.Equal(1, w[1].Index);
        Assert.False(w[0].IsDecision);
        Assert.False(w[0].Acknowledged);
    }

    [Fact]
    public void WarningDecision_ClosesWhenNoUnitMatches()
    {
        var ph = Phase(new PhaseWarning { Condition = "Fuel <= 0", Message = "Dry {unit}", IsDecision = true });
        var pk = Pack(ph);
        var s = State(("a", "Red One", 2));
        s.Position = new PhasePosition(1, 1, "P", 0);
        Assert.Empty(Decisions.Warnings(pk, s, ph));
        Assert.DoesNotContain(Decisions.Compute(pk, s), x => x.Kind == DecisionKind.Warning);
    }

    [Fact]
    public void WarningDecision_ClosesWhenAcknowledged()
    {
        var ph = Phase(new PhaseWarning { Condition = "Fuel <= 0", Message = "Dry {unit}", IsDecision = true });
        var pk = Pack(ph);
        var s = State(("a", "Red One", 0));
        s.Position = new PhasePosition(1, 1, "P", 0);
        s.Acknowledged.Add(0);
        var w = Decisions.Warnings(pk, s, ph);
        Assert.Single(w);
        Assert.True(w[0].Acknowledged);
        Assert.DoesNotContain(Decisions.Compute(pk, s), x => x.Kind == DecisionKind.Warning);
        // singular form
        s.Acknowledged.Clear();
        Assert.Equal("Dry — 1 unit", Decisions.Compute(pk, s).Single(x => x.Kind == DecisionKind.Warning).Text);
    }

    [Fact]
    public void Warnings_LimitedToActingSide()
    {
        var ph = Phase(new PhaseWarning { Condition = "Fuel <= 0", Message = "Dry" }) with { SideId = "red" };
        var pk = Pack(ph);
        var s = State(("a", "Red One", 0), ("b", "Blue One", 0));
        var w = Decisions.Warnings(pk, s, ph);
        Assert.Equal(new[] { "a" }, w.Single().PieceIds);
        Assert.Equal(new[] { "a", "b" }, Decisions.Warnings(pk, s, ph with { SideId = null }).Single().PieceIds);
    }

    [Theory]
    [InlineData("((")]
    [InlineData("Nope <= 0")]
    [InlineData("name() > 3")]
    [InlineData("")]
    public void Warnings_BadCondition_NoHits(string cond)
    {
        var ph = Phase(new PhaseWarning { Condition = cond, Message = "x", IsDecision = true });
        var pk = Pack(ph);
        var s = State(("a", "Red One", 0));
        s.Position = new PhasePosition(1, 1, "P", 0);
        Assert.Empty(Decisions.Warnings(pk, s, ph));
        Assert.DoesNotContain(Decisions.Compute(pk, s), x => x.Kind == DecisionKind.Warning);
    }

    [Fact]
    public void Compute_NameOnlyPhase_NoDecisions()
    {
        var ph = new PhaseDef { Id = "P", Name = "Just a name" };
        var pk = Pack(ph);
        var s = State(("a", "Red One", 0));
        s.Position = new PhasePosition(1, 1, "P", 0);
        Assert.Empty(Decisions.Compute(pk, s));
    }

    [Fact]
    public void Compute_BeforeStart_Empty()
    {
        var pk = Pack(Phase());
        Assert.Empty(Decisions.Compute(pk, State()));
        var s = State();
        s.Position = new PhasePosition(1, 1, "gone", 0);
        Assert.Empty(Decisions.Compute(pk, s));
    }
}
