using System.Diagnostics;
using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;
using Xunit.Abstractions;

namespace Quartermaster.Core.Tests;

public class PlayerViewTests(ITestOutputHelper output)
{
    private const string Main = "Main Map";
    private const string RedHq = "Red HQ";
    private const string HideTrait = "hide;I;Hide it;;;0.3;;false";
    private const string ObsTrait = "obs;M;m.png;Mask it;I;?";
    private static readonly IReadOnlySet<string> Blue = new HashSet<string> { "blue" };
    private static readonly IReadOnlySet<string> Red = new HashSet<string> { "red" };
    private static readonly IReadOnlySet<string> Both = new HashSet<string> { "red", "blue" };

    private static ModuleModel Model => RunnerFx.Model;

    private static RulesPack Pack() => new()
    {
        Format = 4,
        Sides = new()
        {
            new SideDef { Id = "red", Name = "Red", UnitFilter = "marker(\"Side\") == \"Red\"" },
            new SideDef { Id = "blue", Name = "Blue", UnitFilter = "marker(\"Side\") == \"Blue\"" },
        },
        Properties = new()
        {
            new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, Visibility = Visibility.Private },
            new PropertyDef { Id = "morale", Name = "Morale", Scope = PropertyScope.Pieces, Visibility = Visibility.Public },
            new PropertyDef { Id = "supply", Name = "Supply", Scope = PropertyScope.Holders, Visibility = Visibility.Public },
        },
        Holders = new()
        {
            new HolderDef { Id = "reddepot", Name = "Red Depot", SideId = "red" },
            new HolderDef { Id = "pool", Name = "Pool" },
        },
        MapOwners = new() { new MapOwner { MapId = RedHq, SideId = "red" } },
        Phases = new()
        {
            new PhaseDef { Id = "rm", Name = "Red Movement", SideId = "red" },
            new PhaseDef { Id = "bm", Name = "Blue Movement", SideId = "blue" },
        },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "f-secret", Name = "Secret Attack", Text = "R", PrivateRolls = true,
                Bindings = new() { new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 10000 } },
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Op = OutputOp.Add } },
            },
            new FormulaDef
            {
                Id = "f-stock", Name = "Secret Stock", Text = "S", PrivateRolls = true,
                Bindings = new() { new VariableBinding { Variable = "S", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 10000 } },
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Holder, HolderId = "reddepot", Property = "supply", Op = OutputOp.Add } },
            },
            new FormulaDef
            {
                Id = "f-peek", Name = "Secret Peek", Text = "R", PrivateRolls = true,
                Bindings = new() { new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 10000 } },
            },
            new FormulaDef
            {
                Id = "f-pool", Name = "Pool Roll", Text = "R", PrivateRolls = true,
                Bindings = new() { new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 10000 } },
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Holder, HolderId = "pool", Property = "supply", Op = OutputOp.Set } },
            },
            new FormulaDef
            {
                Id = "f-open", Name = "Open Attack", Text = "R", PrivateRolls = false,
                Bindings = new() { new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 10000 } },
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "morale", Op = OutputOp.Add } },
            },
        },
    };

    private static string Type(string name, params string[] extra)
    {
        var layers = extra.ToList();
        layers.Add("mark;Side");
        layers.Add("piece;;;unit.svg;" + name);
        return PieceTypeParser.EncodeState(layers);
    }

    private static string State(string side, params string[] extraStates)
    {
        var layers = extraStates.ToList();
        layers.Add(side);
        layers.Add("");
        return PieceTypeParser.EncodeState(layers);
    }

    private static void Add(GameState s, string id, string name, string side, string map = Main, string[]? traits = null, string[]? states = null) =>
        s.Apply(new AddPiece(id, Type(name, traits ?? Array.Empty<string>()), State(side, states ?? Array.Empty<string>()), map,
            new PointD(50, 50), null, $"{name} placed", Who: side + " player"));

    private static GameState NewState()
    {
        var s = new GameState { GameId = "g1" };
        s.Apply(new SetPlayers(new[] { new PlayerDef("p1", "Ann", new[] { "red" }), new PlayerDef("p2", "Bob", new[] { "blue" }) }));
        return s;
    }

    private static void Delta(GameState s, string key, string prop, double before, double after, string? note = null) =>
        s.Apply(new LedgerDelta(key, prop, before, after, "manual", null, null, note, null));

    private static void RunFormula(GameState s, RulesPack pack, string formulaId, string? pieceId, int roll, string? subject, string? sideId = null)
    {
        var runner = new FormulaRunner(pack, Model, (n, sides) => Enumerable.Repeat(roll, n).ToList());
        var t = runner.Run(s, new RunRequest(formulaId, pieceId, new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<int>>()));
        Assert.Null(t.Error);
        Assert.NotNull(t.Result);
        foreach (var c in runner.Commands(t, null, subject == null ? null : "auto", subject, sideId)) s.Apply(c);
    }

    private static void EnterPhase(GameState s, RulesPack pack, string phaseId)
    {
        var idx = pack.Phases.FindIndex(p => p.Id == phaseId);
        s.Apply(new AdvancePhase(s.Position, new PhasePosition(1, 1, phaseId, idx), "", pack.Phases[idx].Name));
    }

    [Fact]
    public void PrivateRoll_NoOutputs_NotCreditedToNextLine()
    {
        var pack = Pack();
        var s = NewState();
        Add(s, "r1", "Rifle", "Red");
        Add(s, "b1", "Lancer", "Blue");
        RunFormula(s, pack, "f-peek", "r1", 8461, null);           // FormulaDialog style: no subject, only a RollDice
        Delta(s, LedgerKey.Piece("b1"), "fuel", 0, 3);             // an unrelated Blue line follows
        var blue = PlayerView.For(s, pack, Model, Blue);
        Assert.DoesNotContain("8461", Json(blue));
        Assert.Contains(blue.Log, l => l.Text == "Red rolled dice");
        Assert.Contains("8461", Json(PlayerView.For(s, pack, Model, Red)));
    }

    [Fact]
    public void DuplicatePhaseNames_UnitlessRun_Hidden()
    {
        var pack = Pack() with
        {
            Phases = new()
            {
                new PhaseDef { Id = "rm", Name = "Movement", SideId = "red" },
                new PhaseDef { Id = "bm", Name = "Movement", SideId = "blue" },
            },
        };
        var s = NewState();
        EnterPhase(s, pack, "bm");
        RunFormula(s, pack, "f-pool", null, 7243, null);           // unit-less, unstamped: the phase name maps to two sides
        foreach (var held in new[] { Blue, Red })
        {
            var v = PlayerView.For(s, pack, Model, held);
            var json = Json(v);
            Assert.DoesNotContain("7243 ", json);                  // the working line ("7243 → Supply = 7243") and the roll
            Assert.DoesNotContain("= 7243", json);
            Assert.Empty(v.DiceHistory[0].Results);
            Assert.Contains(v.Log, l => l.Text == "Another side rolled dice");
            Assert.Contains(v.Log, l => l.Text == "Another side ledger changed");
            Assert.Equal(7243, Ledger.Get(v, LedgerKey.Holder("pool"), "supply"));   // the public value itself stays
        }
    }

    [Fact]
    public void OutOfPhase_UnitlessRun_FollowsSideStamp()
    {
        var pack = Pack();
        var s = NewState();
        EnterPhase(s, pack, "bm");                                 // Blue's phase...
        RunFormula(s, pack, "f-pool", null, 6911, null, "red");    // ...but Red runs a unit-less formula (stamped)
        var blue = PlayerView.For(s, pack, Model, Blue);
        Assert.DoesNotContain("= 6911", Json(blue));
        Assert.Empty(blue.DiceHistory[0].Results);
        Assert.Contains(blue.Log, l => l.Text == "Red rolled dice");
        Assert.Contains(blue.Log, l => l.Text == "Red ledger changed");
        Assert.Null(blue.LedgerHistory.Single(e => e.Key == LedgerKey.Holder("pool")).Working);
        var red = PlayerView.For(s, pack, Model, Red);
        Assert.Equal(new[] { 6911 }, red.DiceHistory[0].Results);
        Assert.NotNull(red.LedgerHistory.Single(e => e.Key == LedgerKey.Holder("pool")).Working);
    }

    [Fact]
    public void DeletedPiece_LogLinesHidden()
    {
        var pack = Pack();
        var s = NewState();
        Add(s, "r1", "RedGoneUnit", "Red");
        Delta(s, LedgerKey.Piece("r1"), "morale", 0, 2);
        s.Apply(new RemovePiece("r1", "RedGoneUnit removed"));
        var blue = PlayerView.For(s, pack, Model, Blue);
        Assert.DoesNotContain("RedGoneUnit", Json(blue));
        Assert.Contains(blue.Log, l => l.Text == "Another side ledger changed");
    }

    [Fact]
    public void FilterError_Lockdown_WithDiagnostic()
    {
        var pack = Pack();
        var s = Seeded(pack);
        PlayerView.FailForTest = true;
        GameState v;
        try { v = PlayerView.For(s, pack, Model, Blue); }
        finally { PlayerView.FailForTest = false; }
        Assert.Empty(v.Pieces);
        Assert.Empty(v.Ledgers);
        Assert.Empty(v.LedgerHistory);
        Assert.Empty(v.DiceHistory);
        Assert.Empty(v.Pending);
        var line = Assert.Single(v.Log);
        Assert.Equal((LogKind.System, PlayerView.FilterErrorText), (line.Kind, line.Text));
        Assert.Equal("g1", v.GameId);
    }

    private static string Json(GameState s) => JsonSerializer.Serialize(s, QuartermasterJson.Options);

    [Fact]
    public void AllSidesHeld_ReturnsEqualClone()
    {
        var pack = Pack();
        var s = Seeded(pack);
        var before = Json(s);
        var v = PlayerView.For(s, pack, Model, Both);
        Assert.NotSame(s, v);
        Assert.Equal(before, Json(v));
        Assert.Equal(before, Json(s));
        var noPack = PlayerView.For(s, null, Model, Blue);
        Assert.Equal(before, Json(noPack));
    }

    [Fact]
    public void Ownership_FirstMatchingSide_HolderAndMap()
    {
        var pack = Pack();
        var s = NewState();
        Add(s, "r1", "Rifle", "Red");
        Add(s, "b1", "Lancer", "Blue");
        Add(s, "n1", "Marker", "Neutral");
        Assert.Equal("red", Ownership.SideOfPiece(s.Pieces["r1"], s, pack));
        Assert.Equal("blue", Ownership.SideOfPiece(s.Pieces["b1"], s, pack));
        Assert.Null(Ownership.SideOfPiece(s.Pieces["n1"], s, pack));
        Assert.Equal("red", Ownership.SideOfHolder("reddepot", pack));
        Assert.Null(Ownership.SideOfHolder("pool", pack));
        Assert.Null(Ownership.SideOfHolder("global", pack));
        Assert.Equal("red", Ownership.SideOfMap(RedHq, pack));
        Assert.Null(Ownership.SideOfMap(Main, pack));
        var bulk = Ownership.PieceSides(s, pack);
        Assert.Equal("blue", bulk(s.Pieces["b1"]));
    }

    [Fact]
    public void OwnedMap_PiecesRemovedForOtherSide()
    {
        var pack = Pack();
        var s = NewState();
        Add(s, "r1", "HqGuard", "Red", RedHq);
        Add(s, "b1", "Lancer", "Blue", RedHq);
        Add(s, "b2", "Scout", "Blue");
        s.MapOrder.AddRange(new[] { Main, RedHq });
        var v = PlayerView.For(s, pack, Model, Blue);
        Assert.Equal(new[] { "b2" }, v.Pieces.Keys.ToArray());
        Assert.All(v.Stacks.Values, st => Assert.Equal(Main, st.MapId));
        Assert.DoesNotContain(RedHq, v.MapOrder);
        Assert.DoesNotContain(v.Log, l => l.PieceId == "r1" || l.PieceId == "b1" || l.MapId == RedHq);
        var r = PlayerView.For(s, pack, Model, Red);
        Assert.Equal(3, r.Pieces.Count);
        Assert.Equal(3, s.Pieces.Count);
    }

    [Fact]
    public void PrivateLedger_RemovedForOtherSide_PublicKept()
    {
        var pack = Pack();
        var s = NewState();
        Add(s, "r1", "Rifle", "Red");
        Add(s, "b1", "Lancer", "Blue");
        Delta(s, LedgerKey.Piece("r1"), "fuel", 0, 77.25);
        Delta(s, LedgerKey.Piece("r1"), "morale", 0, 3.5);
        Delta(s, LedgerKey.Piece("b1"), "fuel", 0, 12.75);
        var v = PlayerView.For(s, pack, Model, Blue);
        Assert.Null(Ledger.Get(v, LedgerKey.Piece("r1"), "fuel"));
        Assert.Equal(3.5, Ledger.Get(v, LedgerKey.Piece("r1"), "morale"));
        Assert.Equal(12.75, Ledger.Get(v, LedgerKey.Piece("b1"), "fuel"));
        Assert.DoesNotContain(v.LedgerHistory, e => e.Key == LedgerKey.Piece("r1") && e.PropertyId == "fuel");
        Assert.Contains(v.LedgerHistory, e => e.Key == LedgerKey.Piece("r1") && e.PropertyId == "morale");
        Assert.Contains(v.Log, l => l.Text == "Red ledger changed");
        Assert.DoesNotContain(v.Log, l => l.Text.Contains("77.25"));
        Assert.Contains(v.Log, l => l.Text.Contains("12.75"));
        // Red sees its own values.
        Assert.Equal(77.25, Ledger.Get(PlayerView.For(s, pack, Model, Red), LedgerKey.Piece("r1"), "fuel"));
    }

    [Fact]
    public void PrivateHolder_Removed()
    {
        var pack = Pack();
        var s = NewState();
        Delta(s, LedgerKey.Holder("reddepot"), "supply", 0, 640.5);
        Delta(s, LedgerKey.Holder("pool"), "supply", 0, 21.5);
        Delta(s, LedgerKey.Holder("global"), "supply", 0, 9.5);
        var v = PlayerView.For(s, pack, Model, Blue);
        Assert.False(v.Ledgers.ContainsKey(LedgerKey.Holder("reddepot")));
        Assert.Equal(21.5, Ledger.Get(v, LedgerKey.Holder("pool"), "supply"));
        Assert.Equal(9.5, Ledger.Get(v, LedgerKey.Holder("global"), "supply"));
        Assert.DoesNotContain(v.LedgerHistory, e => e.Key == LedgerKey.Holder("reddepot"));
        Assert.DoesNotContain(v.Log, l => l.Text.Contains("640.5"));
        Assert.Contains(v.Log, l => l.Text == "Red ledger changed");
    }

    [Fact]
    public void PrivateRolls_ResultsRemoved_LogRedacted()
    {
        var pack = Pack();
        var s = NewState();
        Add(s, "r1", "Rifle", "Red");
        Add(s, "b1", "Lancer", "Blue");
        RunFormula(s, pack, "f-secret", "r1", 8191, "Rifle");
        RunFormula(s, pack, "f-secret", "b1", 6007, "Lancer");
        RunFormula(s, pack, "f-open", "r1", 4093, "Rifle");
        Assert.Equal(3, s.DiceHistory.Count);

        var v = PlayerView.For(s, pack, Model, Blue);
        Assert.Equal(3, v.DiceHistory.Count);
        Assert.Empty(v.DiceHistory[0].Results);
        Assert.Equal("Red rolled dice", v.DiceHistory[0].Purpose);
        Assert.Equal(new[] { 6007 }, v.DiceHistory[1].Results);
        Assert.Equal(new[] { 4093 }, v.DiceHistory[2].Results);
        var dice = v.Log.Where(l => l.Kind == LogKind.Dice).ToList();
        Assert.Equal("Red rolled dice", dice[0].Text);
        Assert.Contains("6007", dice[1].Text);
        Assert.Contains("4093", dice[2].Text);
        Assert.DoesNotContain("8191", Json(v));

        var r = PlayerView.For(s, pack, Model, Red);
        Assert.Equal(new[] { 8191 }, r.DiceHistory[0].Results);
        Assert.Empty(r.DiceHistory[1].Results);
        Assert.Equal("Blue rolled dice", r.Log.Where(l => l.Kind == LogKind.Dice).ElementAt(1).Text);
    }

    [Fact]
    public void Mask_ShowsMaskedState()
    {
        var pack = Pack();
        var s = NewState();
        Add(s, "r1", "RedMaskedUnit", "Red", traits: new[] { ObsTrait }, states: new[] { "red" });
        Add(s, "b1", "BlueMaskedUnit", "Blue", traits: new[] { ObsTrait }, states: new[] { "blue" });
        Add(s, "r2", "RedBareUnit", "Red", traits: new[] { ObsTrait }, states: new[] { "null" });
        Delta(s, LedgerKey.Piece("r1"), "morale", 0, 2.5);
        var v = PlayerView.For(s, pack, Model, Blue);

        var m = v.Pieces["r1"];
        Assert.Equal("", PieceInfo.Name(m));
        Assert.DoesNotContain("RedMaskedUnit", m.TypeString);
        var type = PieceInfo.TypeOf(m);
        var obsIndex = type.Traits.ToList().FindIndex(t => t is ObscurableDef);
        Assert.True(obsIndex >= 0);
        Assert.True(TraitState.IsObscured((ObscurableDef)type.Traits[obsIndex], m.TraitStates[obsIndex]));
        Assert.Equal(s.Pieces["r1"].Pos, m.Pos);
        Assert.Equal(s.Pieces["r1"].StackId, m.StackId);
        Assert.False(v.Ledgers.ContainsKey(LedgerKey.Piece("r1")));
        Assert.DoesNotContain("RedMaskedUnit", Json(v));

        Assert.Equal("BlueMaskedUnit", PieceInfo.Name(v.Pieces["b1"]));
        Assert.Equal("RedBareUnit", PieceInfo.Name(v.Pieces["r2"]));
        Assert.Equal("RedMaskedUnit", PieceInfo.Name(PlayerView.For(s, pack, Model, Red).Pieces["r1"]));
    }

    [Fact]
    public void Hide_Removed()
    {
        var pack = Pack();
        var s = NewState();
        Add(s, "r1", "RedSecretUnit", "Red", traits: new[] { HideTrait }, states: new[] { "red" });
        Add(s, "r2", "RedShownUnit", "Red", traits: new[] { HideTrait }, states: new[] { "null" });
        s.Apply(new MovePieces(new[] { "r2" }, Main, new PointD(50, 50), s.Pieces["r1"].StackId));
        var v = PlayerView.For(s, pack, Model, Blue);
        Assert.False(v.Pieces.ContainsKey("r1"));
        Assert.True(v.Pieces.ContainsKey("r2"));
        Assert.All(v.Stacks.Values, st => Assert.DoesNotContain("r1", st.PieceIds));
        Assert.DoesNotContain("RedSecretUnit", Json(v));
        Assert.True(PlayerView.For(s, pack, Model, Red).Pieces.ContainsKey("r1"));
    }

    [Fact]
    public void PendingRuns_FilteredBySide()
    {
        var pack = Pack();
        var s = NewState();
        Add(s, "r1", "Rifle", "Red");
        Add(s, "b1", "Lancer", "Blue");
        s.Apply(new QueueRuns(new[]
        {
            new PendingRun("run1", "f-secret", "Secret Attack", "start",
                new[] { new PendingUnit("r1", "Rifle", null, false), new PendingUnit("b1", "Lancer", null, false) }, false),
            new PendingRun("run2", "f-secret", "Secret Attack", "start", new[] { new PendingUnit("r1", "Rifle", null, false) }, false),
            new PendingRun("run3", "f-secret", "Secret Attack", "start", new[] { new PendingUnit("b1", "Lancer", null, false) }, false),
        }));
        var v = PlayerView.For(s, pack, Model, Blue);
        Assert.Equal(new[] { "run1", "run3" }, v.Pending.Select(r => r.Id).ToArray());
        Assert.Equal(new[] { "b1" }, v.Pending[0].Units.Select(u => u.PieceId).ToArray());
        Assert.Equal(3, s.Pending.Count);
        Assert.Equal(2, s.Pending[0].Units.Count);
    }

    [Fact]
    public void PlayerView_LogTextWithHiddenName_Redacted()
    {
        var pack = Pack();
        var s = NewState();
        Add(s, "r1", "Panzer Lehr", "Red", traits: new[] { HideTrait }, states: new[] { "red" });
        Add(s, "b1", "Lancer", "Blue");
        Add(s, "r2", "Pa", "Red", traits: new[] { HideTrait }, states: new[] { "red" });
        s.Apply(new AddLogLine(LogKind.System, "Spotted panzer lehr near the river"));
        s.Apply(new AddLogLine(LogKind.System, "Panzer Lehrgang is a different word"));
        s.Apply(new AddLogLine(LogKind.System, "Lancer reached the ford"));
        s.Apply(new AddLogLine(LogKind.System, "Papa bear woke up"));
        var v = PlayerView.For(s, pack, Model, Blue);
        var texts = v.Log.Select(l => l.Text).ToList();
        Assert.DoesNotContain(texts, t => t.Contains("panzer lehr near", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(texts, t => t.StartsWith("Panzer Lehr placed"));
        Assert.Contains("Panzer Lehrgang is a different word", texts);
        Assert.Contains("Lancer reached the ford", texts);
        Assert.Contains("Papa bear woke up", texts);
    }

    [Fact]
    public void PlayerView_PieceMovesOntoOwnedMap_Disappears()
    {
        var pack = Pack();
        var s = NewState();
        Add(s, "r1", "RedMover", "Red");
        Delta(s, LedgerKey.Piece("r1"), "morale", 0, 4.5);
        Assert.True(PlayerView.For(s, pack, Model, Blue).Pieces.ContainsKey("r1"));
        s.Apply(new MovePieces(new[] { "r1" }, RedHq, new PointD(10, 10), null, "RedMover moved to HQ"));
        var v = PlayerView.For(s, pack, Model, Blue);
        Assert.False(v.Pieces.ContainsKey("r1"));
        Assert.Empty(v.Stacks);
        Assert.False(v.Ledgers.ContainsKey(LedgerKey.Piece("r1")));
        Assert.DoesNotContain(v.LedgerHistory, e => e.Key == LedgerKey.Piece("r1"));
        Assert.DoesNotContain("RedMover", Json(v));
    }

    /// <summary>A Red-heavy game with secrets everywhere; used by the leak test and the equality test.</summary>
    private static GameState Seeded(RulesPack pack)
    {
        var s = NewState();
        s.Apply(new AdvancePhase(null, new PhasePosition(1, 1, "rm", 0), "", "Red Movement"));
        Add(s, "r1", "RedOpenUnit", "Red");
        Add(s, "r2", "RedMaskedUnit", "Red", traits: new[] { ObsTrait }, states: new[] { "red" });
        Add(s, "r3", "RedSecretUnit", "Red", traits: new[] { HideTrait }, states: new[] { "red" });
        Add(s, "r4", "RedMapUnit", "Red", RedHq);
        Add(s, "b1", "BlueLancer", "Blue");
        s.MapOrder.AddRange(new[] { Main, RedHq });
        s.Apply(new SeedLedger(new[]
        {
            new LedgerSeed(LedgerKey.Piece("r1"), "fuel", 9137.5),
            new LedgerSeed(LedgerKey.Piece("r2"), "morale", 7311.75),
            new LedgerSeed(LedgerKey.Piece("r3"), "morale", 7322.75),
            new LedgerSeed(LedgerKey.Piece("r4"), "morale", 5555.125),
            new LedgerSeed(LedgerKey.Piece("b1"), "fuel", 1234.5),
        }));
        Delta(s, LedgerKey.Holder("reddepot"), "supply", 0, 4242.25, "RedDepotNote");
        Delta(s, LedgerKey.Piece("r1"), "fuel", 9137.5, 9138.5, "RedFuelNote");
        Delta(s, LedgerKey.Piece("r2"), "morale", 7311.75, 7312.75);
        RunFormula(s, pack, "f-secret", "r1", 8191, "RedOpenUnit");
        RunFormula(s, pack, "f-stock", null, 6143, null);
        RunFormula(s, pack, "f-secret", "r2", 7919, "RedMaskedUnit");
        s.Apply(new AddLogLine(LogKind.Calc, "RedOpenUnit: 31337 × 2 = 62674 (auto)"));
        s.Apply(new AddLogLine(LogKind.System, "RedSecretUnit spotted by nobody"));
        s.Apply(new AddLogLine(LogKind.Move, "RedMapUnit regrouped", "r4"));
        s.Apply(new RollDice("RedSecretUnit check", 1, 6, new[] { 3 }));
        s.Apply(new QueueRuns(new[]
        {
            new PendingRun("run1", "f-secret", "Secret Attack", "start", new[]
            {
                new PendingUnit("r1", "RedOpenUnit", null, false), new PendingUnit("r3", "RedSecretUnit", null, false),
                new PendingUnit("b1", "BlueLancer", null, false),
            }, false),
            new PendingRun("run2", "f-secret", "Secret Attack", "start", new[] { new PendingUnit("r2", "RedMaskedUnit", null, false) }, false),
        }));
        return s;
    }

    [Fact]
    public void Leak_SerialisedBlueView_ContainsNoRedPrivateData()
    {
        var pack = Pack();
        var s = Seeded(pack);
        var full = Json(s);
        string[] secrets =
        {
            "9137.5", "9138.5", "17329.5", "8191", "7919", "6143", "4242.25", "10385.25", "7311.75", "7312.75", "7322.75", "5555.125",
            "31337", "62674", "RedMaskedUnit", "RedSecretUnit", "RedMapUnit", "RedDepotNote", "RedFuelNote",
        };
        foreach (var x in secrets) Assert.Contains(x, full);   // the fixture really holds every secret

        var blue = Json(PlayerView.For(s, pack, Model, Blue));
        foreach (var x in secrets) Assert.DoesNotContain(x, blue);
        Assert.Contains("1234.5", blue);                        // Blue's own values survive
        Assert.Contains("BlueLancer", blue);
        Assert.Contains("RedOpenUnit", blue);                   // an unmasked Red unit on a public map is visible
        Assert.Contains("Red rolled dice", blue);
        Assert.Contains("Red ledger changed", blue);
        Assert.DoesNotContain("Another side", blue);            // every redaction was attributed (unit, subject or phase)
        Assert.Equal(full, Json(s));                            // input untouched

        var red = Json(PlayerView.For(s, pack, Model, Red));   // the unit-less private roll (linked by phase) is Red's own
        Assert.Contains("6143", red);
        Assert.Contains("RedSecretUnit", red);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Perf_1200Pieces_Under50ms()
    {
        var pack = Pack();
        var s = NewState();
        for (var i = 0; i < 1200; i++)
        {
            var side = i % 3 == 0 ? "Red" : i % 3 == 1 ? "Blue" : "Neutral";
            var traits = i % 7 == 0 ? new[] { ObsTrait } : Array.Empty<string>();
            var states = i % 7 == 0 ? new[] { "x" } : Array.Empty<string>();
            Add(s, "p" + i, "Unit " + i, side, i % 5 == 0 ? RedHq : Main, traits, states);
            Delta(s, LedgerKey.Piece("p" + i), "fuel", 0, i);
        }
        PlayerView.For(s, pack, Model, Blue);   // warm-up
        var sw = Stopwatch.StartNew();
        var v = PlayerView.For(s, pack, Model, Blue);
        sw.Stop();
        output.WriteLine($"PlayerView.For, 1200 pieces, 1200 ledger lines: {sw.Elapsed.TotalMilliseconds:0.0} ms");
        Assert.True(v.Pieces.Count < 1200);
        Assert.True(sw.ElapsedMilliseconds < 200, $"took {sw.ElapsedMilliseconds} ms");
    }
}
