using Quartermaster.Core;
using Quartermaster.Net;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>
/// The launcher's Online lobby for one module (launcher spec 3.3, 4a): the relay address (remembered) with its connection state, the
/// other people's lobbies for this module (refreshed every 10 s), and Join…, Create lobby…, Host a saved game…, Refresh.
/// <see cref="Result"/> is what to play once the window closed with OK. A join is completed here (password checked by the relay) and the
/// joined relay client is handed to the game window; hosting connects from the game window once the game is loaded.
/// Network trouble only ever shows as text in the window.
/// </summary>
sealed class OnlineLobbyForm : Form
{
    public const string JoinText = "Join…", CreateText = "Create lobby…", HostSavedText = "Host a saved game…", RefreshText = "Refresh";
    static readonly TimeSpan AutoRefresh = TimeSpan.FromSeconds(10);

    readonly ModuleRow _row;
    readonly ModuleLibrary _library;
    readonly SettingsStore _store;
    readonly TextBox _relay = new() { Width = 260 };
    readonly Label _state = new() { AutoSize = true, MaximumSize = new Size(300, 18), AutoEllipsis = true, ForeColor = SystemColors.GrayText, Margin = new Padding(8, 6, 3, 3) };
    readonly Button _close = new() { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(75, 26) };
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
    };
    readonly ImageList _icons = new() { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
    readonly Button _join = new() { Text = JoinText, AutoSize = true, MinimumSize = new Size(80, 26) };
    readonly Button _create = new() { Text = CreateText, AutoSize = true, MinimumSize = new Size(100, 26) };
    readonly Button _hostSaved = new() { Text = HostSavedText, AutoSize = true, MinimumSize = new Size(120, 26) };
    readonly Button _refresh = new() { Text = RefreshText, AutoSize = true, MinimumSize = new Size(75, 26) };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = (int)AutoRefresh.TotalMilliseconds };
    RelayClient? _browser;
    Uri? _browserUri;
    string? _lastTrouble;
    bool _busy, _closing, _lastOk, _joining;
    IReadOnlyList<LobbyRow> _rows = Array.Empty<LobbyRow>();

    public LaunchRequest? Result { get; private set; }

    public OnlineLobbyForm(ModuleRow row, ModuleLibrary library, SettingsStore store)
    {
        _row = row;
        _library = library;
        _store = store;
        var title = string.IsNullOrWhiteSpace(row.Entry.Title) ? Path.GetFileNameWithoutExtension(row.Entry.Path) : row.Entry.Title;
        Text = OnlineLobbyModel.Title(title);
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(640, 380);
        MinimumSize = new Size(520, 300);
        ShowIcon = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        _icons.Images.Add("lock", LockIcon());
        _list.SmallImageList = _icons;
        _list.Columns.Add("Lobby", 190);
        _list.Columns.Add("Host", 120);
        _list.Columns.Add("Free sides", 180);
        _list.Columns.Add("Players", 70);

        _relay.Text = store.Current.RelayAddress ?? "";
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(8, 8, 8, 4) };
        top.Controls.Add(new Label { Text = "Relay:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
        top.Controls.Add(_relay);
        top.Controls.Add(_state);
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 0) };
        listHost.Controls.Add(_list);
        var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false };
        left.Controls.AddRange(new Control[] { _join, _create, _hostSaved, _refresh });
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
        right.Controls.Add(_close);
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(8, 6, 8, 6) };
        buttons.Controls.Add(left);
        buttons.Controls.Add(right);
        Controls.Add(listHost);
        Controls.Add(top);
        Controls.Add(buttons);
        CancelButton = _close;

        _join.Click += async (_, _) => await GuardedAsync(JoinSelected);
        _list.DoubleClick += async (_, _) => await GuardedAsync(JoinSelected);
        _create.Click += async (_, _) => await GuardedAsync(CreateLobby);
        _hostSaved.Click += (_, _) => Guarded(HostSaved);
        _refresh.Click += async (_, _) => await GuardedAsync(() => RefreshAsync(manual: true));
        _relay.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            await GuardedAsync(() => RefreshAsync(manual: true));
        };
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _timer.Tick += async (_, _) =>
        {
            if (_lastOk && !_busy && Visible) await GuardedAsync(() => RefreshAsync(manual: false));
        };
        Load += async (_, _) =>
        {
            _timer.Start();
            UpdateButtons();
            if (_relay.Text.Trim().Length > 0) await GuardedAsync(() => RefreshAsync(manual: true));
            else SetState("Enter the relay address and press Refresh.");
        };
        FormClosing += (_, e) =>
        {
            // A join in flight holds a relay connection that only this window can hand over or close: wait for it.
            if (_joining && e.CloseReason == CloseReason.UserClosing && DialogResult != DialogResult.OK) { e.Cancel = true; return; }
            _closing = true;
            _timer.Stop();
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing = true;
            _timer.Dispose();
            _icons.Dispose();
            DropBrowser();
        }
        base.Dispose(disposing);
    }

    // ---------- helpers ----------

    void Guarded(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Problem(ex.Message); }
    }

    async Task GuardedAsync(Func<Task> a)
    {
        try { await a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Problem(ex.Message); }
    }

    void Problem(string message)
    {
        Program.AppLog("online lobby: " + message);
        if (!Program.Smoke && !IsDisposed) MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    void SetState(string text)
    {
        if (IsDisposed) return;
        _state.Text = text;
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        if (IsDisposed) return;
        UseWaitCursor = busy;
        UpdateButtons();
    }

    void UpdateButtons()
    {
        if (IsDisposed) return;
        var sel = Selected;
        _join.Enabled = !_busy && sel != null && sel.CannotJoin == null;
        _create.Enabled = _hostSaved.Enabled = _refresh.Enabled = !_busy;
        _relay.Enabled = !_busy;
        _close.Enabled = !_joining;
    }

    LobbyRow? Selected => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as LobbyRow : null;

    Uri? RelayUri(bool showError)
    {
        var uri = OnlineLobbyModel.ParseRelay(_relay.Text, out var error);
        if (uri == null && showError) { SetState(error ?? OnlineLobbyModel.BadAddressText); if (!Program.Smoke) MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Information); }
        return uri;
    }

    void DropBrowser()
    {
        var b = _browser;
        _browser = null;
        _browserUri = null;
        if (b == null) return;
        b.Disconnected -= OnBrowserDown;
        _ = b.DisposeAsync().AsTask().ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    }

    void OnBrowserDown(string reason)
    {
        _lastTrouble = reason;
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { if (!_closing) SetState(reason); }); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
    }

    // ---------- the table ----------

    /// <summary>Connects (again, when the address changed or the last try failed) and lists this module's lobbies.</summary>
    public async Task RefreshAsync(bool manual)
    {
        if (_busy || _closing) return;
        if (RelayUri(manual) is not { } uri) return;
        if (_row.Entry.Sha256 is not { Length: > 0 } sha) { SetState("This module is still being checked; try again in a moment."); return; }
        SetBusy(true);
        try
        {
            if (_browser == null || _browserUri != uri || !_lastOk)
            {
                DropBrowser();
                SetState("Connecting…");
                _lastTrouble = null;
                var b = new RelayClient(RelayPrompts.Pins());
                b.Disconnected += OnBrowserDown;
                _browser = b;
                _browserUri = uri;
                await b.ConnectAsync(uri, c => RelayPrompts.Trust(this, c), CancellationToken.None);
            }
            if (_closing || _browser is not { } client) return;
            var listings = await client.ListLobbiesAsync(sha);
            if (_closing) return;
            _lastOk = true;
            Show(OnlineLobbyModel.Rows(listings, sha, OnlineLobbyModel.HostedCodes));
            SetState($"Connected to {OnlineLobbyModel.HostPort(uri)} — {_rows.Count} {(_rows.Count == 1 ? "lobby" : "lobbies")}");
            if (!string.Equals(_store.Current.RelayAddress, _relay.Text.Trim(), StringComparison.Ordinal))
                _store.Update(s => s with { RelayAddress = _relay.Text.Trim() });
        }
        catch (RelayException ex)
        {
            _lastOk = false;
            SetState(_lastTrouble ?? ex.Message);
        }
        finally { SetBusy(false); }
    }

    void Show(IReadOnlyList<LobbyRow> rows)
    {
        if (IsDisposed) return;
        var keep = Selected?.Listing.Code;
        _rows = rows;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var r in rows)
            {
                var item = new ListViewItem(new[] { r.Lobby, r.Host, r.FreeSides, r.Players }) { Tag = r, ImageKey = r.Locked ? "lock" : "" };
                if (r.CannotJoin != null) { item.ForeColor = SystemColors.GrayText; item.ToolTipText = r.CannotJoin; }
                _list.Items.Add(item);
                if (r.Listing.Code == keep) item.Selected = true;
            }
            _list.ShowItemToolTips = true;
        }
        finally { _list.EndUpdate(); }
        UpdateButtons();
    }

    public IReadOnlyList<LobbyRow> RowsForSmoke => _rows;

    /// <summary>The connection state shown next to the relay field.</summary>
    public string StateText => _state.Text;

    static Bitmap LockIcon()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(90, 90, 90), 1.6f);
        g.DrawArc(pen, 4.5f, 1.5f, 7, 8, 180, 180);
        g.DrawLine(pen, 4.5f, 5.5f, 4.5f, 7.5f);
        g.DrawLine(pen, 11.5f, 5.5f, 11.5f, 7.5f);
        using var body = new SolidBrush(Color.FromArgb(200, 150, 30));
        g.FillRectangle(body, 3, 7, 10, 8);
        using var edge = new Pen(Color.FromArgb(120, 85, 10));
        g.DrawRectangle(edge, 3, 7, 10, 8);
        return bmp;
    }

    string DefaultName() => _store.Current.PlayerName is { Length: > 0 } n ? n : ChatText.CleanName(Environment.UserName) ?? "Player";

    void Finish(LaunchRequest req)
    {
        Result = req;
        DialogResult = DialogResult.OK;
        Close();
    }

    // ---------- join ----------

    async Task JoinSelected()
    {
        if (_busy || Selected is not { } row) return;
        if (row.CannotJoin is { } why) { Problem(why); return; }
        using var dlg = new JoinDialog(row, DefaultName());
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var req = await JoinAsync(row.Listing, dlg.PlayerName, dlg.Password);
        if (req != null) Finish(req);
    }

    /// <summary>Connects a fresh relay client and joins the room (the relay checks the password). Null (and a message) when it failed.</summary>
    public async Task<LaunchRequest?> JoinAsync(LobbyListing listing, string playerName, string password)
    {
        if (RelayUri(true) is not { } uri) return null;
        var name = OnlineLobbyModel.CleanText(listing.Name);   // listings are untrusted: only cleaned text is shown
        _joining = true;
        SetBusy(true);
        SetState("Joining…");
        // The game window takes over the certificate question once it owns the client (a changed certificate on a reconnect).
        var trust = new CertificateTrust { Ask = c => RelayPrompts.Trust(this, c) };
        var client = new RelayClient(RelayPrompts.Pins());
        string? down = null;
        void OnDown(string r) => down = r;
        client.Disconnected += OnDown;
        try
        {
            await client.ConnectAsync(uri, trust.Check, CancellationToken.None);
            var code = GameCode.Normalize(listing.Code);
            var verifier = await Task.Run(() => Secrets.AuthVerifier(password ?? "", code));
            await client.JoinRoomAsync(code, verifier);
            client.Disconnected -= OnDown;
            if (_closing || IsDisposed)
            {
                // The window went away while joining: nobody takes the connection, so it closes here.
                await client.DisposeAsync();
                return null;
            }
            _store.Update(s => s with { PlayerName = playerName });
            SetState("Joined " + name);
            return new LaunchRequest(_row.Entry.Path, null, LaunchKind.JoinOnline, null, null,
                new OnlineLaunch(uri, playerName, password ?? "", null, null, code, listing, client, trust));
        }
        catch (RelayException ex)
        {
            client.Disconnected -= OnDown;
            await client.DisposeAsync();
            var msg = OnlineLobbyModel.CleanText(down != null && ex.Message == "Not connected to the relay." ? down : ex.Message, 300);   // relay text is untrusted too
            SetState(msg);
            Problem("Could not join " + name + ":\n" + msg);
            return null;
        }
        finally { _joining = false; SetBusy(false); }
    }

    // ---------- hosting ----------

    /// <summary>The module's rules pack and its path, or null (and a message) when online play is not possible with it.</summary>
    (string Path, RulesPack Pack)? HostPack()
    {
        var path = _library.RulesPackFor(_row.Entry);
        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path))) { Problem(OnlineLobbyModel.NeedsPackText); return null; }
        RulesPack pack;
        try { pack = RulesPackIo.Load(path); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Problem("The rules pack could not be read: " + ex.Message); return null; }
        if (OnlineLobbyModel.ValidateHostSides(pack, new[] { "?" }) == OnlineLobbyModel.NeedsPackText) { Problem(OnlineLobbyModel.NeedsPackText); return null; }
        return (path, pack);
    }

    async Task CreateLobby()
    {
        if (_busy || RelayUri(true) is not { } uri || HostPack() is not { } hp) return;
        if (_row.Missing) { Problem("This module's file was not found."); return; }
        SetBusy(true);
        ModuleSession? session = null;
        try
        {
            var modulePath = _row.Entry.Path;
            session = await Task.Run(() => ModuleSession.Open(modulePath));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Problem($"Could not open '{_row.Entry.Path}':\n{ex.Message}");
            return;
        }
        finally { SetBusy(false); }
        using (session)
        {
            if (IsDisposed || _closing) return;
            var name = DefaultName();
            using var dlg = new SetupChooserDialog(session.Model, GameMode.Online, _store.Current.LastDifficulty, hp.Pack, preselectNew: false,
                hostName: name, lobbyName: $"{name}'s game");
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Request is not { } req || req.Players is not { Count: 1 } players) return;
            var host = players[0];
            _store.Update(s => s with { LastDifficulty = req.Difficulty, PlayerName = host.Name });
            Finish(new LaunchRequest(_row.Entry.Path, hp.Path, LaunchKind.HostOnline, req with { Players = null }, null,
                new OnlineLaunch(uri, host.Name, req.Password ?? "", req.LobbyName, host.Sides.ToList())));
        }
    }

    void HostSaved()
    {
        if (_busy || RelayUri(true) is not { } uri || HostPack() is not { } hp) return;
        using var open = new OpenFileDialog { Filter = "Quartermaster saves (*.qsave)|*.qsave|All files (*.*)|*.*", Title = "Host a saved game" };
        if (open.ShowDialog(this) != DialogResult.OK) return;
        if (OnlineLobbyModel.CheckSave(open.FileName, _row.Entry.Sha256) is { } bad) { Problem(bad); return; }
        var name = DefaultName();
        using var dlg = new HostSavedDialog(hp.Pack, name, $"{name}'s game", OnlineLobbyModel.SuggestedHostSides(open.FileName, hp.Pack));
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _store.Update(s => s with { PlayerName = dlg.PlayerName });
        Finish(new LaunchRequest(_row.Entry.Path, hp.Path, LaunchKind.HostOnline, null, open.FileName,
            new OnlineLaunch(uri, dlg.PlayerName, dlg.Password, dlg.LobbyName, dlg.HostSides)));
    }
}

/// <summary>Join…: the player name, the password when the lobby has one, and the note that the host's copy runs the game.</summary>
sealed class JoinDialog : Form
{
    readonly TextBox _name = new() { Width = 200, MaxLength = ChatText.MaxName };
    readonly TextBox _password = new() { Width = 200, UseSystemPasswordChar = true };

    public JoinDialog(LobbyRow row, string playerName)
    {
        Text = "Join " + row.Lobby;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        ShowIcon = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(12), Dock = DockStyle.Fill };
        t.Controls.Add(new Label { Text = $"Hosted by {row.Host} · free sides: {row.FreeSides}", AutoSize = true, Margin = new Padding(3, 0, 3, 8) }, 0, 0);
        t.SetColumnSpan(t.GetControlFromPosition(0, 0)!, 2);
        t.Controls.Add(new Label { Text = "Your name:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, 0, 1);
        _name.Text = playerName;
        t.Controls.Add(_name, 1, 1);
        if (row.Locked)
        {
            t.Controls.Add(new Label { Text = "Password:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, 0, 2);
            t.Controls.Add(_password, 1, 2);
        }
        var note = new Label { Text = OnlineLobbyModel.JoinNote + " You see only your sides' view; the host's rules pack is sent to you.", AutoSize = true, MaximumSize = new Size(320, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 10, 3, 3) };
        t.Controls.Add(note, 0, 3);
        t.SetColumnSpan(note, 2);
        var ok = new Button { Text = "Join", DialogResult = DialogResult.OK, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 75 };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(3, 10, 3, 3) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        t.Controls.Add(buttons, 0, 4);
        t.SetColumnSpan(buttons, 2);
        Controls.Add(t);
        AcceptButton = ok;
        CancelButton = cancel;
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK || PlayerName.Length > 0) return;
            MessageBox.Show(this, HostSession.NoNameText, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            e.Cancel = true;
        };
    }

    public string PlayerName => ChatText.CleanName(_name.Text) ?? "";
    public string Password => _password.Text;
}

/// <summary>Host a saved game…: lobby name, password (with the open-lobby warning), the host's name and own sides.</summary>
sealed class HostSavedDialog : Form
{
    readonly TextBox _lobby = new() { Width = 220, MaxLength = NewGameRules.MaxLobbyName };
    readonly TextBox _password = new() { Width = 220, UseSystemPasswordChar = true };
    readonly Label _warning = new() { Text = OnlineLobbyModel.OpenLobbyWarning, AutoSize = true, ForeColor = Color.DarkGoldenrod };
    readonly HostSidesPanel _sides;

    public HostSavedDialog(RulesPack pack, string playerName, string lobbyName, IReadOnlyCollection<string> hostSides)
    {
        Text = "Host a saved game";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        ShowIcon = false;
        ClientSize = new Size(420, 360);
        _sides = new HostSidesPanel(pack, playerName, hostSides) { Dock = DockStyle.Fill };
        _lobby.Text = lobbyName;
        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8) };
        top.Controls.Add(new Label { Text = "Lobby name:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, 0, 0);
        top.Controls.Add(_lobby, 1, 0);
        top.Controls.Add(new Label { Text = "Password:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, 0, 1);
        top.Controls.Add(_password, 1, 1);
        top.Controls.Add(_warning, 1, 2);
        _password.TextChanged += (_, _) => _warning.Visible = _password.Text.Length == 0;
        var group = new GroupBox { Text = "You and the open seats", Dock = DockStyle.Fill, Padding = new Padding(6) };
        group.Controls.Add(_sides);
        var ok = new Button { Text = "Host", DialogResult = DialogResult.OK, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 75 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 34, Padding = new Padding(4) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        Controls.Add(group);
        Controls.Add(top);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK) return;
            var problem = LobbyName.Length == 0 ? "Give the lobby a name." : _sides.Problem;
            if (problem == null) return;
            MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            e.Cancel = true;
        };
    }

    public string LobbyName => (ChatText.Clean(_lobby.Text) ?? "") is var n && n.Length > NewGameRules.MaxLobbyName ? n[..NewGameRules.MaxLobbyName] : n;
    public string Password => _password.Text;
    public string PlayerName => _sides.PlayerName;
    public IReadOnlyList<string> HostSides => _sides.HostSides;
}

/// <summary>Certificate trust prompts (trust on first use) and where trusted fingerprints are kept.</summary>
static class RelayPrompts
{
    /// <summary>Trusted relay certificates (per host:port) under the app data root.</summary>
    public static PinStore Pins() => new(Path.Combine(AppDataRoot.Path, "relay-pins.json"));

    /// <summary>
    /// Asks whether to trust an unknown or changed self-signed certificate (public-CA certificates never get here). Runs on any thread:
    /// the question is shown on <paramref name="owner"/>'s thread. Smoke runs (in-process relay) trust without asking.
    /// </summary>
    public static bool Trust(Control owner, CertificateCheck c)
    {
        if (Program.Smoke) { Program.AppLog("smoke: trusted the relay certificate of " + c.HostPort); return true; }
        bool Ask()
        {
            if (owner.IsDisposed) return false;
            var (title, message, changed) = OnlineLobbyModel.CertificatePrompt(c);
            return MessageBox.Show(owner, message, title, MessageBoxButtons.YesNo, changed ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                changed ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1) == DialogResult.Yes;
        }
        try
        {
            if (owner.IsDisposed || !owner.IsHandleCreated) return false;
            return owner.InvokeRequired ? (bool)owner.Invoke(Ask) : Ask();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { return false; }
    }
}
