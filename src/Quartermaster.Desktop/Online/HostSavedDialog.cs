using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Quartermaster.Core;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Desktop.Online;

/// <summary>What the Host a saved game dialog answers.</summary>
public sealed record HostSavedChoice(string LobbyName, string Password, string PlayerName, IReadOnlyList<string> HostSides);

/// <summary>Host a saved game…: lobby name, password (with the open-lobby warning), the host's name and own sides (WinForms HostSavedDialog).</summary>
sealed class HostSavedDialog : Window
{
    readonly TextBox _lobby = new() { Width = 220, MaxLength = NewGameRules.MaxLobbyName };
    readonly TextBox _password = new() { Width = 220, PasswordChar = '*' };
    readonly TextBlock _warning = new() { Text = OnlineLobbyModel.OpenLobbyWarning, Foreground = Brushes.DarkGoldenrod };
    readonly HostSidesPanel _sides;

    public HostSavedDialog(RulesPack pack, string playerName, string lobbyName, IReadOnlyCollection<string> hostSides)
    {
        Title = "Host a saved game";
        Name = "HostSavedDialog";
        Width = 440;
        Height = 400;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        OnlineUi.Face(this);

        _sides = new HostSidesPanel(pack, playerName, hostSides);
        _lobby.Text = lobbyName;
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), Margin = new Thickness(8) };
        void Put(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); top.Children.Add(c); }
        Put(OnlineUi.Label("Lobby name:", new Thickness(3, 3, 8, 3)), 0, 0);
        Put(_lobby, 0, 1);
        Put(OnlineUi.Label("Password:", new Thickness(3, 3, 8, 3)), 1, 0);
        Put(_password, 1, 1);
        _warning.Margin = new Thickness(3);
        Put(_warning, 2, 1);
        _password.OnTextSet(() => _warning.IsVisible = (_password.Text ?? "").Length == 0);

        var groupTitle = OnlineUi.Label("You and the open seats", new Thickness(6, 0, 0, 2));
        var frame = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(6), Child = _sides };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("Classic.BorderBrush"));
        var group = new DockPanel { Margin = new Thickness(8, 0, 8, 0) };
        DockPanel.SetDock(groupTitle, Dock.Top);
        group.Children.Add(groupTitle);
        group.Children.Add(frame);

        Ok = OnlineUi.Button("Host", isDefault: true);
        var cancel = OnlineUi.Button("Cancel", isCancel: true);
        Ok.Click += (_, _) => OnlineUi.Accept(this, TryOk, "host dialog");
        cancel.Click += (_, _) => Close(null);
        var buttons = OnlineUi.Buttons(Ok, cancel);
        buttons.Margin = new Thickness(8);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(buttons);
        root.Children.Add(group);
        Content = root;
    }

    internal Button Ok { get; }
    internal TextBox LobbyBox => _lobby;
    internal TextBox PasswordBox => _password;
    internal TextBlock Warning => _warning;
    internal HostSidesPanel Sides => _sides;

    public string LobbyName => (ChatText.Clean(_lobby.Text ?? "") ?? "") is var n && n.Length > NewGameRules.MaxLobbyName ? n[..NewGameRules.MaxLobbyName] : n;
    public string Password => _password.Text ?? "";
    public string PlayerName => _sides.PlayerName;
    public IReadOnlyList<string> HostSides => _sides.HostSides;

    /// <summary>What stops OK: an empty lobby name, else the panel's problem (null = fine).</summary>
    internal string? Problem => LobbyName.Length == 0 ? "Give the lobby a name." : _sides.Problem;

    /// <summary>Closes with the choice, or returns why it can't.</summary>
    internal string? TryOk()
    {
        if (Problem is { } p) return p;
        Close(new HostSavedChoice(LobbyName, Password, PlayerName, HostSides));
        return null;
    }
}
