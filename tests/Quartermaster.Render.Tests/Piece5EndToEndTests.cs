using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>Piece 5 end to end: invented PhaseFixture module and pack, a user alert and an outliner column, temp roots only, no UI.</summary>
public class Piece5EndToEndTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-p5e2e-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public Piece5EndToEndTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static RulesPack WithPiece5(RulesPack p) => p with
    {
        Properties = p.Properties.Select(x => x.Id == "fuel" ? x with { Outliner = OutlinerAggregate.Sum } : x).ToList(),
        Alerts = new() { new AlertDef { Id = "low-fuel", Name = "Low fuel", Condition = "Fuel < 50", Message = "{unit} is low on fuel", Severity = AlertSeverity.Warning } },
    };

    private (ModuleSession S, PhaseController C, HistoryController H) Make()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto);
        s.NewRules("t");
        s.UpdateRules(_ => WithPiece5(PhaseFixture.Pack(s.Archive.Sha256)));
        Func<GameCommand, GameState, GameCommand> prepare = (c, _) => c;
        var phases = new PhaseController(s, prepare);
        return (s, phases, new HistoryController(s, phases, prepare));
    }

    private sealed record View(int Hits, int Matches, double? FuelSum, string Breakdown);

    private static View Look(ModuleSession s)
    {
        var g = s.Game!;
        var hits = AlertEngine.Evaluate(g.State, s.Rules, s.Model, null, null).Where(h => h.Id == "low-fuel").ToList();
        var tree = OutlinerModel.Build(g.State, s.Rules, s.Model, OutlinerGrouping.OrgDisplays, hits);
        var col = tree.Columns.ToList().FindIndex(c => c.PropertyId == "fuel");
        Assert.True(col >= 0, "outliner has a Fuel column");
        double? Sum(IEnumerable<OutlinerNode> nodes) => nodes.Select(n => n.Values[col]).Where(v => v != null).Sum(v => v!.Value);
        var breakdown = LedgerBreakdown.Format(LedgerBreakdown.For(g.State, s.Rules, s.Model, LedgerKey.Piece(PhaseFixture.RedOne), "fuel"));
        return new View(hits.Count, hits.Sum(h => h.Matches.Count), Sum(tree.Roots), breakdown);
    }

    [Fact]
    public void E2E_FullyAuto_AlertsOutlinerBreakdown()
    {
        var (s, c, h) = Make();
        var g = s.Game!;
        c.Start();
        var steps = 0;
        var movedInTurn2 = false;
        while (!movedInTurn2 && steps++ < 60)
        {
            if (g.State.Position!.PhaseId == "ph-move")
            {
                c.ExecuteMove(new[] { new MovePieces(new[] { PhaseFixture.RedOne }, "Main Map", new PointD(60 + steps, 50), null) });
                movedInTurn2 = g.State.Position.Turn == 2;
            }
            c.Advance();
        }
        Assert.True(movedInTurn2);
        Assert.Equal(2, g.State.Position!.Turn);

        var played = Look(s);
        Assert.Equal(1, played.Hits);
        Assert.True(played.Matches >= 1, "the Fuel < 50 alert has at least one match");
        Assert.NotNull(played.FuelSum);
        Assert.Contains("Start of turn 2", played.Breakdown);

        // Step back one phase: all three follow the rewound state.
        var json = System.Text.Json.JsonSerializer.Serialize(g.State, QuartermasterJson.Options);
        var row = h.Checkpoints()[0];
        h.StepBack(row);
        Assert.Contains(g.Log, x => x is SteppedBack);
        var back = Look(s);
        Assert.NotEqual(json, System.Text.Json.JsonSerializer.Serialize(g.State, QuartermasterJson.Options));
        // Rewound to the start of turn 2's movement phase, before Red One's move: same alert and total (the move cost no fuel),
        // but the breakdown has lost the turn-2 movement entry and its "Start of turn 2" header.
        Assert.Equal(new PhasePosition(2, 1, "ph-move", 2), g.State.Position);
        Assert.Equal(1, back.Hits);
        Assert.Equal(3, back.Matches);
        Assert.Equal(118, back.FuelSum);
        Assert.Equal(new[] { "Fuel 38  (Red 1)", "  Earlier turns: net +38" }, back.Breakdown.Split('\n'));
        Assert.Equal(new[] { "Fuel 38  (Red 1)", "    Earlier turns: net +38", "38  Start of turn 2", "0   Fuel for movement  Turn 2 · Movement" },
            played.Breakdown.Split('\n'));

        // Redo restores the played view exactly.
        for (var i = 0; i < 500 && h.CanRedo; i++) h.Redo();
        Assert.Equal(played, Look(s));

        // Save and load keep the alert, the column and the numbers.
        var path = Path.Combine(_tmp, "g.qsave");
        s.SaveNative(path);
        s.LoadNative(path);
        Assert.Contains(s.Rules!.Alerts, a => a.Id == "low-fuel" && a.Condition == "Fuel < 50");
        Assert.Contains(s.Rules.Properties, p => p.Id == "fuel" && p.Outliner == OutlinerAggregate.Sum);
        Assert.Equal(played, Look(s));
    }
}
