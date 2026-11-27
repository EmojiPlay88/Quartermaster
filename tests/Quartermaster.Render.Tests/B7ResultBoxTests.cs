using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B7 (B6 carry): a masked formula's result box shows the result only when the run writes at least one output and every output is visible
/// to the viewer; else it reads <c>hidden</c> (on a guest always <c>hidden — the host calculates</c>). The batch grid shows the same text.
/// Invented Red/Blue names and numbers only (the pack of <see cref="B7FormulaMaskingTests"/>).
/// </summary>
[Collection(AppDataRootEnv.Name)]
public class B7ResultBoxTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-b7result-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();
    private const double Secret = B7FormulaMaskingTests.Secret;
    private static readonly string DoubleText = (Secret * 2).ToString(CultureInfo.InvariantCulture);
    private static readonly IReadOnlySet<string> Blue = B7FormulaMaskingTests.Blue;
    private static readonly IReadOnlySet<string> Red = new HashSet<string> { "red" };

    public B7ResultBoxTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static FormulaDef Doubled(string id, params FormulaOutput[] outputs) =>
        new() { Id = id, Name = "Doubled " + id, Text = "keyed(Mines, hex()) * 2", Outputs = outputs.ToList() };

    private static readonly FormulaDef[] Extra =
    {
        Doubled("f-to-mines", B7FormulaMaskingTests.ToMines),
        Doubled("f-to-water", B7FormulaMaskingTests.ToWater),
        Doubled("f-no-output"),
        Doubled("f-both", B7FormulaMaskingTests.ToWater, B7FormulaMaskingTests.ToMines),
        Doubled("f-own-expr", B7FormulaMaskingTests.ToWater with { Expr = "1" }),
    };

    private ModuleSession Open() => B7FormulaMaskingTests.Open(_tmp, _fixtures, _sessions, Extra);

    private static FormulaDialogModel Dialog(ModuleSession s, string formulaId, string pieceId = PhaseFixture.BlueOne)
    {
        var m = new FormulaDialogModel(s.Runner!, s.Rules!.ById<FormulaDef>(formulaId)!, pieceId);
        FormulaMasking.Configure(m, s);
        return m;
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

    [Fact]
    public void Hotseat_result_hidden_when_output_hidden()
    {
        var s = Open();
        s.SetViewSides(Blue);
        foreach (var id in new[] { "f-to-mines", "f-both" })
        {
            var m = Dialog(s, id);
            Assert.NotEmpty(m.Hidden);
            var v = m.Evaluate(s.Game!.State);
            Assert.Equal(Secret * 2, v.Trace.Result!.Value.Number);   // the run itself has it
            Assert.Equal(FormulaMasking.HiddenText, v.ResultText);
            Assert.DoesNotContain(DoubleText, v.ResultText + v.WorkingText + v.Status);
        }
    }

    [Fact]
    public void Hotseat_result_hidden_even_when_every_output_visible()
    {
        // B7 round 2: Blue's own unit receives the result, but a masked formula never previews it before Apply (the ledger shows it after).
        var s = Open();
        s.SetViewSides(Blue);
        var m = Dialog(s, "f-to-water");
        Assert.NotEmpty(m.Hidden);
        var v = m.Evaluate(s.Game!.State);
        Assert.Equal(Secret * 2, v.Trace.Result!.Value.Number);
        Assert.Equal(FormulaMasking.HiddenText, v.ResultText);
        Assert.Equal(FormulaMasking.WorkingHiddenText, v.WorkingText);
        Assert.True(v.CanApply);
    }

    [Fact]
    public void Hotseat_result_hidden_when_visible_write_has_own_expr()
    {
        // The write is visible but writes 1, not the result: the ledger never shows the result, so the box does not either.
        var s = Open();
        s.SetViewSides(Blue);
        var m = Dialog(s, "f-own-expr");
        Assert.NotEmpty(m.Hidden);
        var v = m.Evaluate(s.Game!.State);
        Assert.Equal(Secret * 2, v.Trace.Result!.Value.Number);
        Assert.True(FormulaMasking.OutputShown(s, Blue, Assert.Single(v.Trace.Outputs)));
        Assert.Equal(FormulaMasking.HiddenText, v.ResultText);
        Assert.DoesNotContain(DoubleText, v.ResultText + v.WorkingText + v.Status);
    }

    [Fact]
    public void Hotseat_result_hidden_without_outputs()
    {
        var s = Open();
        s.SetViewSides(Blue);
        var m = Dialog(s, "f-no-output");
        Assert.NotEmpty(m.Hidden);
        var v = m.Evaluate(s.Game!.State);
        Assert.NotNull(v.Trace.Result);
        Assert.Equal(FormulaMasking.HiddenText, v.ResultText);
    }

    [Fact]
    public void Guest_masked_result_host_calculates()
    {
        var host = Open();
        var guest = Guest(host);
        foreach (var id in new[] { "f-to-water", "f-to-mines", "f-no-output" })
        {
            var m = Dialog(guest, id);
            Assert.NotEmpty(m.Hidden);
            Assert.True(m.HostCalculates);
            var v = m.Evaluate(guest.Game!.State);
            Assert.Equal(FormulaMasking.HostCalculatesText, v.ResultText);
        }
        // An unmasked formula on the guest shows its result as before.
        var plain = Dialog(guest, "f-water").Evaluate(guest.Game!.State);
        Assert.Equal("9", plain.ResultText);
    }

    [Fact]
    public void Batch_row_result_follows_dialog()
    {
        var fx = PhaseFixture.Module(numbered: true);
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "bcache"), Path.Combine(_tmp, "broot"));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.SemiAuto);
        s.NewRules("t");
        // The move formula reads the Private place and writes it back: masked for every viewer not holding both sides.
        s.UpdateRules(_ =>
        {
            var p = B7FormulaMaskingTests.Pack(s.Archive.Sha256);
            return p with
            {
                Formulas = p.Formulas.Select(f => f.Id != "f-fuel" ? f : f with
                {
                    Text = "keyed(Mines, hex()) * 2 + Moved * 0",
                    Bindings = new() { new VariableBinding { Variable = "Moved", Kind = BindingKind.Moved } },
                    Outputs = new() { B7FormulaMaskingTests.ToMines },
                }).ToList(),
            };
        });
        var c = new PhaseController(s, (cmd, _) => cmd);
        c.Start(); c.Advance(); c.Advance();   // Movement
        c.ExecuteMove(new[] { new MovePieces(new[] { PhaseFixture.RedOne }, "Main Map", new PointD(130, 50), null) });
        var hex = PieceInfo.Location(s.Game!.State.Pieces[PhaseFixture.RedOne], s.Model)!;
        s.Game.Execute(new LedgerDelta(B7FormulaMaskingTests.Global, "mines", 0, Secret, "manual", null, null, null, null) { ValueKey = hex });
        var run = s.Game.State.Pending.Single();
        var b = new BatchModel(s, c, run.Id);
        s.SetViewSides(Red);
        b.Invalidate();
        b.CalculateAll();
        var row = b.Row(0);
        // The same run in a dialog (checked, as the grid's rows are): the row shows the dialog's result text.
        var dialog = Dialog(s, "f-fuel", PhaseFixture.RedOne);
        dialog.Checked = true;
        dialog.Moved = run.Units[0].Moved;
        dialog.Calculate(s.Game.State);
        var dv = dialog.Evaluate(s.Game.State);
        Assert.Equal(Secret * 2, dv.Trace.Result!.Value.Number);
        Assert.Equal(FormulaMasking.HiddenText, dv.ResultText);
        Assert.Equal(dv.ResultText, row.Result);
        Assert.DoesNotContain(DoubleText, string.Join("|", row.Cells) + row.LedgerText + row.Exceptions);
        // Holding every side: nothing masked, the row shows the result.
        s.SetViewSides(new HashSet<string> { "red", "blue" });
        b.Invalidate();
        b.CalculateAll();
        Assert.Equal(DoubleText, b.Row(0).Result);
    }

    [Fact]
    public void Batch_row_same_whether_hidden_value_above_or_below_typed_threshold()
    {
        var fx = PhaseFixture.Module(numbered: true);
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "tcache"), Path.Combine(_tmp, "troot"));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.SemiAuto);
        s.NewRules("t");
        // The move compares the Private place with T and writes Red's own (visible) water.
        s.UpdateRules(_ =>
        {
            var p = B7FormulaMaskingTests.Pack(s.Archive.Sha256);
            return p with
            {
                Formulas = p.Formulas.Select(f => f.Id != "f-fuel" ? f : f with
                {
                    Text = "Moved * 0 + 10 - if(keyed(Mines, hex()) > T, 1000, 0)",
                    Bindings = new()
                    {
                        new VariableBinding { Variable = "Moved", Kind = BindingKind.Moved },
                        new VariableBinding { Variable = "T", Kind = BindingKind.Input },
                    },
                    Outputs = new() { B7FormulaMaskingTests.ToWater },
                }).ToList(),
            };
        });
        var c = new PhaseController(s, (cmd, _) => cmd);
        c.Start(); c.Advance(); c.Advance();   // Movement
        c.ExecuteMove(new[] { new MovePieces(new[] { PhaseFixture.RedOne }, "Main Map", new PointD(130, 50), null) });
        var hex = PieceInfo.Location(s.Game!.State.Pieces[PhaseFixture.RedOne], s.Model)!;
        s.Game.Execute(new LedgerDelta(B7FormulaMaskingTests.Global, "mines", 0, Secret, "manual", null, null, null, null) { ValueKey = hex });
        var b = new BatchModel(s, c, s.Game.State.Pending.Single().Id);
        s.SetViewSides(Red);
        b.Invalidate();
        b.CalculateAll();
        string Row(string threshold)
        {
            b.SetCell(0, "T", threshold);
            var r = b.Row(0);
            var cells = r.Cells.Where((_, k) => b.Variables[k] != "T");
            return $"{string.Join(",", cells)}|{string.Join(",", r.Wrong)}|{r.Result}|{r.LedgerText}|{r.Exceptions}|{r.CanApply}";
        }
        var below = Row("0");        // the place holds more: 10 - 1000, goes negative
        var above = Row("99999999");
        Assert.Equal(below, above);
        Assert.Contains("Water changes (hidden)", below);
        Assert.DoesNotContain("negative", below);
        Assert.DoesNotContain("-990", below);
    }

    [Fact]
    public void Solo_result_unchanged()
    {
        var s = Open();
        s.SetViewSides(null);
        foreach (var id in new[] { "f-to-mines", "f-to-water", "f-no-output" })
        {
            var m = Dialog(s, id);
            Assert.Empty(m.Hidden);
            Assert.Equal(DoubleText, m.Evaluate(s.Game!.State).ResultText);
        }
    }
}
