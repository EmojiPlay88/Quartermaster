using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>Formula guards on the Render side: dialog boxes and status, host refusal, hooks and queued runs. Invented pack and numbers only.</summary>
public class FormulaGuardsRenderTests : IDisposable
{
    private static readonly Lazy<ModuleModel> LazyModel = new(() =>
    {
        using var fx = new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile()).AddImage("board.png", 200, 100).AddSvg("unit.svg");
        using var arch = VmodArchive.Open(fx.Build());
        return ModuleModelBuilder.Build(arch, new DiagnosticLog());
    });

    private static VariableBinding In(string v, string? when = null) => new() { Variable = v, Kind = BindingKind.Input, When = when };

    private static RulesPack DialogPack(string? precondition = null, string? refusal = null) => new()
    {
        Properties = new() { new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, Default = 0 } },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "g1", Name = "Ferry", Text = "if(A > 1, A + B, A)", Precondition = precondition, RefusalMessage = refusal,
                Bindings = new() { In("A"), In("B", "A > 1") },
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Op = OutputOp.Subtract } },
            },
        },
    };

    private static GameState State()
    {
        var s = new GameState();
        s.Pieces["u1"] = new PieceInstance { Id = "u1", TypeString = "piece;;;unit.svg;Rifle", MapId = "Main Map", Pos = new PointD(60, 40), TraitStates = new() { "" } };
        s.Ledgers[LedgerKey.Piece("u1")] = new() { ["fuel"] = 10 };
        return s;
    }

    private static FormulaDialogModel Dialog(RulesPack pack) =>
        new(new FormulaRunner(pack, LazyModel.Value, (n, _) => Enumerable.Repeat(1, n).ToList()), pack.Formulas[0], "u1");

    [Fact]
    public void Dialog_shows_not_used_box()
    {
        var m = Dialog(DialogPack());
        m.SetBoxText("A", "1");
        var v = m.Evaluate(State());
        var b = v.Boxes.Single(x => x.Variable == "B");
        Assert.Equal("not used", b.Text);
        Assert.Equal(BoxBackground.Auto, b.Background);
        Assert.False(b.Enabled);
        Assert.Equal("All inputs filled", v.Status);
        Assert.True(v.CanApply);
        Assert.Equal("1", v.ResultText);
    }

    [Fact]
    public void Dialog_when_input_hides_until_answered()
    {
        var m = Dialog(DialogPack());
        var v = m.Evaluate(State());
        var b = v.Boxes.Single(x => x.Variable == "B");
        Assert.Equal("", b.Text);
        Assert.False(b.Enabled);
        Assert.Contains("A", v.Status);
        Assert.StartsWith("Waiting for", v.Status);
        Assert.False(v.CanApply);

        m.SetBoxText("A", "1");
        Assert.Equal("not used", m.Evaluate(State()).Boxes.Single(x => x.Variable == "B").Text);

        m.SetBoxText("A", "3");
        var open = m.Evaluate(State());
        var b3 = open.Boxes.Single(x => x.Variable == "B");
        Assert.Equal("", b3.Text);
        Assert.True(b3.Enabled);
        Assert.Equal("Waiting for: B", open.Status);
    }

    [Fact]
    public void Dialog_refused_status_and_no_apply()
    {
        var m = Dialog(DialogPack("A > 5", "Too early"));
        m.SetBoxText("A", "2");
        var v = m.Evaluate(State());
        Assert.True(v.Trace.Refused);
        Assert.Equal("Too early", v.Status);
        Assert.False(v.CanApply);
        Assert.Equal("", v.ResultText);

        var d = Dialog(DialogPack("A > 5"));
        d.SetBoxText("A", "2");
        Assert.Equal("Ferry cannot be run now", d.Evaluate(State()).Status);
    }

    [Fact]
    public void Dialog_precondition_pending_shows_waiting_not_all_filled()
    {
        var m = Dialog(DialogPack("A > 5"));
        var v = m.Evaluate(State());
        Assert.False(v.CanApply);
        Assert.NotEqual("All inputs filled", v.Status);
        Assert.StartsWith("Waiting for", v.Status);
    }

    [Fact]
    public void Dialog_checked_mode_skipped_global_does_not_block_apply()
    {
        var pack = DialogPack() with { Properties = new() { new PropertyDef { Id = "cost", Name = "Cost", Scope = PropertyScope.Holders, Default = 3 } } };
        var f = pack.Formulas[0] with
        {
            Text = "if(A > 1, A + C, A)", Outputs = new(),
            Bindings = new() { In("A"), new VariableBinding { Variable = "C", Kind = BindingKind.Global, Property = "cost", When = "A > 1" } },
        };
        pack = pack with { Formulas = new() { f } };
        var m = new FormulaDialogModel(new FormulaRunner(pack, LazyModel.Value, (n, _) => Enumerable.Repeat(1, n).ToList()), f, "u1") { Checked = true };
        m.SetBoxText("A", "1");
        var v = m.Evaluate(State());
        Assert.Equal("not used", v.Boxes.Single(x => x.Variable == "C").Text);
        Assert.Equal("All inputs filled", v.Status);
        Assert.True(v.CanApply);
    }

    [Theory]
    [InlineData(false, FormulaDialogModel.HiddenWaitText)]
    [InlineData(true, FormulaDialogModel.HostWaitText)]
    public void Dialog_masked_undecided_says_hidden_wait_not_other_inputs(bool hostCalculates, string expected)
    {
        // A is hidden (never typed, missing on this screen); B's "Only when" reads it, so B is undecided and no visible input is missing.
        var m = Dialog(DialogPack());
        m.Hidden = new HashSet<string> { "A" };
        m.HostCalculates = hostCalculates;
        var v = m.Evaluate(State());
        Assert.True(v.Trace.Variables.Single(x => x.Variable == "B").Waiting);
        Assert.False(v.CanApply);
        Assert.Equal(expected, v.Status);
    }

    // ---- host, hooks, queue (PhaseFixture) ----

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-guardsrender-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public FormulaGuardsRenderTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static readonly IReadOnlySet<string> Red = new HashSet<string> { "red" };

    /// <summary>The Daily water formula only runs when W is above 50 (never true here: water starts at 10).</summary>
    private static RulesPack Guarded(RulesPack p) => p with
    {
        Formulas = p.Formulas.Select(f => f.Id == "f-water" ? f with { Precondition = "W > 50", RefusalMessage = "Not enough water to drain" } : f).ToList(),
    };

    private (ModuleSession S, PhaseController C) Make(Difficulty d)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], d, new[] { new PlayerDef("p1", "Alice", new[] { "red" }) });
        s.NewRules("t");
        s.UpdateRules(_ => Guarded(PhaseFixture.Pack(s.Archive.Sha256)));
        return (s, new PhaseController(s, (c, _) => c));
    }

    private static double Water(ModuleSession s, string id) => Ledger.Get(s.Game!.State, LedgerKey.Piece(id), "water") ?? 10;

    [Fact]
    public void Host_refuses_precondition()
    {
        var (s, c) = Make(Difficulty.FullyAuto);
        var before = System.Text.Json.JsonSerializer.Serialize(s.Game!.State, QuartermasterJson.Options);
        var r = new IntentExecutor(s, c).Execute("p1", Red, new FormulaIntent("f-water", "r1", null, new Dictionary<string, string>(), true));
        Assert.False(r.Ok);
        Assert.Equal("Not enough water to drain", r.Message);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(s.Game.State, QuartermasterJson.Options));
        Assert.Equal(10, Water(s, "r1"));
    }

    [Fact]
    public void Hook_refused_unit_logs_message()
    {
        var (s, c) = Make(Difficulty.FullyAuto);
        c.Start();
        c.Advance();   // Supply OnStart: Daily water for each unit, refused
        Assert.Equal(10, Water(s, "r1"));
        Assert.Equal(10, Water(s, "r2"));
        Assert.DoesNotContain(s.Game!.Log, a => a is LedgerDelta d && d.PropertyId == "water");
        Assert.Contains(s.Game.State.Log, l => l.Text == "Daily water: Red 1: Not enough water to drain");
    }

    [Fact]
    public void Queued_refused_run_stays_pending()
    {
        var (s, c) = Make(Difficulty.SemiAuto);
        c.Start();
        c.Advance();
        var run = s.Game!.State.Pending.Single();
        var trace = s.Runner!.Run(s.Game.State, new RunRequest("f-water", "r1", new Dictionary<string, string>(),
            new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>(), null, true));
        Assert.True(trace.Refused);

        Assert.False(c.ApplyFormula(run.Id, "r1", trace, true));

        Assert.True(c.IsPending(run.Id, "r1", "f-water"));
        Assert.Equal(10, Water(s, "r1"));
        Assert.All(s.Game.State.Pending.Single().Units, u => Assert.False(u.Applied));
    }
}
