using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Quartermaster.Rules;

/// <summary>
/// .qrules IO. Layout: pack.json (metadata, properties, holders, ruleRefs), tables/{id}.json, formulas/{id}.json.
/// A pack may be a zip or a folder of the same layout.
/// </summary>
public static class RulesPackIo
{
    public const int CurrentFormat = 1;
    private const string PackEntry = "pack.json";

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
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
        var head = Parse<RulesPack>(packText, PackEntry);
        if (head.Format > CurrentFormat)
            throw new InvalidDataException($"{PackEntry}: pack format {head.Format} is newer than supported format {CurrentFormat}");

        List<T> ReadAll<T>(string dir)
        {
            var names = entries
                .Where(e => e.StartsWith(dir + "/", StringComparison.Ordinal) && e.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e, StringComparer.Ordinal);
            var res = new List<T>();
            foreach (var n in names)
            {
                var text = read(n);
                if (text != null) res.Add(Parse<T>(text, n));
            }
            return res;
        }

        var pack = Normalise(head with
        {
            Tables = ReadAll<TableDef>("tables"),
            Formulas = ReadAll<FormulaDef>("formulas"),
        });
        CheckDupes("property", pack.Properties.Select(x => x.Id));
        CheckDupes("holder", pack.Holders.Select(x => x.Id));
        CheckDupes("ruleRef", pack.RuleRefs.Select(x => x.Id));
        CheckDupes("table", pack.Tables.Select(x => x.Id));
        CheckDupes("formula", pack.Formulas.Select(x => x.Id));
        return pack;
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
        Properties = (p.Properties ?? new()).Where(x => x != null).ToList(),
        Holders = (p.Holders ?? new()).Where(x => x != null).ToList(),
        RuleRefs = (p.RuleRefs ?? new()).Where(x => x != null).ToList(),
        Tables = (p.Tables ?? new()).Where(x => x != null).Select(t => t with
        {
            RowKeys = t.RowKeys ?? new(),
            ColumnKeys = t.ColumnKeys ?? new(),
            Cells = (t.Cells ?? new()).Select(r => r ?? new()).ToList(),
        }).ToList(),
        Formulas = (p.Formulas ?? new()).Where(x => x != null).Select(f => f with
        {
            Bindings = (f.Bindings ?? new()).Where(b => b != null).ToList(),
            Outputs = (f.Outputs ?? new()).Where(o => o != null).ToList(),
        }).ToList(),
    };

    private static byte[] Utf8Lf(string json) => Encoding.UTF8.GetBytes(json.Replace("\r\n", "\n") + "\n");

    /// <summary>Canonical file set Save writes, sorted by path.</summary>
    private static List<(string Path, byte[] Bytes)> BuildFiles(RulesPack pack)
    {
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

    private static string SafeName(string id)
    {
        if (id.Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains(".."))
            throw new ArgumentException($"Unsafe id '{id}'");
        return id;
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
        var tmp = zipPath + ".tmp";
        try
        {
            using (var fs = File.Create(tmp))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var (p, b) in BuildFiles(pack))
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
