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
    public void Rows_OfSidesNotHeld_ShowHidden_AndNeverApply()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        var (b, _) = MoveBatch(s, c);
        s.SetViewSides(new HashSet<string> { "blue" });
        b.Invalidate();
        var r = b.Row(0);
        Assert.False(r.CanApply);
        Assert.All(r.Cells, cell => Assert.Equal(ModuleSession.HiddenText, cell));
        Assert.Equal(ModuleSession.HiddenText, r.Result);
        Assert.Equal("", r.LedgerText);
        b.CalculateAll();
        Assert.Equal(0, b.ApplyAll());
        Assert.False(b.ApplyRow(1));
        Assert.Equal(40, Fuel(s, "r1"));
        s.SetViewSides(null);
        b.Invalidate();
        Assert.NotEqual(ModuleSession.HiddenText, b.Row(0).Result);
    }

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

    [Fact]
    public void ApplyRow_RunClearedAfterCalculate_AppliesNothing()
    {
        var (s, c) = Make(Difficulty.Hardcore);
        var (b, _) = MoveBatch(s, c);
        b.CalculateAll();
        var fuel = Fuel(s, "r1");
        c.Advance();   // clears the run while the batch grid is still open
        var log = s.Game!.State.Log.Count;

        Assert.False(b.ApplyRow(0));
        Assert.Equal(0, b.ApplyAll());

        Assert.Equal(fuel, Fuel(s, "r1"));
        Assert.Equal(log, s.Game.State.Log.Count);
    }

    [Fact]
    public void ApplyAll_1200Units_FastAndExact()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        var units = Enumerable.Range(0, 1200).Select(i => new PendingUnit("p" + i, "Unit " + i, 0, false)).ToList();
        s.Game!.Execute(new QueueRuns(new[] { new PendingRun("big", "f-weather", "Weather roll", "start", units, false) }));
        var b = new BatchModel(s, c, "big");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var applied = b.ApplyAll();
        sw.Stop();
        Assert.Equal(1200, applied);
        Assert.Empty(s.Game.State.Pending);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"ApplyAll took {sw.Elapsed}");
    }

    [Fact]
    public void Row_OutOfRange_ReturnsEmptyView_DoesNotThrow()
    {
        var (s, c) = Make(Difficulty.Hardcore);
        var (b, _) = MoveBatch(s, c);
        var r = b.Row(99);
        Assert.False(r.CanApply);
        Assert.False(b.Row(-1).CanApply);
        Assert.False(b.ApplyRow(99));
        b.SetCell(99, "Moved", "1");
        b.CalculateRow(99);
        Assert.Throws<ArgumentOutOfRangeException>(() => b.ModelFor(99));   // documented: ModelFor needs a valid row
    }

    private static (BatchModel B, string RunId) ManualRun(ModuleSession s, PhaseController c)
    {
        var run = new PendingRun("manual", "f-fuel", "Fuel for movement", "move", new List<PendingUnit> { new("r1", "Red 1", 3, false) }, false);
        s.Game!.Execute(new QueueRuns(new[] { run }));
        return (new BatchModel(s, c, "manual"), "manual");
    }

    [Fact]
    public void DifficultySwitch_FullyAutoToHardcore_OverrideNeverReachesLedger()
    {
        var (s, c) = Make(Difficulty.FullyAuto);
        var (b, _) = ManualRun(s, c);
        b.SetCell(0, "Moved", "999");   // built in fully automatic: an override feeds the run
        var fuel = Fuel(s, "r1");
        var log = s.Game!.State.Log.Count;
        var raised = 0;
        c.DifficultyChanged += () => raised++;

        c.SetDifficulty(Difficulty.Hardcore);

        Assert.Equal(1, raised);
        Assert.False(b.Row(0).CanApply);
        Assert.False(b.ApplyRow(0));
        Assert.Equal(0, b.ApplyAll());
        Assert.Equal(fuel, Fuel(s, "r1"));
        Assert.Equal(log + 1, s.Game.State.Log.Count);   // only the SetDifficulty line
        Assert.True(b.ModelFor(0).Checked);
        Assert.Single(s.Game.State.Pending);             // the run stays pending, now to be done in the new mode
    }

    [Fact]
    public void DifficultySwitch_HardcoreToFullyAuto_ModelsUncheckedAgain()
    {
        var (s, c) = Make(Difficulty.Hardcore);
        var (b, _) = ManualRun(s, c);
        Assert.True(b.ModelFor(0).Checked);
        c.SetDifficulty(Difficulty.FullyAuto);
        Assert.False(b.ModelFor(0).Checked);
    }

    [Fact]
    public void ChoicesFor_TableKeyInput_ListsKeys_OthersNull()
    {
        var (s, c) = Make(Difficulty.Hardcore);
        var (b, _) = ManualRun(s, c);
        Assert.Equal(new[] { "Clear", "Sand" }, b.ChoicesFor("Terrain"));
        Assert.Null(b.ChoicesFor("Moved"));
        // A typed key still goes through the checked comparison, not the list.
        b.SetCell(0, "Terrain", "sand");
        Assert.Equal("sand", b.Row(0).Cells[b.Variables.ToList().IndexOf("Terrain")]);
    }
    [Fact]
    [Trait("Category", "Perf")]
    public void Perf_HotSeat_ApplyAll20_On1200PieceGame()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        var g = s.Game!;
        for (var i = 0; i < 1200; i++)
            g.Execute(new AddPiece("x" + i, $"piece;;;unit.svg;{(i % 2 == 0 ? "Red" : "Blue")} {i + 10}", "", "Main Map", new PointD(i % 200, i / 200), null));
        var units = Enumerable.Range(0, 20).Select(i => new PendingUnit("x" + (i * 2), "Red " + (i * 2 + 10), 0, false)).ToList();
        g.Execute(new QueueRuns(new[] { new PendingRun("hs", "f-water", "Daily water", "start", units, false) }));
        s.SetViewSides(new HashSet<string> { "red" });
        var b = new BatchModel(s, c, "hs");
        Assert.True(s.CanAct("x0"));
        Assert.False(s.CanAct("x1"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        b.CalculateAll();
        Assert.True(b.Row(0).CanApply, b.Row(0).Exceptions + "|" + b.Row(0).Result);
        var applied = b.ApplyAll();
        sw.Stop();
        Assert.Equal(20, applied);
        Assert.Empty(g.State.Pending);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"ApplyAll took {sw.Elapsed}");
    }
}
