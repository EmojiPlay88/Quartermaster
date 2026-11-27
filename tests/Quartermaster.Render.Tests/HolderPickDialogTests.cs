using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B9 #13b fixture: the Task 2 fixture (<see cref="HolderOwnerTestBase"/>) plus Mill Dump (Southern Compact) and North Depot (Northern
/// League), and Draw stores: a Holder binding Dump over Ford Dump, Mill Dump and North Depot (Stores), an Input Need, and an output that
/// subtracts Need from the chosen holder's Stores. Unload is the same with a unit (so a menu batch lists it). The Supply phase (Northern
/// League) queues Draw stores once. Invented data.
/// </summary>
public abstract class HolderPickTestBase : HolderOwnerTestBase
{
    protected const string Mill = "milldump";
    protected const string Depot = "h-ndepot";
    protected const double MillStores = 20;
    protected const double DepotStores = 30;

    protected static VariableBinding DumpBinding => new()
    {
        Variable = "Dump", Kind = BindingKind.Holder, Property = "p-stores", HolderChoices = new() { Ford, Mill, Depot },
    };

    private static readonly VariableBinding Need = new() { Variable = "Need", Kind = BindingKind.Input };

    private static FormulaOutput Subtract => new()
        { Target = OutputTarget.Holder, HolderVariable = "Dump", Property = "p-stores", Op = OutputOp.Subtract, Expr = "Need" };

    protected static RulesPack PickPack(string sha)
    {
        var p = B9Pack(sha);
        return p with
        {
            Holders = p.Holders.Concat(new[]
            {
                new HolderDef { Id = Mill, Name = "Mill Dump", SideId = "south" },
                new HolderDef { Id = Depot, Name = "North Depot", SideId = "north" },
            }).ToList(),
            Formulas = p.Formulas.Concat(new[]
            {
                new FormulaDef { Id = "f-draw", Name = "Draw stores", Text = "Dump", Bindings = new() { DumpBinding, Need }, Outputs = new() { Subtract } },
                new FormulaDef
                {
                    Id = "f-unload", Name = "Unload", Text = "Dump + W",
                    Bindings = new() { new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" }, DumpBinding, Need },
                    Outputs = new() { Subtract },
                },
            }).ToList(),
            Phases = p.Phases.Select(ph => ph.Id == "ph-supply"
                ? ph with { OnStart = new() { new StartEntry { FormulaId = "f-draw", ForEach = ForEachMode.Once } } } : ph).ToList(),
        };
    }

    /// <summary>The alliance session with the pick pack and every dump's Stores set.</summary>
    protected ModuleSession PickSession(PlayerDef[]? players = null)
    {
        var s = B9Session(players);
        s.UpdateRules(_ => PickPack(s.Archive.Sha256));
        s.Game!.Execute(new LedgerDelta(LedgerKey.Holder(Mill), "p-stores", 0, MillStores, "manual", null, null, null, null));
        s.Game.Execute(new LedgerDelta(LedgerKey.Holder(Depot), "p-stores", 0, DepotStores, "manual", null, null, null, null));
        return s;
    }

    protected static double? StoresOf(GameState st, string holder) => Ledger.Get(st, LedgerKey.Holder(holder), "p-stores");

    protected static FormulaDialogModel Dialog(ModuleSession s, string formulaId = "f-draw", string? pieceId = null)
    {
        var m = new FormulaDialogModel(s.Runner!, s.Rules!.ById<FormulaDef>(formulaId)!, pieceId) { Checked = false };
        FormulaMasking.Configure(m, s);
        return m;
    }

    protected static BoxView Box(DialogView dv, string variable) => dv.Boxes.Single(b => b.Variable == variable);
}

/// <summary>B9 #13b in the formula dialog, the batch grids and the missing-input dialog (hot-seat).</summary>
public class HolderPickDialogTests : HolderPickTestBase
{
    [Fact]
    public void Dialog_dropdown_lists_viewer_candidates()
    {
        var s = PickSession();
        s.SetViewSides(North);
        var m = Dialog(s);
        var dv = m.Evaluate(s.Game!.State);
        var box = Box(dv, "Dump");
        Assert.Equal(new[] { "Ford Dump", "North Depot" }, box.Choices!.ToArray());
        Assert.Equal(BoxBackground.Typed, box.Background);
        Assert.True(box.Enabled);
        Assert.Equal("Waiting for input", box.Tooltip);
        Assert.Contains("Dump", dv.Status);
        m.SetBoxText("Dump", "North Depot");
        m.SetBoxText("Need", "4");
        dv = m.Evaluate(s.Game.State);
        Assert.True(dv.CanApply, dv.Status);
        Assert.Equal("30", dv.ResultText);
        Assert.Contains("Stores of North Depot 30 − 4 = 26", dv.WorkingText);
        Assert.Equal("North Depot", m.Intent(null, calculate: false).Typed["Dump"]);

        // The other seat: its own dumps only; the north's pick is not carried into the south's box.
        s.SetViewSides(South);
        Configure(m, s);
        dv = m.Evaluate(s.Game.State);
        Assert.Equal(new[] { "Mill Dump" }, Box(dv, "Dump").Choices!.ToArray());
        Assert.Equal("", Box(dv, "Dump").Text);
        // After the capture the south may draw from Ford Dump too.
        s.Game.Execute(Capture(s.Rules!));
        dv = Dialog(s).Evaluate(s.Game.State);
        Assert.Equal(new[] { "Ford Dump", "Mill Dump" }, Box(dv, "Dump").Choices!.ToArray());
    }

    private static void Configure(FormulaDialogModel m, ModuleSession s) => FormulaMasking.Configure(m, s);

    [Fact]
    public void Hotseat_label_of_other_side_rejected()
    {
        var s = PickSession();
        s.SetViewSides(North);
        var m = Dialog(s);
        m.SetBoxText("Dump", "Mill Dump");
        m.SetBoxText("Need", "4");
        var dv = m.Evaluate(s.Game!.State);
        Assert.False(dv.CanApply);
        Assert.Equal("Choose one of the listed holders", Box(dv, "Dump").Tooltip);
        Assert.Equal("Waiting for: Dump", dv.Status);
        Assert.Equal("", dv.ResultText);
        Assert.Equal(MillStores, StoresOf(s.Game.State, Mill));
    }

    [Fact]
    public void Batch_grid_holder_column_is_choice_column()
    {
        var s = PickSession();
        var c = Controller(s);
        s.SetViewSides(North);
        To(s, c, "ph-supply");
        var run = Assert.Single(s.Game!.State.Pending, r => r.FormulaId == "f-draw");
        var b = new BatchModel(s, c, run.Id);
        Assert.True(b.IsChoiceColumn("Dump"));
        Assert.False(b.IsChoiceColumn("Need"));
        Assert.Equal(new[] { "Ford Dump", "North Depot" }, b.ChoicesFor("Dump")!.ToArray());
        Assert.Equal(new[] { "Ford Dump", "North Depot" }, b.ChoicesFor("Dump", 0)!.ToArray());
        b.SetCell(0, "Dump", "Ford Dump");
        b.SetCell(0, "Need", "5");
        var row = b.Row(0);
        Assert.True(row.CanApply, row.Exceptions);
        Assert.Equal(1, b.ApplyAll());
        Assert.Equal(FordStores - 5, Stores(s.Game.State));
        Assert.Equal(DepotStores, StoresOf(s.Game.State, Depot));
    }

    [Fact]
    public void Menu_batch_all_rows_fills_holder_pick()
    {
        var s = PickSession();
        var c = Controller(s);
        s.SetViewSides(North);
        var b = new MenuBatchModel(s, c, "f-unload", new[] { "r1", "r1" });
        Assert.Equal(2, b.RowCount);
        Assert.True(b.IsAllRowsColumn("Dump"));
        Assert.True(b.IsChoiceColumn("Dump"));
        Assert.Equal(new[] { "Ford Dump", "North Depot" }, b.ChoicesFor("Dump", -1)!.ToArray());
        Assert.Equal(new[] { "Ford Dump", "North Depot" }, b.ChoicesFor("Dump", 0)!.ToArray());
        b.SetAllRowsCell("Dump", "North Depot");
        b.SetAllRowsCell("Need", "2");
        Assert.Equal("North Depot", b.AllRows.Cells[b.Variables.ToList().IndexOf("Dump")]);
        Assert.True(b.Row(0).CanApply, b.Row(0).Exceptions);
        Assert.True(b.Row(1).CanApply, b.Row(1).Exceptions);
        Assert.Equal(2, b.ApplyAll());
        Assert.Equal(DepotStores - 4, StoresOf(s.Game!.State, Depot));
        Assert.Equal(FordStores, Stores(s.Game.State));
    }

    [Fact]
    public void Missing_inputs_asks_holder_pick()
    {
        var s = PickSession();
        var c = Controller(s);
        s.SetViewSides(North);
        To(s, c, "ph-supply");
        var run = Assert.Single(s.Game!.State.Pending, r => r.FormulaId == "f-draw");
        Assert.True(run.NeedsInput);
        var missing = MissingInputs.For(s, run);
        var dump = Assert.Single(missing, x => x.Variable == "Dump");
        Assert.Equal(new[] { "Ford Dump", "North Depot" }, dump.Choices!.ToArray());
        Assert.Contains(missing, x => x.Variable == "Need");
        Assert.False(MissingInputs.NeedsPick(s, run));   // asked once for the run, not per unit in the dialog
        var (applied, failed) = MissingInputs.ApplyAll(s, c, run, new Dictionary<string, string> { ["Dump"] = "Ford Dump", ["Need"] = "6" });
        Assert.Equal(1, applied);
        Assert.Empty(failed);
        Assert.Equal(FordStores - 6, Stores(s.Game.State));
    }

    /// <summary>North Depot renamed "Ford Dump": the north lists Ford Dump as "Ford Dump" and the depot as "Ford Dump #2".</summary>
    private static RulesPack SameNames(RulesPack p) =>
        p with { Holders = p.Holders.Select(h => h.Id == Depot ? h with { Name = "Ford Dump" } : h).ToList() };

    [Fact]
    public void Hotseat_pick_follows_holder_when_labels_shift()
    {
        var s = PickSession();
        s.UpdateRules(SameNames);
        s.SetViewSides(North);
        var m = Dialog(s);
        var dv = m.Evaluate(s.Game!.State);
        Assert.Equal(new[] { "Ford Dump", "Ford Dump #2" }, Box(dv, "Dump").Choices!.ToArray());
        m.SetBoxText("Dump", "Ford Dump #2");   // the depot
        m.SetBoxText("Need", "1");
        Assert.Equal(Depot, m.Intent(null, calculate: false).Picks!["Dump"]);
        s.Game.Execute(Capture(s.Rules!));   // Ford Dump leaves the north's list: the depot is now "Ford Dump"
        dv = m.Evaluate(s.Game.State);
        Assert.Equal("Ford Dump", Box(dv, "Dump").Text);   // the box follows the depot
        Assert.True(dv.CanApply, dv.Status);
        Assert.Contains("Stores of Ford Dump 30", dv.WorkingText);
        Assert.Equal(LedgerKey.Holder(Depot), Assert.Single(dv.Trace.Outputs).Key);

        // A pick whose holder leaves the list is cleared, never moved to the other "Ford Dump".
        var s2 = PickSession();
        s2.UpdateRules(SameNames);
        s2.SetViewSides(North);
        var m2 = Dialog(s2);
        m2.Evaluate(s2.Game!.State);
        m2.SetBoxText("Dump", "Ford Dump");   // Ford Dump itself
        m2.SetBoxText("Need", "1");
        s2.Game.Execute(Capture(s2.Rules!));
        dv = m2.Evaluate(s2.Game.State);
        Assert.Equal("", Box(dv, "Dump").Text);
        Assert.False(dv.CanApply);
        Assert.Equal("Waiting for input", Box(dv, "Dump").Tooltip);
    }

    [Fact]
    public void Picker_with_stale_fixed_holder_not_masked()
    {
        var s = PickSession();
        // A stale fixed holder of the south next to the choices (the validator warns): the north's own pick is not masked for it.
        s.UpdateRules(p => p with
        {
            Formulas = p.Formulas.Select(f => f.Id != "f-draw" ? f
                : f with { Bindings = f.Bindings.Select(b => b.Variable == "Dump" ? b with { HolderId = Mill } : b).ToList() }).ToList(),
        });
        s.SetViewSides(North);
        Assert.Empty(FormulaMasking.HiddenFor(s, s.Rules!.ById<FormulaDef>("f-draw")!, null));
    }
}
