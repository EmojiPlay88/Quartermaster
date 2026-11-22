using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>Warnings and alerts whose condition never reads the piece show once. Invented data.</summary>
public class GameLevelAlertTests
{
    private static RulesPack Pack(PhaseDef? phase = null, params AlertDef[] alerts) => new()
    {
        Properties = new()
        {
            new PropertyDef { Id = "flag", Name = "Flag", Scope = PropertyScope.Holders, Default = 0, Visibility = Visibility.Public },
            new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Both, Default = 0, Visibility = Visibility.Private },
            new PropertyDef { Id = "grit", Name = "Grit", Scope = PropertyScope.Pieces, Default = 0 },
        },
        Holders = new() { new HolderDef { Id = "south", Name = "SouthStore", SideId = "s" } },
        Sides = new()
        {
            new SideDef { Id = "a", Name = "A", UnitFilter = "startsWith(name(), \"Red\")" },
            new SideDef { Id = "s", Name = "S", UnitFilter = "startsWith(name(), \"Blue\")" },
        },
        Phases = phase == null ? new() : new() { phase },
        Alerts = alerts.ToList(),
    };

    private static GameState State(double flag)
    {
        var s = new GameState();
        foreach (var n in new[] { "One", "Two", "Three" })
        {
            s.Pieces["p" + n] = new PieceInstance
            {
                Id = "p" + n, MapId = "Main Map", TraitStates = new() { "" },
                TypeString = SequenceEncoder.Encode(new[] { "piece;;;unit.svg;Red " + n }, '\t'),
            };
            s.Ledgers[LedgerKey.Piece("p" + n)] = new() { ["grit"] = 1 };
        }
        s.Ledgers[LedgerKey.Holder(HolderDef.GlobalId)] = new() { ["flag"] = flag };
        return s;
    }

    private static PhaseDef Phase(string cond, string msg, bool decision = false) =>
        new() { Id = "P", Name = "P", Warnings = new() { new PhaseWarning { Condition = cond, Message = msg, IsDecision = decision } } };

    [Fact]
    public void Game_level_warning_shows_once()
    {
        var phase = Phase("Flag == 1", "Flag is up");
        var hits = Decisions.Warnings(Pack(phase), State(1), phase);
        var h = Assert.Single(hits);
        Assert.Empty(h.PieceIds);
        Assert.Equal("Flag is up", Assert.Single(h.UnitTexts));
    }

    [Fact]
    public void Game_level_warning_false_shows_nothing()
    {
        var phase = Phase("Flag == 1", "Flag is up");
        Assert.Empty(Decisions.Warnings(Pack(phase), State(0), phase));
    }

    [Fact]
    public void Piece_warning_stays_per_unit()
    {
        var phase = Phase("Grit == 1", "Gritty {unit}");
        var h = Assert.Single(Decisions.Warnings(Pack(phase), State(0), phase));
        Assert.Equal(3, h.PieceIds.Count);
        Assert.Equal(3, h.UnitTexts.Count);
    }

    [Fact]
    public void Game_level_alert_shows_once()
    {
        var pk = Pack(null, new AlertDef { Id = "f", Name = "Flag", Condition = "Flag == 1", Message = "Up {unit}", Severity = AlertSeverity.Info });
        var hit = Assert.Single(AlertEngine.Evaluate(State(1), pk, null, null, null));
        var m = Assert.Single(hit.Matches);
        Assert.Null(m.PieceId);
        Assert.Equal(HolderDef.GlobalId, m.HolderId);
        Assert.Equal("Up", m.Text);
        Assert.Empty(AlertEngine.Evaluate(State(0), pk, null, null, null));
    }

    [Fact]
    public void Game_level_alert_respects_side()
    {
        var phase = new PhaseDef { Id = "P", Name = "P" };
        var pk = Pack(phase, new AlertDef { Id = "f", Name = "Flag", Condition = "Flag == 1", Message = "Up", SideId = "a" });
        // The side's unit filter (Red units) does not apply: one match, no matter how many units.
        Assert.Single(Assert.Single(AlertEngine.Evaluate(State(1), pk, null, null, "a")).Matches);
        Assert.Empty(AlertEngine.Evaluate(State(1), pk, null, null, "s"));
        Assert.Empty(AlertEngine.Evaluate(State(1), pk, null, null, null));
    }

    [Fact]
    public void Game_level_decision_acknowledges()
    {
        var phase = Phase("Flag == 1", "Flag is up", decision: true);
        var pk = Pack(phase);
        var s = State(1);
        s.Position = new PhasePosition(1, 1, "P", 0);
        var d = Assert.Single(Decisions.Compute(pk, s) , x => x.Kind == DecisionKind.Warning);
        Assert.Equal(0, d.Index);
        s.Acknowledged.Add(0);
        Assert.True(Assert.Single(Decisions.Warnings(pk, s, phase)).Acknowledged);
        Assert.DoesNotContain(Decisions.Compute(pk, s), x => x.Kind == DecisionKind.Warning);
    }

    [Fact]
    public void Hidden_holder_value_hides_game_level_warning()
    {
        var phase = Phase("holder(SouthStore, Fuel) > 0", "South has fuel");
        var pk = Pack(phase);
        var s = State(0);
        s.Ledgers[LedgerKey.Holder("south")] = new() { ["fuel"] = 5 };
        Assert.Single(Decisions.Warnings(pk, s, phase));
        s.Ledgers.Remove(LedgerKey.Holder("south"));
        s.IsPlayerView = true;
        Assert.Empty(Decisions.Warnings(pk, s, phase));
    }
}
