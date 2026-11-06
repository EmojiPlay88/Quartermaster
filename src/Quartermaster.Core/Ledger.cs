using System.Globalization;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>One change to a ledger value. Source is "formula", "manual" or "seed".</summary>
public sealed record LedgerEntry(long Seq, string Key, string PropertyId, double Before, double After, string Source, string? FormulaId, string? RuleRefId, string? Note, string? Working);

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

    /// <summary>E.g. "−27 Fuel (Fuel Use)".</summary>
    public static string Describe(LedgerEntry e, RulesPack? pack)
    {
        var delta = e.After - e.Before;
        var sign = delta > 0 ? "+" : delta < 0 ? "−" : "";
        var prop = pack?.ById<PropertyDef>(e.PropertyId)?.Name ?? "(deleted property)";
        string who = e.Source switch
        {
            "formula" => (e.FormulaId == null ? null : pack?.ById<FormulaDef>(e.FormulaId)?.Name) ?? "(deleted formula)",
            "manual" => "manual: " + e.Note,
            "seed" => "seed",
            _ => e.Source,
        };
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
        var seeds = new List<LedgerSeed>();
        foreach (var piece in s.Pieces.Values)
        {
            if (!appliesTo(piece)) continue;
            try { SeedPiece(s, pack, piece, model, seeds); }
            catch (Exception ex)
            {
                log?.AddOnce("seed:" + piece.Id, DiagnosticSeverity.Warning, "rules",
                    $"Could not read starting values for piece '{piece.Id}': {ex.Message}");
            }
        }
        return seeds.Count == 0 ? null : new SeedLedger(seeds);
    }

    private static void SeedPiece(GameState s, RulesPack pack, PieceInstance piece, ModuleModel? model, List<LedgerSeed> seeds)
    {
        var key = LedgerKey.Piece(piece.Id);
        var traits = PieceTypeParser.Parse(piece.TypeString, new DiagnosticLog(), null).Traits;
        var local = new List<LedgerSeed>();
        foreach (var prop in pack.Properties)
        {
            if (prop.Scope == PropertyScope.Holders || string.IsNullOrWhiteSpace(prop.SheetField)) continue;
            if (Ledger.Get(s, key, prop.Id) != null) continue;
            if (!string.IsNullOrWhiteSpace(prop.AppliesTo) && !PieceFilter.Matches(prop.AppliesTo, piece, s, model!, pack)) continue;
            for (var i = 0; i < traits.Count; i++)
            {
                if (traits[i] is not PropertySheetDef def
                    || !def.Rows.Any(r => string.Equals(r.Name, prop.SheetField, StringComparison.OrdinalIgnoreCase))) continue;
                var row = PieceActions.ReadSheet(piece, i)
                    .FirstOrDefault(r => string.Equals(r.Name, prop.SheetField, StringComparison.OrdinalIgnoreCase));
                if (row.Name == null) break;
                if (double.TryParse(row.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v))
                    local.Add(new LedgerSeed(key, prop.Id, v));
                break; // first sheet having the row wins
            }
        }
        seeds.AddRange(local); // a piece that fails midway contributes nothing
    }
}
