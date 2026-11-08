using Quartermaster.Core;
using Quartermaster.Render;

namespace Quartermaster.Render.Tests;

/// <summary>Defaults and copy of the turn-file windows; invented Red/Blue/Green players only.</summary>
public class TurnUiModelTests
{
    private static readonly PlayerDef Red = new("p1", "Rita", new[] { "red" });
    private static readonly PlayerDef Blue = new("p2", "Bo", new[] { "blue" });
    private static readonly PlayerDef Green = new("p3", "Gil", new[] { "green" });
    private static readonly IReadOnlyList<PlayerDef> Three = new[] { Red, Blue, Green };

    [Theory]
    [InlineData("p1", "p2")]
    [InlineData("p2", "p3")]
    [InlineData("p3", "p1")]   // wraps round
    public void DefaultRecipient_IsNextPlayerInOrder(string local, string expected) =>
        Assert.Equal(expected, TurnUiModel.DefaultRecipient(Three, local)?.Id);

    [Fact]
    public void DefaultRecipient_UnknownLocal_IsFirstPlayer() =>
        Assert.Equal("p1", TurnUiModel.DefaultRecipient(Three, "nobody")?.Id);

    [Fact]
    public void DefaultRecipient_NobodyElse_IsNull()
    {
        Assert.Null(TurnUiModel.DefaultRecipient(new[] { Red }, "p1"));
        Assert.Null(TurnUiModel.DefaultRecipient(Array.Empty<PlayerDef>(), "p1"));
        Assert.Null(TurnUiModel.DefaultRecipient(null, null));
    }

    [Fact]
    public void Recipients_ExcludeLocalPlayer_InOrder() =>
        Assert.Equal(new[] { "p1", "p3" }, TurnUiModel.Recipients(Three, "p2").Select(p => p.Id));

    [Fact]
    public void DefaultFileName_ModuleShortGameIdAndTurn() =>
        Assert.Equal("Test Module 0123abcd turn 4.qturn",
            TurnUiModel.DefaultFileName("Test Module", "0123abcd456789ef0123abcd456789ef", 4));

    [Fact]
    public void DefaultFileName_ReplacesCharactersAFileNameCannotHold()
    {
        var name = TurnUiModel.DefaultFileName("Red: Blue/Green?", "abc", 1);
        Assert.Equal("Red_ Blue_Green_ abc turn 1.qturn", name);
        Assert.Equal("Game game turn 0.qturn", TurnUiModel.DefaultFileName(" ", null, -3));
    }

    /// <summary>Turn files move between OSes: every character Windows forbids is replaced on every OS, not only this OS's own set.</summary>
    [Fact]
    public void DefaultFileName_ReplacesWindowsForbiddenCharactersOnEveryOs() =>
        Assert.Equal("a_b_c_d_e_f_g_h_i_j abc turn 1.qturn", TurnUiModel.DefaultFileName("a\\b<c>d\"e|f*g:h?i\tj", "abc", 1));

    [Fact]
    public void DefaultLocalPlayer_KnownThenRecipientThenFirst()
    {
        Assert.Equal("p3", TurnUiModel.DefaultLocalPlayer(Three, "p3", "p2"));
        Assert.Equal("p2", TurnUiModel.DefaultLocalPlayer(Three, "gone", "p2"));
        Assert.Equal("p1", TurnUiModel.DefaultLocalPlayer(Three, null, null));
        Assert.Null(TurnUiModel.DefaultLocalPlayer(Array.Empty<PlayerDef>(), null, "p2"));
    }

    [Fact]
    public void IsTurnGame_NeedsTwoPlayersAndAKnownLocalPlayer()
    {
        var root = Path.Combine(Path.GetTempPath(), "qm-turnui-" + Guid.NewGuid().ToString("N"));
        var id = TurnIdentity.Load(Guid.NewGuid().ToString("N"), root, null);
        var two = new GameState { Players = new List<PlayerDef> { Red, Blue } };
        Assert.False(TurnUiModel.IsTurnGame(two, id));
        Assert.False(TurnUiModel.IsTurnGame(two, null));
        id.LocalPlayerId = "p2";
        Assert.True(TurnUiModel.IsTurnGame(two, id));
        id.LocalPlayerId = "p9";
        Assert.False(TurnUiModel.IsTurnGame(two, id));
        id.LocalPlayerId = "p1";
        Assert.False(TurnUiModel.IsTurnGame(new GameState { Players = new List<PlayerDef> { Red } }, id));
        Assert.False(TurnUiModel.IsTurnGame(null, id));
        Assert.False(Directory.Exists(root));   // nothing was written
    }

    [Fact]
    public void ViewSides_LocalPlayersSides_PublicWhenNotAPlayer_AllInSolo()
    {
        var two = new GameState { Players = new List<PlayerDef> { Red, Blue } };
        Assert.Equal(new[] { "blue" }, TurnUiModel.ViewSides(two, "p2")!.ToArray());
        Assert.Empty(TurnUiModel.ViewSides(two, "p9")!);
        Assert.Null(TurnUiModel.ViewSides(new GameState(), "p1"));
    }

    [Fact]
    public void Copy_IsExact()
    {
        Assert.Equal("New turn waiting from Bo", TurnUiModel.NewTurnWaitingText("Bo"));
        Assert.Equal("Turn files carry the whole game; hidden information is only hidden on screen.", TurnUiModel.HiddenInfoNote);
        Assert.Equal("This turn has already been played.", TurnUiModel.AlreadyPlayedText);
        Assert.Equal("Open anyway", TurnUiModel.OpenAnywayText);
        Assert.Equal("Step 3 / 7", TurnUiModel.StepText(3, 7));
    }
}
