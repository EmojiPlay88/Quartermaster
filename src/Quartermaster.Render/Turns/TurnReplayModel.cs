using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>
/// Step replay of a received turn (replay bar: Next, Play all, Stop). Opening replaces the session's game with the turn's start (as
/// opening a save does) and sets <see cref="ModuleSession.IsReplaying"/>; each step runs one of the sender's commands exactly as
/// <see cref="TurnFiles.Replay"/> does. When every step ran, history is rebased (the receiver cannot undo the sender's turn), the
/// receiver's turn base is marked, the start hash is remembered as played and the view narrows to the local player's sides (hidden
/// information is only hidden on screen). A step that cannot be replayed stops the replay and brings back the game the user had before.
/// UI-free.
/// </summary>
public sealed class TurnReplayModel
{
    /// <summary>Exact copy: the replayed end state differs from the sender's.</summary>
    public const string EndMismatchText = "The replayed turn does not match the sender's game.";

    /// <summary>Shown when the local player is not in the game's player list: only public information is shown.</summary>
    /// <summary>Set when the turn file was made with another app version (a gentle warning); null otherwise.</summary>
    public string? VersionNotice => VersionCheck.TurnFileVersionWarning(AppVersion.Text, _t.AppVersion);

    public const string NotAPlayerText = "You are not a player in this game; only public information is shown.";

    private readonly ModuleSession _s;
    private readonly TurnFile _t;
    private readonly string _localPlayerId;
    private readonly TurnIdentity _identity;
    private readonly GameSession? _before;
    private readonly IReadOnlySet<string>? _beforeSides;
    private readonly GameSession _game;
    private bool _stopRequested;

    /// <param name="identity">The game's identity (played start hashes); null = <see cref="TurnIdentity.Load(string)"/> of the turn's game id.</param>
    /// <exception cref="InvalidOperationException">Another module, a guest session or a replay already running; nothing changes (a viewer copy is replaced).</exception>
    /// <exception cref="InvalidDataException">The turn's game id is not a valid id; nothing changes.</exception>
    public TurnReplayModel(ModuleSession s, TurnFile t, string localPlayerId, TurnIdentity? identity = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(localPlayerId);
        if (!string.Equals(t.ModuleSha256, s.Archive.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(ModuleSession.DifferentModuleTurnText);
        _s = s;
        _t = t;
        _localPlayerId = localPlayerId;
        _identity = identity ?? TurnIdentity.Load(t.GameId);
        _before = s.Game;
        _beforeSides = s.ViewSides;
        _game = new GameSession(t.Start.DeepClone(), s.Archive.Sha256) { RulesSha256 = t.RulesSha256 };
        s.BeginTurnReplay(_game);
        if (Count == 0) Finish();
    }

    public TurnFile Turn => _t;

    /// <summary>Steps already run.</summary>
    public int Step { get; private set; }

    public int Count => _t.Commands.Count;

    /// <summary>The log line of the last step run (or its command's name when it logged nothing); null before the first step.</summary>
    public string? CurrentLine { get; private set; }

    /// <summary>True when every step ran, or the replay failed (<see cref="Error"/>) or was taken over by another game.</summary>
    public bool Done { get; private set; }

    /// <summary>Why the replay failed (the previous game is back); null otherwise.</summary>
    public string? Error { get; private set; }

    /// <summary>After a finished replay: true when the end state matches the sender's (else show <see cref="EndMismatchText"/>).</summary>
    public bool EndMatches { get; private set; }

    /// <summary>After a finished replay: null when all is well, else <see cref="NotAPlayerText"/>.</summary>
    public string? ViewNotice { get; private set; }

    /// <summary>True while the replay is paused by <see cref="Stop"/> (resumed by <see cref="Next"/> or <see cref="PlayAll"/>).</summary>
    public bool Stopped { get; private set; }

    /// <summary>Raised after each step, a stop, the end and a failure.</summary>
    public event Action? Changed;

    /// <summary>Runs one step (nothing when done).</summary>
    public void Next()
    {
        Stopped = false;
        RunOne();
        Changed?.Invoke();
    }

    /// <summary>
    /// Play all's timed step: runs steps until one writes a log line (or the turn ends), so commands that show nothing take no time
    /// of their own. One <see cref="Changed"/> at the end.
    /// </summary>
    public void NextShown()
    {
        Stopped = false;
        do RunOne();
        while (!Done && !LastStepLogged);
        Changed?.Invoke();
    }

    /// <summary>True when the last step run wrote a log line.</summary>
    public bool LastStepLogged { get; private set; }

    /// <summary>Runs the remaining steps; a <see cref="Stop"/> from a <see cref="Changed"/> handler pauses after the current step.</summary>
    public void PlayAll()
    {
        Stopped = false;
        _stopRequested = false;
        while (!Done && !_stopRequested)
        {
            RunOne();
            Changed?.Invoke();
        }
        _stopRequested = false;
    }

    /// <summary>Pauses the replay at the current step (the game stays consistent there).</summary>
    public void Stop()
    {
        if (Done) return;
        _stopRequested = true;
        Stopped = true;
        Changed?.Invoke();
    }

    private void RunOne()
    {
        if (Done) return;
        if (!ReferenceEquals(_s.Game, _game) || !_s.IsReplaying)
        {
            // Another game replaced the replayed one (opened a save, new game): this replay is over and must not touch it.
            Done = true;
            Error ??= "The replay was closed.";
            return;
        }
        int logBefore = _game.State.Log.Count;
        GameCommand c;
        try
        {
            TurnFiles.Step(_game, _t, Step);
            c = _t.Commands[Step];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever went wrong with a step, the user's previous game comes back (never a half-replayed one).
            Fail(ex is InvalidDataException ? ex.Message : $"Step {Step + 1} could not be replayed ({ex.Message}).");
            return;
        }
        Step++;
        LastStepLogged = _game.State.Log.Count > logBefore;
        CurrentLine = LastStepLogged ? _game.State.Log[^1].Text : c.GetType().Name;
        if (Step >= Count) Finish();
    }

    private void Fail(string message)
    {
        Done = true;
        Error = message;
        _s.RestoreAfterFailedReplay(_game, _before, _beforeSides);
    }

    private void Finish()
    {
        Done = true;
        EndMatches = string.Equals(StateHash.Of(_game.State), _t.EndHash, StringComparison.Ordinal);
        _game.Rebase();
        _game.MarkTurnBase();
        if (EndMatches)   // a mismatched turn may be fixed and resent from the same start: it is not "already played"
        {
            _identity.RecordPlayed(_t.StartHash);
            try { _identity.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _s.Log.Add(DiagnosticSeverity.Warning, "turns", $"Could not remember the replayed turn: {ex.Message}");
            }
        }
        var st = _game.State;
        IReadOnlySet<string>? sides;
        if (Players.IsSolo(st)) sides = null;
        else if (st.Players.FirstOrDefault(p => p != null && p.Id == _localPlayerId) is { } me) sides = new HashSet<string>(me.Sides);
        else
        {
            sides = new HashSet<string>();
            ViewNotice = NotAPlayerText;
        }
        _s.EndTurnReplay(_game, sides);
    }
}
