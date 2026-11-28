using System.Diagnostics;
using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;
using Xunit.Abstractions;

namespace Quartermaster.Core.Tests;

/// <summary>
/// B10: a phase's per-unit entries and OnMove rows reach the counters of every side allied to the phase's side (Sequencer.Covers).
/// Invented Riverland pack: Northern Foot (north-land), Northern Wagons (north-supply) and Northern Balloons (north-air) share the
/// Northern Pact; the Southern Compact (south) is alone and, listed second, owns every counter named "taken" first-match.
/// </summary>
public class AllianceScopeTests
{
    internal static RulesPack Pack(bool alliance = true, bool loneAir = false) => new()
    {
        Sides = new()
        {
            new SideDef { Id = "north-land", Name = "Northern Foot", UnitFilter = "startsWith(name(), \"NF\")", AllianceId = alliance && !loneAir ? "Northern Pact" : null },
            new SideDef { Id = "south", Name = "Southern Compact", UnitFilter = "startsWith(name(), \"SC\") or contains(name(), \"taken\")" },
            new SideDef { Id = "north-supply", Name = "Northern Wagons", UnitFilter = "startsWith(name(), \"NW\")", AllianceId = alliance && !loneAir ? " northern pact " : null },
            new SideDef { Id = "north-air", Name = "Northern Balloons", UnitFilter = "startsWith(name(), \"NB\")", AllianceId = alliance || loneAir ? "Northern Pact" : null },
        },
        Phases = new()
        {
            new PhaseDef { Id = "weather", Name = "Weather" },
            new PhaseDef
            {
                Id = "foot", Name = "Foot Supply", SideId = "north-land",
                Warnings = new() { new PhaseWarning { Condition = "startsWith(name(), \"NB\")", Message = "{unit} aloft" } },
            },
            new PhaseDef { Id = "wagon", Name = "Wagon Supply", SideId = "north-supply" },
        },
    };

    internal static void Add(GameState s, string id, string name) =>
        s.Pieces[id] = new PieceInstance
        {
            Id = id, MapId = "Riverland", TraitStates = new() { "" },
            TypeString = SequenceEncoder.Encode(new[] { "piece;;;unit.svg;" + name }, '\t'),
        };

    internal static GameState State()
    {
        var s = new GameState();
        Add(s, "nf1", "NF 1");
        Add(s, "nw1", "NW 1");
        Add(s, "sc1", "SC 1");
        Add(s, "nw2", "NW 2 taken");
        Add(s, "nb1", "NB 1");
        Add(s, "crate", "Loose crate");
        Add(s, "nf2", "NF 2 taken");
        return s;
    }

    private static PhaseDef Foot(RulesPack p) => p.Phases.First(x => x.Id == "foot");
    private static PhaseDef Wagon(RulesPack p) => p.Phases.First(x => x.Id == "wagon");

    private static string[] Names(IEnumerable<PieceInstance> pieces) => pieces.Select(PieceInfo.Name).ToArray();

    private static string[] Moved(RulesPack pk, GameState s, PhaseDef ph, MoveSide mode, Func<PieceInstance, string?>? owners = null) =>
        Names(Sequencer.MovedUnitsFor(pk, s, ph, null, new MoveEntry { FormulaId = "f", FiresFor = mode }, null, owners));

    [Fact]
    public void UnitsFor_covers_allied_counters_in_pack_piece_order()
    {
        var pk = Pack();
        var s = State();
        Assert.Equal(new[] { "nf1", "nw1", "nb1", "nf2" }, Sequencer.UnitsFor(pk, s, Foot(pk), null, null).Select(p => p.Id));
        Assert.Equal(new[] { "NF 1", "NW 1", "NB 1", "NF 2 taken" }, Names(Sequencer.UnitsFor(pk, s, Foot(pk), null, null)));
    }

    [Fact]
    public void UnitsFor_ally_counter_owned_by_enemy_not_covered()
    {
        var pk = Pack();
        Assert.DoesNotContain("NW 2 taken", Names(Sequencer.UnitsFor(pk, State(), Foot(pk), null, null)));
    }

    [Fact]
    public void UnitsFor_own_filter_reach_kept_when_enemy_owns_first()
    {
        var pk = Pack();
        Assert.Equal(new[] { "NF 1", "NW 1", "NW 2 taken", "NB 1", "NF 2 taken" }, Names(Sequencer.UnitsFor(pk, State(), Wagon(pk), null, null)));
    }

    [Fact]
    public void UnitsFor_formula_applies_to_and_entry_filter_still_apply()
    {
        var pk = Pack();
        var s = State();
        Assert.Equal(new[] { "NW 1" }, Names(Sequencer.UnitsFor(pk, s, Foot(pk), null, "startsWith(name(), \"NW\")")));
        var f = new FormulaDef { Id = "f", Name = "F", Text = "1", AppliesTo = "startsWith(name(), \"NB\")" };
        Assert.Equal(new[] { "NB 1" }, Names(Sequencer.UnitsFor(pk, s, Foot(pk), f, null)));
        Assert.Empty(Sequencer.UnitsFor(pk, s, Foot(pk), f, "startsWith(name(), \"NW\")"));
        Assert.Empty(Sequencer.UnitsFor(pk, s, Foot(pk), null, "(("));
    }

    [Fact]
    public void UnitsFor_phase_without_side_every_piece()
    {
        var pk = Pack();
        var s = State();
        var all = new[] { "NF 1", "NW 1", "SC 1", "NW 2 taken", "NB 1", "Loose crate", "NF 2 taken" };
        Assert.Equal(all, Names(Sequencer.UnitsFor(pk, s, pk.Phases[0], null, null)));
        Assert.Equal(all, Names(Sequencer.UnitsFor(pk, s, Foot(pk) with { SideId = "gone" }, null, null)));
        foreach (var mode in new[] { MoveSide.Acting, MoveSide.Other, MoveSide.Any })
            Assert.Equal(all, Moved(pk, s, pk.Phases[0], mode));
    }

    [Fact]
    public void MovedUnitsFor_acting_includes_allies()
    {
        var pk = Pack();
        var s = State();
        Assert.Equal(new[] { "NF 1", "NW 1", "NB 1", "NF 2 taken" }, Moved(pk, s, Foot(pk), MoveSide.Acting));
        // the old six-parameter overload is the same selection
        Assert.Equal(new[] { "NF 1", "NW 1", "NB 1", "NF 2 taken" },
            Names(Sequencer.MovedUnitsFor(pk, s, Foot(pk), null, new MoveEntry { FormulaId = "f", FiresFor = MoveSide.Acting })));
        Assert.Equal(new[] { "NF 1", "NW 1", "NW 2 taken", "NB 1", "NF 2 taken" }, Moved(pk, s, Wagon(pk), MoveSide.Acting));
    }

    [Fact]
    public void MovedUnitsFor_other_is_outside_alliance_plus_sideless()
    {
        var pk = Pack();
        var s = State();
        Assert.Equal(new[] { "SC 1", "NW 2 taken", "Loose crate" }, Moved(pk, s, Foot(pk), MoveSide.Other));
        Assert.Equal(new[] { "SC 1", "Loose crate" }, Moved(pk, s, Wagon(pk), MoveSide.Other));
        Assert.Equal(new[] { "SC 1", "NW 2 taken", "Loose crate" },
            Names(Sequencer.MovedUnitsFor(pk, s, Foot(pk), null, new MoveEntry { FormulaId = "f", FiresFor = MoveSide.Other })));
    }

    [Fact]
    public void MovedUnitsFor_acting_and_other_partition_any()
    {
        var s = State();
        foreach (var pk in new[] { Pack(), Pack(alliance: false), Pack(loneAir: true) })
            foreach (var ph in pk.Phases)
            {
                var acting = Moved(pk, s, ph, MoveSide.Acting).ToHashSet();
                var other = Moved(pk, s, ph, MoveSide.Other).ToHashSet();
                var any = Moved(pk, s, ph, MoveSide.Any);
                Assert.Equal(new[] { "NF 1", "NW 1", "SC 1", "NW 2 taken", "NB 1", "Loose crate", "NF 2 taken" }, any);
                if (ph.SideId != null) Assert.Empty(acting.Intersect(other));
                Assert.Equal(any.ToHashSet(), acting.Union(other).ToHashSet());
            }
    }

    [Fact]
    public void MovedUnitsFor_shared_owner_map_builds_once()
    {
        var pk = Pack();
        var s = State();
        Ownership.SideMapBuilds = 0;
        var owners = Ownership.PieceSides(s, pk);
        Assert.Equal(1, Ownership.SideMapBuilds);
        Assert.Equal(new[] { "NF 1", "NW 1", "NB 1", "NF 2 taken" }, Moved(pk, s, Foot(pk), MoveSide.Acting, owners));
        Assert.Equal(new[] { "SC 1", "NW 2 taken", "Loose crate" }, Moved(pk, s, Foot(pk), MoveSide.Other, owners));
        Assert.Equal(7, Moved(pk, s, Foot(pk), MoveSide.Any, owners).Length);
        Assert.Equal(1, Ownership.SideMapBuilds);

        foreach (var mode in new[] { MoveSide.Acting, MoveSide.Other })
        {
            var before = Ownership.SideMapBuilds;
            Moved(pk, s, Foot(pk), mode);
            Assert.Equal(1, Ownership.SideMapBuilds - before);
        }
    }

    [Fact]
    public void Warnings_reach_allied_units()
    {
        var pk = Pack();
        var hits = Decisions.Warnings(pk, State(), Foot(pk));
        var hit = Assert.Single(hits);
        Assert.Equal(new[] { "nb1" }, hit.PieceIds);
        Assert.Equal(new[] { "NB 1 aloft" }, hit.UnitTexts);
    }

    [Fact]
    public void No_alliance_units_and_order_pinned()
    {
        var pk = Pack(alliance: false);
        var s = State();
        Assert.Equal(new[] { "NF 1", "NF 2 taken" }, Names(Sequencer.UnitsFor(pk, s, Foot(pk), null, null)));
        Assert.Equal(new[] { "NW 1", "NW 2 taken" }, Names(Sequencer.UnitsFor(pk, s, Wagon(pk), null, null)));
        Assert.Equal(new[] { "NF 1", "NF 2 taken" }, Moved(pk, s, Foot(pk), MoveSide.Acting));
        Assert.Equal(new[] { "NW 1", "SC 1", "NW 2 taken", "NB 1", "Loose crate" }, Moved(pk, s, Foot(pk), MoveSide.Other));
        Assert.Empty(Decisions.Warnings(pk, s, Foot(pk)));
    }

    [Fact]
    public void No_alliance_builds_no_side_map()
    {
        var s = State();
        foreach (var pk in new[] { Pack(alliance: false), Pack(loneAir: true) })
        {
            Ownership.SideMapBuilds = 0;
            foreach (var ph in pk.Phases)
            {
                Sequencer.UnitsFor(pk, s, ph, null, null);
                Moved(pk, s, ph, MoveSide.Acting);
                Moved(pk, s, ph, MoveSide.Other);
                Moved(pk, s, ph, MoveSide.Any);
                Decisions.Warnings(pk, s, ph);
            }
            Assert.Equal(0, Ownership.SideMapBuilds);
        }
        // the lone alliance changes nothing
        var lone = Pack(loneAir: true);
        Assert.Equal(new[] { "NF 1", "NF 2 taken" }, Names(Sequencer.UnitsFor(lone, s, Foot(lone), null, null)));
    }
}

[Collection(PerfCollection.Name)]
public class AllianceScopePerfTests(ITestOutputHelper output)
{
    private static long Best(RulesPack pk, GameState s, PhaseDef ph, out int count)
    {
        Sequencer.UnitsFor(pk, s, ph, null, null);   // warm up
        var best = long.MaxValue;
        count = 0;
        for (var run = 0; run < 5; run++)
        {
            var sw = Stopwatch.StartNew();
            count = Sequencer.UnitsFor(pk, s, ph, null, null).Count;
            sw.Stop();
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }
        return best;
    }

    [Fact]
    public void Perf_alliance_scope_1200Pieces()
    {
        var prefixes = new[] { "NF", "NW", "SC", "NB" };
        var s = new GameState();
        for (var i = 0; i < 1200; i++) AllianceScopeTests.Add(s, "u" + i, $"{prefixes[i % 4]} {i}");
        var pk = AllianceScopeTests.Pack();
        var plain = AllianceScopeTests.Pack(alliance: false);
        var foot = pk.Phases.First(x => x.Id == "foot");
        // Best of 5: one slow run on a loaded machine is noise, not a regression.
        var allied = Best(pk, s, foot, out var n1);
        var alone = Best(plain, s, plain.Phases.First(x => x.Id == "foot"), out var n2);
        output.WriteLine($"Foot Supply over 1200 pieces: alliance best of 5 {allied} ms ({n1} units), no alliance {alone} ms ({n2} units)");
        Assert.Equal(900, n1);
        Assert.Equal(300, n2);
        Assert.True(allied < 300, $"took {allied} ms (best of 5)");
    }
}
