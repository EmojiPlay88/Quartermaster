using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>
/// Hot-seat play: tracks which player is at the screen, tells the UI when the next player must take over (after a phase boundary,
/// a rewind, a game load or a player list that turns a solo game into a multi-player one) and narrows the session's view to that
/// player's sides. Honour-based: the full state stays in memory. UI-free; never throws on missing game or rules.
/// </summary>
public sealed class HotSeat
{
    private readonly ModuleSession _s;
    private GameSession? _hooked;
    private bool _wasSolo = true;

    private readonly PhaseController _phases;
    private HistoryController? _history;
    private bool _detached;

    public HotSeat(ModuleSession s, PhaseController phases)
    {
        _s = s;
        _phases = phases;
        phases.PhaseBoundary += CheckHandover;
        s.GameReplaced += OnGameReplaced;
        s.RulesApplied += OnRulesApplied;
        Hook();
        _wasSolo = !Active;
    }

    public PlayerDef? Current { get; private set; }

    /// <summary>The player a raised handover is waiting for (the handover screen is up), else null.</summary>
    public PlayerDef? Pending { get; private set; }

    /// <summary>True when the open game has two or more players.</summary>
    public bool Active => _s.Game is { } g && !Players.IsSolo(g.State);

    /// <summary>A player must take over: the UI hides everything and shows the handover screen. The view is public-only until then.</summary>
    public event Action<PlayerDef>? HandoverRequired;

    /// <summary>The pending handover is no longer due (rewound back into the current player's phase, or the game became solo):
    /// the UI dismisses the handover screen. The current player's view has been restored (or cleared in solo).</summary>
    public event Action? HandoverCancelled;

    public event Action? CurrentChanged;

    /// <summary>Also raise <see cref="HandoverRequired"/> after an undo, redo or step back.</summary>
    public void Watch(HistoryController history)
    {
        if (_detached) return;
        _history = history;
        history.Rewound += OnRewound;
    }

    /// <summary>
    /// Stops following the game for good (an online session owns the view instead, spec 8a): every subscription is dropped, so nothing
    /// here changes <see cref="ModuleSession.ViewSides"/> or raises a handover again. The view is left as it is.
    /// </summary>
    public void Detach()
    {
        if (_detached) return;
        _detached = true;
        _phases.PhaseBoundary -= CheckHandover;
        _s.GameReplaced -= OnGameReplaced;
        _s.RulesApplied -= OnRulesApplied;
        if (_history != null) _history.Rewound -= OnRewound;
        if (_hooked != null) { _hooked.Changed -= OnCommand; _hooked.Rewound -= OnGameRewound; }
        _hooked = null;
        Pending = null;
    }

    /// <summary>The player who must take over now, else null (solo; same player still on; no phase side to hand to).</summary>
    public PlayerDef? HandoverDue()
    {
        if (!Active || _s.Game is not { } g) return null;
        var target = ActingPlayer(g.State);
        if (target == null && Current == null) target = Players.Effective(g.State, _s.Rules).FirstOrDefault();
        return target != null && target.Id != Current?.Id ? target : null;
    }

    public void TakeOver(PlayerDef p)
    {
        Current = p;
        Pending = null;
        var solo = _s.Game == null || Players.IsSolo(_s.Game.State);
        _s.SetViewSides(solo ? null : new HashSet<string>(p.Sides));
        CurrentChanged?.Invoke();
    }

    private PlayerDef? ActingPlayer(GameState st)
    {
        if (_s.Rules is not { } pack || st.Position == null) return null;
        var (pos, _) = Sequencer.Resolve(pack, st.Position);
        if (pos == null || pos.PhaseIndex < 0 || pos.PhaseIndex >= pack.Phases.Count) return null;
        var side = Sequencer.ActingSide(pack, pack.Phases[pos.PhaseIndex]);
        return side == null ? null : Players.Holding(st, pack, side.Id);
    }

    private void CheckHandover()
    {
        if (HandoverDue() is not { } due)
        {
            // Rewound back into the current player's own phase: the screen goes away and their view comes back. (Rewound to where no
            // side acts, the pending handover stays: nothing says the player at the screen should see again.)
            if (Pending == null || Current == null || _s.Game is not { } g || ActingPlayer(g.State)?.Id != Current.Id) return;
            Pending = null;
            _s.SetViewSides(new HashSet<string>(Current.Sides));
            RaiseCancelled();
            return;
        }
        _s.SetViewSides(new HashSet<string>());   // the leaving player's data goes behind the screen too
        if (due.Id == Pending?.Id) return;
        Pending = due;
        HandoverRequired?.Invoke(due);
    }

    private void OnRewound() => Reconcile();

    /// <summary>After a player-list change or a rewind: re-checks the handover, and dismisses a pending one that is no longer due.</summary>
    private void Reconcile()
    {
        // An undo may cross a player-list change; it is handled exactly like the command itself.
        var had = Pending != null;
        var cancels = _cancels;
        if (!PlayersChanged()) CheckHandover();
        if (!had || Pending != null || _cancels != cancels || HandoverDue() != null) return;
        // An edit cleared the pending handover (say, the side went back to the player at the screen): the screen goes away.
        if (Active && Current != null) _s.SetViewSides(new HashSet<string>(Current.Sides));
        RaiseCancelled();
    }

    private void Hook()
    {
        if (ReferenceEquals(_hooked, _s.Game)) return;
        if (_hooked != null) { _hooked.Changed -= OnCommand; _hooked.Rewound -= OnGameRewound; }
        _hooked = _s.Game;
        if (_hooked != null) { _hooked.Changed += OnCommand; _hooked.Rewound += OnGameRewound; }
    }

    private void OnCommand(GameCommand c)
    {
        if (c is SetPlayers) Reconcile();
        // A phase relocation after a rules change or a load (not one inside "Next Phase" or a rewind, which re-check when they end).
        else if (c is AdvancePhase { Reason: not null } && _s.Game is { InAction: false }) Reconcile();
    }

    /// <summary>An edited pack may change who acts now (a phase's side, the sides, the players' effective sides): re-check.</summary>
    private void OnRulesApplied()
    {
        Hook();
        Reconcile();
    }

    private void OnGameRewound() => PlayersChanged();

    /// <summary>Reconciles after the player list may have changed. True when it handled a solo/multi switch.</summary>
    private bool PlayersChanged()
    {
        if (_s.Game is not { } g) return false;
        var solo = !Active;
        if (solo)
        {
            if (_wasSolo) return false;
            _wasSolo = true;
            var hadPending = Pending != null;
            Current = null;
            Pending = null;
            _s.SetViewSides(null);
            if (hadPending) RaiseCancelled();
            CurrentChanged?.Invoke();
            return true;
        }
        if (_wasSolo)
        {
            // Solo turned into 2+ players mid-game: whoever was at the screen saw everything; nobody holds the screen yet.
            _wasSolo = false;
            Current = null;
            Pending = null;
            _s.SetViewSides(new HashSet<string>());
            CurrentChanged?.Invoke();
            CheckHandover();
            return true;
        }
        // Still multi-player: follow edits to the current or pending player (renamed, sides changed, removed).
        var list = Players.Effective(g.State, _s.Rules);
        if (Pending != null)
        {
            var fresh = list.FirstOrDefault(p => p.Id == Pending.Id);
            if (fresh == null || !Same(fresh, Pending)) Pending = null;   // CheckHandover re-raises with the new definition when still due
        }
        if (Current != null)
        {
            var fresh = list.FirstOrDefault(p => p.Id == Current.Id);
            if (fresh == null)
            {
                Current = null;
                _s.SetViewSides(new HashSet<string>());
                CurrentChanged?.Invoke();
            }
            else if (!Same(fresh, Current))
            {
                Current = fresh;
                if (Pending == null) _s.SetViewSides(new HashSet<string>(fresh.Sides));
                CurrentChanged?.Invoke();
            }
        }
        return false;
    }

    private int _cancels;

    private void RaiseCancelled()
    {
        _cancels++;
        HandoverCancelled?.Invoke();
    }

    private static bool Same(PlayerDef a, PlayerDef b) => a.Name == b.Name && a.Sides.SequenceEqual(b.Sides);

    private void OnGameReplaced()
    {
        // A new game: nobody is at the screen yet, and no side id from the old game may linger in the view.
        Hook();
        var hadPending = Pending != null;
        Current = null;
        Pending = null;
        _wasSolo = !Active;
        if (Active && HandoverDue() is { } due)
        {
            Pending = due;   // the session already set ViewSides to the empty set
            CurrentChanged?.Invoke();
            HandoverRequired?.Invoke(due);
        }
        else
        {
            if (hadPending) RaiseCancelled();
            CurrentChanged?.Invoke();
        }
    }
}
