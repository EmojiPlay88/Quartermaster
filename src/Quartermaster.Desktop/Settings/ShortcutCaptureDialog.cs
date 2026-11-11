using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Quartermaster.Desktop.Input;

namespace Quartermaster.Desktop.Preferences;

/// <summary>
/// The "Set shortcut" prompt (WinForms ShortcutCaptureDialog): the next key combination pressed is the answer. Esc cancels (null),
/// Backspace or Delete clears (""), a modifier alone or an unsupported key is swallowed. Keys are taken in the tunnel phase so Tab, Enter
/// and the arrows reach the dialog before focus navigation or a default button.
/// </summary>
sealed class ShortcutCaptureDialog : Window
{
    public TextBlock Prompt { get; }

    public ShortcutCaptureDialog(string commandLabel)
    {
        Title = "Set shortcut";
        Width = 320;
        Height = 80;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Prompt = new TextBlock
        {
            Text = $"Press the new shortcut for \"{commandLabel}\".\nEsc cancels; Backspace or Delete clears.",
            TextAlignment = Avalonia.Media.TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Content = Prompt;
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Opened += (_, _) => Focus();
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        e.Handled = true;   // everything is ours, including unsupported keys
        switch (e.Key)
        {
            case Key.Escape: Close(null); return;
            case Key.Back or Key.Delete: Close(""); return;
        }
        if (ShortcutGestures.Format(e.Key, e.KeyModifiers) is { } s) Close(s);
    }
}
