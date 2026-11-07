using System.Collections.Concurrent;
using Quartermaster.Core;
using Quartermaster.Net;

namespace Quartermaster.Render;

/// <summary>
/// The turn mailbox on the game's relay: sends turns into the recipient's box and watches the local player's own box (now, then every
/// <see cref="PollInterval"/>). Every relay call runs off the calling thread on a short-lived <see cref="RelayClient"/> (connect, act,
/// dispose); results and events reach the app only through <c>post</c>. Never throws on network errors or bad items: failures are
/// reported through <see cref="Status"/>, once per kind until a mailbox call succeeds again. Items that cannot be opened (wrong
/// password, garbage) are skipped and remembered; turns already replayed here are deleted silently; a turn is raised once per session.
/// </summary>
public sealed class MailboxService : IDisposable
{
    /// <summary>How often the watched box is checked.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    /// <summary>Exact copy: the sealed turn does not fit the mailbox.</summary>
    public const string TooLargeText = "This turn is too large for the mailbox; send the file another way.";
    public const string NoRecipientText = "Choose who the turn is for.";
    public const string NoRelayText = "Choose a relay for this game's mailbox first.";
    public const string NoPlayerText = "Choose which player you are in this game before checking the mailbox.";
    public const string OtherGameText = "This turn belongs to another game.";
    public const string UnreadableText = "A turn in the mailbox could not be opened (wrong mailbox password?). It was skipped.";

    private enum Failure { Setup, Unreachable, Refused, Unreadable }

    private readonly Func<string, RelayClient> _connect;
    private readonly Action<Action> _post;
    private readonly TimeProvider _time;
    private readonly Func<CertificateCheck, bool> _trust;
    private readonly object _gate = new();
    private readonly HashSet<Failure> _reported = new();
    private readonly ConcurrentDictionary<RelayClient, byte> _watchClients = new();
    private WatchState? _watch;

    /// <param name="connect">Makes a new, unconnected client for a relay address (the app supplies its pin store).</param>
    /// <param name="post">Runs an action on the app's thread (events and status are raised only through it).</param>
    /// <param name="time">Clock of the polling timer (tests pass a fake one).</param>
    /// <param name="trust">Answers an unknown or changed relay certificate (runs on a thread-pool thread); null = only pinned certificates.</param>
    public MailboxService(Func<string, RelayClient> connect, Action<Action> post, TimeProvider? time = null,
        Func<CertificateCheck, bool>? trust = null)
    {
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _time = time ?? TimeProvider.System;
        _trust = trust ?? (_ => false);
    }

    /// <summary>A turn waiting in the local player's box: the turn and its item id (pass it to <see cref="DeleteAsync"/> after replaying it).</summary>
    public event Action<TurnFile, string>? TurnWaiting;

    /// <summary>A short note for the status bar: a failure (once per kind until a success) or why a send was refused.</summary>
    public event Action<string>? Status;

    /// <summary>The poll running now (completed when none); for tests.</summary>
    internal Task CurrentPoll { get { lock (_gate) return _watch?.Current ?? Task.CompletedTask; } }

    /// <summary>Polls finished since the service was made; for tests.</summary>
    internal int Polls => Volatile.Read(ref _polls);

    private int _polls;

    // ---- send ----

    /// <summary>
    /// Seals <paramref name="t"/> with the game's mailbox key and stores it in the recipient's box. False (with a <see cref="Status"/>
    /// note) when refused: no recipient, no relay, too large (<see cref="TooLargeText"/>, before connecting), or a relay or network failure.
    /// Never throws for those; the work runs off the calling thread.
    /// </summary>
    public async Task<bool> SendAsync(TurnIdentity id, TurnFile t, string password) =>
        await TrySendAsync(id, t, password).ConfigureAwait(false) == null;

    /// <summary>As <see cref="SendAsync"/>, giving why the send was refused (null = stored in the recipient's box).</summary>
    public Task<string?> TrySendAsync(TurnIdentity id, TurnFile t, string password)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(t);
        var gameId = id.GameId;
        var relay = id.Relay;
        var pw = password ?? "";
        return Task.Run(async () =>
        {
            try
            {
                if (string.IsNullOrEmpty(t.ToPlayerId)) return Refuse(NoRecipientText);
                if (!string.Equals(t.GameId, gameId, StringComparison.OrdinalIgnoreCase)) return Refuse(OtherGameText);
                var uri = OnlineLobbyModel.ParseRelay(relay, out var err);
                if (string.IsNullOrWhiteSpace(relay)) return Refuse(NoRelayText);
                if (uri == null) return Refuse(err ?? NoRelayText);
                byte[] plain;
                try { plain = TurnFiles.ToBytes(t); }
                catch (InvalidOperationException) { return Refuse(TooLargeText); }
                var box = Secrets.MailboxBoxId(Secrets.MailboxBoxKey(pw, gameId), t.ToPlayerId);
                // The box id is the associated data: the item opens only in the box it was sent to.
                var sealed_ = Secrets.Seal(Secrets.MailboxSealKey(pw, gameId), plain, box);
                if (sealed_.Length > MailboxLimits.MaxSealedBytes) return Refuse(TooLargeText);
                var (ok, problem) = await WithClient(relay!, uri, null, CancellationToken.None,
                    c => c.MailboxPutAsync(box, sealed_)).ConfigureAwait(false);
                if (!ok) return Refuse(problem?.Text ?? "Turn mailbox: the send was cancelled.");
                Succeeded();
                return null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Refuse("Could not send the turn: " + ex.Message);
            }
        });
    }

    private string Refuse(string text)
    {
        Post(() => Status?.Invoke(text));
        return text;
    }

    // ---- watch ----

    /// <summary>
    /// Starts watching the local player's box of game <paramref name="gameId"/> (stops any earlier watch): one poll now, then one
    /// every <see cref="PollInterval"/>, never two at once. Does nothing but report when the identity has no relay or local player.
    /// </summary>
    public void Watch(string gameId, TurnIdentity id, string password)
    {
        ArgumentNullException.ThrowIfNull(gameId);
        ArgumentNullException.ThrowIfNull(id);
        Stop();
        var uri = OnlineLobbyModel.ParseRelay(id.Relay, out var err);
        if (string.IsNullOrEmpty(id.LocalPlayerId)) { Report(Failure.Setup, NoPlayerText); return; }
        if (string.IsNullOrWhiteSpace(id.Relay)) { Report(Failure.Setup, NoRelayText); return; }
        if (uri == null) { Report(Failure.Setup, err ?? NoRelayText); return; }
        var w = new WatchState(gameId, id, id.Relay!, uri, id.LocalPlayerId!, password ?? "");
        lock (_gate)
        {
            _watch = w;
            _reported.Clear();
        }
        StartPoll(w);
        var timer = _time.CreateTimer(_ => StartPoll(w), null, PollInterval, PollInterval);
        lock (_gate)
        {
            if (w.Stopped) timer.Dispose();
            else w.Timer = timer;
        }
    }

    /// <summary>Stops watching: cancels the timer and any poll in flight and closes its connections. Safe to call any time.</summary>
    public void Stop()
    {
        WatchState? w;
        lock (_gate)
        {
            w = _watch;
            _watch = null;
            if (w == null) return;
            w.Stopped = true;
        }
        try { w.Timer?.Dispose(); } catch (Exception) { }
        try { w.Cts.Cancel(); } catch (Exception) { }
        try { w.Cts.Dispose(); } catch (Exception) { }   // a poll still running reads the token only before this, or fails quietly (stopped)
        foreach (var c in _watchClients.Keys)
            if (_watchClients.TryRemove(c, out _)) _ = c.DisposeAsync().AsTask();
    }

    public void Dispose() => Stop();

    /// <summary>
    /// The watched game's identity changed (a turn was replayed and its start recorded as played): the watch reads the new one, so a
    /// played turn still in the box is deleted rather than raised again. Ignored for another game.
    /// </summary>
    public void SetIdentity(TurnIdentity id)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_gate)
            if (_watch is { } w && string.Equals(w.GameId, id.GameId, StringComparison.OrdinalIgnoreCase)) w.Identity = id;
    }

    /// <summary>
    /// Removes a raised item from the watched box (after its turn was replayed). Never throws; failures go to <see cref="Status"/>.
    /// An item the relay no longer has counts as removed.
    /// </summary>
    public Task DeleteAsync(string itemId)
    {
        WatchState? w;
        lock (_gate) w = _watch;
        if (w == null || string.IsNullOrEmpty(itemId)) return Task.CompletedTask;
        return Task.Run(() => DeleteCore(w, itemId));
    }

    private async Task DeleteCore(WatchState w, string itemId)
    {
        try
        {
            var box = w.Box();
            var (ok, problem) = await WithClient(w.Relay, w.Uri, w, w.Cts.Token, async c =>
            {
                try { await c.MailboxDeleteAsync(box, itemId).ConfigureAwait(false); }
                catch (RelayException ex) when (ex.Message == MailboxLimits.NoSuchItem) { }
                return 0;
            }).ConfigureAwait(false);
            if (w.Stopped) return;
            if (ok) Succeeded();
            else Report(problem!.Kind, problem.Text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!w.Stopped) Report(Failure.Unreachable, "Turn mailbox: " + ex.Message);
        }
    }

    private void StartPoll(WatchState w)
    {
        lock (_gate)
        {
            if (w.Stopped || !w.Current.IsCompleted) return;
            w.Current = Task.Run(() => Poll(w));
        }
    }

    private async Task Poll(WatchState w)
    {
        try
        {
            var box = w.Box();
            var unreadable = false;
            var (ok, failure) = await WithClient(w.Relay, w.Uri, w, w.Cts.Token, async c =>
            {
                var items = await c.MailboxListAsync([box]).ConfigureAwait(false);
                if (w.Stopped) return 0;
                var fetched = 0;
                foreach (var item in items)
                {
                    if (w.Stopped) return 0;
                    if (fetched >= MaxItemsPerPoll) break;   // the rest waits for the next poll
                    if (item is null || item.Box != box || !MailboxLimits.IsItemId(item.ItemId) || !w.Seen.Add(item.ItemId)) continue;
                    fetched++;
                    byte[] sealed_;
                    try { sealed_ = await c.MailboxGetAsync(box, item.ItemId).ConfigureAwait(false); }
                    catch (RelayException ex) when (ex.Message == MailboxLimits.NoSuchItem)
                    {
                        w.Seen.Remove(item.ItemId); // gone meanwhile (another window took it)
                        continue;
                    }
                    catch
                    {
                        w.Seen.Remove(item.ItemId); // try again next poll
                        throw;
                    }
                    var (t, readable) = Read(w, box, sealed_);
                    if (!readable) { unreadable = true; continue; }
                    if (t == null) continue;   // another game's turn, one not for this player, or this player's own: skipped quietly
                    var itemId = item.ItemId;
                    Post(() =>
                    {
                        if (w.Stopped) return;
                        // Played start hashes change on the app's thread, so they are read there.
                        if (w.Identity.PlayedStartHashes.Contains(t.StartHash)) _ = Task.Run(() => DeleteCore(w, itemId));
                        else TurnWaiting?.Invoke(t, itemId);
                    });
                }
                return 0;
            }).ConfigureAwait(false);
            if (w.Stopped) return;
            if (!ok) { Report(failure!.Kind, failure.Text); return; }
            Succeeded();   // only a whole poll that worked lets failures be reported again
            if (unreadable) Report(Failure.Unreadable, UnreadableText);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!w.Stopped) Report(Failure.Unreachable, "Turn mailbox: " + ex.Message);
        }
        finally
        {
            Interlocked.Increment(ref _polls);
        }
    }

    /// <summary>
    /// The turn in a sealed item (opened with the box id as associated data). Readable false: it cannot be opened (wrong password,
    /// moved from another box, garbage). Turn null but readable: another game's turn, one for another player or the local player's
    /// own, all skipped without a word.
    /// </summary>
    private static (TurnFile? Turn, bool Readable) Read(WatchState w, string box, byte[] sealed_)
    {
        TurnFile t;
        try { t = TurnFiles.FromBytes(Secrets.Open(w.SealKey(), sealed_, (int)TurnFiles.MaxPlain, box)); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, false);
        }
        if (!string.Equals(t.GameId, w.GameId, StringComparison.OrdinalIgnoreCase)) return (null, true);
        if (!string.Equals(t.ToPlayerId, w.PlayerId, StringComparison.Ordinal) || string.Equals(t.FromPlayerId, w.PlayerId, StringComparison.Ordinal))
            return (null, true);
        return (t, true);
    }

    /// <summary>At most this many items are fetched by one poll (a box holds at most <see cref="MailboxLimits.MaxItemsPerBox"/>).</summary>
    public const int MaxItemsPerPoll = 50;

    // ---- plumbing ----

    private sealed record Problem(Failure Kind, string Text);

    /// <summary>Connects a fresh client, runs <paramref name="act"/>, disposes the client. A failure comes back as a problem, never thrown.</summary>
    private async Task<(bool Ok, Problem? Problem)> WithClient<T>(string relay, Uri uri, WatchState? w, CancellationToken ct,
        Func<RelayClient, Task<T>> act)
    {
        RelayClient client;
        try { client = _connect(relay); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (false, new Problem(Failure.Setup, "Turn mailbox: " + ex.Message));
        }
        string? down = null;
        client.Disconnected += reason => down ??= reason;
        if (w != null)
        {
            _watchClients[client] = 0;
            if (w.Stopped) { _watchClients.TryRemove(client, out _); await client.DisposeAsync().ConfigureAwait(false); return (false, null); }
        }
        try
        {
            await client.ConnectAsync(uri, _trust, ct).ConfigureAwait(false);
            await act(client).ConfigureAwait(false);
            return (true, null);
        }
        catch (RelayException ex)
        {
            return down != null
                ? (false, new Problem(Failure.Unreachable, "Turn mailbox: " + down))
                : (false, new Problem(Failure.Refused, "Turn mailbox: " + ex.Message));
        }
        finally
        {
            if (w != null) _watchClients.TryRemove(client, out _);
            try { await client.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
        }
    }

    /// <summary>A mailbox call worked: every failure kind may be reported again.</summary>
    private void Succeeded()
    {
        lock (_gate) _reported.Clear();
    }

    private void Report(Failure kind, string text)
    {
        lock (_gate) if (!_reported.Add(kind)) return;
        Post(() => Status?.Invoke(text));
    }

    private void Post(Action a)
    {
        try
        {
            _post(() =>
            {
                try { a(); }
                catch (Exception) { } // a handler's failure never reaches the poller
            });
        }
        catch (Exception) { } // the app is closing
    }

    private sealed class WatchState(string gameId, TurnIdentity identity, string relay, Uri uri, string playerId, string password)
    {
        private byte[]? _sealKey;
        private string? _box;

        private volatile TurnIdentity _identity = identity;

        public string GameId { get; } = gameId;
        public string PlayerId { get; } = playerId;
        /// <summary>Read on the app's thread; replaced by <see cref="MailboxService.SetIdentity"/>.</summary>
        public TurnIdentity Identity { get => _identity; set => _identity = value; }
        public string Relay { get; } = relay;
        public Uri Uri { get; } = uri;
        public CancellationTokenSource Cts { get; } = new();
        /// <summary>Items raised, deleted or skipped this session (touched only by the one poll in flight).</summary>
        public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);
        public ITimer? Timer { get; set; }
        public Task Current { get; set; } = Task.CompletedTask;
        public volatile bool Stopped;

        /// <summary>The local player's box id (PBKDF2: derived off the app's thread, once).</summary>
        public string Box() => _box ??= Secrets.MailboxBoxId(Secrets.MailboxBoxKey(password, GameId), PlayerId);

        public byte[] SealKey() => _sealKey ??= Secrets.MailboxSealKey(password, GameId);
    }
}
