using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;
using static Quartermaster.Core.Tests.RunnerFx;

namespace Quartermaster.Core.Tests;

/// <summary>B4 group rolls: shared dice in a run, roll groups on pending runs, their saves and views. Invented Riverland data.</summary>
public class GroupRollTests
{
    private static readonly IReadOnlySet<string> North = new HashSet<string> { "north" };

    private static RulesPack Pack(string text = "D + 1", VariableBinding? roll = null, bool groupRoll = true) => new()
    {
        Format = 4,
        Sides = new()
        {
            new SideDef { Id = "north", Name = "Northern League", UnitFilter = "startsWith(name(), \"North\")" },
            new SideDef { Id = "south", Name = "Southern Compact", UnitFilter = "startsWith(name(), \"South\")" },
        },
        Properties = new()
        {
            new PropertyDef { Id = "loss", Name = "Loss", Scope = PropertyScope.Pieces, Default = 0, Visibility = Visibility.Public },
            new PropertyDef { Id = "size", Name = "Size", Scope = PropertyScope.Pieces, Default = 1, Visibility = Visibility.Public },
        },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "f-ford", Name = "Ford crossing", Text = text, GroupRoll = groupRoll,
                Bindings = new() { roll ?? new VariableBinding { Variable = "D", Kind = BindingKind.Roll, DiceCount = 2, DiceSides = 6 } },
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "loss", Op = OutputOp.Set } },
            },
        },
    };

    private static void Add(GameState s, string id, string name, double x = 60, double y = 40, string map = "Main Map")
    {
        s.Pieces[id] = new PieceInstance
        {
            Id = id, MapId = map, Pos = new PointD(x, y), TypeString = PieceTypeParser.EncodeState(new[] { "piece;;;unit.svg;" + name }),
            TraitStates = new() { "" },
        };
    }

    private static GameState Barges()
    {
        var s = new GameState();
        Add(s, "n1", "North Barge A");
        Add(s, "n2", "North Barge B", 62, 42);   // same square as n1
        Add(s, "s1", "South Raft", 61, 41);       // same square, other side
        Add(s, "n3", "North Barge C", 160);       // another square
        Add(s, "x1", "Ferry", 63, 43);            // same square, no side
        Add(s, "z1", "North Store", map: "Nowhere");   // off grid
        return s;
    }

    private static FormulaRunner Runner(RulesPack pack, int face = 1) => new(pack, Model, (n, _) => Enumerable.Repeat(face, n).ToList());

    private static RunRequest Req(IReadOnlyDictionary<string, IReadOnlyList<int>>? shared, string piece = "n1") =>
        new("f-ford", piece, new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>())
        { SharedRolls = shared };

    private static Dictionary<string, IReadOnlyList<int>> Dice(string key, params int[] d) => new() { [key] = d };

    // ---- Shared rolls in the runner ----

    [Fact]
    public void SharedRolls_used_and_not_logged()
    {
        var pack = Pack("D + count(D, 4, 6)");
        var runner = Runner(pack, face: 1);
        var t = runner.Run(Barges(), Req(Dice("D", 3, 5)));
        Assert.Null(t.Error);
        Assert.Equal(3 + 5 + 1, t.Result!.Value.Number);   // the shared dice, and count() reads them
        Assert.Empty(t.Rolls);
        Assert.Equal(new[] { 3, 5 }, t.RollsByKey["D"]);
        Assert.Contains("8", t.Working);
        var cmds = runner.Commands(t, null);
        Assert.DoesNotContain(cmds, c => c is RollDice);
        Assert.Contains(cmds, c => c is LedgerDelta { After: 9 });
        // without shared dice the run rolls and logs its own
        var own = runner.Run(Barges(), Req(null));
        Assert.Equal(1 + 1 + 0, own.Result!.Value.Number);
        Assert.Single(own.Rolls);
        Assert.Contains(runner.Commands(own, null), c => c is RollDice);
    }

    [Fact]
    public void SharedRolls_mismatched_count_rolls_own()
    {
        var pack = Pack("D");
        var runner = Runner(pack, face: 2);
        var t = runner.Run(Barges(), Req(Dice("D", 6, 6, 6)));   // 3 dice for a 2-dice roll: not shared
        Assert.Equal(4, t.Result!.Value.Number);
        Assert.Single(t.Rolls);
        Assert.Empty(t.SharedRollKeys);
        // a die outside the sides is not shared either
        var bad = runner.Run(Barges(), Req(Dice("D", 7, 1)));
        Assert.Equal(4, bad.Result!.Value.Number);
        // a dice count expression giving another count rolls its own
        var byExpr = Pack("D", new VariableBinding { Variable = "D", Kind = BindingKind.Roll, DiceCountExpr = "3", DiceSides = 6 });
        var t3 = Runner(byExpr, face: 2).Run(Barges(), Req(Dice("D", 6, 6)));
        Assert.Equal(6, t3.Result!.Value.Number);
        Assert.Single(t3.Rolls);
    }

    [Fact]
    public void SharedRolls_inline_dice_key()
    {
        var pack = Pack("dice(2, 6) + dice(1, 6)", new VariableBinding { Variable = "Unused", Kind = BindingKind.Input });
        var runner = Runner(pack, face: 1);
        var t = runner.Run(Barges(), Req(Dice("dice#0", 4, 4)));
        Assert.Null(t.Error);
        Assert.Equal(4 + 4 + 1, t.Result!.Value.Number);
        Assert.Equal("dice#1", Assert.Single(t.Rolls).Purpose.Split(": ")[1]);   // only the unshared key is logged
        Assert.Equal(new[] { "dice#0" }, t.SharedRollKeys);
        Assert.Single(runner.Commands(t, null).OfType<RollDice>());
    }

    [Fact]
    public void SharedRollKeys_listed()
    {
        var pack = Pack("D + E", null) with { };
        pack = pack with
        {
            Formulas = new()
            {
                pack.Formulas[0] with
                {
                    Bindings = new()
                    {
                        new VariableBinding { Variable = "D", Kind = BindingKind.Roll, DiceCount = 2, DiceSides = 6 },
                        new VariableBinding { Variable = "E", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 },
                    },
                },
            },
        };
        var t = Runner(pack).Run(Barges(), Req(new Dictionary<string, IReadOnlyList<int>> { ["D"] = new[] { 2, 3 }, ["E"] = new[] { 5 } }));
        Assert.Equal(new[] { "D", "E" }, t.SharedRollKeys);
        Assert.Empty(t.Rolls);
        // a plain run lists none
        Assert.Empty(Runner(pack).Run(Barges(), Req(null)).SharedRollKeys);
        // a typed roll (override) is the player's dice, not the group's
        var typed = Runner(pack).Run(Barges(), Req(new Dictionary<string, IReadOnlyList<int>> { ["D"] = new[] { 2, 3 } }) with
            { Overrides = new Dictionary<string, string> { ["D"] = "6 6" } });
        Assert.Equal(12 + 1, typed.Result!.Value.Number);
        Assert.Empty(typed.SharedRollKeys);
    }

    // ---- Roll groups ----

    [Fact]
    public void Assign_same_hex_same_side_one_group()
    {
        var s = Barges();
        var g = RollGroups.Assign(s, Pack(), Model, new string?[] { "n1", "n3", "n2" });
        Assert.Equal(new[] { "north/1", "north/2", "north/1" }, g);
    }

    [Fact]
    public void Assign_other_side_same_hex_other_group()
    {
        var s = Barges();
        var g = RollGroups.Assign(s, Pack(), Model, new string?[] { "s1", "n1", "x1", "n2" });
        Assert.Equal(new[] { "south/1", "north/1", "/1", "north/1" }, g);
    }

    [Fact]
    public void Assign_off_grid_null()
    {
        var s = Barges();
        var g = RollGroups.Assign(s, Pack(), Model, new string?[] { "z1", null, "gone", "n1" });
        Assert.Equal(new string?[] { null, null, null, "north/1" }, g);
    }

    private static PendingRun GroupRun() => new("r1", "f-ford", "Ford crossing", "start", new[]
    {
        new PendingUnit("n1", "North Barge A", null, false) { Group = "north/1" },
        new PendingUnit("n2", "North Barge B", null, false) { Group = "north/1" },
        new PendingUnit("n3", "North Barge C", null, false) { Group = "north/2" },
        new PendingUnit("z1", "North Store", null, false),
    }, false);

    [Fact]
    public void SharedFor_reads_run_group()
    {
        var s = Barges();
        s.Apply(new QueueRuns(new[] { GroupRun() }));
        Assert.Null(RollGroups.SharedFor(s, "r1", "n2"));   // nothing drawn yet
        s.Apply(new ResolveRun("r1", new[] { "n1" }) { GroupRolls = G("north/1", "D", 3, 4) });
        Assert.Equal(new[] { 3, 4 }, RollGroups.SharedFor(s, "r1", "n2")!["D"]);
        Assert.Null(RollGroups.SharedFor(s, "r1", "n3"));   // another group
        Assert.Null(RollGroups.SharedFor(s, "r1", "z1"));   // no group
        Assert.Null(RollGroups.SharedFor(s, "nope", "n2"));
        Assert.Null(RollGroups.SharedFor(s, "r1", "other"));
    }

    private static Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<int>>> G(string group, string key, params int[] dice) =>
        new() { [group] = new Dictionary<string, IReadOnlyList<int>> { [key] = dice } };

    // ---- Saves ----

    /// <summary>Written by master's serializer (pre-B4): a queued run and an answer.</summary>
    private const string QueueLiteral =
        """{"$t":"queueRuns","runs":[{"id":"run-1","formulaId":"f-ford","formulaName":"Ford crossing","trigger":"start","units":[{"pieceId":"u1","label":"Barge A","moved":null,"applied":false},{"pieceId":"u2","label":"Barge B","moved":2.5,"applied":true}],"needsInput":true,"askSideId":"north"}],"at":"2026-05-06T07:08:09+00:00","who":"host"}""";
    private const string ResolveLiteral =
        """{"$t":"resolveRun","runId":"run-1","pieceIds":["u1",null],"at":"2026-05-06T07:08:09+00:00","who":"host"}""";

    [Fact]
    public void Pending_save_without_groups_keeps_bytes()
    {
        foreach (var lit in new[] { QueueLiteral, ResolveLiteral })
        {
            var cmd = JsonSerializer.Deserialize<GameCommand>(lit, QuartermasterJson.Options)!;
            Assert.Equal(lit, JsonSerializer.Serialize(cmd, QuartermasterJson.Options));
        }
        var q = (QueueRuns)JsonSerializer.Deserialize<GameCommand>(QueueLiteral, QuartermasterJson.Options)!;
        Assert.All(q.Runs[0].Units, u => { Assert.Null(u.Group); Assert.False(u.Removed); });
        Assert.Null(q.Runs[0].GroupRolls);
        var r = (ResolveRun)JsonSerializer.Deserialize<GameCommand>(ResolveLiteral, QuartermasterJson.Options)!;
        Assert.Null(r.GroupRolls);
        Assert.False(r.Gone);
        // a state holding the run keeps the old shape too
        var s = new GameState();
        s.Apply(q);
        var json = JsonSerializer.Serialize(s, QuartermasterJson.Options);
        Assert.DoesNotContain("\"group", json);
        Assert.DoesNotContain("\"removed\"", json);
        // new members are written when set
        var g = new ResolveRun("r", new[] { "u1" }) { GroupRolls = G("north/1", "D", 3, 4), Gone = true };
        var gj = JsonSerializer.Serialize<GameCommand>(g, QuartermasterJson.Options);
        Assert.Contains("\"groupRolls\":{\"north/1\":{\"D\":[3,4]}}", gj);
        Assert.Contains("\"gone\":true", gj);
        var back = (ResolveRun)JsonSerializer.Deserialize<GameCommand>(gj, QuartermasterJson.Options)!;
        Assert.Equal(new[] { 3, 4 }, back.GroupRolls!["north/1"]["D"]);
        Assert.True(back.Gone);
        var uj = JsonSerializer.Serialize(new PendingUnit("u", "U", null, true) { Group = "/1", Removed = true }, QuartermasterJson.Options);
        Assert.Contains("\"group\":\"/1\"", uj);
        Assert.Contains("\"removed\":true", uj);
    }

    [Fact]
    public void ResolveRun_group_rolls_first_wins()
    {
        var s = Barges();
        s.Apply(new QueueRuns(new[] { GroupRun() }));
        s.Apply(new ResolveRun("r1", new[] { "n1" }) { GroupRolls = G("north/1", "D", 3, 4) });
        var second = G("north/1", "D", 6, 6);
        ((Dictionary<string, IReadOnlyList<int>>)second["north/1"])["E"] = new[] { 2 };
        second["north/2"] = new Dictionary<string, IReadOnlyList<int>> { ["D"] = new[] { 1, 1 } };
        s.Apply(new ResolveRun("r1", new[] { "n2" }) { GroupRolls = second });
        var run = s.Pending.Single();
        Assert.Equal(new[] { 3, 4 }, run.GroupRolls!["north/1"]["D"]);   // kept: first wins
        Assert.Equal(new[] { 2 }, run.GroupRolls["north/1"]["E"]);
        Assert.Equal(new[] { 1, 1 }, run.GroupRolls["north/2"]["D"]);
        // the state round-trips (a save, a clone)
        var clone = s.DeepClone();
        Assert.Equal(new[] { 3, 4 }, clone.Pending.Single().GroupRolls!["north/1"]["D"]);
        Assert.Equal("north/1", clone.Pending.Single().Units[0].Group);
    }

    [Fact]
    public void ResolveRun_gone_marks_removed_and_summary()
    {
        var s = Barges();
        s.Apply(new QueueRuns(new[] { GroupRun() }));
        s.Apply(new ResolveRun("r1", new[] { "n1" }));
        s.Apply(new ResolveRun("r1", new string?[] { "n2", "n3" }) { Gone = true });
        var units = s.Pending.Single().Units;
        Assert.False(units[0].Removed);
        Assert.True(units[1].Removed && units[1].Applied);
        Assert.True(units[2].Removed);
        s.Apply(new ResolveRun("r1", new[] { "z1" }));
        Assert.Empty(s.Pending);
        Assert.Equal("Ford crossing: 4 units, 2 applied, 2 removed", s.Log[^1].Text);
        // the summary at a phase change counts removed units too
        var t = Barges();
        t.Apply(new QueueRuns(new[] { GroupRun() }));
        t.Apply(new ResolveRun("r1", new[] { "n1" }) { Gone = true });
        t.Apply(new AdvancePhase(null, new PhasePosition(1, 1, "p", 0), "", "Move"));
        Assert.Contains(t.Log, l => l.Text == "Ford crossing: 4 units, 0 applied, 1 removed");
    }

    [Fact]
    public void Summary_unchanged_without_removed()
    {
        var s = Barges();
        s.Apply(new QueueRuns(new[] { GroupRun() }));
        s.Apply(new ResolveRun("r1", new string?[] { "n1", "n2", "n3", "z1" }));
        Assert.Equal("Ford crossing: 4 units, 4 applied", s.Log[^1].Text);
    }

    private static RulesPack ViewPack() => Pack();

    private static GameState ViewState()
    {
        var s = Barges();
        var run = GroupRun() with
        {
            Units = new[]
            {
                new PendingUnit("n1", "North Barge A", null, false) { Group = "north/1" },
                new PendingUnit("s1", "South Raft", null, false) { Group = "south/1" },
            },
        };
        s.Apply(new QueueRuns(new[] { run, run with { Id = "r2", AskSideId = "north" } }));
        s.Apply(new ResolveRun("r1", new string?[] { }) { GroupRolls = G("south/1", "D", 6, 6) });
        s.Apply(new ResolveRun("r2", new string?[] { }) { GroupRolls = G("south/1", "D", 5, 5) });
        return s;
    }

    [Fact]
    public void PlayerView_drops_group_rolls()
    {
        var s = ViewState();
        Assert.NotNull(s.Pending[0].GroupRolls);
        var view = PlayerView.For(s, ViewPack(), Model, North);
        Assert.Equal(2, view.Pending.Count);
        Assert.All(view.Pending, r => Assert.Null(r.GroupRolls));
        var json = JsonSerializer.Serialize(view, QuartermasterJson.Options);
        Assert.DoesNotContain("groupRolls", json);
        Assert.DoesNotContain("south/1", json);   // an ask run keeps other sides' units, never their groups
    }

    [Fact]
    public void PlayerView_keeps_own_unit_group()
    {
        var view = PlayerView.For(ViewState(), ViewPack(), Model, North);
        var unit = Assert.Single(view.Pending[0].Units);
        Assert.Equal("n1", unit.PieceId);
        Assert.Equal("north/1", unit.Group);
        Assert.Equal("north/1", view.Pending[1].Units.Single(u => u.PieceId == "n1").Group);
    }

    // ---- Carried minor: one pick per run ----

    [Fact]
    public void Pick_resolved_once_per_run()
    {
        var s = Barges();
        var pack = Pack() with
        {
            Formulas = new()
            {
                new FormulaDef
                {
                    Id = "f-ram", Name = "Ram", Text = "T + 1",
                    Bindings = new() { new VariableBinding { Variable = "T", Kind = BindingKind.Piece, Property = "size" } },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Piece, PieceVariable = "T", Property = "loss", Op = OutputOp.Set } },
                },
            },
        };
        var label = PieceChoices.For(s, pack, Model, pack.Formulas[0].Bindings[0], North).First(c => c.PieceId == "n3").Label;
        PieceChoices.ViewBuilds = 0;
        var t = Runner(pack).Run(s, new RunRequest("f-ram", "n1", new Dictionary<string, string> { ["T"] = label },
            new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>(), PickSides: North));
        Assert.Null(t.Error);
        Assert.Equal(2, t.Result!.Value.Number);
        Assert.Equal("Loss", Assert.Single(t.Outputs).PropertyName);
        Assert.Equal(1, PieceChoices.ViewBuilds);
    }
}
