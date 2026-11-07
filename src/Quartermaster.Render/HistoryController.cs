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

    // The tail the most recent rewind appended (its "Stepped back" line, re-seed and relocation). Only this tail is dropped by the
    // next Undo / Redo / Step Back; it is forgotten as soon as anything else changes the game.
    private GameSession? _tailGame;
    private int _tailStart = -1;
    private bool _tailHasLine;
    private GameSession? _hooked;
    private bool _busy;

    public HistoryController(ModuleSession s, PhaseController phases, Func<GameCommand, GameState, GameCommand> prepare)
    {
        _s = s;
        _phases = phases;
        _prepare = prepare;
        // Spec 7: a rules-pack change that reseeds / reconciles makes the undone actions unsafe to replay (a plain save does not).
        // The last rewind's tail is kept as history too: its "Stepped back" line is the only record of the dice just discarded.
        s.RulesApplied += () => { _s.Game?.ClearRedo(); ClearTail(); };
        s.GameReplaced += Hook;
        Hook();
    }

    /// <summary>Raised once after an Undo, Redo or Step Back finished (state rebuilt, re-seeded and reconciled).</summary>
    public event Action? Rewound;

    public bool CanUndo => _s.Game is { } g && LastAction(g) != null;
    public bool CanRedo => _s.Game?.CanRedo == true;
    public string UndoText => _s.Game is { } g && LastAction(g) is { } a ? "Undo " + a.Label : "Undo";
    public string RedoText => _s.Game?.RedoLabel is { } l ? "Redo " + l : "Redo";
    /// <summary>True when undo stops at the load point of an old (or inconsistent) save.</summary>
    public bool AtFloorFromOldSave => _s.Game is { } g && g.RebasedOnLoad && !CanUndo;

    // ---- undo / redo ----

    /// <summary>Undoes the last action. After a step back the "Stepped back" line is kept and re-logged so that it covers
    /// everything on the redo stack; a plain Undo adds no line (spec 6.4).</summary>
    public void Undo()
    {
        if (Usable("undo") is not { } g || LastAction(g) == null) return;
        _busy = true;
        try
        {
            var hadLine = false;
            if (!Try("undo", () => { hadLine = DropRewindTail(g); g.Undo(); })) return;
            Finish(g, hadLine ? PositionText(g) : null, -1);
        }
        finally { _busy = false; }
        Rewound?.Invoke();
    }

    public void Redo()
    {
        if (Usable("redo") is not { } g || !g.CanRedo) return;
        _busy = true;
        try
        {
            // Spec 7: the "Stepped back" line (and what the rewind settled after it) goes when an undone action is redone. While the
            // redo stack still holds undone actions it is re-logged to cover just those, so a new action cannot drop their dice
            // without a trace; once everything is redone no line remains.
            var hadLine = false;
            if (!Try("redo", () => { hadLine = DropRewindTail(g); g.Redo(); })) return;
            if (hadLine && g.CanRedo) Finish(g, PositionText(g), -1);
            else After("redo", () => _s.SeedLedgers());   // keys seeded after the rewind and dropped with its tail come back
        }
        finally { _busy = false; }
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
        if (TailStart(g) is { } t) p = p with { Actions = Math.Max(0, p.Actions - g.Actions.Count(a => a.Start >= t)) };
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

    /// <summary>Rewinds to the checkpoint, logs "Stepped back to …", then re-seeds and reconciles. The caller autosaves first.
    /// A step back right after another replaces its line with one covering both.</summary>
    public void StepBack(CheckpointRow row)
    {
        if (Usable("step back") is not { } g) return;
        if (row.LogIndex < 0 || row.LogIndex > g.Log.Count)
        {
            _s.Log.Add(DiagnosticSeverity.Warning, "history", $"Step back skipped: checkpoint \"{row.Phase}\" is no longer in this game's history.");
            return;
        }
        _busy = true;
        try
        {
            var target = row.LogIndex;
            if (!Try("step back", () =>
            {
                DropRewindTail(g);
                target = Math.Min(target, g.Log.Count);
                g.RewindTo(target);
            })) return;
            Finish(g, TargetText(g, row), target);
        }
        finally { _busy = false; }
        Rewound?.Invoke();
    }

    // ---- helpers ----

    /// <summary>After the core rewind: the "Stepped back" line (when <paramref name="lineTarget"/> is set) covering every action and
    /// dice roll now on the redo stack, then re-seed and reconcile. Records what was appended as the rewind's tail.</summary>
    private void Finish(GameSession g, string? lineTarget, int toIndex)
    {
        var start = g.Log.Count;
        After("rewind", () =>
        {
            if (lineTarget != null)
            {
                var redo = g.RedoActions;
                var dice = redo.Sum(a => a.Commands.Count(c => c is RollDice));
                g.Execute(_prepare(new SteppedBack(toIndex < 0 ? start : toIndex, redo.Count, dice, lineTarget), g.State));
            }
            Settle(g);
        });
        if (g.Log.Count > start) { _tailGame = g; _tailStart = start; _tailHasLine = lineTarget != null; }
    }

    /// <summary>Spec 6.3: pick up rules-pack keys added since, and move off a phase the pack no longer has (logged with a reason).
    /// The relocation keeps the redo stack: it is part of the rewind, not a new action.</summary>
    private void Settle(GameSession g)
    {
        _s.SeedLedgers();
        using (g.BeginSettle("phase relocation")) _phases.Reconcile();
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

    /// <summary>Post-rewind bookkeeping: logged, never thrown (the rewind itself already happened).</summary>
    private void After(string what, Action op)
    {
        try { op(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _s.Log.Add(DiagnosticSeverity.Warning, "history", $"After {what}: {ex.Message}");
        }
    }

    private void Hook()
    {
        if (_hooked != null) { _hooked.Changed -= OnChanged; _hooked.Rewound -= OnRewound; }
        _hooked = _s.Game;
        if (_hooked != null) { _hooked.Changed += OnChanged; _hooked.Rewound += OnRewound; }
        ClearTail();
    }

    // Anything that changes the game outside this controller ends the last rewind's tail (view-only folding commands do not).
    private void OnChanged(GameCommand c) { if (!_busy && !GameSession.IsFolding(c)) ClearTail(); }
    private void OnRewound() { if (!_busy) ClearTail(); }

    private void ClearTail() { _tailGame = null; _tailStart = -1; _tailHasLine = false; }

    /// <summary>Where the last rewind's tail starts, or null when there is none (or it was overtaken).</summary>
    private int? TailStart(GameSession g) =>
        ReferenceEquals(_tailGame, g) && _tailStart >= 0 && _tailStart <= g.Log.Count ? _tailStart : null;

    /// <summary>Removes the last rewind's tail without touching the redo stack. True when it held a "Stepped back" line.</summary>
    private bool DropRewindTail(GameSession g)
    {
        if (TailStart(g) is not { } t) return false;
        var hadLine = _tailHasLine;
        while (g.Log.Count > t) g.DropTail();
        ClearTail();
        return hadLine;
    }

    /// <summary>The action Undo goes back to: the last one before the rewind tail.</summary>
    private ActionMark? LastAction(GameSession g)
    {
        var limit = TailStart(g) ?? g.Log.Count;
        for (var i = g.Actions.Count - 1; i >= 0; i--)
            if (g.Actions[i].Start < limit) return g.Actions[i];
        return null;
    }

    /// <summary>Log length without the rewind tail and trailing folding commands: a checkpoint at or past it has nothing done after it.</summary>
    private int WorkEnd(GameSession g)
    {
        var end = TailStart(g) ?? g.Log.Count;
        while (end > 0 && GameSession.IsFolding(g.Log[end - 1])) end--;
        return end;
    }

    /// <summary>Where the game now stands, for a re-logged "Stepped back to …" line after an Undo.</summary>
    private string PositionText(GameSession g)
    {
        if (g.State.Position is { } pos)
            return $"Turn {pos.Turn} · " + (_s.Rules?.Phases.FirstOrDefault(p => p.Id == pos.PhaseId)?.Name ?? pos.PhaseId);
        return g.CanUndo ? $"Turn {g.State.Turn.Turn}" : g.StartLabel;
    }

    private static string TargetText(GameSession g, CheckpointRow row)
    {
        if (row.LogIndex == 0 || row.Phase == g.StartLabel) return g.StartLabel;
        return row.Phase == $"Turn {row.Turn}" ? row.Phase : $"Turn {row.Turn} · {row.Phase}";
    }

    private string PropertyName(string id) =>
        _s.Rules?.Properties.FirstOrDefault(p => p.Id == id)?.Name is { Length: > 0 } n ? n : id;

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
