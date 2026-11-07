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
                    Id = "f-dice", Name = "Supply roll", Text = "W + R + S",
                    Bindings = new()
                    {
                        new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" },
                        new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 },
                        new VariableBinding { Variable = "S", Kind = BindingKind.Holder, HolderId = "reddepot", Property = "supply" },
                    },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
                },
                new FormulaDef
                {
                    Id = "f-dice2", Name = "Second roll", Text = "W + R",
                    Bindings = new()
                    {
                        new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" },
                        new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 },
                    },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
                },
                new FormulaDef
                {
                    Id = "f-tab", Name = "Terrain roll", Text = "R + T",
                    Bindings = new()
                    {
                        new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 },
                        new VariableBinding { Variable = "T", Kind = BindingKind.Table, TableId = "t-cost", RowExpr = "\"Clear\"" },
                    },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Add } },
                },
                new FormulaDef
                {
                    Id = "f-n", Name = "Variable dice", Text = "dice(N, 6)",
                    Bindings = new() { new VariableBinding { Variable = "N", Kind = BindingKind.Input, Min = 1, Max = 3 } },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
                },
                new FormulaDef
                {
                    // Writes Blue's private depot below zero: Red's dialog must not mark it negative.
                    Id = "f-drain", Name = "Drain", Text = "W - 1000000",
                    Bindings = new() { new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" } },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Holder, HolderId = "bluedepot", Property = "supply", Op = OutputOp.Set } },
                },
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

    private static string Json(GameState st) => JsonSerializer.Serialize(st, QuartermasterJson.Options);

    /// <summary>A guest holding Blue: only its filtered snapshot of the host's game and the rules pack.</summary>
    private ModuleSession Guest(ModuleSession host)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var guest = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "gcache" + _sessions.Count), Path.Combine(_tmp, "groot" + _sessions.Count));
        _sessions.Add(guest);
        guest.EnterGuestMode();
        guest.LoadGuestRules(host.Rules!);
        guest.ApplySnapshot(PlayerView.For(host.Game!.State, host.Rules, host.Model, Blue), Blue);
        return guest;
    }

    /// <summary>A remote port answered by the host's executor for Bob ("p2"), the verdict travelling as JSON.</summary>
    private static HostPort PortTo(IntentExecutor executor) => new(intent =>
    {
        var r = executor.Execute("p2", Blue, intent);
        var json = JsonSerializer.Serialize(r.Verdict ?? FormulaVerdict.Refused(r.Message ?? ""), QuartermasterJson.Options);
        Assert.DoesNotContain(RedText, json);
        return JsonSerializer.Deserialize<FormulaVerdict>(json, QuartermasterJson.Options)!;
    });

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

    [Fact]
    public async Task Guest_Calculate_IsPreview_SameDice_ApplyUsesPinnedDice()
    {
        var (host, hc) = Open(Difficulty.Hardcore, new[] { Alice, Bob });
        var next = 0;
        host.SetRoller((n, _) => Enumerable.Range(0, n).Select(_ => next++ % 6 + 1).ToList());   // every roll differs from the last
        var executor = new IntentExecutor(host, hc);
        var guest = Guest(host);
        var port = PortTo(executor);
        var m = Dialog(guest, "f-dice", PhaseFixture.BlueOne, check: true);
        var first = m.Evaluate(guest.Game!.State);
        Assert.Equal(FormulaDialogModel.HostRollsText, first.Boxes.Single(b => b.Variable == "R").Text);   // no local dice for a guest
        Assert.Equal("", first.ResultText);

        // Calculate is a preview: nothing executes on the host; the visible boxes fill as in solo.
        var before = Json(host.Game!.State);
        var p1 = await FormulaMasking.SendAsync(port, m, null, calculate: true, preview: true);
        Assert.False(p1.Applied);
        Assert.Equal(before, Json(host.Game!.State));
        Assert.NotNull(p1.Values);
        Assert.False(p1.Values!.ContainsKey("S"));
        Assert.Equal("10", p1.Values["W"]);
        Assert.DoesNotContain(RedText, string.Join("|", p1.Values.Values));
        var roll = Assert.Single(p1.Rolls!["R"]);
        var shown = m.Evaluate(guest.Game!.State);
        Assert.Equal("10", shown.Boxes.Single(b => b.Variable == "W").Text);
        Assert.Equal(roll.ToString(CultureInfo.InvariantCulture), shown.Boxes.Single(b => b.Variable == "R").Text);
        Assert.Equal(FormulaMasking.HostCalculatesText, shown.Boxes.Single(b => b.Variable == "S").Text);
        Assert.Null(shown.Boxes.Single(b => b.Variable == "R").RerollKey);
        Assert.DoesNotContain(RedText, Shown(shown));

        // Calculate again: the same roll, no fishing.
        var p2 = await FormulaMasking.SendAsync(port, m, null, calculate: true, preview: true);
        Assert.Equal(roll, Assert.Single(p2.Rolls!["R"]));
        Assert.Equal(before, Json(host.Game!.State));
        Assert.Equal(1, executor.Pins.Count("p2"));

        // Apply uses the pinned roll (once).
        var applied = await FormulaMasking.SendAsync(port, m, null, calculate: false);
        Assert.True(applied.Applied, applied.Message);
        Assert.Equal(10 + roll + RedSecret, Ledger.Get(host.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
        Assert.Equal(new[] { roll }, host.Game!.State.DiceHistory.Last().Results);
        Assert.Equal(0, executor.Pins.Count("p2"));
    }

    /// <summary>A host for Bob's intents whose roller never repeats a value in a row and records every die it rolled.</summary>
    private (ModuleSession S, PhaseController C, IntentExecutor X, List<int> Rolled) DiceHost(Func<RulesPack, RulesPack>? tweak = null)
    {
        var (host, hc) = Open(Difficulty.Hardcore, new[] { Alice, Bob });
        if (tweak != null) host.UpdateRules(tweak);
        var rolled = new List<int>();
        var next = 0;
        host.SetRoller((n, _) =>
        {
            var dice = Enumerable.Range(0, n).Select(_ => next++ % 6 + 1).ToList();
            rolled.AddRange(dice);
            return dice;
        });
        return (host, hc, new IntentExecutor(host, hc), rolled);
    }

    private static FormulaVerdict Send(IntentExecutor x, string formulaId, IReadOnlyDictionary<string, string>? typed, bool calculate, bool preview,
        string? runId = null) =>
        x.Execute("p2", Blue, new FormulaIntent(formulaId, PhaseFixture.BlueOne, runId, typed ?? new Dictionary<string, string>(), calculate, preview)).Verdict!;

    private static IReadOnlyList<int> PreviewRoll(IntentExecutor x, string formulaId, string key, IReadOnlyDictionary<string, string>? typed = null) =>
        Send(x, formulaId, typed, calculate: true, preview: true).Rolls![key];

    [Fact]
    public async Task Pins_Full_NoEviction_SameRoll_NewPreviewRefused()
    {
        var (host, _, executor, _) = DiceHost();
        var guest = Guest(host);
        var port = PortTo(executor);
        var a = Dialog(guest, "f-dice", PhaseFixture.BlueOne, check: true);
        var roll = Assert.Single((await FormulaMasking.SendAsync(port, a, null, calculate: true, preview: true)).Rolls!["R"]);
        // Fill the table with other scopes (as 511 other units would).
        for (var i = 1; i < FormulaPins.MaxPerSender; i++)
            Assert.NotNull(executor.Pins.Open(new FormulaPins.Key("p2", "f-dice", "x" + i), host.Game!, host.Rules!, mayRoll: true));
        Assert.Equal(FormulaPins.MaxPerSender, executor.Pins.Count("p2"));
        // A still re-shows its same roll; a new preview is refused before rolling, nothing evicted, nothing executed.
        Assert.Equal(roll, Assert.Single((await FormulaMasking.SendAsync(port, a, null, calculate: true, preview: true)).Rolls!["R"]));
        var before = Json(host.Game!.State);
        var b = Dialog(guest, "f-dice2", PhaseFixture.BlueOne, check: true);
        var refused = await FormulaMasking.SendAsync(port, b, null, calculate: true, preview: true);
        Assert.False(refused.Applied);
        Assert.Equal(FormulaPins.FullText, refused.Message);
        Assert.Null(refused.Rolls);
        Assert.Equal(before, Json(host.Game!.State));
        Assert.Equal(FormulaPins.MaxPerSender, executor.Pins.Count("p2"));
        Assert.Equal(roll, Assert.Single((await FormulaMasking.SendAsync(port, a, null, calculate: true, preview: true)).Rolls!["R"]));
        // A formula without dice needs no pin, so it still previews.
        Assert.NotEqual(FormulaPins.FullText, Send(executor, "f-mix", null, calculate: true, preview: true).Message);
    }

    [Fact]
    public void Pins_RefusedApply_WrongTableValue_RetrySameRoll()
    {
        var (host, _, x, rolled) = DiceHost();
        var wrong = Send(x, "f-tab", new Dictionary<string, string> { ["T"] = "9" }, calculate: false, preview: false);
        Assert.False(wrong.Applied);
        Assert.Equal(FormulaBox.Wrong, wrong.Boxes.Single(b => b.Var == "T").State);
        var first = rolled.ToList();
        Assert.Single(first);   // the refused Apply rolled once, and that die is pinned
        for (var i = 0; i < 3; i++) Assert.False(Send(x, "f-tab", new Dictionary<string, string> { ["T"] = "8" }, false, false).Applied);
        var ok = Send(x, "f-tab", new Dictionary<string, string> { ["T"] = "2" }, calculate: false, preview: false);
        Assert.True(ok.Applied, ok.Message);
        Assert.Equal(first, rolled);   // never rolled again
        Assert.Equal(first, host.Game!.State.DiceHistory.Last().Results);
        Assert.Equal(10 + first[0] + 2, Ledger.Get(host.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
        Assert.Equal(0, x.Pins.Count("p2"));   // consumed by the Apply
    }

    [Fact]
    public void Pins_PerDiceConfig_InputToggleReturnsFirstRoll()
    {
        var (_, _, x, _) = DiceHost();
        var one = PreviewRoll(x, "f-n", "dice#0", new Dictionary<string, string> { ["N"] = "1" });
        var two = PreviewRoll(x, "f-n", "dice#0", new Dictionary<string, string> { ["N"] = "2" });
        Assert.Single(one);
        Assert.Equal(2, two.Count);
        Assert.Equal(one, PreviewRoll(x, "f-n", "dice#0", new Dictionary<string, string> { ["N"] = "1" }));
        Assert.Equal(two, PreviewRoll(x, "f-n", "dice#0", new Dictionary<string, string> { ["N"] = "2" }));
    }

    [Fact]
    public void Pins_AdHocPreview_ThenPendingRunApply_SameRoll()
    {
        var blue = new PhaseDef
        {
            Id = "ph-blue", Name = "Blue roll", SideId = "blue",
            OnStart = new() { new StartEntry { FormulaId = "f-dice2", ForEach = ForEachMode.Units } },
        };
        var (host, hc, x, _) = DiceHost(p => p with { Phases = new[] { blue }.Concat(p.Phases).ToList() });
        hc.Start();
        var run = host.Game!.State.Pending.Single(r => r.FormulaId == "f-dice2" && r.Units.Any(u => u.PieceId == PhaseFixture.BlueOne));
        var roll = Assert.Single(PreviewRoll(x, "f-dice2", "R"));   // ad hoc (no run id)
        var ok = Send(x, "f-dice2", new Dictionary<string, string> { ["W"] = "10" }, calculate: false, preview: false, runId: run.Id);
        Assert.True(ok.Applied, ok.Message);
        Assert.Equal(new[] { roll }, host.Game!.State.DiceHistory.Last().Results);
        Assert.Equal(10 + roll, Ledger.Get(host.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
    }

    [Fact]
    public void Pins_PhaseAdvance_DropsPins()
    {
        var (host, hc, x, _) = DiceHost();
        hc.Start();
        var roll = PreviewRoll(x, "f-dice2", "R");
        Assert.Equal(roll, PreviewRoll(x, "f-dice2", "R"));
        hc.Advance();
        Assert.NotEqual(roll, PreviewRoll(x, "f-dice2", "R"));   // a new phase position: new dice
        Assert.Equal(1, x.Pins.Count("p2"));
        host.UpdateRules(p => p with { Name = "Edited" });
        Assert.NotNull(PreviewRoll(x, "f-dice2", "R"));
        Assert.Equal(1, x.Pins.Count("p2"));   // the old pack's scope was dropped
    }

    [Fact]
    public void Pins_NoTimeExpiry_SurviveUnrelatedChanges()
    {
        var (host, hc, x, _) = DiceHost();
        Assert.Null(typeof(FormulaPins).GetProperty("Clock", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static));   // no wall clock at all
        hc.Start();
        var roll = PreviewRoll(x, "f-dice2", "R");
        // Other things happen in the same phase: another formula, ledger edits, the host's own moves.
        Send(x, "f-mix", new Dictionary<string, string> { ["W"] = "10" }, calculate: false, preview: false);
        host.Game!.Execute(new LedgerDelta(LedgerKey.Holder("reddepot"), "supply", RedSecret, 1, "manual", null, null, null, null));
        Assert.Equal(roll, PreviewRoll(x, "f-dice2", "R"));
    }

    [Fact]
    public void HotSeat_BatchGrid_AppliedRows_HiddenAfterViewerSwitch()
    {
        var (s, c) = Open(Difficulty.SemiAuto);
        s.Game!.Execute(new LedgerDelta(LedgerKey.Piece(PhaseFixture.RedOne), "fuel", 0, 5150, "manual", null, null, null, null));
        s.SetViewSides(Red);
        c.Start(); c.Advance(); c.Advance();   // Movement
        c.ExecuteMove(new[]
        {
            new MovePieces(new[] { PhaseFixture.RedOne }, "Main Map", new PointD(130, 50), null),
            new MovePieces(new[] { "r2" }, "Main Map", new PointD(180, 50), null),
        });
        var b = new BatchModel(s, c, s.Game!.State.Pending.Single().Id);
        b.CalculateAll();
        Assert.Contains("5150", Shown(b.Row(0)));   // Red sees its own fuel
        Assert.True(b.ApplyRow(0));
        Assert.Contains("5150", Shown(b.Row(0)));   // the applied snapshot, still Red's view
        s.SetViewSides(Blue);
        for (var i = 0; i < b.RowCount; i++)
        {
            Assert.DoesNotContain("5150", Shown(b.Row(i)));
            Assert.DoesNotContain(BlueText, Shown(b.Row(i)));
        }
        Assert.All(b.Row(0).Cells, cell => Assert.Equal(ModuleSession.HiddenText, cell));
    }

    [Fact]
    public void HotSeat_Negative_OnlyForVisibleOutputs()
    {
        var (s, _) = Open(Difficulty.FullyAuto);
        var drain = s.Rules!.ById<FormulaDef>("f-drain")!;
        Assert.True(new FormulaDialogModel(s.Runner!, drain, PhaseFixture.RedOne).Evaluate(s.Game!.State).WorkingNegative);   // solo
        s.SetViewSides(Red);
        var red = new FormulaDialogModel(s.Runner!, drain, PhaseFixture.RedOne);
        FormulaMasking.Configure(red, s);
        var v = red.Evaluate(s.Game!.State);
        Assert.False(v.WorkingNegative);
        Assert.DoesNotContain("(negative)", v.WorkingText);
    }

    [Fact]
    public void AppDataRoot_WithOverride_NothingUnderTheProfile()
    {
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quartermaster");
        Assert.Equal(profile, AppDataRoot.Resolve(null));
        var old = Environment.GetEnvironmentVariable(AppDataRoot.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _tmp);
            foreach (var path in new[]
                     {
                         AppDataRoot.Path, AppDataRoot.Sub("webview2"), RulesMemory.DefaultRoot, SettingsStore.DefaultRoot, RejoinTokens.DefaultRoot,
                         ModuleSession.DefaultCacheRoot,
                     })
            {
                Assert.StartsWith(_tmp, path, StringComparison.OrdinalIgnoreCase);
                Assert.False(path.StartsWith(profile, StringComparison.OrdinalIgnoreCase), path);
            }
        }
        finally { Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, old); }
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
