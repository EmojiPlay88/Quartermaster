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
/// <item>Whether a value is kept is decided by <see cref="HiddenValues"/>, the one visibility rule. Private properties of pieces of sides
/// not held (and of deleted pieces, whose side is unknown) and of holders owned by a side not held (B7 #74a: a Public value of such a holder
/// is kept, as a Public value of another side's piece is), and every value of holders missing from the pack, are removed from Ledgers and
/// LedgerHistory. A kept formula history entry keeps its working only when its log line is shown unredacted.</item>
/// <item>Owner of a log line: its SideId stamp; else the side of its PieceId (a unit that still exists); else, only for lines that need
/// one, the side of its phase when that phase name belongs to exactly one side; else unknown, which hides. A "{unit name}: " prefix naming
/// another side's unit can only hide.</item>
/// <item>Log lines about a removed or masked piece, on a hidden map, or naming one (see <see cref="MinNameLength"/>) are dropped; lines
/// about a deleted piece (no held side stamp) are redacted or dropped. Ledger/formula/calc lines are linked to their ledger entry (same
/// order, same text) and become "{Side} ledger changed" when the value is hidden or the owner is not held or unknown (a manual line
/// showing a visible value needs no owner).</item>
/// <item>A table read that a non-Public property overrides (<c>CellOverrideFacts.HiddenOverride</c>) shows that property's value only for the
/// piece the formula runs on, so it is hidden exactly as a Private Unit input is: a run on a piece of a side not held is owned by that side
/// (its PieceId), so its lines are redacted and its entries lose their working; a run on the viewer's own piece shows its own value.</item>
/// <item>A log line's failure flag (<see cref="LogEntry.Error"/>) is cleared when the line is redacted and goes with a dropped line; a kept line
/// keeps it (the viewer already holds that line's owner). The run stamp (<see cref="LogEntry.AppliedRun"/>, <see cref="LogEntry.Stage"/>) is
/// cleared the same way, so <c>runs()</c> cannot count another side's runs from the view (it refuses such counts in a view anyway).</item>
/// <item>Dice of a formula with PrivateRolls follow the line's owner; hidden rolls lose their results and read "{Side} rolled dice". A roll
/// carries the id of its formula (B7 #74d): it is private exactly when that formula is, whatever its current name, and an id the pack no
/// longer has is private; only a roll without an id (an older log) follows the formula-name prefix.</item>
/// <item>A Turn line with a side stamp (a checklist tick or an acknowledged warning of that side's phase, B7) is kept only for a viewer
/// holding that side and dropped for any other; an unstamped one (a phase with no side, an older save) is public.</item>
/// <item>Pending runs lose units of other sides (and removed pieces); runs left empty are dropped. Unit-less runs follow the current phase's side.
/// Every run loses its group dice (<see cref="PendingRun.GroupRolls"/>); an ask run's units of other sides lose their group. A sealed answer
/// (<see cref="PendingRun.SealedAnswer"/>, B7 #53) follows the ask-run rule: it stays only on the asked side's run, so another side never
/// holds it before release (once the game is over every view is the full state).</item>
/// <item>Keyed values (<see cref="GameState.Keyed"/>, B6) are filtered exactly as Ledgers (a whole ledger key hidden, else per property). A place
/// property (keyed by hex, kept by the Global holder) has no side: a Public one is shown to every player, any other only in a full view, so a
/// player view drops its values and history entries and redacts its log lines. A formula whose keyed() reads may be hidden (a place that is
/// not Public, a named holder the viewer may not see) never shows its working.</item>
/// <item>A game that is over (B7 #73, <see cref="GameState.GameOver"/>) hides nothing: every player gets the full state
/// (<see cref="GameState.IsPlayerView"/> false, <see cref="GameState.ViewSides"/> null), as a player holding every side and a state
/// without a pack do (<see cref="HiddenValues.SeesEverything"/>). Only information is lifted, never a right to act; undoing the end of
/// the game hides again.</item>
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
        var held = heldSides ?? new HashSet<string>();   // a missing set holds nothing (fail closed), as before B7
        if (pack == null || HiddenValues.SeesEverything(full, pack, held)) return view;
        try
        {
            if (FailForTest) throw new InvalidOperationException("test fault");
            new Filter(full, pack, model, held).Apply(view);
            view.IsPlayerView = true;
            view.ViewSides = held.Where(x => x != null).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            return view;
        }
        catch (Exception)
        {
            return Lockdown(full.DeepClone());   // fail closed: never leak, never crash
        }
    }

    /// <summary>Trait states <see cref="For"/> gives the nameless placeholder of a masked piece (mask trait, then the basic piece).</summary>
    private static readonly string[] PlaceholderStates = { "masked", "" };

    /// <summary>Type string <see cref="For"/> gives the nameless placeholder of a masked piece: the mask trait over a nameless basic piece.</summary>
    internal static string PlaceholderType(ObscurableDef mask) => PieceTypeParser.EncodeState(new[] { mask.RawType, "piece;;;;" });

    /// <summary>
    /// True for the nameless placeholder <see cref="For"/> leaves for another side's masked piece: its ledger values were all removed,
    /// so a missing value there is hidden, not unset. Only meaningful in a player view.
    /// </summary>
    public static bool IsPlaceholder(PieceInstance p) =>
        p.TraitStates is { Count: 2 } t && t[0] == PlaceholderStates[0] && t[1] == PlaceholderStates[1];

    /// <summary>Everything that could hold another side's data removed, with one log line saying why.</summary>
    private static GameState Lockdown(GameState s)
    {
        s.Pieces = new(); s.Stacks = new(); s.Ledgers = new(); s.Keyed = null; s.LedgerHistory = new(); s.DiceHistory = new();
        s.Pending = new(); s.MapOrder = new();
        s.IsPlayerView = true;
        s.Log = new() { new LogEntry(1, LogKind.System, FilterErrorText, null, null, null, DateTimeOffset.UnixEpoch, s.Position?.Turn,
            s.Position == null ? null : s.Turn.Phase) };
        return s;
    }

    private static bool IsLedgerKind(string? kind) => kind is LogKind.Ledger or LogKind.Formula or LogKind.Calc;

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private enum Outcome { Keep, Drop, Redact }

    /// <summary>The keyed() reads of each formula record (property and holder names), parsed once per record (records are replaced on edit).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FormulaDef, IReadOnlyList<(string Property, string? Holder)>> KeyedReadCache = new();

    /// <summary>How many formula records had their keyed() reads parsed on this thread (tests read it).</summary>
    [ThreadStatic] internal static int KeyedReadParses;

    private static IReadOnlyList<(string Property, string? Holder)> KeyedReadsOf(FormulaDef f) =>
        KeyedReadCache.GetValue(f, d =>
        {
            KeyedReadParses++;
            return FormulaVariables.KeyedReads(d).Select(r => (r.Property, r.Holder)).ToList();
        });

    private sealed class Filter
    {
        private readonly RulesPack _pack;
        private readonly IReadOnlySet<string> _held;
        private readonly Dictionary<string, string?> _sideOf = new(StringComparer.Ordinal);
        private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ObscurableDef> _masked = new(StringComparer.Ordinal);
        private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
        private readonly PieceAccess _access;
        private readonly Dictionary<string, Visibility> _visibility = new(StringComparer.Ordinal);
        /// <summary>Property id -> its record (first wins, as <see cref="RulesPack.ById{T}"/>).</summary>
        private readonly Dictionary<string, PropertyDef> _props = new(StringComparer.Ordinal);
        /// <summary>The viewer's sides and the memo of <see cref="PropertyFacts.IsPublic(RulesPack, PropertyDef?)"/>, as
        /// <see cref="HiddenValues"/> takes them (made once per view, so a value costs no allocation).</summary>
        private readonly Func<string, bool> _holds;
        private readonly Func<PropertyDef, bool> _isPublic;
        /// <summary>Each holder's owner in the full state, worked out once per view (B9: <see cref="Ownership.HolderSides"/>).</summary>
        private readonly Func<string, string?> _holderSide;
        /// <summary>Phase name -> the side ids of every phase with that name ("" for a phase with no side).</summary>
        private readonly Dictionary<string, HashSet<string>> _phaseSides = new(StringComparer.Ordinal);
        /// <summary>Piece name -> first side not held among pieces with that name (null when all are held or public).</summary>
        private readonly Dictionary<string, string?> _subjects = new(StringComparer.Ordinal);
        private readonly List<string> _privatePrefixes;
        /// <summary>Formula id -> its <see cref="FormulaDef.PrivateRolls"/> (first record wins, as <see cref="RulesPack.ById{T}"/>); an id not here is private.</summary>
        private readonly Dictionary<string, bool> _privateById = new(StringComparer.Ordinal);
        /// <summary>
        /// Formulas reading a holder value this viewer may not see, using an aggregate that may read hidden pieces or values, or reading a
        /// computed property built from hidden data (<see cref="FormulaVariables.ComputedReadsHidden"/>), or picking a piece whose non-Public
        /// property they read (<see cref="FormulaVariables.PieceBindingsHidden"/>): their working (which shows that value) is never shown.
        /// </summary>
        private readonly HashSet<string> _hiddenInputFormulas = new(StringComparer.Ordinal);
        /// <summary>Place properties (keyed by hex) that are not Public: only a full view has their values (B6).</summary>
        private readonly HashSet<string> _privatePlaces = new(StringComparer.Ordinal);
        private readonly Regex? _names;

        public Filter(GameState full, RulesPack pack, ModuleModel? model, IReadOnlySet<string> held)
        {
            _pack = pack; _held = held;
            _holds = held.Contains;
            _isPublic = p => !PropPrivate(p.Id);
            _holderSide = Ownership.HolderSides(full, pack);
            _access = new PieceAccess(full, pack, model, held);
            foreach (var p in pack.Properties ?? new())   // a computed property reading hidden data counts as Private
                if (p != null)
                {
                    _visibility.TryAdd(p.Id ?? "", PropertyFacts.IsPublic(pack, p) ? Visibility.Public : Visibility.Private);
                    if (p.Id != null) _props.TryAdd(p.Id, p);
                }
            foreach (var p in pack.Properties ?? new())
                if (p?.Id != null && KeyedFacts.IsPlace(p) && PropPrivate(p.Id)) _privatePlaces.Add(p.Id);
            foreach (var ph in pack.Phases ?? new())
            {
                if (ph == null) continue;
                if (!_phaseSides.TryGetValue(ph.Name ?? "", out var set)) _phaseSides[ph.Name ?? ""] = set = new(StringComparer.Ordinal);
                set.Add(string.IsNullOrWhiteSpace(ph.SideId) ? "" : ph.SideId);
            }
            foreach (var f in pack.Formulas ?? new())
                if (f?.Id != null) _privateById.TryAdd(f.Id, f.PrivateRolls);
            _privatePrefixes = (pack.Formulas ?? new()).Where(f => f is { PrivateRolls: true }).Select(f => (f.Name ?? "") + ": ").Distinct().ToList();
            var hasKeyed = (pack.Properties ?? new()).Any(p => p?.KeyedBy != null && KeyedFacts.IsKeyed(p));   // packs without keyed values pay nothing
            var piecesMayHide = PieceAccess.PiecesMayHide(pack, model, held);   // this view may lack pieces or values (removed, on a hidden map, masked)
            foreach (var f in pack.Formulas ?? new())
                if (f?.Id != null && ((f.Bindings ?? new()).Any(b => b is { Kind: BindingKind.Holder }
                        && ValueHidden(LedgerKey.Holder(b.HolderId ?? ""), b.Property, out _))
                    || FormulaVariables.HolderReads(f).Any(r => r.Holder == null || pack.HolderByName(r.Holder) is not { } h
                        || ValueHidden(LedgerKey.Holder(h.Id), pack.PropertyByName(r.Property ?? "")?.Id, out _))
                    || FormulaVariables.AggregateReadsHidden(pack, f, piecesMayHide)
                    || FormulaVariables.ComputedReadsHidden(pack, f)   // a computed value built from hidden data, even on a held piece or the global holder
                    || FormulaVariables.PieceBindingsHidden(pack, f, piecesMayHide).Any()   // a picked piece's non-Public value, or any once a pick may be masked
                    || (hasKeyed && KeyedReadsOf(f).Any(r => KeyedReadHidden(r.Property, r.Holder)))   // B6: a keyed() read the viewer may not see
                    || (_privatePlaces.Count > 0 && (f.Outputs ?? new()).Any(o => o?.Property != null && _privatePlaces.Contains(o.Property)))))   // B6: its working names a hidden place value
                    _hiddenInputFormulas.Add(f.Id);

            foreach (var p in full.Pieces.Values)
            {
                if (p == null) continue;
                var side = _access.SideOf(p);
                _sideOf[p.Id] = side;
                if (MapHidden(p.MapId)) { _removed.Add(p.Id); continue; }
                if (!NotHeld(side)) continue;
                var (hide, mask) = PieceAccess.Masking(p);
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

        private bool NotHeld(string? side) => _access.NotHeld(side);

        private bool NameHit(string? text) => _names != null && !string.IsNullOrEmpty(text) && _names.IsMatch(text);

        private string Label(string? side) =>
            side == null ? Unknown : (_pack.Sides ?? new()).FirstOrDefault(x => x != null && x.Id == side)?.Name is { Length: > 0 } n ? n : side;

        private bool MapHidden(string? mapId) => _access.MapHidden(mapId);

        /// <summary>The side of the phase with this name, only when every phase of that name belongs to the same single side.</summary>
        private string? PhaseSide(string? phaseName) =>
            phaseName != null && _phaseSides.TryGetValue(phaseName, out var set) && set.Count == 1 && set.First() is { Length: > 0 } s ? s : null;

        private bool PropPrivate(string? prop) =>
            prop == null || !_visibility.TryGetValue(prop, out var v) || v != Visibility.Public;

        /// <summary>
        /// True when a keyed(Property, key[, Holder]) read may show the viewer a value it cannot see: an unknown property or holder (fail closed),
        /// a place that is not Public, or a named holder whose values are hidden. A read of the run piece's own value follows the run's line.
        /// </summary>
        private bool KeyedReadHidden(string property, string? holder)
        {
            if (_pack.PropertyByName(property ?? "") is not { } def) return true;
            if (_privatePlaces.Contains(def.Id)) return true;
            if (holder == null) return false;
            return _pack.HolderByName(holder) is not { } h || ValueHidden(LedgerKey.Holder(h.Id), def.Id, out _);
        }

        /// <summary>
        /// True when this ledger value (<paramref name="prop"/> null: the whole key) is hidden from the viewer, as <see cref="HiddenValues"/>
        /// decides; <paramref name="side"/> is its owner when known. A removed or masked piece hides every value; a deleted piece's side is
        /// unknown, so only a Public value of it is kept. An unknown property id counts as the whole key.
        /// </summary>
        private bool ValueHidden(string? key, string? prop, out string? side)
        {
            side = null;
            if (key == null || !LedgerKey.TryParse(key, out var isPiece, out var id)) return true;
            var def = prop != null && _props.TryGetValue(prop, out var d) ? d : null;
            if (isPiece)
            {
                if (_hidden.Contains(id)) { _sideOf.TryGetValue(id, out side); return true; }
                if (!_sideOf.TryGetValue(id, out var ps)) return HiddenValues.UnknownSideValueHidden(_pack, def, _isPublic);   // deleted piece
                if (NotHeld(ps)) side = ps;
                return HiddenValues.PieceValueHidden(_pack, def, ps, _holds, _isPublic);
            }
            if (!HiddenValues.HolderValueHidden(_pack, id, def, _holds, _isPublic, _holderSide)) return false;
            if (id != HolderDef.GlobalId) side = _holderSide(id);   // null: public, or owned by a deleted side (ignored, spec 8a.10)
            return true;
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

        /// <summary>
        /// A roll with a formula id is private exactly when that formula has <see cref="FormulaDef.PrivateRolls"/> (an id the pack no longer has
        /// counts as private: fail closed); a roll without one (an older log) follows the name-prefix rule.
        /// </summary>
        private bool IsPrivateRoll(string? text, string? formulaId) =>
            !string.IsNullOrEmpty(formulaId) ? !_privateById.TryGetValue(formulaId, out var priv) || priv
            : text != null && _privatePrefixes.Any(px => text.StartsWith(px, StringComparison.Ordinal));

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
            if (l.Kind == LogKind.Chat) return (Outcome.Keep, null);   // chat is public: what a player wrote, never game data
            if (l.PieceId != null && _hidden.Contains(l.PieceId)) return (Outcome.Drop, null);
            if (MapHidden(l.MapId) || NameHit(l.Text)) return (Outcome.Drop, null);
            var stamp = string.IsNullOrWhiteSpace(l.SideId) ? null : l.SideId;
            if (l.PieceId != null && !_sideOf.ContainsKey(l.PieceId) && !(stamp != null && _held.Contains(stamp)))
                return Redact(l.Kind, stamp);   // a deleted piece: its side is unknown
            var hasOwner = ExplicitOwner(l, out var owner);

            if (l.Kind == LogKind.Dice)
            {
                if (!IsPrivateRoll(l.Text, l.FormulaId)) return (Outcome.Keep, null);
                if (hasOwner && owner != null) return HeldOrRedact(l.Kind, owner);
                return HeldOrRedact(l.Kind, PhaseSide(l.Phase));
            }
            if (l.Kind == LogKind.Turn && stamp != null && !_held.Contains(stamp)) return (Outcome.Drop, null);   // another side's tick or acknowledge
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
                p.TypeString = PlaceholderType(mask);
                p.TraitStates = new(PlaceholderStates);
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

            // Keyed values (B6): as Ledgers.
            if (v.Keyed != null)
            {
                foreach (var key in v.Keyed.Keys.ToList())
                {
                    var props = v.Keyed[key];
                    if (props == null || (WholeKeyHidden(key) && ValueHidden(key, null, out _))) { v.Keyed.Remove(key); continue; }
                    foreach (var prop in props.Keys.ToList())
                        if (props[prop] == null || ValueHidden(key, prop, out _)) props.Remove(prop);
                    if (props.Count == 0) v.Keyed.Remove(key);
                }
                if (v.Keyed.Count == 0) v.Keyed = null;
            }

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
                kept.Add(e.Working != null && e.Source == "formula" && (!shown || HidesInput(e)) ? e with { Working = null } : e);
            }
            v.LedgerHistory = kept;

            // Dice, then log.
            v.DiceHistory = FilterDice(v.DiceHistory ?? new(), log, outcomes);
            var lines = new List<LogEntry>(log.Count);
            for (var i = 0; i < log.Count; i++)
            {
                var (o, text) = outcomes[i];
                if (o == Outcome.Keep)
                {
                    var line = log[i];
                    if (entryOfLine[i] is { } e && HidesInput(e))
                    {
                        var plain = $"{e.PropertyId}{(e.ValueKey == null ? "" : $"[{e.ValueKey}]")} {Num(e.Before)} → {Num(e.After)}";
                        // Fail closed: when the working is not found verbatim, the whole line is replaced.
                        line = line with
                        {
                            Text = line.Text is { } t && t.Contains(e.Working!, StringComparison.Ordinal)
                                ? t.Replace(e.Working!, plain, StringComparison.Ordinal) : plain,
                        };
                    }
                    else if (entryOfLine[i] == null && line.Kind is LogKind.Calc or LogKind.Formula && line.FormulaId != LogKind.NoWorking
                             && FormulaHidesInput(line.FormulaId))
                        // An output-less run's working line, or an unpaired formula/calc line (legacy lines without a formula id fail closed).
                        line = line with { Text = HiddenWorkingText(line.FormulaId) };
                    lines.Add(line);
                }
                else if (o == Outcome.Redact) lines.Add(log[i] with { Text = text!, PieceId = null, Error = false, AppliedRun = null, Stage = null });   // the flag would tell a failure apart
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
                // An ask run (spec #72) is the asked side's alone, whatever the phase.
                // The host draws every group die (B4): a view never holds them. A unit's group stays only on units of the viewer's sides.
                var plain = r.GroupRolls == null ? r : r with { GroupRolls = null };
                if (!string.IsNullOrEmpty(r.AskSideId))
                {
                    if (!_held.Contains(r.AskSideId)) continue;
                    var all = plain.Units ?? Array.Empty<PendingUnit>();
                    runs.Add(all.Any(u => u?.Group != null && !UnitVisible(u.PieceId))
                        ? plain with { Units = all.Select(u => u?.Group != null && !UnitVisible(u.PieceId) ? u with { Group = null } : u!).ToList() }
                        : plain);
                    continue;
                }
                var units = plain.Units ?? Array.Empty<PendingUnit>();
                if (units.Count == 0) { if (phaseVisible) runs.Add(plain); continue; }
                var mine = units.Where(u => u != null && UnitVisible(u.PieceId)).ToList();
                if (mine.Count == 0) continue;
                runs.Add(mine.Count == units.Count ? plain : plain with { Units = mine });
            }
            v.Pending = runs;
        }

        /// <summary>
        /// True when a working of this formula may show an input the viewer can't see. Fails closed: no id, or a formula no longer in the
        /// pack (its bindings are unknown), counts as hiding.
        /// </summary>
        private bool FormulaHidesInput(string? formulaId) =>
            string.IsNullOrEmpty(formulaId) || _hiddenInputFormulas.Contains(formulaId)
            || !(_pack.Formulas ?? new()).Any(f => f != null && f.Id == formulaId);

        /// <summary>A formula entry whose working may show an input this viewer may not see (the result itself stays visible). A manual
        /// entry's working only names the value it changed (B6: "{Prop}[{key}] {before} → {after}"), which a kept entry shows anyway.</summary>
        private bool HidesInput(LedgerEntry e) => e.Working != null && e.Source != "manual" && FormulaHidesInput(e.FormulaId);

        /// <summary>The text shown in place of a calc line's working that may hold a hidden input.</summary>
        private string HiddenWorkingText(string? formulaId) =>
            ((_pack.Formulas ?? new()).FirstOrDefault(f => f != null && f.Id == formulaId)?.Name is { Length: > 0 } n ? n : "Formula")
            + ": calculated (inputs hidden)";

        /// <summary>
        /// Removed or masked pieces and unknown holders lose every value; other keys, another side's holder too (B7 #74a), are filtered per
        /// property.
        /// </summary>
        private bool WholeKeyHidden(string key)
        {
            if (!LedgerKey.TryParse(key, out var isPiece, out var id)) return true;
            if (isPiece) return _hidden.Contains(id);
            return id != HolderDef.GlobalId && !(_pack.Holders ?? new()).Any(h => h != null && h.Id == id);
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
                    if (log[i]?.Kind == LogKind.Dice && log[i].Text != null && log[i].Text.StartsWith((d.Purpose ?? "") + ": ", StringComparison.Ordinal)
                        && (d.FormulaId == null || log[i].FormulaId == d.FormulaId))
                    {
                        line = i;
                        cursor = i + 1;
                        break;
                    }
                var shown = line >= 0 && outcomes[line].Outcome == Outcome.Keep;
                if (IsPrivateRoll((d.Purpose ?? "") + ": ", d.FormulaId) && !shown)
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
