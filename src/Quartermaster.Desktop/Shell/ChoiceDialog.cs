using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Quartermaster.Desktop.Shell;

/// <summary>A message with a row of named buttons (the WinForms TaskDialog Retry / Close and Open anyway / Cancel questions). Closes with the
/// index of the button clicked; closing the window any other way leaves <see cref="Result"/> at -1.</summary>
sealed class ChoiceDialog : Window
{
    readonly List<Button> _buttons = new();

    public ChoiceDialog(string message, string caption, IReadOnlyList<string> buttons, int defaultIndex)
    {
        Title = caption;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MinWidth = 260;
        MaxWidth = 520;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(10) };
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var b = new Button { Content = buttons[i], MinWidth = 75, IsDefault = i == defaultIndex, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) => Pick(index);
            _buttons.Add(b);
            row.Children.Add(b);
        }
        var panel = new DockPanel();
        DockPanel.SetDock(row, Dock.Bottom);
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(16, 16, 16, 6) });
        Content = panel;
        Opened += (_, _) => (defaultIndex >= 0 && defaultIndex < _buttons.Count ? _buttons[defaultIndex] : _buttons.FirstOrDefault())?.Focus();
    }

    internal IReadOnlyList<Button> Buttons => _buttons;

    /// <summary>The index clicked; -1 until a button was.</summary>
    internal int Result { get; private set; } = -1;

    /// <summary>What clicking button <paramref name="index"/> does: records it and closes.</summary>
    internal int Pick(int index)
    {
        Result = index;
        Close(index);
        return index;
    }
}
