using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>UI-free logic behind the fully automatic missing-input dialog. Invented pack from PhaseFixture; temp roots only.</summary>
public class MissingInputsTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-missing-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public MissingInputsTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    // Terrain has no AutoValue, so a fully automatic Movement phase cannot run it and queues a NeedsInput run.
    private (ModuleSession S, PhaseController C, PendingRun Run) Make()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _sessions.Add(s);
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto);
        s.NewRules("t");
        s.UpdateRules(_ =>
        {
            var p = PhaseFixture.Pack(s.Archive.Sha256);
            return p with
            {
                Formulas = p.Formulas.Select(f => f.Id != "f-fuel" ? f : f with
                {
                    Bindings = f.Bindings.Select(b => b.Variable == "Terrain" ? b with { AutoValue = null } : b).ToList(),
                }).ToList(),
            };
        });
        var c = new PhaseController(s, (cmd, _) => cmd);
        c.Start(); c.Advance(); c.Advance();   // Movement
        c.ExecuteMove(new[] { new MovePieces(new[] { PhaseFixture.RedOne, PhaseFixture.RedTwo }, "Main Map", new PointD(130, 50), null) });
        var run = Assert.Single(s.Game!.State.Pending, r => r.FormulaId == "f-fuel");
        return (s, c, run);
    }

    [Fact]
    public void For_ListsOnlyTheMissingInputVariables()
    {
        var (s, _, run) = Make();
        Assert.True(run.NeedsInput);
        var m = Assert.Single(MissingInputs.For(s, run));
        Assert.Equal("Terrain", m.Variable);
    }

    [Fact]
    public void For_OffersKeyChoicesOfTheIndexedTable()
    {
        var (s, _, run) = Make();
        var m = Assert.Single(MissingInputs.For(s, run));
        Assert.Equal(new[] { "Clear", "Sand" }, m.Choices);
    }

    [Fact]
    public void ApplyAll_SkipsUnitsOfSidesNotHeld()
    {
        var (s, c, run) = Make();
        s.SetViewSides(new HashSet<string> { "blue" });
        var (applied, failed) = MissingInputs.ApplyAll(s, c, run, new Dictionary<string, string> { ["Terrain"] = "Sand" });
        Assert.Equal(0, applied);
        Assert.Equal(2, failed.Count);
        Assert.All(failed, f => Assert.EndsWith(ModuleSession.OtherSideText, f));
        Assert.Equal(40, Ledger.Get(s.Game!.State, LedgerKey.Piece(PhaseFixture.RedOne), "fuel"));
    }

    [Fact]
    public void ApplyAll_AppliesTheValueToEveryUnitAndResolvesTheRun()
    {
        var (s, c, run) = Make();
        var (applied, failed) = MissingInputs.ApplyAll(s, c, run, new Dictionary<string, string> { ["Terrain"] = "Sand" });
        Assert.Equal(2, applied);
        Assert.Empty(failed);
        Assert.DoesNotContain(s.Game!.State.Pending, r => r.Id == run.Id);
        // 2 cells * 3 (Sand) subtracted from the fixture's 40 fuel, for both units.
        Assert.Equal(34, Ledger.Get(s.Game.State, LedgerKey.Piece(PhaseFixture.RedOne), "fuel"));
        Assert.Equal(34, Ledger.Get(s.Game.State, LedgerKey.Piece(PhaseFixture.RedTwo), "fuel"));
    }

    [Fact]
    public void ApplyAll_BadValue_AppliesNothingAndKeepsTheRunPending()
    {
        var (s, c, run) = Make();
        var (applied, failed) = MissingInputs.ApplyAll(s, c, run, new Dictionary<string, string> { ["Terrain"] = "Swamp" });
        Assert.Equal(0, applied);
        Assert.Equal(2, failed.Count);
        Assert.Contains(s.Game!.State.Pending, r => r.Id == run.Id);
    }
}
