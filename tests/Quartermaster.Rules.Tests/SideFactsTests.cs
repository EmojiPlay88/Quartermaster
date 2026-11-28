namespace Quartermaster.Rules.Tests;

public class SideFactsTests
{
    private static SideDef S(string id, string? alliance = null, params string[] former) =>
        new() { Id = id, Name = id, AllianceId = alliance, FormerIds = former.Length == 0 ? null : former.ToList() };

    private static RulesPack P(params SideDef[] sides) => new() { Sides = sides.ToList() };

    [Fact]
    public void FormerMap_null_without_former_ids() => Assert.Null(SideFacts.FormerMap(P(S("north"), S("south"))));

    [Fact]
    public void FormerMap_ignores_current_id_and_first_listing_wins()
    {
        var map = SideFacts.FormerMap(P(S("north", null, "league", "south"), S("south", null, "league", "union")))!;
        Assert.Equal("north", map["league"]);
        Assert.Equal("south", map["union"]);
        Assert.False(map.ContainsKey("south"));
        Assert.Null(SideFacts.FormerMap(P(S("north", null, "south"), S("south"))));
    }

    [Fact]
    public void Canonical_translates_and_passes_through()
    {
        var p = P(S("east", null, "league"), S("south"));
        Assert.Equal("east", SideFacts.Canonical(p, "league"));
        Assert.Equal("south", SideFacts.Canonical(p, "south"));
        Assert.Equal("zzz", SideFacts.Canonical(p, "zzz"));
        Assert.Null(SideFacts.Canonical(p, null));
        Assert.Equal("x", SideFacts.Canonical(null, "x"));
    }

    [Fact]
    public void Held_null_stays_null() => Assert.Null(SideFacts.Held(P(S("north", "A"), S("south", "A")), null));

    [Fact]
    public void Held_same_instance_without_features()
    {
        var set = new HashSet<string> { "north" };
        Assert.Same(set, SideFacts.Held(P(S("north"), S("south")), set));
        Assert.Same(set, SideFacts.Held(null, set));
    }

    [Fact]
    public void Held_adds_allies_case_insensitive_trimmed()
    {
        var p = P(S("north", " upriver pact"), S("hills", "Upriver Pact"), S("south"));
        var held = SideFacts.Held(p, new[] { "north" })!;
        Assert.Equal(new[] { "hills", "north" }, held.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Held_translates_former_ids_then_allies()
    {
        var p = P(S("east", "Upriver Pact", "league"), S("hills", "Upriver Pact"), S("south"));
        var held = SideFacts.Held(p, new[] { "league" })!;
        Assert.Equal(new[] { "east", "hills" }, held.OrderBy(x => x, StringComparer.Ordinal));
        var q = P(S("east", null, "league"), S("south"));
        var h2 = SideFacts.Held(q, new HashSet<string> { "league" })!;
        Assert.Equal(new[] { "east" }, h2);
    }

    [Fact]
    public void Held_empty_stays_empty() => Assert.Empty(SideFacts.Held(P(S("north", "A"), S("south", "A")), new string[0])!);

    [Fact]
    public void Held_non_ally_not_added()
    {
        var p = P(S("north", "A"), S("hills", "A"), S("south", "B"), S("lone"));
        Assert.Equal(new[] { "south" }, SideFacts.Held(p, new[] { "south" })!);
        Assert.Equal(new[] { "lone" }, SideFacts.Held(p, new[] { "lone" })!);
    }

    [Fact]
    public void Groups_counts_unallied_sides_singly()
    {
        Assert.Equal(3, SideFacts.Groups(P(S("a"), S("b"), S("c"))));
        Assert.Equal(1, SideFacts.Groups(P(S("a", "X"), S("b", " x "))));
        Assert.Equal(3, SideFacts.Groups(P(S("a", "X"), S("b", "x"), S("c"), S("d", "Y"))));
    }

    [Fact]
    public void Allies_lists_alliance_members_case_insensitive()
    {
        var p = P(S("north-land", "Northern Pact"), S("south"), S("north-supply", " northern pact "), S("north-air", "Northern Pact"));
        var allies = SideFacts.Allies(p, "north-land")!;
        Assert.Equal(new[] { "north-air", "north-land", "north-supply" }, allies.OrderBy(x => x, StringComparer.Ordinal));
        Assert.True(allies.Contains("north-supply"));
        Assert.False(allies.Contains("North-Supply"));   // ordinal ids
        Assert.Equal(new[] { "north-air", "north-land", "north-supply" }, SideFacts.Allies(p, "north-supply")!.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Allies_null_without_alliance_unknown_side_or_lone_alliance()
    {
        var p = P(S("north-land", "Northern Pact"), S("south"), S("north-supply", " northern pact "), S("east", "Lone League"), S("west", "  "));
        Assert.Null(SideFacts.Allies(p, "south"));      // no alliance
        Assert.Null(SideFacts.Allies(p, "west"));       // blank alliance
        Assert.Null(SideFacts.Allies(p, "gone"));       // unknown side
        Assert.Null(SideFacts.Allies(p, null));
        Assert.Null(SideFacts.Allies(null, "north-land"));
        Assert.Null(SideFacts.Allies(p, "east"));       // a lone alliance
        Assert.Null(SideFacts.Allies(P(S("north-air", "Northern Pact"), S("south")), "north-air"));
    }
}
