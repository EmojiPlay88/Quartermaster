using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Desktop.Launcher;
using Quartermaster.Desktop.Online;
using Quartermaster.Desktop.Preferences;
using Quartermaster.Desktop.Input;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>
/// Port stage 5c task 4: the game window online (WinForms MainForm.Online), as host and as guest. A real relay runs in process on
/// loopback; its certificate is pinned in the test app data root (a temp folder). Invented TEST module and sides (Red/Blue/Green) only;
/// headless, nothing is ever shown on screen.
/// </summary>
public sealed class MainWindowOnlineTests : IAsyncLifetime
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-online-" + Guid.NewGuid().ToString("N"));
    readonly List<IDisposable> _disposables = new();
    readonly List<Window> _windows = new();
    readonly FakeDialogs _hostDialogs = new() { Answer = true };
    readonly FakeDialogs _guestDialogs = new() { Answer = true };
    InProcessRelay _relay = null!;

    static readonly PlayerDef Anna = new("p1", "Anna", new[] { "red" });
    static readonly PlayerDef Ben = new("p2", "Ben", new[] { "blue" });

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tmp);
        _relay = await InProcessRelay.StartAsync();
        await _relay.ClientAsync(RelayTrust.Pins());   // pins the relay's certificate where the windows look: nobody is asked
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var w in _windows)
        {
            try
            {
                if (w is MainWindow m)
                {
                    if (m.Session is { } s) FormulaWindows.CloseAllFor(s);
                    m.ForceClose();
                }
                else w.Close();
            }
            catch (InvalidOperationException) { }
        }
        Dispatcher.UIThread.RunJobs();
        await _relay.DisposeAsync();
        foreach (var d in _disposables) d.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---------- fixture ----------

    /// <summary>The invented TEST pack: Red, Blue and Green own the pieces whose names start with their name; Fuel is a piece value.</summary>
    static RulesPack Pack(string sha) => RulesPackIo.CreateEmpty("TEST online pack", sha) with
    {
        Sides = new()
        {
            new SideDef { Id = "red", Name = "Red", UnitFilter = "startsWith(name(), \"Red\")" },
            new SideDef { Id = "blue", Name = "Blue", UnitFilter = "startsWith(name(), \"Blue\")" },
            new SideDef { Id = "green", Name = "Green", UnitFilter = "startsWith(name(), \"Green\")" },
        },
        Properties = new() { new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, SheetField = "Fuel" } },
    };

    sealed record Fixture(ModuleRow Row, ModuleLibrary Library, SettingsStore Store, string ModulePath, string PackPath, SetupDef Setup);

    async Task<Fixture> Module()
    {
        var fx = PhaseFixture.Module();
        _disposables.Add(fx);
        var path = fx.Build();
        var store = new SettingsStore(Path.Combine(_tmp, "lobby-layout"));
        store.Load(null);
        var lib = new ModuleLibrary(store);
        lib.Add(path);
        await lib.RefreshAsync(null, CancellationToken.None);
        var row = lib.Rows[0];
        var packPath = Path.Combine(_tmp, "test-" + Guid.NewGuid().ToString("N") + ".qrules");
        RulesPackIo.Save(Pack(row.Entry.Sha256!), packPath);
        lib.SetRulesPack(row.Entry, packPath);
        using var probe = ModuleSession.Open(path, Path.Combine(_tmp, "cache"), null);
        return new Fixture(row, lib, store, path, packPath, probe.Model.Setups[0]);   // "Start": Red 1, Red 2, Blue 1
    }

    MainWindow Window(FakeDialogs dialogs, string name)
    {
        var w = new MainWindow(dialogs, Path.Combine(_tmp, "cache"), null, Path.Combine(_tmp, "layout-" + name)) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        return w;
    }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>Runs the UI thread (jobs, timers, network continuations) until <paramref name="done"/>.</summary>
    static void PumpUntil(Func<bool> done, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            Pump();
            if (done()) return;
            Assert.True(DateTime.UtcNow < until, "timed out");
            using var slice = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
    }

    LaunchRequest HostRequest(Fixture f, string? savePath = null, Uri? relay = null) =>
        new(f.ModulePath, f.PackPath, LaunchKind.HostOnline,
            savePath == null ? new NewGameRequest(GameMode.Online, f.Setup, Difficulty.FullyAuto, null, "TEST lobby", null) : null, savePath,
            new OnlineLaunch(relay ?? _relay.Uri, "Hosty", "", "TEST lobby", new[] { "red" }));

    async Task<MainWindow> Host(Fixture f)
    {
        var w = Window(_hostDialogs, "host");
        await w.StartFromLaunchAsync(HostRequest(f));
        Assert.NotNull(w.HostOnline);
        return w;
    }

    /// <summary>A second window joins the host's lobby through the lobby window (as the launcher would hand it over).</summary>
    async Task<MainWindow> Guest(Fixture f, MainWindow host, string name = "Carol")
    {
        var lobby = new OnlineLobbyWindow(f.Row, f.Library, f.Store, _guestDialogs);
        _windows.Add(lobby);
        lobby.Show();
        Pump();
        lobby.RelayBox.Text = _relay.Address;
        var req = await lobby.JoinAsync(host.HostOnline!.Listing, name, "");
        Assert.NotNull(req);
        lobby.Close();
        Pump();
        var g = Window(_guestDialogs, "guest-" + name);
        await g.StartFromLaunchAsync(req!);
        Assert.NotNull(g.GuestOnline);
        return g;
    }

    /// <summary>Host plus a guest that took Blue and has the host's game.</summary>
    async Task<(MainWindow Host, MainWindow Guest)> Seated(Fixture f)
    {
        var host = await Host(f);
        _guestDialogs.SidesAnswer = new[] { "blue" };
        var guest = await Guest(f, host);
        PumpUntil(() => guest.GuestOnline is { State: GuestState.Playing } && guest.Session?.Game != null);
        return (host, guest);
    }

    // ---------- tests ----------

    [AvaloniaFact]
    public async Task Host_from_launch_request_opens_lobby_and_shows_status()
    {
        var f = await Module();
        var w = await Host(f);
        var h = w.HostOnline!;
        Assert.StartsWith("Online: ", w.OnlineStatusText, StringComparison.Ordinal);
        Assert.Contains(h.Listing.Code, w.OnlineStatusText, StringComparison.Ordinal);
        Assert.Equal($"Your lobby is open: TEST lobby (code {h.Listing.Code}).", w.StatusText);
        Assert.Null(w.OnlineError);
        Assert.True(w.GamePlayersItem!.IsVisible);
        Assert.True(w.GamePlayersItem.IsEnabled);
        Assert.True(w.GameLeaveItem!.IsVisible);
        Assert.True(((MenuItem)w.GamePlayersItem.Parent!).IsVisible);
        Assert.NotNull(w.Log!.Chat);
        Assert.Equal("", w.OnlineBannerText);
        Assert.False(w.Actions!.IsRemote);
        // The relay lists it.
        Assert.Equal("TEST lobby", h.Listing.Name);
    }

    [AvaloniaFact]
    public async Task Hosting_failure_keeps_local_game_and_hot_seat()
    {
        var f = await Module();
        // A game saved with two hot-seat players.
        var prep = Window(new FakeDialogs(), "prep");
        Assert.True(prep.OpenModule(f.ModulePath));
        prep.StartSetup(0, Difficulty.FullyAuto);
        prep.Session!.OpenRules(f.PackPath);
        var g0 = prep.Session.Game!;
        g0.Execute(CommandLog.Prepare(new SetPlayers(new List<PlayerDef> { Anna, Ben }), g0.State, prep.Session));
        var save = Path.Combine(_tmp, "two.qsave");
        prep.SaveGame(save);
        prep.ForceClose();
        Pump();

        var w = Window(_hostDialogs, "host");
        await w.StartFromLaunchAsync(HostRequest(f, save, new Uri("wss://127.0.0.1:1/qm")));
        Pump();
        Assert.Null(w.HostOnline);
        Assert.NotNull(w.OnlineError);
        Assert.NotNull(w.HotSeat);
        Assert.Equal(2, w.Session!.Game!.State.Players.Count);
        Assert.Null(w.Log!.Chat);
        Assert.Equal("", w.OnlineStatusText);
        Assert.False(w.GameLeaveItem!.IsVisible);
        Assert.Contains(_hostDialogs.Infos, i => i.Caption == "Online game"
            && i.Message == "The game could not go online:\n" + w.OnlineError + "\n\nIt continues here as a local game.");
    }

    [AvaloniaFact]
    public async Task Guest_joins_chooses_side_and_plays_through_host()
    {
        var f = await Module();
        var (host, guest) = await Seated(f);
        Assert.True(guest.Actions!.IsRemote);
        Assert.True(guest.Session!.IsGuest);
        Assert.Contains("blue", guest.GuestOnline!.Sides);
        Assert.StartsWith("Online: ", guest.OnlineStatusText, StringComparison.Ordinal);
        Assert.Equal("", guest.OnlineBannerText);

        // A ledger edit by the guest (its own Blue piece) is done by the host and reaches the host's game.
        var key = LedgerKey.Piece(PhaseFixture.BlueOne);
        guest.Actions.EditLedger(key, "fuel", 7, "TEST note");
        PumpUntil(() => Ledger.Get(host.Session!.Game!.State, key, "fuel") == 7);
        PumpUntil(() => Ledger.Get(guest.Session!.Game!.State, key, "fuel") == 7);
    }

    [AvaloniaFact]
    public async Task Chat_line_reaches_both_logs()
    {
        var f = await Module();
        var (host, guest) = await Seated(f);
        var box = guest.Log!.Chat!;
        box.Input.Text = "TEST hello";
        box.Send();
        PumpUntil(() => host.Log!.Rows.Any(r => r.Text.Contains("TEST hello", StringComparison.Ordinal)));
        PumpUntil(() => guest.Log!.Rows.Any(r => r.Text.Contains("TEST hello", StringComparison.Ordinal)));

        host.Log!.Chat!.Input.Text = "TEST reply";
        host.Log.Chat.Send();
        PumpUntil(() => guest.Log!.Rows.Any(r => r.Text.Contains("TEST reply", StringComparison.Ordinal)));
        PumpUntil(() => host.Log!.Rows.Any(r => r.Text.Contains("TEST reply", StringComparison.Ordinal)));
    }

    [AvaloniaFact]
    public async Task Host_players_dialog_and_guest_tooltip()
    {
        var f = await Module();
        var (host, guest) = await Seated(f);
        await host.ShowPlayersDialogAsync();
        Assert.Equal(1, _hostDialogs.SeatsShown);

        var players = guest.GamePlayersItem!;
        Assert.True(players.IsVisible);
        Assert.False(players.IsEnabled);
        Assert.Equal(HostSession.SidesFixedText, ToolTip.GetTip(players));
        await guest.ShowPlayersDialogAsync();
        Assert.Contains(_guestDialogs.Infos, i => i == (HostSession.SidesFixedText, "Players"));
        Assert.Equal(0, _guestDialogs.SeatsShown);
    }

    [AvaloniaFact]
    public async Task Host_leave_returns_to_local_with_hot_seat_restored()
    {
        var f = await Module();
        var w = await Host(f);
        Assert.Null(w.HotSeat);   // online owns the view
        await w.LeaveOnlineGameAsync();
        Pump();
        Assert.Contains(_hostDialogs.Questions, q => q == ("Leave the online game? The lobby closes and the other players are disconnected. "
            + "The game continues here as a local game.", "Leave Online Game"));
        Assert.Null(w.HostOnline);
        Assert.NotNull(w.HotSeat);
        Assert.Equal("You left the online game; it continues here as a local game.", w.StatusText);
        Assert.Equal("", w.OnlineStatusText);
        Assert.False(w.GameLeaveItem!.IsVisible);
        Assert.False(w.GamePlayersItem!.IsVisible);
        Assert.Null(w.Log!.Chat);
        Assert.False(w.Actions!.IsRemote);
        Assert.NotNull(w.Session!.Game);
        Assert.True(w.FindMenuItem("&Save Game")!.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Guest_cannot_save_undo_or_edit_players()
    {
        var f = await Module();
        var (_, guest) = await Seated(f);
        Pump();
        var save = guest.FindMenuItem("&Save Game")!;
        Assert.False(save.IsEnabled);
        Assert.Equal(ModuleSession.GuestSaveText, ToolTip.GetTip(save));
        Assert.False(HistoryCommands.CanUndo(guest.Session!, guest.History!));
        Assert.False(guest.FindMenuItem("&Undo", prefix: true)!.IsEnabled);

        // Settings > Game > Players: a guest is refused with the host-only text.
        SettingsWindow? shown = null;
        guest.ShowSettingsHook = win => { shown = win; return Task.CompletedTask; };
        await guest.ShowSettingsAsync("game");
        Pump();
        var editor = shown!.Pages.OfType<GamePage>().Single().Editor!;
        editor.Model.FillTwoPlayersForTests();
        Pump();
        Assert.True(await shown.CommitAsync());
        Pump();
        Assert.Contains(_guestDialogs.Warnings, x => x == (IntentExecutor.HostOnlyText, "Quartermaster"));
    }

    [AvaloniaFact]
    public async Task Closing_window_ends_online_without_throwing()
    {
        var f = await Module();
        var (host, guest) = await Seated(f);
        host.ForceClose();
        Pump();
        Assert.Null(host.HostOnline);
        // The guest hears the host is gone (paused) and nothing throws.
        PumpUntil(() => guest.GuestOnline is null or { State: GuestState.Paused or GuestState.Refused });
        guest.ForceClose();
        Pump();
        Assert.Null(guest.GuestOnline);
    }
}
