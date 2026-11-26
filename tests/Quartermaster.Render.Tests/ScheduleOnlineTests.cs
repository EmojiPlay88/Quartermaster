using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B5 #62 online: the host's checks of a guest's <see cref="ScheduleIntent"/> (one neutral refusal for unknown, unreached, done and
/// no-actable-counter rows; a bad id is not understood), a guest's due list built from its own view, the schedule log line without names,
/// and the action port defaults. Same invented fixture as <see cref="ScheduleActionTests"/>.
/// </summary>
public class ScheduleOnlineTests : ScheduleTestBase
{
    private static readonly PlayerDef[] Both = { Alice, Bob };

    private ModuleSession Guest(ModuleSession host, IReadOnlySet<string> sides)
    {
        var guest = ModuleSession.Open(ModulePath, Path.Combine(Tmp, "gcache" + Sessions.Count), Path.Combine(Tmp, "groot" + Sessions.Count));
        Sessions.Add(guest);
        guest.EnterGuestMode();
        guest.LoadGuestRules(host.Rules!);
        guest.ApplySnapshot(PlayerView.For(host.Game!.State, host.Rules, host.Model, sides), sides);
        return guest;
    }

    [Fact]
    public void Guest_place_own_row_applies()
    {
        var t = Open(new[] { Row("r-red", RedRelief, formula: "f-water", label: "Red relief arrives") }, players: Both);
        ToSupply(t);
        var g = t.S.Game!;
        var actions = g.Actions.Count;
        var r = t.X.Execute("p1", Red, new ScheduleIntent("r-red"));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(new[] { "r-red" }, g.State.ScheduleDone);
        Assert.Equal(actions + 1, g.Actions.Count);
        Assert.Equal(9, Water(t.S, "rr1"));
        Assert.Contains(g.State.Log, l => l.Text == "Schedule: Red relief arrives" && l.Who == "p1");
    }

    [Fact]
    public void Guest_place_formula_lines_stamped_with_guest()
    {
        var t = Open(new[] { Row("r-red", RedRelief, formula: "f-water") }, players: Both);
        ToSupply(t);
        var g = t.S.Game!;
        var logs = g.State.Log.Count;
        Assert.True(t.X.Execute("p1", Red, new ScheduleIntent("r-red")).Ok);
        var added = g.State.Log.Skip(logs).ToList();
        Assert.Contains(added, l => l.Kind == LogKind.Calc);
        Assert.Contains(added, l => l.Kind == LogKind.Turn);
        Assert.All(added, l =>
        {
            Assert.Equal("p1", l.Who);
            Assert.NotEqual(DateTimeOffset.UnixEpoch, l.At);
        });
    }

    [Fact]
    public void Guest_withdraw_of_own_hidden_counter_redacted_for_others()
    {
        // Blue withdraws its hidden spy to the Main Map, then (second row) removes it: Red's view of the log never names it.
        var t = Open(new[]
        {
            Row("w-spy", "name() == \"Blue Spy\"", ScheduleAction.Leave, phase: "ph-weather", zone: "Main Map"),
            Row("x-spy", "name() == \"Blue Spy\"", ScheduleAction.Leave, phase: "ph-weather", zone: null),
        }, players: Both);
        t.C.Start();   // Weather: no side
        var g = t.S.Game!;
        var logs = g.State.Log.Count;
        Assert.True(t.X.Execute("p2", Blue, new ScheduleIntent("w-spy")).Ok);
        Assert.Equal(t.S.Model.Maps.First(m => m.Name == "Main Map").Id, g.State.Pieces["bs1"].MapId);
        Assert.Contains(g.State.Log.Skip(logs), l => l.Kind == LogKind.Move && l.PieceId == "bs1");
        AssertRedSeesNoSpy(t);
        Assert.True(t.X.Execute("p2", Blue, new ScheduleIntent("x-spy")).Ok);
        Assert.False(g.State.Pieces.ContainsKey("bs1"));
        Assert.Contains(g.State.Log.Skip(logs), l => l.Kind == LogKind.Delete && l.PieceId == "bs1");
        Assert.All(g.State.Log.Skip(logs), l => Assert.Equal("p2", l.Who));
        AssertRedSeesNoSpy(t);
    }

    private static void AssertRedSeesNoSpy(Table t)
    {
        var red = PlayerView.For(t.S.Game!.State, t.S.Rules, t.S.Model, Red);
        Assert.DoesNotContain(red.Log, l => l.Text.Contains("Spy"));
        Assert.DoesNotContain(red.Log, l => l.PieceId == "bs1");
    }

    [Fact]
    public void Guest_row_with_only_hidden_counters_refused_neutral()
    {
        var t = Open(new[] { Row("r-spy", "name() == \"Blue Spy\"", phase: "ph-weather") }, players: Both);
        t.C.Start();   // Weather: no side, so both may act
        var before = Json(t.S.Game!.State);
        var spy = t.X.Execute("p1", Red, new ScheduleIntent("r-spy"));
        var unknown = t.X.Execute("p1", Red, new ScheduleIntent("r-none"));
        Assert.False(spy.Ok);
        Assert.Equal(Refused, spy.Message);
        Assert.Equal(unknown.Message, spy.Message);
        Assert.Equal(before, Json(t.S.Game.State));
        Assert.True(t.X.Execute("p2", Blue, new ScheduleIntent("r-spy")).Ok);   // Blue's own (hidden) counter
    }

    [Fact]
    public void Guest_unknown_row_same_text()
    {
        var t = Open(new[] { Row("r-red", RedRelief), Row("r-later", RedRelief, turn: 2), Row("r-blue", BlueRelief) }, players: Both);
        ToSupply(t);
        Assert.True(t.X.Execute("p1", Red, new ScheduleIntent("r-red")).Ok);
        var before = Json(t.S.Game!.State);
        foreach (var id in new[] { "r-none", "r-later", "r-red", "r-blue" })   // unknown, unreached, done, another side's counters
        {
            var r = t.X.Execute("p1", Red, new ScheduleIntent(id));
            Assert.False(r.Ok, id);
            Assert.Equal(Refused, r.Message);
        }
        Assert.Equal(before, Json(t.S.Game.State));
    }

    [Fact]
    public void Guest_bad_id_not_understood()
    {
        var t = Open(new[] { Row("r-red", RedRelief) }, players: Both);
        ToSupply(t);
        var before = Json(t.S.Game!.State);
        foreach (var id in new[] { "", null!, new string('x', IntentExecutor.MaxId + 1) })
        {
            var r = t.X.Execute("p1", Red, new ScheduleIntent(id));
            Assert.False(r.Ok);
            Assert.Equal(IntentExecutor.NotUnderstoodText, r.Message);
        }
        Assert.Equal(before, Json(t.S.Game.State));

        var json = JsonSerializer.Serialize<GameIntent>(new ScheduleIntent("r-red"), QuartermasterJson.Options);
        Assert.Equal("{\"$t\":\"schedule\",\"rowId\":\"r-red\"}", json);
        Assert.Equal("r-red", Assert.IsType<ScheduleIntent>(JsonSerializer.Deserialize<GameIntent>(json, QuartermasterJson.Options)).RowId);
    }

    [Fact]
    public void Guest_other_phase_and_game_over_refused()
    {
        var t = Open(new[] { Row("r-blue", BlueRelief) }, p => p with { LastTurn = 1 }, Both);
        ToSupply(t);   // Red's Supply
        var r = t.X.Execute("p2", Blue, new ScheduleIntent("r-blue"));
        Assert.Equal("That can't be done during another side's phase.", r.Message);
        for (var i = 0; i < 20 && t.S.Game!.State.GameOver == null; i++) t.C.Advance();
        r = t.X.Execute("p2", Blue, new ScheduleIntent("r-blue"));
        Assert.False(r.Ok);
        Assert.Equal("The game is over.", r.Message);
    }

    [Fact]
    public void Guest_due_list_from_own_view()
    {
        var t = Open(new[] { Row("r-red", RedRelief), Row("r-blue", BlueRelief), Row("r-spy", "name() == \"Blue Spy\""), Row("r-none", "name() == \"Nobody\"") },
            players: Both);
        ToSupply(t);
        var red = Guest(t.S, Red);
        var rows = DueModel.Rows(red);
        var row = Assert.Single(rows);
        Assert.Equal("r-red", row.RowId);
        Assert.Equal(new[] { "rr1", "rr2" }, row.PieceIds.OrderBy(x => x));

        var blue = Guest(t.S, Blue);
        Assert.Equal(new[] { "r-blue", "r-spy" }, DueModel.Rows(blue).Select(r => r.RowId));
    }

    [Fact]
    public void Schedule_log_line_has_no_names()
    {
        var t = Open(new[] { Row("w-red", RedAll, ScheduleAction.Leave) }, players: Both);
        ToSupply(t);
        var logs = t.S.Game!.State.Log.Count;
        Assert.True(t.X.Execute("p1", Red, new ScheduleIntent("w-red")).Ok);
        var line = Assert.Single(t.S.Game.State.Log.Skip(logs), l => l.Kind == LogKind.Turn);
        Assert.Equal("Schedule: Withdrawals, turn 1", line.Text);
        Assert.DoesNotContain("Red", line.Text);
        Assert.DoesNotContain("2", line.Text);

        // Blue's view of the log keeps the line as it is.
        var blue = PlayerView.For(t.S.Game.State, t.S.Rules, t.S.Model, Blue);
        Assert.Contains(blue.Log, l => l.Text == "Schedule: Withdrawals, turn 1");
    }

    [Fact]
    public void Old_port_default_refuses()
    {
        var t = Open(new[] { Row("r-red", RedRelief) });
        ToSupply(t);
        var before = Json(t.S.Game!.State);
        IActionPort old = new OldPort(t.L);
        Assert.Equal(IntentExecutor.NotUnderstoodText, old.ActOnSchedule("r-red"));
        Assert.Equal(before, Json(t.S.Game.State));

        var sw = new ActionPortSwitch(t.L);
        Assert.Null(sw.ActOnSchedule("r-red"));   // the switch forwards to the local port
        Assert.Equal(new[] { "r-red" }, t.S.Game.State.ScheduleDone);
        Assert.Equal(Refused, sw.ActOnSchedule("r-red"));
    }

    /// <summary>A port written before B5: it does not implement <see cref="IActionPort.ActOnSchedule"/>.</summary>
    private sealed class OldPort(IActionPort inner) : IActionPort
    {
        public bool IsRemote => inner.IsRemote;
        public void Move(IReadOnlyList<MovePieces> moves) => inner.Move(moves);
        public void PieceCommand(GameCommand command) => inner.PieceCommand(command);
        public void PieceCommands(IReadOnlyList<GameCommand> commands) => inner.PieceCommands(commands);
        public FormulaVerdict ApplyFormula(FormulaIntent intent) => inner.ApplyFormula(intent);
        public Task<FormulaVerdict> ApplyFormulaAsync(FormulaIntent intent) => inner.ApplyFormulaAsync(intent);
        public bool ApplyTrace(string? runId, string? pieceId, FormulaTrace trace, bool calculated) => inner.ApplyTrace(runId, pieceId, trace, calculated);
        public (int Applied, IReadOnlyList<string> Failed) ApplyMissingInputs(PendingRun run, IReadOnlyDictionary<string, string> values) =>
            inner.ApplyMissingInputs(run, values);
        public void EditLedger(string key, string propertyId, double after, string note) => inner.EditLedger(key, propertyId, after, note);
        public void Roll(DiceSpec spec, string purpose) => inner.Roll(spec, purpose);
        public void RecordRoll(RollDice roll) => inner.RecordRoll(roll);
        public void Tick(int index, bool ticked) => inner.Tick(index, ticked);
        public void Ack(int warningIndex) => inner.Ack(warningIndex);
        public string? NextPhase() => inner.NextPhase();
        public void HideAlert(string alertId, string? name) => inner.HideAlert(alertId, name);
        public void Chat(string text) => inner.Chat(text);
        public void SetTurn(int turn, string? phase) => inner.SetTurn(turn, phase);
        public void SetNotes(string text) => inner.SetNotes(text);
        public void Undo() => inner.Undo();
        public void Redo() => inner.Redo();
        public void StepBack(CheckpointRow row) => inner.StepBack(row);
    }
}
