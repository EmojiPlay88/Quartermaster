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

    /// <param name="identity">The game's identity; null = <see cref="TurnIdentity.Load(string)"/> of the turn's game id.</param>
    /// <exception cref="InvalidDataException">The turn's game id is not a valid id.</exception>
    public static TurnCheck Check(ModuleSession s, TurnFile t, TurnIdentity? identity = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(t);
        if (!string.Equals(t.ModuleSha256, s.Archive.Sha256, StringComparison.OrdinalIgnoreCase)) return TurnCheck.DifferentModule;
        var id = identity ?? TurnIdentity.Load(t.GameId);
        if (id.PlayedStartHashes.Contains(t.StartHash)) return TurnCheck.AlreadyPlayed;
        if (s.Game is not { } g || s.IsGuest || s.IsViewerCopy || !string.Equals(g.State.GameId, t.GameId, StringComparison.OrdinalIgnoreCase))
            return TurnCheck.NewGame;
        return string.Equals(StateHash.Of(g.State), t.StartHash, StringComparison.Ordinal) ? TurnCheck.Ok : TurnCheck.OlderPosition;
    }
}
