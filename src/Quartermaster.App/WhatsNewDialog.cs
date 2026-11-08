using Quartermaster.Render;
using Quartermaster.Render.Update;

namespace Quartermaster.App;

/// <summary>What's new: the release notes of an update as plain text (the Markdown is not rendered), with a link to the release page.</summary>
sealed class WhatsNewDialog : Form
{
    readonly Font _mono = new(FontFamily.GenericMonospace, 9f);

    public WhatsNewDialog(UpdateInfo info)
    {
        Text = $"What's new in Quartermaster {AppVersion.Format(info.Version)}";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 380);
        MinimumSize = new Size(360, 240);
        AutoScaleMode = AutoScaleMode.Font;
        ShowIcon = false;
        ShowInTaskbar = false;
        MinimizeBox = false;

        var notes = string.IsNullOrWhiteSpace(info.Notes) ? "(This release has no notes.)" : info.Notes.ReplaceLineEndings("\r\n");
        var box = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
            Text = notes, BackColor = SystemColors.Window, Font = _mono,
        };
        box.SelectionStart = 0;
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 8, 8, 0) };
        host.Controls.Add(box);

        var page = new LinkLabel { Text = "Open the release page", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 9, 3, 3) };
        page.LinkClicked += (_, _) => AppUpdates.OpenPage(this, info.PageUrl);
        var close = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(75, 26) };
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
        right.Controls.Add(close);
        var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false };
        left.Controls.Add(page);
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(8, 6, 8, 6) };
        buttons.Controls.Add(right);
        buttons.Controls.Add(left);

        Controls.Add(host);
        Controls.Add(buttons);
        AcceptButton = close;
        CancelButton = close;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _mono.Dispose();
        base.Dispose(disposing);
    }
}
