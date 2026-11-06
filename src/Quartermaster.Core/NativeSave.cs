using System.Text.Json;

namespace Quartermaster.Core;

/// <summary>Native .qsave format: JSON { format, moduleSha256, rulesSha256?, snapshot, log }.</summary>
public static class NativeSave
{
    public const int FormatVersion = 3;
    const int MinFormat = 1;

    sealed class SaveFile
    {
        public int Format { get; set; }
        public string ModuleSha256 { get; set; } = "";
        public string? RulesSha256 { get; set; }
        public GameState Snapshot { get; set; } = new();
        public List<GameCommand> Log { get; set; } = new();
    }

    public static void Write(string path, GameSession s)
    {
        var file = new SaveFile
        {
            Format = FormatVersion, ModuleSha256 = s.ModuleSha256, RulesSha256 = s.RulesSha256, Snapshot = s.State, Log = s.Log.ToList(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(file, QuartermasterJson.Options));
    }

    private static void Validate(SaveFile f)
    {
        static void Need(bool ok, string what) { if (!ok) throw new InvalidDataException("Malformed save file: " + what + " is missing."); }
        Need(f.Snapshot != null, "snapshot");
        Need(f.Log != null, "log");
        Need(f.ModuleSha256 != null, "module hash");
        var snap = f.Snapshot!;
        Need(snap.Pieces != null && snap.Stacks != null && snap.MapOrder != null, "snapshot content");
        foreach (var p in snap.Pieces!.Values)
            Need(p != null && p.TraitStates != null && p.Trail != null && p.TypeString != null && p.Id != null && p.MapId != null, "piece data");
        Need(snap.Ledgers != null && snap.LedgerHistory != null && snap.DiceHistory != null, "ledger data");
        foreach (var l in snap.Ledgers!.Values) Need(l != null, "ledger data");
        Need(snap.LedgerHistory!.All(e => e != null && e.Key != null && e.PropertyId != null && e.Source != null), "ledger history");
        Need(snap.DiceHistory!.All(e => e != null && e.Purpose != null && e.Results != null), "dice history");
        foreach (var st in snap.Stacks!.Values)
            Need(st != null && st.PieceIds != null && st.Id != null && st.MapId != null, "stack data");
        Need(f.Log!.All(c => c != null), "log entry");
        Need(snap.Log != null && snap.Log.All(e => e != null && e.Kind != null && e.Text != null), "game log");
        snap.Notes ??= "";
        snap.Turn ??= new TurnState(1, null);
    }

    public static GameSession Read(string path)
    {
        SaveFile? f;
        try
        {
            f = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(path), QuartermasterJson.Options);
        }
        catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException and not OutOfMemoryException)
        {
            // JsonException, NotSupportedException (e.g. a log entry without "$t"), InvalidOperationException, ...
            throw new InvalidDataException("Malformed save file: " + ex.Message, ex);
        }
        if (f == null) throw new InvalidDataException("Empty save file.");
        if (f.Format < MinFormat || f.Format > FormatVersion)
            throw new InvalidDataException($"Unsupported save format version {f.Format} (expected {MinFormat} to {FormatVersion}).");
        Validate(f);
        var session = new GameSession(f.Snapshot, f.ModuleSha256) { RulesSha256 = f.RulesSha256 };
        session.RestoreLog(f.Log);
        return session;
    }
}
