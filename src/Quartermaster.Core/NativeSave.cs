using System.Text.Json;

namespace Quartermaster.Core;

/// <summary>Native .qsave format: JSON { format, moduleSha256, rulesSha256?, snapshot, log }.</summary>
public static class NativeSave
{
    public const int FormatVersion = 7;
    const int MinFormat = 1;

    sealed class SaveFile
    {
        public int Format { get; set; }
        public string ModuleSha256 { get; set; } = "";
        public string? RulesSha256 { get; set; }
        public GameState Snapshot { get; set; } = new();
        public List<GameCommand> Log { get; set; } = new();
        public GameState? Genesis { get; set; }
        public List<ActionMark>? Actions { get; set; }
        public List<Checkpoint>? Checkpoints { get; set; }
        /// <summary>True for an online guest's viewer copy (read-only); absent in ordinary saves, so the format stays 7.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public bool? ViewerCopy { get; set; }
    }

    const string RebaseNotice = "Undo history in this save did not match the game; undo starts from here.";

    public static void Write(string path, GameSession s)
    {
        var file = new SaveFile
        {
            Format = FormatVersion, ModuleSha256 = s.ModuleSha256, RulesSha256 = s.RulesSha256, Snapshot = s.State, Log = s.Log.ToList(),
            Genesis = s.Genesis, Actions = s.Actions.ToList(), Checkpoints = s.Checkpoints.ToList(),
            ViewerCopy = s.ViewerCopy ? true : null,
        };
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, QuartermasterJson.Options));
        File.Move(tmp, path, overwrite: true);
    }

    private static void Validate(SaveFile f)
    {
        static void Need(bool ok, string what) { if (!ok) throw new InvalidDataException("Malformed save file: " + what + " is missing."); }
        Need(f.Snapshot != null, "snapshot");
        Need(f.Log != null, "log");
        Need(f.ModuleSha256 != null, "module hash");
        Need(f.Log!.All(c => c != null), "log entry");
        ValidateState(f.Snapshot!);
    }

    /// <summary>
    /// Checks a state read from outside (a save, an online snapshot) for missing parts and fills optional ones with defaults.
    /// Throws InvalidDataException when something required is missing.
    /// </summary>
    public static void ValidateState(GameState snap)
    {
        static void Need(bool ok, string what) { if (!ok) throw new InvalidDataException("Malformed save file: " + what + " is missing."); }
        Need(snap.Pieces != null && snap.Stacks != null && snap.MapOrder != null, "snapshot content");
        foreach (var p in snap.Pieces!.Values)
            Need(p != null && p.TraitStates != null && p.Trail != null && p.TypeString != null && p.Id != null && p.MapId != null, "piece data");
        Need(snap.Ledgers != null && snap.LedgerHistory != null && snap.DiceHistory != null, "ledger data");
        foreach (var l in snap.Ledgers!.Values) Need(l != null, "ledger data");
        Need(snap.LedgerHistory!.All(e => e != null && e.Key != null && e.PropertyId != null && e.Source != null), "ledger history");
        Need(snap.DiceHistory!.All(e => e != null && e.Purpose != null && e.Results != null), "dice history");
        foreach (var st in snap.Stacks!.Values)
            Need(st != null && st.PieceIds != null && st.Id != null && st.MapId != null, "stack data");
        Need(snap.Log != null && snap.Log.All(e => e != null && e.Kind != null && e.Text != null), "game log");
        snap.Notes ??= "";
        snap.Ticked ??= new();
        snap.Acknowledged ??= new();
        snap.HiddenAlerts = (snap.HiddenAlerts ?? new()).Where(a => a != null).ToList();
        snap.Pending = (snap.Pending ?? new()).Where(r => r != null && r.Units != null).ToList();
        snap.Turn ??= new TurnState(1, null);
        snap.Players = Core.Players.Clean(snap.Players);
        snap.GameId ??= "";
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
        var session = new GameSession(f.Snapshot, f.ModuleSha256) { RulesSha256 = f.RulesSha256, ViewerCopy = f.ViewerCopy == true };
        session.RestoreLog(f.Log);
        if (f.Format < 5 || f.Genesis == null)
        {
            session.Rebase();
            session.RebasedOnLoad = true;
            LegacyId(f, session);
            return session;
        }
        if (TryRestoreHistory(f, session, out var why)) { LegacyId(f, session); return session; }
        session.LoadDiagnostic = why;
        session.Rebase();
        session.RebasedOnLoad = true;
        session.LoadNotice = RebaseNotice;
        LegacyId(f, session);
        return session;
    }

    /// <summary>A save without a GameId (any format) gets one, after the replay check so genesis and state still match.</summary>
    static void LegacyId(SaveFile f, GameSession session)
    {
        if (string.IsNullOrEmpty(session.State.GameId)) session.AssignGameId(Guid.NewGuid().ToString("N"));
    }

    /// <summary>Validates the format-5 history and checks that replaying it reproduces the snapshot.</summary>
    static bool TryRestoreHistory(SaveFile f, GameSession session, out string? why)
    {
        why = null;
        try
        {
            var genesis = f.Genesis!;
            ValidateState(genesis);
            int n = f.Log.Count;
            var actions = f.Actions ?? new();
            int prev = -1;
            foreach (var a in actions)
            {
                if (a == null || a.Label == null || a.Start <= prev || a.Start >= n) { why = "structure"; return false; }
                prev = a.Start;
            }
            List<Checkpoint>? cps = f.Checkpoints;
            if (cps != null)
            {
                if (cps.Count == 0 || cps[0] == null || cps[0].LogIndex != 0) { why = "structure"; return false; }
                int last = 0;
                foreach (var c in cps)
                {
                    if (c == null || c.Label == null || c.LogIndex < last || c.LogIndex > n) { why = "structure"; return false; }
                    last = c.LogIndex;
                }
            }
            var replay = genesis.DeepClone();
            foreach (var c in f.Log) replay.Apply(c);
            if (JsonSerializer.Serialize(replay, QuartermasterJson.Options) != JsonSerializer.Serialize(f.Snapshot, QuartermasterJson.Options))
            {
                // Format-5 saves predate LedgerEntry.Turn/Phase: tolerate that difference only, and keep the replayed state.
                if (f.Format >= 6 || StripTurns(replay) != StripTurns(f.Snapshot)) { why = "replay mismatch"; return false; }
                session.ReplaceState(replay);
            }
            session.RestoreHistory(genesis, actions, cps);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { why = ex.GetType().Name + ": " + ex.Message; return false; }
    }

    static string StripTurns(GameState s)
    {
        var copy = s.DeepClone();
        copy.LedgerHistory = copy.LedgerHistory.Select(e => e with { Turn = null, Phase = null }).ToList();
        return JsonSerializer.Serialize(copy, QuartermasterJson.Options);
    }
}
