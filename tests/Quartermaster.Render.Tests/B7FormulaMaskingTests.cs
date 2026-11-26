using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B7: formula masking decides through the one visibility rule (<see cref="HiddenValues"/>): a Public value of another side's holder is
/// seen (binding and holder(H, P)), a game that is over hides nothing (and gives no right to act), and a binding whose own expressions read a
/// hidden variable is hidden too. Invented Red/Blue names and numbers only.
/// </summary>
[Collection(AppDataRootEnv.Name)]
public class B7FormulaMaskingTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-b7mask-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();
    internal const double Secret = 47474;
    internal static readonly string SecretText = Secret.ToString(CultureInfo.InvariantCulture);
    internal static readonly IReadOnlySet<string> Blue = new HashSet<string> { "blue" };
    private static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });
    private static readonly PlayerDef Bob = new("p2", "Bob", new[] { "blue" });
    internal const string Global = "h:global";
    internal const string Depot = "h:reddepot";

    public B7FormulaMaskingTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static VariableBinding W => new() { Variable = "W", Kind = BindingKind.Unit, Property = "water" };
    internal static FormulaOutput ToWater => new() { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set };
    internal static FormulaOutput ToMines => new() { Target = OutputTarget.Global, Property = "mines", Op = OutputOp.Set, Key = "hex()" };

    private static VariableBinding DepotBinding(string variable, string property) =>
        new() { Variable = variable, Kind = BindingKind.Holder, HolderId = "reddepot", Property = property };

    /// <summary>Red's depot keeps "Cash" (Public) and "Plans" (Private); "Mines" is a Private place; "Morale" a Public piece value.</summary>
    internal static RulesPack Pack(string sha, params FormulaDef[] extra)
    {
        var p = PhaseFixture.Pack(sha);
        return p with
        {
            Format = 4,
            Properties = p.Properties.Concat(new[]
            {
                new PropertyDef { Id = "cash", Name = "Cash", Scope = PropertyScope.Holders, Visibility = Visibility.Public },
                new PropertyDef { Id = "plans", Name = "Plans", Scope = PropertyScope.Holders, Visibility = Visibility.Private },
                new PropertyDef { Id = "mines", Name = "Mines", Scope = PropertyScope.Holders, KeyedBy = "hex", Visibility = Visibility.Private },
                new PropertyDef { Id = "morale", Name = "Morale", Scope = PropertyScope.Pieces, Visibility = Visibility.Public, Default = 5 },
            }).ToList(),
            Holders = new() { new HolderDef { Id = "reddepot", Name = "RedDepot", SideId = "red" } },
            Formulas = p.Formulas.Concat(new[]
            {
                new FormulaDef { Id = "f-cash", Name = "Red cash", Text = "C + W", Bindings = new() { W, DepotBinding("C", "cash") }, Outputs = new() { ToWater } },
                new FormulaDef { Id = "f-plans", Name = "Red plans", Text = "C + W", Bindings = new() { W, DepotBinding("C", "plans") }, Outputs = new() { ToWater } },
                new FormulaDef { Id = "f-cash-call", Name = "Red cash call", Text = "holder(RedDepot, Cash) + W", Bindings = new() { W }, Outputs = new() { ToWater } },
                new FormulaDef { Id = "f-plans-call", Name = "Red plans call", Text = "holder(RedDepot, Plans) + W", Bindings = new() { W }, Outputs = new() { ToWater } },
                new FormulaDef
                {
                    // Two levels of derived values, listed out of order so only a fixpoint finds both.
                    Id = "f-derived", Name = "Derived", Text = "C + W",
                    Bindings = new()
                    {
                        W,
                        new VariableBinding { Variable = "C", Kind = BindingKind.Input, AutoValue = "B * 2" },
                        new VariableBinding { Variable = "B", Kind = BindingKind.Input, AutoValue = "A + 1" },
                        DepotBinding("A", "plans"),
                    },
                    Outputs = new() { ToWater },
                },
                new FormulaDef
                {
                    // A Piece binding whose "Only when" reads the hidden value: the pick is the viewer's own and stays open.
                    Id = "f-pick", Name = "Guarded pick", Text = "P + W",
                    Bindings = new()
                    {
                        W, DepotBinding("A", "plans"),
                        new VariableBinding { Variable = "P", Kind = BindingKind.Piece, Property = "morale", When = "A > 0" },
                    },
                    Outputs = new() { ToWater },
                },
                new FormulaDef
                {
                    // G is asked only when Red's Private plans exceed the threshold T Blue types: a guard over hidden data.
                    Id = "f-guard", Name = "Raid guard", Text = "W + if(A > T, G, 0)",
                    Bindings = new()
                    {
                        W, DepotBinding("A", "plans"),
                        new VariableBinding { Variable = "T", Kind = BindingKind.Input },
                        new VariableBinding { Variable = "G", Kind = BindingKind.Input, When = "A > T" },
                    },
                    Outputs = new() { ToWater },
                },
                new FormulaDef
                {
                    // The text compares the hidden plans with a value Blue types; the result goes to Blue's own unit.
                    Id = "f-compare", Name = "Plans probe", Text = "W - if(A > T, 1000, 0)",
                    Bindings = new() { W, DepotBinding("A", "plans"), new VariableBinding { Variable = "T", Kind = BindingKind.Input } },
                    Outputs = new() { ToWater },
                },
                new FormulaDef
                {
                    // The precondition compares the hidden plans with a value Blue types.
                    Id = "f-pre", Name = "Plans gate", Text = "W + 1", Precondition = "A > T", RefusalMessage = "Red has no plans to raid",
                    Bindings = new() { W, DepotBinding("A", "plans"), new VariableBinding { Variable = "T", Kind = BindingKind.Input } },
                    Outputs = new() { ToWater },
                },
                new FormulaDef
                {
                    // A guarded Input with a range and one with listed choices: their typed values are validated.
                    Id = "f-guard-checked", Name = "Checked raid", Text = "W + if(A > T, G + H, 0)",
                    Bindings = new()
                    {
                        W, DepotBinding("A", "plans"),
                        new VariableBinding { Variable = "T", Kind = BindingKind.Input },
                        new VariableBinding { Variable = "G", Kind = BindingKind.Input, When = "A > T", Min = 0, Max = 100 },
                        new VariableBinding { Variable = "H", Kind = BindingKind.Input, When = "A > T", Choices = new() { "1", "2" } },
                    },
                    Outputs = new() { ToWater },
                },
                new FormulaDef
                {
                    // An auto value that reads the hidden value through holder(): no hidden variable is named.
                    Id = "f-auto-call", Name = "Plans input", Text = "X + W",
                    Bindings = new() { W, new VariableBinding { Variable = "X", Kind = BindingKind.Input, AutoValue = "holder(RedDepot, Plans)" } },
                    Outputs = new() { ToWater },
                },
            }).Concat(extra).ToList(),
        };
    }

    internal static ModuleSession Open(string tmp, List<FixtureModule> fixtures, List<ModuleSession> sessions, params FormulaDef[] extra)
    {
        var fx = PhaseFixture.Module(numbered: true);
        fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(tmp, "cache" + sessions.Count), Path.Combine(tmp, "root" + sessions.Count));
        sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.SemiAuto, new[] { Alice, Bob });
        s.NewRules("t");
        s.UpdateRules(_ => Pack(s.Archive.Sha256, extra));
        foreach (var prop in new[] { "cash", "plans" })
            s.Game!.Execute(new LedgerDelta(Depot, prop, 0, Secret, "manual", null, null, null, null));
        foreach (var piece in new[] { PhaseFixture.BlueOne, PhaseFixture.RedOne })
        {
            var hex = PieceInfo.Location(s.Game!.State.Pieces[piece], s.Model)!;
            s.Game.Execute(new LedgerDelta(Global, "mines", 0, Secret, "manual", null, null, null, null) { ValueKey = hex });
        }
        return s;
    }

    private ModuleSession Open() => Open(_tmp, _fixtures, _sessions);

    private static HashSet<string> Hidden(ModuleSession s, string formulaId, IReadOnlySet<string>? sides = null) =>
        FormulaMasking.HiddenVariables(s.Rules!, s.Model, s.Game!.State, s.Rules!.ById<FormulaDef>(formulaId)!, PhaseFixture.BlueOne, sides ?? Blue);

    [Fact]
    public void Holder_binding_public_property_of_other_side_not_hidden()
    {
        var s = Open();
        Assert.Empty(Hidden(s, "f-cash"));
        s.SetViewSides(Blue);
        var m = new FormulaDialogModel(s.Runner!, s.Rules!.ById<FormulaDef>("f-cash")!, PhaseFixture.BlueOne);
        FormulaMasking.Configure(m, s);
        var v = m.Evaluate(s.Game!.State);
        Assert.Equal(SecretText, v.Boxes.Single(b => b.Variable == "C").Text);
        Assert.Equal(v.Trace.Working, v.WorkingText);
    }

    [Fact]
    public void Holder_binding_private_property_of_other_side_hidden()
    {
        var s = Open();
        Assert.Equal(new[] { "C" }, Hidden(s, "f-plans").ToArray());
        // Red holds the depot: its own Private value is seen.
        Assert.DoesNotContain("C", Hidden(s, "f-plans", new HashSet<string> { "red" }));
    }

    [Fact]
    public void Holder_call_public_property_not_masked()
    {
        var s = Open();
        Assert.Empty(Hidden(s, "f-cash-call"));
        Assert.Equal(new[] { FormulaMasking.HolderCallInput }, Hidden(s, "f-plans-call").ToArray());
    }

    [Fact]
    public void Auto_value_reading_hidden_variable_hidden()
    {
        var s = Open();
        var hidden = Hidden(s, "f-derived");
        Assert.Equal(new[] { "A", "B", "C" }, hidden.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        s.SetViewSides(Blue);
        var m = new FormulaDialogModel(s.Runner!, s.Rules!.ById<FormulaDef>("f-derived")!, PhaseFixture.BlueOne);
        FormulaMasking.Configure(m, s);
        // Calculate fills auto values into the boxes: a derived box would show the hidden value.
        m.Calculate(s.Game!.State);
        var v = m.Evaluate(s.Game.State);
        Assert.All(v.Boxes.Where(b => b.Variable is "A" or "B"), b => Assert.Equal(FormulaMasking.HiddenText, b.Text));
        Assert.Equal(FormulaMasking.HiddenText, v.Boxes.Single(b => b.Variable == "C").Text);
        var shown = string.Join("|", v.Boxes.Select(b => b.Text + "|" + b.Tooltip)) + "|" + v.ResultText + "|" + v.WorkingText + "|" + v.Status;
        Assert.DoesNotContain((Secret + 1).ToString(CultureInfo.InvariantCulture), shown);
        Assert.DoesNotContain(((Secret + 1) * 2).ToString(CultureInfo.InvariantCulture), shown);
        // The derived values are not hidden from the depot's own side.
        Assert.Equal(new[] { "W" }, Hidden(s, "f-derived", new HashSet<string> { "red" }).ToArray());   // only Blue's unit
    }

    [Fact]
    public void Piece_binding_not_hidden_by_fixpoint()
    {
        var s = Open();
        var hidden = Hidden(s, "f-pick");
        Assert.Contains("A", hidden);
        Assert.DoesNotContain("P", hidden);
    }

    [Fact]
    public void Game_over_nothing_hidden()
    {
        var s = Open();
        s.Game!.Execute(new EndGame("Blue holds the river", new[] { "blue" }, null));
        foreach (var id in new[] { "f-plans", "f-plans-call", "f-derived", "f-pick" })
            Assert.Empty(Hidden(s, id));
        s.SetViewSides(Blue);
        var hex = PieceInfo.Location(s.Game.State.Pieces[PhaseFixture.BlueOne], s.Model)!;
        var mines = new OutputPreview(Global, "mines", "Mines", Secret, 1) { Target = OutputTarget.Global, ValueKey = hex };
        Assert.True(FormulaMasking.OutputShown(s, Blue, mines));
        var depot = new OutputPreview(Depot, "plans", "Plans", Secret, 1) { Target = OutputTarget.Holder };
        Assert.True(FormulaMasking.OutputShown(s, Blue, depot));
        // Undo out of game over hides again.
        s.Game.Undo();
        Assert.Equal(new[] { "C" }, Hidden(s, "f-plans").ToArray());
        Assert.False(FormulaMasking.OutputShown(s, Blue, mines));
    }

    [Fact]
    public void Game_over_guest_still_refused_other_side_unit()
    {
        var s = Open();
        s.Game!.Execute(new EndGame("Blue holds the river", new[] { "blue" }, null));
        var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        var x = new IntentExecutor(s, c);
        var before = Ledger.Get(s.Game.State, LedgerKey.Piece(PhaseFixture.RedOne), "water");
        var r = x.Execute("p2", Blue, new FormulaIntent("f-water", PhaseFixture.RedOne, null, new Dictionary<string, string>(), true));
        Assert.NotNull(r.Verdict);
        Assert.False(r.Verdict!.Applied);
        Assert.Equal("That unit belongs to a side you don't hold.", r.Verdict.Message);
        Assert.Equal(before, Ledger.Get(s.Game.State, LedgerKey.Piece(PhaseFixture.RedOne), "water"));
    }
    private static FormulaDialogModel HotSeatDialog(ModuleSession s, string formulaId)
    {
        var m = new FormulaDialogModel(s.Runner!, s.Rules!.ById<FormulaDef>(formulaId)!, PhaseFixture.BlueOne);
        FormulaMasking.Configure(m, s);
        return m;
    }

    /// <summary>Everything a viewer sees of a dialog except the boxes named in <paramref name="except"/> (the viewer's own typing).</summary>
    private static string Seen(DialogView v, params string[] except) =>
        string.Join("|", v.Boxes.Where(b => !except.Contains(b.Variable))
            .Select(b => $"{b.Variable}:{b.Text}:{b.Enabled}:{b.Tooltip}:{b.CaptionSuffix}:{b.Background}:{b.Wrong}"))
        + "|" + v.ResultText + "|" + v.WorkingText + "|" + v.WorkingNegative + "|" + v.Status + "|" + v.CanApply;

    private const string Huge = "99999999";   // above the hidden plans: the guard is false

    [Fact]
    public void HotSeat_guard_over_hidden_value_looks_the_same_either_way()
    {
        var s = Open();
        s.SetViewSides(Blue);
        Assert.Contains("G", FormulaMasking.GuardsOnHidden(s.Rules!.ById<FormulaDef>("f-guard")!, Hidden(s, "f-guard")));
        foreach (var g in new[] { "", "5" })
        {
            var seen = new List<string>();
            var traces = new List<FormulaTrace>();
            foreach (var threshold in new[] { "0", Huge })
            {
                var m = HotSeatDialog(s, "f-guard");
                m.SetBoxText("T", threshold);
                if (g.Length > 0) m.SetBoxText("G", g);
                var v = m.Evaluate(s.Game!.State);
                var box = v.Boxes.Single(b => b.Variable == "G");
                Assert.True(box.Enabled);
                Assert.NotEqual(FormulaDialogModel.NotUsedText, box.Text);
                seen.Add(Seen(v, "T"));
                traces.Add(v.Trace);
                // Calculate fills nothing into the guard box either way.
                m.Calculate(s.Game.State);
                seen.Add(Seen(m.Evaluate(s.Game.State), "T"));
            }
            Assert.Equal(seen[0], seen[2]);
            Assert.Equal(seen[1], seen[3]);
            if (g.Length == 0)
            {
                Assert.Contains("Waiting for: G", seen[0]);
                Assert.EndsWith("|False", seen[0]);
            }
            else
            {
                // The engine decides: G is used when the plans exceed T, ignored otherwise.
                Assert.EndsWith("|True", seen[0]);
                Assert.Equal(15, traces[0].Result!.Value.Number);
                Assert.Equal(10, traces[1].Result!.Value.Number);
            }
        }
    }

    [Fact]
    public void HotSeat_guard_run_applies_correctly_both_ways()
    {
        foreach (var (threshold, water) in new[] { ("0", 15.0), (Huge, 10.0) })
        {
            var s = Open();
            s.SetViewSides(Blue);
            var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
            var m = HotSeatDialog(s, "f-guard");
            m.SetBoxText("T", threshold);
            m.SetBoxText("G", "5");
            var v = m.Evaluate(s.Game!.State);
            Assert.True(v.CanApply);
            Assert.True(c.ApplyFormula(null, PhaseFixture.BlueOne, v.Trace, false));
            Assert.Equal(water, Ledger.Get(s.Game.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
        }
    }

    [Fact]
    public void Guest_guard_verdict_same_either_way()
    {
        foreach (var g in new[] { null, "5" })
        {
            var answers = new List<string>();
            foreach (var preview in new[] { true, false })
            foreach (var threshold in new[] { "0", Huge })
            {
                var s = Open();
                var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
                var x = new IntentExecutor(s, c);
                var typed = new Dictionary<string, string> { ["T"] = threshold, ["W"] = "10" };   // W: the manual mode's checked box
                if (g != null) typed["G"] = g;
                var r = x.Execute("p2", Blue, new FormulaIntent("f-guard", PhaseFixture.BlueOne, null, typed, false, preview));
                var v = r.Verdict!;
                Assert.DoesNotContain(SecretText, System.Text.Json.JsonSerializer.Serialize(v, QuartermasterJson.Options));
                var values = (v.Values ?? new Dictionary<string, string>()).Where(kv => kv.Key != "T").OrderBy(kv => kv.Key, StringComparer.Ordinal);
                answers.Add($"{preview}|{v.Applied}|{v.Message}|{string.Join(",", v.Boxes.Select(b => b.Var + "=" + b.State))}|{string.Join(",", values)}|{v.Rolls?.Count}");
                if (!preview && g != null)
                    Assert.Equal(threshold == "0" ? 15 : 10, Ledger.Get(s.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
            }
            Assert.Equal(answers[0], answers[1]);   // preview
            Assert.Equal(answers[2], answers[3]);   // apply
            if (g == null) Assert.Contains("Waiting for: G", answers[2]);
            else Assert.Contains("|True|", answers[2]);
        }
    }

    [Fact]
    public void HotSeat_auto_value_holder_call_hidden_on_calculate()
    {
        var s = Open();
        s.SetViewSides(Blue);
        var m = HotSeatDialog(s, "f-auto-call");
        Assert.Contains("X", m.Hidden);
        m.Calculate(s.Game!.State);
        var v = m.Evaluate(s.Game.State);
        Assert.Equal(FormulaMasking.HiddenText, v.Boxes.Single(b => b.Variable == "X").Text);
        Assert.DoesNotContain(SecretText, Seen(v));
        // The engine reads the hidden Input: the run completes and writes Blue's own water (Secret + 10).
        Assert.Equal(Secret + 10, v.Trace.Result!.Value.Number);
    }

    [Fact]
    public void HotSeat_derived_hidden_input_runs_and_applies()
    {
        var s = Open();
        s.SetViewSides(Blue);
        var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        var m = HotSeatDialog(s, "f-derived");
        var v = m.Evaluate(s.Game!.State);   // no Calculate: the hidden Inputs are read by the engine on every run
        Assert.All(v.Boxes.Where(b => b.Variable is "A" or "B" or "C"), b => Assert.Equal(FormulaMasking.HiddenText, b.Text));
        Assert.Equal((Secret + 1) * 2 + 10, v.Trace.Result!.Value.Number);
        Assert.True(v.CanApply, v.Status);
        Assert.DoesNotContain(((Secret + 1) * 2).ToString(CultureInfo.InvariantCulture), Seen(v));
        Assert.True(c.ApplyFormula(null, PhaseFixture.BlueOne, v.Trace, false));
        Assert.Equal((Secret + 1) * 2 + 10, Ledger.Get(s.Game.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
    }
    /// <summary>The dialog of <paramref name="formulaId"/> for a Blue hot-seat viewer with <paramref name="typed"/> boxes, evaluated.</summary>
    private static (FormulaDialogModel M, DialogView V) Typed(ModuleSession s, string formulaId, params (string Var, string Text)[] typed)
    {
        var m = HotSeatDialog(s, formulaId);
        foreach (var (v, t) in typed) m.SetBoxText(v, t);
        return (m, m.Evaluate(s.Game!.State));
    }

    [Fact]
    public void HotSeat_retyping_a_value_compared_with_a_hidden_one_previews_nothing()
    {
        var s = Open();
        s.SetViewSides(Blue);
        var (_, low) = Typed(s, "f-compare", ("T", "0"));
        var (_, high) = Typed(s, "f-compare", ("T", Huge));
        Assert.Equal(10 - 1000, low.Trace.Result!.Value.Number);   // the runs differ
        Assert.Equal(10, high.Trace.Result!.Value.Number);
        Assert.Equal(Seen(low, "T"), Seen(high, "T"));
        Assert.Equal(FormulaMasking.HiddenText, low.ResultText);
        Assert.False(low.WorkingNegative);   // the write would go below zero, but the mark is not previewed
        Assert.DoesNotContain("(negative)", low.WorkingText);
    }

    [Fact]
    public void HotSeat_precondition_over_hidden_value_is_decided_at_apply()
    {
        var s = Open();
        s.SetViewSides(Blue);
        var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        var (_, pass) = Typed(s, "f-pre", ("T", "0"));
        var (_, refused) = Typed(s, "f-pre", ("T", Huge));
        Assert.False(pass.Trace.Refused);
        Assert.True(refused.Trace.Refused);   // the real run is refused
        Assert.Equal(Seen(pass, "T"), Seen(refused, "T"));
        Assert.True(refused.CanApply);
        Assert.DoesNotContain("Red has no plans", Seen(refused));
        // Apply: the refused run writes nothing and says only the generic text; the passing one applies.
        Assert.Equal(FormulaMasking.ErrorHiddenText, FormulaDialogModel.ApplyRefusal(refused));
        Assert.Equal("The formula could not be calculated.", FormulaDialogModel.ApplyRefusal(refused));
        Assert.False(c.ApplyFormula(null, PhaseFixture.BlueOne, refused.Trace, false));
        Assert.Equal(10, Ledger.Get(s.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water") ?? 10);
        Assert.Null(FormulaDialogModel.ApplyRefusal(pass));
        Assert.True(c.ApplyFormula(null, PhaseFixture.BlueOne, pass.Trace, false));
        Assert.Equal(11, Ledger.Get(s.Game.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
        // Holding the depot's side, the precondition is the viewer's own: refused before Apply as always.
        s.SetViewSides(new HashSet<string> { "red", "blue" });
        var (_, own) = Typed(s, "f-pre", ("T", Huge));
        Assert.False(own.CanApply);
    }

    [Fact]
    public void Guest_precondition_over_hidden_value_same_preview_generic_refusal()
    {
        var answers = new List<string>();
        foreach (var threshold in new[] { "0", Huge })
        {
            var s = Open();
            var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
            var x = new IntentExecutor(s, c);
            var typed = new Dictionary<string, string> { ["T"] = threshold, ["W"] = "10" };
            var preview = x.Execute("p2", Blue, new FormulaIntent("f-pre", PhaseFixture.BlueOne, null, typed, false, true)).Verdict!;
            var values = (preview.Values ?? new Dictionary<string, string>()).Where(kv => kv.Key != "T").OrderBy(kv => kv.Key, StringComparer.Ordinal);
            answers.Add($"{preview.Applied}|{preview.Message}|{string.Join(",", preview.Boxes.Select(b => b.Var + "=" + b.State))}|{string.Join(",", values)}");
            var apply = x.Execute("p2", Blue, new FormulaIntent("f-pre", PhaseFixture.BlueOne, null, typed, false)).Verdict!;
            var json = System.Text.Json.JsonSerializer.Serialize(apply, QuartermasterJson.Options);
            Assert.DoesNotContain("Red has no plans", json);
            if (threshold == "0")
            {
                Assert.True(apply.Applied, apply.Message);
                Assert.Equal(11, Ledger.Get(s.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
            }
            else
            {
                Assert.False(apply.Applied);
                Assert.Equal(FormulaMasking.ErrorHiddenText, apply.Message);
                Assert.Null(Ledger.Get(s.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
            }
        }
        Assert.Equal(answers[0], answers[1]);
    }

    [Fact]
    public void HotSeat_guard_box_validation_same_either_way()
    {
        var s = Open();
        s.SetViewSides(Blue);
        foreach (var (g, h) in new[] { ("abc", "1"), ("500", "1"), ("5", "3"), ("5", "2") })
        {
            var (_, on) = Typed(s, "f-guard-checked", ("T", "0"), ("G", g), ("H", h));
            var (_, off) = Typed(s, "f-guard-checked", ("T", Huge), ("G", g), ("H", h));
            Assert.Equal(Seen(on, "T"), Seen(off, "T"));
            if (g == "abc") Assert.Contains("Not a number", Seen(off, "T"));
            if (h == "3") Assert.Contains("Choose one of 1, 2", Seen(off, "T"));
            if (g == "5" && h == "2") Assert.True(off.CanApply);
            else Assert.False(off.CanApply);
        }
    }
}
