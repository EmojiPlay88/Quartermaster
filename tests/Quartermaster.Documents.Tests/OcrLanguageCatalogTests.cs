using System.Security.Cryptography;

namespace Quartermaster.Documents.Tests;

/// <summary>The committed tessdata_fast 4.1.0 catalog; reads only the embedded JSON and the shipped eng file.</summary>
public class OcrLanguageCatalogTests
{
    private const string EngSha = "7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2";

    [Fact]
    public void Eng_is_bundled_and_matches_the_shipped_file()
    {
        var eng = OcrLanguageCatalog.Find("eng");
        Assert.NotNull(eng);
        Assert.True(eng.Bundled);
        Assert.Equal(4113088, eng.Bytes);
        Assert.Equal(EngSha, eng.Sha256);

        var path = Path.Combine(AppContext.BaseDirectory, "tessdata", "eng.traineddata");
        Assert.Equal(eng.Bytes, new FileInfo(path).Length);
        Assert.Equal(eng.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
    }

    [Fact]
    public void Codes_are_unique_and_hashes_are_64_hex()
    {
        var entries = OcrLanguageCatalog.Entries;
        Assert.NotEmpty(entries);
        Assert.Equal(entries.Count, entries.Select(e => e.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var e in entries)
        {
            Assert.Matches("^[0-9a-f]{64}$", e.Sha256);
            Assert.True(e.Bytes > 0, e.Code);
            Assert.False(string.IsNullOrWhiteSpace(e.Name), e.Code);
        }
    }

    [Fact]
    public void Only_eng_is_bundled() =>
        Assert.Equal(["eng"], OcrLanguageCatalog.Entries.Where(e => e.Bundled).Select(e => e.Code));

    [Fact]
    public void Url_uses_the_pinned_tag() =>
        Assert.Equal("https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/deu.traineddata", OcrLanguageCatalog.Url("deu"));

    [Fact]
    public void Find_is_case_insensitive_and_null_for_unknown()
    {
        Assert.NotNull(OcrLanguageCatalog.Find("DEU"));
        Assert.Null(OcrLanguageCatalog.Find("xx_none"));
    }
}
