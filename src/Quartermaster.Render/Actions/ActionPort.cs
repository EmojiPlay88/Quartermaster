using Quartermaster.Core;

namespace Quartermaster.Render;

/// <summary>
/// One interface for every user action that changes the game. Solo and hot-seat use <see cref="LocalActions"/> (exactly the direct calls
/// the UI made before); an online guest uses a remote port that sends <see cref="GameIntent"/>s to the host. Local calls are synchronous.
/// Members marked "host only" are refused by a remote port (spec 8a.5); members marked "local" need the full game state and are not
/// available to a guest (a remote port refuses them; guests use the intent-shaped member instead).
/// </summary>
public interface IActionPort
{
    /// <summary>True for an online guest: actions are sent to the host and the state changes when its snapshot arrives.</summary>
    bool IsRemote { get; }

    /// <summary>One move gesture (one undoable action); fires the phase's OnMove formulas.</summary>
    void Move(IReadOnlyList<MovePieces> moves);

    /// <summary>One piece command on its own (palette drop, property sheet); an AddPiece also seeds the new piece's ledgers.</summary>
    void PieceCommand(GameCommand command);

    /// <summary>Several commands from one menu choice as one undoable action; consecutive moves form one move gesture.</summary>
    void PieceCommands(IReadOnlyList<GameCommand> commands);

    /// <summary>Runs a formula from typed values (or calculated) and applies it when it checks out.</summary>
    FormulaVerdict ApplyFormula(FormulaIntent intent);

    /// <summary>As <see cref="ApplyFormula"/>; a remote port completes when the host's verdict arrives.</summary>
    Task<FormulaVerdict> ApplyFormulaAsync(FormulaIntent intent);

    /// <summary>Local: applies a formula run the dialog already made (see <see cref="PhaseController.ApplyFormula"/>).</summary>
    bool ApplyTrace(string? runId, string? pieceId, FormulaTrace trace, bool calculated);

    /// <summary>Local: finishes every open unit of a pending run with one set of answers (see <see cref="MissingInputs.ApplyAll(ModuleSession, PhaseController, PendingRun, IReadOnlyDictionary{string, string})"/>).</summary>
    (int Applied, IReadOnlyList<string> Failed) ApplyMissingInputs(PendingRun run, IReadOnlyDictionary<string, string> values);

    /// <summary>A manual ledger change with its note.</summary>
    void EditLedger(string key, string propertyId, double after, string note);

    /// <summary>A dice-tool roll made with the session roller (on the host).</summary>
    void Roll(DiceSpec spec, string purpose);

    /// <summary>Local: records a roll whose results the player supplied (physical dice) or a special die.</summary>
    void RecordRoll(RollDice roll);

    void Tick(int index, bool ticked);
    void Ack(int warningIndex);

    /// <summary>Starts the game or advances one phase. Returns "Pass to {side} player" when the acting side changed, else null.</summary>
    string? NextPhase();

    /// <summary>Host only.</summary>
    void HideAlert(string alertId, string? name);

    void Chat(string text);

    /// <summary>Host only.</summary>
    void SetTurn(int turn, string? phase);

    /// <summary>Host only.</summary>
    void SetNotes(string text);

    /// <summary>Host only.</summary>
    void Undo();

    /// <summary>Host only.</summary>
    void Redo();

    /// <summary>Host only.</summary>
    void StepBack(CheckpointRow row);
}
