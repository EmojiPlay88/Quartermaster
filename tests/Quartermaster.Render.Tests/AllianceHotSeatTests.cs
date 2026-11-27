using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>B8 #2: in hot-seat a player's view widens to its allies once (ModuleSession.SetViewSides); every rule reads that set.</summary>
public class AllianceHotSeatTests : AllianceTestBase
{
    private static readonly Dictionary<string, string> None = new();

    [Fact]
    public void Hotseat_ally_sees_private_holder_value()
    {
        var s = Session();
        s.SetViewSides(North);
        Assert.Equal(CampSupply, Supply(s.View!, "hillcamp"));
        Assert.Null(Supply(s.View!, "southdepot"));
        s.SetViewSides(South);
        Assert.Null(Supply(s.View!, "hillcamp"));
        Assert.Equal(SouthSupply, Supply(s.View!, "southdepot"));
    }

    [Fact]
    public void Hotseat_ally_sees_private_dice_line()
    {
        var s = Session(d: Difficulty.SemiAuto);
        var c = Controller(s);
        To(s, c, "ph-supply");   // the Northern League's phase
        s.SetViewSides(North);
        var trace = s.Runner!.Run(s.Game!.State, new RunRequest("f-weather", null, None, None, new Dictionary<string, IReadOnlyList<int>>()));
        Assert.True(c.ApplyFormula(null, null, trace, false));
        Assert.Equal("north", s.Game.Log.OfType<RollDice>().Last().SideId);

        s.SetViewSides(Hills);   // the ally
        Assert.DoesNotContain(s.View!.Log, l => l.Text == "Northern League rolled dice");
        Assert.NotEmpty(s.View!.DiceHistory[^1].Results);
        s.SetViewSides(South);
        Assert.Contains(s.View!.Log, l => l.Text == "Northern League rolled dice");
        Assert.Empty(s.View!.DiceHistory[^1].Results);
    }

    [Fact]
    public void Hotseat_ally_sees_side_alert_and_ask_run()
    {
        var s = Session();
        s.Game!.Execute(new QueueRuns(new[]
        {
            new PendingRun("ask-hills", "f-gate", "Open gate", "start", new List<PendingUnit> { new(null, "Open gate", null, false) }, false) { AskSideId = "hills" },
        }));
        IEnumerable<string> Alerts() => new AlertBarModel(s).Recompute(null, null, new HashSet<string>()).Select(x => x.Id).Where(id => id.StartsWith("a-", StringComparison.Ordinal)).OrderBy(x => x, StringComparer.Ordinal);

        s.SetViewSides(North);
        Assert.Equal(new[] { "a-hills", "a-north" }, Alerts());
        Assert.Contains(s.View!.Pending, r => r.Id == "ask-hills");
        Assert.Null(PhaseController.OpenAskFor(s.Game.State, s.ViewSides!));   // the ally's ask is not "another side's" ask
        s.SetViewSides(South);
        Assert.Equal(new[] { "a-south" }, Alerts());
        Assert.DoesNotContain(s.View!.Pending, r => r.Id == "ask-hills");
        Assert.Equal("ask-hills", PhaseController.OpenAskFor(s.Game.State, s.ViewSides!)?.Id);
    }

    [Fact]
    public void Hotseat_ally_sees_owned_map()
    {
        var s = Session();
        s.SetViewSides(North);
        Assert.True(s.CanSeeMap(HillMap));
        s.SetViewSides(South);
        Assert.False(s.CanSeeMap(HillMap));
    }

    [Fact]
    public void Hotseat_ally_can_act_on_ally_unit()
    {
        var s = Session();
        s.SetViewSides(North);
        Assert.True(s.CanAct(PhaseFixture.RedOne));
        Assert.True(s.CanAct(PhaseFixture.RedTwo));
        Assert.False(s.CanAct(PhaseFixture.BlueOne));
        Assert.True(s.CanEditLedger(LedgerKey.Holder("hillcamp")));
        Assert.False(s.CanEditLedger(LedgerKey.Holder("southdepot")));
        s.SetViewSides(South);
        Assert.False(s.CanAct(PhaseFixture.RedTwo));
    }

    [Fact]
    public void Hotseat_ally_may_act_in_ally_phase()
    {
        var s = Session(d: Difficulty.SemiAuto);
        var c = Controller(s);
        To(s, c, "ph-combat");   // the Hill Clans' phase
        s.SetViewSides(North);
        var view = c.View();
        Assert.Null(view.PlayingText);
        Assert.Contains(view.Decisions, d => d.Text == "Resolve attacks");
        Assert.Equal("hills", s.ActorSide());   // a run here is stamped with the ally's acting side

        var actions = new LocalActions(s, c, new HistoryController(s, c, (cmd, st) => CommandLog.Prepare(cmd, st, s)));
        var v = actions.ApplyFormula(new FormulaIntent("f-gate", null, null, None, true));
        Assert.True(v.Applied, v.Message);
        Assert.Equal(1, Gate(s.Game!.State));
        Assert.Equal("hills", s.Game.Log.OfType<LedgerDelta>().Last().SideId);

        c.Advance();
        Assert.Equal("ph-end", s.Game.State.Position?.PhaseId);
        s.SetViewSides(North);
        Assert.Equal("Southern Compact is playing End", c.View().PlayingText);   // not an ally's phase
    }

    [Fact]
    public void Hotseat_ally_missing_inputs_apply_to_ally_units()
    {
        var s = Session();
        var c = Controller(s);
        To(s, c, "ph-move");
        c.ExecuteMove(new[] { new MovePieces(new[] { PhaseFixture.RedOne, PhaseFixture.RedTwo, PhaseFixture.BlueOne }, "Main Map", new PointD(130, 50), null) });
        var run = Assert.Single(s.Game!.State.Pending, r => r.FormulaId == "f-fuel");
        s.SetViewSides(North);
        Assert.Equal("Terrain", Assert.Single(MissingInputs.For(s, run)).Variable);
        var (applied, failed) = MissingInputs.ApplyAll(s, c, run, new Dictionary<string, string> { ["Terrain"] = "Sand" });
        Assert.Equal(2, applied);   // Red 1 and the ally's Red 2
        Assert.Single(failed, f => f.EndsWith(ModuleSession.OtherSideText, StringComparison.Ordinal));
        Assert.NotEqual(40, Ledger.Get(s.Game.State, LedgerKey.Piece(PhaseFixture.RedTwo), "fuel"));
        Assert.Equal(40, Ledger.Get(s.Game.State, LedgerKey.Piece(PhaseFixture.BlueOne), "fuel"));
    }

    [Fact]
    public void Hotseat_non_ally_sees_nothing_more()
    {
        var s = Session();
        s.SetViewSides(South);
        var with = JsonSerializer.Serialize(s.View, QuartermasterJson.Options);
        s.UpdateRules(_ => Pack(s.Archive.Sha256, alliance: false));
        s.SetViewSides(South);
        var without = JsonSerializer.Serialize(s.View, QuartermasterJson.Options);
        Assert.Equal(without, with);
    }

    [Fact]
    public void Hotseat_side_of_ally_unit_unchanged()
    {
        var s = Session();
        s.SetViewSides(North);
        var st = s.Game!.State;
        var isClans = PieceFilter.Compile("side() == \"Hill Clans\"", st, s.Rules!, s.Model);
        Assert.True(isClans(st.Pieces[PhaseFixture.RedTwo]));
        Assert.False(isClans(st.Pieces[PhaseFixture.RedOne]));
        Assert.Equal("hills", Ownership.SideOfHolder("hillcamp", s.Rules!));
    }

    [Fact]
    public void Player_sides_raw_view_sides_closed()
    {
        var s = Session();
        var given = new HashSet<string> { "north" };
        s.SetViewSides(given);
        Assert.Equal(new[] { "north" }, s.PlayerSides!.OrderBy(x => x));
        Assert.Equal(new[] { "hills", "north" }, s.ViewSides!.OrderBy(x => x, StringComparer.Ordinal));
        given.Add("south");   // the session keeps its own copy
        Assert.DoesNotContain("south", s.PlayerSides!);
        Assert.DoesNotContain("south", s.ViewSides!);
        s.SetViewSides(null);
        Assert.Null(s.PlayerSides);
        Assert.Null(s.ViewSides);
    }

    [Fact]
    public void Empty_view_sides_stay_empty_on_handover()
    {
        var s = Session();
        var c = Controller(s);
        var hs = new HotSeat(s, c);
        hs.TakeOver(Alice);
        Assert.Equal(new[] { "hills", "north" }, s.ViewSides!.OrderBy(x => x, StringComparer.Ordinal));
        To(s, c, "ph-end");   // the Southern Compact's phase: a handover to Bob is due
        Assert.Equal("p2", hs.Pending?.Id);
        Assert.Empty(s.ViewSides!);
        Assert.Empty(s.PlayerSides!);
        Assert.Null(Supply(s.View!, "hillcamp"));
    }

    [Fact]
    public void Alliance_added_in_workbench_applies_to_open_view()
    {
        var s = Session(alliance: false);
        s.SetViewSides(North);
        Assert.Equal(new[] { "north" }, s.ViewSides!);
        Assert.Null(Supply(s.View!, "hillcamp"));

        s.UpdateRules(_ => Pack(s.Archive.Sha256, alliance: true));
        Assert.Equal(new[] { "hills", "north" }, s.ViewSides!.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(CampSupply, Supply(s.View!, "hillcamp"));
        Assert.Equal(new[] { "north" }, s.PlayerSides!);

        var path = Path.Combine(Tmp, "plain.qrules");   // opening a pack goes through the other path (Commit)
        RulesPackIo.Save(Pack(s.Archive.Sha256, alliance: false), path);
        s.OpenRules(path);
        Assert.Equal(new[] { "north" }, s.ViewSides!);
        Assert.Null(Supply(s.View!, "hillcamp"));
    }

    [Fact]
    public void No_alliance_same_set_instance()
    {
        var s = Session(alliance: false);
        var given = new HashSet<string> { "north" };
        s.SetViewSides(given);
        Assert.NotSame(given, s.PlayerSides);   // copied once
        Assert.Same(s.PlayerSides, s.ViewSides);   // SideFacts.Held returned it unchanged
    }
}
