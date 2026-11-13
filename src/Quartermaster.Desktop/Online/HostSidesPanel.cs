using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Desktop.Online;

/// <summary>
/// The host's own part of an online setup (launcher spec 3.4): the name to play under and the sides the host holds. Every other side of
/// the pack becomes an open seat. Reused by the Host a saved game dialog and the setup chooser's Online mode.
/// </summary>
public sealed class HostSidesPanel : UserControl
{
    readonly TextBox _name = new() { Width = 200, MaxLength = ChatText.MaxName };
    readonly List<(CheckBox Box, string Id)> _boxes = new();
    readonly RulesPack? _pack;

    public HostSidesPanel(RulesPack? pack, string playerName, IReadOnlyCollection<string>? preselect = null)
    {
        _pack = pack;
        Name = "HostSidesPanel";
        var flow = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(4) };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(OnlineUi.Label("Your name:", new Thickness(3, 3, 6, 3)));
        _name.Text = playerName;
        nameRow.Children.Add(_name);
        flow.Children.Add(nameRow);
        flow.Children.Add(OnlineUi.Label("Sides you hold (every other side is an open seat):", new Thickness(3, 10, 3, 3)));
        var sides = (pack?.Sides ?? new List<SideDef>()).Where(x => !string.IsNullOrEmpty(x?.Id)).ToList();
        var pre = preselect ?? sides.Take(1).Select(x => x.Id).ToList();
        foreach (var s in sides)
        {
            var b = new CheckBox { Content = SideChoiceModel.Label(s), IsChecked = pre.Contains(s.Id), Margin = new Thickness(20, 2, 3, 2) };
            _boxes.Add((b, s.Id));
            flow.Children.Add(b);
        }
        if (sides.Count < 2)
        {
            NeedsPack = new TextBlock { Text = OnlineLobbyModel.NeedsPackText, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, Foreground = Brushes.Firebrick, Margin = new Thickness(3, 6, 3, 3) };
            flow.Children.Add(NeedsPack);
        }
        Content = new ScrollViewer { Content = flow, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    /// <summary>The "needs a rules pack" note, shown under fewer than two sides (null otherwise).</summary>
    internal TextBlock? NeedsPack { get; }

    internal TextBox NameBox => _name;

    internal IReadOnlyList<CheckBox> Boxes => _boxes.Select(b => b.Box).ToList();

    public string PlayerName => ChatText.CleanName(_name.Text ?? "") ?? "";

    public IReadOnlyList<string> HostSides => _boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Id).ToList();

    /// <summary>Null when the name and sides are fine, else the problem in words.</summary>
    public string? Problem =>
        PlayerName.Length == 0 ? HostSession.NoNameText : OnlineLobbyModel.ValidateHostSides(_pack, HostSides);

    /// <summary>The host as the one player of the request (its sides; the rest are open seats).</summary>
    public PlayerDef ToPlayer() => new("host", PlayerName, HostSides.ToList());
}
