using Avalonia.Controls;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Online;
using Quartermaster.Net;
using Quartermaster.Render;

namespace Quartermaster.Desktop;

/// <summary>
/// Live online play in the game window (port of WinForms MainForm.Online; spec 5, 8a; launcher spec 3.3, 4): hosting a lobby (relay
/// connect, room, <see cref="HostSession"/>), playing as a guest (<see cref="GuestSession"/> with <see cref="RemoteActions"/> as the
/// window's action port), the chat line, the <c>Online: {code} · {n} players</c> status, the paused banner, Game → Players… and
/// Game → Leave Online Game, and the guest's limits (no save, no undo, read-only rules). Every network event reaches this window through
/// <see cref="PostToUi"/> and is guarded: nothing here may crash the window, whatever the relay or the other players send.
/// </summary>
public partial class MainWindow
{
    MenuItem? _gameMenu, _gamePlayers, _gameLeave;
    LocalActions? _localActions;
    RelayClient? _relayClient;
    RelayTransport? _transport;
    HostSession? _host;
    GuestSession? _guest;
    RemoteActions? _remote;
    OnlineLaunch? _online;
    string? _onlineCode;
    bool _onlineStarting, _sideAsked, _leavingOnline;
    DispatcherTimer? _sideTimer;

    /// <summary>The hosted online session (null when not hosting).</summary>
    public HostSession? HostOnline => _host;

    /// <summary>The guest session (null when not playing as a guest).</summary>
    public GuestSession? GuestOnline => _guest;

    /// <summary>The status bar's online text ("" when hidden).</summary>
    internal string OnlineStatusText => SbOnlineBox.IsVisible ? SbOnline.Text ?? "" : "";

    /// <summary>The banner text over the game ("" when hidden).</summary>
    internal string OnlineBannerText => OnlineBanner.IsVisible ? OnlineBannerLabel.Text ?? "" : "";

    /// <summary>The last hosting or joining failure.</summary>
    internal string? OnlineError { get; private set; }

    /// <summary>Game → Players… and Game → Leave Online Game (tests).</summary>
    internal MenuItem? GamePlayersItem => _gamePlayers;
    internal MenuItem? GameLeaveItem => _gameLeave;

    /// <summary>How often the side choice was put to this guest (tests: a rejoining seat is never asked).</summary>
    internal int SideQuestionsAsked { get; private set; }

    bool IsOnline => _host != null || _guest != null;

    /// <summary>Game menu: Players… and Leave Online Game, shown only while online or starting (Send Turn… is stage 5d).</summary>
    MenuItem BuildGameMenu()
    {
        var game = _gameMenu = Top("&Game");
        game.IsVisible = false;
        _gamePlayers = Item(game, "&Players…", () => RunAsync(ShowPlayersDialogAsync));
        _gamePlayers.IsVisible = false;
        game.Items.Add(new Separator());
        _gameLeave = Item(game, "&Leave Online Game", () => RunAsync(LeaveOnlineGameAsync));
        _gameLeave.IsVisible = false;
        return game;
    }

    // ---------- marshalling ----------

    /// <summary>The dispatcher the online sessions get: runs the action on the UI thread, never throws, drops it once the window is gone.</summary>
    void PostToUi(Action a)
    {
        if (_closed) return;
        try { Dispatcher.UIThread.Post(() => { if (!_closed) OnlineGuard(a); }); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("online post: " + ex.Message); }
    }

    void OnlineGuard(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("online: " + ex.Message);
            try { SetStatus(ex.Message); } catch (Exception) { }
        }
    }

    bool StillCurrent(ModuleSession s, RelayClient client) =>
        !_closed && ReferenceEquals(_session, s) && ReferenceEquals(_relayClient, client) && !_leavingOnline;

    /// <summary>Online sessions own the view (the host sees its seat's sides; a guest its snapshot): hot-seat stops, its windows come back.</summary>
    void StopHotSeatForOnline() => DestroyHotSeat(restoreWindows: true);

    /// <summary>Runs a hosting or joining start; whatever it throws ends as a message, never as an unobserved or unhandled exception.</summary>
    async Task StartOnlineAsync(Func<Task> start, bool guest)
    {
        try { await start(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { OnlineFailed(ex.Message, guest); }
            catch (Exception inner) when (inner is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("online: " + inner.Message); }
        }
    }

    // ---------- hosting ----------

    /// <summary>
    /// Takes the open game online: connects to the relay (asking to trust its certificate if needed), reserves a code, creates the room
    /// with the public listing and starts the host session. A failure leaves the game running locally and says why.
    /// </summary>
    async Task BeginHostingAsync(ModuleSession s, OnlineLaunch o)
    {
        if (s.Game == null) return;
        var hostSides = (o.HostSides ?? Array.Empty<string>()).ToList();
        if (OnlineLobbyModel.ValidateHostSides(s.Rules, hostSides) is { } bad) { OnlineFailed(bad); return; }
        var pack = s.Rules!;
        StopHotSeatForOnline();
        var client = new RelayClient(RelayTrust.Pins());
        string? down = null;
        void OnDown(string r) => down = r;
        client.Disconnected += OnDown;
        _relayClient = client;
        _online = o;
        _onlineStarting = true;
        SetOnlineStatusText("Online: connecting…");
        UpdateOnlineUi();
        try
        {
            // Off the UI thread: the certificate question blocks its caller until it is answered here.
            await Task.Run(() => client.ConnectAsync(o.Relay, c => RelayTrust.Ask(this, _dialogs, c), CancellationToken.None));
            if (!StillCurrent(s, client)) return;
            var code = await client.ReserveCodeAsync();
            if (!StillCurrent(s, client)) return;
            var password = o.Password ?? "";
            var (verifier, key) = await Task.Run(() => (Secrets.AuthVerifier(password, code), Secrets.GameKey(password, code)));
            if (!StillCurrent(s, client)) return;
            var draft = OnlineLobbyModel.Draft(code, o.LobbyName ?? "Lobby", o.PlayerName, s.Archive.Sha256, s.Model.Name, pack, hostSides, password.Length > 0);
            await client.CreateRoomAsync(code, verifier, draft);   // the relay keeps the host key for this room's life (rejoin)
            if (!StillCurrent(s, client)) return;
            client.Disconnected -= OnDown;
            // Launcher spec 4a.8: the host's choice wins; every other side is an open seat, whoever held it in the save. Only now that
            // the room exists, so a failed start leaves the saved game's players untouched.
            if (s.Game is { } live && live.State.Players.Count > 0) live.Execute(CommandLog.Prepare(new SetPlayers(new List<PlayerDef>()), live.State, s));
            _transport = new RelayTransport(client, key);
            _host = new HostSession(s, new IntentExecutor(s, _phases!), _transport, password, code, draft, PostToUi);
            _host.SeatsChanged += UpdateOnlineUi;
            _host.ConnectionChanged += UpdateOnlineUi;
            _onlineCode = draft.Code;
            OnlineLobbyModel.MarkHosted(draft.Code);
            _onlineStarting = false;
            System.Diagnostics.Trace.WriteLine("online: hosting lobby " + draft.Code);
            SetStatus($"Your lobby is open: {draft.Name} (code {draft.Code}).");
            UpdateOnlineUi();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            client.Disconnected -= OnDown;
            if (!StillCurrent(s, client)) { if (!ReferenceEquals(_relayClient, client)) _ = DisposeLater(client, TimeSpan.Zero); return; }
            var why = ex is RelayException && down != null && ex.Message == "Not connected to the relay." ? down : ex.Message;
            OnlineFailed(why);
        }
    }

    void OnlineFailed(string why, bool guest = false)
    {
        if (_closed) return;
        why = OnlineLobbyModel.CleanText(why, GuestSession.MaxHostText);   // may be relay text: cleaned and clipped before it is shown
        OnlineError = why;
        System.Diagnostics.Trace.WriteLine("online: could not go online: " + why);
        EndOnline(notifyGuests: false);
        if (guest)
        {
            // A guest has no game of its own to fall back on: say why and go back to the launcher.
            GuestJoinFailed(why);
            return;
        }
        RestartHotSeat();
        ShowOnlineMessage("The game could not go online:\n" + why + "\n\nIt continues here as a local game.");
    }

    async void GuestJoinFailed(string why)
    {
        try { await _dialogs.ShowInfoAsync(this, "Could not join the online game:\n" + why, "Online game"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("online message: " + ex.Message); }
        // Never close inside the open that started it.
        Dispatcher.UIThread.Post(() => { if (!_closed) OnlineGuard(LeaveAsGuest); });
    }

    /// <summary>After leaving or a failed start: the game is local again, so hot-seat follows it as before.</summary>
    void RestartHotSeat()
    {
        if (_session == null || _phases == null || _history == null || _hotSeat != null || _session.IsGuest) return;
        CreateHotSeat(_session, _phases, _history);
    }

    // ---------- joining ----------

    /// <summary>Plays as a guest of the room the lobby window joined: the guest session takes over the game and this window's actions.</summary>
    async Task BeginGuestAsync(ModuleSession s, OnlineLaunch o)
    {
        if (o.Client is not { } client || o.Code is not { Length: > 0 } code) { OnlineFailed("The lobby did not hand over a joined connection.", guest: true); return; }
        // The lobby window is gone: a changed certificate on a reconnect must be asked about over this window.
        if (o.Trust != null) o.Trust.Ask = c => RelayTrust.Ask(this, _dialogs, c);
        StopHotSeatForOnline();
        _relayClient = client;
        _online = o;
        _onlineStarting = true;
        SetOnlineStatusText(OnlineLobbyModel.StatusText(code, o.Listing?.Players ?? 1, "joining…"));
        UpdateOnlineUi();
        try
        {
            var password = o.Password ?? "";
            var key = await Task.Run(() => Secrets.GameKey(password, code));
            if (!StillCurrent(s, client)) return;
            _transport = new RelayTransport(client, key);
            _onlineCode = GameCode.Normalize(code);
            // The rejoin token lives under this window's layout root (AppDataRoot.Path unless a test gives its own), so two windows on two
            // roots are two players: one never takes the other's seat with its token.
            var g = new GuestSession(s, _transport, code, o.PlayerName, PostToUi, new RejoinTokens(_layoutRoot));
            _guest = g;
            _remote = new RemoteActions(g);
            _remote.Refused += OnGuestNotice;
            if (_actions != null) _actions.Inner = _remote;
            g.Notice += OnGuestNotice;
            g.StateChanged += OnGuestStateChanged;
            _onlineStarting = false;
            System.Diagnostics.Trace.WriteLine("online: joined lobby " + _onlineCode + " as a guest");
            UpdateOnlineUi();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!StillCurrent(s, client)) return;
            OnlineFailed(ex.Message, guest: true);
        }
    }

    void OnGuestNotice(string message)
    {
        if (_closed) return;
        SetStatus(message);
        System.Diagnostics.Trace.WriteLine("online notice: " + message);
    }

    void OnGuestStateChanged()
    {
        if (_guest is not { } g || _closed) return;
        UpdateOnlineUi();
        switch (g.State)
        {
            case GuestState.ChoosingSide when !_sideAsked:
                // A rejoining seat that already holds sides gets its snapshot right after the welcome: ask only if still choosing a moment later.
                _sideTimer?.Stop();
                _sideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _sideTimer.Tick += (_, _) =>
                {
                    _sideTimer?.Stop();
                    if (!_closed) RunOnline(AskSideAsync);
                };
                _sideTimer.Start();
                break;
            case GuestState.Paused when g.NoAnswer:
                // The join was never answered: offer to leave (staying keeps waiting; a host or relay coming back tries again).
                System.Diagnostics.Trace.WriteLine("online: the host did not answer the join");
                Dispatcher.UIThread.Post(() => RunOnline(async () =>
                {
                    if (_closed || _guest != g || !g.NoAnswer) return;
                    if (await _dialogs.AskYesNoAsync(this, GuestSession.NoAnswerText + ".\n\nLeave the online game and go back to the launcher?",
                            "Online game", defaultNo: false) && _guest == g && !_closed)
                        LeaveAsGuest();
                }));
                break;
            case GuestState.Refused:
                var why = g.StatusText ?? "The host removed you from the game.";
                System.Diagnostics.Trace.WriteLine("online: left the game: " + why);
                Dispatcher.UIThread.Post(() => RunOnline(async () =>
                {
                    // A second Refused event or a Leave racing this one has already ended this guest: leave once only.
                    if (_closed || _guest != g) return;
                    await _dialogs.ShowInfoAsync(this, why, "Online game");
                    if (!_closed && _guest == g) LeaveAsGuest();
                }));
                break;
        }
    }

    /// <summary>Runs an online flow that never throws (failures are traced and shown in the status bar).</summary>
    async void RunOnline(Func<Task> f)
    {
        try { await f(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("online: " + ex.Message);
            try { if (!_closed) SetStatus(ex.Message); } catch (Exception) { }
        }
    }

    /// <summary>The guest's side choice after the host's welcome (once; Game → Players… asks again while still choosing).</summary>
    internal async Task AskSideAsync()
    {
        if (_guest is not { State: GuestState.ChoosingSide } g || _session?.Rules is not { } pack || _sideAsked) return;
        _sideAsked = true;
        var free = pack.Sides.Where(x => x?.Id != null && g.FreeSides.Contains(x.Id)).ToList();
        if (free.Count == 0)
        {
            ShowOnlineMessage(SideChoiceModel.NoFreeSideText);
            return;
        }
        SideQuestionsAsked++;
        var chosen = await _dialogs.ChooseSidesAsync(this, SideChoiceModel.ChooseTitle, SideChoiceModel.ChoosePrompt, free, null, OnlineLobbyModel.JoinNote);
        if (chosen is { Count: > 0 } && _guest == g) g.ChooseSides(chosen);
        else _sideAsked = false;   // not chosen yet: Game → Players… offers it again
    }

    // ---------- leaving ----------

    /// <summary>Game → Leave Online Game: a guest goes back to the launcher; the host plays on locally.</summary>
    internal async Task LeaveOnlineGameAsync()
    {
        // WinForms asks OK / Cancel (OK first): here Yes / No with Yes as the default.
        if (_guest is { } g)
        {
            if (!await _dialogs.AskYesNoAsync(this, "Leave the online game and go back to the launcher?", "Leave Online Game", defaultNo: false)) return;
            if (_guest == g && !_closed) LeaveAsGuest();
            return;
        }
        if (_host == null && !_onlineStarting) return;
        if (!await _dialogs.AskYesNoAsync(this,
                "Leave the online game? The lobby closes and the other players are disconnected. The game continues here as a local game.",
                "Leave Online Game", defaultNo: false)) return;
        if (_closed || (_host == null && !_onlineStarting)) return;
        EndOnline(notifyGuests: true, backToLocal: true);
        RestartHotSeat();
        SetStatus("You left the online game; it continues here as a local game.");
    }

    /// <summary>A guest has no way back to a local game in this window (spec 8a.3): the window closes back to the launcher.</summary>
    void LeaveAsGuest()
    {
        EndOnline(notifyGuests: false);
        if (LauncherAvailable) RunAsync(BackToLauncherAsync);
        else Close();
    }

    /// <summary>
    /// Stops every online part (idempotent; never throws). The host tells its guests first; the relay drops the listing when the host's
    /// link closes.
    /// </summary>
    void EndOnline(bool notifyGuests, bool backToLocal = false)
    {
        notifyGuests |= backToLocal;
        if (_leavingOnline) return;
        _leavingOnline = true;
        try
        {
            _sideTimer?.Stop();
            _sideTimer = null;
            if (_onlineCode != null) OnlineLobbyModel.UnmarkHosted(_onlineCode);
            var hadHost = _host != null;
            if (_host is { } h)
            {
                h.SeatsChanged -= UpdateOnlineUi;
                h.ConnectionChanged -= UpdateOnlineUi;
                try
                {
                    // Leaving to play on: back to a solo game with the full view (no lingering one-side filter). Closing: just tell the guests.
                    if (backToLocal) h.Leave();
                    else if (notifyGuests) h.EndGame();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("online: " + ex.Message); }
                Quiet(h.Dispose);
            }
            if (_guest is { } g)
            {
                g.Notice -= OnGuestNotice;
                g.StateChanged -= OnGuestStateChanged;
                Quiet(g.Dispose);
            }
            if (_remote != null) _remote.Refused -= OnGuestNotice;
            if (_actions != null && _localActions != null && !ReferenceEquals(_actions.Inner, _localActions)) _actions.Inner = _localActions;
            if (_transport is { } t) Quiet(t.Dispose);
            if (_relayClient is { } c) _ = DisposeLater(c, hadHost && notifyGuests ? TimeSpan.FromMilliseconds(400) : TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("online end: " + ex.Message); }
        finally
        {
            _host = null;
            _guest = null;
            _remote = null;
            _transport = null;
            _relayClient = null;
            _online = null;
            _onlineCode = null;
            _onlineStarting = false;
            _sideAsked = false;
            _leavingOnline = false;
        }
        if (!_closed) UpdateOnlineUi();

        static void Quiet(Action a)
        {
            try { a(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("online end: " + ex.Message); }
        }
    }

    /// <summary>Closes a relay client in the background (after a moment, so the last messages leave); never throws.</summary>
    static async Task DisposeLater(RelayClient c, TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay).ConfigureAwait(false);
            await c.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    // ---------- Game menu ----------

    /// <summary>Game → Players…: the host's seats dialog; a guest still choosing picks a side; any other guest is told only the host can.</summary>
    internal async Task ShowPlayersDialogAsync()
    {
        if (_host is { } h)
        {
            await _dialogs.ShowSeatsAsync(this, h, _session?.Rules);
            UpdateOnlineUi();
            return;
        }
        if (_guest is { State: GuestState.ChoosingSide })
        {
            _sideAsked = false;
            await AskSideAsync();
            return;
        }
        if (_guest != null) await _dialogs.ShowInfoAsync(this, HostSession.SidesFixedText, "Players");
    }

    void SendChat(string text)
    {
        if (_host != null)
        {
            var r = _host.Chat(text);
            if (!r.Ok && r.Message != null) SetStatus(OnlineLobbyModel.CleanText(r.Message, GuestSession.MaxHostText));
        }
        else _remote?.Chat(text);
    }

    // ---------- status, banner, restrictions ----------

    void SetOnlineStatusText(string text)
    {
        SbOnline.Text = text;
        SbOnlineBox.IsVisible = true;
    }

    void UpdateOnlineUi()
    {
        if (_closed) return;
        try
        {
            var online = IsOnline;
            var live = online || _onlineStarting;
            if (_gameMenu != null) _gameMenu.IsVisible = live;
            if (_gameLeave != null) { _gameLeave.IsVisible = live; _gameLeave.IsEnabled = live; }
            if (_gamePlayers != null)
            {
                _gamePlayers.IsVisible = live;
                _gamePlayers.IsEnabled = _host != null || _guest is { State: GuestState.ChoosingSide };
                ToolTip.SetTip(_gamePlayers, _guest != null && _guest.State != GuestState.ChoosingSide ? HostSession.SidesFixedText : null);
                ToolTip.SetShowOnDisabled(_gamePlayers, true);
            }
            string? banner = null;
            if (_host is { } h && _onlineCode != null)
            {
                SetOnlineStatusText(OnlineLobbyModel.StatusText(_onlineCode, h.Seats.All.Count(x => x.Connected), h.RelayConnected ? null : "reconnecting…"));
                if (!h.RelayConnected) banner = GuestSession.RelayLostText;
            }
            else if (_guest is { } g && _onlineCode != null)
            {
                var players = _session?.Game?.State.Players.Count is > 0 and var n ? n : _online?.Listing?.Players ?? 1;
                var trouble = g.State switch
                {
                    GuestState.Joining => "joining…",
                    GuestState.ChoosingSide => "choose your side (Game → Players…)",
                    GuestState.Paused => "paused",
                    GuestState.Refused => "left",
                    _ => null,
                };
                SetOnlineStatusText(OnlineLobbyModel.StatusText(_onlineCode, players, trouble));
                banner = OnlineLobbyModel.GuestBanner(g.State, g.StatusText);
            }
            else if (!_onlineStarting) SbOnlineBox.IsVisible = false;
            banner ??= _session?.ViewerBanner;
            OnlineBannerLabel.Text = banner ?? "";
            OnlineBanner.IsVisible = banner != null;
            if (_log is { } log)
            {
                if (online && log.Chat == null) log.SetChat(t => Guard(() => SendChat(t)));
                else if (!online && log.Chat != null) log.SetChat(null);
            }
            ApplyGuestLimits();
            // 5d: UpdateMailboxWatch() (going online stops the turn mailbox; leaving brings it back for a play-by-email game).
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("online ui: " + ex.Message);
        }
    }

    /// <summary>
    /// Guest limits (spec 8a.3): no save, no new or loaded game, no rules editing, no undo. Runs after every UI refresh, so it always wins
    /// over the plain enabling.
    /// </summary>
    void ApplyGuestLimits()
    {
        ApplyGates();
        RefreshHistoryUi();
        // 5d: File → Export Viewer Copy… (guests) and UpdateTurnUi().
    }

    void ShowOnlineMessage(string text)
    {
        if (_closed) return;
        RunOnline(() => _dialogs.ShowInfoAsync(this, text, "Online game"));
    }
}
