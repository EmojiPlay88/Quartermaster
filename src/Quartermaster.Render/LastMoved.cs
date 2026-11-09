using Quartermaster.Core;

namespace Quartermaster.Render;

/// <summary>The map tool "Last moved": which counters a map outlines.</summary>
public static class LastMoved
{
    /// <summary>
    /// Piece ids of the most recent move on <paramref name="mapId"/> in the game's command list (undone moves are gone from it);
    /// empty without a game or a move on that map. Takes the session, not the state, because the command list lives there.
    /// </summary>
    public static IReadOnlySet<string> For(GameSession? game, string mapId) =>
        game == null ? new HashSet<string>() : MapViewTools.LastMovedIds(game.Log, mapId);
}
