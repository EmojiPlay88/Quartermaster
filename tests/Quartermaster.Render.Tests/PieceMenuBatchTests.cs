using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B4 counter menu: "Calculate on selection" (the clicked counter in a selection of two or more) and "Calculate on stack" list the formulas
/// that apply to the clicked counter and to at least two counters the viewer may act on. Invented Riverland data.
/// </summary>
[Collection(AppDataRootEnv.Name)]
public class PieceMenuBatchTests : OtherPiecesTestBase
{
    private const string Map = "Main Map";

    /// <summary>"Ford crossing" for every counter; "Barge only" for North Barge alone.</summary>
    private static FormulaDef[] Formulas() => new[]
    {
        GroupRollRunTests.Ford(group: false),
        F("Barge only", "1", new[] { Own() }) with { Id = "g", AppliesTo = "name() == \"North Barge\"" },
    };

    private static void Stack(ModuleSession s, string onto, params string[] ids)
    {
        var g = s.Game!;
        var stack = g.State.Pieces[onto].StackId!;
        foreach (var id in ids) g.Execute(new MovePieces(new[] { id }, Map, g.State.Stacks[stack].Pos, stack));
    }

    private static (List<(string Formula, IReadOnlyList<string> Pieces)> Opened, PieceMenuHooks Hooks) Hooks()
    {
        var opened = new List<(string, IReadOnlyList<string>)>();
        return (opened, new PieceMenuHooks { ShowFormula = (_, _) => { }, ShowFormulaBatch = (f, ids) => opened.Add((f, ids)) });
    }

    [Fact]
    public void Selection_submenu_lists_formulas_for_two_plus()
    {
        var (s, _) = Open(Difficulty.FullyAuto, Formulas());
        var (opened, hooks) = Hooks();
        // n2 clicked, selection n2 and n1 (game order n1, n2).
        var menu = PieceMenuModel.Build(s, Map, new[] { "n2", "n2", "n1" }, null, hooks);
        Assert.DoesNotContain(menu, e => e.Label == FormulaWindowRules.CalculateOnStackText);
        var sel = menu.Single(e => e.Label == "Calculate on selection");
        Assert.Equal(new[] { "Ford crossing" }, sel.Children.Select(c => c.Label));   // "Barge only" applies to one counter only
        var labels = menu.Select(e => e.Label).ToList();
        Assert.Equal(labels.IndexOf("Calculate") + 1, labels.IndexOf("Calculate on selection"));
        sel.Children[0].Run!();
        Assert.Equal("f", opened.Single().Formula);
        Assert.Equal(new[] { "n1", "n2" }, opened.Single().Pieces);

        // The clicked counter alone in its selection, or not in it: no selection submenu.
        Assert.DoesNotContain(PieceMenuModel.Build(s, Map, new[] { "n2", "n2" }, null, hooks), e => e.Label == "Calculate on selection");
        Assert.DoesNotContain(PieceMenuModel.Build(s, Map, new[] { "n2", "n1", "s1" }, null, hooks), e => e.Label == "Calculate on selection");
    }

    [Fact]
    public void Stack_submenu_when_not_in_selection()
    {
        var (s, _) = Open(Difficulty.FullyAuto, Formulas());
        Stack(s, "n2", "n1");   // bottom to top: n2, n1
        var (opened, hooks) = Hooks();
        var menu = PieceMenuModel.Build(s, Map, new[] { "n1", "s2" }, null, hooks);
        Assert.DoesNotContain(menu, e => e.Label == "Calculate on selection");
        var stack = menu.Single(e => e.Label == "Calculate on stack");
        Assert.Equal(new[] { "Ford crossing" }, stack.Children.Select(c => c.Label));
        stack.Children[0].Run!();
        Assert.Equal(new[] { "n2", "n1" }, opened.Single().Pieces);

        // A counter alone in its stack: nothing to batch.
        Assert.DoesNotContain(PieceMenuModel.Build(s, Map, new[] { "s2" }, null, hooks), e => e.Label == "Calculate on stack");
    }

    [Fact]
    public void Submenu_skips_other_side_counters()
    {
        var (s, _) = Open(Difficulty.FullyAuto, Formulas(), players: new[] { Alice, Bob });
        Stack(s, "n1", "s1", "n2");
        s.SetViewSides(North);
        var (opened, hooks) = Hooks();
        var stack = PieceMenuModel.Build(s, Map, new[] { "n1" }, null, hooks).Single(e => e.Label == "Calculate on stack");
        stack.Children.Single().Run!();
        Assert.Equal(new[] { "n1", "n2" }, opened.Single().Pieces);

        // A selection with one counter of the viewer's: no batch.
        Assert.DoesNotContain(PieceMenuModel.Build(s, Map, new[] { "n1", "n1", "s1" }, null, hooks), e => e.Label == "Calculate on selection");
        // South sees the same stack with one counter of its own.
        s.SetViewSides(South);
        Assert.DoesNotContain(PieceMenuModel.Build(s, Map, new[] { "s1" }, null, hooks), e => e.Label == "Calculate on stack");
    }

    [Fact]
    public void Submenu_absent_without_hook()
    {
        var (s, _) = Open(Difficulty.FullyAuto, Formulas());
        Stack(s, "n2", "n1");
        var hooks = new PieceMenuHooks { ShowFormula = (_, _) => { } };
        Assert.DoesNotContain(PieceMenuModel.Build(s, Map, new[] { "n1" }, null, hooks), e => e.Label is "Calculate on stack" or "Calculate on selection");
        Assert.DoesNotContain(PieceMenuModel.Build(s, Map, new[] { "n1", "n1", "n2" }, null, hooks), e => e.Label is "Calculate on stack" or "Calculate on selection");
        Assert.Contains(PieceMenuModel.Build(s, Map, new[] { "n1" }, null, hooks), e => e.Label == "Calculate");
    }

    [Fact]
    public void Phase_limited_greyed_with_reason()
    {
        var phases = new[] { new PhaseDef { Id = "ph-a", Name = "Muster" }, new PhaseDef { Id = "ph-b", Name = "Crossing" } };
        var ford = GroupRollRunTests.Ford(group: false) with { Phases = new() { "ph-b" } };
        var (s, c) = Open(Difficulty.FullyAuto, new[] { ford }, phases: phases);
        c.Start();   // Muster
        var (opened, hooks) = Hooks();
        var sel = PieceMenuModel.Build(s, Map, new[] { "n1", "n1", "n2" }, null, hooks).Single(e => e.Label == "Calculate on selection");
        var entry = sel.Children.Single();
        Assert.False(entry.Enabled);
        Assert.Equal("Only in: Crossing", entry.DisabledReason);
        Assert.Empty(opened);
    }
}
