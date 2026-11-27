using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Core.Tests;

/// <summary>
/// B7 #47: a Reveal output shows one property value in the log, exactly or as a random window that holds it, and writes nothing. The value
/// is read at the run (after the run's earlier writes), never shown before Apply, and the window's die is never logged. Invented Riverland
/// data: the Northern League (<c>north</c>) and the Southern Compact (<c>south</c>).
/// </summary>
public class RevealTests
{
    private static readonly IReadOnlySet<string> North = new HashSet<string> { "north" };
    private static readonly IReadOnlySet<string> South = new HashSet<string> { "south" };

    /// <summary>Strength (Private, labelled 1 Weak, 2 Shaken, 3 Steady), Supply (Private, on holders), Morale (Public, global).</summary>
    private static RulesPack Pack(params FormulaOutput[] outputs) => new()
    {
        Format = 4,
        Sides = new()
        {
            new SideDef { Id = "north", Name = "Northern League", UnitFilter = "startsWith(name(), \"North\")" },
            new SideDef { Id = "south", Name = "Southern Compact", UnitFilter = "startsWith(name(), \"South\")" },
        },
        Properties = new()
        {
            new PropertyDef { Id = "str", Name = "Strength", Scope = PropertyScope.Pieces, Default = 2, Visibility = Visibility.Private, Labels = "t-steady" },
            new PropertyDef { Id = "supply", Name = "Supply", Scope = PropertyScope.Holders, Visibility = Visibility.Private },
            new PropertyDef { Id = "morale", Name = "Morale", Scope = PropertyScope.Holders, Default = 2, Visibility = Visibility.Public },
        },
        Holders = new() { new HolderDef { Id = "southdepot", Name = "SouthDepot", SideId = "south" } },
        Tables = new()
        {
            new TableDef
            {
                Id = "t-steady", Name = "Steadiness", Kind = TableKind.OneD, RowKeys = new() { "1", "2", "3" }, ColumnKeys = new() { "Label" },
                Cells = new() { new() { "Weak" }, new() { "Shaken" }, new() { "Steady" } },
            },
        },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "f", Name = "Scout the far bank", Text = "1",
                Bindings = outputs.Any(o => o.Target == OutputTarget.Piece)
                    ? new() { new VariableBinding { Variable = "T", Kind = BindingKind.Piece, Property = "str" } }
                    : new(),
                Outputs = outputs.ToList(),
            },
        },
    };

    private static FormulaOutput Reveal(OutputTarget target = OutputTarget.Unit, string property = "str", string? margin = null, string? holder = null) =>
        new()
        {
            Action = OutputAction.Reveal, Target = target, Property = property, Margin = margin, HolderId = holder,
            PieceVariable = target == OutputTarget.Piece ? "T" : null,
        };

    private static GameState State(double strength = 3)
    {
        var s = new GameState { GameId = "g" };
        EffectFx.Add(s, "n1", "North Scout", str: strength);
        EffectFx.Add(s, "s1", "South Raft", x: 160, str: 1);
        s.Ledgers[LedgerKey.Holder("southdepot")] = new() { ["supply"] = 7 };
        return s;
    }

    private static FormulaRunner Runner(RulesPack pack, int die = 1, List<(int N, int Sides)>? calls = null) =>
        new(pack, EffectFx.Model, (n, sides) => { calls?.Add((n, sides)); return Enumerable.Repeat(die, n).ToList(); });

    private static FormulaTrace Run(FormulaRunner runner, GameState s, string? piece = "n1", IReadOnlyDictionary<string, string>? typed = null,
        IReadOnlySet<string>? pickSides = null) =>
        runner.Run(s, new RunRequest("f", piece, typed ?? new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<int>>(), PickSides: pickSides));

    private static List<string> RevealLines(IReadOnlyList<GameCommand> cmds) =>
        cmds.OfType<AddLogLine>().Where(l => l.Kind == LogKind.System).Select(l => l.Text).ToList();

    [Fact]
    public void Reveal_exact_logs_value_with_label()
    {
        var pack = Pack(Reveal());
        var runner = Runner(pack);
        var t = Run(runner, State());
        Assert.Null(t.Error);
        var r = Assert.Single(t.Reveals);
        Assert.Equal(new RevealPreview("North Scout", "str", 3, 0), r);
        var cmds = runner.Commands(t, null, sideId: "north");
        var line = Assert.Single(cmds.OfType<AddLogLine>(), l => l.Kind == LogKind.System);
        Assert.Equal("North Scout: Strength 3 (Steady)", line.Text);
        Assert.Null(line.PieceId);
        Assert.Null(line.SideId);
        Assert.Empty(cmds.OfType<LedgerDelta>());   // a reveal writes nothing
    }

    [Fact]
    public void Reveal_window_contains_value()
    {
        var pack = Pack(Reveal(margin: "2"));
        var calls = new List<(int, int)>();
        var runner = Runner(pack, die: 2, calls);
        var t = Run(runner, State(5));
        Assert.Equal(2, Assert.Single(t.Reveals).Margin);
        Assert.Empty(calls);   // the window is drawn when the commands are built, never at the run
        Assert.Equal(new[] { "North Scout: Strength between 4 and 6" }, RevealLines(runner.Commands(t, null)));
        Assert.Equal((1, 3), Assert.Single(calls));   // one die of margin + 1 faces
    }

    [Fact]
    public void Reveal_window_shifted_at_zero()
    {
        var pack = Pack(Reveal(margin: "2"));
        var runner = Runner(pack, die: 3);
        var t = Run(runner, State(0));
        Assert.Equal(new[] { "North Scout: Strength between 0 and 2" }, RevealLines(runner.Commands(t, null)));
        // a window that already starts at 0 or above is not moved
        var low = Run(Runner(pack, die: 1), State(0));
        Assert.Equal(new[] { "North Scout: Strength between 0 and 2" }, RevealLines(Runner(pack, die: 1).Commands(low, null)));
        Assert.Equal(new[] { "North Scout: Strength between 1.5 and 3.5" }, RevealLines(Runner(pack, die: 2).Commands(Run(Runner(pack), State(2.5)), null)));
    }

    [Fact]
    public void Reveal_reads_after_earlier_write()
    {
        var write = new FormulaOutput { Target = OutputTarget.Unit, Property = "str", Op = OutputOp.Add, Expr = "1" };
        var pack = Pack(Reveal(), write, Reveal());
        var runner = Runner(pack);
        var t = Run(runner, State(2));
        Assert.Null(t.Error);
        Assert.Equal(new double?[] { 2, 3 }, t.Reveals.Select(r => r.Value).ToArray());
        var cmds = runner.Commands(t, null);
        Assert.Equal(new[] { "North Scout: Strength 2 (Shaken)", "North Scout: Strength 3 (Steady)" }, RevealLines(cmds));
        // after the write: the ledger line comes first, the reveal lines after it
        Assert.True(cmds.ToList().FindIndex(c => c is LedgerDelta) < cmds.ToList().FindIndex(c => c is AddLogLine { Kind: LogKind.System }));
    }

    [Fact]
    public void Reveal_piece_target_uses_shown_name()
    {
        var s = new GameState { GameId = "g" };
        EffectFx.Add(s, "s1", "South Raft");
        EffectFx.Add(s, "n2", "North Secret Battery", x: 160, str: 3, traits: new[] { (EffectFx.Mask, "red;") });
        var pack = Pack(Reveal(OutputTarget.Piece));
        var view = PlayerView.For(s, pack, EffectFx.Model, South);
        var label = Assert.Single(PieceChoices.For(view, pack, EffectFx.Model, pack.Formulas[0].Bindings[0], South), c => c.PieceId == "n2").Label;
        Assert.DoesNotContain("Secret", label);
        var runner = Runner(pack);
        // the host runs the Southern player's pick on the full state: the subject is the piece as they see it
        var t = Run(runner, s, "s1", new Dictionary<string, string> { ["T"] = label }, South);
        Assert.Null(t.Error);
        Assert.Equal("Unknown piece", Assert.Single(t.Reveals).Subject);
        Assert.Equal(new[] { "Unknown piece: Strength 3 (Steady)" }, RevealLines(runner.Commands(t, null, sideId: "south")));
        // the Northern player's own pick names it
        var own = Run(runner, s, "s1", new Dictionary<string, string> { ["T"] = EffectFx.Label(s, "n2") });
        Assert.Equal(new[] { "North Secret Battery: Strength 3 (Steady)" }, RevealLines(runner.Commands(own, null)));
    }

    [Fact]
    public void Reveal_holder_and_global_subjects()
    {
        var pack = Pack(Reveal(OutputTarget.Holder, "supply", holder: "southdepot"), Reveal(OutputTarget.Global, "morale"));
        var runner = Runner(pack);
        var t = Run(runner, State(), piece: null);
        Assert.Null(t.Error);
        Assert.Equal(new[] { "SouthDepot", "Global" }, t.Reveals.Select(r => r.Subject).ToArray());
        Assert.Equal(new[] { "SouthDepot: Supply 7", "Global: Morale 2" }, RevealLines(runner.Commands(t, null)));
    }

    [Theory]
    [InlineData("1001")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"wide\"")]
    public void Reveal_margin_error_fails_run(string margin)
    {
        var pack = Pack(Reveal(margin: margin));
        var runner = Runner(pack);
        var t = Run(runner, State());
        Assert.Equal("The margin for Strength must be a whole number from 0 to 1000", t.Error);
        Assert.Empty(runner.Commands(t, null));
        // the ends of the range are accepted
        Assert.Null(Run(Runner(Pack(Reveal(margin: "1000"))), State()).Error);
        Assert.Null(Run(Runner(Pack(Reveal(margin: "0"))), State()).Error);
    }

    [Fact]
    public void Reveal_logs_no_dice_line()
    {
        var pack = Pack(Reveal(margin: "4"));
        var runner = Runner(pack, die: 3);
        var t = Run(runner, State());
        Assert.Empty(t.Rolls);
        Assert.Empty(t.RollsByKey);
        var s = State();
        var cmds = runner.Commands(t, null);
        Assert.Empty(cmds.OfType<RollDice>());
        foreach (var c in cmds) s.Apply(c);
        Assert.Empty(s.DiceHistory);
        Assert.DoesNotContain(s.Log, l => l.Kind == LogKind.Dice);
    }

    [Fact]
    public void Reveal_working_shows_no_value()
    {
        var pack = Pack(Reveal());
        var t = Run(Runner(pack), State(61));
        Assert.Equal("1 = 1 → reveal Strength", t.Working);
        Assert.Empty(t.Outputs);   // no output preview: no box or verdict carries the value
        Assert.Empty(t.Variables);
        Assert.DoesNotContain("61", t.Working);
    }

    [Fact]
    public void Reveal_on_view_unknown_no_error()
    {
        var pack = Pack(Reveal(OutputTarget.Holder, "supply", holder: "southdepot"), Reveal());
        var view = PlayerView.For(State(), pack, EffectFx.Model, North);
        var runner = Runner(pack);
        var t = Run(runner, view);
        Assert.Null(t.Error);
        Assert.Equal(1, t.Result!.Value.Number);
        // another side's Private holder value: unknown in the view (the host reads the full state); the own unit's value is known
        Assert.Equal(new double?[] { null, 3 }, t.Reveals.Select(r => r.Value).ToArray());
        Assert.Equal(new[] { "North Scout: Strength 3 (Steady)" }, RevealLines(runner.Commands(t, null)));
        // another side's piece: unknown, no error
        var south = PlayerView.For(State(), pack, EffectFx.Model, South);
        var u = Run(Runner(Pack(Reveal())), south);
        Assert.Null(u.Error);
        Assert.Null(Assert.Single(u.Reveals).Value);
    }

    [Fact]
    public void Reveal_line_kept_for_other_side_view()
    {
        var pack = Pack(Reveal(OutputTarget.Holder, "supply", holder: "southdepot"));
        var s = State();
        var runner = Runner(pack);
        foreach (var c in runner.Commands(Run(runner, s), null, sideId: "north")) s.Apply(c);
        Assert.Contains(s.Log, l => l.Text == "SouthDepot: Supply 7");
        Assert.Contains(PlayerView.For(s, pack, EffectFx.Model, North).Log, l => l.Text == "SouthDepot: Supply 7");
        Assert.Contains(PlayerView.For(s, pack, EffectFx.Model, South).Log, l => l.Text == "SouthDepot: Supply 7");
        // the revealed value itself stays hidden in the Northern view's ledgers
        Assert.False(PlayerView.For(s, pack, EffectFx.Model, North).Ledgers.TryGetValue(LedgerKey.Holder("southdepot"), out var props)
            && props.ContainsKey("supply"));
    }

    [Fact]
    public void Reveal_line_dropped_when_subject_hidden_from_viewer()
    {
        var s = new GameState { GameId = "g" };
        EffectFx.Add(s, "n2", "North Secret Battery", str: 3, traits: new[] { (EffectFx.Mask, "red;") });
        var pack = Pack(Reveal());
        var runner = Runner(pack);
        foreach (var c in runner.Commands(Run(runner, s, "n2"), null, sideId: "north")) s.Apply(c);
        const string line = "North Secret Battery: Strength 3 (Steady)";
        Assert.Contains(s.Log, l => l.Text == line);
        Assert.Contains(PlayerView.For(s, pack, EffectFx.Model, North).Log, l => l.Text == line);
        Assert.DoesNotContain(PlayerView.For(s, pack, EffectFx.Model, South).Log, l => l.Text.Contains("Secret"));
    }

    [Fact]
    public void Undo_removes_reveal_line()
    {
        var pack = Pack(new FormulaOutput { Target = OutputTarget.Unit, Property = "str", Op = OutputOp.Add, Expr = "1" }, Reveal(margin: "2"));
        var g = new GameSession(State(), "sha");
        var runner = Runner(pack);
        var t = Run(runner, g.State);
        using (g.BeginAction("Scout the far bank on North Scout"))
            foreach (var c in runner.Commands(t, null)) g.Execute(c);
        Assert.Single(g.Actions);   // the reveal line is part of its run's action
        Assert.Contains(g.State.Log, l => l.Text == "North Scout: Strength between 4 and 6");
        g.Undo();
        Assert.DoesNotContain(g.State.Log, l => l.Text.Contains("Strength between"));
        Assert.Equal(3, Ledger.Get(g.State, LedgerKey.Piece("n1"), "str"));
    }

    [Fact]
    public void Reveal_window_of_negative_value_is_not_shifted()
    {
        // Final review F6(d): only a value of 0 or more moves its window up to 0; a negative value keeps lo = v - k.
        var pack = Pack(Reveal(margin: "2"));
        Assert.Equal(new[] { "North Scout: Strength between -4 and -2" }, RevealLines(Runner(pack, die: 2).Commands(Run(Runner(pack), State(-3)), null)));
        Assert.Equal(new[] { "North Scout: Strength between -5 and -3" }, RevealLines(Runner(pack, die: 3).Commands(Run(Runner(pack), State(-3)), null)));
    }

    [Fact]
    public void Reveal_window_well_above_margin_is_not_moved()
    {
        // v = 10, margin 2: the die picks where the value sits in the window (k = die - 1), and no window is moved.
        var pack = Pack(Reveal(margin: "2"));
        Assert.Equal(new[] { "North Scout: Strength between 10 and 12" }, RevealLines(Runner(pack, die: 1).Commands(Run(Runner(pack), State(10)), null)));
        Assert.Equal(new[] { "North Scout: Strength between 8 and 10" }, RevealLines(Runner(pack, die: 3).Commands(Run(Runner(pack), State(10)), null)));
    }
}
