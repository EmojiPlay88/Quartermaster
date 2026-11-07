using Quartermaster.Core;

namespace Quartermaster.Render;

/// <summary>
/// The action port of solo and hot-seat play (and of the online host's own actions): exactly the direct calls the UI made before the port
/// existed, with the same action scopes, <see cref="CommandLog"/> stamping, hot-seat side stamps and may-act checks.
/// </summary>
/// <param name="prepare">Stamps and words a command before it executes; defaults to <see cref="CommandLog.Prepare"/>.</param>
public sealed class LocalActions(ModuleSession s, PhaseController c, HistoryController h, Func<GameCommand, GameState, GameCommand>? prepare = null)
    : IActionPort
{
    private readonly Func<GameCommand, GameState, GameCommand> _prepare = prepare ?? ((cmd, st) => CommandLog.Prepare(cmd, st, s));

    public bool IsRemote => false;

    private void Exec(GameSession g, GameCommand cmd) => g.Execute(_prepare(cmd, g.State));

    public void Move(IReadOnlyList<MovePieces> moves)
    {
        if (s.Game is not { } g || moves == null || moves.Count == 0) return;
        // One scope per gesture so a multi-group drop undoes as a single action; the controller fires the phase's OnMove formulas.
        using var scope = g.BeginAction();
        c.ExecuteMove(moves);
    }

    public void PieceCommand(GameCommand command)
    {
        if (s.Game is not { } g || command == null) return;
        Exec(g, command);
        if (command is AddPiece) s.SeedLedgers();
    }

    public void PieceCommands(IReadOnlyList<GameCommand> commands)
    {
        if (s.Game is not { } g || commands == null) return;
        var gesture = new List<MovePieces>();
        void FlushMoves()
        {
            if (gesture.Count == 0) return;
            var moves = gesture.ToList();
            gesture.Clear();
            c.ExecuteMove(moves);
        }
        using var scope = g.BeginAction();   // one undoable action per call; label comes from the first command
        foreach (var cmd in commands)
        {
            // Move commands (Send to, keyboard moves) are move gestures: consecutive ones go through the phase controller as one call.
            if (cmd is MovePieces m) { gesture.Add(m); continue; }
            FlushMoves();
            Exec(g, cmd);
        }
        FlushMoves();
    }

    public FormulaVerdict ApplyFormula(FormulaIntent intent) => FormulaIntentRunner.Run(s, c, s.ViewSides, intent, guest: false);

    public Task<FormulaVerdict> ApplyFormulaAsync(FormulaIntent intent) => Task.FromResult(ApplyFormula(intent));

    public bool ApplyTrace(string? runId, string? pieceId, FormulaTrace trace, bool calculated) => c.ApplyFormula(runId, pieceId, trace, calculated);

    public (int Applied, IReadOnlyList<string> Failed) ApplyMissingInputs(PendingRun run, IReadOnlyDictionary<string, string> values) =>
        MissingInputs.ApplyAll(s, c, run, values);

    public void EditLedger(string key, string propertyId, double after, string note)
    {
        if (s.Game is not { } g || !s.CanEditLedger(key)) return;
        var prop = s.Rules?.Properties.FirstOrDefault(p => p != null && p.Id == propertyId);
        var before = prop != null ? Ledger.GetOrDefault(g.State, key, prop) : Ledger.Get(g.State, key, propertyId) ?? 0;
        Exec(g, new LedgerDelta(key, propertyId, before, after, "manual", null, null, (note ?? "").Trim(), null));
    }

    public void Roll(DiceSpec spec, string purpose)
    {
        if (s.Game is not { } g || spec == null) return;
        var results = s.Roll(spec.Count, spec.Sides);
        if (results.Count != spec.Count || results.Any(v => v < 1 || v > spec.Sides)) return;
        Exec(g, new RollDice(purpose, spec.Count, spec.Sides, results, DiceExpr.Format(spec), null, spec.Modifier));
    }

    public void RecordRoll(RollDice roll)
    {
        if (s.Game is not { } g || roll == null) return;
        Exec(g, roll);
    }

    public void Tick(int index, bool ticked) => c.Tick(index, ticked);

    public void Ack(int warningIndex) => c.Acknowledge(warningIndex);

    public string? NextPhase()
    {
        if (s.Game == null) return null;
        var v = c.View();
        if (v.NoPhases || v.PlayingText != null) return null;   // hot-seat: another side's phase (the UI says so before calling)
        if (v.BeforeStart) { c.Start(); return null; }
        return c.Advance();
    }

    public void HideAlert(string alertId, string? name)
    {
        if (s.Game is not { } g) return;
        Exec(g, new HideAlert(alertId, Name: string.IsNullOrWhiteSpace(name) ? null : name));
    }

    public void Chat(string text)
    {
        if (s.Game is not { } g || ChatText.Clean(text) is not { } clean) return;
        var name = s.ActorSide() is { } side ? Players.Holding(g.State, s.Rules, side)?.Name : null;
        Exec(g, new AddLogLine(LogKind.Chat, ChatText.Line(name, clean)));
    }

    public void SetTurn(int turn, string? phase)
    {
        if (s.Game is not { } g) return;
        Exec(g, new SetTurn(turn, phase));
    }

    public void SetNotes(string text)
    {
        if (s.Game is not { } g) return;
        Exec(g, new SetNotes(text));
    }

    public void Undo() => h.Undo();

    public void Redo() => h.Redo();

    public void StepBack(CheckpointRow row) => h.StepBack(row);
}
