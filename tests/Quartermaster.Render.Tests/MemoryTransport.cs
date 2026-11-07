using System.Collections.Concurrent;
using Quartermaster.Net;
using Quartermaster.Render;

namespace Quartermaster.Render.Tests;

/// <summary>
/// An in-memory relay room for tests: one host endpoint and any number of guest endpoints. Delivery is synchronous (the receiving
/// session marshals through its own dispatcher). Records every payload each endpoint receives and every listing the host publishes.
/// </summary>
internal sealed class MemoryRelay
{
    public const string HostMember = "host";
    private readonly Dictionary<string, MemoryTransport> _guests = new();

    public MemoryRelay() => Host = new MemoryTransport(this, null);

    public MemoryTransport Host { get; }
    public List<LobbyListing> Listings { get; } = new();

    public MemoryTransport Guest(string memberId)
    {
        var t = new MemoryTransport(this, memberId);
        _guests[memberId] = t;
        return t;
    }

    /// <summary>The guest's connection drops: the host hears MemberLeft.</summary>
    public void Drop(string memberId)
    {
        _guests.Remove(memberId);
        Host.RaiseMemberLeft(memberId);
    }

    internal void Route(MemoryTransport from, string? to, byte[] payload)
    {
        if (from.MemberId == null)
        {
            if (to != null && _guests.TryGetValue(to, out var g)) g.Deliver(HostMember, payload);
        }
        else Host.Deliver(from.MemberId, payload);
    }
}

internal sealed class MemoryTransport : ITransport
{
    private readonly MemoryRelay _relay;

    public MemoryTransport(MemoryRelay relay, string? memberId)
    {
        _relay = relay;
        MemberId = memberId;
    }

    /// <summary>Null for the host.</summary>
    public string? MemberId { get; }

    /// <summary>Every payload this endpoint received, in order (plaintext, as an ITransport hands it over).</summary>
    public ConcurrentQueue<byte[]> Inbox { get; } = new();

    public IEnumerable<GamePayload> Messages => Inbox.Select(b => Framing.Decode<GamePayload>(b));

    public IEnumerable<T> Of<T>() where T : GamePayload => Messages.OfType<T>();

    public Task Send(string? to, byte[] payload)
    {
        _relay.Route(this, to, payload);
        return Task.CompletedTask;
    }

    public void SendPayload(GamePayload p) => Send(null, Framing.Encode(p));

    public Task UpdateListing(LobbyListing listing)
    {
        _relay.Listings.Add(listing);
        return Task.CompletedTask;
    }

    internal void Deliver(string from, byte[] payload)
    {
        Inbox.Enqueue(payload);
        Received?.Invoke(from, payload);
    }

    /// <summary>Feeds raw bytes to this endpoint as if a member had sent them.</summary>
    public void Inject(string from, byte[] payload) => Received?.Invoke(from, payload);

    internal void RaiseMemberLeft(string id) => MemberLeft?.Invoke(id);
    public void RaiseMembersListed(IReadOnlyList<string> ids) => MembersListed?.Invoke(ids);
    public void RaiseDisconnected(string reason) => Disconnected?.Invoke(reason);
    public void RaiseReconnected() => Reconnected?.Invoke();

    public event Action<string, byte[]>? Received;
    public event Action<string>? MemberLeft;
    public event Action<string>? Disconnected;
    public event Action? Reconnected;
    public event Action<IReadOnlyList<string>>? MembersListed;
    public event Action? HostLeft;
    public event Action? HostBack;

    public void RaiseHostLeft() => HostLeft?.Invoke();
    public void RaiseHostBack() => HostBack?.Invoke();
}

/// <summary>A dispatcher that queues posted actions until the test pumps them (like a UI message loop).</summary>
internal sealed class QueueDispatcher
{
    private readonly ConcurrentQueue<Action> _q = new();

    public void Post(Action a)
    {
        _q.Enqueue(a);
    }

    public int Pump()
    {
        var n = 0;
        while (_q.TryDequeue(out var a)) { a(); n++; }
        return n;
    }

    /// <summary>Waits until something is posted (timer callbacks), then pumps.</summary>
    public bool WaitAndPump(TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (_q.IsEmpty)
        {
            if (DateTime.UtcNow > end) return false;
            Thread.Sleep(10);
        }
        Pump();
        return true;
    }
}
