using System.Text.Json;

namespace Quartermaster.Render;

/// <summary>
/// Remembers the last rules pack path per module hash in <c>rules-index.json</c> under an injected root.
/// Every operation is failure-tolerant: a missing or corrupt file means no entry; write failures are ignored.
/// </summary>
public static class RulesMemory
{
    /// <summary>%LOCALAPPDATA%\Quartermaster, redirected by QUARTERMASTER_LAYOUT_ROOT (same as window layout).</summary>
    public static string DefaultRoot =>
        Environment.GetEnvironmentVariable("QUARTERMASTER_LAYOUT_ROOT") is { Length: > 0 } r ? r
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quartermaster");

    private static string IndexPath(string root) => Path.Combine(root, "rules-index.json");

    private static Dictionary<string, string> Read(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(IndexPath(root)));
            if (d != null)
                foreach (var (k, v) in d)
                    if (k != null && !string.IsNullOrWhiteSpace(v)) result[k] = v;
        }
        catch (Exception) { result.Clear(); }
        return result;
    }

    public static string? LastFor(string root, string moduleSha) =>
        Read(root).TryGetValue(moduleSha, out var p) ? p : null;

    public static void Remember(string root, string moduleSha, string path)
    {
        try
        {
            var d = Read(root);
            d[moduleSha] = path;
            Directory.CreateDirectory(root);
            var tmp = IndexPath(root) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, IndexPath(root), overwrite: true);
        }
        catch (Exception) { /* best effort */ }
    }
}
