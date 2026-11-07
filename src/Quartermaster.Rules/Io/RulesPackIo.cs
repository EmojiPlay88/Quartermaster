using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Quartermaster.Rules;

/// <summary>
/// .qrules IO. Layout: pack.json (metadata, properties, holders, ruleRefs, sides, phases, blocks; the last three keep their order), tables/{id}.json, formulas/{id}.json.
/// A pack may be a zip or a folder of the same layout.
/// </summary>
public static class RulesPackIo
{
    public const int CurrentFormat = 3;
    private const string PackEntry = "pack.json";

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static RulesPack CreateEmpty(string name, string moduleSha256) => new()
    {
        Format = CurrentFormat,
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        ModuleSha256 = moduleSha256,
    };

    public static RulesPack Load(string path)
    {
        if (Directory.Exists(path))
        {
            string? ReadDir(string entry)
            {
                var f = Path.Combine(path, entry.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(f) ? File.ReadAllText(f, Encoding.UTF8) : null;
            }
            return Assemble(ReadDir, ListDir(path));
        }
        if (!File.Exists(path)) throw new FileNotFoundException("Rules pack not found", path);

        ZipArchive zip;
        try { zip = ZipFile.OpenRead(path); }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)}: not a valid zip ({ex.Message})", ex);
        }
        using (zip)
        {
            string? ReadZip(string entry)
            {
                var e = zip.GetEntry(entry);
                if (e == null) return null;
                try
                {
                    using var r = new StreamReader(e.Open(), Encoding.UTF8);
                    return r.ReadToEnd();
                }
                catch (InvalidDataException ex)
                {
                    throw new InvalidDataException($"{entry}: unreadable ({ex.Message})", ex);
                }
            }
            return Assemble(ReadZip, zip.Entries.Select(e => e.FullName).ToList());
        }
    }

    private static List<string> ListDir(string root)
    {
        var list = new List<string>();
        foreach (var sub in new[] { "tables", "formulas" })
        {
            var d = Path.Combine(root, sub);
            if (!Directory.Exists(d)) continue;
            list.AddRange(Directory.GetFiles(d, "*.json").Select(f => sub + "/" + Path.GetFileName(f)));
        }
        return list;
    }

    private static RulesPack Assemble(Func<string, string?> read, List<string> entries)
    {
        var packText = read(PackEntry) ?? throw new InvalidDataException("pack.json missing");
        CheckFormat(packText);
        var head = Parse<RulesPack>(packText, PackEntry);

        List<T> ReadAll<T>(string dir, Func<T, string?> idOf)
        {
            var names = entries
                .Where(e => e.StartsWith(dir + "/", StringComparison.Ordinal) && e.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e, StringComparer.Ordinal);
            var res = new List<T>();
            foreach (var n in names)
            {
                var text = read(n);
                if (text == null) continue;
                var item = Parse<T>(text, n);
                if (string.IsNullOrEmpty(idOf(item))) throw new InvalidDataException($"{n}: missing id");
                res.Add(item);
            }
            return res;
        }

        var pack = Normalise(head with
        {
            Tables = ReadAll<TableDef>("tables", t => t.Id),
            Formulas = ReadAll<FormulaDef>("formulas", f => f.Id),
        });
        CheckIds(pack);
        CheckDupes("property", pack.Properties.Select(x => x.Id));
        CheckDupes("holder", pack.Holders.Select(x => x.Id));
        CheckDupes("ruleRef", pack.RuleRefs.Select(x => x.Id));
        CheckDupes("side", pack.Sides.Select(x => x.Id));
        CheckDupes("phase", pack.Phases.Select(x => x.Id));
        CheckDupes("alert", pack.Alerts.Select(x => x.Id));
        CheckDupes("table", pack.Tables.Select(x => x.Id));
        CheckDupes("formula", pack.Formulas.Select(x => x.Id));
        return pack;
    }

    private static void CheckFormat(string packText)
    {
        System.Text.Json.Nodes.JsonNode? node;
        try { node = System.Text.Json.Nodes.JsonNode.Parse(packText); }
        catch (JsonException ex) { throw new InvalidDataException($"{PackEntry}: invalid JSON ({ex.Message})", ex); }
        int? format = null;
        if (node is JsonObject o && o["format"] is JsonValue v && v.TryGetValue<int>(out var n)) format = n;
        if (format is > CurrentFormat)
            throw new InvalidDataException($"{PackEntry}: pack format {format} is newer than supported format {CurrentFormat}");
        if (format is null or < 1)
            throw new InvalidDataException($"{PackEntry}: unsupported format");
    }

    private static void CheckIds(RulesPack p)
    {
        static void Req(string where, string? id)
        {
            if (string.IsNullOrEmpty(id)) throw new InvalidDataException($"{where}: missing id");
        }
        foreach (var x in p.Properties) Req("pack.json property", x.Id);
        foreach (var x in p.Holders) Req("pack.json holder", x.Id);
        foreach (var x in p.RuleRefs) Req("pack.json ruleRef", x.Id);
        foreach (var x in p.Sides) Req("pack.json side", x.Id);
        foreach (var x in p.Phases) Req("pack.json phase", x.Id);
        foreach (var x in p.Alerts) Req("pack.json alert", x.Id);
    }

    private static void CheckDupes(string kind, IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
            if (!seen.Add(id)) throw new InvalidDataException($"Duplicate {kind} id '{id}'");
    }

    private static T Parse<T>(string text, string entry)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(text, JsonOptions) ?? throw new InvalidDataException($"{entry}: empty");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{entry}: invalid JSON ({ex.Message})", ex);
        }
    }

    /// <summary>Replaces nulls coming from JSON with empty values so collections are never null.</summary>
    private static RulesPack Normalise(RulesPack p) => p with
    {
        Id = p.Id ?? "",
        Name = p.Name ?? "",
        ModuleSha256 = p.ModuleSha256 ?? "",
        Properties = (p.Properties ?? new()).Where(x => x != null).Select(x => x with { Name = x.Name ?? "" }).ToList(),
        Holders = (p.Holders ?? new()).Where(x => x != null).Select(x => x with { Name = x.Name ?? "" }).ToList(),
        RuleRefs = (p.RuleRefs ?? new()).Where(x => x != null)
            .Select(x => x with { Label = x.Label ?? "", DocumentEntry = x.DocumentEntry ?? "" }).ToList(),
        Alerts = (p.Alerts ?? new()).Where(x => x != null)
            .Select(x => x with { Name = x.Name ?? "", Condition = x.Condition ?? "", Message = x.Message ?? "" }).ToList(),
        Sides = (p.Sides ?? new()).Where(x => x != null).Select(x => x with { Name = x.Name ?? "" }).ToList(),
        Blocks = (p.Blocks ?? new()).Where(x => x != null)
            .Select(x => x with { FirstPhaseId = x.FirstPhaseId ?? "", LastPhaseId = x.LastPhaseId ?? "", Label = x.Label ?? "" }).ToList(),
        Phases = (p.Phases ?? new()).Where(x => x != null).Select(x => x with
        {
            Name = x.Name ?? "",
            Instructions = x.Instructions ?? "",
            Checklist = (x.Checklist ?? new()).Select(c => c ?? "").ToList(),
            OnStart = (x.OnStart ?? new()).Where(e => e != null).Select(e => e with { FormulaId = e.FormulaId ?? "" }).ToList(),
            OnMove = (x.OnMove ?? new()).Where(e => e != null).Select(e => e with { FormulaId = e.FormulaId ?? "" }).ToList(),
            Warnings = (x.Warnings ?? new()).Where(w => w != null)
                .Select(w => w with { Condition = w.Condition ?? "", Message = w.Message ?? "" }).ToList(),
        }).ToList(),
        Tables = (p.Tables ?? new()).Where(x => x != null).Select(t => t with
        {
            Name = t.Name ?? "",
            RowKeys = (t.RowKeys ?? new()).Select(k => k ?? "").ToList(),
            ColumnKeys = (t.ColumnKeys ?? new()).Select(k => k ?? "").ToList(),
            Cells = (t.Cells ?? new()).Select(r => (r ?? new()).Select(c => c ?? "").ToList()).ToList(),
        }).ToList(),
        Formulas = (p.Formulas ?? new()).Where(x => x != null).Select(f => f with
        {
            Name = f.Name ?? "",
            Text = f.Text ?? "",
            Bindings = (f.Bindings ?? new()).Where(b => b != null).Select(b => b with { Variable = b.Variable ?? "" }).ToList(),
            Outputs = (f.Outputs ?? new()).Where(o => o != null).Select(o => o with { Property = o.Property ?? "" }).ToList(),
        }).ToList(),
    };

    private static byte[] Utf8Lf(string json) => Encoding.UTF8.GetBytes(json.Replace("\r\n", "\n") + "\n");

    /// <summary>Canonical file set Save writes, sorted by path.</summary>
    private static List<(string Path, byte[] Bytes)> BuildFiles(RulesPack pack)
    {
        CheckSaveIds(pack);
        var head = pack with
        {
            Format = CurrentFormat,
            Properties = pack.Properties.OrderBy(x => x.Id, StringComparer.Ordinal).ToList(),
            Holders = pack.Holders.OrderBy(x => x.Id, StringComparer.Ordinal).ToList(),
            RuleRefs = pack.RuleRefs.OrderBy(x => x.Id, StringComparer.Ordinal).ToList(),
        };
        // Tables and formulas live in their own files, so pack.json omits them.
        var node = JsonSerializer.SerializeToNode(head, JsonOptions)!.AsObject();
        node.Remove("tables");
        node.Remove("formulas");

        var files = new List<(string, byte[])> { (PackEntry, Utf8Lf(node.ToJsonString(JsonOptions))) };
        foreach (var t in pack.Tables) files.Add(($"tables/{SafeName(t.Id)}.json", Utf8Lf(JsonSerializer.Serialize(t, JsonOptions))));
        foreach (var f in pack.Formulas) files.Add(($"formulas/{SafeName(f.Id)}.json", Utf8Lf(JsonSerializer.Serialize(f, JsonOptions))));
        files.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return files;
    }

    private static readonly System.Text.RegularExpressions.Regex SafeNameRx = new("^[A-Za-z0-9._-]+$");
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static string SafeName(string id)
    {
        var stem = id.Split('.')[0];
        if (id.Length == 0 || !SafeNameRx.IsMatch(id) || id.EndsWith('.') || id.StartsWith('.') || Reserved.Contains(stem))
            throw new InvalidOperationException($"Id '{id}' cannot be used as a file name");
        return id;
    }

    private static void CheckSaveIds(RulesPack pack)
    {
        void Dupes(string kind, IEnumerable<string?> ids, StringComparer cmp)
        {
            var seen = new HashSet<string>(cmp);
            foreach (var id in ids)
            {
                if (string.IsNullOrEmpty(id)) throw new InvalidOperationException($"A {kind} has an empty id");
                if (!seen.Add(id)) throw new InvalidOperationException($"Duplicate {kind} id '{id}'");
            }
        }
        Dupes("property", pack.Properties.Select(x => x.Id), StringComparer.Ordinal);
        Dupes("holder", pack.Holders.Select(x => x.Id), StringComparer.Ordinal);
        Dupes("ruleRef", pack.RuleRefs.Select(x => x.Id), StringComparer.Ordinal);
        Dupes("side", pack.Sides.Select(x => x.Id), StringComparer.Ordinal);
        Dupes("phase", pack.Phases.Select(x => x.Id), StringComparer.Ordinal);
        Dupes("alert", pack.Alerts.Select(x => x.Id), StringComparer.Ordinal);
        foreach (var t in pack.Tables) SafeName(t.Id ?? "");
        foreach (var f in pack.Formulas) SafeName(f.Id ?? "");
        // Entries are compared case-insensitively because zips extract onto case-insensitive file systems.
        Dupes("table", pack.Tables.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
        Dupes("formula", pack.Formulas.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
    }

    public static string ComputeSha256(RulesPack pack)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (p, b) in BuildFiles(pack))
        {
            var name = Encoding.UTF8.GetBytes(p);
            h.AppendData(BitConverter.GetBytes(name.Length));
            h.AppendData(name);
            h.AppendData(BitConverter.GetBytes(b.Length));
            h.AppendData(b);
        }
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    public static void Save(RulesPack pack, string zipPath)
    {
        var files = BuildFiles(pack); // validates before any file is created
        var tmp = zipPath + ".tmp";
        try
        {
            using (var fs = File.Create(tmp))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var (p, b) in files)
                {
                    var e = zip.CreateEntry(p, CompressionLevel.Optimal);
                    e.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var s = e.Open();
                    s.Write(b);
                }
            }
            File.Move(tmp, zipPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}
