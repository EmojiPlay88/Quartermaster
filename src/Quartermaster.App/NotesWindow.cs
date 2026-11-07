using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>MDI child with the game's free-text notes. Typing commits SetNotes after a pause, on leaving the box and on close.</summary>
sealed class NotesWindow : Form
{
    readonly ModuleSession _s;
    readonly Func<IActionPort?>? _actions;
    readonly TextBox _box = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, AcceptsTab = false, Font = new Font("Consolas", 9f), WordWrap = true };
    readonly System.Windows.Forms.Timer _commitTimer = new() { Interval = 1000 };
    GameSession? _game;
    bool _closing;
    bool _loading;

    public NotesWindow(ModuleSession s, Func<IActionPort?>? actions = null)
    {
        _s = s;
        _actions = actions;
        Text = "Notes";
        Size = new Size(420, 360);
        Controls.Add(_box);
        _box.TextChanged += (_, _) => { if (!_loading) { _commitTimer.Stop(); _commitTimer.Start(); } };
        _box.Leave += (_, _) => Run(Flush);
        _commitTimer.Tick += (_, _) => Run(Flush);
        s.GameReplaced += OnGameReplaced;
        HookGame();
        Reload();
    }

    bool Pending => _commitTimer.Enabled;

    void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("notes window failed: " + ex);
            _s.Log.Add(DiagnosticSeverity.Error, "notes", "The notes window failed: " + ex.Message);
        }
    }

    void OnUi(Action a)
    {
        if (IsDisposed || _closing || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(() => { if (!IsDisposed && !_closing) a(); }); else a();
    }

    void HookGame()
    {
        if (_game != null) _game.Changed -= OnGameChanged;
        if (_game != null) _game.Rewound -= OnRewound;
        _game = _s.Game;
        if (_game != null) _game.Changed += OnGameChanged;
        if (_game != null) _game.Rewound += OnRewound;
    }

    void OnGameChanged(GameCommand _) => OnUi(() => { if (NotesSync.ShouldReload(_box.Focused, Pending)) Run(Reload); });
    void OnRewound() => OnUi(() => { if (NotesSync.ShouldReload(_box.Focused, Pending)) Run(Reload); });
    // Pending text was already flushed into the old game by MainForm (FlushNow) before the replacement; this only reloads.
    void OnGameReplaced() => OnUi(() => { _commitTimer.Stop(); HookGame(); Run(Reload); });

    /// <summary>Writes the box text to the state when it differs from it. Never commits an unchanged text.</summary>
    void Flush()
    {
        _commitTimer.Stop();
        // Only into the session's current game: a stale or disposed one drops the text silently.
        if (_game == null || IsDisposed || _s.Disposed || !ReferenceEquals(_game, _s.Game)) return;
        var text = _box.Text;
        if (!NotesSync.ShouldCommit(text, _game.State.Notes)) return;
        if (_actions?.Invoke() is { } port) port.SetNotes(text);
        else _game.Execute(CommandLog.Stamp(new SetNotes(text)));
    }

    /// <summary>Commits pending text now; MainForm calls this before anything that replaces or closes the game.</summary>
    public void FlushNow() => Run(Flush);

    void Reload()
    {
        var text = _s.Game?.State.Notes ?? "";
        if (_box.Text == text) return;
        _loading = true;
        try { _box.Text = text; _box.SelectionStart = _box.TextLength; }
        finally { _loading = false; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Run(Flush);
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        Unhook();
    }

    void Unhook()
    {
        _closing = true;
        _commitTimer.Stop();
        _s.GameReplaced -= OnGameReplaced;
        if (_game != null) _game.Changed -= OnGameChanged;
        if (_game != null) _game.Rewound -= OnRewound;
        _game = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Unhook(); _commitTimer.Dispose(); }
        base.Dispose(disposing);
    }

    // ---------- smoke ----------

    /// <summary>Smoke: sets the text as typing would and flushes; returns the resulting state notes.</summary>
    public string? SetTextForSmoke(string text)
    {
        _box.Text = text;
        Flush();
        return _s.Game?.State.Notes;
    }

    /// <summary>Smoke: puts text in the box without committing (as typing does before the 1 s pause).</summary>
    public void TypeForSmoke(string text) => _box.Text = text;

    public void SaveScreenshot(string path)
    {
        Update();
        using var bmp = new Bitmap(Math.Max(Width, 1), Math.Max(Height, 1));
        DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Program.AppLog("smoke: notes screenshot saved to " + path);
    }
}
