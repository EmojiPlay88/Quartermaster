namespace Quartermaster.App;

/// <summary>
/// The pale-yellow strip at the top of the launcher: a line of text and a few link actions ("Quartermaster 0.2.0 is available.
/// What's new · Update now · Later · Skip this version", or a one-off notice such as "Updated to 0.2.0."). Hidden until filled.
/// </summary>
sealed class UpdateBar : FlowLayoutPanel
{
    static readonly Color Fill = Color.FromArgb(255, 244, 200); // the AlertBar notice-chip yellow

    readonly Label _text = new() { AutoSize = true, Margin = new Padding(0, 4, 10, 4), UseMnemonic = false };
    readonly List<LinkLabel> _links = new();

    public UpdateBar()
    {
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        WrapContents = true;
        BackColor = Fill;
        Padding = new Padding(8, 2, 8, 3);
        Visible = false;
        ResizeRedraw = true;
        Controls.Add(_text);
    }

    public string Message => _text.Text;

    /// <summary>The link captions now shown (smoke checks).</summary>
    public IReadOnlyList<string> Actions => _links.Select(l => l.Text).ToList();

    /// <summary>Shows <paramref name="text"/> with the given link actions; an empty text hides the bar.</summary>
    public void Show(string text, params (string Caption, Action Click)[] actions)
    {
        foreach (var l in _links) { Controls.Remove(l); l.Dispose(); }
        _links.Clear();
        _text.Text = text;
        foreach (var (caption, click) in actions)
        {
            var l = new LinkLabel { Text = caption, AutoSize = true, Margin = new Padding(0, 4, 10, 4), UseMnemonic = false };
            l.LinkClicked += (_, _) => click();
            _links.Add(l);
            Controls.Add(l);
        }
        Visible = text.Length > 0;
    }

    public void Clear() => Show("");

    public void SetActionEnabled(string caption, bool enabled)
    {
        foreach (var l in _links) if (l.Text == caption) l.Enabled = enabled;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(SystemColors.ControlDark);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}
