using System.Globalization;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>One change to a ledger value. Source is "formula", "manual" or "seed".</summary>
public sealed record LedgerEntry(long Seq, string Key, string PropertyId, double Before, double After, string Source, string? FormulaId, string? RuleRefId, string? Note, string? Working, int? Turn = null, string? Phase = null);

public sealed record DiceEntry(long Seq, string Purpose, int Count, int Sides, IReadOnlyList<int> Results);

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

    public static IReadOnlyList<LedgerEntry> History(GameState s, string key, string propertyId) =>
        s.LedgerHistory.Where(e => e.Key == key && e.PropertyId == propertyId).ToList();

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
        if (!pack.Properties.Any(p => p.Scope != PropertyScope.Holders && !string.IsNullOrWhiteSpace(p.SheetField))) return null;
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
    /// Seeds keep pack order.
    /// </summary>
    private static void SeedPiece(GameState s, RulesPack pack, PieceInstance piece, ModuleModel? model, List<LedgerSeed> seeds, DiagnosticLog? log)
    {
        var key = LedgerKey.Piece(piece.Id);
        var traits = PieceInfo.TypeOf(piece).Traits;
        var candidates = new List<(PropertyDef Prop, double? Value)>();
        foreach (var prop in pack.Properties)
        {
            if (prop.Scope == PropertyScope.Holders || string.IsNullOrWhiteSpace(prop.SheetField)) continue;
            if (Ledger.Get(s, key, prop.Id) != null) continue;
            if (!string.IsNullOrWhiteSpace(prop.AppliesTo) && model == null)
            {
                log?.AddOnce("seed-nomodel:" + prop.Id, DiagnosticSeverity.Warning, "rules",
                    $"Property '{prop.Name}' has an 'applies to' filter that cannot be checked here; it was not seeded.");
                continue;
            }
            candidates.Add((prop, SheetValue(piece, traits, prop.SheetField!)));
        }
        var accepted = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (prop, v) in candidates)
            if (v != null && string.IsNullOrWhiteSpace(prop.AppliesTo)) accepted[prop.Id] = v.Value;
        foreach (var (prop, v) in candidates)
            if (v != null && !string.IsNullOrWhiteSpace(prop.AppliesTo)
                && PieceFilter.MatchesWithPending(prop.AppliesTo, piece, s, pack, accepted)) accepted[prop.Id] = v.Value;
        foreach (var (prop, _) in candidates)
            if (accepted.TryGetValue(prop.Id, out var v)) seeds.Add(new LedgerSeed(key, prop.Id, v));
        // a piece that fails midway contributes nothing (nothing is added before the last loop)
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
