using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Quartermaster.Desktop.Workbench;

/// <summary>
/// "Where should the scanned rows go?" (WinForms TablesTab import prompt): a new table or the selected one. Closes with true = replace the
/// selected table, false = new table, null = cancel.
/// </summary>
sealed class ImportTargetDialog : Window
{
    public const string Caption = "Import into table";
    public const string Heading = "Where should the scanned rows go?";

    public TextBlock HeadingText { get; }
    public TextBlock MessageText { get; }
    public Button NewButton { get; }
    public Button ReplaceButton { get; }
    public Button CancelButton { get; }

    public ImportTargetDialog(string selectedTable)
    {
        Title = Caption;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MinWidth = 260;
        MaxWidth = 520;
        HeadingText = new TextBlock { Text = Heading, FontWeight = Avalonia.Media.FontWeight.Bold, Margin = new Thickness(16, 16, 16, 4) };
        MessageText = new TextBlock
        {
            Text = $"Into a new table, or into the selected table \"{selectedTable}\" (its rows, columns and cells are replaced).",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 6),
        };
        Button Add(string text, bool? result, bool isDefault = false, bool isCancel = false)
        {
            var b = new Button { Content = text, MinWidth = 75, IsDefault = isDefault, IsCancel = isCancel, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) => Close(result);
            return b;
        }
        NewButton = Add("New table…", false, isDefault: true);
        ReplaceButton = Add($"Replace \"{selectedTable}\"", true);
        CancelButton = Add("Cancel", null, isCancel: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(10) };
        buttons.Children.Add(NewButton);
        buttons.Children.Add(ReplaceButton);
        buttons.Children.Add(CancelButton);
        var panel = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(HeadingText, Dock.Top);
        panel.Children.Add(buttons);
        panel.Children.Add(HeadingText);
        panel.Children.Add(MessageText);
        Content = panel;
        Opened += (_, _) => NewButton.Focus();
    }
}
