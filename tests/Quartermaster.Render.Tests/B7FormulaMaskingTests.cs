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
}
