using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>
/// "Who are you?" for a play-by-email game: picks which of the game's players sits at this computer (the view narrows to their sides),
/// and optionally the relay mailbox to watch for this game's turns.
/// </summary>
sealed class WhoAreYouDialog : Form
{
    readonly List<(RadioButton Box, string Id)> _players = new();
    readonly MailboxOptions? _mailbox;

    public string? LocalPlayerId => _players.FirstOrDefault(p => p.Box.Checked).Id;
    public bool UseMailbox => _mailbox?.Use == true;
    public string Relay => _mailbox?.Relay ?? "";
    public string Password => _mailbox?.Password ?? "";

    /// <param name="mailbox">Shows the "watch my relay" choice with these starting values; null hides it.</param>
    public WhoAreYouDialog(string prompt, IReadOnlyList<PlayerDef> players, RulesPack? pack, string? preselect, MailboxValues? mailbox)
    {
        Text = "Who are you?";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        ShowIcon = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(12), WrapContents = false };
        flow.Controls.Add(new Label { Text = prompt, AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(3, 0, 3, 6) });
        foreach (var p in players.Where(p => p != null && !string.IsNullOrEmpty(p.Id)))
        {
            var sides = HandoverForm.SidesText(p, pack);
            var b = new RadioButton { Text = (string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name) + (sides.Length > 0 ? $" ({sides})" : ""), AutoSize = true, Checked = p.Id == preselect, UseMnemonic = false };
            _players.Add((b, p.Id));
            flow.Controls.Add(b);
        }
        if (_players.Count > 0 && !_players.Any(p => p.Box.Checked)) _players[0].Box.Checked = true;
        if (mailbox != null)
        {
            _mailbox = new MailboxOptions("Watch the turn mailbox on my relay", mailbox) { Margin = new Padding(3, 10, 3, 3) };
            flow.Controls.Add(_mailbox);
        }
        flow.Controls.Add(new Label { Text = TurnUiModel.HiddenInfoNote, AutoSize = true, MaximumSize = new Size(380, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 3) });
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 75 };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(3, 10, 3, 3) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        flow.Controls.Add(buttons);
        Controls.Add(flow);
        AcceptButton = ok;
        CancelButton = cancel;
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK) return;
            var problem = LocalPlayerId == null ? "Choose which player you are." : _mailbox?.Problem();
            if (problem == null) return;
            MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            e.Cancel = true;
        };
    }
}

/// <summary>Starting values of the mailbox choice (the game's <see cref="TurnIdentity"/>, the relay from Settings).</summary>
sealed record MailboxValues(bool Use, string? Relay, string? Password)
{
    /// <summary>ToString never shows the password (a record would print it into logs and debugger views).</summary>
    bool PrintMembers(System.Text.StringBuilder b)
    {
        b.Append("Use = ").Append(Use).Append(", Relay = ").Append(Relay).Append(", Password = ")
            .Append(string.IsNullOrEmpty(Password) ? "(none)" : "***");
        return true;
    }
}

/// <summary>The relay mailbox choice shared by the turn dialogs: a tick box, the relay address and the game's mailbox password.</summary>
sealed class MailboxOptions : FlowLayoutPanel
{
    readonly CheckBox _use = new() { AutoSize = true };
    readonly TextBox _relay = new() { Width = 240, MaxLength = 512 };
    readonly TextBox _password = new() { Width = 160, UseSystemPasswordChar = true, MaxLength = 256 };

    public bool Use => _use.Checked;
    public string Relay => _relay.Text.Trim();
    public string Password => _password.Text;

    public MailboxOptions(string caption, MailboxValues v)
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        _use.Text = caption;
        _use.Checked = v.Use;
        _relay.Text = v.Relay ?? "";
        _password.Text = v.Password ?? "";
        Controls.Add(_use);
        Controls.Add(Row("Relay:", _relay));
        Controls.Add(Row("Mailbox password (optional, the same for every player):", _password));
        _use.CheckedChanged += (_, _) => Sync();
        Sync();
    }

    static FlowLayoutPanel Row(string label, Control c)
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(20, 2, 3, 2) };
        row.Controls.Add(new Label { Text = label, AutoSize = true });
        row.Controls.Add(c);
        return row;
    }

    void Sync() => _relay.Enabled = _password.Enabled = _use.Checked;

    /// <summary>Why the choice cannot be used (a bad relay address), else null.</summary>
    public string? Problem()
    {
        if (!Use) return null;
        if (string.IsNullOrWhiteSpace(Relay)) return MailboxService.NoRelayText;
        return OnlineLobbyModel.ParseRelay(Relay, out var err) == null ? err ?? MailboxService.NoRelayText : null;
    }
}
