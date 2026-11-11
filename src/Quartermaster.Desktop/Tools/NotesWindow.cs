using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tools;

/// <summary>
/// The game's free-text notes (WinForms NotesWindow). Typing commits SetNotes after a pause (1 s), on leaving the box and on close;
/// <see cref="FlushNow"/> commits at once (the main window calls it before the game is replaced or closed).
/// </summary>
public sealed class NotesWindow : Window
{
    /// <summary>Consolas where installed, else the platform's monospace face (WinForms 9 pt = 12 px).</summary>
    static readonly FontFamily Mono = new("Consolas, Lucida Console, Menlo, DejaVu Sans Mono, monospace");

    readonly ModuleSession _s;
    readonly Func<IActionPort?>? _actions;
    readonly TextBox _box = new()
    {
        Name = "NotesBox", AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap, FontFamily = Mono, FontSize = 12,
    };
    readonly DispatcherTimer _commitTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    GameSession? _game;
    bool _closing;
    bool _loading;

    internal NotesWindow(ModuleSession s, Func<IActionPort?>? actions = null)
    {
        _s = s;
        _actions = actions;
        Title = "Notes";
        Width = 420;
        Height = 360;
        ShowInTaskbar = false;
        Content = _box;
        _box.OnTextSet(() => { if (!_loading) { _commitTimer.Stop(); _commitTimer.Start(); } });
        _box.LostFocus += (_, _) => Run(Flush);
        _commitTimer.Tick += (_, _) => Run(Flush);
        s.GameReplaced += OnGameReplaced;
        HookGame();
        Reload();
    }

    bool Pending => _commitTimer.IsEnabled;

    void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("notes window failed: " + ex);
            _s.Log.Add(DiagnosticSeverity.Error, "notes", "The notes window failed: " + ex.Message);
        }
    }

    void OnUi(Action a) => Dispatcher.UIThread.Post(() => { if (!_closing) a(); });

    void HookGame()
    {
        if (_game != null) { _game.Changed -= OnGameChanged; _game.Rewound -= OnRewound; }
        _game = _s.Game;
        if (_game != null) { _game.Changed += OnGameChanged; _game.Rewound += OnRewound; }
    }

    void OnGameChanged(GameCommand _) => OnUi(() => { if (NotesSync.ShouldReload(BoxFocused, Pending)) Run(Reload); });
    void OnRewound() => OnUi(() => { if (NotesSync.ShouldReload(BoxFocused, Pending)) Run(Reload); });
    // Pending text was already flushed into the old game by the main window (FlushNow) before the replacement; this only reloads.
    void OnGameReplaced() => OnUi(() => { _commitTimer.Stop(); HookGame(); Run(Reload); });

    /// <summary>Writes the box text to the state when it differs from it. Never commits an unchanged text.</summary>
    void Flush()
    {
        _commitTimer.Stop();
        // Only into the session's current game: a stale or disposed one drops the text silently.
        if (_game == null || _s.Disposed || !ReferenceEquals(_game, _s.Game)) return;
        var text = _box.Text ?? "";
        if (!NotesSync.ShouldCommit(text, _game.State.Notes)) return;
        if (_actions?.Invoke() is { } port) port.SetNotes(text);
        else _game.Execute(CommandLog.Stamp(new SetNotes(text)));
    }

    /// <summary>Commits pending text now.</summary>
    public void FlushNow() => Run(Flush);

    void Reload()
    {
        var text = _s.Game?.State.Notes ?? "";
        if ((_box.Text ?? "") == text) return;
        _loading = true;
        try { _box.Text = text; _box.CaretIndex = text.Length; }
        finally { _loading = false; }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Run(Flush);
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        _commitTimer.Stop();
        _s.GameReplaced -= OnGameReplaced;
        if (_game != null) { _game.Changed -= OnGameChanged; _game.Rewound -= OnRewound; }
        _game = null;
        base.OnClosed(e);
    }

    // ---------- what tests read and drive ----------

    /// <summary>Forces the focus state the reload decision sees (headless focus is unreliable); null = the box's real state.</summary>
    internal bool? FocusedForTest;
    bool BoxFocused => FocusedForTest ?? _box.IsFocused;

    internal TextBox Box => _box;
    internal bool CommitPending => Pending;
    internal TimeSpan CommitDelay { get => _commitTimer.Interval; set => _commitTimer.Interval = value; }

    /// <summary>Types text as the user would (the commit timer starts, nothing is committed yet).</summary>
    internal void Type(string text) => _box.Text = text;
}
