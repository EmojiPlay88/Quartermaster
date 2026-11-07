using Quartermaster.Core;
using Quartermaster.Net;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>
/// Turn files (spec 6; launcher spec 3.2): Game → Send Turn…, File → Open Turn File… (and the launcher's Open turn file…), the replay bar,
/// the "who are you" prompt and the relay mailbox ("New turn waiting from {player}"). A play-by-email game is not a hot-seat game: while
/// one is open there is no handover, and the view is the local player's sides (public only while a turn is being replayed). While a turn
/// replays every mutating path is off (the action port refuses, the menus are disabled) and nothing is saved.
/// </summary>
sealed partial class MainForm
{
    readonly ReplayBar _replayBar = new();
    /// <summary>Menu items and tools that change the game or its rules: disabled while a turn replays.</summary>
    readonly List<ToolStripItem> _replayBlocked = new();
    /// <summary>Mailbox passwords typed this session (used when the protected copy cannot be stored or read back).</summary>
    readonly Dictionary<string, string> _mailboxPasswords = new(StringComparer.OrdinalIgnoreCase);
    ToolStripMenuItem? _sendTurn, _openTurn;
    ToolStripSeparator? _gameOnlineSep;
    MailboxService? _mailbox;
    TurnReplayModel? _replay;
    ReplayContext? _replayCtx;
    /// <summary>The open game's identity when it is a play-by-email game (null for solo, hot-seat and online games).</summary>
    TurnIdentity? _turnIdentity;
    string? _watchKey;
    (TurnFile Turn, string ItemId)? _waiting;

    /// <summary>What to put back when a replay fails (the previous game returns): its save target and dirty state.</summary>
    sealed record ReplayContext(TurnIdentity Identity, string? ItemId, string? SavePath, bool Dirty, bool Recovered, string? GameId, PlayerDef? HotSeatCurrent);

    /// <summary>The replay running now (null when none).</summary>
    public TurnReplayModel? TurnReplay => _replay;

    /// <summary>The alert bar's turn notice ("" when none).</summary>
    public string TurnNoticeText => _alertBar.NoticeText ?? "";

    void InitTurns()
    {
        _replayBar.Failed += ex => ShowError("The turn could not be replayed:\n" + ex.Message);
        _alertBar.NoticeClicked += () => Guard(OpenWaitingTurn);
        _mailbox = new MailboxService(_ => new RelayClient(RelayPrompts.Pins()), PostToUi, null, c => RelayPrompts.Trust(this, c));
        _mailbox.TurnWaiting += OnTurnWaiting;
        _mailbox.Status += OnMailboxStatus;
        // A played turn whose replayed game was lost (the open game is back at its start) is raised again, not deleted.
        _mailbox.OpenGameHash = gid => _session is { Game: { } g, IsReplaying: false }
            && string.Equals(g.State.GameId, gid, StringComparison.OrdinalIgnoreCase) ? StateHash.Of(g.State) : null;
    }

    // ---------- state of the open game ----------

    /// <summary>
    /// After any game replacement. The start of a replayed turn changes nothing (its view stays public-only until the replay ends); a
    /// replay taken over by another game just loses its bar. Otherwise the open game decides: a play-by-email game gets its player's view
    /// and its mailbox watch, any other local game gets hot-seat back.
    /// </summary>
    void OnTurnGameReplaced()
    {
        if (_session is not { } s) return;
        (TurnFile Turn, string ItemId)? abandoned = null;
        if (_replay is { Done: false } m && !s.IsReplaying)
        {
            if (_replayCtx?.ItemId is { } item) abandoned = (m.Turn, item);
            EndReplayUi();
        }
        if (s.IsReplaying) { UpdateTurnUi(); return; }
        // A failed replay brings the previous game back with its view: hot-seat returns without a fresh handover.
        FollowOpenGame(beginHotSeat: _replay is not { Done: true, Error: not null });
        // An abandoned mailbox turn is still in the mailbox: its notice comes back when the same game is open again.
        if (abandoned is { } a) OnTurnWaiting(a.Turn, a.ItemId);
    }

    /// <param name="watch">False when the caller restarts the mailbox watch itself (after deleting a replayed item).</param>
    void FollowOpenGame(bool beginHotSeat, bool watch = true)
    {
        var s = _session;
        var g = s?.Game;
        TurnIdentity? id = null;
        if (g != null && s is { IsGuest: false, IsViewerCopy: false } && TurnIdentity.IsValidGameId(g.State.GameId))
        {
            try { id = TurnIdentity.Load(g.State.GameId); }
            catch (InvalidDataException) { }
        }
        if (s != null && g != null && TurnUiModel.IsTurnGame(g.State, id))
        {
            _turnIdentity = id;
            StopHotSeatForTurns();
            s.SetViewSides(TurnUiModel.ViewSides(g.State, id!.LocalPlayerId));
        }
        else
        {
            _turnIdentity = null;
            if (s != null && _hotSeat == null && !IsOnline && !_onlineStarting && !s.IsGuest && _phases != null && _history != null)
            {
                CreateHotSeat(s, _phases, _history);
                if (beginHotSeat) _hotSeat?.BeginOpenGame();
                else _hotSeat?.Adopt(_replayCtx?.HotSeatCurrent);   // the same game came back: the same player is still at the screen
                UpdatePlayerButton();
            }
        }
        if (_waiting is { } w && !string.Equals(w.Turn.GameId, g?.State.GameId, StringComparison.OrdinalIgnoreCase)) ClearWaiting();
        if (watch) UpdateMailboxWatch();
        UpdateTurnUi();
    }

    /// <summary>A play-by-email game owns the view: hot-seat stops following it (detached, so no late event reaches the view).</summary>
    void StopHotSeatForTurns()
    {
        _hotSeat?.Detach();
        DestroyHotSeat();
    }

    /// <summary>Send Turn and Open Turn File follow the session; every replay-blocked item is off (and says why) while a turn replays.</summary>
    void UpdateTurnUi()
    {
        var s = _session;
        var replaying = s?.IsReplaying == true;
        var local = s != null && !IsOnline && !_onlineStarting && !s.IsGuest;
        if (_sendTurn != null)
        {
            _sendTurn.Enabled = local && s!.Game != null && !s.IsViewerCopy && !replaying;
            _sendTurn.ToolTipText = s?.Game == null ? "" : !local ? TurnUiModel.OnlineText : s.IsViewerCopy ? ModuleSession.ViewerCopyText
                : replaying ? ModuleSession.ReplayingText : "";
        }
        if (_openTurn != null && !local) _openTurn.Enabled = false;
        if (_openTurn != null && replaying) _openTurn.Enabled = false;
        foreach (var i in _replayBlocked)
        {
            if (replaying) { i.Enabled = false; i.ToolTipText = ModuleSession.ReplayingText; }
            else if (i.ToolTipText == ModuleSession.ReplayingText) i.ToolTipText = null;
        }
        if (_gameMenu != null) _gameMenu.Visible = IsOnline || _onlineStarting || s?.Game != null;
    }

    /// <summary>Docking runs from the highest child index down: the bar gets an index below the log and its splitter, so it docks above them.</summary>
    void PlaceReplayBar()
    {
        var below = Math.Min(Controls.GetChildIndex(_logSplitter), Controls.GetChildIndex(_logHost));
        if (Controls.GetChildIndex(_replayBar) > below) Controls.SetChildIndex(_replayBar, below);
    }

    // ---------- Send Turn ----------

    void SendTurn()
    {
        if (_session is not { Game: { } g } s) return;
        if (s.IsGuest || IsOnline || _onlineStarting) { ShowError(TurnUiModel.OnlineText); return; }
        if (s.IsViewerCopy) { ShowError(ModuleSession.ViewerCopyText); return; }
        if (s.IsReplaying) { ShowError(ModuleSession.ReplayingText); return; }
        var players = g.State.Players.Where(p => p != null).ToList();
        if (players.Count < 2) { ShowError(TurnUiModel.NeedPlayersText); return; }
        var id = TurnIdentity.Load(g.State.GameId);   // InvalidDataException → Guard shows it
        // The previous turn's mailbox send is seen through first (its failure would otherwise never be shown).
        if (!ConfirmSendDone("The previous turn is still being sent through the relay. Send another turn anyway?")) return;
        FlushNotes();   // pending note text becomes a command before the turn is built
        if (Program.Smoke) return;
        if (!TurnUiModel.KnowsLocalPlayer(players, id.LocalPlayerId))
        {
            var pre = TurnUiModel.DefaultLocalPlayer(players, _hotSeat?.Current?.Id, null);
            using var who = new WhoAreYouDialog("Which player are you? This computer remembers it for this game.", players, s.Rules, pre, null);
            if (who.ShowDialog(this) != DialogResult.OK || who.LocalPlayerId is not { } me) return;
            id.LocalPlayerId = me;   // kept only if the turn is sent
        }
        var fromName = players.First(p => p.Id == id.LocalPlayerId).Name;
        using var dlg = new SendTurnDialog(fromName, players, id.LocalPlayerId!, s.Rules,
            new MailboxValues(id.UseMailbox, id.Relay ?? _settings.Current.RelayAddress, PasswordFor(id)));
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Recipient is not { } to) return;
        using var save = new SaveFileDialog
        {
            Filter = TurnUiModel.FileFilter, DefaultExt = "qturn", Title = "Save Turn", OverwritePrompt = true,
            FileName = TurnUiModel.DefaultFileName(s.Model.Name, g.State.GameId, g.State.Turn.Turn),
        };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        SendTurnCore(id, to.Id, save.FileName, dlg.UseMailbox, dlg.Relay, dlg.Password);
    }

    /// <summary>
    /// The confirmed send: only now is the turn built (an empty turn logs "{name} passed."), written, mailed when asked, and the next
    /// turn's start marked. From here on the game is a play-by-email game on this computer (no hot-seat; the local player's view).
    /// </summary>
    TurnFile SendTurnCore(TurnIdentity id, string toPlayerId, string path, bool mailbox, string relay, string password)
    {
        var s = _session ?? throw new InvalidOperationException("There is no game to send.");
        var g0 = s.Game ?? throw new InvalidOperationException("There is no game to send.");
        var before = g0.Log.Count;
        var pass = before == g0.TurnBase;   // Build logs "{name} passed." as its own action
        var t = TurnSender.Build(s, toPlayerId, id);
        try { TurnFiles.Write(path, t); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // Nothing was sent: the pass line goes again (not onto the redo stack), and the identity, mailbox and Settings stay as they were.
            if (pass && ReferenceEquals(s.Game, g0) && g0.Log.Count == before + 1 && g0.CanUndo) { g0.Undo(); g0.ClearRedo(); }
            throw new IOException($"Could not write the turn file '{path}':\n{ex.Message}", ex);
        }
        // Written: only now do this computer's choices for the game change.
        ApplyMailbox(id, mailbox, relay, password);
        TurnSender.AfterSend(s);
        id.RecordSent(t.StartHash);   // this turn coming back here is an older position
        SaveIdentity(id);
        // The turns received for this one are part of what was just sent: their local copies go.
        var pruned = ReceivedTurns.Prune(t.GameId);
        if (pruned > 0) Program.AppLog($"turns: removed {pruned} received turn cop{(pruned == 1 ? "y" : "ies")}");
        _turnIdentity = id;
        StopHotSeatForTurns();
        if (s.Game is { } g) s.SetViewSides(TurnUiModel.ViewSides(g.State, id.LocalPlayerId));
        SetStatus($"Turn saved to {Path.GetFileName(path)}");
        Program.AppLog($"turns: sent a turn of {t.Commands.Count} step(s) to {toPlayerId} ({Path.GetFileName(path)})");
        if (mailbox) StartMailboxSend(new PendingSend(id, t, PasswordFor(id), path));
        UpdateMailboxWatch();
        UpdateTurnUi();
        return t;
    }

    // ---------- mailbox send (kept until it is through or the user lets it go) ----------

    /// <summary>A turn on its way to the relay: the same in-memory turn is resent on Retry.</summary>
    sealed record PendingSend(TurnIdentity Id, TurnFile Turn, string Password, string Path);

    /// <summary>The send in flight (null when none) and the attempt running for it.</summary>
    PendingSend? _pendingSend;
    Task<string?>? _sendAttempt;
    /// <summary>True while a close or discard waits for the send: the outcome is handled by the waiter, not by the continuation.</summary>
    bool _waitingForSend;

    /// <summary>How long a close, Back to launcher or discard waits for a turn still being sent before asking.</summary>
    static readonly TimeSpan SendWait = TimeSpan.FromSeconds(15);

    /// <summary>The turn in the mailbox send still running (tests and smoke).</summary>
    internal Task? MailboxSendTask => _sendAttempt;

    void StartMailboxSend(PendingSend p)
    {
        if (_mailbox == null) return;
        SetStatus("Sending the turn through the relay…");
        Task<string?> attempt;
        try { attempt = _mailbox.TrySendAsync(p.Id, p.Turn, p.Password); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { attempt = Task.FromResult<string?>("Could not send the turn: " + ex.Message); }
        _pendingSend = p;
        _sendAttempt = attempt;
        attempt.ContinueWith(_ => PostToUi(() => { if (!_waitingForSend) SendAttemptDone(attempt); }),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// An attempt finished (on the UI thread; once per attempt): through → done; refused → "The turn could not be sent through the relay:
    /// {reason}" with Retry (the same turn again) or Close (the file is saved; send it another way).
    /// </summary>
    void SendAttemptDone(Task<string?> attempt)
    {
        if (!ReferenceEquals(attempt, _sendAttempt) || _pendingSend is not { } p) return;
        _sendAttempt = null;
        _pendingSend = null;
        string? problem;
        try { problem = attempt.GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { problem = ex.Message; }
        if (IsDisposed) return;
        if (problem == null)
        {
            SetStatus("The turn is in the mailbox on the relay.");
            Program.AppLog("turns: the turn is in the mailbox");
            return;
        }
        Program.AppLog("turns: mailbox send failed: " + problem);
        if (AskRetrySend(problem, p.Path)) StartMailboxSend(p);
        else SetStatus(SendElsewhereText(p.Path));
    }

    static string SendFailedText(string reason) => "The turn could not be sent through the relay: " + reason;

    static string SendElsewhereText(string path) => $"The file is saved at {path}; send it another way.";

    /// <summary>Retry (true) or Close (false).</summary>
    bool AskRetrySend(string reason, string path)
    {
        if (Program.Smoke) { Program.AppLog($"smoke: '{SendFailedText(reason)}' answered Close"); return false; }
        var retry = new TaskDialogButton("Retry");
        var close = new TaskDialogButton("Close");
        var page = new TaskDialogPage
        {
            Caption = "Send Turn", Heading = SendFailedText(reason), Text = SendElsewhereText(path), Icon = TaskDialogIcon.Warning,
            Buttons = { retry, close }, DefaultButton = retry,
        };
        return TaskDialog.ShowDialog(this, page) == retry;
    }

    /// <summary>
    /// Before the window closes, goes Back to the launcher or discards the game: a turn still being sent gets up to 15 s ("Sending
    /// turn…"); its outcome is then handled (Retry waits again). Still not through: asks whether to go anyway. False = stay.
    /// </summary>
    /// <param name="question">Asked when the send is still running after the wait (Yes = go on anyway).</param>
    bool ConfirmSendDone(string question = "The turn is still being sent through the relay. Leave anyway?")
    {
        while (_sendAttempt is { } attempt && _pendingSend is { } p)
        {
            if (!attempt.IsCompleted)
            {
                _waitingForSend = true;
                try
                {
                    if (Program.Smoke) attempt.Wait(SendWait);
                    else
                    {
                        using var wait = new SendWaitForm(attempt, SendWait);
                        wait.ShowDialog(this);
                    }
                }
                catch (AggregateException) { }
                finally { _waitingForSend = false; }
            }
            if (!attempt.IsCompleted)
            {
                if (Program.Smoke) return true;
                return MessageBox.Show(this, question + "\n" + SendElsewhereText(p.Path),
                    "Send Turn", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            }
            SendAttemptDone(attempt);   // Retry starts another attempt: the loop waits for it too
        }
        return true;
    }

    /// <summary>A small "Sending turn…" wait that closes when the send finishes or the time is up.</summary>
    sealed class SendWaitForm : Form
    {
        readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };

        public SendWaitForm(Task attempt, TimeSpan limit)
        {
            Text = "Send Turn";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(16);
            Controls.Add(new Label { Text = "Sending turn…", AutoSize = true, Margin = new Padding(8) });
            var end = DateTime.UtcNow + limit;
            _timer.Tick += (_, _) => { if (attempt.IsCompleted || DateTime.UtcNow >= end) Close(); };
            Shown += (_, _) => _timer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }

    void ApplyMailbox(TurnIdentity id, bool use, string relay, string password)
    {
        id.UseMailbox = use;
        if (!use) return;
        id.Relay = relay.Trim();
        _mailboxPasswords[id.GameId] = password ?? "";
        if (!id.SetPassword(password)) SetStatus("The mailbox password could not be stored on this computer; it is kept until the window closes.");
        if (string.IsNullOrWhiteSpace(_settings.Current.RelayAddress) && id.Relay.Length > 0)
        {
            var r = id.Relay;
            _settings.Update(cur => cur with { RelayAddress = r });
        }
    }

    string PasswordFor(TurnIdentity id) =>
        _mailboxPasswords.TryGetValue(id.GameId, out var pw) ? pw : id.GetPassword() ?? "";

    void SaveIdentity(TurnIdentity id)
    {
        try { id.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.AppLog("turns: could not save the game's identity: " + ex.Message);
            SetStatus("Could not remember this game's player on this computer: " + ex.Message);
        }
    }

    // ---------- Open Turn File ----------

    void OpenTurnFileDialog()
    {
        if (_session == null || Program.Smoke) return;
        using var dlg = new OpenFileDialog { Filter = TurnUiModel.FileFilter, Title = "Open Turn File" };
        if (dlg.ShowDialog(this) == DialogResult.OK) OpenTurnFile(dlg.FileName);
    }

    /// <summary>Reads a turn file (anything malformed is said plainly, like a bad save) and opens it for replay.</summary>
    bool OpenTurnFile(string path)
    {
        TurnFile t;
        try { t = TurnFiles.Read(path); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowError($"Could not open '{path}':\n{ex.Message}");
            return false;
        }
        return OpenTurn(t, null);
    }

    void OpenWaitingTurn()
    {
        if (_waiting is { } w) OpenTurn(w.Turn, w.ItemId);
    }

    /// <summary>
    /// Checks a received turn (other module, already played, older position, another game) and starts its replay. Returns false when
    /// nothing was opened (the game is as it was).
    /// </summary>
    /// <param name="itemId">The mailbox item it came from (deleted after a successful replay); null for a file.</param>
    bool OpenTurn(TurnFile t, string? itemId)
    {
        if (_session is not { } s) return false;
        if (s.IsGuest || IsOnline || _onlineStarting) { ShowError(TurnUiModel.OnlineText); return false; }
        if (s.IsReplaying) { ShowError(ModuleSession.ReplayingText); return false; }
        FlushNotes();   // pending note text becomes a command first, so the check below counts it as a local move
        TurnIdentity id;
        TurnCheck check;
        try
        {
            id = TurnIdentity.Load(t.GameId);
            check = TurnOpener.Check(s, t, id);
        }
        catch (InvalidDataException ex)
        {
            ShowError("Could not open the turn:\n" + ex.Message);
            return false;
        }
        Program.AppLog($"turns: opening a turn from {TurnUiModel.ShownName(t.FromName)} ({check})");
        switch (check)
        {
            case TurnCheck.DifferentModule:
                ShowError(ModuleSession.DifferentModuleTurnText);
                return false;
            case TurnCheck.AlreadyPlayed when !AskOpenAnyway(TurnUiModel.AlreadyPlayedText):
            case TurnCheck.OlderPosition when !AskOpenAnyway(TurnOpener.OlderPositionText):
                return false;
        }
        if (check != TurnCheck.Ok && !ConfirmDiscard()) return false;
        var players = t.Start.Players.Where(p => p != null).ToList();
        if (players.Count >= 2 && (check == TurnCheck.NewGame || !TurnUiModel.KnowsLocalPlayer(players, id.LocalPlayerId)))
        {
            if (!AskWhoAreYou(t, players, id)) return false;
        }
        var local = id.LocalPlayerId ?? "";
        var ctx = new ReplayContext(id, itemId, _savePath, _dirty, _recoveredAutosave, s.Game?.State.GameId, _hotSeat?.Current);
        if (_waiting is { } w && (w.ItemId == itemId || w.Turn.StartHash == t.StartHash)) ClearWaiting();   // this very turn is being opened
        // The rules stay as they are while the turn replays: an open workbench closes (it reopens from Tools once the replay is over).
        if (_benches.TryGetValue("workbench", out var bench) && !bench.IsDisposed) bench.Close();
        StopHotSeatForTurns();   // never a handover over a replayed turn; it comes back for a local game afterwards
        TurnReplayModel m;
        _replayCtx = ctx;
        try { m = s.OpenTurn(t, local, id); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            FollowOpenGame(beginHotSeat: false);   // nothing was replaced: hot-seat comes back with the same player
            _replayCtx = null;
            ShowError("Could not open the turn:\n" + ex.Message);
            return false;
        }
        _replay = m;
        m.Changed += OnReplayChanged;
        PlaceReplayBar();
        _replayBar.Bind(m);
        SetStatus($"Replaying the turn from {TurnUiModel.ShownName(t.FromName)}: Next, Play all or Stop.");
        UpdateUi();
        UpdateTitle();
        if (m.Done) OnReplayChanged();   // a turn without steps is done at once
        return true;
    }

    bool AskOpenAnyway(string text)
    {
        if (Program.Smoke) { Program.AppLog($"smoke: '{text}' answered {TurnUiModel.OpenAnywayText}"); return true; }
        var open = new TaskDialogButton(TurnUiModel.OpenAnywayText);
        var page = new TaskDialogPage
        {
            Caption = "Open Turn File", Heading = text, Icon = TaskDialogIcon.Warning,
            Buttons = { open, TaskDialogButton.Cancel }, DefaultButton = TaskDialogButton.Cancel,
        };
        return TaskDialog.ShowDialog(this, page) == open;
    }

    bool AskWhoAreYou(TurnFile t, IReadOnlyList<PlayerDef> players, TurnIdentity id)
    {
        var pre = TurnUiModel.DefaultLocalPlayer(players, id.LocalPlayerId, t.ToPlayerId);
        if (Program.Smoke)
        {
            id.LocalPlayerId = pre;
            Program.AppLog($"smoke: who are you answered {pre}");
        }
        else
        {
            using var dlg = new WhoAreYouDialog($"This turn is from {TurnUiModel.ShownName(t.FromName)}. Which player are you?", players, _session?.Rules, pre,
                new MailboxValues(id.UseMailbox, id.Relay ?? _settings.Current.RelayAddress, PasswordFor(id)));
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.LocalPlayerId is not { } me) return false;
            id.LocalPlayerId = me;
            ApplyMailbox(id, dlg.UseMailbox, dlg.Relay, dlg.Password);
        }
        SaveIdentity(id);
        return true;
    }

    /// <summary>After each step, a stop, the end or a failure: on the end (or failure) the bar goes and the outcome is shown.</summary>
    void OnReplayChanged()
    {
        if (_replay is not { Done: true } m || IsDisposed) return;
        var ctx = _replayCtx;
        EndReplayUi();
        if (m.Error != null)
        {
            // The previous game is back: so are its save target and unsaved state.
            if (ctx != null) { _savePath = ctx.SavePath; _dirty = ctx.Dirty; _recoveredAutosave = ctx.Recovered; }
            if (ctx?.ItemId is { } stillThere) OnTurnWaiting(m.Turn, stillThere);   // still in the mailbox: the notice comes back
            UpdateUi();
            UpdateTitle();
            Program.AppLog("turns: replay failed: " + m.Error);
            ShowError("The turn could not be replayed; your game is as it was before.\n" + m.Error);
            if (_session is { Game: null } s0) NoGameAfterFailedTurn(s0);
            return;
        }
        if (ctx != null && string.Equals(ctx.GameId, m.Turn.GameId, StringComparison.OrdinalIgnoreCase))
        {
            _savePath = ctx.SavePath;   // the same game went on: its save file stays the target
            _recoveredAutosave = ctx.Recovered;
        }
        _dirty = true;
        if (ctx != null && _session?.Game is { } g && TurnUiModel.IsTurnGame(g.State, ctx.Identity)) _turnIdentity = ctx.Identity;
        else FollowOpenGame(beginHotSeat: true, watch: false);
        // The replayed game is saved (autosave) before the mailbox item goes: a crash now never loses the turn. The item leaves the
        // mailbox first; only then may the watch restart (a restart would cancel the delete).
        var saved = ForceAutosave("turn replayed");
        if (ctx?.ItemId is { } item && _mailbox != null && saved) DeleteThenWatch(item);
        else
        {
            if (ctx?.ItemId != null) Program.AppLog("turns: the replayed game could not be autosaved; the turn stays in the mailbox");
            UpdateMailboxWatch();
        }
        UpdateUi();
        UpdateTitle();
        RefreshPhaseViews();
        var from = TurnUiModel.ShownName(m.Turn.FromName);
        Program.AppLog($"turns: replayed {m.Count} step(s) from {from}; end {(m.EndMatches ? "matches" : "DIFFERS")}");
        SetStatus(m.ViewNotice ?? $"{from}'s turn is replayed. Your turn.");
        if (!m.EndMatches) ShowError(TurnReplayModel.EndMismatchText);
    }

    /// <summary>Writes an autosave now (even when timed autosave is off). True when one was written.</summary>
    bool ForceAutosave(string why)
    {
        try
        {
            if (_autosaver?.SaveForced(DateTimeOffset.UtcNow) == null) return false;
            _autosavedSeq = _changeSeq;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog($"{why} autosave: " + ex.Message);
            return false;
        }
    }

    /// <summary>A turn opened from the launcher could not be replayed and there is no game to go back to: never an empty window.</summary>
    void NoGameAfterFailedTurn(ModuleSession s)
    {
        if (Program.Smoke) return;
        if (Program.LauncherHook != null) BeginInvoke(() => { if (!IsDisposed && _session?.Game == null) BackToLauncher(); });
        else BeginInvoke(() => { if (!IsDisposed && ReferenceEquals(_session, s) && s.Game == null) ChooseAndStart(s, preselectNew: false); });
    }

    void EndReplayUi()
    {
        if (_replay is not { } m) return;
        m.Changed -= OnReplayChanged;
        _replay = null;
        _replayCtx = null;
        _replayBar.Bind(null);
        if (!IsDisposed) UpdateUi();
    }

    // ---------- mailbox ----------

    /// <summary>Watches the open play-by-email game's mailbox when it uses one (no network otherwise); stops for any other game.</summary>
    void UpdateMailboxWatch()
    {
        if (_mailbox == null || IsDisposed) return;
        var g = _session?.Game;
        var id = _turnIdentity;
        string? key = null;
        var password = "";
        if (g != null && id is { UseMailbox: true } && !string.IsNullOrWhiteSpace(id.Relay) && !IsOnline && !_onlineStarting
            && string.Equals(id.GameId, g.State.GameId, StringComparison.OrdinalIgnoreCase))
        {
            password = PasswordFor(id);
            key = string.Join("\n", g.State.GameId, id.Relay, id.LocalPlayerId, password);
        }
        if (key == _watchKey)
        {
            // Same game, relay, player and password: the running watch goes on, reading the current identity (played turns).
            if (key != null) _mailbox.SetIdentity(id!);
            return;
        }
        _watchKey = key;
        if (key == null) { _mailbox.Stop(); return; }
        _mailbox.Watch(g!.State.GameId, id!, password);
        Program.AppLog("turns: watching the turn mailbox on " + id!.Relay);
    }

    async void DeleteThenWatch(string itemId)
    {
        try { await _mailbox!.DeleteAsync(itemId); }   // never throws; failures reach the status bar
        catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("turns: " + ex.Message); }
        if (!IsDisposed) OnlineGuard(UpdateMailboxWatch);
    }

    void StopMailbox()
    {
        _mailbox?.Stop();
        _watchKey = null;
        ClearWaiting();
    }

    void OnTurnWaiting(TurnFile t, string itemId)
    {
        if (IsDisposed || _session?.Game is not { } g) return;   // also mid-replay: the notice waits (opening it then says to finish first)
        if (!string.Equals(g.State.GameId, t.GameId, StringComparison.OrdinalIgnoreCase)) return;
        // A copy stays on this computer until the next turn is sent: the relay item goes once the replayed game is saved.
        _ = Task.Run(() =>
        {
            // Off the UI thread: a large turn takes a moment to compress and write.
            if (ReceivedTurns.Keep(t, itemId, out var why) == null) Program.AppLog("turns: could not keep a copy of the received turn: " + why);
        });
        _waiting = (t, itemId);
        var text = TurnUiModel.NewTurnWaitingText(t.FromName);
        _alertBar.SetNotice(text);
        UpdateAlertBarVisible();
        FixTopOrder();
        Program.AppLog("turns: " + text);
    }

    void OnMailboxStatus(string text)
    {
        if (IsDisposed) return;
        SetStatus(text);
        Program.AppLog("turns: " + text);
    }

    void ClearWaiting()
    {
        _waiting = null;
        if (_alertBar.NoticeText == null) return;
        _alertBar.SetNotice(null);
        UpdateAlertBarVisible();
    }

    // ---------- smoke ----------

    /// <summary>
    /// Smoke (--smoke-turns, --screenshot-turns): a second session of the same module sends a turn (a move and a roll) to Blue; this
    /// window opens it as Blue (who-are-you answered), steps once (screenshot of the replay bar), plays the rest and compares hashes;
    /// then sends an empty turn back (a pass) which the first session replays to the same state.
    /// </summary>
    void SmokeTurns()
    {
        var shot = _opts.ScreenshotTurns;
        void Check(bool ok, string what)
        {
            Program.AppLog($"smoke: turns {what} {(ok ? "ok" : "FAILED")}");
            if (!ok) Program.ExitCode = 1;
        }
        if (_session is not { Rules: { } pack } s || OpenedModulePath is not { } module || s.RulesPath is not { } rulesPath)
        {
            Check(false, "setup (needs a module and a rules pack file)");
            return;
        }
        var sides = pack.Sides.Where(x => x != null && !string.IsNullOrEmpty(x.Id)).Select(x => x.Id).ToList();
        if (sides.Count < 2) { Check(false, "setup (the rules pack has fewer than two sides)"); return; }
        var red = new PlayerDef("p1", "Rita", new[] { sides[0] });
        var blue = new PlayerDef("p2", "Bo", new[] { sides[1] });
        var root = Path.Combine(AppDataRoot.Path, "smoke-turns");
        Directory.CreateDirectory(root);

        using var sender = ModuleSession.Open(module);
        sender.OpenRules(rulesPath);
        if (sender.Model.Setups.FirstOrDefault() is { } setup) sender.LoadSetup(setup, Difficulty.FullyAuto, new[] { red, blue });
        else sender.StartNewGame(Difficulty.FullyAuto, new[] { red, blue });
        var sg = sender.Game!;
        var senderId = TurnIdentity.Load(sg.State.GameId, Path.Combine(root, "sender"), null);
        senderId.LocalPlayerId = red.Id;
        using (sg.BeginAction("Smoke roll")) sg.Execute(CommandLog.Stamp(new RollDice("Smoke attack", 2, 6, new[] { 3, 4 })));
        if (sg.State.Pieces.Values.FirstOrDefault() is { } piece)
        {
            using (sg.BeginAction("Smoke move"))
                sg.Execute(CommandLog.Prepare(new MovePieces(new[] { piece.Id }, piece.MapId, new PointD(piece.Pos.X + 60, piece.Pos.Y), null), sg.State, sender));
        }
        using (sg.BeginAction("Smoke roll")) sg.Execute(CommandLog.Stamp(new RollDice("Smoke defence", 1, 6, new[] { 5 })));
        var t = TurnSender.Build(sender, blue.Id, senderId);
        var toBlue = Path.Combine(root, "to-blue.qturn");
        TurnFiles.Write(toBlue, t);
        TurnSender.AfterSend(sender);
        Check(t.Commands.Count >= 2, $"sent ({t.Commands.Count} step(s))");

        var opened = OpenTurnFile(toBlue);
        var m = _replay;
        Check(opened && m != null && s.IsReplaying && _replayBar.Visible && _hotSeat == null && s.ViewSides is { Count: 0 } && !s.CanSave
            && _sendTurn?.Enabled == false && _editUndo?.Enabled == false && _replayBlocked.All(i => !i.Enabled),
            "replay started (public view, no hot-seat, sending, undo and rules edits off)");
        if (m == null) return;
        var logCount = s.Game!.State.Log.Count;
        var rolled = _dice!.RollSpec(new DiceSpec(1, 6, 0), "Roll");
        Check(!rolled && s.Game.State.Log.Count == logCount && _sbMessage.Text == ModuleSession.ReplayingText
            && _toolbar.Items["dice.1d6"]?.Enabled == false && _toolbar.Items["turn.next"]?.Enabled == false, "dice and turn refused mid-replay");
        RefreshPhaseViews();
        Check(!_strip.StartEnabled && _phaseDock?.Panel?.NextButtonEnabled != true, "phase buttons off mid-replay");
        m.Next();
        Pump(10);
        Check(_replayBar.StepText == TurnUiModel.StepText(1, m.Count) && !string.IsNullOrEmpty(_replayBar.LineText), $"first step ('{_replayBar.StepText}': {_replayBar.LineText})");
        if (shot != null)
        {
            // Only the map stays open for the picture (earlier smoke steps leave the ledger and workbench behind).
            foreach (var f in MdiChildren.Where(f => f is not MapWindow).ToList()) f.Close();
            if (FirstMap() is { } map) map.Activate();
            Pump(20);
            Check(_replayBar.Bottom <= _logSplitter.Top + 1 || !_logHost.Visible, "replay bar above the log");
            SaveScreenshot(this, shot, true);
            using var bar = new Bitmap(Math.Max(1, _replayBar.Width), Math.Max(1, _replayBar.Height));
            _replayBar.DrawToBitmap(bar, new Rectangle(Point.Empty, bar.Size));
            bar.Save(Path.ChangeExtension(shot, null) + "-bar.png", System.Drawing.Imaging.ImageFormat.Png);
        }
        m.PlayAll();
        Pump(10);
        var g = s.Game!;
        Check(!s.IsReplaying && _replay == null && !_replayBar.Visible && m.EndMatches && StateHash.Of(g.State) == t.EndHash,
            "replayed to the sender's end state");
        Check(_hotSeat == null && s.ViewSides is { } vs && vs.SetEquals(blue.Sides) && _turnIdentity?.LocalPlayerId == blue.Id && _sendTurn?.Enabled == true,
            "after the replay (Blue's view, no hot-seat, Send Turn on)");
        Check(TurnOpener.Check(s, t) == TurnCheck.AlreadyPlayed, "reopening says already played");

        var toRed = Path.Combine(root, "to-red.qturn");
        var back = SendTurnCore(_turnIdentity!, red.Id, toRed, false, "", "");
        Check(g.State.Log.Any(e => e.Text == "Bo passed.") && back.Commands.Count == 1, "empty turn sent as a pass");
        RefreshHistoryUi();
        Check(_history is { CanUndo: false, UndoBlockedBySentTurn: true } && _editUndo?.Enabled == false
            && _editUndo.ToolTipText == HistoryController.AlreadySentText, "undo of the sent turn refused");
        var rm = sender.OpenTurn(TurnFiles.Read(toRed), red.Id, senderId);
        rm.PlayAll();
        Check(rm.Done && rm.Error == null && rm.EndMatches && StateHash.Of(sender.Game!.State) == StateHash.Of(g.State), "round trip (the sender replays the pass)");
        Check(TurnOpener.Check(s, TurnFiles.Read(toRed)) == TurnCheck.OlderPosition, "own turn back is an older position");
    }

    static void Pump(int times)
    {
        for (var i = 0; i < times; i++) { Application.DoEvents(); Thread.Sleep(20); }
    }
}
