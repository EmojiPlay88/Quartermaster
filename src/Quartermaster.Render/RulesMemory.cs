using System.Text.Json;

namespace Quartermaster.Render;

/// <summary>
/// Remembers the last rules pack path per module hash in <c>rules-index.json</c> under an injected root.
/// Every operation is failure-tolerant: a missing or corrupt file means no entry; write failures are ignored.
/// </summary>
public static class RulesMemory
{
    /// <summary>%LOCALAPPDATA%\Quartermaster, redirected by QUARTERMASTER_LAYOUT_ROOT (same as window layout).</summary>
    public static string DefaultRoot => AppDataRoot.Path;

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

    public static void Remember(string root, string moduleSha, string path) => Change(root, d => { d[moduleSha] = path; return true; });

    /// <summary>Drops the remembered rules pack for this module (no write when there is none).</summary>
    public static void Forget(string root, string moduleSha) => Change(root, d => d.Remove(moduleSha));

    private static void Change(string root, Func<Dictionary<string, string>, bool> change)
    {
        try
        {
            var d = Read(root);
            if (!change(d)) return;
            Directory.CreateDirectory(root);
            var tmp = IndexPath(root) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, IndexPath(root), overwrite: true);
        }
        catch (Exception) { /* best effort */ }
    }
}
