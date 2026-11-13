using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Launcher;

/// <summary>
/// "Play offline" for one module (WinForms PlayOfflineForm): Continue (newest autosave), recent saves, New game…, Open saved game… and
/// Open turn file…. <see cref="Result"/> is the launch request, or null for Back (or the window closed).
/// </summary>
public sealed class PlayOfflineWindow : Window
{
    const string Caption = "Quartermaster";
    static readonly double[] Widths = { 330, 140 };
    static readonly string[] Headers = { "Recent games", "Saved" };

    readonly TaskCompletionSource<LaunchRequest?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ModuleRow _row;
    readonly ModuleLibrary _library;
    readonly SettingsStore _store;
    readonly IDialogs _dialogs;
    readonly string _cacheRoot;
    readonly IReadOnlyList<RecentSave> _saves;
    readonly ContinueInfo? _continue;
    readonly TextBlock _continueLine = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly IReadOnlyList<IReadOnlyList<string>> _cells;
    bool _busy, _finishing;

    /// <summary>The launch request, or null for Back.</summary>
    public Task<LaunchRequest?> Result => _result.Task;

    internal Button ContinueButton { get; } = new() { Content = "Continue", MinWidth = 100 };
    internal Button NewGameButton { get; } = new() { Content = "New game…", MinWidth = 100 };
    internal Button OpenSavedButton { get; } = new() { Content = "Open saved game…", MinWidth = 120 };
    internal Button OpenTurnButton { get; } = new() { Content = "Open turn file…", MinWidth = 110 };
    internal Button OpenSelectedButton { get; } = new() { Content = "Open selected", MinWidth = 100, IsEnabled = false };
    internal Button BackButton { get; } = new() { Content = "Back", MinWidth = 75, IsCancel = true };
    internal ListBox Recent { get; } = new() { SelectionMode = SelectionMode.Single };
    internal string ContinueText => _continueLine.Text ?? "";
    internal IReadOnlyList<string> ColumnHeaders => Headers;
    internal IReadOnlyList<IReadOnlyList<string>> Cells => _cells;

    RecentSave? SelectedSave => Recent.SelectedIndex is var i && i >= 0 && i < _saves.Count ? _saves[i] : null;

    /// <param name="cacheRoot">Where the module cache goes when New game opens the module.</param>
    public PlayOfflineWindow(ModuleRow row, ModuleLibrary library, RecentGames recent, SettingsStore store, IDialogs dialogs, string cacheRoot)
    {
        _row = row;
        _library = library;
        _store = store;
        _dialogs = dialogs;
        _cacheRoot = cacheRoot;
        var sha = row.Entry.Sha256;
        _saves = Safe(() => recent.For(sha), Array.Empty<RecentSave>());
        _continue = string.IsNullOrEmpty(sha) ? null : Safe(() => recent.Continue(sha), null);

        Title = PlayOfflineModel.Title(row);
        Width = 520;
        Height = 360;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _continueLine.Text = PlayOfflineModel.ContinueText(_continue);
        ContinueButton.IsEnabled = _continue != null;
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 8, 8, 4) };
        top.Children.Add(ContinueButton);
        top.Children.Add(_continueLine);

        _cells = _saves.Select(s =>
        {
            var name = Safe(() => Path.GetFileName(s.Path), s.Path);
            return (IReadOnlyList<string>)new[]
            {
                s.Missing ? $"{name} {LauncherModel.NotFound}" : name,
                s.Missing ? "" : s.LastWrite.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
            };
        }).ToList();
        Recent.ItemsSource = _cells.Select((c, i) => LauncherWindow.RowControl(Widths, c, 0, _saves[i].Missing)).ToList();
        var header = LauncherWindow.ColumnHeader(Widths, Headers, 0);
        var listHost = new DockPanel { Margin = new Thickness(8, 4) };
        DockPanel.SetDock(header, Dock.Top);
        listHost.Children.Add(header);
        listHost.Children.Add(Recent);

        var buttons = new WrapPanel { Margin = new Thickness(8), ItemSpacing = 6, LineSpacing = 6 };
        buttons.Children.Add(NewGameButton);
        buttons.Children.Add(OpenSavedButton);
        buttons.Children.Add(OpenTurnButton);
        buttons.Children.Add(OpenSelectedButton);
        buttons.Children.Add(BackButton);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(buttons);
        root.Children.Add(listHost);
        Content = root;

        ContinueButton.Click += async (_, _) => await Guarded(() => { if (_continue != null) Finish(LaunchKind.Continue, null, _continue.Path); return Task.CompletedTask; });
        NewGameButton.Click += async (_, _) => await Guarded(NewGameAsync);
        OpenSavedButton.Click += async (_, _) => await Guarded(OpenDialogAsync);
        OpenTurnButton.Click += async (_, _) => await Guarded(OpenTurnDialogAsync);
        OpenSelectedButton.Click += async (_, _) => await Guarded(OpenSelectedAsync);
        BackButton.Click += (_, _) => Close();
        Recent.SelectionChanged += (_, _) => OpenSelectedButton.IsEnabled = !_busy && SelectedSave is { Missing: false };
        Recent.DoubleTapped += async (_, _) => await Guarded(OpenSelectedAsync);
        Recent.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await Guarded(OpenSelectedAsync);
        };
        Closed += (_, _) => _result.TrySetResult(null);
    }

    static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch (Exception ex) when (ex is not OutOfMemoryException) { return fallback; }
    }

    async Task Guarded(Func<Task> a)
    {
        try { await a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("launcher: " + ex.Message);
            if (!_finishing) await _dialogs.ShowWarningAsync(this, ex.Message, Caption);
        }
    }

    /// <summary>Closing is refused while the module loads or the setup chooser is open (the choice would be lost).</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_busy && !_finishing) e.Cancel = true;
        base.OnClosing(e);
    }

    void Finish(LaunchKind kind, NewGameRequest? ng, string? savePath)
    {
        var rules = Safe(() => _library.RulesPackFor(_row.Entry), null);
        _finishing = true;
        _result.TrySetResult(PlayOfflineModel.Finish(_row, rules, kind, ng, savePath));
        Close();
    }

    /// <summary>Open selected (and a double-click or Enter on the list): a missing save says so.</summary>
    internal async Task OpenSelectedAsync()
    {
        if (_busy || SelectedSave is not { } s) return;
        if (s.Missing)
        {
            await _dialogs.ShowInfoAsync(this, PlayOfflineModel.SaveMissingText, Caption);
            return;
        }
        Finish(LaunchKind.OpenSave, null, s.Path);
    }

    async Task OpenDialogAsync()
    {
        string? start = null;
        if (_saves.FirstOrDefault(s => !s.Missing) is { } newest)
        {
            var dir = Safe(() => Path.GetDirectoryName(newest.Path), null);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) start = dir;
        }
        var path = await _dialogs.OpenFileAsync(this, "Open saved game",
            new[] { new FileFilter("Quartermaster saves (*.qsave)", "*.qsave"), new FileFilter("All files (*.*)", "*.*") }, start);
        if (path != null) Finish(LaunchKind.OpenSave, null, path);
    }

    /// <summary>A received turn: the game window opens the module, asks who you are and replays it (launcher spec 3.2).</summary>
    async Task OpenTurnDialogAsync()
    {
        if (_busy) return;
        var path = await _dialogs.OpenFileAsync(this, "Open turn file",
            new[] { new FileFilter("Quartermaster turns (*.qturn)", "*.qturn"), new FileFilter("All files (*.*)", "*.*") });
        if (path != null) Finish(LaunchKind.OpenTurn, null, path);
    }

    /// <summary>Disables the actions while the module loads or the setup chooser is open (no second click can start another run).</summary>
    void SetBusy(bool busy)
    {
        _busy = busy;
        NewGameButton.IsEnabled = OpenSavedButton.IsEnabled = OpenTurnButton.IsEnabled = BackButton.IsEnabled = !busy;
        ContinueButton.IsEnabled = !busy && _continue != null;
        OpenSelectedButton.IsEnabled = !busy && SelectedSave is { Missing: false };
        Cursor = busy ? new Cursor(StandardCursorType.Wait) : null;
    }

    async Task NewGameAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try { await NewGameCoreAsync(); }
        finally { if (!_finishing) SetBusy(false); }
    }

    /// <summary>Opens the module on a worker (with its remembered rules pack), then the setup chooser; its choice is the new game.</summary>
    async Task NewGameCoreAsync()
    {
        var rules = Safe(() => _library.RulesPackFor(_row.Entry), null);
        var path = _row.Entry.Path;
        var cache = _cacheRoot;
        var rulesRoot = _store.Root;
        ModuleSession session;
        try
        {
            session = await Task.Run(() =>
            {
                var s = ModuleSession.Open(path, cache, rulesRoot);
                try
                {
                    if (!string.IsNullOrEmpty(rules) && !string.Equals(s.RulesPath, rules, StringComparison.OrdinalIgnoreCase)
                        && (File.Exists(rules) || Directory.Exists(rules)))
                    {
                        try { s.OpenRules(rules); }
                        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("launcher: rules pack not loaded: " + ex.Message); }
                    }
                    return s;
                }
                catch { s.Dispose(); throw; }
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Cursor = null;
            await _dialogs.ShowWarningAsync(this, $"Could not open '{path}':\n{ex.Message}", Caption);
            return;
        }
        using (session)
        {
            if (!IsVisible) return;
            Cursor = null;
            var choice = await _dialogs.ChooseSetupAsync(this, session.Model, preselectNew: false, _store.Current.LastDifficulty, session.Rules);
            if (choice == null) return;
            _store.Update(cur => cur with { LastDifficulty = choice.Difficulty });
            var players = choice.Players is { Count: >= 2 } p ? p : null;
            Finish(LaunchKind.NewGame, new NewGameRequest(GameMode.Offline, choice.Setup, choice.Difficulty, players, null, null), null);
        }
    }
}
