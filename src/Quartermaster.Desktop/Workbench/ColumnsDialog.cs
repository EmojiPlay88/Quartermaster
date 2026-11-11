using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Quartermaster.Desktop.Workbench;

/// <summary>Edits the column keys of a two-way table: one key per line (WinForms ColumnsDialog). Closes with the keys, or null when cancelled.</summary>
sealed class ColumnsDialog : Window
{
    public TextBox Box { get; }
    public Button OkButton { get; }
    public Button CancelButton { get; }

    public ColumnsDialog(IEnumerable<string> keys)
    {
        Title = "Columns";
        Width = 300;
        Height = 260;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Box = new TextBox { AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.NoWrap, Text = string.Join(Environment.NewLine, keys) };
        OkButton = new Button { Content = "OK", Width = 75, HorizontalContentAlignment = HorizontalAlignment.Center };
        CancelButton = new Button { Content = "Cancel", Width = 75, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        OkButton.Click += (_, _) => Close(Result());
        CancelButton.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 7, Margin = new Thickness(8) };
        buttons.Children.Add(OkButton);
        buttons.Children.Add(CancelButton);
        var label = new TextBlock
        {
            Text = "One column key per line (a number, a range like 3-5, 7+, or text):",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(8, 6, 8, 6),
        };
        var host = new Border { Padding = new Thickness(8, 0), Child = Box };
        var dock = new DockPanel();
        DockPanel.SetDock(label, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(label);
        dock.Children.Add(buttons);
        dock.Children.Add(host);
        Content = dock;
    }

    /// <summary>The trimmed non-blank lines; a single empty key when there are none (at least one column).</summary>
    public IReadOnlyList<string> Result()
    {
        var list = (Box.Text ?? "").Replace("\r\n", "\n").Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        return list.Count == 0 ? new List<string> { "" } : list;
    }
}
