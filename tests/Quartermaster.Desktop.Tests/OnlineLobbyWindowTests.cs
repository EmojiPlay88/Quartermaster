using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Launcher;
using Quartermaster.Desktop.Online;
using Quartermaster.Desktop.Shell;
using Quartermaster.Net;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>
/// Port stage 5c task 3: the Online lobby window (WinForms OnlineLobbyForm), the launcher's Online lobby button and the setup chooser's
/// Online mode. A real relay runs in process on loopback; its certificate is pre-trusted in the test app data root (a temp folder) except
/// in the certificate test. Invented TEST module and sides (Red/Blue) only; nothing is ever shown on screen.
/// </summary>
public sealed class OnlineLobbyWindowTests : IAsyncLifetime
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-lobby-" + Guid.NewGuid().ToString("N"));
    readonly List<IDisposable> _disposables = new();
    readonly List<Window> _windows = new();
    readonly FakeDialogs _dialogs = new();
    InProcessRelay _relay = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tmp);
        _relay = await InProcessRelay.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var w in _windows) { try { w.Close(); } catch (InvalidOperationException) { } }
        Dispatcher.UIThread.RunJobs();
        await _relay.DisposeAsync();
        foreach (var d in _disposables) d.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static RulesPack Pack(string sha, params string[] sides) => RulesPackIo.CreateEmpty("TEST pack", sha) with
    {
        Sides = sides.Select(s => new SideDef { Id = s.ToLowerInvariant(), Name = s }).ToList(),
    };

    /// <summary>A store in a temp layout root, the TEST fixture module in its library (hashed), and a rules pack with <paramref name="sides"/>.</summary>
    async Task<(ModuleRow Row, ModuleLibrary Library, SettingsStore Store, RulesPack? Pack)> Module(params string[] sides)
    {
        var fx = PhaseFixture.Module();
        _disposables.Add(fx);
        var path = fx.Build();
        var store = new SettingsStore(Path.Combine(_tmp, "layout"));
        store.Load(null);
        var lib = new ModuleLibrary(store);
        lib.Add(path);
        await lib.RefreshAsync(null, CancellationToken.None);
        var row = lib.Rows[0];
        Assert.False(string.IsNullOrEmpty(row.Entry.Sha256));
        RulesPack? pack = null;
        if (sides.Length > 0)
        {
            pack = Pack(row.Entry.Sha256!, sides);
            var packPath = Path.Combine(_tmp, "test-" + Guid.NewGuid().ToString("N") + ".qrules");
            RulesPackIo.Save(pack, packPath);
            lib.SetRulesPack(row.Entry, packPath);
        }
        return (row, lib, store, pack);
    }

    /// <summary>Pins the relay's certificate where the windows look (the test app data root), so they are not asked.</summary>
    Task<RelayClient> TrustedClient() => _relay.ClientAsync(RelayTrust.Pins());

    /// <summary>Puts a lobby on the relay as a host would (a raw client; no HostSession).</summary>
    async Task<LobbyListing> HostRoom(string sha, string lobby, string password, params string[] hostSides)
    {
        var host = await TrustedClient();
        var code = await host.ReserveCodeAsync();
        var draft = OnlineLobbyModel.Draft(code, lobby, "Hosty", sha, "Fixture", Pack(sha, "Red", "Blue"),
            hostSides.Length > 0 ? hostSides : new[] { "red" }, password.Length > 0);
        await host.CreateRoomAsync(code, Secrets.AuthVerifier(password, code), draft);
        return draft;
    }

    OnlineLobbyWindow Lobby(ModuleRow row, ModuleLibrary lib, SettingsStore store)
    {
        var w = new OnlineLobbyWindow(row, lib, store, _dialogs);
        _windows.Add(w);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    static async Task WaitFor(Func<bool> done, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(done());
    }

    static async Task<string> ClosedMessage(RelayClient c)
    {
        var ex = await Assert.ThrowsAsync<RelayException>(() => c.ConnectAsync(new Uri("wss://127.0.0.1:1/qm"), _ => false, CancellationToken.None));
        return ex.Message;
    }

    [AvaloniaFact]
    public async Task Refresh_lists_this_modules_lobbies_and_saves_address()
    {
        var (row, lib, store, _) = await Module("Red", "Blue");
        var sha = row.Entry.Sha256!;
        await HostRoom(sha, "TEST open", "");
        await HostRoom(sha, "TEST locked", "pw");
        await HostRoom(sha, "TEST full", "", "red", "blue");   // no free side
        await HostRoom(new string('a', 64), "TEST other module", "");

        var w = Lobby(row, lib, store);
        Assert.Equal(OnlineLobbyModel.Title(row.Entry.Title), w.Title);
        Assert.Equal((640d, 380d), (w.Width, w.Height));
        Assert.Equal((520d, 300d), (w.MinWidth, w.MinHeight));
        Assert.Equal(new[] { "Lobby", "Host", "Free sides", "Players" }, w.ColumnHeaders);
        Assert.Equal(OnlineLobbyWindow.JoinText, w.JoinButton.Content);
        Assert.Equal("Create lobby…", w.CreateButton.Content);
        Assert.Equal("Host a saved game…", w.HostSavedButton.Content);
        Assert.Equal("Refresh", w.RefreshButton.Content);
        Assert.Equal("Enter the relay address and press Refresh.", w.StateText);
        Assert.False(w.JoinButton.IsEnabled);

        w.RelayBox.Text = _relay.Address;
        await w.RefreshAsync(manual: true);
        Assert.Equal($"Connected to {_relay.Address} — 3 lobbies", w.StateText);
        Assert.Equal(new[] { "TEST locked", "TEST open", "TEST full" }, w.Rows.Select(r => r.Lobby));
        Assert.True(w.RowLocked(0));
        Assert.False(w.RowLocked(1));
        Assert.Equal(_relay.Address, store.Current.RelayAddress);
        Assert.True(w.HasBrowser);

        // Selecting a joinable row enables Join; a full lobby is greyed with its reason as the tooltip and cannot be joined.
        w.List.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.JoinButton.IsEnabled);
        Assert.False(w.RowGreyed(1));
        Assert.Null(w.RowTip(1));
        Assert.True(w.RowGreyed(2));
        Assert.Equal(OnlineLobbyModel.FullText, w.RowTip(2));
        w.List.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.False(w.JoinButton.IsEnabled);

        // A second refresh keeps the connection and the selection.
        await w.RefreshAsync(manual: true);
        Assert.Equal(2, w.List.SelectedIndex);

        w.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(w.HasBrowser);   // the browser client is closed with the window
        Assert.Null(await w.Result);
    }

    [AvaloniaFact]
    public async Task Refresh_on_open_uses_saved_address_and_counts_one_lobby()
    {
        var (row, lib, store, _) = await Module("Red", "Blue");
        await HostRoom(row.Entry.Sha256!, "TEST only", "");
        store.Update(s => s with { RelayAddress = _relay.Address });
        var w = Lobby(row, lib, store);
        Assert.Equal(_relay.Address, w.RelayBox.Text);
        await WaitFor(() => w.StateText.StartsWith("Connected", StringComparison.Ordinal));
        Assert.Equal($"Connected to {_relay.Address} — 1 lobby", w.StateText);

        // Enter in the relay box refreshes.
        await HostRoom(row.Entry.Sha256!, "TEST second", "");
        w.RelayBox.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await WaitFor(() => w.StateText.EndsWith("2 lobbies", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Bad_address_shows_message_and_does_not_connect()
    {
        var (row, lib, store, _) = await Module("Red", "Blue");
        var w = Lobby(row, lib, store);
        w.RelayBox.Text = "nope:xx";
        await w.RefreshAsync(manual: true);
        Assert.Equal(OnlineLobbyModel.BadAddressText, w.StateText);
        Assert.Contains(_dialogs.Infos, i => i.Message == OnlineLobbyModel.BadAddressText);
        Assert.False(w.HasBrowser);
        Assert.Null(store.Current.RelayAddress);
    }

    [AvaloniaFact]
    public async Task Join_returns_request_with_open_client()
    {
        var (row, lib, store, _) = await Module("Red", "Blue");
        var listing = await HostRoom(row.Entry.Sha256!, "TEST locked", "pw");
        var w = Lobby(row, lib, store);
        w.RelayBox.Text = _relay.Address;
        var messy = listing with { Code = listing.Code.ToLowerInvariant().Insert(3, "-") };

        var req = await w.JoinAsync(messy, "Carol", "pw");
        Assert.NotNull(req);
        Assert.Equal(LaunchKind.JoinOnline, req!.Kind);
        Assert.Equal(row.Entry.Path, req.ModulePath);
        var o = req.Online!;
        Assert.Equal(listing.Code, o.Code);
        Assert.Equal("Carol", o.PlayerName);
        Assert.Equal("pw", o.Password);
        Assert.NotNull(o.Trust);
        Assert.NotNull(o.Client);
        Assert.False(string.IsNullOrEmpty(o.Client!.MemberId));   // joined
        Assert.Equal(_relay.Uri.Port, o.Relay.Port);
        Assert.Equal("Joined TEST locked", w.StateText);
        Assert.Equal("Carol", store.Current.PlayerName);
        await o.Client.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Join_selected_through_dialog_finishes_window()
    {
        var (row, lib, store, _) = await Module("Red", "Blue");
        await HostRoom(row.Entry.Sha256!, "TEST open", "");
        var w = Lobby(row, lib, store);
        w.RelayBox.Text = _relay.Address;
        await w.RefreshAsync(manual: true);
        w.List.SelectedIndex = 0;
        _dialogs.JoinAnswer = new JoinChoice("Dave", "");
        await w.JoinSelectedAsync();
        var req = await w.Result;
        Assert.Equal(LaunchKind.JoinOnline, req!.Kind);
        Assert.Equal("Dave", req.Online!.PlayerName);
        Assert.False(w.IsVisible);
        await req.Online.Client!.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Wrong_password_join_fails_and_disposes_client()
    {
        var (row, lib, store, _) = await Module("Red", "Blue");
        var listing = await HostRoom(row.Entry.Sha256!, "TEST locked", "pw");
        var w = Lobby(row, lib, store);
        w.RelayBox.Text = _relay.Address;

        var req = await w.JoinAsync(listing, "Carol", "wrong");
        Assert.Null(req);
        Assert.NotNull(w.LastJoinClient);
        Assert.Equal("The relay client was closed.", await ClosedMessage(w.LastJoinClient!));
        Assert.Contains(_dialogs.Warnings, x => x.Message.StartsWith("Could not join TEST locked:\n", StringComparison.Ordinal));
        Assert.NotEqual("Joining…", w.StateText);
        Assert.Null(store.Current.PlayerName);
        Assert.True(w.CreateButton.IsEnabled);   // not busy any more
    }

    [AvaloniaFact]
    public async Task Unknown_certificate_asks_once_and_no_fails_cleanly()
    {
        var (row, lib, store, _) = await Module("Red", "Blue");
        // No pre-trust: the window's client meets the relay's self-signed certificate for the first time (or a changed one).
        var w = Lobby(row, lib, store);
        w.RelayBox.Text = _relay.Address;
        _dialogs.Answer = false;

        var refresh = w.RefreshAsync(manual: true);
        var done = await Task.WhenAny(refresh, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(refresh, done);   // the UI thread was never blocked by the question
        await refresh;
        var q = Assert.Single(_dialogs.Questions);
        Assert.Contains(q.Caption, new[] { OnlineLobbyModel.CertNewTitle, OnlineLobbyModel.CertChangedTitle });
        Assert.Contains("certificate was not trusted", w.StateText);
        Assert.False(w.HasBrowser);
        Assert.Empty(w.Rows);
        Assert.Null(store.Current.RelayAddress);
        Assert.True(w.RefreshButton.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Close_during_join_waits_then_disposes_client()
    {
        var (row, lib, store, _) = await Module("Red", "Blue");
        var listing = await HostRoom(row.Entry.Sha256!, "TEST open", "");
        var w = Lobby(row, lib, store);
        w.RelayBox.Text = _relay.Address;

        var join = w.JoinAsync(listing, "Carol", "");
        Assert.Equal("Joining…", w.StateText);
        Assert.False(w.CloseButton.IsEnabled);
        w.Close();   // refused while the join holds the connection
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.IsVisible);
        Assert.False(w.Result.IsCompleted);

        Assert.Null(await join);   // the window was asked to close: the joined connection is not handed on
        Assert.Equal("The relay client was closed.", await ClosedMessage(w.LastJoinClient!));
        await WaitFor(() => !w.IsVisible);
        Assert.Null(await w.Result);
    }

    [AvaloniaFact]
    public async Task Create_lobby_returns_host_request_without_password_in_new_game()
    {
        var (row, lib, store, pack) = await Module("Red", "Blue");
        var w = Lobby(row, lib, store);
        w.RelayBox.Text = _relay.Address;
        var host = new PlayerDef("host", "Hosty", new[] { "red" });
        _dialogs.OnlineSetupAnswer = new NewGameRequest(GameMode.Online, null, Difficulty.FullyAuto, new[] { host }, "TEST lobby", "pw");

        await w.CreateLobbyAsync();
        var req = await w.Result;
        Assert.NotNull(req);
        Assert.Equal(LaunchKind.HostOnline, req!.Kind);
        Assert.Equal(row.Entry.Path, req.ModulePath);
        Assert.Equal(lib.RulesPackFor(row.Entry), req.RulesPath);
        Assert.Null(req.NewGame!.Password);
        Assert.Null(req.NewGame.Players);
        Assert.Equal(GameMode.Online, req.NewGame.Mode);
        Assert.Equal("TEST lobby", req.NewGame.LobbyName);
        Assert.Equal("pw", req.Online!.Password);
        Assert.Equal("Hosty", req.Online.PlayerName);
        Assert.Equal("TEST lobby", req.Online.LobbyName);
        Assert.Equal(new[] { "red" }, req.Online.HostSides);
        Assert.Null(req.Online.Client);
        Assert.Equal(_relay.Uri.Port, req.Online.Relay.Port);
        Assert.Equal(Difficulty.FullyAuto, store.Current.LastDifficulty);
        Assert.Equal("Hosty", store.Current.PlayerName);
        // The chooser was offered the pack, the host's name and "{name}'s game".
        var asked = Assert.Single(_dialogs.OnlineSetups);
        Assert.Equal(pack!.Sides.Select(s => s.Id), asked.Pack.Sides.Select(s => s.Id));
        Assert.Equal(asked.HostName + "'s game", asked.LobbyName);
    }

    [AvaloniaFact]
    public async Task Create_lobby_needs_pack_with_two_sides()
    {
        var (row, lib, store, _) = await Module("Red");
        var w = Lobby(row, lib, store);
        w.RelayBox.Text = _relay.Address;
        _dialogs.OnlineSetupAnswer = new NewGameRequest(GameMode.Online, null, Difficulty.FullyAuto,
            new[] { new PlayerDef("host", "Hosty", new[] { "red" }) }, "TEST lobby", "pw");
        await w.CreateLobbyAsync();
        Assert.Contains(_dialogs.Warnings, x => x.Message == OnlineLobbyModel.NeedsPackText);
        Assert.Empty(_dialogs.OnlineSetups);
        Assert.False(w.Result.IsCompleted);

        // No pack at all says the same.
        var (row2, lib2, store2, _) = await Module();
        var w2 = Lobby(row2, lib2, store2);
        w2.RelayBox.Text = _relay.Address;
        _dialogs.Warnings.Clear();
        await w2.CreateLobbyAsync();
        Assert.Contains(_dialogs.Warnings, x => x.Message == OnlineLobbyModel.NeedsPackText);
        Assert.False(w2.Result.IsCompleted);
    }

    [AvaloniaFact]
    public async Task Host_saved_game_checks_the_save_and_returns_request()
    {
        var (row, lib, store, _) = await Module("Red", "Blue");
        var w = Lobby(row, lib, store);
        w.RelayBox.Text = _relay.Address;
        var bad = Path.Combine(_tmp, "bad.qsave");
        File.WriteAllText(bad, "not a save");
        _dialogs.OpenFiles.Enqueue(bad);
        await w.HostSavedAsync();
        Assert.StartsWith("That saved game could not be read", _dialogs.Warnings.Last().Message);
        Assert.Equal("Host a saved game", _dialogs.PickerTitles.Last());
        Assert.Equal("*.qsave", _dialogs.LastOpenFilters![0].Patterns[0]);
        Assert.False(w.Result.IsCompleted);
    }

    [AvaloniaFact]
    public async Task Launcher_online_button_opens_lobby_and_completes_result()
    {
        var (row, lib, store, _) = await Module("Red", "Blue");
        store.Update(s => s with { ReopenLastModule = false });
        var launcher = new LauncherWindow(store, Path.Combine(_tmp, "layout"), _dialogs, null);
        _windows.Add(launcher);
        launcher.Show();
        Dispatcher.UIThread.RunJobs();
        await WaitFor(() => launcher.RefreshTask.IsCompleted);

        Assert.False(launcher.OnlineButton.IsEnabled);
        Assert.Equal("Select a module first.", ToolTip.GetTip(launcher.OnlineButton));
        launcher.Modules.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.True(launcher.OnlineButton.IsEnabled);
        Assert.Equal("Join or host a live online game of this module.", ToolTip.GetTip(launcher.OnlineButton));

        launcher.OnlineButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => launcher.Lobby is { IsVisible: true });
        var lobby = launcher.Lobby!;
        Assert.Equal(OnlineLobbyModel.Title(row.Entry.Title), lobby.Title);
        lobby.RelayBox.Text = _relay.Address;
        _dialogs.OnlineSetupAnswer = new NewGameRequest(GameMode.Online, null, Difficulty.SemiAuto,
            new[] { new PlayerDef("host", "Hosty", new[] { "blue" }) }, "TEST lobby", "");
        await lobby.CreateLobbyAsync();
        var req = await launcher.Result;
        Assert.Equal(LaunchKind.HostOnline, req!.Kind);
        Assert.Equal(new[] { "blue" }, req.Online!.HostSides);
        await WaitFor(() => !launcher.IsVisible);
        Assert.Null(launcher.Lobby);
    }

    [AvaloniaFact]
    public async Task Launcher_online_button_for_missing_module_has_missing_tip()
    {
        var (row, lib, store, _) = await Module("Red", "Blue");
        var gone = Path.Combine(_tmp, "gone.vmod");
        File.Copy(row.Entry.Path, gone);
        lib.Add(gone);
        File.Delete(gone);
        store.Update(s => s with { ReopenLastModule = false });
        var launcher = new LauncherWindow(store, Path.Combine(_tmp, "layout"), _dialogs, null);
        _windows.Add(launcher);
        launcher.Show();
        Dispatcher.UIThread.RunJobs();
        await WaitFor(() => launcher.RefreshTask.IsCompleted);
        launcher.Modules.SelectedIndex = launcher.Library.Rows.ToList().FindIndex(r => r.Missing);
        Dispatcher.UIThread.RunJobs();
        Assert.False(launcher.OnlineButton.IsEnabled);
        Assert.Equal(LauncherModel.MissingTooltip, ToolTip.GetTip(launcher.OnlineButton));
    }

    [AvaloniaFact]
    public void Setup_chooser_online_mode_shows_lobby_panel_and_host_sides()
    {
        var pack = Pack("x", "Red", "Blue");
        var offline = new SetupChooser(null, Difficulty.SemiAuto, preselectNew: true, pack, _dialogs);
        Assert.Equal("New game", offline.Title);
        Assert.False(offline.LobbyPanel.IsVisible);
        Assert.Null(offline.HostSides);

        var c = new SetupChooser(null, Difficulty.Hardcore, preselectNew: true, pack, _dialogs, GameMode.Online, "Hosty", "Hosty's game");
        _windows.Add(c);
        c.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Create lobby", c.Title);
        Assert.True(c.LobbyPanel.IsVisible);
        Assert.Equal("Hosty's game", c.LobbyNameBox.Text);
        Assert.Equal(NewGameRules.MaxLobbyName, c.LobbyNameBox.MaxLength);
        Assert.Equal('*', c.LobbyPasswordBox.PasswordChar);
        Assert.Equal(OnlineLobbyModel.OpenLobbyWarning, c.OpenWarning.Text);
        Assert.True(c.OpenWarning.IsVisible);
        c.LobbyPasswordBox.Text = "pw";   // same tick, no pump
        Assert.False(c.OpenWarning.IsVisible);

        c.ShowPlayersPage();
        Assert.Equal("You and the open seats", c.PlayersTitle.Text);
        Assert.Same(c.HostSides, c.PlayersFrame.Child);
        Assert.Equal("Hosty", c.HostSides!.PlayerName);
        Assert.Equal(new[] { "red" }, c.HostSides.HostSides);

        // A host problem goes to the Players page with a "Players" warning; a blank lobby name is refused as a whole.
        foreach (var b in c.HostSides.Boxes) b.IsChecked = true;
        c.Ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((OnlineLobbyModel.NoOpenSideText, "Players"), _dialogs.Warnings.Last());
        c.HostSides.Boxes[0].IsChecked = false;
        c.LobbyNameBox.Text = "  ";
        c.Ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(("Give the lobby a name.", "Create lobby"), _dialogs.Warnings.Last());
        Assert.True(c.IsVisible);

        c.LobbyNameBox.Text = " TEST lobby ";
        c.Ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var req = c.Request;
        Assert.NotNull(req);
        Assert.Equal(GameMode.Online, req!.Mode);
        Assert.Null(req.Setup);
        Assert.Equal(Difficulty.Hardcore, req.Difficulty);
        var p = Assert.Single(req.Players!);
        Assert.Equal(("host", "Hosty"), (p.Id, p.Name));
        Assert.Equal(new[] { "blue" }, p.Sides);
        Assert.Equal("TEST lobby", req.LobbyName);
        Assert.Equal("pw", req.Password);
        Assert.False(c.IsVisible);
    }
}
