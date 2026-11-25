using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>Where a piece was before a move gesture (for measuring the distance moved).</summary>
public sealed record MoveRecord(string PieceId, string FromMap, PointD From);

public enum ChipState { Done, Current, Upcoming, Skipped, WillSkip }

public sealed record PhaseChip(string Id, string Name, ChipState State, string? Notes = null, string? Tip = null);

/// <summary>Everything the tracker strip and phase panel show. Built fresh by <see cref="PhaseController.View"/>.</summary>
public sealed record PhaseView(bool NoPhases, bool BeforeStart, int Turn, string? StageText, string? SideName,
    IReadOnlyList<PhaseChip> Chips, PhaseDef? Current, string? NextName, IReadOnlyList<Decision> Decisions,
    IReadOnlyList<WarningHit> Warnings, string? Notice, string? PlayingText = null);

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
    private PhasePosition? _noticeAt;   // the position the notice belongs to; once the game is elsewhere (undo, step back) it is dropped
    private PhasePosition? _noticeEnded; // and its end-of-phase mark (undoing a press that stopped for end runs drops its notice)

    private string? Notice
    {
        get
        {
            if (_notice != null && (s.Game?.State.Position != _noticeAt || s.Game?.State.EndedAt != _noticeEnded)) _notice = null;
            return _notice;
        }
    }

    private void SetNotice(string? text) { _notice = text; _noticeAt = s.Game?.State.Position; _noticeEnded = s.Game?.State.EndedAt; }

    /// <summary>Runs queued (or needing input) by a trigger; raised once per OnStart / OnMove batch that queued any.</summary>
    public event Action<IReadOnlyList<PendingRun>>? RunsQueued;

    /// <summary>Raised after the phase changed (start, advance), once its OnStart has run and the "Next Phase" action has closed.</summary>
    public event Action? PhaseBoundary;

    /// <summary>Raised after the difficulty of the open game changed (windows that were checking in the old mode must close).</summary>
    public event Action? DifficultyChanged;

    private void Exec(GameCommand c)
    {
        if (s.Game is not { } g) return;
        if (_actorWho != null) c = CommandLog.Stamp(c, _actorWho);
        g.Execute(prepare(c, g.State));
    }

    // Online host: a guest's intent runs for the guest's sides (not the host's view) and its commands carry the guest as Who.
    private bool _actorSet;
    private IReadOnlySet<string>? _actorSides;
    private string? _actorWho;

    /// <summary>The sides the current action is run for: the guest's while <see cref="ActingFor"/> is open, else the session's view.</summary>
    private IReadOnlySet<string>? ActorSides => _actorSet ? _actorSides : s.ViewSides;

    /// <summary>Optional thread-affinity check for <see cref="ActingFor"/> (debug assert); the online host sets it to "not InvokeRequired".</summary>
    public Func<bool>? IsOwnerThread { get; set; }

    /// <summary>
    /// Until disposed, actions run for a player holding <paramref name="sides"/>: formula stamps, OnMove trigger stamps and the
    /// "may act on this unit" check use them instead of the session's view; commands are stamped with <paramref name="who"/>.
    /// Used by the online host to execute a guest's intent. Not reentrant (a nested call throws <see cref="InvalidOperationException"/>).
    /// Like every game change it must run on the thread that owns the game (the host's UI thread); <see cref="IsOwnerThread"/> lets the
    /// host check that in debug builds.
    /// </summary>
    public IDisposable ActingFor(IReadOnlySet<string> sides, string? who)
    {
        System.Diagnostics.Debug.Assert(IsOwnerThread?.Invoke() ?? true, "PhaseController.ActingFor called off the game's owning thread.");
        if (_actorSet) throw new InvalidOperationException("ActingFor is already open.");
        _actorSet = true;
        _actorSides = sides;
        _actorWho = who;
        return new ActorScope(this);
    }

    private sealed class ActorScope(PhaseController owner) : IDisposable
    {
        public void Dispose()
        {
            owner._actorSet = false;
            owner._actorSides = null;
            owner._actorWho = null;
        }
    }

    /// <param name="error">Marks a failure line (LogEntry.Error): a failed or refused run, a missing formula, a bad filter.</param>
    private void Calc(string text, string? pieceId = null, string? formulaId = null, bool error = false) =>
        Exec(new AddLogLine(LogKind.Calc, text, pieceId, SideId: Stamp(), FormulaId: formulaId, Error: error));

    // Hot-seat stamps. Player-driven applies are run for the player at the table (ModuleSession.ActorSide). Automatic triggers
    // (OnStart, OnMove) are run for the phase: its acting side when it has one, held or not; else the player at the table.
    private bool _inTrigger;
    private string? _triggerSide;

    private string? _askStamp;   // while an ask run is applied: the asked side (spec #72)

    private string? Stamp() => _inTrigger ? _triggerSide : _askStamp ?? s.ActorSideFor(ActorSides);

    private string? TriggerSide(PhaseDef phase, IReadOnlySet<string>? sides) =>
        sides == null || s.Rules is not { } pack ? null : Sequencer.ActingSide(pack, phase)?.Id ?? s.ActorSideFor(sides);

    /// <summary>Runs an automatic trigger with the phase's stamp (nested calls keep the outer one). <paramref name="hostView"/>: the stamp is
    /// worked out from the session's view, as Next Phase works it out, even while a guest's intent is run (entries continued after an answer).</summary>
    private T AsTrigger<T>(PhaseDef phase, Func<T> run, bool hostView = false)
    {
        if (_inTrigger) return run();
        _triggerSide = TriggerSide(phase, hostView ? s.ViewSides : ActorSides);
        _inTrigger = true;
        try { return run(); }
        finally { _inTrigger = false; _triggerSide = null; }
    }

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

    // View() also runs on guests: the will-skip chips evaluate phase conditions against the guest's own (viewer-filtered) state. That is safe because
    // conditions read only globals, which PlayerView never hides (except Lockdown, where previews show the defaults). Advancing is still the host's job.
    public PhaseView View()
    {
        var none = Array.Empty<Decision>();
        if (!HasPhases)
            return new PhaseView(true, false, s.Game?.State.Turn.Turn ?? 1, null, null, Array.Empty<PhaseChip>(), null, null, none, Array.Empty<WarningHit>(), Notice);
        var pack = s.Rules!;
        var st = s.Game!.State;
        if (Resolved(out _) is not { } pos)
        {
            var first = PhaseConditions.First(pack, st, s.Model);
            var willSkip0 = WillSkip(pack, st, s.Model, first, 1);
            var chips0 = pack.Phases.Select(p => Chip(p, willSkip0.Contains(p.Id) ? ChipState.WillSkip : ChipState.Upcoming)).ToList();
            var firstName = first == null ? pack.Phases[0].Name : pack.Phases.ElementAtOrDefault(first.To.PhaseIndex)?.Name;
            return new PhaseView(false, true, 1, null, null, chips0, null, firstName, none, Array.Empty<WarningHit>(), Notice);
        }
        var cur = pack.Phases[pos.PhaseIndex];
        var next = PhaseConditions.Next(pack, st, pos, s.Model);
        var skippedBefore = SkippedBefore(pack, st, pos);
        var willSkip = WillSkip(pack, st, s.Model, next, pos.Turn);
        var chips = pack.Phases.Select((p, i) => Chip(p,
            i < pos.PhaseIndex ? (skippedBefore.Contains(p.Id) ? ChipState.Skipped : ChipState.Done)
            : i == pos.PhaseIndex ? ChipState.Current
            : willSkip.Contains(p.Id) ? ChipState.WillSkip : ChipState.Upcoming)).ToList();
        var side = Sequencer.ActingSide(pack, cur);
        var nextName = next == null ? null : pack.Phases.ElementAtOrDefault(next.To.PhaseIndex)?.Name;
        // Hot-seat: another side's phase shows who is playing and none of their decisions.
        if (s.ViewSides is { } held && side != null && !held.Contains(side.Id))
            return new PhaseView(false, false, pos.Turn, Sequencer.StageText(pack, pos), side.Name, chips, cur, nextName, none,
                Array.Empty<WarningHit>(), Notice, $"{(string.IsNullOrWhiteSpace(side.Name) ? side.Id : side.Name)} is playing {cur.Name}");
        // Hot-seat or online: a phase without a side whose ask run another player answers shows who is answering, as a side's phase does.
        if (s.ViewSides is { } holds && side == null && OpenAskSide(st) is { } ask && !holds.Contains(ask)
            && Players.Holding(st, pack, ask) != null && pack.ById<SideDef>(ask) is { } asked)
            return new PhaseView(false, false, pos.Turn, Sequencer.StageText(pack, pos), null, chips, cur, nextName, none,
                Array.Empty<WarningHit>(), Notice, $"{(string.IsNullOrWhiteSpace(asked.Name) ? asked.Id : asked.Name)} is playing {cur.Name}");
        var seen = s.View ?? st;   // decisions and warnings over what this player may see
        return new PhaseView(false, false, pos.Turn, Sequencer.StageText(pack, pos), side?.Name, chips, cur, nextName,
            Decisions.Compute(pack, seen, s.Model), Decisions.Warnings(pack, seen, cur, s.Model), Notice);
    }

    /// <summary>The side asked by the first pending ask run with a unit still open (spec #72), else null.</summary>
    public static string? OpenAskSide(GameState st) =>
        st.Pending.FirstOrDefault(r => !string.IsNullOrEmpty(r.AskSideId) && r.Units.Any(u => !u.Applied))?.AskSideId;

    private const string SkippedTip = "Skipped this pass (condition false)";
    private const int MaxWalk = 10_000;   // a bound for the preview walks; a turn of a real pack is far shorter

    private static PhaseChip Chip(PhaseDef p, ChipState state) => new(p.Id, p.Name, state, p.Notes,
        state == ChipState.Skipped ? SkippedTip : state == ChipState.WillSkip ? $"Will be skipped unless {p.Condition} changes" : null);

    /// <summary>
    /// Ids of the phases before <paramref name="pos"/> whose most recent pass this turn was skipped: a phase that both ran and was skipped
    /// this turn (block repeats) shows its latest pass. The latest pass of each phase is found by walking the plain sequence from the turn's
    /// start to <paramref name="pos"/>; if that walk never gets there, any skip this turn counts.
    /// </summary>
    private static HashSet<string> SkippedBefore(RulesPack pack, GameState st, PhasePosition pos)
    {
        var res = new HashSet<string>();
        var skips = st.SkippedPhases?.Where(k => k != null && k.Turn == pos.Turn).ToList();
        if (skips is not { Count: > 0 }) return res;
        var lastRepeat = new Dictionary<string, int>();
        var at = new PhasePosition(pos.Turn, 1, pack.Phases[0].Id, 0);
        var reached = false;
        for (var guard = 0; guard < MaxWalk && at.Turn == pos.Turn; guard++)
        {
            if (at.PhaseId == pos.PhaseId && at.Repeat == pos.Repeat) { reached = true; break; }
            lastRepeat[at.PhaseId] = at.Repeat;
            if (Sequencer.Next(pack, at) is not { } n) break;
            at = n;
        }
        foreach (var k in skips)
            if (!reached || (lastRepeat.TryGetValue(k.PhaseId, out var r) && r == k.Repeat)) res.Add(k.PhaseId);
        return res;
    }

    /// <summary>
    /// Ids of the phases a walk from <paramref name="step"/> to the end of turn <paramref name="turn"/> skips and never reaches. A preview
    /// only: it evaluates conditions against the current state and applies nothing. Empty when the walk hits the loop guard.
    /// </summary>
    private static HashSet<string> WillSkip(RulesPack pack, GameState st, ModuleModel? model, PhaseStep? step, int turn)
    {
        var skipped = new HashSet<string>();
        var reached = new HashSet<string>();
        for (var guard = 0; step != null && guard < MaxWalk; guard++)
        {
            if (step.Notice == PhaseConditions.GuardNotice) return new HashSet<string>();
            foreach (var k in step.Skipped)
                if (k.Turn == turn) skipped.Add(k.PhaseId);
            if (step.To.Turn != turn) break;
            reached.Add(step.To.PhaseId);
            var n = PhaseConditions.Next(pack, st, step.To, model);
            if (n == null || n.To == step.To) break;
            step = n;
        }
        skipped.ExceptWith(reached);
        return skipped;
    }

    /// <summary>The strip notice: the non-empty parts joined with ". ".</summary>
    private static string? Join(params string?[] parts)
    {
        var text = string.Join(". ", parts.Where(x => !string.IsNullOrEmpty(x)));
        return text.Length == 0 ? null : text;
    }

    private static string? SkippedText(PhaseStep step) =>
        step.Skipped.Count == 0 ? null : "Skipped: " + string.Join(", ", step.Skipped.Select(k => k.Name));

    // ---- advancing ----

    public void Start()
    {
        if (!HasPhases || s.Game!.State.Position != null) return;
        var pack = s.Rules!;
        var step = PhaseConditions.First(pack, s.Game.State, s.Model);
        if (step == null) return;
        var to = pack.Phases[step.To.PhaseIndex];
        var startNotice = Join(SkippedText(step), step.Notice);
        List<PendingRun> queued;
        // One action; the boundary (and the autosave it triggers) comes after the action closed, so the save holds it and its checkpoint.
        using (s.Game.BeginAction("Next Phase"))
        {
            Exec(new AdvancePhase(null, step.To, "", to.Name, Skipped: step.Skipped.Count == 0 ? null : step.Skipped, Notice: step.Notice));
            SetNotice(startNotice);
            queued = EnterPhase(to);   // only the destination's OnStart runs; skipped phases trigger nothing
        }
        RaiseQueued(queued);
        PhaseBoundary?.Invoke();
    }

    /// <summary>The strip notice when a press ran end-of-phase entries that queued runs, so the game stays in the phase.</summary>
    public const string EndStopNotice = "Finish the end-of-phase runs, then press Next Phase again";

    /// <returns>"Pass to {side} player" when the acting side changed, else null (also when the press stopped for end-of-phase runs).</returns>
    public string? Advance()
    {
        if (!HasPhases) return null;
        if (s.Game!.State.Position == null) { Start(); return null; }
        var pack = s.Rules!;
        string? pass = null;
        List<PendingRun> queued;
        var stopped = false;
        using (s.Game.BeginAction("Next Phase"))
        {
            var relocated = Reconcile();
            if (Resolved(out _) is not { } cur) return null;
            var from = pack.Phases[cur.PhaseIndex];
            // End-of-phase entries run once per pass, in this press's action. All applied: the same press advances (one undo step).
            // Any run queued for the player: the press stops in the phase; the next press finds EndedAt set and advances without
            // re-running them. Skipped phases are never left, so they run none.
            queued = from.OnEnd is { Count: > 0 } && s.Game.State.EndedAt != cur ? OnLeave(from, cur) : new();
            if (queued.Count > 0)
            {
                SetNotice(Join(relocated, EndStopNotice));
                stopped = true;
            }
            else
            {
                // Conditions are evaluated here, against the state before the step (after any end entries); the command records the
                // outcome (replay never re-evaluates).
                var step = PhaseConditions.Next(pack, s.Game.State, cur, s.Model);
                if (step == null) return null;
                var to = pack.Phases[step.To.PhaseIndex];
                SetNotice(relocated);
                LogNotRun(from, cur);
                Exec(new AdvancePhase(cur, step.To, from.Name, to.Name, Skipped: step.Skipped.Count == 0 ? null : step.Skipped, Notice: step.Notice));
                // The prompt compares the destination with the phase left; skipped phases in between never act.
                var toSide = Sequencer.ActingSide(pack, to);
                pass = toSide != null && toSide.Id != Sequencer.ActingSide(pack, from)?.Id ? $"Pass to {toSide.Name} player" : null;
                SetNotice(Join(relocated, SkippedText(step), step.Notice, pass));
                queued = EnterPhase(to);
            }
        }
        RaiseQueued(queued);
        if (stopped) return null;   // still in the phase: no boundary
        PhaseBoundary?.Invoke();
        return pass;
    }

    private void RaiseQueued(List<PendingRun> queued)
    {
        if (queued.Count > 0) RunsQueued?.Invoke(queued);
    }

    /// <summary>After a load or a rules change: moves a game whose phase vanished to the phase now at that place (logged). Returns the strip notice.</summary>
    /// <remarks>Nothing happens while a received turn replays: the sender's game stays exactly as sent (it may use a phase this pack lacks).</remarks>
    public string? Reconcile()
    {
        if (s.IsReplaying || !HasPhases || s.Game!.State.Position is not { } stored) return null;
        var pack = s.Rules!;
        var (pos, notice) = Sequencer.Resolve(pack, stored);
        if (pos == null || notice == null) return null;     // unchanged, or only index / repeat clamped (no state to relocate)
        Exec(new AdvancePhase(stored, pos, stored.PhaseId, pack.Phases[pos.PhaseIndex].Name, notice));
        SetNotice(notice);
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
        if (items == null || index < 0 || index >= items.Count || items[index] is not { } item) return;
        // The raw text (links unrendered) is logged; an item with an id is ticked by id, so reordering items keeps the tick.
        Exec(new TickChecklist(index, ticked, item.Text ?? "", ItemId: item.Id));
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
        if (WritesComputed(s.Rules, trace)) return false;   // a computed value is never stored: the whole trace is refused
        // A linked apply needs its run (and that unit) still pending: the phase may have moved on or another game been loaded.
        if (runId != null && !IsPending(runId, pieceId, trace.Formula.Id)) return false;
        if (!s.CanActFor(pieceId, ActorSides)) return false;   // hot-seat: only the units of the sides held (or of no side)
        var ask = runId == null ? null : s.Game.State.Pending.FirstOrDefault(r => r.Id == runId)?.AskSideId;
        if (!string.IsNullOrEmpty(ask) && ActorSides is { } held && !held.Contains(ask)) return false;   // an ask run is the asked side's (solo: anyone's)
        var name = UnitName(pieceId);
        var unit = name ?? trace.Formula.Name;
        var tag = calculated ? "auto-calculated" : null;
        List<PendingRun> queued = new();
        IReadOnlyList<GameCommand> executed = Array.Empty<GameCommand>();
        using (s.Game.BeginAction(string.IsNullOrWhiteSpace(name) ? trace.Formula.Name : $"{trace.Formula.Name} on {name}"))
        {
            _askStamp = string.IsNullOrEmpty(ask) ? null : ask;   // its rolls and working follow the asked side's privacy
            try
            {
                // The whole run (rolls, a placed piece and its seeds, ledger lines, effects, removals) inside this one action.
                executed = FormulaApply.Execute(s, s.Runner.Commands(trace, null, tag, unit, Stamp()), Exec);
                LogWorking(trace, unit, tag);
            }
            finally { _askStamp = null; }
            if (runId != null) Exec(new ResolveRun(runId, new[] { pieceId }));
            var swept = executed.Any(x => x is RemovePiece) && ResolveGoneUnits();
            if (runId != null || swept) queued = ContinueEntries();   // inside the answer's action: one undo takes back the answer and what it released
        }
        RaiseQueued(queued);
        return true;
    }

    /// <summary>
    /// After a run that deleted pieces (B3: a Piece Remove may delete another pending unit): resolves, in the caller's action, every pending
    /// run's unapplied units whose piece is gone, so a run left with only gone units ends and the start entries are not stalled.
    /// </summary>
    /// <returns>True when any unit was resolved.</returns>
    private bool ResolveGoneUnits()
    {
        if (s.Game?.State is not { } st) return false;
        var sweeps = (st.Pending ?? new()).Where(r => r != null)
            .Select(r => (r.Id, Gone: (r.Units ?? Array.Empty<PendingUnit>()).Where(u => u is { Applied: false, PieceId: { } id } && !st.Pieces.ContainsKey(id))
                .Select(u => u.PieceId).ToList()))
            .Where(x => x.Gone.Count > 0).ToList();
        foreach (var (id, gone) in sweeps) Exec(new ResolveRun(id, gone));
        return sweeps.Count > 0;
    }

    /// <summary>
    /// After an answer (spec #32): when the start entries stopped at this position and no start run is left waiting, runs the entries from the
    /// cursor on, stamped as Next Phase stamps them (the session's view, not a guest's sides). Host only.
    /// </summary>
    private List<PendingRun> ContinueEntries()
    {
        if (s.Game?.State is not { StartCursor: { } cursor } st || cursor.At != st.Position) return new();
        if (st.Pending.Any(r => r is { Trigger: "start" })) return new();
        if (s.IsGuest || s.IsViewerCopy || s.IsReplaying || CurrentPhase() is not { } phase || phase.Id != cursor.At.PhaseId) return new();
        return AsTrigger(phase, () => RunEntries(phase, phase.OnStart, "start", cursor.Next), hostView: true);
    }

    /// <summary>Before leaving a phase whose start entries stopped: names the entries that never ran (Calc, no working, trigger stamp).</summary>
    private void LogNotRun(PhaseDef phase, PhasePosition at)
    {
        if (s.Game?.State.StartCursor is not { } cursor || cursor.At != s.Game.State.Position || cursor.At.PhaseId != at.PhaseId) return;
        var left = (phase.OnStart ?? new()).Skip(Math.Max(0, cursor.Next)).Where(e => e != null)
            .Select(e => s.Rules!.ById<FormulaDef>(e.FormulaId)?.Name ?? e.FormulaId).ToList();
        if (left.Count == 0) return;   // a cursor past the end (a rules edit) counts as done
        AsTrigger(phase, () => { Calc("not run: " + string.Join(", ", left), formulaId: LogKind.NoWorking); return 0; });
    }

    /// <summary>True when an output of the trace writes a computed property (a trace built elsewhere, or from a pack edited by hand).</summary>
    public static bool WritesComputed(RulesPack? pack, FormulaTrace trace) =>
        (trace.Outputs ?? Array.Empty<OutputPreview>()).Any(o => o != null && LedgerViewRules.ComputedRefusal(pack, o.PropertyId) != null);

    /// <summary>True while the run exists and the unit for this piece is not yet applied.</summary>
    public bool IsPending(string runId, string? pieceId, string? formulaId = null) =>
        s.Game?.State.Pending.FirstOrDefault(r => r.Id == runId) is { } run && (formulaId == null || run.FormulaId == formulaId) && run.Units.Any(u => u.PieceId == pieceId && !u.Applied);

    /// <summary>A run with no ledger outputs has no ledger line carrying its working; log it so every calculation is visible (spec 9).</summary>
    /// <remarks>That line records the applied run (<see cref="AddLogLine.AppliedRun"/>, <c>runs()</c>), as the first ledger line does for a run
    /// with outputs, so every applied run is counted exactly once.</remarks>
    private void LogWorking(FormulaTrace trace, string unit, string? tag)
    {
        // A run with any output or effect logs its working itself (FormulaRunner.Commands: on its first own write, else on a line of its own).
        if (trace.Outputs.Count > 0 || (trace.Effects?.Count ?? 0) > 0 || string.IsNullOrWhiteSpace(trace.Working)) return;
        Exec(new AddLogLine(LogKind.Calc, $"{unit}: {trace.Working}" + (tag == null ? "" : $" ({tag})"), trace.PieceId, SideId: Stamp(),
            FormulaId: trace.Formula.Id) { AppliedRun = trace.Formula.Id });
    }

    private string? UnitName(string? pieceId) =>
        pieceId != null && s.Game!.State.Pieces.TryGetValue(pieceId, out var p) ? PieceInfo.Name(p) : null;

    // ---- triggers ----

    /// <summary>Entering a phase (Start, Next Phase): resets the properties that reset there (spec #56), seeds again, then runs the start entries.</summary>
    /// <returns>The runs queued (the caller raises <see cref="RunsQueued"/> once its action has closed).</returns>
    private List<PendingRun> EnterPhase(PhaseDef phase)
    {
        ResetAtEntry(phase);
        return OnEnter(phase);
    }

    private void ResetAtEntry(PhaseDef phase)
    {
        if (s.Rules is not { } pack || s.IsGuest || s.IsViewerCopy || s.IsReplaying) return;   // host only
        var props = (pack.Properties ?? new()).Where(p => p?.ResetAt != null && p.ResetAt.Contains(phase.Id, StringComparer.Ordinal)).ToList();
        if (props.Count == 0) return;
        Exec(new ResetProperties(props.Select(p => p.Id).ToList(),
            $"{phase.Name}: reset {string.Join(", ", props.Select(p => string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name))}"));
        s.SeedLedgers();   // sheet and starting value properties start again from their starting value
    }

    /// <returns>The runs queued (the caller raises <see cref="RunsQueued"/> once its action has closed).</returns>
    private List<PendingRun> OnEnter(PhaseDef phase) => AsTrigger(phase, () => RunEntries(phase, phase.OnStart, "start"));

    /// <summary>Runs the phase's end entries (tag "end") and records that they ran this pass (<see cref="EndPhase"/>).</summary>
    /// <returns>The runs queued (the caller raises <see cref="RunsQueued"/> once its action has closed).</returns>
    private List<PendingRun> OnLeave(PhaseDef phase, PhasePosition at)
    {
        var queued = AsTrigger(phase, () => RunEntries(phase, phase.OnEnd, "end"));
        Exec(new EndPhase(at));
        return queued;
    }

    /// <summary>
    /// OnStart / OnEnd entries from index <paramref name="from"/>: once, or for each unit of the phase's side. Start entries run strictly in
    /// order (spec #32): after the first entry that queued a run the rest wait and <see cref="GameState.StartCursor"/> records where; once
    /// every entry has run a set cursor is cleared. OnEnd entries all run at once.
    /// </summary>
    private List<PendingRun> RunEntries(PhaseDef phase, List<StartEntry>? entries, string trigger, int from = 0)
    {
        var queued = new List<PendingRun>();
        if (s.Game == null || s.Rules is not { } pack) return queued;
        var list = entries ?? new();
        var start = trigger == "start";
        for (var i = Math.Max(0, from); i < list.Count; i++)
        {
            var e = list[i];
            if (e == null) continue;
            var f = FindFormula(phase, e.FormulaId);
            if (f == null) continue;
            if (BadFilter(phase, f, e.UnitFilter)) continue;
            var units = e.ForEach == ForEachMode.Once
                ? new List<Unit> { new(null, f.Name, null) }
                : Sequencer.UnitsFor(pack, s.Game.State, phase, f, e.UnitFilter, s.Model).Select(p => new Unit(p.Id, PieceInfo.Name(p), null)).ToList();
            if (Apply(f, trigger, units, queued, e.AskSideId) && start && i + 1 < list.Count)
            {
                Exec(new SetStartCursor(new StartCursor(s.Game.State.Position!, i + 1)));
                return queued;
            }
        }
        if (start && s.Game.State.StartCursor != null) Exec(new SetStartCursor(null));
        return queued;
    }

    /// <summary>Executes one move gesture (any number of pieces), then fires the current phase's OnMove entries for the moved pieces.</summary>
    public void ExecuteMove(IReadOnlyList<MovePieces> moves)
    {
        if (s.Game is not { } g) return;
        List<PendingRun> queued;
        using (g.BeginAction()) queued = MoveAndTrigger(g, moves);
        RaiseQueued(queued);
    }

    /// <summary>The moves plus their OnMove triggers (one action, opened by <see cref="ExecuteMove"/>).</summary>
    private List<PendingRun> MoveAndTrigger(GameSession g, IReadOnlyList<MovePieces> moves)
    {
        var queued = new List<PendingRun>();
        var records = new Dictionary<string, MoveRecord>();
        foreach (var m in moves)
            foreach (var id in m.PieceIds ?? Array.Empty<string>())
                if (g.State.Pieces.TryGetValue(id, out var p)) records.TryAdd(id, new MoveRecord(id, p.MapId, p.Pos));
        foreach (var m in moves) Exec(m);

        if (s.Rules is not { } pack || s.Runner == null || CurrentPhase() is not { } phase || phase.OnMove is not { Count: > 0 }) return queued;
        return AsTrigger(phase, () => OnMoveCore(g, pack, phase, records, queued));
    }

    private List<PendingRun> OnMoveCore(GameSession g, RulesPack pack, PhaseDef phase, Dictionary<string, MoveRecord> records, List<PendingRun> queued)
    {
        var distances = new Dictionary<string, double>();
        var bySide = Sequencer.ActingSide(pack, phase) != null;
        Func<PieceInstance, string?>? owner = null;   // built once per call, only when an Other / Any entry needs it
        foreach (var e in phase.OnMove)
        {
            if (e == null) continue;
            var f = FindFormula(phase, e.FormulaId);
            if (f == null) continue;
            if (BadFilter(phase, f, e.UnitFilter)) continue;
            var moved = Sequencer.MovedUnitsFor(pack, g.State, phase, f, e, s.Model).Where(p => records.ContainsKey(p.Id)).ToList();
            if (!bySide || e.FiresFor == MoveSide.Acting)
            {
                Apply(f, "move", MovedUnits(moved, records, distances), queued);
                continue;
            }
            // Other / Any: each owner's pieces are a run made for that owner (its stamp, its own queued run), as if that player ran it.
            owner ??= Ownership.PieceSides(g.State, pack);
            foreach (var group in moved.GroupBy(p => owner(p)))
                AsOwner(group.Key, () => Apply(f, "move", MovedUnits(group, records, distances), queued));
        }
        return queued;
    }

    /// <summary>The moved pieces as units with their moved distance (measured once per piece; a problem is logged and counts as 0).</summary>
    private List<Unit> MovedUnits(IEnumerable<PieceInstance> pieces, Dictionary<string, MoveRecord> records, Dictionary<string, double> distances)
    {
        var units = new List<Unit>();
        foreach (var p in pieces)
        {
            var rec = records[p.Id];
            if (!distances.TryGetValue(p.Id, out var d))
            {
                var (cells, problem) = MoveMeasure.Cells(s.Model, rec.FromMap, rec.From, p.MapId, p.Pos);
                d = cells;
                distances[p.Id] = d;
                if (problem != null) Calc($"{PieceInfo.Name(p)}: moved distance counted as 0 ({problem})", p.Id, LogKind.NoWorking);
            }
            units.Add(new Unit(p.Id, PieceInfo.Name(p), d));
        }
        return units;
    }

    /// <summary>
    /// Inside a trigger, runs <paramref name="run"/> stamped for <paramref name="side"/> (a piece's owner) when a hot-seat stamp is in use;
    /// a piece of no side keeps the phase's stamp.
    /// </summary>
    private void AsOwner(string? side, Action run)
    {
        var outer = _triggerSide;
        if (side != null && ActorSides != null) _triggerSide = side;
        try { run(); }
        finally { _triggerSide = outer; }
    }

    private FormulaDef? FindFormula(PhaseDef phase, string id)
    {
        var f = s.Rules!.ById<FormulaDef>(id);
        if (f == null) Calc($"{phase.Name}: formula \"{id}\" not found — skipped", formulaId: LogKind.NoWorking, error: true);
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
                Calc($"{phase.Name}: filter \"{src}\" is not valid — skipped", formulaId: LogKind.NoWorking, error: true);
                return true;
            }
        }
        return false;
    }

    /// <summary>A roll attempted while a start unit's precondition is checked (<see cref="Applicable"/>): the unit is kept.</summary>
    private sealed class NoRollsHere() : Exception("Rolls are not made here");

    /// <summary>
    /// Semi-automatic and Hardcore start entries (spec #32): leaves out the units whose precondition is decided false, logging the refusal
    /// as Fully automatic does, so a unit that can never be applied does not hold the later entries. The check uses no inputs and no auto
    /// values and never rolls; a unit whose precondition is undecided (or whose run fails for another reason) stays in the run.
    /// </summary>
    private List<Unit> Applicable(FormulaDef f, List<Unit> units)
    {
        if (s.Runner is not { } runner || s.Game is not { } g || string.IsNullOrWhiteSpace(f.Precondition)) return units;
        var none = new Dictionary<string, string>();
        var noRolls = new Dictionary<string, IReadOnlyList<int>>();
        Func<string, int, int, IReadOnlyList<int>> dice = (_, _, _) => throw new NoRollsHere();
        var kept = new List<Unit>();
        using (runner.BeginPass(g.State))
            foreach (var u in units)
            {
                var trace = runner.Run(g.State, new RunRequest(f.Id, u.PieceId, none, none, noRolls, u.Moved, false, dice));
                if (trace.Refused) Calc($"{f.Name}: {u.Label}: {trace.Error}", u.PieceId, f.Id, error: true);   // as Fully automatic logs it
                else kept.Add(u);
            }
        return kept;
    }

    /// <param name="askSideId">An entry that asks a side (spec #72): its run is always queued, for that side, in every difficulty.</param>
    /// <returns>True when a run was queued.</returns>
    private bool Apply(FormulaDef f, string trigger, List<Unit> units, List<PendingRun> queued, string? askSideId = null)
    {
        if (units.Count == 0 || s.Game == null) return false;
        var mode = s.Game.State.Difficulty;
        var ask = string.IsNullOrWhiteSpace(askSideId) ? null : askSideId;
        if (mode != Difficulty.FullyAuto || s.Runner == null || ask != null)
        {
            if (mode != Difficulty.FullyAuto && trigger == "start") units = Applicable(f, units);
            if (units.Count == 0) return false;   // every unit refused: the entry counts as done
            var run = new PendingRun(Guid.NewGuid().ToString("N"), f.Id, f.Name, trigger,
                units.Select(u => new PendingUnit(u.PieceId, u.Label, u.Moved, false)).ToList(), false) { AskSideId = ask };
            Exec(new QueueRuns(new[] { run }));
            queued.Add(run);
            return true;
        }

        var none = new Dictionary<string, string>();
        var noRolls = new Dictionary<string, IReadOnlyList<int>>();
        var applied = 0;
        var needInput = new List<Unit>();
        var g = s.Game;
        var state = g.State;
        // One pass (spec #31): the units share one side map and one aggregate state, and the entry raises Changed once; each unit reads the
        // writes of the units before it. A unit whose commands add, remove or move a piece or change a trait state drops the pass's side map
        // and aggregate state (AfterApply), so the next unit sees the change. A placed piece never joins this entry's units (the list is fixed).
        using (g.DeferChanged())
        {
            using (s.Runner.BeginPass(state))
                foreach (var u in units)
                {
                    if (u.PieceId != null && !g.State.Pieces.ContainsKey(u.PieceId)) continue;   // removed by an earlier unit: skipped, no line
                    var trace = s.Runner.Run(state, new RunRequest(f.Id, u.PieceId, none, none, noRolls, u.Moved, true));
                    if (trace.Error != null) { Calc($"{f.Name}: {u.Label}: {trace.Error}", u.PieceId, f.Id, error: true); continue; }   // hidden like its working
                    if (trace.Result == null) { needInput.Add(u); continue; }
                    var executed = FormulaApply.Execute(s, s.Runner.Commands(trace, null, "auto", u.Label, Stamp()), Exec);
                    LogWorking(trace, u.Label, "auto");
                    s.Runner.AfterApply(executed);
                    applied++;
                }
            if (units.Count > 1) Calc($"{f.Name}: {units.Count} units, {applied} applied", formulaId: LogKind.NoWorking);
        }
        if (needInput.Count > 0)
        {
            var run = new PendingRun(Guid.NewGuid().ToString("N"), f.Id, f.Name, trigger,
                needInput.Select(u => new PendingUnit(u.PieceId, u.Label, u.Moved, false)).ToList(), true);
            Exec(new QueueRuns(new[] { run }));
            queued.Add(run);
            return true;
        }
        return false;
    }
}
