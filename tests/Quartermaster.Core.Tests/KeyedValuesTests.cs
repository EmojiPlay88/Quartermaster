using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>Shared Riverland state for the places tests (B6): invented names only.</summary>
internal static class PlacesKit
{
    public static ModuleModel Model => PlacesFixture.Model;
    public const string Global = "h:global";
    public const string Yard = "h:yard";

    public static PieceInstance Unit(string id, string name, string side, string mapId, PointD pos) => new()
    {
        Id = id, MapId = mapId, Pos = pos,
        TypeString = PieceTypeParser.EncodeState(new[] { "mark;Side", "piece;;;unit.svg;" + name }),
        TraitStates = new() { side, "" },
    };

    public static void Put(GameState s, string key, string prop, string valueKey, double after, string? working = null) =>
        s.Apply(new LedgerDelta(key, prop, 0, after, "manual", null, null, null, working) { ValueKey = valueKey });

    /// <summary>
    /// r1 "Red Barge" (Red) on Riverside 0412, b1 "Blue Raft" (Blue) on Delta 0303, s1 "Red Scout" (Red) on Square 0303. Places: FordDepth
    /// 0412 = 3, Mines 0505 = 2. BargesDue 2 = 5, 3 = 1. HullCount at Boatyard: Barge = 5. Cargo: r1 Barge = 7, b1 Raft = 4. Strength r1 = 2.
    /// </summary>
    public static GameState State()
    {
        var s = new GameState { GameId = "g1" };
        s.Apply(new SetPlayers(new[] { new PlayerDef("p1", "Ann", new[] { "red" }), new PlayerDef("p2", "Bob", new[] { "blue" }) }));
        void Add(PieceInstance p) => s.Pieces[p.Id] = p;
        Add(Unit("r1", "Red Barge", "Red", PlacesFixture.MapOf("Riverside"), PlacesFixture.At("Riverside", "0412")));
        Add(Unit("b1", "Blue Raft", "Blue", PlacesFixture.MapOf("Delta"), PlacesFixture.At("Delta", "0303")));
        Add(Unit("s1", "Red Scout", "Red", PlacesFixture.MapOf("Square"), PlacesFixture.At("Square", "0303")));
        Put(s, Global, "p-ford", "0412", 3, "FordDepth[0412] 0 → 3");
        Put(s, Global, "p-mines", "0505", 2, "Mines[0505] 0 → 2");
        Put(s, Global, "p-due", "2", 5);
        Put(s, Global, "p-due", "3", 1);
        Put(s, Yard, "p-hulls", "Barge", 5);
        Put(s, LedgerKey.Piece("r1"), "p-cargo", "Barge", 7);
        Put(s, LedgerKey.Piece("b1"), "p-cargo", "Raft", 4);
        s.Ledgers[LedgerKey.Piece("r1")] = new() { ["p-str"] = 2 };
        return s;
    }

    public static RulesPack Pack(params FormulaDef[] formulas) => PlacesFixture.Pack() with { Formulas = formulas.ToList() };

    public static FormulaDef F(string text, params FormulaOutput[] outputs) => new() { Id = "f", Name = "Probe", Text = text, Outputs = outputs.ToList() };

    public static FormulaTrace Run(GameState s, RulesPack pack, string? pieceId, string formulaId = "f") =>
        new FormulaRunner(pack, Model, (n, _) => Enumerable.Repeat(1, n).ToList()) { NewPieceId = () => "p:new" }
            .Run(s, new RunRequest(formulaId, pieceId, new Dictionary<string, string>(), new Dictionary<string, string>(),
                new Dictionary<string, IReadOnlyList<int>>()));

    /// <summary>Runs a formula of text <paramref name="text"/> (no outputs) and returns its result, or throws with the run's error.</summary>
    public static FormulaTrace Eval(GameState s, string text, string? pieceId) => Run(s, Pack(F(text)), pieceId);
}

public class KeyedValuesTests
{
    private static ModuleModel Model => PlacesKit.Model;
    private static RulesPack Pack => PlacesFixture.Pack();
    private static PropertyDef Prop(string id) => Pack.ById<PropertyDef>(id)!;

    // ---------- store and saves ----------

    /// <summary>Written by master's serializer (before B6): a state with ledgers, history and a log line, and a ledgerDelta command.</summary>
    private const string PreB6State = """{"pieces":{},"stacks":{},"mapOrder":[],"nextStackSeq":0,"ledgers":{"h:global":{"p-str":4}},"ledgerHistory":[{"seq":1,"key":"h:global","propertyId":"p-str","before":0,"after":4,"source":"manual","formulaId":null,"ruleRefId":null,"note":"count","working":null,"turn":1,"phase":null}],"diceHistory":[],"log":[{"seq":1,"kind":"ledger","text":"p-str 0 \u2192 4 (note: count)","who":"Ann","pieceId":null,"mapId":null,"at":"1970-01-01T00:00:00+00:00","turn":null,"phase":null,"sideId":null}],"notes":"","players":[],"gameId":"g1","turn":{"turn":1,"phase":null},"position":null,"difficulty":"SemiAuto","ticked":[],"acknowledged":[],"pending":[],"hiddenAlerts":[]}""";
    private const string PreB6Commands = """[{"$t":"ledgerDelta","key":"h:global","propertyId":"p-str","before":0,"after":4,"source":"manual","formulaId":null,"ruleRefId":null,"note":"count","working":null,"at":"1970-01-01T00:00:00+00:00","who":"Ann","tag":null,"subject":null,"pieceId":null,"sideId":null}]""";

    [Fact]
    public void Save_without_keyed_keeps_bytes()
    {
        var state = JsonSerializer.Deserialize<GameState>(PreB6State, QuartermasterJson.Options)!;
        Assert.Null(state.Keyed);
        Assert.Equal(PreB6State, JsonSerializer.Serialize(state, QuartermasterJson.Options));
        var cmds = JsonSerializer.Deserialize<List<GameCommand>>(PreB6Commands, QuartermasterJson.Options)!;
        Assert.Null(Assert.IsType<LedgerDelta>(cmds[0]).ValueKey);
        Assert.Equal(PreB6Commands, JsonSerializer.Serialize(cmds, QuartermasterJson.Options));
        // Replaying the command on a fresh state gives the same state bytes.
        var replay = new GameState { GameId = "g1" };
        replay.Apply(cmds[0]);
        Assert.Equal(PreB6State, JsonSerializer.Serialize(replay, QuartermasterJson.Options));
    }

    [Fact]
    public void Keyed_delta_writes_keyed_not_ledgers()
    {
        var s = new GameState();
        PlacesKit.Put(s, PlacesKit.Global, "p-ford", "0412", 3);
        Assert.Equal(3, s.Keyed![PlacesKit.Global]["p-ford"]["0412"]);
        Assert.False(s.Ledgers.ContainsKey(PlacesKit.Global));
        Assert.Equal(3, KeyedValues.Get(s, PlacesKit.Global, "p-ford", "0412"));
        Assert.Null(KeyedValues.Get(s, PlacesKit.Global, "p-ford", "0303"));
        Assert.Equal(0, KeyedValues.GetOrDefault(s, PlacesKit.Global, Prop("p-ford"), "0303"));
        Assert.Equal(new[] { "0412" }, KeyedValues.Keys(s, PlacesKit.Global, "p-ford"));
        Assert.Empty(KeyedValues.Keys(s, PlacesKit.Global, "p-mines"));
        var cmd = JsonSerializer.Serialize<GameCommand>(new LedgerDelta("k", "p", 0, 1, "manual", null, null, null, null) { ValueKey = "0412" }, QuartermasterJson.Options);
        Assert.Contains("\"valueKey\":\"0412\"", cmd);
    }

    [Fact]
    public void Keyed_history_entry_has_key_and_History_excludes_it()
    {
        var s = new GameState();
        s.Apply(new LedgerDelta(PlacesKit.Global, "p-ford", 0, 1, "manual", null, null, null, null));
        PlacesKit.Put(s, PlacesKit.Global, "p-ford", "0412", 3);
        Assert.Null(s.LedgerHistory[0].ValueKey);
        Assert.Equal("0412", s.LedgerHistory[1].ValueKey);
        Assert.Single(Ledger.History(s, PlacesKit.Global, "p-ford"));
        Assert.Null(Ledger.History(s, PlacesKit.Global, "p-ford")[0].ValueKey);
        var keyed = Ledger.History(s, PlacesKit.Global, "p-ford", "0412");
        Assert.Single(keyed);
        Assert.Equal(3, keyed[0].After);
        var json = JsonSerializer.Serialize(s, QuartermasterJson.Options);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"valueKey\""));
    }

    [Fact]
    public void RemovePiece_drops_keyed()
    {
        var s = PlacesKit.State();
        s.Apply(new RemovePiece("r1"));
        Assert.False(s.Keyed!.ContainsKey(LedgerKey.Piece("r1")));
        Assert.True(s.Keyed.ContainsKey(LedgerKey.Piece("b1")));
    }

    [Fact]
    public void Reset_deletes_keyed_values()
    {
        var s = PlacesKit.State();
        s.Apply(new ResetProperties(new[] { "p-due", "p-cargo" }, "Movement: reset BargesDue, Cargo"));
        Assert.False(s.Keyed![PlacesKit.Global].ContainsKey("p-due"));
        Assert.True(s.Keyed[PlacesKit.Global].ContainsKey("p-ford"));
        Assert.False(s.Keyed.ContainsKey(LedgerKey.Piece("r1")));   // emptied, so it goes
        Assert.Equal(7, s.LedgerHistory.Single(e => e.PropertyId == "p-cargo" && e.Key == LedgerKey.Piece("r1")).After);   // history kept
    }

    [Fact]
    public void DropPastTurns_removes_lower_keys_only()
    {
        var s = new GameState();
        PlacesKit.Put(s, PlacesKit.Global, "p-due", "1", 4);
        PlacesKit.Put(s, PlacesKit.Global, "p-due", "2", 5);
        PlacesKit.Put(s, PlacesKit.Global, "p-due", "3", 1);
        PlacesKit.Put(s, PlacesKit.Yard, "p-due", "1", 9);
        PlacesKit.Put(s, PlacesKit.Global, "p-hulls", "2", 8);   // not listed: kept
        var log = s.Log.Count;
        var history = s.LedgerHistory.Count;
        s.Apply(new DropPastTurns(new[] { "p-due" }, 3));
        Assert.Equal(new[] { "3" }, KeyedValues.Keys(s, PlacesKit.Global, "p-due"));
        Assert.False(s.Keyed!.ContainsKey(PlacesKit.Yard));
        Assert.Equal(8, KeyedValues.Get(s, PlacesKit.Global, "p-hulls", "2"));
        Assert.Equal(log, s.Log.Count);
        Assert.Equal(history, s.LedgerHistory.Count);
        var json = JsonSerializer.Serialize<GameCommand>(new DropPastTurns(new[] { "p-due" }, 3), QuartermasterJson.Options);
        Assert.Equal("""{"$t":"dropPastTurns","propertyIds":["p-due"],"turn":3}""", json);
        Assert.IsType<DropPastTurns>(JsonSerializer.Deserialize<GameCommand>(json, QuartermasterJson.Options));
    }

    [Fact]
    public void Keyed_null_when_emptied()
    {
        var s = new GameState();
        PlacesKit.Put(s, PlacesKit.Global, "p-due", "1", 4);
        s.Apply(new DropPastTurns(new[] { "p-due" }, 2));
        Assert.Null(s.Keyed);
        Assert.DoesNotContain("\"keyed\"", JsonSerializer.Serialize(s, QuartermasterJson.Options));
        PlacesKit.Put(s, LedgerKey.Piece("x"), "p-cargo", "Barge", 1);
        s.Apply(new ResetProperties(new[] { "p-cargo" }, ""));
        Assert.Null(s.Keyed);
        PlacesKit.Put(s, LedgerKey.Piece("x"), "p-cargo", "Barge", 1);
        s.Pieces["x"] = PlacesKit.Unit("x", "Gone", "Red", "North", new PointD(1, 1));
        s.Apply(new RemovePiece("x"));
        Assert.Null(s.Keyed);
    }

    [Fact]
    public void DeepClone_keeps_keyed()
    {
        var s = PlacesKit.State();
        var c = s.DeepClone();
        Assert.Equal(3, c.Keyed![PlacesKit.Global]["p-ford"]["0412"]);
        Assert.Equal(new[] { "2", "3" }, c.Keyed[PlacesKit.Global]["p-due"].Keys);
        Assert.Equal("0412", c.LedgerHistory.First(e => e.PropertyId == "p-ford").ValueKey);
        Assert.Equal(JsonSerializer.Serialize(s, QuartermasterJson.Options), JsonSerializer.Serialize(c, QuartermasterJson.Options));
    }

    [Fact]
    public void Saved_state_with_null_keyed_inner_is_cleaned()
    {
        var s = JsonSerializer.Deserialize<GameState>("""{"keyed":{"h:global":null,"p:x":{"p-due":null}}}""", QuartermasterJson.Options)!;
        NativeSave.ValidateState(s);
        Assert.Null(s.Keyed);
    }

    // ---------- keys ----------

    private static string Norm(string prop, Value key, ModuleModel? model = null) => KeyedValues.NormaliseKey(Pack, model ?? Model, Prop(prop), key);

    private static string NormError(string prop, Value key, ModuleModel? model = null) =>
        Assert.Throws<EvalError>(() => KeyedValues.NormaliseKey(Pack, model ?? Model, Prop(prop), key)).Message;

    [Fact]
    public void Normalise_table_key_row_spelling()
    {
        Assert.Equal("Barge", Norm("p-hulls", Value.Of("  barge ")));
        Assert.Equal("Keelboat", Norm("p-cargo", Value.Of("KEELBOAT")));
    }

    [Fact]
    public void Normalise_table_unknown_row() =>
        Assert.Equal("no row \"Canoe\" in table \"Hull types\"", NormError("p-hulls", Value.Of("Canoe")));

    [Fact]
    public void Normalise_turn_whole_number()
    {
        Assert.Equal("3", Norm("p-due", Value.Of(3)));
        Assert.Equal("4", Norm("p-due", Value.Of(" 4 ")));
        Assert.Equal("100000", Norm("p-due", Value.Of(100000)));
    }

    [Fact]
    public void Normalise_turn_rejects_0_and_fraction()
    {
        const string text = "the turn must be a whole number of at least 1";
        Assert.Equal(text, NormError("p-due", Value.Of(0)));
        Assert.Equal(text, NormError("p-due", Value.Of(2.5)));
        Assert.Equal(text, NormError("p-due", Value.Of(-1)));
        Assert.Equal(text, NormError("p-due", Value.Of(100001)));
        Assert.Equal(text, NormError("p-due", Value.Of("soon")));
    }

    [Fact]
    public void Normalise_hex_board_spelling()
    {
        Assert.Equal("0412", Norm("p-ford", Value.Of(" 0412 ")));
        Assert.Equal("0201", Norm("p-ford", Value.Of("0201")));   // a hex of the Square board only
    }

    [Fact]
    public void Normalise_hex_unknown_with_grid()
    {
        Assert.Equal("no hex named \"9999\" on any board", NormError("p-ford", Value.Of("9999")));
        Assert.Equal("no hex named \"\" on any board", NormError("p-ford", Value.Of("  ")));
    }

    [Fact]
    public void Normalise_hex_without_model_trimmed()
    {
        Assert.Equal("Ford A", KeyedValues.NormaliseKey(Pack, null, Prop("p-ford"), Value.Of("  Ford A ")));
        Assert.Equal("9999", KeyedValues.NormaliseKey(Pack, null, Prop("p-ford"), Value.Of("9999")));
    }

    [Fact]
    public void Normalise_key_too_long()
    {
        var longKey = new string('7', 65);
        Assert.Equal("the key is too long", NormError("p-ford", Value.Of(longKey)));
        Assert.Equal("the key is too long", Assert.Throws<EvalError>(() => KeyedValues.NormaliseKey(Pack, null, Prop("p-ford"), Value.Of(longKey))).Message);
        Assert.Equal(new string('7', 64), KeyedValues.NormaliseKey(Pack, null, Prop("p-ford"), Value.Of(new string('7', 64))));
    }

    // ---------- reads ----------

    private static Value Result(string text, string? piece = "r1", GameState? s = null)
    {
        var t = PlacesKit.Eval(s ?? PlacesKit.State(), text, piece);
        Assert.True(t.Error == null, t.Error);
        return t.Result!.Value;
    }

    private static string Error(string text, string? piece = "r1", GameState? s = null) =>
        PlacesKit.Eval(s ?? PlacesKit.State(), text, piece).Error ?? "(no error)";

    [Fact]
    public void Hex_returns_location()
    {
        Assert.Equal("0412", Result("hex()").Text);
        Assert.Equal("0303", Result("hex()", "b1").Text);
    }

    [Fact]
    public void Hex_needs_unit() => Assert.Equal("hex() needs a unit", Error("hex()", null));

    [Fact]
    public void Hex_off_grid()
    {
        var s = PlacesKit.State();
        s.Pieces["lost"] = PlacesKit.Unit("lost", "Lost Raft", "Red", "North", new PointD(1000, 800));
        Assert.Equal("Lost Raft is not in a hex", Error("hex()", "lost", s));
    }

    [Fact]
    public void Distance_same_board()
    {
        Assert.Equal(1, Result("distance(\"0303\", \"0304\")").Number);
        Assert.Equal(0, Result("distance(hex(), hex())").Number);
        var b = PlacesFixture.Board("Riverside");
        var expected = BoardCells.Cells(b.Grid, b.Grid!.LocationToPoint("0412")!.Value, b.Grid.LocationToPoint("0505")!.Value)!.Value;
        Assert.Equal(expected, Result("distance(hex(), \"0505\")").Number);
    }

    [Fact]
    public void Distance_prefers_run_piece_board()
    {
        // "0303" and "0507" are hexes of Riverside (hex grid) and cells of Square (square grid, king moves).
        Assert.Equal(4, Result("distance(\"0303\", \"0507\")", "s1").Number);
        Assert.Equal(5, Result("distance(\"0303\", \"0507\")", null).Number);
        Assert.Equal(5, Result("distance(\"0303\", \"0507\")", "r1").Number);
        // b1 is on Delta, which has 0303 but not 0412: both are found on Riverside.
        Assert.Equal(Result("distance(\"0303\", \"0412\")", null).Number, Result("distance(\"0303\", \"0412\")", "b1").Number);
    }

    [Fact]
    public void Distance_other_boards_error() =>
        Assert.Equal("distance(): \"0412\" and \"0201\" are not on the same board", Error("distance(\"0412\", \"0201\")"));

    [Fact]
    public void Distance_unknown_hex()
    {
        Assert.Equal("distance(): no hex named \"9999\" on any board", Error("distance(\"0303\", \"9999\")"));
        Assert.Equal("distance(): no hex named \"9999\" on any board", Error("distance(\"9999\", \"0303\")"));
        Assert.Equal("distance() needs hex names as text", Error("distance(303, \"0303\")"));
    }

    [Fact]
    public void Keyed_piece_property_reads_run_piece()
    {
        Assert.Equal(7, Result("keyed(Cargo, \"barge\")").Number);
        Assert.Equal(4, Result("keyed(Cargo, \"Raft\")", "b1").Number);
        Assert.Equal("keyed() needs a unit", Error("keyed(Cargo, \"Barge\")", null));
    }

    [Fact]
    public void Keyed_holder_property_reads_global()
    {
        Assert.Equal(3, Result("keyed(FordDepth, hex())").Number);
        Assert.Equal(5, Result("keyed(BargesDue, 2)", null).Number);
        Assert.Equal(1, Result("keyed(BargesDue, turn() + 2)", null).Number);
        // The Global holder does not keep HullCount: the read fails as a Global binding of it does.
        Assert.Equal("Global does not keep HullCount", Error("keyed(HullCount, \"Barge\")", null));
        Assert.Equal("Unknown property 'Fog'", Error("keyed(Fog, 1)"));
        Assert.Equal("Strength is not keyed", Error("keyed(Strength, 1)"));
        Assert.Equal("keyed(): no hex named \"9999\" on any board", Error("keyed(FordDepth, \"9999\")"));
        Assert.Equal("keyed(): the turn must be a whole number of at least 1", Error("keyed(BargesDue, 0)"));
    }

    [Fact]
    public void Keyed_with_holder_argument()
    {
        Assert.Equal(5, Result("keyed(HullCount, \"Barge\", Boatyard)", null).Number);
        Assert.Equal(1, Result("keyed(HullCount, \"Raft\", Boatyard)", null).Number);
        Assert.Equal("No holder named 'Dock'", Error("keyed(HullCount, \"Barge\", Dock)"));
        Assert.Equal("Cargo is not a holder property", Error("keyed(Cargo, \"Barge\", Boatyard)"));
    }

    [Fact]
    public void Keyed_missing_key_reads_default()
    {
        Assert.Equal(0, Result("keyed(FordDepth, \"0303\")").Number);
        Assert.Equal(0, Result("keyed(Cargo, \"Keelboat\")").Number);
        Assert.Equal(0, Result("keyed(BargesDue, 7)", null).Number);
    }

    [Fact]
    public void Keyed_in_filter_and_alert_context()
    {
        var s = PlacesKit.State();
        var pack = Pack;
        Assert.True(PieceFilter.Matches("keyed(FordDepth, hex()) == 3", s.Pieces["r1"], s, Model, pack));
        Assert.False(PieceFilter.Matches("keyed(FordDepth, hex()) == 3", s.Pieces["b1"], s, Model, pack));
        Assert.True(PieceFilter.Matches("keyed(Cargo, \"Raft\") == 4 and distance(hex(), \"0304\") == 1", s.Pieces["b1"], s, Model, pack));
        Assert.True(PieceFilter.EvaluateGameLevel("keyed(FordDepth, \"0412\") == 3", s, pack, Model));
        Assert.True(PieceFilter.EvaluateGameLevel("keyed(HullCount, \"Barge\", Boatyard) == 5", s, pack, Model));
        Assert.True(PieceFilter.EvaluateGameLevel("distance(\"0303\", \"0304\") == 1", s, pack, Model));
        Assert.False(PieceFilter.EvaluateGameLevel("hex() == \"0412\"", s, pack, Model));
        // Without a module a hex key is the trimmed text, and distance() is not available.
        Assert.True(PieceFilter.EvaluateGameLevel("keyed(FordDepth, \" 0412\") == 3", s, pack));
        var ctx = new PieceFilter.FilterContext(s.Pieces["r1"], s, pack, PieceFilter.SideMap(s, pack), new AggregateFilters(s, pack, null));
        Assert.Equal("distance() is not available here",
            Assert.Throws<EvalError>(() => Evaluator.Evaluate(Parser.Parse("distance(\"0303\", \"0304\")"), ctx)).Message);
    }

    [Fact]
    public void Keyed_in_phase_condition_global()
    {
        var s = PlacesKit.State();
        var pack = Pack;
        bool Runs(string cond, int turn, out string? error) =>
            PhaseConditions.Runs(pack, s, new PhaseDef { Id = "c", Name = "Barges", Condition = cond }, new PhasePosition(turn, 1, "c", 0), out error, Model);
        Assert.True(Runs("keyed(BargesDue, turn()) > 0", 2, out var e1));
        Assert.Null(e1);
        Assert.False(Runs("keyed(BargesDue, turn()) > 0", 4, out var e2));
        Assert.Null(e2);
        Assert.True(Runs("keyed(HullCount, \"Barge\", Boatyard) == 5", 1, out var e3));
        Assert.Null(e3);
        Assert.False(Runs("keyed(HullCount, \"Raft\", Boatyard) == 5", 1, out var e4));
        Assert.Null(e4);
        // A piece property has no piece here; a bare keyed name is an error (the phase runs, with the notice), never a silent Default.
        Runs("keyed(Cargo, \"Barge\") > 0", 1, out var e5);
        Assert.Equal("keyed() needs a unit", e5);
        Runs("BargesDue > 0", 1, out var e6);
        Assert.Equal("\"BargesDue\" is keyed; read it with keyed()", e6);
    }

    [Fact]
    public void Bare_keyed_name_in_filter_fails()
    {
        var s = PlacesKit.State();
        var pack = Pack;
        var strict = PieceFilter.CompileStrict("Cargo > 0", s, pack, Model);
        Assert.Equal("\"Cargo\" is keyed; read it with keyed()", Assert.Throws<EvalError>(() => strict(s.Pieces["r1"])).Message);
        var global = PieceFilter.CompileStrict("BargesDue > 0", s, pack, Model);
        Assert.Equal("\"BargesDue\" is keyed; read it with keyed()", Assert.Throws<EvalError>(() => global(s.Pieces["r1"])).Message);
        Assert.False(PieceFilter.Matches("Cargo >= 0", s.Pieces["r1"], s, Model, pack));
        Assert.False(PieceFilter.EvaluateGameLevel("BargesDue >= 0", s, pack, Model));
        // holder() of a keyed property fails too
        Assert.Equal("\"HullCount\" is keyed; read it with keyed()", Error("holder(Boatyard, HullCount)", null));
    }

    [Fact]
    public void Binding_reading_keyed_fails()
    {
        var s = PlacesKit.State();
        FormulaDef Bound(BindingKind kind, string prop, string? holder = null) => PlacesKit.F("V") with
        {
            Bindings = new() { new VariableBinding { Variable = "V", Kind = kind, Property = prop, HolderId = holder } },
        };
        Assert.Equal("\"Cargo\" is keyed; read it with keyed()", PlacesKit.Run(s, PlacesKit.Pack(Bound(BindingKind.Unit, "p-cargo")), "r1").Error);
        Assert.Equal("\"FordDepth\" is keyed; read it with keyed()", PlacesKit.Run(s, PlacesKit.Pack(Bound(BindingKind.Global, "p-ford")), "r1").Error);
        Assert.Equal("\"HullCount\" is keyed; read it with keyed()",
            PlacesKit.Run(s, PlacesKit.Pack(Bound(BindingKind.Holder, "p-hulls", "yard")), "r1").Error);
    }

    [Fact]
    public void Aggregate_over_keyed_fails()
    {
        Assert.Equal("sum() cannot total keyed property \"Cargo\"", Error("sum(Cargo, \"\")"));
        Assert.Equal("hexmax() cannot total keyed property \"Cargo\"", Error("hexmax(Cargo)"));
        // a filter may read a keyed value of each piece
        Assert.Equal(1, Result("countof(\"keyed(Cargo, \\\"Barge\\\") == 7\")", null).Number);
    }

    // ---------- tables ----------

    [Fact]
    public void KeyedTable_for_table_keyed_lists_every_row()
    {
        var s = PlacesKit.State();
        var rows = KeyedTable.For(s, Pack, PlacesKit.Yard);
        Assert.Equal(new[] { "BargesDue 1 0 False", "HullCount Barge 5 True", "HullCount Raft 1 False", "HullCount Keelboat 1 False" },
            rows.Select(r => $"{r.Property.Name} {r.Key} {r.Value} {r.Stored}"));
        var cargo = KeyedTable.For(s, Pack, LedgerKey.Piece("r1"));
        Assert.Equal(new[] { "Cargo Barge 7 True", "Cargo Raft 0 False", "Cargo Keelboat 0 False" },
            cargo.Select(r => $"{r.Property.Name} {r.Key} {r.Value} {r.Stored}"));
    }

    [Fact]
    public void KeyedTable_for_turn_keyed_stored_and_current()
    {
        var s = PlacesKit.State();
        PlacesKit.Put(s, PlacesKit.Global, "p-due", "10", 2);
        s.Apply(new SetTurn(4, null));
        var rows = KeyedTable.For(s, Pack, PlacesKit.Global);
        // HullCount is not kept by Global; places have their own tab.
        Assert.Equal(new[] { "2 5 True", "3 1 True", "4 0 False", "10 2 True" }, rows.Select(r => $"{r.Key} {r.Value} {r.Stored}"));
        Assert.All(rows, r => Assert.Equal("p-due", r.Property.Id));
        Assert.Empty(KeyedTable.For(s, Pack with { Properties = Pack.Properties.Where(p => p.KeyedBy == null).ToList() }, PlacesKit.Global));
    }

    [Fact]
    public void KeyedTable_places_sorted_and_filtered()
    {
        var s = PlacesKit.State();
        PlacesKit.Put(s, PlacesKit.Global, "p-ford", "0303", 1);
        var all = KeyedTable.Places(s, Pack, "");
        Assert.Equal(new[] { "FordDepth", "Mines" }, all.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "0303", "0412", "0505" }, all.Rows.Select(r => r.Hex));
        Assert.Equal(new double?[] { 3, null }, all.Rows[1].Values);
        Assert.Equal(new double?[] { null, 2 }, all.Rows[2].Values);
        Assert.Equal(new[] { "0412" }, KeyedTable.Places(s, Pack, " 41 ").Rows.Select(r => r.Hex));
    }

    [Fact]
    public void PlaceText_with_labels()
    {
        var s = PlacesKit.State();
        Assert.Equal("FordDepth 3 (Flooded)", KeyedValues.PlaceText(Pack, KeyedValues.PlacesAt(s, Pack, "0412")));
        PlacesKit.Put(s, PlacesKit.Global, "p-mines", "0412", 2);
        Assert.Equal("FordDepth 3 (Flooded) · Mines 2", KeyedValues.PlaceText(Pack, KeyedValues.PlacesAt(s, Pack, "0412")));
        Assert.Empty(KeyedValues.PlacesAt(s, Pack, "0303"));
        Assert.Equal("", KeyedValues.PlaceText(Pack, KeyedValues.PlacesAt(s, Pack, "0303")));
    }

    [Fact]
    public void DropCommand_only_when_a_past_key_exists()
    {
        var s = PlacesKit.State();
        Assert.Null(KeyedValues.DropCommand(s, Pack, 2));
        var drop = KeyedValues.DropCommand(s, Pack, 3)!;
        Assert.Equal(new[] { "p-due" }, drop.PropertyIds);
        Assert.Equal(3, drop.Turn);
        Assert.Null(KeyedValues.DropCommand(new GameState(), Pack, 9));
    }
}
