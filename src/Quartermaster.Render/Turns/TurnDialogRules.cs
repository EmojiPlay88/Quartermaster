using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>Starting values of the mailbox choice (the game's <see cref="TurnIdentity"/>, the relay from Settings).</summary>
public sealed record MailboxValues(bool Use, string? Relay, string? Password)
{
    /// <summary>ToString never shows the password (a record would print it into logs and debugger views).</summary>
    private bool PrintMembers(System.Text.StringBuilder b)
    {
        b.Append("Use = ").Append(Use).Append(", Relay = ").Append(Relay).Append(", Password = ")
            .Append(string.IsNullOrEmpty(Password) ? "(none)" : "***");
        return true;
    }
}

/// <summary>Texts and validation of the Send Turn and "Who are you?" dialogs (shared by WinForms and Avalonia).</summary>
public static class TurnDialogRules
{
    public const string SendCaption = "Also send through my relay";
    public const string WatchCaption = "Watch the turn mailbox on my relay";
    public const string RelayLabel = "Relay:";
    public const string PasswordLabel = "Mailbox password (optional, the same for every player):";
    public const string WhoTitle = "Who are you?";
    public const string WhoPrompt = "Which player are you? This computer remembers it for this game.";
    public const string WhoMissingText = "Choose which player you are.";

    public static string FromPrompt(string name) => $"This turn is from {name}. Which player are you?";

    public static string SendPrompt(string fromName) => $"Send {fromName}'s turn. Next you choose where to save the turn file.";

    /// <summary>Why the mailbox choice cannot be used (a missing or bad relay address), else null. Off is never a problem.</summary>
    public static string? MailboxProblem(bool use, string? relay)
    {
        if (!use) return null;
        var r = (relay ?? "").Trim();
        if (r.Length == 0) return MailboxService.NoRelayText;
        return OnlineLobbyModel.ParseRelay(r, out var err) == null ? err ?? MailboxService.NoRelayText : null;
    }

    /// <summary>Why Send cannot proceed: no recipient first, then the mailbox choice.</summary>
    public static string? SendProblem(PlayerDef? recipient, bool use, string? relay) =>
        recipient == null ? MailboxService.NoRecipientText : MailboxProblem(use, relay);

    /// <summary>Why "Who are you?" cannot be confirmed: no player first, then the mailbox choice.</summary>
    public static string? WhoProblem(string? localPlayerId, bool use, string? relay) =>
        localPlayerId == null ? WhoMissingText : MailboxProblem(use, relay);

    /// <summary>"Name (sides)"; the id when the name is blank; no brackets when the player has no sides.</summary>
    public static string PlayerLabel(PlayerDef p, RulesPack? pack)
    {
        var sides = HandoverText.SidesText(p, pack);
        return (string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name) + (sides.Length > 0 ? $" ({sides})" : "");
    }
}
