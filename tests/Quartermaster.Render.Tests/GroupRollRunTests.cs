using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B4 group rolls on the Render side (invented Riverland data): every pending-run path (batch grid, unit dialog with a run link,
/// missing inputs, Fully automatic) shares one roll per hex and side; removed units are counted in summaries and titles.
/// </summary>
[Collection(AppDataRootEnv.Name)]
public class GroupRollRunTests : OtherPiecesTestBase
{
    /// <summary>"Ford crossing": 2d6 written to the unit's Strength; one roll per hex and side when <paramref name="group"/>.</summary>
    internal static FormulaDef Ford(bool group = true, string text = "D") =>
        F("Ford crossing", text, text.Contains('N')
                ? new[] { DiceD(), new VariableBinding { Variable = "N", Kind = BindingKind.Input } }
                : new[] { DiceD() },
            UnitWrite(OutputOp.Set)) with { GroupRoll = group };

    private static VariableBinding DiceD() => new() { Variable = "D", Kind = BindingKind.Roll, DiceCount = 2, DiceSides = 6 };

    /// <summary>A phase of no side whose start runs "f" for every unit (n1, n2 and s1 share a hex; s2 is alone).</summary>
    internal static PhaseDef[] FordPhase() => new[]
    {
        new PhaseDef { Id = "ph-ford", Name = "Ford", OnStart = new() { new StartEntry { FormulaId = "f", ForEach = ForEachMode.Units } } },
    };

    /// <summary>Dice 1, 2, 3 ... 6, 1, ... in roll order, so every new roll differs from the one before.</summary>
    internal static void Counting(ModuleSession s)
    {
        var k = 0;
        s.SetRoller((n, _) => Enumerable.Range(0, n).Select(_ => k++ % 6 + 1).ToList());
    }

    private static int RowOf(PendingRun run, string id) => run.Units.ToList().FindIndex(u => u.PieceId == id);

    private static List<RollDice> RollsSince(ModuleSession s, int mark) => s.Game!.Log.Skip(mark).OfType<RollDice>().ToList();

    [Fact]
    public void Hook_group_one_roll_per_hex_and_side()
    {
        var (s, c) = Open(Difficulty.SemiAuto, new[] { Ford() }, phases: FordPhase());
        Counting(s);
        c.Start();
        var run = Assert.Single(s.Game!.State.Pending);
        string? G(string id) => run.Units.Single(u => u.PieceId == id).Group;
        Assert.Equal("red/1", G("n1"));
        Assert.Equal("red/1", G("n2"));
        Assert.Equal("blue/1", G("s1"));
        Assert.Equal("blue/2", G("s2"));

        var b = new BatchModel(s, c, run.Id);
        var d = b.Variables.ToList().IndexOf("D");
        var (first, second) = RowOf(run, "n1") < RowOf(run, "n2") ? ("n1", "n2") : ("n2", "n1");
        // The later row is shown first: it still previews the group's dice (those of the group's first open row).
        var later = b.Row(RowOf(run, second)).Cells[d];
        Assert.Equal(b.Row(RowOf(run, first)).Cells[d], later);
        Assert.NotEqual(later, b.Row(RowOf(run, "s1")).Cells[d]);   // another side in the same hex rolls its own
        var box = b.ModelFor(RowOf(run, second)).Evaluate(s.Game.State).Boxes.Single(x => x.Variable == "D");
        Assert.Equal(" (group roll)", box.CaptionSuffix);
        Assert.Equal("Rolled once for the group", box.Tooltip);
        Assert.False(box.Enabled);
        Assert.Null(box.RerollKey);
        var own = b.ModelFor(RowOf(run, first)).Evaluate(s.Game.State).Boxes.Single(x => x.Variable == "D");
        Assert.Equal("", own.CaptionSuffix);

        var mark = s.Game.Log.Count;
        Assert.Equal(4, b.ApplyAll());
        Assert.Equal(3, RollsSince(s, mark).Count);   // red/1 once, blue/1, blue/2
        Assert.Equal(Str(s, "n1"), Str(s, "n2"));
        Assert.NotEqual(Str(s, "n1"), Str(s, "s1"));
    }

    [Fact]
    public void Hook_group_undo_first_row_rerolls_group()
    {
        var (s, c) = Open(Difficulty.SemiAuto, new[] { Ford() }, phases: FordPhase());
        Counting(s);
        c.Start();
        var g = s.Game!;
        var run = Assert.Single(g.State.Pending);
        var (first, second) = RowOf(run, "n1") < RowOf(run, "n2") ? ("n1", "n2") : ("n2", "n1");
        var b = new BatchModel(s, c, run.Id);
        var mark = g.Log.Count;
        Assert.True(b.ApplyRow(RowOf(run, first)));
        var dice = Assert.Single(RollsSince(s, mark)).Results.ToList();
        Assert.Equal(dice, RollGroups.SharedFor(g.State, run.Id, second)!["D"]);

        g.Undo();   // the group's dice go with the row's action
        Assert.Null(RollGroups.SharedFor(g.State, run.Id, second));
        Assert.Null(g.State.Pending.Single().GroupRolls);

        mark = g.Log.Count;
        var again = new BatchModel(s, c, run.Id);
        Assert.True(again.ApplyRow(RowOf(run, second)));   // the next row applied rolls the group again (and logs it)
        var roll = Assert.Single(RollsSince(s, mark)).Results.ToList();
        Assert.NotEqual(dice, roll);
        Assert.Equal(roll, RollGroups.SharedFor(g.State, run.Id, first)!["D"]);
        mark = g.Log.Count;
        Assert.True(again.ApplyRow(RowOf(run, first)));
        Assert.Empty(RollsSince(s, mark));
        Assert.Equal(Str(s, "n1"), Str(s, "n2"));
    }

    [Fact]
    public void FullyAuto_group_shares_dice()
    {
        var (s, c) = Open(Difficulty.FullyAuto, new[] { Ford() }, phases: FordPhase());
        Counting(s);
        var mark = s.Game!.Log.Count;
        c.Start();
        Assert.Empty(s.Game.State.Pending);
        Assert.Equal(3, RollsSince(s, mark).Count);
        Assert.Equal(Str(s, "n1"), Str(s, "n2"));
        Assert.NotEqual(Str(s, "s1"), Str(s, "s2"));
        Assert.Contains(s.Game.State.Log, l => l.Text == "Ford crossing: 4 units, 4 applied");
    }

    [Fact]
    public void Non_group_formula_units_have_no_group()
    {
        var (s, c) = Open(Difficulty.SemiAuto, new[] { Ford(group: false) }, phases: FordPhase());
        c.Start();
        Assert.All(Assert.Single(s.Game!.State.Pending).Units, u => Assert.Null(u.Group));

        var (a, ac) = Open(Difficulty.FullyAuto, new[] { Ford(group: false) }, phases: FordPhase());
        Counting(a);
        var mark = a.Game!.Log.Count;
        ac.Start();
        Assert.Equal(4, RollsSince(a, mark).Count);   // every unit rolls its own
    }

    [Fact]
    public void MissingInputs_applyall_shares_group()
    {
        var (s, c) = Open(Difficulty.FullyAuto, new[] { Ford(text: "D + N") }, phases: FordPhase());
        Counting(s);
        c.Start();
        var run = Assert.Single(s.Game!.State.Pending);   // N is missing for every unit
        Assert.True(run.NeedsInput);
        Assert.Equal("red/1", run.Units.Single(u => u.PieceId == "n2").Group);
        var mark = s.Game.Log.Count;
        var (applied, failed) = MissingInputs.ApplyAll(s, c, run, new Dictionary<string, string> { ["N"] = "1" });
        Assert.Empty(failed);
        Assert.Equal(4, applied);
        Assert.Equal(3, RollsSince(s, mark).Count);
        Assert.Equal(Str(s, "n1"), Str(s, "n2"));
    }

    [Fact]
    public void Unit_dialog_with_run_link_uses_group_dice()
    {
        var (s, c) = Open(Difficulty.SemiAuto, new[] { Ford() }, phases: FordPhase());
        Counting(s);
        c.Start();
        var g = s.Game!;
        var run = Assert.Single(g.State.Pending);
        Assert.Null(FormulaWindowRules.SharedRolls(s, run.Id, "n2"));
        var m1 = Dialog(s, "n1");
        m1.SharedRolls = FormulaWindowRules.SharedRolls(s, run.Id, "n1");
        var t1 = m1.Run(g.State);
        Assert.True(c.ApplyFormula(run.Id, "n1", t1, false));

        var shared = FormulaWindowRules.SharedRolls(s, run.Id, "n2");
        Assert.Equal(t1.RollsByKey["D"], shared!["D"]);
        Assert.Null(FormulaWindowRules.SharedRolls(s, null, "n2"));   // a menu run is a group of one
        var m2 = Dialog(s, "n2");
        m2.SharedRolls = shared;
        var v = m2.Evaluate(g.State);
        var box = v.Boxes.Single(x => x.Variable == "D");
        Assert.Equal(" (group roll)", box.CaptionSuffix);
        Assert.False(box.Enabled);
        m2.Reroll("D");   // no reroll of a group's dice
        var mark = g.Log.Count;
        Assert.True(c.ApplyFormula(run.Id, "n2", m2.Run(g.State), false));
        Assert.Empty(RollsSince(s, mark));
        Assert.Equal(Str(s, "n1"), Str(s, "n2"));
    }

    // ---- removed units ----

    private static FormulaDef Merge() => F("Merge", "0", new[] { Pick(sameHex: true) }, Remove(OutputTarget.Piece));

    [Fact]
    public void ResolveGoneUnits_marks_gone()
    {
        var (s, c) = Open(Difficulty.SemiAuto, new[] { Merge() }, phases: NorthStart());
        var g = s.Game!;
        c.Start();
        var mark = g.Log.Count;
        Assert.True(c.ApplyFormula(null, "n1", Run(s, "n1", new Dictionary<string, string> { ["T"] = Label(s, "n2") }), false));
        var resolve = Assert.Single(g.Log.Skip(mark).OfType<ResolveRun>());
        Assert.True(resolve.Gone);
        var units = Assert.Single(g.State.Pending).Units;
        Assert.True(units.Single(u => u.PieceId == "n2") is { Applied: true, Removed: true });
        Assert.False(units.Single(u => u.PieceId == "n1").Removed);
    }

    [Fact]
    public void Batch_title_counts_removed()
    {
        var (s, c) = Open(Difficulty.SemiAuto, new[] { Merge() }, phases: NorthStart());
        c.Start();
        var run = Assert.Single(s.Game!.State.Pending);
        var b = new BatchModel(s, c, run.Id);
        Assert.Equal("Merge — 0 of 2 applied", b.Title);
        Assert.True(c.ApplyFormula(null, "n1", Run(s, "n1", new Dictionary<string, string> { ["T"] = Label(s, "n2") }), false));
        Assert.Equal("Merge — 0 of 2 applied, 1 removed", b.Title);
    }

    [Fact]
    public void FullyAuto_summary_counts_removed()
    {
        // Test seam (as FullyAuto_unit_removed_earlier_skipped): the first unit's removal is redirected to the second unit.
        var f = F("Scuttle", "1", Array.Empty<VariableBinding>(), UnitWrite(OutputOp.Set), Remove(OutputTarget.Unit));
        var (s, c) = Open(Difficulty.FullyAuto, new[] { f }, phases: NorthStart(), prepare: s => (cmd, st) =>
            cmd is RemovePiece rp ? rp with { PieceId = rp.PieceId == "n1" ? "n2" : "n1" } : CommandLog.Prepare(cmd, st, s));
        c.Start();
        Assert.Contains(s.Game!.State.Log, l => l.Text == "Scuttle: 2 units, 1 applied, 1 removed");
    }
}
