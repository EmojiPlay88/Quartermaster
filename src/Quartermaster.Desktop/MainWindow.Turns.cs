using Avalonia.Controls;
using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Desktop.Online;
using Quartermaster.Desktop.Shell;
using Quartermaster.Desktop.Turns;
using Quartermaster.Net;
using Quartermaster.Render;

namespace Quartermaster.Desktop;

/// <summary>
/// Turn files (port of WinForms MainForm.Turns; spec 6, launcher spec 3.2): the turn game, Game → Send Turn… with the relay mailbox send,
/// File → Open Turn File… (and the launcher's Open turn file…) with the replay bar, the relay mailbox watch ("New turn waiting from
/// {player}") and the guest's File → Export Viewer Copy…. A play-by-email game is not a hot-seat game: while one is open there is no
/// handover, and the view is the local player's sides (public only while a turn replays). While a turn replays every mutating path is off
/// and nothing is saved. Every mailbox event reaches the window through <see cref="PostToUi"/>.
/// </summary>
public partial class MainWindow
{
    static readonly FileFilter TurnFilter = new("Quartermaster turns (*.qturn)", "*.qturn");

    /// <summary>How long a close, Back to launcher or discard waits for a turn still being sent before asking.</summary>
    static readonly TimeSpan SendWait = TimeSpan.FromSeconds(15);

    const string LeaveDuringSendText = "The turn is still being sent through the relay. Leave anyway?";
    const string SendAnotherText = "The previous turn is still being sent through the relay. Send another turn anyway?";
    const string PasswordCaption = "Turn mailbox";
    const string PasswordPromptText = "This game's mailbox password could not be read on this computer. Type it again (leave it empty if the game has none):";

    /// <summary>Mailbox passwords typed this session (used when the protected copy cannot be stored or read back).</summary>
    readonly Dictionary<string, string> _mailboxPasswords = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Games whose unreadable stored password was asked for this session (asked once per game per session).</summary>
    readonly HashSet<string> _passwordAsked = new(StringComparer.OrdinalIgnoreCase);
    MenuItem? _sendTurn, _exportViewer;
    Separator? _gameOnlineSep, _gameLeaveSep;
    MailboxService? _mailbox;
    /// <summary>The open game's identity when it is a play-by-email game (null for solo, hot-seat and online games).</summary>
    TurnIdentity? _turnIdentity;
    TurnReplayModel? _replay;
    ReplayContext? _replayCtx;
    /// <summary>What the running mailbox watch is for (game, relay, player, password); null when none runs.</summary>
    string? _watchKey;
    (TurnFile Turn, string ItemId)? _waiting;
    Task? _mailboxDelete;

    /// <summary>What to put back when a replay fails (the previous game returns): its save target, dirty state and hot-seat player.</summary>
    sealed record ReplayContext(TurnIdentity Identity, string? ItemId, string? SavePath, bool Dirty, bool Recovered, string? GameId, PlayerDef? HotSeatCurrent);

    /// <summary>A turn on its way to the relay: the same in-memory turn is resent on Retry.</summary>
    sealed record PendingSend(TurnIdentity Id, TurnFile Turn, string Password, string Path);

    /// <summary>The send in flight (null when none) and the attempt running for it.</summary>
    PendingSend? _pendingSend;
    Task<string?>? _sendAttempt;
    /// <summary>True while a close or discard waits for the send: the outcome is handled by the waiter, not by the continuation.</summary>
    bool _waitingForSend;

    /// <summary>
    /// Folder of this window's game identities and received turn copies; null = <see cref="TurnIdentity.DefaultRoot"/>. Tests set it so
    /// two windows in one process act as two computers.
    /// </summary>
    internal string? TurnsRoot { get; set; }

    /// <summary>The open game's turn identity (tests).</summary>
    internal TurnIdentity? TurnIdentityForTests => _turnIdentity;

    /// <summary>The relay mailbox (sends and the watch of the open play-by-email game).</summary>
    internal MailboxService? Mailbox => _mailbox;

    /// <summary>The replay running now (null when none).</summary>
    internal TurnReplayModel? TurnReplay => _replay;

    /// <summary>The replay bar (under the alert bar; hidden unless a replay is bound).</summary>
    internal ReplayBar ReplayBarControl => ReplayBarHost;

    /// <summary>The alert bar's turn notice ("" when none).</summary>
    internal string TurnNoticeText => AlertBarHost.NoticeText ?? "";

    /// <summary>True while the mailbox of the open play-by-email game is watched (tests).</summary>
    internal bool MailboxWatching => _watchKey != null;

    /// <summary>The delete of a replayed mailbox item still running or last run (null before any; tests).</summary>
    internal Task? MailboxDeleteTask => _mailboxDelete;

    /// <summary>Game → Send Turn… (tests).</summary>
    internal MenuItem? SendTurnItem => _sendTurn;

    /// <summary>The mailbox send attempt still running or not yet handled (null when none; tests).</summary>
    internal Task? MailboxSendTask => _sendAttempt;

    void InitTurns()
    {
        // The client connects inside the service's worker: the certificate question is asked over this window from there.
        _mailbox = new MailboxService(_ => new RelayClient(RelayTrust.Pins()), PostToUi, null, c => RelayTrust.Ask(this, _dialogs, c));
        _mailbox.TurnWaiting += OnTurnWaiting;
        _mailbox.Status += OnMailboxStatus;
        // A played turn whose replayed game was lost (the open game is back at its start) is raised again, not deleted.
        _mailbox.OpenGameHash = gid => _session is { Game: { } g, IsReplaying: false }
            && string.Equals(g.State.GameId, gid, StringComparison.OrdinalIgnoreCase) ? StateHash.Of(g.State) : null;
        ReplayBarHost.Failed += ex => ShowError("The turn could not be replayed:\n" + ex.Message);
    }

    void DisposeTurns()
    {
        try { _mailbox?.Dispose(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("turns: " + ex.Message); }
    }

    // ---------- state of the open game ----------

    /// <summary>
    /// After any game replacement (WinForms OnTurnGameReplaced). The start of a replayed turn changes nothing (its view stays public-only
    /// until the replay ends); a replay taken over by another game just loses its bar. Otherwise the open game decides: a play-by-email
    /// game gets its player's view and its mailbox watch, any other local game gets hot-seat back.
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
        // A failed replay brings the previous game back with its view: hot-seat returns without a fresh handover, the same player at the screen.
        var failed = _replay is { Done: true, Error: not null };
        FollowOpenGame(beginHotSeat: !failed, hotSeatCurrent: failed ? _replayCtx?.HotSeatCurrent : null);
        // An abandoned mailbox turn is still in the mailbox: its notice comes back when the same game is open again.
        if (abandoned is { } a) OnTurnWaiting(a.Turn, a.ItemId);
    }

    /// <summary>
    /// After any game replacement: a play-by-email game gets its player's view (no hot-seat); any other local game keeps hot-seat, or gets
    /// it back (<paramref name="beginHotSeat"/>: as a fresh load; otherwise the player <paramref name="hotSeatCurrent"/> is still at the screen).
    /// </summary>
    /// <param name="watch">False when the caller restarts the mailbox watch itself.</param>
    void FollowOpenGame(bool beginHotSeat = true, bool watch = true, PlayerDef? hotSeatCurrent = null)
    {
        var s = _session;
        var g = s?.Game;
        TurnIdentity? id = null;
        if (g != null && s is { IsGuest: false, IsViewerCopy: false } && TurnIdentity.IsValidGameId(g.State.GameId))
        {
            try { id = TurnIdentity.Load(g.State.GameId, TurnsRoot, null); }
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
                else _hotSeat?.Adopt(hotSeatCurrent);
                UpdatePlayerButton();
            }
        }
        if (_waiting is { } w && !string.Equals(w.Turn.GameId, g?.State.GameId, StringComparison.OrdinalIgnoreCase)) ClearWaiting();
        if (watch) UpdateMailboxWatch();
        UpdateTurnUi();
    }

    /// <summary>Send Turn follows the session (WinForms UpdateTurnUi); the Game menu shows with a game or while online.</summary>
    void UpdateTurnUi()
    {
        var s = _session;
        var replaying = s?.IsReplaying == true;
        var local = s != null && !IsOnline && !_onlineStarting && !s.IsGuest;
        if (_sendTurn != null)
        {
            _sendTurn.IsEnabled = local && s!.Game != null && !s.IsViewerCopy && !replaying;
            var tip = s?.Game == null ? null : !local ? TurnUiModel.OnlineText : s.IsViewerCopy ? ModuleSession.ViewerCopyText
                : replaying ? ModuleSession.ReplayingText : null;
            ToolTip.SetTip(_sendTurn, tip);
            ToolTip.SetShowOnDisabled(_sendTurn, true);
        }
        ApplyGates();
        if (_gameMenu != null) _gameMenu.IsVisible = IsOnline || _onlineStarting || s?.Game != null;
    }

    // ---------- Send Turn ----------

    /// <summary>
    /// Game → Send Turn…: refused while online, in a viewer copy, while replaying or with fewer than two players. The previous mailbox send
    /// is seen through first; then who-are-you (when this computer does not know the player yet), the send dialog and the file picker.
    /// </summary>
    internal async Task SendTurnAsync()
    {
        try { await SendTurnCoreAsync(); }
        catch (Exception ex) when (IsUserError(ex)) { ShowError(ex.Message); }
    }

    async Task SendTurnCoreAsync()
    {
        if (_session is not { Game: { } g } s) return;
        if (s.IsGuest || IsOnline || _onlineStarting) { ShowError(TurnUiModel.OnlineText); return; }
        if (s.IsViewerCopy) { ShowError(ModuleSession.ViewerCopyText); return; }
        if (s.IsReplaying) { ShowError(ModuleSession.ReplayingText); return; }
        var players = g.State.Players.Where(p => p != null).ToList();
        if (players.Count < 2) { ShowError(TurnUiModel.NeedPlayersText); return; }
        var id = TurnIdentity.Load(g.State.GameId, TurnsRoot, null);   // InvalidDataException: shown
        // The previous turn's mailbox send is seen through first (its failure would otherwise never be shown).
        if (!await ConfirmSendDoneAsync(SendAnotherText)) return;
        FlushNotes();   // pending note text becomes a command before the turn is built
        if (!Same()) return;
        if (!TurnUiModel.KnowsLocalPlayer(players, id.LocalPlayerId))
        {
            var pre = TurnUiModel.DefaultLocalPlayer(players, _hotSeat?.Current?.Id, null);
            var who = await _dialogs.AskWhoAreYouAsync(this, TurnDialogRules.WhoPrompt, players, s.Rules, pre, null);
            if (who == null || !Same() || !TurnUiModel.KnowsLocalPlayer(players, who.LocalPlayerId)) return;
            id.LocalPlayerId = who.LocalPlayerId;   // kept only if the turn is sent
        }
        var fromName = players.First(p => p.Id == id.LocalPlayerId).Name;
        var password = await PasswordForAsync(id);
        if (!Same()) return;
        var choice = await _dialogs.AskSendTurnAsync(this, fromName, players, id.LocalPlayerId!, s.Rules,
            new MailboxValues(id.UseMailbox, id.Relay ?? SettingsStore.Current.RelayAddress, password));
        if (choice == null || !Same()) return;
        var path = await _dialogs.SaveFileAsync(this, "Save Turn", TurnFilter, "qturn",
            TurnUiModel.DefaultFileName(s.Model.Name, g.State.GameId, g.State.Turn.Turn));
        if (path == null || !Same()) return;
        SendTurnCore(id, choice.Recipient.Id, path, choice.UseMailbox, choice.Relay, choice.Password);

        // The same game is still open, still local and not replaying (a question may have been open while it changed).
        bool Same() => !_closed && ReferenceEquals(_session, s) && ReferenceEquals(s.Game, g) && !IsOnline && !_onlineStarting
            && !s.IsGuest && !s.IsReplaying;
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
        var pruned = ReceivedTurns.Prune(t.GameId, TurnsRoot);
        if (pruned > 0) System.Diagnostics.Trace.WriteLine($"turns: removed {pruned} received turn cop{(pruned == 1 ? "y" : "ies")}");
        _turnIdentity = id;
        StopHotSeatForTurns();
        if (s.Game is { } g) s.SetViewSides(TurnUiModel.ViewSides(g.State, id.LocalPlayerId));
        SetStatus($"Turn saved to {Path.GetFileName(path)}");
        System.Diagnostics.Trace.WriteLine($"turns: sent a turn of {t.Commands.Count} step(s) to {toPlayerId} ({Path.GetFileName(path)})");
        if (mailbox) StartMailboxSend(new PendingSend(id, t, PasswordFor(id), path));
        UpdateMailboxWatch();
        UpdateTurnUi();
        RefreshHistoryUi();   // undo now stops at the turn just sent
        return t;
    }

    /// <summary>A play-by-email game owns the view: hot-seat stops following it (detached, so no late event reaches the view); the game's
    /// windows come back if a handover had hidden them.</summary>
    void StopHotSeatForTurns() => DestroyHotSeat(restoreWindows: true);

    // ---------- mailbox send (kept until it is through or the user lets it go) ----------

    void StartMailboxSend(PendingSend p)
    {
        if (_mailbox == null) return;
        SetStatus("Sending the turn through the relay…");
        Task<string?> attempt;
        try { attempt = _mailbox.TrySendAsync(p.Id, p.Turn, p.Password); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { attempt = Task.FromResult<string?>("Could not send the turn: " + ex.Message); }
        _pendingSend = p;
        _sendAttempt = attempt;
        attempt.ContinueWith(_ => PostToUi(() => { if (!_waitingForSend) RunOnline(() => SendAttemptDoneAsync(attempt)); }),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// An attempt finished (on the UI thread; once per attempt): through → done; refused → "The turn could not be sent through the relay:
    /// {reason}" with Retry (the same turn again) or Close (the file is saved; send it another way).
    /// </summary>
    async Task SendAttemptDoneAsync(Task<string?> attempt)
    {
        if (!ReferenceEquals(attempt, _sendAttempt) || _pendingSend is not { } p) return;
        _sendAttempt = null;
        _pendingSend = null;
        string? problem;
        try { problem = await attempt; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { problem = ex.Message; }
        if (_closed) return;
        if (problem == null)
        {
            SetStatus("The turn is in the mailbox on the relay.");
            System.Diagnostics.Trace.WriteLine("turns: the turn is in the mailbox");
            return;
        }
        problem = OnlineLobbyModel.CleanText(problem, GuestSession.MaxHostText);   // may be relay text
        System.Diagnostics.Trace.WriteLine("turns: mailbox send failed: " + problem);
        var retry = await _dialogs.ChooseAsync(this, SendFailedText(problem) + "\n" + SendElsewhereText(p.Path), "Send Turn",
            new[] { "Retry", "Close" }, 0) == 0;
        if (_closed) return;
        if (retry) StartMailboxSend(p);
        else SetStatus(SendElsewhereText(p.Path));
    }

    static string SendFailedText(string reason) => "The turn could not be sent through the relay: " + reason;

    static string SendElsewhereText(string path) => $"The file is saved at {path}; send it another way.";

    /// <summary>
    /// Before the window closes, goes Back to the launcher or discards the game: a turn still being sent gets up to 15 s ("Sending
    /// turn…"); its outcome is then handled (Retry waits again). Still not through: asks whether to go anyway (default No). False = stay.
    /// </summary>
    /// <param name="question">Asked when the send is still running after the wait (Yes = go on anyway).</param>
    internal async Task<bool> ConfirmSendDoneAsync(string question = LeaveDuringSendText)
    {
        while (_sendAttempt is { } attempt && _pendingSend is { } p)
        {
            if (!attempt.IsCompleted)
            {
                _waitingForSend = true;
                try { await _dialogs.WaitForAsync(this, attempt, SendWait); }
                finally { _waitingForSend = false; }
            }
            if (!attempt.IsCompleted)
                return await _dialogs.AskYesNoAsync(this, question + "\n" + SendElsewhereText(p.Path), "Send Turn", defaultNo: true);
            await SendAttemptDoneAsync(attempt);   // Retry starts another attempt: the loop waits for it too
        }
        return true;
    }

    // ---------- the game's mailbox choices ----------

    void ApplyMailbox(TurnIdentity id, bool use, string relay, string password)
    {
        id.UseMailbox = use;
        if (!use) return;
        id.Relay = (relay ?? "").Trim();
        _mailboxPasswords[id.GameId] = password ?? "";
        if (!id.SetPassword(password)) SetStatus("The mailbox password could not be stored on this computer; it is kept until the window closes.");
        if (string.IsNullOrWhiteSpace(SettingsStore.Current.RelayAddress) && id.Relay.Length > 0)
        {
            var r = id.Relay;
            SettingsStore.Update(cur => cur with { RelayAddress = r });
        }
    }

    /// <summary>The game's mailbox password as known now: typed this session, else the stored one, else "" (never asks).</summary>
    string PasswordFor(TurnIdentity id) =>
        _mailboxPasswords.TryGetValue(id.GameId, out var pw) ? pw : id.GetPassword() ?? "";

    /// <summary>
    /// As <see cref="PasswordFor"/>, but a stored password that cannot be read here (another computer, a store entry that is gone) is asked
    /// for once per game per session. The answer is kept for the session and stored again (it may now succeed); Cancel means "".
    /// </summary>
    async Task<string> PasswordForAsync(TurnIdentity id)
    {
        if (_mailboxPasswords.TryGetValue(id.GameId, out var known)) return known;
        if (id.GetPassword() is { } stored) return stored;
        // Only a mailbox game needs its password: an unused one is never asked for.
        if (!id.UseMailbox || !id.HasStoredPassword || !_passwordAsked.Add(id.GameId)) return "";
        var typed = await _dialogs.AskPasswordAsync(this, PasswordCaption, PasswordPromptText) ?? "";
        _mailboxPasswords[id.GameId] = typed;
        if (typed.Length > 0) StorePasswordAgain(id, typed);
        return typed;
    }

    /// <summary>Stores a re-typed password in the game's saved identity (loaded afresh, so a player chosen but not yet sent is not saved).</summary>
    void StorePasswordAgain(TurnIdentity id, string password)
    {
        try
        {
            var saved = TurnIdentity.Load(id.GameId, TurnsRoot, null);
            if (!saved.SetPassword(password)) return;   // kept for this session only (in memory here)
            SaveIdentity(saved);
            id.ProtectedPassword = saved.ProtectedPassword;
        }
        catch (InvalidDataException ex) { System.Diagnostics.Trace.WriteLine("turns: " + ex.Message); }
    }

    void SaveIdentity(TurnIdentity id)
    {
        try { id.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("turns: could not save the game's identity: " + ex.Message);
            SetStatus("Could not remember this game's player on this computer: " + ex.Message);
        }
    }

    // ---------- Open Turn File ----------

    const string OpenTurnCaption = "Open Turn File";

    /// <summary>File → Open Turn File…: the picker (turn files), then the open.</summary>
    async Task OpenTurnFileDialogAsync()
    {
        if (_session is not { } s) return;
        var path = await _dialogs.OpenFileAsync(this, OpenTurnCaption, new[] { TurnFilter, AllFiles });
        if (path == null || _closed || !ReferenceEquals(s, _session)) return;
        await OpenTurnFileAsync(path);
    }

    /// <summary>Reads a turn file (anything malformed is said plainly, like a bad save) and opens it for replay. False when nothing opened.</summary>
    internal async Task<bool> OpenTurnFileAsync(string path)
    {
        TurnFile t;
        try { t = TurnFiles.Read(path); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowError($"Could not open '{path}':\n{ex.Message}");
            return false;
        }
        return await OpenTurnAsync(t, null);
    }

    /// <summary>The "New turn waiting" notice clicked: opens the waiting mailbox turn.</summary>
    internal async Task OpenWaitingTurnAsync()
    {
        if (_waiting is { } w) await OpenTurnAsync(w.Turn, w.ItemId);
    }

    /// <summary>
    /// Checks a received turn (other module, already played, older position, another game) and starts its replay (WinForms OpenTurn).
    /// Open anyway / Cancel, the unsaved game, the workbench's unsaved rules and who-are-you are asked first; any cancel leaves the game as
    /// it was. Returns false when nothing was opened.
    /// </summary>
    /// <param name="itemId">The mailbox item it came from (deleted after a successful, autosaved replay); null for a file.</param>
    async Task<bool> OpenTurnAsync(TurnFile t, string? itemId)
    {
        if (_session is not { } s) return false;
        if (s.IsGuest || IsOnline || _onlineStarting) { ShowError(TurnUiModel.OnlineText); return false; }
        if (s.IsReplaying) { ShowError(ModuleSession.ReplayingText); return false; }
        FlushNotes();   // pending note text becomes a command first, so the check below counts it as a local move
        var g0 = s.Game;
        TurnIdentity id;
        TurnCheck check;
        try
        {
            id = TurnIdentity.Load(t.GameId, TurnsRoot, null);
            check = TurnOpener.Check(s, t, id);
        }
        catch (InvalidDataException ex)
        {
            ShowError("Could not open the turn:\n" + ex.Message);
            return false;
        }
        System.Diagnostics.Trace.WriteLine($"turns: opening a turn from {TurnUiModel.ShownName(t.FromName)} ({check})");
        var players = t.Start.Players.Where(p => p != null).ToList();
        var next = TurnOpenDecision.Decide(check, players, id, openAnywayChosen: false);
        if (next.Step == TurnOpenStep.Refuse) { ShowError(next.Message ?? ""); return false; }
        if (next.Step == TurnOpenStep.AskOpenAnyway)
        {
            var answer = await _dialogs.ChooseAsync(this, next.Message ?? "", OpenTurnCaption, new[] { TurnUiModel.OpenAnywayText, "Cancel" }, 1);
            if (answer != 0 || !Same()) return false;
            next = TurnOpenDecision.Decide(check, players, id, openAnywayChosen: true);
        }
        if (check != TurnCheck.Ok && (!await ConfirmDiscardAsync() || !Same())) return false;
        // The rules stay as they are while the turn replays: an open workbench closes (it reopens from Tools once the replay is over).
        if (!await CloseWorkbenchForReplayAsync() || !Same()) return false;
        if (next.Step == TurnOpenStep.AskWhoAreYou && (!await AskWhoAreYouAsync(t, players, id) || !Same())) return false;
        FormulaWindows.CloseAllFor(s);
        var local = id.LocalPlayerId ?? "";
        var ctx = new ReplayContext(id, itemId, _savePath, _dirty, _recoveredAutosave, s.Game?.State.GameId, _hotSeat?.Current);
        if (_waiting is { } w && (w.ItemId == itemId || w.Turn.StartHash == t.StartHash)) ClearWaiting();   // this very turn is being opened
        StopHotSeatForTurns();   // never a handover over a replayed turn; it comes back for a local game afterwards
        TurnReplayModel m;
        _replayCtx = ctx;
        try { m = s.OpenTurn(t, local, id); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            FollowOpenGame(beginHotSeat: false, hotSeatCurrent: ctx.HotSeatCurrent);   // nothing was replaced: hot-seat comes back with the same player
            _replayCtx = null;
            ShowError("Could not open the turn:\n" + ex.Message);
            return false;
        }
        _replay = m;
        m.Changed += OnReplayChanged;
        ReplayBarHost.Bind(m);
        SetStatus($"Replaying the turn from {TurnUiModel.ShownName(t.FromName)}: Next, Play all or Stop.");
        UpdateUi();
        UpdateTitle();
        if (m.Done) OnReplayChanged();   // a turn without steps is done at once
        return true;

        // The same game is still open, local and not replaying (a question may have been open while it changed).
        bool Same() => !_closed && ReferenceEquals(_session, s) && ReferenceEquals(s.Game, g0) && !IsOnline && !_onlineStarting
            && !s.IsGuest && !s.IsReplaying;
    }

    /// <summary>"This turn is from {name}. Which player are you?" with the game's mailbox choice; the answer is remembered for the game.</summary>
    async Task<bool> AskWhoAreYouAsync(TurnFile t, IReadOnlyList<PlayerDef> players, TurnIdentity id)
    {
        var pre = TurnUiModel.DefaultLocalPlayer(players, id.LocalPlayerId, t.ToPlayerId);
        var password = await PasswordForAsync(id);
        var who = await _dialogs.AskWhoAreYouAsync(this, TurnDialogRules.FromPrompt(TurnUiModel.ShownName(t.FromName)), players, _session?.Rules, pre,
            new MailboxValues(id.UseMailbox, id.Relay ?? SettingsStore.Current.RelayAddress, password));
        if (who == null || !TurnUiModel.KnowsLocalPlayer(players, who.LocalPlayerId)) return false;
        id.LocalPlayerId = who.LocalPlayerId;
        ApplyMailbox(id, who.UseMailbox, who.Relay, who.Password);
        SaveIdentity(id);
        return true;
    }

    /// <summary>After each step, a stop, the end or a failure: on the end (or failure) the bar goes and the outcome is shown.</summary>
    void OnReplayChanged()
    {
        if (_replay is not { Done: true } m || _closed) return;
        var ctx = _replayCtx;
        EndReplayUi();
        if (m.Error != null)
        {
            // The previous game is back: so are its save target and unsaved state (hot-seat came back with the game, OnTurnGameReplaced).
            if (ctx != null) { _savePath = ctx.SavePath; _dirty = ctx.Dirty; _recoveredAutosave = ctx.Recovered; }
            if (ctx?.ItemId is { } stillThere) OnTurnWaiting(m.Turn, stillThere);   // still in the mailbox: the notice comes back
            UpdateUi();
            UpdateTitle();
            System.Diagnostics.Trace.WriteLine("turns: replay failed: " + m.Error);
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
            if (ctx?.ItemId != null) System.Diagnostics.Trace.WriteLine("turns: the replayed game could not be autosaved; the turn stays in the mailbox");
            UpdateMailboxWatch();
        }
        UpdateUi();
        UpdateTitle();
        FlushPhaseViews();
        var from = TurnUiModel.ShownName(m.Turn.FromName);
        System.Diagnostics.Trace.WriteLine($"turns: replayed {m.Count} step(s) from {from}; end {(m.EndMatches ? "matches" : "DIFFERS")}");
        SetStatus((m.ViewNotice ?? $"{from}'s turn is replayed. Your turn.") + (m.VersionNotice is { } vn ? " " + vn : ""));
        if (!m.EndMatches) ShowError(TurnReplayModel.EndMismatchText);
    }

    /// <summary>Writes an autosave now (even when timed autosave is off). True when one was written.</summary>
    bool ForceAutosave(string why)
    {
        try { return _autosaver?.SaveForced(DateTimeOffset.UtcNow) != null; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine($"{why} autosave: " + ex.Message);
            return false;
        }
    }

    /// <summary>A turn opened from the launcher could not be replayed and there is no game to go back to: never an empty window.</summary>
    void NoGameAfterFailedTurn(ModuleSession s) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (_closed || !ReferenceEquals(_session, s) || s.Game != null) return;
        if (LauncherAvailable) RunAsync(BackToLauncherAsync);
        else RunAsync(() => ChooseAndStartAsync(preselectNew: false));
    });

    void EndReplayUi()
    {
        if (_replay is not { } m) return;
        m.Changed -= OnReplayChanged;
        _replay = null;
        _replayCtx = null;
        ReplayBarHost.Bind(null);
        if (!_closed) UpdateUi();
    }

    // ---------- mailbox watch ----------

    /// <summary>Watches the open play-by-email game's mailbox when it uses one (no network otherwise); stops for any other game and online.</summary>
    void UpdateMailboxWatch()
    {
        if (_mailbox == null || _closed) return;
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
        System.Diagnostics.Trace.WriteLine("turns: watching the turn mailbox on " + id!.Relay);
    }

    async void DeleteThenWatch(string itemId)
    {
        var delete = _mailboxDelete = _mailbox!.DeleteAsync(itemId);
        try { await delete; }   // never throws; failures reach the status bar
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("turns: " + ex.Message); }
        if (!_closed) OnlineGuard(UpdateMailboxWatch);
    }

    void StopMailbox()
    {
        _mailbox?.Stop();
        _watchKey = null;
        ClearWaiting();
    }

    /// <summary>A turn for this game waits in the mailbox: a copy is kept here and the alert bar says so (also with the bar switched off).</summary>
    void OnTurnWaiting(TurnFile t, string itemId)
    {
        if (_closed || _session?.Game is not { } g) return;   // also mid-replay: the notice waits (opening it then says to finish first)
        if (!string.Equals(g.State.GameId, t.GameId, StringComparison.OrdinalIgnoreCase)) return;
        // A copy stays on this computer until the next turn is sent: the relay item goes once the replayed game is saved.
        var root = TurnsRoot;
        _ = Task.Run(() =>
        {
            // Off the UI thread: a large turn takes a moment to compress and write.
            if (ReceivedTurns.Keep(t, itemId, out var why, root) == null) System.Diagnostics.Trace.WriteLine("turns: could not keep a copy of the received turn: " + why);
        });
        _waiting = (t, itemId);
        var text = TurnUiModel.NewTurnWaitingText(t.FromName);
        AlertBarHost.SetNotice(text);
        UpdateAlertBarVisible();
        System.Diagnostics.Trace.WriteLine("turns: " + text);
    }

    /// <summary>Screenshot state "turn-notice": the chip as the mailbox watch raises it, without a watch or a turn (no network).</summary>
    internal void ShowTurnNoticeForScreenshot(string fromName)
    {
        AlertBarHost.SetNotice(TurnUiModel.NewTurnWaitingText(fromName));
        UpdateAlertBarVisible();
    }

    void OnMailboxStatus(string text)
    {
        if (_closed) return;
        text = OnlineLobbyModel.CleanText(text, GuestSession.MaxHostText);   // may carry relay text
        SetStatus(text);
        System.Diagnostics.Trace.WriteLine("turns: " + text);
    }

    void ClearWaiting()
    {
        _waiting = null;
        if (AlertBarHost.NoticeText == null) return;
        AlertBarHost.SetNotice(null);
        UpdateAlertBarVisible();
    }

    // ---------- Export Viewer Copy ----------

    /// <summary>File → Export Viewer Copy… (guests): the filtered view as a read-only save.</summary>
    internal async Task ExportViewerCopyAsync()
    {
        if (_session is not { IsGuest: true, Game: not null } s) return;
        var path = await _dialogs.SaveFileAsync(this, "Export Viewer Copy", SaveFilter, "qsave", "Viewer copy");
        if (path == null || !ReferenceEquals(_session, s)) return;
        try { s.ExportViewerCopy(path); }
        catch (Exception ex) when (IsUserError(ex)) { ShowError(ex.Message); return; }
        SetStatus("Viewer copy saved: " + Path.GetFileName(path));
    }
}
