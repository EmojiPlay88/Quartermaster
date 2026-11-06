using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>Where a piece was before a move gesture (for measuring the distance moved).</summary>
public sealed record MoveRecord(string PieceId, string FromMap, PointD From);

public enum ChipState { Done, Current, Upcoming }

public sealed record PhaseChip(string Id, string Name, ChipState State);

/// <summary>Everything the tracker strip and phase panel show. Built fresh by <see cref="PhaseController.View"/>.</summary>
public sealed record PhaseView(bool NoPhases, bool BeforeStart, int Turn, string? StageText, string? SideName,
    IReadOnlyList<PhaseChip> Chips, PhaseDef? Current, string? NextName, IReadOnlyList<Decision> Decisions,
    IReadOnlyList<WarningHit> Warnings, string? Notice);

/// <summary>
/// Drives the turn sequence: advancing, OnStart / OnMove formula triggers per difficulty mode, checklist ticks and warning
/// acknowledgements. Every game change is a command that goes through <c>prepare</c> (which stamps At/Who) and then
/// <see cref="GameSession.Execute"/>. Bad pack data gives log lines, never exceptions. The Phase boundary autosave is wired by the
/// caller to <see cref="PhaseBoundary"/> (see <see cref="Autosaver.SaveNow"/>).
/// </summary>
public sealed class PhaseController(ModuleSession s, Func<GameCommand, GameState, GameCommand> prepare)
{
    private sealed record Unit(string? PieceId, string Label, double? Moved);

    private string? _notice;

    /// <summary>Runs queued (or needing input) by a trigger; raised once per OnStart / OnMove batch that queued any.</summary>
    public event Action<IReadOnlyList<PendingRun>>? RunsQueued;

    /// <summary>Raised after the phase changed (start, advance), once its OnStart has run.</summary>
    public event Action? PhaseBoundary;

    /// <summary>Raised after the difficulty of the open game changed (windows that were checking in the old mode must close).</summary>
    public event Action? DifficultyChanged;

    private void Exec(GameCommand c)
    {
        if (s.Game is not { } g) return;
        g.Execute(prepare(c, g.State));
    }

    private void Calc(string text) => Exec(new AddLogLine(LogKind.Calc, text));

    private bool HasPhases => s.Game != null && s.Rules is { Phases.Count: > 0 };

    /// <summary>The stored position resolved against the current pack (never null with phases once started).</summary>
    private PhasePosition? Resolved(out string? notice)
    {
        notice = null;
        if (s.Game?.State.Position is not { } stored || s.Rules is not { } pack) return null;
        var (pos, n) = Sequencer.Resolve(pack, stored);
        notice = n;
        return pos;
    }

    private PhaseDef? CurrentPhase()
    {
        if (s.Rules is not { } pack || Resolved(out _) is not { } pos) return null;
        return pack.Phases.FirstOrDefault(p => p.Id == pos.PhaseId);
    }

    // ---- view ----

    public PhaseView View()
    {
        var none = Array.Empty<Decision>();
        if (!HasPhases)
            return new PhaseView(true, false, s.Game?.State.Turn.Turn ?? 1, null, null, Array.Empty<PhaseChip>(), null, null, none, Array.Empty<WarningHit>(), _notice);
        var pack = s.Rules!;
        var st = s.Game!.State;
        if (Resolved(out _) is not { } pos)
        {
            var chips0 = pack.Phases.Select(p => new PhaseChip(p.Id, p.Name, ChipState.Upcoming)).ToList();
            return new PhaseView(false, true, 1, null, null, chips0, null, pack.Phases[0].Name, none, Array.Empty<WarningHit>(), _notice);
        }
        var cur = pack.Phases[pos.PhaseIndex];
        var chips = pack.Phases.Select((p, i) => new PhaseChip(p.Id, p.Name,
            i < pos.PhaseIndex ? ChipState.Done : i == pos.PhaseIndex ? ChipState.Current : ChipState.Upcoming)).ToList();
        var next = Sequencer.Next(pack, pos);
        var side = Sequencer.ActingSide(pack, cur);
        return new PhaseView(false, false, pos.Turn, Sequencer.StageText(pack, pos), side?.Name, chips, cur,
            next == null ? null : pack.Phases.ElementAtOrDefault(next.PhaseIndex)?.Name,
            Decisions.Compute(pack, st), Decisions.Warnings(pack, st, cur), _notice);
    }

    // ---- advancing ----

    public void Start()
    {
        if (!HasPhases || s.Game!.State.Position != null) return;
        var pack = s.Rules!;
        var first = Sequencer.First(pack);
        if (first == null) return;
        _notice = null;
        Exec(new AdvancePhase(null, first, "", pack.Phases[first.PhaseIndex].Name));
        OnEnter(pack.Phases[first.PhaseIndex]);
        PhaseBoundary?.Invoke();
    }

    /// <returns>"Pass to {side} player" when the acting side changed, else null.</returns>
    public string? Advance()
    {
        if (!HasPhases) return null;
        if (s.Game!.State.Position == null) { Start(); return null; }
        var pack = s.Rules!;
        var relocated = Reconcile();
        if (Resolved(out _) is not { } cur) return null;
        var next = Sequencer.Next(pack, cur);
        if (next == null) return null;
        var from = pack.Phases[cur.PhaseIndex];
        var to = pack.Phases[next.PhaseIndex];
        _notice = relocated;
        Exec(new AdvancePhase(cur, next, from.Name, to.Name));
        var toSide = Sequencer.ActingSide(pack, to);
        var pass = toSide != null && toSide.Id != Sequencer.ActingSide(pack, from)?.Id ? $"Pass to {toSide.Name} player" : null;
        _notice = pass == null ? relocated : relocated == null ? pass : relocated + ". " + pass;
        OnEnter(to);
        PhaseBoundary?.Invoke();
        return pass;
    }

    /// <summary>After a load or a rules change: moves a game whose phase vanished to the phase now at that place (logged). Returns the strip notice.</summary>
    public string? Reconcile()
    {
        if (!HasPhases || s.Game!.State.Position is not { } stored) return null;
        var pack = s.Rules!;
        var (pos, notice) = Sequencer.Resolve(pack, stored);
        if (pos == null || notice == null) return null;     // unchanged, or only index / repeat clamped (no state to relocate)
        Exec(new AdvancePhase(stored, pos, stored.PhaseId, pack.Phases[pos.PhaseIndex].Name, notice));
        _notice = notice;
        return notice;
    }

    // ---- difficulty, checklist, warnings ----

    public void SetDifficulty(Difficulty d)
    {
        if (s.Game == null || s.Game.State.Difficulty == d) return;
        Exec(new SetDifficulty(d));
        DifficultyChanged?.Invoke();
    }

    public void Tick(int index, bool ticked)
    {
        var items = CurrentPhase()?.Checklist;
        if (items == null || index < 0 || index >= items.Count) return;
        Exec(new TickChecklist(index, ticked, items[index]));
    }

    public void Acknowledge(int warningIndex)
    {
        var ws = CurrentPhase()?.Warnings;
        if (ws == null || warningIndex < 0 || warningIndex >= ws.Count || ws[warningIndex] == null) return;
        Exec(new AcknowledgeWarning(warningIndex, (ws[warningIndex].Message ?? "").Replace("{unit}", "").Trim()));
    }

    /// <summary>Applies a formula result; <paramref name="runId"/> (when set) marks that unit of the pending run done.</summary>
    public bool ApplyFormula(string? runId, string? pieceId, FormulaTrace trace, bool calculated)
    {
        if (s.Game == null || s.Runner == null) return false;
        if (trace.Error != null || trace.Result == null) return false;   // a failed trace changes nothing and keeps the run pending
        // A linked apply needs its run (and that unit) still pending: the phase may have moved on or another game been loaded.
        if (runId != null && !IsPending(runId, pieceId)) return false;
        var unit = UnitName(pieceId) ?? trace.Formula.Name;
        var tag = calculated ? "auto-calculated" : null;
        foreach (var c in s.Runner.Commands(trace, null, tag, unit)) Exec(c);
        LogWorking(trace, unit, tag);
        if (runId != null) Exec(new ResolveRun(runId, new[] { pieceId }));
        return true;
    }

    /// <summary>True while the run exists and the unit for this piece is not yet applied.</summary>
    public bool IsPending(string runId, string? pieceId) =>
        s.Game?.State.Pending.FirstOrDefault(r => r.Id == runId) is { } run && run.Units.Any(u => u.PieceId == pieceId && !u.Applied);

    /// <summary>A run with no ledger outputs has no ledger line carrying its working; log it so every calculation is visible (spec 9).</summary>
    private void LogWorking(FormulaTrace trace, string unit, string? tag)
    {
        if (trace.Outputs.Count > 0 || string.IsNullOrWhiteSpace(trace.Working)) return;
        Calc($"{unit}: {trace.Working}" + (tag == null ? "" : $" ({tag})"));
    }

    private string? UnitName(string? pieceId) =>
        pieceId != null && s.Game!.State.Pieces.TryGetValue(pieceId, out var p) ? PieceInfo.Name(p) : null;

    // ---- triggers ----

    private void OnEnter(PhaseDef phase)
    {
        if (s.Game == null || s.Rules is not { } pack) return;
        var queued = new List<PendingRun>();
        foreach (var e in phase.OnStart ?? new())
        {
            if (e == null) continue;
            var f = FindFormula(phase, e.FormulaId);
            if (f == null) continue;
            if (BadFilter(phase, f, e.UnitFilter)) continue;
            var units = e.ForEach == ForEachMode.Once
                ? new List<Unit> { new(null, f.Name, null) }
                : Sequencer.UnitsFor(pack, s.Game.State, phase, f, e.UnitFilter).Select(p => new Unit(p.Id, PieceInfo.Name(p), null)).ToList();
            Apply(f, "start", units, queued);
        }
        if (queued.Count > 0) RunsQueued?.Invoke(queued);
    }

    /// <summary>Executes one move gesture (any number of pieces), then fires the current phase's OnMove entries for the moved pieces.</summary>
    public void ExecuteMove(IReadOnlyList<MovePieces> moves)
    {
        if (s.Game is not { } g) return;
        var records = new Dictionary<string, MoveRecord>();
        foreach (var m in moves)
            foreach (var id in m.PieceIds ?? Array.Empty<string>())
                if (g.State.Pieces.TryGetValue(id, out var p)) records.TryAdd(id, new MoveRecord(id, p.MapId, p.Pos));
        foreach (var m in moves) Exec(m);

        if (s.Rules is not { } pack || s.Runner == null || CurrentPhase() is not { } phase || phase.OnMove is not { Count: > 0 }) return;
        var distances = new Dictionary<string, double>();
        var queued = new List<PendingRun>();
        foreach (var e in phase.OnMove)
        {
            if (e == null) continue;
            var f = FindFormula(phase, e.FormulaId);
            if (f == null) continue;
            if (BadFilter(phase, f, e.UnitFilter)) continue;
            var units = new List<Unit>();
            foreach (var p in Sequencer.UnitsFor(pack, g.State, phase, f, e.UnitFilter))
            {
                if (!records.TryGetValue(p.Id, out var rec)) continue;
                if (!distances.TryGetValue(p.Id, out var d))
                {
                    var (cells, problem) = MoveMeasure.Cells(s.Model, rec.FromMap, rec.From, p.MapId, p.Pos);
                    d = cells;
                    distances[p.Id] = d;
                    if (problem != null) Calc($"{PieceInfo.Name(p)}: moved distance counted as 0 ({problem})");
                }
                units.Add(new Unit(p.Id, PieceInfo.Name(p), d));
            }
            Apply(f, "move", units, queued);
        }
        if (queued.Count > 0) RunsQueued?.Invoke(queued);
    }

    private FormulaDef? FindFormula(PhaseDef phase, string id)
    {
        var f = s.Rules!.ById<FormulaDef>(id);
        if (f == null) Calc($"{phase.Name}: formula \"{id}\" not found — skipped");
        return f;
    }

    private bool BadFilter(PhaseDef phase, FormulaDef f, string? entryFilter)
    {
        var side = Sequencer.ActingSide(s.Rules!, phase);
        foreach (var src in new[] { entryFilter, f.AppliesTo, side?.UnitFilter })
        {
            if (string.IsNullOrWhiteSpace(src)) continue;
            try { Parser.Parse(src); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Calc($"{phase.Name}: filter \"{src}\" is not valid — skipped");
                return true;
            }
        }
        return false;
    }

    private void Apply(FormulaDef f, string trigger, List<Unit> units, List<PendingRun> queued)
    {
        if (units.Count == 0 || s.Game == null) return;
        var mode = s.Game.State.Difficulty;
        if (mode != Difficulty.FullyAuto || s.Runner == null)
        {
            var run = new PendingRun(Guid.NewGuid().ToString("N"), f.Id, f.Name, trigger,
                units.Select(u => new PendingUnit(u.PieceId, u.Label, u.Moved, false)).ToList(), false);
            Exec(new QueueRuns(new[] { run }));
            queued.Add(run);
            return;
        }

        var none = new Dictionary<string, string>();
        var noRolls = new Dictionary<string, IReadOnlyList<int>>();
        var applied = 0;
        var needInput = new List<Unit>();
        foreach (var u in units)
        {
            var trace = s.Runner.Run(s.Game.State, new RunRequest(f.Id, u.PieceId, none, none, noRolls, u.Moved, true));
            if (trace.Error != null) { Calc($"{f.Name}: {u.Label}: {trace.Error}"); continue; }
            if (trace.Result == null) { needInput.Add(u); continue; }
            foreach (var c in s.Runner.Commands(trace, null, "auto", u.Label)) Exec(c);
            LogWorking(trace, u.Label, "auto");
            applied++;
        }
        if (units.Count > 1) Calc($"{f.Name}: {units.Count} units, {applied} applied");
        if (needInput.Count > 0)
        {
            var run = new PendingRun(Guid.NewGuid().ToString("N"), f.Id, f.Name, trigger,
                needInput.Select(u => new PendingUnit(u.PieceId, u.Label, u.Moved, false)).ToList(), true);
            Exec(new QueueRuns(new[] { run }));
            queued.Add(run);
        }
    }
}
