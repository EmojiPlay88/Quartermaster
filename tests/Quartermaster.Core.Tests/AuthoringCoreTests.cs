using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>B1 authoring, Core: checklist ticks by id, the log failure flag, kept holders at run time, labels in working. Invented data.</summary>
public class AuthoringCoreTests
{
    private static ModuleModel Model => RunnerFx.Model;
    private static readonly IReadOnlySet<string> Blue = new HashSet<string> { "blue" };
    private static readonly IReadOnlySet<string> Red = new HashSet<string> { "red" };

    // ---------- checklist ----------

    private static RulesPack ChecklistPack(List<ChecklistItem> items) => new()
    {
        Sides = new()
        {
            new SideDef { Id = "red", Name = "Red" },
            new SideDef { Id = "blue", Name = "Blue" },
        },
        Formulas = new() { new FormulaDef { Id = "f-ferry", Name = "Ferry Toll", Text = "1" } },
        Phases = new()
        {
            new PhaseDef { Id = "P", Name = "Riverland Supply", Checklist = items },
            new PhaseDef { Id = "B", Name = "Blue Crossing", SideId = "blue", Checklist = new() { new ChecklistItem { Id = "blue-scout", Text = "Scout the TEST ford at dawn" } } },
            new PhaseDef { Id = "R", Name = "Red Crossing", SideId = "red", Checklist = new() { "Red only" } },
        },
    };

    private static GameState At(RulesPack pack, string phaseId)
    {
        var s = new GameState();
        var idx = pack.Phases.FindIndex(p => p.Id == phaseId);
        s.Apply(new AdvancePhase(null, new PhasePosition(1, 1, phaseId, idx), "", pack.Phases[idx].Name));
        return s;
    }

    private static List<string> OpenItems(RulesPack pack, GameState s) =>
        Decisions.Compute(pack, s).Where(d => d.Kind == DecisionKind.Checklist).Select(d => d.Text).ToList();

    [Fact]
    public void Tick_by_id_survives_reorder()
    {
        var fuel = new ChecklistItem { Id = "fuel", Text = "Refuel the TEST barges" };
        var pack = ChecklistPack(new() { fuel, "Check ammo", "Report" });
        var s = At(pack, "P");
        s.Apply(new TickChecklist(0, true, fuel.Text, ItemId: "fuel"));
        s.Apply(new TickChecklist(1, true, "Check ammo"));
        Assert.Equal(new[] { "fuel" }, s.TickedIds);
        Assert.Equal(new[] { 1 }, s.Ticked);
        Assert.Equal(new[] { "Report" }, OpenItems(pack, s));

        // Reorder: the id-ticked item moves to the end; the index tick stays on index 1 (now "Report").
        var reordered = pack with { Phases = pack.Phases.Select(p => p.Id == "P" ? p with { Checklist = new() { "Check ammo", "Report", fuel } } : p).ToList() };
        Assert.True(ChecklistState.IsTicked(s, fuel, 2));
        Assert.Equal(new[] { "Check ammo" }, OpenItems(reordered, s));
    }

    [Fact]
    public void Tick_without_id_uses_index()
    {
        var pack = ChecklistPack(new() { "Check ammo", "Report" });
        var s = At(pack, "P");
        s.Apply(new TickChecklist(1, true, "Report"));
        Assert.Equal(new[] { 1 }, s.Ticked);
        Assert.Null(s.TickedIds);
        Assert.True(ChecklistState.IsTicked(s, "Report", 1));
        Assert.False(ChecklistState.IsTicked(s, "Report", 0));
        Assert.Contains(s.Log, l => l.Text == "Done: Report");
    }

    [Fact]
    public void TickedIds_cleared_on_advance()
    {
        var pack = ChecklistPack(new() { new ChecklistItem { Id = "fuel", Text = "Refuel" }, new ChecklistItem { Id = "ammo", Text = "Ammo" } });
        var s = At(pack, "P");
        s.Apply(new TickChecklist(0, true, "Refuel", ItemId: "fuel"));
        s.Apply(new TickChecklist(0, false, "Refuel", ItemId: "fuel"));
        Assert.Null(s.TickedIds);   // emptied → null
        s.Apply(new TickChecklist(1, true, "Ammo", ItemId: "ammo"));
        Assert.Equal(new[] { "ammo" }, s.TickedIds);
        Assert.Empty(s.Ticked);
        s.Apply(new AdvancePhase(s.Position, new PhasePosition(1, 1, "B", 1), "Riverland Supply", "Blue Crossing"));
        Assert.Null(s.TickedIds);
    }

    [Fact]
    public void Decision_text_renders_links()
    {
        var pack = ChecklistPack(new()
        {
            new ChecklistItem { Id = "fuel", Text = "Refuel the TEST barges" },
            "Pay {formula:f-ferry} after {item:fuel}; see {item:nothing-here}",
        });
        var s = At(pack, "P");
        Assert.Equal(new[] { "Refuel the TEST barges", "Pay Ferry Toll after \"Refuel the TEST barges\"; see {item:nothing-here}" },
            OpenItems(pack, s));
    }

    [Fact]
    public void Decision_text_keeps_link_into_another_sides_phase_for_a_player_view()
    {
        // Controller note (a): a player whose view hides the Blue phase's checklist never gets its item text through a link.
        var pack = ChecklistPack(new() { "Watch for {item:blue-scout}" });
        var full = At(pack, "P");
        Assert.Equal(new[] { "Watch for \"Scout the TEST ford at dawn\"" }, OpenItems(pack, full));

        var red = PlayerView.For(full, pack, Model, Red);
        Assert.Equal(new[] { "Watch for {item:blue-scout}" }, OpenItems(pack, red));
        Assert.DoesNotContain("Scout the TEST ford", JsonSerializer.Serialize(Decisions.Compute(pack, red)));

        var blue = PlayerView.For(full, pack, Model, Blue);
        Assert.Equal(new[] { "Watch for \"Scout the TEST ford at dawn\"" }, OpenItems(pack, blue));
    }

    [Fact]
    public void Old_commands_and_log_serialise_unchanged()
    {
        var o = QuartermasterJson.Options;
        var tick = JsonSerializer.Serialize<GameCommand>(new TickChecklist(2, true, "Report"), o);
        Assert.DoesNotContain("itemId", tick);
        Assert.Contains("itemId", JsonSerializer.Serialize<GameCommand>(new TickChecklist(2, true, "Report", ItemId: "rep"), o));

        var line = JsonSerializer.Serialize<GameCommand>(new AddLogLine(LogKind.Calc, "Ferry Toll: Barge: boom"), o);
        Assert.DoesNotContain("error", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"error\":true", JsonSerializer.Serialize<GameCommand>(new AddLogLine(LogKind.Calc, "x", Error: true), o));

        var entry = JsonSerializer.Serialize(new LogEntry(1, LogKind.Calc, "x", null, null, null, DateTimeOffset.UnixEpoch), o);
        Assert.DoesNotContain("error", entry, StringComparison.OrdinalIgnoreCase);

        var s = new GameState();
        s.Apply(new AddLogLine(LogKind.Calc, "Ferry Toll: Barge: boom"));
        var json = JsonSerializer.Serialize(s, o);
        Assert.DoesNotContain("tickedIds", json);
        Assert.DoesNotContain("\"error\"", json);
        Assert.False(s.Log.Single().Error);

        s.Apply(new AddLogLine(LogKind.Calc, "Ferry Toll: Barge: boom", Error: true));
        Assert.True(s.Log[^1].Error);
        var back = JsonSerializer.Deserialize<GameState>(JsonSerializer.Serialize(s, o), o)!;
        Assert.True(back.Log[^1].Error);
    }

    // ---------- failure flag through PlayerView ----------

    private static RulesPack SidesPack(List<PropertyDef>? props = null, List<TableDef>? tables = null, List<FormulaDef>? formulas = null) => new()
    {
        Format = 4,
        Sides = new()
        {
            new SideDef { Id = "red", Name = "Red", UnitFilter = "marker(\"Side\") == \"Red\"" },
            new SideDef { Id = "blue", Name = "Blue", UnitFilter = "marker(\"Side\") == \"Blue\"" },
        },
        Properties = props ?? new()
        {
            new PropertyDef { Id = "morale", Name = "Morale", Scope = PropertyScope.Pieces, Visibility = Visibility.Public },
        },
        Tables = tables ?? new(),
        Phases = new()
        {
            new PhaseDef { Id = "rm", Name = "Red Movement", SideId = "red" },
            new PhaseDef { Id = "bm", Name = "Blue Movement", SideId = "blue" },
        },
        Formulas = formulas ?? new() { new FormulaDef { Id = "f-open", Name = "Open Drill", Text = "1" } },
    };

    private static void AddUnit(GameState s, string id, string name, string side) =>
        s.Apply(new AddPiece(id,
            PieceTypeParser.EncodeState(new List<string> { "mark;Side", "piece;;;unit.svg;" + name }),
            PieceTypeParser.EncodeState(new List<string> { side, "" }), "Main Map", new PointD(50, 50), null, $"{name} placed"));

    private static GameState TwoSides()
    {
        var s = new GameState { GameId = "g1" };
        s.Apply(new SetPlayers(new[] { new PlayerDef("p1", "Ann", new[] { "red" }), new PlayerDef("p2", "Bob", new[] { "blue" }) }));
        AddUnit(s, "r1", "Rifle", "Red");
        AddUnit(s, "b1", "Lancer", "Blue");
        return s;
    }

    [Fact]
    public void Redacted_failure_line_drops_error_flag()
    {
        var pack = SidesPack();
        var s = TwoSides();
        s.Apply(new AddLogLine(LogKind.Calc, "Open Drill: Rifle: boom", "r1", SideId: "red", FormulaId: "f-open", Error: true));
        Assert.True(s.Log[^1].Error);

        var blue = PlayerView.For(s, pack, Model, Blue);
        var line = Assert.Single(blue.Log, l => l.Text == "Red ledger changed");
        Assert.False(line.Error);
        Assert.DoesNotContain(blue.Log, l => l.Error);
        Assert.DoesNotContain("\"error\"", JsonSerializer.Serialize(blue, QuartermasterJson.Options));
    }

    [Fact]
    public void Kept_failure_line_keeps_error_flag()
    {
        var pack = SidesPack();
        var s = TwoSides();
        s.Apply(new AddLogLine(LogKind.Calc, "Open Drill: Rifle: boom", "r1", SideId: "red", FormulaId: "f-open", Error: true));

        var red = PlayerView.For(s, pack, Model, Red);
        var line = Assert.Single(red.Log, l => l.Text == "Open Drill: Rifle: boom");
        Assert.True(line.Error);
    }

    // ---------- kept holders at run time ----------

    private static RulesPack KeptPack(FormulaDef f) => new()
    {
        Properties = new()
        {
            // Tolls are kept by Northdepot only.
            new PropertyDef { Id = "tolls", Name = "Tolls", Scope = PropertyScope.Holders, Holders = new() { "north" } },
            new PropertyDef { Id = "stock", Name = "Stock", Scope = PropertyScope.Holders },
        },
        Holders = new()
        {
            new HolderDef { Id = "north", Name = "Northdepot" },
            new HolderDef { Id = "south", Name = "Southdepot" },
        },
        Formulas = new() { f },
    };

    private static FormulaTrace RunKept(FormulaDef f)
    {
        var pack = KeptPack(f);
        var s = new GameState();
        s.Ledgers[LedgerKey.Holder("north")] = new() { ["tolls"] = 3 };
        s.Ledgers[LedgerKey.Holder("south")] = new() { ["tolls"] = 4, ["stock"] = 2 };
        return RunnerFx.Runner(pack).Run(s, RunnerFx.Req(f.Id, piece: null));
    }

    [Fact]
    public void Holder_output_not_kept_fails_run()
    {
        var t = RunKept(new FormulaDef
        {
            Id = "f", Name = "Toll", Text = "2",
            Outputs = new() { new FormulaOutput { Target = OutputTarget.Holder, HolderId = "south", Property = "tolls", Op = OutputOp.Add } },
        });
        Assert.Equal("Southdepot does not keep Tolls", t.Error);
        Assert.Empty(t.Outputs);

        var ok = RunKept(new FormulaDef
        {
            Id = "f", Name = "Toll", Text = "2",
            Outputs = new() { new FormulaOutput { Target = OutputTarget.Holder, HolderId = "north", Property = "tolls", Op = OutputOp.Add } },
        });
        Assert.Null(ok.Error);
        Assert.Equal(5, ok.Outputs.Single().After);
    }

    [Fact]
    public void Holder_binding_not_kept_fails_run()
    {
        var t = RunKept(new FormulaDef
        {
            Id = "f", Name = "Toll", Text = "T",
            Bindings = new() { new VariableBinding { Variable = "T", Kind = BindingKind.Holder, HolderId = "south", Property = "tolls" } },
        });
        Assert.Equal("Southdepot does not keep Tolls", t.Error);

        var g = RunKept(new FormulaDef
        {
            Id = "f", Name = "Toll", Text = "T",
            Bindings = new() { new VariableBinding { Variable = "T", Kind = BindingKind.Global, Property = "tolls" } },
        });
        Assert.Equal("Global does not keep Tolls", g.Error);

        var ok = RunKept(new FormulaDef
        {
            Id = "f", Name = "Toll", Text = "T",
            Bindings = new() { new VariableBinding { Variable = "T", Kind = BindingKind.Holder, HolderId = "north", Property = "tolls" } },
        });
        Assert.Null(ok.Error);
        Assert.Equal(3, ok.Result!.Value.Number);
    }

    [Fact]
    public void Global_output_not_kept_fails_run()
    {
        var t = RunKept(new FormulaDef
        {
            Id = "f", Name = "Toll", Text = "2",
            Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "tolls", Op = OutputOp.Set } },
        });
        Assert.Equal("Global does not keep Tolls", t.Error);
        Assert.Empty(t.Outputs);
    }

    [Fact]
    public void Holder_call_not_kept_fails_run()
    {
        var t = RunKept(new FormulaDef { Id = "f", Name = "Toll", Text = "holder(Southdepot, Tolls)" });
        Assert.Equal("Southdepot does not keep Tolls", t.Error);

        var ok = RunKept(new FormulaDef { Id = "f", Name = "Toll", Text = "holder(Northdepot, Tolls) + holder(Southdepot, Stock)" });
        Assert.Null(ok.Error);
        Assert.Equal(5, ok.Result!.Value.Number);
    }

    // ---------- labels in working ----------

    private static readonly TableDef Weather = new()
    {
        Id = "t-wx", Name = "Weather names", Kind = TableKind.OneD,
        RowKeys = new() { "1", "2", "3" }, ColumnKeys = new() { "Name" },
        Cells = new() { new() { "Clear" }, new() { "Rain" }, new() { "Fog" } },
    };

    private static FormulaTrace RunWeather(OutputOp op)
    {
        var pack = new RulesPack
        {
            Properties = new() { new PropertyDef { Id = "wx", Name = "Weather", Scope = PropertyScope.Holders, Labels = "t-wx" } },
            Tables = new() { Weather },
            Formulas = new()
            {
                new FormulaDef
                {
                    Id = "f-wx", Name = "Worsen", Text = "W + 1",
                    Bindings = new() { new VariableBinding { Variable = "W", Kind = BindingKind.Global, Property = "wx" } },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "wx", Op = op } },
                },
            },
        };
        var s = new GameState();
        s.Ledgers[LedgerKey.Holder(HolderDef.GlobalId)] = new() { ["wx"] = 2 };
        var t = RunnerFx.Runner(pack).Run(s, RunnerFx.Req("f-wx", piece: null));
        Assert.Null(t.Error);
        return t;
    }

    [Fact]
    public void Working_shows_label_for_variable_and_set_output()
    {
        var t = RunWeather(OutputOp.Set);
        Assert.Equal("2 (Rain) + 1 = 3 → Weather = 3 (Fog)", t.Working);
        Assert.Equal(3, t.Outputs.Single().After);   // values stay numbers
    }

    [Fact]
    public void Add_output_stays_bare_number()
    {
        var t = RunWeather(OutputOp.Add);
        Assert.Equal("2 (Rain) + 1 = 3 → Weather 2 + 3 = 5", t.Working);
    }

    [Fact]
    public void Guest_hidden_working_with_label_redacted()
    {
        // Red's "Visibility" is Private and labelled; a Red run reads it on Red's own unit. Blue sees neither the number nor the label.
        var pack = SidesPack(
            props: new()
            {
                new PropertyDef { Id = "vis", Name = "Visibility", Scope = PropertyScope.Pieces, Visibility = Visibility.Private, Labels = "t-vis" },
                new PropertyDef { Id = "morale", Name = "Morale", Scope = PropertyScope.Pieces, Visibility = Visibility.Public },
            },
            tables: new()
            {
                new TableDef
                {
                    Id = "t-vis", Name = "Visibility names", Kind = TableKind.OneD,
                    RowKeys = new() { "4242" }, ColumnKeys = new() { "Name" }, Cells = new() { new() { "Mistbank" } },
                },
            },
            formulas: new()
            {
                new FormulaDef
                {
                    Id = "f-look", Name = "Look Out", Text = "V * 0 + 1",
                    Bindings = new() { new VariableBinding { Variable = "V", Kind = BindingKind.Unit, Property = "vis" } },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "morale", Op = OutputOp.Set } },
                },
            });
        var s = TwoSides();
        s.Apply(new LedgerDelta(LedgerKey.Piece("r1"), "vis", 0, 4242, "manual", null, null, null, null));
        var runner = RunnerFx.Runner(pack);
        var t = runner.Run(s, RunnerFx.Req("f-look", "r1"));
        Assert.Null(t.Error);
        Assert.Contains("4242 (Mistbank)", t.Working);
        foreach (var c in runner.Commands(t, null, sideId: "red")) s.Apply(c);
        Assert.Contains(s.Log, l => l.Text.Contains("4242 (Mistbank)"));

        var blue = PlayerView.For(s, pack, Model, Blue);
        var json = JsonSerializer.Serialize(blue, QuartermasterJson.Options);
        Assert.DoesNotContain("4242", json);
        Assert.DoesNotContain("Mistbank", json);
        Assert.Contains(blue.Log, l => l.Text == "Red ledger changed");
    }
}
