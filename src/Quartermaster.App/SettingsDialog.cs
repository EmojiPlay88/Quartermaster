using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// Tools, Settings. One tab per area; OK applies and closes, Apply applies and stays, Cancel closes (an earlier Apply stays applied).
/// "Restore defaults" resets only the visible tab, and takes effect when applied.
/// </summary>
sealed class SettingsDialog : Form
{
    readonly Action<Settings> _apply;
    readonly SettingsTab[] _tabs;
    readonly TabControl _pages = new() { Dock = DockStyle.Fill };
    Settings _current;
    readonly Func<Settings>? _latest;

    public SettingsDialog(Settings initial, Action<Settings> apply, SettingsContext ctx, Func<Settings>? latest = null)
    {
        _current = initial;
        _latest = latest;
        _apply = apply;
        Keyboard = new KeyboardTab();
        _tabs = new SettingsTab[] { new GeneralTab(), new DisplayTab(), new MapTab(), new DiceLogTab(), new ToolbarTab(ctx.ToolbarItems), Keyboard, new FoldersTab(ctx) };

        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 400);

        foreach (var t in _tabs)
        {
            var page = new TabPage(t.Title) { UseVisualStyleBackColor = true };
            page.Controls.Add(t.Build());
            t.Load(initial);
            _pages.TabPages.Add(page);
        }

        var ok = new Button { Text = "OK", Width = 80 };
        var cancel = new Button { Text = "Cancel", Width = 80, DialogResult = DialogResult.Cancel };
        var apply2 = new Button { Text = "Apply", Width = 80 };
        var restore = new Button { Text = "Restore defaults", Width = 110, Dock = DockStyle.Left };
        ok.Click += (_, _) => Guarded(() => { Commit(); DialogResult = DialogResult.OK; });
        apply2.Click += (_, _) => Guarded(Commit);
        restore.Click += (_, _) => Guarded(RestoreTab);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange(new Control[] { ok, cancel, apply2 });
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(8, 6, 8, 6) };
        bar.Controls.Add(buttons);
        bar.Controls.Add(restore);
        Controls.Add(_pages);
        Controls.Add(bar);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>The keyboard page, whose <see cref="KeyboardTab.Host"/> the shortcut editor fills.</summary>
    public KeyboardTab Keyboard { get; }

    public Settings CollectForSmoke() => Collect();

    public bool FoldersLoadedForSmoke => _tabs.OfType<FoldersTab>().First().Loaded;

    public void SelectTab(int index)
    {
        if (index >= 0 && index < _pages.TabPages.Count) _pages.SelectedIndex = index;
    }

    Settings Collect() => _tabs.Aggregate(_current, (s, t) => t.Collect(s));

    void Commit()
    {
        _current = _tabs.Aggregate(_latest?.Invoke() ?? _current, (s, t) => t.Collect(s)); // on top of what the store holds now
        _apply(_current);
    }

    void RestoreTab()
    {
        var i = _pages.SelectedIndex;
        if (i < 0 || i >= _tabs.Length) return;
        _tabs[i].Load(new Settings());
    }

    void Guarded(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("settings dialog: " + ex);
            if (!Program.Smoke) MessageBox.Show(this, ex.Message, "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public void SaveScreenshot(string path)
    {
        Update();
        using var bmp = new Bitmap(Math.Max(Width, 1), Math.Max(Height, 1));
        DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Program.AppLog("smoke: settings screenshot saved to " + path);
    }
}
