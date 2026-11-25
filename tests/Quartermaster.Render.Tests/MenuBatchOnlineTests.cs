using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B4 menu batch online (invented Riverland data): a guest sends one intent per batch; the host checks every row as a single run, pins
/// the dice per sender and counter, previews the chain on a clone and applies every row as one action, or nothing.
/// </summary>
[Collection(AppDataRootEnv.Name)]
public class MenuBatchOnlineTests : OtherPiecesTestBase
{
    private static readonly Dictionary<string, string> None = new();

    private static FormulaBatchIntent Batch(IEnumerable<string?> pieces, bool preview = false, bool calculate = false) =>
        new("f", pieces.Select(p => new FormulaBatchRow(p, None, null)).ToList(), calculate, preview);

    private static IntentResult Send(IntentExecutor x, IReadOnlySet<string> sides, FormulaBatchIntent i) =>
        x.Execute(sides == North ? "p1" : "p2", sides, i);

    private static List<RollDice> RollsSince(ModuleSession s, int mark) => s.Game!.Log.Skip(mark).OfType<RollDice>().ToList();

    private (ModuleSession S, PhaseController C, IntentExecutor X) Host(Difficulty d = Difficulty.FullyAuto, bool group = false, bool hiding = false,
        IEnumerable<PhaseDef>? phases = null)
    {
        var (s, c) = Open(d, new[] { GroupRollRunTests.Ford(group) }, hiding: hiding, players: new[] { Alice, Bob }, phases: phases);
        GroupRollRunTests.Counting(s);
        return (s, c, new IntentExecutor(s, c));
    }

    [Fact]
    public void Guest_batch_one_action_on_host()
    {
        var (s, _, x) = Host();
        var g = s.Game!;
        var actions = g.Actions.Count;
        var mark = g.Log.Count;
        var r = Send(x, North, Batch(new[] { "n1", "n2" }));
        Assert.True(r.Ok, r.Message);
        var v = r.BatchVerdict!;
        Assert.True(v.Applied);
        Assert.Equal(2, v.Rows.Count);
        Assert.All(v.Rows, row => Assert.True(row.Applied));
        Assert.Equal(actions + 1, g.Actions.Count);
        Assert.Equal("Ford crossing on 2 units", g.UndoLabel);
        var rolls = RollsSince(s, mark);
        Assert.Equal(2, rolls.Count);
        Assert.Equal(rolls[0].Results.Sum(), Str(s, "n1"));
        Assert.Equal(rolls[1].Results.Sum(), Str(s, "n2"));
        // The verdict travels as JSON and back.
        var json = JsonSerializer.Serialize(v, QuartermasterJson.Options);
        Assert.Equal(2, JsonSerializer.Deserialize<FormulaBatchVerdict>(json, QuartermasterJson.Options)!.Rows.Count);
        var intentJson = JsonSerializer.Serialize<GameIntent>(Batch(new[] { "n1" }), QuartermasterJson.Options);
        Assert.Contains("\"$t\":\"formulaBatch\"", intentJson);
        Assert.IsType<FormulaBatchIntent>(JsonSerializer.Deserialize<GameIntent>(intentJson, QuartermasterJson.Options));
    }

    [Fact]
    public void Guest_batch_preview_then_apply_same_dice()
    {
        var (s, _, x) = Host();
        var preview = Send(x, South, Batch(new[] { "s1", "s2" }, preview: true)).BatchVerdict!;
        Assert.False(preview.Applied);
        Assert.Empty(s.Game!.Log.OfType<RollDice>());   // a preview logs nothing
        var d1 = preview.Rows[0].Rolls!["D"].Sum();
        var d2 = preview.Rows[1].Rolls!["D"].Sum();
        Assert.Equal(7, Str(s, "s1"));
        var applied = Send(x, South, Batch(new[] { "s1", "s2" }));
        Assert.True(applied.Ok, applied.Message);
        Assert.Equal(d1, Str(s, "s1"));
        Assert.Equal(d2, Str(s, "s2"));
    }

    [Fact]
    public async Task Guest_batch_through_menu_model()
    {
        var (host, _, x) = Host();
        var guest = Guest(host, hiding: false);
        var port = new LoopbackPort(i => x.Execute("p2", South, i).BatchVerdict!);
        var b = new MenuBatchModel(guest, port, "f", new[] { "s1", "s2" });
        Assert.True(b.HostCalculates);
        var d = b.Variables.ToList().IndexOf("D");
        var (n, _) = await b.SendAsync(calculate: true, preview: true);
        Assert.Equal(0, n);
        var shown = b.Row(0).Cells[d];
        Assert.Equal(FormulaWindowRules.HostCalculatedText(2), b.Status);
        var (applied, message) = await b.SendAsync(calculate: false);
        Assert.Equal(2, applied);
        Assert.Null(message);
        Assert.Equal("The host applied 2 run(s) as one step.", b.Status);
        Assert.StartsWith(Str(host, "s1")!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), shown);
    }

    [Fact]
    public void Guest_batch_pins_survive_reorder()
    {
        var (s, _, x) = Host();
        var preview = Send(x, North, Batch(new[] { "n1", "n2" }, preview: true)).BatchVerdict!;
        var d1 = preview.Rows[0].Rolls!["D"].Sum();
        var d2 = preview.Rows[1].Rolls!["D"].Sum();
        Assert.NotEqual(d1, d2);
        var again = Send(x, North, Batch(new[] { "n2", "n1" }, preview: true)).BatchVerdict!;
        Assert.Equal(d2, again.Rows[0].Rolls!["D"].Sum());
        var r = Send(x, North, Batch(new[] { "n2", "n1" }));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(d1, Str(s, "n1"));
        Assert.Equal(d2, Str(s, "n2"));
    }

    [Fact]
    public void Guest_batch_group_rolls_once()
    {
        var (s, _, x) = Host(group: true);
        var preview = Send(x, North, Batch(new[] { "n1", "n2" }, preview: true)).BatchVerdict!;
        Assert.Equal(preview.Rows[0].Rolls!["D"], preview.Rows[1].Rolls!["D"]);
        var mark = s.Game!.Log.Count;
        Assert.True(Send(x, North, Batch(new[] { "n2", "n1" })).Ok);
        Assert.Single(RollsSince(s, mark));
        Assert.Equal(preview.Rows[0].Rolls!["D"].Sum(), Str(s, "n1"));
        Assert.Equal(Str(s, "n1"), Str(s, "n2"));
    }

    [Fact]
    public void Guest_batch_hidden_counter_refused()
    {
        var (s, _, x) = Host(hiding: true);
        var before = JsonSerializer.Serialize(s.Game!.State, QuartermasterJson.Options);
        // n3 is hidden by its owner: to Southern Compact it does not exist, exactly as an id that never existed.
        foreach (var id in new[] { "n3", "nope" })
        {
            var r = Send(x, South, Batch(new[] { "s1", id }));
            Assert.False(r.Ok);
            Assert.Equal("Row 2: That unit no longer exists.", r.Message);
            Assert.Equal("Row 2: That unit no longer exists.", r.BatchVerdict!.Message);
        }
        Assert.Equal(before, JsonSerializer.Serialize(s.Game.State, QuartermasterJson.Options));
    }

    [Fact]
    public void Guest_batch_other_side_refused()
    {
        var (s, _, x) = Host();
        var r = Send(x, South, Batch(new[] { "s1", "n1" }));
        Assert.False(r.Ok);
        Assert.Equal($"Row 2: That unit {ModuleSession.OtherSideText}.", r.Message);
        Assert.Equal(7, Str(s, "s1"));
        Assert.Empty(s.Game!.Log.OfType<RollDice>());
    }

    [Fact]
    public void Guest_batch_pending_unit_refused()
    {
        var (s, c, x) = Host(Difficulty.SemiAuto, phases: GroupRollRunTests.FordPhase());
        c.Start();
        Assert.Single(s.Game!.State.Pending);
        var r = Send(x, North, Batch(new[] { "n1", "n2" }));
        Assert.False(r.Ok);
        Assert.Equal("Row 1: " + FormulaIntentRunner.UsePendingText, r.Message);
    }

    [Fact]
    public void Guest_batch_over_100_rows_not_understood()
    {
        var (s, _, x) = Host();
        var r = Send(x, North, Batch(Enumerable.Repeat("n1", MenuBatchModel.MaxBatchRows + 1)));
        Assert.False(r.Ok);
        Assert.Equal(IntentExecutor.NotUnderstoodText, r.Message);
        Assert.True(Send(x, North, Batch(Enumerable.Repeat("n1", MenuBatchModel.MaxBatchRows))).Ok);
        var empty = Send(x, North, new FormulaBatchIntent("f", Array.Empty<FormulaBatchRow>(), false, false));
        Assert.Equal(IntentExecutor.NotUnderstoodText, empty.Message);
        var tooLong = Send(x, North, new FormulaBatchIntent("f",
            new[] { new FormulaBatchRow("n1", new Dictionary<string, string> { ["N"] = new string('x', 300) }, null) }, false, false));
        Assert.Equal(IntentExecutor.NotUnderstoodText, tooLong.Message);
    }

    // ---- fix round 1: nothing re-rolled by asking again ----

    private static FormulaVerdict Single(IntentExecutor x, string pieceId, bool preview) =>
        x.Execute("p1", North, new FormulaIntent("f", pieceId, null, None, false, preview)).Verdict!;

    [Fact]
    public void Single_preview_then_batch_apply_same_dice()
    {
        var (s, _, x) = Host();
        var d = Single(x, "n1", preview: true).Rolls!["D"].Sum();
        var r = Send(x, North, Batch(new[] { "n2", "n1" }));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(d, Str(s, "n1"));
    }

    [Fact]
    public void Batch_preview_then_single_apply_same_dice()
    {
        var (s, _, x) = Host();
        var preview = Send(x, North, Batch(new[] { "n1", "n2" }, preview: true)).BatchVerdict!;
        var v = Single(x, "n1", preview: false);
        Assert.True(v.Applied, v.Message);
        Assert.Equal(preview.Rows[0].Rolls!["D"].Sum(), Str(s, "n1"));
    }

    [Fact]
    public void Group_batch_re_preview_same_dice()
    {
        var (s, _, x) = Host(group: true);
        var single = Single(x, "n2", preview: true).Rolls!["D"];
        var first = Send(x, North, Batch(new[] { "n1", "n2" }, preview: true)).BatchVerdict!.Rows[0].Rolls!["D"];
        Assert.Equal(first, Send(x, North, Batch(new[] { "n2", "n1" }, preview: true)).BatchVerdict!.Rows[0].Rolls!["D"]);
        Assert.Equal(first, Send(x, North, Batch(new[] { "n2" }, preview: true)).BatchVerdict!.Rows[0].Rolls!["D"]);
        // The group's dice are the first counter's own pinned roll: a single run on it shows the same dice.
        Assert.Equal(first, Single(x, "n1", preview: true).Rolls!["D"]);
        Assert.NotEqual(single, first);   // n2's own single roll is its own (a single run is a group of one)
        var r = Send(x, North, Batch(new[] { "n2", "n1" }));
        Assert.True(r.Ok, r.Message);
        Assert.Equal(first.Sum(), Str(s, "n1"));
        Assert.Equal(first.Sum(), Str(s, "n2"));
    }

    [Fact]
    public void Removed_row_pins_released()
    {
        var scuttle = F("Scuttle", "D", new[] { new VariableBinding { Variable = "D", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 }, Own(), Pick() },
            Remove(OutputTarget.Piece));
        var (s, c) = Open(Difficulty.FullyAuto, new[] { scuttle }, players: new[] { Alice, Bob });
        var x = new IntentExecutor(s, c);
        var intent = new FormulaBatchIntent("f", new[]
        {
            new FormulaBatchRow("n1", new Dictionary<string, string> { ["T"] = Label(s, "n2") }, new Dictionary<string, string> { ["T"] = "n2" }),
            new FormulaBatchRow("n2", None, null),
        }, false, false);
        var r = x.Execute("p1", North, intent);
        Assert.True(r.Ok, r.Message);
        Assert.False(s.Game!.State.Pieces.ContainsKey("n2"));
        Assert.Equal(0, x.Pins.Count("p1"));
    }

    [Fact]
    public void Stopped_text_counts_earlier_rows()
    {
        Assert.Equal("Nothing applied: row 1 cannot be applied yet.", FormulaWindowRules.StoppedText(0, 1));
        Assert.Equal("Applied 2 run(s) as one step. Row 3: cannot be applied yet.", FormulaWindowRules.StoppedText(2, 3));
    }

    [Fact]
    public async Task Old_port_default_refuses()
    {
        IActionPort port = new OldPort();
        var v = await port.ApplyFormulaBatchAsync(Batch(new[] { "n1" }));
        Assert.False(v.Applied);
        Assert.Equal(IntentExecutor.NotUnderstoodText, v.Message);
        Assert.Empty(v.Rows);
    }

    /// <summary>A guest port whose batch intents the given host answers.</summary>
    private sealed class LoopbackPort(Func<FormulaBatchIntent, FormulaBatchVerdict> host) : IActionPort
    {
        public bool IsRemote => true;
        public async Task<FormulaBatchVerdict> ApplyFormulaBatchAsync(FormulaBatchIntent intent)
        {
            await Task.Yield();
            return host(intent);
        }
        public FormulaVerdict ApplyFormula(FormulaIntent intent) => throw new NotSupportedException();
        public Task<FormulaVerdict> ApplyFormulaAsync(FormulaIntent intent) => throw new NotSupportedException();
        public void Move(IReadOnlyList<MovePieces> moves) => throw new NotSupportedException();
        public void PieceCommand(GameCommand command) => throw new NotSupportedException();
        public void PieceCommands(IReadOnlyList<GameCommand> commands) => throw new NotSupportedException();
        public bool ApplyTrace(string? runId, string? pieceId, FormulaTrace trace, bool calculated) => throw new NotSupportedException();
        public (int Applied, IReadOnlyList<string> Failed) ApplyMissingInputs(PendingRun run, IReadOnlyDictionary<string, string> values) =>
            throw new NotSupportedException();
        public void EditLedger(string key, string propertyId, double after, string note) => throw new NotSupportedException();
        public void Roll(DiceSpec spec, string purpose) => throw new NotSupportedException();
        public void RecordRoll(RollDice roll) => throw new NotSupportedException();
        public void Tick(int index, bool ticked) => throw new NotSupportedException();
        public void Ack(int warningIndex) => throw new NotSupportedException();
        public string? NextPhase() => throw new NotSupportedException();
        public void HideAlert(string alertId, string? name) => throw new NotSupportedException();
        public void Chat(string text) => throw new NotSupportedException();
        public void SetTurn(int turn, string? phase) => throw new NotSupportedException();
        public void SetNotes(string text) => throw new NotSupportedException();
        public void Undo() => throw new NotSupportedException();
        public void Redo() => throw new NotSupportedException();
        public void StepBack(CheckpointRow row) => throw new NotSupportedException();
    }

    /// <summary>A port written before menu batches: it has no batch member of its own.</summary>
    private sealed class OldPort : IActionPort
    {
        public bool IsRemote => true;
        public FormulaVerdict ApplyFormula(FormulaIntent intent) => throw new NotSupportedException();
        public Task<FormulaVerdict> ApplyFormulaAsync(FormulaIntent intent) => throw new NotSupportedException();
        public void Move(IReadOnlyList<MovePieces> moves) => throw new NotSupportedException();
        public void PieceCommand(GameCommand command) => throw new NotSupportedException();
        public void PieceCommands(IReadOnlyList<GameCommand> commands) => throw new NotSupportedException();
        public bool ApplyTrace(string? runId, string? pieceId, FormulaTrace trace, bool calculated) => throw new NotSupportedException();
        public (int Applied, IReadOnlyList<string> Failed) ApplyMissingInputs(PendingRun run, IReadOnlyDictionary<string, string> values) =>
            throw new NotSupportedException();
        public void EditLedger(string key, string propertyId, double after, string note) => throw new NotSupportedException();
        public void Roll(DiceSpec spec, string purpose) => throw new NotSupportedException();
        public void RecordRoll(RollDice roll) => throw new NotSupportedException();
        public void Tick(int index, bool ticked) => throw new NotSupportedException();
        public void Ack(int warningIndex) => throw new NotSupportedException();
        public string? NextPhase() => throw new NotSupportedException();
        public void HideAlert(string alertId, string? name) => throw new NotSupportedException();
        public void Chat(string text) => throw new NotSupportedException();
        public void SetTurn(int turn, string? phase) => throw new NotSupportedException();
        public void SetNotes(string text) => throw new NotSupportedException();
        public void Undo() => throw new NotSupportedException();
        public void Redo() => throw new NotSupportedException();
        public void StepBack(CheckpointRow row) => throw new NotSupportedException();
    }
}
