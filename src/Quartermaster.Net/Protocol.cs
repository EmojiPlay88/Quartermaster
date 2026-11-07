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
[JsonDerivedType(typeof(Ping), "Ping")]
[JsonDerivedType(typeof(Pong), "Pong")]
public abstract record RelayMessage;

/// <summary>Role is "host", "guest" or "browser".</summary>
public sealed record Hello(int Version, string Role) : RelayMessage;
public sealed record ReserveCode : RelayMessage;
public sealed record CodeReserved(string Code) : RelayMessage;
public sealed record CreateRoom(string Code, string AuthVerifier, LobbyListing Draft) : RelayMessage;
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
public sealed record Ping : RelayMessage;
public sealed record Pong : RelayMessage;

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
public abstract record GamePayload;

public sealed record JoinRequest(string Name, string? RejoinToken, string ModuleSha256) : GamePayload;
public sealed record Welcome(string PlayerId, string RejoinToken, byte[] RulesPack, IReadOnlyList<string> FreeSides) : GamePayload;
public sealed record ChooseSides(IReadOnlyList<string> Sides) : GamePayload;
/// <summary>StateJsonGz: gzip of the UTF-8 JSON of the guest's filtered view.</summary>
public sealed record Snapshot(long Version, byte[] StateJsonGz) : GamePayload;
public sealed record IntentMsg(Guid Id, string IntentJson) : GamePayload;
public sealed record IntentResult(Guid Id, bool Ok, string? Message, string? VerdictJson) : GamePayload;
public sealed record Chat(string From, string Text) : GamePayload;
public sealed record Kicked(string Reason) : GamePayload;
public sealed record Paused(string Reason) : GamePayload;

public static class Framing
{
    public const int MaxFrame = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        RespectNullableAnnotations = true,
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

    /// <summary>Missing non-nullable members (absent from the JSON) make the frame malformed.</summary>
    private static void RequireFields(object value)
    {
        var ctx = new System.Reflection.NullabilityInfoContext();
        foreach (var p in value.GetType().GetProperties())
        {
            if (p.PropertyType.IsValueType || p.GetIndexParameters().Length > 0) continue;
            if (ctx.Create(p).WriteState == System.Reflection.NullabilityState.NotNull && p.GetValue(value) is null)
                throw new ProtocolException($"Malformed message: {p.Name} is missing.");
        }
    }

    public static T Decode<T>(ReadOnlySpan<byte> frame)
    {
        if (frame.Length > MaxFrame) throw new ProtocolException("Message is too large.");
        if (frame.IsEmpty) throw new ProtocolException("Empty message.");
        try
        {
            var value = JsonSerializer.Deserialize<T>(frame, Options)
                ?? throw new ProtocolException("Empty message.");
            RequireFields(value);
            return value;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        { throw new ProtocolException("Malformed message.", e); }
    }
}
