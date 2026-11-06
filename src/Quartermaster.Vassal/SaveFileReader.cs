using System.IO.Compression;
using System.Text;

namespace Quartermaster.Vassal;

public sealed record SaveFile(IReadOnlyList<string> Commands);

/// <summary>Reads a VASSAL .vsav (a zip holding a <c>savedGame</c> command log).</summary>
public static class SaveFileReader
{
    public const char CommandSeparator = '\u001B';
    private const string EntryName = "savedGame";

    /// <exception cref="InvalidDataException">The stream is not a zip or has no <c>savedGame</c> entry.</exception>
    public static SaveFile Read(Stream vsavZip)
    {
        using var zip = new ZipArchive(vsavZip, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry(EntryName)
            ?? throw new InvalidDataException($"Save file has no '{EntryName}' entry.");

        using var ms = new MemoryStream();
        using (var s = entry.Open()) s.CopyTo(ms);

        var text = Encoding.UTF8.GetString(Obfuscation.Deobfuscate(ms.ToArray()));
        return new SaveFile(text.Split(CommandSeparator));
    }
}
