using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B7 #53: a sealed start entry asks every side in secret; each answer is stored on its run, and the answer that completes the group releases
/// them all, in side order, in its own action. Invented Riverland data: phase "River toll" (no side), entry "Toll bid" (Input bid 0-10, Global
/// TollBid keyed by the "Toll sides" table, Key askside()); the Northern League (<c>north</c>) and the Southern Compact (<c>south</c>).
/// </summary>
public class SealedFlowTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-b7sealed-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    private static readonly PlayerDef Alice = new("p1", "Alice", new[] { "north" });
    private static readonly PlayerDef Bob = new("p2", "Bob", new[] { "south" });
    private static readonly IReadOnlySet<string> North = new HashSet<string> { "north" };
    private static readonly IReadOnlySet<string> South = new HashSet<string> { "south" };

    private const string Notice = "Answer sealed; it is applied when every side has answered.";
    private const string AlreadyIn = "Your sealed answer is already in.";
    private const string Released = "Toll bid: every side has answered";
    private const string Discarded = "Toll bid: not every side answered; the sealed answers were discarded";

    public SealedFlowTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    /// <param name="spy">The bid adds N, an Input whose auto value reads the Northern League's Private "Funds" (hidden from the south).</param>
    private static FormulaDef Toll(string? precondition = null, bool spy = false) => new()
    {
        Id = "f-toll", Name = "Toll bid", Text = spy ? "bid + N" : "bid", Precondition = precondition,
        Bindings = spy
            ? new()
            {
                new VariableBinding { Variable = "bid", Kind = BindingKind.Input, Min = 0, Max = 10 },
                new VariableBinding { Variable = "N", Kind = BindingKind.Input, AutoValue = "holder(NorthDepot, Funds)" },
            }
            : new() { new VariableBinding { Variable = "bid", Kind = BindingKind.Input, Min = 0, Max = 10 } },
        Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "p-bid", Op = OutputOp.Set, Key = "askside()" } },
    };

    private static readonly FormulaDef Mark = new()
    {
        Id = "f-mark", Name = "Ferry opens", Text = "1",
        Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "p-open", Op = OutputOp.Set } },
    };

    private static RulesPack Pack(string sha, string? precondition, bool spy = false)
    {
        var p = PhaseFixture.Pack(sha);
        return p with
        {
            Format = 4,
            Sides = new()
            {
                new SideDef { Id = "north", Name = "Northern League", UnitFilter = "startsWith(name(), \"Red\")" },
                new SideDef { Id = "south", Name = "Southern Compact", UnitFilter = "startsWith(name(), \"Blue\")" },
            },
            Properties = p.Properties.Concat(new[]
            {
                new PropertyDef { Id = "p-bid", Name = "TollBid", Scope = PropertyScope.Holders, KeyedBy = "table:t-sides", Holders = new() { "global" }, Visibility = Visibility.Public },
                new PropertyDef { Id = "p-open", Name = "FerryOpen", Scope = PropertyScope.Holders, Holders = new() { "global" }, Visibility = Visibility.Public },
                new PropertyDef { Id = "p-funds", Name = "Funds", Scope = PropertyScope.Holders, Visibility = Visibility.Private },
            }).ToList(),
            Holders = (p.Holders ?? new()).Append(new HolderDef { Id = "northdepot", Name = "NorthDepot", SideId = "north" }).ToList(),
            Tables = p.Tables.Append(new TableDef
            {
                Id = "t-sides", Name = "Toll sides", Kind = TableKind.OneD, RowKeys = new() { "Northern League", "Southern Compact" },
                ColumnKeys = new() { "Value" }, Cells = new() { new() { "1" }, new() { "2" } },
            }).ToList(),
            Formulas = p.Formulas.Concat(new[] { Toll(precondition, spy), Mark }).ToList(),
            Phases = p.Phases.Select(x => x.Id == "ph-weather"
                ? x with
                {
                    Name = "River toll",
                    OnStart = new()
                    {
                        new StartEntry { FormulaId = "f-toll", ForEach = ForEachMode.Once, Sealed = true },
                        new StartEntry { FormulaId = "f-mark", ForEach = ForEachMode.Once },
                    },
                }
                : x.Id == "ph-supply" || x.Id == "ph-move" ? x with { SideId = "north", OnStart = new() } : x.Id == "ph-combat" ? x with { SideId = "south" } : x).ToList(),
        };
    }

    private (ModuleSession S, PhaseController C, IntentExecutor X) Open(Difficulty d = Difficulty.FullyAuto, bool solo = false, string? precondition = null, bool spy = false)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(4, n).ToList());
        if (solo) s.LoadSetup(s.Model.Setups[0], d);
        else s.LoadSetup(s.Model.Setups[0], d, new[] { Alice, Bob });
        s.NewRules("t");
        s.UpdateRules(_ => Pack(s.Archive.Sha256, precondition, spy));
        var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        return (s, c, new IntentExecutor(s, c));
    }

    private static PendingRun RunOf(ModuleSession s, string side) => s.Game!.State.Pending.Single(r => r.AskSideId == side);

    private static FormulaTrace Bid(ModuleSession s, string side, int bid) =>
        s.Runner!.Run(s.Game!.State, new RunRequest("f-toll", null, new Dictionary<string, string> { ["bid"] = bid.ToString() },
            new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>()) { AskSideId = side, AutoInputs = new HashSet<string> { "N" } });

    private static void SetFunds(ModuleSession s, double from, double to) =>
        s.Game!.Execute(new LedgerDelta("h:northdepot", "p-funds", from, to, "manual", null, null, null, null));

    /// <summary>The host's screen for <paramref name="side"/> answers its run (hot-seat).</summary>
    private static bool Answer(ModuleSession s, PhaseController c, string side, int bid)
    {
        if (!Players.IsSolo(s.Game!.State)) s.SetViewSides(side == "north" ? North : South);
        return c.ApplyFormula(RunOf(s, side).Id, null, Bid(s, side, bid), false);
    }

    private static double? BidOf(ModuleSession s, string sideName) =>
        s.Game!.State.Keyed?.GetValueOrDefault(LedgerKey.Holder(HolderDef.GlobalId))?.GetValueOrDefault("p-bid")?.GetValueOrDefault(sideName) is { } v ? v : null;

    private static double? Open_(ModuleSession s) => Ledger.Get(s.Game!.State, LedgerKey.Holder(HolderDef.GlobalId), "p-open");

    private static FormulaIntent Intent(string runId, int bid) =>
        new("f-toll", null, runId, new Dictionary<string, string> { ["bid"] = bid.ToString() }, false);

    [Fact]
    public void Sealed_entry_queues_one_run_per_side_in_pack_order()
    {
        var (s, c, _) = Open();
        var mark = s.Game!.Log.Count;
        c.Start();
        var runs = s.Game.State.Pending;
        Assert.Equal(new[] { "north", "south" }, runs.Select(r => r.AskSideId).ToArray());
        Assert.All(runs, r => Assert.Equal(runs[0].Id, r.Seal));
        Assert.All(runs, r => Assert.Equal("start", r.Trigger));
        Assert.All(runs, r => Assert.Null(Assert.Single(r.Units).PieceId));
        Assert.Single(s.Game.Log.Skip(mark).OfType<QueueRuns>());   // one QueueRuns for the group
    }

    [Fact]
    public void Fully_auto_still_asks()
    {
        foreach (var d in new[] { Difficulty.FullyAuto, Difficulty.SemiAuto, Difficulty.Hardcore })
        {
            var (s, c, _) = Open(d);
            c.Start();
            Assert.Equal(2, s.Game!.State.Pending.Count);
            Assert.Null(BidOf(s, "Northern League"));
        }
    }

    [Fact]
    public void First_answer_stores_not_applies()
    {
        var (s, c, _) = Open();
        c.Start();
        var ledger = s.Game!.State.LedgerHistory.Count;
        Assert.True(Answer(s, c, "north", 7));
        Assert.Equal(ledger, s.Game.State.LedgerHistory.Count);
        Assert.Null(BidOf(s, "Northern League"));
        Assert.Equal("7", RunOf(s, "north").SealedAnswer!["bid"]);
        Assert.False(RunOf(s, "north").Units[0].Applied);
        Assert.Equal(Notice, c.View().Notice);
        Assert.Equal("Northern League has answered Toll bid (sealed)", s.Game.State.Log.Last().Text);
    }

    [Fact]
    public void Second_answer_releases_both_in_side_order()
    {
        var (s, c, _) = Open();
        c.Start();
        Assert.True(Answer(s, c, "south", 4));   // the south answers first; release is still in side order
        var mark = s.Game!.Log.Count;
        Assert.True(Answer(s, c, "north", 7));
        Assert.Equal(7, BidOf(s, "Northern League"));
        Assert.Equal(4, BidOf(s, "Southern Compact"));
        Assert.Empty(s.Game.State.Pending);
        var added = s.Game.Log.Skip(mark).ToList();
        var release = added.FindIndex(x => x is AddLogLine { Text: Released });
        Assert.True(release >= 0);
        Assert.Equal(LogKind.NoWorking, ((AddLogLine)added[release]).FormulaId);
        var deltas = added.OfType<LedgerDelta>().Where(x => x.PropertyId == "p-bid").ToList();
        Assert.Equal(new[] { "Northern League", "Southern Compact" }, deltas.Select(x => x.ValueKey).ToArray());
        Assert.Equal(new[] { "north", "south" }, deltas.Select(x => x.SideId).ToArray());
        Assert.True(release < added.IndexOf(deltas[0]));
        Assert.Null(c.View().Notice);
    }

    [Fact]
    public void Release_one_undo_takes_back_answer_and_release()
    {
        var (s, c, _) = Open();
        c.Start();
        Assert.True(Answer(s, c, "north", 7));
        Assert.True(Answer(s, c, "south", 4));
        Assert.Equal(1, Open_(s));   // the next start entry ran after the release
        s.Game!.Undo();
        Assert.Null(BidOf(s, "Northern League"));
        Assert.Null(Open_(s));
        Assert.Equal(2, s.Game.State.Pending.Count);
        Assert.Equal("7", RunOf(s, "north").SealedAnswer!["bid"]);
        Assert.Null(RunOf(s, "south").SealedAnswer);
    }

    [Fact]
    public void Release_failure_logs_side_error_and_continues()
    {
        // The precondition holds at each answer, but the north's release writes its bid first, so the south's run is refused at release.
        var (s, c, _) = Open(precondition: "keyed(TollBid, \"Northern League\") == 0");
        c.Start();
        Assert.Equal(2, s.Game!.State.Pending.Count);
        Assert.Null(Bid(s, "north", 7).Error);
        Assert.True(Answer(s, c, "north", 7));
        Assert.True(Answer(s, c, "south", 4));
        Assert.Equal(7, BidOf(s, "Northern League"));
        Assert.NotEqual(4, BidOf(s, "Southern Compact"));   // the refused run wrote nothing
        Assert.DoesNotContain(s.Game!.Log.OfType<LedgerDelta>(), d => d.ValueKey == "Southern Compact");
        var err = s.Game!.State.Log.Single(l => l.Text.StartsWith("Toll bid: Southern Compact: ", StringComparison.Ordinal));
        Assert.True(err.Error);
        Assert.Equal("f-toll", err.FormulaId);
        Assert.Equal("south", err.SideId);
        Assert.Empty(s.Game.State.Pending);
        Assert.Equal(1, Open_(s));
    }

    [Fact]
    public void Second_answer_refused_already_in()
    {
        var (s, c, x) = Open();
        c.Start();
        Assert.True(Answer(s, c, "north", 7));
        var run = RunOf(s, "north");
        Assert.Equal(AlreadyIn, PhaseController.AnswerRefusal(s.Game!.State, run.Id));   // the host dialog's refusal
        Assert.Null(PhaseController.AnswerRefusal(s.Game.State, RunOf(s, "south").Id));
        var count = s.Game.Log.Count;
        Assert.False(Answer(s, c, "north", 3));
        Assert.Equal(count, s.Game.Log.Count);
        Assert.Equal("7", RunOf(s, "north").SealedAnswer!["bid"]);

        var r = x.Execute("p1", North, Intent(run.Id, 2));   // and the guest's intent
        Assert.False(r.Ok);
        Assert.Equal(AlreadyIn, r.Message);
        Assert.Equal("7", RunOf(s, "north").SealedAnswer!["bid"]);
    }

    [Fact]
    public void Guest_answers_sealed_run()
    {
        var (s, c, x) = Open();
        c.Start();
        var r = x.Execute("p2", South, Intent(RunOf(s, "south").Id, 4));
        Assert.True(r.Ok, r.Message);
        Assert.Equal("4", RunOf(s, "south").SealedAnswer!["bid"]);
        Assert.Null(BidOf(s, "Southern Compact"));
        Assert.True(x.Execute("p1", North, Intent(RunOf(s, "north").Id, 6)).Ok);
        Assert.Equal(4, BidOf(s, "Southern Compact"));   // askside() was the guest's side
        Assert.Equal(6, BidOf(s, "Northern League"));
    }

    [Fact]
    public void Guest_cannot_answer_other_side_sealed_run()
    {
        var (s, c, x) = Open();
        c.Start();
        var r = x.Execute("p2", South, Intent(RunOf(s, "north").Id, 9));
        Assert.False(r.Ok);
        Assert.Equal("That calculation " + ModuleSession.OtherSideText + ".", r.Message);
        Assert.Null(RunOf(s, "north").SealedAnswer);
    }

    [Fact]
    public void Leaving_early_discards_with_line()
    {
        var (s, c, _) = Open();
        c.Start();
        Assert.True(Answer(s, c, "north", 7));
        var mark = s.Game!.Log.Count;
        var at = s.Game.State.Position;
        c.Advance();
        Assert.NotEqual(at, s.Game.State.Position);
        Assert.Empty(s.Game.State.Pending);
        Assert.Null(BidOf(s, "Northern League"));
        var added = s.Game.Log.Skip(mark).ToList();
        var line = added.FindIndex(x => x is AddLogLine { Text: Discarded });
        Assert.True(line >= 0);
        Assert.Equal(LogKind.NoWorking, ((AddLogLine)added[line]).FormulaId);
        Assert.True(line < added.FindIndex(x => x is AdvancePhase));
        s.Game.Undo();   // one press, one undo step (the discard line with it)
        Assert.Equal(at, s.Game.State.Position);
        Assert.Equal("7", RunOf(s, "north").SealedAnswer!["bid"]);
        Assert.DoesNotContain(s.Game.State.Log, l => l.Text == Discarded);
    }

    [Fact]
    public void Leaving_with_no_answer_no_line()
    {
        var (s, c, _) = Open();
        c.Start();
        c.Advance();
        Assert.DoesNotContain(s.Game!.State.Log, l => l.Text == Discarded);
    }

    [Fact]
    public void Start_cursor_waits_for_release()
    {
        var (s, c, _) = Open();
        c.Start();
        Assert.Equal(1, s.Game!.State.StartCursor?.Next);
        Assert.Null(Open_(s));
        Assert.True(Answer(s, c, "north", 7));
        Assert.Equal(1, s.Game.State.StartCursor?.Next);
        Assert.Null(Open_(s));
        Assert.True(Answer(s, c, "south", 4));
        Assert.Equal(1, Open_(s));
        Assert.Null(s.Game.State.StartCursor);
    }

    [Fact]
    public void Hotseat_other_player_cannot_see_answer()
    {
        var (s, c, _) = Open();
        c.Start();
        Assert.True(Answer(s, c, "north", 7));
        s.SetViewSides(South);
        var view = s.View!;
        Assert.DoesNotContain(view.Pending, r => r.SealedAnswer != null);
        Assert.Contains(view.Log, l => l.Text == "Northern League has answered Toll bid (sealed)");
        Assert.DoesNotContain(c.View().Decisions, d => d.RunId == RunOf(s, "north").Id);
    }

    [Fact]
    public void Hotseat_playing_text_after_own_answer()
    {
        var (s, c, _) = Open();
        c.Start();
        s.SetViewSides(North);
        Assert.Null(c.View().PlayingText);   // its own answer comes first
        Assert.True(Answer(s, c, "north", 7));
        Assert.Equal("Southern Compact is playing River toll", c.View().PlayingText);
        Assert.Equal("south", PhaseController.OpenAskSide(s.Game!.State));
        Assert.Equal("south", PhaseController.OpenAskFor(s.Game.State, North)?.AskSideId);
        Assert.Null(PhaseController.OpenAskFor(s.Game.State, South));   // the north's answered run is no longer open
    }

    [Fact]
    public void Hidden_input_is_not_sealed_and_release_reads_its_auto_value()
    {
        var (s, c, _) = Open(spy: true);
        SetFunds(s, 0, 5);
        c.Start();
        Assert.True(Answer(s, c, "south", 4));   // the engine read N = 5 from the north's Private funds
        Assert.Equal(new[] { "bid" }, RunOf(s, "south").SealedAnswer!.Keys);
        s.SetViewSides(South);
        var own = s.View!.Pending.Single(r => r.AskSideId == "south");
        Assert.NotNull(own.SealedAnswer);
        Assert.False(own.SealedAnswer!.ContainsKey("N"));
        Assert.DoesNotContain(own.SealedAnswer.Values, v => v == "5");
        SetFunds(s, 5, 9);
        Assert.True(Answer(s, c, "north", 1));
        Assert.Equal(13, BidOf(s, "Southern Compact"));   // 4 + the funds at release, not the stale 5
        Assert.Equal(10, BidOf(s, "Northern League"));
    }

    [Fact]
    public void Hotseat_hands_over_after_a_partial_sealed_answer()
    {
        var (s, c, _) = Open();
        var hs = new HotSeat(s, c);
        hs.TakeOver(Alice);
        c.Start();
        var due = new List<string>();
        hs.HandoverRequired += p => due.Add(p.Id);
        Assert.True(c.ApplyFormula(RunOf(s, "north").Id, null, Bid(s, "north", 7), false));
        Assert.Equal("p2", hs.HandoverDue()?.Id);
        Assert.Equal(new[] { "p2" }, due);
        hs.TakeOver(Bob);
        Assert.True(c.ApplyFormula(RunOf(s, "south").Id, null, Bid(s, "south", 3), false));
        Assert.Equal(7, BidOf(s, "Northern League"));
        Assert.Equal(3, BidOf(s, "Southern Compact"));
    }

    [Fact]
    public void Release_and_discard_lines_name_no_side()
    {
        var (s, c, _) = Open();
        c.Start();
        Assert.True(Answer(s, c, "north", 7));
        Assert.True(Answer(s, c, "south", 3));
        Assert.Null(s.Game!.State.Log.Last(l => l.Text == Released).SideId);
        var (s2, c2, _) = Open();
        c2.Start();
        Assert.True(Answer(s2, c2, "north", 7));
        s2.SetViewSides(North);
        c2.Advance();
        Assert.Null(s2.Game!.State.Log.Last(l => l.Text == Discarded).SideId);
    }

    [Fact]
    public void Solo_answers_each_side()
    {
        var (s, c, _) = Open(solo: true);
        Assert.Null(s.ViewSides);
        c.Start();
        Assert.True(Answer(s, c, "north", 7));
        Assert.True(Answer(s, c, "south", 4));
        Assert.Equal(7, BidOf(s, "Northern League"));
        Assert.Equal(4, BidOf(s, "Southern Compact"));
    }
}
