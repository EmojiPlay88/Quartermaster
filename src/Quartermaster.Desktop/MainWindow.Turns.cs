using Avalonia.Controls;
using Quartermaster.Core;
using Quartermaster.Desktop.Online;
using Quartermaster.Desktop.Shell;
using Quartermaster.Net;
using Quartermaster.Render;

namespace Quartermaster.Desktop;

/// <summary>
/// Turn files (port of WinForms MainForm.Turns, first half; spec 6): the turn game, Game → Send Turn… with the relay mailbox send, and the
/// guest's File → Export Viewer Copy…. A play-by-email game is not a hot-seat game: while one is open there is no handover, and the view
/// is the local player's sides. Every mailbox event reaches the window through <see cref="PostToUi"/>.
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

    /// <summary>A turn on its way to the relay: the same in-memory turn is resent on Retry.</summary>
    sealed record PendingSend(TurnIdentity Id, TurnFile Turn, string Password, string Path);

    /// <summary>The send in flight (null when none) and the attempt running for it.</summary>
    PendingSend? _pendingSend;
    Task<string?>? _sendAttempt;
    /// <summary>True while a close or discard waits for the send: the outcome is handled by the waiter, not by the continuation.</summary>
    bool _waitingForSend;

    /// <summary>The open game's turn identity (tests).</summary>
    internal TurnIdentity? TurnIdentityForTests => _turnIdentity;

    /// <summary>The relay mailbox (send now; the watch comes with Open Turn File).</summary>
    internal MailboxService? Mailbox => _mailbox;

    /// <summary>Game → Send Turn… (tests).</summary>
    internal MenuItem? SendTurnItem => _sendTurn;

    /// <summary>The mailbox send attempt still running or not yet handled (null when none; tests).</summary>
    internal Task? MailboxSendTask => _sendAttempt;

    void InitTurns()
    {
        // The client connects inside the service's worker: the certificate question is asked over this window from there.
        _mailbox = new MailboxService(_ => new RelayClient(RelayTrust.Pins()), PostToUi, null, c => RelayTrust.Ask(this, _dialogs, c));
    }

    void DisposeTurns()
    {
        try { _mailbox?.Dispose(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("turns: " + ex.Message); }
    }

    // ---------- state of the open game ----------

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
                else _hotSeat?.Adopt(hotSeatCurrent);
                UpdatePlayerButton();
            }
        }
        if (watch) UpdateMailboxWatch();
        UpdateTurnUi();
    }

    /// <summary>The mailbox watch follows the open game (port stage 5d task 5); nothing yet.</summary>
    void UpdateMailboxWatch()
    {
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
        var id = TurnIdentity.Load(g.State.GameId);   // InvalidDataException: shown
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
        var pruned = ReceivedTurns.Prune(t.GameId);
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
        if (!id.HasStoredPassword || !_passwordAsked.Add(id.GameId)) return "";
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
            var saved = TurnIdentity.Load(id.GameId);
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
