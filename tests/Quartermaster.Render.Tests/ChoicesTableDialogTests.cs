using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B4 choices from a table in the dialogs (invented Riverland data): "Barge cargo" offers the rows of table Cargo whose minimum size is
/// at most the counter's Strength (n1 4: Grain, Timber; n2 3: Grain; s2 5: Grain, Timber).
/// </summary>
[Collection(AppDataRootEnv.Name)]
public class ChoicesTableDialogTests : OtherPiecesTestBase
{
    private const string SizeFilter = "lookup(Cargo, row(), \"Min size\") <= Strength";

    private static FormulaDef Cargo(string filter = SizeFilter) => F("Barge cargo", "C", new[]
    {
        new VariableBinding { Variable = "S", Kind = BindingKind.Unit, Property = "str" },
        new VariableBinding { Variable = "C", Kind = BindingKind.Input, ChoicesTable = "t-cargo", ChoicesFilter = filter },
    });

    private (ModuleSession S, PhaseController C) Make(Difficulty d = Difficulty.SemiAuto, IEnumerable<PhaseDef>? phases = null,
        string filter = SizeFilter, Visibility strength = Visibility.Public, string iron = "6")
    {
        var (s, c) = Open(d, new[] { Cargo(filter) }, strength: strength, phases: phases);
        s.UpdateRules(p => p with
        {
            Tables = new()
            {
                new TableDef
                {
                    Id = "t-cargo", Name = "Cargo", Kind = TableKind.TwoD, RowKeys = new() { "Grain", "Timber", "Iron" },
                    ColumnKeys = new() { "Min size" }, Cells = new() { new() { "1" }, new() { "4" }, new() { iron } },
                },
            },
        });
        return (s, c);
    }

    [Fact]
    public void Dialog_dropdown_lists_piece_choices()
    {
        var (s, _) = Make();
        IReadOnlyList<string>? Choices(string id) =>
            Dialog(s, id).Evaluate(s.Game!.State).Boxes.Single(b => b.Variable == "C").Choices;
        Assert.Equal(new[] { "Grain", "Timber" }, Choices("n1"));
        Assert.Equal(new[] { "Grain" }, Choices("n2"));
        Assert.Equal(new[] { "Grain", "Timber", "Iron" }, Choices("s1"));
    }

    [Fact]
    public void Batch_choices_per_row_piece()
    {
        var (s, c) = Make(phases: NorthStart());
        c.Start();
        var run = Assert.Single(s.Game!.State.Pending);
        var b = new BatchModel(s, c, run.Id);
        int Row(string id) => run.Units.ToList().FindIndex(u => u.PieceId == id);
        Assert.True(b.IsChoiceColumn("C"));
        Assert.Equal(new[] { "Grain", "Timber" }, b.ChoicesFor("C", Row("n1")));
        Assert.Equal(new[] { "Grain" }, b.ChoicesFor("C", Row("n2")));
        Assert.Equal(new[] { "Grain", "Timber", "Iron" }, b.ChoicesFor("C"));   // no row: every key
    }

    [Fact]
    public void Hot_seat_dropdown_never_reflects_hidden_values()
    {
        // The filter reads the Southern Compact's private Strength (s1 7 + s2 5): on the full state Iron (10) is offered while the total is
        // 12 and not once s1 drops to 0. The Northern League's seat must list the same choices either way.
        var (s, c) = Make(phases: NorthStart(), filter: "lookup(Cargo, row(), \"Min size\") <= sum(Strength, \"startsWith(name(), \\\"South\\\")\")",
            strength: Visibility.Private, iron: "10");
        c.Start();
        var run = Assert.Single(s.Game!.State.Pending);
        var row = run.Units.ToList().FindIndex(u => u.PieceId == "n1");
        s.SetViewSides(North);
        IReadOnlyList<string>? DialogChoices() => Dialog(s, "n1").Evaluate(s.Game!.State).Boxes.Single(b => b.Variable == "C").Choices;
        IReadOnlyList<string>? GridChoices() => new BatchModel(s, c, run.Id).ChoicesFor("C", row);
        var dialog = DialogChoices();
        var grid = GridChoices();
        s.Game.Execute(new LedgerDelta(LedgerKey.Piece("s1"), "str", 7, 0, "test", null, null, null, null));
        Assert.Equal(dialog, DialogChoices());
        Assert.Equal(grid, GridChoices());
        Assert.Equal(dialog, grid);
    }

    [Fact]
    public void Guest_choices_refusal_neutral()
    {
        var (s, c) = Make(Difficulty.FullyAuto);   // no checked boxes: only the choice decides
        FormulaVerdict v;
        using (c.ActingFor(South, "blue-guest"))
            v = FormulaIntentRunner.Run(s, c, South, new FormulaIntent("f", "s2", null, new Dictionary<string, string> { ["C"] = "Iron" }, false),
                guest: true, who: "blue-guest", pins: new FormulaPins());
        Assert.False(v.Applied);
        Assert.Equal("Not one of the choices for this piece", v.Message);
        Assert.DoesNotContain("Grain", v.Message);

        using (c.ActingFor(South, "blue-guest"))
            v = FormulaIntentRunner.Run(s, c, South, new FormulaIntent("f", "s2", null, new Dictionary<string, string> { ["C"] = "Timber" }, false),
                guest: true, who: "blue-guest", pins: new FormulaPins());
        Assert.True(v.Applied, v.Message);
    }
}
