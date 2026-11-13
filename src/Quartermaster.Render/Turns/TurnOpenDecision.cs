using Quartermaster.Core;

namespace Quartermaster.Render;

/// <summary>What the window does next when opening a received turn.</summary>
public enum TurnOpenStep
{
    /// <summary>Say <see cref="TurnOpenDecision.Message"/> and stop.</summary>
    Refuse,
    /// <summary>Ask "Open anyway / Cancel" with <see cref="TurnOpenDecision.Message"/>; call Decide again with the answer.</summary>
    AskOpenAnyway,
    /// <summary>Ask which player this computer is, then replay.</summary>
    AskWhoAreYou,
    /// <summary>Go ahead (after the usual discard question for anything but <see cref="TurnCheck.Ok"/>).</summary>
    Replay,
}

public sealed record TurnOpenDecision(TurnOpenStep Step, string? Message)
{
    /// <summary>
    /// Maps a <see cref="TurnCheck"/> to the next step. Already played / older position ask first (<paramref name="openAnywayChosen"/>
    /// is the answer once asked). The who-are-you prompt shows for a game with two or more players when the turn starts a new game or
    /// the local player is not known.
    /// </summary>
    public static TurnOpenDecision Decide(TurnCheck check, IReadOnlyList<PlayerDef> players, TurnIdentity? identity, bool openAnywayChosen)
    {
        switch (check)
        {
            case TurnCheck.DifferentModule:
                return new(TurnOpenStep.Refuse, ModuleSession.DifferentModuleTurnText);
            case TurnCheck.AlreadyPlayed when !openAnywayChosen:
                return new(TurnOpenStep.AskOpenAnyway, TurnUiModel.AlreadyPlayedText);
            case TurnCheck.OlderPosition when !openAnywayChosen:
                return new(TurnOpenStep.AskOpenAnyway, TurnOpener.OlderPositionText);
        }
        var list = players ?? Array.Empty<PlayerDef>();
        if (list.Count >= 2 && (check == TurnCheck.NewGame || !TurnUiModel.KnowsLocalPlayer(list, identity?.LocalPlayerId)))
            return new(TurnOpenStep.AskWhoAreYou, null);
        return new(TurnOpenStep.Replay, null);
    }
}
