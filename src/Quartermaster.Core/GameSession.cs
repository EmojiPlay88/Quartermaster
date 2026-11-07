namespace Quartermaster.Core;

public sealed class GameSession
{
    const int MaxCached = 40;
    /// <summary>Besides checkpoints, a snapshot is cached whenever an action crosses a multiple of this many commands.</summary>
    internal const int SnapshotEvery = 500;

    readonly List<GameCommand> _log = new();
    readonly List<ActionMark> _actions = new();
    readonly Stack<UndoneAction> _redo = new();
    readonly Dictionary<int, GameState> _cache = new();
    readonly List<int> _cacheOrder = new();
    int _scopeDepth;
    string? _scopeLabel;
    bool _scopeActionOpen;
    bool _scopeSettle;
    bool _redoing;
    readonly List<Checkpoint> _checkpoints = new();
    bool _cpPending;
    string? _cpAdvanceName;
    DateTimeOffset? _cpAt;

    public GameSession(GameState initial, string moduleSha256)
    {
        State = initial;
        Genesis = initial.DeepClone();
        _cache[0] = Genesis;
        ModuleSha256 = moduleSha256;
        ResetCheckpoints();
    }

    /// <summary>The live state. The object is replaced on rewind, so never hold it across calls.</summary>
    public GameState State { get; private set; }
    /// <summary>State at log index 0 (deep clone, never mutated).</summary>
    public GameState Genesis { get; private set; }
    public string ModuleSha256 { get; }
    /// <summary>Hash of the rules pack the ledgers were played with; null when none.</summary>
    public string? RulesSha256 { get; set; }
    /// <summary>
    /// True for an online guest's exported filtered view (a "viewer copy"): it opens read-only and cannot be continued. Saved in the file.
    /// </summary>
    public bool ViewerCopy { get; init; }
    public IReadOnlyList<GameCommand> Log => _log;
    public IReadOnlyList<ActionMark> Actions => _actions;
    /// <summary>Phase/turn boundaries that can be stepped back to. [0] is always game start (or the load point, see <see cref="StartLabel"/>).</summary>
    public IReadOnlyList<Checkpoint> Checkpoints => _checkpoints;
    public event Action<GameCommand>? Changed;
    public event Action? Rewound;

    /// <summary>True while an action scope (<see cref="BeginAction"/>) is open; rewinds are refused then.</summary>
    public bool InAction => _scopeDepth > 0;
    public bool CanUndo => _actions.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _actions.Count > 0 ? _actions[^1].Label : null;
    /// <summary>The undone actions, next to redo first.</summary>
    public IReadOnlyList<UndoneAction> RedoActions => _redo.ToArray();
    public string? RedoLabel => _redo.Count > 0 ? _redo.Peek().Label : null;

    /// <summary>Commands that never form an action of their own (view-only or bookkeeping).</summary>
    public static bool IsFolding(GameCommand c) => c switch
    {
        SetStackExpanded => true,
        SeedLedger => true,
        SteppedBack => true,
        _ => false,
    };

    /// <summary>Groups the commands executed inside the scope into one action. Nested scopes join the outermost.</summary>
    public IDisposable BeginAction(string? label = null)
    {
        if (_scopeDepth++ == 0) { _scopeLabel = label; _scopeActionOpen = false; ClearPendingCheckpoint(); }
        return new Scope(this);
    }

    /// <summary>Like <see cref="BeginAction"/>, for a follow-up a rewind makes itself (e.g. moving off a deleted phase):
    /// the action it records does not clear the redo stack.</summary>
    public IDisposable BeginSettle(string label)
    {
        var scope = BeginAction(label);
        if (_scopeDepth == 1) _scopeSettle = true;
        return scope;
    }

    sealed class Scope(GameSession s) : IDisposable
    {
        bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            s.EndScope();
        }
    }

    void EndScope()
    {
        if (--_scopeDepth > 0) return;
        if (_scopeActionOpen) CloseAction();
        _scopeLabel = null;
        _scopeActionOpen = false;
        _scopeSettle = false;
    }

    /// <summary>The single entry point for changing game state.</summary>
    public void Execute(GameCommand c)
    {
        bool oneShot = false;
        if (_scopeDepth > 0)
        {
            if (!_scopeActionOpen) { NewAction(_scopeLabel ?? ActionLabels.For(c, 1), keepRedo: _scopeSettle); _scopeActionOpen = true; }
        }
        else if (!IsFolding(c))
        {
            NewAction(ActionLabels.For(c, 1));
            ClearPendingCheckpoint();
            oneShot = true;
        }
        // folding outside a scope: joins the last action, or is an orphan at the head

        State.Apply(c);
        _log.Add(c);
        NoteCheckpoint(c);
        if (oneShot) CloseAction();
        Changed?.Invoke(c);
    }

    void ClearPendingCheckpoint() { _cpPending = false; _cpAdvanceName = null; _cpAt = null; }

    void NoteCheckpoint(GameCommand c)
    {
        switch (c)
        {
            case AdvancePhase { Reason: null } ap: _cpPending = true; _cpAdvanceName = ap.ToName; _cpAt = ap.At; break;
            case SetTurn st: _cpPending = true; _cpAt = st.At; break;
        }
    }

    /// <summary>Called when an action ends: records a checkpoint if it contained a phase advance or turn change.</summary>
    void CloseAction()
    {
        if (_cpPending)
        {
            var label = _cpAdvanceName ?? $"Turn {State.Turn.Turn}";
            _checkpoints.Add(new Checkpoint(_log.Count, State.Turn.Turn, State.Position, label, _cpAt));
            ClearPendingCheckpoint();
            CacheSnapshot(_log.Count);
        }
        // A game with no phase or turn changes still gets a snapshot every SnapshotEvery commands, so undo never replays from genesis.
        else if (_actions.Count > 0 && _actions[^1].Start / SnapshotEvery < _log.Count / SnapshotEvery)
            CacheSnapshot(_log.Count);
    }

    /// <summary>Label of checkpoint 0: "load point" when an old save's history was discarded on load, else "game start".</summary>
    public string StartLabel => RebasedOnLoad ? "load point" : "game start";

    void ResetCheckpoints()
    {
        _checkpoints.Clear();
        _checkpoints.Add(new Checkpoint(0, Genesis.Turn.Turn, Genesis.Position, StartLabel, null));
    }

    void NewAction(string label, bool keepRedo = false)
    {
        _actions.Add(new ActionMark(_log.Count, label));
        if (!_redoing && !keepRedo) _redo.Clear();
    }

    /// <summary>Splits the commands from <paramref name="logIndex"/> onward into redoable actions.</summary>
    List<UndoneAction> SplitFrom(int logIndex)
    {
        var removedMarks = _actions.Where(a => a.Start >= logIndex).ToList();
        var prev = _actions.LastOrDefault(a => a.Start < logIndex);
        int firstRemoved = removedMarks.Count > 0 ? removedMarks[0].Start : _log.Count;
        var undone = new List<UndoneAction>();
        // A cut through the middle of an action: its tail is redone first, as its own entry.
        // A tail of only folding commands (SteppedBack, SeedLedger, view state) is dropped, not redoable.
        if (prev != null && firstRemoved > logIndex
            && !_log.Skip(logIndex).Take(firstRemoved - logIndex).All(IsFolding))
            undone.Add(new UndoneAction(prev.Label, _log.GetRange(logIndex, firstRemoved - logIndex)));
        for (int k = 0; k < removedMarks.Count; k++)
        {
            // With no earlier action, orphan folded commands before the first removed action go down with it.
            int from = k == 0 && prev == null ? logIndex : removedMarks[k].Start;
            int to = k + 1 < removedMarks.Count ? removedMarks[k + 1].Start : _log.Count;
            undone.Add(new UndoneAction(removedMarks[k].Label, _log.GetRange(from, to - from)));
        }
        return undone;
    }

    /// <summary>What <see cref="RewindTo"/> would undo.</summary>
    public RewindPreview Preview(int logIndex)
    {
        if (logIndex < 0 || logIndex > _log.Count) throw new ArgumentOutOfRangeException(nameof(logIndex));
        int dice = 0;
        var ledger = new List<(string PropertyId, HashSet<string> Keys)>();
        var moved = new HashSet<string>(); var added = new HashSet<string>(); var removed = new HashSet<string>();
        for (int i = logIndex; i < _log.Count; i++)
        {
            switch (_log[i])
            {
                case RollDice: dice++; break;
                case LedgerDelta d:
                    var e = ledger.FindIndex(x => x.PropertyId == d.PropertyId);
                    if (e < 0) { ledger.Add((d.PropertyId, new HashSet<string>())); e = ledger.Count - 1; }
                    ledger[e].Keys.Add(d.Key);
                    break;
                case MovePieces m: foreach (var id in m.PieceIds) moved.Add(id); break;
                case AddPiece a: added.Add(a.PieceId); break;
                case RemovePiece r: removed.Add(r.PieceId); break;
            }
        }
        return new RewindPreview(SplitFrom(logIndex).Count, dice,
            ledger.Select(x => (x.PropertyId, x.Keys.Count)).ToList(), moved.Count, added.Count, removed.Count);
    }

    /// <summary>Truncates the log to <paramref name="logIndex"/> and rebuilds state; removed actions become redoable.</summary>
    public IReadOnlyList<UndoneAction> RewindTo(int logIndex)
    {
        if (_scopeDepth > 0) throw new InvalidOperationException("Cannot rewind while an action scope is open.");
        if (logIndex < 0 || logIndex > _log.Count) throw new ArgumentOutOfRangeException(nameof(logIndex));

        var undone = SplitFrom(logIndex);
        Truncate(logIndex);
        for (int i = undone.Count - 1; i >= 0; i--) _redo.Push(undone[i]);
        Rewound?.Invoke();
        return undone;
    }

    /// <summary>Cuts the log at <paramref name="logIndex"/> and rebuilds state; the redo stack is not touched.</summary>
    void Truncate(int logIndex)
    {
        // Rebuild state from the nearest snapshot at or before the target.
        int baseIdx = _cache.Keys.Where(k => k <= logIndex).Max();
        var state = _cache[baseIdx].DeepClone();
        for (int i = baseIdx; i < logIndex; i++) state.Apply(_log[i]);

        _log.RemoveRange(logIndex, _log.Count - logIndex);
        _actions.RemoveAll(a => a.Start >= logIndex);
        _checkpoints.RemoveAll(c => c.LogIndex > logIndex);
        foreach (var k in _cacheOrder.Where(k => k > logIndex).ToList()) { _cache.Remove(k); _cacheOrder.Remove(k); }
        State = state;
    }

    /// <summary>Removes the last logged command (e.g. a trailing <see cref="SteppedBack"/> before Redo) without touching the redo stack.</summary>
    public void DropTail()
    {
        if (_scopeDepth > 0) throw new InvalidOperationException("Cannot rewind while an action scope is open.");
        if (_log.Count == 0) return;
        Truncate(_log.Count - 1);
        Rewound?.Invoke();
    }

    /// <summary>Forgets every undone action (e.g. after a rules-pack change).</summary>
    public void ClearRedo() => _redo.Clear();

    /// <summary>Rewinds the last action. Returns what was undone, in log order.</summary>
    public IReadOnlyList<UndoneAction> Undo()
    {
        if (_scopeDepth > 0) throw new InvalidOperationException("Cannot undo while an action scope is open.");
        if (_actions.Count == 0) return Array.Empty<UndoneAction>();
        return RewindTo(_actions[^1].Start);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var a = _redo.Pop();
        _redoing = true;
        try
        {
            using (BeginAction(a.Label))
                foreach (var c in a.Commands) Execute(c);
        }
        finally { _redoing = false; }
    }

    /// <summary>
    /// Replaces the whole state with <paramref name="state"/> (an online guest's snapshot) and discards history, then raises
    /// <see cref="Rewound"/> so views refresh as after a rewind. No command runs.
    /// </summary>
    public void ReplaceWithSnapshot(GameState state)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        Rebase();
        Rewound?.Invoke();
    }

    /// <summary>Makes the current state the new genesis; history is discarded.</summary>
    public void Rebase()
    {
        Genesis = State.DeepClone();
        _log.Clear();
        _actions.Clear();
        _redo.Clear();
        ResetCache();
        ResetCheckpoints();
    }

    void ResetCache()
    {
        _cache.Clear();
        _cacheOrder.Clear();
        _cache[0] = Genesis;
    }

    /// <summary>Snapshots the current state at <paramref name="index"/> (must equal the log length) for faster rewinds.</summary>
    internal void CacheSnapshot(int index)
    {
        if (index <= 0 || index != _log.Count || _cache.ContainsKey(index)) return;
        _cache[index] = State.DeepClone();
        _cacheOrder.Add(index);
        while (_cacheOrder.Count > MaxCached)
        {
            _cache.Remove(_cacheOrder[0]);
            _cacheOrder.RemoveAt(0);
        }
    }

    /// <summary>Gives a legacy game its id after the history check, on the live state and genesis alike so replay still matches.</summary>
    internal void AssignGameId(string id)
    {
        State.GameId = id;
        Genesis.GameId = id;
        foreach (var s in _cache.Values) s.GameId = id;
    }

    internal void ReplaceState(GameState state) => State = state;

    internal void RestoreLog(IEnumerable<GameCommand> log) => _log.AddRange(log);

    /// <summary>Log indices with a cached snapshot (tests).</summary>
    internal IReadOnlyCollection<int> CachedIndices => _cache.Keys;

    /// <summary>Set by <c>NativeSave.Read</c> when it rebased an inconsistent format-5 save.</summary>
    public string? LoadNotice { get; internal set; }
    /// <summary>Why the history check failed (diagnostics only).</summary>
    internal string? LoadDiagnostic { get; set; }

    /// <summary>True when <c>NativeSave.Read</c> rebased history (old format or inconsistent save). Checkpoint 0 is then the "load point".</summary>
    public bool RebasedOnLoad
    {
        get => _rebasedOnLoad;
        internal set
        {
            _rebasedOnLoad = value;
            if (_checkpoints.Count > 0 && _checkpoints[0].LogIndex == 0) _checkpoints[0] = _checkpoints[0] with { Label = StartLabel };
        }
    }
    bool _rebasedOnLoad;

    /// <summary>Restores genesis, actions and (already validated) checkpoints; null checkpoints keep just "game start".</summary>
    internal void RestoreHistory(GameState genesis, IEnumerable<ActionMark> actions, IEnumerable<Checkpoint>? checkpoints = null)
    {
        Genesis = genesis;
        _actions.Clear();
        _actions.AddRange(actions);
        _redo.Clear();
        ResetCache();
        ResetCheckpoints();
        if (checkpoints != null)
        {
            _checkpoints.Clear();
            _checkpoints.AddRange(checkpoints);
        }
    }
}
