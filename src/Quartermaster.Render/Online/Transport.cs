using System.Security.Cryptography;
using Quartermaster.Net;

namespace Quartermaster.Render;

/// <summary>
/// What an online session needs from the relay, with game payloads as plaintext bytes (the encoded <see cref="GamePayload"/>): the
/// transport encrypts and decrypts. Events may be raised on any thread and their handlers must not block; sessions marshal them to the
/// game's owning thread. Implementations never throw from <see cref="Send"/> or <see cref="UpdateListing"/> (failures are dropped).
/// </summary>
public interface ITransport
{
    /// <summary>
    /// To a member id; null = to the host (guests). Never throws; the task faults (<see cref="ProtocolException"/>) only when the payload
    /// is too large to send, other failures are dropped.
    /// </summary>
    Task Send(string? to, byte[] payload);

    /// <summary>Host only: publish the public lobby listing.</summary>
    Task UpdateListing(LobbyListing listing);

    /// <summary>(from member id, plaintext payload).</summary>
    event Action<string, byte[]>? Received;
    /// <summary>Host: a guest left the room (its connection dropped or it left).</summary>
    event Action<string>? MemberLeft;
    /// <summary>This client lost the relay (reason); it reconnects by itself.</summary>
    event Action<string>? Disconnected;
    event Action? Reconnected;
    /// <summary>Host, after re-attaching to its room: the guests still in it.</summary>
    event Action<IReadOnlyList<string>>? MembersListed;
    /// <summary>Guest: the host left / came back.</summary>
    event Action? HostLeft;
    event Action? HostBack;
}

/// <summary>
/// <see cref="ITransport"/> over a <see cref="RelayClient"/>: every payload is sealed with the game key (AES-GCM, <see cref="Secrets.Seal"/>)
/// before it reaches the relay, and every delivery is opened with it. Undecryptable, garbage or oversized frames are dropped silently
/// and counted in <see cref="Dropped"/>; nothing here throws to the caller. Never logs or formats relay messages.
/// </summary>
public sealed class RelayTransport : ITransport, IDisposable
{
    private readonly RelayClient _client;
    private readonly byte[] _key;
    private int _dropped, _sendFailures;
    private volatile bool _disposed;

    /// <param name="gameKey">The end-to-end key, <see cref="Secrets.GameKey"/>(password, code); 32 bytes.</param>
    public RelayTransport(RelayClient client, byte[] gameKey)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (gameKey is not { Length: 32 }) throw new ArgumentException("The game key must be exactly 32 bytes.", nameof(gameKey));
        _key = (byte[])gameKey.Clone();
        _client.Delivered += OnDelivered;
        _client.MemberLeft += OnMemberLeft;
        _client.Disconnected += OnDisconnected;
        _client.Reconnected += OnReconnected;
        _client.MembersListed += OnMembersListed;
        _client.HostLeft += OnHostLeft;
        _client.HostBack += OnHostBack;
    }

    /// <summary>Incoming frames dropped (undecryptable, garbage or too large).</summary>
    public int Dropped => Volatile.Read(ref _dropped);

    /// <summary>Outgoing payloads that could not be sent (too large, or the relay was unreachable).</summary>
    public int SendFailures => Volatile.Read(ref _sendFailures);

    public event Action<string, byte[]>? Received;
    public event Action<string>? MemberLeft;
    public event Action<string>? Disconnected;
    public event Action? Reconnected;
    public event Action<IReadOnlyList<string>>? MembersListed;
    public event Action? HostLeft;
    public event Action? HostBack;

    /// <summary>
    /// Never throws. The returned task faults (with a <see cref="ProtocolException"/>) only when the payload is too large to go through the
    /// relay, sealed or not; every other failure (the relay unreachable) is counted and dropped.
    /// </summary>
    public async Task Send(string? to, byte[] payload)
    {
        if (_disposed || payload == null) return;
        byte[] sealed_;
        try
        {
            if (payload.Length > Framing.MaxFrame) throw new ProtocolException("Message is too large.");
            sealed_ = Secrets.Seal(_key, payload);
            if (!FitsRelayFrame(to, sealed_.Length)) throw new ProtocolException("Message is too large.");
        }
        catch (ProtocolException)
        {
            Interlocked.Increment(ref _sendFailures);
            throw;
        }
        try { await _client.SendAsync(to, sealed_).ConfigureAwait(false); }
        catch (Exception)
        {
            Interlocked.Increment(ref _sendFailures);
        }
    }

    /// <summary>
    /// Whether a <see cref="Net.Send"/> (and the matching <see cref="Deliver"/>) carrying this many sealed bytes stays within
    /// <see cref="Framing.MaxFrame"/>: base64 of the bytes, the member id (escaped at worst) and the JSON around them.
    /// </summary>
    internal static bool FitsRelayFrame(string? to, int sealedLength) =>
        4L * ((sealedLength + 2L) / 3) + 6L * (to?.Length ?? 0) + 256 <= Framing.MaxFrame;

    public async Task UpdateListing(LobbyListing listing)
    {
        try { await _client.UpdateListingAsync(listing).ConfigureAwait(false); }
        catch (Exception) { Interlocked.Increment(ref _sendFailures); }
    }

    /// <summary>The plaintext of a delivered frame, or null when it must be dropped (too large, not sealed with this key, garbage).</summary>
    internal static byte[]? TryOpen(byte[] key, byte[]? sealed_)
    {
        if (sealed_ == null || sealed_.Length == 0 || sealed_.Length > Framing.MaxFrame) return null;
        try
        {
            var plain = Secrets.Open(key, sealed_, Framing.MaxFrame);   // bounded before inflating
            return plain.Length == 0 || plain.Length > Framing.MaxFrame ? null : plain;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    private void OnDelivered(string from, byte[] sealed_)
    {
        if (TryOpen(_key, sealed_) is not { } plain) { Interlocked.Increment(ref _dropped); return; }
        Raise(() => Received?.Invoke(from, plain));
    }

    private void OnMemberLeft(string id) => Raise(() => MemberLeft?.Invoke(id));
    private void OnDisconnected(string reason) => Raise(() => Disconnected?.Invoke(reason));
    private void OnReconnected() => Raise(() => Reconnected?.Invoke());
    private void OnMembersListed(IReadOnlyList<string> ids) => Raise(() => MembersListed?.Invoke(ids));
    private void OnHostLeft() => Raise(() => HostLeft?.Invoke());
    private void OnHostBack() => Raise(() => HostBack?.Invoke());

    /// <summary>A throwing subscriber must not break the client's pump.</summary>
    private static void Raise(Action a)
    {
        try { a(); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        _client.Delivered -= OnDelivered;
        _client.MemberLeft -= OnMemberLeft;
        _client.Disconnected -= OnDisconnected;
        _client.Reconnected -= OnReconnected;
        _client.MembersListed -= OnMembersListed;
        _client.HostLeft -= OnHostLeft;
        _client.HostBack -= OnHostBack;
        _disposed = true;
    }
}
