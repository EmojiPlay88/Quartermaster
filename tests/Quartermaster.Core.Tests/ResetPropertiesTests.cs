using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>#56: the ResetProperties command and the B2 log/command members that old saves never carry.</summary>
public class ResetPropertiesTests
{
    private static GameState S()
    {
        var s = new GameState { GameId = "g" };
        s.Ledgers["p:a"] = new() { ["fuel"] = 3, ["morale"] = 5 };
        s.Ledgers["p:b"] = new() { ["fuel"] = 1 };
        s.Ledgers["h:depot"] = new() { ["fuel"] = 9, ["ferry"] = 1 };
        s.Ledgers["h:global"] = new() { ["ferry"] = 2 };
        return s;
    }

    [Fact]
    public void Reset_deletes_on_pieces_and_holders_and_logs_line()
    {
        var s = S();
        s.Apply(new LedgerDelta("p:a", "fuel", 3, 4, "manual", null, null, null, null));
        var history = s.LedgerHistory.Count;
        s.Apply(new ResetProperties(new[] { "fuel", "ferry" }, "Dawn: reset Fuel, Ferry used", Who: "Ann"));
        Assert.Equal(new[] { "morale" }, s.Ledgers["p:a"].Keys.ToArray());
        Assert.False(s.Ledgers.ContainsKey("h:depot"));
        Assert.Equal(history, s.LedgerHistory.Count);   // history is kept
        var line = s.Log[^1];
        Assert.Equal((LogKind.System, "Dawn: reset Fuel, Ferry used", "Ann", (string?)null), (line.Kind, line.Text, line.Who, line.PieceId));
        Assert.Equal("Reset values", ActionLabels.For(new ResetProperties(new[] { "fuel" }, "x"), 1));
    }

    [Fact]
    public void Reset_drops_empty_ledger()
    {
        var s = S();
        s.Apply(new ResetProperties(new[] { "fuel", "ferry" }, "Dawn: reset Fuel, Ferry used"));
        Assert.Equal(new[] { "p:a" }, s.Ledgers.Keys.ToArray());
        s.Apply(new ResetProperties(new[] { "nothing" }, "Dawn: reset Nothing"));   // an id held nowhere changes nothing
        Assert.Equal(new[] { "p:a" }, s.Ledgers.Keys.ToArray());
        Assert.Equal(2, s.Log.Count);
    }

    [Fact]
    public void Reset_line_names_properties_only()
    {
        var pack = new RulesPack
        {
            Format = 4,
            Sides = new()
            {
                new SideDef { Id = "red", Name = "Red", UnitFilter = "marker(\"Side\") == \"Red\"" },
                new SideDef { Id = "blue", Name = "Blue", UnitFilter = "marker(\"Side\") == \"Blue\"" },
            },
            Properties = new() { new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, Visibility = Visibility.Private } },
            Phases = new() { new PhaseDef { Id = "dawn", Name = "Dawn" } },
        };
        var s = new GameState { GameId = "g" };
        s.Apply(new SetPlayers(new[] { new PlayerDef("p1", "Ann", new[] { "red" }), new PlayerDef("p2", "Bob", new[] { "blue" }) }));
        s.Apply(new AddPiece("r1", PieceTypeParser.EncodeState(new[] { "mark;Side", "piece;;;unit.svg;Rifle" }), PieceTypeParser.EncodeState(new[] { "Red", "" }),
            "Main Map", new PointD(50, 50), null, "Rifle placed"));
        s.Apply(new LedgerDelta("p:r1", "fuel", 0, 17, "manual", null, null, null, null));
        s.Apply(new AdvancePhase(null, new PhasePosition(1, 1, "dawn", 0), "", "Dawn"));
        s.Apply(new ResetProperties(new[] { "fuel" }, "Dawn: reset Fuel"));
        var blue = PlayerView.For(s, pack, RunnerFx.Model, new HashSet<string> { "blue" });
        var line = blue.Log[^1];
        Assert.Equal((LogKind.System, "Dawn: reset Fuel", (string?)null, (string?)null), (line.Kind, line.Text, line.PieceId, line.SideId));
        Assert.DoesNotContain("17", JsonSerializer.Serialize(blue.Log, QuartermasterJson.Options));
    }

    [Fact]
    public void Old_commands_and_log_serialise_unchanged()
    {
        var o = QuartermasterJson.Options;
        GameCommand delta = new LedgerDelta("p:a", "fuel", 1, 2, "formula", "f", null, null, "w", PieceId: "a");
        GameCommand line = new AddLogLine(LogKind.Calc, "w", "a", FormulaId: "f");
        var entry = new LogEntry(1, LogKind.Formula, "w", null, "a", null, DateTimeOffset.UnixEpoch, 1, "Dawn", FormulaId: "f");
        foreach (var json in new[] { JsonSerializer.Serialize(delta, o), JsonSerializer.Serialize(line, o), JsonSerializer.Serialize(entry, o) })
        {
            Assert.DoesNotContain("appliedRun", json);
            Assert.DoesNotContain("stage", json);
        }
        Assert.Equal(
            "{\"$t\":\"ledgerDelta\",\"key\":\"p:a\",\"propertyId\":\"fuel\",\"before\":1,\"after\":2,\"source\":\"formula\",\"formulaId\":\"f\",\"ruleRefId\":null,\"note\":null,\"working\":\"w\",\"at\":null,\"who\":null,\"tag\":null,\"subject\":null,\"pieceId\":\"a\",\"sideId\":null}",
            JsonSerializer.Serialize(delta, o));

        // The new members round-trip when set; the new command has its own discriminator.
        var stamped = JsonSerializer.Serialize<GameCommand>(new LedgerDelta("p:a", "fuel", 1, 2, "formula", "f", null, null, "w") { AppliedRun = "f" }, o);
        Assert.Contains("\"appliedRun\":\"f\"", stamped);
        Assert.Equal("f", ((LedgerDelta)JsonSerializer.Deserialize<GameCommand>(stamped, o)!).AppliedRun);
        var stampedLine = JsonSerializer.Serialize<GameCommand>(new AddLogLine(LogKind.Calc, "w") { AppliedRun = "f" }, o);
        Assert.Equal("f", ((AddLogLine)JsonSerializer.Deserialize<GameCommand>(stampedLine, o)!).AppliedRun);
        var stampedEntry = JsonSerializer.Serialize(entry with { AppliedRun = "f", Stage = 2 }, o);
        var back = JsonSerializer.Deserialize<LogEntry>(stampedEntry, o)!;
        Assert.Equal(("f", (int?)2), (back.AppliedRun, back.Stage));
        var reset = JsonSerializer.Serialize<GameCommand>(new ResetProperties(new[] { "fuel" }, "Dawn: reset Fuel"), o);
        Assert.StartsWith("{\"$t\":\"resetProperties\"", reset);
        Assert.Equal(new[] { "fuel" }, ((ResetProperties)JsonSerializer.Deserialize<GameCommand>(reset, o)!).PropertyIds);
    }
}
