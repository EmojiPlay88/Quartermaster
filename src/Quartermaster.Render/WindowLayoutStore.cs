using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    sealed class OpenMapsFile { public List<string?>? OpenMaps { get; set; } public string? SelectedMap { get; set; } }

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

    /// <summary>Saves the window entries; the module file's other fields (the open maps) are kept.</summary>
    public void SaveModule(string moduleKey, IReadOnlyDictionary<string, SavedWindow> windows)
        => Update(ModulePath(moduleKey), o => o["Windows"] = JsonSerializer.SerializeToNode(windows.ToDictionary(p => p.Key, p => To(p.Value))));

    /// <summary>The map ids whose tabs were open, in tab order (null when never saved or unreadable). Stored in the module file next to
    /// the window entries, so readers of the old shape (<see cref="LoadModule"/>, WinForms) are unaffected.</summary>
    public IReadOnlyList<string>? LoadOpenMaps(string moduleKey)
    {
        try
        {
            return JsonSerializer.Deserialize<OpenMapsFile>(File.ReadAllText(ModulePath(moduleKey)))?.OpenMaps?.OfType<string>().ToList();
        }
        catch (Exception) { return null; }
    }

    /// <summary>The map id whose tab was selected when the open maps were saved (null when none or unknown).</summary>
    public string? LoadSelectedMap(string moduleKey)
    {
        try
        {
            return JsonSerializer.Deserialize<OpenMapsFile>(File.ReadAllText(ModulePath(moduleKey)))?.SelectedMap;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Remembers the open map tabs (and the selected one); the window entries are kept.</summary>
    public void SaveOpenMaps(string moduleKey, IReadOnlyList<string> ids, string? selected = null)
        => Update(ModulePath(moduleKey), o =>
        {
            o["OpenMaps"] = JsonSerializer.SerializeToNode(ids.ToList());
            o["SelectedMap"] = selected;
        });

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

    /// <summary>ui.json. <c>LeftSplit</c> is the WinForms/old value (module tree share of tree + tables); the others are the Desktop left column.</summary>
    sealed class UiFile
    {
        public double LeftSplit { get; set; }
        public double TreeFraction { get; set; }
        public double PaletteFraction { get; set; }
        public bool? PaletteShown { get; set; }
    }

    /// <summary>Defaults of the Desktop left column (fractions of its height): module tree, counter palette; the tables tree gets the rest.</summary>
    public const double DefaultTreeFraction = 0.4, DefaultPaletteFraction = 0.35, MinPartFraction = 0.15, MaxSplitSum = 0.85;

    string UiPath => Path.Combine(root, "ui.json");

    UiFile ReadUi()
    {
        try { return JsonSerializer.Deserialize<UiFile>(File.ReadAllText(UiPath)) ?? new UiFile(); }
        catch (Exception) { return new UiFile(); }
    }

    void UpdateUi(Action<UiFile> change)
    {
        var f = ReadUi();
        change(f);
        Write(UiPath, f);
    }

    /// <summary>Clamps both fractions to a sensible range and leaves the tables tree at least <c>1 - MaxSplitSum</c> of the column.</summary>
    public static (double Tree, double Palette) NormalizeSplits(double tree, double palette)
    {
        tree = double.IsFinite(tree) ? Math.Clamp(tree, MinPartFraction, MaxSplitSum - MinPartFraction) : DefaultTreeFraction;
        palette = double.IsFinite(palette) ? Math.Clamp(palette, MinPartFraction, MaxSplitSum - MinPartFraction) : DefaultPaletteFraction;
        var sum = tree + palette;
        if (sum > MaxSplitSum) { tree *= MaxSplitSum / sum; palette *= MaxSplitSum / sum; }
        return (tree, palette);
    }

    /// <summary>The Desktop left column's split as fractions of its height (module tree, counter palette). An old file with only
    /// <c>LeftSplit</c> keeps the tree share it had (scaled into the room the default palette leaves); missing or corrupt gives the defaults.</summary>
    public (double Tree, double Palette) LoadLeftSplits()
    {
        var f = ReadUi();
        var palette = double.IsFinite(f.PaletteFraction) && f.PaletteFraction > 0 ? f.PaletteFraction : DefaultPaletteFraction;
        if (double.IsFinite(f.TreeFraction) && f.TreeFraction > 0) return NormalizeSplits(f.TreeFraction, palette);
        if (double.IsFinite(f.LeftSplit) && f.LeftSplit > 0)
            return NormalizeSplits(Math.Clamp(f.LeftSplit, MinLeftSplit, MaxLeftSplit) * (1 - palette), palette);
        return NormalizeSplits(DefaultTreeFraction, palette);
    }

    public void SaveLeftSplits(double tree, double palette)
    {
        var (t, p) = NormalizeSplits(tree, palette);
        UpdateUi(f => { f.TreeFraction = t; f.PaletteFraction = p; });
    }

    /// <summary>Whether the counter palette is switched on (default true; it still needs game master mode to show).</summary>
    public bool LoadPaletteShown() => ReadUi().PaletteShown ?? true;

    public void SavePaletteShown(bool shown) => UpdateUi(f => f.PaletteShown = shown);

    /// <summary>Fraction of the left panel height given to the module tree (clamped 0.2-0.8; default 0.6 when missing or corrupt).</summary>
    public double LoadLeftSplit()
    {
        var f = ReadUi();
        return double.IsFinite(f.LeftSplit) && f.LeftSplit > 0 ? Math.Clamp(f.LeftSplit, MinLeftSplit, MaxLeftSplit) : DefaultLeftSplit;
    }

    public void SaveLeftSplit(double fraction)
        => UpdateUi(f => f.LeftSplit = double.IsFinite(fraction) ? Math.Clamp(fraction, MinLeftSplit, MaxLeftSplit) : DefaultLeftSplit);

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

    /// <summary>Read-modify-write of a JSON object file: an unreadable or non-object file starts empty.</summary>
    static void Update(string path, Action<JsonObject> change)
    {
        JsonObject o;
        try { o = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject(); }
        catch (Exception) { o = new JsonObject(); }
        change(o);
        Write(path, o);
    }

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
