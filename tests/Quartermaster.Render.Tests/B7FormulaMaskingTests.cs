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
                new PropertyDef { Id = "spies", Name = "Spies", Scope = PropertyScope.Holders, Visibility = Visibility.Private },
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
                    // A guarded Input whose choices (a table filtered for this counter) are empty.
                    Id = "f-guard-none", Name = "Empty raid", Text = "W + if(A > T, lookup(TestTerrainCost, K), 0)",
                    Bindings = new()
                    {
                        W, DepotBinding("A", "plans"),
                        new VariableBinding { Variable = "T", Kind = BindingKind.Input },
                        new VariableBinding
                        {
                            Variable = "K", Kind = BindingKind.Input, When = "A > T", ChoicesTable = "t-cost",
                            ChoicesFilter = "lookup(TestTerrainCost, row()) > 100",
                        },
                    },
                    Outputs = new() { ToWater },
                },
                new FormulaDef
                {
                    // A guarded Input choosing a row of a table (Clear, Sand).
                    Id = "f-guard-table", Name = "Table raid", Text = "W + if(A > T, lookup(TestTerrainCost, K), 0)",
                    Bindings = new()
                    {
                        W, DepotBinding("A", "plans"),
                        new VariableBinding { Variable = "T", Kind = BindingKind.Input },
                        new VariableBinding { Variable = "K", Kind = BindingKind.Input, When = "A > T", ChoicesTable = "t-cost" },
                    },
                    Outputs = new() { ToWater },
                },
                new FormulaDef
                {
                    // As many dice as Red's (Private) spies: the count is hidden.
                    Id = "f-dice-count", Name = "Spy dice", Text = "W + dice(S, 6)",
                    Bindings = new() { W, DepotBinding("S", "spies") },
                    Outputs = new() { ToWater },
                },
                new FormulaDef
                {
                    // A die rolled only when Red's spies exceed T: whether it is rolled follows the hidden value.
                    Id = "f-dice-if", Name = "Spy check", Text = "W + if(S > T, dice(1, 6), 0)",
                    Bindings = new() { W, DepotBinding("S", "spies"), new VariableBinding { Variable = "T", Kind = BindingKind.Input } },
                    Outputs = new() { ToWater },
                },
                new FormulaDef
                {
                    // Raises the Global (Public) cash once the Private place of the counter's hex passes T: a menu batch chains it to the next row.
                    Id = "f-chain", Name = "Mined cash", Text = "G + 1 + W * 0", Precondition = "keyed(Mines, hex()) > T",
                    Bindings = new()
                    {
                        W,
                        new VariableBinding { Variable = "G", Kind = BindingKind.Global, Property = "cash" },
                        new VariableBinding { Variable = "T", Kind = BindingKind.Input },
                    },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "cash", Op = OutputOp.Set } },
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
        s.Game!.Execute(new LedgerDelta(Depot, "spies", 0, 3, "manual", null, null, null, null));
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
    /// <summary>A guest's preview and apply verdicts for <paramref name="formulaId"/> with <paramref name="typed"/>, as one comparable line
    /// (T, the threshold the guest typed, left out of the values).</summary>
    private string GuestAnswers(string formulaId, Dictionary<string, string> typed)
    {
        var s = Open();
        var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        var x = new IntentExecutor(s, c);
        var line = new List<string>();
        foreach (var preview in new[] { true, false })
        {
            var v = x.Execute("p2", Blue, new FormulaIntent(formulaId, PhaseFixture.BlueOne, null, typed, false, preview)).Verdict!;
            var values = (v.Values ?? new Dictionary<string, string>()).Where(kv => kv.Key != "T").OrderBy(kv => kv.Key, StringComparer.Ordinal);
            line.Add($"{preview}|{v.Applied}|{v.Message}|{string.Join(",", v.Boxes.Select(b => b.Var + "=" + b.State))}|{string.Join(",", values)}|{v.Rolls?.Count}");
        }
        return string.Join(" / ", line);
    }

    [Fact]
    public void Guest_guard_box_invalid_choice_same_verdict_either_way()
    {
        foreach (var h in new[] { "3", "2" })
        {
            Dictionary<string, string> Typed(string t) => new() { ["T"] = t, ["W"] = "10", ["G"] = "5", ["H"] = h };
            var on = GuestAnswers("f-guard-checked", Typed("0"));
            var off = GuestAnswers("f-guard-checked", Typed(Huge));
            Assert.Equal(on, off);
            Assert.DoesNotContain(SecretText, on);
        }
        // A key not among the table's choices: the neutral "not one of the choices" text whether or not the guard holds.
        foreach (var k in new[] { "Iron", "Sand" })
        {
            Dictionary<string, string> Typed(string t) => new() { ["T"] = t, ["W"] = "10", ["K"] = k };
            var on = GuestAnswers("f-guard-table", Typed("0"));
            Assert.Equal(on, GuestAnswers("f-guard-table", Typed(Huge)));
            if (k == "Iron") Assert.Contains(InputChoices.NotOneText, on);
        }
    }

    [Fact]
    public void Guest_guard_box_with_no_choice_same_verdict_either_way()
    {
        Dictionary<string, string> Typed(string t) => new() { ["T"] = t, ["W"] = "10", ["K"] = "Clear" };
        var on = GuestAnswers("f-guard-none", Typed("0"));
        var off = GuestAnswers("f-guard-none", Typed(Huge));
        Assert.Equal(on, off);
        Assert.Contains(InputChoices.NoChoiceText, on);   // the forced run validates the box whether or not the guard holds
    }

    [Fact]
    public void Masked_dice_not_shown_or_sent_before_apply()
    {
        var s = Open();
        s.SetViewSides(Blue);
        // dice(S, 6): the number of dice is Red's spies.
        var (_, count) = Typed(s, "f-dice-count");
        Assert.Equal(3, count.Trace.Rolls.Sum(r => r.Results.Count));   // the run rolled them
        Assert.Empty(count.InlineRolls);
        Assert.DoesNotContain("3", count.ResultText);
        // if(S > T, dice(1, 6), 0): whether a die is rolled follows the hidden value.
        var (_, rolled) = Typed(s, "f-dice-if", ("T", "0"));
        var (_, none) = Typed(s, "f-dice-if", ("T", "99"));
        Assert.NotEmpty(rolled.Trace.Rolls);
        Assert.Empty(none.Trace.Rolls);
        Assert.Empty(rolled.InlineRolls);
        Assert.Equal(Seen(rolled, "T"), Seen(none, "T"));
        // A guest's preview carries no dice and the same answer either way; the host keeps its pins (asking again rolls nothing new).
        Assert.Equal(GuestAnswers("f-dice-if", new() { ["T"] = "0", ["W"] = "10" }), GuestAnswers("f-dice-if", new() { ["T"] = "99", ["W"] = "10" }));
        var host = Open();
        var rolls = 0;
        host.SetRoller((n, _) => { rolls += n; return Enumerable.Repeat(4, n).ToList(); });
        var x = new IntentExecutor(host, new PhaseController(host, (cmd, st) => CommandLog.Prepare(cmd, st, host)));
        var typed = new Dictionary<string, string> { ["W"] = "10" };
        var p1 = x.Execute("p2", Blue, new FormulaIntent("f-dice-count", PhaseFixture.BlueOne, null, typed, true, true)).Verdict!;
        Assert.Null(p1.Rolls);
        Assert.Null(x.Execute("p2", Blue, new FormulaIntent("f-dice-count", PhaseFixture.BlueOne, null, typed, true, true)).Verdict!.Rolls);
        Assert.Equal(3, rolls);   // pinned on the host: the second preview rolled nothing
        Assert.True(x.Execute("p2", Blue, new FormulaIntent("f-dice-count", PhaseFixture.BlueOne, null, typed, false)).Verdict!.Applied);
        Assert.Equal(3, rolls);   // the Apply used the pinned dice
        Assert.Equal(10 + 12, Ledger.Get(host.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
    }

    [Fact]
    public void Menu_batch_next_row_same_whether_hidden_check_passed()
    {
        string RowOne(string threshold)
        {
            var s = Open();
            s.SetViewSides(new HashSet<string> { "red" });
            var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
            var b = new MenuBatchModel(s, c, "f-chain", new[] { PhaseFixture.RedOne, PhaseFixture.RedTwo });
            b.SetCell(0, "T", threshold);   // r1's hex holds the Private mines: T = 0 passes, T huge does not
            b.SetCell(0, "G", "0");
            b.SetCell(0, "W", "10");
            b.SetCell(1, "T", "0");
            b.SetCell(1, "G", "1");   // what row 1 reads if row 0 applied
            b.SetCell(1, "W", "10");
            var r0 = b.Row(0);
            Assert.True(r0.CanApply, string.Join(",", r0.Cells) + "|" + string.Join(",", r0.Wrong) + "|" + r0.Exceptions + "|" + b.Variables.Count);   // a hidden precondition is decided at Apply
            var r = b.Row(1);
            return $"{string.Join(",", r.Cells)}|{string.Join(",", r.Wrong)}|{r.Result}|{r.LedgerText}|{r.Exceptions}|{r.CanApply}";
        }
        Assert.Equal(RowOne("0"), RowOne(Huge));
    }

    // ---- final review wave ----

    /// <summary>F1: Q has a (visible) auto value; the precondition compares Red's Private plans with the T Blue types.</summary>
    private static FormulaDef PreAuto => new()
    {
        Id = "f-pre-auto", Name = "Plans gate auto", Text = "W + Q", Precondition = "A > T", RefusalMessage = "Red has no plans to raid",
        Bindings = new() { W, DepotBinding("A", "plans"), new VariableBinding { Variable = "T", Kind = BindingKind.Input },
            new VariableBinding { Variable = "Q", Kind = BindingKind.Input, AutoValue = "2" } },
        Outputs = new() { ToWater },
    };

    [Fact]
    public void HotSeat_calculate_fills_the_same_boxes_whether_hidden_precondition_holds()
    {
        var s = Open(_tmp, _fixtures, _sessions, PreAuto);
        s.SetViewSides(Blue);
        foreach (var manual in new[] { false, true })
        {
            var seen = new List<string>();
            foreach (var threshold in new[] { "0", Huge })
            {
                var m = HotSeatDialog(s, "f-pre-auto");
                m.Checked = manual;
                m.SetBoxText("T", threshold);
                m.Calculate(s.Game!.State);
                var v = m.Evaluate(s.Game.State);
                Assert.Equal("2", v.Boxes.Single(b => b.Variable == "Q").Text);
                if (manual) Assert.Equal("10", v.Boxes.Single(b => b.Variable == "W").Text);   // the checked box is filled either way
                seen.Add(Seen(v, "T"));
            }
            Assert.Equal(seen[0], seen[1]);
        }
    }

    [Fact]
    public void Guest_calculate_preview_same_verdict_whether_hidden_precondition_holds()
    {
        var answers = new List<string>();
        foreach (var threshold in new[] { "0", Huge })
        {
            var s = Open(_tmp, _fixtures, _sessions, PreAuto);
            var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
            var x = new IntentExecutor(s, c);
            var typed = new Dictionary<string, string> { ["T"] = threshold };
            var v = x.Execute("p2", Blue, new FormulaIntent("f-pre-auto", PhaseFixture.BlueOne, null, typed, true, true)).Verdict!;
            var values = (v.Values ?? new Dictionary<string, string>()).Where(kv => kv.Key != "T").OrderBy(kv => kv.Key, StringComparer.Ordinal);
            answers.Add($"{v.Applied}|{v.Message}|{string.Join(",", v.Boxes.Select(b => b.Var + "=" + b.State))}|{string.Join(",", values)}");
        }
        Assert.Equal(answers[0], answers[1]);
        Assert.Contains("[Q, 2]", answers[0]);   // Calculate filled Q either way
    }

    /// <summary>F2 / F6(a): a Roll whose count is Red's Private spies, and an Input whose auto value reads that Roll.</summary>
    private static FormulaDef SpyRoll => new()
    {
        Id = "f-spy-roll", Name = "Spy roll", Text = "W + R + B",
        Bindings = new()
        {
            W, DepotBinding("S", "spies"),
            new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCountExpr = "S", DiceSides = 6 },
            new VariableBinding { Variable = "B", Kind = BindingKind.Input, AutoValue = "R + 1" },
        },
        Outputs = new() { ToWater },
    };

    [Fact]
    public void HotSeat_physical_dice_hidden_roll_is_rolled_by_the_engine()
    {
        var s = Open(_tmp, _fixtures, _sessions, SpyRoll);
        s.SetViewSides(Blue);
        var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        var m = HotSeatDialog(s, "f-spy-roll");
        m.PhysicalDice = true;
        Assert.Contains("R", m.Hidden);
        var v = m.Evaluate(s.Game!.State);
        var box = v.Boxes.Single(b => b.Variable == "R");
        Assert.Equal(FormulaMasking.HiddenText, box.Text);
        Assert.False(box.Enabled);
        // 3 spies, every die a 3: R = 9, B = R + 1 = 10 (read by the engine), W = 10.
        Assert.True(v.CanApply, v.Status);
        Assert.Equal(29, v.Trace.Result!.Value.Number);
        var dice = s.Game.State.DiceHistory.Count;
        Assert.True(c.ApplyFormula(null, PhaseFixture.BlueOne, v.Trace, false));
        Assert.Equal(29, Ledger.Get(s.Game.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
        Assert.Equal(dice + 1, s.Game.State.DiceHistory.Count);   // the dice line comes with the Apply
    }

    [Fact]
    public void HotSeat_input_auto_value_over_hidden_count_roll_is_hidden_after_calculate()
    {
        var s = Open(_tmp, _fixtures, _sessions, SpyRoll);
        s.SetViewSides(Blue);
        var m = HotSeatDialog(s, "f-spy-roll");
        Assert.Contains("B", m.Hidden);
        m.Calculate(s.Game!.State);
        var v = m.Evaluate(s.Game.State);
        Assert.Equal(FormulaMasking.HiddenText, v.Boxes.Single(b => b.Variable == "B").Text);
        Assert.Equal(FormulaMasking.HiddenText, v.Boxes.Single(b => b.Variable == "R").Text);
    }

    /// <summary>F3: a precondition over Red's Private plans, Q asked; and a table choice filtered by the plans.</summary>
    private static FormulaDef QueuedPre => new()
    {
        Id = "f-q-pre", Name = "Queued gate", Text = "W + Q", Precondition = "A > 5", RefusalMessage = "Red has no plans to raid",
        Bindings = new() { W, DepotBinding("A", "plans"), new VariableBinding { Variable = "Q", Kind = BindingKind.Input } },
        Outputs = new() { ToWater },
    };

    private static FormulaDef QueuedChoice => new()
    {
        Id = "f-q-choice", Name = "Queued choice", Text = "W + lookup(TestTerrainCost, K)",
        Bindings = new()
        {
            W,
            new VariableBinding { Variable = "K", Kind = BindingKind.Input, ChoicesTable = "t-cost", ChoicesFilter = "holder(RedDepot, Plans) < 5" },
        },
        Outputs = new() { ToWater },
    };

    /// <summary>A hot-seat Blue session whose Red plans are <paramref name="plans"/>, with one queued run of <paramref name="formulaId"/> for Blue 1.</summary>
    private (ModuleSession S, PhaseController C, PendingRun Run) Queued(string formulaId, double plans)
    {
        var s = Open(_tmp, _fixtures, _sessions, QueuedPre, QueuedChoice);
        if (plans != Secret) s.Game!.Execute(new LedgerDelta(Depot, "plans", Secret, plans, "manual", null, null, null, null));
        var run = new PendingRun("q1", formulaId, formulaId, "start", new List<PendingUnit> { new(PhaseFixture.BlueOne, "Blue 1", null, false) }, true);
        s.Game!.Execute(new QueueRuns(new[] { run }));
        s.SetViewSides(Blue);
        return (s, new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s)), run);
    }

    private static string Missing(ModuleSession s, PendingRun run) =>
        string.Join(";", MissingInputs.For(s, run).Select(m => $"{m.Variable}:{string.Join(",", m.Choices ?? Array.Empty<string>())}"));

    [Fact]
    public void Missing_inputs_hidden_precondition_lists_the_same_and_fails_generically()
    {
        var (pass, pc, prun) = Queued("f-q-pre", Secret);
        var (refuse, rc, rrun) = Queued("f-q-pre", 0);
        Assert.Equal(Missing(pass, prun), Missing(refuse, rrun));
        Assert.Equal("Q:", Missing(pass, prun));
        var answer = new Dictionary<string, string> { ["Q"] = "1" };
        var (applied, failed) = MissingInputs.ApplyAll(pass, pc, prun, answer);
        Assert.Equal(1, applied);
        Assert.Empty(failed);
        (applied, failed) = MissingInputs.ApplyAll(refuse, rc, rrun, answer);
        Assert.Equal(0, applied);
        Assert.Equal(new[] { "Blue 1: " + FormulaMasking.ErrorHiddenText }, failed);
        Assert.DoesNotContain("Red has no plans", string.Join("|", failed));
    }

    [Fact]
    public void Missing_inputs_choices_filter_over_hidden_value_judged_on_chooser_view()
    {
        var results = new List<string>();
        foreach (var plans in new[] { Secret, 0 })
        {
            var (s, c, run) = Queued("f-q-choice", plans);
            var listed = Missing(s, run);
            var (applied, failed) = MissingInputs.ApplyAll(s, c, run, new Dictionary<string, string> { ["K"] = "Clear" });
            results.Add($"{listed}|{applied}|{string.Join(",", failed)}");
        }
        Assert.Equal(results[0], results[1]);
        Assert.EndsWith("|1|", results[0]);
    }
}
