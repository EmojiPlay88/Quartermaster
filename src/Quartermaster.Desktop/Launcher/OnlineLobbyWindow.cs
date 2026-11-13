using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Online;
using Quartermaster.Desktop.Shell;
using Quartermaster.Net;
using Quartermaster.Render;
using Quartermaster.Rules;
using Shapes = Avalonia.Controls.Shapes;

namespace Quartermaster.Desktop.Launcher;

/// <summary>
/// The launcher's Online lobby for one module (WinForms OnlineLobbyForm; launcher spec 3.3, 4a): the relay address (remembered) with its
/// connection state, the other people's lobbies for this module (refreshed every 10 s), and Join…, Create lobby…, Host a saved game…,
/// Refresh. <see cref="Result"/> is what to play, or null when the window closed without a choice. A join is completed here (password
/// checked by the relay) and the joined relay client is handed to the game window; hosting connects from the game window once the game is
/// loaded. Network trouble only ever shows as text in the window. Every relay connection is made on a worker thread (the certificate
/// question blocks it while it is asked on the UI thread, see <see cref="RelayTrust.Ask"/>).
/// </summary>
public sealed class OnlineLobbyWindow : Window
{
    public const string JoinText = "Join…", CreateText = "Create lobby…", HostSavedText = "Host a saved game…", RefreshText = "Refresh";
    const string EnterAddressText = "Enter the relay address and press Refresh.";
    static readonly TimeSpan AutoRefresh = TimeSpan.FromSeconds(10);
    static readonly double[] Widths = { 190, 120, 180, 70 };
    static readonly string[] Headers = { "Lobby", "Host", "Free sides", "Players" };
    const int StarColumn = 2;

    readonly TaskCompletionSource<LaunchRequest?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ModuleRow _row;
    readonly ModuleLibrary _library;
    readonly SettingsStore _store;
    readonly IDialogs _dialogs;
    readonly TextBlock _state = new() { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 3, 0) };
    readonly DispatcherTimer _timer = new() { Interval = AutoRefresh };
    readonly List<Grid> _rowControls = new();
    RelayClient? _browser;
    Action<string>? _browserDown;
    Uri? _browserUri;
    string? _lastTrouble;
    bool _busy, _closed, _lastOk, _joining, _finishing, _closeAfterJoin;
    IReadOnlyList<LobbyRow> _rows = Array.Empty<LobbyRow>();

    /// <summary>What to play (a join or a hosting request), or null when the window was closed without one.</summary>
    public Task<LaunchRequest?> Result => _result.Task;

    internal TextBox RelayBox { get; } = new() { Width = 260 };
    internal ListBox List { get; } = new() { SelectionMode = SelectionMode.Single };
    internal Button JoinButton { get; } = new() { Content = JoinText, MinWidth = 80 };
    internal Button CreateButton { get; } = new() { Content = CreateText, MinWidth = 100 };
    internal Button HostSavedButton { get; } = new() { Content = HostSavedText, MinWidth = 120 };
    internal Button RefreshButton { get; } = new() { Content = RefreshText, MinWidth = 75 };
    internal Button CloseButton { get; } = new() { Content = "Close", MinWidth = 75, IsCancel = true };

    /// <summary>The lobbies shown (this module's, not our own).</summary>
    internal IReadOnlyList<LobbyRow> Rows => _rows;

    /// <summary>The connection state shown next to the relay field.</summary>
    internal string StateText => _state.Text ?? "";

    internal IReadOnlyList<string> ColumnHeaders => Headers;

    /// <summary>True while the window holds a lobby-browsing relay connection.</summary>
    internal bool HasBrowser => _browser != null;

    /// <summary>The relay client of the last join attempt (tests check it was closed when the join did not hand it on).</summary>
    internal RelayClient? LastJoinClient { get; private set; }

    internal bool RowLocked(int i) => _rowControls[i].Children.OfType<StackPanel>().First().Children[0].IsVisible;

    internal bool RowGreyed(int i) => _rowControls[i].Classes.Contains("grey");

    internal string? RowTip(int i) => ToolTip.GetTip(_rowControls[i]) as string;

    LobbyRow? Selected => List.SelectedIndex is var i && i >= 0 && i < _rows.Count ? _rows[i] : null;

    public OnlineLobbyWindow(ModuleRow row, ModuleLibrary library, SettingsStore store, IDialogs dialogs)
    {
        _row = row ?? throw new ArgumentNullException(nameof(row));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        var title = string.IsNullOrWhiteSpace(row.Entry.Title) ? Path.GetFileNameWithoutExtension(row.Entry.Path) : row.Entry.Title;
        Title = OnlineLobbyModel.Title(title);
        Width = 640;
        Height = 380;
        MinWidth = 520;
        MinHeight = 300;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        OnlineUi.Face(this);
        _state.Bind(TextBlock.ForegroundProperty, _state.GetResourceObservable("Classic.GrayTextBrush"));

        RelayBox.Text = store.Current.RelayAddress ?? "";
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 8, 8, 4) };
        top.Children.Add(OnlineUi.Label("Relay:", new Thickness(3, 3, 6, 3)));
        top.Children.Add(RelayBox);
        top.Children.Add(_state);

        var header = LauncherWindow.ColumnHeader(Widths, Headers, StarColumn);
        var listHost = new DockPanel { Margin = new Thickness(8, 4, 8, 0) };
        DockPanel.SetDock(header, Dock.Top);
        listHost.Children.Add(header);
        listHost.Children.Add(List);

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        left.Children.Add(JoinButton);
        left.Children.Add(CreateButton);
        left.Children.Add(HostSavedButton);
        left.Children.Add(RefreshButton);
        var buttons = new DockPanel { Margin = new Thickness(8, 6) };
        DockPanel.SetDock(left, Dock.Left);
        CloseButton.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Children.Add(left);
        buttons.Children.Add(CloseButton);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(buttons);
        root.Children.Add(listHost);
        Content = root;

        JoinButton.Click += async (_, _) => await GuardedAsync(JoinSelectedAsync);
        List.DoubleTapped += async (_, _) => await GuardedAsync(JoinSelectedAsync);
        CreateButton.Click += async (_, _) => await GuardedAsync(CreateLobbyAsync);
        HostSavedButton.Click += async (_, _) => await GuardedAsync(HostSavedAsync);
        RefreshButton.Click += async (_, _) => await GuardedAsync(() => RefreshAsync(manual: true));
        CloseButton.Click += (_, _) => Close();
        RelayBox.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await GuardedAsync(() => RefreshAsync(manual: true));
        }, RoutingStrategies.Tunnel);
        List.SelectionChanged += (_, _) => UpdateButtons();
        _timer.Tick += async (_, _) =>
        {
            if (_lastOk && !_busy && IsVisible) await GuardedAsync(() => RefreshAsync(manual: false));
        };
        Opened += async (_, _) =>
        {
            _timer.Start();
            UpdateButtons();
            if ((RelayBox.Text ?? "").Trim().Length > 0) await GuardedAsync(() => RefreshAsync(manual: true));
            else SetState(EnterAddressText);
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            DropBrowser();
            _result.TrySetResult(null);
        };
    }

    /// <summary>A join in flight holds a relay connection that only this window can hand over or close: closing waits for it, then the
    /// window closes and the connection is closed with it.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_joining && !_finishing)
        {
            e.Cancel = true;
            _closeAfterJoin = true;
        }
        base.OnClosing(e);
    }

    // ---------- helpers ----------

    async Task GuardedAsync(Func<Task> a)
    {
        try { await a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { await Problem(ex.Message); }
    }

    /// <summary>A warning with the window's title (traced too); never throws.</summary>
    async Task Problem(string message)
    {
        System.Diagnostics.Trace.WriteLine("online lobby: " + message);
        if (_closed) return;
        try { await _dialogs.ShowWarningAsync(this, message, Title ?? ""); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("online lobby: warning not shown: " + ex.Message); }
    }

    /// <summary>Runs <paramref name="a"/> on the UI thread unless the window has closed by then; nothing it throws escapes.</summary>
    void PostToUi(Action a)
    {
        if (_closed) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            try { a(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("online lobby: " + ex.Message); }
        });
    }

    void SetState(string text)
    {
        if (_closed) return;
        _state.Text = text;
        ToolTip.SetTip(_state, text);
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        if (_closed) return;
        Cursor = busy ? new Cursor(StandardCursorType.Wait) : null;
        UpdateButtons();
    }

    void UpdateButtons()
    {
        if (_closed) return;
        var sel = Selected;
        JoinButton.IsEnabled = !_busy && sel != null && sel.CannotJoin == null;
        CreateButton.IsEnabled = HostSavedButton.IsEnabled = RefreshButton.IsEnabled = !_busy;
        RelayBox.IsEnabled = !_busy;
        CloseButton.IsEnabled = !_joining;
    }

    /// <summary>The relay address typed, or null; with <paramref name="showError"/> the problem is shown as the state and in a message.</summary>
    async Task<Uri?> RelayUri(bool showError)
    {
        var uri = OnlineLobbyModel.ParseRelay(RelayBox.Text, out var error);
        if (uri == null && showError)
        {
            var text = error ?? OnlineLobbyModel.BadAddressText;
            SetState(text);
            await _dialogs.ShowInfoAsync(this, text, Title ?? "");
        }
        return uri;
    }

    void DropBrowser()
    {
        var b = _browser;
        var down = _browserDown;
        _browser = null;
        _browserDown = null;
        _browserUri = null;
        if (b == null) return;
        if (down != null) b.Disconnected -= down;
        _ = b.DisposeAsync().AsTask().ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    }

    string DefaultName() => _store.Current.PlayerName is { Length: > 0 } n ? n : ChatText.CleanName(Environment.UserName) ?? "Player";

    void Finish(LaunchRequest req)
    {
        _finishing = true;
        _result.TrySetResult(req);
        Close();
    }

    // ---------- the table ----------

    /// <summary>Connects (again, when the address changed or the last try failed) and lists this module's lobbies.</summary>
    public async Task RefreshAsync(bool manual)
    {
        if (_busy || _closed) return;
        if (await RelayUri(manual) is not { } uri) return;
        if (_row.Entry.Sha256 is not { Length: > 0 } sha) { SetState(LauncherModel.NotReadyText); return; }
        SetBusy(true);
        try
        {
            if (_browser == null || _browserUri != uri || !_lastOk)
            {
                DropBrowser();
                SetState("Connecting…");
                _lastTrouble = null;
                var b = new RelayClient(RelayTrust.Pins());
                void Down(string reason)
                {
                    _lastTrouble = reason;
                    PostToUi(() => { if (ReferenceEquals(_browser, b)) SetState(OnlineLobbyModel.CleanText(reason, GuestSession.MaxHostText)); });
                }
                b.Disconnected += Down;
                _browser = b;
                _browserDown = Down;
                _browserUri = uri;
                await Task.Run(() => b.ConnectAsync(uri, c => RelayTrust.Ask(this, _dialogs, c), CancellationToken.None));
            }
            if (_closed || _browser is not { } client) return;
            var listings = await client.ListLobbiesAsync(sha);
            if (_closed) return;
            _lastOk = true;
            ShowRows(OnlineLobbyModel.Rows(listings, sha, OnlineLobbyModel.HostedCodes));
            SetState($"Connected to {OnlineLobbyModel.HostPort(uri)} — {_rows.Count} {(_rows.Count == 1 ? "lobby" : "lobbies")}");
            var typed = (RelayBox.Text ?? "").Trim();
            if (!string.Equals(_store.Current.RelayAddress, typed, StringComparison.Ordinal))
                _store.Update(s => s with { RelayAddress = typed });
        }
        catch (RelayException ex)
        {
            _lastOk = false;
            SetState(OnlineLobbyModel.CleanText(_lastTrouble ?? ex.Message, GuestSession.MaxHostText));   // relay text is untrusted
            DropBrowser();   // a failed connection is made afresh on the next refresh
        }
        finally { SetBusy(false); }
    }

    /// <summary>Screenshots (build/port-screenshots.ps1): shows invented rows as if the relay had listed them; no connection is made.</summary>
    internal void ShowRowsForScreenshot(IReadOnlyList<LobbyRow> rows)
    {
        RelayBox.Text = "relay.example.test:8443";
        ShowRows(rows);
        SetState($"Connected to relay.example.test:8443 — {rows.Count} {(rows.Count == 1 ? "lobby" : "lobbies")}");
    }

    void ShowRows(IReadOnlyList<LobbyRow> rows)
    {
        if (_closed) return;
        var keep = Selected?.Listing.Code;
        _rows = rows;
        _rowControls.Clear();
        foreach (var r in rows) _rowControls.Add(RowControl(r));
        List.ItemsSource = _rowControls.ToList();
        var index = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            if (keep != null && rows[i].Listing.Code == keep) { index = i; break; }
        }
        List.SelectedIndex = index;
        UpdateButtons();
    }

    /// <summary>One table row: a lock glyph before a locked lobby's name; a lobby that cannot be joined is grey with the reason as tooltip.</summary>
    Grid RowControl(LobbyRow r)
    {
        var cols = new ColumnDefinitions();
        for (var i = 0; i < Widths.Length; i++)
            cols.Add(i == StarColumn ? new ColumnDefinition(1, GridUnitType.Star) { MinWidth = Widths[i] / 2 } : new ColumnDefinition(Widths[i], GridUnitType.Pixel));
        var g = new Grid { ColumnDefinitions = cols };
        var cells = new[] { r.Lobby, r.Host, r.FreeSides, r.Players };
        for (var i = 0; i < cells.Length; i++)
        {
            var t = new TextBlock { Text = cells[i], Margin = new Thickness(4, 1), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            if (r.CannotJoin != null) t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("Classic.GrayTextBrush"));
            Control cell = t;
            if (i == 0)
            {
                var lockIcon = LockIcon();
                lockIcon.IsVisible = r.Locked;
                var p = new StackPanel { Orientation = Orientation.Horizontal };
                p.Children.Add(lockIcon);
                p.Children.Add(t);
                cell = p;
            }
            Grid.SetColumn(cell, i);
            g.Children.Add(cell);
        }
        if (r.CannotJoin != null)
        {
            g.Classes.Add("grey");
            ToolTip.SetTip(g, r.CannotJoin);
        }
        return g;
    }

    /// <summary>The WinForms lock icon: a grey shackle over a gold body, 16×16.</summary>
    static Control LockIcon()
    {
        var c = new Canvas { Width = 16, Height = 16, Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        c.Children.Add(new Shapes.Path
        {
            Data = Geometry.Parse("M 4.5,7.5 L 4.5,5.5 A 3.5,4 0 0 1 11.5,5.5 L 11.5,7.5"),
            Stroke = new SolidColorBrush(Color.FromRgb(90, 90, 90)), StrokeThickness = 1.6,
        });
        var body = new Shapes.Rectangle
        {
            Width = 10, Height = 8, Fill = new SolidColorBrush(Color.FromRgb(200, 150, 30)),
            Stroke = new SolidColorBrush(Color.FromRgb(120, 85, 10)), StrokeThickness = 1,
        };
        Canvas.SetLeft(body, 3);
        Canvas.SetTop(body, 7);
        c.Children.Add(body);
        return c;
    }

    // ---------- join ----------

    /// <summary>Join… (and a double-click): the Join dialog for the selected lobby, then the join; a join finishes the window.</summary>
    internal async Task JoinSelectedAsync()
    {
        if (_busy || _closed || Selected is not { } row) return;
        if (row.CannotJoin is { } why) { await Problem(why); return; }
        var choice = await _dialogs.AskJoinAsync(this, row, DefaultName());
        if (choice == null || _closed) return;
        var req = await JoinAsync(row.Listing, choice.PlayerName, choice.Password);
        if (req != null) Finish(req);
    }

    /// <summary>Connects a fresh relay client and joins the room (the relay checks the password). Null (and a message) when it failed, or
    /// when the window was asked to close meanwhile (the connection is then closed here).</summary>
    public async Task<LaunchRequest?> JoinAsync(LobbyListing listing, string playerName, string password)
    {
        ArgumentNullException.ThrowIfNull(listing);
        if (_joining || _closed) return null;
        if (await RelayUri(true) is not { } uri) return null;
        var name = OnlineLobbyModel.CleanText(listing.Name);   // listings are untrusted: only cleaned text is shown
        _joining = true;
        SetBusy(true);
        SetState("Joining…");
        // The game window takes over the certificate question once it owns the client (a changed certificate on a reconnect).
        var trust = new CertificateTrust { Ask = c => RelayTrust.Ask(this, _dialogs, c) };
        var client = new RelayClient(RelayTrust.Pins());
        LastJoinClient = client;
        string? down = null;
        void OnDown(string r) => down = r;
        client.Disconnected += OnDown;
        string? failure = null;
        try
        {
            await Task.Run(() => client.ConnectAsync(uri, trust.Check, CancellationToken.None));
            var code = GameCode.Normalize(listing.Code ?? "");
            var verifier = await Task.Run(() => Secrets.AuthVerifier(password ?? "", code));
            await client.JoinRoomAsync(code, verifier);
            client.Disconnected -= OnDown;
            if (_closed || _closeAfterJoin)
            {
                // The window is going away: nobody takes the connection, so it closes here.
                await client.DisposeAsync();
                return null;
            }
            _store.Update(s => s with { PlayerName = playerName });
            SetState("Joined " + name);
            return new LaunchRequest(_row.Entry.Path, null, LaunchKind.JoinOnline, null, null,
                new OnlineLaunch(uri, playerName, password ?? "", null, null, code, listing, client, trust));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any failure (the relay's refusal, a broken connection, anything else): the client is closed here, nobody else holds it.
            client.Disconnected -= OnDown;
            try { await client.DisposeAsync(); } catch (Exception) { }
            failure = OnlineLobbyModel.CleanText(ex is RelayException && down != null && ex.Message == "Not connected to the relay." ? down : ex.Message,
                GuestSession.MaxHostText);   // relay text is untrusted too
            SetState(failure);
        }
        finally
        {
            _joining = false;
            SetBusy(false);
            if (_closeAfterJoin && !_closed) Dispatcher.UIThread.Post(Close);
        }
        if (!_closeAfterJoin) await Problem("Could not join " + name + ":\n" + failure);
        return null;
    }

    // ---------- hosting ----------

    /// <summary>The module's rules pack and its path, or null (and a message) when online play is not possible with it.</summary>
    async Task<(string Path, RulesPack Pack)?> HostPack()
    {
        var path = _library.RulesPackFor(_row.Entry);
        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path))) { await Problem(OnlineLobbyModel.NeedsPackText); return null; }
        RulesPack pack;
        try { pack = RulesPackIo.Load(path); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { await Problem("The rules pack could not be read: " + ex.Message); return null; }
        if (OnlineLobbyModel.ValidateHostSides(pack, new[] { "?" }) == OnlineLobbyModel.NeedsPackText) { await Problem(OnlineLobbyModel.NeedsPackText); return null; }
        return (path, pack);
    }

    /// <summary>Create lobby…: opens the module on a worker, then the setup chooser in Online mode; its answer is the hosting request.</summary>
    internal async Task CreateLobbyAsync()
    {
        if (_busy || _closed || await RelayUri(true) is not { } uri || await HostPack() is not { } hp) return;
        if (_row.Missing) { await Problem("This module's file was not found."); return; }
        SetBusy(true);
        ModuleSession? session;
        var modulePath = _row.Entry.Path;
        var rulesRoot = _store.Root;
        try
        {
            session = await Task.Run(() => ModuleSession.Open(modulePath, ModuleSession.DefaultCacheRoot, rulesRoot));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetBusy(false);
            await Problem($"Could not open '{modulePath}':\n{ex.Message}");
            return;
        }
        SetBusy(false);
        using (session)
        {
            if (_closed) return;
            var name = DefaultName();
            var req = await _dialogs.ChooseOnlineSetupAsync(this, session.Model, _store.Current.LastDifficulty, hp.Pack, name, $"{name}'s game");
            if (req is not { Players: { Count: 1 } players } || _closed) return;
            var host = players[0];
            _store.Update(s => s with { LastDifficulty = req.Difficulty, PlayerName = host.Name });
            Finish(new LaunchRequest(_row.Entry.Path, hp.Path, LaunchKind.HostOnline, req.WithoutPassword() with { Players = null }, null,
                new OnlineLaunch(uri, host.Name, req.Password ?? "", req.LobbyName, host.Sides.ToList())));
        }
    }

    /// <summary>Host a saved game…: a .qsave of this module, then the lobby name, password and the host's sides.</summary>
    internal async Task HostSavedAsync()
    {
        if (_busy || _closed || await RelayUri(true) is not { } uri || await HostPack() is not { } hp) return;
        var file = await _dialogs.OpenFileAsync(this, "Host a saved game",
            new[] { new FileFilter("Quartermaster saves (*.qsave)", "*.qsave"), new FileFilter("All files (*.*)", "*.*") });
        if (file == null || _closed) return;
        if (OnlineLobbyModel.CheckSave(file, _row.Entry.Sha256) is { } bad) { await Problem(bad); return; }
        var name = DefaultName();
        var choice = await _dialogs.AskHostSavedAsync(this, hp.Pack, name, $"{name}'s game", OnlineLobbyModel.SuggestedHostSides(file, hp.Pack));
        if (choice == null || _closed) return;
        _store.Update(s => s with { PlayerName = choice.PlayerName });
        Finish(new LaunchRequest(_row.Entry.Path, hp.Path, LaunchKind.HostOnline, null, file,
            new OnlineLaunch(uri, choice.PlayerName, choice.Password, choice.LobbyName, choice.HostSides)));
    }
}
