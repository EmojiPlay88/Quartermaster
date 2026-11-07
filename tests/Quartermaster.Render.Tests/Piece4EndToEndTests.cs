using System.Diagnostics;
using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;
using Xunit.Abstractions;

namespace Quartermaster.Render.Tests;

/// <summary>Piece 4 end to end: invented PhaseFixture module and pack, temp roots only, no UI.</summary>
public class Piece4EndToEndTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-p4e2e-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();

    public Piece4EndToEndTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private (ModuleSession S, PhaseController C, HistoryController H) Make(Difficulty d)
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _sessions.Add(s);
        s.SetRoller((n, _) => Enumerable.Repeat(3, n).ToList());
        s.LoadSetup(s.Model.Setups[0], d);
        s.NewRules("t");
        s.UpdateRules(_ => PhaseFixture.Pack(s.Archive.Sha256));
        Func<GameCommand, GameState, GameCommand> prepare = (c, _) => c;
        var phases = new PhaseController(s, prepare);
        return (s, phases, new HistoryController(s, phases, prepare));
    }

    private static string Json(GameState st) => JsonSerializer.Serialize(st, QuartermasterJson.Options);
    private static MovePieces Move(string id, int x) => new(new[] { id }, "Main Map", new PointD(x, 50), null);

    [Fact]
    public void E2E_FullyAuto_TwoTurns_StepBackEachCheckpoint_MatchesRecorded()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        var g = s.Game!;
        var recorded = new Dictionary<int, string> { [0] = Json(g.Genesis) };
        var seen = g.Checkpoints.Count;
        void Record()
        {
            while (seen < g.Checkpoints.Count)
            {
                var cp = g.Checkpoints[seen++];
                if (cp.LogIndex == g.Log.Count) recorded[cp.LogIndex] = Json(g.State);
            }
        }

        c.Start();
        Record();
        var steps = 0;
        while (g.State.Position!.Turn < 3 && steps++ < 60)
        {
            if (steps % 3 == 0) c.ExecuteMove(new[] { Move(PhaseFixture.RedOne, 40 + steps * 5) });
            c.Advance();
            Record();
        }
        Assert.Equal(3, g.State.Position!.Turn);
        Assert.True(recorded.Count >= 8, "expected at least eight checkpoints, got " + recorded.Count);
        var played = Json(g.State);
        var playedLog = g.Log.Count;

        // Rewind to every recorded checkpoint (newest first), compare, then redo everything back.
        foreach (var (index, json) in recorded.OrderByDescending(kv => kv.Key))
        {
            g.RewindTo(index);
            Assert.Equal(json, Json(g.State));
            for (var i = 0; i < 500 && g.CanRedo; i++) g.Redo();
            Assert.Equal(played, Json(g.State));
            Assert.Equal(playedLog, g.Log.Count);
        }

        // The user-facing path: Step Back to each listed row; the position and pieces match what was recorded there.
        var rows = h.Checkpoints();
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            h.StepBack(row);
            Assert.Contains(g.Log, x => x is SteppedBack);
            if (recorded.TryGetValue(row.LogIndex, out var json))
            {
                var expected = JsonSerializer.Deserialize<GameState>(json, QuartermasterJson.Options)!;
                Assert.Equal(expected.Position, g.State.Position);
                Assert.Equal(JsonSerializer.Serialize(expected.Pieces, QuartermasterJson.Options), JsonSerializer.Serialize(g.State.Pieces, QuartermasterJson.Options));
            }
            for (var i = 0; i < 500 && h.CanRedo; i++) h.Redo();
            Assert.Equal(played, Json(g.State));
        }
    }

    [Fact]
    public void E2E_SaveLoad_StepBackToGameStart()
    {
        var (s, c, h) = Make(Difficulty.FullyAuto);
        var startPieces = JsonSerializer.Serialize(s.Game!.State.Pieces, QuartermasterJson.Options);
        c.Start();
        c.Advance();
        c.Advance();
        c.ExecuteMove(new[] { Move(PhaseFixture.RedOne, 150) });
        c.Advance();
        var played = Json(s.Game.State);

        var path = Path.Combine(_tmp, "g.qsave");
        s.SaveNative(path);
        s.LoadNative(path);
        var g = s.Game!;
        Assert.Equal(played, Json(g.State));
        Assert.False(g.RebasedOnLoad);

        var rows = h.Checkpoints();
        var first = rows[^1];
        Assert.Equal("game start", first.Phase);
        h.StepBack(first);
        Assert.Null(g.State.Position);
        Assert.Equal(startPieces, JsonSerializer.Serialize(g.State.Pieces, QuartermasterJson.Options));
        Assert.Contains(g.Log, x => x is SteppedBack);

        // And forward again, byte for byte.
        for (var i = 0; i < 500 && h.CanRedo; i++) h.Redo();
        Assert.Equal(played, Json(g.State));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Perf_SyntheticGame_UndoAndStepBack()
    {
        const int pieces = 1200, blocks = 50, perBlock = 100;   // 5,000 commands in 50 checkpoints
        var state = new GameState();
        state.MapOrder.Add("m1");
        var g = new GameSession(state, "sha");
        for (var i = 0; i < pieces; i++)
            g.Execute(new AddPiece("p" + i, "piece;;;a.png;p" + i, "", "m1", new PointD(i % 100, i / 100), null));

        var rnd = new Random(7);
        var phase = 0;
        for (var b = 0; b < blocks; b++)
        {
            for (var k = 0; k < perBlock - 1; k++)
            {
                var id = "p" + rnd.Next(pieces);
                GameCommand cmd = (k % 5) switch
                {
                    0 or 1 or 2 => (GameCommand)new MovePieces(new[] { id }, "m1", new PointD(rnd.Next(500), rnd.Next(500)), null),
                    3 => new LedgerDelta("k" + (k % 7), "fuel", k, k + 1, "manual", null, null, null, null),
                    _ => new SetNotes("note " + b + "/" + k),
                };
                g.Execute(cmd);
            }
            var from = new PhasePosition(1, 1, "ph" + phase, phase);
            phase++;
            g.Execute(new AdvancePhase(from, new PhasePosition(1, 1, "ph" + phase, phase), "P" + (phase - 1), "P" + phase));
        }
        Assert.Equal(blocks + 1, g.Checkpoints.Count);
        var total = g.Log.Count;
        _out.WriteLine($"log commands: {total}, checkpoints: {g.Checkpoints.Count}");

        var sw = Stopwatch.StartNew();
        g.Undo();
        var undoMs = sw.Elapsed.TotalMilliseconds;
        _out.WriteLine($"Undo (last action): {undoMs:F1} ms");
        g.Redo();

        var target = g.Checkpoints[^11].LogIndex;   // 10 checkpoints back
        sw.Restart();
        g.RewindTo(target);
        var backMs = sw.Elapsed.TotalMilliseconds;
        _out.WriteLine($"Step back 10 checkpoints: {backMs:F1} ms");

        sw.Restart();
        g.RewindTo(g.Checkpoints[0].LogIndex);
        var startMs = sw.Elapsed.TotalMilliseconds;
        _out.WriteLine($"Step back to game start (cold): {startMs:F1} ms");

        Assert.True(undoMs < 500, $"Undo took {undoMs:F1} ms");
        Assert.True(backMs < 3000, $"Step back of 10 checkpoints took {backMs:F1} ms");
    }
}
