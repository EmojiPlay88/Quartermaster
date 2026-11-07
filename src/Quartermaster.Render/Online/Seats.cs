using System.Security.Cryptography;
using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>
/// One seat at an online table: the host's or a guest's. A guest seat outlives its connection (marked disconnected) and is reclaimed only
/// with the rejoin token issued at first join; only the token's SHA-256 is kept.
/// </summary>
public sealed class Seat
{
    internal Seat(string playerId, string name, bool isHost, IReadOnlyList<string> sides, byte[]? tokenHash)
    {
        PlayerId = playerId;
        Name = name;
        IsHost = isHost;
        Sides = sides;
        TokenHash = tokenHash;
    }

    public string PlayerId { get; }
    public string Name { get; }
    public bool IsHost { get; }
    /// <summary>Side ids held (empty until a guest chooses).</summary>
    public IReadOnlyList<string> Sides { get; internal set; }
    /// <summary>The relay member id of the guest's current connection; null while disconnected (always null for the host).</summary>
    public string? MemberId { get; internal set; }
    public bool Connected => IsHost || MemberId != null;

    internal byte[]? TokenHash { get; }
    internal RateWindow Intents { get; } = new(Seats.MaxIntentsPerSecond);
    internal RateWindow Chats { get; } = new(Seats.MaxChatsPerSecond);

    internal PlayerDef ToPlayer() => new(PlayerId, Name, Sides.ToList());
}

/// <summary>At most N events in any one-second window (sliding).</summary>
internal sealed class RateWindow(int max)
{
    private readonly Queue<DateTime> _times = new();

    public bool TryTake(DateTime now)
    {
        while (_times.Count > 0 && now - _times.Peek() >= TimeSpan.FromSeconds(1)) _times.Dequeue();
        if (_times.Count >= max) return false;
        _times.Enqueue(now);
        return true;
    }
}

/// <summary>The seats of a hosted game. Not thread-safe: used on the game's owning thread only.</summary>
public sealed class Seats
{
    public const int MaxIntentsPerSecond = 20;
    public const int MaxChatsPerSecond = 5;
    public const int MaxName = 40;
    public const string NotASideText = "That is not a side of this game.";

    private readonly List<Seat> _seats = new();
    private int _next = 1;

    public Seats(PlayerDef host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _seats.Add(new Seat(host.Id, host.Name, true, (host.Sides ?? Array.Empty<string>()).ToList(), null));
    }

    public Seat Host => _seats[0];
    public IReadOnlyList<Seat> All => _seats;
    public IEnumerable<Seat> Guests => _seats.Skip(1);

    public Seat? ByMember(string? memberId) => memberId == null ? null : _seats.FirstOrDefault(x => x.MemberId == memberId);

    public Seat? ByPlayer(string? playerId) => playerId == null ? null : _seats.FirstOrDefault(x => x.PlayerId == playerId);

    public Seat? ByName(string name) => _seats.FirstOrDefault(x => string.Equals(x.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The guest seat this rejoin token was issued for (constant-time hash comparison against every seat), or null.</summary>
    public Seat? ByToken(string? token)
    {
        if (HashToken(token) is not { } h) return null;
        Seat? found = null;
        foreach (var s in _seats)
            if (s.TokenHash != null && CryptographicOperations.FixedTimeEquals(s.TokenHash, h)) found ??= s;
        return found;
    }

    /// <summary>A new guest seat with a fresh player id; returns it with the rejoin token to give the guest (never stored).</summary>
    public (Seat Seat, string Token) AddGuest(string name, IEnumerable<string> takenIds)
    {
        var taken = new HashSet<string>(takenIds.Concat(_seats.Select(x => x.PlayerId)));
        string id;
        do id = "g" + _next++; while (taken.Contains(id));
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var seat = new Seat(id, name, false, Array.Empty<string>(), HashToken(token));
        _seats.Add(seat);
        return (seat, token);
    }

    public bool Remove(Seat seat) => !seat.IsHost && _seats.Remove(seat);

    /// <summary>Side ids of the pack no seat holds, in pack order.</summary>
    public IReadOnlyList<string> FreeSides(RulesPack? pack)
    {
        var held = _seats.SelectMany(x => x.Sides).ToHashSet();
        return (pack?.Sides ?? new List<SideDef>()).Where(x => x?.Id != null && !held.Contains(x.Id)).Select(x => x.Id).ToList();
    }

    /// <summary>The game's player list: the host, then every guest holding at least one side.</summary>
    public List<PlayerDef> ToPlayers() => _seats.Where(x => x.IsHost || x.Sides.Count > 0).Select(x => x.ToPlayer()).ToList();

    /// <summary>
    /// Null when <paramref name="seat"/> may hold <paramref name="sides"/>: <see cref="Players.Validate"/> on the resulting player list.
    /// Sides still free (open seats) are not an error: for the check only, they count as the host's.
    /// </summary>
    public string? CheckChoice(Seat seat, IReadOnlyList<string> sides, RulesPack pack)
    {
        if (sides == null || sides.Count == 0) return $"{seat.Name} holds no side.";
        if (sides.Count > (pack.Sides?.Count ?? 0) || sides.Any(x => x is not { Length: > 0 and <= IntentExecutor.MaxId }))
            return NotASideText;
        var after = _seats.Select(x => ReferenceEquals(x, seat) ? x.ToPlayer() with { Sides = sides.ToList() } : x.ToPlayer())
            .Where(p => p.Sides.Count > 0 || p.Id == Host.PlayerId).ToList();
        var held = after.SelectMany(p => p.Sides).ToHashSet();
        var open = (pack.Sides ?? new List<SideDef>()).Where(x => x?.Id != null && !held.Contains(x.Id)).Select(x => x.Id).ToList();
        if (open.Count > 0) after[0] = after[0] with { Sides = after[0].Sides.Concat(open).ToList() };
        return Players.Validate(after, pack);
    }

    internal static byte[]? HashToken(string? token)
    {
        if (token is not { Length: 64 }) return null;
        try { return SHA256.HashData(Convert.FromHexString(token)); }
        catch (FormatException) { return null; }
    }
}
