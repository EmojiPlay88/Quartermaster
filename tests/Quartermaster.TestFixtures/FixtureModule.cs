using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace Quartermaster.TestFixtures;

/// <summary>
/// Fluent builder for tiny synthetic .vmod files written to a temp path (deleted on Dispose).
/// Synthetic content only. When the build file is <see cref="MinimalBuildFile"/>, <see cref="Build"/>
/// adds the images it references (board.png 200x100, unit.svg) unless the caller already added them.
/// </summary>
public sealed class FixtureModule : IDisposable
{
    private readonly Dictionary<string, byte[]> _files = new();
    private string? _path;

    public FixtureModule AddImage(string name, int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        bmp.Erase(new SKColor(0x40, 0x80, 0xC0));
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        _files["images/" + name] = data.ToArray();
        return this;
    }

    public FixtureModule AddSvg(string name)
    {
        const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"32\" height=\"32\" viewBox=\"0 0 32 32\">" +
                           "<rect width=\"32\" height=\"32\" fill=\"#c04040\"/></svg>";
        _files["images/" + name] = Encoding.UTF8.GetBytes(svg);
        return this;
    }

    public FixtureModule WithBuildFile(string xml) => AddFile("buildFile.xml", Encoding.UTF8.GetBytes(xml));

    public FixtureModule AddSave(string entryName, IEnumerable<string> commands)
    {
        var plain = Encoding.UTF8.GetBytes(string.Join('\u001B', commands));
        var obf = ObfuscateHex(plain, 0x5A);
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using var s = z.CreateEntry("savedGame").Open();
            s.Write(obf);
        }
        return AddFile(entryName, ms.ToArray());
    }

    public FixtureModule AddFile(string name, byte[] bytes)
    {
        _files[name] = bytes;
        return this;
    }

    public string Build()
    {
        if (_files.TryGetValue("buildFile.xml", out var bf) && Encoding.UTF8.GetString(bf) == MinimalBuildFile())
        {
            if (!_files.ContainsKey("images/board.png")) AddImage("board.png", 200, 100);
            if (!_files.ContainsKey("images/unit.svg")) AddSvg("unit.svg");
        }

        if (_path != null) File.Delete(_path);
        _path = Path.Combine(Path.GetTempPath(), "qm-fixture-" + Guid.NewGuid().ToString("N") + ".vmod");
        using var fs = File.Create(_path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, bytes) in _files)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(bytes);
        }
        return _path;
    }

    public void Dispose()
    {
        if (_path != null && File.Exists(_path)) File.Delete(_path);
        _path = null;
    }

    /// <summary>A small, well-formed buildFile: one Map/Board/SquareGrid, one PieceSlot, one prototype.</summary>
    public static string MinimalBuildFile() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="no"?>
        <VASSAL.build.GameModule name="Fixture" version="1.0" VassalVersion="3.7.0">
          <VASSAL.build.module.PrototypesContainer>
            <VASSAL.build.module.PrototypeDefinition name="Proto">+/null/mark;Side/null</VASSAL.build.module.PrototypeDefinition>
          </VASSAL.build.module.PrototypesContainer>
          <VASSAL.build.module.PieceWindow defaultWidth="0" hidden="false" hotkey="" scale="1.0" text="Units" tooltip="" visible="true">
            <VASSAL.build.widget.ListWidget entryName="Units">
              <VASSAL.build.widget.PieceSlot entryName="Test Unit" gpid="1" height="32" width="32">+/null/piece;;;unit.svg;Test Unit/null;0;0;1</VASSAL.build.widget.PieceSlot>
            </VASSAL.build.widget.ListWidget>
          </VASSAL.build.module.PieceWindow>
          <VASSAL.build.module.Map mapName="Main Map">
            <VASSAL.build.module.map.BoardPicker>
              <VASSAL.build.module.map.boardPicker.Board image="board.png" name="Main Board" reversible="false">
                <VASSAL.build.module.map.boardPicker.board.SquareGrid color="0,0,0" dx="50" dy="50" edgesLegal="false" visible="true" x0="25" y0="25"/>
              </VASSAL.build.module.map.boardPicker.Board>
            </VASSAL.build.module.map.BoardPicker>
          </VASSAL.build.module.Map>
        </VASSAL.build.GameModule>
        """;

    // Test-only copy of the legacy hex format, kept independent of the production code under test.
    private static byte[] ObfuscateHex(byte[] plain, byte key)
    {
        var sb = new StringBuilder("!VCSK").Append(key.ToString("x2"));
        foreach (var b in plain) sb.Append(((byte)(b ^ key)).ToString("x2"));
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
