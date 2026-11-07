using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>
/// Hot-seat handover screen: shown in place of the main window (which is hidden with every other Quartermaster window while it is up):
/// <c>Pass to {player} ({sides})</c> and a Continue button. Closing it asks the main window to close (the usual unsaved-changes prompt).
/// </summary>
sealed class HandoverForm : Form
{
    readonly Label _text = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomCenter, Padding = new Padding(12) };
    readonly Button _continue = new() { Text = "Continue", FlatStyle = FlatStyle.System, AutoSize = true, Padding = new Padding(20, 4, 20, 4) };
    readonly Font _big;
    bool _closingForReal;

    public HandoverForm(PlayerDef player, string sidesText)
    {
        Text = "Quartermaster";
        Name = "HandoverForm";
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(360, 220);
        BackColor = SystemColors.Control;
        KeyPreview = true;
        _big = new Font(Font.FontFamily, 18f, FontStyle.Bold);
        _text.Font = _big;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.Controls.Add(_text, 0, 0);
        var holder = new Panel { Dock = DockStyle.Fill };
        holder.Controls.Add(_continue);
        holder.Resize += (_, _) => _continue.Location = new Point(Math.Max(0, (holder.Width - _continue.Width) / 2), 12);
        layout.Controls.Add(holder, 0, 1);
        Controls.Add(layout);
        AcceptButton = _continue;
        _continue.Click += (_, _) => Continued?.Invoke();
        SetPlayer(player, sidesText);
    }

    public PlayerDef Player { get; private set; } = null!;

    /// <summary>The player clicked Continue; the host takes over for <see cref="Player"/> and closes this.</summary>
    public event Action? Continued;

    /// <summary>The user closed the screen; the host decides (it asks the main window to close).</summary>
    public event Action? CloseRequested;

    public string Message => _text.Text;

    public static string MessageFor(PlayerDef p, string sidesText) => $"Pass to {p.Name} ({sidesText})";

    /// <summary>Side names of the player in pack order (ids when the pack has no such side).</summary>
    public static string SidesText(PlayerDef p, RulesPack? pack)
    {
        var names = (p.Sides ?? Array.Empty<string>()).Select(id =>
            pack?.Sides.FirstOrDefault(s => s != null && s.Id == id) is { } sd && !string.IsNullOrWhiteSpace(sd.Name) ? sd.Name : id);
        return string.Join(", ", names);
    }

    public void SetPlayer(PlayerDef p, string sidesText)
    {
        Player = p;
        _text.Text = MessageFor(p, sidesText);
    }

    public void SmokeContinue() => _continue.PerformClick();

    public void CloseForReal()
    {
        _closingForReal = true;
        Close();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _continue.Focus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closingForReal && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            CloseRequested?.Invoke();
        }
        base.OnFormClosing(e);
    }

    public void SaveScreenshot(string path)
    {
        using var bmp = new Bitmap(Math.Max(Width, 1), Math.Max(Height, 1));
        DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _big.Dispose();
        base.Dispose(disposing);
    }
}
