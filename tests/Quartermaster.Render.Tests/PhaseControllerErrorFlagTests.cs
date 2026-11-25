using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>The controller flags failure lines (LogEntry.Error) at the failure sites only. Invented PhaseFixture data.</summary>
public class PhaseControllerErrorFlagTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-errflag-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public PhaseControllerErrorFlagTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private (ModuleSession S, PhaseController C) Make(Func<RulesPack, RulesPack> tweak)
    {
        var fx = PhaseFixture.Module(true);
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto);
        s.NewRules("t");
        s.UpdateRules(_ => tweak(PhaseFixture.Pack(s.Archive.Sha256)));
        return (s, new PhaseController(s, (c, _) => c));
    }

    private static RulesPack Edit(RulesPack p, Func<FormulaDef, FormulaDef> f) =>
        p with { Formulas = p.Formulas.Select(x => x.Id == "f-water" ? f(x) : x).ToList() };

    private static List<LogEntry> Calc(ModuleSession s) => s.Game!.State.Log.Where(l => l.Kind == LogKind.Calc).ToList();

    [Fact]
    public void Failed_auto_run_line_has_error_flag()
    {
        var (s, c) = Make(p => Edit(p, f => f with { Text = "W / 0" }));
        c.Start(); c.Advance();
        var lines = Calc(s).Where(l => l.Text.StartsWith("Daily water: Red 1: ")).ToList();
        Assert.Single(lines);
        Assert.True(lines[0].Error);
    }

    [Fact]
    public void Refused_auto_run_line_has_error_flag()
    {
        var (s, c) = Make(p => Edit(p, f => f with { Precondition = "0 > 1", RefusalMessage = "Not now" }));
        c.Start(); c.Advance();
        var lines = Calc(s).Where(l => l.Text == "Daily water: Red 1: Not now").ToList();
        Assert.Single(lines);
        Assert.True(lines[0].Error);
    }

    [Fact]
    public void Missing_formula_line_has_error_flag()
    {
        var (s, c) = Make(p => p with { Phases = p.Phases.Select(ph => ph.Id != "ph-supply" ? ph : ph with
        {
            OnStart = new() { new StartEntry { FormulaId = "f-gone", ForEach = ForEachMode.Units }, new StartEntry { FormulaId = "f-water", ForEach = ForEachMode.Units, UnitFilter = "((" } },
        }).ToList() });
        c.Start(); c.Advance();
        var missing = Calc(s).Single(l => l.Text == "Supply: formula \"f-gone\" not found — skipped");
        Assert.True(missing.Error);
        var bad = Calc(s).Single(l => l.Text.Contains("is not valid — skipped"));
        Assert.True(bad.Error);
    }

    [Fact]
    public void Ordinary_calc_line_has_no_flag()
    {
        var (s, c) = Make(p => p);
        c.Start(); c.Advance();
        var calc = Calc(s);
        Assert.NotEmpty(calc);
        Assert.All(calc, l => Assert.False(l.Error, l.Text));
    }
}
