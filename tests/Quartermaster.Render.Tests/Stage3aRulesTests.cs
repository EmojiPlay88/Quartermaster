using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

public class Stage3aRulesTests : IDisposable
{
    private const string V = "VASSAL.build.";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-s3a-" + Guid.NewGuid().ToString("N"));

    public Stage3aRulesTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static OutlinerNode Node(string key, string name, OutlinerNodeKind kind, string? pieceId = null, double?[]? values = null, params OutlinerNode[] kids) =>
        new(key, name, kind, pieceId, null, 1, values ?? Array.Empty<double?>(), Array.Empty<AlertSeverity>(), kids);

    private static OutlinerTree Tree()
    {
        var p1 = Node("p:1", "Red 1", OutlinerNodeKind.Piece, "r1", new double?[] { 3 });
        var p2 = Node("p:2", "Red 2", OutlinerNodeKind.Piece, "r2", new double?[] { 4 });
        var grp = Node("map:Main Map", "Main Map", OutlinerNodeKind.Group, null, new double?[] { 7 }, p1, p2);
        var folder = Node("folder:Org", "Org", OutlinerNodeKind.Folder, null, new double?[] { 7 }, grp);
        return new OutlinerTree(new[] { new OutlinerColumn("fuel", "Fuel", OutlinerAggregate.Sum, null) }, new[] { folder });
    }

    // ---------- outliner ----------

    [Fact]
    public void Outliner_folders_open_groups_closed()
    {
        var t = Tree();
        Assert.True(OutlinerViewRules.StartsExpanded(t.Roots[0]));
        Assert.False(OutlinerViewRules.StartsExpanded(t.Roots[0].Children[0]));
        Assert.False(OutlinerViewRules.StartsExpanded(t.Roots[0].Children[0].Children[0]));
    }

    [Fact]
    public void Outliner_flatten_respects_expanded()
    {
        var t = Tree();
        var none = OutlinerViewRules.Flatten(t, new HashSet<string>());
        Assert.Equal(new[] { ("folder:Org", 0) }, none.Select(r => (r.Node.Key, r.Depth)));
        var folder = OutlinerViewRules.Flatten(t, new HashSet<string> { "folder:Org" });
        Assert.Equal(new[] { ("folder:Org", 0), ("map:Main Map", 1) }, folder.Select(r => (r.Node.Key, r.Depth)));
        var all = OutlinerViewRules.Flatten(t, new HashSet<string> { "folder:Org", "map:Main Map" });
        Assert.Equal(new[] { ("folder:Org", 0), ("map:Main Map", 1), ("p:1", 2), ("p:2", 2) }, all.Select(r => (r.Node.Key, r.Depth)));
    }

    private static ModuleModel OrgModel()
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            $"<{V}module.ChartWindow name=\"Charts\"><{V}widget.TabWidget entryName=\"Tab\"><{V}widget.WidgetMap mapName=\"Org A\"/></{V}widget.TabWidget></{V}module.ChartWindow></VASSAL.build.GameModule>");
        using var fx = new FixtureModule().WithBuildFile(xml).AddImage("board.png", 200, 100).AddSvg("unit.svg");
        using var a = VmodArchive.Open(fx.Build());
        return ModuleModelBuilder.Build(a, new DiagnosticLog());
    }

    [Fact]
    public void Outliner_activate_org_display_opens_map()
    {
        var model = OrgModel();
        var t = Tree();
        var piece = t.Roots[0].Children[0].Children[0];
        Assert.Equal(OutlinerActivation.CentrePiece, OutlinerViewRules.Activate(piece, model));
        // "Main Map" is a normal map (not an org display): the group just toggles.
        Assert.Equal(OutlinerActivation.Toggle, OutlinerViewRules.Activate(t.Roots[0].Children[0], model));
        Assert.Equal(OutlinerActivation.Toggle, OutlinerViewRules.Activate(t.Roots[0], model));
        var org = model.Maps.Single(m => m.IsOrgDisplay);
        var byId = Node("map:" + org.Id, org.Name, OutlinerNodeKind.Group);
        Assert.Equal(OutlinerActivation.OpenMap, OutlinerViewRules.Activate(byId, model));
        Assert.Same(org, OutlinerViewRules.OrgDisplayFor(byId, model));
        var byName = Node("map:" + org.Name, org.Name, OutlinerNodeKind.Group);
        Assert.Equal(OutlinerActivation.OpenMap, OutlinerViewRules.Activate(byName, model));
    }

    [Fact]
    public void Outliner_aggregate_title_visible_only_suffix()
    {
        var t = Tree();
        var col = t.Columns[0];
        var grp = t.Roots[0].Children[0];
        Assert.Equal("Fuel total 7  (Main Map)", OutlinerViewRules.AggregateTitle(col, grp, 7, false));
        Assert.Equal("Fuel total 7  (Main Map) — visible units only", OutlinerViewRules.AggregateTitle(col, grp, 7, true));
        Assert.Equal("Fuel lowest 3.5  (Main Map)", OutlinerViewRules.AggregateTitle(col with { Aggregate = OutlinerAggregate.Min }, grp, 3.5, false));
        Assert.Equal("Fuel average 3.5  (Main Map)", OutlinerViewRules.AggregateTitle(col with { Aggregate = OutlinerAggregate.Average }, grp, 3.5, false));
        var b = OutlinerViewRules.AggregateBreakdown(col, grp, 0, false)!;
        Assert.Equal(new[] { "Red 2", "Red 1" }, b.Lines.Select(l => l.Label));
        Assert.Equal("+4", b.Lines[0].Amount);
        Assert.StartsWith("Fuel total 7", b.Title);
    }

    [Fact]
    public void Outliner_severity_colours_and_now_texts()
    {
        Assert.Equal(((byte)178, (byte)34, (byte)34), OutlinerViewRules.SeverityColour(AlertSeverity.Critical));
        Assert.Equal(((byte)255, (byte)140, (byte)0), OutlinerViewRules.SeverityColour(AlertSeverity.Warning));
        Assert.Equal(((byte)70, (byte)130, (byte)180), OutlinerViewRules.SeverityColour(AlertSeverity.Info));
        Assert.Equal("Now", OutlinerViewRules.NowText);
        Assert.Equal("Now — 3", OutlinerViewRules.NowCountText(3));
        Assert.Equal("(nothing waiting)", OutlinerViewRules.NothingWaitingText);
    }

    // ---------- inventory ----------

    [Fact]
    public void Inventory_key_names_sorted_pieces_scope()
    {
        var model = OrgModel();
        var pack = new RulesPack
        {
            Format = 2, Id = "x", Name = "x", ModuleSha256 = "0",
            Properties = new()
            {
                new PropertyDef { Id = "zeta", Name = "Zeta", Scope = PropertyScope.Pieces },
                new PropertyDef { Id = "alpha", Name = "alpha", Scope = PropertyScope.Both },
                new PropertyDef { Id = "side", Name = "Side total", Scope = PropertyScope.Holders },
            },
        };
        Assert.Equal(new[] { "alpha", "Zeta" }, InventoryRules.KeyNames(pack, InventoryKey.LedgerProperty, model));
        Assert.Empty(InventoryRules.KeyNames(null, InventoryKey.LedgerProperty, model));
        Assert.Empty(InventoryRules.KeyNames(pack, InventoryKey.Name, model));
        Assert.Empty(InventoryRules.KeyNames(pack, InventoryKey.Map, model));
    }

    [Fact]
    public void Inventory_total_tip_text()
    {
        using var fx = PhaseFixture.Module();
        using var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "c3"));
        s.LoadSetup(s.Model.Setups[0]);
        var pack = PhaseFixture.Pack(s.Archive.Sha256);
        var view = s.Game!.State;
        foreach (var id in view.Pieces.Keys) view.Ledgers[LedgerKey.Piece(id)] = new OrderedDictionary<string, double> { ["fuel"] = 5 };
        var rows = InventoryQuery.Run(view, s.Model, pack, InventoryKey.LedgerProperty, "Fuel", "");
        var row = rows.First(r => r.Total != null);
        var tip = InventoryRules.TotalTip(row, "Fuel", view, pack, false);
        Assert.StartsWith($"Fuel {LedgerTable.FormatNumber(row.Total!.Value)}  ({row.Group}, {row.Count} {(row.Count == 1 ? "piece" : "pieces")})", tip);
        Assert.EndsWith(" — visible units only", InventoryRules.TotalTip(row, "Fuel", view, pack, true).Split('\n')[0]);
        Assert.Equal("", InventoryRules.TotalTip(row, "No such property", view, pack, false));
    }

    // ---------- diagnostics ----------

    private static IReadOnlyList<Diagnostic> Diags() => new[]
    {
        new Diagnostic(DiagnosticSeverity.Info, "c1", "info msg", null),
        new Diagnostic(DiagnosticSeverity.Warning, "c2", "warn msg", "ctx"),
        new Diagnostic(DiagnosticSeverity.Error, "c3", "err msg", null),
    };

    [Fact]
    public void Diagnostics_filter_levels()
    {
        var d = Diags();
        Assert.Equal(3, DiagnosticsView.Apply(d, DiagnosticsFilter.All).Count);
        Assert.Equal(new[] { "warn msg", "err msg" }, DiagnosticsView.Apply(d, DiagnosticsFilter.WarningsAndErrors).Select(x => x.Message));
        Assert.Equal(new[] { "err msg" }, DiagnosticsView.Apply(d, DiagnosticsFilter.ErrorsOnly).Select(x => x.Message));
        Assert.Equal(DiagnosticsFilter.WarningsAndErrors, DiagnosticsView.FilterFor(1));
        Assert.Equal(DiagnosticsFilter.ErrorsOnly, DiagnosticsView.FilterFor(2));
        Assert.Equal(DiagnosticsFilter.All, DiagnosticsView.FilterFor(0));
    }

    [Fact]
    public void Diagnostics_copy_text_tab_separated_and_empty()
    {
        var nl = Environment.NewLine;
        Assert.Equal("Warning\tc2\twarn msg\tctx" + nl + "Error\tc3\terr msg\t" + nl,
            DiagnosticsView.CopyText(DiagnosticsView.Apply(Diags(), DiagnosticsFilter.WarningsAndErrors)));
        Assert.Equal(" ", DiagnosticsView.CopyText(Array.Empty<Diagnostic>()));
    }

    [Fact]
    public void Status_link_text()
    {
        Assert.Equal("", DiagnosticsView.StatusLinkText(0, 0));
        Assert.Equal("3 warning(s)/error(s) - click here to view diagnostics", DiagnosticsView.StatusLinkText(2, 1));
    }

    // ---------- overview ----------

    [Fact]
    public void Overview_fit_margin_8()
    {
        var f = OverviewGeometry.Fit(new SizeD(200, 100), new SizeD(240, 200));
        Assert.Equal(1.16, f.Scale, 9);                 // min(232/200, 192/100) = 1.16
        Assert.Equal((240 - 200 * 1.16) / 2, f.OffsetX, 9);
        Assert.Equal((200 - 100 * 1.16) / 2, f.OffsetY, 9);
        var tiny = OverviewGeometry.Fit(new SizeD(200, 100), new SizeD(4, 4));
        Assert.Equal(1.0 / 200, tiny.Scale, 9);          // max(w - 8, 1) floor
    }

    [Fact]
    public void Overview_to_map_clamps()
    {
        var ext = new RectD(10, 20, 200, 100);
        var f = OverviewGeometry.Fit(ext, new SizeD(240, 200));
        var mid = OverviewGeometry.ToMap(new PointD(f.OffsetX + 100 * f.Scale, f.OffsetY + 50 * f.Scale), f, new SizeD(200, 100));
        Assert.Equal(110, mid.X, 6);
        Assert.Equal(70, mid.Y, 6);
        var far = OverviewGeometry.ToMap(new PointD(-500, 9999), f, new SizeD(200, 100));
        Assert.Equal(new PointD(10, 120), far);
        var r = OverviewGeometry.ViewportRect(new RectD(10, 20, 100, 50), f);
        Assert.Equal(f.OffsetX, r.X, 6);
        Assert.Equal(f.OffsetY, r.Y, 6);
        Assert.Equal(100 * f.Scale, r.W, 6);
        Assert.Equal(50 * f.Scale, r.H, 6);
    }

    [Fact]
    public void Overview_tile_level_is_coarsest()
    {
        var ts = new TileSet(Path.Combine(_tmp, "t"), 4000, 3000, 3);
        Assert.Equal(3, OverviewGeometry.TileLevel(ts));
    }

    // ---------- export ----------

    [Fact]
    public void Export_sanitize_file_name()
    {
        Assert.Equal("map", MapExportFlow.SanitizeFileName(""));
        Assert.Equal("map", MapExportFlow.SanitizeFileName("   "));
        Assert.Equal("Main Map", MapExportFlow.SanitizeFileName("  Main Map "));
        Assert.Equal("a_b", MapExportFlow.SanitizeFileName("a" + Path.GetInvalidFileNameChars()[0] + "b"));
    }

    [Fact]
    public void Export_zoom_choices()
    {
        Assert.Equal(0.25, MapExportFlow.ZoomForChoice(0));
        Assert.Equal(0.5, MapExportFlow.ZoomForChoice(1));
        Assert.Equal(1.0, MapExportFlow.ZoomForChoice(2));
        Assert.Equal(1.0, MapExportFlow.ZoomForChoice(7));
        Assert.Equal("An export is already running", MapExportFlow.AlreadyRunningText);
        Assert.Equal("There is no game in progress.", MapExportFlow.NoGameText);
    }

    [Fact]
    public async Task Export_cancel_returns_cancelled_and_no_file()
    {
        using var fx = PhaseFixture.Module();
        using var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "c4"));
        s.LoadSetup(s.Model.Setups[0]);
        var path = Path.Combine(_tmp, "out.png");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await MapExportFlow.RunWholeAsync(s, s.Model.Maps[0].Id, 0.5, path, null, cts.Token);
        Assert.False(r.Ok);
        Assert.True(r.Cancelled);
        Assert.Equal("Export cancelled", r.Message);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_tmp, "out.png*"));
    }

    [Fact]
    public async Task Export_writes_png_atomically()
    {
        using var fx = PhaseFixture.Module();
        using var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "c5"));
        s.LoadSetup(s.Model.Setups[0]);
        var path = Path.Combine(_tmp, "out.png");
        var reported = new List<double>();
        var r = await MapExportFlow.RunWholeAsync(s, s.Model.Maps[0].Id, 0.5, path, new SyncProgress(reported), CancellationToken.None);
        Assert.True(r.Ok, r.Message);
        Assert.False(r.Cancelled);
        Assert.Equal("Saved " + path, r.Message);
        Assert.True(File.Exists(path));
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(path).Take(4).ToArray());
        Assert.Empty(Directory.GetFiles(_tmp, "*.tmp"));
        Assert.NotEmpty(reported);
    }

    [Fact]
    public async Task Export_without_game_or_unknown_map_fails_with_text()
    {
        using var fx = PhaseFixture.Module();
        using var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "c6"));
        var r = await MapExportFlow.RunWholeAsync(s, s.Model.Maps[0].Id, 0.5, Path.Combine(_tmp, "n.png"), null, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(MapExportFlow.NoGameText, r.Message);
        s.LoadSetup(s.Model.Setups[0]);
        var r2 = await MapExportFlow.RunWholeAsync(s, "nope", 0.5, Path.Combine(_tmp, "n.png"), null, CancellationToken.None);
        Assert.False(r2.Ok);
        Assert.StartsWith("The picture could not be saved: ", r2.Message);
    }

    [Fact]
    public async Task Export_out_of_memory_during_render_returns_oom_text()
    {
        using var fx = PhaseFixture.Module();
        using var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "c7"));
        s.LoadSetup(s.Model.Setups[0]);
        var path = Path.Combine(_tmp, "oom.png");
        var r = await MapExportFlow.RunWholeAsync(s, s.Model.Maps[0].Id, 0.5, path, new ThrowingProgress(() => throw new OutOfMemoryException()), CancellationToken.None);
        Assert.False(r.Ok);
        Assert.False(r.Cancelled);
        Assert.Equal(MapExportFlow.OutOfMemoryText, r.Detail);
        Assert.Equal(MapExportFlow.FailurePrefix + MapExportFlow.OutOfMemoryText, r.Message);
        Assert.Empty(Directory.GetFiles(_tmp, "oom.png*"));
    }

    [Fact]
    public async Task Export_cancel_mid_render_leaves_no_file()
    {
        using var fx = PhaseFixture.Module();
        using var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "c8"));
        s.LoadSetup(s.Model.Setups[0]);
        var path = Path.Combine(_tmp, "mid.png");
        using var cts = new CancellationTokenSource();
        var r = await MapExportFlow.RunWholeAsync(s, s.Model.Maps[0].Id, 0.5, path, new ThrowingProgress(() => cts.Cancel()), cts.Token);
        Assert.False(r.Ok);
        Assert.True(r.Cancelled);
        Assert.Equal(MapExportFlow.CancelledText, r.Message);
        Assert.Empty(Directory.GetFiles(_tmp, "mid.png*"));
    }

    private sealed class ThrowingProgress(Action act) : IProgress<double>
    {
        public void Report(double value) => act();
    }

    private sealed class SyncProgress(List<double> sink) : IProgress<double>
    {
        public void Report(double value) { lock (sink) sink.Add(value); }
    }
}
