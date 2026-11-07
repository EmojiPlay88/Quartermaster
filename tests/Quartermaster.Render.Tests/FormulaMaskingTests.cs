using System.Globalization;
using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>
/// Formula input masking (spec 8a.2): hidden inputs in the formula dialog, the batch grid and the cheat sheet, for hot-seat, an online
/// guest and solo. Invented Red/Blue sides on the PhaseFixture module only.
/// </summary>
public class FormulaMaskingTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-mask-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    private const double RedSecret = 61616;
    private const double BlueSecret = 73737;
    private static readonly string RedText = RedSecret.ToString(CultureInfo.InvariantCulture);
    private static readonly string BlueText = BlueSecret.ToString(CultureInfo.InvariantCulture);
    private static readonly IReadOnlySet<string> Red = new HashSet<string> { "red" };
    private static readonly IReadOnlySet<string> Blue = new HashSet<string> { "blue" };
    private static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });
    private static readonly PlayerDef Bob = new("p2", "Bob", new[] { "blue" });

    public FormulaMaskingTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static RulesPack Pack(string sha)
    {
        var p = PhaseFixture.Pack(sha);
        return p with
        {
            Format = 4,
            Properties = p.Properties.Concat(new[]
            {
                new PropertyDef { Id = "supply", Name = "Supply", Scope = PropertyScope.Holders, Visibility = Visibility.Private },
            }).ToList(),
            Holders = new()
            {
                new HolderDef { Id = "reddepot", Name = "Red Depot", SideId = "red" },
                new HolderDef { Id = "bluedepot", Name = "Blue Depot", SideId = "blue" },
            },
            Formulas = p.Formulas.Select(f => f.Id != "f-fuel" ? f : f with
            {
                // Red's movement also reads Blue's private depot (times zero, so the fixture's fuel numbers stay the same).
                Text = "Moved * lookup(TestTerrainCost, Terrain) + S * 0",
                Bindings = f.Bindings.Concat(new[]
                {
                    new VariableBinding { Variable = "S", Kind = BindingKind.Holder, HolderId = "bluedepot", Property = "supply" },
                }).ToList(),
            }).Concat(new[]
            {
                new FormulaDef
                {
                    Id = "f-mix", Name = "Mixed supply", Text = "W + S",
                    Bindings = new()
                    {
                        new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" },
                        new VariableBinding { Variable = "S", Kind = BindingKind.Holder, HolderId = "reddepot", Property = "supply" },
                    },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
                },
            }).ToList(),
        };
    }

    private (ModuleSession S, PhaseController C) Open(Difficulty d, IReadOnlyList<PlayerDef>? players = null)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], d, players);
        s.NewRules("t");
        s.UpdateRules(_ => Pack(s.Archive.Sha256));
        s.Game!.Execute(new LedgerDelta(LedgerKey.Holder("reddepot"), "supply", 0, RedSecret, "manual", null, null, null, null));
        s.Game!.Execute(new LedgerDelta(LedgerKey.Holder("bluedepot"), "supply", 0, BlueSecret, "manual", null, null, null, null));
        return (s, new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s)));
    }

    /// <summary>Every text the dialog shows for this view: boxes, tooltips, captions, result, working, status, inline rolls.</summary>
    private static string Shown(DialogView v) =>
        string.Join("|", v.Boxes.SelectMany(b => new[] { b.Text, b.Tooltip ?? "", b.CaptionSuffix }))
        + "|" + v.ResultText + "|" + v.WorkingText + "|" + v.Status + "|" + string.Join("|", v.InlineRolls.Select(r => r.Text));

    private static string Shown(IReadOnlyList<CheatRow> rows) =>
        string.Join("|", rows.Select(r => r.Variable + "|" + r.Source + "|" + r.ValueText));

    private static string Shown(BatchRowView r) =>
        string.Join("|", r.Cells) + "|" + r.Label + "|" + r.Result + "|" + r.LedgerText + "|" + r.Exceptions;

    private static FormulaDialogModel Dialog(ModuleSession s, string formulaId, string? pieceId, bool check)
    {
        var m = new FormulaDialogModel(s.Runner!, s.Rules!.ById<FormulaDef>(formulaId)!, pieceId) { Checked = check };
        FormulaMasking.Configure(m, s);
        return m;
    }

    [Fact]
    public void HotSeat_HiddenInput_ShownAsHidden_NotInCheatSheet()
    {
        var (s, c) = Open(Difficulty.SemiAuto, new[] { Alice, Bob });
        s.SetViewSides(Blue);   // Bob's turn at the shared screen: Red's depot is private
        var g = s.Game!;

        var m = Dialog(s, "f-mix", PhaseFixture.BlueOne, check: true);
        Assert.Equal(new[] { "S" }, m.Hidden.ToArray());
        Assert.False(m.HostCalculates);
        m.SetBoxText("S", "1");   // a hidden box can't be typed into
        m.SetBoxText("W", "999");
        var wrong = m.Evaluate(g.State);
        var s1 = wrong.Boxes.Single(b => b.Variable == "S");
        Assert.Equal(FormulaMasking.HiddenText, s1.Text);
        Assert.False(s1.Enabled);
        Assert.Null(s1.Tooltip);
        Assert.False(s1.Wrong);
        Assert.True(wrong.Boxes.Single(b => b.Variable == "W").Wrong);
        Assert.Equal("Doesn't match: W", wrong.Status);
        Assert.DoesNotContain(RedText, Shown(wrong));

        // Calculate runs locally (this PC is the host) but never puts the hidden value in a box, the working or the status.
        m.Calculate(g.State);
        var calc = m.Evaluate(g.State);
        Assert.True(calc.CanApply, calc.Status);
        Assert.Equal(FormulaMasking.HiddenText, calc.Boxes.Single(b => b.Variable == "S").Text);
        Assert.Equal("10", calc.Boxes.Single(b => b.Variable == "W").Text);
        Assert.Equal(FormulaMasking.WorkingHiddenText, calc.WorkingText);
        Assert.DoesNotContain(RedText, Shown(calc));
        Assert.Contains(RedText, calc.Trace.Working);   // the run itself read it

        // The cheat sheet leaves the hidden input out entirely.
        var sheet = CheatSheetModel.Build(m.LastExpected!, s.Rules!, m.Hidden);
        Assert.DoesNotContain(sheet, r => r.Variable == "S");
        Assert.Contains(sheet, r => r.Variable == "W");
        Assert.DoesNotContain(RedText, Shown(sheet));

        // Typing only the visible box applies, with the hidden one read by the engine.
        var typed = Dialog(s, "f-mix", PhaseFixture.BlueOne, check: true);
        typed.SetBoxText("W", "10");
        var ok = typed.Evaluate(g.State);
        Assert.True(ok.CanApply, ok.Status);
        Assert.True(c.ApplyFormula(null, PhaseFixture.BlueOne, ok.Trace, false));
        Assert.Equal(10 + RedSecret, Ledger.Get(g.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));

        // Fully automatic: the prefilled box is masked too, and an evaluation error never quotes a value.
        var auto = new FormulaDialogModel(s.Runner!, s.Rules!.ById<FormulaDef>("f-mix")!, PhaseFixture.BlueOne);
        FormulaMasking.Configure(auto, s);
        var av = auto.Evaluate(g.State);
        Assert.Equal(FormulaMasking.HiddenText, av.Boxes.Single(b => b.Variable == "S").Text);
        Assert.Equal(FormulaMasking.WorkingHiddenText, av.WorkingText);
        Assert.DoesNotContain(RedText, Shown(av));

        // The host's intent runner masks exactly the same inputs.
        var verdict = new LocalActions(s, c, new HistoryController(s, c, (cmd, st) => cmd)).ApplyFormula(
            new FormulaIntent("f-mix", PhaseFixture.BlueOne, null, new Dictionary<string, string> { ["W"] = "1" }, false));
        Assert.Equal(m.Hidden.ToArray(), verdict.Boxes.Where(b => b.State == FormulaBox.Hidden).Select(b => b.Var).ToArray());

        // Red's own view: nothing hidden.
        s.SetViewSides(Red);
        Assert.Empty(FormulaMasking.HiddenFor(s, s.Rules!.ById<FormulaDef>("f-mix")!, PhaseFixture.RedOne));
    }

    [Fact]
    public void HotSeat_BatchGrid_HiddenCells_NeverShowValue()
    {
        var (s, c) = Open(Difficulty.SemiAuto);
        s.SetViewSides(Red);   // Red moves; its movement reads Blue's depot
        c.Start(); c.Advance(); c.Advance();   // Movement
        c.ExecuteMove(new[]
        {
            new MovePieces(new[] { PhaseFixture.RedOne }, "Main Map", new PointD(130, 50), null),
            new MovePieces(new[] { "r2" }, "Main Map", new PointD(180, 50), null),
        });
        var run = s.Game!.State.Pending.Single();
        var b = new BatchModel(s, c, run.Id);
        var col = b.Variables.ToList().IndexOf("S");
        Assert.True(col >= 0);
        b.CalculateAll();
        for (var i = 0; i < b.RowCount; i++)
        {
            var row = b.Row(i);
            Assert.Equal(FormulaMasking.HiddenText, row.Cells[col]);
            Assert.DoesNotContain(BlueText, Shown(row));
            var m = b.ModelFor(i);
            Assert.DoesNotContain(BlueText, Shown(CheatSheetModel.Build(m.LastExpected!, s.Rules!, m.Hidden)));
            Assert.DoesNotContain(CheatSheetModel.Build(m.LastExpected!, s.Rules!, m.Hidden), r => r.Variable == "S");
        }
        b.SetCell(0, "S", "5");   // ignored: a hidden cell takes no typing
        Assert.Equal(FormulaMasking.HiddenText, b.Row(0).Cells[col]);
        Assert.Equal(2, b.ApplyAll());   // the hidden input is read by the engine
    }

    [Fact]
    public async Task Guest_HardcoreTyped_VerdictFromHost_NoHiddenValues()
    {
        // The host's game (full state) and a guest holding Blue that has only its filtered snapshot and the rules pack.
        var (host, hc) = Open(Difficulty.Hardcore, new[] { Alice, Bob });
        var executor = new IntentExecutor(host, hc);
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var guest = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "gcache"), Path.Combine(_tmp, "groot"));
        _sessions.Add(guest);
        guest.EnterGuestMode();
        guest.LoadGuestRules(host.Rules!);
        guest.ApplySnapshot(PlayerView.For(host.Game!.State, host.Rules, host.Model, Blue), Blue);
        Assert.DoesNotContain(RedText, JsonSerializer.Serialize(guest.Game!.State, QuartermasterJson.Options));

        var sent = new List<FormulaIntent>();
        var port = new HostPort(intent =>
        {
            sent.Add(intent);
            var r = executor.Execute("p2", Blue, intent);
            // Over the wire: the verdict travels as JSON.
            var json = JsonSerializer.Serialize(r.Verdict ?? FormulaVerdict.Refused(r.Message ?? ""), QuartermasterJson.Options);
            Assert.DoesNotContain(RedText, json);
            return JsonSerializer.Deserialize<FormulaVerdict>(json, QuartermasterJson.Options)!;
        });

        var m = Dialog(guest, "f-mix", PhaseFixture.BlueOne, check: true);
        Assert.True(m.HostCalculates);
        Assert.Equal(new[] { "S" }, m.Hidden.ToArray());
        Assert.Equal(host.Rules!.ById<FormulaDef>("f-mix")!.Bindings.Count, m.Formula.Bindings.Count);
        var before = m.Evaluate(guest.Game!.State);
        Assert.Equal(FormulaMasking.HostCalculatesText, before.Boxes.Single(b => b.Variable == "S").Text);
        Assert.False(before.Boxes.Single(b => b.Variable == "S").Enabled);

        // Hardcore: Bob types a wrong visible value; the host checks it and answers with states only.
        m.SetBoxText("W", "999");
        m.SetBoxText("S", "1");
        var v1 = await FormulaMasking.SendAsync(port, m, null, calculate: false);
        Assert.False(v1.Applied);
        Assert.Equal(new Dictionary<string, string> { ["W"] = "999" }, sent[^1].Typed);   // a hidden box is never sent
        Assert.Equal(FormulaBox.Wrong, m.VerdictState("W"));
        var shown = m.Evaluate(guest.Game!.State);
        Assert.True(shown.Boxes.Single(b => b.Variable == "W").Wrong);
        Assert.Equal(FormulaMasking.HostCalculatesText, shown.Boxes.Single(b => b.Variable == "S").Text);
        Assert.DoesNotContain(RedText, Shown(shown));
        Assert.Null(Ledger.Get(host.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));   // nothing applied

        // Fixing the box clears the host's mark; Apply goes to the host, which fills the hidden input and applies.
        m.SetBoxText("W", "10");
        Assert.Null(m.VerdictState("W"));
        var v2 = await FormulaMasking.SendAsync(port, m, null, calculate: false);
        Assert.True(v2.Applied, v2.Message);
        Assert.Equal(10 + RedSecret, Ledger.Get(host.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
        Assert.DoesNotContain(RedText, Shown(m.Evaluate(guest.Game!.State)));

        // A host that never answers: the dialog says so.
        var silent = new HostPort(_ => FormulaVerdict.Refused(GuestSession.NoAnswerText));
        await FormulaMasking.SendAsync(silent, m, null, calculate: true);
        Assert.Equal(GuestSession.NoAnswerText, m.Evaluate(guest.Game!.State).Status);
    }

    [Fact]
    public void Solo_NoMasking()
    {
        var (s, _) = Open(Difficulty.FullyAuto);
        Assert.Null(s.ViewSides);
        Assert.Null(FormulaMasking.ViewerSides(s));
        var f = s.Rules!.ById<FormulaDef>("f-mix")!;
        Assert.Empty(FormulaMasking.HiddenFor(s, f, PhaseFixture.BlueOne));

        var plain = new FormulaDialogModel(s.Runner!, f, PhaseFixture.BlueOne);
        var masked = new FormulaDialogModel(s.Runner!, f, PhaseFixture.BlueOne);
        FormulaMasking.Configure(masked, s);
        var a = plain.Evaluate(s.Game!.State);
        var b = masked.Evaluate(s.Game!.State);
        Assert.Equal(Shown(a), Shown(b));
        Assert.Equal(RedText, b.Boxes.Single(x => x.Variable == "S").Text);
        Assert.Contains(RedText, b.WorkingText);

        var check = Dialog(s, "f-mix", PhaseFixture.BlueOne, check: true);
        check.Calculate(s.Game!.State);
        check.Evaluate(s.Game!.State);
        var sheet = CheatSheetModel.Build(check.LastExpected!, s.Rules!, check.Hidden);
        Assert.Contains(sheet, r => r.Variable == "S" && r.ValueText == RedText);
    }

    /// <summary>A port whose formula intents are answered by the given host function; nothing else is used by these tests.</summary>
    private sealed class HostPort(Func<FormulaIntent, FormulaVerdict> host) : IActionPort
    {
        public bool IsRemote => true;
        public FormulaVerdict ApplyFormula(FormulaIntent intent) => host(intent);
        public async Task<FormulaVerdict> ApplyFormulaAsync(FormulaIntent intent)
        {
            await Task.Yield();
            return host(intent);
        }
        public void Move(IReadOnlyList<MovePieces> moves) => throw new NotSupportedException();
        public void PieceCommand(GameCommand command) => throw new NotSupportedException();
        public void PieceCommands(IReadOnlyList<GameCommand> commands) => throw new NotSupportedException();
        public bool ApplyTrace(string? runId, string? pieceId, FormulaTrace trace, bool calculated) => throw new NotSupportedException();
        public (int Applied, IReadOnlyList<string> Failed) ApplyMissingInputs(PendingRun run, IReadOnlyDictionary<string, string> values) =>
            throw new NotSupportedException();
        public void EditLedger(string key, string propertyId, double after, string note) => throw new NotSupportedException();
        public void Roll(DiceSpec spec, string purpose) => throw new NotSupportedException();
        public void RecordRoll(RollDice roll) => throw new NotSupportedException();
        public void Tick(int index, bool ticked) => throw new NotSupportedException();
        public void Ack(int warningIndex) => throw new NotSupportedException();
        public string? NextPhase() => throw new NotSupportedException();
        public void HideAlert(string alertId, string? name) => throw new NotSupportedException();
        public void Chat(string text) => throw new NotSupportedException();
        public void SetTurn(int turn, string? phase) => throw new NotSupportedException();
        public void SetNotes(string text) => throw new NotSupportedException();
        public void Undo() => throw new NotSupportedException();
        public void Redo() => throw new NotSupportedException();
        public void StepBack(CheckpointRow row) => throw new NotSupportedException();
    }
}
