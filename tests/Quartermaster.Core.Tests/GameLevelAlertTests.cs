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
        Assert.Equal("Flag is up", d.Text);
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

    [Fact]
    public void Empty_messages_fall_back_to_readable_text()
    {
        var phase = Phase("Flag == 1", "");
        Assert.Equal("Flag == 1", Assert.Single(Assert.Single(Decisions.Warnings(Pack(phase), State(1), phase)).UnitTexts));
        var pk = Pack(null, new AlertDef { Id = "f", Name = "Flag", Condition = "Flag == 1", Message = "" });
        Assert.Equal("Flag", Assert.Single(Assert.Single(AlertEngine.Evaluate(State(1), pk, null, null, null)).Matches).Text);
    }

    [Theory]
    [InlineData("Fuel == 0")]
    [InlineData("name() == \"Red One\"")]
    public void Piece_conditions_stay_per_unit(string cond)
    {
        var phase = Phase(cond, "x");
        var s = State(0);
        foreach (var p in s.Pieces.Keys) s.Ledgers[LedgerKey.Piece(p)]["fuel"] = 0;
        var h = Assert.Single(Decisions.Warnings(Pack(phase), s, phase));
        Assert.NotEmpty(h.PieceIds);
    }

    [Theory]
    [InlineData("Flag ==")]
    [InlineData("1/0 > 0")]
    public void Bad_or_failing_game_level_condition_gives_no_hit(string cond)
    {
        var phase = Phase(cond, "x");
        Assert.Empty(Decisions.Warnings(Pack(phase), State(1), phase));
        var pk = Pack(null, new AlertDef { Id = "f", Name = "F", Condition = cond, Message = "x" });
        Assert.Empty(AlertEngine.Evaluate(State(1), pk, null, null, null));
    }

    [Fact]
    public void Game_level_decision_is_listed_by_the_unacknowledged_warning_alert()
    {
        var phase = Phase("Flag == 1", "Flag is up", decision: true);
        var pk = Pack(phase);
        var s = State(1);
        s.Position = new PhasePosition(1, 1, "P", 0);
        var hit = Assert.Single(AlertEngine.Evaluate(s, pk, null, phase, null), h => h.Id == AlertEngine.WarningId);
        var m = Assert.Single(hit.Matches);
        Assert.Null(m.PieceId);
        Assert.Null(m.HolderId);
        Assert.Equal("Flag is up", m.Text);
        s.Acknowledged.Add(0);
        Assert.DoesNotContain(AlertEngine.Evaluate(s, pk, null, phase, null), h => h.Id == AlertEngine.WarningId);
    }

    [Theory]
    [InlineData("{unit}: set up", "set up")]
    [InlineData("{unit}, check the flag", "check the flag")]
    [InlineData("{unit} - flag up", "flag up")]
    [InlineData("{unit} — flag up", "flag up")]
    [InlineData("Flag up for {unit}", "Flag up for")]
    [InlineData("-1 MP to all units", "-1 MP to all units")]   // a sign is never a separator
    [InlineData("{unit} -1 MP", "-1 MP")]
    [InlineData("  {unit}: set up", "set up")]
    [InlineData("Set up: {unit}", "Set up:")]
    public void Game_level_texts_drop_leftover_punctuation(string message, string expected)
    {
        var phase = Phase("Flag == 1", message, decision: true);
        var pk = Pack(phase, new AlertDef { Id = "f", Name = "Flag", Condition = "Flag == 1", Message = message });
        var s = State(1);
        s.Position = new PhasePosition(1, 1, "P", 0);
        Assert.Equal(expected, Assert.Single(Assert.Single(Decisions.Warnings(pk, s, phase)).UnitTexts));
        Assert.Equal(expected, Assert.Single(Decisions.Compute(pk, s), x => x.Kind == DecisionKind.Warning).Text);
        Assert.Equal(expected, Assert.Single(Assert.Single(AlertEngine.Evaluate(s, pk, null, null, null), h => h.Id == "f").Matches).Text);
    }

    [Theory]
    [InlineData("-1 MP for {unit}", "-1 MP for — 3 units")]
    [InlineData("{unit}: set up", ": set up — 3 units")]   // per unit: exactly as before this branch
    public void Per_unit_decision_head_is_unchanged(string message, string expected)
    {
        var phase = Phase("Grit == 1", message, decision: true);
        var s = State(0);
        s.Position = new PhasePosition(1, 1, "P", 0);
        Assert.Equal(expected, Assert.Single(Decisions.Compute(Pack(phase), s), x => x.Kind == DecisionKind.Warning).Text);
    }

    [Fact]
    public void Own_empty_holder_reads_its_default_in_the_view_and_another_sides_does_not()
    {
        var pk = Pack();
        var full = State(0);   // the S holder SouthStore has no ledger record at all
        Assert.False(full.Ledgers.ContainsKey(LedgerKey.Holder("south")));
        var own = PlayerView.For(full, pk, RunnerFx.Model, new HashSet<string> { "s" });
        Assert.True(own.IsPlayerView);
        Assert.Equal(new[] { "s" }, own.ViewSides);
        Assert.True(PieceFilter.EvaluateGameLevel("holder(SouthStore, Fuel) == 0", own, pk));   // the Default
        Assert.Equal(0, PieceFilter.HolderRead(own, pk, "SouthStore", "Fuel").Number);

        var other = PlayerView.For(full, pk, RunnerFx.Model, new HashSet<string> { "a" });
        Assert.Equal(new[] { "a" }, other.ViewSides);
        Assert.False(PieceFilter.EvaluateGameLevel("holder(SouthStore, Fuel) == 0", other, pk));
        Assert.Throws<Quartermaster.Rules.Expr.EvalError>(() => PieceFilter.HolderRead(other, pk, "SouthStore", "Fuel"));

        // A view from an older host carries no ViewSides: its own empty holder keeps failing closed.
        own.ViewSides = null;
        Assert.Throws<Quartermaster.Rules.Expr.EvalError>(() => PieceFilter.HolderRead(own, pk, "SouthStore", "Fuel"));
    }

    [Fact]
    public void View_sides_survive_a_round_trip_and_a_full_save_never_writes_them()
    {
        var pk = Pack();
        var full = State(0);
        var fullJson = System.Text.Json.JsonSerializer.Serialize(full, QuartermasterJson.Options);
        Assert.DoesNotContain("viewSides", fullJson, StringComparison.OrdinalIgnoreCase);
        Assert.Null(full.DeepClone().ViewSides);
        Assert.Equal(fullJson, System.Text.Json.JsonSerializer.Serialize(full.DeepClone(), QuartermasterJson.Options));

        var view = PlayerView.For(full, pk, RunnerFx.Model, new HashSet<string> { "s" });
        var json = System.Text.Json.JsonSerializer.Serialize(view, QuartermasterJson.Options);
        Assert.Contains("\"viewSides\"", json);
        Assert.Equal(new[] { "s" }, System.Text.Json.JsonSerializer.Deserialize<GameState>(json, QuartermasterJson.Options)!.ViewSides);
        Assert.Equal(new[] { "s" }, view.DeepClone().ViewSides);
        // Every side held: the view is the full state, with no ViewSides.
        Assert.Null(PlayerView.For(full, pk, RunnerFx.Model, new HashSet<string> { "a", "s" }).ViewSides);
    }
}
