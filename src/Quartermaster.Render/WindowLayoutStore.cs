using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Quartermaster.Render;

/// <summary>Remembered state of one window. Bounds are screen coordinates (empty when unknown).</summary>
/// <remarks><c>Dpi</c> is the DPI of the monitor the bounds were captured on (0 = unknown).</remarks>
public readonly record struct SavedWindow(bool Floating, Rectangle Bounds, bool Maximized, int Dpi = 0);

/// <summary>
/// Per-module layout (<c>layout/&lt;sha&gt;.json</c>) and global main-window layout (<c>layout.json</c>) under an injected root.
/// Every operation is failure-tolerant: missing/corrupt/unwritable files give defaults and never throw.
/// </summary>
public sealed class WindowLayoutStore(string root)
{
    sealed class Dto { public int X { get; set; } public int Y { get; set; } public int W { get; set; } public int H { get; set; } public bool Floating { get; set; } public bool Maximized { get; set; } public int Dpi { get; set; } }
    sealed class ModuleFile { public Dictionary<string, Dto>? Windows { get; set; } }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    string ModulePath(string key) => Path.Combine(root, "layout", Regex.Replace(key, "[^A-Za-z0-9_-]", "_") + ".json");
    string MainPath => Path.Combine(root, "layout.json");

    public IReadOnlyDictionary<string, SavedWindow> LoadModule(string moduleKey)
    {
        var result = new Dictionary<string, SavedWindow>();
        try
        {
            var f = JsonSerializer.Deserialize<ModuleFile>(File.ReadAllText(ModulePath(moduleKey)));
            if (f?.Windows != null)
                foreach (var (k, d) in f.Windows)
                    if (d != null) result[k] = From(d);
        }
        catch (Exception) { result.Clear(); }
        return result;
    }

    public void SaveModule(string moduleKey, IReadOnlyDictionary<string, SavedWindow> windows)
        => Write(ModulePath(moduleKey), new ModuleFile { Windows = windows.ToDictionary(p => p.Key, p => To(p.Value)) });

    public SavedWindow? LoadMain()
    {
        try
        {
            var d = JsonSerializer.Deserialize<Dto>(File.ReadAllText(MainPath));
            return d is { W: > 0, H: > 0 } ? From(d) : null; // a main window always has real bounds
        }
        catch (Exception) { return null; }
    }

    public void SaveMain(SavedWindow main) => Write(MainPath, To(main));

    public const double DefaultLeftSplit = 0.6, MinLeftSplit = 0.2, MaxLeftSplit = 0.8;

    sealed class UiFile { public double LeftSplit { get; set; } }

    /// <summary>Fraction of the left panel height given to the module tree (clamped 0.2-0.8; default 0.6 when missing or corrupt).</summary>
    public double LoadLeftSplit()
    {
        try
        {
            var f = JsonSerializer.Deserialize<UiFile>(File.ReadAllText(Path.Combine(root, "ui.json")));
            return f != null && double.IsFinite(f.LeftSplit) && f.LeftSplit > 0 ? Math.Clamp(f.LeftSplit, MinLeftSplit, MaxLeftSplit) : DefaultLeftSplit;
        }
        catch (Exception) { return DefaultLeftSplit; }
    }

    public void SaveLeftSplit(double fraction)
        => Write(Path.Combine(root, "ui.json"), new UiFile { LeftSplit = double.IsFinite(fraction) ? Math.Clamp(fraction, MinLeftSplit, MaxLeftSplit) : DefaultLeftSplit });

    /// <summary>Lower-case hex SHA-256 of the file, or null when it cannot be read.</summary>
    public static string? HashFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch (Exception) { return null; }
    }

    static SavedWindow From(Dto d) => new(d.Floating, new Rectangle(d.X, d.Y, d.W, d.H), d.Maximized, d.Dpi);
    static Dto To(SavedWindow w) => new() { X = w.Bounds.X, Y = w.Bounds.Y, W = w.Bounds.Width, H = w.Bounds.Height, Floating = w.Floating, Maximized = w.Maximized, Dpi = w.Dpi };

    static void Write(string path, object value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception) { /* layout memory is best-effort */ }
    }
}
