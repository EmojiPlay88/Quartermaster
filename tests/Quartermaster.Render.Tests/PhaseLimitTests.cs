using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

/// <summary>Formulas limited to phases: greyed in the menu, refused by the dialog and the host, ignored by hooks. Invented PhaseFixture pack only.</summary>
[Collection(AppDataRootEnv.Name)]
public class PhaseLimitTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-phaselimit-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public PhaseLimitTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static readonly IReadOnlySet<string> Red = new HashSet<string> { "red" };
    private static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });

    /// <summary>"Daily water" is limited to the (renamed) Movement phase; Movement is called Organization.</summary>
    private static RulesPack Limited(RulesPack p) => p with
    {
        Formulas = p.Formulas.Select(f => f.Id == "f-water" ? f with { Phases = new() { "ph-move" } } : f).ToList(),
        Phases = p.Phases.Select(x => x.Id == "ph-move" ? x with { Name = "Organization" } : x).ToList(),
    };

    private (ModuleSession S, PhaseController C) Open(Func<RulesPack, RulesPack>? tweak = null)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto, new[] { Alice });
        s.NewRules("t");
        s.UpdateRules(_ => { var p = Limited(PhaseFixture.Pack(s.Archive.Sha256)); return tweak == null ? p : tweak(p); });
        return (s, new PhaseController(s, (c, st) => CommandLog.Prepare(c, st, s)));
    }

    private static MenuEntry WaterEntry(ModuleSession s)
    {
        var menu = PieceMenuModel.Build(s, s.Game!.State.Pieces[PhaseFixture.RedOne].MapId, new[] { PhaseFixture.RedOne }, null);
        return menu.Single(e => e.Label == "Calculate").Children.Single(e => e.Label == "Daily water");
    }

    private static string Json(GameState st) => JsonSerializer.Serialize(st, QuartermasterJson.Options);

    [Fact]
    public void Menu_greys_formula_outside_its_phases()
    {
        var (s, c) = Open();
        c.Start();   // Weather
        var e = WaterEntry(s);
        Assert.False(e.Enabled);
        Assert.Equal("Only in: Organization", e.DisabledReason);
    }

    [Fact]
    public void Menu_formula_allowed_in_listed_phase()
    {
        var (s, c) = Open();
        c.Start(); c.Advance(); c.Advance();   // Organization (Movement)
        var e = WaterEntry(s);
        Assert.True(e.Enabled);
        Assert.Null(e.DisabledReason);
    }

    [Fact]
    public void Dialog_run_disabled_outside_phases()
    {
        var (s, c) = Open();
        c.Start();   // Weather
        var f = s.Rules!.ById<FormulaDef>("f-water")!;
        var m = new FormulaDialogModel(s.Runner!, f, PhaseFixture.RedOne) { EnforcePhaseLimit = true };
        var v = m.Evaluate(s.Game!.State);
        Assert.False(v.CanApply);
        Assert.Equal("Only in: Organization", v.Status);
        Assert.Null(v.Trace.Result);

        c.Advance(); c.Advance();   // Organization
        var ok = m.Evaluate(s.Game!.State);
        Assert.True(ok.CanApply);
        Assert.Equal("All inputs filled", ok.Status);

        // A run that is not player-started (waiting run, hook) ignores the limit.
        c.Advance();
        var free = new FormulaDialogModel(s.Runner!, f, PhaseFixture.RedOne);
        Assert.True(free.Evaluate(s.Game!.State).CanApply);
    }

    [Fact]
    public void Host_refuses_formula_outside_its_phases()
    {
        var (s, c) = Open();
        var x = new IntentExecutor(s, c);
        c.Start();   // Weather
        var before = Json(s.Game!.State);
        var r = x.Execute("p1", Red, new FormulaIntent("f-water", PhaseFixture.RedOne, null, new Dictionary<string, string>(), true));
        Assert.False(r.Ok);
        Assert.Equal("Daily water can only be run in: Organization", r.Message);
        Assert.Equal(before, Json(s.Game!.State));

        c.Advance(); c.Advance();   // Organization
        var ok = x.Execute("p1", Red, new FormulaIntent("f-water", PhaseFixture.RedOne, null, new Dictionary<string, string>(), true));
        Assert.True(ok.Ok, ok.Message);
    }

    [Fact]
    public void Hooks_ignore_phase_limit()
    {
        var (s, c) = Open();   // Supply's OnStart runs f-water for each unit, though f-water is limited to Organization
        c.Start();
        c.Advance();           // Supply
        Assert.Equal(9, Ledger.Get(s.Game!.State, LedgerKey.Piece(PhaseFixture.RedOne), "water"));
    }
}
