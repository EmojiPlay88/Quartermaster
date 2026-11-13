using Avalonia;
using Avalonia.Controls;
using Quartermaster.Core;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Online;

/// <summary>What the Join dialog answers.</summary>
public sealed record JoinChoice(string PlayerName, string Password);

/// <summary>Join…: the player name, the password when the lobby has one, and the note that the host's copy runs the game (WinForms JoinDialog).</summary>
sealed class JoinDialog : Window
{
    readonly TextBox _name = new() { Width = 200, MaxLength = ChatText.MaxName };
    readonly TextBox _password = new() { Width = 200, PasswordChar = '*' };

    public JoinDialog(LobbyRow row, string playerName)
    {
        Title = "Join " + row.Lobby;
        Name = "JoinDialog";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        OnlineUi.Face(this);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto"), Margin = new Thickness(12) };
        var r = 0;
        void Row(Control a, Control? b)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(a, r);
            grid.Children.Add(a);
            if (b != null) { Grid.SetRow(b, r); Grid.SetColumn(b, 1); grid.Children.Add(b); }
            else Grid.SetColumnSpan(a, 2);
            r++;
        }
        Row(OnlineUi.Label($"Hosted by {row.Host} · free sides: {row.FreeSides}", new Thickness(3, 0, 3, 8)), null);
        _name.Text = playerName;
        Row(OnlineUi.Label("Your name:", new Thickness(3, 6, 6, 3)), _name);
        if (row.Locked) Row(OnlineUi.Label("Password:", new Thickness(3, 6, 6, 3)), _password);
        var note = new TextBlock
        {
            Text = OnlineLobbyModel.JoinNote + " You see only your sides' view; the host's rules pack is sent to you.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 320, Margin = new Thickness(3, 10, 3, 3),
        };
        OnlineUi.Gray(note);
        Row(note, null);
        Ok = OnlineUi.Button("Join", isDefault: true);
        var cancel = OnlineUi.Button("Cancel", isCancel: true);
        Ok.Click += (_, _) => OnlineUi.Accept(this, TryOk, "join dialog");
        cancel.Click += (_, _) => Close(null);
        Row(OnlineUi.Buttons(Ok, cancel), null);
        Content = grid;
    }

    internal Button Ok { get; }
    internal TextBox NameBox => _name;
    internal TextBox PasswordBox => _password;

    /// <summary>The password box is part of the dialog only for a locked lobby.</summary>
    internal bool PasswordShown => _password.Parent != null;

    public string PlayerName => ChatText.CleanName(_name.Text ?? "") ?? "";
    public string Password => _password.Text ?? "";

    /// <summary>Closes with the choice, or returns why it can't (no name).</summary>
    internal string? TryOk()
    {
        if (PlayerName.Length == 0) return HostSession.NoNameText;
        Close(new JoinChoice(PlayerName, Password));
        return null;
    }
}
