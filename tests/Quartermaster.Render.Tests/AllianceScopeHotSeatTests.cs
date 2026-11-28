using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B10 alliance scope: invented Riverland module and pack. Northern Foot (<c>north-land</c>, "NF"), Northern Wagons (<c>north-supply</c>,
/// "NW") and Northern Balloons (<c>north-air</c>, "NB") share the Northern Pact; the Southern Compact (<c>south</c>, "SC" or "taken") is
/// alone and, second in pack order, owns "NW 2 taken" first-match. Phases: Weather (no side), Foot Supply (north-land: daily water for each
/// unit, fuel on move), Wagon Supply (north-supply), End (no side). Water is Private. Temp roots only.
/// </summary>
public abstract class AllianceScopeTestBase : IDisposable
{
    protected readonly string Tmp = Path.Combine(Path.GetTempPath(), "qm-b10scope-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    protected static readonly PlayerDef Alice = new("p1", "Alice", new[] { "north-land" });
    protected static readonly PlayerDef Bob = new("p2", "Bob", new[] { "south" });
    protected static readonly PlayerDef Cara = new("p3", "Cara", new[] { "north-supply" });
    protected static readonly PlayerDef Dina = new("p4", "Dina", new[] { "north-air" });
    protected static IReadOnlySet<string> Land => new HashSet<string> { "north-land" };
    protected static IReadOnlySet<string> Wagons => new HashSet<string> { "north-supply" };
    protected static IReadOnlySet<string> South => new HashSet<string> { "south" };

    /// <summary>Piece ids by name, in insertion order.</summary>
    protected static readonly (string Id, string Name)[] Units =
    {
        ("nf1", "NF 1"), ("nw1", "NW 1"), ("sc1", "SC 1"), ("nw2", "NW 2 taken"), ("nb1", "NB 1"), ("crate", "Loose crate"), ("nf2", "NF 2 taken"),
    };

    protected static readonly string[] NorthIds = { "nf1", "nw1", "nb1", "nf2" };

    protected AllianceScopeTestBase() => Directory.CreateDirectory(Tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(Tmp, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static string AddPiece(string id, string name, int x) =>
        "+/" + SequenceEncoder.Encode(new[] { id, $"propertysheet;0Fuel;Sheet;\tpiece;;;unit.svg;{name}", $"40\tMain Map;{x};50;1;0" }, '/');

    private static FixtureModule Module()
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            "<VASSAL.build.module.PredefinedSetup name=\"Start\" file=\"s.vsav\" useFile=\"true\" isMenu=\"false\"/></VASSAL.build.GameModule>");
        var lines = new List<string> { "begin_save", "" };
        lines.AddRange(Units.Select(u => AddPiece(u.Id, u.Name, 30)));
        lines.Add("end_save");
        return new FixtureModule().WithBuildFile(xml).AddImage("board.png", 200, 100).AddSvg("unit.svg").AddSave("s.vsav", lines);
    }

    protected static RulesPack Pack(string sha, bool alliance = true, MoveSide fires = MoveSide.Acting)
    {
        var p = PhaseFixture.Pack(sha);
        return p with
        {
            Format = 4,
            Sides = new()
            {
                new SideDef { Id = "north-land", Name = "Northern Foot", UnitFilter = "startsWith(name(), \"NF\")", AllianceId = alliance ? "Northern Pact" : null },
                new SideDef { Id = "south", Name = "Southern Compact", UnitFilter = "startsWith(name(), \"SC\") or contains(name(), \"taken\")" },
                new SideDef { Id = "north-supply", Name = "Northern Wagons", UnitFilter = "startsWith(name(), \"NW\")", AllianceId = alliance ? " northern pact " : null },
                new SideDef { Id = "north-air", Name = "Northern Balloons", UnitFilter = "startsWith(name(), \"NB\")", AllianceId = alliance ? "Northern Pact" : null },
            },
            Properties = p.Properties.Select(x => x.Id == "water" ? x with { Visibility = Visibility.Private } : x).ToList(),
            Phases = new()
            {
                new PhaseDef { Id = "ph-weather", Name = "Weather" },
                new PhaseDef
                {
                    Id = "ph-foot", Name = "Foot Supply", SideId = "north-land",
                    OnStart = new() { new StartEntry { FormulaId = "f-water", ForEach = ForEachMode.Units } },
                    OnMove = new() { new MoveEntry { FormulaId = "f-fuel", FiresFor = fires } },
                },
                new PhaseDef { Id = "ph-wagon", Name = "Wagon Supply", SideId = "north-supply" },
                new PhaseDef { Id = "ph-end", Name = "End" },
            },
            Blocks = new(),
        };
    }

    /// <summary>A hot-seat session (four players) with the pack applied, dice always 4.</summary>
    protected ModuleSession Session(bool alliance = true, MoveSide fires = MoveSide.Acting, PlayerDef[]? players = null, Difficulty d = Difficulty.FullyAuto)
    {
        var fx = Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(Tmp, "cache" + _sessions.Count), Path.Combine(Tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(4, n).ToList());
        s.LoadSetup(s.Model.Setups[0], d, players ?? new[] { Alice, Bob, Cara, Dina });
        s.NewRules("t");
        s.UpdateRules(_ => Pack(s.Archive.Sha256, alliance, fires));
        return s;
    }

    protected static PhaseController Controller(ModuleSession s) => new(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));

    protected static double? Water(GameState st, string id) => Ledger.Get(st, LedgerKey.Piece(id), "water");
    protected static double? Fuel(GameState st, string id) => Ledger.Get(st, LedgerKey.Piece(id), "fuel");
    protected static MovePieces Move(string id, int x) => new(new[] { id }, "Main Map", new PointD(x, 50), null);
    protected static string Json(GameState st) => JsonSerializer.Serialize(st, QuartermasterJson.Options);

    /// <summary>Start (Weather), then Next Phase into Foot Supply.</summary>
    protected static void ToFoot(ModuleSession s, PhaseController c)
    {
        c.Start();
        c.Advance();
        Assert.Equal("ph-foot", s.Game!.State.Position?.PhaseId);
    }

    /// <summary>The fuel-for-movement ledger deltas the formula wrote after <paramref name="mark"/>.</summary>
    protected static List<LedgerDelta> FuelDeltas(ModuleSession s, int mark) =>
        s.Game!.Log.Skip(mark).OfType<LedgerDelta>().Where(d => d.Source == "formula" && d.PropertyId == "fuel").ToList();

    /// <summary>What a viewer outside the alliance must not have: a north counter's water, or a log line naming or pointing at one.</summary>
    protected static void AssertNothingOfNorth(GameState view)
    {
        foreach (var id in NorthIds) Assert.Null(Water(view, id));
        var names = Units.Where(u => NorthIds.Contains(u.Id)).Select(u => u.Name).ToArray();
        Assert.DoesNotContain(view.Log, l => l.PieceId != null && NorthIds.Contains(l.PieceId));
        Assert.DoesNotContain(view.Log, l => names.Any(n => (l.Text ?? "").Contains(n, StringComparison.Ordinal)));
    }
}

/// <summary>B10 hot-seat: Foot Supply's start entry and On move rows reach the Wagons' and Balloons' counters.</summary>
public class AllianceScopeHotSeatTests : AllianceScopeTestBase
{
    [Fact]
    public void Start_entry_for_each_unit_writes_allied_counters()
    {
        var s = Session();
        var c = Controller(s);
        c.Start();
        var before = Json(s.Game!.State);

        c.Advance();   // Foot Supply: daily water for each unit of the Northern Pact

        var st = s.Game.State;
        Assert.Equal("ph-foot", st.Position?.PhaseId);
        foreach (var id in NorthIds) Assert.Equal(9, Water(st, id));
        Assert.Null(Water(st, "sc1"));
        Assert.Null(Water(st, "nw2"));     // the Wagons' name, but the Compact's counter
        Assert.Null(Water(st, "crate"));

        s.Game.Undo();   // one undo takes it all back
        Assert.Equal(before, Json(s.Game.State));
    }

    [Fact]
    public void OnMove_acting_fires_for_ally_counter()
    {
        var s = Session();
        var c = Controller(s);
        ToFoot(s, c);
        s.SetViewSides(Land);
        var mark = s.Game!.Log.Count;

        c.ExecuteMove(new[] { Move("nb1", 130) });

        Assert.Equal(36, Fuel(s.Game.State, "nb1"));   // 2 cells x Clear (2)
        Assert.Single(FuelDeltas(s, mark));
    }

    [Fact]
    public void OnMove_other_skips_ally_fires_for_enemy()
    {
        var s = Session(fires: MoveSide.Other);
        var c = Controller(s);
        ToFoot(s, c);
        s.SetViewSides(Land);
        var mark = s.Game!.Log.Count;

        c.ExecuteMove(new[] { Move("nw1", 130) });
        Assert.Empty(FuelDeltas(s, mark));
        Assert.Equal(40, Fuel(s.Game.State, "nw1"));

        mark = s.Game.Log.Count;
        c.ExecuteMove(new[] { Move("sc1", 130) });
        var d = Assert.Single(FuelDeltas(s, mark));
        Assert.Equal("p:sc1", d.Key);
        Assert.Equal("south", d.SideId);
        Assert.Equal(36, Fuel(s.Game.State, "sc1"));
    }

    [Fact]
    public void South_view_learns_nothing_new()
    {
        var s = Session();
        var c = Controller(s);
        ToFoot(s, c);
        Assert.Equal(9, Water(s.Game!.State, "nb1"));   // the allied run happened

        AssertNothingOfNorth(PlayerView.For(s.Game.State, s.Rules, s.Model, South));
        s.SetViewSides(South);
        AssertNothingOfNorth(s.View!);
        s.SetViewSides(Wagons);   // an ally sees it
        Assert.Equal(9, Water(s.View!, "nb1"));
    }

    [Fact]
    public void No_alliance_pack_same_log_as_before()
    {
        var s = Session(alliance: false);
        var c = Controller(s);
        ToFoot(s, c);
        s.SetViewSides(Land);
        c.ExecuteMove(new[] { Move("nw1", 130) });   // not the Foot's: no run
        c.ExecuteMove(new[] { Move("nf1", 130) });

        Assert.Equal(9, Water(s.Game!.State, "nf1"));
        Assert.Equal(9, Water(s.Game.State, "nf2"));
        Assert.Null(Water(s.Game.State, "nw1"));
        Assert.Null(Water(s.Game.State, "nb1"));
        Assert.Equal(40, Fuel(s.Game.State, "nw1"));
        Assert.Equal(36, Fuel(s.Game.State, "nf1"));
        var texts = s.Game.State.Log.Select(l => l.Text).ToArray();
        Assert.Equal(new string[]
        {
            "Setup \"Start\" loaded: 7 pieces",
            "Turn 1 · Weather",
            "Turn 1 · Weather → Foot Supply",
            "NF 1: 10 − 1 = 9 → Water = 9 (auto)",
            "NF 2 taken: 10 − 1 = 9 → Water = 9 (auto)",
            "Daily water: 2 units, 2 applied",
            "NW 1 moved",
            "NF 1 moved",
            "NF 1: 2 × TestTerrainCost[Clear] = 4 → Fuel 40 − 4 = 36 (auto)",
        }, texts);
    }
}
