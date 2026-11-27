using System.Globalization;
using System.Text.Json.Serialization;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>One change to a ledger value. Source is "formula", "manual" or "seed".</summary>
public sealed record LedgerEntry(long Seq, string Key, string PropertyId, double Before, double After, string Source, string? FormulaId, string? RuleRefId, string? Note, string? Working, int? Turn = null, string? Phase = null)
{
    /// <summary>The value key of a keyed property's change (B6, <see cref="LedgerDelta.ValueKey"/>); null for an unkeyed value. Omitted when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValueKey { get; init; }
}

public sealed record DiceEntry(long Seq, string Purpose, int Count, int Sides, IReadOnlyList<int> Results)
{
    /// <summary>The formula whose run rolled these dice (B7, see <see cref="RollDice.FormulaId"/>); null for a dice-tool roll and in an older save. Omitted when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FormulaId { get; init; }
}

/// <summary>Ledger keys: "p:{pieceId}" for pieces, "h:{holderId}" for holders.</summary>
public static class LedgerKey
{
    public static string Piece(string pieceId) => "p:" + pieceId;
    public static string Holder(string holderId) => "h:" + holderId;

    public static bool TryParse(string key, out bool isPiece, out string id)
    {
        isPiece = false; id = "";
        if (key == null || key.Length < 2) return false;
        if (key.StartsWith("p:", StringComparison.Ordinal)) isPiece = true;
        else if (!key.StartsWith("h:", StringComparison.Ordinal)) return false;
        id = key.Substring(2);
        return true;
    }
}

public static class Ledger
{
    public static double? Get(GameState s, string key, string propertyId) =>
        s.Ledgers.TryGetValue(key, out var inner) && inner.TryGetValue(propertyId, out var v) ? v : null;

    public static double GetOrDefault(GameState s, string key, PropertyDef p) => Get(s, key, p.Id) ?? p.Default;

    /// <summary>The changes of an unkeyed value (entries without a <see cref="LedgerEntry.ValueKey"/>).</summary>
    public static IReadOnlyList<LedgerEntry> History(GameState s, string key, string propertyId) =>
        s.LedgerHistory.Where(e => e.Key == key && e.PropertyId == propertyId && e.ValueKey == null).ToList();

    /// <summary>The changes of one keyed value (B6): entries whose <see cref="LedgerEntry.ValueKey"/> is <paramref name="valueKey"/> (ordinal).</summary>
    public static IReadOnlyList<LedgerEntry> History(GameState s, string key, string propertyId, string valueKey) =>
        s.LedgerHistory.Where(e => e.Key == key && e.PropertyId == propertyId && string.Equals(e.ValueKey, valueKey, StringComparison.Ordinal)).ToList();

    /// <summary>Where a change came from: "manual: note", "seeded", "Formula · Rule", ...</summary>
    public static string SourceLabel(LedgerEntry e, RulesPack? pack)
    {
        switch (e.Source)
        {
            case "manual": return string.IsNullOrEmpty(e.Note) ? "manual" : "manual: " + e.Note;
            case "seed": return "seeded";
            case "formula":
                var name = (e.FormulaId == null ? null : pack?.ById<FormulaDef>(e.FormulaId)?.Name) ?? "deleted formula";
                var rule = e.RuleRefId == null ? null : pack?.ById<RuleRef>(e.RuleRefId)?.Label;
                return string.IsNullOrEmpty(rule) ? name : name + " · " + rule;
            default: return e.Source;
        }
    }

    /// <summary>E.g. "−27 Fuel (Fuel Use)".</summary>
    public static string Describe(LedgerEntry e, RulesPack? pack)
    {
        var delta = e.After - e.Before;
        var sign = delta > 0 ? "+" : delta < 0 ? "−" : "";
        var prop = pack?.ById<PropertyDef>(e.PropertyId)?.Name ?? "(deleted property)";
        var who = e.Source == "seed" ? "seed" : SourceLabel(e, pack);
        return $"{sign}{Math.Abs(delta).ToString("0.##", CultureInfo.InvariantCulture)} {prop} ({who})";
    }
}

public static class LedgerSeeder
{
    /// <summary>
    /// Seeds for piece-scoped properties bound to a property-sheet row. Null when nothing to seed.
    /// A property's own AppliesTo filter is honoured. Malformed pieces are skipped with a "rules" warning when a log is given.
    /// </summary>
    public static SeedLedger? BuildSeeds(GameState s, RulesPack pack, Func<PieceInstance, bool> appliesTo, ModuleModel? model = null, DiagnosticLog? log = null)
    {
        // Nothing to seed: skip every piece (workbench commits re-seed often and a game can hold thousands of pieces).
        if (!pack.Properties.Any(Seeded)) return null;
        var seeds = new List<LedgerSeed>();
        foreach (var piece in s.Pieces.Values)
        {
            if (!appliesTo(piece)) continue;
            try { SeedPiece(s, pack, piece, model, seeds, log); }
            catch (Exception ex)
            {
                log?.AddOnce("seed:" + piece.Id, DiagnosticSeverity.Warning, "rules",
                    $"Could not read starting values for piece '{piece.Id}': {ex.Message}");
            }
        }
        return seeds.Count == 0 ? null : new SeedLedger(seeds);
    }

    /// <summary>
    /// Seeds one piece. Sheet values are read first; properties without an applies-to are accepted, then each applies-to is checked in
    /// pack order against the piece's ledger plus the values accepted so far, so it reads a sibling's sheet value, not its Default.
    /// Then, in pack order, each property with a starting value expression (<see cref="PropertyDef.DefaultExpr"/>) and no sheet value whose
    /// applies-to accepts the piece gets the expression's value, worked out with the values accepted so far (a sibling's sheet value, an
    /// earlier starting value). An error or a result that is not a number seeds the Default, with a "rules" warning once per property, so
    /// each piece is worked out once. Seeds keep pack order.
    /// </summary>
    private static void SeedPiece(GameState s, RulesPack pack, PieceInstance piece, ModuleModel? model, List<LedgerSeed> seeds, DiagnosticLog? log)
    {
        var key = LedgerKey.Piece(piece.Id);
        var traits = PieceInfo.TypeOf(piece).Traits;
        var candidates = new List<(PropertyDef Prop, double? Value)>();
        foreach (var prop in pack.Properties)
        {
            if (!Seeded(prop)) continue;
            if (Ledger.Get(s, key, prop.Id) != null) continue;
            if (!string.IsNullOrWhiteSpace(prop.AppliesTo) && model == null)
            {
                log?.AddOnce("seed-nomodel:" + prop.Id, DiagnosticSeverity.Warning, "rules",
                    $"Property '{prop.Name}' has an 'applies to' filter that cannot be checked here; it was not seeded.");
                continue;
            }
            candidates.Add((prop, string.IsNullOrWhiteSpace(prop.SheetField) ? null : SheetValue(piece, traits, prop.SheetField)));
        }
        var accepted = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (prop, v) in candidates)
            if (v != null && string.IsNullOrWhiteSpace(prop.AppliesTo)) accepted[prop.Id] = v.Value;
        foreach (var (prop, v) in candidates)
            if (v != null && !string.IsNullOrWhiteSpace(prop.AppliesTo)
                && PieceFilter.MatchesWithPending(prop.AppliesTo, piece, s, pack, accepted)) accepted[prop.Id] = v.Value;
        foreach (var (prop, v) in candidates)
            if (v == null && !string.IsNullOrWhiteSpace(prop.DefaultExpr)
                && PieceFilter.MatchesWithPending(prop.AppliesTo, piece, s, pack, accepted))
                accepted[prop.Id] = StartingValue(s, pack, piece, prop, accepted, log);
        foreach (var (prop, _) in candidates)
            if (accepted.TryGetValue(prop.Id, out var v)) seeds.Add(new LedgerSeed(key, prop.Id, v));
        // a piece that fails midway contributes nothing (nothing is added before the last loop)
    }

    /// <summary>
    /// Seeds that copy the source piece's stored values to its clone (the Clone command, spec #20): piece properties (Pieces or Both) that
    /// are not computed and whose <see cref="PropertyDef.CloneAs"/> is Copy, in pack order. Null when there is nothing to copy, either piece
    /// is missing, or the clone's side worked out with the copied values (and the normal seeding that follows them) differs from the source's side; then nothing is copied (so another
    /// side's hidden values never move to a counter a different side sees) and a "rules" warning is added to <paramref name="log"/>.
    /// The clone's other properties get the normal seeding afterwards. <paramref name="model"/> is not read today (sides need no module);
    /// it keeps the shape of <see cref="BuildSeeds"/>. Never throws.
    /// </summary>
    public static SeedLedger? BuildCloneSeeds(GameState s, RulesPack pack, ModuleModel? model, string newPieceId, string sourcePieceId, DiagnosticLog? log = null)
    {
        if (s == null || pack == null || newPieceId == null || sourcePieceId == null || newPieceId == sourcePieceId) return null;
        if (!s.Pieces.TryGetValue(sourcePieceId, out var source) || !s.Pieces.ContainsKey(newPieceId)) return null;
        try
        {
            var from = LedgerKey.Piece(sourcePieceId);
            var to = LedgerKey.Piece(newPieceId);
            var seeds = new List<LedgerSeed>();
            foreach (var prop in pack.Properties)
            {
                if (prop == null || prop.Scope == PropertyScope.Holders || PropertyFacts.IsComputed(prop) || prop.CloneAs != CloneValue.Copy
                    || KeyedFacts.IsKeyed(prop)) continue;   // a clone copies no keyed value
                if (Ledger.Get(s, from, prop.Id) is { } v && double.IsFinite(v)) seeds.Add(new LedgerSeed(to, prop.Id, v));
            }
            if (seeds.Count == 0) return null;
            var result = new SeedLedger(seeds, sourcePieceId);

            var probe = s.DeepClone();
            probe.Apply(result);
            // The normal seeding that follows (sheet values of Default properties) can change the side too: judge the clone after it.
            if (BuildSeeds(probe, pack, p => p.Id == newPieceId, model) is { } normal) probe.Apply(normal);
            var sourceSide = Ownership.PieceSides(s, pack)(source);
            var cloneSide = Ownership.PieceSides(probe, pack)(probe.Pieces[newPieceId]);
            if (sourceSide == cloneSide) return result;
            log?.Add(DiagnosticSeverity.Warning, "rules", $"Clone of \"{PieceInfo.Name(source)}\" belongs to another side, so its values were not copied");
            return null;
        }
        catch (Exception ex)
        {
            log?.Add(DiagnosticSeverity.Warning, "rules", $"Could not copy the values of piece '{sourcePieceId}': {ex.Message}");
            return null;
        }
    }

    /// <summary>A stored piece property bound to a sheet row or with a starting value expression (a computed property is never seeded: its
    /// Sheet field and starting value are ignored).</summary>
    private static bool Seeded(PropertyDef p) =>
        p.Scope != PropertyScope.Holders && (!string.IsNullOrWhiteSpace(p.SheetField) || !string.IsNullOrWhiteSpace(p.DefaultExpr))
        && !PropertyFacts.IsComputed(p) && !KeyedFacts.IsKeyed(p);   // a keyed property is never seeded (it has no single value)

    /// <summary>Parsed starting value per property record (records are replaced on edit, so this never goes stale).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PropertyDef, ParsedExpr> ExprCache = new();

    private sealed class ParsedExpr(Quartermaster.Rules.Expr.Expr? expr, string? error)
    {
        public Quartermaster.Rules.Expr.Expr? Expr { get; } = expr;
        public string? Error { get; } = error;
    }

    /// <summary>The starting value expression's number on this piece; on any error (or a result that is not a finite number) the Default, with a
    /// warning once per property.</summary>
    private static double StartingValue(GameState s, RulesPack pack, PieceInstance piece, PropertyDef prop, IReadOnlyDictionary<string, double> pending,
        DiagnosticLog? log)
    {
        string error;
        try
        {
            var parsed = ExprCache.GetValue(prop, d =>
            {
                try { return new ParsedExpr(Quartermaster.Rules.Expr.Parser.Parse(d.DefaultExpr!), null); }
                catch (Exception ex) { return new ParsedExpr(null, ex.Message); }
            });
            if (parsed.Expr == null) error = parsed.Error ?? "the starting value expression does not parse";
            else
            {
                var v = PieceFilter.EvaluateWithPending(parsed.Expr, piece, s, pack, pending);
                if (v.Kind == Quartermaster.Rules.Expr.ValueKind.Number && double.IsFinite(v.Number)) return v.Number;
                error = "the starting value is not a number";
            }
        }
        catch (Exception ex) { error = ex.Message; }
        log?.AddOnce("seedexpr:" + prop.Id, DiagnosticSeverity.Warning, "rules",
            $"Starting value of \"{prop.Name}\" failed on \"{PieceInfo.Name(piece)}\": {error}; it starts at {prop.Default.ToString("0.######", CultureInfo.InvariantCulture)}");
        return prop.Default;
    }

    /// <summary>The finite number in the first property sheet having this row, else null.</summary>
    private static double? SheetValue(PieceInstance piece, IReadOnlyList<TraitDef> traits, string field)
    {
        for (var i = 0; i < traits.Count; i++)
        {
            if (traits[i] is not PropertySheetDef def
                || !def.Rows.Any(r => string.Equals(r.Name, field, StringComparison.OrdinalIgnoreCase))) continue;
            var row = PieceActions.ReadSheet(piece, i)
                .FirstOrDefault(r => string.Equals(r.Name, field, StringComparison.OrdinalIgnoreCase));
            if (row.Name == null) return null;
            return double.TryParse(row.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
        }
        return null;
    }
}
