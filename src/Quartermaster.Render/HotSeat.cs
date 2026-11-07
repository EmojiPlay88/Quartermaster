using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>
/// Hot-seat play: tracks which player is at the screen, tells the UI when the next player must take over (after a phase boundary,
/// a rewind or a game load) and narrows the session's view to that player's sides. Honour-based: the full state stays in memory.
/// UI-free; never throws on missing game or rules.
/// </summary>
public sealed class HotSeat
{
    private readonly ModuleSession _s;

    public HotSeat(ModuleSession s, PhaseController phases)
    {
        _s = s;
        phases.PhaseBoundary += CheckHandover;
        s.GameReplaced += OnGameReplaced;
    }

    public PlayerDef? Current { get; private set; }

    /// <summary>True when the open game has two or more players.</summary>
    public bool Active => _s.Game is { } g && !Players.IsSolo(g.State);

    public event Action<PlayerDef>? HandoverRequired;
    public event Action? CurrentChanged;

    /// <summary>Also raise <see cref="HandoverRequired"/> after an undo, redo or step back.</summary>
    public void Watch(HistoryController history) => history.Rewound += CheckHandover;

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
        if (HandoverDue() is { } due) HandoverRequired?.Invoke(due);
    }

    private void OnGameReplaced()
    {
        // A new game: nobody is at the screen yet, and no side id from the old game may linger in the view.
        Current = null;
        if (Active && HandoverDue() is { } due)
        {
            _s.SetViewSides(new HashSet<string>(due.Sides));   // filtered until the handover is accepted
            CurrentChanged?.Invoke();
            HandoverRequired?.Invoke(due);
        }
        else CurrentChanged?.Invoke();   // solo: the session already cleared ViewSides
    }
}
