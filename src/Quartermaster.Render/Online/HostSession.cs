using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Net;
using Quartermaster.Rules;
using Quartermaster.Vassal;
using NetIntentResult = Quartermaster.Net.IntentResult;

namespace Quartermaster.Render;

/// <summary>
/// The host's side of a live online game (spec 5, 8a; launcher spec 4). Owns the seats, answers joins, executes guest intents through
/// <see cref="IntentExecutor"/> and sends every connected guest that holds a side its filtered view (<see cref="PlayerView.For"/>) after
/// each change. Everything touching the game runs on the owning (UI) thread: transport events are marshalled through the
/// <c>post</c> dispatcher and never block. Never throws to the transport; bad payloads are dropped and counted.
/// <para>
/// Replies: a refused join gets <see cref="Kicked"/>; a side choice is answered with an <see cref="NetIntentResult"/> whose id is
/// <see cref="Guid.Empty"/> (then, when accepted, a snapshot); a refused <see cref="Net.Chat"/> payload is answered the same way.
/// </para>
/// </summary>
public sealed class HostSession : IDisposable
{
    public const string DifferentModuleText = "Different module";
    public const string NoNameText = "Enter a player name.";
    public const string FullText = "This game is full.";
    public const string NoRulesText = "The host has no rules pack loaded.";
    public const string SlowDownText = "Slow down.";
    public static string NameTakenText(string name) => $"A player named {name} is already in this game.";

    public static readonly TimeSpan SnapshotDelay = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan ListingInterval = TimeSpan.FromSeconds(30);

    private readonly ModuleSession _s;
    private readonly IntentExecutor _exec;
    private readonly ITransport _t;
    private readonly Action<Action> _post;
    private readonly LobbyListing _draft;
    private readonly bool _locked;
    private readonly Timer _snapshotTimer;
    private readonly Timer _listingTimer;
    private bool _snapshotDue;
    private bool _disposed;
    private long _version;
    private int _dropped;
    private byte[]? _packBytes;
    private RulesPack? _packBytesFor;

    /// <param name="post">Runs an action on the game's owning thread (the App passes BeginInvoke; tests a synchronous or queued dispatcher).</param>
    public HostSession(ModuleSession s, IntentExecutor exec, ITransport t, string password, string code, LobbyListing listing, Action<Action> post)
    {
        _s = s ?? throw new ArgumentNullException(nameof(s));
        _exec = exec ?? throw new ArgumentNullException(nameof(exec));
        _t = t ?? throw new ArgumentNullException(nameof(t));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        ArgumentNullException.ThrowIfNull(listing);
        _locked = !string.IsNullOrEmpty(password);
        _draft = listing with { Code = GameCode.Normalize(code ?? listing.Code) };
        Seats = new Seats(HostPlayer());

        _t.Received += OnReceived;
        _t.MemberLeft += OnMemberLeft;
        _t.MembersListed += OnMembersListed;
        _t.Disconnected += OnDisconnected;
        _t.Reconnected += OnReconnected;
        _s.ViewChanged += OnViewChanged;
        _s.GameReplaced += OnGameReplaced;

        _snapshotTimer = new Timer(_ => Post(FlushSnapshots), null, Timeout.Infinite, Timeout.Infinite);
        _listingTimer = new Timer(_ => Post(PublishListing), null, ListingInterval, ListingInterval);
        SyncPlayers();
        PublishListing();
    }

    public Seats Seats { get; }

    /// <summary>Raised on the owning thread when a seat is taken, reclaimed, changes sides, drops or is freed.</summary>
    public event Action? SeatsChanged;

    /// <summary>Raised on the owning thread when the host's own relay connection drops or comes back.</summary>
    public event Action? ConnectionChanged;

    public bool RelayConnected { get; private set; } = true;

    /// <summary>The version of the last snapshot sent (monotonic for this session).</summary>
    public long SnapshotVersion => _version;

    /// <summary>Payloads dropped: undecodable, from members without a seat, or out of place.</summary>
    public int Dropped => _dropped;

    /// <summary>The public listing as last published.</summary>
    public LobbyListing Listing => BuildListing();

    /// <summary>Test hook: the clock used for rate limits.</summary>
    internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    // ---- host-side actions (owning thread) ----

    /// <summary>The host's own chat line (logged, so it reaches every guest's next snapshot).</summary>
    public IntentResult Chat(string text) =>
        _exec.Execute(Seats.Host.PlayerId, Seats.Host.Sides.ToHashSet(), new ChatIntent(text));

    /// <summary>Frees a disconnected guest's seat (the host's Players dialog); its sides become open seats. False when not allowed.</summary>
    public bool FreeSeat(string playerId)
    {
        if (Seats.ByPlayer(playerId) is not { IsHost: false, Connected: false } seat) return false;
        Seats.Remove(seat);
        AfterSeatChange();
        return true;
    }

    /// <summary>Sends every pending snapshot now (normally run <see cref="SnapshotDelay"/> after the last change).</summary>
    public void FlushSnapshots()
    {
        if (_disposed) return;
        _snapshotDue = false;
        foreach (var seat in Seats.Guests.ToList()) SendSnapshot(seat);
    }

    // ---- transport events (any thread) → owning thread ----

    private void Post(Action a)
    {
        if (_disposed) return;
        try { _post(() => Guard(a)); }
        catch (Exception) { /* the dispatcher is gone (window closing) */ }
    }

    private void Guard(Action a)
    {
        if (_disposed) return;
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { _s.Log.Add(DiagnosticSeverity.Error, "online", "Online hosting error: " + ex.Message); }
            catch (Exception) { }
        }
    }

    private void OnReceived(string from, byte[] payload) => Post(() => Handle(from, payload));
    private void OnMemberLeft(string memberId) => Post(() => MemberGone(memberId));
    private void OnMembersListed(IReadOnlyList<string> ids) => Post(() => Reattached(ids));
    private void OnDisconnected(string _) => Post(() => { RelayConnected = false; ConnectionChanged?.Invoke(); });
    private void OnReconnected() => Post(() => { RelayConnected = true; ConnectionChanged?.Invoke(); PublishListing(); });

    private void OnViewChanged() => ScheduleSnapshots();

    private void OnGameReplaced() => Post(SyncPlayers);

    private void ScheduleSnapshots()
    {
        if (_disposed || _snapshotDue) return;
        _snapshotDue = true;
        try { _snapshotTimer.Change(SnapshotDelay, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    // ---- payloads ----

    private void Handle(string from, byte[] payload)
    {
        GamePayload msg;
        try { msg = Framing.Decode<GamePayload>(payload); }
        catch (ProtocolException) { _dropped++; return; }
        switch (msg)
        {
            case JoinRequest j: Join(from, j); break;
            case ChooseSides c: Choose(from, c); break;
            case IntentMsg i: Intent(from, i); break;
            case Net.Chat c: ChatPayload(from, c); break;
            default: _dropped++; break;   // host-to-guest messages are never accepted from a guest
        }
    }

    private void Join(string from, JoinRequest j)
    {
        if (j.ProtocolVersion != Protocol.ProtocolVersion) { Send(from, new Kicked(Protocol.MismatchMessage(j.ProtocolVersion))); return; }
        if (!string.Equals(j.ModuleSha256, _s.Archive.Sha256, StringComparison.OrdinalIgnoreCase)) { Send(from, new Kicked(DifferentModuleText)); return; }
        if (PackBytes() is not { } pack) { Send(from, new Kicked(NoRulesText)); return; }

        if (Seats.ByToken(j.RejoinToken) is { } mine)
        {
            Bind(mine, from);
            Send(from, new Welcome(mine.PlayerId, j.RejoinToken!, pack, Seats.FreeSides(_s.Rules)));
            SendSnapshot(mine);
            AfterSeatChange(sync: false);
            return;
        }
        if (Seats.ByMember(from) != null) { _dropped++; return; }   // already seated on this connection

        var name = ChatText.Clean(j.Name);
        if (name == null) { Send(from, new Kicked(NoNameText)); return; }
        if (name.Length > Seats.MaxName) name = name[..Seats.MaxName].TrimEnd();
        if (Seats.ByName(name) is { } other) { Send(from, new Kicked(NameTakenText(other.Name))); return; }
        if (Seats.All.Count >= MaxPlayers || Seats.FreeSides(_s.Rules).Count == 0) { Send(from, new Kicked(FullText)); return; }

        var taken = _s.Game?.State.Players.Select(p => p.Id) ?? Enumerable.Empty<string>();
        var (seat, token) = Seats.AddGuest(name, taken);
        Bind(seat, from);
        Send(from, new Welcome(seat.PlayerId, token, pack, Seats.FreeSides(_s.Rules)));
        AfterSeatChange(sync: false);
    }

    private void Bind(Seat seat, string memberId)
    {
        foreach (var other in Seats.All)
            if (!ReferenceEquals(other, seat) && other.MemberId == memberId) other.MemberId = null;
        seat.MemberId = memberId;
    }

    private void Choose(string from, ChooseSides c)
    {
        if (Seats.ByMember(from) is not { IsHost: false } seat) { _dropped++; return; }
        if (!seat.Intents.TryTake(Now())) { Reply(from, Guid.Empty, IntentResult.Refused(SlowDownText)); return; }
        if (_s.Game == null || _s.Rules is not { } pack) { Reply(from, Guid.Empty, IntentResult.Refused(IntentExecutor.NoGameText)); return; }
        var sides = (c.Sides ?? Array.Empty<string>()).Distinct().ToList();
        if (Seats.CheckChoice(seat, sides, pack) is { } why) { Reply(from, Guid.Empty, IntentResult.Refused(why)); return; }
        seat.Sides = sides;
        AfterSeatChange();
        Reply(from, Guid.Empty, IntentResult.Done());
        SendSnapshot(seat);
    }

    private void Intent(string from, IntentMsg m)
    {
        if (Seats.ByMember(from) is not { IsHost: false } seat) { _dropped++; return; }
        if (!seat.Intents.TryTake(Now())) { Reply(from, m.Id, IntentResult.Refused(SlowDownText)); return; }
        GameIntent? intent;
        try { intent = JsonSerializer.Deserialize<GameIntent>(m.IntentJson, QuartermasterJson.Options); }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        { intent = null; }
        if (intent == null) { Reply(from, m.Id, IntentResult.Refused(IntentExecutor.NotUnderstoodText)); return; }
        if (intent is ChatIntent && !seat.Chats.TryTake(Now())) { Reply(from, m.Id, IntentResult.Refused(SlowDownText)); return; }
        var r = _exec.Execute(seat.PlayerId, seat.Sides.ToHashSet(), intent);
        Reply(from, m.Id, r);
        if (r.Ok) ScheduleSnapshots();
    }

    private void ChatPayload(string from, Net.Chat c)
    {
        if (Seats.ByMember(from) is not { IsHost: false } seat) { _dropped++; return; }
        if (!seat.Intents.TryTake(Now()) || !seat.Chats.TryTake(Now())) { Reply(from, Guid.Empty, IntentResult.Refused(SlowDownText)); return; }
        var r = _exec.Execute(seat.PlayerId, seat.Sides.ToHashSet(), new ChatIntent(c.Text));
        if (!r.Ok) Reply(from, Guid.Empty, r);
        else ScheduleSnapshots();
    }

    // ---- membership ----

    private void MemberGone(string memberId)
    {
        if (Seats.ByMember(memberId) is not { } seat) return;
        seat.MemberId = null;   // the seat stays; the host may free it
        AfterSeatChange(sync: false);
    }

    /// <summary>After the host re-attached: seats whose guest is gone are disconnected; the others get a fresh full snapshot.</summary>
    private void Reattached(IReadOnlyList<string> ids)
    {
        var present = new HashSet<string>(ids ?? Array.Empty<string>());
        foreach (var seat in Seats.Guests)
        {
            if (seat.MemberId == null) continue;
            if (!present.Contains(seat.MemberId)) seat.MemberId = null;
            else SendSnapshot(seat);
        }
        AfterSeatChange(sync: false);
    }

    private void AfterSeatChange(bool sync = true)
    {
        if (sync) SyncPlayers();
        PublishListing();
        SeatsChanged?.Invoke();
    }

    // ---- players, listing, snapshots ----

    private int MaxPlayers => Math.Clamp(_draft.MaxPlayers <= 0 ? Players.MaxPlayers : _draft.MaxPlayers, 1, Players.MaxPlayers);

    /// <summary>
    /// The host's player: in a game with players, the one named like the listing's host (else the first), keeping only its sides (every
    /// other side is an open seat, launcher spec 4a.8); in a solo game, every side not listed as free (by id or name).
    /// </summary>
    private PlayerDef HostPlayer()
    {
        var st = _s.Game?.State;
        var players = st?.Players ?? new List<PlayerDef>();
        if (players.Count > 0)
        {
            var p = players.FirstOrDefault(x => string.Equals(x.Name?.Trim(), _draft.HostName?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? players[0];
            return p with { Sides = (p.Sides ?? Array.Empty<string>()).ToList() };
        }
        var free = new HashSet<string>(_draft.FreeSides ?? Array.Empty<string>(), StringComparer.Ordinal);
        var sides = (_s.Rules?.Sides ?? new List<SideDef>())
            .Where(x => x?.Id != null && !free.Contains(x.Id) && !free.Contains(x.Name ?? "")).Select(x => x.Id).ToList();
        var name = ChatText.Clean(_draft.HostName) ?? "Host";
        return new PlayerDef("host", name.Length > Seats.MaxName ? name[..Seats.MaxName] : name, sides);
    }

    private static bool Same(IReadOnlyList<PlayerDef> a, IReadOnlyList<PlayerDef> b) =>
        a.Count == b.Count && a.Zip(b).All(x => x.First.Id == x.Second.Id && x.First.Name == x.Second.Name && x.First.Sides.SequenceEqual(x.Second.Sides));

    /// <summary>Makes the game's player list match the seats (SetPlayers, host's seat first) and shows the host its own sides.</summary>
    private void SyncPlayers()
    {
        if (_s.Game is not { } g) return;
        var players = Seats.ToPlayers();
        if (!Same(players, g.State.Players))
            g.Execute(CommandLog.Prepare(CommandLog.Stamp(new SetPlayers(players), Seats.Host.PlayerId), g.State, _s));
        var hostSides = Seats.Host.Sides.ToHashSet();
        if (_s.ViewSides == null || !_s.ViewSides.SetEquals(hostSides)) _s.SetViewSides(hostSides);
    }

    private LobbyListing BuildListing()
    {
        var pack = _s.Rules;
        string NameOf(string id) => pack?.Sides?.FirstOrDefault(x => x?.Id == id)?.Name is { Length: > 0 } n ? n : id;
        var sides = (pack?.Sides ?? new List<SideDef>()).Where(x => x?.Id != null).Select(x => NameOf(x.Id)).ToList();
        return _draft with
        {
            Sides = sides.Count > 0 ? sides : _draft.Sides,
            FreeSides = Seats.FreeSides(pack).Select(NameOf).ToList(),
            Players = Seats.All.Count,
            MaxPlayers = MaxPlayers,
            Locked = _locked,
            Version = Protocol.ProtocolVersion,
        };
    }

    private void PublishListing()
    {
        if (_disposed) return;
        Observe(() => _t.UpdateListing(BuildListing()));
    }

    private byte[]? PackBytes()
    {
        if (_s.Rules is not { } pack) return null;
        if (!ReferenceEquals(pack, _packBytesFor)) { _packBytes = RulesPackIo.ToBytes(pack); _packBytesFor = pack; }
        return _packBytes;
    }

    /// <summary>The guest's filtered view, if it is connected and holds a side (never without a rules pack: no pack means no filter).</summary>
    private void SendSnapshot(Seat seat)
    {
        if (seat.IsHost || seat.MemberId is not { } to || seat.Sides.Count == 0) return;
        if (_s.Game?.State is not { } full || _s.Rules is not { } pack) return;
        var view = PlayerView.For(full, pack, _s.Model, seat.Sides.ToHashSet());
        var json = JsonSerializer.SerializeToUtf8Bytes(view, QuartermasterJson.Options);
        Send(to, new Snapshot(++_version, Gzip.Compress(json)));
    }

    private void Reply(string to, Guid id, IntentResult r) =>
        Send(to, new NetIntentResult(id, r.Ok, r.Message, r.Verdict == null ? null : JsonSerializer.Serialize(r.Verdict, QuartermasterJson.Options)));

    private void Send(string to, GamePayload p)
    {
        byte[] bytes;
        try { bytes = Framing.Encode(p); }
        catch (ProtocolException ex)
        {
            _s.Log.Add(DiagnosticSeverity.Warning, "online", "A message to a guest could not be sent: " + ex.Message);
            return;
        }
        Observe(() => _t.Send(to, bytes));
    }

    /// <summary>Fire and forget; a failing or throwing transport never reaches the game.</summary>
    private static void Observe(Func<Task> send)
    {
        try { send().ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _t.Received -= OnReceived;
        _t.MemberLeft -= OnMemberLeft;
        _t.MembersListed -= OnMembersListed;
        _t.Disconnected -= OnDisconnected;
        _t.Reconnected -= OnReconnected;
        _s.ViewChanged -= OnViewChanged;
        _s.GameReplaced -= OnGameReplaced;
        _snapshotTimer.Dispose();
        _listingTimer.Dispose();
    }
}
