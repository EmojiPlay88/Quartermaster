using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>Invented pack, formulas and numbers from PhaseFixture; temp roots only.</summary>
public class BatchModelTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-batch-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public BatchModelTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private (ModuleSession S, PhaseController C) Make(Difficulty d, Func<RulesPack, RulesPack>? tweak = null)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], d);
        s.NewRules("t");
        s.UpdateRules(_ => { var p = PhaseFixture.Pack(s.Archive.Sha256); return tweak == null ? p : tweak(p); });
        return (s, new PhaseController(s, (c, _) => c));
    }

    private static MovePieces Move(string id, int x) => new(new[] { id }, "Main Map", new PointD(x, 50), null);

    private static (BatchModel B, string RunId) MoveBatch(ModuleSession s, PhaseController c)
    {
        c.Start(); c.Advance(); c.Advance();   // Movement
        c.ExecuteMove(new[] { Move("r1", 130), Move("r2", 180) });
        var run = s.Game!.State.Pending.Single();
        return (new BatchModel(s, c, run.Id), run.Id);
    }

    private static double Fuel(ModuleSession s, string id) => Ledger.Get(s.Game!.State, LedgerKey.Piece(id), "fuel") ?? double.NaN;

    [Fact]
    public void Rows_HardcoreCellsEmpty_CannotApply()
    {
        var (s, c) = Make(Difficulty.Hardcore);
        var (b, _) = MoveBatch(s, c);
        Assert.Equal(2, b.RowCount);
        Assert.Contains("Moved", b.Variables);
        Assert.Contains("Terrain", b.Variables);
        var r = b.Row(0);
        Assert.False(r.CanApply);
        Assert.All(r.Cells, cell => Assert.Equal("", cell));
        Assert.Equal("Fuel for movement — 0 of 2 applied", b.Title);
        Assert.Equal(0, b.ApplyAll());
        Assert.Single(s.Game!.State.Pending);
    }

    [Fact]
    public void CalculateAll_ThenApplyAll_ResolvesRun_OneSummary()
    {
        var (s, c) = Make(Difficulty.Hardcore);
        var (b, _) = MoveBatch(s, c);
        b.CalculateAll();
        var r = b.Row(0);
        Assert.True(r.CanApply);
        Assert.Contains("→", r.LedgerText);
        Assert.Equal(2, b.ApplyAll());
        Assert.Empty(s.Game!.State.Pending);
        Assert.Equal(1, s.Game.State.Log.Count(l => l.Text == "Fuel for movement: 2 units, 2 applied"));
        Assert.True(Fuel(s, "r1") < 40);
        Assert.True(Fuel(s, "r2") < 40);
        Assert.Equal(0, b.RowCount);
        Assert.Equal("(finished)", b.Title);
    }

    [Fact]
    public void ApplyRow_Wrong_Refused()
    {
        var (s, c) = Make(Difficulty.Hardcore);
        var (b, _) = MoveBatch(s, c);
        b.CalculateAll();
        b.SetCell(0, "Moved", "99");
        var r = b.Row(0);
        Assert.False(r.CanApply);
        Assert.Contains(true, r.Wrong);
        Assert.False(b.ApplyRow(0));
        Assert.Equal(40, Fuel(s, "r1"));
        // Apply all skips the wrong row but applies the good one.
        Assert.Equal(1, b.ApplyAll());
        Assert.Equal(40, Fuel(s, "r1"));
        Assert.True(Fuel(s, "r2") < 40);
        Assert.Equal("Fuel for movement — 1 of 2 applied", b.Title);
        Assert.True(b.Row(1).Applied);
        Assert.False(b.Row(1).CanApply);
        // Fixing the cell lets it apply.
        b.CalculateRow(0);
        Assert.True(b.ApplyRow(0));
        Assert.Empty(s.Game!.State.Pending);
    }

    [Fact]
    public void Exceptions_NegativeAndWarning()
    {
        var (s, c) = Make(Difficulty.SemiAuto, p => p with
        {
            Formulas = p.Formulas.Select(f => f.Id == "f-fuel" ? f with { Text = "Moved * lookup(TestTerrainCost, Terrain) + 100" } : f).ToList(),
            Phases = p.Phases.Select(ph => ph.Id == "ph-move"
                ? ph with { Warnings = new() { new PhaseWarning { Condition = "Fuel <= 100", Message = "Low fuel {unit}" } } } : ph).ToList(),
        });
        var (b, _) = MoveBatch(s, c);
        b.CalculateAll();
        var r = b.Row(0);
        Assert.Contains("goes negative", r.Exceptions);
        Assert.Contains("Low fuel", r.Exceptions);
        Assert.Contains("; ", r.Exceptions);
    }

    [Fact]
    public void LazyRows_1200Units_OnlyVisibleBuilt()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        var units = Enumerable.Range(0, 1200).Select(i => new PendingUnit("r1", "Unit " + i, 0, false)).ToList();
        var run = new PendingRun("big", "f-water", "Daily water", "start", units, false);
        s.Game!.Execute(new QueueRuns(new[] { run }));
        var b = new BatchModel(s, c, "big");
        Assert.Equal(1200, b.RowCount);
        Assert.Equal(0, b.BuiltCount);
        Assert.Equal("Unit 0", b.Row(0).Label);
        Assert.Equal("Unit 1199", b.Row(1199).Label);
        Assert.Equal(2, b.BuiltCount);
        b.Row(0);
        Assert.Equal(2, b.BuiltCount);
    }

    [Fact]
    public void RunGone_ZeroRows()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        var (b, _) = MoveBatch(s, c);
        Assert.Equal(2, b.RowCount);
        c.Advance();   // phase ends, pending dropped
        b.Invalidate();
        Assert.Equal(0, b.RowCount);
        Assert.Equal("(finished)", b.Title);
        Assert.False(b.ApplyRow(0));
        Assert.Equal(0, b.ApplyAll());
    }
}
