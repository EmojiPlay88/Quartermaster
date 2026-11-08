using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>Builds the local player's turn file (Game → Send Turn…). The caller writes or mails it, then calls <see cref="AfterSend"/>.</summary>
public static class TurnSender
{
    /// <summary>
    /// The local player's turn: the game at <see cref="GameSession.TurnBase"/> plus every command since. A turn with no commands is a
    /// pass: "{name} passed." is logged first (its own action), so a pass always sends.
    /// </summary>
    /// <param name="identity">The game's identity (its <see cref="TurnIdentity.LocalPlayerId"/> is the sender); null = loaded for the game.</param>
    public static TurnFile Build(ModuleSession s, string toPlayerId, TurnIdentity? identity = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.IsGuest) throw new InvalidOperationException(ModuleSession.GuestBusyText);
        if (s.IsViewerCopy) throw new InvalidOperationException(ModuleSession.ViewerCopyText);
        if (s.IsReplaying) throw new InvalidOperationException(ModuleSession.ReplayingText);
        var g = s.Game ?? throw new InvalidOperationException("There is no game to send.");
        var id = identity ?? TurnIdentity.Load(g.State.GameId ?? "");
        var from = id.LocalPlayerId is { Length: > 0 } f ? f : throw new InvalidOperationException("Choose which player you are first.");
        var name = g.State.Players.FirstOrDefault(p => p != null && p.Id == from)?.Name ?? from;
        if (g.Log.Count == g.TurnBase)
        {
            using (g.BeginAction($"{name} passed"))
                g.Execute(new AddLogLine(LogKind.System, $"{name} passed.", At: DateTimeOffset.UtcNow, Who: from));
        }
        if (s.Rules is { } pack)
        {
            try { g.RulesSha256 = RulesPackIo.ComputeSha256(pack); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        return TurnFiles.Create(g, from, name, toPlayerId, AppVersion.Text);
    }

    /// <summary>The turn was sent: the local player's next turn starts here.</summary>
    public static void AfterSend(ModuleSession s)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.Game?.MarkTurnBase();
    }
}
