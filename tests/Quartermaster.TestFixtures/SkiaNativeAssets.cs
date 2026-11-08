using System.Reflection;
using System.Text.Json;

namespace Quartermaster.TestFixtures;

/// <summary>
/// Guards the SkiaSharp native assets a test project ships: the Linux native must be in the output (ubuntu CI loads it from
/// runtimes/linux-x64/native) and every SkiaSharp package must resolve to one version (only one native libSkiaSharp may live in a process).
/// </summary>
public static class SkiaNativeAssets
{
    public const string Version = "4.154.0";

    /// <summary>Problems found in the output folder of the calling test assembly (empty when all is well).</summary>
    public static IReadOnlyList<string> Problems(Assembly testAssembly)
    {
        var problems = new List<string>();
        var dir = AppContext.BaseDirectory;
        var so = Path.Combine(dir, "runtimes", "linux-x64", "native", "libSkiaSharp.so");
        if (!File.Exists(so)) problems.Add("missing " + so);

        var deps = Path.Combine(dir, testAssembly.GetName().Name + ".deps.json");
        if (!File.Exists(deps)) { problems.Add("missing " + deps); return problems; }
        using var doc = JsonDocument.Parse(File.ReadAllText(deps));
        var skia = doc.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(p => p.Name)
            .Where(n => n.StartsWith("SkiaSharp", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var versions = skia.Select(n => n[(n.IndexOf('/') + 1)..]).Distinct().ToList();
        if (versions.Count != 1 || versions[0] != Version)
            problems.Add($"SkiaSharp packages resolve to {string.Join(", ", skia)}; expected all at {Version}");
        if (!skia.Contains("SkiaSharp.NativeAssets.Linux/" + Version))
            problems.Add($"SkiaSharp.NativeAssets.Linux/{Version} not in {Path.GetFileName(deps)}");
        return problems;
    }
}
