using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>One row of the Step Back list. Stage and Side are resolved against the current pack (null when the phase no longer exists).</summary>
public sealed record CheckpointRow(int LogIndex, int Turn, string? Stage, string? Side, string Phase, DateTimeOffset? At, int ActionsSince);

/// <summary>
/// Orchestrates Undo, Redo and Step Back on the open game: the core rewind, then re-seed and reconcile against the current rules pack,
/// the "Stepped back" log line, and one <see cref="Rewound"/> for the UI. UI-free; never throws on a bad row or an open action
/// (a diagnostic is logged and nothing changes).
/// </summary>
public sealed class HistoryController
{
    private readonly ModuleSession _s;
    private readonly PhaseController _phases;
    private readonly Func<GameCommand, GameState, GameCommand> _prepare;

    public HistoryController(ModuleSession s, PhaseController phases, Func<GameCommand, GameState, GameCommand> prepare)
    {
        _s = s;
        _phases = phases;
        _prepare = prepare;
        // Spec 7: a rules-pack change (which reseeds / reconciles) makes the undone actions unsafe to replay.
        s.RulesChanged += () => _s.Game?.ClearRedo();
    }

    /// <summary>Raised once after an Undo, Redo or Step Back finished (state rebuilt, re-seeded and reconciled).</summary>
    public event Action? Rewound;

    public bool CanUndo => _s.Game?.CanUndo == true;
    public bool CanRedo => _s.Game?.CanRedo == true;
    public string UndoText => _s.Game?.UndoLabel is { } l ? "Undo " + l : "Undo";
    public string RedoText => _s.Game?.RedoLabel is { } l ? "Redo " + l : "Redo";
    /// <summary>True when undo stops at the load point of an old (or inconsistent) save.</summary>
    public bool AtFloorFromOldSave => _s.Game is { } g && g.RebasedOnLoad && !g.CanUndo;

    // ---- undo / redo ----

    public void Undo()
    {
        if (Usable("undo") is not { } g || !g.CanUndo) return;
        if (!Try("undo", () => { DropSteppedBackTail(g); g.Undo(); })) return;
        Settle(g);
        Rewound?.Invoke();
    }

    public void Redo()
    {
        if (Usable("redo") is not { } g || !g.CanRedo) return;
        // Spec 7: the "Stepped back" line goes when the first stepped-back action is redone.
        if (!Try("redo", () => { DropSteppedBackTail(g); g.Redo(); })) return;
        _s.SeedLedgers();   // keys seeded after the step back and dropped with its line come back
        Rewound?.Invoke();
    }

    // ---- step back ----

    /// <summary>Checkpoints that can be stepped back to, newest first (those with something done after them).</summary>
    public IReadOnlyList<CheckpointRow> Checkpoints()
    {
        if (_s.Game is not { } g) return Array.Empty<CheckpointRow>();
        var end = WorkEnd(g);
        var cps = g.Checkpoints.OrderBy(c => c.LogIndex).ToList();
        var rows = new List<CheckpointRow>();
        for (var i = 0; i < cps.Count; i++)
        {
            var cp = cps[i];
            if (cp.LogIndex >= end) continue;
            var until = i + 1 < cps.Count ? cps[i + 1].LogIndex : g.Log.Count;
            var since = g.Actions.Count(a => a.Start >= cp.LogIndex && a.Start < until);
            string? stage = null, side = null;
            if (cp.Position is { } pos && _s.Rules is { } pack && pack.Phases.FirstOrDefault(p => p.Id == pos.PhaseId) is { } phase)
            {
                stage = Sequencer.StageText(pack, pos);
                side = Sequencer.ActingSide(pack, phase)?.Name;
            }
            rows.Add(new CheckpointRow(cp.LogIndex, cp.Turn, stage, side, cp.Label, cp.At, since));
        }
        rows.Reverse();
        return rows;
    }

    /// <summary>The checkpoint "Step Back to Start of Phase" goes to: the newest one with something done after it.</summary>
    public CheckpointRow? StartOfPhase() => Checkpoints().FirstOrDefault();

    /// <summary>What stepping back to <paramref name="row"/> would undo. Ledger entries carry property display names.</summary>
    public RewindPreview Preview(CheckpointRow row)
    {
        var empty = new RewindPreview(0, 0, Array.Empty<(string, int)>(), 0, 0, 0);
        if (_s.Game is not { } g || row.LogIndex < 0 || row.LogIndex > g.Log.Count) return empty;
        var p = g.Preview(row.LogIndex);
        return p with { Ledger = p.Ledger.Select(x => (PropertyName(x.PropertyId), x.Units)).ToList() };
    }

    /// <summary>Plain-words summary of a preview, one line per kind; kinds with nothing to undo are left out.</summary>
    public static string Describe(RewindPreview p)
    {
        var lines = new List<string>();
        if (p.Actions > 0) lines.Add(Count(p.Actions, "action", "actions"));
        if (p.DiceRolls > 0) lines.Add(Count(p.DiceRolls, "dice roll", "dice rolls") + ". Rolling again will give new results.");
        var ledger = p.Ledger.Where(x => x.Units > 0).Select(x => $"{x.PropertyId} on {Count(x.Units, "unit", "units")}").ToList();
        if (ledger.Count > 0) lines.Add("Ledger: " + string.Join(", ", ledger));
        var pieces = new List<string>();
        if (p.PiecesMoved > 0) pieces.Add($"{p.PiecesMoved} moved");
        if (p.PiecesAdded > 0) pieces.Add($"{p.PiecesAdded} added");
        if (p.PiecesRemoved > 0) pieces.Add($"{p.PiecesRemoved} removed");
        if (pieces.Count > 0) lines.Add("Pieces: " + string.Join(", ", pieces));
        return lines.Count == 0 ? "Nothing to undo." : string.Join(Environment.NewLine, lines);
    }

    /// <summary>Rewinds to the checkpoint, logs "Stepped back to …", then re-seeds and reconciles. The caller autosaves first.</summary>
    public void StepBack(CheckpointRow row)
    {
        if (Usable("step back") is not { } g) return;
        if (row.LogIndex < 0 || row.LogIndex > g.Log.Count)
        {
            _s.Log.Add(DiagnosticSeverity.Warning, "history", $"Step back skipped: checkpoint \"{row.Phase}\" is no longer in this game's history.");
            return;
        }
        IReadOnlyList<UndoneAction>? undone = null;
        var target = row.LogIndex;
        if (!Try("step back", () =>
        {
            // A second step back replaces the first one's line rather than burying it in the redo stack.
            DropSteppedBackTail(g);
            target = Math.Min(target, g.Log.Count);
            undone = g.RewindTo(target);
        })) return;
        var dice = undone!.Sum(a => a.Commands.Count(c => c is RollDice));
        g.Execute(_prepare(new SteppedBack(target, undone!.Count, dice, TargetText(row)), g.State));
        Settle(g);
        Rewound?.Invoke();
    }

    // ---- helpers ----

    /// <summary>Spec 6.3: pick up rules-pack keys added since, and move off a phase the pack no longer has (logged with a reason).</summary>
    private void Settle(GameSession g)
    {
        _s.SeedLedgers();
        using (g.BeginAction("phase relocation")) _phases.Reconcile();
    }

    /// <summary>The open game, or null (with a diagnostic when an action is still open).</summary>
    private GameSession? Usable(string what)
    {
        if (_s.Game is not { } g) return null;
        if (!g.InAction) return g;
        _s.Log.Add(DiagnosticSeverity.Warning, "history", $"Cannot {what} while an action is still being recorded.");
        return null;
    }

    private bool Try(string what, Action op)
    {
        try { op(); return true; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            _s.Log.Add(DiagnosticSeverity.Warning, "history", $"Could not {what}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Removes a trailing <see cref="SteppedBack"/> and any folding commands after it, without touching the redo stack.</summary>
    private static void DropSteppedBackTail(GameSession g)
    {
        var k = g.Log.Count - 1;
        while (k >= 0 && GameSession.IsFolding(g.Log[k]) && g.Log[k] is not SteppedBack) k--;
        if (k < 0 || g.Log[k] is not SteppedBack) return;
        while (g.Log.Count > k) g.DropTail();
    }

    /// <summary>Log length without trailing folding commands: a checkpoint at or past it has nothing done after it.</summary>
    private static int WorkEnd(GameSession g)
    {
        var end = g.Log.Count;
        while (end > 0 && GameSession.IsFolding(g.Log[end - 1])) end--;
        return end;
    }

    private static string TargetText(CheckpointRow row)
    {
        if (row.LogIndex == 0 || row.Phase == "game start") return "game start";
        return row.Phase == $"Turn {row.Turn}" ? row.Phase : $"Turn {row.Turn} · {row.Phase}";
    }

    private string PropertyName(string id) =>
        _s.Rules?.Properties.FirstOrDefault(p => p.Id == id)?.Name is { Length: > 0 } n ? n : id;

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
