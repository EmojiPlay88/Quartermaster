using Quartermaster.Core;
using Quartermaster.Render;

namespace Quartermaster.Desktop;

/// <summary>
/// Hot-seat (port of WinForms MainForm.HotSeat): for now the player list edits from Settings > Game > Players.
/// </summary>
public partial class MainWindow
{
    /// <summary>Placeholder until stage 5c brings the online session (WinForms MainForm.IsOnline); the Desktop app is never online yet.</summary>
    bool IsOnline => false;

    // ---------- player list (Settings > Game > Players) ----------

    (Quartermaster.Rules.RulesPack? Pack, IReadOnlyList<PlayerDef> Players)? GamePlayers() =>
        _session?.Game is { } g ? (_session.Rules, g.State.Players.ToList()) : null;

    /// <summary>Executes SetPlayers when the list changed and is valid (the editor validated it already; this re-checks).</summary>
    void SetGamePlayers(IReadOnlyList<PlayerDef> players)
    {
        if (_session?.Game is not { } g) return;
        if (PlayersEditModel.Same(g.State.Players, players)) return;
        if (_session.IsReplaying) { ShowError(ModuleSession.ReplayingText); return; }
        if (IsOnline || _session.IsGuest)
        {
            // Spec 8a.10: during a live session only the host's Players dialog changes who holds what.
            ShowError(_session.IsGuest ? IntentExecutor.HostOnlyText : "While the game is online, change seats with Game → Players….");
            return;
        }
        if (players.Count >= 2)
        {
            var problem = _session.Rules is { } pack ? Players.Validate(players, pack) : "Open a rules pack to set up players.";
            if (problem != null) { ShowError(problem); return; }
        }
        g.Execute(CommandLog.Prepare(new SetPlayers(players.ToList()), g.State, _session));
    }
}
