using Quartermaster.Core;

namespace Quartermaster.Render;

/// <summary>What opening a received turn would do.</summary>
public enum TurnCheck
{
    /// <summary>The open game is exactly where the turn starts.</summary>
    Ok,
    /// <summary>The turn was made with another module ("This turn file belongs to a different module").</summary>
    DifferentModule,
    /// <summary>The open game of this id has moved on from the turn's start ("This turn starts from an older position than your game").</summary>
    OlderPosition,
    /// <summary>This turn (its start) was already replayed here.</summary>
    AlreadyPlayed,
    /// <summary>No game with the turn's id is open: opening it replaces the open game.</summary>
    NewGame,
}

/// <summary>Decides what opening a received turn would do. Never changes the session.</summary>
public static class TurnOpener
{
    /// <summary>Exact copy of the warning shown for <see cref="TurnCheck.OlderPosition"/> (buttons "Open anyway" / "Cancel").</summary>
    public const string OlderPositionText = "This turn starts from an older position than your game";

    /// <summary>Why a turn whose start does not hash to its <see cref="TurnFile.StartHash"/> is refused.</summary>
    public const string StartMismatchText = "Malformed turn file: start does not match its hash.";

    /// <summary>
    /// In order: another module; the open game of this id is exactly at the turn's start (Ok, even when the turn was played before:
    /// a replayed turn whose result was never saved can be played again); already played; no game of this id open (a new game); a turn
    /// this computer sent (older position); a turn for the local player from someone else while the open game has no local moves since
    /// its turn base (Ok: nothing local is lost, e.g. the third player's turn in a chain); else an older position.
    /// </summary>
    /// <param name="identity">The game's identity; null = <see cref="TurnIdentity.Load(string)"/> of the turn's game id.</param>
    /// <exception cref="InvalidDataException">The turn's game id is not a valid id, or its start does not match its start hash.</exception>
    public static TurnCheck Check(ModuleSession s, TurnFile t, TurnIdentity? identity = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(t);
        if (!string.Equals(t.ModuleSha256, s.Archive.Sha256, StringComparison.OrdinalIgnoreCase)) return TurnCheck.DifferentModule;
        var id = identity ?? TurnIdentity.Load(t.GameId);
        if (t.Start == null || !string.Equals(StateHash.Of(t.Start), t.StartHash, StringComparison.Ordinal))
            throw new InvalidDataException(StartMismatchText);
        var same = s.Game is { } g && !s.IsGuest && !s.IsViewerCopy && string.Equals(g.State.GameId, t.GameId, StringComparison.OrdinalIgnoreCase);
        if (same && string.Equals(StateHash.Of(s.Game!.State), t.StartHash, StringComparison.Ordinal)) return TurnCheck.Ok;
        if (id.PlayedStartHashes.Contains(t.StartHash)) return TurnCheck.AlreadyPlayed;
        if (!same) return TurnCheck.NewGame;
        if (id.SentStartHashes.Contains(t.StartHash)) return TurnCheck.OlderPosition;   // a turn this computer sent, coming back
        var open = s.Game!;
        var me = id.LocalPlayerId;
        var forMe = !string.IsNullOrEmpty(me) && string.Equals(t.ToPlayerId, me, StringComparison.Ordinal)
            && !string.Equals(t.FromPlayerId, me, StringComparison.Ordinal);
        return forMe && open.Log.Count == open.TurnBase && !open.RebasedOnLoad ? TurnCheck.Ok : TurnCheck.OlderPosition;
    }
}
