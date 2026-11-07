using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>
/// Game → Send Turn…: who the turn is for (default: the next player after the local one), whether it also goes through the relay
/// mailbox, and the plain note that turn files hold the whole game. Nothing is built until the user confirms (and picks the file).
/// </summary>
sealed class SendTurnDialog : Form
{
    readonly ComboBox _to = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly List<PlayerDef> _recipients;
    readonly MailboxOptions _mailbox;

    public PlayerDef? Recipient => _to.SelectedIndex >= 0 && _to.SelectedIndex < _recipients.Count ? _recipients[_to.SelectedIndex] : null;
    public bool UseMailbox => _mailbox.Use;
    public string Relay => _mailbox.Relay;
    public string Password => _mailbox.Password;

    public SendTurnDialog(string fromName, IReadOnlyList<PlayerDef> players, string localPlayerId, RulesPack? pack, MailboxValues mailbox)
    {
        Text = "Send Turn";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        ShowIcon = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _recipients = TurnUiModel.Recipients(players, localPlayerId).ToList();
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(12), WrapContents = false };
        flow.Controls.Add(new Label { Text = $"Send {fromName}'s turn. Next you choose where to save the turn file.", AutoSize = true, MaximumSize = new Size(400, 0), UseMnemonic = false, Margin = new Padding(3, 0, 3, 8) });
        flow.Controls.Add(new Label { Text = "Send to:", AutoSize = true });
        foreach (var p in _recipients)
        {
            var sides = HandoverForm.SidesText(p, pack);
            _to.Items.Add((string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name) + (sides.Length > 0 ? $" ({sides})" : ""));
        }
        var def = TurnUiModel.DefaultRecipient(players, localPlayerId);
        _to.SelectedIndex = def == null ? (_recipients.Count > 0 ? 0 : -1) : _recipients.FindIndex(p => p.Id == def.Id);
        flow.Controls.Add(_to);
        _mailbox = new MailboxOptions("Also send through my relay", mailbox) { Margin = new Padding(3, 10, 3, 3) };
        flow.Controls.Add(_mailbox);
        flow.Controls.Add(new Label { Text = TurnUiModel.HiddenInfoNote, AutoSize = true, MaximumSize = new Size(400, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 10, 3, 3) });
        var ok = new Button { Text = "Send…", DialogResult = DialogResult.OK, Width = 75 };
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
            var problem = Recipient == null ? MailboxService.NoRecipientText : _mailbox.Problem();
            if (problem == null) return;
            MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            e.Cancel = true;
        };
    }
}
