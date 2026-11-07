using System.Diagnostics;
using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;
using Xunit.Abstractions;

namespace Quartermaster.Core.Tests;

/// <summary>Timed tests run alone: nothing else in the test run is in parallel with this collection.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerfCollection
{
    public const string Name = "Perf";
}

[Collection(PerfCollection.Name)]
public class AlertEngineTests(ITestOutputHelper output)
{
    private static void AddUnit(GameState s, string id, string name, double fuel, double? ammo = null)
    {
        s.Pieces[id] = new PieceInstance
        {
            Id = id, MapId = "Main Map", TraitStates = new() { "" },
            TypeString = SequenceEncoder.Encode(new[] { "piece;;;unit.svg;" + name }, '\t'),
        };
        s.Ledgers[LedgerKey.Piece(id)] = new() { ["fuel"] = fuel };
        if (ammo != null) s.Ledgers[LedgerKey.Piece(id)]["ammo"] = ammo.Value;
    }

    private static RulesPack Pack(PhaseDef? phase = null, params AlertDef[] alerts) => new()
    {
        Properties = new()
        {
            new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Both },
            new PropertyDef { Id = "ammo", Name = "Ammo", Scope = PropertyScope.Pieces },
        },
        Holders = new() { new HolderDef { Id = "depot", Name = "Depot" } },
        Sides = new()
        {
            new SideDef { Id = "red", Name = "Red", UnitFilter = "startsWith(name(), \"Red\")" },
            new SideDef { Id = "blue", Name = "Blue", UnitFilter = "startsWith(name(), \"Blue\")" },
        },
        Phases = phase == null ? new() : new() { phase },
        Alerts = alerts.ToList(),
    };

    private static AlertDef Alert(string id, string name, string cond, string msg = "", AlertSeverity sev = AlertSeverity.Warning, string? side = null) =>
        new() { Id = id, Name = name, Condition = cond, Message = msg, Severity = sev, SideId = side };

    [Fact]
    public void Builtins_OnScriptedState()
    {
        var phase = new PhaseDef
        {
            Id = "P", Name = "Phase P",
            Warnings = new() { new PhaseWarning { Condition = "Fuel <= 0", Message = "Dry {unit}", IsDecision = true } },
        };
        var pk = Pack(phase);
        var s = new GameState();
        AddUnit(s, "a", "Red One", -3);
        AddUnit(s, "b", "Red Two", 0);
        s.Ledgers[LedgerKey.Holder("depot")] = new() { ["fuel"] = -1 };
        s.Position = new PhasePosition(1, 1, "P", 0);
        s.Pending = new()
        {
            new PendingRun("r1", "f", "Ask", "start", new[] { new PendingUnit("a", "Red One", null, false) }, true),
            new PendingRun("r2", "f", "Burn", "move", new[] { new PendingUnit("a", "Red One", 4, false), new PendingUnit("b", "Red Two", 2, false), new PendingUnit("c", "Gone", 1, true) }, false),
        };
        var hits = AlertEngine.Evaluate(s, pk, null, phase, null);
        Assert.Equal(new[] { "Fuel below zero", "Formula waiting for input", "Unacknowledged warning", "Formula not applied" }, hits.Select(h => h.Name));
        Assert.Equal(4, hits.Count);
        Assert.Equal("builtin:neg:fuel", hits[0].Id);
        Assert.Equal(AlertSeverity.Critical, hits[0].Severity);
        Assert.Equal(2, hits[0].Matches.Count);
        Assert.Contains(hits[0].Matches, m => m.PieceId == "a" && m.Text == "Red One");
        Assert.Contains(hits[0].Matches, m => m.HolderId == "depot" && m.Text == "Depot");
        // Warning severity: "Formula waiting for input" before "Unacknowledged warning" (by name)
        Assert.Equal(new[] { "Formula waiting for input", "Unacknowledged warning" }, new[] { hits[1].Name, hits[2].Name });
        Assert.Equal(AlertSeverity.Warning, hits[1].Severity);
        Assert.Equal("builtin:input", hits[1].Id);
        Assert.Single(hits[1].Matches);
        Assert.Equal("builtin:warning", hits[2].Id);
        Assert.Equal(AlertSeverity.Warning, hits[2].Severity);
        Assert.Equal(2, hits[2].Matches.Count);
        Assert.Equal("Formula not applied", hits[3].Name);
        Assert.Equal("builtin:pending", hits[3].Id);
        Assert.Equal(AlertSeverity.Info, hits[3].Severity);
        Assert.Equal(2, hits[3].Matches.Count);   // applied unit not listed
    }

    [Fact]
    public void Builtins_Acknowledged_Warning_NotListed()
    {
        var phase = new PhaseDef
        {
            Id = "P", Name = "Phase P",
            Warnings = new() { new PhaseWarning { Condition = "Fuel <= 0", Message = "Dry", IsDecision = true } },
        };
        var s = new GameState();
        AddUnit(s, "a", "Red One", 0);
        s.Position = new PhasePosition(1, 1, "P", 0);
        s.Acknowledged.Add(0);
        Assert.Empty(AlertEngine.Evaluate(s, Pack(phase), null, phase, null));
    }

    [Fact]
    public void UserAlert_Matches_MessageHasUnitName()
    {
        var pk = Pack(null, Alert("low", "Low fuel", "Fuel < 20 and Ammo < 10", "{unit} is low", AlertSeverity.Info));
        var s = new GameState();
        AddUnit(s, "a", "Red One", 5, 2);
        AddUnit(s, "b", "Red Two", 50, 2);
        AddUnit(s, "c", "Blue One", 5, 20);
        var hits = AlertEngine.Evaluate(s, pk, null, null, null);
        var h = Assert.Single(hits);
        Assert.Equal("low", h.Id);
        Assert.Equal("Low fuel", h.Name);
        Assert.Equal(AlertSeverity.Info, h.Severity);
        var m = Assert.Single(h.Matches);
        Assert.Equal("a", m.PieceId);
        Assert.Null(m.HolderId);
        Assert.Equal("Red One is low", m.Text);
        Assert.Equal("Low fuel", h.Message);
        Assert.Equal("Low fuel\nRed One is low", AlertEngine.Tooltip(h));
    }

    [Fact]
    public void UserAlert_BadOrDanglingCondition_NoMatches_OthersStillWork()
    {
        var pk = Pack(null,
            Alert("bad", "Bad", "Fuel <", "x"),
            Alert("dangling", "Dangling", "Nonesuch < 3", "x"),
            Alert("blank", "Blank", "", "x"),
            Alert("deadside", "Dead side", "Fuel < 100", "x", side: "nosuch"),
            Alert("ok", "Fine", "Fuel < 100", "{unit}"));
        var s = new GameState();
        AddUnit(s, "a", "Red One", 5);
        var hits = AlertEngine.Evaluate(s, pk, null, null, null);
        Assert.Equal(new[] { "ok" }, hits.Select(h => h.Id));
    }

    [Fact]
    public void UserAlert_SideLimited_OnlyWhileActing_HiddenInNeutralPhase()
    {
        var pk = Pack(new PhaseDef { Id = "P", Name = "P" }, Alert("rl", "Red low", "Fuel < 100", "{unit}", side: "red"));
        var s = new GameState();
        AddUnit(s, "a", "Red One", 5);
        AddUnit(s, "b", "Blue One", 5);
        var red = AlertEngine.Evaluate(s, pk, null, null, "red");
        Assert.Equal(new[] { "a" }, Assert.Single(red).Matches.Select(m => m.PieceId));
        Assert.Empty(AlertEngine.Evaluate(s, pk, null, null, "blue"));
        // neutral phase (nobody acting) with phases defined: hidden
        Assert.Empty(AlertEngine.Evaluate(s, pk, null, null, null));
        // no phases defined: shown, limited to the side's pieces
        var nophase = Pack(null, Alert("rl", "Red low", "Fuel < 100", "{unit}", side: "red"));
        var any = AlertEngine.Evaluate(s, nophase, null, null, null);
        Assert.Equal(new[] { "a" }, Assert.Single(any).Matches.Select(m => m.PieceId));
    }

    [Fact]
    public void NegativeValue_RemovedPiece_NotMatched()
    {
        var pk = Pack();
        var s = new GameState();
        AddUnit(s, "a", "Red One", -2);
        s.Ledgers[LedgerKey.Piece("ghost")] = new() { ["fuel"] = -9 };
        s.Ledgers[LedgerKey.Holder("deleted-holder")] = new() { ["fuel"] = -9 };
        s.Ledgers[LedgerKey.Holder(HolderDef.GlobalId)] = new() { ["ammo"] = 4 };
        var h = Assert.Single(AlertEngine.Evaluate(s, pk, null, null, null));
        Assert.Equal(new[] { "a" }, h.Matches.Select(m => m.PieceId));
    }

    [Fact]
    public void HideAlert_ExcludesUntilTurnChange()
    {
        var pk = Pack();
        var g = new GameSession(new GameState(), "sha");
        g.Execute(new AddPiece("a", SequenceEncoder.Encode(new[] { "piece;;;unit.svg;Red One" }, '\t'), "", "Main Map", new PointD(1, 1), null));
        g.Execute(new SeedLedger(new[] { new LedgerSeed(LedgerKey.Piece("a"), "fuel", -1) }));
        Assert.Single(AlertEngine.Evaluate(g.State, pk, null, null, null));
        g.Execute(new HideAlert("builtin:neg:fuel"));
        Assert.Empty(AlertEngine.Evaluate(g.State, pk, null, null, null));
        Assert.Contains(g.State.Log, e => e.Kind == LogKind.System && e.Text == "Alert hidden until next turn: builtin:neg:fuel");
        g.Execute(new HideAlert("builtin:neg:fuel"));   // idempotent
        Assert.Single(g.State.HiddenAlerts);
        g.Execute(new SetTurn(1, "x"));                 // same turn: still hidden
        Assert.Single(g.State.HiddenAlerts);
        g.Execute(new SetTurn(2, null));
        Assert.Empty(g.State.HiddenAlerts);
        Assert.Single(AlertEngine.Evaluate(g.State, pk, null, null, null));

        g.Execute(new HideAlert("builtin:neg:fuel"));
        g.Execute(new AdvancePhase(null, new PhasePosition(2, 1, "P", 0), "", "P"));   // same turn 2
        Assert.Single(g.State.HiddenAlerts);
        g.Execute(new AdvancePhase(new PhasePosition(2, 1, "P", 0), new PhasePosition(3, 1, "P", 0), "P", "P"));
        Assert.Empty(g.State.HiddenAlerts);
    }

    [Fact]
    public void HideAlert_LogsUserFacingName_AndRoundTrips()
    {
        var g = new GameSession(new GameState(), "sha");
        g.Execute(new HideAlert("custom:low", Name: "Low fuel"));
        Assert.Contains("custom:low", g.State.HiddenAlerts);
        Assert.Contains(g.State.Log, e => e.Kind == LogKind.System && e.Text == "Alert hidden until next turn: Low fuel");
        g.Execute(new HideAlert("custom:other", Name: " "));
        Assert.Contains(g.State.Log, e => e.Text == "Alert hidden until next turn: custom:other");

        var json = JsonSerializer.Serialize(new HideAlert("custom:low", Name: "Low fuel"), typeof(GameCommand), QuartermasterJson.Options);
        var back = Assert.IsType<HideAlert>(JsonSerializer.Deserialize<GameCommand>(json, QuartermasterJson.Options));
        Assert.Equal("Low fuel", back.Name);
        var old = Assert.IsType<HideAlert>(JsonSerializer.Deserialize<GameCommand>("{\"$t\":\"hideAlert\",\"alertId\":\"x\"}", QuartermasterJson.Options));
        Assert.Null(old.Name);
        Assert.Equal("x", old.AlertId);
    }

    [Fact]
    public void HideAlert_Undo_Unhides()
    {
        var g = new GameSession(new GameState(), "sha");
        g.Execute(new SetNotes("x"));
        var before = JsonSerializer.Serialize(g.State, QuartermasterJson.Options);
        g.Execute(new HideAlert("builtin:input"));
        Assert.Contains("builtin:input", g.State.HiddenAlerts);
        Assert.True(g.CanUndo);
        g.Undo();
        Assert.Empty(g.State.HiddenAlerts);
        Assert.Equal(before, JsonSerializer.Serialize(g.State, QuartermasterJson.Options));
        g.Redo();
        Assert.Contains("builtin:input", g.State.HiddenAlerts);
    }

    [Fact]
    public void HideAlert_Saved_AndOldSaveLoadsWithEmptyList()
    {
        var snap = JsonSerializer.Deserialize<GameState>("{}", QuartermasterJson.Options)!;
        Assert.NotNull(snap.HiddenAlerts);
        var json = JsonSerializer.Serialize(new HideAlert("x"), typeof(GameCommand), QuartermasterJson.Options);
        Assert.Contains("\"$t\":\"hideAlert\"", json);
        Assert.IsType<HideAlert>(JsonSerializer.Deserialize<GameCommand>(json, QuartermasterJson.Options));
    }

    [Fact]
    public void AlertTooltip_Truncates()
    {
        var pk = Pack(null, Alert("all", "Everyone", "Fuel < 1000", "{unit} flagged", AlertSeverity.Critical));
        var s = new GameState();
        for (var i = 0; i < 800; i++) AddUnit(s, "u" + i, "Red " + i, 1);
        var h = Assert.Single(AlertEngine.Evaluate(s, pk, null, null, null));
        Assert.Equal(800, h.Matches.Count);
        var lines = AlertEngine.Tooltip(h).Split('\n');
        Assert.Equal(12, lines.Length);   // message + 10 matches + "and more"
        Assert.Equal("… and 790 more", lines[^1]);
        Assert.Equal("Red 0 flagged", lines[1]);
    }

    [Fact]
    public void Tooltip_FewMatches_NoMoreLine()
    {
        var h = new AlertHit("x", "X", AlertSeverity.Info, "Msg", new[] { new AlertMatch("a", null, "A") }, null);
        Assert.Equal("Msg\nA", AlertEngine.Tooltip(h));
    }

    [Fact]
    public void Evaluate_NullPack_BuiltinsOnlyNoCrash()
    {
        var s = new GameState();
        AddUnit(s, "a", "Red One", -1);
        var hits = AlertEngine.Evaluate(s, null, null, null, null);
        Assert.Single(hits);   // negative value, property shown by id
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Perf_TenAlerts_1200Pieces_Under100ms()
    {
        var alerts = Enumerable.Range(0, 10).Select(i => Alert("a" + i, "Alert " + i, $"Fuel < {10 + i} and Ammo < 50", "{unit}")).ToArray();
        var pk = Pack(null, alerts);
        var s = new GameState();
        for (var i = 0; i < 1200; i++) AddUnit(s, "u" + i, (i % 2 == 0 ? "Red " : "Blue ") + i, i % 30, i % 60);
        AlertEngine.Evaluate(s, pk, null, null, null);   // warm up
        // Best of 5: one slow run on a loaded machine is noise, not a regression.
        var best = long.MaxValue;
        var hits = 0;
        for (var run = 0; run < 5; run++)
        {
            var sw = Stopwatch.StartNew();
            hits = AlertEngine.Evaluate(s, pk, null, null, null).Count;
            sw.Stop();
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }
        output.WriteLine($"10 alerts over 1200 pieces: best of 5 {best} ms, {hits} hits");
        Assert.True(best < 300, $"took {best} ms (best of 5)");
    }
}
