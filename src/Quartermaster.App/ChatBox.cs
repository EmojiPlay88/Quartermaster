using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// The chat line under the log while a game is online (spec 5.2.5): Enter (or Send) sends the text and clears the box. Chat lines come
/// back as logged commands, so they show in the log itself.
/// </summary>
sealed class ChatBox : UserControl
{
    readonly TextBox _text = new() { Dock = DockStyle.Fill, MaxLength = ChatText.MaxLength, PlaceholderText = LogPanelRules.ChatPlaceholder };
    readonly Button _send = new() { Text = "Send", Dock = DockStyle.Right, Width = 60, FlatStyle = FlatStyle.System };
    readonly Action<string> _sendText;

    public ChatBox(Action<string> send)
    {
        _sendText = send;
        Name = "ChatBox";
        Height = 26;
        Padding = new Padding(2);
        Controls.Add(_text);
        Controls.Add(_send);
        _send.Click += (_, _) => Send();
        _text.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            Send();
        };
    }

    /// <summary>The edit control (keyboard shortcuts of the main window must not steal its keys).</summary>
    public TextBox Input => _text;

    /// <summary>Sends what was typed (nothing for blank text); the box is cleared only when the text was handed on.</summary>
    public void Send()
    {
        var t = ChatText.Clean(_text.Text);
        if (t == null) return;
        try { _sendText(t); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("chat: " + ex.Message);
            return;
        }
        _text.Clear();
    }

    /// <summary>Smoke: types and sends a line.</summary>
    public void SendForSmoke(string text)
    {
        _text.Text = text;
        Send();
    }
}
