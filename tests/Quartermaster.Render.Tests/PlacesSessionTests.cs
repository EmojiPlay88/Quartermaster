using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>B6 in a session: turn keys dropped on phase entry, local keyed edits, the Places tab, the map status place text and the keyed
/// panel model. Invented Riverland / Red-Blue names only.</summary>
public class PlacesSessionTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-places-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();
    private static readonly IReadOnlySet<string> Blue = new HashSet<string> { "blue" };
    private const string Global = "h:global";

    public PlacesSessionTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static readonly PropertyDef Due = new()
    {
        Id = "due", Name = "Barges due", Scope = PropertyScope.Holders, KeyedBy = "turn", Visibility = Visibility.Public,
    };

    /// <summary>One phase per turn (each Next Phase starts a new turn); <paramref name="keyed"/> adds the turn-keyed "Barges due".</summary>
    private static RulesPack Pack(string sha, bool keyed)
    {
        var p = PhaseFixture.Pack(sha);
        return p with
        {
            Format = 4,
            Properties = keyed ? p.Properties.Append(Due).ToList() : p.Properties,
            Phases = new() { new PhaseDef { Id = "ph-orders", Name = "Orders" } },
            Blocks = new(),
        };
    }

    private (ModuleSession S, PhaseController C, LocalActions A) Open(bool keyed = true)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto, new[] { new PlayerDef("p1", "Ann", new[] { "red" }), new PlayerDef("p2", "Bob", new[] { "blue" }) });
        s.NewRules("t");
        s.UpdateRules(_ => Pack(s.Archive.Sha256, keyed));
        Func<GameCommand, GameState, GameCommand> prepare = (c, st) => CommandLog.Prepare(c, st, s);
        var c = new PhaseController(s, prepare);
        return (s, c, new LocalActions(s, c, new HistoryController(s, c, prepare), prepare));
    }

    private static void PutDue(ModuleSession s, string turn, double v) =>
        s.Game!.Execute(new LedgerDelta(Global, "due", 0, v, "manual", null, null, "test", null) { ValueKey = turn });

    private static IReadOnlyList<string> DueKeys(GameState st) => KeyedValues.Keys(st, Global, "due");

    // ---------- turn keys dropped on phase entry ----------

    [Fact]
    public void Turn_keys_dropped_on_entry_and_undo()
    {
        var (s, c, _) = Open();
        c.Start();      // turn 1
        c.Advance();    // turn 2
        PutDue(s, "1", 4);
        PutDue(s, "2", 5);
        PutDue(s, "3", 6);
        var mark = s.Game!.Log.Count;
        c.Advance();    // into turn 3: keys 1 and 2 go
        Assert.Equal(3, s.Game.State.Turn.Turn);
        Assert.Equal(new[] { "3" }, DueKeys(s.Game.State));
        var added = s.Game.Log.Skip(mark).ToList();
        var adv = added.FindIndex(x => x is AdvancePhase);
        var drop = added.FindIndex(x => x is DropPastTurns);
        Assert.True(adv >= 0 && drop > adv, $"{adv} {drop}");
        var cmd = (DropPastTurns)added[drop];
        Assert.Equal(new[] { "due" }, cmd.PropertyIds);
        Assert.Equal(3, cmd.Turn);
        Assert.Equal("Next Phase", s.Game.UndoLabel);

        s.Game.Undo();  // one undo: the advance and the drop together
        Assert.Equal(2, s.Game.State.Turn.Turn);
        Assert.Equal(new[] { "1", "2", "3" }, DueKeys(s.Game.State));
        Assert.Equal(6, KeyedValues.Get(s.Game.State, Global, "due", "3"));
    }

    [Fact]
    public void Next_phase_in_the_same_turn_drops_nothing_twice()
    {
        var (s, c, _) = Open();
        c.Start();
        c.Advance();    // turn 2
        PutDue(s, "1", 4);
        c.Advance();    // turn 3: drops 1
        c.Advance();    // turn 4: nothing below 4 is stored, no command
        Assert.Single(s.Game!.Log.OfType<DropPastTurns>());
    }

    [Fact]
    public void No_drop_command_without_turn_keyed_properties()
    {
        var (s, c, _) = Open(keyed: false);
        c.Start();
        c.Advance();
        c.Advance();
        Assert.DoesNotContain(s.Game!.Log, x => x is DropPastTurns);
        Assert.Null(s.Game.State.Keyed);
    }

    [Fact]
    public void Guest_session_never_drops()
    {
        var (host, hc, _) = Open();
        hc.Start();
        hc.Advance();   // turn 2
        PutDue(host, "1", 4);
        PutDue(host, "2", 5);
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var guest = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "gcache"), Path.Combine(_tmp, "groot"));
        _sessions.Add(guest);
        guest.EnterGuestMode();
        guest.LoadGuestRules(host.Rules!);
        guest.ApplySnapshot(PlayerView.For(host.Game!.State, host.Rules, host.Model, Blue), Blue);
        Assert.Equal(new[] { "1", "2" }, DueKeys(guest.Game!.State));
        var gc = new PhaseController(guest, (cmd, _) => cmd);
        gc.Advance();   // even a guest's own controller entering turn 3 leaves the keys to the host
        Assert.Equal(3, guest.Game.State.Turn.Turn);
        Assert.DoesNotContain(guest.Game.Log, x => x is DropPastTurns);
        Assert.Equal(new[] { "1", "2" }, DueKeys(guest.Game.State));
    }

    // ---------- local keyed edits ----------

    [Fact]
    public void Local_edit_keyed_writes_with_working()
    {
        var (s, _, a) = Open();
        a.EditKeyed(Global, "due", " 2.0 ", 5, " convoy ");   // the key is normalised: turn 2
        Assert.Equal(5, KeyedValues.Get(s.Game!.State, Global, "due", "2"));
        var d = Assert.IsType<LedgerDelta>(s.Game.Log[^1]);
        Assert.Equal("2", d.ValueKey);
        Assert.Equal("manual", d.Source);
        Assert.Equal("convoy", d.Note);
        Assert.Equal("Barges due[2] 0 → 5", d.Working);
        Assert.Null(Ledger.Get(s.Game.State, Global, "due"));   // never in Ledgers
        a.EditKeyed(Global, "due", "2", 7.5, "more");
        Assert.Equal("Barges due[2] 5 → 7.5", ((LedgerDelta)s.Game.Log[^1]).Working);
    }

    [Fact]
    public void Local_edit_keyed_bad_key_ignored()
    {
        var (s, _, a) = Open();
        var count = s.Game!.Log.Count;
        a.EditKeyed(Global, "due", "soon", 5, "n");         // not a turn
        a.EditKeyed(Global, "due", "0", 5, "n");            // below turn 1
        a.EditKeyed(Global, "due", new string('1', 65), 5, "n");   // too long
        a.EditKeyed(Global, "water", "2", 5, "n");          // not keyed
        a.EditKeyed(Global, "nope", "2", 5, "n");           // unknown
        a.EditKeyed(Global, "due", "2", double.NaN, "n");   // not a number
        Assert.Equal(count, s.Game.Log.Count);
        Assert.Null(s.Game.State.Keyed);
    }

    [Fact]
    public void Local_edit_on_keyed_property_through_edit_ledger_ignored()
    {
        var (s, _, a) = Open();
        var count = s.Game!.Log.Count;
        a.EditLedger(Global, "due", 5, "n");   // an unkeyed write of a keyed property never lands in Ledgers
        Assert.Equal(count, s.Game.Log.Count);
        Assert.Null(Ledger.Get(s.Game.State, Global, "due"));
    }

    [Fact]
    public void Local_edit_keyed_unchanged_no_history()
    {
        var (s, _, a) = Open();
        a.EditKeyed(Global, "due", "2", 5, "n");
        var history = s.Game!.State.LedgerHistory.Count;
        var log = s.Game.Log.Count;
        a.EditKeyed(Global, "due", "2", 5, "again");
        Assert.Equal(history, s.Game.State.LedgerHistory.Count);
        Assert.Equal(log, s.Game.Log.Count);
    }

    [Fact]
    public void Old_port_default_does_nothing()
    {
        var port = new OldPort();
        ((IActionPort)port).EditKeyed(Global, "due", "2", 5, "n");
        Assert.Equal(0, port.Calls);
    }

    [Fact]
    public void Port_switch_forwards_edit_keyed()
    {
        var (s, _, a) = Open();
        new ActionPortSwitch(a).EditKeyed(Global, "due", "3", 2, "n");
        Assert.Equal(2, KeyedValues.Get(s.Game!.State, Global, "due", "3"));
    }

    // ---------- ledger tabs ----------

    private static RulesPack Riverland => PlacesFixture.Pack();

    [Fact]
    public void Tabs_with_pack_adds_places_after_everything()
    {
        var user = new[] { new UserLedgerTab("Boats", null, Array.Empty<string>(), false, null) };
        var tabs = LedgerTabs.Tabs(user, Riverland);
        Assert.Equal(new[] { "units", "holders", "all", "places", "user:Boats" }, tabs.Select(t => t.Key));
        var places = tabs[3];
        Assert.Equal("Places", places.Title);
        Assert.Equal(LedgerTabKind.Places, places.Kind);
        Assert.Null(places.User);
        var rows = LedgerTabs.Rows(places, new GameState(), Riverland, PlacesFixture.Model, "", null, LedgerGrouping.None, true);
        Assert.Empty(rows.Columns);
        Assert.Empty(rows.Rows);
        Assert.Equal(0, rows.Count);
        Assert.Null(rows.FilterError);
    }

    [Fact]
    public void Tabs_without_place_property_unchanged()
    {
        var pack = Riverland with { Properties = Riverland.Properties.Where(p => !KeyedFacts.IsPlace(p)).ToList() };
        var user = new[] { new UserLedgerTab("Boats", null, Array.Empty<string>(), false, null) };
        Assert.Equal(LedgerTabs.Tabs(user), LedgerTabs.Tabs(user, pack));
        Assert.Equal(LedgerTabs.Tabs(user), LedgerTabs.Tabs(user, null!));
    }

    [Fact]
    public void Old_Tabs_overload_unchanged()
    {
        var tabs = LedgerTabs.Tabs(Array.Empty<UserLedgerTab>());
        Assert.Equal(new[] { "units", "holders", "all" }, tabs.Select(t => t.Key));
        Assert.DoesNotContain(tabs, t => t.Kind == LedgerTabKind.Places);
        Assert.Equal(4, (int)LedgerTabKind.Places);   // appended after User
    }

    // ---------- map status text ----------

    private static GameState RiverState()
    {
        var s = new GameState { GameId = "g1" };
        void Put(string prop, string hex, double v) =>
            s.Apply(new LedgerDelta(Global, prop, 0, v, "manual", null, null, null, null) { ValueKey = hex });
        Put("p-ford", "0412", 3);
        Put("p-mines", "0412", 2);
        Put("p-ford", "0505", 1);
        return s;
    }

    private static MapDef North => PlacesFixture.Model.Maps.Single(m => m.Id == PlacesFixture.MapOf("Riverside"));

    [Fact]
    public void Status_location_appends_place_text()
    {
        var at = PlacesFixture.At("Riverside", "0412");
        var plain = MapStatusText.Location(North, at);
        var text = MapStatusText.Location(North, at, MapStatusText.PlaceText(RiverState(), Riverland));
        Assert.Equal(plain + "  ·  FordDepth 3 (Flooded) · Mines 2", text);
        Assert.EndsWith("  0412  ·  FordDepth 3 (Flooded) · Mines 2", text);
        Assert.Equal("0412: FordDepth 3 (Flooded) · Mines 2", MapStatusText.Tooltip("0412", MapStatusText.PlaceText(RiverState(), Riverland)));
    }

    [Fact]
    public void Status_location_unchanged_without_places()
    {
        var at = PlacesFixture.At("Riverside", "0101");
        var plain = MapStatusText.Location(North, at);
        Assert.Equal(plain, MapStatusText.Location(North, at, MapStatusText.PlaceText(RiverState(), Riverland)));   // no place values there
        Assert.Equal(plain, MapStatusText.Location(North, at, null));
        var noPlaces = Riverland with { Properties = Riverland.Properties.Where(p => !KeyedFacts.IsPlace(p)).ToList() };
        Assert.Null(MapStatusText.PlaceText(RiverState(), noPlaces));   // a pack without places pays nothing
        Assert.Null(MapStatusText.PlaceText(null, Riverland));
        Assert.Equal(MapStatusText.Location(North, PlacesFixture.At("Riverside", "0412")),
            MapStatusText.Location(North, PlacesFixture.At("Riverside", "0412"), _ => null));
        Assert.Null(MapStatusText.Tooltip("0101", MapStatusText.PlaceText(RiverState(), Riverland)));
        // off every board: only the coordinates
        Assert.Equal("5000, 5000", MapStatusText.Location(North, new PointD(5000, 5000), _ => "FordDepth 1"));
    }

    [Fact]
    public void Status_place_text_from_view_hides_private()
    {
        var view = PlayerView.For(RiverState(), Riverland, PlacesFixture.Model, Blue);
        var at = PlacesFixture.At("Riverside", "0412");
        var text = MapStatusText.Location(North, at, MapStatusText.PlaceText(view, Riverland));
        Assert.EndsWith("  0412  ·  FordDepth 3 (Flooded)", text);
        Assert.DoesNotContain("Mines", text);
    }

    // ---------- keyed panel and Places models over a view ----------

    private static GameState BoatState()
    {
        var s = RiverState();
        PieceInstance Unit(string id, string name, string side) => new()
        {
            Id = id, MapId = PlacesFixture.MapOf("Riverside"), Pos = PlacesFixture.At("Riverside", "0303"),
            TypeString = PieceTypeParser.EncodeState(new[] { "mark;Side", "piece;;;unit.svg;" + name }), TraitStates = new() { side, "" },
        };
        s.Pieces["r1"] = Unit("r1", "Red Barge", "Red");
        s.Pieces["b1"] = Unit("b1", "Blue Raft", "Blue");
        void Put(string key, string prop, string vk, double v) =>
            s.Apply(new LedgerDelta(key, prop, 0, v, "manual", null, null, null, null) { ValueKey = vk });
        Put(LedgerKey.Piece("r1"), "p-cargo", "Barge", 7);
        Put(LedgerKey.Piece("b1"), "p-cargo", "Raft", 4);
        Put("h:yard", "p-hulls", "Barge", 5);
        Put("h:blueyard", "p-hulls", "Raft", 3);
        return s;
    }

    [Fact]
    public void Keyed_panel_masks_values_the_view_lacks()
    {
        var view = PlayerView.For(BoatState(), Riverland, PlacesFixture.Model, Blue);
        // Blue's own piece: its stored value, the other keys at the Default.
        var own = LedgerViewRules.KeyedRows(view, Riverland, LedgerKey.Piece("b1"));
        Assert.Equal(new[] { "Barge", "Raft", "Keelboat" }, own.Select(r => r.Key));
        Assert.Equal(new double?[] { 0, 4, 0 }, own.Select(r => r.Value));
        Assert.All(own, r => Assert.False(r.Hidden));
        // Red's piece, Private Cargo: no value shown at all, never the Default.
        var other = LedgerViewRules.KeyedRows(view, Riverland, LedgerKey.Piece("r1"));
        Assert.Equal(3, other.Count);
        Assert.All(other, r => { Assert.True(r.Hidden); Assert.Null(r.Value); Assert.False(r.Stored); });
        Assert.Equal("hidden", LedgerViewRules.KeyedValueText(other[0]));
        // A public holder: the stored row and the Default (1) for the others.
        var yard = LedgerViewRules.KeyedRows(view, Riverland, "h:yard").Where(r => r.Property.Id == "p-hulls").ToList();
        Assert.Equal(new double?[] { 5, 1, 1 }, yard.Select(r => r.Value));
        Assert.Equal(new[] { true, false, false }, yard.Select(r => r.Stored));
        Assert.Equal("5", LedgerViewRules.KeyedValueText(yard[0]));
        // The full state shows everything.
        var full = LedgerViewRules.KeyedRows(BoatState(), Riverland, LedgerKey.Piece("r1"));
        Assert.Equal(new double?[] { 7, 0, 0 }, full.Select(r => r.Value));
    }

    [Fact]
    public void Keyed_panel_masks_a_holder_of_a_side_not_held()
    {
        var red = new HashSet<string> { "red" };
        // B7 #74a: another side's holder hides only its non-Public values (here HullCount made Private; Barges due stays Public)
        var pack = Riverland with { Properties = Riverland.Properties.Select(p => p.Id == "p-hulls" ? p with { Visibility = Visibility.Private } : p).ToList() };
        var view = PlayerView.For(BoatState(), pack, PlacesFixture.Model, red);
        var rows = LedgerViewRules.KeyedRows(view, pack, "h:blueyard");
        Assert.Equal(4, rows.Count);   // HullCount's three rows and Barges due at the current turn
        Assert.All(rows.Where(r => r.Property.Id == "p-hulls"), r => { Assert.True(r.Hidden); Assert.Null(r.Value); });
        Assert.All(rows.Where(r => r.Property.Id != "p-hulls"), r => Assert.False(r.Hidden));
        Assert.All(LedgerViewRules.KeyedRows(PlayerView.For(BoatState(), Riverland, PlacesFixture.Model, red), Riverland, "h:blueyard"),
            r => Assert.False(r.Hidden));   // all Public: every row shown
        Assert.All(LedgerViewRules.KeyedRows(PlayerView.For(BoatState(), Riverland, PlacesFixture.Model, Blue), Riverland, "h:blueyard"),
            r => Assert.False(r.Hidden));
    }

    [Fact]
    public void Places_model_leaves_out_private_columns_in_a_view()
    {
        var full = LedgerViewRules.Places(RiverState(), Riverland, "");
        Assert.Equal(new[] { "FordDepth", "Mines" }, full.Columns.Select(c => c.Name));
        var view = LedgerViewRules.Places(PlayerView.For(RiverState(), Riverland, PlacesFixture.Model, Blue), Riverland, "");
        Assert.Equal(new[] { "FordDepth" }, view.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "0412", "0505" }, view.Rows.Select(r => r.Hex));
        Assert.Equal(new double?[] { 3 }, view.Rows[0].Values);
    }

    /// <summary>A port written before keyed edits: it has no EditKeyed of its own.</summary>
    private sealed class OldPort : IActionPort
    {
        public int Calls;
        public bool IsRemote => false;
        public void Move(IReadOnlyList<MovePieces> moves) => Calls++;
        public void PieceCommand(GameCommand command) => Calls++;
        public void PieceCommands(IReadOnlyList<GameCommand> commands) => Calls++;
        public FormulaVerdict ApplyFormula(FormulaIntent intent) { Calls++; return FormulaVerdict.Refused(""); }
        public Task<FormulaVerdict> ApplyFormulaAsync(FormulaIntent intent) { Calls++; return Task.FromResult(FormulaVerdict.Refused("")); }
        public bool ApplyTrace(string? runId, string? pieceId, FormulaTrace trace, bool calculated) { Calls++; return false; }
        public (int Applied, IReadOnlyList<string> Failed) ApplyMissingInputs(PendingRun run, IReadOnlyDictionary<string, string> values)
        { Calls++; return (0, Array.Empty<string>()); }
        public void EditLedger(string key, string propertyId, double after, string note) => Calls++;
        public void Roll(DiceSpec spec, string purpose) => Calls++;
        public void RecordRoll(RollDice roll) => Calls++;
        public void Tick(int index, bool ticked) => Calls++;
        public void Ack(int warningIndex) => Calls++;
        public string? NextPhase() { Calls++; return null; }
        public void HideAlert(string alertId, string? name) => Calls++;
        public void Chat(string text) => Calls++;
        public void SetTurn(int turn, string? phase) => Calls++;
        public void SetNotes(string text) => Calls++;
        public void Undo() => Calls++;
        public void Redo() => Calls++;
        public void StepBack(CheckpointRow row) => Calls++;
    }
}
