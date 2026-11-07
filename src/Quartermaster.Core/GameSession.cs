namespace Quartermaster.Core;

public sealed class GameSession
{
    const int MaxCached = 40;

    readonly List<GameCommand> _log = new();
    readonly List<ActionMark> _actions = new();
    readonly Stack<UndoneAction> _redo = new();
    readonly Dictionary<int, GameState> _cache = new();
    readonly List<int> _cacheOrder = new();
    int _scopeDepth;
    string? _scopeLabel;
    bool _scopeActionOpen;
    bool _redoing;

    public GameSession(GameState initial, string moduleSha256)
    {
        State = initial;
        Genesis = initial.DeepClone();
        _cache[0] = Genesis;
        ModuleSha256 = moduleSha256;
    }

    /// <summary>The live state. The object is replaced on rewind, so never hold it across calls.</summary>
    public GameState State { get; private set; }
    /// <summary>State at log index 0 (deep clone, never mutated).</summary>
    public GameState Genesis { get; private set; }
    public string ModuleSha256 { get; }
    /// <summary>Hash of the rules pack the ledgers were played with; null when none.</summary>
    public string? RulesSha256 { get; set; }
    public IReadOnlyList<GameCommand> Log => _log;
    public IReadOnlyList<ActionMark> Actions => _actions;
    public event Action<GameCommand>? Changed;
    public event Action? Rewound;

    public bool CanUndo => _actions.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _actions.Count > 0 ? _actions[^1].Label : null;
    public string? RedoLabel => _redo.Count > 0 ? _redo.Peek().Label : null;

    /// <summary>Commands that never form an action of their own (view-only or bookkeeping).</summary>
    public static bool IsFolding(GameCommand c) => c switch
    {
        SetStackExpanded => true,
        SeedLedger => true,
        _ => false,
    };

    /// <summary>Groups the commands executed inside the scope into one action. Nested scopes join the outermost.</summary>
    public IDisposable BeginAction(string? label = null)
    {
        if (_scopeDepth++ == 0) { _scopeLabel = label; _scopeActionOpen = false; }
        return new Scope(this);
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
        if (_scopeActionOpen && _scopeLabel == null && _actions.Count > 0)
        {
            var m = _actions[^1];
            var n = _log.Count - m.Start;
            if (n > 1) _actions[^1] = m with { Label = ActionLabels.For(_log[m.Start], n) };
        }
        _scopeLabel = null;
        _scopeActionOpen = false;
    }

    /// <summary>The single entry point for changing game state.</summary>
    public void Execute(GameCommand c)
    {
        if (_scopeDepth > 0)
        {
            if (!_scopeActionOpen) { NewAction(_scopeLabel ?? ActionLabels.For(c, 1)); _scopeActionOpen = true; }
        }
        else if (!IsFolding(c))
            NewAction(ActionLabels.For(c, 1));
        // folding outside a scope: joins the last action, or is an orphan at the head

        State.Apply(c);
        _log.Add(c);
        Changed?.Invoke(c);
    }

    void NewAction(string label)
    {
        _actions.Add(new ActionMark(_log.Count, label));
        if (!_redoing) _redo.Clear();
    }

    /// <summary>Truncates the log to <paramref name="logIndex"/> and rebuilds state; removed actions become redoable.</summary>
    public IReadOnlyList<UndoneAction> RewindTo(int logIndex)
    {
        if (logIndex < 0 || logIndex > _log.Count) throw new ArgumentOutOfRangeException(nameof(logIndex));

        var removedMarks = _actions.Where(a => a.Start >= logIndex).ToList();
        var undone = new List<UndoneAction>();
        for (int k = 0; k < removedMarks.Count; k++)
        {
            // The first removed action also takes any folded/orphan commands before it down to logIndex.
            int from = k == 0 ? logIndex : removedMarks[k].Start;
            int to = k + 1 < removedMarks.Count ? removedMarks[k + 1].Start : _log.Count;
            undone.Add(new UndoneAction(removedMarks[k].Label, _log.GetRange(from, to - from)));
        }

        // Rebuild state from the nearest snapshot at or before the target.
        int baseIdx = _cache.Keys.Where(k => k <= logIndex).Max();
        var state = _cache[baseIdx].DeepClone();
        for (int i = baseIdx; i < logIndex; i++) state.Apply(_log[i]);

        _log.RemoveRange(logIndex, _log.Count - logIndex);
        _actions.RemoveAll(a => a.Start >= logIndex);
        foreach (var k in _cacheOrder.Where(k => k > logIndex).ToList()) { _cache.Remove(k); _cacheOrder.Remove(k); }
        for (int i = undone.Count - 1; i >= 0; i--) _redo.Push(undone[i]);

        State = state;
        Rewound?.Invoke();
        return undone;
    }

    public void Undo()
    {
        if (_actions.Count == 0) return;
        RewindTo(_actions[^1].Start);
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

    /// <summary>Makes the current state the new genesis; history is discarded.</summary>
    public void Rebase()
    {
        Genesis = State.DeepClone();
        _log.Clear();
        _actions.Clear();
        _redo.Clear();
        ResetCache();
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

    internal void RestoreLog(IEnumerable<GameCommand> log) => _log.AddRange(log);

    internal void RestoreHistory(GameState genesis, IEnumerable<ActionMark> actions)
    {
        Genesis = genesis;
        _actions.Clear();
        _actions.AddRange(actions);
        _redo.Clear();
        ResetCache();
    }
}
