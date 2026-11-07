using System.Text.Json;
using Quartermaster.Core;

namespace Quartermaster.Render;

/// <summary>
/// The action port of an online guest: every action becomes a <see cref="GameIntent"/> sent to the host through the
/// <see cref="GuestSession"/>; the game changes when the host's next filtered snapshot arrives. Host-only actions and the "local" members
/// that need the full game are refused here with a notice. Trails and stack expansion are the guest's own view: they are applied locally
/// (the host refuses them) and survive later snapshots. Refusals and a host that doesn't answer are reported through <see cref="GuestSession.Notice"/>.
/// </summary>
public sealed class RemoteActions(GuestSession g) : IActionPort
{
    public const string SentText = "Sent to the host.";
    public const string LocalOnlyText = "Online, the host calculates this: use Calculate or Apply.";
    public const string RecordRollText = "Only the host can record rolls made with physical dice.";

    private readonly GuestSession _g = g ?? throw new ArgumentNullException(nameof(g));

    public bool IsRemote => true;

    /// <summary>Raised for a refusal decided here (host-only action, local-only member), as the session's notices are.</summary>
    public event Action<string>? Refused;

    private void Refuse(string why) => Refused?.Invoke(why);

    private void Send(GameIntent intent) => _ = _g.SendIntent(intent);

    public void Move(IReadOnlyList<MovePieces> moves)
    {
        if (moves == null || moves.Count == 0) return;
        Send(new MoveIntent(moves.ToList()));
    }

    /// <summary>True when the command is the guest's own view (applied locally, never sent).</summary>
    private bool Local(GameCommand command)
    {
        switch (command)
        {
            case ToggleTrail t: _g.ToggleTrail(t.PieceId); return true;
            case SetStackExpanded e: _g.SetStackExpanded(e.StackId, e.Expanded); return true;
            default: return false;
        }
    }

    public void PieceCommand(GameCommand command)
    {
        if (command == null || Local(command)) return;
        Send(new PieceCommandIntent(command));
    }

    public void PieceCommands(IReadOnlyList<GameCommand> commands)
    {
        if (commands == null) return;
        var gesture = new List<MovePieces>();
        void FlushMoves()
        {
            if (gesture.Count == 0) return;
            Send(new MoveIntent(gesture.ToList()));
            gesture.Clear();
        }
        foreach (var cmd in commands)
        {
            if (cmd == null) continue;
            if (cmd is MovePieces m) { gesture.Add(m); continue; }
            FlushMoves();
            if (!Local(cmd)) Send(new PieceCommandIntent(cmd));
        }
        FlushMoves();
    }

    /// <summary>Sends the intent; the host's verdict follows (use <see cref="ApplyFormulaAsync"/> to wait for it).</summary>
    public FormulaVerdict ApplyFormula(FormulaIntent intent)
    {
        if (intent == null) return FormulaVerdict.Refused(IntentExecutor.NotUnderstoodText);
        _ = _g.SendIntent(intent);
        return new FormulaVerdict(false, Array.Empty<FormulaBox>(), SentText);
    }

    /// <summary>The host's verdict; <see cref="GuestSession.NoAnswerText"/> after the timeout (a later answer is ignored).</summary>
    public async Task<FormulaVerdict> ApplyFormulaAsync(FormulaIntent intent)
    {
        if (intent == null) return FormulaVerdict.Refused(IntentExecutor.NotUnderstoodText);
        var r = await _g.SendIntent(intent, notify: false);
        if (r == null) return FormulaVerdict.Refused(GuestSession.NoAnswerText);
        FormulaVerdict? v = null;
        if (!string.IsNullOrEmpty(r.VerdictJson))
        {
            try { v = JsonSerializer.Deserialize<FormulaVerdict>(r.VerdictJson, QuartermasterJson.Options); }
            catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or ArgumentException) { v = null; }
        }
        if (v == null) return new FormulaVerdict(r.Ok, Array.Empty<FormulaBox>(), r.Message);
        return v with { Boxes = (v.Boxes ?? Array.Empty<FormulaBox>()).Where(b => b != null).ToList(), Message = v.Message ?? r.Message };
    }

    public bool ApplyTrace(string? runId, string? pieceId, FormulaTrace trace, bool calculated)
    {
        Refuse(LocalOnlyText);
        return false;
    }

    public (int Applied, IReadOnlyList<string> Failed) ApplyMissingInputs(PendingRun run, IReadOnlyDictionary<string, string> values)
    {
        Refuse(LocalOnlyText);
        return (0, new[] { LocalOnlyText });
    }

    public void EditLedger(string key, string propertyId, double after, string note) =>
        Send(new LedgerEditIntent(key, propertyId, after, note ?? ""));

    public void Roll(DiceSpec spec, string purpose)
    {
        if (spec == null) return;
        Send(new DiceIntent(spec, purpose ?? ""));
    }

    public void RecordRoll(RollDice roll) => Refuse(RecordRollText);

    public void Tick(int index, bool ticked) => Send(new TickIntent(index, ticked));

    public void Ack(int warningIndex) => Send(new AckIntent(warningIndex));

    /// <summary>Sent to the host; any "Pass to…" handover is the host's business, so this always returns null.</summary>
    public string? NextPhase()
    {
        Send(new NextPhaseIntent());
        return null;
    }

    public void HideAlert(string alertId, string? name) => Refuse(IntentExecutor.HostOnlyText);

    public void Chat(string text)
    {
        if (ChatText.Clean(text) is null) return;
        Send(new ChatIntent(text));
    }

    public void SetTurn(int turn, string? phase) => Refuse(IntentExecutor.HostOnlyText);

    public void SetNotes(string text) => Refuse(IntentExecutor.HostOnlyText);

    public void Undo() => Refuse(ModuleSession.GuestUndoText);

    public void Redo() => Refuse(ModuleSession.GuestUndoText);

    public void StepBack(CheckpointRow row) => Refuse(ModuleSession.GuestUndoText);
}
