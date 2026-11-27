using System.Collections;
using System.Reflection;
using System.Text.Json.Serialization;
using Quartermaster.Core;

namespace Quartermaster.Core.Tests;

/// <summary>B8 #8: former side ids (Riverland: <c>league</c> is now <c>north</c>) translated in a state and in commands.</summary>
public class FormerSidesTests
{
    static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string> { ["league"] = "north", ["union"] = "north" };

    static PendingRun Run(string id, string? side) => new(id, "f-toll", "River toll", "start", Array.Empty<PendingUnit>(), true) { AskSideId = side };

    static LogEntry Line(long seq, string? side) => new(seq, LogKind.System, "line " + seq, null, null, null, DateTimeOffset.UnixEpoch, SideId: side);

    [Fact]
    public void State_members_translated()
    {
        var s = new GameState
        {
            Players = [new PlayerDef("p1", "Ann", ["league", "north", "south"]), new PlayerDef("p2", "Bo", ["hills"])],
            Pending = [Run("r1", "league"), Run("r2", "south"), Run("r3", null)],
            Log = [Line(1, "league"), Line(2, "south"), Line(3, null)],
            GameOver = new GameOverState("Northern League wins", ["league", "south", "north"], "v1", null),
            IsPlayerView = true,
            ViewSides = ["hills", "league", "south"],
        };
        var bob = s.Players[1];
        Assert.True(FormerSides.Uses(s, Map));
        Assert.True(FormerSides.Apply(s, Map));
        Assert.Equal(["north", "south"], s.Players[0].Sides);
        Assert.Same(bob, s.Players[1]);
        Assert.Equal(["north", "south", null], s.Pending.Select(r => r.AskSideId));
        Assert.Equal(["north", "south", null], s.Log.Select(l => l.SideId));
        Assert.Equal("line 1", s.Log[0].Text);
        Assert.Equal(["north", "south"], s.GameOver!.Winners!);
        Assert.Equal("Northern League wins", s.GameOver.Text);
        Assert.Equal(["hills", "north", "south"], s.ViewSides);
        Assert.False(FormerSides.Uses(s, Map));
        Assert.False(FormerSides.Apply(s, Map));
    }

    [Fact]
    public void Command_members_translated()
    {
        var at = DateTimeOffset.UnixEpoch;
        GameCommand Map1(GameCommand c) => FormerSides.Apply(c, Map);

        Assert.Equal("north", ((RemovePiece)Map1(new RemovePiece("u1") { SideId = "league" })).SideId);
        Assert.Equal("north", ((LedgerDelta)Map1(new LedgerDelta("p:u1", "supply", 1, 2, "formula", "f", null, null, null, SideId: "league"))).SideId);
        var rd = (RollDice)Map1(new RollDice("Toll", 1, 6, [4], SideId: "league") { FormulaId = "league" });
        Assert.Equal("north", rd.SideId);
        Assert.Equal("league", rd.FormulaId);   // a formula id, not a side
        Assert.Equal(6, rd.Sides);
        Assert.Equal("north", ((AddLogLine)Map1(new AddLogLine(LogKind.System, "text league", SideId: "league"))).SideId);
        Assert.Equal("text league", ((AddLogLine)Map1(new AddLogLine(LogKind.System, "text league", SideId: "league"))).Text);
        var sp = (SetPlayers)Map1(new SetPlayers([new PlayerDef("p1", "Ann", ["league", "union", "south"]), new PlayerDef("p2", "Bo", ["hills"])], at));
        Assert.Equal(["north", "south"], sp.Players[0].Sides);
        Assert.Equal(["hills"], sp.Players[1].Sides);
        var qr = (QueueRuns)Map1(new QueueRuns([Run("r1", "league"), Run("r2", "south")], at));
        Assert.Equal(["north", "south"], qr.Runs.Select(r => r.AskSideId));
        Assert.Equal(["north"], ((EndGame)Map1(new EndGame("won", ["league", "north"], "v1"))).Winners!);
        Assert.Equal("north", ((TickChecklist)Map1(new TickChecklist(0, true, "Pay toll") { SideId = "league" })).SideId);
        Assert.Equal("north", ((AcknowledgeWarning)Map1(new AcknowledgeWarning(0, "Low supply") { SideId = "league" })).SideId);
        // SealAnswer (B7) carries no side id: its run's side is on the PendingRun; unchanged.
        var seal = new SealAnswer("r1", new Dictionary<string, string> { ["x"] = "league" }, "Northern League has answered River toll (sealed)");
        Assert.Same(seal, Map1(seal));
    }

    [Fact]
    public void Unchanged_command_same_instance()
    {
        GameCommand[] cmds =
        [
            new RemovePiece("u1") { SideId = "south" },
            new RemovePiece("u2"),
            new LedgerDelta("p:u1", "supply", 1, 2, "formula", "f", null, null, null, SideId: "north"),
            new RollDice("Toll", 1, 6, [4]),
            new AddLogLine(LogKind.System, "league"),
            new SetPlayers([new PlayerDef("p1", "Ann", ["north", "north"])]),
            new QueueRuns([Run("r1", "south")]),
            new EndGame("draw", null, null),
            new TickChecklist(0, true, "x") { SideId = "hills" },
            new AcknowledgeWarning(0, "x"),
            new AddPiece("u1", "t", "", "m", new Quartermaster.Vassal.PointD(0, 0), null),
        ];
        foreach (var c in cmds)
        {
            Assert.Same(c, FormerSides.Apply(c, Map));
            Assert.False(FormerSides.Uses(c, Map));
        }
    }

    [Fact]
    public void Uses_false_for_unrelated_ids()
    {
        var s = new GameState
        {
            Players = [new PlayerDef("league", "league", ["north"])],   // a player id or name is not a side id
            Pending = [Run("league", "south")],
            Log = [new LogEntry(1, LogKind.System, "league", "league", "league", "league", DateTimeOffset.UnixEpoch)],
            GameOver = new GameOverState("league", null, "league", null),
            HiddenAlerts = ["league"],
            ScheduleDone = ["league"],
        };
        var before = System.Text.Json.JsonSerializer.Serialize(s, QuartermasterJson.Options);
        Assert.False(FormerSides.Uses(s, Map));
        Assert.False(FormerSides.Apply(s, Map));
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(s, QuartermasterJson.Options));
        Assert.False(FormerSides.Uses(new AddLogLine(LogKind.System, "league", PieceId: "league", Who: "league"), Map));
        Assert.False(FormerSides.Uses(new RollDice("league", 1, 6, [1], Expression: "league") { FormulaId = "league" }, Map));
        var empty = new Dictionary<string, string>();
        Assert.False(FormerSides.Uses(new RemovePiece("u") { SideId = "league" }, empty));
    }

    /// <summary>Every side-id member reachable from a state or a command is translated: a later batch that adds one fails here until
    /// <see cref="FormerSides"/> maps it. Members: <c>string</c> named <c>…SideId</c>, string lists named <c>Sides</c>, <c>Winners</c>,
    /// <c>ViewSides</c> or <c>…SideIds</c>, on the named types, every registered command and every Core type reachable from them.</summary>
    [Fact]
    public void Side_bearing_members_are_all_mapped()
    {
        var roots = new List<Type> { typeof(GameState), typeof(PlayerDef), typeof(PendingRun), typeof(LogEntry), typeof(GameOverState) };
        var derived = typeof(GameCommand).GetCustomAttributes<JsonDerivedTypeAttribute>().Select(a => a.DerivedType).ToList();
        Assert.Contains(typeof(SealAnswer), derived);
        Assert.Contains(typeof(TickChecklist), derived);
        roots.AddRange(derived);

        var found = new SortedSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<Type>();
        void Visit(Type t)
        {
            if (t.IsArray) { Visit(t.GetElementType()!); return; }
            if (t.IsGenericType) foreach (var g in t.GetGenericArguments()) Visit(g);
            if (t.Namespace?.StartsWith("Quartermaster", StringComparison.Ordinal) != true || t.IsEnum || !seen.Add(t)) return;
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                var pt = p.PropertyType;
                if (pt == typeof(string))
                {
                    if (p.Name.EndsWith("SideId", StringComparison.Ordinal)) found.Add(t.Name + "." + p.Name);
                }
                else if (typeof(IEnumerable<string>).IsAssignableFrom(pt))
                {
                    if (p.Name is "Sides" or "Winners" or "ViewSides" || p.Name.EndsWith("SideIds", StringComparison.Ordinal))
                        found.Add(t.Name + "." + p.Name);
                }
                else if (pt != typeof(object) && !pt.IsPrimitive) Visit(pt);
            }
        }
        foreach (var r in roots) Visit(r);

        Assert.Contains("TickChecklist.SideId", found);
        Assert.Contains("PlayerDef.Sides", found);
        Assert.DoesNotContain("RollDice.Sides", found);   // an int: the die's faces
        Assert.DoesNotContain("DiceEntry.Sides", found);
        var unmapped = found.Except(FormerSides.MappedMembers).ToList();
        Assert.True(unmapped.Count == 0, "Side-id members not translated by FormerSides: " + string.Join(", ", unmapped));
        var stale = FormerSides.MappedMembers.Except(found).ToList();
        Assert.True(stale.Count == 0, "FormerSides.MappedMembers names members that do not exist: " + string.Join(", ", stale));
    }
}
