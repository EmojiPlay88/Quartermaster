using System.Globalization;
using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B6 final review C1: formula dialogs (hot-seat, the online host's screen) and the host's guest preview verdicts mask keyed() reads and place
/// outputs the viewer may not see (a Private place for a viewer not holding every side, another side's keyed holder value) exactly as they mask
/// other hidden inputs. Invented Red/Blue names and numbers only.
/// </summary>
[Collection(AppDataRootEnv.Name)]
public class PlacesFormulaMaskingTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-placemask-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();
    private const double Secret = 61616;
    private static readonly string SecretText = Secret.ToString(CultureInfo.InvariantCulture);
    private static readonly IReadOnlySet<string> Red = new HashSet<string> { "red" };
    private static readonly IReadOnlySet<string> Blue = new HashSet<string> { "blue" };
    private static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });
    private static readonly PlayerDef Bob = new("p2", "Bob", new[] { "blue" });
    private const string Global = "h:global";

    public PlacesFormulaMaskingTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static VariableBinding W => new() { Variable = "W", Kind = BindingKind.Unit, Property = "water" };
    private static FormulaOutput ToWater => new() { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set };

    /// <summary>"Mines" (a Private place), "Flood" (a Public place), "Stock" (keyed by turn on holders) and formulas reading or writing them.</summary>
    private static RulesPack Pack(string sha)
    {
        var p = PhaseFixture.Pack(sha);
        return p with
        {
            Format = 4,
            Properties = p.Properties.Concat(new[]
            {
                new PropertyDef { Id = "mines", Name = "Mines", Scope = PropertyScope.Holders, KeyedBy = "hex", Visibility = Visibility.Private },
                new PropertyDef { Id = "flood", Name = "Flood", Scope = PropertyScope.Holders, KeyedBy = "hex", Visibility = Visibility.Public },
                new PropertyDef { Id = "stock", Name = "Stock", Scope = PropertyScope.Holders, KeyedBy = "turn", Visibility = Visibility.Public },
            }).ToList(),
            Holders = new() { new HolderDef { Id = "reddepot", Name = "RedDepot", SideId = "red" } },
            Formulas = p.Formulas.Concat(new[]
            {
                new FormulaDef { Id = "f-mines", Name = "Mines here", Text = "keyed(Mines, hex()) + W", Bindings = new() { W }, Outputs = new() { ToWater } },
                new FormulaDef { Id = "f-flood", Name = "Flood here", Text = "keyed(Flood, hex()) + W", Bindings = new() { W }, Outputs = new() { ToWater } },
                new FormulaDef { Id = "f-stock", Name = "Red stock", Text = "keyed(Stock, turn(), RedDepot) + W", Bindings = new() { W }, Outputs = new() { ToWater } },
                new FormulaDef
                {
                    Id = "f-auto", Name = "Mines input", Text = "I + W",
                    Bindings = new() { W, new VariableBinding { Variable = "I", Kind = BindingKind.Input, AutoValue = "keyed(Mines, hex())" } },
                    Outputs = new() { ToWater },
                },
                new FormulaDef
                {
                    // The row key is the hidden value: the lookup error would quote it.
                    Id = "f-row", Name = "Mines row", Text = "T + W",
                    Bindings = new() { W, new VariableBinding { Variable = "T", Kind = BindingKind.Table, TableId = "t-cost", RowExpr = "keyed(Mines, hex())" } },
                    Outputs = new() { ToWater },
                },
                new FormulaDef
                {
                    // Takes more than the place holds: the Before and the "(negative)" mark would show the hidden value.
                    Id = "f-lay", Name = "Clear mines", Text = "W + 1000000", Bindings = new() { W },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "mines", Op = OutputOp.Subtract, Key = "hex()" } },
                },
                new FormulaDef
                {
                    Id = "f-set", Name = "Set mines", Text = "W", Bindings = new() { W },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "mines", Op = OutputOp.Set, Key = "hex()" } },
                },
                new FormulaDef
                {
                    // The pick's candidates are filtered by the hidden place value (review M2).
                    Id = "f-pick", Name = "Mined pick", Text = "P + W",
                    Bindings = new() { W, new VariableBinding { Variable = "P", Kind = BindingKind.Piece, Property = "water", PieceFilter = "keyed(Mines, hex()) > 0" } },
                    Outputs = new() { ToWater },
                },
                new FormulaDef
                {
                    Id = "f-lay-flood", Name = "Drain flood", Text = "W + 1000000", Bindings = new() { W },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "flood", Op = OutputOp.Subtract, Key = "hex()" } },
                },
            }).ToList(),
        };
    }

    private ModuleSession Open()
    {
        var fx = PhaseFixture.Module(numbered: true);
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.SemiAuto, new[] { Alice, Bob });
        s.NewRules("t");
        s.UpdateRules(_ => Pack(s.Archive.Sha256));
        var hex = PieceInfo.Location(s.Game!.State.Pieces[PhaseFixture.BlueOne], s.Model)!;
        foreach (var prop in new[] { "mines", "flood" })
            s.Game.Execute(new LedgerDelta(Global, prop, 0, Secret, "manual", null, null, null, null) { ValueKey = hex });
        s.Game.Execute(new LedgerDelta(LedgerKey.Holder("reddepot"), "stock", 0, Secret, "manual", null, null, null, null)
            { ValueKey = s.Game.State.Turn.Turn.ToString(CultureInfo.InvariantCulture) });
        return s;
    }

    private ModuleSession Guest(ModuleSession host)
    {
        var fx = PhaseFixture.Module(numbered: true);
        _fixtures.Add(fx);
        var guest = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "gcache" + _sessions.Count), Path.Combine(_tmp, "groot" + _sessions.Count));
        _sessions.Add(guest);
        guest.EnterGuestMode();
        guest.LoadGuestRules(host.Rules!);
        guest.ApplySnapshot(PlayerView.For(host.Game!.State, host.Rules, host.Model, Blue), Blue);
        return guest;
    }

    private static FormulaDialogModel Dialog(ModuleSession s, string formulaId, string pieceId = PhaseFixture.BlueOne)
    {
        var m = new FormulaDialogModel(s.Runner!, s.Rules!.ById<FormulaDef>(formulaId)!, pieceId);
        FormulaMasking.Configure(m, s);
        return m;
    }

    private static string Shown(DialogView v) =>
        string.Join("|", v.Boxes.SelectMany(b => new[] { b.Text, b.Tooltip ?? "", b.CaptionSuffix }))
        + "|" + v.ResultText + "|" + v.WorkingText + "|" + v.Status + "|" + string.Join("|", v.InlineRolls.Select(r => r.Text));

    private static FormulaVerdict Preview(IntentExecutor x, string formulaId)
    {
        var r = x.Execute("p2", Blue, new FormulaIntent(formulaId, PhaseFixture.BlueOne, null, new Dictionary<string, string>(), true, true));
        Assert.NotNull(r.Verdict);
        return r.Verdict!;
    }

    private static string Json(FormulaVerdict v) => JsonSerializer.Serialize(v, QuartermasterJson.Options);

    [Fact]
    public void HotSeat_private_place_read_masks_working()
    {
        var s = Open();
        s.SetViewSides(Blue);   // Bob at the shared screen: the dialog runs on the full state
        var m = Dialog(s, "f-mines");
        Assert.Contains(FormulaMasking.KeyedCallInput, m.Hidden);
        var v = m.Evaluate(s.Game!.State);
        Assert.Equal(Secret + 10, v.Trace.Result!.Value.Number);   // the run itself read it
        Assert.NotEqual("", v.Trace.Working);
        Assert.Equal(FormulaMasking.WorkingHiddenText, v.WorkingText);
        Assert.DoesNotContain(SecretText, Shown(v));
        // Red does not hold every side either: a Private place is hidden from both.
        s.SetViewSides(Red);
        Assert.Equal(new[] { FormulaMasking.KeyedCallInput }, Dialog(s, "f-mines", PhaseFixture.RedOne).Hidden.ToArray());
        // Holding every side: nothing hidden.
        s.SetViewSides(new HashSet<string> { "red", "blue" });
        Assert.Empty(Dialog(s, "f-mines").Hidden);
    }

    [Fact]
    public void HotSeat_public_place_read_unmasked()
    {
        var s = Open();
        s.SetViewSides(Blue);
        var m = Dialog(s, "f-flood");
        Assert.Empty(m.Hidden);
        var v = m.Evaluate(s.Game!.State);
        Assert.Equal(v.Trace.Working, v.WorkingText);
        Assert.Empty(Dialog(s, "f-lay-flood").Hidden);
    }

    [Fact]
    public void HotSeat_other_side_keyed_holder_public_read_unmasked()
    {
        // B7 #74a: a Public keyed value of another side's holder is seen (the one rule, HiddenValues), so the read masks nothing.
        var s = Open();
        s.SetViewSides(Blue);
        var m = Dialog(s, "f-stock");
        Assert.Empty(m.Hidden);
        var v = m.Evaluate(s.Game!.State);
        Assert.Equal(v.Trace.Working, v.WorkingText);
        s.SetViewSides(Red);   // Red's own depot, run on Red's own piece
        Assert.Empty(Dialog(s, "f-stock", PhaseFixture.RedOne).Hidden);
    }

    [Fact]
    public void HotSeat_binding_reading_private_place_is_hidden_and_error_masked()
    {
        var s = Open();
        s.SetViewSides(Blue);
        var auto = Dialog(s, "f-auto");
        Assert.Contains("I", auto.Hidden);
        var av = auto.Evaluate(s.Game!.State);
        Assert.DoesNotContain(SecretText, Shown(av));

        var row = Dialog(s, "f-row");
        Assert.Contains("T", row.Hidden);
        var rv = row.Evaluate(s.Game.State);
        Assert.Contains(SecretText, rv.Trace.Error);   // the lookup error quotes the hidden value
        Assert.Equal(FormulaMasking.ErrorHiddenText, rv.Status);
        Assert.DoesNotContain(SecretText, Shown(rv));
    }

    [Fact]
    public void HotSeat_private_place_output_shows_no_before_nor_negative()
    {
        var s = Open();
        s.SetViewSides(Blue);
        var m = Dialog(s, "f-lay");
        Assert.Contains(FormulaMasking.PlaceOutputInput, m.Hidden);
        var v = m.Evaluate(s.Game!.State);
        var o = Assert.Single(v.Trace.Outputs);
        Assert.Equal(Secret, o.Before);   // the run read it
        Assert.False(v.WorkingNegative);
        Assert.DoesNotContain("(negative)", v.WorkingText);
        Assert.DoesNotContain(SecretText, Shown(v));
        Assert.False(FormulaMasking.OutputShown(s, Blue, o));
        // A Public place output stays shown, negative mark included.
        var flood = Dialog(s, "f-lay-flood").Evaluate(s.Game.State);
        Assert.True(flood.WorkingNegative);
        Assert.True(FormulaMasking.OutputShown(s, Blue, Assert.Single(flood.Trace.Outputs)));
    }

    [Fact]
    public void Host_screen_for_side_masks_as_hot_seat()
    {
        // The online host's own screen runs the dialog on the full state for the host's sides, as hot-seat does.
        var s = Open();
        s.SetViewSides(Red);
        var m = Dialog(s, "f-mines", PhaseFixture.RedOne);
        Assert.Equal(new[] { FormulaMasking.KeyedCallInput }, m.Hidden.ToArray());   // only the place read: Red's own unit is Red's
        var v = m.Evaluate(s.Game!.State);
        Assert.Equal(FormulaMasking.WorkingHiddenText, v.WorkingText);
        Assert.DoesNotContain(SecretText, Shown(v));
    }

    [Fact]
    public void Guest_dialog_masks_same_inputs_as_host()
    {
        var host = Open();
        var guest = Guest(host);
        foreach (var id in new[] { "f-mines", "f-auto", "f-row", "f-lay" })
        {
            var g = Dialog(guest, id);
            Assert.NotEmpty(g.Hidden);
            Assert.Equal(FormulaMasking.HiddenVariables(host.Rules!, host.Model, host.Game!.State, host.Rules!.ById<FormulaDef>(id)!, PhaseFixture.BlueOne, Blue)
                .OrderBy(x => x), g.Hidden.OrderBy(x => x));
        }
        Assert.Empty(Dialog(guest, "f-flood").Hidden);
        Assert.Empty(Dialog(guest, "f-stock").Hidden);   // B7 #74a: a Public value of Red's depot
    }

    [Fact]
    public void Guest_preview_verdict_carries_no_hidden_keyed_value()
    {
        var host = Open();
        var c = new PhaseController(host, (cmd, st) => CommandLog.Prepare(cmd, st, host));
        var x = new IntentExecutor(host, c);
        foreach (var id in new[] { "f-mines", "f-auto", "f-row", "f-lay", "f-stock" })
        {
            var v = Preview(x, id);
            Assert.DoesNotContain(SecretText, Json(v));
            Assert.DoesNotContain(SecretText, v.Message ?? "");
        }
        var auto = Preview(x, "f-auto");
        Assert.False(auto.Values?.ContainsKey("I") == true);
        Assert.True(auto.Values?.ContainsKey("W") == true);   // the visible input still goes back
        Assert.Equal(FormulaBox.Hidden, auto.Boxes.Single(b => b.Var == "I").State);
        // A Public place reads through.
        var flood = Preview(x, "f-flood");
        Assert.DoesNotContain(flood.Boxes, b => b.State == FormulaBox.Hidden);
    }

    [Fact]
    public void GuardsOnHidden_counts_keyed_calls()
    {
        var f = new FormulaDef
        {
            Id = "f-g", Name = "Guarded", Text = "W + V",
            Bindings = new() { W, new VariableBinding { Variable = "V", Kind = BindingKind.Input, When = "keyed(Mines, hex()) > 0" } },
        };
        Assert.Contains("V", FormulaMasking.GuardsOnHidden(f, new HashSet<string> { FormulaMasking.KeyedCallInput }));
        Assert.Empty(FormulaMasking.GuardsOnHidden(f, new HashSet<string> { FormulaMasking.HolderCallInput }));
    }

    [Fact]
    public void Private_place_set_output_masked_on_guest_and_host()
    {
        // Review M1: a Set previews on a guest's view with Before = Default; the masking hides the formula and its output text.
        var host = Open();
        host.SetViewSides(Blue);
        var hm = Dialog(host, "f-set");
        Assert.Contains(FormulaMasking.PlaceOutputInput, hm.Hidden);
        var hv = hm.Evaluate(host.Game!.State);
        Assert.Equal(FormulaMasking.WorkingHiddenText, hv.WorkingText);
        Assert.False(FormulaMasking.OutputShown(host, Blue, Assert.Single(hv.Trace.Outputs)));
        var guest = Guest(host);
        var gm = Dialog(guest, "f-set");
        Assert.Contains(FormulaMasking.PlaceOutputInput, gm.Hidden);
        var gv = gm.Evaluate(guest.Game!.State);
        Assert.Equal(FormulaMasking.WorkingHiddenText, gv.WorkingText);
        foreach (var o in gv.Trace.Outputs) Assert.False(FormulaMasking.OutputShown(guest, Blue, o));
    }

    [Fact]
    public void Piece_filter_reading_private_place_masks()
    {
        // Review M2: a Piece binding's filter reads each candidate's keyed values, so a hidden keyed() read there masks the formula.
        var s = Open();
        s.SetViewSides(Blue);
        Assert.Contains(FormulaMasking.KeyedCallInput, Dialog(s, "f-pick").Hidden);
        s.SetViewSides(new HashSet<string> { "red", "blue" });
        Assert.Empty(Dialog(s, "f-pick").Hidden);
    }

    [Fact]
    public void HotSeat_local_keyed_edit_of_hidden_value_refused()
    {
        // Review M5: as the host refuses a guest's edit of a value it cannot see, a hot-seat viewer's EditKeyed of a Private place does nothing.
        var s = Open();
        var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        var a = new LocalActions(s, c, new HistoryController(s, c, (cmd, st) => cmd));
        var hex = PieceInfo.Location(s.Game!.State.Pieces[PhaseFixture.BlueOne], s.Model)!;
        s.SetViewSides(Blue);
        a.EditKeyed(Global, "mines", hex, 5, "n");
        Assert.Equal(Secret, KeyedValues.Get(s.Game.State, Global, "mines", hex));
        a.EditKeyed(Global, "flood", hex, 5, "n");
        Assert.Equal(5, KeyedValues.Get(s.Game.State, Global, "flood", hex));
        s.SetViewSides(new HashSet<string> { "red", "blue" });
        a.EditKeyed(Global, "mines", hex, 5, "n");
        Assert.Equal(5, KeyedValues.Get(s.Game.State, Global, "mines", hex));
    }

    [Fact]
    public void Game_over_gives_no_new_edit_of_a_private_place()
    {
        // Final review F7: game over lifts hiding, never adds a right. A Private place a Blue viewer could not edit stays unedited.
        var s = Open();
        var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        var a = new LocalActions(s, c, new HistoryController(s, c, (cmd, st) => cmd));
        var x = new IntentExecutor(s, c);
        var hex = PieceInfo.Location(s.Game!.State.Pieces[PhaseFixture.BlueOne], s.Model)!;
        s.Game.Execute(new EndGame("Blue holds the river", new[] { "blue" }, null));
        s.SetViewSides(Blue);
        Assert.Empty(Dialog(s, "f-mines").Hidden);   // the information is lifted
        a.EditKeyed(Global, "mines", hex, 5, "n");
        Assert.Equal(Secret, KeyedValues.Get(s.Game.State, Global, "mines", hex));
        var r = x.Execute("p2", Blue, new KeyedEditIntent(Global, "mines", hex, 6, "n"));
        Assert.False(r.Ok);
        Assert.Equal(Secret, KeyedValues.Get(s.Game.State, Global, "mines", hex));
        // A Public place stays editable, as before game over.
        Assert.True(x.Execute("p2", Blue, new KeyedEditIntent(Global, "flood", hex, 6, "n")).Ok);
        Assert.Equal(6, KeyedValues.Get(s.Game.State, Global, "flood", hex));
    }
}
