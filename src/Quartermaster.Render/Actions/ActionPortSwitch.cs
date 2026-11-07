using Quartermaster.Core;

namespace Quartermaster.Render;

/// <summary>
/// The action port the game window hands out: forwards every call to <see cref="Inner"/>, which is <see cref="LocalActions"/> for solo,
/// hot-seat and the host, and <see cref="RemoteActions"/> while the window plays as an online guest. Panels built before the online
/// session started keep working because they hold this switch, not the port behind it.
/// </summary>
public sealed class ActionPortSwitch(IActionPort inner) : IActionPort
{
    private IActionPort _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public IActionPort Inner
    {
        get => _inner;
        set => _inner = value ?? throw new ArgumentNullException(nameof(value));
    }

    public bool IsRemote => _inner.IsRemote;
    public void Move(IReadOnlyList<MovePieces> moves) => _inner.Move(moves);
    public void PieceCommand(GameCommand command) => _inner.PieceCommand(command);
    public void PieceCommands(IReadOnlyList<GameCommand> commands) => _inner.PieceCommands(commands);
    public FormulaVerdict ApplyFormula(FormulaIntent intent) => _inner.ApplyFormula(intent);
    public Task<FormulaVerdict> ApplyFormulaAsync(FormulaIntent intent) => _inner.ApplyFormulaAsync(intent);
    public bool ApplyTrace(string? runId, string? pieceId, FormulaTrace trace, bool calculated) => _inner.ApplyTrace(runId, pieceId, trace, calculated);
    public (int Applied, IReadOnlyList<string> Failed) ApplyMissingInputs(PendingRun run, IReadOnlyDictionary<string, string> values) => _inner.ApplyMissingInputs(run, values);
    public void EditLedger(string key, string propertyId, double after, string note) => _inner.EditLedger(key, propertyId, after, note);
    public void Roll(DiceSpec spec, string purpose) => _inner.Roll(spec, purpose);
    public void RecordRoll(RollDice roll) => _inner.RecordRoll(roll);
    public void Tick(int index, bool ticked) => _inner.Tick(index, ticked);
    public void Ack(int warningIndex) => _inner.Ack(warningIndex);
    public string? NextPhase() => _inner.NextPhase();
    public void HideAlert(string alertId, string? name) => _inner.HideAlert(alertId, name);
    public void Chat(string text) => _inner.Chat(text);
    public void SetTurn(int turn, string? phase) => _inner.SetTurn(turn, phase);
    public void SetNotes(string text) => _inner.SetNotes(text);
    public void Undo() => _inner.Undo();
    public void Redo() => _inner.Redo();
    public void StepBack(CheckpointRow row) => _inner.StepBack(row);
}
