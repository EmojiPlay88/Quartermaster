using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

/// <summary>B6 online: the host checks every guest keyed edit (<see cref="KeyedEditIntent"/>) and refuses an unkeyed edit of a keyed property.
/// Invented Red/Blue names only.</summary>
public class KeyedEditOnlineTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-keyed-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();
    private static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });
    private static readonly PlayerDef Bob = new("p2", "Bob", new[] { "blue" });
    private static readonly IReadOnlySet<string> Blue = new HashSet<string> { "blue" };
    private const string Global = "h:global";

    public KeyedEditOnlineTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    /// <summary>"Cargo" (Pieces, keyed by the cost table's rows, Private), "Flood" (a Public place), "Barges due" (Holders, by turn) and a
    /// Global formula "Flood here" writing Flood at the run piece's hex.</summary>
    private static RulesPack Pack(string sha)
    {
        var p = PhaseFixture.Pack(sha);
        return p with
        {
            Format = 4,
            Properties = p.Properties.Concat(new[]
            {
                new PropertyDef { Id = "cargo", Name = "Cargo", Scope = PropertyScope.Pieces, KeyedBy = "table:t-cost", Visibility = Visibility.Private },
                new PropertyDef { Id = "flood", Name = "Flood", Scope = PropertyScope.Holders, KeyedBy = "hex", Visibility = Visibility.Public },
                new PropertyDef { Id = "due", Name = "Barges due", Scope = PropertyScope.Holders, KeyedBy = "turn", Visibility = Visibility.Public },
            }).ToList(),
            Holders = new() { new HolderDef { Id = "reddepot", Name = "Red Depot", SideId = "red" } },
            Formulas = p.Formulas.Append(new FormulaDef
            {
                Id = "f-flood", Name = "Flood here", Text = "W - 7",
                Bindings = new() { new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" } },
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Global, Property = "flood", Op = OutputOp.Set, Key = "hex()" } },
            }).ToList(),
        };
    }

    private (ModuleSession S, IntentExecutor X) Open()
    {
        var fx = PhaseFixture.Module(numbered: true);
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(4, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto, new[] { Alice, Bob });
        s.NewRules("t");
        s.UpdateRules(_ => Pack(s.Archive.Sha256));
        var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        return (s, new IntentExecutor(s, c));
    }

    private static string Hex(ModuleSession s, string pieceId) => PieceInfo.Location(s.Game!.State.Pieces[pieceId], s.Model)!;

    [Fact]
    public void Guest_keyed_edit_applied_by_host()
    {
        var (s, x) = Open();
        var r = x.Execute("p2", Blue, new KeyedEditIntent(LedgerKey.Piece(PhaseFixture.BlueOne), "cargo", "sand", 6, " loaded "));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(6, KeyedValues.Get(s.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "cargo", "Sand"));   // the row's own spelling
        var d = Assert.IsType<LedgerDelta>(s.Game.Log[^1]);
        Assert.Equal("Sand", d.ValueKey);
        Assert.Equal("p2", d.Who);
        Assert.Equal("loaded", d.Note);
        Assert.Equal("Cargo[Sand] 0 → 6", d.Working);
        // the same value again: done, no history entry
        var history = s.Game.State.LedgerHistory.Count;
        Assert.True(x.Execute("p2", Blue, new KeyedEditIntent(LedgerKey.Piece(PhaseFixture.BlueOne), "cargo", "Sand", 6, "again")).Ok);
        Assert.Equal(history, s.Game.State.LedgerHistory.Count);
        // a turn key and a place key
        Assert.True(x.Execute("p2", Blue, new KeyedEditIntent(Global, "due", "4", 2, "n")).Ok);
        Assert.Equal(2, KeyedValues.Get(s.Game.State, Global, "due", "4"));
    }

    [Fact]
    public void Guest_keyed_edit_other_side_piece_refused()
    {
        var (s, x) = Open();
        var before = JsonSerializer.Serialize(s.Game!.State, QuartermasterJson.Options);
        var r = x.Execute("p2", Blue, new KeyedEditIntent(LedgerKey.Piece(PhaseFixture.RedOne), "cargo", "Sand", 6, "n"));
        Assert.False(r.Ok);
        Assert.Equal("That value " + ModuleSession.OtherSideText + ".", r.Message);
        var r2 = x.Execute("p2", Blue, new KeyedEditIntent(LedgerKey.Holder("reddepot"), "due", "2", 6, "n"));
        Assert.Equal("That value " + ModuleSession.OtherSideText + ".", r2.Message);
        Assert.Equal(before, JsonSerializer.Serialize(s.Game.State, QuartermasterJson.Options));
    }

    [Fact]
    public void Guest_keyed_edit_checks_as_ledger_edit()
    {
        var (_, x) = Open();
        var b1 = LedgerKey.Piece(PhaseFixture.BlueOne);
        Assert.Equal("That is not a number.", x.Execute("p2", Blue, new KeyedEditIntent(b1, "cargo", "Sand", double.PositiveInfinity, "n")).Message);
        Assert.Equal("A manual change needs a note.", x.Execute("p2", Blue, new KeyedEditIntent(b1, "cargo", "Sand", 6, "  ")).Message);
        Assert.Equal("That value no longer exists.", x.Execute("p2", Blue, new KeyedEditIntent(b1, "gone", "Sand", 6, "n")).Message);
        Assert.Equal(IntentExecutor.GoneText, x.Execute("p2", Blue, new KeyedEditIntent(LedgerKey.Piece("nope"), "cargo", "Sand", 6, "n")).Message);
        Assert.Equal(IntentExecutor.NotUnderstoodText, x.Execute("p2", Blue, new KeyedEditIntent("", "cargo", "Sand", 6, "n")).Message);
    }

    [Fact]
    public void Guest_place_edit_needs_global_right()
    {
        var (s, x) = Open();
        var hex = Hex(s, PhaseFixture.BlueOne);
        // a place lives on the Global holder only
        var depot = x.Execute("p1", new HashSet<string> { "red" }, new KeyedEditIntent(LedgerKey.Holder("reddepot"), "flood", hex, 2, "n"));
        Assert.False(depot.Ok);
        Assert.Equal(IntentExecutor.NotUnderstoodText, depot.Message);
        Assert.Null(s.Game!.State.Keyed);
        var ok = x.Execute("p2", Blue, new KeyedEditIntent(Global, "flood", hex, 2, "n"));
        Assert.True(ok.Ok, ok.Message);
        Assert.Equal(2, KeyedValues.Get(s.Game.State, Global, "flood", hex));
    }

    [Fact]
    public void Guest_keyed_edit_bad_key_not_understood()
    {
        var (s, x) = Open();
        foreach (var key in new[] { "Swamp", "", new string('S', 65) })
        {
            var r = x.Execute("p2", Blue, new KeyedEditIntent(LedgerKey.Piece(PhaseFixture.BlueOne), "cargo", key, 6, "n"));
            Assert.Equal(IntentExecutor.NotUnderstoodText, r.Message);
        }
        Assert.Equal(IntentExecutor.NotUnderstoodText, x.Execute("p2", Blue, new KeyedEditIntent(Global, "due", "0", 6, "n")).Message);
        Assert.Equal(IntentExecutor.NotUnderstoodText, x.Execute("p2", Blue, new KeyedEditIntent(Global, "flood", "9999", 6, "n")).Message);
        Assert.Equal(IntentExecutor.NotUnderstoodText, x.Execute("p2", Blue, new KeyedEditIntent(Global, "due", null!, 6, "n")).Message);
        Assert.Null(s.Game!.State.Keyed);
    }

    [Fact]
    public void Guest_unkeyed_edit_on_keyed_property_not_understood()
    {
        var (s, x) = Open();
        var r = x.Execute("p2", Blue, new LedgerEditIntent(Global, "due", 6, "n"));
        Assert.False(r.Ok);
        Assert.Equal(IntentExecutor.NotUnderstoodText, r.Message);
        Assert.Null(Ledger.Get(s.Game!.State, Global, "due"));
    }

    [Fact]
    public void Guest_keyed_edit_unkeyed_property_not_understood()
    {
        var (s, x) = Open();
        var r = x.Execute("p2", Blue, new KeyedEditIntent(LedgerKey.Piece(PhaseFixture.BlueOne), "water", "Sand", 6, "n"));
        Assert.False(r.Ok);
        Assert.Equal(IntentExecutor.NotUnderstoodText, r.Message);
        Assert.Null(Ledger.Get(s.Game!.State, LedgerKey.Piece(PhaseFixture.BlueOne), "water"));
    }

    [Fact]
    public void Guest_formula_place_write_through_host()
    {
        var (s, x) = Open();
        var r = x.Execute("p2", Blue, new FormulaIntent("f-flood", PhaseFixture.BlueOne, null, new Dictionary<string, string>(), true));
        Assert.True(r.Ok, r.Message ?? r.Verdict?.Message);
        Assert.True(r.Verdict?.Applied, r.Verdict?.Message);
        Assert.Equal(3, KeyedValues.Get(s.Game!.State, Global, "flood", Hex(s, PhaseFixture.BlueOne)));
        Assert.Null(Ledger.Get(s.Game.State, Global, "flood"));
    }

    [Fact]
    public void Keyed_edit_intent_round_trips_with_its_tag()
    {
        GameIntent i = new KeyedEditIntent("h:global", "due", "2", 5, "n");
        var json = JsonSerializer.Serialize(i, QuartermasterJson.Options);
        Assert.Contains("\"$t\":\"keyedEdit\"", json);
        Assert.Equal(i, JsonSerializer.Deserialize<GameIntent>(json, QuartermasterJson.Options));
    }
}
