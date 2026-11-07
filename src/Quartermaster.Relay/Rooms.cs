using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Quartermaster.Net;

namespace Quartermaster.Relay;

/// <summary>Relay limits (spec §7, launcher spec §4).</summary>
public static class Limits
{
    public const int MaxNameLength = 40;
    public const int MaxTitleLength = 100;
    public const int MaxSides = 16;
    public const int LobbiesPerIp = 20;
    public const int ReservationsPerIp = 20;
    public const int MembersPerRoom = 7; // the host's slot always counts, present or not
    public const int ConnectionsPerIp = 32;
    /// <summary>Largest frame accepted before Hello.</summary>
    public const int PreHelloMaxFrame = 4 * 1024;
    public static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);
    public const double MessagesPerSecond = 50;
    public const double Burst = 200;
    /// <summary>Bytes queued for one slow receiver before it is dropped.</summary>
    public const long MaxQueuedBytes = 4L * Framing.MaxFrame;
    public static readonly TimeSpan ListingMinRefresh = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ListingTtl = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan ReservationTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RoomIdle = TimeSpan.FromHours(24);
    public static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);
}

/// <summary>One WebSocket client. Outgoing frames are queued and written by the connection's writer loop.</summary>
public sealed class Connection
{
    private readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _abort = new();
    private readonly TimeProvider _time;
    private long _queuedBytes;
    private double _tokens = Limits.Burst;
    private long _lastRefill;

    public Connection(string ip, TimeProvider time)
    {
        Ip = ip;
        _time = time;
        _lastRefill = time.GetTimestamp();
    }

    /// <summary>The per-address bucket (<see cref="Bucket"/>) all per-IP limits use.</summary>
    public string Ip { get; }

    /// <summary>IPv4 address as is (IPv4-mapped IPv6 unwrapped); IPv6 by its /64 prefix.</summary>
    public static string Bucket(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    /// <summary>Cancelled when the connection is dropped without flushing (a receiver too slow to keep up).</summary>
    public CancellationToken Aborted => _abort.Token;
    public bool Greeted { get; internal set; }
    internal Room? Room { get; set; }
    internal string? MemberId { get; set; }
    internal bool IsHost => Room is not null && MemberId == Rooms.HostId;

    /// <summary>Set once the relay decided to close this connection.</summary>
    public (WebSocketCloseStatus Status, string Reason)? CloseRequest { get; private set; }
    public bool Closing => CloseRequest is not null;

    public ChannelReader<byte[]> Outgoing => _out.Reader;

    internal void Sent(byte[] frame) => Interlocked.Add(ref _queuedBytes, -frame.Length);

    /// <summary>Queues a message; never blocks. A receiver too slow to keep up is closed.</summary>
    public void Post(RelayMessage message) => PostFrame(Framing.Encode(message));

    internal void PostFrame(byte[] frame)
    {
        if (Closing) return;
        if (Interlocked.Add(ref _queuedBytes, frame.Length) > Limits.MaxQueuedBytes)
        {
            Close(WebSocketCloseStatus.PolicyViolation, "Too slow to receive; reconnect.");
            Abort();
            return;
        }
        _out.Writer.TryWrite(frame);
    }

    /// <summary>Flushes what is queued, then closes with the reason (first reason wins).</summary>
    public void Close(WebSocketCloseStatus status, string reason)
    {
        lock (_out)
        {
            CloseRequest ??= (status, reason);
        }
        _out.Writer.TryComplete();
    }

    /// <summary>Stops sending at once and drops everything still queued.</summary>
    public void Abort()
    {
        _out.Writer.TryComplete();
        try { _abort.Cancel(); } catch (ObjectDisposedException) { }
        while (_out.Reader.TryRead(out var f)) Interlocked.Add(ref _queuedBytes, -f.Length);
    }

    /// <summary>Token bucket: 50 messages/s, burst 200.</summary>
    public bool TakeToken()
    {
        var now = _time.GetTimestamp();
        _tokens = Math.Min(Limits.Burst, _tokens + _time.GetElapsedTime(_lastRefill, now).TotalSeconds * Limits.MessagesPerSecond);
        _lastRefill = now;
        if (_tokens < 1) return false;
        _tokens -= 1;
        return true;
    }
}

internal sealed class Room(string code, string verifierHash, string hostKeyHash, string creatorIp, LobbyListing listing, DateTimeOffset now)
{
    public string Code { get; } = code;
    public string VerifierHash { get; } = verifierHash;
    public string HostKeyHash { get; } = hostKeyHash;
    public string CreatorIp { get; } = creatorIp;
    public DateTimeOffset Created { get; } = now;
    public Connection? Host { get; set; }
    public Dictionary<string, Connection> Members { get; } = [];
    public int NextMember { get; set; }
    public LobbyListing Listing { get; set; } = listing;
    public LobbyListing? Pending { get; set; }
    public DateTimeOffset LastRefresh { get; set; } = now;
    public DateTimeOffset LastApplied { get; set; } = now;
    /// <summary>When the host was last seen (connected now, or left at).</summary>
    public DateTimeOffset LastHostSeen { get; set; } = now;
}

internal sealed record Reservation(string Code, Connection Owner, DateTimeOffset Expires);

/// <summary>
/// All relay state: code reservations, rooms with their members and public listings.
/// The relay routes <see cref="Send.Sealed"/> bytes without reading or keeping them, and never logs them.
/// </summary>
public sealed class Rooms : IDisposable
{
    public const string HostId = "host";
    public const string WrongCodeOrPassword = "Wrong code or password";
    private static readonly HashSet<string> RolesAllowed = ["host", "guest", "browser"];
    private static readonly string NoHash = new('0', 64), NoRoom = new('F', 64);

    private readonly object _lock = new();
    private readonly Dictionary<string, Room> _rooms = [];
    private readonly Dictionary<string, Reservation> _reservations = [];
    private readonly Dictionary<string, int> _connectionsPerIp = [];
    private readonly TimeProvider _time;
    private readonly ILogger<Rooms> _log;
    private readonly ITimer _sweeper;

    public Rooms(TimeProvider time, ILogger<Rooms> log)
    {
        _time = time;
        _log = log;
        _sweeper = time.CreateTimer(_ => Sweep(), null, Limits.SweepEvery, Limits.SweepEvery);
    }

    public void Dispose() => _sweeper.Dispose();

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>Handles one complete frame from a connection.</summary>
    public void Handle(Connection c, ReadOnlySpan<byte> frame)
    {
        RelayMessage message;
        try { message = Framing.Decode<RelayMessage>(frame); }
        catch (ProtocolException)
        {
            _log.LogDebug("Ignored a malformed message.");
            return;
        }

        if (!c.Greeted)
        {
            if (message is not Hello hello)
            {
                Refuse(c, "Send Hello first.");
                c.Close(WebSocketCloseStatus.PolicyViolation, "Send Hello first.");
            }
            else if (hello.Version != Protocol.ProtocolVersion)
            {
                Refuse(c, Protocol.MismatchMessage(Protocol.ProtocolVersion));
                c.Close(WebSocketCloseStatus.PolicyViolation, "Different network version.");
            }
            else if (!RolesAllowed.Contains(hello.Role))
            {
                Refuse(c, "Unknown role.");
                c.Close(WebSocketCloseStatus.PolicyViolation, "Unknown role.");
            }
            else c.Greeted = true;
            return;
        }

        lock (_lock)
        {
            SweepLocked();
            switch (message)
            {
                case Ping: c.Post(new Pong()); break;
                case ReserveCode: Reserve(c); break;
                case CreateRoom m: Create(c, m); break;
                case UpdateListing m: Update(c, m.Listing); break;
                case ListLobbies m: c.Post(new Lobbies(Listings(m.ModuleSha256))); break;
                case JoinRoom m: Join(c, m); break;
                case Send m: Route(c, m); break;
                default: _log.LogDebug("Ignored a {Type} message from a client.", message.GetType().Name); break;
            }
        }
    }

    /// <summary>Counts a new connection from the address bucket; false when it already has the maximum.</summary>
    public bool TryAdmit(string bucket)
    {
        lock (_lock)
        {
            _connectionsPerIp.TryGetValue(bucket, out var n);
            if (n >= Limits.ConnectionsPerIp) return false;
            _connectionsPerIp[bucket] = n + 1;
            return true;
        }
    }

    public void Release(string bucket)
    {
        lock (_lock)
        {
            if (!_connectionsPerIp.TryGetValue(bucket, out var n)) return;
            if (n <= 1) _connectionsPerIp.Remove(bucket);
            else _connectionsPerIp[bucket] = n - 1;
        }
    }

    /// <summary>A connection ended: drop its reservations, leave its room, notify the others.</summary>
    public void Disconnect(Connection c)
    {
        lock (_lock)
        {
            foreach (var r in _reservations.Values.Where(r => r.Owner == c).ToList()) _reservations.Remove(r.Code);
            var room = c.Room;
            if (room is null) return;
            if (c.IsHost)
            {
                if (room.Host == c)
                {
                    room.Host = null;
                    room.LastHostSeen = Now;
                    foreach (var m in room.Members.Values) m.Post(new HostLeft());
                    _log.LogInformation("A host left; the room stays open for its return.");
                }
            }
            else if (c.MemberId is { } id && room.Members.Remove(id))
            {
                room.Host?.Post(new MemberLeft(id));
            }
            c.Room = null;
            c.MemberId = null;
        }
    }

    private static void Refuse(Connection c, string reason) => c.Post(new Refused(reason));

    private void Reserve(Connection c)
    {
        var mine = _reservations.Values.Where(r => r.Owner.Ip == c.Ip).OrderBy(r => r.Expires).ToList();
        if (mine.Count >= Limits.ReservationsPerIp) _reservations.Remove(mine[0].Code);
        string code;
        do code = GameCode.New();
        while (_rooms.ContainsKey(code) || _reservations.ContainsKey(code));
        _reservations[code] = new Reservation(code, c, Now + Limits.ReservationTtl);
        c.Post(new CodeReserved(code));
    }

    private void Create(Connection c, CreateRoom m)
    {
        if (c.Room is not null) { Refuse(c, "Already in a game on this connection."); return; }
        var code = GameCode.Normalize(m.Code);
        var hash = HashVerifier(m.AuthVerifier);
        var keyHash = HashVerifier(m.HostKey);

        if (_rooms.TryGetValue(code, out var existing))
        {
            // The host coming back to its room, e.g. after a dropped connection: same password AND same host key.
            // Both compares always run (constant time, no hint which one failed).
            bool passwordOk = Secrets.VerifierEquals(hash ?? NoHash, existing.VerifierHash);
            bool keyOk = Secrets.VerifierEquals(keyHash ?? NoHash, existing.HostKeyHash);
            if (!(passwordOk & keyOk) || hash is null || keyHash is null) { Refuse(c, WrongCodeOrPassword); return; }
            if (existing.Host is not null) { Refuse(c, "This game already has a host."); return; }
            existing.Host = c;
            existing.LastHostSeen = Now;
            existing.Listing = Sanitize(m.Draft, code);
            existing.Pending = null;
            existing.LastRefresh = existing.LastApplied = Now;
            c.Room = existing;
            c.MemberId = HostId;
            c.Post(new RoomCreated(code));
            c.Post(new Members(existing.Members.Keys.ToList()));
            foreach (var guest in existing.Members.Values) guest.Post(new HostBack());
            _log.LogInformation("A host returned to its room.");
            return;
        }

        // No room: only the creator's own live reservation may be used; anything else gets the same answer as a wrong password.
        if (!_reservations.TryGetValue(code, out var reservation) || reservation.Owner != c || hash is null)
        {
            Refuse(c, WrongCodeOrPassword);
            return;
        }
        if (keyHash is null) { Refuse(c, "The host key must be 32 bytes of hex."); return; }

        var fromIp = _rooms.Values.Where(r => r.CreatorIp == c.Ip).ToList();
        if (fromIp.Count >= Limits.LobbiesPerIp)
        {
            // Make room by closing the oldest lobby from this address whose host has gone; else refuse.
            var stale = fromIp.Where(r => r.Host is null).OrderBy(r => r.LastHostSeen).FirstOrDefault();
            if (stale is null) { Refuse(c, $"Too many lobbies from this address (at most {Limits.LobbiesPerIp})."); return; }
            CloseRoom(stale, "The game was closed.");
        }

        _reservations.Remove(code);
        var room = new Room(code, hash, keyHash, c.Ip, Sanitize(m.Draft, code), Now) { Host = c };
        _rooms[code] = room;
        c.Room = room;
        c.MemberId = HostId;
        c.Post(new RoomCreated(code));
        _log.LogInformation("Room opened ({Count} open).", _rooms.Count);
    }

    private void Update(Connection c, LobbyListing listing)
    {
        if (!c.IsHost || c.Room is not { } room || room.Host != c)
        {
            _log.LogDebug("Ignored a listing update from a connection that is not a host.");
            return;
        }
        var now = Now;
        room.LastRefresh = now;
        var clean = Sanitize(listing, room.Code);
        if (now - room.LastApplied >= Limits.ListingMinRefresh)
        {
            room.Listing = clean;
            room.Pending = null;
            room.LastApplied = now;
        }
        else room.Pending = clean; // applied once 5 s have passed since the last change
    }

    private List<LobbyListing> Listings(string moduleSha)
    {
        var now = Now;
        var result = new List<LobbyListing>();
        foreach (var room in _rooms.Values.OrderBy(r => r.Created))
        {
            if (room.Host is null || now - room.LastRefresh > Limits.ListingTtl) continue;
            if (room.Pending is { } p && now - room.LastApplied >= Limits.ListingMinRefresh)
            {
                room.Listing = p;
                room.Pending = null;
                room.LastApplied = now;
            }
            if (string.Equals(room.Listing.ModuleSha256, moduleSha, StringComparison.OrdinalIgnoreCase))
                result.Add(room.Listing);
        }
        return result;
    }

    private void Join(Connection c, JoinRoom m)
    {
        if (c.Room is not null) { Refuse(c, "Already in a game on this connection."); return; }
        var code = GameCode.Normalize(m.Code);
        var hash = HashVerifier(m.AuthVerifier);
        _rooms.TryGetValue(code, out var room);
        // Same answer (and the same work) for an unknown code, a malformed verifier and a wrong password: no oracle.
        bool match = Secrets.VerifierEquals(hash ?? NoHash, room?.VerifierHash ?? NoRoom);
        if (!match || hash is null || room is null) { Refuse(c, WrongCodeOrPassword); return; }
        if (room.Members.Count + 1 >= Limits.MembersPerRoom) { Refuse(c, "This game is full."); return; }
        var id = $"g{++room.NextMember}";
        room.Members[id] = c;
        c.Room = room;
        c.MemberId = id;
        c.Post(new Joined(id));
    }

    private void Route(Connection c, Send m)
    {
        if (c.Room is not { } room || c.MemberId is not { } from) return;
        Connection? target;
        if (c.IsHost) target = m.ToMemberId is { } to && room.Members.TryGetValue(to, out var member) ? member : null;
        else target = m.ToMemberId is null or HostId ? room.Host : null; // guests talk to the host only
        if (target is null) return;
        byte[] frame;
        try { frame = Framing.Encode<RelayMessage>(new Deliver(from, m.Sealed)); }
        catch (ProtocolException)
        {
            c.Close(WebSocketCloseStatus.MessageTooBig, "Message is too large.");
            return;
        }
        target.PostFrame(frame);
    }

    private void CloseRoom(Room room, string reason)
    {
        _rooms.Remove(room.Code);
        foreach (var m in room.Members.Values)
        {
            m.Room = null;
            m.MemberId = null;
            m.Close(WebSocketCloseStatus.NormalClosure, reason);
        }
        if (room.Host is { } h)
        {
            h.Room = null;
            h.MemberId = null;
            h.Close(WebSocketCloseStatus.NormalClosure, reason);
        }
        _log.LogInformation("Room closed ({Count} open).", _rooms.Count);
    }

    /// <summary>Drops expired reservations and rooms whose host has been gone for 24 h.</summary>
    public void Sweep()
    {
        lock (_lock) SweepLocked();
    }

    private void SweepLocked()
    {
        var now = Now;
        foreach (var r in _reservations.Values.Where(r => r.Expires <= now).ToList()) _reservations.Remove(r.Code);
        foreach (var room in _rooms.Values.Where(r => r.Host is null && now - r.LastHostSeen > Limits.RoomIdle).ToList())
            CloseRoom(room, "The game closed: its host was away for 24 hours.");
    }

    /// <summary>SHA-256 (hex) of the 32-byte verifier, or null when it is not 64 hex digits.</summary>
    private static string? HashVerifier(string verifier)
    {
        if (verifier is not { Length: 64 }) return null;
        try { return Convert.ToHexString(SHA256.HashData(Convert.FromHexString(verifier))); }
        catch (FormatException) { return null; }
    }

    private static string Clip(string? s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length <= max ? s : s[..max];
    }

    private static IReadOnlyList<string> ClipList(IReadOnlyList<string>? list) =>
        (list ?? []).Take(Limits.MaxSides).Select(s => Clip(s, Limits.MaxNameLength)).ToList();

    /// <summary>The relay fills the code and bounds every field; nothing else from the client is kept.</summary>
    private static LobbyListing Sanitize(LobbyListing l, string code) => new(
        code,
        Clip(l.Name, Limits.MaxNameLength),
        Clip(l.HostName, Limits.MaxNameLength),
        Clip(l.ModuleSha256, 64),
        Clip(l.ModuleTitle, Limits.MaxTitleLength),
        ClipList(l.Sides),
        ClipList(l.FreeSides),
        Math.Clamp(l.Players, 0, 99),
        Math.Clamp(l.MaxPlayers, 0, 99),
        l.Locked,
        l.Version);
}
