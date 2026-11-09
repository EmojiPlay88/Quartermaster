using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>
/// The UI-free half of the dice and turn tools (Tools → Dice / Turn, the dice and Next turn toolbar buttons, F5 / F6): rolling with the
/// session roller (or physical dice), recording one <see cref="RollDice"/> per roll through the action port, multi-rolls, module dice,
/// stepping and setting the turn, and the refusal while the game may not change. Both front ends call this one copy; they only show
/// the menus, dialogs and the status text it hands to <c>show</c>.
/// </summary>
public sealed class DiceTurnActions
{
    /// <summary>Shown when the roller hands back the wrong number of dice or a value off the die.</summary>
    public const string InvalidRollText = "The dice roller returned invalid results; nothing was recorded.";

    /// <summary>Tooltip of a module die that cannot be rolled.</summary>
    public const string NeedsFacesText = "This die needs at least 2 faces";

    /// <summary>The purpose logged for 1d6, 2d6 and custom rolls.</summary>
    public const string RollPurpose = "Roll";

    public static DiceSpec OneD6 => new(1, 6, 0);
    public static DiceSpec TwoD6 => new(2, 6, 0);

    readonly Func<ModuleSession?> _session;
    readonly Action<string> _show;

    /// <param name="session">The open module (null = none).</param>
    /// <param name="show">Shows a status text: why nothing may change, an invalid roll, or the roll's log line.</param>
    public DiceTurnActions(Func<ModuleSession?> session, Action<string> show)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _show = show ?? throw new ArgumentNullException(nameof(show));
    }

    /// <summary>The main window's action port; null (or a null port) = execute directly on the game.</summary>
    public Func<IActionPort?>? Actions { get; set; }

    /// <summary>Prompt for each die value instead of rolling (Settings: physical dice).</summary>
    public bool PhysicalDice { get; set; }

    /// <summary>Asks the value of each of n dice with the given sides (physical dice); null = cancelled.</summary>
    public Func<int, int, IReadOnlyList<int>?>? AskPhysical { get; set; }

    /// <summary>Whether a roll's result text is shown (Settings: show last roll in the status bar).</summary>
    public bool ShowLastRoll { get; set; } = true;

    /// <summary>Why nothing may be rolled or stepped now (a received turn is replaying; a viewer copy is read-only), else null.</summary>
    public static string? BlockedText(ModuleSession? s) =>
        s == null ? null : s.IsReplaying ? ModuleSession.ReplayingText : s.IsViewerCopy ? ModuleSession.ViewerCopyText : null;

    /// <summary>The status-bar turn label ("Turn 3 · Movement").</summary>
    public static string TurnText(TurnState t) =>
        "Turn " + t.Turn.ToString(CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(t.Phase) ? "" : " · " + t.Phase);

    /// <summary>The current game's turn (turn 1 without a game).</summary>
    public TurnState CurrentTurn => _session()?.Game?.State.Turn ?? new TurnState(1, null);

    /// <summary>Whether Tools → Turn → Previous turn is enabled.</summary>
    public bool CanStepBack => CurrentTurn.Turn > 1;

    /// <summary>True (and says why) when the game may not change; no roll is made or shown then.</summary>
    public bool Refused()
    {
        if (BlockedText(_session()) is not { } why) return false;
        _show(why);
        return true;
    }

    /// <summary>Rolls (or asks for physical results) and records one RollDice command. Returns false when cancelled or impossible.</summary>
    public bool RollSpec(DiceSpec spec, string purpose, bool quiet = false)
    {
        if (_session() is not { Game: { } game } s || Refused()) return false;
        var results = Results(s, spec.Count, spec.Sides);
        if (results == null) return false;
        Record(game, new RollDice(purpose, spec.Count, spec.Sides, results, DiceExpr.Format(spec), null, spec.Modifier));
        if (!quiet && ShowLastRoll) ShowLast(game);
        return true;
    }

    /// <summary>Rolls a module die (VASSAL SpecialDie) and records the face text.</summary>
    public bool RollSpecial(DieDef die)
    {
        if (_session() is not { Game: { } game } s || die.Faces.Count < 2 || Refused()) return false;
        var results = Results(s, 1, die.Faces.Count);
        if (results == null) return false;
        var face = die.Faces[results[0] - 1];
        Record(game, new RollDice(die.Name, 1, die.Faces.Count, results, null, new[] { face }));
        if (ShowLastRoll) ShowLast(game);
        return true;
    }

    /// <summary>Rolls <paramref name="spec"/> <paramref name="times"/> times ("Multi-roll k/n"); only the last result is shown. Stops at the first failed roll.</summary>
    public void MultiRoll(DiceSpec spec, int times)
    {
        for (var k = 1; k <= times; k++)
            if (!RollSpec(spec, $"Multi-roll {k}/{times}", quiet: k < times)) break;
    }

    /// <summary>Moves the turn counter by <paramref name="delta"/> (never below 1; no command when it would not change).</summary>
    public void StepTurn(int delta)
    {
        if (_session()?.Game is not { } game || Refused()) return;
        var cur = game.State.Turn;
        var n = Math.Max(1, cur.Turn + delta);
        if (n == cur.Turn) return;
        ChangeTurn(game, n, null);
    }

    /// <summary>Whether Set turn may ask now: a game is open and may change (says why otherwise).</summary>
    public bool CanSetTurn() => _session()?.Game != null && !Refused();

    /// <summary>Sets the turn and phase from the Set Turn dialog (a blank phase = none; the phase is trimmed).</summary>
    public void SetTurn(int turn, string? phase)
    {
        if (_session()?.Game is not { } game) return;
        ChangeTurn(game, turn, string.IsNullOrWhiteSpace(phase) ? null : phase.Trim());
    }

    void ShowLast(GameSession game)
    {
        if (game.State.Log.LastOrDefault()?.Text is { Length: > 0 } text) _show(text);
    }

    void Record(GameSession game, RollDice roll)
    {
        if (Actions?.Invoke() is { } port) port.RecordRoll(roll);
        else game.Execute(CommandLog.Stamp(roll));
    }

    void ChangeTurn(GameSession game, int turn, string? phase)
    {
        if (Actions?.Invoke() is { } port) port.SetTurn(turn, phase);
        else game.Execute(CommandLog.Stamp(new SetTurn(turn, phase)));
    }

    IReadOnlyList<int>? Results(ModuleSession s, int n, int sides)
    {
        if (PhysicalDice) return AskPhysical?.Invoke(n, sides);
        var r = s.Roll(n, sides);
        if (r.Count != n || r.Any(v => v < 1 || v > sides))
        {
            _show(InvalidRollText);
            return null;
        }
        return r;
    }
}
