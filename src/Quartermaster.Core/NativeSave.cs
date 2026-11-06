using System.Text.Json;

namespace Quartermaster.Core;

/// <summary>Native .qsave format: JSON { format, moduleSha256, snapshot, log }.</summary>
public static class NativeSave
{
    public const int FormatVersion = 1;

    sealed class SaveFile
    {
        public int Format { get; set; }
        public string ModuleSha256 { get; set; } = "";
        public GameState Snapshot { get; set; } = new();
        public List<GameCommand> Log { get; set; } = new();
    }

    public static void Write(string path, GameSession s)
    {
        var file = new SaveFile
        {
            Format = FormatVersion, ModuleSha256 = s.ModuleSha256, Snapshot = s.State, Log = s.Log.ToList(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(file, QuartermasterJson.Options));
    }

    public static GameSession Read(string path)
    {
        SaveFile? f;
        try
        {
            f = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(path), QuartermasterJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Malformed save file: " + ex.Message, ex);
        }
        if (f == null) throw new InvalidDataException("Empty save file.");
        if (f.Format != FormatVersion)
            throw new InvalidDataException($"Unsupported save format version {f.Format} (expected {FormatVersion}).");
        var session = new GameSession(f.Snapshot, f.ModuleSha256);
        session.RestoreLog(f.Log);
        return session;
    }
}
