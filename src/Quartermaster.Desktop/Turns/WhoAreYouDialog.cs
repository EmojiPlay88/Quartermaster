using Avalonia;
using Avalonia.Controls;
using Quartermaster.Core;
using Quartermaster.Desktop.Online;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Desktop.Turns;

/// <summary>The "Who are you?" answer: the local player's id and the mailbox choice (off and empty when the dialog hid it).</summary>
public sealed record WhoChoice(string LocalPlayerId, bool UseMailbox, string Relay, string Password)
{
    /// <summary>ToString never shows the password.</summary>
    private bool PrintMembers(System.Text.StringBuilder b)
    {
        b.Append("LocalPlayerId = ").Append(LocalPlayerId).Append(", UseMailbox = ").Append(UseMailbox).Append(", Relay = ").Append(Relay)
            .Append(", Password = ").Append(string.IsNullOrEmpty(Password) ? "(none)" : "***");
        return true;
    }
}

/// <summary>"Who are you?" for a play-by-email game (WinForms WhoAreYouDialog): picks which of the game's players sits at this computer (the view
/// narrows to their sides), and optionally the relay mailbox to watch for this game's turns. Closes with a <see cref="WhoChoice"/>.</summary>
sealed class WhoAreYouDialog : Window
{
    readonly List<(RadioButton Box, string Id)> _players = new();
    readonly MailboxOptions? _mailbox;

    /// <param name="mailbox">Shows the "watch my relay" choice with these starting values; null hides it.</param>
    public WhoAreYouDialog(string prompt, IReadOnlyList<PlayerDef> players, RulesPack? pack, string? preselect, MailboxValues? mailbox)
    {
        Title = TurnDialogRules.WhoTitle;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MaxWidth = 420;
        OnlineUi.Face(this);

        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(3, 0, 3, 6) });
        foreach (var p in players.Where(p => p != null && !string.IsNullOrEmpty(p.Id)))
        {
            var b = new RadioButton { Content = TurnDialogRules.PlayerLabel(p, pack), GroupName = "who", IsChecked = p.Id == preselect };
            _players.Add((b, p.Id));
            panel.Children.Add(b);
        }
        if (_players.Count > 0 && !_players.Any(p => p.Box.IsChecked == true)) _players[0].Box.IsChecked = true;
        if (mailbox != null)
        {
            _mailbox = new MailboxOptions(TurnDialogRules.WatchCaption, mailbox) { Margin = new Thickness(3, 10, 3, 3) };
            panel.Children.Add(_mailbox);
        }
        var note = new TextBlock { Text = TurnUiModel.HiddenInfoNote, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(3, 8, 3, 3) };
        OnlineUi.Gray(note);
        panel.Children.Add(note);
        Ok = OnlineUi.Button("OK", isDefault: true);
        var cancel = OnlineUi.Button("Cancel", isCancel: true);
        Ok.Click += (_, _) => OnlineUi.Accept(this, TryOk, "who are you");
        cancel.Click += (_, _) => Close(null);
        panel.Children.Add(OnlineUi.Buttons(Ok, cancel));
        Content = panel;
    }

    internal Button Ok { get; }

    internal bool ShowsMailbox => _mailbox != null;

    internal IReadOnlyList<string> PlayerLabels => _players.Select(p => (string)p.Box.Content!).ToList();

    public string? LocalPlayerId => _players.FirstOrDefault(p => p.Box.IsChecked == true).Id;

    /// <summary>The answer as it stands (null when no player is ticked).</summary>
    internal WhoChoice? Choice => LocalPlayerId is { } id
        ? new WhoChoice(id, _mailbox?.Use == true, _mailbox?.Relay ?? "", _mailbox?.Password ?? "")
        : null;

    /// <summary>Closes with the choice, or returns why it can't (no player, a missing or bad relay).</summary>
    internal string? TryOk()
    {
        var problem = TurnDialogRules.WhoProblem(LocalPlayerId, _mailbox?.Use == true, _mailbox?.Relay);
        if (problem != null) return problem;
        Close(Choice);
        return null;
    }
}
