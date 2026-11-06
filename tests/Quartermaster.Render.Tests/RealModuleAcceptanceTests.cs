using System.Diagnostics;
using System.Text.Json;
using Quartermaster.Core;
using Quartermaster.Vassal;
using Xunit.Abstractions;

namespace Quartermaster.Render.Tests;

/// <summary>
/// Generic acceptance run against any real module. Does nothing unless QM_REAL_VMOD names a .vmod file.
/// Asserts invariants only; prints counts. No module-specific values belong here.
/// </summary>
public class RealModuleAcceptanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RealModule_SatisfiesInvariants()
    {
        var path = Environment.GetEnvironmentVariable("QM_REAL_VMOD");
        if (string.IsNullOrWhiteSpace(path))
        {
            output.WriteLine("SKIPPED: QM_REAL_VMOD is not set.");
            return;
        }

        var cache = Path.Combine(Path.GetTempPath(), "qm-accept-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var s = ModuleSession.Open(path, cache);
            var m = s.Model;
            output.WriteLine($"maps={m.Maps.Count(x => !x.IsOrgDisplay)} orgDisplays={m.Maps.Count(x => x.IsOrgDisplay)} " +
                             $"charts={m.Charts.Count} documents={m.Documents.Count} setups={m.Setups.Count}");

            foreach (var c in m.Charts)
                Assert.True(s.Archive.HasEntry(VmodArchive.ImagePath(c.FileName)) || s.Archive.HasEntry(c.FileName), "A chart file is missing from the archive.");
            foreach (var d in m.Documents)
                Assert.True(s.Archive.HasEntry(d.EntryName), "A document entry is missing from the archive.");

            var imported = 0;
            foreach (var setup in m.Setups.Where(x => x.FileEntry != null))
            {
                using var entry = s.Archive.OpenEntry(setup.FileEntry!);
                var save = SaveFileReader.Read(entry);
                var r = SaveImporter.Import(save, m, s.Log);
                output.WriteLine($"setup #{imported}: pieces={r.PiecesImported} skippedCommands={r.CommandsSkipped}");
                Assert.Equal(0, r.CommandsSkipped);
                imported++;

                var gs = new GameSession(r.State, s.Archive.Sha256);
                var file = Path.Combine(cache, $"rt{imported}.qsave");
                NativeSave.Write(file, gs);
                var back = NativeSave.Read(file);
                Assert.Equal(JsonSerializer.Serialize(gs.State, QuartermasterJson.Options),
                             JsonSerializer.Serialize(back.State, QuartermasterJson.Options));
            }

            var first = m.Maps.FirstOrDefault(x => !x.IsOrgDisplay) ?? m.Maps.FirstOrDefault();
            if (first != null)
            {
                var sw = Stopwatch.StartNew();
                foreach (var b in first.Boards)
                {
                    var ts = await s.Tiler.EnsureTilesAsync(b, null, CancellationToken.None);
                    Assert.True(ts.Width > 0 && ts.Height > 0);
                }
                output.WriteLine($"tiling: boards={first.Boards.Count} seconds={sw.Elapsed.TotalSeconds:F1}");
            }

            foreach (var g in s.Log.Items.GroupBy(d => (d.Severity, d.Category)).OrderBy(g => g.Key.Severity))
                output.WriteLine($"diagnostics {g.Key.Severity}/{g.Key.Category}: {g.Count()}");
            Assert.DoesNotContain(s.Log.Items, d => d.Severity == DiagnosticSeverity.Error);
        }
        finally
        {
            try { Directory.Delete(cache, true); } catch (IOException) { }
        }
    }
}
