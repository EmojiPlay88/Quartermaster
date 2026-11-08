using Quartermaster.Net;
using Xunit;

namespace Quartermaster.Render.Tests;

public class VersionWarningTests
{
    [Fact] public void VersionWarning_Equal_Null() => Assert.Null(VersionCheck.VersionWarning("0.1.0", "0.1.0", "Host"));
    [Fact] public void VersionWarning_Different_Text() =>
        Assert.Equal("Host runs 0.2.0, you run 0.1.0 — update first if anything misbehaves.", VersionCheck.VersionWarning("0.1.0", "0.2.0", "Host"));
    [Fact] public void VersionWarning_TheirsNull_Null() { Assert.Null(VersionCheck.VersionWarning("0.1.0", null, "Host")); Assert.Null(VersionCheck.VersionWarning(null, "0.1.0", "Host")); }
    [Fact] public void TurnFileVersionWarning_Text() =>
        Assert.Equal("This turn file was made with 0.2.0, you run 0.1.0 — update first if anything misbehaves.", VersionCheck.TurnFileVersionWarning("0.1.0", "0.2.0"));
    [Fact] public void TurnFileVersionWarning_NullOrEqual() { Assert.Null(VersionCheck.TurnFileVersionWarning("1", "1")); Assert.Null(VersionCheck.TurnFileVersionWarning("1", null)); }

    [Fact]
    public void OldJson_WithoutAppVersion_Deserialises()
    {
        var jr = (JoinRequest)Framing.Decode<GamePayload>(System.Text.Encoding.UTF8.GetBytes("{\"$t\":\"JoinRequest\",\"Name\":\"Ann\",\"RejoinToken\":null,\"ModuleSha256\":\"s\"}"));
        Assert.Null(jr.AppVersion);
        var w = (Welcome)Framing.Decode<GamePayload>(System.Text.Encoding.UTF8.GetBytes("{\"$t\":\"Welcome\",\"PlayerId\":\"p\",\"RejoinToken\":\"t\",\"RulesPack\":\"AQ==\",\"FreeSides\":[]}"));
        Assert.Null(w.HostAppVersion);
    }

    [Fact]
    public void NewFields_RoundTrip()
    {
        var jr = (JoinRequest)Framing.Decode<GamePayload>(Framing.Encode<GamePayload>(new JoinRequest("Ann", null, "s", AppVersion: "9.9.9")));
        Assert.Equal("9.9.9", jr.AppVersion);
        var w = (Welcome)Framing.Decode<GamePayload>(Framing.Encode<GamePayload>(new Welcome("p", "t", new byte[] { 1 }, new[] { "Red" }, HostAppVersion: "9.9.9")));
        Assert.Equal("9.9.9", w.HostAppVersion);
    }
}
