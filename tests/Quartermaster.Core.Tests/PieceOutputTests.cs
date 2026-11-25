using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;
using static Quartermaster.Core.Tests.RunnerFx;

namespace Quartermaster.Core.Tests;

/// <summary>Shared invented Riverland fixture for B3 Piece outputs and same-hex candidates: side "red" = Northern League (names start
/// "North"), side "blue" = Southern Compact (names start "South").</summary>
internal static class PieceOutFx
{
    public const string HideTrait = "hide;I;Hide it;;;0.3;;false";
    public const string ObsTrait = "obs;M;m.png;Mask it;I;?";
    public static readonly IReadOnlySet<string> North = new HashSet<string> { "red" };
    public static readonly IReadOnlySet<string> South = new HashSet<string> { "blue" };

    public static VariableBinding Pick(string variable = "T", string property = "str", bool sameHex = false, string? filter = null) =>
        new() { Variable = variable, Kind = BindingKind.Piece, Property = property, SameHex = sameHex, PieceFilter = filter };

    public static FormulaOutput ToPiece(string property = "str", OutputOp op = OutputOp.Subtract, string variable = "T", string? expr = null) =>
        new() { Target = OutputTarget.Piece, PieceVariable = variable, Property = property, Op = op, Expr = expr };

    public static RulesPack Pack(FormulaDef f, bool sides = true) => new()
    {
        Format = 4,
        Sides = sides
            ? new()
            {
                new SideDef { Id = "red", Name = "Northern League", UnitFilter = "startsWith(name(), \"North\")" },
                new SideDef { Id = "blue", Name = "Southern Compact", UnitFilter = "startsWith(name(), \"South\")" },
            }
            : new(),
        Properties = new()
        {
            new PropertyDef { Id = "str", Name = "Strength", Scope = PropertyScope.Pieces, Default = 2, Visibility = Visibility.Public },
            new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, Default = 0, Visibility = Visibility.Public },
            new PropertyDef { Id = "secret", Name = "Secret", Scope = PropertyScope.Pieces, Default = 0, Visibility = Visibility.Private },
            new PropertyDef { Id = "tally", Name = "Tally", Scope = PropertyScope.Holders, Default = 0 },
        },
        Formulas = new() { f },
    };

    public static FormulaDef Shell(params FormulaOutput[] outputs) => new()
    {
        Id = "f", Name = "Shelling", Text = "2",
        Bindings = new() { Pick() },
        Outputs = outputs.Length == 0 ? new() { ToPiece() } : outputs.ToList(),
    };

    public static void Add(GameState s, string id, string name, double? str = null, double x = 60, double y = 40, string map = "Main Map",
        string? trait = null, string traitState = "red", double? fuel = null, double? secret = null)
    {
        var layers = new List<string>();
        var states = new List<string>();
        if (trait != null) { layers.Add(trait); states.Add(traitState); }
        layers.Add("piece;;;unit.svg;" + name);
        states.Add("");
        s.Pieces[id] = new PieceInstance { Id = id, MapId = map, Pos = new PointD(x, y), TypeString = PieceTypeParser.EncodeState(layers), TraitStates = states };
        var ledger = new OrderedDictionary<string, double>();
        if (str != null) ledger["str"] = str.Value;
        if (fuel != null) ledger["fuel"] = fuel.Value;
        if (secret != null) ledger["secret"] = secret.Value;
        if (ledger.Count > 0) s.Ledgers[LedgerKey.Piece(id)] = ledger;
    }

    public static string Loc(GameState s, string id) => PieceInfo.Location(s.Pieces[id], Model) ?? throw new InvalidOperationException("off grid");

    public static FormulaRunner Runner(RulesPack pack) => new(pack, Model, (n, _) => Enumerable.Repeat(1, n).ToList());

    public static FormulaTrace Run(GameState s, RulesPack pack, string? piece, IReadOnlyDictionary<string, string>? typed = null,
        IReadOnlySet<string>? pickSides = null) =>
        Runner(pack).Run(s, new RunRequest("f", piece, typed ?? new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<int>>(), PickSides: pickSides));

    public static Dictionary<string, string> Typed(string label, string variable = "T") => new() { [variable] = label };
}

/// <summary>B3 #3/#43: an output writing the piece a Piece binding picked, and its log lines.</summary>
public class PieceOutputTests
{
    private static GameState Field()
    {
        var s = new GameState { GameId = "g" };
        PieceOutFx.Add(s, "b1", "South Guns", 5, x: 160);
        PieceOutFx.Add(s, "r1", "North Rifles", 7);
        return s;
    }

    private static string RiflesLabel(GameState s) => $"North Rifles ({PieceOutFx.Loc(s, "r1")})";

    [Fact]
    public void Piece_output_adds_to_picked_piece()
    {
        var s = Field();
        var pack = PieceOutFx.Pack(PieceOutFx.Shell(PieceOutFx.ToPiece(op: OutputOp.Add)));
        var t = PieceOutFx.Run(s, pack, "b1", PieceOutFx.Typed(RiflesLabel(s)));
        Assert.Null(t.Error);
        var o = Assert.Single(t.Outputs);
        Assert.Equal(LedgerKey.Piece("r1"), o.Key);
        Assert.Equal((7.0, 9.0), (o.Before, o.After));
        Assert.Equal(OutputTarget.Piece, o.Target);
        Assert.Equal("red", o.SideId);
        Assert.Equal("Strength 7 + 2 = 9", o.Part);
        Assert.Equal("North Rifles", o.Subject);
        Assert.EndsWith(" → Strength of North Rifles 7 + 2 = 9", t.Working);

        foreach (var c in PieceOutFx.Runner(pack).Commands(t, null, sideId: "blue")) s.Apply(c);
        Assert.Equal(9, Ledger.Get(s, LedgerKey.Piece("r1"), "str"));
        Assert.Equal(5, Ledger.Get(s, LedgerKey.Piece("b1"), "str"));
    }

    [Fact]
    public void Skipped_target_binding_skips_its_outputs()
    {
        // "Only when" false: the target is not asked and the outputs naming it do nothing, typed label or not.
        var s = Field();
        var f = PieceOutFx.Shell(new FormulaOutput { Target = OutputTarget.Unit, Property = "str", Op = OutputOp.Subtract }, PieceOutFx.ToPiece(op: OutputOp.Add),
            new FormulaOutput { Target = OutputTarget.Piece, PieceVariable = "T", Action = OutputAction.Remove });
        f = f with { Bindings = new() { PieceOutFx.Pick() with { When = "1 > 2" } } };
        var pack = PieceOutFx.Pack(f);
        foreach (var typed in new[] { null, PieceOutFx.Typed(RiflesLabel(s)) })
        {
            var t = PieceOutFx.Run(s, pack, "b1", typed);
            Assert.Null(t.Error);
            Assert.NotNull(t.Result);
            Assert.True(Assert.Single(t.Variables, v => v.Variable == "T").Skipped);
            var o = Assert.Single(t.Outputs);
            Assert.Equal(LedgerKey.Piece("b1"), o.Key);
            Assert.Empty(t.Effects);
        }
    }

    [Fact]
    public void Transfer_subtracts_and_adds_in_one_trace()
    {
        var s = new GameState();
        PieceOutFx.Add(s, "b1", "South Barge", fuel: 5);
        PieceOutFx.Add(s, "b2", "South Tug", fuel: 4, x: 62, y: 42);
        var f = new FormulaDef
        {
            Id = "f", Name = "Barge transfer", Text = "Amount",
            Bindings = new()
            {
                new VariableBinding { Variable = "Amount", Kind = BindingKind.Input, Min = 0, Max = 10 },
                PieceOutFx.Pick("Receiver", "fuel", sameHex: true),
            },
            Outputs = new()
            {
                new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Op = OutputOp.Subtract },
                PieceOutFx.ToPiece("fuel", OutputOp.Add, "Receiver"),
            },
        };
        var pack = PieceOutFx.Pack(f);
        var typed = new Dictionary<string, string> { ["Amount"] = "2", ["Receiver"] = $"South Tug ({PieceOutFx.Loc(s, "b2")})" };
        var t = PieceOutFx.Run(s, pack, "b1", typed);
        Assert.Null(t.Error);
        Assert.Equal(new[] { (LedgerKey.Piece("b1"), 5.0, 3.0), (LedgerKey.Piece("b2"), 4.0, 6.0) },
            t.Outputs.Select(o => (o.Key, o.Before, o.After)).ToArray());
        Assert.Equal("2 = 2 → Fuel 5 − 2 = 3; Fuel of South Tug 4 + 2 = 6", t.Working);
        foreach (var c in PieceOutFx.Runner(pack).Commands(t, null)) s.Apply(c);
        Assert.Equal((3.0, 6.0), (Ledger.Get(s, LedgerKey.Piece("b1"), "fuel")!.Value, Ledger.Get(s, LedgerKey.Piece("b2"), "fuel")!.Value));
    }

    [Fact]
    public void Piece_output_waits_for_pick()
    {
        var s = Field();
        // The binding is used only as the output's target (the text does not read it): the run still waits for it.
        var pack = PieceOutFx.Pack(PieceOutFx.Shell());
        var t = PieceOutFx.Run(s, pack, "b1");
        Assert.Null(t.Error);
        Assert.Null(t.Result);
        Assert.Empty(t.Outputs);
        Assert.Equal("Waiting for input", Assert.Single(t.Variables, v => v.Variable == "T").MissingReason);
        Assert.Empty(PieceOutFx.Runner(pack).Commands(t, null));
    }

    [Fact]
    public void Piece_output_bad_variable_fails()
    {
        var s = Field();
        foreach (var f in new[]
        {
            PieceOutFx.Shell(PieceOutFx.ToPiece(variable: "Nobody")),
            PieceOutFx.Shell(PieceOutFx.ToPiece(variable: "X")) with
            {
                Bindings = new() { PieceOutFx.Pick(), new VariableBinding { Variable = "X", Kind = BindingKind.Input } },
            },
            PieceOutFx.Shell(PieceOutFx.ToPiece() with { PieceVariable = null }),
        })
        {
            var t = PieceOutFx.Run(s, PieceOutFx.Pack(f), "b1", new Dictionary<string, string> { ["T"] = RiflesLabel(s), ["X"] = "1" });
            Assert.Equal("An output refers to a deleted Piece variable", t.Error);
            Assert.Empty(t.Outputs);
        }
    }

    [Fact]
    public void Piece_output_placeholder_hidden_in_view()
    {
        var s = new GameState();
        PieceOutFx.Add(s, "b1", "South Guns", 5, x: 160);
        PieceOutFx.Add(s, "r1", "North Secret Battery", 7, trait: PieceOutFx.ObsTrait);
        var pack = PieceOutFx.Pack(PieceOutFx.Shell() with { Text = "1" });
        var view = PlayerView.For(s, pack, Model, PieceOutFx.South);
        var label = Assert.Single(PieceChoices.For(view, pack, Model, PieceOutFx.Pick(), PieceOutFx.South), c => c.PieceId == "r1").Label;
        Assert.StartsWith("Unknown piece", label);
        var t = PieceOutFx.Run(view, pack, "b1", PieceOutFx.Typed(label), PieceOutFx.South);
        Assert.Null(t.Error);
        Assert.Null(t.Result);
        Assert.Empty(t.Outputs);
        Assert.Equal("Hidden from this player", Assert.Single(t.Variables, v => v.Variable == "T").MissingReason);

        // A non-Public property of a visible enemy piece is not written from a view either.
        var s2 = Field();
        var priv = PieceOutFx.Pack(PieceOutFx.Shell(PieceOutFx.ToPiece("secret")) with { Text = "1" });
        var v2 = PlayerView.For(s2, priv, Model, PieceOutFx.South);
        var t2 = PieceOutFx.Run(v2, priv, "b1", PieceOutFx.Typed(RiflesLabel(s2)), PieceOutFx.South);
        Assert.Null(t2.Result);
        Assert.Empty(t2.Outputs);
        Assert.Equal("Hidden from this player", Assert.Single(t2.Variables, v => v.Variable == "T").MissingReason);

        // On the host (full state) the guest's masked pick resolves and is written; the dialog names it as the guest sees it.
        var host = PieceOutFx.Run(s, pack, "b1", PieceOutFx.Typed(label), PieceOutFx.South);
        Assert.Null(host.Error);
        var o = Assert.Single(host.Outputs);
        Assert.Equal(LedgerKey.Piece("r1"), o.Key);
        Assert.Equal("Unknown piece", o.Subject);
        Assert.DoesNotContain("Secret", host.Working);
        // its line for the target side carries the real name
        var line = Assert.Single(PieceOutFx.Runner(pack).Commands(host, null, sideId: "blue").OfType<LedgerDelta>());
        Assert.Equal("North Secret Battery", line.Subject);
        Assert.Equal("red", line.SideId);
    }

    [Fact]
    public void Piece_output_holders_scope_refused()
    {
        var s = Field();
        var f = PieceOutFx.Shell(PieceOutFx.ToPiece("tally"));
        var pack = PieceOutFx.Pack(f);
        Assert.Contains(RulesValidator.Validate(pack),
            e => e.Severity == RulesIssueSeverity.Error && e.Message.EndsWith("output property \"Tally\" cannot be written to a piece (its scope is Holders)"));
        var t = PieceOutFx.Run(s, pack, "b1", PieceOutFx.Typed(RiflesLabel(s)));
        Assert.Equal("Formula \"Shelling\": output property \"Tally\" cannot be written to a piece (its scope is Holders)", t.Error);
        Assert.Empty(t.Outputs);
    }

    [Fact]
    public void Piece_line_stamped_with_target_side()
    {
        var s = Field();
        var pack = PieceOutFx.Pack(PieceOutFx.Shell());
        var t = PieceOutFx.Run(s, pack, "b1", PieceOutFx.Typed(RiflesLabel(s)));
        var cmds = PieceOutFx.Runner(pack).Commands(t, "note", "auto", "South Guns", "blue");
        var d = Assert.Single(cmds.OfType<LedgerDelta>());
        Assert.Equal(LedgerKey.Piece("r1"), d.Key);
        Assert.Equal("r1", d.PieceId);
        Assert.Equal("red", d.SideId);
        Assert.Equal("North Rifles", d.Subject);
        Assert.Equal("Shelling: Strength 7 − 2 = 5", d.Working);
        Assert.Equal("auto", d.Tag);
        Assert.Null(d.AppliedRun);

        // a target of no side is stamped null (public)
        var s2 = Field();
        PieceOutFx.Add(s2, "w1", "Wreck", 3, x: 160, y: 80);
        var t2 = PieceOutFx.Run(s2, pack, "b1", PieceOutFx.Typed($"Wreck ({PieceOutFx.Loc(s2, "w1")})"));
        Assert.Null(Assert.Single(PieceOutFx.Runner(pack).Commands(t2, null, sideId: "blue").OfType<LedgerDelta>()).SideId);
    }

    [Fact]
    public void Own_lines_working_omits_piece_parts()
    {
        var s = Field();
        var pack = PieceOutFx.Pack(PieceOutFx.Shell(
            new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Op = OutputOp.Subtract },
            PieceOutFx.ToPiece(),
            new FormulaOutput { Target = OutputTarget.Global, Property = "tally", Op = OutputOp.Add }));
        var t = PieceOutFx.Run(s, pack, "b1", PieceOutFx.Typed(RiflesLabel(s)));
        Assert.Null(t.Error);
        Assert.Equal("2 = 2 → Fuel 0 − 2 = -2; Strength of North Rifles 7 − 2 = 5; Tally 0 + 2 = 2", t.Working);
        var deltas = PieceOutFx.Runner(pack).Commands(t, null, sideId: "blue").OfType<LedgerDelta>().ToList();
        Assert.Equal(3, deltas.Count);
        var own = deltas.Where(d => d.Key != LedgerKey.Piece("r1")).ToList();
        Assert.Equal(2, own.Count);
        Assert.All(own, d => Assert.Equal("2 = 2 → Fuel 0 − 2 = -2; Tally 0 + 2 = 2", d.Working));
        Assert.All(own, d => Assert.DoesNotContain("North Rifles", d.Working));
        Assert.Empty(PieceOutFx.Runner(pack).Commands(t, null).OfType<AddLogLine>());   // an own line carries the working: no working line
    }

    [Fact]
    public void Applied_run_on_first_own_line()
    {
        var s = Field();
        // The Piece output is listed first; the own (Unit) write still comes first and carries the run stamp.
        var pack = PieceOutFx.Pack(PieceOutFx.Shell(
            PieceOutFx.ToPiece(),
            new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Op = OutputOp.Add }));
        var t = PieceOutFx.Run(s, pack, "b1", PieceOutFx.Typed(RiflesLabel(s)));
        var deltas = PieceOutFx.Runner(pack).Commands(t, null, sideId: "blue").OfType<LedgerDelta>().ToList();
        Assert.Equal(new[] { LedgerKey.Piece("b1"), LedgerKey.Piece("r1") }, deltas.Select(d => d.Key).ToArray());
        Assert.Equal(new[] { "f", null }, deltas.Select(d => d.AppliedRun).ToArray());
    }

    [Fact]
    public void Piece_only_run_writes_working_line_with_applied_run()
    {
        var s = Field();
        var pack = PieceOutFx.Pack(PieceOutFx.Shell());
        var t = PieceOutFx.Run(s, pack, "b1", PieceOutFx.Typed(RiflesLabel(s)));
        var cmds = PieceOutFx.Runner(pack).Commands(t, null, "auto", null, "blue");
        Assert.IsType<LedgerDelta>(cmds[0]);
        var line = Assert.IsType<AddLogLine>(cmds[^1]);
        Assert.Equal(LogKind.Calc, line.Kind);
        Assert.Equal("Shelling: 2 = 2 (auto)", line.Text);
        Assert.Equal("b1", line.PieceId);
        Assert.Equal("blue", line.SideId);
        Assert.Equal("f", line.FormulaId);
        Assert.Equal("f", line.AppliedRun);
        Assert.Null(cmds.OfType<LedgerDelta>().Single().AppliedRun);
        // with a subject, untagged
        var named = Assert.Single(PieceOutFx.Runner(pack).Commands(t, null, null, "South Guns", "blue").OfType<AddLogLine>());
        Assert.Equal("South Guns: 2 = 2", named.Text);
    }

    private static (GameState State, RulesPack Pack) Shelled()
    {
        var s = Field();
        s.Apply(new SetPlayers(new[] { new PlayerDef("p1", "Ann", new[] { "red" }), new PlayerDef("p2", "Bob", new[] { "blue" }) }));
        var pack = PieceOutFx.Pack(PieceOutFx.Shell());
        var t = PieceOutFx.Run(s, pack, "b1", PieceOutFx.Typed(RiflesLabel(s)), PieceOutFx.South);
        foreach (var c in PieceOutFx.Runner(pack).Commands(t, null, sideId: "blue")) s.Apply(c);
        return (s, pack);
    }

    [Fact]
    public void PlayerView_actor_sees_ledger_changed_for_enemy_target()
    {
        var (s, pack) = Shelled();
        var view = PlayerView.For(s, pack, Model, PieceOutFx.South);
        Assert.DoesNotContain(view.Log, l => l.Text.Contains("Strength 7"));
        Assert.Contains(view.Log, l => l.Text == "Northern League ledger changed");
        Assert.Contains(view.Log, l => l.Text == "Shelling: 2 = 2" && l.AppliedRun == "f");   // its own working line
    }

    [Fact]
    public void PlayerView_target_side_sees_full_piece_line()
    {
        var (s, pack) = Shelled();
        var view = PlayerView.For(s, pack, Model, PieceOutFx.North);
        Assert.Contains(view.Log, l => l.Text == "North Rifles: Shelling: Strength 7 − 2 = 5" && l.PieceId == "r1");
        Assert.Contains(s.Log, l => l.Text == "North Rifles: Shelling: Strength 7 − 2 = 5");
    }

    [Fact]
    public void Runs_counts_piece_only_run_on_run_piece()
    {
        var (s, pack) = Shelled();
        Assert.Equal(1, RunIndex.Count(s, pack, "f", "b1", "game"));
        Assert.Equal(0, RunIndex.Count(s, pack, "f", "r1", "game"));
    }
}
