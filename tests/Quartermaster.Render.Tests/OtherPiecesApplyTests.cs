using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>
/// Invented Riverland module and pack for B3 (acting on other pieces) on the Render side: the phase fixture's numbered map with its setup
/// pieces replaced by North Barge (n1), North Bridge (n2) and South Gunboat (s1) in hex 0101, South Fort (s2, a "Levels" layer) in 0301; a
/// "Markers/Wreck" palette slot ("North Wreck", a Salvage sheet of 5 and the Levels layer) and a "Dead pile" map with one board. With hiding:
/// the Wreck also carries a mask (so pieces may hide), plus North Battery (n3, hidden by Northern League) and North Minefield (n4, Levels and
/// a mask set by Northern League) in 0201. Side "red" = Northern League, "blue" = Southern Compact.
/// </summary>
public abstract class OtherPiecesTestBase : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-b3r-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    protected static readonly IReadOnlySet<string> North = new HashSet<string> { "red" };
    protected static readonly IReadOnlySet<string> South = new HashSet<string> { "blue" };
    protected static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });
    protected static readonly PlayerDef Bob = new("p2", "Bob", new[] { "blue" });

    /// <summary>A 4-level layer "Levels" (not always active; level names Camp, -, Fort, Citadel).</summary>
    protected const string Levels = "emb2;Activate;130;A;Up;130;U;Down;130;D;;;1;false;0;0;l1.png,l2.png,l3.png,l4.png;Camp,,Fort,Citadel;true;Levels;;;false;;1;1;false";
    protected const string Mask = "obs;M;m.png;Mask it;I;?";
    protected const string Hide = "hide;I;Hide it;;;0.3;;false";

    protected OtherPiecesTestBase() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static FixtureModule Module(bool hiding)
    {
        var wreckTraits = hiding ? new[] { Levels, Mask, "propertysheet;0Salvage;Sheet;", "piece;;;unit.svg;North Wreck" }
            : new[] { Levels, "propertysheet;0Salvage;Sheet;", "piece;;;unit.svg;North Wreck" };
        var wreckStates = hiding ? new[] { "", "null", "5", "null;0;0;9" } : new[] { "", "5", "null;0;0;9" };
        var extra = $"""
              <VASSAL.build.module.PieceWindow defaultWidth="0" hidden="false" hotkey="" scale="1.0" text="Markers" tooltip="" visible="true">
                <VASSAL.build.widget.PieceSlot entryName="Wreck" gpid="9" height="32" width="32">+/null/{PieceTypeParser.EncodeState(wreckTraits)}/{PieceTypeParser.EncodeState(wreckStates)}</VASSAL.build.widget.PieceSlot>
              </VASSAL.build.module.PieceWindow>
              <VASSAL.build.module.Map mapName="Dead pile">
                <VASSAL.build.module.map.BoardPicker>
                  <VASSAL.build.module.map.boardPicker.Board image="pile.png" name="Pile board" reversible="false"/>
                </VASSAL.build.module.map.BoardPicker>
              </VASSAL.build.module.Map>
            </VASSAL.build.GameModule>
            """;
        return PhaseFixture.Module(numbered: true).EditBuildFile(x => x.Replace("</VASSAL.build.GameModule>", extra)).AddImage("pile.png", 120, 80);
    }

    protected static RulesPack Pack(string sha, Visibility strength, IEnumerable<FormulaDef> formulas, IEnumerable<PhaseDef>? phases = null) => new()
    {
        Format = 4,
        Id = "riverland",
        Name = "Riverland",
        ModuleSha256 = sha,
        Sides = new()
        {
            new SideDef { Id = "red", Name = "Northern League", UnitFilter = "startsWith(name(), \"North\")" },
            new SideDef { Id = "blue", Name = "Southern Compact", UnitFilter = "startsWith(name(), \"South\")" },
        },
        Properties = new()
        {
            new PropertyDef { Id = "str", Name = "Strength", Scope = PropertyScope.Pieces, Default = 2, Visibility = strength },
            new PropertyDef { Id = "salvage", Name = "Salvage", Scope = PropertyScope.Pieces, SheetField = "Salvage", Visibility = Visibility.Public },
            new PropertyDef { Id = "orders", Name = "Orders", Scope = PropertyScope.Pieces, Visibility = Visibility.Private },
        },
        Formulas = formulas.ToList(),
        Phases = phases?.ToList() ?? new(),
    };

    /// <summary>One phase of Northern League whose start runs formula "f" for each of its units.</summary>
    protected static PhaseDef[] NorthStart() => new[]
    {
        new PhaseDef { Id = "ph-n", Name = "Northern muster", SideId = "red", OnStart = new() { new StartEntry { FormulaId = "f", ForEach = ForEachMode.Units } } },
    };

    private static void Add(GameSession g, string id, string name, double x, double str, params (string Trait, string State)[] traits)
    {
        var type = PieceTypeParser.EncodeState(traits.Select(t => t.Trait).Append("piece;;;unit.svg;" + name).ToList());
        var state = PieceTypeParser.EncodeState(traits.Select(t => t.State).Append("").ToList());
        g.Execute(new AddPiece(id, type, state, "Main Map", new PointD(x, 50), null, id + " placed"));
        g.Execute(new LedgerDelta(LedgerKey.Piece(id), "str", 0, str, "seed", null, null, null, null));
    }

    /// <param name="prepare">Replaces the controller's command preparation (a test seam); null = <see cref="CommandLog.Prepare"/>.</param>
    protected (ModuleSession S, PhaseController C) Open(Difficulty d, IEnumerable<FormulaDef> formulas, bool hiding = false,
        Visibility strength = Visibility.Public, IReadOnlyList<PlayerDef>? players = null, IEnumerable<PhaseDef>? phases = null,
        Func<ModuleSession, Func<GameCommand, GameState, GameCommand>>? prepare = null)
    {
        var fx = Module(hiding);
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], d, players);
        var g = s.Game!;
        foreach (var id in new[] { PhaseFixture.RedOne, PhaseFixture.RedTwo, PhaseFixture.BlueOne }) g.Execute(new RemovePiece(id, null));
        Add(g, "n1", "North Barge", 30, 4);
        Add(g, "n2", "North Bridge", 30, 3);
        Add(g, "s1", "South Gunboat", 30, 7);
        Add(g, "s2", "South Fort", 130, 5, (Levels, ""));
        if (hiding)
        {
            Add(g, "n3", "North Battery", 80, 6, (Hide, "red"));
            Add(g, "n4", "North Minefield", 80, 1, (Levels, ""), (Mask, "red;"));
        }
        s.NewRules("t");
        s.UpdateRules(_ => Pack(s.Archive.Sha256, strength, formulas, phases));
        g.Rebase();
        var c = new PhaseController(s, prepare?.Invoke(s) ?? ((cmd, st) => CommandLog.Prepare(cmd, st, s)));
        return (s, c);
    }

    /// <summary>A guest holding Southern Compact: only its filtered snapshot of the host's game and the rules pack.</summary>
    protected ModuleSession Guest(ModuleSession host, bool hiding)
    {
        var fx = Module(hiding);
        _fixtures.Add(fx);
        var guest = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "gcache" + _sessions.Count), Path.Combine(_tmp, "groot" + _sessions.Count));
        _sessions.Add(guest);
        guest.EnterGuestMode();
        guest.LoadGuestRules(host.Rules!);
        guest.ApplySnapshot(PlayerView.For(host.Game!.State, host.Rules, host.Model, South), South);
        return guest;
    }

    protected static VariableBinding Pick(bool sameHex = false) =>
        new() { Variable = "T", Kind = BindingKind.Piece, Property = "str", SameHex = sameHex };

    /// <summary>A binding of the run piece's own (public) Salvage, so the formula applies to a unit (a guest's run starts from its unit).</summary>
    protected static VariableBinding Own() => new() { Variable = "U", Kind = BindingKind.Unit, Property = "salvage" };

    protected static FormulaDef F(string name, string text, IEnumerable<VariableBinding> bindings, params FormulaOutput[] outputs) => new()
    {
        Id = "f", Name = name, Text = text, Bindings = bindings.ToList(), Outputs = outputs.ToList(),
    };

    protected static FormulaOutput UnitWrite(OutputOp op) => new() { Target = OutputTarget.Unit, Property = "str", Op = op };
    protected static FormulaOutput PieceWrite(OutputOp op, string property = "str") =>
        new() { Target = OutputTarget.Piece, PieceVariable = "T", Property = property, Op = op };
    protected static FormulaOutput Place() => new() { Action = OutputAction.Place, Target = OutputTarget.Unit, Slot = "Markers/Wreck" };
    protected static FormulaOutput PlacedSet(string property, string expr) =>
        new() { Target = OutputTarget.Placed, Property = property, Op = OutputOp.Set, Expr = expr };
    protected static FormulaOutput Remove(OutputTarget target, string? toMap = null) =>
        new() { Action = OutputAction.Remove, Target = target, ToMapId = toMap, PieceVariable = target == OutputTarget.Piece ? "T" : null };
    protected static FormulaOutput SetLayer(OutputTarget target, string expr) =>
        new() { Action = OutputAction.SetLayer, Target = target, Layer = "Levels", Expr = expr, PieceVariable = target == OutputTarget.Piece ? "T" : null };
    protected static FormulaOutput SetMask(OutputTarget target, string expr) =>
        new() { Action = OutputAction.SetMask, Target = target, Expr = expr, PieceVariable = target == OutputTarget.Piece ? "T" : null };

    /// <summary>A piece's candidate label on the full state: "{name} ({location})".</summary>
    protected static string Label(ModuleSession s, string id) =>
        $"{PieceInfo.Name(s.Game!.State.Pieces[id])} ({PieceInfo.Location(s.Game!.State.Pieces[id], s.Model)})";

    protected static FormulaTrace Run(ModuleSession s, string? pieceId, IReadOnlyDictionary<string, string>? typed = null, IReadOnlySet<string>? pickSides = null) =>
        s.Runner!.Run(s.Game!.State, new RunRequest("f", pieceId, typed ?? new Dictionary<string, string>(), new Dictionary<string, string>(),
            new Dictionary<string, IReadOnlyList<int>>(), PickSides: pickSides));

    protected static double? Str(ModuleSession s, string id) => Ledger.Get(s.Game!.State, LedgerKey.Piece(id), "str");

    protected static FormulaDialogModel Dialog(ModuleSession s, string? pieceId)
    {
        var m = new FormulaDialogModel(s.Runner!, s.Rules!.ById<FormulaDef>("f")!, pieceId) { Checked = false };
        FormulaMasking.Configure(m, s);
        return m;
    }
}

/// <summary>B3 on the Render side: a run's piece outputs and effects applied as one undo step, the placed piece seeded first, the pass refresh.</summary>
[Collection(AppDataRootEnv.Name)]
public class OtherPiecesApplyTests : OtherPiecesTestBase
{
    [Fact]
    public void Transfer_one_undo_step()
    {
        var f = F("Transfer", "2", new[] { Pick(sameHex: true) }, UnitWrite(OutputOp.Subtract), PieceWrite(OutputOp.Add));
        var (s, c) = Open(Difficulty.SemiAuto, new[] { f });
        var g = s.Game!;
        var t = Run(s, "n1", new Dictionary<string, string> { ["T"] = Label(s, "s1") });
        Assert.Null(t.Error);
        Assert.True(c.ApplyFormula(null, "n1", t, false));
        Assert.Equal(2, Str(s, "n1"));
        Assert.Equal(9, Str(s, "s1"));
        Assert.Single(g.Actions);

        g.Undo();
        Assert.Equal(4, Str(s, "n1"));
        Assert.Equal(7, Str(s, "s1"));
        Assert.False(g.CanUndo);
        g.Redo();
        Assert.Equal(2, Str(s, "n1"));
        Assert.Equal(9, Str(s, "s1"));
    }

    [Fact]
    public void Place_then_placed_set_one_undo_and_seeded_first()
    {
        var f = F("Salvage", "9", Array.Empty<VariableBinding>(), Place(), PlacedSet("salvage", "9"));
        var (s, c) = Open(Difficulty.SemiAuto, new[] { f });
        var g = s.Game!;
        s.Runner!.NewPieceId = () => "p:w1";
        var t = Run(s, "n1");
        Assert.Null(t.Error);
        Assert.True(c.ApplyFormula(null, "n1", t, false));
        Assert.True(g.State.Pieces.ContainsKey("p:w1"));
        var key = LedgerKey.Piece("p:w1");
        Assert.Equal(9, Ledger.Get(g.State, key, "salvage"));
        var lines = g.State.LedgerHistory.Where(e => e.Key == key && e.PropertyId == "salvage").ToList();
        Assert.Equal(new double[] { 5, 9 }, lines.Select(e => e.After).ToArray());   // the sheet seed first, then the Set over it
        Assert.Single(g.Actions);

        g.Undo();
        Assert.False(g.State.Pieces.ContainsKey("p:w1"));
        Assert.Null(Ledger.Get(g.State, key, "salvage"));
        Assert.DoesNotContain(g.State.LedgerHistory, e => e.Key == key);
        Assert.False(g.CanUndo);

        g.Redo();   // the same piece id again, seeded then set
        Assert.True(g.State.Pieces.ContainsKey("p:w1"));
        Assert.Equal(new double[] { 5, 9 }, g.State.LedgerHistory.Where(e => e.Key == key && e.PropertyId == "salvage").Select(e => e.After).ToArray());
    }

    [Fact]
    public void Remove_run_piece_resolves_pending_run()
    {
        var f = F("Scuttle", "0", Array.Empty<VariableBinding>(), Remove(OutputTarget.Unit));
        var (s, c) = Open(Difficulty.SemiAuto, new[] { f }, phases: NorthStart());
        var g = s.Game!;
        c.Start();
        var run = Assert.Single(g.State.Pending);
        Assert.Equal(new[] { "n1", "n2" }, run.Units.Select(u => u.PieceId).Order().ToArray());

        Assert.True(c.ApplyFormula(run.Id, "n1", Run(s, "n1"), false));
        Assert.False(g.State.Pieces.ContainsKey("n1"));
        Assert.True(Assert.Single(g.State.Pending).Units.Single(u => u.PieceId == "n1").Applied);

        g.Undo();   // the removal and the answer go back together
        Assert.True(g.State.Pieces.ContainsKey("n1"));
        Assert.False(Assert.Single(g.State.Pending).Units.Single(u => u.PieceId == "n1").Applied);
        g.Redo();

        Assert.True(c.ApplyFormula(run.Id, "n2", Run(s, "n2"), false));
        Assert.Empty(g.State.Pending);
        Assert.False(g.State.Pieces.ContainsKey("n2"));
    }

    [Fact]
    public void FullyAuto_entry_with_effects_matches_one_by_one()
    {
        // Each unit counts its hex, writes the count and moves to the Dead pile: the second unit must see the first one gone.
        var f = F("Withdraw", "hexcount(\"\")", Array.Empty<VariableBinding>(), UnitWrite(OutputOp.Set), Remove(OutputTarget.Unit, "Dead pile"));
        var (auto, ac) = Open(Difficulty.FullyAuto, new[] { f }, phases: NorthStart());
        var (manual, mc) = Open(Difficulty.SemiAuto, new[] { f }, phases: NorthStart());

        ac.Start();
        mc.Start();
        var run = Assert.Single(manual.Game!.State.Pending);
        foreach (var u in run.Units) Assert.True(mc.ApplyFormula(run.Id, u.PieceId, Run(manual, u.PieceId), false));

        foreach (var id in new[] { "n1", "n2" })
        {
            Assert.Equal(Str(manual, id), Str(auto, id));
            Assert.Equal(manual.Game!.State.Pieces[id].MapId, auto.Game!.State.Pieces[id].MapId);
        }
        Assert.Equal(new double?[] { 3, 2 }, run.Units.Select(u => Str(auto, u.PieceId!)).ToArray());
        Assert.Contains(auto.Game!.State.Log, l => l.Text == "Withdraw: 2 units, 2 applied");
    }

    [Fact]
    public void FullyAuto_unit_removed_earlier_skipped()
    {
        // Test seam: the first unit's removal is redirected to the second unit, so the second unit is gone when its turn comes.
        var f = F("Scuttle", "1", Array.Empty<VariableBinding>(), UnitWrite(OutputOp.Set), Remove(OutputTarget.Unit));
        string? first = null;
        var (s, c) = Open(Difficulty.FullyAuto, new[] { f }, phases: NorthStart(), prepare: s => (cmd, st) =>
        {
            if (cmd is RemovePiece rp)
            {
                first ??= rp.PieceId;
                return rp with { PieceId = rp.PieceId == "n1" ? "n2" : "n1" };
            }
            return CommandLog.Prepare(cmd, st, s);
        });
        c.Start();
        var g = s.Game!;
        Assert.Equal("n1", first);
        Assert.False(g.State.Pieces.ContainsKey("n2"));
        Assert.True(g.State.Pieces.ContainsKey("n1"));
        Assert.DoesNotContain(g.State.Log, l => l.Text.StartsWith("Scuttle: North Bridge", StringComparison.Ordinal));
        Assert.Contains(g.State.Log, l => l.Text == "Scuttle: 2 units, 1 applied");
    }

    [Fact]
    public void Placed_piece_not_in_current_entry_units()
    {
        var f = F("Muster", "1", Array.Empty<VariableBinding>(), Place(), UnitWrite(OutputOp.Set));
        var (s, c) = Open(Difficulty.FullyAuto, new[] { f }, phases: NorthStart());
        var g = s.Game!;
        c.Start();
        var wrecks = g.State.Pieces.Values.Where(p => PieceInfo.Name(p) == "North Wreck").ToList();
        Assert.Equal(2, wrecks.Count);
        foreach (var w in wrecks)
        {
            Assert.Null(Ledger.Get(g.State, LedgerKey.Piece(w.Id), "str"));   // never one of the entry's units
            Assert.Equal(5, Ledger.Get(g.State, LedgerKey.Piece(w.Id), "salvage"));   // seeded inside the run
        }
        Assert.Contains(g.State.Log, l => l.Text == "Muster: 2 units, 2 applied");
        g.Undo();   // the phase start, its runs, the placed pieces and their seeds: one step
        Assert.DoesNotContain(g.State.Pieces.Values, p => PieceInfo.Name(p) == "North Wreck");
        Assert.DoesNotContain(g.State.LedgerHistory, e => e.PropertyId == "salvage");
    }

    [Fact]
    public void Batch_row_choices_follow_the_row_unit()
    {
        var f = F("Board", "1", new[] { Own(), Pick(sameHex: true) }, PieceWrite(OutputOp.Subtract));
        var (s, c) = Open(Difficulty.FullyAuto, new[] { f }, phases: NorthStart());   // the pick stops each unit: a run waits for input
        c.Start();
        var run = Assert.Single(s.Game!.State.Pending);
        var b = new BatchModel(s, c, run.Id);
        for (var row = 0; row < run.Units.Count; row++)
        {
            var self = run.Units[row].PieceId!;
            var expected = new[] { "n1", "n2", "s1" }.Where(id => id != self).Select(id => Label(s, id)).ToArray();
            Assert.Equal(expected, b.ChoicesFor("T", row)!.ToArray());   // only the other pieces in this row's hex
            Assert.Equal(expected, b.ModelFor(row).Evaluate(s.Game!.State).Boxes.Single(x => x.Variable == "T").Choices!.ToArray());
        }
        Assert.Empty(b.ChoicesFor("T")!);   // no row, no run piece: a "same hex" column has no list of its own
        // A row applies its pick with its own unit's list.
        var first = run.Units[0].PieceId!;
        var target = first == "n1" ? "n2" : "n1";
        b.SetCell(0, "T", Label(s, target));
        Assert.Equal(1, b.ApplyAll());
        Assert.Equal(target == "n1" ? 3 : 2, Str(s, target));
    }

    [Fact]
    public void LogWorking_skips_effects_trace()
    {
        var f = F("Raise", "2", Array.Empty<VariableBinding>(), SetLayer(OutputTarget.Unit, "2"));
        var (s, c) = Open(Difficulty.SemiAuto, new[] { f });
        var g = s.Game!;
        var t = Run(s, "s2");
        Assert.Null(t.Error);
        Assert.True(c.ApplyFormula(null, "s2", t, false));
        var applied = g.State.Log.Where(l => l.AppliedRun == "f").ToList();
        var line = Assert.Single(applied);
        Assert.Equal("South Fort: 2 = 2 → Levels of South Fort = 2", line.Text);
        Assert.Single(g.State.Log, l => l.Kind == LogKind.Calc && l.FormulaId == "f");
    }
}
