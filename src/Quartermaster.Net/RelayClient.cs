using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;

namespace Quartermaster.Net;

/// <summary>An unknown or changed certificate; the caller decides whether to trust it.</summary>
public sealed record CertificateCheck(string HostPort, string Fingerprint, string? PinnedFingerprint);

/// <summary>A request (reserve, create, join, list) that the relay refused or that could not complete.</summary>
public sealed class RelayException(string message) : Exception(message);

/// <summary>
/// WebSocket client for the relay. Network failures never escape as exceptions from background work: they surface
/// as <see cref="Disconnected"/>, followed by automatic reconnects that re-join the same room. Only request methods
/// throw <see cref="RelayException"/> when their own request is refused or the link is down.
/// </summary>
public sealed class RelayClient : IAsyncDisposable
{
    public static readonly TimeSpan[] DefaultBackoff =
        { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly string VersionRefusalPrefix = Protocol.MismatchMessage(0).Split('(')[0];

    private readonly PinStore _pins;
    private readonly TimeSpan[] _backoff;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1), _requestLock = new(1, 1);
    private readonly object _gate = new();

    private Uri _relay = null!;
    private Func<CertificateCheck, bool> _onCert = _ => false;
    private ClientWebSocket? _ws;
    private volatile bool _certRejected, _down, _disposed;
    private bool _helloSent, _awaitingRejoin, _postSinceRequest;
    private readonly TimeSpan _rejoinTimeout;
    private int _connectStarted;
    private string? _role;
    private volatile string? _memberId;
    private RelayMessage? _rejoin;
    private TaskCompletionSource<RelayMessage>? _pending;
    private TaskCompletionSource? _rejoinDone;
    private Task _pump = Task.CompletedTask;

    /// <summary>The guest's member id from the latest (re)join; null before joining.</summary>
    public string? MemberId => _memberId;
    /// <summary>A request the relay refused that nobody was waiting on (e.g. a rate-limited listing update). The link stays up.</summary>
    public event Action<string>? RequestRefused;

    public event Action<string, byte[]>? Delivered;
    public event Action<string>? MemberLeft;
    public event Action? HostLeft;
    /// <summary>Guest: the host re-attached to the room.</summary>
    public event Action? HostBack;
    /// <summary>Host: the members present, sent by the relay right after the host re-attaches.</summary>
    public event Action<IReadOnlyList<string>>? MembersListed;
    public event Action<string>? Disconnected;
    /// <summary>Raised after a reconnect has re-joined the room (or, with no room yet, re-established the link).</summary>
    public event Action? Reconnected;

    /// <remarks>
    /// Events are raised on the client's pump thread (or the connecting thread) and must not block or call back into
    /// this client synchronously. A Refused arriving after a fire-and-forget Post (UpdateListing/Send) is reported
    /// only through <see cref="RequestRefused"/>; it is never used to fail a pending request.
    /// </remarks>
    public RelayClient(PinStore pins, IReadOnlyList<TimeSpan>? backoff = null, TimeSpan? rejoinTimeout = null)
    {
        _rejoinTimeout = rejoinTimeout ?? RequestTimeout;
        _pins = pins;
        _backoff = backoff is { Count: > 0 } ? backoff.ToArray() : DefaultBackoff;
    }

    /// <summary>
    /// Connects; never throws on network errors (raises Disconnected and keeps retrying, except after a refused certificate or version).
    /// The certificate callback runs on a thread-pool thread (during the TLS handshake), so UI code must marshal to its own thread.
    /// Throws <see cref="RelayException"/> if called twice or after disposal.
    /// </summary>
    public async Task ConnectAsync(Uri relay, Func<CertificateCheck, bool> onUnknownOrChangedCert, CancellationToken ct)
    {
        if (_disposed) throw new RelayException("The relay client was closed.");
        if (Interlocked.Exchange(ref _connectStarted, 1) != 0) throw new RelayException("Already connected.");
        _relay = relay.AbsolutePath is "/" or "" ? new UriBuilder(relay) { Path = "/qm" }.Uri : relay;
        _onCert = onUnknownOrChangedCert;
        var (ws, reason) = await TryConnect(ct).ConfigureAwait(false);
        if (_disposed) { ws?.Dispose(); return; }
        if (ws == null)
        {
            _down = true;
            Raise(reason!);
            if (_certRejected) return;
        }
        else lock (_gate) { _ws = ws; _helloSent = false; }
        lock (_gate)
        {
            if (_disposed) { ws?.Dispose(); _ws = null; return; }
            _pump = Task.Run(() => Pump(ws));
        }
    }

    // ---- requests ----

    public async Task<string> ReserveCodeAsync() =>
        ((CodeReserved)Expect(await Request("host", new ReserveCode(), typeof(CodeReserved)).ConfigureAwait(false))).Code;

    /// <summary>The host key of the hosted room (generated once per room, resent on every host rejoin); persist it to resume the room after a restart.</summary>
    public string? HostKey { get { lock (_gate) return (_rejoin as CreateRoom)?.HostKey; } }

    /// <param name="hostKey">Pass the persisted key to re-attach to an existing room; null generates a fresh one.</param>
    public async Task CreateRoomAsync(string code, string verifier, LobbyListing draft, string? hostKey = null)
    {
        var msg = new CreateRoom(code, verifier, hostKey ?? Secrets.NewHostKey(), draft);
        Expect(await Request("host", msg, typeof(RoomCreated)).ConfigureAwait(false));
        lock (_gate) _rejoin = msg;
    }

    public async Task UpdateListingAsync(LobbyListing listing)
    {
        await Post("host", new UpdateListing(listing)).ConfigureAwait(false);
        lock (_gate) if (_rejoin is CreateRoom c) _rejoin = c with { Draft = listing };
    }

    public async Task<IReadOnlyList<LobbyListing>> ListLobbiesAsync(string moduleSha) =>
        ((Lobbies)Expect(await Request("browser", new ListLobbies(moduleSha), typeof(Lobbies)).ConfigureAwait(false))).Listings;

    public async Task<string> JoinRoomAsync(string code, string verifier)
    {
        var msg = new JoinRoom(code, verifier);
        var id = ((Joined)Expect(await Request("guest", msg, typeof(Joined)).ConfigureAwait(false))).MemberId;
        lock (_gate) _rejoin = msg;
        _memberId = id;
        return id;
    }

    // ---- turn mailbox (any connection; never joins a room, never touches the rejoin) ----

    /// <summary>Stores a sealed blob in a box; returns the relay's item id. Throws <see cref="RelayException"/> when refused.</summary>
    public async Task<string> MailboxPutAsync(string box, byte[] sealed_)
    {
        if (!MailboxLimits.IsBoxId(box)) throw new RelayException(MailboxLimits.BadBox);
        if (sealed_ is not { Length: > 0 and <= MailboxLimits.MaxSealedBytes }) throw new RelayException(MailboxLimits.BadItem);
        return ((MailboxStored)await Request("mailbox", new MailboxPut(box, sealed_), typeof(MailboxStored)).ConfigureAwait(false)).ItemId;
    }

    /// <summary>The items waiting in up to <see cref="MailboxLimits.MaxBoxesPerList"/> boxes, oldest first.</summary>
    public async Task<IReadOnlyList<MailboxItemInfo>> MailboxListAsync(IReadOnlyList<string> boxes)
    {
        if (boxes.Count > MailboxLimits.MaxBoxesPerList || boxes.Any(b => !MailboxLimits.IsBoxId(b))) throw new RelayException(MailboxLimits.BadBox);
        return ((MailboxItems)await Request("mailbox", new MailboxList(boxes.ToList()), typeof(MailboxItems)).ConfigureAwait(false)).Items;
    }

    /// <summary>The sealed bytes of one item (still encrypted; open with the mailbox seal key).</summary>
    public async Task<byte[]> MailboxGetAsync(string box, string itemId)
    {
        if (!MailboxLimits.IsBoxId(box)) throw new RelayException(MailboxLimits.BadBox);
        var item = (MailboxItem)await Request("mailbox", new MailboxGet(box, itemId), typeof(MailboxItem)).ConfigureAwait(false);
        if (item.Box != box || item.ItemId != itemId) throw new RelayException("The relay sent an unexpected answer.");
        return item.Sealed;
    }

    public async Task MailboxDeleteAsync(string box, string itemId)
    {
        if (!MailboxLimits.IsBoxId(box)) throw new RelayException(MailboxLimits.BadBox);
        await Request("mailbox", new MailboxDelete(box, itemId), typeof(MailboxDeleted)).ConfigureAwait(false);
    }

    public Task SendAsync(string? to, byte[] sealed_) => Post(_role ?? "guest", new Send(to, sealed_));

    private static RelayMessage Expect(RelayMessage m) => m;

    private async Task Post(string role, RelayMessage msg)
    {
        if (_disposed) throw new RelayException("The relay client was closed.");
        if (!await SendMsg(role, msg, true).ConfigureAwait(false)) throw new RelayException("Not connected to the relay.");
    }

    private async Task<RelayMessage> Request(string role, RelayMessage msg, Type expected)
    {
        if (_disposed) throw new RelayException("The relay client was closed.");
        try { await _requestLock.WaitAsync(_stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw new RelayException("The relay client was closed."); }
        try
        {
            var tcs = new TaskCompletionSource<RelayMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _pending = tcs;
            try
            {
                if (!await SendMsg(role, msg, false).ConfigureAwait(false)) throw new RelayException("Not connected to the relay.");
                var done = await Task.WhenAny(tcs.Task, Task.Delay(RequestTimeout, _stop.Token)).ConfigureAwait(false);
                if (done != tcs.Task) throw new RelayException("The relay did not answer.");
                var reply = await tcs.Task.ConfigureAwait(false);
                if (reply is Refused r) throw new RelayException(r.Reason);
                if (!expected.IsInstanceOfType(reply)) throw new RelayException("The relay sent an unexpected answer.");
                return reply;
            }
            finally { lock (_gate) _pending = null; }
        }
        catch (OperationCanceledException) { throw new RelayException("Not connected to the relay."); }
        finally { _requestLock.Release(); }
    }

    /// <summary>"browser" and "mailbox" are weak roles: a later host/guest request replaces them (before Hello is sent).</summary>
    private static string Upgrade(string? current, string wanted) =>
        current == null || (IsWeak(current) && !IsWeak(wanted)) ? wanted : current;

    private static bool IsWeak(string role) => role is "browser" or "mailbox";

    /// <summary>
    /// App send: takes the send lock, and if a rejoin is still unanswered releases it and waits (re-checking after every
    /// acquisition), so nothing reaches the relay between a reconnect and its rejoin answer.
    /// </summary>
    private async Task<bool> SendMsg(string role, RelayMessage msg, bool isPost)
    {
        try
        {
            while (true)
            {
                Task? wait = null;
                await _sendLock.WaitAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    lock (_gate) if (_awaitingRejoin) wait = _rejoinDone?.Task;
                    if (wait == null) return await SendLocked(role, msg, isPost).ConfigureAwait(false);
                }
                finally { _sendLock.Release(); }
                await Task.WhenAny(wait, Task.Delay(RequestTimeout, _stop.Token)).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>Caller holds _sendLock. Hello is decided and sent here, so it always precedes the first message.</summary>
    private async Task<bool> SendLocked(string role, RelayMessage msg, bool isPost)
    {
        try
        {
            ClientWebSocket? ws;
            bool hello;
            string effective;
            lock (_gate)
            {
                ws = _ws;
                hello = !_helloSent;
                effective = Upgrade(_role, role);
            }
            if (ws == null || ws.State != WebSocketState.Open) return false;
            if (hello)
            {
                await SendRaw(ws, new Hello(Protocol.ProtocolVersion, effective)).ConfigureAwait(false);
                lock (_gate) _helloSent = true;
            }
            // Set before sending: the relay's answer may be read by the receive pump before SendRaw returns, and a Refused
            // answering this Post must not be taken as the answer to a pending Request.
            lock (_gate) _postSinceRequest = isPost;
            await SendRaw(ws, msg).ConfigureAwait(false);
            lock (_gate) _role = effective;
            return true;
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>If the relay doesn't answer the rejoin in time, drop the socket; the pump then reconnects again.</summary>
    private void WatchRejoin(ClientWebSocket ws, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_rejoinTimeout, ct).ConfigureAwait(false);
                bool stuck;
                lock (_gate) stuck = _awaitingRejoin && ReferenceEquals(_ws, ws);
                if (stuck) ws.Abort();
            }
            catch (Exception) { }
        }, ct);
    }

    private Task SendRaw(ClientWebSocket ws, RelayMessage msg) =>
        ws.SendAsync(Framing.Encode(msg), WebSocketMessageType.Text, true, _stop.Token);

    // ---- connection management ----

    private async Task<(ClientWebSocket?, string?)> TryConnect(CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = ValidateCert;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            linked.CancelAfter(TimeSpan.FromSeconds(20));
            await ws.ConnectAsync(_relay, linked.Token).ConfigureAwait(false);
            return (ws, null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ws.Dispose();
            return (null, _certRejected
                ? $"The relay's certificate was not trusted ({_relay.Host}:{_relay.Port})."
                : $"Could not reach the relay: {Describe(e)}");
        }
    }

    private static string Describe(Exception e) => (e.InnerException ?? e).Message;

    private bool ValidateCert(object sender, System.Security.Cryptography.X509Certificates.X509Certificate? cert,
        System.Security.Cryptography.X509Certificates.X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (cert == null) return false;
        var hostPort = $"{_relay.Host}:{_relay.Port}";
        var fp = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
        var pinned = _pins.Get(hostPort);
        if (string.Equals(pinned, fp, StringComparison.OrdinalIgnoreCase)) return true;
        bool ok;
        try { ok = _onCert(new CertificateCheck(hostPort, fp, pinned)); }
        catch (Exception) { ok = false; }
        if (ok) { _pins.Set(hostPort, fp); _certRejected = false; }
        else _certRejected = true;
        return ok;
    }

    private void Raise(string reason)
    {
        try { Disconnected?.Invoke(reason); } catch (Exception) { }
    }

    private void FailPending(string reason)
    {
        TaskCompletionSource<RelayMessage>? p;
        lock (_gate) p = _pending;
        p?.TrySetResult(new Refused(reason));
    }

    private async Task Pump(ClientWebSocket? ws)
    {
        int attempt = 0;
        var ct = _stop.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (ws == null)
                {
                    await Task.Delay(_backoff[Math.Min(attempt++, _backoff.Length - 1)], ct).ConfigureAwait(false);
                    var (w, reason) = await TryConnect(ct).ConfigureAwait(false);
                    if (w == null)
                    {
                        if (_certRejected) { Raise(reason!); return; }
                        continue;
                    }
                    ws = w;
                    RelayMessage? rejoin;
                    bool sent = true;
                    // Hold the send lock from publishing the socket until Hello + rejoin are written, so no app message can slip in first.
                    await _sendLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        lock (_gate)
                        {
                            if (_disposed) return; // Dispose landed first; the finally below disposes the socket
                            _ws = ws; _helloSent = false; rejoin = _rejoin; _awaitingRejoin = rejoin != null;
                            if (rejoin != null) _rejoinDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        }
                        if (rejoin != null)
                        {
                            sent = await SendLocked(rejoin is CreateRoom ? "host" : "guest", rejoin, false).ConfigureAwait(false);
                            if (sent) WatchRejoin(ws, ct);
                        }
                    }
                    finally { _sendLock.Release(); }
                    if (!sent) { var dead = ws; ws = Drop(dead); EndRejoin(); continue; }
                    if (rejoin == null) { attempt = 0; _down = false; try { Reconnected?.Invoke(); } catch (Exception) { } }
                }

                var end = await ReceiveLoop(ws, ct).ConfigureAwait(false);
                lock (_gate) { _ws = null; }
                EndRejoin();
                FailPending(end.Reason);
                ws.Dispose();
                ws = null;
                if (!_down) { _down = true; Raise(end.Reason); }
                if (end.Fatal) return;
                attempt = 0;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (!_disposed) { Raise($"Relay link failed: {Describe(e)}"); }
        finally { try { ws?.Dispose(); } catch (Exception) { } }
    }

    /// <summary>Marks the rejoin answered (or abandoned) and releases app sends waiting on it.</summary>
    private bool EndRejoin()
    {
        TaskCompletionSource? done;
        bool was;
        lock (_gate) { was = _awaitingRejoin; _awaitingRejoin = false; done = _rejoinDone; _rejoinDone = null; }
        done?.TrySetResult();
        return was;
    }

    private ClientWebSocket? Drop(ClientWebSocket ws)
    {
        lock (_gate) { _ws = null; }
        ws.Dispose();
        return null;
    }

    private readonly record struct End(string Reason, bool Fatal);

    private async Task<End> ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                        return new End(string.IsNullOrEmpty(r.CloseStatusDescription)
                            ? "The relay closed the connection." : $"The relay closed the connection: {r.CloseStatusDescription}", false);
                    ms.Write(buf, 0, r.Count);
                    if (ms.Length > Framing.MaxFrame)
                    {
                        ws.Abort();
                        return new End("The relay sent an oversized message.", false);
                    }
                } while (!r.EndOfMessage);

                RelayMessage msg;
                try { msg = Framing.Decode<RelayMessage>(ms.GetBuffer().AsSpan(0, (int)ms.Length)); }
                catch (ProtocolException) { continue; }

                var fatal = await Dispatch(ws, msg).ConfigureAwait(false);
                if (fatal != null) { ws.Abort(); return new End(fatal, true); }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new End("Lost connection to the relay.", false); // socket aborted (e.g. rejoin timeout)
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            return new End($"Lost connection to the relay: {Describe(e)}", false);
        }
        return new End("Disconnected.", true);
    }

    /// <summary>Returns a reason when the link must be abandoned for good.</summary>
    private async Task<string?> Dispatch(ClientWebSocket ws, RelayMessage msg)
    {
        switch (msg)
        {
            case Deliver d: Try(() => Delivered?.Invoke(d.FromMemberId, d.Sealed)); break;
            case Quartermaster.Net.MemberLeft m: Try(() => MemberLeft?.Invoke(m.MemberId)); break;
            case Quartermaster.Net.HostLeft: Try(() => HostLeft?.Invoke()); break;
            case Quartermaster.Net.HostBack: Try(() => HostBack?.Invoke()); break;
            case Quartermaster.Net.Members mm: Try(() => MembersListed?.Invoke(mm.MemberIds)); break;
            case Ping:
                try { await _sendLock.WaitAsync(_stop.Token).ConfigureAwait(false); try { await SendRaw(ws, new Pong()).ConfigureAwait(false); } finally { _sendLock.Release(); } }
                catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
                break;
            case Refused r:
            {
                // Fatal only when it answers Hello (version) or the rejoin; anything else keeps the link.
                if (r.Reason.StartsWith(VersionRefusalPrefix, StringComparison.Ordinal))
                {
                    Complete(msg);
                    return r.Reason;
                }
                if (EndRejoin()) return $"Could not re-join the game after reconnecting: {r.Reason}";
                // Attributable to the pending Request only if no fire-and-forget Post was sent since it.
                bool attributable;
                lock (_gate) attributable = _pending != null && !_postSinceRequest;
                if (!attributable || !Complete(msg)) Try(() => RequestRefused?.Invoke(r.Reason));
                break;
            }
            case Joined or RoomCreated:
            {
                // The rejoin has its own completion; its answer never completes an app request.
                if (EndRejoin())
                {
                    if (msg is Joined j) _memberId = j.MemberId;
                    _down = false;
                    Try(() => Reconnected?.Invoke());
                }
                else Complete(msg);
                break;
            }
            case CodeReserved or Lobbies or MailboxStored or MailboxItems or MailboxItem or MailboxDeleted: Complete(msg); break;
        }
        return null;
    }

    private bool Complete(RelayMessage msg)
    {
        TaskCompletionSource<RelayMessage>? p;
        lock (_gate) p = _pending;
        return p != null && p.TrySetResult(msg);
    }

    private static void Try(Action a)
    {
        try { a(); } catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        ClientWebSocket? ws;
        Task pump;
        lock (_gate) { ws = _ws; _ws = null; pump = _pump; }
        try { ws?.Abort(); } catch (Exception) { }
        try { await pump.ConfigureAwait(false); } catch (Exception) { }
        EndRejoin();
        FailPending("Disposed.");
        // _stop is deliberately not disposed: late callers still observe its cancelled token.
    }
}
