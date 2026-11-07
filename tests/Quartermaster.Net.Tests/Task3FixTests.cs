using Quartermaster.Net;

namespace Quartermaster.Net.Tests;

public class Task3FixTests
{
    [Fact]
    public void RejectNullElements_DoesNotRecurseIntoNonProtocolTypes()
    {
        // The old reflection walk recursed forever through DateTime.Date etc.
        Framing.RejectNullElements(DateTime.UtcNow);
        Framing.RejectNullElements(DateTimeOffset.UtcNow);
        Framing.RejectNullElements(1.5m);
        Framing.RejectNullElements(TimeSpan.FromSeconds(3));
        Framing.RejectNullElements(new List<object?> { DateTime.UtcNow, new[] { "a" } });
    }

    [Fact]
    public void RejectNullElements_StillWalksProtocolTypes()
    {
        var bad = new LobbyListing("A", "N", "H", "s", "T", new[] { "x" }, new string[] { null! }, 1, 2, false, 1);
        Assert.Throws<ProtocolException>(() => Framing.RejectNullElements(new UpdateListing(bad)));
    }

    [Fact]
    public void VerifierEquals_Null_IsFalse()
    {
        Assert.False(Secrets.VerifierEquals(null, "ab"));
        Assert.False(Secrets.VerifierEquals("ab", null));
        Assert.False(Secrets.VerifierEquals(null, null));
    }
}
