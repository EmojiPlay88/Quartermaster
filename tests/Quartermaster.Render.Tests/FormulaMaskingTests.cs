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
[Collection(AppDataRootEnv.Name)]
public class FormulaMaskingTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-mask-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    // Fractional secrets (final review M9): a whole number such as 61616 can turn up by chance in a serialized timestamp's fraction or a
    // GUID; "61616.25" cannot (a timestamp's dot follows two digits, a GUID has none).
    private const double RedSecret = 61616.25;
    private const double BlueSecret = 73737.25;
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
    private ModuleSession Guest(ModuleSession host, bool hideable = false)
    {
        var fx = Module(hideable);
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

    /// <summary>The phase fixture module; with <paramref name="hideable"/>, its prototype carries a Hideable trait (any piece may be hidden).</summary>
    private static FixtureModule Module(bool hideable) => hideable
        ? PhaseFixture.Module().EditBuildFile(x => x.Replace("+/null/mark;Side/null",
            "+/null/" + PieceTypeParser.EncodeState(new[] { "hide;I;Hide it;;;0.3;;false", "mark;Side" }) + "/null"))
        : PhaseFixture.Module();

    private (ModuleSession S, PhaseController C) Open(Difficulty d, IReadOnlyList<PlayerDef>? players = null, bool hideable = false)
    {
        var fx = Module(hideable);
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

    /// <summary>
    /// Adds a bare-named Red holder holding the Red secret and two formulas that read it through holder() in their expressions, not
    /// through a binding: one that works and one whose lookup error would quote the value.
    /// </summary>
    private static void AddHolderCalls(ModuleSession s)
    {
        s.UpdateRules(p => p with
        {
            Holders = p.Holders.Append(new HolderDef { Id = "redstore", Name = "RedStore", SideId = "red" }).ToList(),
            Formulas = p.Formulas.Concat(new[]
            {
                new FormulaDef
                {
                    Id = "f-hcall", Name = "Store read", Text = "W + holder(RedStore, Supply)",
                    Bindings = new() { new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" } },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
                },
                new FormulaDef
                {
                    Id = "f-herr", Name = "Store lookup", Text = "W + T",
                    Bindings = new()
                    {
                        new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" },
                        // The row key is the hidden value: the lookup error ("No row for ...") would quote it.
                        new VariableBinding { Variable = "T", Kind = BindingKind.Table, TableId = "t-cost", RowExpr = "holder(RedStore, Supply)" },
                    },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
                },
            }).ToList(),
        });
        s.Game!.Execute(new LedgerDelta(LedgerKey.Holder("redstore"), "supply", 0, RedSecret, "manual", null, null, null, null));
    }

    [Fact]
    public void HotSeat_HolderCallInText_MasksErrorAndWorking_AndBlueViewDropsTheWorking()
    {
        var (s, c) = Open(Difficulty.SemiAuto, new[] { Alice, Bob });
        AddHolderCalls(s);
        s.SetViewSides(Blue);   // Bob at the shared screen: RedStore is Red's
        var g = s.Game!;

        // An error that would quote the hidden value is replaced; the table binding counts as hidden, as for a hidden Holder binding.
        var err = Dialog(s, "f-herr", PhaseFixture.BlueOne, check: false);
        Assert.Contains(FormulaMasking.HolderCallInput, err.Hidden);
        Assert.Contains("T", err.Hidden);
        var ev = err.Evaluate(g.State);
        Assert.Contains(RedText, ev.Trace.Error);   // the run itself read it (the test would prove nothing otherwise)
        Assert.Equal(FormulaMasking.ErrorHiddenText, ev.Status);
        Assert.DoesNotContain(RedText, Shown(ev));

        // A working that shows the value is replaced; applying it logs a working Blue's view never shows.
        var m = Dialog(s, "f-hcall", PhaseFixture.BlueOne, check: false);
        Assert.Equal(new[] { FormulaMasking.HolderCallInput }, m.Hidden.ToArray());
        var v = m.Evaluate(g.State);
        Assert.NotEqual("", v.Trace.Working);
        Assert.Equal(FormulaMasking.WorkingHiddenText, v.WorkingText);
        Assert.DoesNotContain(RedText, Shown(v));
        Assert.True(v.CanApply, v.Status);
        Assert.True(c.ApplyFormula(null, PhaseFixture.BlueOne, v.Trace, false));
        Assert.Contains(g.State.LedgerHistory, e => e.FormulaId == "f-hcall" && e.Working != null);
        var blue = PlayerView.For(g.State, s.Rules, s.Model, Blue);
        Assert.DoesNotContain(blue.LedgerHistory, e => e.FormulaId == "f-hcall" && e.Working != null);
        Assert.DoesNotContain(RedText, Json(blue));

        // Red's own view: nothing hidden; an unknown holder fails closed.
        s.SetViewSides(Red);
        Assert.Empty(FormulaMasking.HiddenFor(s, s.Rules!.ById<FormulaDef>("f-hcall")!, PhaseFixture.RedOne));
        var unknown = s.Rules!.ById<FormulaDef>("f-hcall")! with { Text = "W + holder(NoSuchStore, Supply)" };
        Assert.Contains(FormulaMasking.HolderCallInput, FormulaMasking.HiddenFor(s, unknown, PhaseFixture.RedOne));
    }

    [Fact]
    public void HotSeat_HolderCallInOutputExpression_MasksLikeTheText()
    {
        var (s, c) = Open(Difficulty.SemiAuto, new[] { Alice, Bob });
        AddHolderCalls(s);
        s.UpdateRules(p => p with
        {
            Formulas = p.Formulas.Select(f => f.Id != "f-hcall" ? f : f with
            {
                Text = "W",
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set, Expr = "W + holder(RedStore, Supply)" } },
            }).ToList(),
        });
        s.SetViewSides(Blue);
        var m = Dialog(s, "f-hcall", PhaseFixture.BlueOne, check: false);
        Assert.Equal(new[] { FormulaMasking.HolderCallInput }, m.Hidden.ToArray());
        var v = m.Evaluate(s.Game!.State);
        Assert.Equal(FormulaMasking.WorkingHiddenText, v.WorkingText);
        Assert.DoesNotContain(RedText, Shown(v));
        s.SetViewSides(Red);
        Assert.Empty(FormulaMasking.HiddenFor(s, s.Rules!.ById<FormulaDef>("f-hcall")!, PhaseFixture.RedOne));
    }

    [Fact]
    public async Task Guest_HolderCallInText_HostIntentMasksErrorAndWorking()
    {
        var (host, hc) = Open(Difficulty.SemiAuto, new[] { Alice, Bob });
        AddHolderCalls(host);
        var executor = new IntentExecutor(host, hc);
        var guest = Guest(host);
        var port = PortTo(executor);   // asserts no verdict carries the Red secret

        // The guest's own dialog masks from the pack alone, on its filtered state.
        var err = Dialog(guest, "f-herr", PhaseFixture.BlueOne, check: false);
        Assert.Contains(FormulaMasking.HolderCallInput, err.Hidden);
        // The host runs it on the full state and answers with the hidden-error text only.
        var direct = Send(executor, "f-herr", null, calculate: true, preview: false);
        Assert.False(direct.Applied);
        Assert.Equal(FormulaMasking.ErrorHiddenText, direct.Message);
        var v1 = await FormulaMasking.SendAsync(port, err, null, calculate: true);
        Assert.False(v1.Applied);
        Assert.DoesNotContain(RedText, Shown(err.Evaluate(guest.Game!.State)));

        // Apply through the host: the logged working that read RedStore is stripped from Blue's view (its next snapshot).
        var m = Dialog(guest, "f-hcall", PhaseFixture.BlueOne, check: false);
        var v2 = await FormulaMasking.SendAsync(port, m, null, calculate: true);
        Assert.True(v2.Applied, v2.Message);
        var full = host.Game!.State;
        Assert.Contains(full.LedgerHistory, e => e.FormulaId == "f-hcall" && e.Working != null);
        var blue = PlayerView.For(full, host.Rules, host.Model, Blue);
        Assert.DoesNotContain(blue.LedgerHistory, e => e.FormulaId == "f-hcall" && e.Working != null);
        Assert.DoesNotContain(RedText, Json(blue));
    }

    /// <summary>
    /// Adds a public piece property (Morale) and formulas on a unit that read every piece through an aggregate: over the private Water
    /// (f-asum), a count (f-acount), over the public Morale (f-apub) and over Morale filtered on the private Water (f-apubf). Red 1's water
    /// is set to the Red secret, so f-asum on Blue 1 gives 10 + (secret + 10 + 10): its working line (which ends in the result) reads it.
    /// </summary>
    private static void AddAggregates(ModuleSession s)
    {
        FormulaDef F(string id, string name, string text) => new()
        {
            Id = id, Name = name, Text = text,
            Bindings = new() { new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" } },
            Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
        };
        s.UpdateRules(p => p with
        {
            Properties = p.Properties.Append(new PropertyDef
                { Id = "morale", Name = "Morale", Scope = PropertyScope.Pieces, Visibility = Visibility.Public, Default = 5 }).ToList(),
            Formulas = p.Formulas.Concat(new[]
            {
                F("f-asum", "Water total", "W + sum(Water, \"Water >= 0\")"),
                F("f-acount", "Wet count", "W + countof(\"Water > 0\") * 0"),
                F("f-apub", "Morale total", "W + sum(Morale, \"Morale >= 0\") * 0"),
                F("f-apubf", "Wet morale", "W + sum(Morale, \"Water > 0\") * 0"),
            }).ToList(),
        });
        s.Game!.Execute(new LedgerDelta(LedgerKey.Piece(PhaseFixture.RedOne), "water", 10, RedSecret, "manual", null, null, null, null));
    }

    private static readonly string WaterSumText = (RedSecret + 30).ToString(CultureInfo.InvariantCulture);

    [Fact]
    public async Task Guest_aggregate_masked()
    {
        var (host, hc) = Open(Difficulty.SemiAuto, new[] { Alice, Bob });
        AddAggregates(host);
        var executor = new IntentExecutor(host, hc);
        var guest = Guest(host);
        var port = PortTo(executor);

        // A private property, a count, and a public property filtered on a private one: all read hidden data.
        foreach (var id in new[] { "f-asum", "f-acount", "f-apubf" })
            Assert.Equal(new[] { FormulaMasking.AggregateCallInput }, Dialog(guest, id, PhaseFixture.BlueOne, check: false).Hidden.ToArray());

        // The host runs it on the full state; the guest never sees the working, nor Blue's next snapshot.
        var m = Dialog(guest, "f-asum", PhaseFixture.BlueOne, check: false);
        var v = await FormulaMasking.SendAsync(port, m, null, calculate: true);
        Assert.True(v.Applied, v.Message);
        var full = host.Game!.State;
        Assert.Contains(full.LedgerHistory, e => e.FormulaId == "f-asum" && e.Working != null && e.Working.Contains(WaterSumText));
        var blue = PlayerView.For(full, host.Rules, host.Model, Blue);
        Assert.DoesNotContain(blue.LedgerHistory, e => e.FormulaId == "f-asum" && e.Working != null);

        // A guard over an aggregate is decided by the host once an aggregate reads hidden data.
        var g = new FormulaDef
        {
            Id = "f", Name = "F", Text = "A + B", Precondition = "countof(\"Water > 0\") > 0",
            Bindings = new()
            {
                new VariableBinding { Variable = "A", Kind = BindingKind.Input, When = "hexcount() > 1" },
                new VariableBinding { Variable = "B", Kind = BindingKind.Input, When = "B2 > 0" },
                new VariableBinding { Variable = "B2", Kind = BindingKind.Input, AutoValue = "sum(Water, \"Water > 0\")" },
            },
        };
        Assert.Empty(FormulaMasking.GuardsOnHidden(g, new HashSet<string> { FormulaMasking.HolderCallInput }));
        Assert.Equal(new[] { "A", "B", FormulaMasking.PreconditionGuard }.OrderBy(x => x, StringComparer.Ordinal),
            FormulaMasking.GuardsOnHidden(g, new HashSet<string> { FormulaMasking.AggregateCallInput }).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Guest_formula_reading_public_computed_from_private_is_masked()
    {
        // Thirst is declared Public but computed from the Private Water; Spirit is computed from the Public Morale only.
        var (host, _) = Open(Difficulty.SemiAuto, new[] { Alice, Bob });
        FormulaDef F(string id, string prop) => new()
        {
            Id = id, Name = id, Text = "W + 1",
            Bindings = new() { new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = prop } },
        };
        host.UpdateRules(p => p with
        {
            Properties = p.Properties.Concat(new[]
            {
                new PropertyDef { Id = "morale", Name = "Morale", Scope = PropertyScope.Pieces, Visibility = Visibility.Public, Default = 5 },
                new PropertyDef { Id = "thirst", Name = "Thirst", Scope = PropertyScope.Pieces, Visibility = Visibility.Public, Expr = "Water * 2" },
                new PropertyDef { Id = "spirit", Name = "Spirit", Scope = PropertyScope.Pieces, Visibility = Visibility.Public, Expr = "Morale + 1" },
            }).ToList(),
            Formulas = p.Formulas.Concat(new[] { F("f-thirst", "thirst"), F("f-spirit", "spirit") }).ToList(),
        });
        var guest = Guest(host);
        Assert.Equal(new[] { "W" }, Dialog(guest, "f-thirst", PhaseFixture.RedOne, check: false).Hidden.ToArray());
        Assert.Empty(Dialog(guest, "f-spirit", PhaseFixture.RedOne, check: false).Hidden);
    }

    /// <summary>
    /// Adds Wetness, a Public global computed from every piece's Private Water, and Blue's "Wet gate" on its own unit: W + if(G &lt; 10, B, 0)
    /// with G the Global Wetness and B an Input asked only when G &lt; 10. Red 1's water is the Red secret, so Wetness is far above 10.
    /// </summary>
    private static void AddComputedGlobal(ModuleSession s)
    {
        s.UpdateRules(p => p with
        {
            Properties = p.Properties.Append(new PropertyDef
                { Id = "wetness", Name = "Wetness", Scope = PropertyScope.Holders, Visibility = Visibility.Public, Expr = "sum(Water, \"\")" }).ToList(),
            Formulas = p.Formulas.Append(new FormulaDef
            {
                Id = "f-gwet", Name = "Wet gate", Text = "W + if(G < 10, B, 0)",
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
                Bindings = new()
                {
                    new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" },
                    new VariableBinding { Variable = "G", Kind = BindingKind.Global, Property = "wetness" },
                    new VariableBinding { Variable = "B", Kind = BindingKind.Input, When = "G < 10" },
                },
            }).ToList(),
        });
        s.Game!.Execute(new LedgerDelta(LedgerKey.Piece(PhaseFixture.RedOne), "water", 10, RedSecret, "manual", null, null, null, null));
    }

    [Fact]
    public void HotSeat_global_bound_to_computed_from_hidden_is_masked()
    {
        var (s, _) = Open(Difficulty.SemiAuto, new[] { Alice, Bob });
        AddComputedGlobal(s);
        s.SetViewSides(Blue);
        var f = s.Rules!.ById<FormulaDef>("f-gwet")!;
        Assert.Contains("G", FormulaMasking.HiddenFor(s, f, PhaseFixture.BlueOne));
        Assert.DoesNotContain("W", FormulaMasking.HiddenFor(s, f, PhaseFixture.BlueOne));   // Blue's own water
        var dialog = Dialog(s, "f-gwet", PhaseFixture.BlueOne, check: false);
        dialog.SetBoxText("B", "7");   // B7: B's guard reads G, so B is always to be filled
        var v = dialog.Evaluate(s.Game!.State);
        Assert.Equal(FormulaMasking.HiddenText, v.Boxes.Single(b => b.Variable == "G").Text);
        Assert.DoesNotContain(RedText, Shown(v));
        Assert.Equal(FormulaMasking.WorkingHiddenText, v.WorkingText);
        // holder(Global, Wetness) in the text reads it as well.
        var call = f with { Text = "W + holder(Global, Wetness) * 0", Bindings = f.Bindings.Take(1).ToList() };
        Assert.NotEmpty(FormulaMasking.HiddenFor(s, call, PhaseFixture.BlueOne));
        // Holding every side shows everything.
        s.SetViewSides(new HashSet<string> { "red", "blue" });
        Assert.Empty(FormulaMasking.HiddenFor(s, f, PhaseFixture.BlueOne));
    }

    [Fact]
    public async Task Guest_global_bound_to_computed_from_hidden_host_decides()
    {
        var (host, hc) = Open(Difficulty.FullyAuto, new[] { Alice, Bob });
        AddComputedGlobal(host);
        var executor = new IntentExecutor(host, hc);
        var guest = Guest(host);
        var port = PortTo(executor);

        var m = Dialog(guest, "f-gwet", PhaseFixture.BlueOne, check: false);
        Assert.Contains("G", m.Hidden);
        var before = m.Evaluate(guest.Game!.State);
        Assert.All(before.Boxes.Where(b => b.Variable == "G"), b => Assert.Equal(FormulaMasking.HostCalculatesText, b.Text));
        var box = before.Boxes.Single(b => b.Variable == "B");
        Assert.NotEqual(FormulaDialogModel.NotUsedText, box.Text);   // the guest cannot decide G < 10
        Assert.True(box.Enabled);
        Assert.Equal("Waiting for: B", before.Status);   // B7: a guard over hidden data is always to be filled
        Assert.DoesNotContain("sum()", Shown(before));

        // B7: the host asks for B although it knows G is far above 10 (its answer never depends on the guard).
        var waiting = await FormulaMasking.SendAsync(port, m, null, calculate: false);
        Assert.False(waiting.Applied);
        Assert.Equal("Waiting for: B", waiting.Message);
        // With B typed the run applies, and B is not used.
        m.SetBoxText("B", "7");
        var applied = await FormulaMasking.SendAsync(port, m, null, calculate: false);
        Assert.True(applied.Applied, applied.Message);
        Assert.Contains(host.Game!.State.LedgerHistory, e => e.FormulaId == "f-gwet" && e.Working != null);
        var blue = PlayerView.For(host.Game!.State, host.Rules, host.Model, Blue);
        Assert.DoesNotContain(blue.LedgerHistory, e => e.FormulaId == "f-gwet" && e.Working != null);
        Assert.DoesNotContain(RedText, Json(blue));
    }

    [Fact]
    public void Host_aggregate_not_masked()
    {
        var (s, c) = Open(Difficulty.SemiAuto, new[] { Alice, Bob });
        AddAggregates(s);
        var both = new HashSet<string> { "red", "blue" };
        s.SetViewSides(both);   // the host holding every side sees everything
        foreach (var id in new[] { "f-asum", "f-acount", "f-apub", "f-apubf" })
            Assert.Empty(FormulaMasking.HiddenVariables(s.Rules!, s.Model, s.Game!.State, s.Rules!.ById<FormulaDef>(id)!, PhaseFixture.BlueOne, both));
        var m = Dialog(s, "f-asum", PhaseFixture.BlueOne, check: false);
        Assert.Empty(m.Hidden);
        var v = m.Evaluate(s.Game!.State);
        Assert.Contains(WaterSumText, v.WorkingText);
        Assert.True(c.ApplyFormula(null, PhaseFixture.BlueOne, v.Trace, false));
        var all = PlayerView.For(s.Game!.State, s.Rules, s.Model, both);
        Assert.Contains(all.LedgerHistory, e => e.FormulaId == "f-asum" && e.Working != null);
    }

    [Fact]
    public void Aggregate_public_property_sum_not_masked()
    {
        var (s, _) = Open(Difficulty.SemiAuto, new[] { Alice, Bob });
        AddAggregates(s);
        s.SetViewSides(Blue);
        Assert.Empty(FormulaMasking.HiddenFor(s, s.Rules!.ById<FormulaDef>("f-apub")!, PhaseFixture.BlueOne));
        var everyPiece = s.Rules!.ById<FormulaDef>("f-apub")! with { Text = "W + sum(Morale, \"\") * 0" };   // "" = every piece
        Assert.Empty(FormulaMasking.HiddenFor(s, everyPiece, PhaseFixture.BlueOne));
        // The same formula masks once a count appears anywhere in it, here in an output's value
        var counted = s.Rules!.ById<FormulaDef>("f-apub")! with
        {
            Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set, Expr = "W + hexcount() * 0" } },
        };
        Assert.Equal(new[] { FormulaMasking.AggregateCallInput }, FormulaMasking.HiddenFor(s, counted, PhaseFixture.BlueOne).ToArray());
    }

    [Fact]
    public void Guest_public_sum_not_masked_when_nothing_can_hide()
    {
        var (host, _) = Open(Difficulty.SemiAuto, new[] { Alice, Bob });
        AddAggregates(host);
        var guest = Guest(host);
        var m = Dialog(guest, "f-apub", PhaseFixture.BlueOne, check: false);
        Assert.Empty(m.Hidden);   // no owned map, no hide or mask trait: the guest's view holds every piece and Morale
        var v = m.Evaluate(guest.Game!.State);
        Assert.Null(v.Trace.Error);
        Assert.NotNull(v.Trace.Result);
        Assert.Contains("sum(Morale", v.WorkingText);
    }

    [Fact]
    public void Guest_public_sum_masked_when_pieces_may_hide()
    {
        var (host, _) = Open(Difficulty.SemiAuto, new[] { Alice, Bob }, hideable: true);
        AddAggregates(host);
        Assert.Contains(host.Model.Prototypes.Values, t => t.Traits.Any(x => x is HideableDef));
        var guest = Guest(host, hideable: true);
        foreach (var id in new[] { "f-apub", "f-asum" })
            Assert.Equal(new[] { FormulaMasking.AggregateCallInput }, Dialog(guest, id, PhaseFixture.BlueOne, check: false).Hidden.ToArray());
        // Hot seat: Blue's screen masks it too, and Blue's view never shows its working.
        host.SetViewSides(Blue);
        Assert.Equal(new[] { FormulaMasking.AggregateCallInput }, FormulaMasking.HiddenFor(host, host.Rules!.ById<FormulaDef>("f-apub")!, PhaseFixture.BlueOne).ToArray());
    }

    [Fact]
    public async Task Guest_WhenOverPublicSum_BoxTypeable_HostDecides_WhenPiecesMayHide()
    {
        var (host, hc) = Open(Difficulty.FullyAuto, new[] { Alice, Bob }, hideable: true);
        AddAggregates(host);
        host.UpdateRules(p => p with
        {
            Formulas = p.Formulas.Append(new FormulaDef
            {
                Id = "f-gsum", Name = "Morale gate", Text = "W + if(sum(Morale, \"\") < 10, B, 0)",
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
                Bindings = new()
                {
                    new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" },
                    new VariableBinding { Variable = "B", Kind = BindingKind.Input, When = "sum(Morale, \"\") < 10" },
                },
            }).ToList(),
        });
        var executor = new IntentExecutor(host, hc);
        var guest = Guest(host, hideable: true);
        var port = PortTo(executor);

        var m = Dialog(guest, "f-gsum", PhaseFixture.BlueOne, check: false);
        Assert.Contains(FormulaMasking.AggregateCallInput, m.Hidden);
        var before = m.Evaluate(guest.Game!.State);
        var box = before.Boxes.Single(b => b.Variable == "B");
        Assert.NotEqual(FormulaDialogModel.NotUsedText, box.Text);   // the guest cannot know: hidden pieces may count
        Assert.True(box.Enabled);
        Assert.Equal("Waiting for: B", before.Status);   // B7: a guard over hidden data is always to be filled

        // The host sees three pieces of Morale 5 (15): it still asks for B (B7), then applies without using it.
        var before15 = Ledger.Get(host.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water");
        Assert.Equal("Waiting for: B", (await FormulaMasking.SendAsync(port, m, null, calculate: false)).Message);
        m.SetBoxText("B", "7");
        var applied = await FormulaMasking.SendAsync(port, m, null, calculate: false);
        Assert.True(applied.Applied, applied.Message);
        Assert.Equal(before15 ?? host.Rules!.Properties.First(p => p.Id == "water").Default, Ledger.Get(host.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
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
    public void Guest_marks_every_snapshot_as_a_player_view()
    {
        var (host, _) = Open(Difficulty.Hardcore, new[] { Alice, Bob });
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var guest = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "gcache-flag"), Path.Combine(_tmp, "groot-flag"));
        _sessions.Add(guest);
        guest.EnterGuestMode();
        guest.LoadGuestRules(host.Rules!);
        // An older host filters its snapshots but does not set the flag.
        var old = PlayerView.For(host.Game!.State, host.Rules, host.Model, Blue);
        old.IsPlayerView = false;
        guest.ApplySnapshot(old, Blue);
        Assert.True(guest.Game!.State.IsPlayerView);
        var later = PlayerView.For(host.Game!.State, host.Rules, host.Model, Blue);
        later.IsPlayerView = false;
        guest.ApplySnapshot(later, Blue);
        Assert.True(guest.Game!.State.IsPlayerView);
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

    /// <summary>
    /// Guards over Red's private depot (E). Blue's snapshot has no depot value, so a guest deciding them locally would decide on the default.
    /// f-gw: B is asked only when the depot is not empty; f-gp: runs only when it is not empty; f-gv: a guard over Blue's own unit.
    /// </summary>
    private static void AddHiddenGuards(ModuleSession s)
    {
        VariableBinding W() => new() { Variable = "W", Kind = BindingKind.Unit, Property = "water" };
        VariableBinding E() => new() { Variable = "E", Kind = BindingKind.Holder, HolderId = "reddepot", Property = "supply" };
        List<FormulaOutput> Water() => new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } };
        s.UpdateRules(p => p with
        {
            Formulas = p.Formulas.Concat(new[]
            {
                new FormulaDef
                {
                    Id = "f-gw", Name = "Depot raid", Text = "W + if(E > 0, B, 0)", Outputs = Water(),
                    Bindings = new() { W(), E(), new VariableBinding { Variable = "B", Kind = BindingKind.Input, When = "E > 0" } },
                },
                new FormulaDef
                {
                    Id = "f-gp", Name = "Depot gate", Text = "W + 1", Precondition = "E > 0", RefusalMessage = "The depot is empty", Outputs = Water(),
                    Bindings = new() { W(), E() },
                },
                new FormulaDef
                {
                    Id = "f-gv", Name = "Own gate", Text = "W + if(W > 1000, B, 0)", Outputs = Water(),
                    Bindings = new() { W(), new VariableBinding { Variable = "B", Kind = BindingKind.Input, When = "W > 1000" } },
                },
            }).ToList(),
        });
    }

    /// <summary>
    /// Adds the invented table Speeds (Red 1: 3, Blue 1: 5), "Boost" (<paramref name="vis"/>) overriding its Speed column, Red 1's Boost set to
    /// the Red secret and Blue 1's to 9, and formulas reading the cell through a Table binding ("f-sb"), inline ("f-sl"), and an Input asked only
    /// when the inline lookup is above 1 ("f-sg").
    /// </summary>
    private static void AddOverride(ModuleSession s, Visibility vis)
    {
        s.UpdateRules(p => p with
        {
            Properties = p.Properties.Append(new PropertyDef
                { Id = "boost", Name = "Boost", Scope = PropertyScope.Pieces, Visibility = vis, OverridesTable = "t-speed", OverridesColumn = "Speed" }).ToList(),
            Tables = p.Tables.Append(new TableDef
            {
                Id = "t-speed", Name = "Speeds", Kind = TableKind.TwoD, RowKeys = new() { "Red 1", "Blue 1" }, ColumnKeys = new() { "Speed" },
                Cells = new() { new() { "3" }, new() { "5" } },
            }).ToList(),
            Formulas = p.Formulas.Concat(new[]
            {
                new FormulaDef
                {
                    Id = "f-sb", Name = "Speed bound", Text = "T + 1",
                    Bindings = new() { new VariableBinding { Variable = "T", Kind = BindingKind.Table, TableId = "t-speed", RowExpr = "name()", ColExpr = "\"Speed\"" } },
                },
                new FormulaDef { Id = "f-sl", Name = "Speed inline", Text = "lookup(Speeds, name(), \"Speed\") + 1" },
                new FormulaDef
                {
                    Id = "f-sg", Name = "Speed gate", Text = "B",
                    Bindings = new() { new VariableBinding { Variable = "B", Kind = BindingKind.Input, When = "lookup(Speeds, name(), \"Speed\") > 1" } },
                },
            }).ToList(),
        });
        s.Game!.Execute(new LedgerDelta(LedgerKey.Piece(PhaseFixture.RedOne), "boost", 0, RedSecret, "manual", null, null, null, null));
        s.Game!.Execute(new LedgerDelta(LedgerKey.Piece(PhaseFixture.BlueOne), "boost", 0, 9, "manual", null, null, null, null));
    }

    [Fact]
    public void Guest_private_override_masked()
    {
        var (host, _) = Open(Difficulty.SemiAuto, new[] { Alice, Bob });
        AddOverride(host, Visibility.Private);
        var guest = Guest(host);
        HashSet<string> HostSees(string id, string piece) =>
            FormulaMasking.HiddenVariables(host.Rules!, host.Model, host.Game!.State, host.Rules!.ById<FormulaDef>(id)!, piece, Blue);

        // On Red's piece: the bound cell is hidden, and the inline lookup masks the formula; the host decides the guard.
        var bound = Dialog(guest, "f-sb", PhaseFixture.RedOne, check: false);
        Assert.Equal(new[] { "T" }, bound.Hidden.ToArray());
        Assert.Equal(bound.Hidden.ToArray(), HostSees("f-sb", PhaseFixture.RedOne).ToArray());   // host and guest agree
        var shown = Shown(bound.Evaluate(guest.Game!.State));
        Assert.DoesNotContain(RedText, shown);
        Assert.Contains(FormulaMasking.HostCalculatesText, shown);
        Assert.Equal(new[] { FormulaMasking.TableCallInput }, Dialog(guest, "f-sl", PhaseFixture.RedOne, check: false).Hidden.ToArray());
        Assert.Equal(new[] { FormulaMasking.TableCallInput }, HostSees("f-sl", PhaseFixture.RedOne).ToArray());
        var gate = Dialog(guest, "f-sg", PhaseFixture.RedOne, check: false);
        Assert.Equal(new[] { "B" }, FormulaMasking.GuardsOnHidden(gate.Formula, gate.Hidden).ToArray());
        Assert.DoesNotContain(RedText, Shown(Dialog(guest, "f-sl", PhaseFixture.RedOne, check: false).Evaluate(guest.Game!.State)));

        // On Blue's own piece nothing is hidden: the guest reads its own Boost.
        Assert.Empty(Dialog(guest, "f-sb", PhaseFixture.BlueOne, check: false).Hidden);
        Assert.Empty(HostSees("f-sl", PhaseFixture.BlueOne));
        Assert.Equal("10", Dialog(guest, "f-sb", PhaseFixture.BlueOne, check: false).Evaluate(guest.Game!.State).ResultText);

        // Hot-seat: Blue's view on the host's screen masks the same boxes.
        Assert.Equal(new[] { "T" }, FormulaMasking.HiddenVariables(host.Rules!, host.Model, host.Game!.State, host.Rules!.ById<FormulaDef>("f-sb")!,
            PhaseFixture.RedOne, Blue).ToArray());
    }

    [Fact]
    public void Guest_public_override_shown()
    {
        var (host, _) = Open(Difficulty.SemiAuto, new[] { Alice, Bob });
        AddOverride(host, Visibility.Public);
        var guest = Guest(host);
        foreach (var id in new[] { "f-sb", "f-sl" })
        {
            var m = Dialog(guest, id, PhaseFixture.RedOne, check: false);
            Assert.Empty(m.Hidden);
            Assert.Equal((RedSecret + 1).ToString(CultureInfo.InvariantCulture), m.Evaluate(guest.Game!.State).ResultText);
        }
        Assert.Empty(FormulaMasking.HiddenVariables(host.Rules!, host.Model, host.Game!.State, host.Rules!.ById<FormulaDef>("f-sb")!, PhaseFixture.RedOne, Blue));
    }

    [Fact]
    public void GuardsOnHidden_FollowsHiddenVariablesAndHolderCalls()
    {
        var f = new FormulaDef
        {
            Id = "f", Name = "F", Text = "A + B + C + D", Precondition = "X > 0",
            Bindings = new()
            {
                new VariableBinding { Variable = "A", Kind = BindingKind.Input, When = "H > 0" },
                new VariableBinding { Variable = "B", Kind = BindingKind.Input, When = "A > 0" },   // through A's own guard
                new VariableBinding { Variable = "C", Kind = BindingKind.Input, When = "holder(RedStore, Supply) > 0" },
                new VariableBinding { Variable = "D", Kind = BindingKind.Input, When = "X > 0" },
                new VariableBinding { Variable = "H", Kind = BindingKind.Holder, HolderId = "reddepot", Property = "supply" },
                new VariableBinding { Variable = "X", Kind = BindingKind.Input },
            },
        };
        Assert.Empty(FormulaMasking.GuardsOnHidden(f, new HashSet<string>()));
        Assert.Equal(new[] { "A", "B" }, FormulaMasking.GuardsOnHidden(f, new HashSet<string> { "H" }).OrderBy(x => x));
        Assert.Equal(new[] { "C" }, FormulaMasking.GuardsOnHidden(f, new HashSet<string> { FormulaMasking.HolderCallInput }));
        Assert.Equal(new[] { "D", FormulaMasking.PreconditionGuard }.OrderBy(x => x, StringComparer.Ordinal),
            FormulaMasking.GuardsOnHidden(f, new HashSet<string> { "X" }).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Guest_WhenOverHiddenHolder_BoxTypeable_HostDecides()
    {
        var (host, hc) = Open(Difficulty.FullyAuto, new[] { Alice, Bob });
        AddHiddenGuards(host);
        var executor = new IntentExecutor(host, hc);
        var guest = Guest(host);
        var port = PortTo(executor);   // asserts no verdict carries the Red secret

        var m = Dialog(guest, "f-gw", PhaseFixture.BlueOne, check: false);
        Assert.Contains("E", m.Hidden);
        var before = m.Evaluate(guest.Game!.State);
        var box = before.Boxes.Single(b => b.Variable == "B");
        Assert.NotEqual(FormulaDialogModel.NotUsedText, box.Text);   // the guest cannot know: the depot value is Red's
        Assert.True(box.Enabled);
        Assert.Equal(BoxBackground.Typed, box.Background);
        Assert.Equal("Waiting for: B", before.Status);   // B7: a guard over hidden data is always to be filled
        Assert.Equal(FormulaMasking.HostCalculatesText, before.ResultText);   // B7: a guest's masked result is always the host's
        Assert.DoesNotContain(RedText, Shown(before));

        // Without B the host waits for it (whatever the depot holds); with B it applies.
        var waiting = await FormulaMasking.SendAsync(port, m, null, calculate: false);
        Assert.False(waiting.Applied);
        Assert.Equal("Waiting for: B", waiting.Message);
        m.SetBoxText("B", "5");
        Assert.Equal("5", m.Evaluate(guest.Game!.State).Boxes.Single(b => b.Variable == "B").Text);
        var applied = await FormulaMasking.SendAsync(port, m, null, calculate: false);
        Assert.True(applied.Applied, applied.Message);
        Assert.Equal(15, Ledger.Get(host.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
    }

    [Fact]
    public async Task Guest_PreconditionOverHiddenHolder_NoLocalRefusal_HostDecides()
    {
        var (host, hc) = Open(Difficulty.FullyAuto, new[] { Alice, Bob });
        AddHiddenGuards(host);
        var executor = new IntentExecutor(host, hc);
        var guest = Guest(host);
        var port = PortTo(executor);

        var m = Dialog(guest, "f-gp", PhaseFixture.BlueOne, check: false);
        var local = m.Evaluate(guest.Game!.State);
        Assert.True(local.Trace.Refused);   // the guest's own run sees an empty depot
        Assert.Equal(FormulaDialogModel.HostWaitText, local.Status);
        Assert.DoesNotContain("The depot is empty", Shown(local));

        var applied = await FormulaMasking.SendAsync(port, m, null, calculate: false);
        Assert.True(applied.Applied, applied.Message);
        Assert.Equal(11, Ledger.Get(host.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
    }

    [Fact]
    public void Guest_WhenOverVisibleData_StillNotUsed()
    {
        var (host, _) = Open(Difficulty.FullyAuto, new[] { Alice, Bob });
        AddHiddenGuards(host);
        var guest = Guest(host);
        var m = Dialog(guest, "f-gv", PhaseFixture.BlueOne, check: false);
        Assert.True(m.HostCalculates);
        var v = m.Evaluate(guest.Game!.State);
        var box = v.Boxes.Single(b => b.Variable == "B");
        Assert.Equal(FormulaDialogModel.NotUsedText, box.Text);
        Assert.False(box.Enabled);
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
        Assert.Equal(FormulaMasking.HostCalculatesText, first.Boxes.Single(b => b.Variable == "R").Text);   // B7: a masked formula's dice are never shown before Apply
        Assert.Equal(FormulaMasking.HostCalculatesText, first.ResultText);   // B7: a guest's masked result is always the host's

        // Calculate is a preview: nothing executes on the host; the visible boxes fill as in solo.
        var before = Json(host.Game!.State);
        var p1 = await FormulaMasking.SendAsync(port, m, null, calculate: true, preview: true);
        Assert.False(p1.Applied);
        Assert.Equal(before, Json(host.Game!.State));
        Assert.NotNull(p1.Values);
        Assert.False(p1.Values!.ContainsKey("S"));
        Assert.Equal("10", p1.Values["W"]);
        Assert.DoesNotContain(RedText, string.Join("|", p1.Values.Values));
        Assert.Null(p1.Rolls);   // B7: the dice stay pinned on the host
        Assert.False(p1.Values.ContainsKey("R"));
        Assert.Equal(1, next);   // rolled once, on the host
        var shown = m.Evaluate(guest.Game!.State);
        Assert.Equal("10", shown.Boxes.Single(b => b.Variable == "W").Text);
        Assert.Equal(FormulaMasking.HostCalculatesText, shown.Boxes.Single(b => b.Variable == "R").Text);
        Assert.Equal(FormulaMasking.HostCalculatesText, shown.Boxes.Single(b => b.Variable == "S").Text);
        Assert.Null(shown.Boxes.Single(b => b.Variable == "R").RerollKey);
        Assert.DoesNotContain(RedText, Shown(shown));

        // Calculate again: the same roll, no fishing.
        var p2 = await FormulaMasking.SendAsync(port, m, null, calculate: true, preview: true);
        Assert.Null(p2.Rolls);
        Assert.Equal(1, next);   // nothing rolled again
        Assert.Equal(before, Json(host.Game!.State));
        Assert.Equal(1, executor.Pins.Count("p2"));

        // Apply uses the pinned roll (once).
        var applied = await FormulaMasking.SendAsync(port, m, null, calculate: false);
        Assert.True(applied.Applied, applied.Message);
        Assert.Equal(1, next);
        var roll = Assert.Single(host.Game!.State.DiceHistory.Last().Results);
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
        var (host, _, executor, rolled) = DiceHost();
        var guest = Guest(host);
        var port = PortTo(executor);
        var a = Dialog(guest, "f-dice", PhaseFixture.BlueOne, check: true);
        // B7: f-dice is masked, so its dice stay on the host: the roll is followed through the host's roller.
        Assert.Null((await FormulaMasking.SendAsync(port, a, null, calculate: true, preview: true)).Rolls);
        var roll = Assert.Single(rolled);
        // Fill the table with other scopes (as 511 other units would).
        for (var i = 1; i < FormulaPins.MaxPerSender; i++)
            Assert.NotNull(executor.Pins.Open(new FormulaPins.Key("p2", "f-dice", "x" + i), host.Game!, host.Rules!, mayRoll: true));
        Assert.Equal(FormulaPins.MaxPerSender, executor.Pins.Count("p2"));
        // A still re-shows its same roll; a new preview is refused before rolling, nothing evicted, nothing executed.
        Assert.Null((await FormulaMasking.SendAsync(port, a, null, calculate: true, preview: true)).Rolls);
        Assert.Equal(roll, Assert.Single(rolled));   // the same roll: nothing rolled again
        var before = Json(host.Game!.State);
        var b = Dialog(guest, "f-dice2", PhaseFixture.BlueOne, check: true);
        var refused = await FormulaMasking.SendAsync(port, b, null, calculate: true, preview: true);
        Assert.False(refused.Applied);
        Assert.Equal(FormulaPins.FullText, refused.Message);
        Assert.Null(refused.Rolls);
        Assert.Equal(before, Json(host.Game!.State));
        Assert.Equal(FormulaPins.MaxPerSender, executor.Pins.Count("p2"));
        Assert.Null((await FormulaMasking.SendAsync(port, a, null, calculate: true, preview: true)).Rolls);
        Assert.Equal(roll, Assert.Single(rolled));
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
    public void Pins_PendingRun_AdHocRefused_RunPreviewThenApplySameRoll()
    {
        var blue = new PhaseDef
        {
            Id = "ph-blue", Name = "Blue roll", SideId = "blue",
            OnStart = new() { new StartEntry { FormulaId = "f-dice2", ForEach = ForEachMode.Units } },
        };
        var (host, hc, x, rolled) = DiceHost(p => p with { Phases = new[] { blue }.Concat(p.Phases).ToList() });
        hc.Start();
        var run = host.Game!.State.Pending.Single(r => r.FormulaId == "f-dice2" && r.Units.Any(u => u.PieceId == PhaseFixture.BlueOne));
        var before = Json(host.Game!.State);
        // An ad-hoc run of the same formula on that unit is refused, preview or Apply, and rolls nothing.
        Assert.Equal(FormulaIntentRunner.UsePendingText, Send(x, "f-dice2", null, calculate: true, preview: true).Message);
        Assert.Equal(FormulaIntentRunner.UsePendingText,
            Send(x, "f-dice2", new Dictionary<string, string> { ["W"] = "10" }, calculate: false, preview: false).Message);
        Assert.Empty(rolled);
        Assert.Equal(before, Json(host.Game!.State));
        // Another formula on the same unit is not affected.
        Assert.NotEqual(FormulaIntentRunner.UsePendingText, Send(x, "f-dice", null, calculate: true, preview: true).Message);
        // The run itself: preview, then Apply with the same roll.
        var roll = Assert.Single(Send(x, "f-dice2", null, calculate: true, preview: true, runId: run.Id).Rolls!["R"]);
        var ok = Send(x, "f-dice2", new Dictionary<string, string> { ["W"] = "10" }, calculate: false, preview: false, runId: run.Id);
        Assert.True(ok.Applied, ok.Message);
        Assert.Equal(new[] { roll }, host.Game!.State.DiceHistory.Last().Results);
        Assert.Equal(10 + roll, Ledger.Get(host.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
    }

    [Fact]
    public async Task Guest_InputChangesDice_LocalRollNeverShownAsHosts()
    {
        var (host, _, executor, _) = DiceHost();
        var guest = Guest(host);
        guest.SetRoller((n, _) => Enumerable.Repeat(6, n).ToList());   // the guest's own (local) dice, never the host's
        var port = PortTo(executor);
        var m = Dialog(guest, "f-n", PhaseFixture.BlueOne, check: true);
        m.SetBoxText("N", "1");
        var hostDice = (await FormulaMasking.SendAsync(port, m, null, calculate: true, preview: true)).Rolls!["dice#0"];
        var shown = m.Evaluate(guest.Game!.State);
        Assert.Equal(hostDice.Sum().ToString(CultureInfo.InvariantCulture), Assert.Single(shown.InlineRolls).Text);
        Assert.NotEqual("", shown.ResultText);

        // Two dice now: the guest's local run rolls its own, which are not the host's.
        m.SetBoxText("N", "2");
        var two = m.Evaluate(guest.Game!.State);
        Assert.Empty(two.InlineRolls);
        Assert.Equal("", two.ResultText);
        Assert.Equal("", two.WorkingText);
        Assert.Equal(hostDice, m.PinnedRolls["dice#0"]);   // the host's dice were not overwritten by the local roll

        // Back to one die: the host's roll is shown again.
        m.SetBoxText("N", "1");
        var back = m.Evaluate(guest.Game!.State);
        Assert.Equal(hostDice.Sum().ToString(CultureInfo.InvariantCulture), Assert.Single(back.InlineRolls).Text);
    }

    [Theory]
    [InlineData("W + R", false)]
    [InlineData("dice(2, 6) + W", true)]
    [InlineData("W + DICE (1,6)", true)]
    [InlineData("paradice(2) + W", false)]
    [InlineData("dice + W", false)]
    public void MayRoll_MatchesDiceCalls(string text, bool expected)
    {
        var f = new FormulaDef { Id = "x", Text = text, Bindings = new() { new VariableBinding { Variable = "W", Kind = BindingKind.Input } } };
        Assert.Equal(expected, FormulaPins.MayRoll(f));
    }

    [Fact]
    public void MayRoll_SeesInputAutoValueAndTableKeys()
    {
        Assert.True(FormulaPins.MayRoll(new FormulaDef
        {
            Id = "x", Text = "N",
            Bindings = new() { new VariableBinding { Variable = "N", Kind = BindingKind.Input, AutoValue = "dice(1, 6)" } },
        }));
        Assert.True(FormulaPins.MayRoll(new FormulaDef
        {
            Id = "x", Text = "T",
            Bindings = new() { new VariableBinding { Variable = "T", Kind = BindingKind.Table, TableId = "t-cost", RowExpr = "dice(1,2)" } },
        }));
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
    public void Pins_StaticGuessMisses_ScopeRegisteredOnFirstRoll_FullRefusedFailClosed()
    {
        var (host, hc, x, rolled) = DiceHost();
        x.Pins.MayRollHint = _ => false;   // as if the formula rolled through a path the static check does not see
        hc.Start();
        // Still pinned: registered lazily on the first real roll, so asking again shows the same dice.
        var roll = PreviewRoll(x, "f-dice2", "R");
        Assert.Equal(1, x.Pins.Count("p2"));
        Assert.Equal(roll, PreviewRoll(x, "f-dice2", "R"));
        Assert.Single(rolled);

        // The sender's table is full: the first roll of a new scope cannot be pinned, so the run is refused and nothing is rolled.
        for (var i = 1; i < FormulaPins.MaxPerSender; i++)
            Assert.NotNull(x.Pins.Open(new FormulaPins.Key("p2", "f-dice", "x" + i), host.Game!, host.Rules!, mayRoll: true));
        var before = Json(host.Game!.State);
        var refused = Send(x, "f-dice", null, calculate: true, preview: true);
        Assert.False(refused.Applied);
        Assert.Equal(FormulaPins.FullText, refused.Message);
        Assert.Null(refused.Rolls);
        Assert.Null(refused.Values);
        var applyRefused = Send(x, "f-dice", null, calculate: true, preview: false);
        Assert.Equal(FormulaPins.FullText, applyRefused.Message);
        Assert.Equal(before, Json(host.Game!.State));
        Assert.Single(rolled);   // no die was rolled for the refused runs
        Assert.Equal(FormulaPins.MaxPerSender, x.Pins.Count("p2"));
        // A formula without dice never registers, so it still runs.
        Assert.NotEqual(FormulaPins.FullText, Send(x, "f-mix", null, calculate: true, preview: true).Message);
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
        // B7: the movement reads Blue's depot, so the masked row previews no value before Apply, not even Red's own fuel.
        Assert.DoesNotContain("5150", Shown(b.Row(0)));
        Assert.Contains("Fuel changes (hidden)", Shown(b.Row(0)));
        Assert.True(b.ApplyRow(0));
        Assert.True(Ledger.Get(s.Game!.State, LedgerKey.Piece(PhaseFixture.RedOne), "fuel") < 5150);   // applied: Red's fuel was spent
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

    /// <summary>
    /// Piece bindings (invented): "Spy" (W + P * 0, P = the picked piece's Private Water) and "Compare" (W + M, M = the picked piece's Public
    /// Morale). Red 1's water is the Red secret, its morale 7; Red 2's morale 9. With <paramref name="hideRedTwo"/> Red 2 carries a Hideable
    /// trait switched on by Red, so Blue cannot see it.
    /// </summary>
    private static void AddPieceBindings(ModuleSession s, bool hideRedTwo = false)
    {
        s.UpdateRules(p => p with
        {
            Properties = p.Properties.Append(new PropertyDef
                { Id = "morale", Name = "Morale", Scope = PropertyScope.Pieces, Visibility = Visibility.Public, Default = 5 }).ToList(),
            Formulas = p.Formulas.Concat(new[]
            {
                new FormulaDef
                {
                    Id = "f-spy", Name = "Spy", Text = "W + P * 0",
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
                    Bindings = new()
                    {
                        new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" },
                        new VariableBinding { Variable = "P", Kind = BindingKind.Piece, Property = "water" },
                    },
                },
                new FormulaDef
                {
                    Id = "f-cmp", Name = "Compare", Text = "W + M",
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
                    Bindings = new()
                    {
                        new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" },
                        new VariableBinding { Variable = "M", Kind = BindingKind.Piece, Property = "morale" },
                    },
                },
            }).ToList(),
        });
        var g = s.Game!;
        g.Execute(new LedgerDelta(LedgerKey.Piece(PhaseFixture.RedOne), "water", 10, RedSecret, "manual", null, null, null, null));
        g.Execute(new LedgerDelta(LedgerKey.Piece(PhaseFixture.RedOne), "morale", 5, 7, "manual", null, null, null, null));
        g.Execute(new LedgerDelta(LedgerKey.Piece(PhaseFixture.RedTwo), "morale", 5, 9, "manual", null, null, null, null));
        if (hideRedTwo)
        {
            var r2 = g.State.Pieces[PhaseFixture.RedTwo];
            r2.TypeString = PieceTypeParser.EncodeState(new[] { "hide;I;Hide it;;;0.3;;false", "piece;;;unit.svg;Red 2" });
            r2.TraitStates = new() { "red", "" };
        }
    }

    private static VariableBinding PieceBinding(ModuleSession s, string formulaId, string variable) =>
        s.Rules!.ById<FormulaDef>(formulaId)!.Bindings.Single(b => b.Variable == variable);

    private static IReadOnlyList<string> Labels(ModuleSession s, string formulaId, string variable, IReadOnlySet<string>? sides) =>
        Quartermaster.Core.PieceChoices.For(s.Game!.State, s.Rules!, s.Model, PieceBinding(s, formulaId, variable), sides).Select(c => c.Label).ToList();

    private static string LabelOf(IReadOnlyList<string> labels, string name) =>
        labels.Single(l => l.StartsWith(name + " (", StringComparison.Ordinal) || l == name);

    [Fact]
    public void PieceBinding_dialog_lists_visible_choices()
    {
        var (host, _) = Open(Difficulty.SemiAuto, new[] { Alice, Bob });
        AddPieceBindings(host, hideRedTwo: true);
        var blueLabels = Labels(host, "f-cmp", "M", Blue);
        Assert.Equal(2, blueLabels.Count);
        Assert.DoesNotContain(blueLabels, l => l.StartsWith("Red 2", StringComparison.Ordinal));

        // Hot seat, Blue at the screen: the list is Blue's, and the box is a typed choice box.
        host.SetViewSides(Blue);
        var m = Dialog(host, "f-cmp", PhaseFixture.BlueOne, check: false);
        Assert.Equal(Blue, m.PickSides);
        var v = m.Evaluate(host.Game!.State);
        var box = v.Boxes.Single(b => b.Variable == "M");
        Assert.Equal(blueLabels, box.Choices);
        Assert.Equal(BoxBackground.Typed, box.Background);
        Assert.True(box.Enabled);
        Assert.Equal("", box.Text);
        Assert.Equal("Waiting for: M", v.Status);
        m.SetBoxText("M", LabelOf(blueLabels, "Red 1"));
        var picked = m.Evaluate(host.Game!.State);
        Assert.Equal(LabelOf(blueLabels, "Red 1"), picked.Boxes.Single(b => b.Variable == "M").Text);
        Assert.Equal("17", picked.ResultText);
        Assert.Equal("Morale of Red 1", picked.Trace.Variables.Single(x => x.Variable == "M").Source);
        // Blue cannot reach the hidden Red 2 by typing its label.
        m.SetBoxText("M", LabelOf(Labels(host, "f-cmp", "M", null), "Red 2"));
        Assert.Null(m.Evaluate(host.Game!.State).Trace.Result);

        // Red at the screen sees its own hidden piece.
        host.SetViewSides(Red);
        var red = Dialog(host, "f-cmp", PhaseFixture.RedOne, check: false).Evaluate(host.Game!.State);
        Assert.Contains(red.Boxes.Single(b => b.Variable == "M").Choices!, l => l.StartsWith("Red 2", StringComparison.Ordinal));

        // Solo (no viewer): every piece.
        host.SetViewSides(null);
        var solo = Dialog(host, "f-cmp", PhaseFixture.RedOne, check: false);
        Assert.Null(solo.PickSides);
        Assert.Equal(3, solo.Evaluate(host.Game!.State).Boxes.Single(b => b.Variable == "M").Choices!.Count);

        // The guest's dialog runs on its own view: the same list, built from that view.
        var guest = Guest(host);
        var gm = Dialog(guest, "f-cmp", PhaseFixture.BlueOne, check: false);
        Assert.Null(gm.PickSides);
        Assert.Equal(blueLabels, gm.Evaluate(guest.Game!.State).Boxes.Single(b => b.Variable == "M").Choices);
    }

    [Fact]
    public void HotSeat_viewer_change_clears_piece_pick()
    {
        var (host, _) = Open(Difficulty.SemiAuto, new[] { Alice, Bob });
        AddPieceBindings(host);
        host.SetViewSides(Blue);
        var m = Dialog(host, "f-cmp", PhaseFixture.BlueOne, check: false);
        var label = LabelOf(Labels(host, "f-cmp", "M", Blue), "Red 1");
        m.SetBoxText("M", label);
        Assert.Equal("17", m.Evaluate(host.Game!.State).ResultText);
        FormulaMasking.Configure(m, host);   // the same viewer: the pick stays
        Assert.Equal(label, m.Evaluate(host.Game!.State).Boxes.Single(b => b.Variable == "M").Text);

        host.SetViewSides(Red);   // Red takes the shared screen: Blue's pick is never Red's
        FormulaMasking.Configure(m, host);
        var v = m.Evaluate(host.Game!.State);
        Assert.Equal("", v.Boxes.Single(b => b.Variable == "M").Text);
        Assert.Equal("Waiting for: M", v.Status);
        Assert.False(m.Intent(null, false).Typed.ContainsKey("M"));
    }

    [Fact]
    public async Task Guest_piece_binding_private_masked()
    {
        var (host, hc) = Open(Difficulty.FullyAuto, new[] { Alice, Bob });
        AddPieceBindings(host);
        var executor = new IntentExecutor(host, hc);
        var guest = Guest(host);
        var port = PortTo(executor);
        Assert.Equal(new[] { "P" }, FormulaMasking.HiddenVariables(host.Rules!, host.Model, host.Game!.State,
            host.Rules!.ById<FormulaDef>("f-spy")!, PhaseFixture.BlueOne, Blue).ToArray());

        var m = Dialog(guest, "f-spy", PhaseFixture.BlueOne, check: false);
        Assert.Contains("P", m.Hidden);
        Assert.True(m.HostCalculates);
        var before = m.Evaluate(guest.Game!.State);
        var box = before.Boxes.Single(b => b.Variable == "P");
        Assert.NotNull(box.Choices);   // the pick stays the guest's own: still a choice box
        Assert.True(box.Enabled);
        Assert.Equal("Waiting for: P", before.Status);
        var label = LabelOf(box.Choices!, "Red 1");
        m.SetBoxText("P", label);
        var picked = m.Evaluate(guest.Game!.State);
        var pbox = picked.Boxes.Single(b => b.Variable == "P");
        Assert.Equal(label, pbox.Text);
        Assert.Equal(FormulaMasking.HostCalculatesText, pbox.Tooltip);
        Assert.Equal(FormulaDialogModel.HostWaitText, picked.Status);
        Assert.Equal("", picked.WorkingText);
        Assert.DoesNotContain(RedText, Shown(picked));
        Assert.Equal(label, m.Intent(null, false).Typed["P"]);

        // The host reads the Red secret with Blue's list and applies; the verdict (checked by PortTo) and Blue's view never carry it.
        var applied = await FormulaMasking.SendAsync(port, m, null, calculate: false);
        Assert.True(applied.Applied, applied.Message);
        Assert.Equal(FormulaBox.Hidden, applied.Boxes.Single(b => b.Var == "P").State);
        var full = host.Game!.State;
        Assert.Contains(full.LedgerHistory, e => e.FormulaId == "f-spy" && e.Working != null && e.Working.Contains(RedText));
        var blue = PlayerView.For(full, host.Rules, host.Model, Blue);
        Assert.DoesNotContain(blue.LedgerHistory, e => e.FormulaId == "f-spy" && e.Working != null);
        Assert.DoesNotContain(RedText, Json(blue));
        // The rule is per formula (as every hidden input): Red's view drops it too; only the full state keeps it.
        Assert.DoesNotContain(PlayerView.For(full, host.Rules, host.Model, Red).LedgerHistory, e => e.FormulaId == "f-spy" && e.Working != null);

        // Hot seat, Blue at the screen: the box is a choice box too, its value hidden, the working redacted.
        host.SetViewSides(Blue);
        var hs = Dialog(host, "f-spy", PhaseFixture.BlueOne, check: false);
        hs.SetBoxText("P", label);
        var hv = hs.Evaluate(full);
        Assert.Equal(label, hv.Boxes.Single(b => b.Variable == "P").Text);
        Assert.Equal(FormulaMasking.HiddenText, hv.Boxes.Single(b => b.Variable == "P").Tooltip);
        Assert.Equal(FormulaMasking.WorkingHiddenText, hv.WorkingText);
        Assert.DoesNotContain(RedText, Shown(hv));
        Assert.True(hv.CanApply, hv.Status);
    }

    [Fact]
    public async Task Guest_piece_binding_public_shown()
    {
        var (host, hc) = Open(Difficulty.FullyAuto, new[] { Alice, Bob });
        AddPieceBindings(host);
        var executor = new IntentExecutor(host, hc);
        var guest = Guest(host);
        var port = PortTo(executor);

        var m = Dialog(guest, "f-cmp", PhaseFixture.BlueOne, check: false);
        Assert.Empty(m.Hidden);
        m.SetBoxText("M", LabelOf(Labels(host, "f-cmp", "M", Blue), "Red 1"));
        var v = m.Evaluate(guest.Game!.State);
        Assert.Equal("17", v.ResultText);
        Assert.Contains("7", v.WorkingText);
        var applied = await FormulaMasking.SendAsync(port, m, null, calculate: false);
        Assert.True(applied.Applied, applied.Message);
        Assert.Equal(17, Ledger.Get(host.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
        var blue = PlayerView.For(host.Game!.State, host.Rules, host.Model, Blue);
        Assert.Contains(blue.LedgerHistory, e => e.FormulaId == "f-cmp" && e.Working != null);
    }

    [Fact]
    public async Task HotSeat_masked_pick_of_public_property_is_hidden()
    {
        // Red 1 masked from Blue (Obscurable on): Blue picks "Unknown piece (...)"; its Public Morale is not on Blue's screen.
        var (host, hc) = Open(Difficulty.FullyAuto, new[] { Alice, Bob }, hideable: true);
        AddPieceBindings(host);
        var r1 = host.Game!.State.Pieces[PhaseFixture.RedOne];
        r1.TypeString = PieceTypeParser.EncodeState(new[] { "obs;M;m.png;Mask it;I;?", "piece;;;unit.svg;Red 1" });
        r1.TraitStates = new() { "red", "" };
        var f = host.Rules!.ById<FormulaDef>("f-cmp")!;
        Assert.Contains("M", FormulaMasking.HiddenVariables(host.Rules!, host.Model, host.Game!.State, f, PhaseFixture.BlueOne, Blue));
        Assert.DoesNotContain("M", FormulaMasking.HiddenVariables(host.Rules!, host.Model, host.Game!.State, f, PhaseFixture.BlueOne,
            new HashSet<string> { "red", "blue" }));

        host.SetViewSides(Blue);
        var m = Dialog(host, "f-cmp", PhaseFixture.BlueOne, check: false);
        Assert.Contains("M", m.Hidden);
        var label = Assert.Single(m.Evaluate(host.Game!.State).Boxes.Single(b => b.Variable == "M").Choices!,
            l => l.StartsWith(Quartermaster.Core.PieceChoices.UnknownName, StringComparison.Ordinal));
        m.SetBoxText("M", label);
        var v = m.Evaluate(host.Game!.State);
        var box = v.Boxes.Single(b => b.Variable == "M");
        Assert.Equal(label, box.Text);
        Assert.Equal(FormulaMasking.HiddenText, box.Tooltip);
        Assert.Equal(FormulaMasking.WorkingHiddenText, v.WorkingText);
        Assert.DoesNotContain("Red 1", Shown(v));
        Assert.True(v.CanApply, v.Status);

        // Through the host for a guest (Bob holding Blue): applied, and Blue's view never shows the working.
        var applied = new IntentExecutor(host, hc).Execute("p2", Blue,
            new FormulaIntent("f-cmp", PhaseFixture.BlueOne, null, new Dictionary<string, string> { ["M"] = label }, false)).Verdict!;
        Assert.True(applied.Applied, applied.Message);
        Assert.Equal(FormulaBox.Hidden, applied.Boxes.Single(b => b.Var == "M").State);
        var blue = PlayerView.For(host.Game!.State, host.Rules, host.Model, Blue);
        Assert.DoesNotContain(blue.LedgerHistory, e => e.FormulaId == "f-cmp" && e.Working != null);
        await Task.CompletedTask;
    }

    [Fact]
    public void Host_resolves_guest_pick_with_guest_sides()
    {
        var (host, hc) = Open(Difficulty.FullyAuto, new[] { Alice, Bob });
        AddPieceBindings(host, hideRedTwo: true);
        var executor = new IntentExecutor(host, hc);
        var hiddenLabel = LabelOf(Labels(host, "f-cmp", "M", null), "Red 2");   // on the host's full list only
        FormulaVerdict Send(string label) =>
            executor.Execute("p2", Blue, new FormulaIntent("f-cmp", PhaseFixture.BlueOne, null, new Dictionary<string, string> { ["M"] = label }, false)).Verdict!;

        var refused = Send(hiddenLabel);
        Assert.False(refused.Applied);
        Assert.Equal("Waiting for: M", refused.Message);
        Assert.DoesNotContain(host.Game!.State.LedgerHistory, e => e.FormulaId == "f-cmp");

        var ok = Send(LabelOf(Labels(host, "f-cmp", "M", Blue), "Red 1"));
        Assert.True(ok.Applied, ok.Message);
        Assert.Equal(17, Ledger.Get(host.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
    }

    private static FormulaVerdict SendPick(IntentExecutor x, string label, IReadOnlyDictionary<string, string>? picks) =>
        x.Execute("p2", Blue, new FormulaIntent("f-cmp", PhaseFixture.BlueOne, null, new Dictionary<string, string> { ["M"] = label }, false,
            Picks: picks)).Verdict!;

    private static IReadOnlyDictionary<string, string> PickOf(string id) => new Dictionary<string, string> { ["M"] = id };

    /// <summary>Red 2 becomes a second "Red 1" on Red 1's spot (after it in state order): labels "{Red 1 label}" (r1) and "... #2" (r2).</summary>
    private static string TwinRedOne(ModuleSession host)
    {
        var st = host.Game!.State;
        var r1 = st.Pieces[PhaseFixture.RedOne];
        var r2 = st.Pieces[PhaseFixture.RedTwo];
        r2.TypeString = r1.TypeString;
        r2.TraitStates = r1.TraitStates?.ToList() ?? new();
        r2.MapId = r1.MapId;
        r2.Pos = r1.Pos;
        var labels = Quartermaster.Core.PieceChoices.For(st, host.Rules!, host.Model, PieceBinding(host, "f-cmp", "M"), Blue);
        Assert.EndsWith(" #2", labels.Single(c => c.PieceId == PhaseFixture.RedTwo).Label);
        return labels.Single(c => c.PieceId == PhaseFixture.RedOne).Label;
    }

    [Fact]
    public void Guest_pick_with_matching_id_applies()
    {
        var (host, hc) = Open(Difficulty.FullyAuto, new[] { Alice, Bob });
        AddPieceBindings(host);
        var label = LabelOf(Labels(host, "f-cmp", "M", Blue), "Red 1");
        var ok = SendPick(new IntentExecutor(host, hc), label, PickOf(PhaseFixture.RedOne));
        Assert.True(ok.Applied, ok.Message);
        Assert.Equal(17, Ledger.Get(host.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
    }

    [Fact]
    public void Guest_stale_label_resolving_to_another_piece_is_refused()
    {
        var (host, hc) = Open(Difficulty.FullyAuto, new[] { Alice, Bob });
        AddPieceBindings(host);
        var label = TwinRedOne(host);   // the guest picked r1 under the plain label
        host.Game!.State.Pieces.Remove(PhaseFixture.RedOne);   // r1 leaves on the host: the plain label is now r2's
        var refused = SendPick(new IntentExecutor(host, hc), label, PickOf(PhaseFixture.RedOne));
        Assert.False(refused.Applied);
        Assert.Equal(Quartermaster.Core.PieceChoices.Unknown, refused.Message);
        Assert.DoesNotContain(host.Game!.State.LedgerHistory, e => e.FormulaId == "f-cmp");
        Assert.Null(Ledger.Get(host.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
    }

    [Fact]
    public void Guest_intent_without_picks_still_resolves_by_label()
    {
        var (host, hc) = Open(Difficulty.FullyAuto, new[] { Alice, Bob });
        AddPieceBindings(host);
        var label = LabelOf(Labels(host, "f-cmp", "M", Blue), "Red 1");
        // An older guest's message has no "Picks" member: it still deserializes (the member is optional) and resolves by label.
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize<GameIntent>(
            new FormulaIntent("f-cmp", PhaseFixture.BlueOne, null, new Dictionary<string, string> { ["M"] = label }, false), QuartermasterJson.Options))!.AsObject();
        Assert.True(node.Remove("picks"));
        var json = node.ToJsonString();
        Assert.DoesNotContain("picks", json);
        var intent = Assert.IsType<FormulaIntent>(JsonSerializer.Deserialize<GameIntent>(json, QuartermasterJson.Options));
        Assert.Null(intent.Picks);
        var ok = new IntentExecutor(host, hc).Execute("p2", Blue, intent).Verdict!;
        Assert.True(ok.Applied, ok.Message);
        Assert.Equal(17, Ledger.Get(host.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
        // And a new message round-trips its picks.
        var sent = new FormulaIntent("f-cmp", PhaseFixture.BlueOne, null, new Dictionary<string, string> { ["M"] = label }, false, Picks: PickOf("r1"));
        var back = (FormulaIntent)JsonSerializer.Deserialize<GameIntent>(JsonSerializer.Serialize<GameIntent>(sent, QuartermasterJson.Options), QuartermasterJson.Options)!;
        Assert.Equal("r1", back.Picks!["M"]);
    }

    [Fact]
    public void Guest_pick_id_of_hidden_piece_is_refused_neutrally()
    {
        var (host, hc) = Open(Difficulty.FullyAuto, new[] { Alice, Bob });
        AddPieceBindings(host, hideRedTwo: true);
        var executor = new IntentExecutor(host, hc);
        var label = LabelOf(Labels(host, "f-cmp", "M", Blue), "Red 1");
        var hidden = SendPick(executor, label, PickOf(PhaseFixture.RedTwo));   // Red 2 is hidden from Blue
        var missing = SendPick(executor, label, PickOf("no-such-piece"));
        var hiddenLabel = SendPick(executor, LabelOf(Labels(host, "f-cmp", "M", null), "Red 2"), PickOf(PhaseFixture.RedTwo));
        foreach (var v in new[] { hidden, missing, hiddenLabel })
        {
            Assert.False(v.Applied);
            Assert.Equal(Quartermaster.Core.PieceChoices.Unknown, v.Message);
            Assert.Empty(v.Boxes);
        }
        Assert.DoesNotContain(host.Game!.State.LedgerHistory, e => e.FormulaId == "f-cmp");
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
