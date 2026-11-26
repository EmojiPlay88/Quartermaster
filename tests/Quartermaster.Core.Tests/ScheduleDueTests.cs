using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>B5 #62: the reinforcement and withdrawal schedule (<see cref="ScheduleDue"/>, <see cref="MarkScheduled"/>). Invented Riverland data.</summary>
public class ScheduleDueTests
{
    private const string Main = "Main Map";
    private const string Box = "North holding box";

    private static readonly Lazy<ModuleModel> LazyModel = new(() =>
    {
        var extra = """
              <VASSAL.build.module.Map mapName="North holding box">
                <VASSAL.build.module.map.BoardPicker>
                  <VASSAL.build.module.map.boardPicker.Board image="box.png" name="Box board" reversible="false"/>
                </VASSAL.build.module.map.BoardPicker>
              </VASSAL.build.module.Map>
            </VASSAL.build.GameModule>
            """;
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>", extra);
        using var fx = new FixtureModule().WithBuildFile(xml).AddImage("board.png", 200, 100).AddImage("box.png", 120, 80).AddSvg("unit.svg");
        var path = fx.Build();
        using var arch = VmodArchive.Open(path);
        return ModuleModelBuilder.Build(arch, new DiagnosticLog());
    });

    private static ModuleModel Model => LazyModel.Value;
    private static readonly IReadOnlySet<string> North = new HashSet<string> { "north" };
    private static readonly IReadOnlySet<string> South = new HashSet<string> { "south" };

    /// <summary>Weather, North Supply, North Movement, South Combat, End; block Day (count 2) over North Movement–South Combat.</summary>
    private static RulesPack Pack(params ScheduleRow[] rows) => new()
    {
        Format = 4,
        Sides = new()
        {
            new SideDef { Id = "north", Name = "Northern League", UnitFilter = "startsWith(name(), \"North\")" },
            new SideDef { Id = "south", Name = "Southern Compact", UnitFilter = "startsWith(name(), \"South\")" },
        },
        Phases = new()
        {
            new PhaseDef { Id = "W", Name = "Weather" },
            new PhaseDef { Id = "NS", Name = "North Supply", SideId = "north" },
            new PhaseDef { Id = "NM", Name = "North Movement", SideId = "north" },
            new PhaseDef { Id = "SC", Name = "South Combat", SideId = "south" },
            new PhaseDef { Id = "E", Name = "End" },
        },
        Blocks = new() { new PhaseBlock { FirstPhaseId = "NM", LastPhaseId = "SC", Count = 2, Label = "Day" } },
        Schedule = rows.Length == 0 ? null : rows.ToList(),
    };

    private static ScheduleRow Row(string id, int turn, string phase, string piece = "contains(name(), \"barge\")", int? stage = null,
        ScheduleAction action = ScheduleAction.Arrive, string? zone = Box, string? label = null) =>
        new() { Id = id, Turn = turn, PhaseId = phase, Piece = piece, Stage = stage, Action = action, ZoneMapId = zone, Label = label };

    private static PhasePosition P(RulesPack pk, int turn, int repeat, string id) => new(turn, repeat, id, pk.Phases.FindIndex(p => p.Id == id));

    private static GameState At(RulesPack pk, PhasePosition pos)
    {
        var s = new GameState { GameId = "g1" };
        s.Apply(new AdvancePhase(null, pos, "", pk.Phases[pos.PhaseIndex].Name));
        return s;
    }

    private static void AddPiece(GameState s, string id, string name, string map) =>
        s.Apply(new AddPiece(id, PieceTypeParser.EncodeState(new[] { "piece;;;unit.svg;" + name }), "", map, new PointD(50, 50), null));

    /// <summary>North barges 1 (holding box) and 2 (main map), South barges 1 (holding box) and 2 (main map), a side-less Ferry barge (box).</summary>
    private static GameState Board(RulesPack pk, PhasePosition pos)
    {
        var s = At(pk, pos);
        AddPiece(s, "n1", "North barge 1", Box);
        AddPiece(s, "n2", "North barge 2", Main);
        AddPiece(s, "s1", "South barge 1", Box);
        AddPiece(s, "s2", "South barge 2", Main);
        AddPiece(s, "f1", "Ferry barge", Box);
        return s;
    }

    private static string[] Ids(IEnumerable<PieceInstance> ps) => ps.Select(p => p.Id).ToArray();

    // ---- reached and due ----

    [Fact]
    public void Reached_same_phase()
    {
        var pk = Pack();
        Assert.True(ScheduleDue.Reached(pk, Row("r", 3, "NS"), P(pk, 3, 1, "NS")));
    }

    [Fact]
    public void Reached_later_phase_same_turn()
    {
        var pk = Pack();
        Assert.True(ScheduleDue.Reached(pk, Row("r", 3, "NS"), P(pk, 3, 1, "E")));
        Assert.True(ScheduleDue.Reached(pk, Row("r", 3, "NS"), P(pk, 3, 2, "SC")));
    }

    [Fact]
    public void Reached_later_turn()
    {
        var pk = Pack();
        Assert.True(ScheduleDue.Reached(pk, Row("r", 3, "E"), P(pk, 4, 1, "W")));
    }

    [Fact]
    public void Not_reached_earlier_phase()
    {
        var pk = Pack();
        Assert.False(ScheduleDue.Reached(pk, Row("r", 3, "NS"), P(pk, 3, 1, "W")));
        Assert.False(ScheduleDue.Reached(pk, Row("r", 3, "NS"), P(pk, 2, 1, "E")));
    }

    [Fact]
    public void Stage_row_reached_only_at_or_after_stage()
    {
        var pk = Pack();
        var row = Row("r", 3, "NM", stage: 2);
        Assert.False(ScheduleDue.Reached(pk, row, P(pk, 3, 1, "NS")));
        Assert.False(ScheduleDue.Reached(pk, row, P(pk, 3, 1, "NM")));
        Assert.False(ScheduleDue.Reached(pk, row, P(pk, 3, 1, "SC")));   // a later phase of an earlier stage
        Assert.True(ScheduleDue.Reached(pk, row, P(pk, 3, 2, "NM")));
        Assert.True(ScheduleDue.Reached(pk, row, P(pk, 3, 2, "SC")));
        Assert.True(ScheduleDue.Reached(pk, row, P(pk, 3, 1, "E")));    // after the block
    }

    [Fact]
    public void Stage_row_reached_in_later_repeat_at_block_first()
    {
        var pk = Pack();
        Assert.True(ScheduleDue.Reached(pk, Row("r", 3, "SC", stage: 1), P(pk, 3, 2, "NM")));
        Assert.True(ScheduleDue.Reached(pk, Row("r", 3, "SC"), P(pk, 3, 2, "NM")));   // no stage: the first stage
        Assert.False(ScheduleDue.Reached(pk, Row("r", 3, "SC", stage: 2), P(pk, 3, 2, "NM")));
    }

    [Fact]
    public void Now_vs_overdue()
    {
        var pk = Pack();
        var row = Row("r", 3, "NS");
        Assert.True(ScheduleDue.IsNow(pk, row, P(pk, 3, 1, "NS")));
        Assert.False(ScheduleDue.IsNow(pk, row, P(pk, 3, 1, "E")));
        Assert.False(ScheduleDue.IsNow(pk, row, P(pk, 4, 1, "NS")));
        Assert.False(ScheduleDue.IsNow(pk, row, P(pk, 3, 1, "W")));   // not reached
        var staged = Row("r", 3, "NM", stage: 2);
        Assert.True(ScheduleDue.IsNow(pk, staged, P(pk, 3, 2, "NM")));
        Assert.False(ScheduleDue.IsNow(pk, staged, P(pk, 3, 1, "NM")));
        var anyStage = Row("r", 3, "NM");
        Assert.True(ScheduleDue.IsNow(pk, anyStage, P(pk, 3, 1, "NM")));
        Assert.True(ScheduleDue.IsNow(pk, anyStage, P(pk, 3, 2, "NM")));
    }

    [Fact]
    public void Unknown_phase_never_reached()
    {
        var pk = Pack();
        Assert.False(ScheduleDue.Reached(pk, Row("r", 1, "gone"), P(pk, 9, 2, "SC")));
        Assert.False(ScheduleDue.IsNow(pk, Row("r", 1, "gone"), P(pk, 1, 1, "W")));
    }

    // ---- counters ----

    [Fact]
    public void Arrive_counters_on_zone_only()
    {
        var pk = Pack();
        var s = Board(pk, P(pk, 3, 1, "NS"));
        Assert.Equal(new[] { "n1" }, Ids(ScheduleDue.Counters(pk, s, Model, Row("r", 3, "NS", "startsWith(name(), \"North barge\")"))));
        Assert.Equal(new[] { "n1" }, Ids(ScheduleDue.Counters(pk, s, Model,
            Row("r", 3, "NS", "startsWith(name(), \"North barge\")", action: ScheduleAction.Return))));
        Assert.Empty(ScheduleDue.Counters(pk, s, Model, Row("r", 3, "NS", zone: null)));   // arriving needs a zone map (validator)
        Assert.Empty(ScheduleDue.Counters(pk, s, Model, Row("r", 3, "NS", piece: "")));    // no filter matches nothing
    }

    [Fact]
    public void Leave_counters_off_zone()
    {
        var pk = Pack();
        var s = Board(pk, P(pk, 3, 1, "NS"));
        Assert.Equal(new[] { "n2" }, Ids(ScheduleDue.Counters(pk, s, Model,
            Row("r", 3, "NS", "startsWith(name(), \"North barge\")", action: ScheduleAction.Leave))));
    }

    [Fact]
    public void Leave_without_zone_all_matching()
    {
        var pk = Pack();
        var s = Board(pk, P(pk, 3, 1, "NS"));
        Assert.Equal(new[] { "n1", "n2" }, Ids(ScheduleDue.Counters(pk, s, Model,
            Row("r", 3, "NS", "startsWith(name(), \"North barge\")", action: ScheduleAction.Leave, zone: null))));
    }

    [Fact]
    public void Zone_map_by_name()
    {
        var box = Model.Maps.Single(m => m.Name == Box);
        Assert.Same(box, ScheduleDue.ZoneMap(Model, Box));
        Assert.Same(box, ScheduleDue.ZoneMap(Model, "  " + Box + " "));
        Assert.Null(ScheduleDue.ZoneMap(Model, "Nowhere"));
        Assert.Null(ScheduleDue.ZoneMap(Model, null));
        Assert.Null(ScheduleDue.ZoneMap(null, Box));
        // By id first, then by name.
        var renamed = box with { Id = "map-7" };
        var other = box with { Id = Box, Name = "Other box" };
        Assert.Same(renamed, ScheduleDue.ZoneMapIn(new[] { renamed }, Box));
        Assert.Same(other, ScheduleDue.ZoneMapIn(new[] { renamed, other }, Box));
        Assert.Same(renamed, ScheduleDue.ZoneMapIn(new[] { renamed, other }, "map-7"));
    }

    // ---- due list ----

    [Fact]
    public void For_lists_reached_not_done_in_order()
    {
        var pk = Pack(
            Row("r1", 3, "NM", stage: 2),
            Row("r2", 2, "E", label: "Hired pilot leaves", action: ScheduleAction.Leave, zone: null),
            Row("r3", 3, "NS"),
            Row("r4", 5, "W"),
            Row("r5", 1, "W"));
        var s = Board(pk, P(pk, 3, 2, "NM"));
        s.Apply(new MarkScheduled("r5", "Schedule: Arrivals, turn 1"));
        var due = ScheduleDue.For(pk, s, Model, null);
        Assert.Equal(new[] { "r2", "r3", "r1" }, due.Select(d => d.Row.Id));
        Assert.Equal(new[] { "Hired pilot leaves", "Arrivals, turn 3", "Arrivals, turn 3" }, due.Select(d => d.Label));
        Assert.Equal(new[] { false, false, true }, due.Select(d => d.Now));
        Assert.Equal(new[] { "n1", "n2", "s1", "s2", "f1" }, due[0].PieceIds);   // Leave, no zone map: every matching counter (solo)
        Assert.Equal(new[] { "n1", "s1", "f1" }, due[2].PieceIds);
    }

    [Fact]
    public void For_only_actable_counters()
    {
        var pk = Pack(Row("r1", 3, "NS"));
        var s = Board(pk, P(pk, 3, 1, "NS"));
        Assert.Equal(new[] { "n1", "f1" }, Assert.Single(ScheduleDue.For(pk, s, Model, North)).PieceIds);
        Assert.Equal(new[] { "s1", "f1" }, Assert.Single(ScheduleDue.For(pk, s, Model, South)).PieceIds);
        // From a player's own view the same: another side's counters are left out.
        var view = PlayerView.For(s, pk, Model, North);
        Assert.Equal(new[] { "n1", "f1" }, Assert.Single(ScheduleDue.For(pk, view, Model, North)).PieceIds);
    }

    [Fact]
    public void For_hides_row_without_actable_counters_for_player()
    {
        var pk = Pack(Row("r1", 3, "NS", "startsWith(name(), \"South\")"));
        var s = Board(pk, P(pk, 3, 1, "NS"));
        Assert.Empty(ScheduleDue.For(pk, s, Model, North));
        Assert.Empty(ScheduleDue.For(pk, PlayerView.For(s, pk, Model, North), Model, North));
        Assert.Equal(new[] { "s1" }, Assert.Single(ScheduleDue.For(pk, s, Model, South)).PieceIds);
    }

    [Fact]
    public void Solo_lists_row_without_counters()
    {
        var pk = Pack(Row("r1", 3, "NS", "name() == \"Relief barge 9\""));
        var s = Board(pk, P(pk, 3, 1, "NS"));
        var due = Assert.Single(ScheduleDue.For(pk, s, Model, null));
        Assert.Empty(due.PieceIds);
        Assert.True(due.Now);
        Assert.Empty(ScheduleDue.For(pk, s, Model, North));
    }

    [Fact]
    public void For_empty_when_game_over()
    {
        var pk = Pack(Row("r1", 3, "NS"));
        var s = Board(pk, P(pk, 3, 1, "NS"));
        Assert.Single(ScheduleDue.For(pk, s, Model, null));
        s.Apply(new EndGame("the game has ended", null, null));
        Assert.Empty(ScheduleDue.For(pk, s, Model, null));
        Assert.Empty(ScheduleDue.For(pk, new GameState(), Model, null));   // before turn 1
    }

    [Fact]
    public void MarkScheduled_adds_once_and_logs()
    {
        var pk = Pack(Row("r1", 3, "NS", label: "Relief barges arrive"));
        var s = Board(pk, P(pk, 3, 1, "NS"));
        var before = s.Log.Count;
        s.Apply(new MarkScheduled("r1", "Schedule: Relief barges arrive", Who: "Ann"));
        s.Apply(new MarkScheduled("r1", "Schedule: Relief barges arrive", Who: "Ann"));
        s.Apply(new MarkScheduled(null!, "Schedule: nothing"));
        Assert.Equal(new[] { "r1" }, s.ScheduleDone);
        var line = Assert.Single(s.Log.Skip(before));
        Assert.Equal(LogKind.Turn, line.Kind);
        Assert.Equal("Schedule: Relief barges arrive", line.Text);
        Assert.Equal("Ann", line.Who);
        Assert.Empty(ScheduleDue.For(pk, s, Model, null));

        GameCommand c = new MarkScheduled("r1", "Schedule: Relief barges arrive");
        var json = JsonSerializer.Serialize(c, QuartermasterJson.Options);
        Assert.Contains("\"$t\":\"markScheduled\"", json);
        Assert.Equal(c, JsonSerializer.Deserialize<GameCommand>(json, QuartermasterJson.Options));
    }

    [Fact]
    public void ScheduleDone_omitted_when_null()
    {
        var pk = Pack(Row("r1", 3, "NS"));
        var s = At(pk, P(pk, 3, 1, "NS"));
        Assert.DoesNotContain("scheduleDone", JsonSerializer.Serialize(s, QuartermasterJson.Options));
        s.Apply(new MarkScheduled("r1", "Schedule: Arrivals, turn 3"));
        Assert.Contains("\"scheduleDone\":[\"r1\"]", JsonSerializer.Serialize(s, QuartermasterJson.Options));
        Assert.Equal(new[] { "r1" }, s.DeepClone().ScheduleDone);
    }

    [Fact]
    public void Decision_for_due_now_only()
    {
        var pk = Pack(
            Row("r1", 2, "NS", label: "Late barges"),             // overdue: only in the list
            Row("r2", 3, "NS", label: "Relief barges arrive"),    // due now with counters
            Row("r3", 3, "NS", "name() == \"Relief barge 9\""));  // due now, no counter: no decision
        var s = Board(pk, P(pk, 3, 1, "NS"));
        var d = Assert.Single(Decisions.Compute(pk, s, Model), x => x.Kind == DecisionKind.Schedule);
        Assert.Equal("Due: Relief barges arrive", d.Text);
        Assert.Equal(new[] { "n1", "s1", "f1" }, d.PieceIds);
        // From a player's view: that player's counters only.
        var view = PlayerView.For(s, pk, Model, North);
        Assert.Equal(new[] { "n1", "f1" }, Assert.Single(Decisions.Compute(pk, view, Model), x => x.Kind == DecisionKind.Schedule).PieceIds);
        s.Apply(new MarkScheduled("r2", "Schedule: Relief barges arrive"));
        Assert.DoesNotContain(Decisions.Compute(pk, s, Model), x => x.Kind == DecisionKind.Schedule);
    }
}
