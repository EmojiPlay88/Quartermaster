using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

/// <summary>B1 displays in Render: a Ledger edit of a value a holder does not keep, labels in outliner values and popup ledger lines. Invented data.</summary>
[Collection(AppDataRootEnv.Name)]
public sealed class LedgerViewOutlinerPopupLabelTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-b1-labels-" + Guid.NewGuid().ToString("N"));
    readonly List<FixtureModule> _fx = new();
    readonly List<ModuleSession> _ss = new();

    public void Dispose()
    {
        foreach (var s in _ss) s.Dispose();
        foreach (var f in _fx) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Demo(string file, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", file));

    static TableDef Names(string id, string name, params string[] labels) => new()
    {
        Id = id, Name = name, Kind = TableKind.OneD,
        RowKeys = Enumerable.Range(1, labels.Length).Select(i => i.ToString()).ToList(), ColumnKeys = new() { "Name" },
        Cells = labels.Select(l => new List<string> { l }).ToList(),
    };

    [Fact]
    public void Ledger_edit_not_kept_refused()
    {
        var fx = PhaseFixture.Module();
        _fx.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _ss.Add(s);
        s.LoadSetup(s.Model.Setups[0]);
        s.NewRules("t");
        s.UpdateRules(_ => PhaseFixture.Pack(s.Archive.Sha256) with
        {
            Holders = new() { new HolderDef { Id = "north", Name = "North depot" }, new HolderDef { Id = "south", Name = "South depot" } },
            Properties = PhaseFixture.Pack(s.Archive.Sha256).Properties
                .Append(new PropertyDef { Id = "tolls", Name = "Ferry tolls", Scope = PropertyScope.Holders, Holders = new() { "north" } }).ToList(),
        });
        var c = LedgerViewRules.CheckManualEdit(s, LedgerKey.Holder("south"), "tolls", null, "4", "Ferry tolls");
        Assert.False(c.Ok);
        Assert.True(c.Reload);
        Assert.Equal("South depot does not keep Ferry tolls", c.Error);
        var g = LedgerViewRules.CheckManualEdit(s, LedgerKey.Holder(HolderDef.GlobalId), "tolls", null, "4", "Ferry tolls");
        Assert.Equal("Global does not keep Ferry tolls", g.Error);
        var ok = LedgerViewRules.CheckManualEdit(s, LedgerKey.Holder("north"), "tolls", null, "4", "Ferry tolls");
        Assert.True(ok.Ok);
        Assert.Null(ok.Error);
    }

    [Fact]
    public void Outliner_value_shows_label()
    {
        var pack = new RulesPack
        {
            Properties = new() { new PropertyDef { Id = "wx", Name = "Weather", Scope = PropertyScope.Pieces, Labels = "t-wx" } },
            Tables = new() { Names("t-wx", "Weather names", "Clear", "Rain", "Fog") },
        };
        OutlinerNode Node(OutlinerNodeKind kind, double? v) =>
            new("k", "n", kind, kind == OutlinerNodeKind.Piece ? "p1" : null, null, 1, new[] { v }, Array.Empty<AlertSeverity>(), Array.Empty<OutlinerNode>());
        var sum = new OutlinerColumn("wx", "Weather", OutlinerAggregate.Sum, null);
        var min = new OutlinerColumn("wx", "Weather", OutlinerAggregate.Min, null);
        Assert.Equal("3 (Fog)", OutlinerViewRules.ValueText(pack, sum, Node(OutlinerNodeKind.Piece, 3), 0));
        Assert.Equal("2 (Rain)", OutlinerViewRules.ValueText(pack, min, Node(OutlinerNodeKind.Group, 2), 0));   // a lowest value is a value
        Assert.Equal("3", OutlinerViewRules.ValueText(pack, sum, Node(OutlinerNodeKind.Group, 3), 0));          // a total is not
        Assert.Equal("9", OutlinerViewRules.ValueText(pack, sum, Node(OutlinerNodeKind.Piece, 9), 0));          // no row matches
        Assert.Equal("", OutlinerViewRules.ValueText(pack, sum, Node(OutlinerNodeKind.Piece, null), 0));
        Assert.Equal("1.5", OutlinerViewRules.ValueText(null, sum, Node(OutlinerNodeKind.Piece, 1.5), 0));
    }

    [Fact]
    public void Popup_ledger_line_shows_label()
    {
        var s = ModuleSession.Open(Demo("Riverland-Demo.vmod"), Path.Combine(_tmp, "cache2"), null);
        _ss.Add(s);
        s.StartNewGame(Difficulty.FullyAuto);
        s.OpenRules(Demo("Riverland-Demo.qrules"));
        s.UpdateRules(p => p with
        {
            Tables = p.Tables.Append(Names("t-ammo", "Ammo names TEST", "Low", "Half", "Full")).ToList(),
            Properties = p.Properties.Select(x => x.Id == "ammo" ? x with { Labels = "t-ammo" } : x).ToList(),
        });
        var stack = new StackInstance { Id = "b1-1", MapId = s.Model.Maps[0].Id, PieceIds = { "n:1" } };
        var l = CounterPopupLayout.Build(s, s.View!, stack, new PieceRenderer(s.Images));
        Assert.Contains("Ammo: 3 (Full)", l.Lines);
        Assert.Contains("Fuel: 3", l.Lines);
        Assert.StartsWith("Ammo 3 (Full)", l.HotLines[0].Tip.Title);
    }
}
