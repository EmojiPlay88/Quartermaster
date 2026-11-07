using System.Security.Cryptography;
using Quartermaster.Net;

namespace Quartermaster.Net.Tests;

public class NetTests
{
    [Fact]
    public void GameCode_New_ValidAndUnambiguous()
    {
        for (int i = 0; i < 500; i++)
        {
            var c = GameCode.New();
            Assert.Equal(6, c.Length);
            Assert.True(GameCode.IsValid(c));
            Assert.DoesNotContain(c, ch => "01OIL".Contains(ch));
        }
        Assert.NotEqual(GameCode.New(), GameCode.New());
        Assert.False(GameCode.IsValid("ABC1EF"));
        Assert.False(GameCode.IsValid("ABCDE"));
        Assert.False(GameCode.IsValid("abcdef"));
    }

    [Fact]
    public void GameCode_Normalize()
    {
        Assert.Equal("ABC234", GameCode.Normalize(" abc-234 "));
        Assert.Equal("ABC234", GameCode.Normalize("a b c 2 3 4"));
    }

    [Fact]
    public void Secrets_AuthAndKey_Differ_AndAreDeterministic()
    {
        var a1 = Secrets.AuthVerifier("pw", "ABC234");
        Assert.Equal(a1, Secrets.AuthVerifier("pw", "ABC234"));
        Assert.NotEqual(a1, Secrets.AuthVerifier("pw2", "ABC234"));
        Assert.NotEqual(a1, Secrets.AuthVerifier("pw", "ABC235"));
        var k = Secrets.GameKey("pw", "ABC234");
        Assert.Equal(32, k.Length);
        Assert.Equal(k, Secrets.GameKey("pw", "ABC234"));
        Assert.NotEqual(Convert.ToHexString(k), a1.ToUpperInvariant());
        Assert.NotEqual(Secrets.GameKey("", "ABC234"), k);
    }

    [Fact]
    public void Seal_Open_RoundTrip()
    {
        var key = Secrets.GameKey("pw", "ABC234");
        var plain = System.Text.Encoding.UTF8.GetBytes(new string('x', 5000) + "secret");
        var sealedBytes = Secrets.Seal(key, plain);
        Assert.Equal(plain, Secrets.Open(key, sealedBytes));
        Assert.True(sealedBytes.Length < plain.Length);
        Assert.NotEqual(sealedBytes, Secrets.Seal(key, plain));
        Assert.Empty(Secrets.Open(key, Secrets.Seal(key, Array.Empty<byte>())));
    }

    [Fact]
    public void Seal_WithAssociatedData_OpensOnlyWithTheSame()
    {
        var key = Secrets.MailboxSealKey("pw", "0123abcd");
        var plain = new byte[] { 1, 2, 3 };
        var s = Secrets.Seal(key, plain, "box-a");
        Assert.Equal(plain, Secrets.Open(key, s, 100, "box-a"));
        Assert.ThrowsAny<CryptographicException>(() => Secrets.Open(key, s, 100, "box-b"));
        Assert.ThrowsAny<CryptographicException>(() => Secrets.Open(key, s));
        Assert.ThrowsAny<CryptographicException>(() => Secrets.Open(key, Secrets.Seal(key, plain), 100, "box-a"));
        Assert.Equal(plain, Secrets.Open(key, Secrets.Seal(key, plain, null), 100, null));   // no associated data = the plain overloads
    }

    [Fact]
    public void Open_Tampered_Throws()
    {
        var key = Secrets.GameKey("pw", "ABC234");
        var s = Secrets.Seal(key, new byte[] { 1, 2, 3, 4 });
        var t = (byte[])s.Clone();
        t[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => Secrets.Open(key, t));
        Assert.ThrowsAny<CryptographicException>(() => Secrets.Open(Secrets.GameKey("other", "ABC234"), s));
        Assert.ThrowsAny<CryptographicException>(() => Secrets.Open(key, new byte[5]));
    }

    private static readonly LobbyListing L = new("ABC234", "Game", "Host", "sha", "Title",
        new[] { "Red", "Blue" }, new[] { "Blue" }, 1, 2, false, 1);

    [Fact]
    public void Framing_RoundTrip_AllMessages()
    {
        RelayMessage[] relay =
        {
            new Hello(1, "host"), new ReserveCode(), new CodeReserved("ABC234"),
            new CreateRoom("ABC234", "v", "k", L), new RoomCreated("ABC234"), new UpdateListing(L),
            new ListLobbies("sha"), new Lobbies(new[] { L }), new JoinRoom("ABC234", "v"),
            new Joined("m1"), new Refused("no"), new Send(null, new byte[] { 1, 2 }),
            new Send("m2", new byte[] { 3 }), new Deliver("m1", new byte[] { 9, 8 }),
            new MemberLeft("m1"), new HostLeft(), new HostBack(), new Members(new[] { "g1", "g2" }), new Ping(), new Pong(),
        };
        foreach (var m in relay)
        {
            var back = Framing.Decode<RelayMessage>(Framing.Encode(m));
            Assert.Equal(m.GetType(), back.GetType());
            Assert.Equal(Framing.Encode(m), Framing.Encode(back));
        }
        var lob = (Lobbies)Framing.Decode<RelayMessage>(Framing.Encode(new Lobbies(new[] { L })));
        Assert.Equal("Blue", lob.Listings[0].FreeSides[0]);
        var snd = (Send)Framing.Decode<RelayMessage>(Framing.Encode(new Send(null, new byte[] { 1, 2 })));
        Assert.Null(snd.ToMemberId);
        Assert.Equal(new byte[] { 1, 2 }, snd.Sealed);
        var mem = (Members)Framing.Decode<RelayMessage>(Framing.Encode(new Members(new[] { "g1", "g2" })));
        Assert.Equal(new[] { "g1", "g2" }, mem.MemberIds);
        var cr = (CreateRoom)Framing.Decode<RelayMessage>(Framing.Encode(new CreateRoom("ABC234", "v", "k", L)));
        Assert.Equal("k", cr.HostKey);
        Assert.Throws<ProtocolException>(() => Framing.Decode<RelayMessage>(System.Text.Encoding.UTF8.GetBytes(
            "{\"$t\":\"CreateRoom\",\"Code\":\"ABC234\",\"AuthVerifier\":\"v\",\"Draft\":" +
            System.Text.Json.JsonSerializer.Serialize(L) + "}"))); // HostKey is required

        var hk = Secrets.NewHostKey();
        Assert.Equal(64, hk.Length);
        Assert.Equal(32, Convert.FromHexString(hk).Length);
        Assert.NotEqual(hk, Secrets.NewHostKey());

        var id = Guid.NewGuid();
        GamePayload[] game =
        {
            new JoinRequest("Ann", null, "sha"), new Welcome("p1", "tok", new byte[] { 5 }, new[] { "Red" }),
            new ChooseSides(new[] { "Red" }), new Snapshot(7, new byte[] { 1 }),
            new IntentMsg(id, "{\"a\":1}"), new IntentResult(id, true, null, "{}"),
            new Chat("Ann", "hi"), new Kicked("bye"), new Paused("wait"), new RulesChanged(new byte[] { 4, 2 }),
        };
        foreach (var m in game)
        {
            var back = Framing.Decode<GamePayload>(Framing.Encode(m));
            Assert.Equal(m.GetType(), back.GetType());
            Assert.Equal(Framing.Encode(m), Framing.Encode(back));
        }
        var im = (IntentMsg)Framing.Decode<GamePayload>(Framing.Encode(new IntentMsg(id, "{\"a\":1}")));
        Assert.Equal(id, im.Id);
        Assert.Equal(new byte[] { 4, 2 }, ((RulesChanged)Framing.Decode<GamePayload>(Framing.Encode(new RulesChanged(new byte[] { 4, 2 })))).RulesPack);
        var jr = (JoinRequest)Framing.Decode<GamePayload>(System.Text.Encoding.UTF8.GetBytes("{\"$t\":\"JoinRequest\",\"Name\":\"Ann\",\"RejoinToken\":null,\"ModuleSha256\":\"s\"}"));
        Assert.Equal(Protocol.ProtocolVersion, jr.ProtocolVersion);   // older guests omit the field
    }

    [Fact]
    public void Open_MaxPlain_Bounded()
    {
        var key = Secrets.GameKey("pw", "ABC234");
        var s = Secrets.Seal(key, new byte[1000]);
        Assert.Equal(1000, Secrets.Open(key, s, 1000).Length);
        Assert.ThrowsAny<CryptographicException>(() => Secrets.Open(key, s, 999));
    }

    [Fact]
    public void Framing_Oversized_Throws()
    {
        var big = new byte[Framing.MaxFrame + 1];
        Assert.Throws<ProtocolException>(() => Framing.Decode<RelayMessage>(big));
        Assert.Throws<ProtocolException>(() => Framing.Encode(new Send(null, big)));
    }

    [Fact]
    public void Framing_Garbage_Throws()
    {
        Assert.Throws<ProtocolException>(() => Framing.Decode<RelayMessage>(new byte[] { 0xFF, 0x00, 0x12 }));
        Assert.Throws<ProtocolException>(() => Framing.Decode<RelayMessage>("{}"u8.ToArray()));
        Assert.Throws<ProtocolException>(() => Framing.Decode<RelayMessage>("{\"$t\":\"Nope\"}"u8.ToArray()));
        Assert.Throws<ProtocolException>(() => Framing.Decode<RelayMessage>(Array.Empty<byte>()));
        Assert.Throws<ProtocolException>(() => Framing.Decode<RelayMessage>("{\"$t\":\"Hello\"}"u8.ToArray()));
    }
}
