using Quartermaster.Core;

namespace Quartermaster.Render;

/// <summary>
/// The logic behind the turn-file windows (Send Turn dialog, "who are you" prompt, replay bar, mailbox alert): exact copy, defaults and
/// the view a play-by-email player gets. UI-free; never throws on bad input.
/// </summary>
public static class TurnUiModel
{
    /// <summary>Exact copy: the Send Turn dialog's note.</summary>
    public const string HiddenInfoNote = "Turn files carry the whole game; hidden information is only hidden on screen.";

    /// <summary>Shown (buttons "Open anyway" / "Cancel") for a turn whose start was already replayed here.</summary>
    public const string AlreadyPlayedText = "This turn has already been played.";

    public const string OpenAnywayText = "Open anyway";

    /// <summary>Send Turn needs somebody to send to.</summary>
    public const string NeedPlayersText = "Turn files need two or more players. Set them up first (Tools → Settings → Game → Players).";

    /// <summary>Send Turn and Open Turn File are for local games only.</summary>
    public const string OnlineText = "Leave the online game first.";

    public const string FileFilter = "Quartermaster turns (*.qturn)|*.qturn|All files (*.*)|*.*";

    /// <summary>Exact copy of the mailbox alert.</summary>
    public static string NewTurnWaitingText(string? player) =>
        $"New turn waiting from {(string.IsNullOrWhiteSpace(player) ? "another player" : player.Trim())}";

    /// <summary>Who the turn may go to: every player of the game except the local one (in the game's order).</summary>
    public static IReadOnlyList<PlayerDef> Recipients(IReadOnlyList<PlayerDef>? players, string? localPlayerId) =>
        (players ?? Array.Empty<PlayerDef>()).Where(p => p != null && !string.IsNullOrEmpty(p.Id) && p.Id != localPlayerId).ToList();

    /// <summary>
    /// The default recipient: the next player after the local one in the game's player order (wrapping round); the first other player
    /// when the local player is not in the list; null when there is nobody else.
    /// </summary>
    public static PlayerDef? DefaultRecipient(IReadOnlyList<PlayerDef>? players, string? localPlayerId)
    {
        var list = (players ?? Array.Empty<PlayerDef>()).Where(p => p != null && !string.IsNullOrEmpty(p.Id)).ToList();
        var me = list.FindIndex(p => p.Id == localPlayerId);
        if (me < 0) return list.FirstOrDefault(p => p.Id != localPlayerId);
        for (var i = 1; i < list.Count; i++)
        {
            var p = list[(me + i) % list.Count];
            if (p.Id != localPlayerId) return p;
        }
        return null;
    }

    /// <summary>
    /// The Save As name: <c>{module} {GameId[..8]} turn {n}.qturn</c> (n = the game's turn number), with characters a file name cannot
    /// hold replaced.
    /// </summary>
    public static string DefaultFileName(string? moduleName, string? gameId, int turn)
    {
        var module = string.IsNullOrWhiteSpace(moduleName) ? "Game" : moduleName.Trim();
        var id = string.IsNullOrEmpty(gameId) ? "game" : gameId.Length > 8 ? gameId[..8] : gameId;
        var name = $"{module} {id} turn {Math.Max(0, turn)}";
        var bad = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => bad.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (name.Length > 120) name = name[..120].TrimEnd();
        return name + ".qturn";
    }

    /// <summary>The player preselected in the "who are you" prompt: the one already known here, else the turn's recipient, else the first.</summary>
    public static string? DefaultLocalPlayer(IReadOnlyList<PlayerDef>? players, string? known, string? toPlayerId)
    {
        var list = (players ?? Array.Empty<PlayerDef>()).Where(p => p != null && !string.IsNullOrEmpty(p.Id)).ToList();
        if (known != null && list.Any(p => p.Id == known)) return known;
        if (toPlayerId != null && list.Any(p => p.Id == toPlayerId)) return toPlayerId;
        return list.FirstOrDefault()?.Id;
    }

    /// <summary>True when the local player is known and is one of the game's players (else the "who are you" prompt asks).</summary>
    public static bool KnowsLocalPlayer(IReadOnlyList<PlayerDef>? players, string? localPlayerId) =>
        !string.IsNullOrEmpty(localPlayerId) && (players ?? Array.Empty<PlayerDef>()).Any(p => p != null && p.Id == localPlayerId);

    /// <summary>
    /// A play-by-email game (not a hot-seat one): this computer knows which of the game's two or more players it is. Such a game never
    /// gets the hot-seat handover; its view is the local player's sides.
    /// </summary>
    public static bool IsTurnGame(GameState? state, TurnIdentity? identity) =>
        state != null && !Players.IsSolo(state) && KnowsLocalPlayer(state.Players, identity?.LocalPlayerId);

    /// <summary>The sides a play-by-email player sees (null = everything, in solo; empty = public only, when not a player).</summary>
    public static IReadOnlySet<string>? ViewSides(GameState state, string? localPlayerId)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (Players.IsSolo(state)) return null;
        var me = state.Players.FirstOrDefault(p => p != null && p.Id == localPlayerId);
        return me == null ? new HashSet<string>() : new HashSet<string>(me.Sides ?? Array.Empty<string>());
    }

    /// <summary>The replay bar's step counter: "Step n / N".</summary>
    public static string StepText(int step, int count) => $"Step {Math.Max(0, step)} / {Math.Max(0, count)}";
}
