using System.Security.Cryptography;
using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Net;
using Quartermaster.Rules;
using Quartermaster.Vassal;
using NetIntentResult = Quartermaster.Net.IntentResult;

namespace Quartermaster.Render;

/// <summary>Where a guest is in an online game.</summary>
public enum GuestState
{
    /// <summary>The join request is out (first join, or a rejoin after the host or the relay came back).</summary>
    Joining,
    /// <summary>Seated without a side: the guest picks one (only chat until then).</summary>
    ChoosingSide,
    /// <summary>Holding a side; snapshots arrive after every change.</summary>
    Playing,
    /// <summary>The host or the relay is gone (or the host paused): the last snapshot stays on screen; nothing can be sent.</summary>
    Paused,
    /// <summary>The host refused or removed this guest (<see cref="GuestSession.StatusText"/> says why). There is no automatic retry.</summary>
    Refused,
}

/// <summary>
/// A guest's side of a live online game (spec 5, 8a; launcher spec 4). Puts the <see cref="ModuleSession"/> into guest mode, joins
/// with the stored rejoin token for this game code (if any), loads the host's rules pack in memory, shows each newer filtered snapshot
/// and sends intents for <see cref="RemoteActions"/>. Transport events are marshalled to the owning (UI) thread through <c>post</c>
/// and never block. Bad, stale or oversized payloads are ignored and counted; nothing here throws to the transport.
/// <para>
/// View toggles (trails, stack expansion) are the guest's own: the host refuses them, so they are kept in a local overlay keyed by
/// piece / stack id and reapplied to every snapshot. Snapshots are the truth: an added piece may come back under another id.
/// </para>
/// </summary>
public sealed class GuestSession : IDisposable
{
    public const string HostGoneText = "Host disconnected — the game is paused.";
    public const string RelayLostText = "Connection to the relay lost — reconnecting…";
    public const string RejoiningText = "Rejoining the game…";
    public const string NoAnswerText = "The host didn't answer";
    public const string BadPackText = "The host's rules pack could not be read.";
    public const string NotPlayingText = "The game is paused.";
    public const string ChooseSideText = HostSession.ChooseSideFirstText;

    public static readonly TimeSpan DefaultIntentTimeout = TimeSpan.FromSeconds(10);
    private const int MaxPending = 256;

    private readonly ModuleSession _s;
    private readonly ITransport _t;
    private readonly Action<Action> _post;
    private readonly RejoinTokens _tokens;
    private readonly string _code;
    private readonly string _name;
    private readonly Dictionary<Guid, Pending> _pending = new();
    private readonly Dictionary<string, bool> _trails = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _expanded = new(StringComparer.Ordinal);
    private string? _token;
    private IReadOnlySet<string> _sides = new HashSet<string>();
    private long _lastVersion;
    private bool _disposed;

    private sealed record Pending(TaskCompletionSource<NetIntentResult?> Done, bool Notify);

    /// <param name="code">The game code (normalised); the rejoin token is stored per code.</param>
    /// <param name="playerName">The name the guest joins with.</param>
    /// <param name="post">Runs an action on the game's owning thread (the App passes BeginInvoke; tests a queued dispatcher).</param>
    /// <param name="tokens">Where rejoin tokens live; defaults to the layout root.</param>
    public GuestSession(ModuleSession s, ITransport t, string code, string playerName, Action<Action> post, RejoinTokens? tokens = null)
    {
        _s = s ?? throw new ArgumentNullException(nameof(s));
        _t = t ?? throw new ArgumentNullException(nameof(t));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _code = GameCode.Normalize(code ?? "");
        _name = playerName ?? "";
        _tokens = tokens ?? new RejoinTokens(RejoinTokens.DefaultRoot);
        _token = _tokens.Get(_code);
        _s.EnterGuestMode();
        _t.Received += OnReceived;
        _t.HostLeft += OnHostLeft;
        _t.HostBack += OnHostBack;
        _t.Disconnected += OnDisconnected;
        _t.Reconnected += OnReconnected;
        SendJoin();
    }

    public GuestState State { get; private set; } = GuestState.Joining;

    /// <summary>Why the game is paused or the guest was refused; null while joining or playing normally.</summary>
    public string? StatusText { get; private set; }

    /// <summary>This guest's player id (from the host's welcome); null before it.</summary>
    public string? PlayerId { get; private set; }

    /// <summary>The sides still free when the host welcomed this guest (side ids).</summary>
    public IReadOnlyList<string> FreeSides { get; private set; } = Array.Empty<string>();

    /// <summary>The sides this guest holds, from the latest snapshot (empty until one arrives; reset by every welcome).</summary>
    public IReadOnlySet<string> Sides => _sides;

    /// <summary>The version of the last snapshot shown (reset on every welcome: a re-attached host counts again).</summary>
    public long SnapshotVersion => _lastVersion;

    /// <summary>Snapshots or rules packs that could not be read (corrupt, too large, malformed).</summary>
    public int Malformed { get; private set; }

    /// <summary>Payloads ignored: undecodable, out of place, stale snapshots, late intent results.</summary>
    public int Dropped { get; private set; }

    /// <summary>Raised on the owning thread when <see cref="State"/> or <see cref="StatusText"/> changes.</summary>
    public event Action? StateChanged;

    /// <summary>A message for the player (a refused action, a refused side choice, a host that didn't answer). Owning thread.</summary>
    public event Action<string>? Notice;

    /// <summary>Test hook: how long an intent waits for the host's answer.</summary>
    internal TimeSpan IntentTimeout { get; set; } = DefaultIntentTimeout;

    // ---- guest actions (owning thread) ----

    /// <summary>Asks the host for these sides (once; later changes only through the host). The answer arrives as a notice or a snapshot.</summary>
    public bool ChooseSides(IReadOnlyList<string> sides)
    {
        if (_disposed || State != GuestState.ChoosingSide || sides == null || sides.Count == 0) return false;
        Send(new ChooseSides(sides.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList()));
        return true;
    }

    /// <summary>
    /// Sends an intent; the task completes on the owning thread with the host's result, or null when the host didn't answer within
    /// <see cref="IntentTimeout"/> (a later answer is ignored). With <paramref name="notify"/>, a refusal is also raised as a <see cref="Notice"/>.
    /// </summary>
    public Task<NetIntentResult?> SendIntent(GameIntent intent, bool notify = true)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (_disposed) return Task.FromResult<NetIntentResult?>(null);
        if (WhyNot(intent) is { } why)
        {
            if (notify) Notice?.Invoke(why);
            return Task.FromResult<NetIntentResult?>(new NetIntentResult(Guid.Empty, false, why, null));
        }
        if (_pending.Count >= MaxPending) return Task.FromResult<NetIntentResult?>(new NetIntentResult(Guid.Empty, false, HostSession.SlowDownText, null));
        var id = Guid.NewGuid();
        var done = new TaskCompletionSource<NetIntentResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new Pending(done, notify);
        Send(new IntentMsg(id, JsonSerializer.Serialize(intent, QuartermasterJson.Options)));
        var timeout = IntentTimeout;
        _ = Task.Delay(timeout).ContinueWith(_ => Post(() => Expire(id)), TaskScheduler.Default);
        return done.Task;
    }

    /// <summary>Why an intent cannot be sent now (null = it can).</summary>
    private string? WhyNot(GameIntent intent) => State switch
    {
        GuestState.Playing => null,
        GuestState.ChoosingSide => intent is ChatIntent ? null : ChooseSideText,
        _ => StatusText ?? NotPlayingText,
    };

    private void Expire(Guid id)
    {
        if (!_pending.Remove(id, out var p)) return;
        if (p.Notify) Notice?.Invoke(NoAnswerText);
        p.Done.TrySetResult(null);
    }

    /// <summary>Local view only (the host never sees it): flips a piece's trail; kept across snapshots.</summary>
    public void ToggleTrail(string pieceId)
    {
        if (_disposed || pieceId == null || _s.Game is not { } g || !g.State.Pieces.TryGetValue(pieceId, out var p)) return;
        p.TrailVisible = !p.TrailVisible;
        _trails[pieceId] = p.TrailVisible;
        g.ReplaceWithSnapshot(g.State);
    }

    /// <summary>Local view only: expands or collapses a stack; kept across snapshots while the stack exists.</summary>
    public void SetStackExpanded(string stackId, bool expanded)
    {
        if (_disposed || stackId == null || _s.Game is not { } g || !g.State.Stacks.TryGetValue(stackId, out var st)) return;
        st.Expanded = expanded;
        _expanded[stackId] = expanded;
        g.ReplaceWithSnapshot(g.State);
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
            try { _s.Log.Add(DiagnosticSeverity.Error, "online", "Online game error: " + ex.Message); }
            catch (Exception) { }
        }
    }

    private void OnReceived(string from, byte[] payload) => Post(() => Handle(payload));
    private void OnHostLeft() => Post(() => Pause(HostGoneText));
    private void OnDisconnected(string _) => Post(() => Pause(RelayLostText));
    private void OnHostBack() => Post(Rejoin);
    private void OnReconnected() => Post(Rejoin);

    private void SetState(GuestState state, string? status)
    {
        if (State == state && StatusText == status) return;
        State = state;
        StatusText = status;
        StateChanged?.Invoke();
    }

    private void Pause(string why)
    {
        if (State == GuestState.Refused) return;
        SetState(GuestState.Paused, why);
        FailPending();
    }

    private void Rejoin()
    {
        if (State == GuestState.Refused) return;   // refused: no retry loop
        SetState(GuestState.Joining, RejoiningText);
        SendJoin();
    }

    private void SendJoin() => Send(new JoinRequest(_name, _token, _s.Archive.Sha256));

    // ---- payloads ----

    private void Handle(byte[] payload)
    {
        GamePayload msg;
        try { msg = Framing.Decode<GamePayload>(payload); }
        catch (ProtocolException) { Dropped++; return; }
        if (State == GuestState.Refused) { Dropped++; return; }
        switch (msg)
        {
            case Welcome w: Welcomed(w); break;
            case Snapshot snap: Show(snap); break;
            case NetIntentResult r: Result(r); break;
            case Kicked k: Kick(k.Reason); break;
            case Paused p: Pause(string.IsNullOrWhiteSpace(p.Reason) ? NotPlayingText : p.Reason); break;
            case RulesChanged rc: NewRules(rc.RulesPack); break;
            default: Dropped++; break;   // guest-to-host messages are never accepted from the host side
        }
    }

    private static RulesPack? ReadPack(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > Framing.MaxFrame) return null;
        try { return RulesPackIo.FromBytes(bytes); }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    private void Welcomed(Welcome w)
    {
        if (ReadPack(w.RulesPack) is not { } pack)
        {
            Malformed++;
            Kick(BadPackText);
            return;
        }
        PlayerId = w.PlayerId;
        if (!string.IsNullOrEmpty(w.RejoinToken) && w.RejoinToken != _token)
        {
            _token = w.RejoinToken;
            _tokens.Set(_code, w.RejoinToken);
        }
        FreeSides = (w.FreeSides ?? Array.Empty<string>()).ToList();
        _lastVersion = 0;   // a re-attached host numbers its snapshots afresh; everything after a welcome is new
        _sides = new HashSet<string>();   // a seat that holds sides gets its snapshot right after the welcome
        _s.LoadGuestRules(pack);
        SetState(GuestState.ChoosingSide, null);
    }

    private void NewRules(byte[]? bytes)
    {
        if (ReadPack(bytes) is not { } pack) { Malformed++; return; }
        _s.LoadGuestRules(pack);
    }

    private void Show(Snapshot snap)
    {
        if (PlayerId == null || snap.Version <= _lastVersion) { Dropped++; return; }
        GameState? state;
        try
        {
            var json = Gzip.DecompressBounded(snap.StateJsonGz ?? Array.Empty<byte>(), Framing.MaxFrame);
            state = JsonSerializer.Deserialize<GameState>(json, QuartermasterJson.Options);
            if (state == null) { Malformed++; return; }
            NativeSave.ValidateState(state);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or NotSupportedException or InvalidOperationException
            or InvalidDataException or ArgumentException or FormatException or NullReferenceException)
        {
            Malformed++;
            return;
        }
        _lastVersion = snap.Version;
        ApplyOverlay(state);
        var held = (state.Players ?? new List<PlayerDef>()).FirstOrDefault(p => p.Id == PlayerId)?.Sides;
        _sides = held is { Count: > 0 } ? held.ToHashSet() : new HashSet<string>();
        _s.ApplySnapshot(state, _sides.Count > 0 ? _sides : null);
        SetState(_sides.Count > 0 ? GuestState.Playing : GuestState.ChoosingSide, null);
    }

    /// <summary>Reapplies the guest's own view toggles; entries for pieces or stacks that no longer exist are dropped.</summary>
    private void ApplyOverlay(GameState state)
    {
        foreach (var (id, on) in _trails.ToList())
            if (state.Pieces.TryGetValue(id, out var p)) p.TrailVisible = on;
            else _trails.Remove(id);
        foreach (var (id, on) in _expanded.ToList())
            if (state.Stacks.TryGetValue(id, out var st)) st.Expanded = on;
            else _expanded.Remove(id);
    }

    private void Result(NetIntentResult r)
    {
        if (r.Id == Guid.Empty)
        {
            // A side choice or a chat payload the host refused (accepted choices are followed by a snapshot).
            if (!r.Ok && !string.IsNullOrWhiteSpace(r.Message)) Notice?.Invoke(r.Message!);
            return;
        }
        if (!_pending.Remove(r.Id, out var p)) { Dropped++; return; }   // late (timed out) or unknown
        if (!r.Ok && p.Notify && !string.IsNullOrWhiteSpace(r.Message)) Notice?.Invoke(r.Message!);
        p.Done.TrySetResult(r);
    }

    private void Kick(string? reason)
    {
        var why = string.IsNullOrWhiteSpace(reason) ? "The host removed you from the game." : reason!;
        if (why == HostSession.SeatFreedText) { _token = null; _tokens.Forget(_code); }
        SetState(GuestState.Refused, why);
        FailPending();
    }

    private void FailPending()
    {
        if (_pending.Count == 0) return;
        var all = _pending.Values.ToList();
        _pending.Clear();
        foreach (var p in all) p.Done.TrySetResult(null);
    }

    private void Send(GamePayload p)
    {
        byte[] bytes;
        try { bytes = Framing.Encode(p); }
        catch (ProtocolException ex)
        {
            Notice?.Invoke("That could not be sent: " + ex.Message);
            return;
        }
        try { _t.Send(null, bytes).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        FailPending();
        _disposed = true;
        _t.Received -= OnReceived;
        _t.HostLeft -= OnHostLeft;
        _t.HostBack -= OnHostBack;
        _t.Disconnected -= OnDisconnected;
        _t.Reconnected -= OnReconnected;
    }
}
