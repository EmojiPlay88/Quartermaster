using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.TestFixtures;

namespace Quartermaster.Core.Tests;

/// <summary>What a player's view keeps of keyed and place values (B6). Invented Riverland data.</summary>
public class PlacesPlayerViewTests
{
    private static readonly IReadOnlySet<string> Red = new HashSet<string> { "red" };
    private static readonly IReadOnlySet<string> Blue = new HashSet<string> { "blue" };
    private static readonly IReadOnlySet<string> Both = new HashSet<string> { "red", "blue" };

    private static GameState View(GameState full, IReadOnlySet<string> held, RulesPack? pack = null) =>
        PlayerView.For(full, pack ?? PlacesFixture.Pack(), PlacesKit.Model, held);

    private static double? Value(GameState s, string key, string prop, string valueKey) => KeyedValues.Get(s, key, prop, valueKey);

    [Fact]
    public void View_drops_private_place_values_and_history()
    {
        var full = PlacesKit.State();
        foreach (var held in new[] { Red, Blue })
        {
            var v = View(full, held);
            Assert.Null(Value(v, PlacesKit.Global, "p-mines", "0505"));
            Assert.DoesNotContain(v.LedgerHistory, e => e.PropertyId == "p-mines");
            Assert.False(v.Keyed![PlacesKit.Global].ContainsKey("p-mines"));
        }
    }

    [Fact]
    public void View_keeps_public_place()
    {
        var full = PlacesKit.State();
        foreach (var held in new[] { Red, Blue })
        {
            var v = View(full, held);
            Assert.Equal(3, Value(v, PlacesKit.Global, "p-ford", "0412"));
            Assert.Equal(5, Value(v, PlacesKit.Global, "p-due", "2"));
            Assert.Contains(v.LedgerHistory, e => e.PropertyId == "p-ford" && e.ValueKey == "0412");
            Assert.Contains(v.Log, l => l.Text.Contains("FordDepth[0412] 0 → 3"));
            Assert.Equal("FordDepth 3 (Flooded)", KeyedValues.PlaceText(PlacesFixture.Pack(), KeyedValues.PlacesAt(v, PlacesFixture.Pack(), "0412")));
        }
    }

    [Fact]
    public void View_private_place_log_line_redacted()
    {
        var v = View(PlacesKit.State(), Red);
        Assert.DoesNotContain(v.Log, l => l.Text.Contains("Mines") || l.Text.Contains("0505"));
        Assert.Contains(v.Log, l => l.Text.EndsWith("ledger changed"));
        Assert.Equal("", KeyedValues.PlaceText(PlacesFixture.Pack(), KeyedValues.PlacesAt(v, PlacesFixture.Pack(), "0505")));
    }

    [Fact]
    public void View_keyed_piece_value_of_other_side_dropped()
    {
        var full = PlacesKit.State();
        var red = View(full, Red);
        Assert.Equal(7, Value(red, LedgerKey.Piece("r1"), "p-cargo", "Barge"));
        Assert.Null(Value(red, LedgerKey.Piece("b1"), "p-cargo", "Raft"));
        Assert.False(red.Keyed!.ContainsKey(LedgerKey.Piece("b1")));
        Assert.DoesNotContain(red.LedgerHistory, e => e.Key == LedgerKey.Piece("b1"));
        var blue = View(full, Blue);
        Assert.Equal(4, Value(blue, LedgerKey.Piece("b1"), "p-cargo", "Raft"));
        Assert.Null(Value(blue, LedgerKey.Piece("r1"), "p-cargo", "Barge"));
        // A holder owned by a side not held loses its keyed values too.
        PlacesKit.Put(full, "h:blueyard", "p-hulls", "Barge", 8);
        Assert.False(View(full, Red).Keyed!.ContainsKey("h:blueyard"));
        Assert.Equal(8, Value(View(full, Blue), "h:blueyard", "p-hulls", "Barge"));
    }

    [Fact]
    public void View_keyed_read_hidden_is_HiddenData()
    {
        var full = PlacesKit.State();
        PlacesKit.Put(full, "h:blueyard", "p-hulls", "Barge", 8);
        var pack = PlacesFixture.Pack();
        var v = View(full, Red, pack);
        EvalError Hidden(string text, string pieceId)
        {
            var ctx = new PieceFilter.FilterContext(v.Pieces[pieceId], v, pack, PieceFilter.SideMap(v, pack), new AggregateFilters(v, pack, PlacesKit.Model));
            return Assert.Throws<EvalError>(() => Evaluator.Evaluate(Parser.Parse(text), ctx));
        }
        foreach (var (text, piece) in new[]
                 {
                     ("keyed(Mines, \"0505\")", "r1"),               // a Private place
                     ("keyed(Cargo, \"Raft\")", "b1"),               // a Private piece value of a side not held
                     ("keyed(HullCount, \"Barge\", Blueyard)", "r1"),   // a holder of a side not held
                 })
        {
            var e = Hidden(text, piece);
            Assert.Equal("keyed() reads data this player cannot see", e.Message);
            Assert.True(e.HiddenData);
        }
        // What the view may see reads as usual.
        Assert.Equal(3, PlacesKit.Eval(v, "keyed(FordDepth, hex())", "r1").Result!.Value.Number);
        Assert.Equal(7, PlacesKit.Eval(v, "keyed(Cargo, \"Barge\")", "r1").Result!.Value.Number);
        Assert.Equal(1, PlacesKit.Eval(v, "keyed(HullCount, \"Raft\", Boatyard)", "r1").Result!.Value.Number);
        // A run on the view is undecided (no answer, no error) when its precondition reads hidden data.
        var pre = PlacesKit.Run(v, PlacesKit.Pack(PlacesKit.F("1") with { Precondition = "keyed(Mines, \"0505\") > 0" }), "r1");
        Assert.Null(pre.Error);
        Assert.Null(pre.Result);
        Assert.Equal("keyed() reads data this player cannot see", PlacesKit.Eval(v, "keyed(Mines, \"0505\")", "r1").Error);
    }

    [Fact]
    public void View_formula_reading_private_place_hides_working()
    {
        var full = PlacesKit.State();
        var f = new FormulaDef
        {
            Id = "f-mine", Name = "Mine check", Text = "keyed(Mines, \"0505\") + 1",
            Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "p-str", Op = OutputOp.Set } },
        };
        var pack = PlacesKit.Pack(f);
        var runner = new FormulaRunner(pack, PlacesKit.Model, (n, _) => Enumerable.Repeat(1, n).ToList());
        var t = runner.Run(full, new RunRequest("f-mine", "r1", new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<int>>()));
        Assert.Null(t.Error);
        foreach (var c in runner.Commands(t, null, sideId: "red")) full.Apply(c);
        Assert.Contains(full.Log, l => l.Text.Contains("keyed(Mines"));
        var red = View(full, Red, pack);
        Assert.DoesNotContain(red.Log, l => l.Text.Contains("keyed(Mines"));
        Assert.Null(red.LedgerHistory.Last(e => e.PropertyId == "p-str").Working);
        // A formula reading only a Public place keeps its working.
        var open = f with { Id = "f-ford", Name = "Ford check", Text = "keyed(FordDepth, \"0412\") + 1" };
        var pack2 = PlacesKit.Pack(open);
        var full2 = PlacesKit.State();
        var runner2 = new FormulaRunner(pack2, PlacesKit.Model, (n, _) => Enumerable.Repeat(1, n).ToList());
        var t2 = runner2.Run(full2, new RunRequest("f-ford", "r1", new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<int>>()));
        foreach (var c in runner2.Commands(t2, null, sideId: "red")) full2.Apply(c);
        Assert.Contains(View(full2, Red, pack2).Log, l => l.Text.Contains("keyed(FordDepth"));
    }

    [Fact]
    public void View_formula_writing_private_place_hides_working_of_its_other_lines()
    {
        var full = PlacesKit.State();
        var f = new FormulaDef
        {
            Id = "f-lay", Name = "Lay mines", Text = "2",
            Outputs = new()
            {
                new FormulaOutput { Target = OutputTarget.Unit, Property = "p-str", Op = OutputOp.Set },
                new FormulaOutput { Target = OutputTarget.Global, Property = "p-mines", Op = OutputOp.Add, Key = "\"0303\"" },
            },
        };
        var pack = PlacesKit.Pack(f);
        var runner = new FormulaRunner(pack, PlacesKit.Model, (n, _) => Enumerable.Repeat(1, n).ToList());
        var t = runner.Run(full, new RunRequest("f-lay", "r1", new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<int>>()));
        Assert.Null(t.Error);
        foreach (var c in runner.Commands(t, null, sideId: "red")) full.Apply(c);
        Assert.Contains(full.Log, l => l.Text.Contains("Mines[0303] 0 + 2 = 2"));
        var red = View(full, Red, pack);
        Assert.DoesNotContain(red.Log, l => l.Text.Contains("Mines") || l.Text.Contains("0303"));
        Assert.DoesNotContain(red.LedgerHistory, e => e.PropertyId == "p-mines" || (e.Working?.Contains("Mines") ?? false));
        // A guest's preview on its view cannot add to a value it lacks: the run waits for the host.
        var preview = runner.Run(red, new RunRequest("f-lay", "r1", new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<int>>()));
        Assert.Null(preview.Error);
        Assert.Null(preview.Result);
        Assert.Empty(preview.Outputs);
    }

    [Fact]
    public void Full_view_keeps_private_place()
    {
        var v = View(PlacesKit.State(), Both);
        Assert.Equal(2, Value(v, PlacesKit.Global, "p-mines", "0505"));
        Assert.Contains(v.LedgerHistory, e => e.PropertyId == "p-mines");
        Assert.Contains(v.Log, l => l.Text.Contains("Mines[0505] 0 → 2"));
        Assert.Equal(4, Value(v, LedgerKey.Piece("b1"), "p-cargo", "Raft"));
    }

    [Fact]
    public void View_of_pack_without_keyed_parses_no_keyed_reads()
    {
        var plain = new RulesPack
        {
            Sides = PlacesFixture.Pack().Sides,
            Properties = new() { new PropertyDef { Id = "p-str", Name = "Strength", Scope = PropertyScope.Pieces, Visibility = Visibility.Public } },
            Formulas = new() { new FormulaDef { Id = "f", Name = "F", Text = "Strength + 1" } },
        };
        var full = PlacesKit.State();
        PlayerView.KeyedReadParses = 0;
        View(full, Red, plain);
        Assert.Equal(0, PlayerView.KeyedReadParses);
        // With keyed properties each formula record is parsed once, not once per view.
        var keyed = PlacesKit.Pack(new FormulaDef { Id = "g", Name = "G", Text = "keyed(Mines, \"0505\")" });
        View(full, Red, keyed);
        Assert.Equal(1, PlayerView.KeyedReadParses);
        View(full, Blue, keyed);
        Assert.Equal(1, PlayerView.KeyedReadParses);
    }

    [Fact]
    public void Lockdown_view_has_no_keyed()
    {
        PlayerView.FailForTest = true;
        try { Assert.Null(View(PlacesKit.State(), Red).Keyed); }
        finally { PlayerView.FailForTest = false; }
    }
}
