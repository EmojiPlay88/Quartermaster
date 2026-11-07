using Quartermaster.Net;

namespace Quartermaster.Net.Tests;

public class Task3FixTests
{
    private sealed record Holder(DateTime When, DateTimeOffset At, decimal Amount, TimeSpan Span, IReadOnlyList<string> Names);

    [Fact]
    public void Decode_NonProtocolTypes_AreLeaves_NoRecursion()
    {
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new Holder(DateTime.UtcNow, DateTimeOffset.UtcNow, 1.5m, TimeSpan.FromSeconds(3), new[] { "a" }));
        var h = Framing.Decode<Holder>(json);
        Assert.Equal(1.5m, h.Amount);
    }

    [Fact]
    public void VerifierEquals_Null_IsFalse()
    {
        Assert.False(Secrets.VerifierEquals(null, "ab"));
        Assert.False(Secrets.VerifierEquals("ab", null));
        Assert.False(Secrets.VerifierEquals(null, null));
    }
}
