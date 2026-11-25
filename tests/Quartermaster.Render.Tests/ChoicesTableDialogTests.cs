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
    private static FormulaDef Cargo() => F("Barge cargo", "C", new[]
    {
        new VariableBinding { Variable = "S", Kind = BindingKind.Unit, Property = "str" },
        new VariableBinding { Variable = "C", Kind = BindingKind.Input, ChoicesTable = "t-cargo", ChoicesFilter = "lookup(Cargo, row(), \"Min size\") <= Strength" },
    });

    private (ModuleSession S, PhaseController C) Make(Difficulty d = Difficulty.SemiAuto, IEnumerable<PhaseDef>? phases = null)
    {
        var (s, c) = Open(d, new[] { Cargo() }, phases: phases);
        s.UpdateRules(p => p with
        {
            Tables = new()
            {
                new TableDef
                {
                    Id = "t-cargo", Name = "Cargo", Kind = TableKind.TwoD, RowKeys = new() { "Grain", "Timber", "Iron" },
                    ColumnKeys = new() { "Min size" }, Cells = new() { new() { "1" }, new() { "4" }, new() { "6" } },
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
