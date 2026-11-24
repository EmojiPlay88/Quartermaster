using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;
using static Quartermaster.Core.Tests.RunnerFx;

namespace Quartermaster.Core.Tests;

/// <summary>Piece bindings: a property of another piece, picked by its label when the formula runs. Invented Riverland-style data.</summary>
public class PieceBindingRunTests
{
    private const string HideTrait = "hide;I;Hide it;;;0.3;;false";
    private const string ObsTrait = "obs;M;m.png;Mask it;I;?";
    private static readonly IReadOnlySet<string> Blue = new HashSet<string> { "blue" };
    private static readonly IReadOnlySet<string> Red = new HashSet<string> { "red" };

    private static VariableBinding Pick(string property = "str", string? filter = null) =>
        new() { Variable = "S", Kind = BindingKind.Piece, Property = property, PieceFilter = filter };

    private static RulesPack Pack(VariableBinding? binding = null, bool sides = false) => new()
    {
        Format = 4,
        Sides = sides
            ? new()
            {
                new SideDef { Id = "red", Name = "Red", UnitFilter = "startsWith(name(), \"Red\")" },
                new SideDef { Id = "blue", Name = "Blue", UnitFilter = "startsWith(name(), \"Blue\")" },
            }
            : new(),
        Properties = new()
        {
            new PropertyDef { Id = "str", Name = "Strength", Scope = PropertyScope.Pieces, Default = 2, Visibility = Visibility.Public },
            new PropertyDef { Id = "secret", Name = "Secret", Scope = PropertyScope.Pieces, Default = 0, Visibility = Visibility.Private },
            new PropertyDef { Id = "tally", Name = "Tally", Scope = PropertyScope.Holders, Default = 0 },
        },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "f", Name = "Compare", Text = "S",
                Bindings = new() { binding ?? Pick() },
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "tally", Op = OutputOp.Set } },
            },
        },
    };

    private static void Add(GameState s, string id, string name, double? str = null, double x = 60, double y = 40, string map = "Main Map",
        string? trait = null, double? secret = null)
    {
        var layers = new List<string>();
        var states = new List<string>();
        if (trait != null) { layers.Add(trait); states.Add("red"); }
        layers.Add("piece;;;unit.svg;" + name);
        states.Add("");
        s.Pieces[id] = new PieceInstance { Id = id, MapId = map, Pos = new PointD(x, y), TypeString = PieceTypeParser.EncodeState(layers), TraitStates = states };
        var ledger = new OrderedDictionary<string, double>();
        if (str != null) ledger["str"] = str.Value;
        if (secret != null) ledger["secret"] = secret.Value;
        if (ledger.Count > 0) s.Ledgers[LedgerKey.Piece(id)] = ledger;
    }

    private static FormulaTrace Run(GameState s, RulesPack pack, string? typed, IReadOnlySet<string>? pickSides = null, bool auto = false, string? piece = null)
    {
        var t = typed == null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["S"] = typed };
        return new FormulaRunner(pack, Model, (n, _) => Enumerable.Repeat(1, n).ToList())
            .Run(s, new RunRequest("f", piece, t, new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>(),
                UseAutoValues: auto, PickSides: pickSides));
    }

    private static string Loc(GameState s, string id) => PieceInfo.Location(s.Pieces[id], Model) ?? throw new InvalidOperationException("off grid");

    private static GameState Two()
    {
        var s = new GameState();
        Add(s, "r1", "Red Rifles", 3);
        Add(s, "b1", "Blue Guns", 7, x: 160);
        return s;
    }

    [Fact]
    public void Reads_picked_piece_property()
    {
        var s = Two();
        var pack = Pack();
        var label = $"Blue Guns ({Loc(s, "b1")})";
        Assert.Contains(PieceChoices.For(s, pack, Model, Pick(), null), c => c.Label == label && c.PieceId == "b1");
        var t = Run(s, pack, label);
        Assert.Null(t.Error);
        Assert.Equal(7, t.Result!.Value.Number);
        // the typed label is matched trimmed and ignoring case
        Assert.Equal(7, Run(s, pack, "  " + label.ToUpperInvariant() + " ").Result!.Value.Number);
        Assert.Equal(3, Run(s, pack, $"red rifles ({Loc(s, "r1")})").Result!.Value.Number);
    }

    [Fact]
    public void Default_when_unset()
    {
        var s = new GameState();
        Add(s, "b1", "Blue Guns");
        var t = Run(s, Pack(), $"Blue Guns ({Loc(s, "b1")})");
        Assert.Null(t.Error);
        Assert.Equal(2, t.Result!.Value.Number);
    }

    [Fact]
    public void Labels_with_location_duplicates_sorted()
    {
        var s = new GameState();
        Add(s, "r1", "Red Rifles", 3);
        Add(s, "b1", "Blue Guns", x: 160);
        Add(s, "r2", "Red Rifles", x: 62, y: 42);   // same square as r1: same label, later in state order
        Add(s, "z1", "Zulu Depot", map: "Nowhere");   // no board: off grid
        Add(s, "a1", "apple Cart", x: 160);
        Add(s, "r3", "Red Rifles", x: 61, y: 41);
        var l = Loc(s, "r1");
        Assert.Equal(l, Loc(s, "r2"));
        Assert.Equal(l, Loc(s, "r3"));
        var lb = Loc(s, "b1");
        var list = PieceChoices.For(s, Pack(), Model, Pick(), null);
        Assert.Equal(new[]
        {
            ($"apple Cart ({lb})", "a1"),
            ($"Blue Guns ({lb})", "b1"),
            ($"Red Rifles ({l})", "r1"),
            ($"Red Rifles ({l}) #2", "r2"),
            ($"Red Rifles ({l}) #3", "r3"),
            ("Zulu Depot", "z1"),
        }, list.Select(c => (c.Label, c.PieceId)).ToArray());
        Assert.Equal(2, Run(s, Pack(), $"Red Rifles ({l}) #2").Result!.Value.Number);
        Assert.Equal(3, Run(s, Pack(), $"Red Rifles ({l})").Result!.Value.Number);
        Assert.Equal(2, Run(s, Pack(), "Zulu Depot").Result!.Value.Number);
    }

    [Fact]
    public void Filter_limits_candidates()
    {
        var s = Two();
        var b = Pick(filter: "startsWith(name(), \"Blue\")");
        var list = PieceChoices.For(s, Pack(b), Model, b, null);
        Assert.Equal("b1", Assert.Single(list).PieceId);
        var t = Run(s, Pack(b), $"Red Rifles ({Loc(s, "r1")})");
        Assert.Null(t.Result);
        Assert.Equal("Choose one of the listed pieces", Assert.Single(t.Variables).MissingReason);
    }

    [Fact]
    public void No_candidates_error()
    {
        var s = Two();
        var b = Pick(filter: "name() == \"Nobody\"");
        var t = Run(s, Pack(b), "Blue Guns");
        Assert.Null(t.Result);
        Assert.Equal("No piece matches \"name() == \"Nobody\"\"", Assert.Single(t.Variables).MissingReason);
        Assert.Empty(PieceChoices.For(s, Pack(b), Model, b, null));
        var empty = Run(new GameState(), Pack(), null);
        Assert.Equal("No piece to choose", Assert.Single(empty.Variables).MissingReason);
    }

    [Fact]
    public void Pick_resolves_only_visible_to_pick_sides()
    {
        // r2 (hidden from Blue, earlier in state order, Strength 9) and r1 (visible, Strength 3) share name and square.
        var s = new GameState();
        Add(s, "r2", "Red Rifles", 9, x: 62, y: 42, trait: HideTrait);
        Add(s, "r1", "Red Rifles", 3);
        Add(s, "b1", "Blue Guns", 7, x: 160);
        var pack = Pack(sides: true);
        var l = Loc(s, "r1");
        Assert.Equal(new[] { ($"Red Rifles ({l})", "r2"), ($"Red Rifles ({l}) #2", "r1") },
            PieceChoices.For(s, pack, Model, Pick(), null).Where(c => c.Label.StartsWith("Red")).Select(c => (c.Label, c.PieceId)).ToArray());
        var blue = PieceChoices.For(s, pack, Model, Pick(), Blue);
        Assert.DoesNotContain(blue, c => c.PieceId == "r2");
        Assert.Contains(blue, c => c.Label == $"Red Rifles ({l})" && c.PieceId == "r1");

        Assert.Equal(3, Run(s, pack, $"Red Rifles ({l})", Blue).Result!.Value.Number);   // the guest's pick, never the hidden piece
        Assert.Equal(9, Run(s, pack, $"Red Rifles ({l})").Result!.Value.Number);         // full state: every piece
        var guessed = Run(s, pack, $"Red Rifles ({l}) #2", Blue);
        Assert.Null(guessed.Result);
        Assert.Equal("Choose one of the listed pieces", Assert.Single(guessed.Variables).MissingReason);
        Assert.Equal(9, Run(s, pack, $"Red Rifles ({l})", Red).Result!.Value.Number);    // Red sees its own hidden piece
    }

    [Fact]
    public void Masked_piece_listed_by_placeholder_name()
    {
        var s = new GameState();
        Add(s, "r1", "Red Secret Unit", 4, trait: ObsTrait, secret: 8);
        Add(s, "b1", "Blue Guns", 7, x: 160);
        var pack = Pack(sides: true);
        var l = Loc(s, "r1");
        var placeholder = PieceInfo.Name(PlayerView.For(s, pack, Model, Blue).Pieces["r1"]);
        Assert.DoesNotContain("Secret", placeholder);

        var blue = PieceChoices.For(s, pack, Model, Pick(), Blue);
        Assert.All(blue, c => Assert.DoesNotContain("Secret", c.Label));
        var masked = Assert.Single(blue, c => c.PieceId == "r1");
        Assert.Equal("", placeholder);
        Assert.Equal($"Unknown piece ({l})", masked.Label);
        Assert.Contains(PieceChoices.For(s, pack, Model, Pick(), null), c => c.Label == $"Red Secret Unit ({l})");
        Assert.Contains(PieceChoices.For(s, pack, Model, Pick(), Red), c => c.Label == $"Red Secret Unit ({l})");

        // Host run for Blue: a Public value reads normally, and the working never names the hidden piece.
        var t = Run(s, pack, masked.Label, Blue);
        Assert.Equal(4, t.Result!.Value.Number);
        Assert.Equal("Strength of Unknown piece", Assert.Single(t.Variables).Source);
        Assert.Equal(4, Run(s, pack, $"Red Secret Unit ({l})").Result!.Value.Number);

        // On Blue's own view the placeholder's values are gone: neither the Private nor the Public value is read (no Default either).
        var view = PlayerView.For(s, pack, Model, Blue);
        var viewLabel = Assert.Single(PieceChoices.For(view, pack, Model, Pick(), Blue), c => c.PieceId == "r1").Label;
        Assert.Equal(masked.Label, viewLabel);
        foreach (var prop in new[] { "secret", "str" })
        {
            var g = Run(view, Pack(Pick(prop), sides: true), viewLabel, Blue);
            Assert.Null(g.Result);
            Assert.NotNull(Assert.Single(g.Variables).MissingReason);
        }
    }

    [Fact]
    public void Stale_pick_is_error()
    {
        var s = Two();
        var pack = Pack();
        var label = Assert.Single(PieceChoices.For(s, pack, Model, Pick(), null), c => c.PieceId == "b1").Label;
        s.Pieces["b1"].Pos = new PointD(60, 40);   // moved after the list was shown
        var runner = new FormulaRunner(pack, Model, (n, _) => Enumerable.Repeat(1, n).ToList());
        var moved = Run(s, pack, label);
        Assert.Null(moved.Result);
        Assert.Equal("Choose one of the listed pieces", Assert.Single(moved.Variables).MissingReason);
        Assert.Empty(moved.Outputs);
        Assert.Empty(runner.Commands(moved, null));
        s.Pieces.Remove("b1");   // deleted
        var gone = Run(s, pack, label);
        Assert.Equal("Choose one of the listed pieces", Assert.Single(gone.Variables).MissingReason);
        Assert.Empty(runner.Commands(gone, null));
    }

    [Fact]
    public void Auto_run_waits_for_piece_pick()
    {
        var s = Two();
        var b = Pick() with { AutoValue = "1" };
        var t = Run(s, Pack(b), null, auto: true);
        Assert.Null(t.Result);
        Assert.Null(t.Error);
        var v = Assert.Single(t.Variables);
        Assert.Equal("Waiting for input", v.MissingReason);
        Assert.False(v.Auto);
        Assert.Equal("Waiting for input", Assert.Single(Run(s, Pack(b), "  ").Variables).MissingReason);
    }

    [Fact]
    public void Working_names_piece()
    {
        var s = Two();
        var t = Run(s, Pack(), $"Blue Guns ({Loc(s, "b1")})", piece: "r1");
        Assert.Null(t.Error);
        var v = Assert.Single(t.Variables);
        Assert.Equal("Strength of Blue Guns", v.Source);
        Assert.False(v.Auto);
        Assert.Equal(7, Assert.Single(t.Outputs).After);
    }

    [Fact]
    public void Nameless_candidate_labelled_unknown_piece()
    {
        var s = new GameState();
        Add(s, "n1", "");
        Add(s, "n2", "", map: "Nowhere");   // off grid
        var list = PieceChoices.For(s, Pack(), Model, Pick(), null);
        Assert.Equal(new[] { ("Unknown piece", "n2"), ($"Unknown piece ({Loc(s, "n1")})", "n1") },
            list.Select(c => (c.Label, c.PieceId)).ToArray());
        Assert.Equal(2, Run(s, Pack(), "Unknown piece").Result!.Value.Number);
    }

    [Fact]
    public void Duplicate_suffix_skips_real_names()
    {
        // A piece really named "Depot #2" keeps its plain label; the second "Depot" skips to " #3".
        var s = new GameState();
        Add(s, "d1", "Depot", 1, map: "Nowhere");
        Add(s, "d2", "Depot", 2, map: "Nowhere");
        Add(s, "d3", "Depot #2", 3, map: "Nowhere");
        Add(s, "d4", "Depot", 4, map: "Nowhere");
        var list = PieceChoices.For(s, Pack(), Model, Pick(), null);
        Assert.Equal(new[] { ("Depot", "d1"), ("Depot #2", "d3"), ("Depot #3", "d2"), ("Depot #4", "d4") },
            list.Select(c => (c.Label, c.PieceId)).ToArray());
        Assert.Equal(list.Count, list.Select(c => c.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(3, Run(s, Pack(), "Depot #2").Result!.Value.Number);
        Assert.Equal(2, Run(s, Pack(), "Depot #3").Result!.Value.Number);
    }

    [Fact]
    public void Player_view_private_property_of_visible_enemy_is_hidden()
    {
        var s = new GameState();
        Add(s, "r1", "Red Rifles", 3, secret: 8);
        Add(s, "b1", "Blue Guns", 7, x: 160);
        var b = Pick("secret");
        var pack = Pack(b, sides: true);
        var view = PlayerView.For(s, pack, Model, Blue);
        var label = Assert.Single(PieceChoices.For(view, pack, Model, b, Blue), c => c.PieceId == "r1").Label;
        Assert.Equal($"Red Rifles ({Loc(s, "r1")})", label);
        var t = Run(view, pack, label, Blue);
        Assert.Null(t.Result);
        var v = Assert.Single(t.Variables);
        Assert.Equal("Hidden from this player", v.MissingReason);
        Assert.Null(v.Value);
        // Blue's own piece reads its Private value as usual.
        Assert.Equal(0, Run(view, pack, $"Blue Guns ({Loc(s, "b1")})", Blue).Result!.Value.Number);
    }

    [Fact]
    public void View_drops_public_piece_binding_working_when_pieces_may_hide()
    {
        // A Public property read through a pick: shown to Blue when no piece can hide from it, hidden once one may (a masked pick reads the real value on the host).
        var s = Two();
        s.LedgerHistory.Add(new LedgerEntry(1, LedgerKey.Piece("b1"), "str", 7, 3, "formula", "f", null, null, "Strength of Red Rifles 3"));
        s.Log.Add(new LogEntry(1, LogKind.Formula, "Compare: Strength of Red Rifles 3", null, "b1", "Main Map", DateTimeOffset.UnixEpoch, FormulaId: "f"));
        var open = Pack(Pick("str"), sides: true);
        Assert.Contains(PlayerView.For(s, open, Model, Blue).LedgerHistory, e => e.FormulaId == "f" && e.Working != null);
        var mayHide = open with { MapOwners = new() { new MapOwner { MapId = "Red Map", SideId = "red" } } };
        Assert.True(PieceAccess.PiecesMayHide(mayHide, Model, Blue));
        Assert.DoesNotContain(PlayerView.For(s, mayHide, Model, Blue).LedgerHistory, e => e.FormulaId == "f" && e.Working != null);
        Assert.Contains(PlayerView.For(s, mayHide, Model, new HashSet<string> { "red", "blue" }).LedgerHistory, e => e.FormulaId == "f" && e.Working != null);
    }
}
