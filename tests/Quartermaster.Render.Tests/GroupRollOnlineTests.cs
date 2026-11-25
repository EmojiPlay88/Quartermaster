using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B4 group rolls online (invented Riverland data): a guest's pending rows of one group preview the same host dice (pin key per group);
/// once a row is applied the state's group dice win; a guest's view never carries the group dice.
/// </summary>
[Collection(AppDataRootEnv.Name)]
public class GroupRollOnlineTests : OtherPiecesTestBase
{
    private static readonly Dictionary<string, string> None = new();

    private static FormulaVerdict Send(ModuleSession s, PhaseController c, FormulaPins pins, IReadOnlySet<string> sides, string who,
        string runId, string pieceId, bool preview)
    {
        using (c.ActingFor(sides, who))
            return FormulaIntentRunner.Run(s, c, sides, new FormulaIntent("f", pieceId, runId, None, false, preview), guest: true, who: who, pins: pins);
    }

    private (ModuleSession S, PhaseController C, PendingRun Run) Start()
    {
        var (s, c) = Open(Difficulty.SemiAuto, new[] { GroupRollRunTests.Ford() }, phases: GroupRollRunTests.FordPhase());
        GroupRollRunTests.Counting(s);
        c.Start();
        return (s, c, Assert.Single(s.Game!.State.Pending));
    }

    [Fact]
    public void Guest_group_rows_preview_same_dice()
    {
        var (s, c, run) = Start();
        var pins = new FormulaPins();
        var a = Send(s, c, pins, North, "red-guest", run.Id, "n2", preview: true);
        var b = Send(s, c, pins, North, "red-guest", run.Id, "n1", preview: true);
        Assert.Equal(a.Rolls!["D"], b.Rolls!["D"]);
        var other = Send(s, c, pins, South, "blue-guest", run.Id, "s1", preview: true);
        Assert.NotEqual(a.Rolls["D"], other.Rolls!["D"]);   // another side's group, its own dice
        Assert.Empty(s.Game!.Log.OfType<RollDice>());   // previews log nothing
    }

    [Fact]
    public void Guest_view_has_no_group_rolls()
    {
        var (s, c, run) = Start();
        var applied = Send(s, c, new FormulaPins(), North, "red-guest", run.Id, "n1", preview: false);
        Assert.True(applied.Applied, applied.Message);
        Assert.NotNull(s.Game!.State.Pending.Single().GroupRolls);
        foreach (var sides in new[] { North, South })
            Assert.All(PlayerView.For(s.Game.State, s.Rules, s.Model, sides).Pending, r => Assert.Null(r.GroupRolls));
        var guest = Guest(s, hiding: false);
        Assert.All(guest.Game!.State.Pending, r => Assert.Null(r.GroupRolls));
        Assert.Null(RollGroups.SharedFor(guest.Game.State, run.Id, "s1"));
    }

    [Fact]
    public void Guest_applied_group_row_then_second_uses_state_dice()
    {
        var (s, c, run) = Start();
        var pins = new FormulaPins();
        var g = s.Game!;
        var mark = g.Log.Count;
        var first = Send(s, c, pins, North, "red-guest", run.Id, "n1", preview: false);
        Assert.True(first.Applied, first.Message);
        var dice = Assert.Single(g.Log.Skip(mark).OfType<RollDice>()).Results.ToList();
        Assert.Equal(dice, RollGroups.SharedFor(g.State, run.Id, "n2")!["D"]);

        var preview = Send(s, c, pins, North, "red-guest", run.Id, "n2", preview: true);
        Assert.Equal(dice, preview.Rolls!["D"]);
        mark = g.Log.Count;
        var second = Send(s, c, pins, North, "red-guest", run.Id, "n2", preview: false);
        Assert.True(second.Applied, second.Message);
        Assert.Empty(g.Log.Skip(mark).OfType<RollDice>());   // the group's dice were logged once
        Assert.Equal(Str(s, "n1"), Str(s, "n2"));
    }
}
