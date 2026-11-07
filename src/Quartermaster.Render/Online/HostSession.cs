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
/// A rules pack applied by the host goes to every connected guest as <see cref="RulesChanged"/> before the next snapshot.
/// </para>
/// <para>
/// Sends are bounded so a misbehaving guest cannot make the host exceed its relay budget: joins are taken at most once per
/// <see cref="JoinInterval"/> per member (the rest dropped), at most one <see cref="SlowDownText"/> per seat per second (the rest dropped
/// without reply), and scheduled snapshots reach each guest at most once per <see cref="MinSnapshotInterval"/> (coalesced).
/// </para>
/// </summary>
public sealed class HostSession : IDisposable
{
    public const string DifferentModuleText = "Different module";
    public const string NoNameText = "Enter a player name.";
    public const string FullText = "This game is full.";
    public const string NoRulesText = "The host has no rules pack loaded.";
    public const string SlowDownText = "Slow down.";
    public const string SidesFixedText = "Only the host can change sides (Game → Players…).";
    public const string ChooseSideFirstText = "Choose your side first.";
    public const string SignedInElsewhereText = "Signed in elsewhere.";
    public const string SeatFreedText = "The host freed your seat.";
    public static string NameTakenText(string name) => $"A player named {name} is already in this game.";

    public static readonly TimeSpan SnapshotDelay = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MinSnapshotInterval = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan JoinInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan SlowDownInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ListingInterval = TimeSpan.FromSeconds(30);
    private const int MaxJoinMemory = 256;

    private readonly ModuleSession _s;
    private readonly IntentExecutor _exec;
    private readonly ITransport _t;
    private readonly Action<Action> _post;
    private readonly LobbyListing _draft;
    private readonly bool _locked;
    private readonly Timer _snapshotTimer;
    private readonly Timer _listingTimer;
    private readonly Dictionary<string, DateTime> _lastJoin = new();
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
        _draft = listing with { Code = GameCode.Normalize(code ?? listing.Code ?? "") };
        Seats = new Seats(HostPlayer());
        SyncPlayers();

        // Started last, so a failure above leaves nothing subscribed or running.
        _snapshotTimer = new Timer(_ => Post(FlushDue), null, Timeout.Infinite, Timeout.Infinite);
        _listingTimer = new Timer(_ => Post(PublishListing), null, Timeout.Infinite, Timeout.Infinite);
        _t.Received += OnReceived;
        _t.MemberLeft += OnMemberLeft;
        _t.MembersListed += OnMembersListed;
        _t.Disconnected += OnDisconnected;
        _t.Reconnected += OnReconnected;
        _s.ViewChanged += OnViewChanged;
        _s.GameReplaced += OnGameReplaced;
        _s.RulesApplied += OnRulesApplied;
        PublishListing();
        _listingTimer.Change(ListingInterval, ListingInterval);
    }

    public Seats Seats { get; }

    /// <summary>Raised on the owning thread when a seat is taken, reclaimed, changes sides, drops or is freed.</summary>
    public event Action? SeatsChanged;

    /// <summary>Raised on the owning thread when the host's own relay connection drops or comes back.</summary>
    public event Action? ConnectionChanged;

    public bool RelayConnected { get; private set; } = true;

    /// <summary>The version of the last snapshot sent (monotonic for this session).</summary>
    public long SnapshotVersion => _version;

    /// <summary>Payloads dropped: undecodable, from members without a seat, out of place, or over a rate limit.</summary>
    public int Dropped => _dropped;

    /// <summary>The public listing for the current seats (what the next publish sends).</summary>
    public LobbyListing Listing => BuildListing();

    /// <summary>Test hook: the clock used for rate limits and snapshot spacing.</summary>
    internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    // ---- host-side actions (owning thread) ----

    /// <summary>The host's own chat line (logged, so it reaches every guest's next snapshot).</summary>
    public IntentResult Chat(string text) =>
        _exec.Execute(Seats.Host.PlayerId, Seats.Host.Sides.ToHashSet(), new ChatIntent(text));

    /// <summary>
    /// Frees a guest's seat from the host's Players dialog: a disconnected seat, or a connected one that holds no side yet (that guest is
    /// told and unbound). Its sides become open seats. False when not allowed.
    /// </summary>
    public bool FreeSeat(string playerId)
    {
        if (Seats.ByPlayer(playerId) is not { IsHost: false } seat) return false;
        if (seat.Connected && seat.Sides.Count > 0) return false;
        if (seat.MemberId is { } member) Send(member, new Kicked(SeatFreedText));
        seat.MemberId = null;
        Seats.Remove(seat);
        AfterSeatChange();
        return true;
    }

    public const string HostEndedText = "The host ended the online game.";

    /// <summary>The host leaves the online game: every connected guest is told (<see cref="HostEndedText"/>). Call before disposing.</summary>
    public void EndGame()
    {
        if (_disposed) return;
        foreach (var seat in Seats.Guests.ToList())
            if (seat.MemberId is { } member) Send(member, new Kicked(HostEndedText));
    }

    public const string NoSuchSeatText = "That player is not in this game.";
    public const string SideHeldText = "That side is held by another player.";

    /// <summary>
    /// The host's Players dialog (spec 8a.10): gives a seat (a guest's or the host's own) these sides. Each side must be free or already
    /// held by that seat; the result must pass <see cref="Seats.CheckChoice"/>. Returns null when done, else why not. A guest that now
    /// holds sides gets its view at once.
    /// </summary>
    public string? AssignSides(string playerId, IReadOnlyList<string> sides)
    {
        if (_disposed) return IntentExecutor.NoGameText;
        if (Seats.ByPlayer(playerId) is not { } seat) return NoSuchSeatText;
        if (_s.Game == null || _s.Rules is not { } pack) return IntentExecutor.NoGameText;
        var list = (sides ?? Array.Empty<string>()).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        var others = Seats.All.Where(x => !ReferenceEquals(x, seat)).SelectMany(x => x.Sides).ToHashSet();
        if (list.Any(others.Contains)) return SideHeldText;
        if (Seats.CheckChoice(seat, list, pack) is { } why) return why;
        if (seat.Sides.SequenceEqual(list)) return null;
        seat.Sides = list;
        AfterSeatChange();
        if (!seat.IsHost && seat.Connected) SendSnapshot(seat);
        return null;
    }

    /// <summary>Sends every guest its snapshot now, ignoring the spacing (normally run <see cref="SnapshotDelay"/> after the last change).</summary>
    public void FlushSnapshots()
    {
        if (_disposed) return;
        _snapshotDue = false;
        foreach (var seat in Seats.Guests.ToList()) SendSnapshot(seat);
    }

    /// <summary>The timer's flush: guests marked due get a snapshot unless they had one within <see cref="MinSnapshotInterval"/>; those wait.</summary>
    internal void FlushDue()
    {
        if (_disposed) return;
        _snapshotDue = false;
        var now = Now();
        TimeSpan? wait = null;
        foreach (var seat in Seats.Guests.ToList())
        {
            if (!seat.SnapshotDue) continue;
            if (seat.LastSnapshot is { } last && now - last < MinSnapshotInterval)
            {
                var left = MinSnapshotInterval - (now - last);
                wait = wait is { } w && w < left ? w : left;
                continue;
            }
            SendSnapshot(seat);
        }
        if (wait is { } delay) Arm(delay);
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

    /// <summary>Raised on the owning thread before the view changes: the new pack goes out ahead of the next snapshot.</summary>
    private void OnRulesApplied() => Guard(() =>
    {
        if (PackBytes() is not { } pack) return;
        foreach (var seat in Seats.Guests)
            if (seat.MemberId is { } to) Send(to, new RulesChanged(pack));
        ScheduleSnapshots();
    });

    private void ScheduleSnapshots()
    {
        if (_disposed) return;
        foreach (var seat in Seats.Guests) seat.SnapshotDue = true;
        if (_snapshotDue) return;
        Arm(SnapshotDelay);
    }

    private void Arm(TimeSpan delay)
    {
        _snapshotDue = true;
        try { _snapshotTimer.Change(delay, Timeout.InfiniteTimeSpan); }
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

    /// <summary>One join per member per <see cref="JoinInterval"/>; the memory of past joins is bounded.</summary>
    private bool JoinAllowed(string from)
    {
        var now = Now();
        if (_lastJoin.TryGetValue(from, out var last) && now - last < JoinInterval) return false;
        if (_lastJoin.Count >= MaxJoinMemory)
            foreach (var old in _lastJoin.Where(kv => now - kv.Value >= JoinInterval).Select(kv => kv.Key).ToList()) _lastJoin.Remove(old);
        if (_lastJoin.Count >= MaxJoinMemory) return false;
        _lastJoin[from] = now;
        return true;
    }

    private void Join(string from, JoinRequest j)
    {
        if (!JoinAllowed(from)) { _dropped++; return; }
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

        var name = ChatText.CleanName(j.Name);
        if (name == null) { Send(from, new Kicked(NoNameText)); return; }
        if (Seats.ByName(name) is { } other) { Send(from, new Kicked(NameTakenText(other.Name))); return; }
        if (Seats.All.Count >= MaxPlayers || Seats.FreeSides(_s.Rules).Count == 0) { Send(from, new Kicked(FullText)); return; }

        var taken = _s.Game?.State.Players.Select(p => p.Id) ?? Enumerable.Empty<string>();
        var (seat, token) = Seats.AddGuest(name, taken);
        Bind(seat, from);
        Send(from, new Welcome(seat.PlayerId, token, pack, Seats.FreeSides(_s.Rules)));
        AfterSeatChange(sync: false);
    }

    /// <summary>Binds the seat to this connection; a previous connection of the seat is told and loses it.</summary>
    private void Bind(Seat seat, string memberId)
    {
        foreach (var other in Seats.All)
            if (!ReferenceEquals(other, seat) && other.MemberId == memberId) other.MemberId = null;
        if (seat.MemberId is { } old && old != memberId) Send(old, new Kicked(SignedInElsewhereText));
        seat.MemberId = memberId;
        seat.LastSnapshot = null;
    }

    /// <summary>"Slow down." at most once per seat per <see cref="SlowDownInterval"/>; further over-limit requests get no reply.</summary>
    private void SlowDown(Seat seat, string to, Guid id)
    {
        var now = Now();
        if (seat.LastSlowDown is { } last && now - last < SlowDownInterval) { _dropped++; return; }
        seat.LastSlowDown = now;
        Reply(to, id, IntentResult.Refused(SlowDownText));
    }

    private void Choose(string from, ChooseSides c)
    {
        if (Seats.ByMember(from) is not { IsHost: false } seat) { _dropped++; return; }
        if (!seat.Intents.TryTake(Now())) { SlowDown(seat, from, Guid.Empty); return; }
        // Spec 8a.10: once seated with sides, only the host's Players dialog changes them.
        if (seat.Sides.Count > 0) { Reply(from, Guid.Empty, IntentResult.Refused(SidesFixedText)); return; }
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
        if (!seat.Intents.TryTake(Now())) { SlowDown(seat, from, m.Id); return; }
        GameIntent? intent;
        try { intent = JsonSerializer.Deserialize<GameIntent>(m.IntentJson, QuartermasterJson.Options); }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        { intent = null; }
        if (intent == null) { Reply(from, m.Id, IntentResult.Refused(IntentExecutor.NotUnderstoodText)); return; }
        if (intent is ChatIntent && !seat.Chats.TryTake(Now())) { SlowDown(seat, from, m.Id); return; }
        if (intent is not ChatIntent && seat.Sides.Count == 0) { Reply(from, m.Id, IntentResult.Refused(ChooseSideFirstText)); return; }
        var r = _exec.Execute(seat.PlayerId, seat.Sides.ToHashSet(), intent, seat.Name);
        Reply(from, m.Id, r);
        if (r.Ok) ScheduleSnapshots();
    }

    private void ChatPayload(string from, Net.Chat c)
    {
        if (Seats.ByMember(from) is not { IsHost: false } seat) { _dropped++; return; }
        if (!seat.Intents.TryTake(Now()) || !seat.Chats.TryTake(Now())) { SlowDown(seat, from, Guid.Empty); return; }
        var r = _exec.Execute(seat.PlayerId, seat.Sides.ToHashSet(), new ChatIntent(c.Text), seat.Name);
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
        var hostName = ChatText.CleanName(_draft.HostName);
        if (players.Count > 0)
        {
            var p = players.FirstOrDefault(x => hostName != null && string.Equals(ChatText.CleanName(x.Name), hostName, StringComparison.OrdinalIgnoreCase))
                ?? players[0];
            return p with { Sides = (p.Sides ?? Array.Empty<string>()).ToList() };
        }
        var free = new HashSet<string>(_draft.FreeSides ?? Array.Empty<string>(), StringComparer.Ordinal);
        var sides = (_s.Rules?.Sides ?? new List<SideDef>())
            .Where(x => x?.Id != null && !free.Contains(x.Id) && !free.Contains(x.Name ?? "")).Select(x => x.Id).ToList();
        return new PlayerDef("host", hostName ?? "Host", sides);
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
        seat.SnapshotDue = false;
        if (seat.IsHost || seat.MemberId is not { } to || seat.Sides.Count == 0) return;
        if (_s.Game?.State is not { } full || _s.Rules is not { } pack) return;
        var view = PlayerView.For(full, pack, _s.Model, seat.Sides.ToHashSet());
        var json = JsonSerializer.SerializeToUtf8Bytes(view, QuartermasterJson.Options);
        seat.LastSnapshot = Now();
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
        _s.RulesApplied -= OnRulesApplied;
        _snapshotTimer.Dispose();
        _listingTimer.Dispose();
    }
}
