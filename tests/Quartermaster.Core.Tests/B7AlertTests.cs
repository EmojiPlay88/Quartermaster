using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>B7 #74b/#74c: side alerts by the viewer's sides, own-only below-zero lists, the warning chip for the phase's side. Invented data.</summary>
public class B7AlertTests
{
    private static readonly IReadOnlySet<string> Red = new HashSet<string> { "red" };
    private static readonly IReadOnlySet<string> Blue = new HashSet<string> { "blue" };
    private static readonly IReadOnlySet<string> Both = new HashSet<string> { "red", "blue" };

    private static void AddUnit(GameState s, string id, string name, double fuel)
    {
        s.Pieces[id] = new PieceInstance
        {
            Id = id, MapId = "Main Map", TraitStates = new() { "" },
            TypeString = SequenceEncoder.Encode(new[] { "piece;;;unit.svg;" + name }, '\t'),
        };
        s.Ledgers[LedgerKey.Piece(id)] = new() { ["fuel"] = fuel };
    }

    private static RulesPack Pack(PhaseDef? phase = null, params AlertDef[] alerts) => new()
    {
        Properties = new() { new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Both } },
        Holders = new()
        {
            new HolderDef { Id = "depot", Name = "Depot" },
            new HolderDef { Id = "redDepot", Name = "Red depot", SideId = "red" },
            new HolderDef { Id = "blueDepot", Name = "Blue depot", SideId = "blue" },
        },
        Sides = new()
        {
            new SideDef { Id = "red", Name = "Red", UnitFilter = "startsWith(name(), \"Red\")" },
            new SideDef { Id = "blue", Name = "Blue", UnitFilter = "startsWith(name(), \"Blue\")" },
        },
        Phases = phase == null ? new() : new() { phase },
        Alerts = alerts.ToList(),
    };

    private static AlertDef Side(string side) =>
        new() { Id = "a-" + side, Name = side + " alert", Condition = "Fuel < 100", Message = "low", Severity = AlertSeverity.Warning, SideId = side };

    private static PhaseDef Phase(string? side, bool warn = false) => new()
    {
        Id = "P", Name = "Phase P", SideId = side,
        Warnings = warn ? new() { new PhaseWarning { Condition = "Fuel <= 0", Message = "Dry", IsDecision = true } } : new(),
    };

    private static GameState State(double redFuel = 5, double blueFuel = 5)
    {
        var s = new GameState();
        AddUnit(s, "r", "Red One", redFuel);
        AddUnit(s, "b", "Blue One", blueFuel);
        s.Position = new PhasePosition(1, 1, "P", 0);
        return s;
    }

    private static IReadOnlyList<AlertHit> Eval(GameState s, RulesPack pk, PhaseDef? ph, IReadOnlySet<string>? viewer) =>
        AlertEngine.Evaluate(s, pk, null, ph, ph?.SideId, viewer);

    [Fact]
    public void Side_alert_shown_to_holder_in_other_side_phase()
    {
        var ph = Phase("blue");
        var hits = Eval(State(), Pack(ph, Side("red")), ph, Red);
        Assert.Equal("a-red", Assert.Single(hits).Id);
    }

    [Fact]
    public void Side_alert_hidden_from_non_holder_in_its_own_phase()
    {
        var ph = Phase("red");
        Assert.Empty(Eval(State(), Pack(ph, Side("red")), ph, Blue));
        Assert.Empty(Eval(State(), Pack(ph, Side("red")), ph, new HashSet<string>()));
    }

    [Fact]
    public void Side_alert_solo_every_phase()
    {
        var ph = Phase("blue");
        Assert.Equal("a-red", Assert.Single(Eval(State(), Pack(ph, Side("red")), ph, null)).Id);
        Assert.Equal("a-red", Assert.Single(Eval(State(), Pack(ph, Side("red")), ph, Both)).Id);
    }

    [Fact]
    public void Five_argument_overload_unchanged()
    {
        var ph = Phase("blue");
        var pk = Pack(ph, Side("red"));
        Assert.Empty(AlertEngine.Evaluate(State(), pk, null, ph, "blue"));
        Assert.Single(AlertEngine.Evaluate(State(), pk, null, ph, "red"));
        // every below-zero entry and the warning chip stay for any acting side
        var s = State(-1, -2);
        s.Ledgers[LedgerKey.Holder("blueDepot")] = new() { ["fuel"] = -3 };
        var w = Phase("red", warn: true);
        var hits = AlertEngine.Evaluate(s, Pack(w), null, w, null);
        Assert.Equal(new[] { "Fuel below zero", "Unacknowledged warning" }, hits.Select(h => h.Name));
        Assert.Equal(3, hits[0].Matches.Count);
    }

    [Fact]
    public void Negative_lists_only_own_pieces()
    {
        var s = State(-1, -2);
        var hit = Assert.Single(Eval(s, Pack(), null, Red));
        var m = Assert.Single(hit.Matches);
        Assert.Equal("r", m.PieceId);
        Assert.DoesNotContain("Blue", AlertEngine.Tooltip(hit));
        Assert.Equal(2, Eval(s, Pack(), null, null).Single().Matches.Count);
        Assert.Equal(2, Eval(s, Pack(), null, Both).Single().Matches.Count);
        Assert.Empty(Eval(s, Pack(), null, new HashSet<string>()));
    }

    [Fact]
    public void Negative_lists_only_own_and_unsided_holders_and_global()
    {
        var s = State();
        s.Ledgers[LedgerKey.Holder("depot")] = new() { ["fuel"] = -1 };
        s.Ledgers[LedgerKey.Holder("redDepot")] = new() { ["fuel"] = -1 };
        s.Ledgers[LedgerKey.Holder("blueDepot")] = new() { ["fuel"] = -1 };
        s.Ledgers[LedgerKey.Holder(HolderDef.GlobalId)] = new() { ["fuel"] = -1 };
        var hit = Assert.Single(Eval(s, Pack(), null, Red));
        Assert.Equal(new[] { "Depot", "Global", "Red depot" }, hit.Matches.Select(m => m.Text).OrderBy(x => x));
    }

    [Fact]
    public void Negative_skips_masked_placeholder()
    {
        var s = State(-1, 5);
        s.Pieces["m"] = new PieceInstance { Id = "m", MapId = "Main Map", TraitStates = new() { "masked", "" }, TypeString = "piece;;;;" };
        s.Ledgers[LedgerKey.Piece("m")] = new() { ["fuel"] = -9 };
        s.IsPlayerView = true;
        var hit = Assert.Single(Eval(s, Pack(), null, Red));
        Assert.Equal("r", Assert.Single(hit.Matches).PieceId);
    }

    [Fact]
    public void Negative_no_chip_when_list_empty()
    {
        var s = State(5, -2);
        Assert.Empty(Eval(s, Pack(), null, Red));
        Assert.Single(Eval(s, Pack(), null, Blue));
    }

    [Fact]
    public void Warning_chip_only_for_phase_side()
    {
        var ph = Phase("red", warn: true);
        var s = State(0, 5);
        Assert.Contains(Eval(s, Pack(ph), ph, Red), h => h.Id == AlertEngine.WarningId);
        Assert.DoesNotContain(Eval(s, Pack(ph), ph, Blue), h => h.Id == AlertEngine.WarningId);
        Assert.Contains(Eval(s, Pack(ph), ph, null), h => h.Id == AlertEngine.WarningId);
    }

    [Fact]
    public void Warning_chip_side_less_phase_all()
    {
        var ph = Phase(null, warn: true);
        var s = State(0, 5);
        Assert.Contains(Eval(s, Pack(ph), ph, Blue), h => h.Id == AlertEngine.WarningId);
        Assert.Contains(Eval(s, Pack(ph), ph, Red), h => h.Id == AlertEngine.WarningId);
    }

    // ---- final review wave (F6 b, c) ----

    private static RulesPack PlansPack(PhaseDef? phase, params AlertDef[] alerts)
    {
        var pk = Pack(phase, alerts);
        pk.Properties.Add(new PropertyDef { Id = "plans", Name = "Plans", Scope = PropertyScope.Holders, Visibility = Visibility.Private });
        pk.Holders.Add(new HolderDef { Id = "redStore", Name = "RedStore", SideId = "red" });
        return pk;
    }

    [Fact]
    public void User_alert_over_other_side_private_holder_value_silent_on_non_holder_view()
    {
        var a = new AlertDef { Id = "a-plans", Name = "Plans alert", Condition = "holder(RedStore, Plans) > 5", Message = "plans", Severity = AlertSeverity.Warning };
        var ph = Phase(null);
        var pk = PlansPack(ph, a);
        var s = State();
        s.Ledgers[LedgerKey.Holder("redStore")] = new() { ["plans"] = 9 };
        Assert.Contains(Eval(s, pk, ph, null), h => h.Id == "a-plans");   // solo: it fires
        Assert.Contains(Eval(PlayerView.For(s, pk, RunnerFx.Model, Red), pk, ph, Red), h => h.Id == "a-plans");
        Assert.DoesNotContain(Eval(PlayerView.For(s, pk, RunnerFx.Model, Blue), pk, ph, Blue), h => h.Id == "a-plans");
    }

    [Fact]
    public void Pending_and_side_less_warning_list_no_other_side_ask_or_sealed_units_on_view()
    {
        var ph = Phase(null, warn: true);
        var pk = PlansPack(ph);
        var s = State(redFuel: 0, blueFuel: 0);
        s.Pending = new()
        {
            new PendingRun("ask-r", "f", "Red muster", "start", new[] { new PendingUnit("r", "Red One", null, false) }, false) { AskSideId = "red" },
            new PendingRun("seal-r", "f", "Toll bid", "start", new[] { new PendingUnit(null, "Red toll", null, false) }, false) { AskSideId = "red", Seal = "seal-r" },
            new PendingRun("seal-b", "f", "Toll bid", "start", new[] { new PendingUnit(null, "Blue toll", null, false) }, false) { AskSideId = "blue", Seal = "seal-r" },
        };
        var full = Eval(s, pk, ph, null);
        Assert.Equal(3, full.Single(h => h.Id == AlertEngine.PendingId).Matches.Count);
        var hits = Eval(PlayerView.For(s, pk, RunnerFx.Model, Blue), pk, ph, Blue);
        Assert.Equal(new[] { "Blue toll" }, hits.Single(h => h.Id == AlertEngine.PendingId).Matches.Select(m => m.Text));
        var warning = hits.Single(h => h.Id == AlertEngine.WarningId);
        Assert.DoesNotContain(warning.Matches, m => m.PieceId == "r" || m.Text.Contains("Red"));
        Assert.Contains(warning.Matches, m => m.PieceId == "b");
    }
}
