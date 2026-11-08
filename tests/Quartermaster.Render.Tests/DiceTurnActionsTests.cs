using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>The dice and turn tools' shared logic (moved out of WinForms DiceTools): rolls, module dice, multi-rolls, turn steps. Invented data only.</summary>
public class DiceTurnActionsTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-dice-" + Guid.NewGuid().ToString("N"));
    private readonly List<FixtureModule> _fixtures = new();
    private readonly List<ModuleSession> _sessions = new();
    private readonly List<string> _shown = new();

    public void Dispose()
    {
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private ModuleSession Open(Func<int, int, IReadOnlyList<int>> roller)
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            "<VASSAL.build.module.SpecialDiceButton name=\"Dice\"><VASSAL.build.module.SpecialDie name=\"Weather\">" +
            "<VASSAL.build.module.SpecialDieFace text=\"Sun\"/><VASSAL.build.module.SpecialDieFace text=\"Rain\"/><VASSAL.build.module.SpecialDieFace text=\"Fog\"/>" +
            "</VASSAL.build.module.SpecialDie></VASSAL.build.module.SpecialDiceButton></VASSAL.build.GameModule>");
        var fx = new FixtureModule().WithBuildFile(xml);
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache" + _sessions.Count), null);
        _sessions.Add(s);
        s.SetRoller(roller);
        s.StartNewGame();
        return s;
    }

    private DiceTurnActions Tools(ModuleSession s) => new(() => s, _shown.Add);

    [Fact]
    public void RollSpec_records_one_roll_and_shows_its_log_line()
    {
        var s = Open((n, _) => Enumerable.Repeat(3, n).ToList());
        var before = s.Game!.State.Log.Count;
        Assert.True(Tools(s).RollSpec(new DiceSpec(2, 6, 1), DiceTurnActions.RollPurpose));
        var game = s.Game!;
        Assert.Equal(before + 1, game.State.Log.Count);
        Assert.Equal(LogKind.Dice, game.State.Log[^1].Kind);
        var roll = Assert.IsType<RollDice>(game.Log[^1]);
        Assert.Equal(("Roll", 2, 6, "2d6+1", 1), (roll.Purpose, roll.Count, roll.Sides, roll.Expression, roll.Modifier));
        Assert.Equal(new[] { 3, 3 }, roll.Results);
        Assert.Equal(new[] { game.State.Log[^1].Text }, _shown);
    }

    [Fact]
    public void Invalid_roller_records_nothing()
    {
        var s = Open((_, _) => new[] { 7 });
        var before = s.Game!.State.Log.Count;
        Assert.False(Tools(s).RollSpec(DiceTurnActions.OneD6, "Roll"));
        Assert.Equal(before, s.Game!.State.Log.Count);
        Assert.Equal(new[] { DiceTurnActions.InvalidRollText }, _shown);
    }

    [Fact]
    public void RollSpecial_logs_the_face()
    {
        var s = Open((_, _) => new[] { 2 });
        var die = Assert.Single(s.Model.Dice);
        Assert.True(Tools(s).RollSpecial(die));
        var roll = Assert.IsType<RollDice>(s.Game!.Log[^1]);
        Assert.Equal(("Weather", 1, 3), (roll.Purpose, roll.Count, roll.Sides));
        Assert.Equal(new[] { "Rain" }, roll.FaceTexts);
        Assert.False(Tools(s).RollSpecial(new DieDef("One", new[] { "x" })));
    }

    [Fact]
    public void MultiRoll_names_each_roll_and_shows_only_the_last()
    {
        var s = Open((n, _) => Enumerable.Repeat(1, n).ToList());
        Tools(s).MultiRoll(DiceTurnActions.OneD6, 3);
        var rolls = s.Game!.Log.OfType<RollDice>().Select(r => r.Purpose).ToList();
        Assert.Equal(new[] { "Multi-roll 1/3", "Multi-roll 2/3", "Multi-roll 3/3" }, rolls);
        Assert.Single(_shown);
    }

    [Fact]
    public void Physical_dice_ask_and_cancel()
    {
        var s = Open((_, _) => throw new InvalidOperationException("not rolled"));
        var t = Tools(s);
        t.PhysicalDice = true;
        t.AskPhysical = (n, sides) => n == 2 && sides == 6 ? new[] { 5, 6 } : null;
        Assert.True(t.RollSpec(DiceTurnActions.TwoD6, "Roll"));
        Assert.Equal(new[] { 5, 6 }, Assert.IsType<RollDice>(s.Game!.Log[^1]).Results);
        Assert.False(t.RollSpec(DiceTurnActions.OneD6, "Roll"));
    }

    [Fact]
    public void Turn_steps_never_below_one_and_set_trims_phase()
    {
        var s = Open((n, _) => Enumerable.Repeat(1, n).ToList());
        var t = Tools(s);
        var count = s.Game!.Log.Count;
        t.StepTurn(-1);
        Assert.Equal(count, s.Game!.Log.Count);   // already turn 1: no command
        Assert.False(t.CanStepBack);
        t.StepTurn(1);
        Assert.Equal(2, s.Game!.State.Turn.Turn);
        Assert.True(t.CanStepBack);
        Assert.True(t.CanSetTurn());
        t.SetTurn(5, "  Move ");
        Assert.Equal(new TurnState(5, "Move"), s.Game!.State.Turn);
        Assert.Equal("Turn 5 · Move", DiceTurnActions.TurnText(s.Game!.State.Turn));
        t.SetTurn(6, "  ");
        Assert.Equal(new TurnState(6, null), s.Game!.State.Turn);
        Assert.Equal("Turn 6", DiceTurnActions.TurnText(s.Game!.State.Turn));
    }

    [Fact]
    public void Goes_through_the_action_port()
    {
        var s = Open((n, _) => Enumerable.Repeat(2, n).ToList());
        var port = new RecordingPort();
        var t = Tools(s);
        t.Actions = () => port;
        Assert.True(t.RollSpec(DiceTurnActions.OneD6, "Roll"));
        t.StepTurn(1);
        Assert.Equal(new[] { "roll Roll 1d6", "turn 2 " }, port.Calls);
    }

    /// <summary>Records roll and turn calls; nothing else is used by these tests.</summary>
    private sealed class RecordingPort : IActionPort
    {
        public readonly List<string> Calls = new();
        public bool IsRemote => false;
        public void RecordRoll(RollDice roll) => Calls.Add($"roll {roll.Purpose} {roll.Expression}");
        public void SetTurn(int turn, string? phase) => Calls.Add($"turn {turn} {phase}");
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
        public void Tick(int index, bool ticked) => throw new NotSupportedException();
        public void Ack(int warningIndex) => throw new NotSupportedException();
        public string? NextPhase() => throw new NotSupportedException();
        public void HideAlert(string alertId, string? name) => throw new NotSupportedException();
        public void Chat(string text) => throw new NotSupportedException();
        public void SetNotes(string text) => throw new NotSupportedException();
        public void Undo() => throw new NotSupportedException();
        public void Redo() => throw new NotSupportedException();
        public void StepBack(CheckpointRow row) => throw new NotSupportedException();
    }
}
