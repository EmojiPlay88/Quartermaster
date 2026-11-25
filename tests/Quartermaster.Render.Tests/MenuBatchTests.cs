using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B4 menu batch (invented Riverland data): rows over a selection, a stack or repeated runs of one counter, previewed as a chain on a
/// cloned state and applied in order as one undo step, all or nothing.
/// </summary>
[Collection(AppDataRootEnv.Name)]
public class MenuBatchTests : OtherPiecesTestBase
{
    private static VariableBinding Unit(string v = "S") => new() { Variable = v, Kind = BindingKind.Unit, Property = "str" };
    private static VariableBinding Input(string v = "N") => new() { Variable = v, Kind = BindingKind.Input };

    private static int Col(IBatchRows b, string v) => b.Variables.ToList().IndexOf(v);

    private static List<RollDice> RollsSince(ModuleSession s, int mark) => s.Game!.Log.Skip(mark).OfType<RollDice>().ToList();

    [Fact]
    public void Menu_batch_chain_and_one_undo()
    {
        // "Shore up": adds the counter's own Strength to the picked counter's.
        var f = F("Shore up", "S", new[] { Unit(), Pick() }, PieceWrite(OutputOp.Add));
        var (s, c) = Open(Difficulty.FullyAuto, new[] { f });
        var g = s.Game!;
        var b = new MenuBatchModel(s, c, "f", new[] { "n1", "n2", "s1" });
        Assert.Equal("Shore up — 3 runs", b.Title);
        var t = Col(b, "T");
        b.SetCell(0, "T", Label(s, "n2"));   // n2: 3 + 4 = 7
        b.SetCell(1, "T", Label(s, "s1"));   // s1: 7 + 7 = 14 (row 2 sees row 1)
        b.SetCell(2, "T", Label(s, "n1"));   // n1: 4 + 14 = 18 (row 3 sees row 2)
        Assert.Equal("4", b.Row(0).Result);
        Assert.Equal("7", b.Row(1).Result);
        Assert.Equal("14", b.Row(2).Result);
        Assert.True(t >= 0);
        Assert.Equal(4, Str(s, "n1"));   // a preview changes nothing

        var actions = g.Actions.Count;
        Assert.Equal(3, b.ApplyAll());
        Assert.Equal("Applied 3 run(s) as one step.", b.Status);
        Assert.Equal(actions + 1, g.Actions.Count);
        Assert.Equal("Shore up on 3 units", g.UndoLabel);
        Assert.Equal(18, Str(s, "n1"));
        Assert.Equal(7, Str(s, "n2"));
        Assert.Equal(14, Str(s, "s1"));
        Assert.Equal(3, g.State.Log.Count(l => l.AppliedRun == "f"));   // each row its own run

        g.Undo();
        Assert.Equal(4, Str(s, "n1"));
        Assert.Equal(3, Str(s, "n2"));
        Assert.Equal(7, Str(s, "s1"));
    }

    [Fact]
    public void All_rows_fills_blank_cells_only()
    {
        var f = F("Muster", "N", new[] { Own(), Input() }, UnitWrite(OutputOp.Set));
        var (s, c) = Open(Difficulty.FullyAuto, new[] { f });
        var b = new MenuBatchModel(s, c, "f", new[] { "n1", "n2" });
        var n = Col(b, "N");
        Assert.True(b.IsAllRowsColumn("N"));
        Assert.False(b.IsAllRowsColumn("U"));
        Assert.Equal(FormulaWindowRules.AllRowsText, b.AllRows.Label);
        b.SetCell(1, "N", "5");
        b.SetAllRowsCell("N", "2");
        Assert.Equal("2", b.AllRows.Cells[n]);
        Assert.Equal("2", b.Row(0).Cells[n]);
        Assert.Equal("5", b.Row(1).Cells[n]);
        b.SetCell(1, "N", "");
        Assert.Equal("2", b.Row(1).Cells[n]);
        Assert.Equal(2, b.ApplyAll());
        Assert.Equal(2, Str(s, "n1"));
        Assert.Equal(2, Str(s, "n2"));
    }

    [Fact]
    public void Add_row_same_counter_own_dice()
    {
        var (s, c) = Open(Difficulty.FullyAuto, new[] { GroupRollRunTests.Ford(group: false) });
        GroupRollRunTests.Counting(s);
        var b = new MenuBatchModel(s, c, "f", new[] { "n1" });
        Assert.True(b.AddRow(0));
        Assert.Equal(2, b.RowCount);
        Assert.Equal("n1", b.Row(1).PieceId);
        Assert.Equal("Ford crossing — 2 runs", b.Title);
        var d = Col(b, "D");
        Assert.NotEqual(b.Row(0).Cells[d], b.Row(1).Cells[d]);
        var mark = s.Game!.Log.Count;
        Assert.Equal(2, b.ApplyAll());
        Assert.Equal(2, RollsSince(s, mark).Count);
        Assert.Equal("Ford crossing: 2 runs", s.Game.UndoLabel);
        Assert.Equal(RollsSince(s, mark)[1].Results.Sum(), Str(s, "n1"));
    }

    [Fact]
    public void Add_row_limit_100()
    {
        var (s, c) = Open(Difficulty.FullyAuto, new[] { GroupRollRunTests.Ford(group: false) });
        var b = new MenuBatchModel(s, c, "f", new[] { "n1", "n2" });
        Assert.Equal(100, MenuBatchModel.MaxBatchRows);
        for (var i = 2; i < 100; i++) Assert.True(b.AddRow(null));
        Assert.Equal(100, b.RowCount);
        Assert.Equal("n2", b.Row(99).PieceId);   // no selection: the last row's counter
        Assert.False(b.AddRow(0));
        Assert.Equal("A batch holds at most 100 rows.", b.Status);
        Assert.Equal(100, b.RowCount);
        while (b.RowCount > 1) Assert.True(b.RemoveRow(b.RowCount - 1));
        Assert.False(b.RemoveRow(0));   // the last row stays
        Assert.Equal(1, b.RowCount);
        // Rows over the limit are never taken in.
        Assert.Equal(100, new MenuBatchModel(s, c, "f", Enumerable.Repeat("n1", 150).ToList()).RowCount);
    }

    [Fact]
    public void Group_roll_batch_one_roll_logged_once()
    {
        var (s, c) = Open(Difficulty.FullyAuto, new[] { GroupRollRunTests.Ford() });
        GroupRollRunTests.Counting(s);
        var b = new MenuBatchModel(s, c, "f", new[] { "n1", "n2", "s2" });
        var d = Col(b, "D");
        Assert.Equal(b.Row(0).Cells[d], b.Row(1).Cells[d]);
        Assert.Equal(b.Row(0).Cells[d], b.Row(2).Cells[d]);   // the menu batch is one group, wherever its counters are
        var box = b.ModelFor(2).View(b.ModelFor(2).Run(s.Game!.State)).Boxes.Single(x => x.Variable == "D");
        Assert.Equal(FormulaDialogModel.GroupRollSuffix, box.CaptionSuffix);
        var mark = s.Game.Log.Count;
        Assert.Equal(3, b.ApplyAll());
        Assert.Single(RollsSince(s, mark));
        Assert.Equal(Str(s, "n1"), Str(s, "n2"));
        Assert.Equal(Str(s, "n1"), Str(s, "s2"));
    }

    [Fact]
    public void No_group_roll_each_row_rolls()
    {
        var (s, c) = Open(Difficulty.FullyAuto, new[] { GroupRollRunTests.Ford(group: false) });
        GroupRollRunTests.Counting(s);
        var b = new MenuBatchModel(s, c, "f", new[] { "n1", "n2", "s2" });
        var d = Col(b, "D");
        Assert.Equal(3, new[] { 0, 1, 2 }.Select(i => b.Row(i).Cells[d]).Distinct().Count());
        var mark = s.Game!.Log.Count;
        Assert.Equal(3, b.ApplyAll());
        Assert.Equal(3, RollsSince(s, mark).Count);
    }

    [Fact]
    public void Row_removed_by_earlier_row_skipped()
    {
        var f = F("Scuttle", "1", new[] { Own(), Pick() }, Remove(OutputTarget.Piece));
        var (s, c) = Open(Difficulty.FullyAuto, new[] { f });
        var b = new MenuBatchModel(s, c, "f", new[] { "n1", "n2" });
        b.SetCell(0, "T", Label(s, "n2"));
        var removed = b.Row(1);
        Assert.Equal(FormulaWindowRules.RemovedByEarlierRowText, removed.Exceptions);
        Assert.Equal("removed by an earlier row", removed.Exceptions);
        Assert.False(removed.CanApply);
        Assert.True(s.Game!.State.Pieces.ContainsKey("n2"));   // only the preview removed it
        Assert.Equal(1, b.ApplyAll());   // the removed row does not block
        Assert.Equal("Applied 1 run(s) as one step.", b.Status);
        Assert.False(s.Game.State.Pieces.ContainsKey("n2"));
        Assert.Equal(1, s.Game.State.Log.Count(l => l.AppliedRun == "f"));
    }

    [Fact]
    public void Placed_piece_never_a_row()
    {
        var f = F("Wreck it", "1", new[] { Own() }, Place());
        var (s, c) = Open(Difficulty.FullyAuto, new[] { f });
        var before = s.Game!.State.Pieces.Keys.ToHashSet();
        var b = new MenuBatchModel(s, c, "f", new[] { "n1", "n2" });
        Assert.Equal(2, b.ApplyAll());
        var placed = s.Game.State.Pieces.Keys.Where(k => !before.Contains(k)).ToList();
        Assert.Equal(2, placed.Count);
        Assert.Equal(2, b.RowCount);
        Assert.Equal(new[] { "n1", "n2" }, Enumerable.Range(0, b.RowCount).Select(i => b.Row(i).PieceId));
    }

    [Fact]
    public void Runs_counts_each_row()
    {
        var counted = F("Count", "runs(\"f\")", new[] { Own() });
        var capped = F("Capped", "1", new[] { Own() }) with { Id = "g", Precondition = "runs(\"g\") < 2" };
        var (s, c) = Open(Difficulty.FullyAuto, new[] { counted, capped });
        var b = new MenuBatchModel(s, c, "f", new[] { "n1" });
        b.AddRow(0);
        b.AddRow(1);
        Assert.Equal(new[] { "0", "1", "2" }, Enumerable.Range(0, 3).Select(i => b.Row(i).Result));
        Assert.Equal(3, b.ApplyAll());
        Assert.Equal(3, s.Game!.State.Log.Count(l => l.AppliedRun == "f"));

        var log = s.Game.State.Log.Count;
        var actions = s.Game.Actions.Count;
        var cap = new MenuBatchModel(s, c, "g", new[] { "n1", "n1", "n1" });
        Assert.False(cap.Row(2).CanApply);
        Assert.Equal(0, cap.ApplyAll());
        Assert.Equal("Nothing applied: row 3 cannot be applied yet.", cap.Status);
        Assert.Equal(log, s.Game.State.Log.Count);
        Assert.Equal(actions, s.Game.Actions.Count);
    }

    [Fact]
    public void Apply_all_or_nothing()
    {
        var f = F("Muster", "N", new[] { Own(), Input() }, UnitWrite(OutputOp.Set));
        var (s, c) = Open(Difficulty.FullyAuto, new[] { f });
        var b = new MenuBatchModel(s, c, "f", new[] { "n1", "n2" });
        b.SetCell(0, "N", "1");
        var actions = s.Game!.Actions.Count;
        Assert.Equal(0, b.ApplyAll());
        Assert.Equal("Nothing applied: row 2 cannot be applied yet.", b.Status);
        Assert.Equal(4, Str(s, "n1"));
        Assert.Equal(actions, s.Game.Actions.Count);
        b.SetCell(1, "N", "6");
        Assert.Equal(2, b.ApplyAll());
        Assert.Equal(1, Str(s, "n1"));
        Assert.Equal(6, Str(s, "n2"));
    }

    [Fact]
    public void Action_label_units_vs_runs()
    {
        Assert.Equal("Ford crossing on 2 units", MenuBatchModel.ActionLabelFor("Ford crossing", new[] { "n1", "n2" }));
        Assert.Equal("Ford crossing on 1 unit", MenuBatchModel.ActionLabelFor("Ford crossing", new[] { "n1" }));
        Assert.Equal("Ford crossing: 3 runs", MenuBatchModel.ActionLabelFor("Ford crossing", new[] { "n1", "n2", "n1" }));
        var (s, c) = Open(Difficulty.FullyAuto, new[] { GroupRollRunTests.Ford(group: false) });
        var b = new MenuBatchModel(s, c, "f", new[] { "n1", "n2" });
        Assert.Equal("Ford crossing on 2 units", b.ActionLabel);
        b.AddRow(1);
        Assert.Equal("Ford crossing: 3 runs", b.ActionLabel);
    }

    [Fact]
    public void Hot_seat_rows_only_actable_and_remasked()
    {
        var f = F("Muster", "S + 1", new[] { Unit() }, UnitWrite(OutputOp.Set));
        var (s, c) = Open(Difficulty.FullyAuto, new[] { f }, strength: Visibility.Private, players: new[] { Alice, Bob });
        s.SetViewSides(North);
        var b = new MenuBatchModel(s, c, "f", new[] { "n1", "s1", "n2" });
        Assert.Equal(2, b.RowCount);   // only counters this viewer may act on
        Assert.Equal(new[] { "n1", "n2" }, new[] { b.Row(0).PieceId, b.Row(1).PieceId });
        Assert.Equal("5", b.Row(0).Result);
        Assert.Empty(b.ModelFor(0).Hidden);

        s.SetViewSides(South);
        var r = b.Row(0);
        Assert.False(r.CanApply);
        Assert.Equal(ModuleSession.HiddenText, r.Result);
        Assert.All(r.Cells, x => Assert.Equal(ModuleSession.HiddenText, x));
        Assert.Contains("S", b.ModelFor(0).Hidden);   // masked again for the new viewer
        Assert.Equal(0, b.ApplyAll());
        Assert.Equal(4, Str(s, "n1"));

        s.SetViewSides(North);
        Assert.Equal("5", b.Row(0).Result);
        Assert.Empty(b.ModelFor(0).Hidden);
    }

    [Fact]
    public void BatchModel_implements_IBatchRows_unchanged()
    {
        Assert.True(typeof(IBatchRows).IsAssignableFrom(typeof(BatchModel)));
        Assert.True(typeof(IBatchRows).IsAssignableFrom(typeof(MenuBatchModel)));
        var t = typeof(BatchModel);
        foreach (var name in new[] { "Title", "Variables", "RowCount", "HostCalculates" })
            Assert.NotNull(t.GetProperty(name));
        foreach (var name in new[] { "Row", "ModelFor", "OutputsOf", "Invalidate", "SetCell", "CalculateRow", "CalculateAll", "ApplyRow",
                     "ApplyAll", "SendRowsAsync", "OpenRows", "IsChoiceColumn" })
            Assert.NotNull(t.GetMethod(name));
        Assert.NotNull(t.GetMethod("ChoicesFor", new[] { typeof(string) }));
        Assert.NotNull(t.GetMethod("ChoicesFor", new[] { typeof(string), typeof(int) }));
        Assert.Equal(typeof(int), t.GetMethod("ApplyAll")!.ReturnType);
    }
}
