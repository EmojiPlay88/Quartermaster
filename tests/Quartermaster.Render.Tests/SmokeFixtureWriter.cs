using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>
/// Not a real test: when QM_WRITE_SMOKE_FIXTURE names a folder it writes phases.vmod and phases.qrules there for the app smokes.
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
    }
}
