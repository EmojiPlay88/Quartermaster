using Avalonia;
using Avalonia.Controls;
using Quartermaster.Core;
using Quartermaster.Desktop.Online;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Desktop.Turns;

/// <summary>The Send Turn dialog's answer: who the turn is for and whether it also goes through the relay mailbox (Relay trimmed).</summary>
public sealed record SendTurnChoice(PlayerDef Recipient, bool UseMailbox, string Relay, string Password)
{
    /// <summary>ToString never shows the password.</summary>
    private bool PrintMembers(System.Text.StringBuilder b)
    {
        b.Append("Recipient = ").Append(Recipient.Id).Append(", UseMailbox = ").Append(UseMailbox).Append(", Relay = ").Append(Relay)
            .Append(", Password = ").Append(string.IsNullOrEmpty(Password) ? "(none)" : "***");
        return true;
    }
}

/// <summary>Game, Send Turn (WinForms SendTurnDialog): who the turn is for (default: the next player after the local one), whether it also goes
/// through the relay mailbox, and the plain note that turn files hold the whole game. Closes with a <see cref="SendTurnChoice"/>; a missing
/// recipient or a bad relay is said in a message box and the dialog stays open.</summary>
sealed class SendTurnDialog : Window
{
    readonly ComboBox _to = new() { Width = 260 };
    readonly List<PlayerDef> _recipients;
    readonly MailboxOptions _mailbox;

    public SendTurnDialog(string fromName, IReadOnlyList<PlayerDef> players, string localPlayerId, RulesPack? pack, MailboxValues mailbox)
    {
        Title = "Send Turn";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MaxWidth = 440;
        OnlineUi.Face(this);

        _recipients = TurnUiModel.Recipients(players, localPlayerId).ToList();
        Recipients = _recipients.Select(p => TurnDialogRules.PlayerLabel(p, pack)).ToList();
        _to.ItemsSource = Recipients;
        var def = TurnUiModel.DefaultRecipient(players, localPlayerId);
        _to.SelectedIndex = def == null ? (_recipients.Count > 0 ? 0 : -1) : _recipients.FindIndex(p => p.Id == def.Id);

        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 2 };
        panel.Children.Add(Wrapped(TurnDialogRules.SendPrompt(fromName), new Thickness(3, 0, 3, 8)));
        panel.Children.Add(OnlineUi.Label("Send to:"));
        panel.Children.Add(_to);
        _mailbox = new MailboxOptions(TurnDialogRules.SendCaption, mailbox) { Margin = new Thickness(3, 10, 3, 3) };
        panel.Children.Add(_mailbox);
        var note = Wrapped(TurnUiModel.HiddenInfoNote, new Thickness(3, 10, 3, 3));
        OnlineUi.Gray(note);
        panel.Children.Add(note);
        Ok = OnlineUi.Button("Send…", isDefault: true);
        var cancel = OnlineUi.Button("Cancel", isCancel: true);
        Ok.Click += (_, _) => OnlineUi.Accept(this, TryOk, "send turn");
        cancel.Click += (_, _) => Close(null);
        panel.Children.Add(OnlineUi.Buttons(Ok, cancel));
        Content = panel;
    }

    static TextBlock Wrapped(string text, Thickness margin) =>
        new() { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 400, Margin = margin };

    internal Button Ok { get; }

    /// <summary>The recipients' labels in list order ("Name (sides)").</summary>
    internal IReadOnlyList<string> Recipients { get; }

    public PlayerDef? Recipient => _to.SelectedIndex >= 0 && _to.SelectedIndex < _recipients.Count ? _recipients[_to.SelectedIndex] : null;
    public bool UseMailbox => _mailbox.Use;

    /// <summary>The answer as it stands (null without a recipient).</summary>
    internal SendTurnChoice? Choice => Recipient is { } r ? new SendTurnChoice(r, _mailbox.Use, _mailbox.Relay, _mailbox.Password) : null;

    /// <summary>Closes with the choice, or returns why it can't (no recipient, a missing or bad relay).</summary>
    internal string? TryOk()
    {
        var problem = TurnDialogRules.SendProblem(Recipient, _mailbox.Use, _mailbox.Relay);
        if (problem != null) return problem;
        Close(Choice);
        return null;
    }
}
