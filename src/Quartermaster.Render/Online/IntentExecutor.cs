using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>
/// Executes a guest's intent on the host (spec 8a.1, 8a.5). Validation uses the sender's sides, never the host's own view: pieces, units and
/// holders of held sides or of no side; tick, acknowledge and Next Phase only while a held side acts (when phases exist); undo, step back,
/// turn, difficulty, notes, players, hidden alerts and computed results are host only. Accepted intents run exactly as if the host had
/// done them (same commands, triggers and dice) with the sender's side as the actor and the sender's id as Who. Pieces the sender cannot
/// see are treated as if they did not exist, so a refusal never reveals them. Never throws.
/// </summary>
public sealed class IntentExecutor(ModuleSession s, PhaseController c)
{
    public const string HostOnlyText = "Only the host can do that.";
    public const string NoGameText = "No game is running.";
    public const string NotUnderstoodText = "That request was not understood.";
    public const string GoneText = "That piece no longer exists.";
    public const string MapText = "You can't do that on this map.";
    public const string StackText = "You can't stack there.";

    /// <summary>Longest id (piece, map, stack, formula, run, ledger key, property) a guest may send.</summary>
    public const int MaxId = 256;
    /// <summary>Longest piece type or state string a guest may send (real module types can run to a few kilobytes).</summary>
    public const int MaxTypeText = 16 * 1024;
    /// <summary>Longest trait state a guest may set.</summary>
    public const int MaxTraitState = 4 * 1024;
    /// <summary>Most pieces in one guest move.</summary>
    public const int MaxMovePieces = 1000;

    public IntentResult Execute(string playerId, IReadOnlySet<string> sides, GameIntent intent)
    {
        try
        {
            if (intent == null) return IntentResult.Refused(NotUnderstoodText);
            if (s.Game is not { } g) return IntentResult.Refused(NoGameText);
            var held = sides ?? new HashSet<string>();
            var who = string.IsNullOrWhiteSpace(playerId) ? "guest" : playerId.Length > MaxId ? playerId[..MaxId] : playerId;
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
            try { s.Log.Add(DiagnosticSeverity.Error, "online", "A guest request failed: " + ex.Message); }
            catch (Exception) { /* logging must not throw either */ }
            return IntentResult.Refused("The host could not do that.");
        }
    }

    private PieceAccess Access(GameSession g, IReadOnlySet<string> sides) => new(g.State, s.Rules, s.Model, sides);

    private static string OtherSide(string what) => $"{what} {ModuleSession.OtherSideText}.";

    private static bool BadId(string? id) => string.IsNullOrEmpty(id) || id.Length > MaxId;

    /// <summary>True when the piece exists and the sender can see it (anything else is "no such piece" to the sender).</summary>
    private static bool Exists(GameSession g, PieceAccess a, string? id) => id != null && g.State.Pieces.ContainsKey(id) && a.CanSee(id);

    // ---- moves and piece commands ----

    /// <summary>
    /// The stack a guest's pieces may join: the requested one when every piece in it is the sender's to act on; null (a new stack at the
    /// position) when it does not exist, is on another map or holds a piece the sender cannot see, so its existence is never revealed.
    /// <paramref name="why"/> is set when a visible piece of another side is in it.
    /// </summary>
    private static string? StackFor(GameSession g, PieceAccess a, string mapId, string? onto, out string? why)
    {
        why = null;
        if (onto == null || onto.Length > MaxId) return null;
        if (!g.State.Stacks.TryGetValue(onto, out var st) || st == null || st.MapId != mapId) return null;
        if (!st.PieceIds.All(a.CanSee)) return null;
        if (!st.PieceIds.All(a.CanAct)) why = StackText;
        return onto;
    }

    private static string? CheckMove(GameSession g, PieceAccess a, MovePieces m)
    {
        if (m == null || m.PieceIds == null || m.PieceIds.Count == 0 || m.PieceIds.Count > MaxMovePieces || BadId(m.MapId)
            || !double.IsFinite(m.Pos.X) || !double.IsFinite(m.Pos.Y))
            return NotUnderstoodText;
        if (m.PieceIds.Any(id => BadId(id) || !Exists(g, a, id))) return "A piece in that move no longer exists.";
        if (!m.PieceIds.All(a.CanAct)) return OtherSide("A piece in that move");
        if (a.MapHidden(m.MapId)) return MapText;
        StackFor(g, a, m.MapId, m.StackOnto, out var why);
        return why;
    }

    private IntentResult Move(GameSession g, IReadOnlySet<string> sides, string who, IReadOnlyList<MovePieces>? moves)
    {
        if (moves == null || moves.Count == 0 || moves.Count > MaxMovePieces) return IntentResult.Refused(NotUnderstoodText);
        var a = Access(g, sides);
        foreach (var m in moves)
            if (CheckMove(g, a, m) is { } why) return IntentResult.Refused(why);
        // The guest's own log text and stamps are never trusted; a stack it may not see becomes a new stack.
        var clean = moves.Select(m => m with { LogText = null, At = null, Who = null, StackOnto = StackFor(g, a, m.MapId, m.StackOnto, out _) }).ToList();
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
                if (CheckAdd(g, a, sides, add) is { } why) return IntentResult.Refused(why);
                // A taken id gets a fresh one from the host: refusing would let a guest probe for pieces it cannot see.
                var id = g.State.Pieces.ContainsKey(add.PieceId) ? "p:" + Guid.NewGuid().ToString("N") : add.PieceId;
                clean = add with { PieceId = id, LogText = null, At = null, Who = null, StackOnto = StackFor(g, a, add.MapId, add.StackOnto, out _) };
                break;
            }
            case RemovePiece r:
            {
                if (Actable(g, a, r.PieceId) is { } why) return IntentResult.Refused(why);
                clean = r with { LogText = null, At = null, Who = null };
                break;
            }
            case SetTraitState t:
            {
                if (CheckTrait(g, a, sides, t) is { } why) return IntentResult.Refused(why);
                clean = t;
                break;
            }
            case ClearMoved cm:
            {
                // One message for every refusal: it must not reveal whether the map holds pieces the sender cannot see.
                if (BadId(cm.MapId) || a.MapHidden(cm.MapId)
                    || !g.State.Pieces.Values.Where(p => p != null && p.MapId == cm.MapId).All(p => a.CanAct(p.Id)))
                    return IntentResult.Refused(MapText);
                clean = cm;
                break;
            }
            case ToggleTrail:
            case SetStackExpanded:
                return IntentResult.Refused("Trails and stack views are shown on your own screen only.");
            default:
                return IntentResult.Refused(HostOnlyText);
        }
        using (c.ActingFor(sides, who))
        {
            g.Execute(CommandLog.Prepare(CommandLog.Stamp(clean, who), g.State, s));
            if (clean is AddPiece) s.SeedLedgers();
        }
        return IntentResult.Done();
    }

    private static string? Actable(GameSession g, PieceAccess a, string? pieceId)
    {
        if (BadId(pieceId) || !Exists(g, a, pieceId)) return GoneText;
        return a.CanAct(pieceId) ? null : OtherSide("That piece");
    }

    /// <summary>A side change by trait state or a new piece is allowed only into a held side (or, for a side-less piece, staying side-less).</summary>
    private static string? SideRule(IReadOnlySet<string> sides, string? before, string? after, string refusal)
    {
        if (after == null) return before == null ? null : refusal;
        return sides.Contains(after) ? null : refusal;
    }

    private static string? CheckAdd(GameSession g, PieceAccess a, IReadOnlySet<string> sides, AddPiece add)
    {
        if (BadId(add.PieceId) || BadId(add.MapId) || string.IsNullOrEmpty(add.TypeString) || add.TypeString.Length > MaxTypeText
            || (add.StateString?.Length ?? 0) > MaxTypeText || !double.IsFinite(add.Pos.X) || !double.IsFinite(add.Pos.Y))
            return "That piece can't be placed.";
        if (a.MapHidden(add.MapId)) return MapText;
        // Decode the state exactly as the reducer does, so the side is judged on the piece as it will exist.
        var probe = new PieceInstance { Id = add.PieceId, TypeString = add.TypeString, MapId = add.MapId, Pos = add.Pos,
            TraitStates = GameState.DecodeState(add.TypeString, add.StateString ?? "") };
        if (SideRule(sides, null, a.SideOf(probe), OtherSide("That piece")) is { } why) return why;
        StackFor(g, a, add.MapId, add.StackOnto, out var stackWhy);
        return stackWhy;
    }

    private static string? CheckTrait(GameSession g, PieceAccess a, IReadOnlySet<string> sides, SetTraitState t)
    {
        if (Actable(g, a, t.PieceId) is { } why) return why;
        var p = g.State.Pieces[t.PieceId];
        int count;
        try { count = PieceInfo.TypeOf(p).Traits.Count; }
        catch (Exception) { return NotUnderstoodText; }
        if (t.StateIndex < 0 || t.StateIndex >= count || t.NewState == null || t.NewState.Length > MaxTraitState) return NotUnderstoodText;
        var states = new List<string>(p.TraitStates);
        while (states.Count <= t.StateIndex) states.Add("");
        states[t.StateIndex] = t.NewState;
        var after = new PieceInstance { Id = p.Id, TypeString = p.TypeString, MapId = p.MapId, Pos = p.Pos, StackId = p.StackId, TraitStates = states };
        return SideRule(sides, a.SideOf(p), a.SideOf(after), "That change would give the piece to a side you don't hold.");
    }

    // ---- formulas, ledger, dice ----

    private IntentResult Formula(IReadOnlySet<string> sides, string who, FormulaIntent f)
    {
        FormulaVerdict v;
        using (c.ActingFor(sides, who)) v = FormulaIntentRunner.Run(s, c, sides, f, guest: true);
        return new IntentResult(v.Applied, v.Message, v);
    }

    private IntentResult EditLedger(GameSession g, IReadOnlySet<string> sides, string who, LedgerEditIntent l)
    {
        if (BadId(l.Key) || BadId(l.PropertyId)) return IntentResult.Refused(NotUnderstoodText);
        if (!double.IsFinite(l.After)) return IntentResult.Refused("That is not a number.");
        if (ChatText.Clean(l.Note) is not { } note) return IntentResult.Refused("A manual change needs a note.");
        if (s.Rules?.Properties.FirstOrDefault(p => p != null && p.Id == l.PropertyId) is not { } prop)
            return IntentResult.Refused("That value no longer exists.");
        if (LedgerKey.TryParse(l.Key, out var isPiece, out var id) && isPiece && !Exists(g, Access(g, sides), id))
            return IntentResult.Refused(GoneText);
        if (!s.CanEditLedgerFor(l.Key, sides)) return IntentResult.Refused(OtherSide("That value"));
        if (Ledger.Get(g.State, l.Key, prop.Id) is { } cur && cur == l.After) return IntentResult.Done();   // unchanged: no history entry
        var before = Ledger.GetOrDefault(g.State, l.Key, prop);
        g.Execute(CommandLog.Prepare(CommandLog.Stamp(new LedgerDelta(l.Key, prop.Id, before, l.After, "manual", null, null, note, null), who), g.State, s));
        return IntentResult.Done();
    }

    private IntentResult Dice(GameSession g, string who, DiceIntent d)
    {
        // Same limits as the dice expression parser.
        if (d.Spec is not { Count: >= 1 and <= 100, Sides: >= 2 and <= 1000, Modifier: >= -1000 and <= 1000 } spec)
            return IntentResult.Refused(DiceExpr.FormatError);
        var purpose = ChatText.Clean(d.Purpose) ?? "Roll";
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
/// Calculate when asked, then <see cref="PhaseController.ApplyFormula"/>. Typed values are taken only where the dialog lets a player type
/// without overriding the engine: Input boxes always, checked boxes (which only gate Apply) in the manual modes, never dice. Inputs whose
/// value comes from data the sender may not see are filled by the engine and reported as <see cref="FormulaBox.Hidden"/>; no value ever
/// goes into the verdict.
/// </summary>
internal static class FormulaIntentRunner
{
    private const string NotWaitingText = "This calculation is no longer waiting.";
    private const string GoneUnitText = "That unit no longer exists.";
    private const string NotYourPhaseText = "That can't be calculated during another side's phase.";
    public const int MaxTyped = 64;
    public const int MaxTypedValue = 256;

    private static bool IsCheckedKind(BindingKind k) =>
        k is BindingKind.Unit or BindingKind.Global or BindingKind.Holder or BindingKind.Table or BindingKind.Location or BindingKind.Moved;

    /// <param name="guest">True for a guest's intent (host-side rules on outputs and unit-less runs apply).</param>
    public static FormulaVerdict Run(ModuleSession s, PhaseController c, IReadOnlySet<string>? sides, FormulaIntent i, bool guest)
    {
        if (s.Game is not { } g || s.Runner is not { } runner || s.Rules is not { } pack) return FormulaVerdict.Refused(IntentExecutor.NoGameText);
        if (i == null || i.FormulaId == null || i.FormulaId.Length > IntentExecutor.MaxId || (i.RunId?.Length ?? 0) > IntentExecutor.MaxId
            || (i.PieceId?.Length ?? 0) > IntentExecutor.MaxId)
            return FormulaVerdict.Refused(IntentExecutor.NotUnderstoodText);
        if (i.Typed is { Count: > MaxTyped } || (i.Typed ?? new Dictionary<string, string>()).Any(kv => kv.Key == null || kv.Key.Length > MaxTypedValue || (kv.Value?.Length ?? 0) > MaxTypedValue))
            return FormulaVerdict.Refused(IntentExecutor.NotUnderstoodText);
        if (pack.ById<FormulaDef>(i.FormulaId) is not { } f) return FormulaVerdict.Refused("That formula no longer exists.");
        var full = g.State;
        PieceInstance? piece = null;
        if (i.PieceId != null)
        {
            // A unit the sender cannot see does not exist for it.
            if (!full.Pieces.TryGetValue(i.PieceId, out piece) || (sides != null && !new PieceAccess(full, pack, s.Model, sides).CanSee(i.PieceId)))
                return FormulaVerdict.Refused(GoneUnitText);
        }
        if (!s.CanActFor(i.PieceId, sides)) return FormulaVerdict.Refused($"That unit {ModuleSession.OtherSideText}.");
        if (guest && sides != null && GuestRule(s, pack, f, i.PieceId, sides) is { } why) return FormulaVerdict.Refused(why);
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
        {
            if (variable == null || hidden.Contains(variable)) continue;
            var b = f.Bindings.FirstOrDefault(x => x != null && x.Variable == variable);
            // Input boxes always; checked boxes only in the manual modes (they gate Apply and never feed the run); never dice or overrides.
            var accept = b != null && (b.Kind == BindingKind.Input || (model.Checked && IsCheckedKind(b.Kind)));
            if (accept) model.SetBoxText(variable, text ?? "");
        }
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

    /// <summary>
    /// False only while a side the sender does not hold owns the turn: true without phases, before the game started and in a phase with
    /// no acting side (no turn ownership, no restriction), and in a phase of a held side.
    /// </summary>
    private static bool ActingSideHeld(ModuleSession s, RulesPack pack, IReadOnlySet<string> sides)
    {
        if (pack.Phases is not { Count: > 0 } || s.Game?.State is not { Position: not null } st) return true;
        if (Sequencer.Current(pack, st) is not { } phase || Sequencer.ActingSide(pack, phase) is not { } side) return true;
        return sides.Contains(side.Id);
    }

    /// <summary>
    /// A guest may write holders of held sides or of no side; the global holder only during a phase of a side it holds; and may run a
    /// unit-less formula only during a phase of a side it holds. Null when allowed.
    /// </summary>
    private static string? GuestRule(ModuleSession s, RulesPack pack, FormulaDef f, string? pieceId, IReadOnlySet<string> sides)
    {
        if (pieceId == null && !ActingSideHeld(s, pack, sides)) return NotYourPhaseText;
        foreach (var o in f.Outputs ?? new List<FormulaOutput>())
        {
            if (o == null) continue;
            var global = o.Target == OutputTarget.Global || (o.Target == OutputTarget.Holder && o.HolderId == HolderDef.GlobalId);
            if (global)
            {
                if (!ActingSideHeld(s, pack, sides)) return "Global values can't be changed during another side's phase.";
                continue;
            }
            if (o.Target == OutputTarget.Holder)
            {
                if (o.HolderId == null) return IntentExecutor.NotUnderstoodText;
                if (Ownership.SideOfHolder(o.HolderId, pack) is { } owner && !sides.Contains(owner))
                    return $"That formula writes to a holder that {ModuleSession.OtherSideText}.";
            }
        }
        return null;
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
