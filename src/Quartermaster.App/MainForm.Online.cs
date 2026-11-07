using Quartermaster.Core;
using Quartermaster.Net;
using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// Live online play in the game window (spec 5, 8a; launcher spec 3.3, 4): hosting a lobby (relay connect, room, <see cref="HostSession"/>),
/// playing as a guest (<see cref="GuestSession"/> with <see cref="RemoteActions"/> as the window's action port), the chat line, the
/// <c>Online: {code} · {n} players</c> status, the paused banner, Game → Players… and Game → Leave Online Game, and the guest's limits
/// (no save, no undo, read-only rules). Every network event reaches this window through <see cref="PostToUi"/> and is guarded: nothing
/// here may crash the window, whatever the relay or the other players send.
/// </summary>
sealed partial class MainForm
{
    readonly ToolStripStatusLabel _sbOnline = new() { Visible = false, Name = "status.online", BorderSides = ToolStripStatusLabelBorderSides.Left, BorderStyle = Border3DStyle.Etched };
    readonly Panel _onlineBanner = new() { Dock = DockStyle.Top, Height = 26, Visible = false, BackColor = Color.FromArgb(255, 240, 200) };
    readonly Label _onlineBannerText = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 8, 0), Font = new Font("Tahoma", 8.25f, FontStyle.Bold) };
    readonly List<ToolStripItem> _guestBlocked = new();
    readonly List<ToolStripItem> _needsSave = new();
    ToolStripMenuItem? _gameMenu, _gamePlayers, _gameLeave, _exportViewer;
    LocalActions? _localActions;
    RelayClient? _relayClient;
    RelayTransport? _transport;
    HostSession? _host;
    GuestSession? _guest;
    RemoteActions? _remote;
    OnlineLaunch? _online;
    string? _onlineCode;
    bool _onlineStarting, _sideAsked, _leavingOnline;
    System.Windows.Forms.Timer? _sideTimer;

    /// <summary>The hosted online session (null when not hosting).</summary>
    public HostSession? HostOnline => _host;

    /// <summary>The guest session (null when not playing as a guest).</summary>
    public GuestSession? GuestOnline => _guest;

    /// <summary>The status bar's online text (smoke checks).</summary>
    public string OnlineStatusText => _sbOnline.Visible ? _sbOnline.Text ?? "" : "";

    /// <summary>The banner text over the game ("" when hidden).</summary>
    public string OnlineBannerText => _onlineBanner.Visible ? _onlineBannerText.Text : "";

    /// <summary>The last hosting or joining failure (smoke checks).</summary>
    public string? OnlineError { get; private set; }

    void InitOnline()
    {
        _onlineBanner.Controls.Add(_onlineBannerText);
    }

    /// <summary>Game menu (shown while a game is open): Send Turn…; while online also Players… and Leave Online Game.</summary>
    ToolStripMenuItem BuildGameMenu()
    {
        var game = _gameMenu = new ToolStripMenuItem("&Game") { Visible = false };
        _sendTurn = Item(game.DropDownItems, "Send &Turn…", SendTurn);
        _gameOnlineSep = new ToolStripSeparator { Visible = false };
        game.DropDownItems.Add(_gameOnlineSep);
        _gamePlayers = Item(game.DropDownItems, "&Players…", ShowPlayersDialog);
        _gamePlayers.Visible = false;
        game.DropDownItems.Add(new ToolStripSeparator());
        _gameLeave = Item(game.DropDownItems, "&Leave Online Game", LeaveOnlineGame);
        _gameLeave.Visible = false;
        return game;
    }

    bool IsOnline => _host != null || _guest != null;

    // ---------- marshalling ----------

    /// <summary>The dispatcher the online sessions get: runs the action on this window's thread, never throws, drops it once the window is gone.</summary>
    void PostToUi(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!IsDisposed) OnlineGuard(a); }); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { /* closing */ }
    }

    void OnlineGuard(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("online: " + ex.Message);
            try { SetStatus(ex.Message); } catch (Exception) { }
        }
    }

    bool StillCurrent(ModuleSession s, RelayClient client) =>
        !IsDisposed && ReferenceEquals(_session, s) && ReferenceEquals(_relayClient, client) && !_leavingOnline;

    /// <summary>Online sessions own the view (the host sees its seat's sides; a guest its snapshot): hot-seat stops for good.</summary>
    void StopHotSeatForOnline()
    {
        _hotSeat?.Detach();
        DestroyHotSeat();
    }

    /// <summary>Runs a hosting or joining start; whatever it throws ends as a message, never as an unobserved or unhandled exception.</summary>
    async void StartOnline(Func<Task> start, bool guest)
    {
        try { await start(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { OnlineFailed(ex.Message, guest); }
            catch (Exception inner) when (inner is not OutOfMemoryException) { Program.AppLog("online: " + inner.Message); }
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
        var client = new RelayClient(RelayPrompts.Pins());
        string? down = null;
        void OnDown(string r) => down = r;
        client.Disconnected += OnDown;
        _relayClient = client;
        _online = o;
        _onlineStarting = true;
        SetOnlineStatusText("Online: connecting…");
        try
        {
            await client.ConnectAsync(o.Relay, c => RelayPrompts.Trust(this, c), CancellationToken.None);
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
            Program.AppLog("online: hosting lobby " + draft.Code);
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
        if (IsDisposed) return;
        why = OnlineLobbyModel.CleanText(why, GuestSession.MaxHostText);   // may be relay text: cleaned and clipped before it is shown
        OnlineError = why;
        Program.AppLog("online: could not go online: " + why);
        EndOnline(notifyGuests: false);
        if (guest)
        {
            // A guest has no game of its own to fall back on: say why and go back to the launcher.
            ShowOnlineMessage("Could not join the online game:\n" + why);
            if (!Program.Smoke && IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) LeaveAsGuest(); });   // never close inside the open that started it
            return;
        }
        RestartHotSeat();
        ShowOnlineMessage("The game could not go online:\n" + why + "\n\nIt continues here as a local game.");
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
        if (o.Trust != null) o.Trust.Ask = c => RelayPrompts.Trust(this, c);
        StopHotSeatForOnline();
        _relayClient = client;
        _online = o;
        _onlineStarting = true;
        SetOnlineStatusText(OnlineLobbyModel.StatusText(code, o.Listing?.Players ?? 1, "joining…"));
        try
        {
            var password = o.Password ?? "";
            var key = await Task.Run(() => Secrets.GameKey(password, code));
            if (!StillCurrent(s, client)) return;
            _transport = new RelayTransport(client, key);
            _onlineCode = GameCode.Normalize(code);
            var g = new GuestSession(s, _transport, code, o.PlayerName, PostToUi);
            _guest = g;
            _remote = new RemoteActions(g);
            _remote.Refused += OnGuestNotice;
            if (_actions != null) _actions.Inner = _remote;
            g.Notice += OnGuestNotice;
            g.StateChanged += OnGuestStateChanged;
            _onlineStarting = false;
            Program.AppLog("online: joined lobby " + _onlineCode + " as a guest");
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
        if (IsDisposed) return;
        SetStatus(message);
        Program.AppLog("online notice: " + message);
    }

    void OnGuestStateChanged()
    {
        if (_guest is not { } g || IsDisposed) return;
        UpdateOnlineUi();
        switch (g.State)
        {
            case GuestState.ChoosingSide when !_sideAsked:
                // A rejoining seat that already holds sides gets its snapshot right after the welcome: ask only if still choosing a moment later.
                _sideTimer?.Dispose();
                _sideTimer = new System.Windows.Forms.Timer { Interval = 400 };
                _sideTimer.Tick += (_, _) =>
                {
                    _sideTimer?.Stop();
                    OnlineGuard(AskSide);
                };
                _sideTimer.Start();
                break;
            case GuestState.Paused when g.NoAnswer:
                // The join was never answered: offer to leave (staying keeps waiting; a host or relay coming back tries again).
                Program.AppLog("online: the host did not answer the join");
                if (Program.Smoke) return;
                BeginInvoke(() =>
                {
                    if (IsDisposed || _guest != g || !g.NoAnswer) return;
                    if (MessageBox.Show(this, GuestSession.NoAnswerText + ".\n\nLeave the online game and go back to the launcher?", "Online game",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes && _guest == g)
                        LeaveAsGuest();
                });
                break;
            case GuestState.Refused:
                var why = g.StatusText ?? "The host removed you from the game.";
                Program.AppLog("online: left the game: " + why);
                if (Program.Smoke) return;
                BeginInvoke(() =>
                {
                    if (IsDisposed) return;
                    Info(why, "Online game");
                    LeaveAsGuest();
                });
                break;
        }
    }

    /// <summary>The guest's side choice after the host's welcome (once; Game → Players… asks again while still choosing).</summary>
    void AskSide()
    {
        if (_guest is not { State: GuestState.ChoosingSide } g || _session?.Rules is not { } pack || _sideAsked) return;
        _sideAsked = true;
        var free = pack.Sides.Where(x => x?.Id != null && g.FreeSides.Contains(x.Id)).ToList();
        if (free.Count == 0)
        {
            ShowOnlineMessage("No side is free in this game any more. You can chat; the host can give you a side.");
            return;
        }
        if (Program.Smoke) { Program.AppLog("smoke: side choice offered: " + string.Join(", ", free.Select(x => x.Id))); return; }
        using var dlg = new SideChoiceDialog("Choose your side", "Play these sides:", free, null, OnlineLobbyModel.JoinNote);
        if (dlg.ShowDialog(this) == DialogResult.OK && _guest == g) g.ChooseSides(dlg.Chosen);
        else _sideAsked = false;   // not chosen yet: Game → Players… offers it again
    }

    /// <summary>Smoke: Game → Leave Online Game (no confirmation in smoke runs).</summary>
    public void LeaveOnlineForSmoke() => OnlineGuard(LeaveOnlineGame);

    /// <summary>Smoke: every open map has finished tiling.</summary>
    public bool TilingDoneForSmoke => _maps.Values.All(w => w.IsDisposed || w.TilingDone);

    /// <summary>Smoke: the window with its active map, as --screenshot does.</summary>
    public void SaveScreenshotForSmoke(string path) => SaveScreenshot(this, path, true);

    /// <summary>Smoke: picks sides as the dialog would.</summary>
    public bool ChooseSidesForSmoke(IReadOnlyList<string> sides) => _guest?.ChooseSides(sides) == true;

    /// <summary>Smoke: sends a chat line through the chat box.</summary>
    public bool SendChatForSmoke(string text)
    {
        if (_logDock.Panel?.Chat is not { } box) return false;
        box.SendForSmoke(text);
        return true;
    }

    // ---------- leaving ----------

    void LeaveOnlineGame()
    {
        if (_guest != null)
        {
            if (!Program.Smoke && MessageBox.Show(this, "Leave the online game and go back to the launcher?", "Leave Online Game",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            LeaveAsGuest();
            return;
        }
        if (_host == null && !_onlineStarting) return;
        if (!Program.Smoke && MessageBox.Show(this,
                "Leave the online game? The lobby closes and the other players are disconnected. The game continues here as a local game.",
                "Leave Online Game", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        EndOnline(notifyGuests: true, backToLocal: true);
        RestartHotSeat();
        SetStatus("You left the online game; it continues here as a local game.");
    }

    /// <summary>A guest has no way back to a local game in this window (spec 8a.3): the window closes back to the launcher.</summary>
    void LeaveAsGuest()
    {
        EndOnline(notifyGuests: false);
        if (Program.LauncherHook != null) BackToLauncher();
        else Close();
    }

    /// <summary>Stops every online part (idempotent). The host tells its guests first; the relay drops the listing when the host's link closes.</summary>
    void EndOnline(bool notifyGuests, bool backToLocal = false)
    {
        notifyGuests |= backToLocal;
        if (_leavingOnline) return;
        _leavingOnline = true;
        try
        {
            _sideTimer?.Dispose();
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
                catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("online: " + ex.Message); }
                h.Dispose();
            }
            if (_guest is { } g)
            {
                g.Notice -= OnGuestNotice;
                g.StateChanged -= OnGuestStateChanged;
                g.Dispose();
            }
            if (_remote != null) _remote.Refused -= OnGuestNotice;
            if (_actions != null && _localActions != null && !ReferenceEquals(_actions.Inner, _localActions)) _actions.Inner = _localActions;
            _transport?.Dispose();
            if (_relayClient is { } c) _ = DisposeLater(c, hadHost && notifyGuests ? TimeSpan.FromMilliseconds(400) : TimeSpan.Zero);
            _host = null;
            _guest = null;
            _remote = null;
            _transport = null;
            _relayClient = null;
            _online = null;
            _onlineCode = null;
            _onlineStarting = false;
            _sideAsked = false;
        }
        finally { _leavingOnline = false; }
        if (!IsDisposed) UpdateOnlineUi();
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

    void ShowPlayersDialog()
    {
        if (_host != null)
        {
            using var dlg = new SeatsDialog(_host, _session?.Rules);
            dlg.ShowDialog(this);
            UpdateOnlineUi();
            return;
        }
        if (_guest is { State: GuestState.ChoosingSide })
        {
            _sideAsked = false;
            AskSide();
            return;
        }
        if (_guest != null) Info(HostSession.SidesFixedText, "Players");
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
        _sbOnline.Text = text;
        _sbOnline.Visible = true;
    }

    void UpdateOnlineUi()
    {
        if (IsDisposed) return;
        try
        {
            var online = IsOnline;
            if (_gameMenu != null) _gameMenu.Visible = online || _onlineStarting || _session?.Game != null;
            // Players… and Leave Online Game belong to a live session; Send Turn… (first) to a local game.
            if (_gameOnlineSep != null) _gameOnlineSep.Visible = online || _onlineStarting;
            if (_gameLeave != null) { _gameLeave.Visible = online || _onlineStarting; _gameLeave.Enabled = online || _onlineStarting; }
            if (_gamePlayers != null) _gamePlayers.Visible = online || _onlineStarting;
            if (_gamePlayers != null)
            {
                _gamePlayers.Enabled = _host != null || _guest is { State: GuestState.ChoosingSide };
                _gamePlayers.ToolTipText = _guest != null && _guest.State != GuestState.ChoosingSide ? HostSession.SidesFixedText : "";
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
            else if (!_onlineStarting) _sbOnline.Visible = false;
            banner ??= _session?.ViewerBanner;
            _onlineBannerText.Text = banner ?? "";
            _onlineBanner.Visible = banner != null;
            if (_logDock.Panel is { } log)
            {
                if (online && log.Chat == null) log.SetChat(t => Guard(() => SendChat(t)));
                else if (!online && log.Chat != null) log.SetChat(null);
            }
            ApplyGuestLimits();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("online ui: " + ex.Message);
        }
    }

    /// <summary>
    /// Guest limits (spec 8a.3): no save (Export Viewer Copy… instead), no new or loaded game, no rules editing, no undo; a viewer copy
    /// cannot be saved either. Runs after every UI refresh, so it always wins over the plain enabling.
    /// </summary>
    void ApplyGuestLimits()
    {
        var guest = _session?.IsGuest == true;
        foreach (var i in _guestBlocked)
        {
            if (guest) i.Enabled = false;
            Tip(i, guest ? ModuleSession.GuestBusyText : null);
        }
        foreach (var i in _needsSave)
        {
            if (_session?.Game != null && _session.CanSave != true) i.Enabled = false;
            Tip(i, guest ? ModuleSession.GuestSaveText : _session?.IsViewerCopy == true ? ModuleSession.ViewerCopyText : null);
        }

        // Only our own tooltips are set or cleared; others (shortcut hints) stay.
        static void Tip(ToolStripItem i, string? tip)
        {
            if (tip != null) i.ToolTipText = tip;
            else if (i.ToolTipText is ModuleSession.GuestBusyText or ModuleSession.GuestSaveText or ModuleSession.ViewerCopyText) i.ToolTipText = null;
        }
        if (_exportViewer != null)
        {
            _exportViewer.Visible = guest;
            _exportViewer.Enabled = guest && _session?.Game != null;
        }
        UpdateTurnUi();
    }

    /// <summary>File → Export Viewer Copy… (guests): the filtered view as a read-only save.</summary>
    void ExportViewerCopy()
    {
        if (_session is not { IsGuest: true, Game: not null } s || Program.Smoke) return;
        using var dlg = new SaveFileDialog { Filter = "Quartermaster saves (*.qsave)|*.qsave", DefaultExt = "qsave", Title = "Export Viewer Copy", FileName = "Viewer copy" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        s.ExportViewerCopy(dlg.FileName);
        SetStatus("Viewer copy saved: " + Path.GetFileName(dlg.FileName));
    }

    void ShowOnlineMessage(string text)
    {
        if (Program.Smoke) { Program.AppLog("online: " + text.Replace('\n', ' ')); return; }
        if (IsDisposed) return;
        MessageBox.Show(this, text, "Online game", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
