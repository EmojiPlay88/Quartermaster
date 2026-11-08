using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quartermaster.Net;

public static class Protocol
{
    public const int ProtocolVersion = 1;
    public const int DefaultPort = 47820;

    public static string MismatchMessage(int theirs) =>
        $"This game uses a different Quartermaster network version ({theirs}); update both copies.";
}

public sealed class ProtocolException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Public lobby listing; the only unencrypted game-related data the relay holds.</summary>
public sealed record LobbyListing(
    string Code, string Name, string HostName, string ModuleSha256, string ModuleTitle,
    IReadOnlyList<string> Sides, IReadOnlyList<string> FreeSides,
    int Players, int MaxPlayers, bool Locked, int Version);

// ---- Relay-level messages ----

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[JsonDerivedType(typeof(Hello), "Hello")]
[JsonDerivedType(typeof(ReserveCode), "ReserveCode")]
[JsonDerivedType(typeof(CodeReserved), "CodeReserved")]
[JsonDerivedType(typeof(CreateRoom), "CreateRoom")]
[JsonDerivedType(typeof(RoomCreated), "RoomCreated")]
[JsonDerivedType(typeof(UpdateListing), "UpdateListing")]
[JsonDerivedType(typeof(ListLobbies), "ListLobbies")]
[JsonDerivedType(typeof(Lobbies), "Lobbies")]
[JsonDerivedType(typeof(JoinRoom), "JoinRoom")]
[JsonDerivedType(typeof(Joined), "Joined")]
[JsonDerivedType(typeof(Refused), "Refused")]
[JsonDerivedType(typeof(Send), "Send")]
[JsonDerivedType(typeof(Deliver), "Deliver")]
[JsonDerivedType(typeof(MemberLeft), "MemberLeft")]
[JsonDerivedType(typeof(HostLeft), "HostLeft")]
[JsonDerivedType(typeof(HostBack), "HostBack")]
[JsonDerivedType(typeof(Members), "Members")]
[JsonDerivedType(typeof(Ping), "Ping")]
[JsonDerivedType(typeof(Pong), "Pong")]
[JsonDerivedType(typeof(MailboxPut), "MailboxPut")]
[JsonDerivedType(typeof(MailboxStored), "MailboxStored")]
[JsonDerivedType(typeof(MailboxList), "MailboxList")]
[JsonDerivedType(typeof(MailboxItems), "MailboxItems")]
[JsonDerivedType(typeof(MailboxGet), "MailboxGet")]
[JsonDerivedType(typeof(MailboxItem), "MailboxItem")]
[JsonDerivedType(typeof(MailboxDelete), "MailboxDelete")]
[JsonDerivedType(typeof(MailboxDeleted), "MailboxDeleted")]
public abstract record RelayMessage;

/// <summary>Role is "host", "guest", "browser" or "mailbox".</summary>
public sealed record Hello(int Version, string Role) : RelayMessage;
public sealed record ReserveCode : RelayMessage;
public sealed record CodeReserved(string Code) : RelayMessage;
/// <summary>
/// HostKey: a random 32-byte hex secret (<see cref="Secrets.NewHostKey"/>) the host generates and keeps for the room's life.
/// The relay stores its hash; re-attaching to an existing room as host needs the same verifier AND the same HostKey.
/// </summary>
public sealed record CreateRoom(string Code, string AuthVerifier, string HostKey, LobbyListing Draft) : RelayMessage;
public sealed record RoomCreated(string Code) : RelayMessage;
public sealed record UpdateListing(LobbyListing Listing) : RelayMessage;
public sealed record ListLobbies(string ModuleSha256) : RelayMessage;
public sealed record Lobbies(IReadOnlyList<LobbyListing> Listings) : RelayMessage;
public sealed record JoinRoom(string Code, string AuthVerifier) : RelayMessage;
public sealed record Joined(string MemberId) : RelayMessage;
public sealed record Refused(string Reason) : RelayMessage;
/// <summary>ToMemberId null = to the host.</summary>
public sealed record Send(string? ToMemberId, byte[] Sealed) : RelayMessage;
public sealed record Deliver(string FromMemberId, byte[] Sealed) : RelayMessage;
public sealed record MemberLeft(string MemberId) : RelayMessage;
public sealed record HostLeft : RelayMessage;
/// <summary>Relay → each guest when the host re-attaches to the room.</summary>
public sealed record HostBack : RelayMessage;
/// <summary>Relay → host right after a re-attach: the guests still in the room.</summary>
public sealed record Members(IReadOnlyList<string> MemberIds) : RelayMessage;
public sealed record Ping : RelayMessage;
public sealed record Pong : RelayMessage;

// ---- Turn mailbox (spec §7). Any greeted connection may use it; it never joins a room. ----
// Box: 64 lower-hex characters (Secrets.MailboxBoxId); the relay never sees game ids, player ids or passwords.
// ItemId: 32 lower-hex characters, made by the relay. Sealed: opaque encrypted bytes (Secrets.Seal).
// Every request is answered by its reply or by Refused.

public sealed record MailboxPut(string Box, byte[] Sealed) : RelayMessage;
public sealed record MailboxStored(string ItemId) : RelayMessage;
/// <summary>At most <see cref="MailboxLimits.MaxBoxesPerList"/> boxes.</summary>
public sealed record MailboxList(IReadOnlyList<string> Boxes) : RelayMessage;
public sealed record MailboxItemInfo(string Box, string ItemId, long Size, DateTime StoredUtc);
public sealed record MailboxItems(IReadOnlyList<MailboxItemInfo> Items) : RelayMessage;
public sealed record MailboxGet(string Box, string ItemId) : RelayMessage;
public sealed record MailboxItem(string Box, string ItemId, byte[] Sealed) : RelayMessage;
public sealed record MailboxDelete(string Box, string ItemId) : RelayMessage;
public sealed record MailboxDeleted(string ItemId) : RelayMessage;

public static class MailboxLimits
{
    public const int MaxBoxesPerList = 32;
    public const int MaxItemsPerBox = 50;
    /// <summary>Upload attempts per hour per address (refused attempts count too).</summary>
    public const int UploadsPerHour = 30;
    /// <summary>Bytes an address may store per 24 hours.</summary>
    public const long UploadBytesPerDay = 100L * 1024 * 1024;
    public const int ExpiryDays = 30;
    /// <summary>Room left in a frame for the JSON around the Sealed bytes of a <see cref="MailboxItem"/>.</summary>
    private const int Envelope = 1024;
    /// <summary>Largest Sealed blob the mailbox takes: its base64 form plus the envelope fits one relay frame both ways.</summary>
    public const int MaxSealedBytes = (Framing.MaxFrame - Envelope) / 4 * 3;

    public const string Full = "The mailbox is full.";
    public const string TooManyUploads = "Too many uploads; try again later.";
    public const string NoSuchItem = "No such item.";
    public const string BadBox = "Bad mailbox id.";
    public const string BadItem = "The mailbox item is empty or too large.";
    public const string StoreFailed = "The mailbox could not store the item.";
    public const string ReadFailed = "The mailbox item could not be read.";

    /// <summary>Exactly 64 lower-case hex characters.</summary>
    public static bool IsBoxId(string? s) => IsLowerHex(s, 64);
    /// <summary>Exactly 32 lower-case hex characters.</summary>
    public static bool IsItemId(string? s) => IsLowerHex(s, 32);

    private static bool IsLowerHex(string? s, int length)
    {
        if (s is null || s.Length != length) return false;
        foreach (var ch in s)
            if (!(ch is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }
}

// ---- Game-level payloads (travel inside Sealed). Game data is opaque JSON owned by Render. ----

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[JsonDerivedType(typeof(JoinRequest), "JoinRequest")]
[JsonDerivedType(typeof(Welcome), "Welcome")]
[JsonDerivedType(typeof(ChooseSides), "ChooseSides")]
[JsonDerivedType(typeof(Snapshot), "Snapshot")]
[JsonDerivedType(typeof(IntentMsg), "IntentMsg")]
[JsonDerivedType(typeof(IntentResult), "IntentResult")]
[JsonDerivedType(typeof(Chat), "Chat")]
[JsonDerivedType(typeof(Kicked), "Kicked")]
[JsonDerivedType(typeof(Paused), "Paused")]
[JsonDerivedType(typeof(RulesChanged), "RulesChanged")]
public abstract record GamePayload;

/// <summary>ProtocolVersion: the guest's network version (the host refuses another with <see cref="Protocol.MismatchMessage"/>).</summary>
public sealed record JoinRequest(string Name, string? RejoinToken, string ModuleSha256, int ProtocolVersion = Protocol.ProtocolVersion,
    string? AppVersion = null) : GamePayload;
/// <summary>Sides: what the seat already holds (a rejoin); empty or missing for a new seat, which then chooses.</summary>
public sealed record Welcome(string PlayerId, string RejoinToken, byte[] RulesPack, IReadOnlyList<string> FreeSides,
    IReadOnlyList<string>? Sides = null, string? HostAppVersion = null) : GamePayload;
public sealed record ChooseSides(IReadOnlyList<string> Sides) : GamePayload;
/// <summary>StateJsonGz: gzip of the UTF-8 JSON of the guest's filtered view.</summary>
public sealed record Snapshot(long Version, byte[] StateJsonGz) : GamePayload;
public sealed record IntentMsg(Guid Id, string IntentJson) : GamePayload;
public sealed record IntentResult(Guid Id, bool Ok, string? Message, string? VerdictJson) : GamePayload;
public sealed record Chat(string From, string Text) : GamePayload;
public sealed record Kicked(string Reason) : GamePayload;
public sealed record Paused(string Reason) : GamePayload;
/// <summary>Host → guest: the host applied another rules pack (zip bytes); sent before the next snapshot.</summary>
public sealed record RulesChanged(byte[] RulesPack) : GamePayload;

public static class Framing
{
    public const int MaxFrame = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    private static Type Root(Type t) =>
        typeof(RelayMessage).IsAssignableFrom(t) ? typeof(RelayMessage)
        : typeof(GamePayload).IsAssignableFrom(t) ? typeof(GamePayload) : t;

    public static byte[] Encode<T>(T msg)
    {
        byte[] bytes;
        try { bytes = JsonSerializer.SerializeToUtf8Bytes(msg, Root(typeof(T)), Options); }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        { throw new ProtocolException("Message could not be encoded.", e); }
        if (bytes.Length > MaxFrame) throw new ProtocolException("Message is too large.");
        return bytes;
    }

    internal static void RejectNullElements(object? v)
    {
        if (v is null or string or byte[]) return;
        if (v is System.Collections.IEnumerable items)
        {
            foreach (var item in items)
                if (item is null) throw new ProtocolException("Malformed message: null list entry.");
                else RejectNullElements(item);
            return;
        }
        // Only protocol types are walked; DateTime, decimal, TimeSpan etc. are leaves.
        if (v.GetType().Assembly != typeof(RelayMessage).Assembly) return;
        foreach (var p in v.GetType().GetProperties())
            if (p.GetIndexParameters().Length == 0) RejectNullElements(p.GetValue(v));
    }

    public static T Decode<T>(ReadOnlySpan<byte> frame)
    {
        if (frame.Length > MaxFrame) throw new ProtocolException("Message is too large.");
        if (frame.IsEmpty) throw new ProtocolException("Empty message.");
        try
        {
            var value = JsonSerializer.Deserialize<T>(frame, Options)
                ?? throw new ProtocolException("Empty message.");
            RejectNullElements(value);
            return value;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        { throw new ProtocolException("Malformed message.", e); }
    }
}
