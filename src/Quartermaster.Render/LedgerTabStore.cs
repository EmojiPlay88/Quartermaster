using System.Text.Json;
using System.Text.Json.Nodes;
using Quartermaster.Core;

namespace Quartermaster.Render;

/// <summary>A user's Ledger tabs and view preferences for one module (stored on this PC, never in the rules pack).</summary>
public sealed record LedgerPrefs(IReadOnlyList<UserLedgerTab> Tabs, string? LastTab, bool ShowCounters, string? Side,
    LedgerGrouping Grouping, IReadOnlyDictionary<string, IReadOnlyList<int>> ColumnWidths)
{
    public static LedgerPrefs Default { get; } = new(Array.Empty<UserLedgerTab>(), null, true, null, LedgerGrouping.Map,
        new Dictionary<string, IReadOnlyList<int>>());
}

/// <summary>
/// Per-module personal ledger tabs in <c>ledger-tabs.json</c> under an injected root, keyed by module SHA-256.
/// Load never throws (corrupt data means defaults, tolerant per field); Save is best effort, atomic tmp+move.
/// </summary>
public static class LedgerTabStore
{
    /// <summary>Raised with the module SHA after every Save attempt returns.</summary>
    public static event Action<string>? Changed;

    private static string FilePath(string root) => Path.Combine(root, "ledger-tabs.json");

    /// <summary>
    /// The whole file: empty when it does not exist; null when it exists but cannot be read right now (locked, no access), so Save leaves it
    /// alone. A file that reads but does not parse as a JSON object is first copied to <c>ledger-tabs.json.bad-&lt;yyyyMMddHHmmss&gt;</c> (UTC),
    /// once, so the next Save cannot destroy the other modules' tabs without a copy.
    /// </summary>
    private static JsonObject? ReadAll(string root)
    {
        var path = FilePath(root);
        string text;
        try
        {
            if (!File.Exists(path)) return new JsonObject();
            text = File.ReadAllText(path);
        }
        catch (Exception) { return null; }
        try
        {
            if (JsonNode.Parse(text) is JsonObject o) return o;
        }
        catch (Exception) { /* not JSON: kept below */ }
        KeepBadCopy(path, text);
        return new JsonObject();
    }

    private static void KeepBadCopy(string path, string text)
    {
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileName(path) + ".bad-";
            // Once: an identical copy already kept is enough (Load and Save both get here until the file is rewritten).
            foreach (var old in Directory.GetFiles(dir, name + "*"))
                if (File.ReadAllText(old) == text) return;
            var bad = path + ".bad-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            if (!File.Exists(bad)) File.Copy(path, bad);
        }
        catch (Exception) { /* best effort */ }
    }

    public static LedgerPrefs Load(string root, string moduleSha)
    {
        try
        {
            if (ReadAll(root)?[moduleSha] is not JsonObject o) return LedgerPrefs.Default;
            var tabs = new List<UserLedgerTab>();
            if (o["tabs"] is JsonArray arr)
                foreach (var n in arr)
                    if (n is JsonObject t && Str(t["name"]) is { } name)
                    {
                        var cols = new List<string>();
                        if (t["columnIds"] is JsonArray ca)
                            foreach (var c in ca) if (Str(c) is { } s) cols.Add(s);
                        tabs.Add(new UserLedgerTab(name, Str(t["filter"]), cols, Bool(t["includeHolders"]) ?? false, Str(t["notes"])));
                    }
            var widths = new Dictionary<string, IReadOnlyList<int>>();
            if (o["columnWidths"] is JsonObject wo)
                foreach (var (k, v) in wo)
                    if (v is JsonArray wa)
                    {
                        var l = new List<int>();
                        foreach (var w in wa) { if (Int(w) is { } i) l.Add(i); }
                        widths[k] = l;
                    }
            var grouping = Str(o["grouping"]) is { } g && Enum.TryParse<LedgerGrouping>(g, true, out var gv) && Enum.IsDefined(gv)
                ? gv : LedgerGrouping.Map;
            return new LedgerPrefs(Normalize(tabs), Str(o["lastTab"]), Bool(o["showCounters"]) ?? true, Str(o["side"]), grouping, widths);
        }
        catch (Exception) { return LedgerPrefs.Default; }
    }

    public static void Save(string root, string moduleSha, LedgerPrefs prefs)
    {
        try
        {
            var all = ReadAll(root) ?? throw new IOException("ledger-tabs.json cannot be read right now; not overwriting it");
            var tabs = new JsonArray();
            foreach (var t in Normalize(prefs.Tabs))
            {
                var cols = new JsonArray();
                foreach (var c in t.ColumnIds ?? Array.Empty<string>()) cols.Add(c);
                tabs.Add(new JsonObject
                {
                    ["name"] = t.Name, ["filter"] = t.Filter, ["columnIds"] = cols,
                    ["includeHolders"] = t.IncludeHolders, ["notes"] = t.Notes,
                });
            }
            var widths = new JsonObject();
            foreach (var (k, v) in prefs.ColumnWidths ?? new Dictionary<string, IReadOnlyList<int>>())
            {
                var wa = new JsonArray();
                foreach (var w in v) wa.Add(w);
                widths[k] = wa;
            }
            all[moduleSha] = new JsonObject
            {
                ["tabs"] = tabs, ["lastTab"] = prefs.LastTab, ["showCounters"] = prefs.ShowCounters,
                ["side"] = prefs.Side, ["grouping"] = prefs.Grouping.ToString(), ["columnWidths"] = widths,
            };
            Directory.CreateDirectory(root);
            var tmp = FilePath(root) + ".tmp";
            File.WriteAllText(tmp, all.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath(root), overwrite: true);
        }
        catch (Exception) { /* best effort */ }
        try { Changed?.Invoke(moduleSha); } catch (Exception) { /* a bad subscriber must not break saving */ }
    }

    /// <summary>Trims names, drops blank ones, dedups case-insensitively (first wins).</summary>
    public static IReadOnlyList<UserLedgerTab> Normalize(IReadOnlyList<UserLedgerTab> tabs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<UserLedgerTab>();
        foreach (var t in tabs ?? Array.Empty<UserLedgerTab>())
        {
            var name = t?.Name?.Trim();
            if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
            result.Add(t! with { Name = name, ColumnIds = t.ColumnIds ?? Array.Empty<string>() });
        }
        return result;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}
