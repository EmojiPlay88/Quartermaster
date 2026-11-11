using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Preferences;

public sealed class ToolbarPage : SettingsPage
{
    readonly IReadOnlyList<(string Id, string Text)> _items;
    internal readonly List<CheckBox> Checks = new();

    public ToolbarPage(IReadOnlyList<(string Id, string Text)> items)
    {
        _items = items;
        var list = new StackPanel();
        foreach (var (_, text) in items)
        {
            var c = new CheckBox { Content = text };
            Checks.Add(c);
            list.Children.Add(c);
        }
        var dock = new DockPanel { Margin = new Thickness(10) };
        var head = new TextBlock { Text = "Tick the buttons to show in the toolbar:", Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(new ScrollViewer { Content = list });
        Content = dock;
    }

    public override string Title => "Toolbar";

    public override void Load(Settings s)
    {
        for (var i = 0; i < _items.Count; i++) Checks[i].IsChecked = s.IsToolbarVisible(_items[i].Id);
    }

    public override Settings Collect(Settings s)
    {
        var map = new Dictionary<string, bool>(s.ToolbarVisible);
        for (var i = 0; i < _items.Count; i++) map[_items[i].Id] = Checks[i].IsChecked == true;
        return s with { ToolbarVisible = map };
    }
}
