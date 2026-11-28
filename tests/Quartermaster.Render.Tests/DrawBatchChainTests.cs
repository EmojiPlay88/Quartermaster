using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B9 #55, fix round 1: a batch's later rows never show anything that follows an earlier row's split before Apply (hot-seat menu batch,
/// the guest's batch preview verdict, the per-unit batch grid), and the logged working after Apply is not read by a third side. The
/// "remove the drawn barges" variant makes the weight left in the hex depend on which barges went.
/// </summary>
[Collection(AppDataRootEnv.Name)]
public class DrawBatchChainTests : DrawTestBase
{
    // Shots 2 on SC 1 Barge's hex. Faces 1, 1: SC 1 Barge twice (Boats left 1 + 2 = 3). Faces 6, 5: SC 4 Barge twice (Boats left 3 + 1 = 4).
    private static readonly int[] SplitA = { 1, 1 };
    private static readonly int[] SplitB = { 6, 5 };

    private string MenuLaterRow(int[] firstRow, out ModuleSession session)
    {
        var (s, c) = Open(Visibility.Public, remove: true);
        session = s;
        s.SetViewSides(North);
        var b = new MenuBatchModel(s, c, "f", new[] { "nl1", "nl1" });
        b.SetCell(0, "Shots", "2");
        b.SetCell(1, "Shots", "4");
        foreach (var face in firstRow) Next.Enqueue(face);
        b.CalculateAll();
        var first = b.Row(0);
        Assert.True(first.Result == "2", Json(first));   // the first row is the viewer's own run on the live state: k shown
        var later = b.Row(1);
        Assert.Equal(FormulaMasking.HiddenText, later.Result);
        Assert.False(NamesBarge(Json(later)), Json(later));
        var json = Json(later);
        Assert.Equal(2, b.ApplyAll());
        return json;
    }

    [Fact]
    public void Menu_batch_later_row_same_for_any_earlier_split()
    {
        var a = MenuLaterRow(SplitA, out var sa);
        Assert.False(sa.Game!.State.Pieces.ContainsKey("sc1"));   // the live apply used the first row's pins
        var b = MenuLaterRow(SplitB, out var sb);
        Assert.False(sb.Game!.State.Pieces.ContainsKey("sc4"));
        Assert.Equal(a, b);
    }

    private string GuestLaterRow(int[] firstRow)
    {
        var (host, c) = Open(Visibility.Public, remove: true);
        var x = new IntentExecutor(host, c);
        foreach (var face in firstRow) Next.Enqueue(face);
        var rows = new[] { "2", "4" }.Select(n => new FormulaBatchRow("nl1", new Dictionary<string, string> { ["Shots"] = n }, null)).ToList();
        var v = x.Execute("p1", North, new FormulaBatchIntent("f", rows, true, true)).BatchVerdict!;
        Assert.Equal(2, v.Rows.Count);
        Assert.Equal("2", v.Rows[0].Values!["Hits"]);
        Assert.False(v.Rows[1].Values?.ContainsKey("Hits") ?? false);
        Assert.Null(v.Rows[1].Rolls);
        var apply = x.Execute("p1", North, new FormulaBatchIntent("f", rows, false, false)).BatchVerdict!;
        Assert.True(apply.Applied, apply.Message);
        return Json(v.Rows[1]);
    }

    [Fact]
    public void Guest_batch_later_row_verdict_same_for_any_earlier_split()
    {
        Assert.Equal(GuestLaterRow(SplitA), GuestLaterRow(SplitB));
    }

    /// <summary>The per-unit grid evaluates every row on the live state (no chain), so a later row never sees an earlier row's split.</summary>
    private string PendingLaterRow(int[] firstRow)
    {
        var (s, c) = Open(Visibility.Public, remove: true, twoBatteries: true, phases: NorthGuns());
        s.SetViewSides(North);
        c.Start();
        var run = Assert.Single(s.Game!.State.Pending, r => r.FormulaId == "f");
        var b = new BatchModel(s, c, run.Id);
        Assert.Equal(2, b.RowCount);
        b.SetCell(0, "Shots", "2");
        b.SetCell(1, "Shots", "4");
        foreach (var face in firstRow) Next.Enqueue(face);
        Assert.Equal("2", b.Row(0).Result);
        var later = b.Row(1);
        Assert.False(NamesBarge(Json(later)), Json(later));
        return Json(later);
    }

    [Fact]
    public void Pending_batch_later_row_same_for_any_earlier_split()
    {
        Assert.Equal(PendingLaterRow(SplitA), PendingLaterRow(SplitB));
    }

    [Fact]
    public void Third_side_does_not_read_k_after_apply()
    {
        var (s, c) = Open(Visibility.Private);
        s.SetViewSides(North);
        var m = Dialog(s);
        m.SetBoxText("Shots", "3");
        var t = m.Evaluate(s.Game!.State).Trace;
        Assert.True(c.ApplyFormula(null, "nl1", t, false));
        var st = s.Game.State;
        var working = Assert.Single(st.Log, l => l.AppliedRun == "f");
        Assert.Contains("Hits = 3", working.Text);
        var hills = new HashSet<string> { "hills" };
        var third = PlayerView.For(st, s.Rules, s.Model, hills);
        Assert.DoesNotContain(third.Log, l => l.Text.Contains("Hits = 3", StringComparison.Ordinal));
        Assert.DoesNotContain(third.Log, l => l.Text.StartsWith("Shore battery: Hits:", StringComparison.Ordinal));
        // The acting side learns k (decision 6); the barges' side reads its own lines.
        Assert.Contains(PlayerView.For(st, s.Rules, s.Model, North).Log, l => l.Text.Contains("Hits = 3", StringComparison.Ordinal));
        Assert.Contains(PlayerView.For(st, s.Rules, s.Model, South).Log, l => l.Text == "Shore battery: Hits: 2 on SC 4 Barge");
    }

    [Fact]
    public void Per_counter_output_error_never_shown()
    {
        var (s, c) = Open(Visibility.Public);
        // Inside the output, Hits reads that counter's count: the expression fails only for a counter that gave one boat.
        s.UpdateRules(p => p with
        {
            Formulas = p.Formulas.Select(f => f with { Outputs = f.Outputs.Select(o => o with { Expr = "10 / (Hits - 1)" }).ToList() }).ToList(),
        });
        s.SetViewSides(North);
        var m = Dialog(s);
        m.SetBoxText("Shots", "3");   // the highest faces: SC 2 Barge gives 1, SC 4 Barge 2
        var dv = m.Evaluate(s.Game!.State);
        Assert.NotNull(dv.Trace.Error);   // the real run fails on SC 2 Barge's share
        Assert.DoesNotContain("Division", dv.Status, StringComparison.Ordinal);
        Assert.True(dv.CanApply, dv.Status);   // the shown run does not act on the drawn counters: Apply never follows the split
        Assert.Equal(FormulaMasking.ErrorHiddenText, FormulaDialogModel.ApplyRefusal(dv));
        var x = new IntentExecutor(s, c);
        var v = x.Execute("p1", North, new FormulaIntent("f", "nl1", null, new Dictionary<string, string> { ["Shots"] = "3" }, false)).Verdict!;
        Assert.False(v.Applied);
        Assert.Equal(FormulaMasking.ErrorHiddenText, v.Message);
    }

    /// <summary>One shot on hex 0101: face 1 takes SC 1 Barge (which has the mask and the layer), face 6 SC 4 Barge (which has neither).</summary>
    private (string View, bool CanApply, string Verdict) EffectView(string effect, int face, IReadOnlySet<string>? viewer)
    {
        var (s, c) = Open(Visibility.Public, effect: effect, traited: new HashSet<string> { "sc1" });
        if (viewer != null) s.SetViewSides(viewer);
        var m = Dialog(s);
        m.SetBoxText("Shots", "1");
        Next.Enqueue(face);
        m.Calculate(s.Game!.State);
        var dv = m.Evaluate(s.Game.State);
        var view = Shown(dv);
        Assert.False(NamesBarge(view), view);
        var x = new IntentExecutor(s, c);
        Next.Enqueue(face);
        var v = x.Execute("p1", North, new FormulaIntent("f", "nl1", null, new Dictionary<string, string> { ["Shots"] = "1" }, true, Preview: true)).Verdict!;
        return (view, dv.CanApply, Json(v));
    }

    [Theory]
    [InlineData("mask")]
    [InlineData("layer")]
    public void Effect_error_on_one_drawn_counter_same_view_for_any_split(string effect)
    {
        foreach (var viewer in new[] { null, North })
        {
            var ok = EffectView(effect, 1, viewer);
            var bad = EffectView(effect, 6, viewer);
            Assert.Equal(ok.View, bad.View);
            Assert.Equal(ok.CanApply, bad.CanApply);
            Assert.Equal(ok.Verdict, bad.Verdict);
        }
        // At Apply the host decides: the drawn counter without the trait refuses with the generic text, the other applies.
        var (s, c) = Open(Visibility.Public, effect: effect, traited: new HashSet<string> { "sc1" });
        s.SetViewSides(North);
        var m = Dialog(s);
        m.SetBoxText("Shots", "1");
        Next.Enqueue(6);
        var dv = m.Evaluate(s.Game!.State);
        Assert.True(dv.CanApply, dv.Status);
        Assert.Equal(FormulaMasking.ErrorHiddenText, FormulaDialogModel.ApplyRefusal(dv));
        Assert.False(c.ApplyFormula(null, "nl1", dv.Trace, false));
    }

    /// <summary>
    /// Final review I1: a Fully automatic start entry draws 4 of hex 0101's 6 boats, so at least one drawn barge lacks the layer (only SC 1
    /// Barge has it, with 3 boats). The run is refused, and the acting side's log names no drawn counter: the error is the neutral text.
    /// </summary>
    [Theory]
    [InlineData("mask")]
    [InlineData("layer")]
    public void Automatic_run_error_on_drawn_counter_names_no_counter(string effect)
    {
        var (s, c) = Open(Visibility.Private, effect: effect, traited: new HashSet<string> { "sc1" }, phases: NorthGuns());
        s.UpdateRules(p => p with
        {
            Formulas = p.Formulas.Select(f => f with
            {
                Bindings = f.Bindings.Where(b => b.Kind != BindingKind.Input).Select(b => b with { DrawCount = "4" }).ToList(),
            }).ToList(),
        });
        s.SetViewSides(North);
        var before = s.Game!.State.Log.Count == 0 ? 0 : s.Game.State.Log.Max(l => l.Seq);   // the setup's own "sc1 placed" lines name ids
        c.Start();
        var st = s.Game.State;
        var north = PlayerView.For(st, s.Rules, s.Model, North).Log.Where(l => l.Seq > before).ToList();
        Assert.DoesNotContain(north, l => NamesBarge(l.Text));
        Assert.Contains(north, l => l.Error && l.Text == "Shore battery: NL Battery: Formula \"Shore battery\": output 1 cannot act on one of the drawn counters");
        Assert.All(new[] { 3.0, 1.0, 0.0, 2.0 }.Zip(AllBoats(s)), p => Assert.Equal(p.First, p.Second));   // nothing applied
    }
}
