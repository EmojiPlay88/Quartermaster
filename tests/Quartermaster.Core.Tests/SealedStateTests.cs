using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Core.Tests;

/// <summary>
/// B7 #53: a sealed answer is stored on its pending run (<see cref="SealAnswer"/>), only in the full state and its own side's view until
/// release (every view once the game is over); askside() names the answering side. Invented Riverland data: the Northern League
/// (<c>north</c>) and the Southern Compact (<c>south</c>).
/// </summary>
public class SealedStateTests
{
    private static readonly IReadOnlySet<string> North = new HashSet<string> { "north" };
    private static readonly IReadOnlySet<string> South = new HashSet<string> { "south" };

    private static RulesPack Pack(string northName = "Northern League") => new()
    {
        Format = 4,
        Sides = new()
        {
            new SideDef { Id = "north", Name = northName, UnitFilter = "startsWith(name(), \"North\")" },
            new SideDef { Id = "south", Name = "Southern Compact", UnitFilter = "startsWith(name(), \"South\")" },
        },
        Phases = new() { new PhaseDef { Id = "ph-toll", Name = "River toll" } },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "f-toll", Name = "Toll bid", Text = "bid",
                Bindings = new() { new VariableBinding { Variable = "bid", Kind = BindingKind.Input, Min = 0, Max = 10 } },
            },
            new FormulaDef { Id = "f-who", Name = "Who answers", Text = "askside()" },
        },
    };

    private static PendingRun Run(string id, string side, string seal) =>
        new(id, "f-toll", "Toll bid", "start", new[] { new PendingUnit(null, "Toll bid", null, false) }, false) { AskSideId = side, Seal = seal };

    private static GameState Sealed()
    {
        var s = new GameState { GameId = "g" };
        s.Apply(new SetPlayers(new[] { new PlayerDef("p1", "Ann", new[] { "north" }), new PlayerDef("p2", "Bob", new[] { "south" }) }));
        s.Apply(new AdvancePhase(null, new PhasePosition(1, 1, "ph-toll", 0), "", "River toll"));
        s.Apply(new QueueRuns(new[] { Run("r1", "north", "r1"), Run("r2", "south", "r1") }));
        return s;
    }

    private static readonly IReadOnlyDictionary<string, string> Bid7 = new Dictionary<string, string> { ["bid"] = "7" };

    [Fact]
    public void SealAnswer_sets_answer_and_logs_public_line()
    {
        var s = Sealed();
        s.Apply(new SealAnswer("r1", Bid7, "Northern League has answered Toll bid (sealed)"));
        var run = s.Pending.Single(r => r.Id == "r1");
        Assert.Equal("7", run.SealedAnswer!["bid"]);
        Assert.False(run.Units[0].Applied);   // stored, not applied
        Assert.Null(s.Pending.Single(r => r.Id == "r2").SealedAnswer);
        var line = s.Log.Last();
        Assert.Equal(LogKind.Turn, line.Kind);
        Assert.Equal("Northern League has answered Toll bid (sealed)", line.Text);
        Assert.Null(line.SideId);

        // Wire and save shape: "$t" "sealAnswer"; Seal and SealedAnswer omitted when null.
        GameCommand cmd = new SealAnswer("r1", Bid7, "x");
        var json = JsonSerializer.Serialize(cmd, QuartermasterJson.Options);
        Assert.Contains("\"sealAnswer\"", json);
        Assert.Equal(cmd.ToString(), JsonSerializer.Deserialize<GameCommand>(json, QuartermasterJson.Options)!.ToString());
        var plain = JsonSerializer.Serialize(new PendingRun("r", "f", "F", "start", Array.Empty<PendingUnit>(), false), QuartermasterJson.Options);
        Assert.DoesNotContain("seal", plain, StringComparison.OrdinalIgnoreCase);
        var back = JsonSerializer.Deserialize<GameState>(JsonSerializer.Serialize(s, QuartermasterJson.Options), QuartermasterJson.Options)!;
        Assert.Equal("r1", back.Pending[1].Seal);
        Assert.Equal("7", back.Pending[0].SealedAnswer!["bid"]);

        s.Apply(new SealAnswer("nope", Bid7, "ignored"));   // an unknown run is ignored
        Assert.DoesNotContain(s.Log, l => l.Text == "ignored");
    }

    [Fact]
    public void Sealed_answer_in_own_view_only()
    {
        var s = Sealed();
        s.Apply(new SealAnswer("r1", Bid7, "Northern League has answered Toll bid (sealed)"));
        var south = PlayerView.For(s, Pack(), RunnerFx.Model, South);
        Assert.DoesNotContain(south.Pending, r => r.SealedAnswer != null);
        Assert.DoesNotContain(south.Pending, r => r.Id == "r1");
        Assert.Contains(south.Log, l => l.Text == "Northern League has answered Toll bid (sealed)");   // the public line names no value
        Assert.DoesNotContain(JsonSerializer.Serialize(south, QuartermasterJson.Options), "\"bid\"");
        var north = PlayerView.For(s, Pack(), RunnerFx.Model, North);
        Assert.Equal("7", north.Pending.Single(r => r.Id == "r1").SealedAnswer!["bid"]);
    }

    [Fact]
    public void Sealed_answer_in_every_view_at_game_over()
    {
        var s = Sealed();
        s.Apply(new SealAnswer("r1", Bid7, "Northern League has answered Toll bid (sealed)"));
        s.GameOver = new GameOverState("Draw", null, null, s.Position);
        var south = PlayerView.For(s, Pack(), RunnerFx.Model, South);
        Assert.Equal("7", south.Pending.Single(r => r.Id == "r1").SealedAnswer!["bid"]);
    }

    [Fact]
    public void Answered_sealed_run_not_a_decision()
    {
        var s = Sealed();
        Assert.Equal(2, Decisions.Compute(Pack(), s).Count(d => d.Kind == DecisionKind.Pending));
        s.Apply(new SealAnswer("r1", Bid7, "Northern League has answered Toll bid (sealed)"));
        var left = Decisions.Compute(Pack(), s).Where(d => d.Kind == DecisionKind.Pending).ToList();
        Assert.Equal("r2", Assert.Single(left).RunId);
    }

    private static FormulaTrace Who(RulesPack pack, string? askSide) =>
        new FormulaRunner(pack, RunnerFx.Model, (n, _) => Enumerable.Repeat(1, n).ToList()).Run(new GameState { GameId = "g" },
            new RunRequest("f-who", null, new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>())
            { AskSideId = askSide });

    [Fact]
    public void Askside_in_run_is_side_name()
    {
        var t = Who(Pack(), "south");
        Assert.Null(t.Error);
        Assert.Equal("Southern Compact", t.Result!.Value.Display());
        Assert.Equal("north", Who(Pack(northName: " "), "north").Result!.Value.Display());   // a blank name reads as the id
    }

    [Fact]
    public void Askside_outside_ask_run_empty()
    {
        var t = Who(Pack(), null);
        Assert.Null(t.Error);
        Assert.Equal("", t.Result!.Value.Display());
        // A filter or phase condition has no answering side either.
        Assert.Equal("", PieceFilterAskside());
    }

    private static string PieceFilterAskside() =>
        PhaseConditions.Evaluate(Pack(), new GameState { GameId = "g" }, "askside()", new PhasePosition(1, 1, "ph-toll", 0), null).Display();
}
