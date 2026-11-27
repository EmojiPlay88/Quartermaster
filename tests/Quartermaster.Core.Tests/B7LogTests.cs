using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;
using static Quartermaster.Core.Tests.HiddenValuesTests;

namespace Quartermaster.Core.Tests;

/// <summary>B7 #74d (private dice by formula id) and the side stamp on tick and acknowledge lines. Invented Riverland data.</summary>
public class B7LogTests
{
    [Fact]
    public void Save_without_b7_members_keeps_bytes()
    {
        // Commands as the pre-B7 serializer wrote them (no formulaId / sideId on tick and acknowledge, no formulaId on a roll).
        const string roll = "{\"$t\":\"rollDice\",\"purpose\":\"Scout: R\",\"count\":1,\"sides\":6,\"results\":[4],\"expression\":null,\"faceTexts\":null,\"modifier\":0,\"at\":null,\"who\":null,\"pieceId\":null,\"sideId\":null}";
        const string tick = "{\"$t\":\"tickChecklist\",\"index\":0,\"ticked\":true,\"item\":\"Muster\",\"at\":null,\"who\":null}";
        const string ack = "{\"$t\":\"acknowledgeWarning\",\"index\":0,\"message\":\"Low stores\",\"at\":null,\"who\":null}";
        foreach (var literal in new[] { roll, tick, ack })
        {
            var cmd = JsonSerializer.Deserialize<GameCommand>(literal, QuartermasterJson.Options)!;
            Assert.Equal(literal, JsonSerializer.Serialize(cmd, QuartermasterJson.Options));
        }
        var s = new GameState { GameId = "g1" };
        foreach (var literal in new[] { roll, tick, ack }) s.Apply(JsonSerializer.Deserialize<GameCommand>(literal, QuartermasterJson.Options)!);
        s.Apply(new QueueRuns(new[] { new PendingRun("r1", "f-haul", "Haul", "start", new[] { new PendingUnit("u1", "North 1", null, false) }, true) { AskSideId = North } }));
        var json = JsonSerializer.Serialize(s, QuartermasterJson.Options);
        Assert.Contains("\"diceHistory\":[{\"seq\":1,\"purpose\":\"Scout: R\",\"count\":1,\"sides\":6,\"results\":[4]}]", json);
        Assert.DoesNotContain("formulaId\":\"f-", json.Replace("\"formulaId\":\"f-haul\"", ""));
        var back = JsonSerializer.Deserialize<GameState>(json, QuartermasterJson.Options)!;
        Assert.Equal(json, JsonSerializer.Serialize(back, QuartermasterJson.Options));
    }

    private static readonly IReadOnlySet<string> SouthOnly = new HashSet<string> { South };
    private static readonly IReadOnlySet<string> NorthOnly = new HashSet<string> { North };
    private static ModuleModel Model => RunnerFx.Model;

    private static RulesPack DicePack()
    {
        var pk = Pack();
        pk.Formulas.Add(new FormulaDef { Id = "f-scout-secret", Name = "Scout", Text = "R", PrivateRolls = true,
            Bindings = new() { new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 } } });
        pk.Formulas.Add(new FormulaDef { Id = "f-scout-open", Name = "Scout", Text = "R", PrivateRolls = false,
            Bindings = new() { new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 } } });
        return pk;
    }

    private static GameState NewState()
    {
        var s = new GameState { GameId = "g1" };
        s.Apply(new SetPlayers(new[] { new PlayerDef("p1", "Ann", new[] { North }), new PlayerDef("p2", "Bob", new[] { South }) }));
        return s;
    }

    private static void Roll(GameState s, string purpose, int result, string? formulaId) =>
        s.Apply(new RollDice(purpose, 1, 6, new[] { result }, SideId: North) { FormulaId = formulaId });

    private static List<LogEntry> DiceLines(GameState v) => v.Log.Where(l => l.Kind == LogKind.Dice).ToList();

    [Fact]
    public void Formula_roll_carries_formula_id()
    {
        var pk = DicePack();
        var s = NewState();
        var runner = new FormulaRunner(pk, Model, (n, sides) => Enumerable.Repeat(3, n).ToList());
        var t = runner.Run(s, new RunRequest("f-scout-secret", null, new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<int>>()));
        Assert.Null(t.Error);
        var cmds = runner.Commands(t, null, null, null, North);
        Assert.All(cmds.OfType<RollDice>(), r => Assert.Equal("f-scout-secret", r.FormulaId));
        Assert.NotEmpty(cmds.OfType<RollDice>());
        foreach (var c in cmds) s.Apply(c);
        Assert.Equal("f-scout-secret", s.DiceHistory.Single().FormulaId);
        Assert.Equal("f-scout-secret", DiceLines(s).Single().FormulaId);
    }

    [Fact]
    public void Private_dice_by_id_same_name_public_formula_shown()
    {
        var pk = DicePack();
        var s = NewState();
        Roll(s, "Scout: R", 4, "f-scout-secret");
        Roll(s, "Scout: R", 5, "f-scout-open");
        var v = PlayerView.For(s, pk, Model, SouthOnly);
        Assert.Empty(v.DiceHistory[0].Results);
        Assert.Equal("Northern League rolled dice", v.DiceHistory[0].Purpose);
        Assert.Equal(new[] { 5 }, v.DiceHistory[1].Results);
        var lines = DiceLines(v);
        Assert.Equal("Northern League rolled dice", lines[0].Text);
        Assert.Contains("5", lines[1].Text);
        Assert.DoesNotContain("= 4", JsonSerializer.Serialize(v, QuartermasterJson.Options));
        var own = PlayerView.For(s, pk, Model, NorthOnly);
        Assert.Equal(new[] { 4 }, own.DiceHistory[0].Results);
    }

    [Fact]
    public void Private_dice_renamed_formula_still_private()
    {
        var pk = DicePack();
        var s = NewState();
        Roll(s, "Reconnaissance: R", 4, "f-scout-secret");   // logged under an earlier name
        var v = PlayerView.For(s, pk, Model, SouthOnly);
        Assert.Empty(v.DiceHistory.Single().Results);
        Assert.Equal("Northern League rolled dice", DiceLines(v).Single().Text);
    }

    [Fact]
    public void Private_dice_unknown_formula_id_hidden()
    {
        var pk = DicePack();
        var s = NewState();
        Roll(s, "Gone: R", 4, "f-deleted");
        var v = PlayerView.For(s, pk, Model, SouthOnly);
        Assert.Empty(v.DiceHistory.Single().Results);
        Assert.Equal("Northern League rolled dice", DiceLines(v).Single().Text);
    }

    [Fact]
    public void Old_log_prefix_rule_kept()
    {
        var pk = DicePack();
        var s = NewState();
        Roll(s, "Scout: R", 4, null);   // a log from before formula ids: the name prefix decides (the private "Scout" matches)
        Roll(s, "Parley: R", 2, null);
        var v = PlayerView.For(s, pk, Model, SouthOnly);
        Assert.Empty(v.DiceHistory[0].Results);
        Assert.Equal(new[] { 2 }, v.DiceHistory[1].Results);
        Assert.Equal("Northern League rolled dice", DiceLines(v)[0].Text);
    }

    [Fact]
    public void Dice_tool_roll_unchanged()
    {
        var pk = DicePack();
        var s = NewState();
        s.Apply(new RollDice("Dice tool", 2, 6, new[] { 3, 5 }, Expression: "2d6"));
        var v = PlayerView.For(s, pk, Model, SouthOnly);
        Assert.Equal(new[] { 3, 5 }, v.DiceHistory.Single().Results);
        Assert.Contains("2d6 = 3 + 5 = 8", DiceLines(v).Single().Text);
        Assert.Null(s.DiceHistory.Single().FormulaId);
        Assert.Null(DiceLines(s).Single().FormulaId);
    }

    private static RulesPack SidedPhases() => Pack();

    [Fact]
    public void Tick_line_stamped_hidden_from_other_side()
    {
        var pk = SidedPhases();
        var s = NewState();
        s.Apply(new TickChecklist(0, true, "Muster") { SideId = North });
        Assert.Equal(North, s.Log.Single(l => l.Kind == LogKind.Turn).SideId);
        Assert.Contains(PlayerView.For(s, pk, Model, NorthOnly).Log, l => l.Text == "Done: Muster");
        var v = PlayerView.For(s, pk, Model, SouthOnly);
        Assert.DoesNotContain(v.Log, l => l.Kind == LogKind.Turn);
        Assert.DoesNotContain("Muster", JsonSerializer.Serialize(v.Log, QuartermasterJson.Options));
    }

    [Fact]
    public void Tick_line_side_less_phase_public()
    {
        var pk = SidedPhases();
        var s = NewState();
        s.Apply(new TickChecklist(0, true, "Muster"));
        var v = PlayerView.For(s, pk, Model, SouthOnly);
        Assert.Contains(v.Log, l => l.Text == "Done: Muster");
        Assert.Null(s.Log.Single(l => l.Kind == LogKind.Turn).SideId);
    }

    [Fact]
    public void Ack_line_stamped_hidden_from_other_side()
    {
        var pk = SidedPhases();
        var s = NewState();
        s.Apply(new AcknowledgeWarning(0, "Low stores") { SideId = North });
        Assert.Equal(North, s.Log.Single(l => l.Kind == LogKind.Turn).SideId);
        Assert.Contains(PlayerView.For(s, pk, Model, NorthOnly).Log, l => l.Text == "Acknowledged: Low stores");
        Assert.DoesNotContain(PlayerView.For(s, pk, Model, SouthOnly).Log, l => l.Kind == LogKind.Turn);
    }

    [Fact]
    public void Old_unstamped_tick_line_kept()
    {
        var pk = SidedPhases();
        var s = NewState();
        s.Apply(new TickChecklist(0, true, "Muster {item:other}"));   // an old save: raw text, no stamp
        var v = PlayerView.For(s, pk, Model, SouthOnly);
        Assert.Contains(v.Log, l => l.Text == "Done: Muster {item:other}");
    }
}
