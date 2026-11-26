using System.Globalization;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Rules.Tables;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>
/// Keyed values (B6): one value per key of a property keyed by a table's rows, by turn or by hex (<see cref="PropertyDef.KeyedBy"/>), kept in
/// <see cref="GameState.Keyed"/>. Reading, key normalisation, place values (hex-keyed values of the Global holder) and the turn-key cleanup.
/// </summary>
public static class KeyedValues
{
    /// <summary>The longest value key, in characters.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>The highest turn number a turn key may have.</summary>
    public const int MaxTurn = 100000;

    /// <summary>Error text of a keyed() read a player view may not answer.</summary>
    public const string HiddenText = "keyed() reads data this player cannot see";

    /// <summary>The stored value of one key; null when none is stored.</summary>
    public static double? Get(GameState s, string ledgerKey, string propertyId, string valueKey) =>
        Values(s, ledgerKey, propertyId) is { } values && valueKey != null && values.TryGetValue(valueKey, out var v) ? v : null;

    /// <summary>The stored value of one key, else the property's Default (as an unset ledger value reads).</summary>
    public static double GetOrDefault(GameState s, string ledgerKey, PropertyDef p, string valueKey) => Get(s, ledgerKey, p.Id, valueKey) ?? p.Default;

    /// <summary>The stored keys of a property at a ledger key, in the order they were first written; empty when none.</summary>
    public static IReadOnlyList<string> Keys(GameState s, string ledgerKey, string propertyId) =>
        Values(s, ledgerKey, propertyId) is { } values ? values.Keys.ToList() : Array.Empty<string>();

    private static OrderedDictionary<string, double>? Values(GameState s, string ledgerKey, string propertyId) =>
        s.Keyed != null && ledgerKey != null && propertyId != null && s.Keyed.TryGetValue(ledgerKey, out var props) && props != null
        && props.TryGetValue(propertyId, out var values) ? values : null;

    /// <summary>
    /// The stored form of a key for a keyed property. Table: the row the key finds (<see cref="TableLookup.Locate"/>), as its normalised row
    /// key. Turn: a whole number from 1 to 100000, as invariant integer text. Hex: the trimmed text; with a module that has a gridded board it
    /// must be a hex of some board, stored as that board's spelling. At most 64 characters. Errors are <see cref="EvalError"/> with the
    /// message tail (<c>no row "{k}" in table "{t}"</c>, <c>the turn must be a whole number of at least 1</c>, <c>no hex named "{k}" on any
    /// board</c>, <c>the key is too long</c>); callers prefix it (<c>keyed(): </c>, <c>The key for {property}: </c>).
    /// </summary>
    public static string NormaliseKey(RulesPack pack, ModuleModel? model, PropertyDef p, Value key)
    {
        var raw = (key.Kind == ValueKind.Text ? key.Text ?? "" : key.Display()).Trim();
        if (raw.Length > MaxKeyLength) throw new EvalError("the key is too long");
        var (kind, tableId) = KeyedFacts.Parse(p.KeyedBy);
        string result;
        switch (kind)
        {
            case KeyKind.Table:
            {
                var table = pack.ById<TableDef>(tableId!);
                var noRow = new EvalError($"no row \"{raw}\" in table \"{table?.Name ?? tableId}\"");
                if (table == null) throw noRow;
                int row;
                // A two-way table is keyed by its rows; Locate needs a column only to pick a cell.
                try { row = TableLookup.Locate(table.Kind == TableKind.TwoD ? table with { Kind = TableKind.OneD } : table, key, null).Row; }
                catch (EvalError) { throw noRow; }
                result = TableLookup.Normalise(table.RowKeys[row]);
                break;
            }
            case KeyKind.Turn:
            {
                double n;
                if (key.Kind == ValueKind.Number) n = key.Number;
                else if (key.Kind != ValueKind.Text || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out n)) n = double.NaN;
                if (!double.IsFinite(n) || n != Math.Floor(n) || n < 1 || n > MaxTurn) throw new EvalError("the turn must be a whole number of at least 1");
                result = ((int)n).ToString(CultureInfo.InvariantCulture);
                break;
            }
            case KeyKind.Hex:
            {
                var noHex = new EvalError($"no hex named \"{raw}\" on any board");
                if (raw.Length == 0) throw noHex;
                if (model != null && HasGrid(model))
                {
                    var hit = BoardCells.Find(model, raw, null) ?? throw noHex;
                    result = hit.Board.Grid!.LocationName(hit.Local).Trim();
                }
                else result = raw;
                break;
            }
            default:
                throw new EvalError($"{p.Name} is not keyed");
        }
        if (result.Length > MaxKeyLength) throw new EvalError("the key is too long");
        return result;
    }

    private static bool HasGrid(ModuleModel model) => model.Maps.Any(m => m.Boards.Any(b => b.Grid != null));

    /// <summary>The place properties (keyed by hex) with a value stored at <paramref name="hex"/> (trimmed; exact spelling first, then
    /// ignoring case), with those values, in pack order. Empty when none.</summary>
    public static IReadOnlyList<(PropertyDef Property, double Value)> PlacesAt(GameState s, RulesPack pack, string hex)
    {
        var list = new List<(PropertyDef, double)>();
        if (s.Keyed == null || pack == null || !s.Keyed.TryGetValue(LedgerKey.Holder(HolderDef.GlobalId), out var props) || props == null) return list;
        var name = (hex ?? "").Trim();
        if (name.Length == 0) return list;
        foreach (var p in pack.Properties)
        {
            if (!KeyedFacts.IsPlace(p) || !props.TryGetValue(p.Id, out var values) || values == null) continue;
            if (values.TryGetValue(name, out var v)) { list.Add((p, v)); continue; }
            foreach (var (k, kv) in values)
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) { list.Add((p, kv)); break; }
        }
        return list;
    }

    /// <summary>A hex's place text: <c>{Prop} {value}</c> per value (with its label, e.g. <c>Ford depth 3 (Flooded)</c>), joined with " · ".
    /// Empty when there are none.</summary>
    public static string PlaceText(RulesPack pack, IReadOnlyList<(PropertyDef Property, double Value)> places) =>
        string.Join(" · ", (places ?? Array.Empty<(PropertyDef, double)>()).Select(x => $"{x.Property.Name} {PropertyLabels.Text(pack, x.Property, x.Value)}"));

    /// <summary>
    /// The command dropping the keys below <paramref name="turn"/> of the turn-keyed properties (<see cref="DropPastTurns"/>): null when no such
    /// property has a stored key below it (so a pack without turn-keyed values executes nothing).
    /// </summary>
    public static DropPastTurns? DropCommand(GameState s, RulesPack pack, int turn)
    {
        if (s?.Keyed == null || pack == null) return null;
        List<string>? ids = null;
        foreach (var p in pack.Properties)
        {
            if (p?.KeyedBy == null || KeyedFacts.Parse(p.KeyedBy).Kind != KeyKind.Turn) continue;
            foreach (var props in s.Keyed.Values)
                if (props != null && props.TryGetValue(p.Id, out var values) && values != null && values.Keys.Any(k => TurnOf(k) is { } t && t < turn))
                {
                    (ids ??= new()).Add(p.Id);
                    break;
                }
        }
        return ids == null ? null : new DropPastTurns(ids, turn);
    }

    /// <summary>The turn number of a stored turn key; null for a key that is not one.</summary>
    internal static int? TurnOf(string key) =>
        int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : null;

    /// <summary>
    /// keyed(Property, key[, Holder]) in any evaluation context. Whose value: the named holder (which must keep the property); else a Pieces
    /// property the piece being evaluated (<paramref name="piece"/>; none: <c>keyed() needs a unit</c>); a Holders property the Global holder
    /// (which must keep it, as a Global binding); a Both property the piece when there is one, else the Global holder. The key is normalised
    /// (<see cref="NormaliseKey"/>, errors prefixed <c>keyed(): </c>); a key never written reads the Default. In a player view a value the view
    /// may lack (a Private place, a non-Public value of another side's piece, a holder of a side not held) is <see cref="HiddenText"/> with
    /// <see cref="EvalError.HiddenData"/>.
    /// </summary>
    internal static double Read(GameState s, RulesPack pack, ModuleModel? model, PieceInstance? piece, string property, Value key, string? holder,
        Lazy<Func<PieceInstance, string?>>? sides)
    {
        PieceFilter.RefuseInStartingValue("keyed");   // a seed never reads another side's or a later state's values
        var name = (property ?? "").Trim();
        if (name.Length == 0) throw new EvalError("keyed() needs a property name");
        var def = pack.PropertyByName(name) ?? throw new EvalError($"Unknown property '{name}'");
        if (!KeyedFacts.IsKeyed(def)) throw new EvalError($"{def.Name} is not keyed");
        string? holderId = null;
        if (holder != null)
        {
            var h = pack.HolderByName(holder) ?? throw new EvalError($"No holder named '{holder}'");
            if (def.Scope == PropertyScope.Pieces) throw new EvalError($"{def.Name} is not a holder property");
            holderId = h.Id;
        }
        else if (def.Scope == PropertyScope.Pieces || (def.Scope == PropertyScope.Both && piece != null))
        {
            if (piece == null) throw new EvalError("keyed() needs a unit");
        }
        else holderId = HolderDef.GlobalId;
        if (holderId != null) FormulaRunner.EnsureKept(pack, holderId, def);
        var ledgerKey = holderId != null ? LedgerKey.Holder(holderId) : LedgerKey.Piece(piece!.Id);

        string valueKey;
        try { valueKey = NormaliseKey(pack, model, def, key); }
        catch (EvalError ex) { throw new EvalError("keyed(): " + ex.Message); }
        if (s.IsPlayerView && HiddenInView(s, pack, def, holderId, holderId == null ? piece : null, sides))
            throw new EvalError(HiddenText) { HiddenData = true };
        return GetOrDefault(s, ledgerKey, def, valueKey);
    }

    /// <summary>
    /// True when <paramref name="view"/> is a player view that may lack the values of <paramref name="p"/> at <paramref name="ledgerKey"/> (a
    /// Private place, a non-Public value of another side's piece or holder: <see cref="HiddenValues"/>), as keyed() decides. A Ledger model shows such a
    /// value as hidden instead of the Default <see cref="KeyedTable.For"/> lists for it. False for a full state.
    /// </summary>
    public static bool HiddenIn(GameState view, RulesPack pack, string ledgerKey, PropertyDef p)
    {
        if (view == null || !view.IsPlayerView) return false;
        return HiddenAt(view, pack, ledgerKey, p, HiddenValues.HeldBy(view));
    }

    /// <summary>
    /// The host's side of <see cref="HiddenIn"/>: true when the player view of a player holding <paramref name="held"/> may lack the values of
    /// <paramref name="p"/> at <paramref name="ledgerKey"/> of the full state <paramref name="full"/> (the same rule as <see cref="PlayerView"/>:
    /// a player holding every side, solo (null), and anyone once the game is over see everything: <see cref="HiddenValues.SeesEverything"/>).
    /// Used to refuse a guest's edit of a value it cannot see.
    /// </summary>
    public static bool HiddenFor(GameState full, RulesPack pack, IReadOnlySet<string>? held, string ledgerKey, PropertyDef p)
    {
        if (full == null || pack == null || HiddenValues.SeesEverything(full, pack, held)) return false;
        return HiddenAt(full, pack, ledgerKey, p, held!.Contains);
    }

    private static bool HiddenAt(GameState s, RulesPack pack, string ledgerKey, PropertyDef p, Func<string, bool> holds)
    {
        if (pack == null || p == null || !LedgerKey.TryParse(ledgerKey, out var isPiece, out var id)) return true;
        if (!isPiece) return HiddenInView(s, pack, p, id, null, null, holds);   // an unknown holder fails closed (HiddenValues)
        return !s.Pieces.TryGetValue(id, out var piece) || piece == null || HiddenInView(s, pack, p, null, piece, null, holds);
    }

    /// <summary>True when a player view may lack this keyed value (see <see cref="Read"/>), as <see cref="HiddenValues"/> decides (the
    /// placeholder of a masked piece lacks every value); <paramref name="holds"/>: whether the viewer holds a side (default: the view's
    /// <see cref="GameState.ViewSides"/>).</summary>
    private static bool HiddenInView(GameState s, RulesPack pack, PropertyDef def, string? holderId, PieceInstance? piece,
        Lazy<Func<PieceInstance, string?>>? sides, Func<string, bool>? holds = null)
    {
        holds ??= HiddenValues.HeldBy(s);
        if (piece != null)
            return PlayerView.IsPlaceholder(piece) || HiddenValues.PieceValueHidden(pack, def, piece, sides ?? PieceFilter.SideMap(s, pack), holds);
        return holderId != null && HiddenValues.HolderValueHidden(pack, holderId, def, holds);
    }

    /// <summary>hex(): the grid location name of the piece being evaluated, as text.</summary>
    internal static Value Hex(PieceInstance? piece, ModuleModel? model)
    {
        PieceFilter.RefuseInStartingValue("hex");   // there is no grid while seeding
        if (piece == null) throw new EvalError("hex() needs a unit");
        if (model == null) throw new EvalError("hex() is not available here");
        return Value.Of(PieceInfo.Location(piece, model)
            ?? throw new EvalError($"{(PieceInfo.Name(piece)?.Trim() is { Length: > 0 } n ? n : "piece")} is not in a hex"));
    }

    /// <summary>
    /// distance(a, b): cells between two hex names. Each name is found on the board of <paramref name="piece"/> first, then on every board in
    /// module order (<see cref="BoardCells.Find"/>); the second is looked for on the first's board first, and the first again on the second's
    /// board when they differ, so two names a board shares are measured there.
    /// </summary>
    internal static double Distance(ModuleModel? model, PieceInstance? piece, string a, string b)
    {
        PieceFilter.RefuseInStartingValue("distance");
        if (model == null) throw new EvalError("distance() is not available here");
        var an = (a ?? "").Trim();
        var bn = (b ?? "").Trim();
        var prefer = piece == null ? null : PieceInfo.BoardOf(piece, model);
        var ha = BoardCells.Find(model, an, prefer) ?? throw new EvalError($"distance(): no hex named \"{an}\" on any board");
        var hb = BoardCells.Find(model, bn, ha.Board) ?? throw new EvalError($"distance(): no hex named \"{bn}\" on any board");
        if (!ReferenceEquals(ha.Board, hb.Board))
        {
            if (BoardCells.Find(model, an, hb.Board) is { } again && ReferenceEquals(again.Board, hb.Board)) ha = again;
            else throw new EvalError($"distance(): \"{an}\" and \"{bn}\" are not on the same board");
        }
        return BoardCells.Cells(ha.Board.Grid, ha.Local, hb.Local) ?? throw new EvalError("distance() needs a hex or square grid");
    }

    /// <summary>The error of a read of a keyed property by its bare name (a binding, a filter, holder()).</summary>
    internal static EvalError BareRead(PropertyDef def) => new($"\"{def.Name}\" is keyed; read it with keyed()");
}
