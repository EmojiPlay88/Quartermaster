using System.Diagnostics;
using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;
using Xunit.Abstractions;

namespace Quartermaster.Render.Tests;

/// <summary>#31: a Fully automatic start entry runs its units in one pass with one change notification. Invented PhaseFixture pack.</summary>
/// <remarks>Not run in parallel with other test classes (<see cref="PhasePipelinePerfCollection"/>): the timing must not measure the rest of the suite.</remarks>
[Collection(PhasePipelinePerfCollection.Name)]
public class PhasePipelinePerfTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-pipeperf-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    /// <summary>One Table binding and one Unit Set output (the benchmark's formula).</summary>
    private static readonly FormulaDef Cost = new()
    {
        Id = "f-cost", Name = "Terrain water", Text = "T",
        Bindings = new() { new VariableBinding { Variable = "T", Kind = BindingKind.Table, TableId = "t-cost", RowExpr = "\"Clear\"" } },
        Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
    };

    /// <summary>Supply (Red): Daily water, then Terrain water, for each unit.</summary>
    private static RulesPack Supply(RulesPack p) => p with
    {
        Formulas = p.Formulas.Append(Cost).ToList(),
        Phases = p.Phases.Select(ph => ph.Id != "ph-supply" ? ph : ph with
        {
            OnStart = new()
            {
                new StartEntry { FormulaId = "f-water", ForEach = ForEachMode.Units },
                new StartEntry { FormulaId = "f-cost", ForEach = ForEachMode.Units },
            },
        }).ToList(),
    };

    /// <summary>A Fully automatic game in Weather, with <paramref name="extra"/> invented Red pieces besides the fixture's.</summary>
    private (ModuleSession S, PhaseController C) Make(int extra = 0, Func<RulesPack, RulesPack>? tweak = null)
    {
        Directory.CreateDirectory(_tmp);
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto);
        s.NewRules("t");
        s.UpdateRules(_ => (tweak ?? Supply)(PhaseFixture.Pack(s.Archive.Sha256)));
        for (var i = 0; i < extra; i++)
            s.Game!.Execute(new AddPiece("x" + i, $"piece;;;unit.svg;Red {i + 10}", "", "Main Map", new PointD(i % 200, i / 200), null));
        var c = new PhaseController(s, (cmd, _) => cmd);
        c.Start();   // Weather
        Assert.Equal("ph-weather", s.Game!.State.Position!.PhaseId);
        return (s, c);
    }

    [Fact]
    public void FullyAuto_entry_raises_changed_once()
    {
        var (s, c) = Make(tweak: p => p);   // Supply: Daily water for each unit (Red 1, Red 2)
        var g = s.Game!;
        var seen = new List<GameCommand>();
        g.Changed += seen.Add;
        var before = g.Log.Count;
        c.Advance();
        var added = g.Log.Skip(before).ToList();
        Assert.Equal(new[] { "AdvancePhase", "LedgerDelta", "LedgerDelta", "AddLogLine" }, added.Select(x => x.GetType().Name).ToArray());
        // The phase advance raises on its own; the entry's units and its summary line raise once, with the summary.
        Assert.True(seen.Count == 2, string.Join(", ", seen.Select(x => x.GetType().Name)));
        Assert.IsType<AdvancePhase>(seen[0]);
        Assert.Same(added[^1], seen[1]);
        Assert.Equal("Daily water: 2 units, 2 applied", ((AddLogLine)seen[1]).Text);
        Assert.Equal(9, Ledger.Get(g.State, LedgerKey.Piece(PhaseFixture.RedTwo), "water"));
    }

    [Fact]
    public void FullyAuto_entry_log_unchanged_by_pass()
    {
        var (a, ca) = Make();
        var before = a.Game!.Log.Count;
        ca.Advance();
        var golden = Golden();
        Assert.Equal(Json(golden.Game!.Log.Skip(before)), Json(a.Game.Log.Skip(before)));
        Assert.Equal(Json(Untimed(golden.Game.State.Log)), Json(Untimed(a.Game.State.Log)));
        Assert.Equal(Json(golden.Game.State.Ledgers), Json(a.Game.State.Ledgers));
        Assert.Equal(golden.Game.Actions.Count, a.Game.Actions.Count);
    }

    /// <summary>The same press built by hand with plain execution, one command at a time, no pass (the pre-B2 path's commands and stamps).</summary>
    private ModuleSession Golden()
    {
        var (s, _) = Make();
        var g = s.Game!;
        var pack = s.Rules!;
        var none = new Dictionary<string, string>();
        var noRolls = new Dictionary<string, IReadOnlyList<int>>();
        var supply = pack.Phases.First(p => p.Id == "ph-supply");
        using (g.BeginAction("Next Phase"))
        {
            var from = g.State.Position!;
            g.Execute(new AdvancePhase(from, new PhasePosition(from.Turn, 1, "ph-supply", 1), "Weather", "Supply"));
            foreach (var id in new[] { "f-water", "f-cost" })
            {
                var f = pack.ById<FormulaDef>(id)!;
                var units = Sequencer.UnitsFor(pack, g.State, supply, f, null, s.Model);
                foreach (var p in units)
                {
                    var runner = new FormulaRunner(pack, s.Model, (n, _) => Enumerable.Repeat(3, n).ToList());
                    var t = runner.Run(g.State, new RunRequest(id, p.Id, none, none, noRolls, null, true));
                    foreach (var cmd in runner.Commands(t, null, "auto", PieceInfo.Name(p), null)) g.Execute(cmd);   // solo: no stamp
                }
                g.Execute(new AddLogLine(LogKind.Calc, $"{f.Name}: {units.Count} units, {units.Count} applied", FormulaId: LogKind.NoWorking));
            }
        }
        return s;
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    /// <summary>The log lines without their wall-clock time (each session stamps its own).</summary>
    private static List<LogEntry> Untimed(IEnumerable<LogEntry> log) => log.Select(e => e with { At = default }).ToList();
    private static string Json(IEnumerable<GameCommand> log) => JsonSerializer.Serialize(log.ToList());

    [Fact]
    [Trait("Category", "Perf")]
    public void Perf_StartEntry_1000Units()
    {
#if DEBUG
        output.WriteLine("skipped (Debug build or debugger)");
        return;
#else
        if (Debugger.IsAttached) { output.WriteLine("skipped (Debug build or debugger)"); return; }
        var (s, c) = Make(1000, p => p with
        {
            Formulas = p.Formulas.Append(Cost).ToList(),
            Phases = p.Phases.Select(ph => ph.Id != "ph-supply" ? ph : ph with
            {
                OnStart = new() { new StartEntry { FormulaId = "f-cost", ForEach = ForEachMode.Units } },
            }).ToList(),
        });
        var g = s.Game!;
        var n = Sequencer.UnitsFor(s.Rules!, g.State, s.Rules!.Phases.First(p => p.Id == "ph-supply"), Cost, null, s.Model).Count;
        Assert.Equal(1002, n);
        double Pass()
        {
            var sw = Stopwatch.StartNew();
            c.Advance();
            sw.Stop();
            Assert.Equal("ph-supply", g.State.Position!.PhaseId);
            Assert.Contains(g.State.Log, e => e.Text == $"Terrain water: {n} units, {n} applied");
            g.Undo();
            Assert.Equal("ph-weather", g.State.Position!.PhaseId);
            return sw.Elapsed.TotalMilliseconds;
        }
        Pass();   // warm-up
        var best = Enumerable.Range(0, 3).Select(_ => Pass()).Min();
        var perUnit = best / n;
        output.WriteLine(FormattableString.Invariant($"Start entry, {n} units: {perUnit:0.000} ms per unit (best of 3)"));
        var budget = Environment.GetEnvironmentVariable("CI") == "true" ? 1.0 : 0.2;
        Assert.True(perUnit < budget, $"{perUnit:0.000} ms per unit, budget {budget} ms");
#endif
    }
}

/// <summary>Runs <see cref="PhasePipelinePerfTests"/> after the parallel classes, alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PhasePipelinePerfCollection
{
    public const string Name = "Phase pipeline perf (serial)";
}
