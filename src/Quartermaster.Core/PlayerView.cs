using System.Globalization;
using System.Text.RegularExpressions;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>
/// What one player may see (spec 3.3): a deep-cloned game state with other sides' hidden information removed. Pure; the input is
/// never changed. Attribution is explicit; heuristics may only hide, never reveal; anything unattributable is hidden. Rules:
/// <list type="bullet">
/// <item>Pieces on a map owned by a side not held are removed, with their stacks, ledgers, history and log lines; the map leaves MapOrder.</item>
/// <item>A piece of a side not held with an active Hideable is removed; with an active Obscurable it becomes a nameless placeholder
/// carrying only that mask trait (drawn as the mask), and its ledgers, history and log lines are removed.</item>
/// <item>Private properties of pieces of sides not held (and of deleted pieces, whose side is unknown), and every value of holders owned
/// by a side not held (or of holders missing from the pack), are removed from Ledgers and LedgerHistory. A kept formula history entry
/// keeps its working only when its log line is shown unredacted.</item>
/// <item>Owner of a log line: its SideId stamp; else the side of its PieceId (a unit that still exists); else, only for lines that need
/// one, the side of its phase when that phase name belongs to exactly one side; else unknown, which hides. A "{unit name}: " prefix naming
/// another side's unit can only hide.</item>
/// <item>Log lines about a removed or masked piece, on a hidden map, or naming one (see <see cref="MinNameLength"/>) are dropped; lines
/// about a deleted piece (no held side stamp) are redacted or dropped. Ledger/formula/calc lines are linked to their ledger entry (same
/// order, same text) and become "{Side} ledger changed" when the value is hidden or the owner is not held or unknown (a manual line
/// showing a visible value needs no owner).</item>
/// <item>Dice of a formula with PrivateRolls follow the line's owner; hidden rolls lose their results and read "{Side} rolled dice".</item>
/// <item>Pending runs lose units of other sides (and removed pieces); runs left empty are dropped. Unit-less runs follow the current phase's side.</item>
/// <item>If filtering fails, the view is emptied (fail closed) and its log holds one System line, <see cref="FilterErrorText"/>.</item>
/// </list>
/// </summary>
public static class PlayerView
{
    /// <summary>Log text of the only line left when filtering failed and everything was hidden.</summary>
    public const string FilterErrorText = "View hidden: filter error";

    /// <summary>
    /// Name rule for log redaction: names of removed and masked pieces, trimmed, at least this long, matched case-insensitively as whole
    /// words (no letter, digit or underscore either side). A name also carried by a visible piece is not used (it reveals nothing).
    /// </summary>
    private const int MinNameLength = 2;

    private const string Unknown = "Another side";

    /// <summary>Test hook: makes filtering throw on this thread.</summary>
    [ThreadStatic] internal static bool FailForTest;

    public static GameState For(GameState full, RulesPack? pack, ModuleModel model, IReadOnlySet<string> heldSides)
    {
        var view = full.DeepClone();
        if (pack == null) return view;
        var held = heldSides ?? new HashSet<string>();
        if ((pack.Sides ?? new List<SideDef>()).All(x => x == null || held.Contains(x.Id))) return view;
        try
        {
            if (FailForTest) throw new InvalidOperationException("test fault");
            new Filter(full, pack, model, held).Apply(view);
            return view;
        }
        catch (Exception)
        {
            return Lockdown(full.DeepClone());   // fail closed: never leak, never crash
        }
    }

    /// <summary>Everything that could hold another side's data removed, with one log line saying why.</summary>
    private static GameState Lockdown(GameState s)
    {
        s.Pieces = new(); s.Stacks = new(); s.Ledgers = new(); s.LedgerHistory = new(); s.DiceHistory = new();
        s.Pending = new(); s.MapOrder = new();
        s.Log = new() { new LogEntry(1, LogKind.System, FilterErrorText, null, null, null, DateTimeOffset.UnixEpoch, s.Position?.Turn,
            s.Position == null ? null : s.Turn.Phase) };
        return s;
    }

    private static bool IsLedgerKind(string? kind) => kind is LogKind.Ledger or LogKind.Formula or LogKind.Calc;

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private enum Outcome { Keep, Drop, Redact }

    private sealed class Filter
    {
        private readonly RulesPack _pack;
        private readonly ModuleModel? _model;
        private readonly IReadOnlySet<string> _held;
        private readonly Dictionary<string, string?> _sideOf = new(StringComparer.Ordinal);
        private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ObscurableDef> _masked = new(StringComparer.Ordinal);
        private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _mapHidden = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Visibility> _visibility = new(StringComparer.Ordinal);
        /// <summary>Phase name -> the side ids of every phase with that name ("" for a phase with no side).</summary>
        private readonly Dictionary<string, HashSet<string>> _phaseSides = new(StringComparer.Ordinal);
        /// <summary>Piece name -> first side not held among pieces with that name (null when all are held or public).</summary>
        private readonly Dictionary<string, string?> _subjects = new(StringComparer.Ordinal);
        private readonly List<string> _privatePrefixes;
        private readonly Regex? _names;

        public Filter(GameState full, RulesPack pack, ModuleModel? model, IReadOnlySet<string> held)
        {
            _pack = pack; _model = model; _held = held;
            foreach (var p in pack.Properties ?? new()) if (p != null) _visibility.TryAdd(p.Id ?? "", p.Visibility);
            foreach (var ph in pack.Phases ?? new())
            {
                if (ph == null) continue;
                if (!_phaseSides.TryGetValue(ph.Name ?? "", out var set)) _phaseSides[ph.Name ?? ""] = set = new(StringComparer.Ordinal);
                set.Add(string.IsNullOrWhiteSpace(ph.SideId) ? "" : ph.SideId);
            }
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
                if (_hidden.Contains(p.Id)) { if (trimmed.Length >= MinNameLength) hiddenNames.Add(trimmed); }
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

        /// <summary>The side of the phase with this name, only when every phase of that name belongs to the same single side.</summary>
        private string? PhaseSide(string? phaseName) =>
            phaseName != null && _phaseSides.TryGetValue(phaseName, out var set) && set.Count == 1 && set.First() is { Length: > 0 } s ? s : null;

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

        /// <summary>A "{unit name}: " prefix naming another side's piece (hides only; never reveals).</summary>
        private string? SubjectSide(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            for (var idx = text.IndexOf(": ", StringComparison.Ordinal); idx > 0; idx = text.IndexOf(": ", idx + 1, StringComparison.Ordinal))
                if (_subjects.TryGetValue(text.Substring(0, idx), out var side) && side != null) return side;
            return null;
        }

        /// <summary>
        /// The explicit owner: the SideId stamp, else the side of an existing PieceId (null side = a public unit). With both a stamp and a
        /// unit of a known side, the line belongs to both: the owner is whichever of them is not held (fail closed).
        /// </summary>
        private bool ExplicitOwner(LogEntry l, out string? side)
        {
            side = null;
            string? pieceSide = null;
            var hasPiece = l.PieceId != null && _sideOf.TryGetValue(l.PieceId, out pieceSide);
            if (!string.IsNullOrWhiteSpace(l.SideId))
            {
                side = pieceSide != null && NotHeld(pieceSide) ? pieceSide : l.SideId;
                return true;
            }
            side = pieceSide;
            return hasPiece;
        }

        private bool IsPrivateRoll(string? text) =>
            text != null && _privatePrefixes.Any(px => text.StartsWith(px, StringComparison.Ordinal));

        private (Outcome, string?) Redact(string kind, string? side) =>
            kind == LogKind.Dice ? (Outcome.Redact, $"{Label(side)} rolled dice")
            : IsLedgerKind(kind) ? (Outcome.Redact, $"{Label(side)} ledger changed")
            : (Outcome.Drop, null);

        /// <summary>Hidden-unless-held: shown only for a side the viewer holds; null (unknown) hides.</summary>
        private (Outcome, string?) HeldOrRedact(string kind, string? side) =>
            side != null && _held.Contains(side) ? (Outcome.Keep, null) : Redact(kind, side);

        private (Outcome Outcome, string? Text) Decide(LogEntry? l, LedgerEntry? e)
        {
            if (l == null) return (Outcome.Drop, null);
            if (l.PieceId != null && _hidden.Contains(l.PieceId)) return (Outcome.Drop, null);
            if (MapHidden(l.MapId) || NameHit(l.Text)) return (Outcome.Drop, null);
            var stamp = string.IsNullOrWhiteSpace(l.SideId) ? null : l.SideId;
            if (l.PieceId != null && !_sideOf.ContainsKey(l.PieceId) && !(stamp != null && _held.Contains(stamp)))
                return Redact(l.Kind, stamp);   // a deleted piece: its side is unknown
            var hasOwner = ExplicitOwner(l, out var owner);

            if (l.Kind == LogKind.Dice)
            {
                if (!IsPrivateRoll(l.Text)) return (Outcome.Keep, null);
                if (hasOwner && owner != null) return HeldOrRedact(l.Kind, owner);
                return HeldOrRedact(l.Kind, PhaseSide(l.Phase));
            }
            if (!IsLedgerKind(l.Kind)) return (Outcome.Keep, null);

            if (e != null && ValueHidden(e.Key, e.PropertyId, out var vs)) return Redact(l.Kind, vs ?? owner);
            var calc = l.Kind is LogKind.Formula or LogKind.Calc;
            if (hasOwner)
            {
                if (owner == null || _held.Contains(owner)) return (Outcome.Keep, null);   // own or public unit
                return calc || e == null ? Redact(l.Kind, owner) : (Outcome.Keep, null);   // manual edit of a visible value
            }
            if (SubjectSide(l.Text) is { } subject) return Redact(l.Kind, subject);
            if (!calc && e != null) return (Outcome.Keep, null);   // a manual line showing a visible value
            return HeldOrRedact(l.Kind, PhaseSide(l.Phase));
        }

        public void Apply(GameState v)
        {
            var log = v.Log ?? new();
            var history = v.LedgerHistory ?? new();
            var (entryOfLine, lineOfEntry) = PairLedgerLines(log, history);
            var outcomes = new (Outcome Outcome, string? Text)[log.Count];
            for (var i = 0; i < log.Count; i++) outcomes[i] = Decide(log[i], entryOfLine[i]);

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
            var kept = new List<LedgerEntry>(history.Count);
            for (var k = 0; k < history.Count; k++)
            {
                var e = history[k];
                if (e == null || ValueHidden(e.Key, e.PropertyId, out _) || NameHit(e.Note) || NameHit(e.Working)) continue;
                var shown = lineOfEntry[k] >= 0 && outcomes[lineOfEntry[k]].Outcome == Outcome.Keep;
                kept.Add(e.Working != null && e.Source == "formula" && !shown ? e with { Working = null } : e);
            }
            v.LedgerHistory = kept;

            // Dice, then log.
            v.DiceHistory = FilterDice(v.DiceHistory ?? new(), log, outcomes);
            var lines = new List<LogEntry>(log.Count);
            for (var i = 0; i < log.Count; i++)
            {
                var (o, text) = outcomes[i];
                if (o == Outcome.Keep) lines.Add(log[i]);
                else if (o == Outcome.Redact) lines.Add(log[i] with { Text = text!, PieceId = null });
            }
            v.Log = lines;

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

        /// <summary>
        /// Links each ledger history entry (except seeds, which log nothing) to the log line its LedgerDelta wrote: the next unclaimed
        /// ledger/formula/calc line whose text contains the entry's working or default text, with the same piece id for a piece key.
        /// </summary>
        private static (LedgerEntry?[] EntryOfLine, int[] LineOfEntry) PairLedgerLines(List<LogEntry> log, List<LedgerEntry> history)
        {
            var entryOfLine = new LedgerEntry?[log.Count];
            var lineOfEntry = new int[history.Count];
            var cursor = 0;
            for (var k = 0; k < history.Count; k++)
            {
                lineOfEntry[k] = -1;
                var e = history[k];
                if (e == null || e.Source == "seed" || e.Key == null || !LedgerKey.TryParse(e.Key, out var isPiece, out var id)) continue;
                var core = e.Working ?? $"{e.PropertyId} {Num(e.Before)} → {Num(e.After)}";
                for (var i = cursor; i < log.Count; i++)
                {
                    var l = log[i];
                    if (l == null || entryOfLine[i] != null || !IsLedgerKind(l.Kind)) continue;
                    if ((isPiece && l.PieceId != id) || l.Text == null || !l.Text.Contains(core, StringComparison.Ordinal)) continue;
                    entryOfLine[i] = e;
                    lineOfEntry[k] = i;
                    cursor = i + 1;
                    break;
                }
            }
            return (entryOfLine, lineOfEntry);
        }

        /// <summary>
        /// Each dice entry is linked to the next dice log line starting with its purpose. A private roll is shown only when that line is
        /// shown unredacted; otherwise it loses its results.
        /// </summary>
        private List<DiceEntry> FilterDice(List<DiceEntry> dice, List<LogEntry> log, (Outcome Outcome, string? Text)[] outcomes)
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
                var shown = line >= 0 && outcomes[line].Outcome == Outcome.Keep;
                if (IsPrivateRoll((d.Purpose ?? "") + ": ") && !shown)
                {
                    var text = line < 0 ? $"{Unknown} rolled dice"
                        : outcomes[line].Outcome == Outcome.Redact ? outcomes[line].Text!
                        : $"{Label(ExplicitOwner(log[line], out var owner) ? owner : null)} rolled dice";   // line dropped
                    result.Add(d with { Purpose = text, Results = new List<int>() });
                }
                else if (!shown || NameHit(d.Purpose)) result.Add(d with { Purpose = "Dice roll" });
                else result.Add(d);
            }
            return result;
        }
    }
}
