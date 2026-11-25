using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B3 hidden information: an online guest's (and a hot-seat player's) runs that act on another side's pieces, the host deciding the pick,
/// each side's log lines, and <see cref="FormulaMasking.PieceOutputInput"/>. Invented Riverland data only (see <see cref="OtherPiecesTestBase"/>).
/// </summary>
[Collection(AppDataRootEnv.Name)]
public class OtherPiecesOnlineTests : OtherPiecesTestBase
{
    private static FormulaVerdict Send(IntentExecutor x, string formulaId, string pieceId, string label, string pickId) =>
        x.Execute("p2", South, new FormulaIntent(formulaId, pieceId, null, new Dictionary<string, string> { ["T"] = label }, false,
            Picks: new Dictionary<string, string> { ["T"] = pickId })).Verdict!;

    private static FormulaVerdict Send(IntentExecutor x, FormulaIntent intent) => x.Execute("p2", South, intent).Verdict!;

    private static string Json(object o) => JsonSerializer.Serialize(o, QuartermasterJson.Options);

    private static (int Level, bool Active) LevelOf(GameState st, string id)
    {
        var p = st.Pieces[id];
        var (index, def) = PieceInfo.FindLayer(PieceInfo.TypeOf(p), "Levels")!.Value;
        return TraitState.LayerLevel(def, p.TraitStates[index]);
    }

    // ---- logs and the host's decision ----

    [Fact]
    public void Guest_piece_output_logs_each_side()
    {
        var f = F("Shelling", "2", new[] { Own(), Pick() }, PieceWrite(OutputOp.Subtract));
        // Strength is public, so the defender may read the working (a formula picking a private value never shows its working: PlayerView).
        var (host, hc) = Open(Difficulty.FullyAuto, new[] { f }, players: new[] { Alice, Bob });
        var x = new IntentExecutor(host, hc);
        var v = Send(x, "f", "s1", Label(host, "n1"), "n1");
        Assert.True(v.Applied, v.Message);
        Assert.DoesNotContain("Strength 4", Json(v));
        Assert.Equal(2, Str(host, "n1"));

        var st = host.Game!.State;
        var north = PlayerView.For(st, host.Rules, host.Model, North);
        var south = PlayerView.For(st, host.Rules, host.Model, South);
        // The defender reads the full line; the acting side reads only that the other side's ledger changed.
        Assert.Contains(north.Log, l => l.Text == "North Barge: Shelling: Strength 4 − 2 = 2");
        Assert.Contains(north.LedgerHistory, e => e.Key == LedgerKey.Piece("n1") && e.Working == "Shelling: Strength 4 − 2 = 2");
        Assert.DoesNotContain(south.Log, l => l.Text.Contains("Strength 4", StringComparison.Ordinal));
        Assert.Contains(south.Log, l => l.Text == "Northern League ledger changed");
        Assert.DoesNotContain("Strength 4", Json(south));
        // The run's own working line (no own write) is the acting side's: on the run piece, without the Piece part.
        var working = Assert.Single(st.Log, l => l.AppliedRun == "f");
        Assert.Equal("South Gunboat: 2 = 2", working.Text);
        Assert.Equal("s1", working.PieceId);
        Assert.Equal("blue", working.SideId);
        Assert.Contains(south.Log, l => l.AppliedRun == "f" && l.Text == "South Gunboat: 2 = 2");
        Assert.DoesNotContain(north.Log, l => l.Text == "South Gunboat: 2 = 2");
    }

    [Fact]
    public void Guest_dialog_masks_piece_output()
    {
        var fortify = F("Fortify", "1", new[] { Own(), Pick() }, SetLayer(OutputTarget.Piece, "1"));
        var bad = F("Overbuild", "1", new[] { Own(), Pick() }, SetLayer(OutputTarget.Piece, "9")) with { Id = "f9" };
        var (host, hc) = Open(Difficulty.FullyAuto, new[] { fortify, bad }, hiding: true, players: new[] { Alice, Bob });
        var x = new IntentExecutor(host, hc);
        var guest = Guest(host, hiding: true);

        var m = Dialog(guest, "s1");
        Assert.Contains(FormulaMasking.PieceOutputInput, m.Hidden);
        var dv = m.Evaluate(guest.Game!.State);
        var choices = dv.Boxes.Single(b => b.Variable == "T").Choices!;
        Assert.Contains("Unknown piece (0202)", choices);
        Assert.DoesNotContain(choices, c => c.StartsWith("North Battery", StringComparison.Ordinal) || c.StartsWith("North Minefield", StringComparison.Ordinal));
        m.SetBoxText("T", "Unknown piece (0202)");
        dv = m.Evaluate(guest.Game!.State);
        Assert.Equal(FormulaDialogModel.HostWaitText, dv.Status);
        Assert.Equal("", dv.WorkingText);
        var intent = m.Intent(null, calculate: false);
        Assert.Equal("n4", intent.Picks!["T"]);

        var v = Send(x, intent);
        Assert.True(v.Applied, v.Message);
        Assert.Equal((1, true), LevelOf(host.Game!.State, "n4"));

        // The host's error on a masked pick names the piece and its layer: the guest gets the generic text only.
        var refused = Send(x, new FormulaIntent("f9", "s1", null, new Dictionary<string, string> { ["T"] = "Unknown piece (0202)" }, false,
            Picks: new Dictionary<string, string> { ["T"] = "n4" }));
        Assert.False(refused.Applied);
        Assert.Equal(FormulaMasking.ErrorHiddenText, refused.Message);
        Assert.DoesNotContain("Minefield", Json(refused));
        Assert.DoesNotContain("Levels", Json(refused));
    }

    [Fact]
    public void Guest_dialog_sends_when_target_value_hidden()
    {
        // The pick reads a public value, but the output writes another side's private Orders: the guest's view cannot run it; the host does.
        var f = F("Orders", "3", new[] { Own(), Pick() }, PieceWrite(OutputOp.Set, "orders"));
        var (host, hc) = Open(Difficulty.FullyAuto, new[] { f }, players: new[] { Alice, Bob });
        var x = new IntentExecutor(host, hc);
        var guest = Guest(host, hiding: false);
        var m = Dialog(guest, "s1");
        Assert.Equal(new[] { FormulaMasking.PieceOutputInput }, m.Hidden.ToArray());
        m.SetBoxText("T", Label(host, "n1"));
        var dv = m.Evaluate(guest.Game!.State);
        Assert.Equal(FormulaDialogModel.HostWaitText, dv.Status);
        var v = Send(x, m.Intent(null, calculate: false));
        Assert.True(v.Applied, v.Message);
        Assert.Equal(3, Ledger.Get(host.Game!.State, LedgerKey.Piece("n1"), "orders"));
    }

    [Fact]
    public void Guest_pick_of_hidden_piece_refused()
    {
        var f = F("Shelling", "2", new[] { Own(), Pick() }, PieceWrite(OutputOp.Subtract));
        var (host, hc) = Open(Difficulty.FullyAuto, new[] { f }, hiding: true, players: new[] { Alice, Bob });
        var x = new IntentExecutor(host, hc);
        var v = Send(x, "f", "s1", Label(host, "n3"), "n3");
        Assert.False(v.Applied);
        Assert.Equal("Choose one of the listed pieces", v.Message);
        Assert.Equal(6, Str(host, "n3"));
        Assert.False(host.Game!.CanUndo);
    }

    [Fact]
    public void Guest_pick_same_hex_resolves_on_host()
    {
        var f = F("Board", "1", new[] { Own(), Pick(sameHex: true) }, PieceWrite(OutputOp.Subtract));
        var (host, hc) = Open(Difficulty.FullyAuto, new[] { f }, players: new[] { Alice, Bob });
        var x = new IntentExecutor(host, hc);
        var guest = Guest(host, hiding: false);
        var m = Dialog(guest, "s1");
        var dv = m.Evaluate(guest.Game!.State);
        Assert.Equal(new[] { Label(host, "n1"), Label(host, "n2") }, dv.Boxes.Single(b => b.Variable == "T").Choices!.ToArray());
        m.SetBoxText("T", Label(host, "n2"));
        var v = Send(x, m.Intent(null, calculate: false));
        Assert.True(v.Applied, v.Message);
        Assert.Equal(2, Str(host, "n2"));
    }

    [Fact]
    public void Guest_remove_enemy_piece_applied_at_once_and_undoable()
    {
        var f = F("Sink", "1", new[] { Own(), Pick(sameHex: true) }, Remove(OutputTarget.Piece));
        var (host, hc) = Open(Difficulty.FullyAuto, new[] { f }, players: new[] { Alice, Bob });
        var x = new IntentExecutor(host, hc);
        var v = Send(x, "f", "s1", Label(host, "n1"), "n1");
        Assert.True(v.Applied, v.Message);
        var g = host.Game!;
        Assert.False(g.State.Pieces.ContainsKey("n1"));
        Assert.Null(Str(host, "n1"));
        var north = PlayerView.For(g.State, host.Rules, host.Model, North);
        var south = PlayerView.For(g.State, host.Rules, host.Model, South);
        Assert.Contains(north.Log, l => l.Kind == LogKind.Delete && l.Text == "Sink: removed North Barge");
        Assert.DoesNotContain(south.Log, l => l.Kind == LogKind.Delete);

        Assert.Single(g.Actions);
        g.Undo();
        Assert.True(g.State.Pieces.ContainsKey("n1"));
        Assert.Equal(4, Str(host, "n1"));
    }

    [Fact]
    public void Guest_setmask_reveal_enemy_piece()
    {
        var f = F("Sweep", "0", new[] { Own(), Pick() }, SetMask(OutputTarget.Piece, "0"));
        var (host, hc) = Open(Difficulty.FullyAuto, new[] { f }, hiding: true, players: new[] { Alice, Bob });
        var x = new IntentExecutor(host, hc);
        var guest = Guest(host, hiding: true);
        Assert.True(string.IsNullOrEmpty(PieceInfo.Name(guest.Game!.State.Pieces["n4"])));   // masked: a nameless placeholder
        var m = Dialog(guest, "s1");
        m.SetBoxText("T", "Unknown piece (0202)");
        m.Evaluate(guest.Game!.State);
        var v = Send(x, m.Intent(null, calculate: false));
        Assert.True(v.Applied, v.Message);
        Assert.False(PieceAccess.Masking(host.Game!.State.Pieces["n4"]).Mask != null);
        var south = PlayerView.For(host.Game!.State, host.Rules, host.Model, South);
        Assert.Equal("North Minefield", PieceInfo.Name(south.Pieces["n4"]));
        // The acting side's working names the piece as it was picked; its view never shows it (the pick may be masked: inputs hidden).
        Assert.Contains(host.Game!.State.Log, l => l.AppliedRun == "f" && l.Text == "South Gunboat: 0 = 0 → Unknown piece unmasked");
        Assert.Contains(south.Log, l => l.AppliedRun == "f" && l.Text == "Sweep: calculated (inputs hidden)");
        // The reveal line names the piece: the defender's, and the acting side's now that the piece is no longer masked.
        Assert.Contains(south.Log, l => l.Kind == LogKind.System && l.Text == "Sweep: North Minefield unmasked" && l.SideId == "red");
    }

    [Fact]
    public void Hot_seat_piece_output_uses_current_player_candidates()
    {
        var f = F("Fortify", "1", new[] { Own(), Pick() }, SetLayer(OutputTarget.Piece, "1"));
        var (s, c) = Open(Difficulty.SemiAuto, new[] { f }, hiding: true, players: new[] { Alice, Bob });
        s.SetViewSides(South);
        var m = Dialog(s, "s1");
        var dv = m.Evaluate(s.Game!.State);
        var choices = dv.Boxes.Single(b => b.Variable == "T").Choices!;
        Assert.Contains("Unknown piece (0202)", choices);
        Assert.DoesNotContain(choices, x => x.StartsWith("North Battery", StringComparison.Ordinal) || x.StartsWith("North Minefield", StringComparison.Ordinal));
        m.SetBoxText("T", "Unknown piece (0202)");
        dv = m.Evaluate(s.Game!.State);
        Assert.True(dv.CanApply, dv.Status);
        Assert.Equal(FormulaMasking.WorkingHiddenText, dv.WorkingText);
        Assert.True(c.ApplyFormula(null, "s1", dv.Trace, false));
        Assert.Equal((1, true), LevelOf(s.Game!.State, "n4"));
        var st = s.Game!.State;
        // The actor's working names the masked piece as it saw it and gives the level as a number, never the level's name.
        var working = Assert.Single(st.Log, l => l.AppliedRun == "f");
        Assert.Equal("South Gunboat: 1 = 1 → Levels of Unknown piece = 1", working.Text);
        Assert.Contains(st.Log, l => l.Kind == LogKind.System && l.Text == "Fortify: Levels of North Minefield = Camp" && l.SideId == "red");
        var south = PlayerView.For(st, s.Rules, s.Model, South);
        Assert.DoesNotContain(south.Log, l => l.Text.Contains("Camp", StringComparison.Ordinal) || l.Text.Contains("Minefield", StringComparison.Ordinal));
    }

    // ---- FormulaMasking.PieceOutputInput ----

    [Fact]
    public void PieceOutputInput_added_for_private_property()
    {
        var shell = F("Shelling", "2", new[] { Own(), Pick() }, PieceWrite(OutputOp.Subtract));
        var (s, _) = Open(Difficulty.SemiAuto, new[] { shell }, strength: Visibility.Private);
        Assert.Contains(FormulaMasking.PieceOutputInput, FormulaMasking.HiddenVariables(s.Rules!, s.Model, s.Game!.State, shell, "s1", South));
        // A public pick that writes a private property.
        var orders = F("Orders", "3", new[] { Own(), Pick() }, PieceWrite(OutputOp.Set, "orders"));
        Assert.Equal(new[] { FormulaMasking.PieceOutputInput },
            FormulaMasking.HiddenVariables(s.Rules! with { Properties = s.Rules!.Properties.Select(p => p.Id == "str" ? p with { Visibility = Visibility.Public } : p).ToList() },
                s.Model, s.Game!.State, orders, "s1", South).ToArray());
        Assert.Empty(FormulaMasking.HiddenVariables(s.Rules!, s.Model, s.Game!.State, shell, "s1", new HashSet<string> { "red", "blue" }));
    }

    [Fact]
    public void PieceOutputInput_added_when_pieces_may_hide()
    {
        var f = F("Fortify", "1", new[] { Own(), Pick() }, SetLayer(OutputTarget.Piece, "1"));
        var (s, _) = Open(Difficulty.SemiAuto, new[] { f }, hiding: true);
        Assert.Contains(FormulaMasking.PieceOutputInput, FormulaMasking.HiddenVariables(s.Rules!, s.Model, s.Game!.State, f, "s1", South));
        var remove = F("Sink", "1", new[] { Pick() }, Remove(OutputTarget.Piece));
        Assert.Contains(FormulaMasking.PieceOutputInput, FormulaMasking.HiddenVariables(s.Rules!, s.Model, s.Game!.State, remove, "s1", South));
    }

    [Fact]
    public void PieceOutputInput_absent_for_public_and_no_hiding()
    {
        var f = F("Board", "1", new[] { Pick() }, PieceWrite(OutputOp.Subtract), SetLayer(OutputTarget.Piece, "1"), SetMask(OutputTarget.Piece, "0"));
        var (s, _) = Open(Difficulty.SemiAuto, new[] { f });
        Assert.DoesNotContain(FormulaMasking.PieceOutputInput, FormulaMasking.HiddenVariables(s.Rules!, s.Model, s.Game!.State, f, "s1", South));
        // Own effects (target Unit) never add it, even when pieces may hide.
        var own = F("Raise", "1", Array.Empty<VariableBinding>(), SetLayer(OutputTarget.Unit, "1"));
        var (h, _) = Open(Difficulty.SemiAuto, new[] { own }, hiding: true);
        Assert.DoesNotContain(FormulaMasking.PieceOutputInput, FormulaMasking.HiddenVariables(h.Rules!, h.Model, h.Game!.State, own, "s2", South));
    }

    [Fact]
    public void PieceOutputInput_guard_on_the_target_is_the_hosts()
    {
        var f = F("Orders", "B", new[] { Own(), Pick(), new VariableBinding { Variable = "B", Kind = BindingKind.Input, When = "T > 2" } },
            PieceWrite(OutputOp.Set, "orders"));
        Assert.Equal(new[] { "B" }, FormulaMasking.GuardsOnHidden(f, new HashSet<string> { FormulaMasking.PieceOutputInput }).ToArray());
        Assert.Empty(FormulaMasking.GuardsOnHidden(f, new HashSet<string> { FormulaMasking.HolderCallInput }));
    }
}
