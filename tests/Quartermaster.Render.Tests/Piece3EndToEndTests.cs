using System.Text.Json;
using System.Text.Json.Nodes;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>Piece 3 end to end: invented PhaseFixture module and pack, temp roots only, no UI.</summary>
public class Piece3EndToEndTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-p3e2e-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public Piece3EndToEndTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private (ModuleSession S, PhaseController C) Make(Difficulty d)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], d);
        s.NewRules("t");
        s.UpdateRules(_ => PhaseFixture.Pack(s.Archive.Sha256));
        return (s, new PhaseController(s, (c, _) => c));
    }

    private static string Json(object o) => JsonSerializer.Serialize(o, QuartermasterJson.Options);
    private static MovePieces Move(string id, int x) => new(new[] { id }, "Main Map", new PointD(x, 50), null);
    private static IEnumerable<string> Texts(ModuleSession s) => s.Game!.State.Log.Select(l => l.Text);

    [Fact]
    public void E2E_FullyAuto_TurnWrap_SaveLoadReplay()
    {
        var (s, c) = Make(Difficulty.FullyAuto);
        var initial = s.Game!.State.DeepClone();
        c.Start();
        c.ExecuteMove(new[] { Move(PhaseFixture.RedOne, 80) });
        var steps = 0;
        while (s.Game!.State.Position!.Turn == 1 && steps++ < 30) c.Advance();
        Assert.Equal(2, s.Game.State.Position!.Turn);
        Assert.Equal(2, Texts(s).Count(t => t == "Turn 1 · Movement → Combat"));   // the block ran twice
        Assert.Equal(1, Texts(s).Count(t => t == "Turn 2 · End → Weather"));
        Assert.Empty(s.Game.State.Pending);

        var path = Path.Combine(_tmp, "g.qsave");
        s.SaveNative(path);
        var loaded = NativeSave.Read(path);
        Assert.Equal(5, NativeSave.FormatVersion);
        s.LoadNative(path);
        Assert.Null(c.Reconcile());

        var replayed = initial;
        foreach (var cmd in loaded.Log) replayed.Apply(cmd);
        Assert.Equal(Json(loaded.State), Json(replayed));
    }

    [Fact]
    public void E2E_SemiAuto_PendingApplyAndSkip()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        c.Start();
        c.Advance();   // Supply: one run for the two red units
        var run = Assert.Single(s.Game!.State.Pending, r => r.FormulaId == "f-water");
        Assert.Equal(2, run.Units.Count);
        var none = new Dictionary<string, string>();
        var trace = s.Runner!.Run(s.Game.State, new RunRequest("f-water", PhaseFixture.RedOne, none, none, new Dictionary<string, IReadOnlyList<int>>(), null, true));
        Assert.True(c.ApplyFormula(run.Id, PhaseFixture.RedOne, trace, false));
        Assert.Single(s.Game.State.Pending.Single(r => r.Id == run.Id).Units, u => !u.Applied);

        c.Advance();   // Movement: the unfinished run is skipped, never blocks
        Assert.Contains(Texts(s), t => t.StartsWith("skipped: Daily water, Red 2", StringComparison.Ordinal));
        Assert.Empty(s.Game.State.Pending);
    }

    [Fact]
    public void E2E_Hardcore_WrongValueBlocksApply_CalculateApplies()
    {
        var (s, c) = Make(Difficulty.Hardcore);
        c.Start(); c.Advance(); c.Advance();   // Movement
        c.ExecuteMove(new[] { Move(PhaseFixture.RedOne, 80) });
        var run = Assert.Single(s.Game!.State.Pending, r => r.Trigger == "move");
        var unit = run.Units.Single();
        var formula = s.Rules!.Formulas.Single(f => f.Id == run.FormulaId);
        var model = new FormulaDialogModel(s.Runner!, formula, unit.PieceId) { Checked = true, Moved = unit.Moved };

        model.SetBoxText("Moved", "999");
        Assert.False(model.Evaluate(s.Game.State).CanApply);
        model.Calculate(s.Game.State);
        var view = model.Evaluate(s.Game.State);
        Assert.True(view.CanApply);
        Assert.True(model.AutoCalculated);
        Assert.True(c.ApplyFormula(run.Id, unit.PieceId, view.Trace, model.AutoCalculated));
        Assert.Empty(s.Game.State.Pending);
        Assert.Contains("auto-calculated", Json(s.Game.State));
    }

    [Fact]
    public void E2E_OldSaveFormat3_BeforeTurn1_StartWorks()
    {
        var (s, c) = Make(Difficulty.Hardcore);
        var path = Path.Combine(_tmp, "old.qsave");
        s.SaveNative(path);
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["format"] = 3;
        var snap = node["snapshot"]!.AsObject();
        foreach (var k in snap.Select(kv => kv.Key).Where(k => k.Contains("difficulty", StringComparison.OrdinalIgnoreCase) || k.Contains("position", StringComparison.OrdinalIgnoreCase)).ToList())
            snap.Remove(k);
        File.WriteAllText(path, node.ToJsonString());

        s.LoadNative(path);
        Assert.Null(s.Game!.State.Position);
        Assert.Equal(Difficulty.SemiAuto, s.Game.State.Difficulty);
        Assert.True(c.View().BeforeStart);
        c.Start();
        Assert.False(c.View().BeforeStart);
        Assert.Equal("Weather", c.View().Current!.Name);
    }

    [Fact]
    public void E2E_PackFormat1_NoPhases_FreeForm()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        var folder = Path.Combine(_tmp, "pack1");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "pack.json"), "{\"format\":1,\"id\":\"x\",\"name\":\"old\",\"moduleSha256\":\"" + s.Archive.Sha256 + "\"}");
        s.OpenRules(folder);
        Assert.True(c.View().NoPhases);
        var before = Json(s.Game!.State);
        Assert.Null(c.Advance());
        c.Start();
        Assert.Equal(before, Json(s.Game.State));
        Assert.Null(s.Game.State.Position);
    }

    [Fact]
    public void FixtureBuilds_AreByteIdentical_SameModuleSha()
    {
        string Sha()
        {
            using var fx = PhaseFixture.Module();
            var path = fx.Build();
            System.Threading.Thread.Sleep(2100);   // zip stamps have 2 second resolution: a real clock stamp would differ
            using var a = VmodArchive.Open(path);
            return a.Sha256;
        }
        Assert.Equal(Sha(), Sha());
    }
}
