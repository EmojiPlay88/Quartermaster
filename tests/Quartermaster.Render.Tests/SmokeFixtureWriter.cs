using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render.Tests;

/// <summary>
/// Not a real test: when QM_WRITE_SMOKE_FIXTURE names a folder it writes phases.vmod, phases.qrules and documents.vmod there for the app smokes.
/// The module carries an invented two-page rulebook PDF (generated here) for the rulebook search smoke.
/// Does nothing when the variable is unset. The written files are never committed.
/// </summary>
public class SmokeFixtureWriter
{
    public const string RulebookEntry = "invented-rules.pdf";

    /// <summary>Two invented pages: a contents page that mentions supply, and the page that explains it.</summary>
    internal static byte[] InventedRulebook() => RulebookSearchTests.Pdf(
        new[] { "Invented Rules - Contents", "Supply and movement are covered on the next page of this invented booklet." },
        new[] { "Supply", "Each unit traces supply to a depot at the start of its turn.", "A unit out of supply moves at half speed." });

    [Fact]
    public void WriteSmokeFixture_WhenEnvSet()
    {
        var dir = Environment.GetEnvironmentVariable("QM_WRITE_SMOKE_FIXTURE");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var fx = PhaseFixture.Module()
            .EditBuildFile(x => x.Replace("</VASSAL.build.GameModule>",
                "<VASSAL.build.module.Documentation><VASSAL.build.module.documentation.BrowserPDFFile title=\"Invented rules\" pdfFile=\""
                + RulebookEntry + "\"/></VASSAL.build.module.Documentation></VASSAL.build.GameModule>"))
            .AddFile(RulebookEntry, InventedRulebook());
        var built = fx.Build();
        var vmod = Path.Combine(dir, "phases.vmod");
        File.Copy(built, vmod, true);
        string sha;
        using (var a = VmodArchive.Open(vmod)) sha = a.Sha256;
        RulesPackIo.Save(PhaseFixture.Pack(sha), Path.Combine(dir, "phases.qrules"));
        Assert.True(File.Exists(Path.Combine(dir, "phases.qrules")));

        // documents.vmod: a minimal module with the invented rulebook and an invented chart picture, for build/port-screenshots.ps1 (chart / document states).
        var docXml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            "<VASSAL.build.module.Documentation><VASSAL.build.module.documentation.BrowserPDFFile title=\"Invented rules\" pdfFile=\""
            + RulebookEntry + "\"/></VASSAL.build.module.Documentation>"
            + "<VASSAL.build.module.ChartWindow name=\"Charts\"><VASSAL.build.widget.Chart chartName=\"Odds Table\" fileName=\"odds.png\"/></VASSAL.build.module.ChartWindow>"
            + "</VASSAL.build.GameModule>");
        using var docFx = new FixtureModule().WithBuildFile(docXml).AddImage("odds.png", 640, 400, new SKColor(0xC0, 0x80, 0x40)).AddFile(RulebookEntry, InventedRulebook());
        File.Copy(docFx.Build(), Path.Combine(dir, "documents.vmod"), true);
        Assert.True(File.Exists(Path.Combine(dir, "documents.vmod")));
    }
}
