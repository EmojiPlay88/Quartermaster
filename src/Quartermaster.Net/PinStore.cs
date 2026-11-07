using System.Text.Json;

namespace Quartermaster.Net;

/// <summary>Trust-on-first-use certificate fingerprints per "host:port", persisted as a small JSON file.</summary>
public sealed class PinStore(string path)
{
    private readonly object _gate = new();

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public string? Get(string hostPort)
    {
        lock (_gate) return Load().TryGetValue(hostPort, out var f) ? f.ToUpperInvariant() : null;
    }

    public void Set(string hostPort, string fingerprint)
    {
        lock (_gate)
        {
            var map = Load();
            map[hostPort] = fingerprint.ToUpperInvariant();
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(map));
                File.Move(tmp, path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
