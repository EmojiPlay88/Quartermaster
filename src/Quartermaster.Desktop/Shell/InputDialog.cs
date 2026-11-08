using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// The small classic "prompt" dialog (WinForms InputDialog): the prompt, one text box, OK / Cancel, 320 wide. Closes with the entered text,
/// or null when cancelled. Used for counter actions that ask for a value and for rules-pack names (through <see cref="IDialogs.AskTextAsync"/>).
/// </summary>
sealed class InputDialog : Window
{
    public InputDialog(string title, string prompt, string initial)
    {
        Title = title;
        SizeToContent = SizeToContent.Height;
        Width = 320;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var box = new TextBox { Text = initial };
        var ok = new Button { Content = "OK", MinWidth = 75, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", MinWidth = 75, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => Close(box.Text ?? "");
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        Content = panel;
        Opened += (_, _) => ok.Focus();   // as WinForms InputDialog: OK has TabIndex 0 and so the focus
    }
}
