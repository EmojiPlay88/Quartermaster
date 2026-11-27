using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>
/// Invented Riverland fixture for B9 #55 draws on the Render side: the phase fixture's numbered map with its setup pieces replaced by the
/// barges SC 1 Barge..SC 4 Barge (Southern Compact, side "south") with Boats 3, 1, 0, 2 and NL Battery (Northern League, side "north"), all
/// in hex 0101. Split: SC 3 Barge, SC 4 Barge and NL 2 Battery stand in hex 0301 instead. Formula "Shore battery" (run on a battery): Input
/// Shots; Draw Hits, weight Boats, same hex, filter side() == "Southern Compact", draw count Shots; output Piece Hits Subtract Boats by Hits.
/// The roller takes the faces queued in <see cref="Next"/>, else rolls the highest face; <see cref="RollerCalls"/> counts its calls.
/// </summary>
public abstract class DrawTestBase : IDisposable
{
    protected readonly string Tmp = Path.Combine(Path.GetTempPath(), "qm-b9draw-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    protected static readonly IReadOnlySet<string> North = new HashSet<string> { "north" };
    protected static readonly IReadOnlySet<string> South = new HashSet<string> { "south" };
    protected static readonly PlayerDef Alice = new("p1", "Alice", new[] { "north" });
    protected static readonly PlayerDef Bob = new("p2", "Bob", new[] { "south" });
    protected static readonly PlayerDef Dan = new("p3", "Dan", new[] { "north" });

    protected const string Tooltip = "Drawn at random when you apply; the log names the counters.";
    protected const string Collapsed = "Boats of the drawn counters changes";
    protected static readonly string[] BargeIds = { "sc1", "sc2", "sc3", "sc4" };

    /// <summary>Faces the roller hands out first (one per die), in order.</summary>
    protected readonly Queue<int> Next = new();
    protected int RollerCalls;

    protected DrawTestBase() => Directory.CreateDirectory(Tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(Tmp, true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    protected static FormulaDef Battery(bool groupRoll = false) => new()
    {
        Id = "f", Name = "Shore battery", Text = "Hits", GroupRoll = groupRoll,
        Bindings = new()
        {
            new VariableBinding { Variable = "Shots", Kind = BindingKind.Input },
            new VariableBinding
            {
                Variable = "Hits", Kind = BindingKind.Draw, Property = "p-boats", SameHex = true,
                PieceFilter = "side() == \"Southern Compact\"", DrawCount = "Shots",
            },
        },
        Outputs = new() { new FormulaOutput { Target = OutputTarget.Piece, PieceVariable = "Hits", Property = "p-boats", Op = OutputOp.Subtract, Expr = "Hits" } },
    };

    protected static RulesPack Pack(string sha, Visibility boats, FormulaDef formula) => new()
    {
        Format = 4,
        Id = "riverland",
        Name = "Riverland",
        ModuleSha256 = sha,
        Sides = new()
        {
            new SideDef { Id = "north", Name = "Northern League", UnitFilter = "startsWith(name(), \"NL\")" },
            new SideDef { Id = "south", Name = "Southern Compact", UnitFilter = "startsWith(name(), \"SC\")" },
        },
        Properties = new() { new PropertyDef { Id = "p-boats", Name = "Boats", Scope = PropertyScope.Pieces, Default = 0, Visibility = boats } },
        Formulas = new() { formula },
    };

    private static FixtureModule Module() => PhaseFixture.Module(numbered: true);

    private static void Add(GameSession g, string id, string name, double x, double? boats)
    {
        var type = PieceTypeParser.EncodeState(new[] { "piece;;;unit.svg;" + name });
        g.Execute(new AddPiece(id, type, PieceTypeParser.EncodeState(new[] { "" }), "Main Map", new PointD(x, 50), null, id + " placed"));
        if (boats != null) g.Execute(new LedgerDelta(LedgerKey.Piece(id), "p-boats", 0, boats.Value, "seed", null, null, null, null));
    }

    protected (ModuleSession S, PhaseController C) Open(Visibility boats = Visibility.Private, bool split = false, bool groupRoll = false,
        Difficulty d = Difficulty.FullyAuto, IReadOnlyList<PlayerDef>? players = null)
    {
        var fx = Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(Tmp, "cache" + _sessions.Count), Path.Combine(Tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, sides) =>
        {
            RollerCalls++;
            return Enumerable.Range(0, n).Select(_ => Next.Count > 0 ? Next.Dequeue() : sides).ToList();
        });
        s.LoadSetup(s.Model.Setups[0], d, players ?? new[] { Alice, Bob, Dan });
        var g = s.Game!;
        foreach (var id in new[] { PhaseFixture.RedOne, PhaseFixture.RedTwo, PhaseFixture.BlueOne }) g.Execute(new RemovePiece(id, null));
        var east = split ? 130 : 30;
        Add(g, "sc1", "SC 1 Barge", 30, 3);
        Add(g, "sc2", "SC 2 Barge", 30, 1);
        Add(g, "sc3", "SC 3 Barge", east, 0);
        Add(g, "sc4", "SC 4 Barge", east, 2);
        Add(g, "nl1", "NL Battery", 30, null);
        if (split) Add(g, "nl2", "NL 2 Battery", 130, null);
        s.NewRules("t");
        s.UpdateRules(_ => Pack(s.Archive.Sha256, boats, Battery(groupRoll)));
        g.Rebase();
        RollerCalls = 0;
        return (s, new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s)));
    }

    /// <summary>A guest holding <paramref name="sides"/>: only its filtered snapshot of the host's game and the rules pack.</summary>
    protected ModuleSession Guest(ModuleSession host, IReadOnlySet<string> sides)
    {
        var fx = Module();
        _fixtures.Add(fx);
        var guest = ModuleSession.Open(fx.Build(), Path.Combine(Tmp, "gcache" + _sessions.Count), Path.Combine(Tmp, "groot" + _sessions.Count));
        _sessions.Add(guest);
        guest.EnterGuestMode();
        guest.LoadGuestRules(host.Rules!);
        guest.ApplySnapshot(PlayerView.For(host.Game!.State, host.Rules, host.Model, sides), sides);
        return guest;
    }

    protected static FormulaDialogModel Dialog(ModuleSession s, string pieceId = "nl1")
    {
        var m = new FormulaDialogModel(s.Runner!, s.Rules!.ById<FormulaDef>("f")!, pieceId) { Checked = s.Game!.State.Difficulty != Difficulty.FullyAuto };
        FormulaMasking.Configure(m, s);
        return m;
    }

    protected static double? Boats(ModuleSession s, string id) => Ledger.Get(s.Game!.State, LedgerKey.Piece(id), "p-boats");

    protected static double?[] AllBoats(ModuleSession s) => BargeIds.Select(id => Boats(s, id)).ToArray();

    protected static BoxView Box(DialogView dv, string variable) => dv.Boxes.Single(b => b.Variable == variable);

    protected static string Json(object o) => JsonSerializer.Serialize(o, QuartermasterJson.Options);

    /// <summary>Everything a dialog view shows (boxes, tooltips, inline dice, result, working, status, output lines), never its traces.</summary>
    protected static string Shown(DialogView dv) => Json(new
    {
        Boxes = dv.Boxes.Select(b => new { b.Variable, b.Text, b.Tooltip, b.CaptionSuffix, b.RerollKey, b.Hint, b.Choices }),
        dv.InlineRolls, dv.ResultText, dv.WorkingText, dv.Status, dv.OutputLines,
    });

    /// <summary>True when <paramref name="text"/> names a barge or its id.</summary>
    protected static bool NamesBarge(string text) =>
        text.Contains("Barge", StringComparison.Ordinal) || BargeIds.Any(id => text.Contains(id, StringComparison.Ordinal));
}

/// <summary>B9 #55 in the formula dialog, the batch grids, hot-seat and undo: the split is pinned and never shown before Apply.</summary>
[Collection(AppDataRootEnv.Name)]
public class DrawDialogTests : DrawTestBase
{
    [Fact]
    public void Calculate_twice_then_apply_same_split()
    {
        var (s, c) = Open();
        s.SetViewSides(North);
        var m = Dialog(s);
        m.SetBoxText("Shots", "3");
        foreach (var face in new[] { 1, 1, 1 }) Next.Enqueue(face);   // the first draw: SC 1 Barge three times
        m.Calculate(s.Game!.State);
        Assert.Equal(new[] { "draw:Hits#1", "draw:Hits#2", "draw:Hits#3" }, m.PinnedRolls.Keys.Where(k => k.StartsWith("draw:", StringComparison.Ordinal)).Order());
        m.Calculate(s.Game.State);   // pinned: nothing drawn again
        var dv = m.Evaluate(s.Game.State);
        Assert.Equal(3, RollerCalls);
        Assert.True(dv.CanApply, dv.Status);
        // No reroll for a draw key: the pin stays.
        m.Reroll("draw:Hits#1");
        Assert.True(m.PinnedRolls.ContainsKey("draw:Hits#1"));
        Assert.True(c.ApplyFormula(null, "nl1", m.Evaluate(s.Game.State).Trace, m.AutoCalculated));
        Assert.Equal(3, RollerCalls);
        Assert.Equal(new double?[] { 0, 1, 0, 2 }, AllBoats(s));
        Assert.Contains(s.Game.State.Log, l => l.Text == "Shore battery: Hits: 3 on SC 1 Barge" && l.SideId == "south" && l.PieceId == "sc1");

        // Physical-dice mode: still no box to type, the engine draws with the session roller.
        var (s2, c2) = Open(Visibility.Public);
        s2.SetViewSides(North);
        var m2 = Dialog(s2);
        m2.PhysicalDice = true;
        m2.SetBoxText("Shots", "3");
        m2.SetBoxText("Hits", "1");   // ignored
        var dv2 = m2.Evaluate(s2.Game!.State);
        var hits = Box(dv2, "Hits");
        Assert.False(hits.Enabled);
        Assert.Equal(BoxBackground.Auto, hits.Background);
        Assert.Equal("3", hits.Text);
        Assert.True(dv2.CanApply, dv2.Status);
        Assert.True(c2.ApplyFormula(null, "nl1", dv2.Trace, false));
        Assert.Equal(new double?[] { 3, 0, 0, 0 }, AllBoats(s2));   // the highest faces: SC 4 Barge twice, SC 2 Barge once
    }

    [Fact]
    public void Dialog_never_shows_split_before_apply()
    {
        foreach (var boats in new[] { Visibility.Public, Visibility.Private })
            foreach (var viewer in new[] { null, North })
            {
                var (s, _) = Open(boats);
                if (viewer != null) s.SetViewSides(viewer);
                var m = Dialog(s);
                m.SetBoxText("Shots", "3");
                m.Calculate(s.Game!.State);
                var dv = m.Evaluate(s.Game.State);
                Assert.True(dv.CanApply, dv.Status);
                Assert.Contains(dv.Trace.Draws, d => d.Drawn.Count > 0);   // the real run drew
                var shown = Shown(dv);
                Assert.False(NamesBarge(shown), shown);
                Assert.Equal(new[] { Collapsed }, dv.OutputLines);
                Assert.Empty(dv.InlineRolls);
                Assert.Null(Box(dv, "Hits").RerollKey);
                Assert.Equal(Tooltip, Box(dv, "Hits").Tooltip);
                Assert.False(dv.WorkingNegative);
            }
    }

    [Fact]
    public void Masked_viewer_box_reads_hidden()
    {
        var (s, c) = Open(Visibility.Private);
        s.SetViewSides(North);
        var m = Dialog(s);
        Assert.Contains(FormulaMasking.DrawInput, m.Hidden);
        Assert.Contains("Hits", m.Hidden);
        m.SetBoxText("Shots", "3");
        var dv = m.Evaluate(s.Game!.State);
        var hits = Box(dv, "Hits");
        Assert.Equal(FormulaMasking.HiddenText, hits.Text);
        Assert.Equal(Tooltip, hits.Tooltip);
        Assert.False(hits.Enabled);
        Assert.Equal(FormulaMasking.HiddenText, dv.ResultText);
        Assert.Equal(FormulaMasking.WorkingHiddenText, dv.WorkingText);
        Assert.True(dv.CanApply, dv.Status);
        Assert.Equal(new[] { Collapsed }, dv.OutputLines);
        Assert.True(c.ApplyFormula(null, "nl1", dv.Trace, false));
        Assert.Equal(new double?[] { 3, 0, 0, 0 }, AllBoats(s));
    }

    [Fact]
    public void Unmasked_draw_shows_k()
    {
        var (s, _) = Open(Visibility.Public);
        s.SetViewSides(North);
        var m = Dialog(s);
        Assert.Empty(m.Hidden);
        m.SetBoxText("Shots", "4");
        var dv = m.Evaluate(s.Game!.State);
        var hits = Box(dv, "Hits");
        Assert.Equal("4", hits.Text);
        Assert.Equal(Tooltip, hits.Tooltip);
        Assert.False(hits.Enabled);
        Assert.Equal("4", dv.ResultText);
        Assert.Contains("Hits = 4", dv.WorkingText);
        Assert.False(NamesBarge(Shown(dv)));
        // k = min(count, members): six boats in the hex.
        m.SetBoxText("Shots", "9");
        Assert.Equal("6", Box(m.Evaluate(s.Game.State), "Hits").Text);
        // The caption names the draw.
        var layout = FormulaLayout.Build(s.Rules!.ById<FormulaDef>("f")!, s.Rules!);
        var items = layout.Main.Concat(layout.SubRows.SelectMany(r => r.Items));
        Assert.Equal("Boats (drawn)", Assert.Single(items.OfType<BoxItem>(), x => x.Variable == "Hits").Caption);
    }

    [Fact]
    public void Hotseat_south_sees_draw_lines_north_does_not()
    {
        var (s, c) = Open(Visibility.Private);
        s.SetViewSides(North);
        var m = Dialog(s);
        m.SetBoxText("Shots", "3");
        Assert.True(c.ApplyFormula(null, "nl1", m.Evaluate(s.Game!.State).Trace, false));
        var st = s.Game.State;
        var south = PlayerView.For(st, s.Rules, s.Model, South);
        var north = PlayerView.For(st, s.Rules, s.Model, North);
        Assert.Contains(south.Log, l => l.Text == "Shore battery: Hits: 1 on SC 2 Barge");
        Assert.Contains(south.Log, l => l.Text == "Shore battery: Hits: 2 on SC 4 Barge");
        Assert.DoesNotContain(north.Log, l => l.Text.StartsWith("Shore battery: Hits:", StringComparison.Ordinal));
        Assert.DoesNotContain(north.Log, l => l.Text.Contains("Barge", StringComparison.Ordinal) && l.Text.Contains("Boats", StringComparison.Ordinal));
        Assert.Contains(north.Log, l => l.Text == "Southern Compact ledger changed");
        // The hot-seat view the session shows follows the seat.
        Assert.DoesNotContain(s.View!.Log, l => l.Text.StartsWith("Shore battery: Hits:", StringComparison.Ordinal));
        s.SetViewSides(South);
        Assert.Contains(s.View!.Log, l => l.Text == "Shore battery: Hits: 2 on SC 4 Barge");
    }

    [Fact]
    public void Undo_after_apply_restores_boats_and_lines()
    {
        var (s, c) = Open();
        s.SetViewSides(North);
        var g = s.Game!;
        var log = g.State.Log.Count;
        var m = Dialog(s);
        m.SetBoxText("Shots", "3");
        Assert.True(c.ApplyFormula(null, "nl1", m.Evaluate(g.State).Trace, false));
        Assert.Single(g.Actions);
        Assert.Equal(new double?[] { 3, 0, 0, 0 }, AllBoats(s));
        g.Undo();
        Assert.Equal(new double?[] { 3, 1, 0, 2 }, AllBoats(s));
        Assert.Equal(log, g.State.Log.Count);
        Assert.DoesNotContain(g.State.Log, l => l.Text.StartsWith("Shore battery: Hits:", StringComparison.Ordinal));
        Assert.False(g.CanUndo);
        var calls = RollerCalls;
        g.Redo();
        Assert.Equal(new double?[] { 3, 0, 0, 0 }, AllBoats(s));
        Assert.Equal(calls, RollerCalls);   // redo re-executes the commands: nothing drawn again
    }

    [Fact]
    public void Menu_batch_chain_and_live_apply_agree()
    {
        var (s, c) = Open(Visibility.Public, split: true, groupRoll: true);
        s.SetViewSides(North);
        var b = new MenuBatchModel(s, c, "f", new[] { "nl1", "nl2" });
        b.SetAllRowsCell("Shots", "2");
        // Row 1 (hex 0101: SC 1 Barge 3, SC 2 Barge 1) draws faces 4 then 1; row 2 (hex 0301: SC 4 Barge 2) draws 1 then 1.
        foreach (var face in new[] { 4, 1, 1, 1 }) Next.Enqueue(face);
        b.CalculateAll();
        Assert.Equal(4, RollerCalls);
        for (var i = 0; i < 2; i++)
        {
            var row = b.Row(i);
            Assert.True(row.CanApply, row.Exceptions);
            Assert.Equal(Collapsed, row.LedgerText);
            Assert.Empty(b.OutputsOf(i));
            Assert.False(NamesBarge(Json(row)), Json(row));
        }
        // Never shared by a group roll: each row has its own draw dice.
        Assert.Equal(new[] { 4 }, b.ModelFor(0).PinnedRolls["draw:Hits#1"]);
        Assert.Equal(new[] { 1 }, b.ModelFor(1).PinnedRolls["draw:Hits#1"]);
        b.CalculateAll();
        Assert.Equal(4, RollerCalls);
        Assert.Equal(2, b.ApplyAll());
        Assert.Equal(4, RollerCalls);
        Assert.Single(s.Game!.Actions);
        Assert.Equal(new double?[] { 2, 0, 0, 0 }, AllBoats(s));
        s.Game.Undo();
        Assert.Equal(new double?[] { 3, 1, 0, 2 }, AllBoats(s));
    }
}
