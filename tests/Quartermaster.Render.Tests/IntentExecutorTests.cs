using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>The host executes guest intents with the guest's sides (never the host's view). Invented Red/Blue only.</summary>
public class IntentExecutorTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-intent-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    private const double SecretStock = 61616;
    private const string RedMap = "Red Command Post";
    private static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });
    private static readonly PlayerDef Bob = new("p2", "Bob", new[] { "blue" });
    private static readonly IReadOnlySet<string> Red = new HashSet<string> { "red" };
    private static readonly IReadOnlySet<string> Blue = new HashSet<string> { "blue" };

    public IntentExecutorTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static RulesPack Pack(string sha)
    {
        var p = PhaseFixture.Pack(sha);
        return p with
        {
            Format = 4,
            Properties = p.Properties.Concat(new[]
            {
                new PropertyDef { Id = "supply", Name = "Supply", Scope = PropertyScope.Holders, Visibility = Visibility.Private },
                new PropertyDef { Id = "morale", Name = "Morale", Scope = PropertyScope.Holders },
            }).ToList(),
            Holders = new() { new HolderDef { Id = "reddepot", Name = "Red Depot", SideId = "red" } },
            MapOwners = new() { new MapOwner { MapId = RedMap, SideId = "red" } },
            // Name decides the side of the fixture units; a "Side" marker decides it for the marked test pieces.
            Sides = new()
            {
                new SideDef { Id = "red", Name = "Red", UnitFilter = "startsWith(name(), \"Red\") or marker(\"Side\") == \"Red\"" },
                new SideDef { Id = "blue", Name = "Blue", UnitFilter = "startsWith(name(), \"Blue\") or marker(\"Side\") == \"Blue\"" },
            },
            Formulas = p.Formulas.Concat(new[]
            {
                new FormulaDef
                {
                    Id = "f-roll", Name = "Rolled water", Text = "D",
                    Bindings = new() { new VariableBinding { Variable = "D", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 } },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
                },
                new FormulaDef
                {
                    Id = "f-glob", Name = "Morale water", Text = "W + G",
                    Bindings = new()
                    {
                        new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" },
                        new VariableBinding { Variable = "G", Kind = BindingKind.Global, Property = "morale" },
                    },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
                },
                new FormulaDef
                {
                    Id = "f-depot", Name = "Fill depot", Text = "W",
                    Bindings = new() { new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" } },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Holder, HolderId = "reddepot", Property = "supply", Op = OutputOp.Set } },
                },
                new FormulaDef
                {
                    Id = "f-gout", Name = "Raise morale", Text = "W",
                    Bindings = new() { new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" } },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "morale", Op = OutputOp.Set } },
                },
                new FormulaDef
                {
                    Id = "f-mix", Name = "Mixed supply", Text = "W + S",
                    Bindings = new()
                    {
                        new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" },
                        new VariableBinding { Variable = "S", Kind = BindingKind.Holder, HolderId = "reddepot", Property = "supply" },
                    },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
                },
            }).ToList(),
            Phases = p.Phases.Select(x => x.Id == "ph-combat" ? x with { SideId = "blue" } : x).ToList(),
        };
    }

    private (ModuleSession S, PhaseController C, IntentExecutor X) Open(Difficulty difficulty = Difficulty.FullyAuto, double stock = SecretStock)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(4, n).ToList());
        s.LoadSetup(s.Model.Setups[0], difficulty, new[] { Alice, Bob });
        s.NewRules("t");
        s.UpdateRules(_ => Pack(s.Archive.Sha256));
        s.Game!.Execute(new LedgerDelta(LedgerKey.Holder("reddepot"), "supply", 0, stock, "manual", null, null, null, null));
        var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        return (s, c, new IntentExecutor(s, c));
    }

    private static string Json(GameState? st) => JsonSerializer.Serialize(st, QuartermasterJson.Options);

    [Fact]
    public void Intent_OtherSidesUnit_Refused_StateUnchanged()
    {
        var (s, _, x) = Open();
        var g = s.Game!;
        var r1 = g.State.Pieces[PhaseFixture.RedOne];
        var b1 = g.State.Pieces[PhaseFixture.BlueOne];
        var before = Json(g.State);
        var log = g.Log.Count;

        var intents = new GameIntent[]
        {
            new MoveIntent(new[] { new MovePieces(new[] { PhaseFixture.RedOne }, r1.MapId, new PointD(150, 50), null) }),
            new MoveIntent(new[] { new MovePieces(new[] { PhaseFixture.BlueOne, PhaseFixture.RedOne }, r1.MapId, new PointD(150, 50), null) }),
            new MoveIntent(new[] { new MovePieces(new[] { PhaseFixture.BlueOne }, r1.MapId, r1.Pos, r1.StackId) }),   // stacks onto Red
            new PieceCommandIntent(new RemovePiece(PhaseFixture.RedOne)),
            new PieceCommandIntent(new ToggleTrail(PhaseFixture.RedOne)),
            new FormulaIntent("f-water", PhaseFixture.RedOne, null, new Dictionary<string, string>(), true),
            new LedgerEditIntent(LedgerKey.Piece(PhaseFixture.RedOne), "water", 1, "drained"),
            new LedgerEditIntent(LedgerKey.Holder("reddepot"), "supply", 1, "drained"),
        };
        foreach (var i in intents)
        {
            var r = x.Execute("p2", Blue, i);
            Assert.False(r.Ok, i.GetType().Name);
            Assert.False(string.IsNullOrWhiteSpace(r.Message), i.GetType().Name);
        }
        Assert.Equal(before, Json(g.State));
        Assert.Equal(log, g.Log.Count);

        // The same kinds of intent on Bob's own unit are accepted, though the host's own view is public-only.
        Assert.Empty(s.ViewSides!);
        var ok = x.Execute("p2", Blue, new MoveIntent(new[] { new MovePieces(new[] { PhaseFixture.BlueOne }, b1.MapId, new PointD(150, 50), null) }));
        Assert.True(ok.Ok, ok.Message);
        Assert.Equal(new PointD(150, 50), g.State.Pieces[PhaseFixture.BlueOne].Pos);
        Assert.Equal("p2", g.State.Log.Last().Who);
        var edit = x.Execute("p2", Blue, new LedgerEditIntent(LedgerKey.Piece(PhaseFixture.BlueOne), "water", 4, "filled"));
        Assert.True(edit.Ok, edit.Message);
        Assert.Equal(4, Ledger.Get(g.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
    }

    [Fact]
    public void Intent_NextPhaseOutOfPhase_Refused()
    {
        var (s, c, x) = Open();
        c.Start(); c.Advance();   // Supply (red)
        var g = s.Game!;
        var before = Json(g.State);
        foreach (var i in new GameIntent[] { new NextPhaseIntent(), new TickIntent(0, true), new AckIntent(0) })
        {
            var r = x.Execute("p2", Blue, i);
            Assert.False(r.Ok, i.GetType().Name);
            Assert.Contains("Red", r.Message);
        }
        Assert.Equal(before, Json(g.State));

        var ok = x.Execute("p1", Red, new NextPhaseIntent());
        Assert.True(ok.Ok, ok.Message);
        Assert.Equal("ph-move", g.State.Position!.PhaseId);
    }

    [Fact]
    public void Intent_HostOnly_Refused()
    {
        var (s, _, x) = Open();
        var g = s.Game!;
        var before = Json(g.State);
        var intents = new GameIntent[]
        {
            new PieceCommandIntent(new SteppedBack(0, 1, 0, "game start")),
            new PieceCommandIntent(new SetTurn(5, null)),
            new PieceCommandIntent(new SetDifficulty(Difficulty.Hardcore)),
            new PieceCommandIntent(new SetNotes("mine now")),
            new PieceCommandIntent(new SetPlayers(new[] { Alice with { Sides = new[] { "red", "blue" } } })),
            new PieceCommandIntent(new LedgerDelta(LedgerKey.Piece(PhaseFixture.BlueOne), "water", 10, 99, "manual", null, null, "x", null)),
            new PieceCommandIntent(new RollDice("Rigged", 1, 6, new[] { 6 })),
            new HideAlertIntent("builtin:neg:water", "Negative water"),
        };
        foreach (var i in intents)
        {
            var r = x.Execute("p2", Blue, i);
            Assert.False(r.Ok, i.GetType().Name);
            Assert.False(string.IsNullOrWhiteSpace(r.Message));
        }
        Assert.Equal(before, Json(g.State));
        Assert.False(x.Execute("p2", Blue, null!).Ok);   // never throws
    }

    [Fact]
    public void FormulaIntent_HardcoreTyped_WrongBoxReported_NotApplied()
    {
        var (s, _, x) = Open(Difficulty.Hardcore);
        var g = s.Game!;
        var before = Json(g.State);
        var r = x.Execute("p2", Blue, new FormulaIntent("f-mix", PhaseFixture.BlueOne, null,
            new Dictionary<string, string> { ["W"] = "999", ["S"] = "1" }, false));
        Assert.False(r.Ok);
        Assert.NotNull(r.Verdict);
        Assert.False(r.Verdict!.Applied);
        Assert.Equal(FormulaBox.Wrong, r.Verdict.Boxes.Single(b => b.Var == "W").State);
        Assert.Equal(FormulaBox.Hidden, r.Verdict.Boxes.Single(b => b.Var == "S").State);
        Assert.Equal(before, Json(g.State));
        var wire = JsonSerializer.Serialize(r, QuartermasterJson.Options);
        Assert.DoesNotContain(SecretStock.ToString(System.Globalization.CultureInfo.InvariantCulture), wire);
        Assert.DoesNotContain("61", r.Message ?? "");

        // Typing the right visible value applies, with the hidden one filled by the host.
        var ok = x.Execute("p2", Blue, new FormulaIntent("f-mix", PhaseFixture.BlueOne, null, new Dictionary<string, string> { ["W"] = "10" }, false));
        Assert.True(ok.Ok, ok.Message);
        Assert.True(ok.Verdict!.Applied);
        Assert.Equal(10 + SecretStock, Ledger.Get(g.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
    }

    [Fact]
    public void FormulaIntent_Calculate_Applies_StampsGuestSide()
    {
        var (s, _, x) = Open(Difficulty.Hardcore, stock: 5);
        var g = s.Game!;
        Assert.Null(s.ActorSide());   // the host holds nothing in its own view
        var r = x.Execute("p2", Blue, new FormulaIntent("f-mix", PhaseFixture.BlueOne, null, new Dictionary<string, string>(), true));
        Assert.True(r.Ok, r.Message);
        Assert.True(r.Verdict!.Applied);
        Assert.Equal(15, Ledger.Get(g.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
        var delta = g.Log.OfType<LedgerDelta>().Last();
        Assert.Equal("blue", delta.SideId);
        Assert.Equal("p2", delta.Who);
        Assert.Equal(FormulaBox.Hidden, r.Verdict.Boxes.Single(b => b.Var == "S").State);
    }

    [Fact]
    public void DiceIntent_RolledByHost()
    {
        var (s, _, x) = Open();
        var r = x.Execute("p1", Red, new DiceIntent(new DiceSpec(2, 6, 0), "Attack"));
        Assert.True(r.Ok, r.Message);
        var roll = s.Game!.Log.OfType<RollDice>().Last();
        Assert.Equal(new[] { 4, 4 }, roll.Results);
        Assert.Equal("p1", roll.Who);
        Assert.Equal("Attack: 2d6 = 4 + 4 = 8", s.Game.State.Log.Last().Text);
        Assert.False(x.Execute("p1", Red, new DiceIntent(new DiceSpec(0, 6, 0), "Bad")).Ok);
        Assert.False(x.Execute("p1", Red, new DiceIntent(new DiceSpec(1, 1, 0), "Bad")).Ok);
    }

    [Fact]
    public void ChatIntent_LoggedAsChat()
    {
        var (s, _, x) = Open();
        var r = x.Execute("p2", Blue, new ChatIntent("hello Red"));
        Assert.True(r.Ok, r.Message);
        var line = s.Game!.State.Log.Last();
        Assert.Equal(LogKind.Chat, line.Kind);
        Assert.Equal("Bob: hello Red", line.Text);
        // Chat is public: the other side sees it.
        var red = PlayerView.For(s.Game.State, s.Rules, s.Model, Red);
        Assert.Contains(red.Log, l => l.Kind == LogKind.Chat && l.Text == "Bob: hello Red");
        Assert.False(x.Execute("p2", Blue, new ChatIntent("   ")).Ok);
    }

    [Fact]
    public void GameIntent_RoundTripsAsJson()
    {
        var intents = new GameIntent[]
        {
            new MoveIntent(new[] { new MovePieces(new[] { "a" }, "Main Map", new PointD(1, 2), null) }),
            new PieceCommandIntent(new ToggleTrail("a")),
            new FormulaIntent("f", "a", "run", new Dictionary<string, string> { ["X"] = "1" }, true),
            new LedgerEditIntent("p:a", "water", 3, "n"),
            new DiceIntent(new DiceSpec(2, 6, 1), "Roll"),
            new TickIntent(1, true), new AckIntent(2), new NextPhaseIntent(), new HideAlertIntent("x", null), new ChatIntent("hi"),
        };
        foreach (var i in intents)
        {
            var json = JsonSerializer.Serialize<GameIntent>(i, QuartermasterJson.Options);
            Assert.Contains("\"$t\"", json);
            var back = JsonSerializer.Deserialize<GameIntent>(json, QuartermasterJson.Options);
            Assert.Equal(i.GetType(), back!.GetType());
            Assert.Equal(json, JsonSerializer.Serialize(back, QuartermasterJson.Options));
        }
        var verdict = new FormulaVerdict(false, new[] { new FormulaBox("W", FormulaBox.Wrong) }, "Doesn't match: W");
        var vj = JsonSerializer.Serialize(verdict, QuartermasterJson.Options);
        Assert.Equal(vj, JsonSerializer.Serialize(JsonSerializer.Deserialize<FormulaVerdict>(vj, QuartermasterJson.Options), QuartermasterJson.Options));
    }

    // ---- fix round 1: hardening ----

    private static string MarkedType(string name) => PieceTypeParser.EncodeState(new[] { "mark;Side", "piece;;;unit.svg;" + name });
    private static string MarkedState(string side) => PieceTypeParser.EncodeState(new[] { side, "" });

    [Theory]
    [InlineData(Difficulty.Hardcore)]
    [InlineData(Difficulty.SemiAuto)]
    [InlineData(Difficulty.FullyAuto)]
    public void FormulaIntent_TypedRollOrOverride_Ignored(Difficulty mode)
    {
        var (s, _, x) = Open(mode, stock: 5);
        var g = s.Game!;
        var key = LedgerKey.Piece(PhaseFixture.BlueOne);
        var r = x.Execute("p2", Blue, new FormulaIntent("f-roll", PhaseFixture.BlueOne, null, new Dictionary<string, string> { ["D"] = "6" }, false));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(4, Ledger.Get(g.State, key, "water"));   // the host's roll, not the guest's 6
        Assert.Equal(4, g.Log.OfType<RollDice>().Last().Results.Single());

        if (mode != Difficulty.FullyAuto) return;
        g.Execute(new LedgerDelta(LedgerKey.Holder(HolderDef.GlobalId), "morale", 0, 3, "manual", null, null, null, null));
        var o = x.Execute("p2", Blue, new FormulaIntent("f-glob", PhaseFixture.BlueOne, null,
            new Dictionary<string, string> { ["W"] = "999", ["G"] = "100" }, false));
        Assert.True(o.Ok, o.Message);
        Assert.Equal(7, Ledger.Get(g.State, key, "water"));   // 4 + 3: Unit and Global overrides ignored
    }

    [Fact]
    public void SetTraitState_BadIndexOrHugeState_Refused_HostAlive()
    {
        var (s, _, x) = Open();
        var g = s.Game!;
        var before = Json(g.State);
        foreach (var t in new[]
        {
            new SetTraitState(PhaseFixture.BlueOne, int.MaxValue, "x"),
            new SetTraitState(PhaseFixture.BlueOne, -1, "x"),
            new SetTraitState(PhaseFixture.BlueOne, 2, "x"),
            new SetTraitState(PhaseFixture.BlueOne, 0, new string('9', 5000)),
            new SetTraitState(new string('b', 300), 0, "1"),
        })
            Assert.False(x.Execute("p2", Blue, new PieceCommandIntent(t)).Ok);
        Assert.Equal(before, Json(g.State));
        Assert.True(x.Execute("p2", Blue, new PieceCommandIntent(new SetTraitState(PhaseFixture.BlueOne, 0, "41"))).Ok);
    }

    [Fact]
    public void SetTraitState_SideChange_Refused()
    {
        var (s, _, x) = Open();
        var g = s.Game!;
        g.Execute(new AddPiece("m1", MarkedType("Flag"), MarkedState("Blue"), "Main Map", new PointD(150, 50), null));
        var before = Json(g.State);
        Assert.False(x.Execute("p2", Blue, new PieceCommandIntent(new SetTraitState("m1", 0, "Red"))).Ok);     // to another side
        Assert.False(x.Execute("p2", Blue, new PieceCommandIntent(new SetTraitState("m1", 0, "Green"))).Ok);   // to no side
        Assert.False(x.Execute("p1", Red, new PieceCommandIntent(new SetTraitState("m1", 0, "Red"))).Ok);      // not Alice's piece
        Assert.Equal(before, Json(g.State));
        Assert.True(x.Execute("p2", Blue, new PieceCommandIntent(new SetTraitState("m1", 0, "Blue"))).Ok);
    }

    [Fact]
    public void AddPiece_SideFromDecodedState_AndForgedStampsReplaced()
    {
        var (s, _, x) = Open();
        var g = s.Game!;
        var before = Json(g.State);
        Assert.False(x.Execute("p2", Blue, new PieceCommandIntent(
            new AddPiece("m2", MarkedType("Flag"), MarkedState("Red"), "Main Map", new PointD(150, 50), null))).Ok);
        Assert.False(x.Execute("p2", Blue, new PieceCommandIntent(
            new AddPiece("m2", MarkedType("Flag"), MarkedState("Blue"), RedMap, new PointD(150, 50), null))).Ok);
        Assert.Equal(before, Json(g.State));

        var forgedAt = new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ok = x.Execute("p2", Blue, new PieceCommandIntent(new AddPiece("m2", MarkedType("Flag"), MarkedState("Blue"), "Main Map",
            new PointD(150, 50), null, "Alice placed a forged piece", forgedAt, "p1")));
        Assert.True(ok.Ok, ok.Message);
        var line = g.State.Log.Last(l => l.Kind == LogKind.Place);
        Assert.Equal("p2", line.Who);
        Assert.NotEqual(forgedAt, line.At);
        Assert.DoesNotContain("forged", line.Text);
        Assert.True(x.Execute("p2", Blue, new PieceCommandIntent(
            new AddPiece("m3", MarkedType("Crate"), MarkedState("Green"), "Main Map", new PointD(160, 50), null))).Ok);   // side-less is fine
    }

    [Fact]
    public void Move_ForgedStampsReplaced()
    {
        var (s, _, x) = Open();
        var g = s.Game!;
        var forgedAt = new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var r = x.Execute("p2", Blue, new MoveIntent(new[]
        {
            new MovePieces(new[] { PhaseFixture.BlueOne }, "Main Map", new PointD(150, 50), null, "Alice moved forged", forgedAt, "p1"),
        }));
        Assert.True(r.Ok, r.Message);
        var line = g.State.Log.Last(l => l.Kind == LogKind.Move);
        Assert.Equal("p2", line.Who);
        Assert.NotEqual(forgedAt, line.At);
        Assert.DoesNotContain("forged", line.Text);
    }

    [Fact]
    public void FormulaIntent_GuestOutputsAndUnitlessRuns_NeedHeldSide()
    {
        var (s, c, x) = Open();
        var g = s.Game!;
        var before = Json(g.State);
        Assert.False(x.Execute("p2", Blue, new FormulaIntent("f-depot", PhaseFixture.BlueOne, null, new Dictionary<string, string>(), true)).Ok);
        Assert.False(x.Execute("p2", Blue, new FormulaIntent("f-gout", PhaseFixture.BlueOne, null, new Dictionary<string, string>(), true)).Ok);
        Assert.False(x.Execute("p2", Blue, new FormulaIntent("f-weather", null, null, new Dictionary<string, string>(), true)).Ok);
        Assert.Equal(before, Json(g.State));

        c.Start(); c.Advance(); c.Advance(); c.Advance();   // Combat (blue)
        Assert.Equal("ph-combat", g.State.Position!.PhaseId);
        Assert.False(x.Execute("p1", Red, new FormulaIntent("f-gout", PhaseFixture.RedOne, null, new Dictionary<string, string>(), true)).Ok);
        Assert.False(x.Execute("p2", Blue, new FormulaIntent("f-depot", PhaseFixture.BlueOne, null, new Dictionary<string, string>(), true)).Ok);
        var gout = x.Execute("p2", Blue, new FormulaIntent("f-gout", PhaseFixture.BlueOne, null, new Dictionary<string, string>(), true));
        Assert.True(gout.Ok, gout.Message);
        Assert.Equal(Ledger.GetOrDefault(g.State, LedgerKey.Piece(PhaseFixture.BlueOne), s.Rules!.Properties.First(p => p.Id == "water")),
            Ledger.Get(g.State, LedgerKey.Holder(HolderDef.GlobalId), "morale"));
        var weather = x.Execute("p2", Blue, new FormulaIntent("f-weather", null, null, new Dictionary<string, string>(), true));
        Assert.True(weather.Ok, weather.Message);
    }

    [Fact]
    public void HiddenPieces_AreNonexistent_ToTheGuest()
    {
        var (s, _, x) = Open();
        var g = s.Game!;
        g.Execute(new AddPiece("secret", PieceTypeParser.EncodeState(new[] { "piece;;;unit.svg;Red Phantom" }), "", RedMap, new PointD(10, 10), null));
        var before = Json(g.State);
        var missing = x.Execute("p2", Blue, new PieceCommandIntent(new RemovePiece("nope")));
        var hidden = x.Execute("p2", Blue, new PieceCommandIntent(new RemovePiece("secret")));
        Assert.False(hidden.Ok);
        Assert.Equal(missing.Message, hidden.Message);
        var moveMissing = x.Execute("p2", Blue, new MoveIntent(new[] { new MovePieces(new[] { "nope" }, "Main Map", new PointD(1, 1), null) }));
        var moveHidden = x.Execute("p2", Blue, new MoveIntent(new[] { new MovePieces(new[] { "secret" }, "Main Map", new PointD(1, 1), null) }));
        Assert.Equal(moveMissing.Message, moveHidden.Message);
        Assert.Equal(x.Execute("p2", Blue, new FormulaIntent("f-water", "nope", null, new Dictionary<string, string>(), true)).Message,
            x.Execute("p2", Blue, new FormulaIntent("f-water", "secret", null, new Dictionary<string, string>(), true)).Message);
        var hiddenMap = x.Execute("p2", Blue, new PieceCommandIntent(new ClearMoved(RedMap)));
        var mixedMap = x.Execute("p2", Blue, new PieceCommandIntent(new ClearMoved("Main Map")));   // holds Red units
        Assert.Equal(IntentExecutor.MapText, hiddenMap.Message);
        Assert.Equal(IntentExecutor.MapText, mixedMap.Message);
        Assert.Equal(before, Json(g.State));
    }

    [Fact]
    public void ToggleTrail_FromGuest_Refused()
    {
        var (s, _, x) = Open();
        var before = Json(s.Game!.State);
        Assert.False(x.Execute("p2", Blue, new PieceCommandIntent(new ToggleTrail(PhaseFixture.BlueOne))).Ok);
        Assert.Equal(before, Json(s.Game.State));
    }

    [Fact]
    public void GuestStrings_AreCapped()
    {
        var (s, _, x) = Open();
        var before = Json(s.Game!.State);
        var many = Enumerable.Range(0, 65).ToDictionary(i => "V" + i, _ => "1");
        Assert.False(x.Execute("p2", Blue, new FormulaIntent("f-water", PhaseFixture.BlueOne, null, many, false)).Ok);
        Assert.False(x.Execute("p2", Blue, new FormulaIntent("f-water", PhaseFixture.BlueOne, null,
            new Dictionary<string, string> { ["W"] = new string('1', 300) }, false)).Ok);
        Assert.False(x.Execute("p2", Blue, new FormulaIntent("f-water", new string('b', 300), null, new Dictionary<string, string>(), true)).Ok);
        Assert.False(x.Execute("p2", Blue, new PieceCommandIntent(
            new AddPiece(new string('p', 300), MarkedType("Flag"), MarkedState("Blue"), "Main Map", new PointD(1, 1), null))).Ok);
        Assert.False(x.Execute("p2", Blue, new PieceCommandIntent(
            new AddPiece("big", MarkedType(new string('F', 20000)), MarkedState("Blue"), "Main Map", new PointD(1, 1), null))).Ok);
        Assert.Equal(before, Json(s.Game.State));
    }

    [Fact]
    public void ChatText_StripsControlAndSeparators()
    {
        Assert.Equal("a b c d", ChatText.Clean("a" + (char)0x2028 + "b" + (char)0x85 + "c" + (char)9 + "d" + (char)7));
        Assert.Equal("x  y", ChatText.Clean("x" + (char)13 + (char)10 + "y" + (char)0x2029));
        Assert.Null(ChatText.Clean((char)1 + "" + (char)0x2028 + " "));
        Assert.Equal("Bob: hi", ChatText.Line("Bob\n", "hi"));
    }

    [Fact]
    public void ActingFor_IsNotReentrant()
    {
        var (_, c, _) = Open();
        using (c.ActingFor(Blue, "p2"))
            Assert.Throws<InvalidOperationException>(() => c.ActingFor(Red, "p1"));
        using (c.ActingFor(Red, "p1")) { }   // usable again once closed
    }
}
