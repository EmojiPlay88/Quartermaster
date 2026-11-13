using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Turns;
using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 5d task 3: the replay bar over a TurnReplayModel built from a fixture turn (Alice sends two moves and a roll to Bob).</summary>
public sealed class ReplayBarTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-replaybar-" + Guid.NewGuid().ToString("N"));
    readonly List<FixtureModule> _fixtures = new();
    readonly List<ModuleSession> _sessions = new();

    public ReplayBarTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });
    static readonly PlayerDef Bob = new("p2", "Bob", new[] { "blue" });

    sealed class FakeProtector : IPasswordProtector
    {
        public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).Prepend((byte)0xEE).ToArray();
        public byte[] Unprotect(byte[] p) => p.Skip(1).Select(b => (byte)(b ^ 0x5A)).ToArray();
    }

    TurnIdentity Identity(string who, string gameId, string? player = null)
    {
        var id = TurnIdentity.Load(gameId, Path.Combine(_tmp, "turns-" + who), new FakeProtector());
        if (player != null) id.LocalPlayerId = player;
        return id;
    }

    ModuleSession Open()
    {
        var fx = PhaseFixture.Module();
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), null);
        _sessions.Add(s);
        return s;
    }

    static void MoveBy(GameSession g, string id, double dx)
    {
        var p = g.State.Pieces[id];
        using (g.BeginAction()) g.Execute(new MovePieces(new[] { id }, p.MapId, new PointD(p.Pos.X + dx, p.Pos.Y), null));
    }

    TurnReplayModel Model()
    {
        var alice = Open();
        alice.LoadSetup(alice.Model.Setups[0], Difficulty.FullyAuto, new[] { Alice, Bob });
        var g = alice.Game!;
        var id = g.State.Pieces.Keys.OrderBy(k => k, StringComparer.Ordinal).First();
        MoveBy(g, id, 10);
        MoveBy(g, id, 5);
        using (g.BeginAction()) g.Execute(new RollDice("Red attack", 2, 6, new[] { 3, 5 }));
        var t = TurnSender.Build(alice, Bob.Id, Identity("alice", g.State.GameId!, Alice.Id));
        TurnSender.AfterSend(alice);
        var bob = Open();
        return bob.OpenTurn(t, Bob.Id, Identity("bob", t.GameId));
    }

    static void Slice()
    {
        Dispatcher.UIThread.RunJobs();
        using var slice = new CancellationTokenSource(10);
        Dispatcher.UIThread.MainLoop(slice.Token);   // DispatcherTimers fire from the main loop
    }

    static void PumpUntil(Func<bool> done, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < until) Slice();
        Assert.True(done());
    }

    static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void Next_steps_and_updates_texts()
    {
        var m = Model();
        var bar = new ReplayBar();
        Assert.False(bar.IsVisible);
        bar.Bind(m);
        Assert.True(bar.IsVisible);
        Assert.Equal("Next", bar.NextButton.Content);
        Assert.Equal("Play all", bar.PlayAllButton.Content);
        Assert.Equal("Stop", bar.StopButton.Content);
        Assert.Equal("replay.next", bar.NextButton.Name);
        Assert.Equal("replay.playAll", bar.PlayAllButton.Name);
        Assert.Equal("replay.stop", bar.StopButton.Name);
        Assert.Equal(TurnUiModel.StepText(0, m.Count), bar.StepText);
        Assert.Equal("Press Next or Play all to replay the turn.", bar.LineText);
        Assert.True(bar.NextButton.IsEnabled);
        Assert.True(bar.PlayAllButton.IsEnabled);
        Assert.False(bar.StopButton.IsEnabled);
        Click(bar.NextButton);
        Assert.Equal(1, m.Step);
        Assert.Equal(TurnUiModel.StepText(1, m.Count), bar.StepText);
        Assert.Equal(m.CurrentLine, bar.LineText);
        Assert.False(bar.Playing);
        bar.Bind(null);
        Assert.False(bar.IsVisible);
    }

    [AvaloniaFact]
    public void Play_all_becomes_finish_then_completes()
    {
        var m = Model();
        var bar = new ReplayBar();
        bar.Bind(m);
        Click(bar.PlayAllButton);
        Assert.True(bar.Playing);
        Assert.Equal("Finish", bar.PlayAllButton.Content);
        Assert.True(bar.StopButton.IsEnabled);
        Click(bar.PlayAllButton);   // Finish: the rest at once
        Assert.True(m.Done);
        Assert.False(bar.Playing);
        Assert.Equal("Play all", bar.PlayAllButton.Content);
        Assert.False(bar.NextButton.IsEnabled);
        Assert.False(bar.PlayAllButton.IsEnabled);
        Assert.False(bar.StopButton.IsEnabled);

        // The timer alone also completes a replay.
        var m2 = Model();
        var bar2 = new ReplayBar();
        bar2.Bind(m2);
        Click(bar2.PlayAllButton);
        PumpUntil(() => m2.Done);
        Assert.False(bar2.Playing);
    }

    [AvaloniaFact]
    public void Stop_disables_and_stops_timer()
    {
        var m = Model();
        var bar = new ReplayBar();
        bar.Bind(m);
        Click(bar.PlayAllButton);
        Assert.True(bar.Playing);
        Click(bar.StopButton);
        Assert.False(bar.Playing);
        Assert.False(bar.StopButton.IsEnabled);
        Assert.True(bar.NextButton.IsEnabled);   // paused, not done
        Assert.Equal("Play all", bar.PlayAllButton.Content);
        var step = m.Step;
        var until = DateTime.UtcNow.AddMilliseconds(500);
        while (DateTime.UtcNow < until) Slice();   // a live timer would have stepped by now
        Assert.Equal(step, m.Step);
    }

    [AvaloniaFact]
    public void Model_exception_raises_failed()
    {
        var m = Model();
        var bar = new ReplayBar();
        bar.Bind(m);
        Exception? seen = null;
        bar.Failed += e => seen = e;
        m.Changed += () => throw new InvalidOperationException("boom");   // runs inside the bar's Next click
        Click(bar.NextButton);
        Assert.Equal("boom", seen?.Message);
        Assert.False(bar.Playing);
    }
}
