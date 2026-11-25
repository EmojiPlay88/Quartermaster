using System.Text.Json;
using Quartermaster.Core;

namespace Quartermaster.Core.Tests;

/// <summary>#31: GameSession.DeferChanged raises Changed once, with the last command, when the outer scope closes; nothing else changes.</summary>
public class DeferChangedTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static GameCommand[] Commands() => new GameCommand[]
    {
        new LedgerDelta(LedgerKey.Piece("u1"), "fuel", 0, 4, "formula", "f-a", null, null, "4", At: T0, Who: "Ann") { AppliedRun = "f-a" },
        new RollDice("TEST roll", 1, 6, new[] { 3 }, At: T0, Who: "Ann"),
        new LedgerDelta(LedgerKey.Piece("u2"), "fuel", 0, 5, "formula", "f-a", null, null, "5", At: T0, Who: "Ann") { AppliedRun = "f-a" },
        new AddLogLine(LogKind.Calc, "TEST: 2 units, 2 applied"),
    };

    private static GameSession New() => new(new GameState { GameId = "g" }, "sha");

    private static string Json(GameSession g) =>
        JsonSerializer.Serialize(new { Commands = g.Log.ToList(), g.Actions, g.Checkpoints, Lines = g.State.Log.Select(e => e with { At = default }).ToList(), g.State.Ledgers });

    [Fact]
    public void DeferChanged_raises_once_with_last_command()
    {
        var g = New();
        var seen = new List<GameCommand>();
        g.Changed += seen.Add;
        var cmds = Commands();
        using (g.BeginAction("TEST"))
        using (g.DeferChanged())
        {
            foreach (var c in cmds) g.Execute(c);
            Assert.Empty(seen);
            Assert.Equal(4, g.Log.Count);   // executed and logged at once
        }
        Assert.Single(seen);
        Assert.Same(cmds[^1], seen[0]);
        g.Execute(new AddLogLine(LogKind.Calc, "TEST after"));
        Assert.Equal(2, seen.Count);   // closed: each command raises again
    }

    [Fact]
    public void DeferChanged_log_identical_to_plain_execute()
    {
        var plain = New();
        using (plain.BeginAction("TEST"))
            foreach (var c in Commands()) plain.Execute(c);
        var deferred = New();
        using (deferred.BeginAction("TEST"))
        using (deferred.DeferChanged())
            foreach (var c in Commands()) deferred.Execute(c);
        Assert.Equal(Json(plain), Json(deferred));

        // Outside an action as well: each command is its own action, as without the scope.
        var plain2 = New();
        foreach (var c in Commands()) plain2.Execute(c);
        var deferred2 = New();
        using (deferred2.DeferChanged())
            foreach (var c in Commands()) deferred2.Execute(c);
        Assert.Equal(Json(plain2), Json(deferred2));
        Assert.Equal(plain2.Actions.Count, deferred2.Actions.Count);
    }

    [Fact]
    public void DeferChanged_nested_and_empty()
    {
        var g = New();
        var seen = new List<GameCommand>();
        g.Changed += seen.Add;
        using (g.DeferChanged()) { }
        Assert.Empty(seen);   // nothing ran: nothing raised

        var cmds = Commands();
        var outer = g.DeferChanged();
        using (g.DeferChanged())
        {
            g.Execute(cmds[0]);
            using (g.DeferChanged()) g.Execute(cmds[1]);
        }
        Assert.Empty(seen);   // inner scopes join the outer one
        g.Execute(cmds[2]);
        outer.Dispose();
        Assert.Equal(new[] { cmds[2] }, seen);
        outer.Dispose();      // a second dispose does nothing
        Assert.Single(seen);
    }
}
