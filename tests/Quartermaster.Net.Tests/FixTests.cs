using System.Text;
using Quartermaster.Net;

namespace Quartermaster.Net.Tests;

public class FixTests
{
    private static readonly LobbyListing L = new("ABC234", "Game", "Host", "sha", "Title",
        new[] { "Red", "Blue" }, new[] { "Blue" }, 1, 2, false, 1);

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Open_GzipBomb_Throws_AndLargeUnderCapRoundTrips()
    {
        var key = Secrets.GameKey("pw", "ABC234");
        Assert.ThrowsAny<Exception>(() => Secrets.Open(key, Secrets.Seal(key, new byte[100 * 1024 * 1024])));
        var big = new byte[20 * 1024 * 1024];
        Assert.Equal(big, Secrets.Open(key, Secrets.Seal(key, big)));
    }

    [Fact]
    public void Framing_Validation()
    {
        Assert.Throws<ProtocolException>(() => Framing.Decode<RelayMessage>(B("{\"$t\":\"UpdateListing\",\"Listing\":{\"Code\":\"A\"}}")));
        Assert.Throws<ProtocolException>(() => Framing.Decode<RelayMessage>(B("{\"$t\":\"Hello\",\"Role\":\"host\"}")));
        Assert.Throws<ProtocolException>(() => Framing.Decode<RelayMessage>(B("{\"$t\":\"Deliver\",\"FromMemberId\":\"m\",\"Sealed\":\"!!notbase64\"}")));
        Assert.Throws<ProtocolException>(() => Framing.Decode<GamePayload>(B("{\"$t\":\"IntentMsg\",\"Id\":\"nope\",\"IntentJson\":\"{}\"}")));
        var json = Encoding.UTF8.GetString(Framing.Encode(new CreateRoom("ABC234", "v", L))).Replace("\"Red\"", "null");
        Assert.Throws<ProtocolException>(() => Framing.Decode<RelayMessage>(B(json)));
    }

    [Fact]
    public void VerifierEquals_CodeNormalisation_KeyGuard()
    {
        var v = Secrets.AuthVerifier("pw", "ABC234");
        Assert.True(Secrets.VerifierEquals(v, v.ToUpperInvariant()));
        Assert.False(Secrets.VerifierEquals(v, Secrets.AuthVerifier("x", "ABC234")));
        Assert.False(Secrets.VerifierEquals(v, "zz"));
        Assert.Equal(v, Secrets.AuthVerifier("pw", " abc-234 "));
        Assert.Equal(Secrets.GameKey("pw", "ABC234"), Secrets.GameKey("pw", "abc 234"));
        Assert.Throws<ArgumentException>(() => Secrets.Seal(new byte[16], new byte[1]));
    }
}
