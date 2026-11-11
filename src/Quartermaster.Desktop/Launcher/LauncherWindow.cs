using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Launcher;

/// <summary>
/// The first window (WinForms LauncherForm): the module list with Add/Remove, the module's rules pack, Online lobby (stage 5c) and Play offline.
/// <see cref="Result"/> is what to play once Play offline chose something; it completes with null when the window is closed otherwise.
/// </summary>
public sealed class LauncherWindow : Window
{
    const string Caption = "Quartermaster";
    static readonly double[] Widths = { 200, 70, 240, 120 };
    static readonly string[] Headers = { "Module", "Version", "File", "Last played" };
    const int StarColumn = 2;

    readonly TaskCompletionSource<LaunchRequest?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly CancellationTokenSource _cts = new();
    readonly RecentGames _recent;
    readonly string? _justPlayed;
    readonly IUpdateSlot? _updates;
    readonly TextBlock _rules = new() { Text = LauncherModel.RulesLabel(null), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _progress = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    IReadOnlyList<ModuleRow> _rows = Array.Empty<ModuleRow>();
    IReadOnlyList<IReadOnlyList<string>> _cells = Array.Empty<IReadOnlyList<string>>();
    Task _refresh = Task.CompletedTask;
    bool _closing, _addedDuringRefresh;

    /// <summary>What to play (Play offline's answer), or null when the launcher was closed.</summary>
    public Task<LaunchRequest?> Result => _result.Task;

    /// <summary>The module check started when the window opened (and after Add module).</summary>
    public Task RefreshTask => _refresh;

    /// <summary>"Quartermaster {version}" under the buttons.</summary>
    public string FooterText { get; } = string.Format(System.Globalization.CultureInfo.InvariantCulture, LauncherModel.FooterFormat, AppVersion.Text);

    internal ModuleLibrary Library { get; }
    internal SettingsStore Store { get; }
    internal IDialogs Dialogs { get; }

    /// <summary>The module list: rows show Module / Version / File / Last played.</summary>
    internal ListBox Modules { get; } = new() { SelectionMode = SelectionMode.Single };

    /// <summary>The strip above the list where the update bar goes (task 5).</summary>
    internal Panel UpdateBarHost { get; } = new StackPanel();

    /// <summary>Links beside the footer's version text (task 5: "Check for updates").</summary>
    internal Panel FooterLinks { get; } = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };

    internal Button AddButton { get; } = new() { Content = "Add module…", MinWidth = 90 };
    internal Button RemoveButton { get; } = new() { Content = "Remove", MinWidth = 75 };
    internal Button OnlineButton { get; } = new() { Content = "Online lobby ▶", MinWidth = 110, IsEnabled = false };
    internal Button PlayButton { get; } = new() { Content = "Play offline ▶", MinWidth = 110, IsDefault = true };
    internal Button ChangeLink { get; } = Link("change…");
    internal Button NoneLink { get; } = Link("use (none)");

    /// <summary>Where module caches go when a module is opened for New game (the default cache folder of this layout root).</summary>
    internal string CacheRoot { get; set; }

    /// <summary>The Play offline window while it is open.</summary>
    internal PlayOfflineWindow? Offline { get; private set; }

    internal IReadOnlyList<string> ColumnHeaders => Headers;
    internal IReadOnlyList<IReadOnlyList<string>> Cells => _cells;
    internal string RulesText => _rules.Text ?? "";
    internal string ProgressText => _progress.Text ?? "";
    internal ModuleRow? SelectedRow => Modules.SelectedIndex is var i && i >= 0 && i < _rows.Count ? _rows[i] : null;

    /// <param name="layoutRoot">The app data folder: autosaves (Continue), the module cache.</param>
    /// <param name="justPlayed">After Back to launcher: the module just played, selected regardless of "Select the last module in the launcher".</param>
    /// <param name="updates">Attaches the update screens once the window is open (null: none).</param>
    public LauncherWindow(SettingsStore store, string layoutRoot, IDialogs dialogs, string? justPlayed, IUpdateSlot? updates = null)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        Library = new ModuleLibrary(store);
        _recent = new RecentGames(store, layoutRoot);
        CacheRoot = Path.Combine(layoutRoot, "cache");
        _updates = updates;
        try { _justPlayed = justPlayed == null ? null : Path.GetFullPath(justPlayed); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { _justPlayed = justPlayed; }

        Title = Caption;
        Width = 680;
        Height = 400;
        MinWidth = 560;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AddLinkStyle(this);

        ToolTip.SetTip(OnlineButton, "Online play comes with stage 5c.");
        ToolTip.SetShowOnDisabled(OnlineButton, true);
        ToolTip.SetShowOnDisabled(PlayButton, true);
        ToolTip.SetTip(NoneLink, "Play this module without a rules pack.");
        NoneLink.IsVisible = false;
        _progress.Bind(TextBlock.ForegroundProperty, _progress.GetResourceObservable("Classic.GrayTextBrush"));

        var rulesRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 6, 8, 0) };
        rulesRow.Children.Add(_rules);
        rulesRow.Children.Add(ChangeLink);
        rulesRow.Children.Add(NoneLink);
        rulesRow.Children.Add(_progress);

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        left.Children.Add(AddButton);
        left.Children.Add(RemoveButton);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(OnlineButton);
        right.Children.Add(PlayButton);
        var buttons = new DockPanel { Margin = new Thickness(8, 6) };
        DockPanel.SetDock(left, Dock.Left);
        buttons.Children.Add(left);
        buttons.Children.Add(right);

        var version = new TextBlock { Text = FooterText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        version.Bind(TextBlock.ForegroundProperty, version.GetResourceObservable("Classic.GrayTextBrush"));
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 4) };
        footer.Children.Add(version);
        footer.Children.Add(FooterLinks);

        var bottom = new StackPanel();
        bottom.Children.Add(rulesRow);
        bottom.Children.Add(buttons);
        bottom.Children.Add(footer);

        var header = ColumnHeader(Widths, Headers, StarColumn);
        var listHost = new DockPanel { Margin = new Thickness(8, 8, 8, 0) };
        DockPanel.SetDock(header, Dock.Top);
        listHost.Children.Add(header);
        listHost.Children.Add(Modules);

        var root = new DockPanel();
        DockPanel.SetDock(UpdateBarHost, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(UpdateBarHost);
        root.Children.Add(bottom);
        root.Children.Add(listHost);
        Content = root;

        AddButton.Click += async (_, _) => await Guarded(AddModulesAsync);
        RemoveButton.Click += async (_, _) => await Guarded(() => { RemoveSelected(); return Task.CompletedTask; });
        PlayButton.Click += async (_, _) => await Guarded(PlayOfflineAsync);
        ChangeLink.Click += async (_, _) => await Guarded(ChangeRulesAsync);
        NoneLink.Click += async (_, _) => await Guarded(() => { ClearRules(); return Task.CompletedTask; });
        Modules.SelectionChanged += (_, _) => UpdateButtons();
        Modules.DoubleTapped += async (_, _) => await Guarded(PlayOfflineAsync);
        Modules.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Delete) return;
            e.Handled = true;
            await Guarded(() => { RemoveSelected(); return Task.CompletedTask; });
        };
        Opened += (_, _) =>
        {
            Fill(preselectLast: true);
            StartRefresh();
            UpdateButtons();
            _updates?.Attach(this);
        };
        Closed += (_, _) =>
        {
            _closing = true;
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            _result.TrySetResult(null);
        };
    }

    /// <summary>Runs a handler so that nothing it throws reaches the dispatcher: it is traced and shown as a warning.</summary>
    async Task Guarded(Func<Task> a)
    {
        try { await a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("launcher: " + ex.Message);
            if (!_closing) await Dialogs.ShowWarningAsync(this, ex.Message, Caption);
        }
    }

    // ---------- the module check ----------

    void StartRefresh()
    {
        if (_closing) return;
        var total = Library.Rows.Count;
        if (total == 0) return;
        var done = 0;
        _progress.Text = "Checking modules…";
        var progress = new UiProgress(() =>
        {
            if (_closing) return;
            done++;
            _progress.Text = $"Checking modules… {done} of {total}";
        });
        _refresh = RefreshAsyncSafe(progress);
    }

    async Task RefreshAsyncSafe(IProgress<ModuleRow> progress)
    {
        try { await Library.RefreshAsync(progress, _cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("launcher: refresh failed: " + ex.Message); }
        if (_closing) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_closing) return;
            _progress.Text = "";
            Fill(preselectLast: false);
            // Only re-run for modules added since this refresh started (an unhashable file never loops).
            if (!_addedDuringRefresh) return;
            _addedDuringRefresh = false;
            StartRefresh();
            UpdateButtons();
        });
    }

    /// <summary>Reports each checked module on the UI thread.</summary>
    sealed class UiProgress(Action step) : IProgress<ModuleRow>
    {
        public void Report(ModuleRow value) => Dispatcher.UIThread.Post(step);
    }

    // ---------- the list ----------

    /// <summary>Rebuilds the list from the library, keeping the selection (or choosing the initial row when asked).</summary>
    void Fill(bool preselectLast)
    {
        var keep = SelectedRow?.Entry.Path;
        _rows = Library.Rows;
        _cells = _rows.Select(r => (IReadOnlyList<string>)new[]
        {
            LauncherModel.TitleText(r), r.Entry.Version, LauncherModel.FileText(r), LauncherModel.LastPlayedText(r.Entry),
        }).ToList();
        Modules.ItemsSource = _cells.Select((c, i) => RowControl(Widths, c, StarColumn, _rows[i].Missing)).ToList();
        var index = keep != null ? IndexOf(keep) : preselectLast ? LauncherModel.InitialSelection(_rows, _justPlayed, Store.Current) : -1;
        Modules.SelectedIndex = index;
        UpdateButtons();
    }

    int IndexOf(string path)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (string.Equals(_rows[i].Entry.Path, path, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    void UpdateButtons()
    {
        var row = SelectedRow;
        var hashReady = row != null && (!string.IsNullOrEmpty(row.Entry.Sha256) || _refresh.IsCompleted);
        var b = LauncherModel.Buttons(row, hashReady);
        PlayButton.IsEnabled = b.PlayOffline;
        RemoveButton.IsEnabled = b.Remove;
        ToolTip.SetTip(PlayButton, b.PlayOfflineTip);
        ChangeLink.IsEnabled = row is { Missing: false };
        var pack = row == null ? null : Library.RulesPackFor(row.Entry);
        _rules.Text = LauncherModel.RulesLabel(pack);
        NoneLink.IsVisible = row is { Missing: false } && !string.IsNullOrEmpty(pack);
    }

    async Task AddModulesAsync()
    {
        var files = await Dialogs.OpenFilesAsync(this, "Add module",
            new[] { new FileFilter("VASSAL modules (*.vmod)", "*.vmod"), new FileFilter("All files (*.*)", "*.*") });
        string? last = null;
        foreach (var f in files)
        {
            try { Library.Add(f); last = Path.GetFullPath(f); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine($"launcher: could not add {f}: {ex.Message}"); }
        }
        if (_closing) return;
        Fill(preselectLast: false);
        if (last == null) return;
        Modules.SelectedIndex = IndexOf(last);
        if (_refresh.IsCompleted) StartRefresh(); // hash what was just added
        else _addedDuringRefresh = true;          // the running refresh took its snapshot before: run again when it ends
        UpdateButtons();
    }

    void RemoveSelected()
    {
        if (SelectedRow is not { } row) return;
        Library.Remove(row.Entry.Path);
        Fill(preselectLast: false);
    }

    async Task ChangeRulesAsync()
    {
        if (SelectedRow is not { Missing: false } row) return;
        if (row.Entry.Sha256 is not { Length: > 0 })
        {
            await Dialogs.ShowInfoAsync(this, LauncherModel.NotReadyText, Caption);
            return;
        }
        var path = await Dialogs.OpenFileAsync(this, "Rules pack for " + row.Entry.Title,
            new[] { new FileFilter("Rules packs (*.qrules)", "*.qrules"), new FileFilter("All files (*.*)", "*.*") });
        if (path == null || _closing) return;
        Library.SetRulesPack(row.Entry, path);
        UpdateButtons();
    }

    /// <summary>The "use (none)" link: forget the module's rules pack so it plays without one.</summary>
    void ClearRules()
    {
        if (SelectedRow is not { } row) return;
        Library.SetRulesPack(row.Entry, null);
        UpdateButtons();
    }

    /// <summary>Play offline ▶ (and a double-click): the Play offline window for the selected module; its choice completes <see cref="Result"/>
    /// and closes the launcher, Back returns here.</summary>
    internal async Task PlayOfflineAsync()
    {
        if (Offline != null || _closing || SelectedRow is not { } row) return;
        if (row.Missing)
        {
            await Dialogs.ShowInfoAsync(this, LauncherModel.MissingModuleText, Caption);
            return;
        }
        if (row.Entry.Sha256 is not { Length: > 0 } && !_refresh.IsCompleted)
        {
            await Dialogs.ShowInfoAsync(this, LauncherModel.NotReadyText, Caption);
            return;
        }
        var w = new PlayOfflineWindow(row, Library, _recent, Store, Dialogs, CacheRoot);
        Offline = w;
        LaunchRequest? req;
        try
        {
            _ = w.ShowDialog(this);
            req = await w.Result;
        }
        finally { Offline = null; }
        if (req == null || _closing) return;
        _result.TrySetResult(req);
        Close();
    }

    /// <summary>Completes <see cref="Result"/> with <paramref name="request"/> and closes, as a Play offline choice does (tests drive the loop with it).</summary>
    internal void Choose(LaunchRequest request)
    {
        _result.TrySetResult(request);
        Close();
    }

    // ---------- shared look (also the Play offline window) ----------

    static readonly IBrush LinkBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x66, 0xCC));

    /// <summary>A WinForms LinkLabel: blue underlined text, no button face (see <see cref="AddLinkStyle"/>).</summary>
    internal static Button Link(string text)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, TextDecorations = TextDecorations.Underline },
            Foreground = LinkBrush, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center,
        };
        b.Classes.Add("link");
        return b;
    }

    /// <summary>Keeps link buttons flat on hover and press (the classic theme paints button faces there).</summary>
    internal static void AddLinkStyle(Window w)
    {
        var face = new Style(x => x.OfType<Button>().Class("link").Template().OfType<ContentPresenter>());
        face.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent));
        face.Setters.Add(new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent));
        w.Styles.Add(face);
    }

    internal static Grid ColumnHeader(double[] widths, IReadOnlyList<string> titles, int star)
    {
        var g = new Grid { ColumnDefinitions = Columns(widths, star), Margin = new Thickness(1, 0) };
        for (var i = 0; i < titles.Count; i++) g.Children.Add(Cell(titles[i], i, bold: true));
        return g;
    }

    internal static Grid RowControl(double[] widths, IReadOnlyList<string> cells, int star, bool grey)
    {
        var g = new Grid { ColumnDefinitions = Columns(widths, star) };
        for (var i = 0; i < cells.Count; i++)
        {
            var t = Cell(cells[i], i);
            if (grey) t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("Classic.GrayTextBrush"));
            g.Children.Add(t);
        }
        return g;
    }

    static ColumnDefinitions Columns(double[] widths, int star)
    {
        var cols = new ColumnDefinitions();
        for (var i = 0; i < widths.Length; i++)
            cols.Add(i == star ? new ColumnDefinition(1, GridUnitType.Star) { MinWidth = widths[i] / 2 } : new ColumnDefinition(widths[i], GridUnitType.Pixel));
        return cols;
    }

    static TextBlock Cell(string text, int column, bool bold = false)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(4, 1), TextTrimming = TextTrimming.CharacterEllipsis };
        if (bold) t.FontWeight = FontWeight.Bold;
        Grid.SetColumn(t, column);
        return t;
    }
}
