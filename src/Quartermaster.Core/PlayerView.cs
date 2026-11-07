using System.Globalization;
using System.Text.RegularExpressions;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>
/// What one player may see (spec 3.3): a deep-cloned game state with other sides' hidden information removed. Pure; the input is
/// never changed. When in doubt it hides (over-redaction), never shows. Rules:
/// <list type="bullet">
/// <item>Pieces on a map owned by a side not held are removed, with their stacks, ledgers, history and log lines; the map leaves MapOrder.</item>
/// <item>A piece of a side not held with an active Hideable is removed; with an active Obscurable it becomes a nameless placeholder
/// carrying only that mask trait (drawn as the mask), and its ledgers, history and log lines are removed.</item>
/// <item>Private properties of pieces of sides not held (and of deleted pieces, whose side is unknown), and every value of holders owned
/// by a side not held (or of holders missing from the pack), are removed from Ledgers and LedgerHistory. Formula working of kept history
/// entries for other sides' units (or holder entries made in another side's phase) is cleared, as it may cite private inputs.</item>
/// <item>Log: lines about a removed or masked piece (PieceId), on a hidden map (MapId), or naming one (see <see cref="NameRule"/>) are
/// dropped. Ledger/formula/calc lines are linked to their ledger entry (same order, same text) and redacted to "{Side} ledger changed"
/// when the value is hidden, when they belong to another side's unit (PieceId, or a "{unit name}: " subject prefix), or, for
/// formula/calc lines with no unit, when written during another side's phase.</item>
/// <item>Dice of a formula with PrivateRolls: the rolling side is the unit of the ledger line following the roll (PieceId or subject),
/// else the side of the phase it was rolled in; unknown counts as hidden. Hidden rolls lose their results and read "{Side} rolled dice".</item>
/// <item>Pending runs lose units of other sides (and removed pieces); runs left empty are dropped. Unit-less runs follow the current phase's side.</item>
/// </list>
/// </summary>
public static class PlayerView
{
    /// <summary>
    /// Name rule for log redaction: names of removed and masked pieces, trimmed, at least 2 characters, matched case-insensitively as whole
    /// words (no letter, digit or underscore either side). A name also carried by a visible piece is not used (it reveals nothing).
    /// </summary>
    public const int NameRule = 2;

    private const string Unknown = "Another side";

    public static GameState For(GameState full, RulesPack? pack, ModuleModel model, IReadOnlySet<string> heldSides)
    {
        var view = full.DeepClone();
        if (pack == null) return view;
        var held = heldSides ?? new HashSet<string>();
        if ((pack.Sides ?? new List<SideDef>()).All(x => x == null || held.Contains(x.Id))) return view;
        try
        {
            new Filter(full, pack, model, held).Apply(view);
            return view;
        }
        catch (Exception)
        {
            return Lockdown(full.DeepClone());   // fail closed: never leak, never crash
        }
    }

    /// <summary>Everything that could hold another side's data removed; used only if filtering itself failed.</summary>
    private static GameState Lockdown(GameState s)
    {
        s.Pieces = new(); s.Stacks = new(); s.Ledgers = new(); s.LedgerHistory = new(); s.DiceHistory = new();
        s.Log = new(); s.Pending = new(); s.MapOrder = new();
        return s;
    }

    private static bool IsLedgerKind(string? kind) => kind is LogKind.Ledger or LogKind.Formula or LogKind.Calc;

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private sealed class Filter
    {
        private readonly GameState _full;
        private readonly RulesPack _pack;
        private readonly ModuleModel? _model;
        private readonly IReadOnlySet<string> _held;
        private readonly Dictionary<string, string?> _sideOf = new(StringComparer.Ordinal);
        private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ObscurableDef> _masked = new(StringComparer.Ordinal);
        private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _mapHidden = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Visibility> _visibility = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _phaseSide = new(StringComparer.Ordinal);
        /// <summary>Piece name -> first side not held among pieces with that name (null when all are held or public).</summary>
        private readonly Dictionary<string, string?> _subjects = new(StringComparer.Ordinal);
        private readonly List<string> _privatePrefixes;
        private readonly Regex? _names;

        public Filter(GameState full, RulesPack pack, ModuleModel? model, IReadOnlySet<string> held)
        {
            _full = full; _pack = pack; _model = model; _held = held;
            foreach (var p in pack.Properties ?? new()) if (p != null) _visibility.TryAdd(p.Id ?? "", p.Visibility);
            foreach (var ph in pack.Phases ?? new()) if (ph != null) _phaseSide.TryAdd(ph.Name ?? "", ph.SideId);
            _privatePrefixes = (pack.Formulas ?? new()).Where(f => f is { PrivateRolls: true }).Select(f => (f.Name ?? "") + ": ").Distinct().ToList();

            var sides = Ownership.PieceSides(full, pack);
            foreach (var p in full.Pieces.Values)
            {
                if (p == null) continue;
                var side = sides(p);
                _sideOf[p.Id] = side;
                if (MapHidden(p.MapId)) { _removed.Add(p.Id); continue; }
                if (!NotHeld(side)) continue;
                var type = PieceInfo.TypeOf(p);
                ObscurableDef? mask = null;
                var hide = false;
                for (var i = 0; i < type.Traits.Count; i++)
                {
                    var st = i < p.TraitStates.Count ? p.TraitStates[i] : null;
                    if (type.Traits[i] is HideableDef h && TraitState.IsHidden(h, st)) hide = true;
                    if (type.Traits[i] is ObscurableDef o && mask == null && TraitState.IsObscured(o, st)) mask = o;
                }
                if (hide) _removed.Add(p.Id);
                else if (mask != null) _masked[p.Id] = mask;
            }
            _hidden.UnionWith(_removed);
            _hidden.UnionWith(_masked.Keys);

            var visibleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hiddenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in full.Pieces.Values)
            {
                if (p == null) continue;
                var name = PieceInfo.Name(p) ?? "";
                var side = _sideOf[p.Id];
                if (name.Length > 0)
                {
                    if (!_subjects.TryGetValue(name, out var cur)) _subjects[name] = NotHeld(side) ? side : null;
                    else if (cur == null && NotHeld(side)) _subjects[name] = side;
                }
                var trimmed = name.Trim();
                if (_hidden.Contains(p.Id)) { if (trimmed.Length >= NameRule) hiddenNames.Add(trimmed); }
                else visibleNames.Add(trimmed);
            }
            hiddenNames.ExceptWith(visibleNames);
            if (hiddenNames.Count > 0)
                _names = new Regex(@"(?<![\p{L}\p{N}_])(?:" + string.Join("|", hiddenNames.OrderByDescending(n => n.Length).Select(Regex.Escape))
                    + @")(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private bool NotHeld(string? side) => side != null && !_held.Contains(side);

        private bool NameHit(string? text) => _names != null && !string.IsNullOrEmpty(text) && _names.IsMatch(text);

        private string Label(string? side) =>
            side == null ? Unknown : (_pack.Sides ?? new()).FirstOrDefault(x => x != null && x.Id == side)?.Name is { Length: > 0 } n ? n : side;

        private bool MapHidden(string? mapId)
        {
            if (mapId == null) return false;
            if (_mapHidden.TryGetValue(mapId, out var known)) return known;
            var side = Ownership.SideOfMap(mapId, _pack);
            if (side == null && _model != null)
                foreach (var m in _model.Maps)
                    if (m.Name == mapId && m.Id != mapId && Ownership.SideOfMap(m.Id, _pack) is { } s) { side = s; break; }
            return _mapHidden[mapId] = NotHeld(side);
        }

        private string? PhaseSide(string? phaseName) =>
            phaseName != null && _phaseSide.TryGetValue(phaseName, out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

        private bool PropPrivate(string? prop) =>
            prop == null || !_visibility.TryGetValue(prop, out var v) || v != Visibility.Public;

        /// <summary>True when this ledger value is hidden from the viewer; <paramref name="side"/> is its owner when known.</summary>
        private bool ValueHidden(string? key, string? prop, out string? side)
        {
            side = null;
            if (key == null || !LedgerKey.TryParse(key, out var isPiece, out var id)) return true;
            if (isPiece)
            {
                if (_hidden.Contains(id)) { _sideOf.TryGetValue(id, out side); return true; }
                if (!_sideOf.TryGetValue(id, out var ps)) return PropPrivate(prop);   // deleted piece: owner unknown
                if (!NotHeld(ps)) return false;
                side = ps;
                return PropPrivate(prop);
            }
            if (id == HolderDef.GlobalId) return false;
            if ((_pack.Holders ?? new()).FirstOrDefault(h => h != null && h.Id == id) is not { } holder) return true;   // unknown holder
            if (string.IsNullOrWhiteSpace(holder.SideId)) return false;
            side = holder.SideId;
            return NotHeld(holder.SideId);
        }

        /// <summary>A "{unit name}: " prefix naming a piece; <paramref name="side"/> is a side not held among pieces of that name.</summary>
        private bool Subject(string? text, out string? side)
        {
            side = null;
            if (string.IsNullOrEmpty(text)) return false;
            for (var idx = text.IndexOf(": ", StringComparison.Ordinal); idx > 0; idx = text.IndexOf(": ", idx + 1, StringComparison.Ordinal))
                if (_subjects.TryGetValue(text.Substring(0, idx), out side)) return true;
            return false;
        }

        public void Apply(GameState v)
        {
            var log = v.Log ?? new();
            var paired = PairLedgerLines(log);

            // Pieces, stacks, maps.
            foreach (var id in _removed) v.Pieces.Remove(id);
            foreach (var (id, mask) in _masked)
            {
                if (!v.Pieces.TryGetValue(id, out var p)) continue;
                p.TypeString = PieceTypeParser.EncodeState(new[] { mask.RawType, "piece;;;;" });
                p.TraitStates = new() { "masked", "" };
                p.Trail = new();
                p.TrailVisible = false;
            }
            foreach (var key in v.Stacks.Keys.ToList())
            {
                var st = v.Stacks[key];
                if (st == null || MapHidden(st.MapId)) { v.Stacks.Remove(key); continue; }
                st.PieceIds.RemoveAll(_removed.Contains);
                if (st.PieceIds.Count == 0) v.Stacks.Remove(key);
            }
            v.MapOrder = (v.MapOrder ?? new()).Where(m => !MapHidden(m)).ToList();

            // Ledgers.
            foreach (var key in v.Ledgers.Keys.ToList())
            {
                var inner = v.Ledgers[key];
                if (inner == null || (WholeKeyHidden(key) && ValueHidden(key, null, out _))) { v.Ledgers.Remove(key); continue; }
                foreach (var prop in inner.Keys.ToList())
                    if (ValueHidden(key, prop, out _)) inner.Remove(prop);
                if (inner.Count == 0) v.Ledgers.Remove(key);
            }
            v.LedgerHistory = (v.LedgerHistory ?? new())
                .Where(e => e != null && !ValueHidden(e.Key, e.PropertyId, out _) && !NameHit(e.Note) && !NameHit(e.Working))
                .Select(e => e.Working != null && WorkingFromOtherSide(e) ? e with { Working = null } : e)
                .ToList();

            // Log and dice.
            var rolled = new Dictionary<int, string?>();   // dice log index -> hidden roller (only for hidden private rolls)
            for (var i = 0; i < log.Count; i++)
            {
                var l = log[i];
                if (l?.Kind == LogKind.Dice && IsPrivateRoll(l.Text) && !RollVisible(log, i, out var side)) rolled[i] = side;
            }
            v.DiceHistory = FilterDice(v.DiceHistory ?? new(), log, rolled);
            var kept = new List<LogEntry>(log.Count);
            for (var i = 0; i < log.Count; i++)
            {
                var l = log[i];
                if (l == null) continue;
                if (l.PieceId != null && _hidden.Contains(l.PieceId)) continue;
                if (MapHidden(l.MapId)) continue;
                if (NameHit(l.Text)) continue;
                if (rolled.TryGetValue(i, out var roller)) kept.Add(l with { Text = $"{Label(roller)} rolled dice", PieceId = null });
                else if (IsLedgerKind(l.Kind) && LineHidden(log, paired, i, out var owner))
                    kept.Add(l with { Text = $"{Label(owner)} ledger changed", PieceId = null });
                else kept.Add(l);
            }
            v.Log = kept;

            // Pending runs.
            var phaseSide = v.Position == null ? null : _pack.ById<PhaseDef>(v.Position.PhaseId ?? "")?.SideId;
            var phaseVisible = string.IsNullOrWhiteSpace(phaseSide) || _held.Contains(phaseSide);
            bool UnitVisible(string? pid) =>
                pid == null ? phaseVisible : _sideOf.TryGetValue(pid, out var s) && !_hidden.Contains(pid) && !NotHeld(s);
            var runs = new List<PendingRun>();
            foreach (var r in v.Pending ?? new())
            {
                if (r == null) continue;
                var units = r.Units ?? Array.Empty<PendingUnit>();
                if (units.Count == 0) { if (phaseVisible) runs.Add(r); continue; }
                var mine = units.Where(u => u != null && UnitVisible(u.PieceId)).ToList();
                if (mine.Count == 0) continue;
                runs.Add(mine.Count == units.Count ? r : r with { Units = mine });
            }
            v.Pending = runs;
        }

        /// <summary>Removed or masked pieces and private or unknown holders lose every value; other keys are filtered per property.</summary>
        private bool WholeKeyHidden(string key)
        {
            if (!LedgerKey.TryParse(key, out var isPiece, out var id)) return true;
            return isPiece ? _hidden.Contains(id) : id != HolderDef.GlobalId;
        }

        private bool WorkingFromOtherSide(LedgerEntry e)
        {
            if (e.Source != "formula" || !LedgerKey.TryParse(e.Key, out var isPiece, out var id)) return false;
            if (isPiece) return !_sideOf.TryGetValue(id, out var s) || NotHeld(s);
            return NotHeld(PhaseSide(e.Phase));
        }

        private bool IsPrivateRoll(string? text) =>
            text != null && _privatePrefixes.Any(px => text.StartsWith(px, StringComparison.Ordinal));

        /// <summary>
        /// Links each ledger history entry (except seeds, which log nothing) to the log line its LedgerDelta wrote: the next unclaimed
        /// ledger/formula/calc line with the same piece id (none for holders) whose text contains the entry's working or default text.
        /// </summary>
        private LedgerEntry?[] PairLedgerLines(List<LogEntry> log)
        {
            var paired = new LedgerEntry?[log.Count];
            var cursor = 0;
            foreach (var e in _full.LedgerHistory ?? new())
            {
                if (e == null || e.Source == "seed" || e.Key == null || !LedgerKey.TryParse(e.Key, out var isPiece, out var id)) continue;
                var core = e.Working ?? $"{e.PropertyId} {Num(e.Before)} → {Num(e.After)}";
                for (var i = cursor; i < log.Count; i++)
                {
                    var l = log[i];
                    if (l == null || paired[i] != null || !IsLedgerKind(l.Kind)) continue;
                    if ((isPiece ? l.PieceId != id : l.PieceId != null) || l.Text == null || !l.Text.Contains(core, StringComparison.Ordinal)) continue;
                    paired[i] = e;
                    cursor = i + 1;
                    break;
                }
            }
            return paired;
        }

        private bool LineHidden(List<LogEntry> log, LedgerEntry?[] paired, int i, out string? side)
        {
            var l = log[i];
            var e = paired[i];
            if (e != null && ValueHidden(e.Key, e.PropertyId, out side)) return true;
            var calc = l.Kind is LogKind.Formula or LogKind.Calc;
            if (l.PieceId != null && _sideOf.TryGetValue(l.PieceId, out var ps))
            {
                side = ps;
                if (NotHeld(ps)) return calc || e == null;
                side = null;
                return false;
            }
            if (Subject(l.Text, out side)) return side != null;
            side = null;
            if (calc && PhaseSide(l.Phase) is { } phase && NotHeld(phase)) { side = phase; return true; }
            // A ledger line no history entry explains (its key is unknown) is hidden; a unit-less calc note is not.
            return e == null && l.Kind is LogKind.Ledger or LogKind.Formula;
        }

        /// <summary>Whether the viewer may see the private roll logged at <paramref name="i"/>; <paramref name="side"/> is the roller when known.</summary>
        private bool RollVisible(List<LogEntry> log, int i, out string? side)
        {
            side = null;
            var j = i + 1;
            while (j < log.Count && log[j]?.Kind == LogKind.Dice) j++;
            if (j < log.Count && log[j] is { } next && IsLedgerKind(next.Kind))
            {
                if (next.PieceId != null && _sideOf.TryGetValue(next.PieceId, out var ps) && ps != null) { side = ps; return _held.Contains(ps); }
                if (Subject(next.Text, out var subj) && subj != null) { side = subj; return false; }
            }
            if (PhaseSide(log[i].Phase) is { } phase) { side = phase; return _held.Contains(phase); }
            return false;
        }

        /// <summary>Each dice entry is linked to the next dice log line starting with its purpose; hidden rolls lose their results.</summary>
        private List<DiceEntry> FilterDice(List<DiceEntry> dice, List<LogEntry> log, Dictionary<int, string?> rolled)
        {
            var result = new List<DiceEntry>(dice.Count);
            var cursor = 0;
            foreach (var d in dice)
            {
                if (d == null) continue;
                var line = -1;
                for (var i = cursor; i < log.Count; i++)
                    if (log[i]?.Kind == LogKind.Dice && log[i].Text != null && log[i].Text.StartsWith((d.Purpose ?? "") + ": ", StringComparison.Ordinal))
                    {
                        line = i;
                        cursor = i + 1;
                        break;
                    }
                if (IsPrivateRoll((d.Purpose ?? "") + ": ") && (line < 0 || rolled.ContainsKey(line)))
                {
                    var roller = line < 0 ? null : rolled[line];
                    result.Add(d with { Purpose = $"{Label(roller)} rolled dice", Results = new List<int>() });
                }
                else if (NameHit(d.Purpose)) result.Add(d with { Purpose = "Dice roll" });
                else result.Add(d);
            }
            return result;
        }
    }
}
