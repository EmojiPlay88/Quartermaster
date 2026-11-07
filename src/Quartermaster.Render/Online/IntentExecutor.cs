using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;

namespace Quartermaster.Render;

/// <summary>
/// Executes a guest's intent on the host (spec 8a.1, 8a.5). Validation uses the sender's sides, never the host's own view: pieces, units and
/// holders of held sides or of no side; tick, acknowledge and Next Phase only while a held side acts (when phases exist); undo, step back,
/// turn, difficulty, notes, players, hidden alerts and computed results are host only. Accepted intents run exactly as if the host had
/// done them (same commands, triggers and dice) with the sender's side as the actor and the sender's id as Who. Never throws.
/// </summary>
public sealed class IntentExecutor(ModuleSession s, PhaseController c)
{
    public const string HostOnlyText = "Only the host can do that.";
    public const string NoGameText = "No game is running.";

    public IntentResult Execute(string playerId, IReadOnlySet<string> sides, GameIntent intent)
    {
        try
        {
            if (intent == null) return IntentResult.Refused("That request was not understood.");
            if (s.Game is not { } g) return IntentResult.Refused(NoGameText);
            var held = sides ?? new HashSet<string>();
            var who = string.IsNullOrWhiteSpace(playerId) ? "guest" : playerId;
            if (g.InAction) return IntentResult.Refused("The host is busy; try again.");
            return intent switch
            {
                MoveIntent m => Move(g, held, who, m.Moves),
                PieceCommandIntent p => PieceCommand(g, held, who, p.Command),
                FormulaIntent f => Formula(held, who, f),
                LedgerEditIntent l => EditLedger(g, held, who, l),
                DiceIntent d => Dice(g, who, d),
                TickIntent t => Phase(held, who, () => c.Tick(t.Index, t.Ticked)),
                AckIntent a => Phase(held, who, () => c.Acknowledge(a.Index)),
                NextPhaseIntent => NextPhase(held, who),
                ChatIntent ch => Chat(g, who, ch.Text),
                HideAlertIntent => IntentResult.Refused(HostOnlyText),
                _ => IntentResult.Refused(HostOnlyText),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { s.Log.Add(Quartermaster.Vassal.DiagnosticSeverity.Error, "online", "A guest request failed: " + ex.Message); }
            catch (Exception) { /* logging must not throw either */ }
            return IntentResult.Refused("The host could not do that.");
        }
    }

    private PieceAccess Access(GameSession g, IReadOnlySet<string> sides) => new(g.State, s.Rules, s.Model, sides);

    private static string OtherSide(string what) => $"{what} {ModuleSession.OtherSideText}.";

    // ---- moves and piece commands ----

    private static string? CheckStack(GameSession g, PieceAccess a, string mapId, string? onto)
    {
        if (onto == null || !g.State.Stacks.TryGetValue(onto, out var st) || st == null || st.MapId != mapId) return null;
        return st.PieceIds.All(a.CanAct) ? null : "Those pieces can't be stacked onto another side's piece.";
    }

    private static string? CheckMove(GameSession g, PieceAccess a, MovePieces m)
    {
        if (m == null || m.PieceIds == null || m.PieceIds.Count == 0 || m.MapId == null) return "That move was not understood.";
        if (m.PieceIds.Any(id => id == null || !g.State.Pieces.ContainsKey(id))) return "A piece in that move no longer exists.";
        if (!m.PieceIds.All(a.CanAct)) return OtherSide("A piece in that move");
        if (a.MapHidden(m.MapId)) return "That map belongs to a side you don't hold.";
        return CheckStack(g, a, m.MapId, m.StackOnto);
    }

    private IntentResult Move(GameSession g, IReadOnlySet<string> sides, string who, IReadOnlyList<MovePieces>? moves)
    {
        if (moves == null || moves.Count == 0) return IntentResult.Refused("That move was not understood.");
        var a = Access(g, sides);
        foreach (var m in moves)
            if (CheckMove(g, a, m) is { } why) return IntentResult.Refused(why);
        // The guest's own log text and stamps are never trusted.
        var clean = moves.Select(m => m with { LogText = null, At = null, Who = null }).ToList();
        using (c.ActingFor(sides, who))
        using (g.BeginAction())
            c.ExecuteMove(clean);
        return IntentResult.Done();
    }

    private IntentResult PieceCommand(GameSession g, IReadOnlySet<string> sides, string who, GameCommand? cmd)
    {
        if (cmd is MovePieces mp) return Move(g, sides, who, new[] { mp });
        var a = Access(g, sides);
        GameCommand clean;
        switch (cmd)
        {
            case AddPiece add:
            {
                if (add.PieceId == null || add.TypeString == null || add.MapId == null || g.State.Pieces.ContainsKey(add.PieceId))
                    return IntentResult.Refused("That piece can't be placed.");
                if (a.MapHidden(add.MapId)) return IntentResult.Refused("That map belongs to a side you don't hold.");
                if (a.NotHeld(a.SideOf(new PieceInstance { Id = add.PieceId, TypeString = add.TypeString, MapId = add.MapId, Pos = add.Pos })))
                    return IntentResult.Refused(OtherSide("That piece"));
                if (CheckStack(g, a, add.MapId, add.StackOnto) is { } why) return IntentResult.Refused(why);
                clean = add with { LogText = null, At = null, Who = null };
                break;
            }
            case RemovePiece r when Actable(g, a, r.PieceId) is { } why:
                return IntentResult.Refused(why);
            case RemovePiece r:
                clean = r with { LogText = null, At = null, Who = null };
                break;
            case SetTraitState t when Actable(g, a, t.PieceId) is { } why:
                return IntentResult.Refused(why);
            case SetTraitState t:
                clean = t;
                break;
            case ToggleTrail tt when Actable(g, a, tt.PieceId) is { } why:
                return IntentResult.Refused(why);
            case ToggleTrail tt:
                clean = tt;
                break;
            case ClearMoved cm:
            {
                if (cm.MapId == null || a.MapHidden(cm.MapId)) return IntentResult.Refused("That map belongs to a side you don't hold.");
                if (!g.State.Pieces.Values.Where(p => p != null && p.MapId == cm.MapId).All(p => a.CanAct(p.Id)))
                    return IntentResult.Refused("That map holds pieces of a side you don't hold.");
                clean = cm;
                break;
            }
            default:
                return IntentResult.Refused(HostOnlyText);
        }
        g.Execute(CommandLog.Prepare(CommandLog.Stamp(clean, who), g.State, s));
        if (clean is AddPiece) s.SeedLedgers();
        return IntentResult.Done();
    }

    private static string? Actable(GameSession g, PieceAccess a, string? pieceId)
    {
        if (pieceId == null || !g.State.Pieces.ContainsKey(pieceId)) return "That piece no longer exists.";
        return a.CanAct(pieceId) ? null : OtherSide("That piece");
    }

    // ---- formulas, ledger, dice ----

    private IntentResult Formula(IReadOnlySet<string> sides, string who, FormulaIntent f)
    {
        FormulaVerdict v;
        using (c.ActingFor(sides, who)) v = FormulaIntentRunner.Run(s, c, sides, f);
        return new IntentResult(v.Applied, v.Message, v);
    }

    private IntentResult EditLedger(GameSession g, IReadOnlySet<string> sides, string who, LedgerEditIntent l)
    {
        if (l.Key == null || l.PropertyId == null) return IntentResult.Refused("That change was not understood.");
        if (!double.IsFinite(l.After)) return IntentResult.Refused("That is not a number.");
        if (string.IsNullOrWhiteSpace(l.Note)) return IntentResult.Refused("A manual change needs a note.");
        if (s.Rules?.Properties.FirstOrDefault(p => p != null && p.Id == l.PropertyId) is not { } prop)
            return IntentResult.Refused("That value no longer exists.");
        if (LedgerKey.TryParse(l.Key, out var isPiece, out var id) && isPiece && !g.State.Pieces.ContainsKey(id))
            return IntentResult.Refused("That piece no longer exists.");
        if (!s.CanEditLedgerFor(l.Key, sides)) return IntentResult.Refused(OtherSide("That value"));
        if (Ledger.Get(g.State, l.Key, prop.Id) is { } cur && cur == l.After) return IntentResult.Done();   // unchanged: no history entry
        var before = Ledger.GetOrDefault(g.State, l.Key, prop);
        var note = l.Note.Trim();
        if (note.Length > ChatText.MaxLength) note = note[..ChatText.MaxLength];
        g.Execute(CommandLog.Prepare(CommandLog.Stamp(new LedgerDelta(l.Key, prop.Id, before, l.After, "manual", null, null, note, null), who), g.State, s));
        return IntentResult.Done();
    }

    private IntentResult Dice(GameSession g, string who, DiceIntent d)
    {
        // Same limits as the dice expression parser.
        if (d.Spec is not { Count: >= 1 and <= 100, Sides: >= 2 and <= 1000, Modifier: >= -1000 and <= 1000 } spec)
            return IntentResult.Refused(DiceExpr.FormatError);
        var purpose = string.IsNullOrWhiteSpace(d.Purpose) ? "Roll" : d.Purpose.Trim();
        if (purpose.Length > 80) purpose = purpose[..80];
        var results = s.Roll(spec.Count, spec.Sides);
        if (results.Count != spec.Count || results.Any(x => x < 1 || x > spec.Sides))
            return IntentResult.Refused("The dice roller returned invalid results; nothing was recorded.");
        g.Execute(CommandLog.Prepare(CommandLog.Stamp(
            new RollDice(purpose, spec.Count, spec.Sides, results, DiceExpr.Format(spec), null, spec.Modifier), who), g.State, s));
        return IntentResult.Done();
    }

    // ---- phases ----

    /// <summary>Null when a player holding these sides may tick, acknowledge or advance now; else why not.</summary>
    private string? PhaseGate(IReadOnlySet<string> sides)
    {
        if (s.Rules is not { Phases.Count: > 0 } pack || s.Game?.State is not { } st || st.Position == null) return null;
        if (Sequencer.Current(pack, st) is not { } phase) return null;
        if (Sequencer.ActingSide(pack, phase) is not { } side || sides.Contains(side.Id)) return null;
        return $"{(string.IsNullOrWhiteSpace(side.Name) ? side.Id : side.Name)} is playing {phase.Name}; wait for your phase.";
    }

    private IntentResult Phase(IReadOnlySet<string> sides, string who, Action act)
    {
        if (PhaseGate(sides) is { } why) return IntentResult.Refused(why);
        using (c.ActingFor(sides, who)) act();
        return IntentResult.Done();
    }

    private IntentResult NextPhase(IReadOnlySet<string> sides, string who)
    {
        if (s.Rules is not { Phases.Count: > 0 }) return IntentResult.Refused("This game has no phases; only the host can change the turn.");
        if (PhaseGate(sides) is { } why) return IntentResult.Refused(why);
        string? pass;
        using (c.ActingFor(sides, who))
        {
            var v = c.View();
            if (v.BeforeStart) { c.Start(); pass = null; }
            else pass = c.Advance();
        }
        return new IntentResult(true, pass);
    }

    // ---- chat ----

    private IntentResult Chat(GameSession g, string who, string? text)
    {
        if (ChatText.Clean(text) is not { } clean) return IntentResult.Refused("Type a message first.");
        var name = g.State.Players.FirstOrDefault(p => p != null && p.Id == who)?.Name ?? who;
        g.Execute(CommandLog.Prepare(CommandLog.Stamp(new AddLogLine(LogKind.Chat, ChatText.Line(name, clean)), who), g.State, s));
        return IntentResult.Done();
    }
}

/// <summary>
/// The formula dialog's path without the dialog: a <see cref="FormulaDialogModel"/> in the game's checking mode, the sender's typed values,
/// Calculate when asked, then <see cref="PhaseController.ApplyFormula"/>. Inputs whose value comes from data the sender may not see are filled
/// by the engine and reported as <see cref="FormulaBox.Hidden"/>; no value ever goes into the verdict.
/// </summary>
internal static class FormulaIntentRunner
{
    private const string NotWaitingText = "This calculation is no longer waiting.";

    public static FormulaVerdict Run(ModuleSession s, PhaseController c, IReadOnlySet<string>? sides, FormulaIntent i)
    {
        if (s.Game is not { } g || s.Runner is not { } runner || s.Rules is not { } pack) return FormulaVerdict.Refused(IntentExecutor.NoGameText);
        if (i == null || i.FormulaId == null || pack.ById<FormulaDef>(i.FormulaId) is not { } f) return FormulaVerdict.Refused("That formula no longer exists.");
        var full = g.State;
        PieceInstance? piece = null;
        if (i.PieceId != null && !full.Pieces.TryGetValue(i.PieceId, out piece)) return FormulaVerdict.Refused("That unit no longer exists.");
        if (!s.CanActFor(i.PieceId, sides)) return FormulaVerdict.Refused($"That unit {ModuleSession.OtherSideText}.");
        double? moved = null;
        if (i.RunId != null)
        {
            if (!c.IsPending(i.RunId, i.PieceId)) return FormulaVerdict.Refused(NotWaitingText);
            moved = full.Pending.First(r => r.Id == i.RunId).Units.First(u => u.PieceId == i.PieceId && !u.Applied).Moved;
        }
        else if (piece != null && !PieceFilter.FormulasFor(pack, piece, full, s.Model).Any(x => x.Id == f.Id))
            return FormulaVerdict.Refused("That formula does not apply to this unit.");

        var hidden = HiddenVariables(s, pack, f, full, i.PieceId, sides);
        var model = new FormulaDialogModel(runner, f, i.PieceId) { Checked = full.Difficulty != Difficulty.FullyAuto, Moved = moved };
        foreach (var (variable, text) in i.Typed ?? new Dictionary<string, string>())
            if (variable != null && !hidden.Contains(variable)) model.SetBoxText(variable, text ?? "");
        if (i.Calculate) model.Calculate(full);
        else if (model.Checked && hidden.Count > 0)
        {
            // The sender cannot type what it cannot see: the engine's expected value fills those boxes (dice stay pinned between the runs).
            model.Run(full);
            foreach (var v in model.LastExpected?.Variables ?? Array.Empty<VariableState>())
                if (hidden.Contains(v.Variable) && v.Value is { } val) model.SetBoxText(v.Variable, Exact(val));
        }
        var dv = model.Evaluate(full);
        var boxes = dv.Boxes.Select(b => new FormulaBox(b.Variable,
            hidden.Contains(b.Variable) ? FormulaBox.Hidden : b.Wrong ? FormulaBox.Wrong : FormulaBox.Ok)).ToList();
        if (!dv.CanApply) return new FormulaVerdict(false, boxes, SafeStatus(dv, hidden));
        if (!c.ApplyFormula(i.RunId, i.PieceId, dv.Trace, model.AutoCalculated)) return new FormulaVerdict(false, boxes, NotWaitingText);
        return new FormulaVerdict(true, boxes, null);
    }

    /// <summary>The dialog's status when it names only variables; an evaluation error is passed on only when no input is hidden.</summary>
    private static string SafeStatus(DialogView dv, HashSet<string> hidden)
    {
        if (dv.Status.StartsWith("Doesn't match: ", StringComparison.Ordinal) || dv.Status.StartsWith("Waiting for: ", StringComparison.Ordinal))
            return dv.Status;
        if (hidden.Count == 0 && dv.Trace.Error is { Length: > 0 } err) return err;
        return "The formula could not be calculated.";
    }

    private static string Exact(Value v) =>
        v.Kind == ValueKind.Number ? v.Number.ToString("R", CultureInfo.InvariantCulture) : v.Display();

    /// <summary>
    /// The variables whose value comes from ledger data the sender's view does not hold (a private unit, holder or global value). A table
    /// lookup counts as hidden when any other input is, since its keys may be computed from them. Empty in solo (null sides).
    /// </summary>
    internal static HashSet<string> HiddenVariables(ModuleSession s, RulesPack pack, FormulaDef f, GameState full, string? pieceId, IReadOnlySet<string>? sides)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        if (sides == null) return hidden;
        var view = PlayerView.For(full, pack, s.Model, sides);
        bool Missing(string key, string? prop) =>
            prop != null && Ledger.Get(full, key, prop) != null && Ledger.Get(view, key, prop) == null;
        foreach (var b in f.Bindings ?? new List<VariableBinding>())
        {
            if (b == null) continue;
            var isHidden = b.Kind switch
            {
                BindingKind.Unit => pieceId != null && Missing(LedgerKey.Piece(pieceId), b.Property),
                BindingKind.Global => Missing(LedgerKey.Holder(HolderDef.GlobalId), b.Property),
                BindingKind.Holder => b.HolderId != null && Missing(LedgerKey.Holder(b.HolderId), b.Property),
                _ => false,
            };
            if (isHidden) hidden.Add(b.Variable);
        }
        if (hidden.Count > 0)
            foreach (var b in f.Bindings!)
                if (b is { Kind: BindingKind.Table }) hidden.Add(b.Variable);
        return hidden;
    }
}
