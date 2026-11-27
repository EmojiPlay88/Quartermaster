using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

/// <summary>B9 #13b online: a guest lists the dumps of its own view, the host checks every holder label against the sender's own list.</summary>
[Collection(AppDataRootEnv.Name)]
public class HolderPickOnlineTests : HolderPickTestBase
{
    private (ModuleSession S, PhaseController C, IntentExecutor X) Host()
    {
        var s = PickSession();
        var c = Controller(s);
        c.Start();   // Weather: no side acts
        return (s, c, new IntentExecutor(s, c));
    }

    /// <summary>A guest holding the Southern Compact (and its Delta Pact ally): the host's view for it and the rules pack.</summary>
    private ModuleSession Guest(ModuleSession host)
    {
        var g = GuestSession();
        g.LoadGuestRules(host.Rules!);
        Refresh(g, host);
        return g;
    }

    private static void Refresh(ModuleSession guest, ModuleSession host) =>
        guest.ApplySnapshot(PlayerView.For(host.Game!.State, host.Rules, host.Model, SideFacts.Held(host.Rules, South)!), South);

    private static FormulaIntent Draw(string dump, string need = "4", bool preview = false, string formulaId = "f-draw") =>
        new(formulaId, null, null, new Dictionary<string, string> { ["Dump"] = dump, ["Need"] = need }, preview, Preview: preview);

    private static string Json(object o) => JsonSerializer.Serialize(o, QuartermasterJson.Options);

    [Fact]
    public void Guest_dropdown_from_own_view()
    {
        var (host, _, _) = Host();
        var guest = Guest(host);
        var dv = Dialog(guest).Evaluate(guest.Game!.State);
        Assert.Equal(new[] { "Mill Dump" }, Box(dv, "Dump").Choices!.ToArray());
        Assert.Equal("", Box(dv, "Dump").Text);
        Assert.Equal("Waiting for input", Box(dv, "Dump").Tooltip);
        host.Game!.Execute(Capture(host.Rules!));
        Refresh(guest, host);
        dv = Dialog(guest).Evaluate(guest.Game.State);
        Assert.Equal(new[] { "Ford Dump", "Mill Dump" }, Box(dv, "Dump").Choices!.ToArray());
        // The pick is the guest's own: the value read from it is seen in its own dialog.
        var m = Dialog(guest);
        m.SetBoxText("Dump", "Ford Dump");
        m.SetBoxText("Need", "1");
        dv = m.Evaluate(guest.Game.State);
        Assert.Empty(m.Hidden);
        Assert.Contains("Stores of Ford Dump", dv.WorkingText);
    }

    [Fact]
    public void Guest_apply_with_own_holder_applies()
    {
        var (host, _, x) = Host();
        var guest = Guest(host);
        var m = Dialog(guest);
        m.SetBoxText("Dump", "Mill Dump");
        m.SetBoxText("Need", "4");
        var intent = m.Intent(null, calculate: false);
        Assert.Equal("Mill Dump", intent.Typed["Dump"]);
        var v = x.Execute("p2", South, intent).Verdict!;
        Assert.True(v.Applied, v.Message);
        Assert.Equal(MillStores - 4, StoresOf(host.Game!.State, Mill));
        Assert.Equal(FordStores, Stores(host.Game.State));
        Assert.Contains(host.Game.State.LedgerHistory, e => e.Key == LedgerKey.Holder(Mill) && e.After == MillStores - 4);
        host.Game.Undo();
        Assert.Equal(MillStores, StoresOf(host.Game.State, Mill));
    }

    [Fact]
    public void Guest_label_not_in_own_list_refused()
    {
        var (host, _, x) = Host();
        host.Game!.Execute(Capture(host.Rules!));   // the south takes Ford Dump ...
        var ok = x.Execute("p2", South, Draw("Ford Dump", "1")).Verdict!;
        Assert.True(ok.Applied, ok.Message);
        Assert.Equal(FordStores - 1, Stores(host.Game.State));
        host.Game.Execute(Capture(host.Rules!, 2, 1));   // ... and loses it again
        var no = x.Execute("p2", South, Draw("Ford Dump", "1")).Verdict!;
        Assert.False(no.Applied);
        Assert.Equal("Choose one of the listed holders", no.Message);
        Assert.Equal(FordStores - 1, Stores(host.Game.State));
        // The north (the owner again) may draw from it.
        var north = x.Execute("p1", North, Draw("Ford Dump", "1")).Verdict!;
        Assert.True(north.Applied, north.Message);
        Assert.Equal(FordStores - 2, Stores(host.Game.State));
    }

    [Fact]
    public void Guest_preview_same_text_whether_label_exists_or_not()
    {
        var (host, _, x) = Host();
        var history = host.Game!.State.LedgerHistory.Count;
        var real = x.Execute("p2", South, Draw("Ford Dump", preview: true));   // the north's dump
        var invented = x.Execute("p2", South, Draw("Weir Dump", preview: true));
        Assert.False(real.Verdict!.Applied);
        Assert.Equal("Choose one of the listed holders", real.Verdict.Message);
        Assert.Equal(Json(real), Json(invented));
        var applyReal = x.Execute("p2", South, Draw("Ford Dump"));
        var applyInvented = x.Execute("p2", South, Draw("Weir Dump"));
        Assert.Equal(Json(applyReal), Json(applyInvented));
        Assert.DoesNotContain(FordStores.ToString(System.Globalization.CultureInfo.InvariantCulture), Json(real));
        Assert.Equal(FordStores, Stores(host.Game!.State));
        Assert.Equal(history, host.Game.State.LedgerHistory.Count);   // nothing written
    }

    [Fact]
    public void Guest_rule_skips_holder_variable_outputs()
    {
        var s = PickSession();
        // A stale fixed holder next to the Holder variable (the validator warns; the run does not use it): the guest is not refused for it.
        s.UpdateRules(p => p with
        {
            Formulas = p.Formulas.Select(f => f.Id != "f-draw" ? f
                : f with { Outputs = f.Outputs.Select(o => o with { HolderId = Ford }).ToList() }).ToList(),
        });
        var c = Controller(s);
        c.Start();
        var x = new IntentExecutor(s, c);
        var v = x.Execute("p2", South, Draw("Mill Dump", "3")).Verdict!;
        Assert.True(v.Applied, v.Message);
        Assert.Equal(MillStores - 3, StoresOf(s.Game!.State, Mill));
        Assert.Equal(FordStores, Stores(s.Game.State));
        // A fixed Holder output is still checked with the current owner.
        var haul = x.Execute("p2", South, new FormulaIntent("f-haul", null, null, new Dictionary<string, string>(), true));
        Assert.False(haul.Ok);
    }
}
