using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Quartermaster.Desktop.Shell;

/// <summary>A small modal with the prompt text, one masked text box and OK / Cancel (the InputDialog look); closes with the entered text, or
/// null when cancelled. Asks for a game's mailbox password.</summary>
sealed class PasswordPrompt : Window
{
    public PasswordPrompt(string caption, string prompt)
    {
        Title = caption;
        SizeToContent = SizeToContent.Height;
        Width = 340;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Box = new TextBox { PasswordChar = '*', MaxLength = 256 };
        var ok = new Button { Content = "OK", MinWidth = 75, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", MinWidth = 75, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => Close(Entered);
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(Box);
        panel.Children.Add(buttons);
        Content = panel;
        Opened += (_, _) => Box.Focus();
    }

    internal TextBox Box { get; }

    internal string Entered => Box.Text ?? "";
}
