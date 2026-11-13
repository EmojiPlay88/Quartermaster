using Quartermaster.Core;
using Quartermaster.Render;
using Xunit;

namespace Quartermaster.Render.Tests;

public class TurnDialogRulesTests
{
    private static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });

    [Fact]
    public void Mailbox_off_has_no_problem() => Assert.Null(TurnDialogRules.MailboxProblem(false, "not a relay"));

    [Fact]
    public void Blank_relay_is_no_relay_text()
    {
        Assert.Equal(MailboxService.NoRelayText, TurnDialogRules.MailboxProblem(true, null));
        Assert.Equal(MailboxService.NoRelayText, TurnDialogRules.MailboxProblem(true, "   "));
    }

    [Fact]
    public void Bad_relay_returns_parse_error()
    {
        var expected = OnlineLobbyModel.ParseRelay("http://x y", out var err) == null ? err ?? MailboxService.NoRelayText : null;
        Assert.NotNull(expected);
        Assert.Equal(expected, TurnDialogRules.MailboxProblem(true, "http://x y"));
    }

    [Fact]
    public void Send_without_recipient_first()
    {
        Assert.Equal(MailboxService.NoRecipientText, TurnDialogRules.SendProblem(null, true, ""));
        Assert.Null(TurnDialogRules.SendProblem(Alice, false, null));
        Assert.Equal(MailboxService.NoRelayText, TurnDialogRules.SendProblem(Alice, true, ""));
    }

    [Fact]
    public void Who_without_player_first()
    {
        Assert.Equal("Choose which player you are.", TurnDialogRules.WhoProblem(null, true, ""));
        Assert.Null(TurnDialogRules.WhoProblem("p1", false, null));
        Assert.Equal(MailboxService.NoRelayText, TurnDialogRules.WhoProblem("p1", true, ""));
    }

    [Fact]
    public void Player_label_uses_sides_and_falls_back_to_id()
    {
        Assert.Equal("Alice (red)", TurnDialogRules.PlayerLabel(Alice, null));
        Assert.Equal("p2 (blue)", TurnDialogRules.PlayerLabel(new PlayerDef("p2", " ", new[] { "blue" }), null));
        Assert.Equal("Cy", TurnDialogRules.PlayerLabel(new PlayerDef("p3", "Cy", Array.Empty<string>()), null));
    }

    [Fact]
    public void Texts_are_exact()
    {
        Assert.Equal("Send Alice's turn. Next you choose where to save the turn file.", TurnDialogRules.SendPrompt("Alice"));
        Assert.Equal("This turn is from Bob. Which player are you?", TurnDialogRules.FromPrompt("Bob"));
        Assert.Equal("Who are you?", TurnDialogRules.WhoTitle);
    }

    [Fact]
    public void Mailbox_values_mask_password()
    {
        var s = new MailboxValues(true, "wss://r.example", "hunter2").ToString();
        Assert.DoesNotContain("hunter2", s);
        Assert.Contains("***", s);
        Assert.Contains("(none)", new MailboxValues(false, null, "").ToString());
    }
}
