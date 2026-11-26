using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>B5 x B6 (merge): on phase entry the turn keys of past turns are dropped before the victory conditions are checked, so a victory
/// condition reading a turn-keyed property sees the state after the drop. Invented Riverland / Red-Blue names only.</summary>
public class TurnKeyedVictoryTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-tkv-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();
    private const string Global = "h:global";

    public TurnKeyedVictoryTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static readonly PropertyDef Barges = new()
    {
        Id = "barges", Name = "BargesDue", Scope = PropertyScope.Holders, KeyedBy = "turn", Visibility = Visibility.Public,
    };

    /// <summary>One phase per turn (each Next Phase starts a new turn), the turn-keyed "BargesDue" and one victory condition.</summary>
    private (ModuleSession S, PhaseController C) Open(string victory)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), Path.Combine(_tmp, "root" + _sessions.Count));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto, new[] { new PlayerDef("p1", "Ann", new[] { "red" }), new PlayerDef("p2", "Bob", new[] { "blue" }) });
        s.NewRules("t");
        s.UpdateRules(_ =>
        {
            var p = PhaseFixture.Pack(s.Archive.Sha256);
            return p with
            {
                Format = 4,
                Properties = p.Properties.Append(Barges).ToList(),
                Phases = new() { new PhaseDef { Id = "ph-orders", Name = "Orders" } },
                Blocks = new(),
                Victory = new() { new VictoryCondition { Id = "v-barges", Condition = victory, SideId = "red", Message = "Barges home" } },
            };
        });
        Assert.DoesNotContain(RulesValidator.Validate(s.Rules!), i => i.Severity == RulesIssueSeverity.Error);
        Func<GameCommand, GameState, GameCommand> prepare = (c, st) => CommandLog.Prepare(c, st, s);
        return (s, new PhaseController(s, prepare));
    }

    private static void Put(ModuleSession s, string turn, double v) =>
        s.Game!.Execute(new LedgerDelta(Global, "barges", 0, v, "manual", null, null, "test", null) { ValueKey = turn });

    [Fact]
    public void Victory_on_entry_sees_past_turn_keys_already_dropped()
    {
        var (s, c) = Open("keyed(BargesDue, 2) >= 5");
        c.Start();      // turn 1
        c.Advance();    // turn 2
        Put(s, "2", 5);
        Assert.Null(s.Game!.State.GameOver);   // checked on entry to turn 2, before the value was written
        c.Advance();    // into turn 3: key 2 is dropped first, so the condition no longer holds
        Assert.Equal(3, s.Game.State.Turn.Turn);
        Assert.Empty(KeyedValues.Keys(s.Game.State, Global, "barges"));
        Assert.Null(s.Game.State.GameOver);
    }

    [Fact]
    public void Victory_on_entry_reading_the_current_turn_key_ends_the_game_after_the_drop()
    {
        var (s, c) = Open("keyed(BargesDue, turn()) >= 1");
        c.Start();      // turn 1
        c.Advance();    // turn 2
        Put(s, "1", 9);
        Put(s, "3", 1);
        var mark = s.Game!.Log.Count;
        c.Advance();    // into turn 3: key 1 goes, then the victory condition (key 3) is met
        var added = s.Game.Log.Skip(mark).ToList();
        int drop = added.FindIndex(x => x is DropPastTurns), end = added.FindIndex(x => x is EndGame);
        Assert.True(drop >= 0 && end > drop, $"drop {drop}, end {end}");
        Assert.Equal(new[] { "3" }, KeyedValues.Keys(s.Game.State, Global, "barges"));
        Assert.NotNull(s.Game.State.GameOver);
        Assert.Equal("v-barges", s.Game.State.GameOver!.VictoryId);

        // keyed values stay editable after game over, as ledgers are
        Put(s, "3", 2);
        Assert.Equal(2, KeyedValues.Get(s.Game.State, Global, "barges", "3"));
        s.Game.Undo();

        s.Game.Undo();  // one undo: the advance, the drop and the end together
        Assert.Null(s.Game.State.GameOver);
        Assert.Equal(2, s.Game.State.Turn.Turn);
        Assert.Equal(new[] { "1", "3" }, KeyedValues.Keys(s.Game.State, Global, "barges"));
    }
}
