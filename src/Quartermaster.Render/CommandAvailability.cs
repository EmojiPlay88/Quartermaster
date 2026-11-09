namespace Quartermaster.Render;

/// <summary>
/// Which gated commands (menu items and toolbar buttons) are enabled for a session, and why not: the one copy of the gating rules.
/// The five gates of the WinForms app: needs a module, needs a game, needs a rules pack, blocked for an online guest
/// (<see cref="ModuleSession.GuestBusyText"/>), save commands (a guest, a viewer copy or a replay cannot save) and replay-blocked
/// (<see cref="ModuleSession.ReplayingText"/>). Toolbar buttons have their own ids (tool.*, dice.*, turn.next) with the same rule as their menu item.
/// </summary>
public static class CommandAvailability
{
    private readonly record struct Gate(bool Module = false, bool Game = false, bool Rules = false, bool Guest = false,
        bool Save = false, bool Replay = false, bool Local = false);

    private static readonly Dictionary<string, Gate> Table = new()
    {
        // File menu
        ["file.openModule"] = new(Guest: true),
        ["file.newGame"] = new(Module: true, Guest: true),
        ["file.loadSetup"] = new(Module: true, Guest: true),
        ["file.openGame"] = new(Module: true, Guest: true),
        ["file.openAutosave"] = new(Module: true, Guest: true),
        ["file.openTurn"] = new(Module: true, Guest: true, Local: true),
        ["file.saveGame"] = new(Game: true, Save: true),
        ["file.saveGameAs"] = new(Game: true, Save: true),
        ["file.newRules"] = new(Module: true, Guest: true, Replay: true),
        ["file.openRules"] = new(Module: true, Guest: true, Replay: true),
        ["file.saveRules"] = new(Rules: true, Guest: true),
        ["file.saveRulesAs"] = new(Rules: true, Guest: true),
        // View, Tools, Help menus
        ["view.diagnostics"] = new(Module: true),
        ["view.log"] = new(Game: true),
        ["view.outliner"] = new(Game: true),
        ["view.overview"] = new(Game: true),
        ["view.palette"] = new(Game: true),
        ["view.ledger"] = new(Rules: true),
        ["tools.workbench"] = new(Module: true, Replay: true),
        ["tools.inventory"] = new(Game: true),
        ["tools.notes"] = new(Game: true),
        ["tools.dice"] = new(Game: true),
        ["tools.turn"] = new(Game: true),
        ["help.tour"] = new(Game: true),
        // Toolbar buttons
        ["tool.openModule"] = new(Guest: true),
        ["tool.newGame"] = new(Module: true, Guest: true),
        ["tool.loadSetup"] = new(Module: true, Guest: true),
        ["tool.openGame"] = new(Module: true, Guest: true),
        ["tool.saveGame"] = new(Game: true, Save: true),
        ["tool.diagnostics"] = new(Module: true),
        ["tool.notes"] = new(Game: true),
        ["tool.overview"] = new(Game: true),
        ["tool.inventory"] = new(Game: true),
        ["tool.palette"] = new(Game: true),
        ["tool.los"] = new(Game: true),
        ["dice.1d6"] = new(Game: true),
        ["dice.2d6"] = new(Game: true),
        ["dice.menu"] = new(Game: true),
        ["turn.next"] = new(Game: true),
    };

    /// <summary>Every command id this class reports on.</summary>
    public static IReadOnlyList<string> Ids { get; } = Table.Keys.ToArray();

    /// <summary>The ids that are off (with <see cref="ModuleSession.ReplayingText"/>) while a received turn replays.</summary>
    public static IReadOnlyList<string> ReplayBlockedIds { get; } = Table.Where(kv => kv.Value.Replay).Select(kv => kv.Key).ToArray();

    /// <summary>
    /// Availability of every id. <paramref name="online"/> = an online game is running or starting here (File → Open Turn File is off then).
    /// </summary>
    public static IReadOnlyDictionary<string, (bool Enabled, string? Reason)> For(ModuleSession? s, bool online = false)
    {
        var d = new Dictionary<string, (bool, string?)>();
        var guest = s?.IsGuest == true;
        var replaying = s?.IsReplaying == true;
        var local = s != null && !online && !guest;
        foreach (var (id, g) in Table)
        {
            var enabled = (!g.Module || s != null) && (!g.Game || s?.Game != null) && (!g.Rules || s?.Rules != null);
            if (g.Guest && guest) enabled = false;
            if (g.Save && s?.Game != null && s.CanSave != true) enabled = false;
            if (g.Replay && replaying) enabled = false;
            if (g.Local && (!local || replaying)) enabled = false;
            // The replay text is shown last in WinForms, so it wins over the guest text.
            string? reason = g.Replay && replaying ? ModuleSession.ReplayingText
                : g.Guest ? (guest ? ModuleSession.GuestBusyText : null)
                : g.Save ? (guest ? ModuleSession.GuestSaveText : s?.IsViewerCopy == true ? ModuleSession.ViewerCopyText : null)
                : null;
            d[id] = (enabled, reason);
        }
        return d;
    }

    /// <summary>True for ids whose tooltip this class owns (guest, save and replay gates); other items keep their own tooltips.</summary>
    public static bool OwnsTooltip(string id) => Table.TryGetValue(id, out var g) && (g.Guest || g.Save || g.Replay);

    /// <summary>The tooltip texts this class sets; a front end clears only these when a reason goes away.</summary>
    public static bool IsOwnText(string? text) =>
        text is ModuleSession.GuestBusyText or ModuleSession.GuestSaveText or ModuleSession.ViewerCopyText or ModuleSession.ReplayingText;
}
