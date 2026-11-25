using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>Invented Riverland module for B3 effects: the formula fixture's map plus a palette slot "Markers/Wreck" (gpid 9), a "Dead pile"
/// map with one board and an "Empty shelf" map with none. Side "red" = Northern League ("North..."), "blue" = Southern Compact ("South...").</summary>
internal static class EffectFx
{
    private const string P = "VASSAL.build.module.map.boardPicker.board.";

    /// <summary>A 4-level layer "Levels" (not always active; level names Camp, -, Fort, Citadel).</summary>
    public const string Levels = "emb2;Activate;130;A;Up;130;U;Down;130;D;;;1;false;0;0;l1.png,l2.png,l3.png,l4.png;Camp,,Fort,Citadel;true;Levels;;;false;;1;1;false";
    /// <summary>An always-active 2-level layer "Flag".</summary>
    public const string Flag = "emb2;;130;;Up;130;U;Down;130;D;;;1;false;0;0;f1.png,f2.png;,;true;Flag;;;false;;1;1;true";
    /// <summary>A layer "Damage" that follows the property "Hits".</summary>
    public const string Damage = "emb2;;130;;;130;;;130;;;;1;false;0;0;h1.png,h2.png;,;true;Damage;;;true;Hits;1;1;true";
    public const string Mask = "obs;M;m.png;Mask it;I;?";

    public static string WreckType => PieceTypeParser.EncodeState(new[] { Levels, Mask, "piece;;;unit.svg;Wreck" });
    private static string WreckState => PieceTypeParser.EncodeState(new[] { "", "null", "null;0;0;9" });

    private static readonly Lazy<ModuleModel> LazyModel = new(() =>
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("y0=\"25\"/>",
            $"y0=\"25\"><{P}mapgrid.SquareGridNumbering first=\"H\" hType=\"N\" vType=\"N\" sep=\"\" hLeading=\"1\" vLeading=\"1\" hOff=\"1\" vOff=\"1\"/></{P}SquareGrid>");
        var extra = $"""
              <VASSAL.build.module.PieceWindow defaultWidth="0" hidden="false" hotkey="" scale="1.0" text="Markers" tooltip="" visible="true">
                <VASSAL.build.widget.PieceSlot entryName="Wreck" gpid="9" height="32" width="32">+/null/{WreckType}/{WreckState}</VASSAL.build.widget.PieceSlot>
              </VASSAL.build.module.PieceWindow>
              <VASSAL.build.module.Map mapName="Dead pile">
                <VASSAL.build.module.map.BoardPicker>
                  <VASSAL.build.module.map.boardPicker.Board image="pile.png" name="Pile board" reversible="false"/>
                </VASSAL.build.module.map.BoardPicker>
              </VASSAL.build.module.Map>
              <VASSAL.build.module.Map mapName="Empty shelf"/>
            </VASSAL.build.GameModule>
            """;
        xml = xml.Replace("</VASSAL.build.GameModule>", extra);
        using var fx = new FixtureModule().WithBuildFile(xml).AddImage("board.png", 200, 100).AddImage("pile.png", 120, 80).AddSvg("unit.svg");
        var path = fx.Build();
        using var arch = VmodArchive.Open(path);
        return ModuleModelBuilder.Build(arch, new DiagnosticLog());
    });

    public static ModuleModel Model => LazyModel.Value;

    public static readonly IReadOnlySet<string> North = new HashSet<string> { "red" };
    public static readonly IReadOnlySet<string> South = new HashSet<string> { "blue" };

    public static RulesPack Pack(params FormulaDef[] formulas) => new()
    {
        Format = 4,
        Sides = new()
        {
            new SideDef { Id = "red", Name = "Northern League", UnitFilter = "startsWith(name(), \"North\")" },
            new SideDef { Id = "blue", Name = "Southern Compact", UnitFilter = "startsWith(name(), \"South\")" },
        },
        Properties = new()
        {
            new PropertyDef { Id = "str", Name = "Strength", Scope = PropertyScope.Pieces, Default = 2, Visibility = Visibility.Public },
            new PropertyDef { Id = "salvage", Name = "Salvage", Scope = PropertyScope.Pieces, Default = 1, Visibility = Visibility.Public },
            new PropertyDef { Id = "hits", Name = "Hits", Scope = PropertyScope.Pieces, Default = 0, Visibility = Visibility.Public },
        },
        Formulas = formulas.ToList(),
    };

    public static FormulaDef F(string name, string text, params FormulaOutput[] outputs) => new()
    {
        Id = "f", Name = name, Text = text,
        Bindings = new() { new VariableBinding { Variable = "T", Kind = BindingKind.Piece, Property = "str" } },
        Outputs = outputs.ToList(),
    };

    /// <summary>Adds a piece through AddPiece (so it is stacked), with the given traits (outermost first) and their states.</summary>
    public static void Add(GameState s, string id, string name, double x = 60, double y = 40, double? str = null, string map = "Main Map",
        params (string Trait, string State)[] traits)
    {
        var type = PieceTypeParser.EncodeState(traits.Select(t => t.Trait).Append("piece;;;unit.svg;" + name).ToList());
        var state = PieceTypeParser.EncodeState(traits.Select(t => t.State).Append("").ToList());
        s.Apply(new AddPiece(id, type, state, map, new PointD(x, y), null, id + " placed"));
        if (str != null) s.Apply(new LedgerDelta(LedgerKey.Piece(id), "str", 0, str.Value, "seed", null, null, null, null));
    }

    public static string Label(GameState s, string id) => $"{PieceInfo.Name(s.Pieces[id])} ({PieceInfo.Location(s.Pieces[id], Model)})";

    public static FormulaRunner Runner(RulesPack pack) => new(pack, Model, (n, _) => Enumerable.Repeat(1, n).ToList());

    public static FormulaTrace Run(FormulaRunner runner, GameState s, string? piece, IReadOnlyDictionary<string, string>? typed = null,
        IReadOnlySet<string>? pickSides = null) =>
        runner.Run(s, new RunRequest("f", piece, typed ?? new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<int>>(), PickSides: pickSides));

    public static FormulaTrace Run(GameState s, RulesPack pack, string? piece, IReadOnlyDictionary<string, string>? typed = null,
        IReadOnlySet<string>? pickSides = null) => Run(Runner(pack), s, piece, typed, pickSides);

    public static IReadOnlyList<GameCommand> Apply(GameState s, RulesPack pack, FormulaTrace t, string? sideId = "blue")
    {
        var cmds = Runner(pack).Commands(t, null, sideId: sideId);
        foreach (var c in cmds) s.Apply(c);
        return cmds;
    }

    public static FormulaOutput Place(OutputTarget target = OutputTarget.Unit, string slot = "Markers/Wreck") =>
        new() { Action = OutputAction.Place, Target = target, Slot = slot, PieceVariable = target == OutputTarget.Piece ? "T" : null };

    public static FormulaOutput Remove(OutputTarget target = OutputTarget.Piece, string? toMap = null) =>
        new() { Action = OutputAction.Remove, Target = target, ToMapId = toMap, PieceVariable = target == OutputTarget.Piece ? "T" : null };

    public static FormulaOutput SetLayer(string layer, string? expr = null, OutputTarget target = OutputTarget.Unit) =>
        new() { Action = OutputAction.SetLayer, Target = target, Layer = layer, Expr = expr, PieceVariable = target == OutputTarget.Piece ? "T" : null };

    public static FormulaOutput SetMask(string? expr = null, OutputTarget target = OutputTarget.Unit) =>
        new() { Action = OutputAction.SetMask, Target = target, Expr = expr, PieceVariable = target == OutputTarget.Piece ? "T" : null };

    public static Dictionary<string, string> Typed(string label) => new() { ["T"] = label };
}

/// <summary>B3 #37/#52/#68: formulas that place, remove, set a layer or set a mask; the fixed command order.</summary>
public class PieceEffectTests
{
    // ---- Place ----

    [Fact]
    public void Place_in_run_piece_hex_stacked()
    {
        var s = new GameState { GameId = "g" };
        EffectFx.Add(s, "b1", "South Barge");
        var f = EffectFx.F("Scuttle", "1", EffectFx.Place()) with { Bindings = new() };
        var pack = EffectFx.Pack(f);
        var runner = EffectFx.Runner(pack);
        runner.NewPieceId = () => "p:wreck1";
        var t = EffectFx.Run(runner, s, "b1");
        Assert.Null(t.Error);
        var e = Assert.Single(t.Effects);
        Assert.Equal(PieceEffectKind.Place, e.Kind);
        Assert.Equal("p:wreck1", e.PieceId);
        Assert.Equal("placed Wreck", e.Text);
        Assert.Equal("Main Map", e.MapId);
        Assert.Equal(s.Pieces["b1"].Pos, e.Pos);
        Assert.Equal(s.Pieces["b1"].StackId, e.StackOnto);
        Assert.Equal("1 = 1 → placed Wreck", t.Working);

        var cmds = runner.Commands(t, null, sideId: "blue");
        var add = Assert.IsType<AddPiece>(cmds[0]);
        Assert.Equal("Scuttle: placed Wreck", add.LogText);
        foreach (var c in cmds) s.Apply(c);
        var w = s.Pieces["p:wreck1"];
        Assert.Equal("Wreck", PieceInfo.Name(w));
        Assert.Equal(s.Pieces["b1"].StackId, w.StackId);
        Assert.Contains(s.Log, l => l.Text == "Scuttle: placed Wreck" && l.PieceId == "p:wreck1");
    }

    [Fact]
    public void Place_in_picked_piece_hex()
    {
        var s = new GameState { GameId = "g" };
        EffectFx.Add(s, "b1", "South Guns");
        EffectFx.Add(s, "r1", "North Bridge", x: 160, str: 3);
        var pack = EffectFx.Pack(EffectFx.F("Shelling", "1", EffectFx.Place(OutputTarget.Piece)));
        var runner = EffectFx.Runner(pack);
        runner.NewPieceId = () => "p:w";
        var t = EffectFx.Run(runner, s, "b1", EffectFx.Typed(EffectFx.Label(s, "r1")));
        Assert.Null(t.Error);
        var add = Assert.IsType<AddPiece>(runner.Commands(t, null)[0]);
        Assert.Equal(("Main Map", s.Pieces["r1"].Pos, s.Pieces["r1"].StackId), (add.MapId, add.Pos, add.StackOnto));
    }

    [Fact]
    public void Placed_set_writes_new_piece()
    {
        var s = new GameState { GameId = "g" };
        EffectFx.Add(s, "b1", "South Barge");
        var f = EffectFx.F("Scuttle", "1", new FormulaOutput { Target = OutputTarget.Placed, Property = "salvage", Op = OutputOp.Set, Expr = "3" },
            EffectFx.Place()) with { Bindings = new() };
        var pack = EffectFx.Pack(f);
        var runner = EffectFx.Runner(pack);
        runner.NewPieceId = () => "p:w";
        var t = EffectFx.Run(runner, s, "b1");
        Assert.Null(t.Error);
        var o = Assert.Single(t.Outputs);
        Assert.Equal((LedgerKey.Piece("p:w"), 1.0, 3.0, OutputTarget.Placed), (o.Key, o.Before, o.After, o.Target));
        Assert.Equal("1 = 1 → Salvage of Wreck = 3; placed Wreck", t.Working);
        var cmds = runner.Commands(t, null, sideId: "blue");
        Assert.IsType<AddPiece>(cmds[0]);
        var d = Assert.IsType<LedgerDelta>(cmds[1]);
        Assert.Equal(("p:w", "Wreck", "Scuttle: Salvage = 3"), (d.PieceId, d.Subject, d.Working));
        foreach (var c in cmds) s.Apply(c);
        Assert.Equal(3, Ledger.Get(s, LedgerKey.Piece("p:w"), "salvage"));
    }

    [Fact]
    public void Placed_without_place_fails()
    {
        var s = new GameState();
        EffectFx.Add(s, "b1", "South Barge");
        foreach (var o in new[]
        {
            new FormulaOutput { Target = OutputTarget.Placed, Property = "salvage", Op = OutputOp.Set },
            EffectFx.SetLayer("Levels", "2", OutputTarget.Placed),
        })
        {
            var t = EffectFx.Run(s, EffectFx.Pack(EffectFx.F("Scuttle", "1", o) with { Bindings = new() }), "b1");
            Assert.Equal("An output writes the placed piece, but nothing was placed", t.Error);
        }
    }

    [Fact]
    public void Place_unknown_slot_fails()
    {
        var s = new GameState();
        EffectFx.Add(s, "b1", "South Barge");
        var t = EffectFx.Run(s, EffectFx.Pack(EffectFx.F("Scuttle", "1", EffectFx.Place(slot: "Markers/Raft")) with { Bindings = new() }), "b1");
        Assert.Equal("Palette entry \"Markers/Raft\" was not found in the module", t.Error);
        Assert.Empty(t.Effects);
    }

    [Fact]
    public void Place_uses_NewPieceId()
    {
        var s = new GameState();
        EffectFx.Add(s, "b1", "South Barge");
        // the gpid finds the entry too
        var pack = EffectFx.Pack(EffectFx.F("Scuttle", "1", EffectFx.Place(slot: "9"), EffectFx.SetLayer("Levels", "2", OutputTarget.Placed)) with { Bindings = new() });
        var runner = EffectFx.Runner(pack);
        var n = 0;
        runner.NewPieceId = () => "p:n" + ++n;
        var t = EffectFx.Run(runner, s, "b1");
        Assert.Null(t.Error);
        Assert.Equal(1, n);
        Assert.All(t.Effects, e => Assert.Equal("p:n1", e.PieceId));
        var cmds = runner.Commands(t, null);
        Assert.Equal("p:n1", cmds.OfType<AddPiece>().Single().PieceId);
        Assert.Equal("p:n1", cmds.OfType<SetTraitState>().Single().PieceId);
        foreach (var c in cmds) s.Apply(c);
        Assert.Equal(2, TraitState.LayerLevel(LayerOf(s.Pieces["p:n1"]), s.Pieces["p:n1"].TraitStates[0]).Level);
        // the default id is a fresh "p:" guid
        var fresh = EffectFx.Runner(pack).NewPieceId();
        Assert.StartsWith("p:", fresh);
        Assert.Equal(34, fresh.Length);
    }

    private static LayerDef LayerOf(PieceInstance p) => PieceInfo.TypeOf(p).Traits.OfType<LayerDef>().First();

    // ---- Remove ----

    private static GameState Field()
    {
        var s = new GameState { GameId = "g" };
        EffectFx.Add(s, "b1", "South Guns", str: 5);
        EffectFx.Add(s, "r1", "North Bridge", x: 160, str: 3);
        return s;
    }

    [Fact]
    public void Remove_deletes_and_drops_ledger()
    {
        var s = Field();
        var pack = EffectFx.Pack(EffectFx.F("Demolition", "1", EffectFx.Remove()));
        var t = EffectFx.Run(s, pack, "b1", EffectFx.Typed(EffectFx.Label(s, "r1")));
        Assert.Null(t.Error);
        Assert.Equal("1 = 1 → removed North Bridge", t.Working);
        var cmds = EffectFx.Apply(s, pack, t);
        var rm = Assert.IsType<RemovePiece>(cmds[^1]);
        Assert.Equal(("r1", "Demolition: removed North Bridge", "red"), (rm.PieceId, rm.LogText, rm.SideId));
        Assert.False(s.Pieces.ContainsKey("r1"));
        Assert.False(s.Ledgers.ContainsKey(LedgerKey.Piece("r1")));
        Assert.Contains(s.Log, l => l.Kind == LogKind.Delete && l.Text == "Demolition: removed North Bridge" && l.SideId == "red");
    }

    [Fact]
    public void Remove_to_map_keeps_ledger_and_moves_to_board_centre()
    {
        var s = Field();
        var pack = EffectFx.Pack(EffectFx.F("Demolition", "1", EffectFx.Remove(toMap: "Dead pile")));
        var t = EffectFx.Run(s, pack, "b1", EffectFx.Typed(EffectFx.Label(s, "r1")));
        Assert.Null(t.Error);
        Assert.Equal("1 = 1 → moved North Bridge to Dead pile", t.Working);
        var cmds = EffectFx.Apply(s, pack, t);
        var mv = Assert.IsType<MovePieces>(cmds[^1]);
        var b = EffectFx.Model.Maps.Single(m => m.Id == "Dead pile").Boards[0];
        var centre = new PointD(b.Offset.X + b.Width / 2.0, b.Offset.Y + b.Height / 2.0);
        Assert.Equal("r1", Assert.Single(mv.PieceIds));
        Assert.Equal(("Dead pile", centre, (string?)null, "Demolition: moved North Bridge to Dead pile"), (mv.MapId, mv.Pos, mv.StackOnto, mv.LogText));
        Assert.Equal(("Dead pile", centre), (s.Pieces["r1"].MapId, s.Pieces["r1"].Pos));
        Assert.Equal(3, Ledger.Get(s, LedgerKey.Piece("r1"), "str"));

        foreach (var (map, error) in new[] { ("Nowhere", "Map \"Nowhere\" was not found in the module"), ("Empty shelf", "Map \"Empty shelf\" has no board") })
        {
            var bad = EffectFx.Pack(EffectFx.F("Demolition", "1", EffectFx.Remove(toMap: map)));
            var fresh = Field();
            Assert.Equal(error, EffectFx.Run(fresh, bad, "b1", EffectFx.Typed(EffectFx.Label(fresh, "r1"))).Error);
        }
    }

    [Fact]
    public void Remove_line_stamped_with_removed_side()
    {
        var s = Field();
        s.Apply(new SetPlayers(new[] { new PlayerDef("p1", "Ann", new[] { "red" }), new PlayerDef("p2", "Bob", new[] { "blue" }) }));
        var pack = EffectFx.Pack(EffectFx.F("Demolition", "1", EffectFx.Remove()));
        var t = EffectFx.Run(s, pack, "b1", EffectFx.Typed(EffectFx.Label(s, "r1")), EffectFx.South);
        EffectFx.Apply(s, pack, t);
        Assert.Contains(PlayerView.For(s, pack, EffectFx.Model, EffectFx.North).Log, l => l.Text == "Demolition: removed North Bridge");
        var south = PlayerView.For(s, pack, EffectFx.Model, EffectFx.South);
        Assert.DoesNotContain(south.Log, l => l.Text == "Demolition: removed North Bridge");
        Assert.Contains(south.Log, l => l.Kind == LogKind.Calc && l.PieceId == "b1" && l.SideId == "blue");   // its own working line
    }

    [Fact]
    public void Remove_save_without_side_keeps_bytes()
    {
        const string old = "{\"$t\":\"removePiece\",\"pieceId\":\"p1\",\"logText\":\"Bridge removed\",\"at\":null,\"who\":null}";
        var c = JsonSerializer.Deserialize<GameCommand>(old, QuartermasterJson.Options);
        var r = Assert.IsType<RemovePiece>(c);
        Assert.Null(r.SideId);
        Assert.Equal(old, JsonSerializer.Serialize<GameCommand>(r, QuartermasterJson.Options));
        var stamped = JsonSerializer.Serialize<GameCommand>(r with { SideId = "red" }, QuartermasterJson.Options);
        Assert.Contains("\"sideId\":\"red\"", stamped);
        Assert.Equal("red", Assert.IsType<RemovePiece>(JsonSerializer.Deserialize<GameCommand>(stamped, QuartermasterJson.Options)).SideId);
    }

    // ---- Layers and masks ----

    private static GameState Fort(string levels = "", string mask = "null")
    {
        var s = new GameState { GameId = "g" };
        EffectFx.Add(s, "b1", "South Fort", traits: new[] { (EffectFx.Levels, levels), (EffectFx.Flag, ""), (EffectFx.Damage, ""), (EffectFx.Mask, mask) });
        EffectFx.Add(s, "b2", "South Guns", x: 160);
        return s;
    }

    private static FormulaDef Fortify(params FormulaOutput[] outputs) => EffectFx.F("Fortify", "3", outputs) with { Bindings = new() };

    [Fact]
    public void SetLayer_sets_level()
    {
        var s = Fort();
        var pack = EffectFx.Pack(Fortify(EffectFx.SetLayer(" levels ")));
        var t = EffectFx.Run(s, pack, "b1");
        Assert.Null(t.Error);
        Assert.Equal("3 = 3 → Levels of South Fort = Fort", t.Working);
        var cmds = EffectFx.Apply(s, pack, t);
        var set = Assert.Single(cmds.OfType<SetTraitState>());
        Assert.Equal(("b1", 0, "3"), (set.PieceId, set.StateIndex, set.NewState));
        var line = cmds.OfType<AddLogLine>().First(l => l.Kind == LogKind.System);
        Assert.Equal(("Fortify: Levels of South Fort = Fort", "b1", "blue"), (line.Text, line.PieceId, line.SideId));
        Assert.Equal((3, true), TraitState.LayerLevel(LayerOf(s.Pieces["b1"]), s.Pieces["b1"].TraitStates[0]));

        // a level without a name shows its number; 0 turns the layer off (keeps the level)
        var two = EffectFx.Run(s, EffectFx.Pack(Fortify(EffectFx.SetLayer("Levels", "2"))), "b1");
        Assert.Equal("3 = 3 → Levels of South Fort = 2", two.Working);
        var off = EffectFx.Run(s, EffectFx.Pack(Fortify(EffectFx.SetLayer("Levels", "0"))), "b1");
        Assert.Equal("3 = 3 → Levels of South Fort = 0", off.Working);
        EffectFx.Apply(s, pack, off);
        Assert.Equal((3, false), TraitState.LayerLevel(LayerOf(s.Pieces["b1"]), s.Pieces["b1"].TraitStates[0]));

        foreach (var (o, error) in new[]
        {
            (EffectFx.SetLayer("Levels", "5"), "Layer \"Levels\" has no level 5"),
            (EffectFx.SetLayer("Levels", "1.5"), "Layer \"Levels\" has no level 1.5"),
            (EffectFx.SetLayer("Levels", "-1"), "Layer \"Levels\" has no level -1"),
            (EffectFx.SetLayer("Walls", "1"), "South Fort has no layer \"Walls\""),
        })
            Assert.Equal(error, EffectFx.Run(s, EffectFx.Pack(Fortify(o)), "b1").Error);
    }

    [Fact]
    public void SetLayer_zero_on_always_active_fails()
    {
        var s = Fort();
        Assert.Equal("Layer \"Flag\" has no level 0", EffectFx.Run(s, EffectFx.Pack(Fortify(EffectFx.SetLayer("Flag", "0"))), "b1").Error);
        var t = EffectFx.Run(s, EffectFx.Pack(Fortify(EffectFx.SetLayer("Flag", "2"))), "b1");
        Assert.Null(t.Error);
        Assert.Equal("3 = 3 → Flag of South Fort = 2", t.Working);
    }

    [Fact]
    public void SetLayer_follow_property_fails()
    {
        var s = Fort();
        Assert.Equal("Layer \"Damage\" follows a property, so it cannot be set",
            EffectFx.Run(s, EffectFx.Pack(Fortify(EffectFx.SetLayer("Damage", "1"))), "b1").Error);
    }

    [Fact]
    public void SetMask_masks_and_unmasks()
    {
        var s = Fort();
        var pack = EffectFx.Pack(Fortify(EffectFx.SetMask("1")));
        var t = EffectFx.Run(s, pack, "b1");
        Assert.Null(t.Error);
        Assert.Equal("3 = 3 → South Fort masked", t.Working);
        var cmds = EffectFx.Apply(s, pack, t);
        var set = Assert.Single(cmds.OfType<SetTraitState>());
        Assert.Equal(("b1", 3, "local;"), (set.PieceId, set.StateIndex, set.NewState));
        Assert.Contains(cmds.OfType<AddLogLine>(), l => l.Kind == LogKind.System && l.Text == "Fortify: South Fort masked" && l.SideId == "blue");
        Assert.NotNull(PieceAccess.Masking(s.Pieces["b1"]).Mask);

        var unpack = EffectFx.Pack(Fortify(EffectFx.SetMask("0")));
        var u = EffectFx.Run(s, unpack, "b1");
        Assert.Equal("3 = 3 → South Fort unmasked", u.Working);
        var ucmds = EffectFx.Apply(s, unpack, u);
        Assert.Equal("null", Assert.Single(ucmds.OfType<SetTraitState>()).NewState);
        Assert.Contains(ucmds.OfType<AddLogLine>(), l => l.Text == "Fortify: South Fort unmasked");
        Assert.Null(PieceAccess.Masking(s.Pieces["b1"]).Mask);

        Assert.Equal("The mask value must be 0 or 1", EffectFx.Run(s, EffectFx.Pack(Fortify(EffectFx.SetMask("2"))), "b1").Error);
        Assert.Equal("South Guns has no mask", EffectFx.Run(s, EffectFx.Pack(Fortify(EffectFx.SetMask("1"))), "b2").Error);
        Assert.Equal("local;", TraitState.ObscuredSet(new ObscurableDef(EffectFx.Mask), "null", true, "local"));
        Assert.Equal("null", TraitState.ObscuredSet(new ObscurableDef(EffectFx.Mask), "local;", false, "local"));
    }

    [Fact]
    public void SetMask_line_dropped_for_viewer_who_now_sees_mask()
    {
        var s = new GameState { GameId = "g" };
        EffectFx.Add(s, "r1", "North Minefield", traits: new[] { (EffectFx.Mask, "null") });
        s.Apply(new SetPlayers(new[] { new PlayerDef("p1", "Ann", new[] { "red" }), new PlayerDef("p2", "Bob", new[] { "blue" }) }));
        var pack = EffectFx.Pack(EffectFx.F("Lay mines", "1", EffectFx.SetMask()) with { Bindings = new() });
        EffectFx.Apply(s, pack, EffectFx.Run(s, pack, "r1"), "red");
        Assert.Contains(PlayerView.For(s, pack, EffectFx.Model, EffectFx.North).Log, l => l.Text == "Lay mines: North Minefield masked");
        var south = PlayerView.For(s, pack, EffectFx.Model, EffectFx.South);
        Assert.DoesNotContain(south.Log, l => l.Text.Contains("Minefield"));
    }

    // ---- Order and working ----

    [Fact]
    public void Commands_order_fixed_removals_last()
    {
        var s = Fort();
        EffectFx.Add(s, "r1", "North Bridge", x: 110, str: 3);
        var f = EffectFx.F("Barrage", "dice(1, 6)",
            EffectFx.Remove(),
            EffectFx.SetLayer("Levels", "2"),
            new FormulaOutput { Target = OutputTarget.Piece, PieceVariable = "T", Property = "str", Op = OutputOp.Subtract },
            EffectFx.SetMask("1", OutputTarget.Placed),
            new FormulaOutput { Target = OutputTarget.Placed, Property = "salvage", Op = OutputOp.Set, Expr = "2" },
            EffectFx.Place(),
            new FormulaOutput { Target = OutputTarget.Unit, Property = "str", Op = OutputOp.Add });
        var pack = EffectFx.Pack(f);
        var runner = EffectFx.Runner(pack);
        runner.NewPieceId = () => "p:w";
        var t = EffectFx.Run(runner, s, "b1", EffectFx.Typed(EffectFx.Label(s, "r1")));
        Assert.Null(t.Error);
        var cmds = runner.Commands(t, null, sideId: "blue");
        Assert.Equal(new[]
        {
            "RollDice", "AddPiece", "LedgerDelta", "LedgerDelta", "LedgerDelta",
            "SetTraitState", "AddLogLine", "SetTraitState", "AddLogLine", "RemovePiece",
        }, cmds.Select(c => c.GetType().Name).ToArray());
        var deltas = cmds.OfType<LedgerDelta>().ToList();
        Assert.Equal(new[] { LedgerKey.Piece("b1"), LedgerKey.Piece("r1"), LedgerKey.Piece("p:w") }, deltas.Select(d => d.Key).ToArray());
        Assert.Equal("f", deltas[0].AppliedRun);
        // the own line's working: own parts, then the effects by the actor's names; no Piece or Placed parts
        Assert.Equal("dice(1, 6) = 1 → Strength 2 + 1 = 3; placed Wreck; Levels of South Fort = 2; Wreck masked; removed North Bridge", deltas[0].Working);
        Assert.Equal(new[] { PieceEffectKind.Place, PieceEffectKind.SetLayer, PieceEffectKind.SetMask, PieceEffectKind.Remove }, t.Effects.Select(e => e.Kind).ToArray());
        Assert.Equal(new[] { "b1", "p:w" }, cmds.OfType<SetTraitState>().Select(c => c.PieceId).ToArray());
        foreach (var c in cmds) s.Apply(c);
        Assert.False(s.Pieces.ContainsKey("r1"));
        Assert.True(PieceAccess.Masking(s.Pieces["p:w"]).Mask != null);
    }

    [Fact]
    public void Effects_only_run_writes_working_line()
    {
        var s = Fort();
        var pack = EffectFx.Pack(Fortify(EffectFx.SetLayer("Levels")));
        var cmds = EffectFx.Runner(pack).Commands(EffectFx.Run(s, pack, "b1"), null, "auto", null, "blue");
        Assert.Equal(new[] { "SetTraitState", "AddLogLine", "AddLogLine" }, cmds.Select(c => c.GetType().Name).ToArray());
        var line = Assert.IsType<AddLogLine>(cmds[^1]);
        Assert.Equal((LogKind.Calc, "Fortify: 3 = 3 → Levels of South Fort = Fort (auto)", "b1", "blue", "f", "f"),
            (line.Kind, line.Text, line.PieceId, line.SideId, line.FormulaId, line.AppliedRun));
        Assert.Null(((AddLogLine)cmds[1]).AppliedRun);
    }

    [Fact]
    public void Working_names_masked_pick_as_unknown_piece()
    {
        var s = new GameState { GameId = "g" };
        EffectFx.Add(s, "b1", "South Guns");
        EffectFx.Add(s, "r1", "North Secret Battery", x: 160, traits: new[] { (EffectFx.Levels, ""), (EffectFx.Mask, "red;") });
        var pack = EffectFx.Pack(EffectFx.F("Shelling", "1", EffectFx.SetLayer("Levels", "1", OutputTarget.Piece), EffectFx.Remove()));
        var view = PlayerView.For(s, pack, EffectFx.Model, EffectFx.South);
        var label = Assert.Single(PieceChoices.For(view, pack, EffectFx.Model, pack.Formulas[0].Bindings[0], EffectFx.South), c => c.PieceId == "r1").Label;
        // the guest's own view cannot act on the placeholder
        Assert.Equal("Hidden from this player", Assert.Single(EffectFx.Run(view, pack, "b1", EffectFx.Typed(label), EffectFx.South).Variables,
            v => v.Variable == "T").MissingReason);
        // the host resolves the guest's pick; the guest's working names it as the guest sees it (and a level by its number, never its name:
        // that would tell what the piece is), the effect lines by its real name
        var t = EffectFx.Run(s, pack, "b1", EffectFx.Typed(label), EffectFx.South);
        Assert.Null(t.Error);
        Assert.Equal("1 = 1 → Levels of Unknown piece = 1; removed Unknown piece", t.Working);
        var cmds = EffectFx.Runner(pack).Commands(t, null, sideId: "blue");
        Assert.Equal("Shelling: 1 = 1 → Levels of Unknown piece = 1; removed Unknown piece", cmds.OfType<AddLogLine>().Single(l => l.Kind == LogKind.Calc).Text);
        var sys = cmds.OfType<AddLogLine>().Single(l => l.Kind == LogKind.System);
        Assert.Equal(("Shelling: Levels of North Secret Battery = Camp", "red"), (sys.Text, sys.SideId));
        Assert.Equal("Shelling: removed North Secret Battery", cmds.OfType<RemovePiece>().Single().LogText);
        Assert.All(t.Effects, e => Assert.Equal("North Secret Battery", e.Name));
    }
}
