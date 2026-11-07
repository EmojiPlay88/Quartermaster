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

    private readonly PinStore _pins;
    private readonly TimeSpan[] _backoff;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1), _requestLock = new(1, 1);
    private readonly object _gate = new();

    private Uri _relay = null!;
    private Func<CertificateCheck, bool> _onCert = _ => false;
    private ClientWebSocket? _ws;
    private bool _helloSent, _certRejected, _down, _disposed, _awaitingRejoin;
    private string? _role;
    private RelayMessage? _rejoin;
    private TaskCompletionSource<RelayMessage>? _pending;
    private Task _pump = Task.CompletedTask;

    public event Action<string, byte[]>? Delivered;
    public event Action<string>? MemberLeft;
    public event Action? HostLeft;
    public event Action<string>? Disconnected;
    /// <summary>Raised after a reconnect has re-joined the room (or, with no room yet, re-established the link).</summary>
    public event Action? Reconnected;

    public RelayClient(PinStore pins, IReadOnlyList<TimeSpan>? backoff = null)
    {
        _pins = pins;
        _backoff = backoff is { Count: > 0 } ? backoff.ToArray() : DefaultBackoff;
    }

    /// <summary>Connects; never throws on network errors (raises Disconnected and keeps retrying, except after a refused certificate or version).</summary>
    public async Task ConnectAsync(Uri relay, Func<CertificateCheck, bool> onUnknownOrChangedCert, CancellationToken ct)
    {
        _relay = relay.AbsolutePath is "/" or "" ? new UriBuilder(relay) { Path = "/qm" }.Uri : relay;
        _onCert = onUnknownOrChangedCert;
        var (ws, reason) = await TryConnect(ct);
        if (ws == null)
        {
            _down = true;
            Raise(reason!);
            if (_certRejected) return;
        }
        else lock (_gate) { _ws = ws; _helloSent = false; }
        _pump = Task.Run(() => Pump(ws));
    }

    // ---- requests ----

    public async Task<string> ReserveCodeAsync() =>
        ((CodeReserved)Expect(await Request("host", new ReserveCode(), typeof(CodeReserved)))).Code;

    public async Task CreateRoomAsync(string code, string verifier, LobbyListing draft)
    {
        var msg = new CreateRoom(code, verifier, draft);
        Expect(await Request("host", msg, typeof(RoomCreated)));
        lock (_gate) _rejoin = msg;
    }

    public async Task UpdateListingAsync(LobbyListing listing) => await Post("host", new UpdateListing(listing));

    public async Task<IReadOnlyList<LobbyListing>> ListLobbiesAsync(string moduleSha) =>
        ((Lobbies)Expect(await Request("browser", new ListLobbies(moduleSha), typeof(Lobbies)))).Listings;

    public async Task<string> JoinRoomAsync(string code, string verifier)
    {
        var msg = new JoinRoom(code, verifier);
        var id = ((Joined)Expect(await Request("guest", msg, typeof(Joined)))).MemberId;
        lock (_gate) _rejoin = msg;
        return id;
    }

    public Task SendAsync(string? to, byte[] sealed_) => Post(_role ?? "guest", new Send(to, sealed_));

    private static RelayMessage Expect(RelayMessage m) => m;

    private async Task Post(string role, RelayMessage msg)
    {
        if (!await SendMsg(role, msg)) throw new RelayException("Not connected to the relay.");
    }

    private async Task<RelayMessage> Request(string role, RelayMessage msg, Type expected)
    {
        await _requestLock.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<RelayMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _pending = tcs;
            try
            {
                if (!await SendMsg(role, msg)) throw new RelayException("Not connected to the relay.");
                var done = await Task.WhenAny(tcs.Task, Task.Delay(RequestTimeout, _stop.Token)).ConfigureAwait(false);
                if (done != tcs.Task) throw new RelayException("The relay did not answer.");
                var reply = await tcs.Task;
                if (reply is Refused r) throw new RelayException(r.Reason);
                if (!expected.IsInstanceOfType(reply)) throw new RelayException("The relay sent an unexpected answer.");
                return reply;
            }
            finally { lock (_gate) _pending = null; }
        }
        catch (OperationCanceledException) { throw new RelayException("Not connected to the relay."); }
        finally { _requestLock.Release(); }
    }

    private async Task<bool> SendMsg(string role, RelayMessage msg)
    {
        ClientWebSocket? ws;
        bool hello;
        lock (_gate)
        {
            ws = _ws;
            hello = !_helloSent;
            _role ??= role;
            if (ws == null || ws.State != WebSocketState.Open) return false;
            _helloSent = true;
        }
        try
        {
            await _sendLock.WaitAsync(_stop.Token).ConfigureAwait(false);
            try
            {
                if (hello) await SendRaw(ws, new Hello(Protocol.ProtocolVersion, _role!));
                await SendRaw(ws, msg);
            }
            finally { _sendLock.Release(); }
            return true;
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or IOException)
        {
            return false;
        }
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
                    lock (_gate) { _ws = ws; _helloSent = false; rejoin = _rejoin; _awaitingRejoin = rejoin != null; }
                    if (rejoin != null) { if (!await SendMsg(_role ?? "guest", rejoin)) { ws = Drop(ws); continue; } }
                    else { attempt = 0; _down = false; try { Reconnected?.Invoke(); } catch (Exception) { } }
                }

                var end = await ReceiveLoop(ws, ct).ConfigureAwait(false);
                lock (_gate) { _ws = null; _awaitingRejoin = false; }
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

                var fatal = await Dispatch(ws, msg);
                if (fatal != null) { ws.Abort(); return new End(fatal, true); }
            }
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
            case Ping:
                try { await _sendLock.WaitAsync(_stop.Token); try { await SendRaw(ws, new Pong()); } finally { _sendLock.Release(); } }
                catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
                break;
            case Refused r:
            {
                bool rejoin;
                lock (_gate) { rejoin = _awaitingRejoin && _pending == null; if (rejoin) _awaitingRejoin = false; }
                if (rejoin) return $"Could not re-join the game after reconnecting: {r.Reason}";
                if (!Complete(msg)) return r.Reason;
                break;
            }
            case Joined or RoomCreated:
            {
                bool rejoin;
                lock (_gate) { rejoin = _awaitingRejoin && _pending == null; if (rejoin) _awaitingRejoin = false; }
                if (rejoin) { _down = false; Try(() => Reconnected?.Invoke()); }
                else Complete(msg);
                break;
            }
            case CodeReserved or Lobbies: Complete(msg); break;
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
        lock (_gate) { ws = _ws; _ws = null; }
        try { ws?.Abort(); } catch (Exception) { }
        try { await _pump.ConfigureAwait(false); } catch (Exception) { }
        FailPending("Disposed.");
        _stop.Dispose();
    }
}
