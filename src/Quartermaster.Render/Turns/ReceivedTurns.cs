using Quartermaster.Core;
using Quartermaster.Net;

namespace Quartermaster.Render;

/// <summary>
/// Local copies of turns that came through the mailbox (<c>&lt;root&gt;/{GameId}/received/{itemId}.qturn</c>): the relay item is
/// deleted once the replayed game is autosaved, so a copy stays here until the local player's next turn is sent (then pruned). Never
/// throws: a copy that cannot be written or removed is only logged by the caller.
/// </summary>
public static class ReceivedTurns
{
    /// <summary>The folder of a game's received turns; null when the id is not safe as a file name.</summary>
    public static string? Folder(string? gameId, string? root = null) =>
        TurnIdentity.IsValidGameId(gameId) ? Path.Combine(root ?? TurnIdentity.DefaultRoot, gameId!.ToLowerInvariant(), "received") : null;

    /// <summary>Writes the copy of a received turn. Returns its path, or null when it could not be written (the reason in <paramref name="error"/>).</summary>
    public static string? Keep(TurnFile t, string itemId, out string? error, string? root = null)
    {
        error = null;
        ArgumentNullException.ThrowIfNull(t);
        if (Folder(t.GameId, root) is not { } dir || !MailboxLimits.IsItemId(itemId)) { error = "invalid game or item id"; return null; }
        var path = Path.Combine(dir, itemId + ".qturn");
        try
        {
            Directory.CreateDirectory(dir);
            if (!File.Exists(path)) TurnFiles.Write(path, t);
            return path;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>The copies kept for a game (empty when none).</summary>
    public static IReadOnlyList<string> List(string? gameId, string? root = null)
    {
        try
        {
            return Folder(gameId, root) is { } dir && Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.qturn").Order(StringComparer.Ordinal).ToList()
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>The local player sent a turn: the received copies of the game are no longer needed. Returns how many were removed.</summary>
    public static int Prune(string? gameId, string? root = null)
    {
        var n = 0;
        foreach (var f in List(gameId, root))
        {
            try { File.Delete(f); n++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return n;
    }
}
