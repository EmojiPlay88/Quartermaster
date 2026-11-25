using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>#71: runs("formula-id", period) counts applied runs from the log's run stamps (LogEntry.AppliedRun / Stage).</summary>
public class RunsFunctionTests
{
    private static RulesPack Pack() => new()
    {
        Format = 4,
        Sides = new()
        {
            new SideDef { Id = "red", Name = "Red", UnitFilter = "marker(\"Side\") == \"Red\"" },
            new SideDef { Id = "blue", Name = "Blue", UnitFilter = "marker(\"Side\") == \"Blue\"" },
        },
        Properties = new()
        {
            new PropertyDef { Id = "morale", Name = "Morale", Scope = PropertyScope.Pieces },
            new PropertyDef { Id = "supply", Name = "Supply", Scope = PropertyScope.Holders },
        },
        Phases = new() { new PhaseDef { Id = "dawn", Name = "Dawn" } },
        Formulas = new()
        {
            new FormulaDef { Id = "f-raid", Name = "Raid", Text = "1", Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "morale", Op = OutputOp.Add } } },
            new FormulaDef
            {
                Id = "f-multi", Name = "Multi", Text = "1",
                Outputs = new()
                {
                    new FormulaOutput { Target = OutputTarget.Unit, Property = "morale", Op = OutputOp.Add },
                    new FormulaOutput { Target = OutputTarget.Global, Property = "supply", Op = OutputOp.Add },
                },
            },
            new FormulaDef { Id = "f-none", Name = "Look", Text = "1" },
            new FormulaDef { Id = "f-global", Name = "Stock", Text = "1", Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "supply", Op = OutputOp.Add } } },
            new FormulaDef { Id = "f-fail", Name = "Fail", Text = "1 / 0", Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "morale", Op = OutputOp.Add } } },
            new FormulaDef
            {
                Id = "f-once", Name = "Bridge repair", Text = "1", Precondition = "runs(\"f-once\", \"stage\") < 1",
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "morale", Op = OutputOp.Add } },
            },
        },
    };

    private static GameState S()
    {
        var s = new GameState { GameId = "g" };
        s.Apply(new SetPlayers(new[] { new PlayerDef("p1", "Ann", new[] { "red" }), new PlayerDef("p2", "Bob", new[] { "blue" }) }));
        foreach (var (id, side) in new[] { ("u1", "Red"), ("u2", "Red"), ("b1", "Blue") })
            s.Apply(new AddPiece(id, PieceTypeParser.EncodeState(new[] { "mark;Side", "piece;;;unit.svg;Boat " + id }), PieceTypeParser.EncodeState(new[] { side, "" }),
                "Main Map", new PointD(50, 50), null, id + " placed"));
        At(s, 1, 1);
        return s;
    }

    private static void At(GameState s, int turn, int repeat) => s.Apply(new AdvancePhase(s.Position, new PhasePosition(turn, repeat, "dawn", 0), "Dawn", "Dawn"));

    private static FormulaTrace Run(GameState s, RulesPack pack, string formula, string? piece, Action<GameCommand>? apply = null)
    {
        var runner = new FormulaRunner(pack, RunnerFx.Model, (n, _) => Enumerable.Repeat(1, n).ToList());
        var t = runner.Run(s, new RunRequest(formula, piece, new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>()));
        foreach (var c in runner.Commands(t, null)) (apply ?? s.Apply)(c);
        return t;
    }

    private static double N(GameState s, RulesPack pack, string f, string? piece, string period) => RunIndex.Count(s, pack, f, piece, period);

    private static (double, double, double) All(GameState s, RulesPack pack, string f, string? piece) =>
        (N(s, pack, f, piece, "stage"), N(s, pack, f, piece, "turn"), N(s, pack, f, piece, "game"));

    [Fact]
    public void Runs_counts_applied_runs_per_piece_and_period()
    {
        var pack = Pack();
        var s = S();
        Run(s, pack, "f-raid", "u1");
        Run(s, pack, "f-raid", "u1");
        Run(s, pack, "f-raid", "u2");
        Assert.Equal((2.0, 2.0, 2.0), All(s, pack, "f-raid", "u1"));
        At(s, 1, 2);   // a block repeat: same turn, next stage
        Run(s, pack, "f-raid", "u1");
        Assert.Equal((1.0, 3.0, 3.0), All(s, pack, "f-raid", "u1"));
        Assert.Equal((0.0, 1.0, 1.0), All(s, pack, "f-raid", "u2"));
        At(s, 2, 1);
        Run(s, pack, "f-raid", "u1");
        Assert.Equal((1.0, 1.0, 4.0), All(s, pack, "f-raid", "u1"));
        Assert.Equal((0.0, 0.0, 1.0), All(s, pack, "f-raid", "u2"));
        Assert.Equal((0.0, 0.0, 0.0), All(s, pack, "f-raid", "b1"));

        // Through the expression functions: a unit filter reads the piece's count, the default period is "game".
        Assert.True(PieceFilter.Compile("runs(\"f-raid\", \"Turn\") == 1 and runs(\"f-raid\") == 4", s, pack, RunnerFx.Model)(s.Pieces["u1"]));
        // The working shows the call as written, not a substituted count.
        var counted = pack with { Formulas = pack.Formulas.Append(new FormulaDef { Id = "f-count", Name = "Count", Text = "runs(\"f-raid\", \"turn\") + 1" }).ToList() };
        Assert.Equal("runs(\"f-raid\", \"turn\") + 1 = 2", Run(s, counted, "f-count", "u1").Working);
        var e = Assert.Throws<EvalError>(() => N(s, pack, "f-nope", "u1", "game"));
        Assert.Equal("runs() names formula \"f-nope\", which does not exist", e.Message);
    }

    [Fact]
    public void Runs_multi_output_run_counts_once()
    {
        var pack = Pack();
        var s = S();
        var cmds = new List<GameCommand>();
        Run(s, pack, "f-multi", "u1", c => { cmds.Add(c); s.Apply(c); });
        var deltas = cmds.OfType<LedgerDelta>().ToList();
        Assert.Equal(new[] { "f-multi", null }, deltas.Select(d => d.AppliedRun).ToArray());
        Assert.Equal(new[] { "f-multi", null }, s.Log.TakeLast(2).Select(l => l.AppliedRun).ToArray());
        Assert.Equal(new int?[] { 1, null }, s.Log.TakeLast(2).Select(l => l.Stage).ToArray());
        Assert.Equal((1.0, 1.0, 1.0), All(s, pack, "f-multi", "u1"));
        Assert.Equal(0.0, N(s, pack, "f-multi", null, "game"));   // the global output's line carries the unit
    }

    [Fact]
    public void Runs_no_output_run_counts_once()
    {
        var pack = Pack();
        var s = S();
        At(s, 1, 3);
        s.Apply(new AddLogLine(LogKind.Calc, "Look: 1", "u1", FormulaId: "f-none") { AppliedRun = "f-none" });
        Assert.Equal(("f-none", (int?)3), (s.Log[^1].AppliedRun, s.Log[^1].Stage));
        s.Apply(new AddLogLine(LogKind.Calc, "Look: 1", "u1", FormulaId: "f-none"));   // an unstamped working line does not count
        Assert.Null(s.Log[^1].Stage);
        Assert.Equal((1.0, 1.0, 1.0), All(s, pack, "f-none", "u1"));
    }

    [Fact]
    public void Runs_failed_and_refused_not_counted()
    {
        var pack = Pack();
        var s = S();
        var failed = Run(s, pack, "f-fail", "u1");
        Assert.NotNull(failed.Error);
        Run(s, pack, "f-once", "u1");
        var refused = Run(s, pack, "f-once", "u1");
        Assert.True(refused.Refused);
        s.Apply(new AddLogLine(LogKind.Calc, "Bridge repair: refused", "u1", FormulaId: "f-once", Error: true));
        Assert.Equal(0.0, N(s, pack, "f-fail", "u1", "game"));
        Assert.Equal(1.0, N(s, pack, "f-once", "u1", "game"));
    }

    [Fact]
    public void Runs_undo_drops_count()
    {
        var pack = Pack();
        var session = new GameSession(S(), "mod");
        using (session.BeginAction("raid")) Run(session.State, pack, "f-raid", "u1", session.Execute);
        using (session.BeginAction("raid")) Run(session.State, pack, "f-raid", "u1", session.Execute);
        Assert.Equal(2.0, N(session.State, pack, "f-raid", "u1", "game"));
        session.Undo();
        Assert.Equal(1.0, N(session.State, pack, "f-raid", "u1", "game"));
        session.Undo();
        Assert.Equal(0.0, N(session.State, pack, "f-raid", "u1", "game"));
    }

    [Fact]
    public void Runs_unitless_counts_runs_without_unit()
    {
        var pack = Pack();
        var s = S();
        Run(s, pack, "f-global", null);
        Run(s, pack, "f-global", "u1");
        Assert.Equal(1.0, N(s, pack, "f-global", null, "game"));
        Assert.Equal(1.0, N(s, pack, "f-global", "u1", "game"));
        Assert.True(PieceFilter.EvaluateGameLevel("runs(\"f-global\", \"stage\") == 1", s, pack, RunnerFx.Model));
    }

    [Fact]
    public void Runs_in_precondition_allows_one_per_stage()
    {
        var pack = Pack();
        var s = S();
        Assert.False(Run(s, pack, "f-once", "u1").Refused);
        Assert.True(Run(s, pack, "f-once", "u1").Refused);
        Assert.False(Run(s, pack, "f-once", "u2").Refused);   // another unit has its own count
        At(s, 1, 2);
        Assert.False(Run(s, pack, "f-once", "u1").Refused);
        Assert.Equal(2.0, N(s, pack, "f-once", "u1", "game"));
    }

    [Fact]
    public void Runs_index_incremental_and_rebuilt()
    {
        var pack = Pack();
        var s = S();
        Run(s, pack, "f-raid", "u1");
        RunIndex.ScannedForTest = 0;
        Assert.Equal(1.0, N(s, pack, "f-raid", "u1", "game"));
        Assert.Equal(s.Log.Count, RunIndex.ScannedForTest);   // first use: the whole log
        Assert.Equal(1.0, N(s, pack, "f-raid", "u1", "stage"));
        Assert.Equal(s.Log.Count, RunIndex.ScannedForTest);   // nothing new: no rescan
        Run(s, pack, "f-raid", "u1");
        Assert.Equal(2.0, N(s, pack, "f-raid", "u1", "game"));
        Assert.Equal(s.Log.Count, RunIndex.ScannedForTest);   // only the appended line
        s.Log.RemoveAt(s.Log.Count - 1);                       // the count drops: rebuilt
        var before = RunIndex.ScannedForTest;
        Assert.Equal(1.0, N(s, pack, "f-raid", "u1", "game"));
        Assert.Equal(before + s.Log.Count, RunIndex.ScannedForTest);
        s.Log[^1] = s.Log[^1] with { AppliedRun = null };      // same count, other last line: rebuilt
        Assert.Equal(0.0, N(s, pack, "f-raid", "u1", "game"));
        var clone = s.DeepClone();                             // another list: its own index
        Run(clone, pack, "f-raid", "u1");
        Assert.Equal(1.0, N(clone, pack, "f-raid", "u1", "game"));
        Assert.Equal(0.0, N(s, pack, "f-raid", "u1", "game"));
    }

    [Fact]
    public void Redacted_line_clears_run_stamp()
    {
        var pack = Pack() with
        {
            Properties = new()
            {
                new PropertyDef { Id = "morale", Name = "Morale", Scope = PropertyScope.Pieces, Visibility = Visibility.Private },
                new PropertyDef { Id = "supply", Name = "Supply", Scope = PropertyScope.Holders },
            },
        };
        var s = S();
        Run(s, pack, "f-raid", "u1");
        var full = s.Log[^1];
        Assert.Equal(("f-raid", (int?)1), (full.AppliedRun, full.Stage));
        var blue = PlayerView.For(s, pack, RunnerFx.Model, new HashSet<string> { "blue" });
        var line = blue.Log[^1];
        Assert.Equal("Red ledger changed", line.Text);
        Assert.Equal(((string?)null, (int?)null), (line.AppliedRun, line.Stage));
        var red = PlayerView.For(s, pack, RunnerFx.Model, new HashSet<string> { "red" });
        Assert.Equal(("f-raid", (int?)1), (red.Log[^1].AppliedRun, red.Log[^1].Stage));
    }

    [Fact]
    public void Runs_hidden_on_unheld_piece()
    {
        var pack = Pack();
        var s = S();
        Run(s, pack, "f-raid", "u1");
        Run(s, pack, "f-raid", "b1");
        var blue = PlayerView.For(s, pack, RunnerFx.Model, new HashSet<string> { "blue" });
        Assert.Equal(1.0, N(blue, pack, "f-raid", "b1", "game"));
        var e = Assert.Throws<EvalError>(() => N(blue, pack, "f-raid", "u1", "game"));
        Assert.Equal("runs() cannot be counted in this view", e.Message);
        Assert.True(e.HiddenData);
        // A guest's precondition on another side's unit is undecided, not refused (the host decides).
        var red = PlayerView.For(s, pack, RunnerFx.Model, new HashSet<string> { "red" });
        Run(s, pack, "f-once", "u1");
        var view = PlayerView.For(s, pack, RunnerFx.Model, new HashSet<string> { "blue" });
        var t = Run(view, pack, "f-once", "u1", _ => { });
        Assert.False(t.Refused);
        Assert.Null(t.Result);
        Assert.True(Run(PlayerView.For(s, pack, RunnerFx.Model, new HashSet<string> { "red" }), pack, "f-once", "u1", _ => { }).Refused);
        Assert.Equal(1.0, N(red, pack, "f-raid", "u1", "game"));
    }

    [Fact]
    public void Runs_unitless_hidden_in_player_view()
    {
        var pack = Pack();
        var s = S();
        Run(s, pack, "f-global", null);
        Assert.Equal(1.0, N(s, pack, "f-global", null, "game"));
        var blue = PlayerView.For(s, pack, RunnerFx.Model, new HashSet<string> { "blue" });
        var e = Assert.Throws<EvalError>(() => N(blue, pack, "f-global", null, "game"));
        Assert.True(e.HiddenData);
        Assert.Equal("runs() cannot be counted in this view", e.Message);
        Assert.False(PieceFilter.EvaluateGameLevel("runs(\"f-global\") == 1", blue, pack, RunnerFx.Model));   // hidden: fails closed
        Assert.False(PieceFilter.EvaluateGameLevel("runs(\"f-global\") != 1", blue, pack, RunnerFx.Model));
    }
}
