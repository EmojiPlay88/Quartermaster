using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Quartermaster.Desktop.Online;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Turns;

/// <summary>The relay mailbox choice shared by the turn dialogs (WinForms MailboxOptions): a tick box, the relay address and the game's mailbox
/// password. The boxes are enabled only while the tick is set; the checks are <see cref="TurnDialogRules.MailboxProblem"/>.</summary>
sealed class MailboxOptions : UserControl
{
    public MailboxOptions(string caption, MailboxValues v)
    {
        UseBox = new CheckBox { Content = caption, IsChecked = v.Use };
        RelayBox = new TextBox { Width = 240, MaxLength = 512, Text = v.Relay ?? "", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        PasswordBox = new TextBox { Width = 160, MaxLength = 256, PasswordChar = '*', Text = v.Password ?? "", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(UseBox);
        panel.Children.Add(Row(TurnDialogRules.RelayLabel, RelayBox));
        panel.Children.Add(Row(TurnDialogRules.PasswordLabel, PasswordBox));
        Content = panel;
        // The tick property changes synchronously, so the boxes follow it in the same tick.
        UseBox.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty) Sync(); };
        Sync();
    }

    internal CheckBox UseBox { get; }
    internal TextBox RelayBox { get; }
    internal TextBox PasswordBox { get; }

    public bool Use => UseBox.IsChecked == true;
    public string Relay => (RelayBox.Text ?? "").Trim();
    public string Password => PasswordBox.Text ?? "";

    static StackPanel Row(string label, Control c)
    {
        var row = new StackPanel { Margin = new Thickness(20, 2, 3, 2) };
        row.Children.Add(OnlineUi.Label(label, new Thickness(0, 0, 0, 2)));
        row.Children.Add(c);
        return row;
    }

    void Sync() => RelayBox.IsEnabled = PasswordBox.IsEnabled = Use;

    /// <summary>Why the choice cannot be used (a missing or bad relay address), else null.</summary>
    public string? Problem => TurnDialogRules.MailboxProblem(Use, Relay);
}
