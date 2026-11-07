using System.IO.Compression;
using System.Xml;

namespace Quartermaster.Vassal;

/// <summary>A module's display title and version, read cheaply from its metadata without parsing the whole build file.</summary>
public sealed record ModuleInfo(string Title, string Version)
{
    /// <summary>
    /// Reads the <c>moduledata</c> entry (&lt;name&gt;, &lt;version&gt;); when it is missing or unusable, the root element's
    /// <c>name</c>/<c>version</c> attributes from the build file (first element only). Falls back to the file name. Never throws.
    /// </summary>
    public static ModuleInfo Read(string vmodPath)
    {
        var fallback = new ModuleInfo(SafeFileName(vmodPath), "");
        try
        {
            using var fs = new FileStream(vmodPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            if (Find(zip, "moduledata") is { } md && FromModuleData(md) is { } a) return a;
            foreach (var n in new[] { "buildFile.xml", "buildFile" })
                if (Find(zip, n) is { } bf && FromBuildFile(bf) is { } b) return b;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return fallback;
    }

    static string SafeFileName(string p)
    {
        try { return Path.GetFileNameWithoutExtension(p); } catch (ArgumentException) { return p; }
    }

    static ZipArchiveEntry? Find(ZipArchive zip, string name) =>
        zip.Entries.FirstOrDefault(e => e.FullName == name) ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));

    static readonly XmlReaderSettings Safe = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };

    static ModuleInfo? FromModuleData(ZipArchiveEntry e)
    {
        try
        {
            using var s = e.Open();
            using var r = XmlReader.Create(s, Safe);
            string? name = null, version = null;
            r.Read();
            while (!r.EOF)
            {
                if (r.NodeType == XmlNodeType.Element && r.Depth == 1 && r.Name == "name") name = r.ReadElementContentAsString().Trim();
                else if (r.NodeType == XmlNodeType.Element && r.Depth == 1 && r.Name == "version") version = r.ReadElementContentAsString().Trim();
                else r.Read();
            }
            return string.IsNullOrEmpty(name) ? null : new ModuleInfo(name, version ?? "");
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException) { return null; }
    }

    static ModuleInfo? FromBuildFile(ZipArchiveEntry e)
    {
        try
        {
            using var s = e.Open();
            using var r = XmlReader.Create(s, Safe);
            if (!r.Read()) return null;
            while (r.NodeType != XmlNodeType.Element) if (!r.Read()) return null;
            var name = r.GetAttribute("name");
            return string.IsNullOrWhiteSpace(name) ? null : new ModuleInfo(name.Trim(), (r.GetAttribute("version") ?? "").Trim());
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException) { return null; }
    }
}
