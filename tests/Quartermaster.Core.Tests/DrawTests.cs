using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;
using static Quartermaster.Core.Tests.RunnerFx;

namespace Quartermaster.Core.Tests;

/// <summary>B9 #55: a formula without a Draw binding takes the old path (pinned before the draw code was added).</summary>
public class DrawOldPathTests
{
    [Fact]
    public void No_draw_binding_old_path()
    {
        var s = new GameState { GameId = "g" };
        PieceOutFx.Add(s, "b1", "South Guns", 5, x: 160);
        PieceOutFx.Add(s, "r1", "North Rifles", 7);
        var f = PieceOutFx.Shell(PieceOutFx.ToPiece(op: OutputOp.Add),
            new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Op = OutputOp.Add, Expr = "dice(1, 6)" },
            new FormulaOutput { Target = OutputTarget.Piece, PieceVariable = "T", Action = OutputAction.Remove });
        var pack = PieceOutFx.Pack(f);
        var t = PieceOutFx.Run(s, pack, "b1", PieceOutFx.Typed($"North Rifles ({PieceOutFx.Loc(s, "r1")})"));
        Assert.Null(t.Error);
        Assert.Equal("2 = 2 → Strength of North Rifles 7 + 2 = 9; Fuel 0 + 1 = 1; removed North Rifles", t.Working);
        Assert.Equal(new[] { "dice#0" }, t.RollsByKey.Keys);
        var cmds = PieceOutFx.Runner(pack).Commands(t, "n", sideId: "blue");
        var text = string.Join("\n", cmds.Select(c => c switch
        {
            RollDice d => $"roll {d.Purpose} {d.Count}d{d.Sides} {string.Join(",", d.Results)} {d.PieceId} {d.SideId} {d.FormulaId}",
            LedgerDelta l => $"delta {l.Key} {l.PropertyId} {l.Before}->{l.After} {l.Working} {l.Subject} {l.PieceId} {l.SideId} {l.AppliedRun}",
            AddLogLine a => $"log {a.Kind} {a.Text} {a.PieceId} {a.SideId} {a.FormulaId}",
            RemovePiece r => $"remove {r.PieceId} {r.LogText} {r.SideId}",
            _ => c.GetType().Name,
        }));
        Assert.Equal(string.Join("\n",
            "roll Shelling: dice#0 1d6 1 b1 blue f",
            $"delta {LedgerKey.Piece("b1")} fuel 0->1 2 = 2 → Fuel 0 + 1 = 1; removed North Rifles  b1 blue f",
            $"delta {LedgerKey.Piece("r1")} str 7->9 Shelling: Strength 7 + 2 = 9 North Rifles r1 red ",
            "remove r1 Shelling: removed North Rifles red"), text);
        Assert.Empty(t.Draws);
        Assert.Empty(t.DrawRolls);
        Assert.All(t.Outputs, o => Assert.Null(o.Draw));
    }
}

/// <summary>
/// Invented Riverland fixture for B9 #55 draws: barges SC 1 Barge..SC 4 Barge (Southern Compact, side "south") in one hex with Private
/// Boats 3, 1, 0, 2, and NL Battery (Northern League, side "north") elsewhere. Formula "Shore battery" (run on SC 1 Barge): Input Shots;
/// Draw Hits, weight Boats, same hex, filter side() == "Southern Compact", draw count Shots; output Piece Hits Subtract Boats by Hits.
/// </summary>
internal static class DrawFx
{
    public static readonly IReadOnlySet<string> North = new HashSet<string> { "north" };
    public static readonly IReadOnlySet<string> South = new HashSet<string> { "south" };

    public static VariableBinding Hits(string? weight = "p-boats", bool sameHex = true, string? filter = "side() == \"Southern Compact\"",
        string? count = "Shots") =>
        new() { Variable = "Hits", Kind = BindingKind.Draw, Property = weight, SameHex = sameHex, PieceFilter = filter, DrawCount = count };

    public static FormulaOutput Losses(string? expr = "Hits") =>
        new() { Target = OutputTarget.Piece, PieceVariable = "Hits", Property = "p-boats", Op = OutputOp.Subtract, Expr = expr };

    public static FormulaDef Battery(VariableBinding? draw = null, string text = "Hits", params FormulaOutput[] outputs) => new()
    {
        Id = "f", Name = "Shore battery", Text = text,
        Bindings = new() { new VariableBinding { Variable = "Shots", Kind = BindingKind.Input }, draw ?? Hits() },
        Outputs = outputs.Length == 0 ? new() { Losses() } : outputs.ToList(),
    };

    public static RulesPack Pack(FormulaDef? f = null) => new()
    {
        Format = 4,
        Sides = new()
        {
            new SideDef { Id = "north", Name = "Northern League", UnitFilter = "startsWith(name(), \"NL\")" },
            new SideDef { Id = "south", Name = "Southern Compact", UnitFilter = "startsWith(name(), \"SC\")" },
        },
        Properties = new()
        {
            new PropertyDef { Id = "p-boats", Name = "Boats", Scope = PropertyScope.Pieces, Default = 0, Visibility = Visibility.Private },
            new PropertyDef { Id = "p-salvos", Name = "Salvos", Scope = PropertyScope.Holders, Default = 0, Visibility = Visibility.Public },
        },
        Formulas = new() { f ?? Battery() },
    };

    public static void Add(GameState s, string id, string name, double? boats, double x = 60, double y = 40)
    {
        s.Pieces[id] = new PieceInstance
        {
            Id = id, MapId = "Main Map", Pos = new PointD(x, y),
            TypeString = PieceTypeParser.EncodeState(new[] { "piece;;;unit.svg;" + name }), TraitStates = new List<string> { "" },
        };
        if (boats != null) s.Ledgers[LedgerKey.Piece(id)] = new OrderedDictionary<string, double> { ["p-boats"] = boats.Value };
    }

    /// <summary>The barges added out of id order (b4, b2, b1, b3), so the pool's id order is tested; the battery elsewhere.</summary>
    public static GameState State(double b2Boats = 1)
    {
        var s = new GameState { GameId = "g" };
        Add(s, "b4", "SC 4 Barge", 2);
        Add(s, "b2", "SC 2 Barge", b2Boats);
        Add(s, "b1", "SC 1 Barge", 3);
        Add(s, "b3", "SC 3 Barge", 0);
        Add(s, "n1", "NL Battery", 5, x: 160);
        return s;
    }

    public static void Players(GameState s) =>
        s.Apply(new SetPlayers(new[] { new PlayerDef("p1", "Ann", new[] { "north" }), new PlayerDef("p2", "Bob", new[] { "south" }) }));

    /// <summary>A roller that always rolls the highest face, and the (count, sides) of each call.</summary>
    public static (FormulaRunner Runner, List<(int N, int Sides)> Calls) Runner(RulesPack pack)
    {
        var calls = new List<(int N, int Sides)>();
        return (new FormulaRunner(pack, Model, (n, sides) => { calls.Add((n, sides)); return Enumerable.Repeat(sides, n).ToList(); }), calls);
    }

    public static Dictionary<string, IReadOnlyList<int>> Pins(params int[] faces)
    {
        var d = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        for (var i = 0; i < faces.Length; i++) d[$"draw:Hits#{i + 1}"] = new[] { faces[i] };
        return d;
    }

    public static RunRequest Request(string shots = "3", string? piece = "b1", IReadOnlyDictionary<string, IReadOnlyList<int>>? pins = null,
        Func<string, int, int, IReadOnlyList<int>>? dice = null, IReadOnlyDictionary<string, string>? overrides = null) =>
        new("f", piece, new Dictionary<string, string> { ["Shots"] = shots }, overrides ?? new Dictionary<string, string>(),
            pins ?? new Dictionary<string, IReadOnlyList<int>>(), Dice: dice);

    public static FormulaTrace Run(GameState s, RulesPack pack, RunRequest r) => Runner(pack).Runner.Run(s, r);

    public static IEnumerable<(string, int)> Split(FormulaTrace t, string variable = "Hits") =>
        Assert.Single(t.Draws, d => d.Variable == variable).Drawn.Select(x => (x.PieceId, x.Count)).ToList();
}

/// <summary>B9 #55: draws in the runner: the weighted pool, one die per step under draw:{Var}#{i}, outputs per drawn counter, log lines.</summary>
public class DrawTests
{
    [Fact]
    public void Pool_order_by_piece_id_and_zero_weight_skipped()
    {
        // members in id order: b1 b1 b1 b2 b4 b4 (b3 has no boats); face 1 each time takes the first remaining member
        var s = DrawFx.State();
        var faces = new List<int>();
        var t = DrawFx.Run(s, DrawFx.Pack(), DrawFx.Request("6", dice: (k, n, sides) => { faces.Add(sides); return new[] { 1 }; }));
        Assert.Null(t.Error);
        Assert.Equal(new[] { 6, 5, 4, 3, 2, 1 }, faces);
        Assert.Equal(new[] { ("b1", 3), ("b2", 1), ("b4", 2) }, DrawFx.Split(t));
        Assert.Equal(new[] { LedgerKey.Piece("b1"), LedgerKey.Piece("b2"), LedgerKey.Piece("b4") }, t.Outputs.Select(o => o.Key));
    }

    [Fact]
    public void Same_hex_includes_run_piece()
    {
        var s = DrawFx.State();
        var pack = DrawFx.Pack(DrawFx.Battery(DrawFx.Hits(weight: null, filter: null)));
        var t = DrawFx.Run(s, pack, DrawFx.Request("10"));
        Assert.Null(t.Error);
        Assert.Equal(4.0, t.Result!.Value.Number);   // every barge in the run piece's hex, the run piece too; not the battery elsewhere
        Assert.Equal(new[] { ("b1", 1), ("b2", 1), ("b3", 1), ("b4", 1) }, DrawFx.Split(t));
        // without same hex the battery is in the pool too
        var all = DrawFx.Run(s, DrawFx.Pack(DrawFx.Battery(DrawFx.Hits(weight: null, sameHex: false, filter: null))), DrawFx.Request("10"));
        Assert.Contains(("n1", 1), DrawFx.Split(all));
        // same hex with no run piece
        var none = DrawFx.Run(s, pack, DrawFx.Request("10", piece: null));
        Assert.Equal("Needs a unit", Assert.Single(none.Variables, v => v.Variable == "Hits").MissingReason);
        Assert.Null(none.Result);
    }

    [Fact]
    public void Null_weight_counts_one_each()
    {
        // members b1 b2 b3 b4: face 4 takes b4, then face 3 of b1 b2 b3 takes b3 (a counter with no boats is a member too)
        var s = DrawFx.State();
        var pack = DrawFx.Pack(DrawFx.Battery(DrawFx.Hits(weight: null)));
        var t = DrawFx.Run(s, pack, DrawFx.Request("2", pins: DrawFx.Pins(4, 3)));
        Assert.Null(t.Error);
        Assert.Equal(new[] { ("b3", 1), ("b4", 1) }, DrawFx.Split(t));
    }

    [Fact]
    public void Weight_not_whole_fails_with_text()
    {
        foreach (var bad in new[] { 1.5, 1001, -1 })
        {
            var t = DrawFx.Run(DrawFx.State(b2Boats: bad), DrawFx.Pack(), DrawFx.Request());
            Assert.Equal("Boats of SC 2 Barge must be a whole number from 0 to 1000 to draw from", t.Error);
            Assert.Empty(DrawFx.Runner(DrawFx.Pack()).Runner.Commands(t, null));
        }
    }

    [Fact]
    public void Count_not_whole_or_over_1000_fails_with_text()
    {
        foreach (var bad in new[] { "2.5", "1001", "-1", "many" })
            Assert.Equal("The draw count for Hits must be a whole number from 0 to 1000", DrawFx.Run(DrawFx.State(), DrawFx.Pack(), DrawFx.Request(bad)).Error);
        var zero = DrawFx.Run(DrawFx.State(), DrawFx.Pack(), DrawFx.Request("0"));
        Assert.Null(zero.Error);
        Assert.Equal(0.0, zero.Result!.Value.Number);
        Assert.Empty(DrawFx.Split(zero));
        Assert.Empty(zero.DrawRolls);
    }

    [Fact]
    public void K_is_min_of_count_and_members()
    {
        var t = DrawFx.Run(DrawFx.State(), DrawFx.Pack(), DrawFx.Request("10"));
        Assert.Null(t.Error);
        Assert.Equal(6.0, t.Result!.Value.Number);
        Assert.Equal(6.0, Assert.Single(t.Variables, v => v.Variable == "Hits").Value!.Value.Number);
        Assert.Equal(new[] { ("b1", 3), ("b2", 1), ("b4", 2) }, DrawFx.Split(t));
        Assert.Equal(Enumerable.Range(1, 6).Select(i => $"draw:Hits#{i}"), t.DrawRolls.Keys);
    }

    [Fact]
    public void Pinned_draw_reproduces_split()
    {
        // b1 b1 b1 b2 b4 b4: face 1 -> b1; of b1 b1 b2 b4 b4 face 4 -> b4; of b1 b1 b2 b4 face 4 -> b4
        var (runner, calls) = DrawFx.Runner(DrawFx.Pack());
        var t = runner.Run(DrawFx.State(), DrawFx.Request(pins: DrawFx.Pins(1, 4, 4)));
        Assert.Null(t.Error);
        Assert.Empty(calls);
        Assert.Equal(new[] { ("b1", 1), ("b4", 2) }, DrawFx.Split(t));
        Assert.Equal(new[] { 1, 4, 4 }, t.DrawRolls.Values.Select(v => Assert.Single(v)));
        // the same pins draw the same counters again
        var again = runner.Run(DrawFx.State(), DrawFx.Request(pins: t.DrawRolls));
        Assert.Equal(DrawFx.Split(t), DrawFx.Split(again));
    }

    [Fact]
    public void Out_of_range_pin_redrawn()
    {
        // pin 7 of 6 faces is drawn again (the roller rolls 6 -> b4); then 1 -> b1, 1 -> b1
        var (runner, calls) = DrawFx.Runner(DrawFx.Pack());
        var t = runner.Run(DrawFx.State(), DrawFx.Request(pins: DrawFx.Pins(7, 1, 1)));
        Assert.Null(t.Error);
        Assert.Equal(new[] { (1, 6) }, calls);
        Assert.Equal(new[] { 6 }, t.DrawRolls["draw:Hits#1"]);
        Assert.Equal(new[] { ("b1", 2), ("b4", 1) }, DrawFx.Split(t));
        // a pin of two dice is no pin either
        var two = runner.Run(DrawFx.State(), DrawFx.Request("1", pins: new Dictionary<string, IReadOnlyList<int>> { ["draw:Hits#1"] = new[] { 1, 1 } }));
        Assert.Equal(new[] { 6 }, two.DrawRolls["draw:Hits#1"]);
    }

    [Fact]
    public void Dice_func_receives_draw_keys_with_remaining_faces()
    {
        var asked = new List<(string Key, int N, int Sides)>();
        var (runner, calls) = DrawFx.Runner(DrawFx.Pack());
        var t = runner.Run(DrawFx.State(), DrawFx.Request(dice: (k, n, sides) => { asked.Add((k, n, sides)); return new[] { 1 }; }));
        Assert.Null(t.Error);
        Assert.Empty(calls);
        Assert.Equal(new[] { ("draw:Hits#1", 1, 6), ("draw:Hits#2", 1, 5), ("draw:Hits#3", 1, 4) }, asked);
        // a die the Dice function gives outside the faces is rolled by the roller
        var odd = runner.Run(DrawFx.State(), DrawFx.Request("1", dice: (k, n, sides) => new[] { 9 }));
        Assert.Equal(new[] { 6 }, odd.DrawRolls["draw:Hits#1"]);
    }

    [Fact]
    public void Draw_keys_not_in_rolls_count_or_dice_log()
    {
        var pack = DrawFx.Pack(DrawFx.Battery(text: "Hits + dice(1, 6)"));
        var (runner, _) = DrawFx.Runner(pack);
        var t = runner.Run(DrawFx.State(), DrawFx.Request(pins: DrawFx.Pins(1, 1, 1)));
        Assert.Null(t.Error);
        Assert.Equal(new[] { "dice#0" }, t.RollsByKey.Keys);   // the inline dice keep their numbering
        Assert.Equal(new[] { "dice#0" }, t.RollSides.Keys);
        Assert.Equal("Shore battery: dice#0", Assert.Single(t.Rolls).Purpose);
        Assert.Equal(3, t.DrawRolls.Count);
        var cmds = runner.Commands(t, null, sideId: "north");
        Assert.Equal("Shore battery: dice#0", Assert.Single(cmds.OfType<RollDice>()).Purpose);
        var count = DrawFx.Run(DrawFx.State(), DrawFx.Pack(DrawFx.Battery(text: "count(Hits, 1, 6)")), DrawFx.Request());
        Assert.Equal("count() needs a roll binding, not Hits", count.Error);
    }

    [Fact]
    public void Not_shared_by_group_roll()
    {
        var (runner, calls) = DrawFx.Runner(DrawFx.Pack());
        var shared = new Dictionary<string, IReadOnlyList<int>>
        {
            ["draw:Hits#1"] = new[] { 1 }, [RollGroups.GroupKey("draw:Hits#1", 6) + "@d6"] = new[] { 1 },
            ["draw:Hits#2"] = new[] { 1 }, ["draw:Hits#3"] = new[] { 1 },
        };
        var t = runner.Run(DrawFx.State(), DrawFx.Request() with { SharedRolls = shared });
        Assert.Null(t.Error);
        Assert.Empty(t.SharedRollKeys);
        Assert.Equal(new[] { (1, 6), (1, 5), (1, 4) }, calls);   // drawn by the roller (highest face each time)
        Assert.Equal(new[] { ("b2", 1), ("b4", 2) }, DrawFx.Split(t));
    }

    [Fact]
    public void Output_value_reads_piece_share_elsewhere_k()
    {
        var f = DrawFx.Battery(text: "Hits * 10", outputs: new[]
        {
            DrawFx.Losses(),
            new FormulaOutput { Target = OutputTarget.Global, Property = "p-salvos", Op = OutputOp.Add, Expr = "Hits" },
        });
        var t = DrawFx.Run(DrawFx.State(), DrawFx.Pack(f), DrawFx.Request(pins: DrawFx.Pins(1, 4, 4)));
        Assert.Null(t.Error);
        Assert.Equal(30.0, t.Result!.Value.Number);
        var b1 = Assert.Single(t.Outputs, o => o.Key == LedgerKey.Piece("b1"));
        var b4 = Assert.Single(t.Outputs, o => o.Key == LedgerKey.Piece("b4"));
        Assert.Equal((3.0, 2.0), (b1.Before, b1.After));
        Assert.Equal((2.0, 0.0), (b4.Before, b4.After));
        Assert.Equal(("Hits", "Hits"), (b1.Draw, b4.Draw));
        var global = Assert.Single(t.Outputs, o => o.Target == OutputTarget.Global);
        Assert.Equal(3.0, global.After);
        Assert.Null(global.Draw);
    }

    [Fact]
    public void Each_drawn_piece_gets_own_ledger_delta_stamped_with_its_side()
    {
        var pack = DrawFx.Pack();
        var (runner, _) = DrawFx.Runner(pack);
        var t = runner.Run(DrawFx.State(), DrawFx.Request(pins: DrawFx.Pins(1, 4, 4)));
        var deltas = runner.Commands(t, null, sideId: "north").OfType<LedgerDelta>().ToList();
        Assert.Equal(2, deltas.Count);
        Assert.Equal((LedgerKey.Piece("b1"), "b1", "south", "SC 1 Barge", "Shore battery: Boats 3 − 1 = 2"),
            (deltas[0].Key, deltas[0].PieceId, deltas[0].SideId, deltas[0].Subject, deltas[0].Working));
        Assert.Equal((LedgerKey.Piece("b4"), "b4", "south", "SC 4 Barge", "Shore battery: Boats 2 − 2 = 0"),
            (deltas[1].Key, deltas[1].PieceId, deltas[1].SideId, deltas[1].Subject, deltas[1].Working));
        var s = DrawFx.State();
        foreach (var c in runner.Commands(t, null, sideId: "north")) s.Apply(c);
        Assert.Equal(2, Ledger.Get(s, LedgerKey.Piece("b1"), "p-boats"));
        Assert.Equal(0, Ledger.Get(s, LedgerKey.Piece("b4"), "p-boats"));
        Assert.Equal(1, Ledger.Get(s, LedgerKey.Piece("b2"), "p-boats"));
    }

    [Fact]
    public void Remove_on_draw_removes_each_drawn_piece_last()
    {
        var f = DrawFx.Battery(outputs: new[]
        {
            new FormulaOutput { Target = OutputTarget.Piece, PieceVariable = "Hits", Action = OutputAction.Remove },
            DrawFx.Losses(),
        });
        var pack = DrawFx.Pack(f);
        var (runner, _) = DrawFx.Runner(pack);
        var t = runner.Run(DrawFx.State(), DrawFx.Request(pins: DrawFx.Pins(1, 4, 4)));
        Assert.Null(t.Error);
        Assert.Equal(new[] { "b1", "b4" }, t.Effects.Select(e => e.PieceId));
        var cmds = runner.Commands(t, null, sideId: "north");
        Assert.Equal(new[] { "b1", "b4" }, cmds.OfType<RemovePiece>().Select(r => r.PieceId));
        Assert.All(cmds.OfType<RemovePiece>(), r => Assert.Equal("south", r.SideId));
        Assert.IsType<RemovePiece>(cmds[^1]);
        Assert.IsType<RemovePiece>(cmds[^2]);
        var s = DrawFx.State();
        foreach (var c in cmds) s.Apply(c);
        Assert.False(s.Pieces.ContainsKey("b1"));
        Assert.False(s.Pieces.ContainsKey("b4"));
        Assert.True(s.Pieces.ContainsKey("b2"));
    }

    [Fact]
    public void Draw_lines_name_piece_and_share_stamped_with_side()
    {
        var pack = DrawFx.Pack();
        var (runner, _) = DrawFx.Runner(pack);
        var s = DrawFx.State();
        DrawFx.Players(s);
        var t = runner.Run(s, DrawFx.Request(pins: DrawFx.Pins(1, 4, 4)));
        var cmds = runner.Commands(t, null, sideId: "north").ToList();
        var lines = cmds.OfType<AddLogLine>().Where(l => l.Kind == LogKind.System).ToList();
        Assert.Equal(new[] { "Shore battery: Hits: 1 on SC 1 Barge", "Shore battery: Hits: 2 on SC 4 Barge" }, lines.Select(l => l.Text));
        Assert.Equal(new[] { "b1", "b4" }, lines.Select(l => l.PieceId));
        Assert.All(lines, l => Assert.Equal("south", l.SideId));
        Assert.True(cmds.IndexOf(lines[0]) > cmds.FindLastIndex(c => c is LedgerDelta));   // after the ledger writes
        foreach (var c in cmds) s.Apply(c);
        var south = PlayerView.For(s, pack, Model, DrawFx.South);
        Assert.Contains(south.Log, l => l.Text == "Shore battery: Hits: 2 on SC 4 Barge");
        var north = PlayerView.For(s, pack, Model, DrawFx.North);
        Assert.DoesNotContain(north.Log, l => l.Text.Contains(" on SC "));
        Assert.Contains(north.Log, l => l.Text == "Southern Compact ledger changed");   // the writes, as any other side's
    }

    [Fact]
    public void Skip_draw_outputs_draws_but_acts_on_no_counter()
    {
        var s = DrawFx.State();
        var pack = DrawFx.Pack();
        var real = DrawFx.Run(s, pack, DrawFx.Request(pins: DrawFx.Pins(1, 1, 1)));
        var shown = DrawFx.Run(s, pack, DrawFx.Request(pins: DrawFx.Pins(1, 1, 1)) with { SkipDrawOutputs = true });
        Assert.Null(shown.Error);
        Assert.Equal(real.Result, shown.Result);
        Assert.Equal(real.Working, shown.Working);
        Assert.Equal(real.DrawRolls.Keys, shown.DrawRolls.Keys);
        Assert.Equal(DrawFx.Split(real), DrawFx.Split(shown));
        Assert.Contains(real.Outputs, o => o.Draw == "Hits");
        Assert.DoesNotContain(shown.Outputs, o => o.Draw != null);
    }

    [Fact]
    public void Acting_working_shows_k_not_split()
    {
        var pack = DrawFx.Pack();
        var (runner, _) = DrawFx.Runner(pack);
        var t = runner.Run(DrawFx.State(), DrawFx.Request(pins: DrawFx.Pins(1, 4, 4)));
        Assert.Contains("Hits = 3", t.Working);
        Assert.DoesNotContain("Barge", t.Working);
        var calc = Assert.Single(runner.Commands(t, null, sideId: "north").OfType<AddLogLine>(), l => l.Kind == LogKind.Calc);
        Assert.Equal("north", calc.SideId);
        Assert.Contains("Hits = 3", calc.Text);
        Assert.DoesNotContain("Barge", calc.Text);
        // a drawn counter's removal is not named in the acting side's working either
        var rm = DrawFx.Pack(DrawFx.Battery(outputs: new[] { new FormulaOutput { Target = OutputTarget.Piece, PieceVariable = "Hits", Action = OutputAction.Remove } }));
        var (rr, _) = DrawFx.Runner(rm);
        var rt = rr.Run(DrawFx.State(), DrawFx.Request(pins: DrawFx.Pins(1, 4, 4)));
        Assert.DoesNotContain("Barge", rt.Working);
        Assert.DoesNotContain("Barge", Assert.Single(rr.Commands(rt, null, sideId: "north").OfType<AddLogLine>(), l => l.Kind == LogKind.Calc).Text);
    }

    [Fact]
    public void Pool_hidden_on_view_is_missing_hidden()
    {
        var pack = DrawFx.Pack();
        var s = DrawFx.State();
        DrawFx.Players(s);
        var north = DrawFx.Run(PlayerView.For(s, pack, Model, DrawFx.North), pack, DrawFx.Request(pins: DrawFx.Pins(1, 4, 4)));
        Assert.Equal("Hidden from this player", Assert.Single(north.Variables, v => v.Variable == "Hits").MissingReason);
        Assert.Null(north.Result);
        Assert.Empty(north.Draws);
        // the Compact's own view holds the boats of its barges
        var south = DrawFx.Run(PlayerView.For(s, pack, Model, DrawFx.South), pack, DrawFx.Request(pins: DrawFx.Pins(1, 4, 4)));
        Assert.Null(south.Error);
        Assert.Equal(new[] { ("b1", 1), ("b4", 2) }, DrawFx.Split(south));
    }

    [Fact]
    public void Place_or_reveal_on_draw_fails()
    {
        var f = DrawFx.Battery(outputs: new[] { new FormulaOutput { Target = OutputTarget.Piece, PieceVariable = "Hits", Action = OutputAction.Reveal, Property = "p-boats" } });
        Assert.Equal("Formula \"Shore battery\": output 1: Reveal cannot act on the counters of draw \"Hits\"",
            DrawFx.Run(DrawFx.State(), DrawFx.Pack(f), DrawFx.Request()).Error);
    }

    [Fact]
    public void Filter_may_hide_is_the_aggregate_filter_rule()
    {
        // one test shared by the pool on a view, the masking and the piece aggregates (host and guest agree)
        var pack = DrawFx.Pack();
        Assert.False(FormulaVariables.FilterMayHide(pack, null));
        Assert.False(FormulaVariables.FilterMayHide(pack, " "));
        Assert.False(FormulaVariables.FilterMayHide(pack, "side() == \"Southern Compact\""));
        Assert.False(FormulaVariables.FilterMayHide(pack, "Salvos > 0"));                       // Public
        Assert.True(FormulaVariables.FilterMayHide(pack, "Boats > 0"));                         // Private
        Assert.True(FormulaVariables.FilterMayHide(pack, "sum(Salvos, \"\") > 0"));             // an aggregate
        Assert.True(FormulaVariables.FilterMayHide(pack, "holder(\"Global\", \"Salvos\") > 0")); // holder()
        Assert.True(FormulaVariables.FilterMayHide(pack, "Boats >"));                           // does not parse: fail closed
        foreach (var filter in new[] { null, "Salvos > 0", "Boats > 0", "Boats >" })
            Assert.Equal(FormulaVariables.FilterMayHide(pack, filter), FormulaVariables.AggregateCallHidden(pack, "sum", "Salvos", filter, false));
    }

    [Fact]
    public void All_zero_weights_draw_nothing()
    {
        var s = new GameState { GameId = "g" };
        DrawFx.Add(s, "b1", "SC 1 Barge", 0);
        DrawFx.Add(s, "b2", "SC 2 Barge", 0);
        var t = DrawFx.Run(s, DrawFx.Pack(), DrawFx.Request("3"));
        Assert.Null(t.Error);
        Assert.Equal(0.0, t.Result!.Value.Number);
        Assert.Empty(DrawFx.Split(t));
        Assert.Empty(t.DrawRolls);
        Assert.Empty(t.Outputs);
    }

    [Fact]
    public void Unset_weight_reads_default_and_computed_weight_reads_value()
    {
        // Boats Default 2: SC 3 Barge has no ledger, so it has 2 members
        var s = DrawFx.State();
        s.Ledgers.Remove(LedgerKey.Piece("b3"));
        var basePack = DrawFx.Pack();
        var withDefault = basePack with { Properties = basePack.Properties.Select(p => p.Id == "p-boats" ? p with { Default = 2 } : p).ToList() };
        var t = DrawFx.Run(s, withDefault, DrawFx.Request("100"));
        Assert.Equal(new[] { ("b1", 3), ("b2", 1), ("b3", 2), ("b4", 2) }, DrawFx.Split(t));
        // a computed weight (Public Load = Boats * 2, not for SC 2 Barge): one that does not apply is 0
        var load = new PropertyDef
        {
            Id = "p-load", Name = "Load", Scope = PropertyScope.Pieces, Visibility = Visibility.Public, Expr = "Boats * 2",
            AppliesTo = "name() != \"SC 2 Barge\"",
        };
        var computed = DrawFx.Pack(DrawFx.Battery(DrawFx.Hits(weight: "p-load"))) is var cp ? cp with { Properties = cp.Properties.Append(load).ToList() } : null!;
        var c = DrawFx.Run(DrawFx.State(), computed, DrawFx.Request("100"));
        Assert.Null(c.Error);
        Assert.Equal(new[] { ("b1", 6), ("b4", 4) }, DrawFx.Split(c));
    }

    [Fact]
    public void Draw_no_output_acts_on_still_logs_lines()
    {
        var f = DrawFx.Battery(outputs: new[] { new FormulaOutput { Target = OutputTarget.Global, Property = "p-salvos", Op = OutputOp.Add, Expr = "Hits" } });
        var pack = DrawFx.Pack(f);
        var (runner, _) = DrawFx.Runner(pack);
        var t = runner.Run(DrawFx.State(), DrawFx.Request(pins: DrawFx.Pins(1, 4, 4)));
        Assert.Null(t.Error);
        var cmds = runner.Commands(t, null, sideId: "north");
        Assert.Equal(LedgerKey.Holder(HolderDef.GlobalId), Assert.Single(cmds.OfType<LedgerDelta>()).Key);
        Assert.Equal(new[] { "Shore battery: Hits: 1 on SC 1 Barge", "Shore battery: Hits: 2 on SC 4 Barge" },
            cmds.OfType<AddLogLine>().Where(l => l.Kind == LogKind.System).Select(l => l.Text));
        Assert.Contains("Hits = 3", Assert.Single(cmds.OfType<LedgerDelta>()).Working);
    }

    [Fact]
    public void Undecided_precondition_holds_the_draw()
    {
        var f = DrawFx.Battery() with { Precondition = "Gate > 0" };
        f = f with { Bindings = f.Bindings.Append(new VariableBinding { Variable = "Gate", Kind = BindingKind.Input }).ToList() };
        var (runner, calls) = DrawFx.Runner(DrawFx.Pack(f));
        var t = runner.Run(DrawFx.State(), DrawFx.Request());
        Assert.Null(t.Result);
        Assert.True(Assert.Single(t.Variables, v => v.Variable == "Hits").Waiting);
        Assert.Empty(t.DrawRolls);
        Assert.Empty(calls);
    }

    [Fact]
    public void Preview_without_lines_logs_no_draw_line()
    {
        // fail closed: a preview not made by the runner has no names or sides, so no (public, unstamped) line is logged
        var pack = DrawFx.Pack();
        var (runner, _) = DrawFx.Runner(pack);
        var t = runner.Run(DrawFx.State(), DrawFx.Request(pins: DrawFx.Pins(1, 4, 4)));
        var bare = t with { Draws = t.Draws.Select(d => new DrawPreview(d.Variable, d.Drawn)).ToList() };
        Assert.DoesNotContain(runner.Commands(bare, null, sideId: "north").OfType<AddLogLine>(), l => l.Kind == LogKind.System);
    }
}
