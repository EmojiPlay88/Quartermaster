namespace Quartermaster.Render;

/// <summary>
/// Which module/rules commands are enabled for a session, and why not. Mirrors the WinForms gating: needs-module (no module open),
/// needs-rules (no rules pack) and guest-blocked (an online guest: <see cref="ModuleSession.GuestBusyText"/>).
/// The toolbar buttons use the same ids as their menu items.
/// </summary>
public static class CommandAvailability
{
    private static readonly string[] GuestBlocked =
    {
        "file.openModule", "file.newGame", "file.loadSetup", "file.openGame", "file.openAutosave", "file.openTurn",
        "file.newRules", "file.openRules", "file.saveRules", "file.saveRulesAs",
    };
    private static readonly string[] NeedsModule =
    {
        "file.newGame", "file.loadSetup", "file.openGame", "file.openAutosave", "file.openTurn", "file.newRules", "file.openRules",
        "file.saveRules", "file.saveRulesAs", "view.ledger",
    };
    private static readonly string[] NeedsRules = { "file.saveRules", "file.saveRulesAs", "view.ledger" };

    /// <summary>Every command id this class reports on.</summary>
    public static IReadOnlyList<string> Ids { get; } =
        GuestBlocked.Concat(NeedsModule).Concat(NeedsRules).Distinct().ToArray();

    public static IReadOnlyDictionary<string, (bool Enabled, string? Reason)> For(ModuleSession? s)
    {
        var d = new Dictionary<string, (bool, string?)>();
        foreach (var id in Ids)
        {
            var enabled = true;
            if (NeedsModule.Contains(id) && s == null) enabled = false;
            if (NeedsRules.Contains(id) && s?.Rules == null) enabled = false;
            string? reason = null;
            if (s?.IsGuest == true && GuestBlocked.Contains(id)) { enabled = false; reason = ModuleSession.GuestBusyText; }
            d[id] = (enabled, reason);
        }
        return d;
    }
}
