using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>
/// Reads and writes <c>settings.json</c> under an injected root. Loading is tolerant per field: a missing, mistyped or unknown value falls
/// back to its default (one "settings" warning naming the field) and numbers are clamped. Saving is atomic and never throws.
/// </summary>
public sealed class SettingsStore(string root)
{
    static readonly Regex Hex = new("^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    /// <summary>%LOCALAPPDATA%\Quartermaster, redirected by QUARTERMASTER_LAYOUT_ROOT.</summary>
    public static string DefaultRoot => RulesMemory.DefaultRoot;

    /// <summary>The folder holding <c>settings.json</c> (and, beside it, the rules index and autosaves).</summary>
    public string Root => root;

    string FilePath => Path.Combine(root, "settings.json");

    /// <summary>The settings last loaded or saved (defaults until then).</summary>
    public Settings Current { get; private set; } = new();

    /// <summary>Raised by <see cref="Save"/> after the new settings became <see cref="Current"/>. A failing subscriber does not stop the others.</summary>
    public event Action<Settings>? Changed;

    /// <summary>Why the last <see cref="Save"/> could not write the file (null when it could).</summary>
    public string? LastSaveError { get; private set; }

    public static bool TryParseColor(string? text, out (byte R, byte G, byte B) rgb)
    {
        rgb = default;
        if (text == null || !Hex.IsMatch(text)) return false;
        rgb = (byte.Parse(text.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(text.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(text.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return true;
    }

    // ---------- load ----------

    public Settings Load(DiagnosticLog? log)
    {
        lock (_gate) Current = ReadFile(log);
        return Current;
    }

    Settings ReadFile(DiagnosticLog? log)
    {
        string text;
        try { text = File.ReadAllText(FilePath); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return new Settings(); }
        catch (Exception ex)
        {
            Warn(log, "file", "could not be read: " + ex.Message);
            return new Settings();
        }
        JsonObject? o;
        try { o = JsonNode.Parse(text) as JsonObject; }
        catch (JsonException ex)
        {
            Warn(log, "file", "is not valid JSON: " + ex.Message);
            return new Settings();
        }
        if (o == null)
        {
            Warn(log, "file", "does not hold a JSON object");
            return new Settings();
        }

        var r = new Reader(o, log);
        var d = new Settings();
        var tabMin = r.Int("TabMinWidth", d.TabMinWidth, 40, 400);
        var tabMax = r.Int("TabMaxWidth", d.TabMaxWidth, 60, 600);
        if (tabMax < tabMin)
        {
            Warn(log, "TabMaxWidth", "is smaller than TabMinWidth; both reset");
            tabMin = d.TabMinWidth;
            tabMax = d.TabMaxWidth;
        }
        return new Settings
        {
            ReopenLastModule = r.Bool("ReopenLastModule", d.ReopenLastModule),
            AutosaveMinutes = r.Int("AutosaveMinutes", d.AutosaveMinutes, 0, 240),
            ConfirmDelete = r.Bool("ConfirmDelete", d.ConfirmDelete),
            GameMasterMode = r.Bool("GameMasterMode", d.GameMasterMode),
            GridDefault = r.Enum("GridDefault", d.GridDefault),
            LabelFontSize = r.Int("LabelFontSize", d.LabelFontSize, 6, 20),
            HighlightColor = r.Str("HighlightColor", d.HighlightColor, s => TryParseColor(s, out _) ? s.ToUpperInvariant() : null),
            TabMinWidth = tabMin,
            TabMaxWidth = tabMax,
            WheelZoomStep = r.Double("WheelZoomStep", d.WheelZoomStep, 1.05, 2),
            SnapOnDrop = r.Bool("SnapOnDrop", d.SnapOnDrop),
            DragThresholdPx = r.Int("DragThresholdPx", d.DragThresholdPx, 0, 50),
            HoverDelayMs = r.Int("HoverDelayMs", d.HoverDelayMs, 0, 5000),
            PhysicalDiceDefault = r.Bool("PhysicalDiceDefault", d.PhysicalDiceDefault),
            ShowLastRollInStatus = r.Bool("ShowLastRollInStatus", d.ShowLastRollInStatus),
            LogShowMoves = r.Bool("LogShowMoves", d.LogShowMoves),
            LastDifficulty = r.Enum("LastDifficulty", d.LastDifficulty),
            LogTimeFormat = r.Str("LogTimeFormat", d.LogTimeFormat, ValidTimeFormat),
            AlertBarVisible = r.Bool("AlertBarVisible", d.AlertBarVisible),
            OutlinerVisible = r.Bool("OutlinerVisible", d.OutlinerVisible),
            OutlinerGrouping = r.Enum("OutlinerGrouping", d.OutlinerGrouping),
            TourShown = r.Bool("TourShown", d.TourShown),
            ToolbarVisible = ToolbarIds.Migrate(r.Map<bool>("ToolbarVisible", (JsonNode? n, out bool b) => { b = false; return n is JsonValue v && v.TryGetValue(out b); })),
            ShortcutOverrides = r.Map<string>("ShortcutOverrides", (JsonNode? n, out string s) => { s = ""; return n is JsonValue v && v.TryGetValue(out s!) && s != null; }),
            LeftSplitterDistance = r.Double("LeftSplitterDistance", d.LeftSplitterDistance, 0.2, 0.8),
            LastModulePath = r.NullableStr("LastModulePath"),
            Modules = r.List<ModuleEntry>("Modules", ReadModule) is { Count: > 0 } mods ? mods : d.Modules,
            RecentSaves = r.Map<IReadOnlyList<string>>("RecentSaves", (JsonNode? n, out IReadOnlyList<string> l) =>
            {
                l = Array.Empty<string>();
                if (n is not JsonArray arr) return false;
                l = arr.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null).OfType<string>().Take(10).ToList();
                return true;
            }) is { Count: > 0 } recent ? recent : d.RecentSaves,
        };
    }

    static bool ReadModule(JsonNode? n, out ModuleEntry e)
    {
        e = null!;
        if (n is not JsonObject o) return false;
        static string? S(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var path = S(o, "Path");
        if (string.IsNullOrWhiteSpace(path)) return false;
        long size = o["Size"] is JsonValue sv && sv.TryGetValue<long>(out var sz) && sz >= 0 ? sz : 0;
        var written = DateTime.TryParse(S(o, "LastWriteUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var w) ? w : DateTime.MinValue;
        DateTimeOffset? played = DateTimeOffset.TryParse(S(o, "LastPlayed"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var p) ? p : null;
        var sha = S(o, "Sha256");
        e = new ModuleEntry(path, string.IsNullOrEmpty(sha) ? null : sha, size, written, S(o, "Title") ?? Path.GetFileNameWithoutExtension(path), S(o, "Version") ?? "", played);
        return true;
    }

    static JsonObject ModuleJson(ModuleEntry m) => new()
    {
        ["Path"] = m.Path,
        ["Sha256"] = m.Sha256,
        ["Size"] = m.Size,
        ["LastWriteUtc"] = m.LastWriteUtc.ToString("o", CultureInfo.InvariantCulture),
        ["Title"] = m.Title,
        ["Version"] = m.Version,
        ["LastPlayed"] = m.LastPlayed?.ToString("o", CultureInfo.InvariantCulture),
    };

    public static bool IsValidTimeFormat(string? s) => s != null && ValidTimeFormat(s) != null;

    static string? ValidTimeFormat(string s)
    {
        if (s.Length == 0 || s.Length > 40) return null;
        try { _ = DateTime.Now.ToString(s, CultureInfo.InvariantCulture); return s; }
        catch (FormatException) { return null; }
    }

    static void Warn(DiagnosticLog? log, string field, string what) =>
        log?.Add(DiagnosticSeverity.Warning, "settings", $"Setting '{field}' {what}; using the default.", field);

    sealed class Reader(JsonObject o, DiagnosticLog? log)
    {
        bool TryGet(string field, out JsonNode? node)
        {
            node = null;
            return o.TryGetPropertyValue(field, out node) && node != null;
        }

        public bool Bool(string field, bool def)
        {
            if (!TryGet(field, out var n)) return def;
            if (n is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
            Warn(log, field, "must be true or false");
            return def;
        }

        public int Int(string field, int def, int min, int max)
        {
            if (!TryGet(field, out var n)) return def;
            if (n is JsonValue v && v.TryGetValue<int>(out var i))
            {
                if (i >= min && i <= max) return i;
                Warn(log, field, $"is out of range {min}-{max} (clamped)");
                return Math.Clamp(i, min, max);
            }
            Warn(log, field, "must be a whole number");
            return def;
        }

        public double Double(string field, double def, double min, double max)
        {
            if (!TryGet(field, out var n)) return def;
            if (n is JsonValue v && v.TryGetValue<double>(out var x) && double.IsFinite(x))
            {
                if (x >= min && x <= max) return x;
                Warn(log, field, $"is out of range {min.ToString(CultureInfo.InvariantCulture)}-{max.ToString(CultureInfo.InvariantCulture)} (clamped)");
                return Math.Clamp(x, min, max);
            }
            Warn(log, field, "must be a number");
            return def;
        }

        public T Enum<T>(string field, T def) where T : struct, Enum
        {
            if (!TryGet(field, out var n)) return def;
            if (n is JsonValue v && v.TryGetValue<string>(out var s) && System.Enum.TryParse<T>(s, true, out var e) && !int.TryParse(s, out _) && System.Enum.IsDefined(e))
                return e;
            Warn(log, field, $"must be one of {string.Join(", ", System.Enum.GetNames<T>())}");
            return def;
        }

        public string Str(string field, string def, Func<string, string?> validate)
        {
            if (!TryGet(field, out var n)) return def;
            if (n is JsonValue v && v.TryGetValue<string>(out var s) && validate(s) is { } ok) return ok;
            Warn(log, field, "is not valid");
            return def;
        }

        public string? NullableStr(string field)
        {
            if (!TryGet(field, out var n)) return null;
            if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s.Length == 0 ? null : s;
            Warn(log, field, "must be text");
            return null;
        }

        public delegate bool TryRead<T>(JsonNode? node, out T value);

        public IReadOnlyList<T> List<T>(string field, TryRead<T> read)
        {
            var result = new List<T>();
            if (!TryGet(field, out var n)) return result;
            if (n is not JsonArray arr)
            {
                Warn(log, field, "must be a list");
                return result;
            }
            var bad = false;
            foreach (var v in arr)
            {
                if (read(v, out var x)) result.Add(x); else bad = true;
            }
            if (bad) Warn(log, field, "has entries of the wrong type (skipped)");
            return result;
        }

        public IReadOnlyDictionary<string, T> Map<T>(string field, TryRead<T> read)
        {
            var result = new Dictionary<string, T>();
            if (!TryGet(field, out var n)) return result;
            if (n is not JsonObject obj)
            {
                Warn(log, field, "must be an object");
                return result;
            }
            var bad = false;
            foreach (var (k, v) in obj)
            {
                if (read(v, out var x)) result[k] = x; else bad = true;
            }
            if (bad) Warn(log, field, "has entries of the wrong type (skipped)");
            return result;
        }
    }

    // ---------- save ----------

    readonly object _gate = new();

    /// <summary>Makes <paramref name="s"/> current, writes it atomically (failure is recorded in <see cref="LastSaveError"/>, never thrown) and raises <see cref="Changed"/>.
    /// Thread-safe; <see cref="Changed"/> is raised on the calling thread (UI subscribers must marshal).</summary>
    public void Save(Settings s)
    {
        lock (_gate) Write(s);
        Raise(s);
    }

    /// <summary>Atomically reads <see cref="Current"/>, applies <paramref name="change"/>, saves the result and raises <see cref="Changed"/> (on the calling thread, outside the lock). Concurrent updates never lose each other.</summary>
    public void Update(Func<Settings, Settings> change)
    {
        Settings next;
        lock (_gate)
        {
            next = change(Current);
            Write(next);
        }
        Raise(next);
    }

    void Write(Settings s)
    {
        Current = s;
        try
        {
            Directory.CreateDirectory(root);
            var tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, ToJson(s).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, overwrite: true);
            }
            finally { try { File.Delete(tmp); } catch (Exception) { } }
            LastSaveError = null;
        }
        catch (Exception ex)
        {
            LastSaveError = ex.Message;
        }
    }

    void Raise(Settings s)
    {
        if (Changed == null) return;
        foreach (var h in Changed.GetInvocationList().Cast<Action<Settings>>())
        {
            try { h(s); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { LastSaveError ??= "a settings subscriber failed: " + ex.Message; }
        }
    }

    static JsonObject ToJson(Settings s) => new()
    {
        ["ReopenLastModule"] = s.ReopenLastModule,
        ["AutosaveMinutes"] = s.AutosaveMinutes,
        ["ConfirmDelete"] = s.ConfirmDelete,
        ["GameMasterMode"] = s.GameMasterMode,
        ["GridDefault"] = s.GridDefault.ToString(),
        ["LastDifficulty"] = s.LastDifficulty.ToString(),
        ["LabelFontSize"] = s.LabelFontSize,
        ["HighlightColor"] = s.HighlightColor,
        ["TabMinWidth"] = s.TabMinWidth,
        ["TabMaxWidth"] = s.TabMaxWidth,
        ["WheelZoomStep"] = s.WheelZoomStep,
        ["SnapOnDrop"] = s.SnapOnDrop,
        ["DragThresholdPx"] = s.DragThresholdPx,
        ["HoverDelayMs"] = s.HoverDelayMs,
        ["PhysicalDiceDefault"] = s.PhysicalDiceDefault,
        ["ShowLastRollInStatus"] = s.ShowLastRollInStatus,
        ["LogShowMoves"] = s.LogShowMoves,
        ["LogTimeFormat"] = s.LogTimeFormat,
        ["AlertBarVisible"] = s.AlertBarVisible,
        ["OutlinerVisible"] = s.OutlinerVisible,
        ["OutlinerGrouping"] = s.OutlinerGrouping.ToString(),
        ["TourShown"] = s.TourShown,
        ["ToolbarVisible"] = new JsonObject(s.ToolbarVisible.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(p.Value)))),
        ["ShortcutOverrides"] = new JsonObject(s.ShortcutOverrides.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(p.Value)))),
        ["LeftSplitterDistance"] = s.LeftSplitterDistance,
        ["LastModulePath"] = s.LastModulePath,
        ["Modules"] = new JsonArray(s.Modules.Select(m => (JsonNode?)ModuleJson(m)).ToArray()),
        ["RecentSaves"] = new JsonObject(s.RecentSaves.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)new JsonArray(p.Value.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray())))),
    };
}
