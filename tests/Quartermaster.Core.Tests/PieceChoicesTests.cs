using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;
using static Quartermaster.Core.Tests.RunnerFx;

namespace Quartermaster.Core.Tests;

/// <summary>B3 #3: Piece binding candidates list the run piece's hex first; "same hex" keeps only that hex. Invented Riverland data.</summary>
public class PieceChoicesTests
{
    /// <summary>b1 (run piece), b2, r1 share a square; a1 and z9 are elsewhere on the map; d1 is off grid.</summary>
    private static GameState Hex()
    {
        var s = new GameState();
        PieceOutFx.Add(s, "a1", "North Archers", x: 160);
        PieceOutFx.Add(s, "b1", "South Barge", fuel: 5);
        PieceOutFx.Add(s, "z9", "Ammo Dump", x: 160, y: 80);
        PieceOutFx.Add(s, "r1", "North Rifles", 7, x: 62, y: 42);
        PieceOutFx.Add(s, "b2", "South Tug", x: 61, y: 41);
        PieceOutFx.Add(s, "d1", "Dead pile marker", map: "Nowhere");
        return s;
    }

    private static RulesPack Pack(VariableBinding b) => PieceOutFx.Pack(PieceOutFx.Shell() with { Bindings = new() { b } });

    private static string[] Ids(IEnumerable<PieceChoice> list) => list.Select(c => c.PieceId).ToArray();

    [Fact]
    public void Same_hex_candidates_listed_first()
    {
        var s = Hex();
        var b = PieceOutFx.Pick();
        Assert.Equal(PieceOutFx.Loc(s, "b1"), PieceOutFx.Loc(s, "r1"));
        Assert.Equal(PieceOutFx.Loc(s, "b1"), PieceOutFx.Loc(s, "b2"));
        // hex group (by label): North Rifles, South Barge, South Tug; then the rest by label: Ammo Dump, Dead pile marker, North Archers
        Assert.Equal(new[] { "r1", "b1", "b2", "z9", "d1", "a1" }, Ids(PieceChoices.For(s, Pack(b), Model, b, null, "b1")));
        // labels unchanged
        Assert.Contains(PieceChoices.For(s, Pack(b), Model, b, null, "b1"), c => c.Label == $"North Rifles ({PieceOutFx.Loc(s, "r1")})");
        // a run piece off grid: today's order
        Assert.Equal(Ids(PieceChoices.For(s, Pack(b), Model, b, null)), Ids(PieceChoices.For(s, Pack(b), Model, b, null, "d1")));
    }

    [Fact]
    public void SameHex_limits_to_hex_without_run_piece()
    {
        var s = Hex();
        var b = PieceOutFx.Pick(sameHex: true);
        Assert.Equal(new[] { "r1", "b2" }, Ids(PieceChoices.For(s, Pack(b), Model, b, null, "b1")));
        // with a filter as well
        var only = PieceOutFx.Pick(sameHex: true, filter: "startsWith(name(), \"South\")");
        Assert.Equal(new[] { "b2" }, Ids(PieceChoices.For(s, Pack(only), Model, only, null, "b1")));

        // the runner resolves among the same list: a piece outside the hex is not a candidate
        var pack = Pack(b);
        var t = PieceOutFx.Run(s, pack, "b1", PieceOutFx.Typed($"North Archers ({PieceOutFx.Loc(s, "a1")})"));
        Assert.Equal(PieceChoices.Unknown, Assert.Single(t.Variables).MissingReason);
        var ok = PieceOutFx.Run(s, pack, "b1", PieceOutFx.Typed($"North Rifles ({PieceOutFx.Loc(s, "r1")})"));
        Assert.Null(ok.Error);
        Assert.Equal(LedgerKey.Piece("r1"), Assert.Single(ok.Outputs).Key);
    }

    [Fact]
    public void SameHex_off_grid_no_piece_in_hex()
    {
        var s = Hex();
        PieceOutFx.Add(s, "d2", "Spare marker", map: "Nowhere");   // also off grid: never "the same hex"
        var b = PieceOutFx.Pick(sameHex: true);
        Assert.Empty(PieceChoices.For(s, Pack(b), Model, b, null, "d1"));
        var t = PieceOutFx.Run(s, Pack(b), "d1", PieceOutFx.Typed("Spare marker"));
        Assert.Null(t.Result);
        Assert.Equal("No piece in this hex", PieceChoices.NoPieceInHex);
        Assert.Equal(PieceChoices.NoPieceInHex, Assert.Single(t.Variables).MissingReason);

        // alone in its hex
        var lone = new GameState();
        PieceOutFx.Add(lone, "b1", "South Barge");
        PieceOutFx.Add(lone, "a1", "North Archers", x: 160);
        Assert.Equal(PieceChoices.NoPieceInHex, Assert.Single(PieceOutFx.Run(lone, Pack(b), "b1").Variables).MissingReason);
    }

    [Fact]
    public void SameHex_without_run_piece_needs_unit()
    {
        var s = Hex();
        var b = PieceOutFx.Pick(sameHex: true);
        Assert.Empty(PieceChoices.For(s, Pack(b), Model, b, null, null));
        var t = PieceOutFx.Run(s, Pack(b), null, PieceOutFx.Typed($"North Rifles ({PieceOutFx.Loc(s, "r1")})"));
        Assert.Null(t.Result);
        Assert.Equal("Needs a unit", Assert.Single(t.Variables).MissingReason);
    }

    [Fact]
    public void SameHex_candidates_skip_hidden()
    {
        var s = Hex();
        PieceOutFx.Add(s, "h1", "North Hidden Scouts", x: 61, y: 42, trait: PieceOutFx.HideTrait);
        PieceOutFx.Add(s, "m1", "North Masked Battery", x: 62, y: 41, trait: PieceOutFx.ObsTrait);
        var b = PieceOutFx.Pick(sameHex: true);
        var pack = Pack(b);
        var loc = PieceOutFx.Loc(s, "b1");
        var south = PieceChoices.For(s, pack, Model, b, PieceOutFx.South, "b1");
        Assert.DoesNotContain(south, c => c.PieceId == "h1");
        Assert.Contains(south, c => c.PieceId == "m1" && c.Label == $"Unknown piece ({loc})");
        Assert.All(south, c => Assert.DoesNotContain("Hidden", c.Label));
        Assert.All(south, c => Assert.DoesNotContain("Masked", c.Label));
        Assert.Equal(new[] { "r1", "b2", "m1" }, Ids(south));
        // the same list on the guest's own view
        var view = PlayerView.For(s, pack, Model, PieceOutFx.South);
        Assert.Equal(south.Select(c => (c.Label, c.PieceId)), PieceChoices.For(view, pack, Model, b, PieceOutFx.South, "b1").Select(c => (c.Label, c.PieceId)));
        // the owner sees both under their names
        var north = PieceChoices.For(s, pack, Model, b, PieceOutFx.North, "b1");
        Assert.Contains(north, c => c.PieceId == "h1" && c.Label.StartsWith("North Hidden Scouts"));
    }

    [Fact]
    public void Old_overload_order_unchanged_without_run_piece()
    {
        var s = Hex();
        var b = PieceOutFx.Pick();
        var old = PieceChoices.For(s, Pack(b), Model, b, null);
        Assert.Equal(old, PieceChoices.For(s, Pack(b), Model, b, null, null));
        Assert.Equal(old.OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase), old);
        // the runner without a run piece resolves as before
        Assert.Null(PieceOutFx.Run(s, Pack(b), null, PieceOutFx.Typed($"North Rifles ({PieceOutFx.Loc(s, "r1")})")).Error);
    }
}
