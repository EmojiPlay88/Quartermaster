using Quartermaster.Core;
using Quartermaster.Render;
using Xunit;

namespace Quartermaster.Render.Tests;

public class TurnOpenDecisionTests : IDisposable
{
    private static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });
    private static readonly PlayerDef Bob = new("p2", "Bob", new[] { "blue" });
    private static readonly PlayerDef[] Two = { Alice, Bob };

    private sealed class FakeProtector : IPasswordProtector
    {
        public byte[] Protect(byte[] plain) => plain;
        public byte[] Unprotect(byte[] p) => p;
    }

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-tod-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private TurnIdentity Id(string? player)
    {
        var id = TurnIdentity.Load(Guid.NewGuid().ToString("N"), _tmp, new FakeProtector());
        id.LocalPlayerId = player;
        return id;
    }

    [Fact]
    public void Ok_known_player_replays()
    {
        var d = TurnOpenDecision.Decide(TurnCheck.Ok, Two, Id("p1"), false);
        Assert.Equal(TurnOpenStep.Replay, d.Step);
        Assert.Null(d.Message);
    }

    [Fact]
    public void Ok_unknown_player_asks_who() =>
        Assert.Equal(TurnOpenStep.AskWhoAreYou, TurnOpenDecision.Decide(TurnCheck.Ok, Two, Id(null), false).Step);

    [Fact]
    public void Different_module_refuses()
    {
        var d = TurnOpenDecision.Decide(TurnCheck.DifferentModule, Two, Id("p1"), true);
        Assert.Equal(TurnOpenStep.Refuse, d.Step);
        Assert.Equal(ModuleSession.DifferentModuleTurnText, d.Message);
    }

    [Fact]
    public void Older_position_asks_open_anyway()
    {
        var d = TurnOpenDecision.Decide(TurnCheck.OlderPosition, Two, Id("p1"), false);
        Assert.Equal(TurnOpenStep.AskOpenAnyway, d.Step);
        Assert.Equal(TurnOpener.OlderPositionText, d.Message);
        Assert.Equal(TurnOpenStep.Replay, TurnOpenDecision.Decide(TurnCheck.OlderPosition, Two, Id("p1"), true).Step);
    }

    [Fact]
    public void Already_played_open_anyway_then_replay()
    {
        var first = TurnOpenDecision.Decide(TurnCheck.AlreadyPlayed, Two, Id("p1"), false);
        Assert.Equal(TurnOpenStep.AskOpenAnyway, first.Step);
        Assert.Equal(TurnUiModel.AlreadyPlayedText, first.Message);
        Assert.Equal(TurnOpenStep.Replay, TurnOpenDecision.Decide(TurnCheck.AlreadyPlayed, Two, Id("p1"), true).Step);
    }

    [Fact]
    public void New_game_with_two_players_asks_who() =>
        Assert.Equal(TurnOpenStep.AskWhoAreYou, TurnOpenDecision.Decide(TurnCheck.NewGame, Two, Id("p1"), false).Step);

    [Fact]
    public void New_game_with_one_player_replays() =>
        Assert.Equal(TurnOpenStep.Replay, TurnOpenDecision.Decide(TurnCheck.NewGame, new[] { Alice }, Id(null), false).Step);

    [Fact]
    public void Null_identity_counts_as_unknown() =>
        Assert.Equal(TurnOpenStep.AskWhoAreYou, TurnOpenDecision.Decide(TurnCheck.Ok, Two, null, false).Step);
}
