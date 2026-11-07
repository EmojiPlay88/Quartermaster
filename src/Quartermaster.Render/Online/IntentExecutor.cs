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
    /// <summary>Dice of formula previews, pinned per sender for its next Apply (no fishing for a better roll).</summary>
    internal FormulaPins Pins { get; } = new();

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

    /// <param name="name">The sender's display name for chat lines when it is not (yet) in the game's player list (a seat without a side).</param>
    public IntentResult Execute(string playerId, IReadOnlySet<string> sides, GameIntent intent, string? name = null)
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
                ChatIntent ch => Chat(g, who, ch.Text, name),
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
        using (c.ActingFor(sides, who)) v = FormulaIntentRunner.Run(s, c, sides, f, guest: true, who, Pins);
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

    private IntentResult Chat(GameSession g, string who, string? text, string? fallbackName)
    {
        if (ChatText.Clean(text) is not { } clean) return IntentResult.Refused("Type a message first.");
        var name = g.State.Players.FirstOrDefault(p => p != null && p.Id == who)?.Name ?? fallbackName ?? who;
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
    public const string UsePendingText = "Use the pending run for this unit.";
    private const string NotYourPhaseText = "That can't be calculated during another side's phase.";
    public const int MaxTyped = 64;
    public const int MaxTypedValue = 256;

    private static bool IsCheckedKind(BindingKind k) =>
        k is BindingKind.Unit or BindingKind.Global or BindingKind.Holder or BindingKind.Table or BindingKind.Location or BindingKind.Moved;

    /// <param name="guest">True for a guest's intent (host-side rules on outputs and unit-less runs apply).</param>
    /// <param name="who">The sender (pins are per sender).</param>
    /// <param name="pins">Where a preview's dice are pinned and an Apply finds them; null = no pinning (the host's own dialog pins in its model).</param>
    public static FormulaVerdict Run(ModuleSession s, PhaseController c, IReadOnlySet<string>? sides, FormulaIntent i, bool guest,
        string? who = null, FormulaPins? pins = null)
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

        // The same masking as the sender's own dialog: hidden boxes are filled by the engine and never typed (FormulaMasking).
        var hidden = FormulaMasking.HiddenVariables(pack, s.Model, full, f, i.PieceId, sides);
        var model = new FormulaDialogModel(runner, f, i.PieceId) { Checked = full.Difficulty != Difficulty.FullyAuto, Moved = moved, Hidden = hidden };
        foreach (var (variable, text) in i.Typed ?? new Dictionary<string, string>())
        {
            if (variable == null || hidden.Contains(variable)) continue;
            var b = f.Bindings.FirstOrDefault(x => x != null && x.Variable == variable);
            // Input boxes always; checked boxes only in the manual modes (they gate Apply and never feed the run); never dice or overrides.
            var accept = b != null && (b.Kind == BindingKind.Input || (model.Checked && IsCheckedKind(b.Kind)));
            if (accept) model.SetBoxText(variable, text ?? "");
        }
        // One unit, one set of dice: while the unit has this formula's run waiting, a guest uses that run (an ad-hoc run would be another roll).
        if (guest && i.RunId == null && i.PieceId != null
            && full.Pending.Any(r => r != null && r.FormulaId == f.Id && (r.Units ?? Array.Empty<PendingUnit>()).Any(u => u is { Applied: false } && u.PieceId == i.PieceId)))
            return FormulaVerdict.Refused(UsePendingText);
        // Every die of a guest's run is pinned per (sender, formula, unit) and dice configuration before anything is answered: a preview,
        // a refused Apply and a retry all see the same roll, so nothing can be re-rolled by asking again.
        var pinKey = new FormulaPins.Key(who ?? "", f.Id, i.PieceId);
        FormulaPins.Scope? scope = null;
        if (pins != null)
        {
            scope = pins.Open(pinKey, g, pack, pins.MayRollHint(f));
            if (scope == null) return FormulaVerdict.Refused(FormulaPins.FullText);
            var roller = s.Roll;
            var sc = scope;
            model.DiceSource = (key, n, sides) => sc.Roll(key, n, sides, roller);
        }
        if (i.Calculate) model.Calculate(full);
        var dv = model.Evaluate(full);
        // A run that rolled where the static guess saw no dice could not pin them (the sender's table was full): nothing goes back.
        if (scope is { Unpinned: true }) return FormulaVerdict.Refused(FormulaPins.FullText);
        var boxes = dv.Boxes.Select(b => new FormulaBox(b.Variable,
            hidden.Contains(b.Variable) ? FormulaBox.Hidden : b.Wrong ? FormulaBox.Wrong : FormulaBox.Ok)).ToList();
        if (i.Preview)
        {
            // Nothing is executed. The visible values and the dice go back; the dice stay pinned for this sender's Apply.
            if (scope != null) scope.Previewed = true;
            var source = model.Checked ? model.LastExpected ?? dv.Trace : dv.Trace;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var v in source.Variables)
                if (v?.Variable != null && !hidden.Contains(v.Variable) && v.Value is { } val) values[v.Variable] = FormulaDialogModel.ExactText(val);
            return new FormulaVerdict(false, boxes, dv.CanApply ? null : SafeStatus(dv, hidden), values,
                model.PinnedRolls.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value.ToList(), StringComparer.Ordinal));
        }
        if (!dv.CanApply) return new FormulaVerdict(false, boxes, SafeStatus(dv, hidden));   // the dice stay pinned for a corrected Apply
        if (!c.ApplyFormula(i.RunId, i.PieceId, dv.Trace, model.AutoCalculated || scope?.Previewed == true))
            return new FormulaVerdict(false, boxes, NotWaitingText);
        pins?.Remove(pinKey);   // consumed
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
}

/// <summary>
/// The dice of guests' formula runs on the host (spec 8a.1: the host rolls). Every die rolled for a guest's run (preview, refused Apply,
/// any refusal after the run) is pinned before the answer goes back, per scope (sender, formula, unit; the run id is not part of it, so
/// an ad-hoc run and the pending run share their dice) and per dice configuration (<c>key|count|sides</c>). Pins are append-only: an
/// existing configuration is never rolled again, so asking again, retrying a wrong Apply or toggling an input that changes the dice
/// all come back to the first roll. A scope lives until a successful Apply consumes it, the phase position (turn/phase) changes, or the
/// game session or rules pack is replaced; there is no wall-clock expiry. A live scope is never evicted: with
/// <see cref="MaxPerSender"/> scopes a sender's run that may roll dice in a new scope is refused with <see cref="FullText"/>.
/// </summary>
internal sealed class FormulaPins
{
    public const int MaxPerSender = 512;
    public const string FullText = "Apply or wait before previewing more.";

    public readonly record struct Key(string Who, string FormulaId, string? PieceId);

    /// <summary>One scope's dice by configuration, valid for one game session, rules pack and phase position.</summary>
    public sealed class Scope(GameSession game, RulesPack pack, PhasePosition? position)
    {
        private readonly Dictionary<string, IReadOnlyList<int>> _dice = new(StringComparer.Ordinal);
        public GameSession Game { get; } = game;
        public RulesPack Pack { get; } = pack;
        public PhasePosition? Position { get; } = position;

        /// <summary>True once a preview showed these dice (an Apply after it counts as calculated).</summary>
        public bool Previewed { get; set; }

        /// <summary>Registers an unregistered scope on its first real roll (false = the sender's table is full); null once registered.</summary>
        internal Func<bool>? Register { get; set; }

        /// <summary>
        /// True when a die was asked for but the scope could not be registered: those dice were never rolled or pinned and the run must be
        /// refused (fail closed).
        /// </summary>
        public bool Unpinned { get; private set; }

        public int Count => _dice.Count;

        /// <summary>The pinned dice of this configuration, or a new roll that is pinned first (append-only).</summary>
        public IReadOnlyList<int> Roll(string key, int n, int sides, Func<int, int, IReadOnlyList<int>> roller)
        {
            if (Register is { } register)
            {
                if (!register())
                {
                    // Not rolled: the run is refused, so nothing may depend on these placeholder dice.
                    Unpinned = true;
                    return Enumerable.Repeat(1, Math.Max(0, n)).ToList();
                }
                Register = null;
            }
            var config = ConfigKey(key, n, sides);
            if (_dice.TryGetValue(config, out var pinned)) return pinned;
            var rolled = roller(n, sides).ToList();
            _dice[config] = rolled;
            return rolled;
        }
    }

    /// <summary>The static guess whether a formula rolls (<see cref="MayRoll"/>); a test hook can make it miss. A miss is caught on the first roll.</summary>
    internal Func<FormulaDef, bool> MayRollHint { get; set; } = MayRoll;

    private readonly Dictionary<string, Dictionary<Key, Scope>> _bySender = new(StringComparer.Ordinal);

    public static string ConfigKey(string key, int n, int sides) => $"{key}|{n}|{sides}";

    private static readonly System.Text.RegularExpressions.Regex DiceCall = new(@"(?<![\p{L}\p{N}_])dice\s*\(",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// True when the formula may roll dice: a Roll binding, or a dice( call in its text, a table key expression or a binding's AutoValue
    /// (an Input's pre-fill). Only those take a pinned scope.
    /// </summary>
    public static bool MayRoll(FormulaDef f)
    {
        var bindings = f.Bindings ?? new List<VariableBinding>();
        bool Calls(string? expr) => !string.IsNullOrEmpty(expr) && DiceCall.IsMatch(expr);
        return bindings.Any(b => b is { Kind: BindingKind.Roll })
            || Calls(f.Text)
            || bindings.Any(b => b != null && (Calls(b.RowExpr) || Calls(b.ColExpr) || Calls(b.AutoValue)));
    }

    private static bool Valid(Scope sc, GameSession g, RulesPack pack) =>
        ReferenceEquals(sc.Game, g) && ReferenceEquals(sc.Pack, pack) && Equals(sc.Position, g.State.Position);

    /// <summary>
    /// The live scope for this key, or a new one. A new scope is registered now when <paramref name="mayRoll"/> (the static guess), else
    /// lazily on its first actual roll: whatever the guess, every die rolled for a guest is pinned. Null when the sender already holds
    /// <see cref="MaxPerSender"/> live scopes and this one would be new and may roll: the caller refuses before rolling anything. A lazy
    /// scope that finds the table full on its first roll rolls nothing and is marked <see cref="Scope.Unpinned"/> (the caller refuses).
    /// </summary>
    public Scope? Open(Key key, GameSession g, RulesPack pack, bool mayRoll)
    {
        if (!_bySender.TryGetValue(key.Who, out var scopes)) _bySender[key.Who] = scopes = new();
        foreach (var stale in scopes.Where(kv => !Valid(kv.Value, g, pack)).Select(kv => kv.Key).ToList()) scopes.Remove(stale);
        if (scopes.TryGetValue(key, out var live)) return live;
        var scope = new Scope(g, pack, g.State.Position);
        if (!mayRoll)
        {
            scope.Register = () =>
            {
                if (scopes.TryGetValue(key, out var other)) return ReferenceEquals(other, scope);
                if (scopes.Count >= MaxPerSender) return false;
                scopes[key] = scope;
                return true;
            };
            return scope;
        }
        if (scopes.Count >= MaxPerSender) return null;
        scopes[key] = scope;
        return scope;
    }

    public void Remove(Key key)
    {
        if (_bySender.TryGetValue(key.Who, out var scopes)) scopes.Remove(key);
    }

    /// <summary>Live and stale scopes held for a sender (tests).</summary>
    public int Count(string who) => _bySender.TryGetValue(who, out var scopes) ? scopes.Count : 0;
}
