using Avalonia.Controls;
using Avalonia.Input;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// The chat line under the log while a game is online (WinForms ChatBox, spec 5.2.5): Enter (or Send) sends the text and clears the box.
/// Chat lines come back as logged commands, so they show in the log itself. Offline there is no chat line (WinForms shows none either).
/// </summary>
public sealed class ChatBox : UserControl
{
    readonly TextBox _text = new() { MaxLength = ChatText.MaxLength, PlaceholderText = LogPanelRules.ChatPlaceholder, Name = "ChatInput" };
    readonly Button _send = new() { Content = "Send", Width = 60, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, Name = "ChatSend" };
    readonly Action<string> _sendText;

    public ChatBox(Action<string> send)
    {
        _sendText = send ?? throw new ArgumentNullException(nameof(send));
        Name = "ChatBox";
        Height = 30;
        var dock = new DockPanel { Margin = new Avalonia.Thickness(2) };
        DockPanel.SetDock(_send, Dock.Right);
        dock.Children.Add(_send);
        dock.Children.Add(_text);
        Content = dock;
        _send.Click += (_, _) => Send();
        _text.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Send();
        };
    }

    /// <summary>The edit control (the main window's shortcuts must not steal its keys).</summary>
    public TextBox Input => _text;

    /// <summary>The Send button.</summary>
    public Button SendButton => _send;

    /// <summary>Sends what was typed (nothing for blank text); the box is cleared only when the text was handed on.</summary>
    public void Send()
    {
        var t = ChatText.Clean(_text.Text);
        if (t == null) return;
        try { _sendText(t); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return; }
        _text.Clear();
    }
}
