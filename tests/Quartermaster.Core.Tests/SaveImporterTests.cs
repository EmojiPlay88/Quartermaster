using Quartermaster.Core;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

public class SaveImporterTests
{
    private static string Xml(string mapName) =>
        FixtureModule.MinimalBuildFile().Replace("Main Map", mapName);

    private static string PieceType(string name) => "piece;;;unit.svg;" + name;
    private static string Add(string id, string type, string state) =>
        "+/" + SequenceEncoder.Encode(new[] { id, type, state }, '/');
    private static string Stack(string id, string state) => Add(id, "stack", state);

    private static (ImportResult r, DiagnosticLog log) Run(IEnumerable<string> commands, string map = "M")
    {
        using var fx = new FixtureModule().WithBuildFile(Xml(map)).AddSave("s.vsav", commands);
        var path = fx.Build();
        var log = new DiagnosticLog();
        using var arch = VmodArchive.Open(path);
        var model = ModuleModelBuilder.Build(arch, log);
        using var entry = arch.OpenEntry("s.vsav");
        var save = SaveFileReader.Read(entry);
        return (SaveImporter.Import(save, model, log), log);
    }

    [Fact]
    public void Import_PlacesPiecesAndStacks()
    {
        var (r, _) = Run(new[]
        {
            "begin_save", "",
            Add("p1", PieceType("A"), "M;100;50;1;0"),
            Add("p2", PieceType("B"), "M;100;50;1;0"),
            Add("p3", PieceType("C"), "M;7;8;1;0"),
            Stack("st1", "M;100;50;p1;p2;@@0"),
            "end_save",
        });
        Assert.Equal(3, r.PiecesImported);
        Assert.Equal(0, r.CommandsSkipped);
        var s = r.State;
        Assert.Equal(3, s.Pieces.Count);
        Assert.Equal(new[] { "p1", "p2" }, s.Stacks[s.Pieces["p1"].StackId!].PieceIds);
        Assert.Equal("M", s.Pieces["p1"].MapId);
        Assert.Equal(new PointD(100, 50), s.Pieces["p2"].Pos);
        Assert.Equal(s.Pieces["p1"].StackId, s.Pieces["p2"].StackId);
        Assert.NotEqual(s.Pieces["p1"].StackId, s.Pieces["p3"].StackId);
        Assert.Equal(new PointD(7, 8), s.Pieces["p3"].Pos);
        Assert.Equal(2, s.Stacks.Count);
        Assert.Contains("M", s.MapOrder);
    }

    [Fact]
    public void Import_StackBeforePieceOrder_StillResolves()
    {
        var (r, _) = Run(new[]
        {
            Stack("st1", "M;100;50;p1;p2;@@0"),
            Add("p1", PieceType("A"), "M;100;50;1;0"),
            Add("p2", PieceType("B"), "M;100;50;1;0"),
        });
        var s = r.State;
        Assert.Single(s.Stacks);
        Assert.Equal(new[] { "p1", "p2" }, s.Stacks.Values.First().PieceIds);
        Assert.Equal(2, r.PiecesImported);
    }

    [Fact]
    public void Import_UnknownCommand_SkippedAndLogged()
    {
        var (r, log) = Run(new[]
        {
            "ZZZ_UNKNOWN_COMMAND_ONE", "ZZZ_UNKNOWN_COMMAND_TWO", "PLAYER\tx", "MBoardPicker\tMain Board\t0\t0",
            Add("p1", PieceType("A"), "M;1;2;1;0"),
        });
        Assert.Equal(2, r.CommandsSkipped);
        Assert.Single(log.Items, d => d.Category == "save" && d.Message.Contains("ZZZ_UNKNOWN_"));
        Assert.Contains(log.Items, d => d.Severity == DiagnosticSeverity.Info && d.Message.Contains("PLAYER"));
        Assert.Contains(log.Items, d => d.Severity == DiagnosticSeverity.Info && d.Message.Contains("board"));
        Assert.Single(r.State.Pieces);
    }

    [Fact]
    public void Import_SetupStackMarkerAndLoneSeparator_NotSkipped()
    {
        var (r, log) = Run(new[]
        {
            "\\", "SETUP_STACK	", Add("p1", PieceType("A"), "M;1;2;1;0"),
        });
        Assert.Equal(0, r.CommandsSkipped);
        Assert.DoesNotContain(log.Items, d => d.Category == "save" && d.Severity >= DiagnosticSeverity.Warning);
        Assert.Contains(log.Items, d => d.Severity == DiagnosticSeverity.Info && d.Category == "save");
        Assert.Single(r.State.Pieces);
    }

    [Fact]
    public void Import_NullMapPiece_CountedAsOrphan()
    {
        var (r, log) = Run(new[] { Add("p1", PieceType("A"), "null;0;0;1;0"), Add("p2", PieceType("B"), "M;3;4;1;0") });
        Assert.Equal(2, r.PiecesImported);
        Assert.Null(r.State.Pieces["p1"].StackId);
        Assert.Equal("", r.State.Pieces["p1"].MapId);
        Assert.Contains(log.Items, d => d.Category == "save" && d.Message.Contains("1 piece") && d.Message.Contains("off-map"));
        Assert.Single(r.State.Stacks);
    }

    [Fact]
    public void Import_UnknownMap_KeptWithWarningOnce()
    {
        var (r, log) = Run(new[]
        {
            Add("p1", PieceType("A"), "Nowhere;1;1;1;0"),
            Add("p2", PieceType("B"), "Nowhere;2;2;1;0"),
        });
        Assert.Equal("Nowhere", r.State.Pieces["p1"].MapId);
        Assert.Single(log.Items, d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("Nowhere"));
    }

    [Fact]
    public void Import_PrototypeTypeIsExpanded()
    {
        var type = SequenceEncoder.Encode(new[] { "prototype;Proto", PieceType("A") }, '\t');
        var (r, _) = Run(new[] { Add("p1", type, SequenceEncoder.Encode(new[] { "", "M;1;2;1;0" }, '\t')) });
        var p = r.State.Pieces["p1"];
        var traits = PieceTypeParser.Parse(p.TypeString, new DiagnosticLog(), null).Traits;
        Assert.DoesNotContain(traits, t => t is PrototypeRef);
        Assert.Equal(new PointD(1, 2), p.Pos);
        Assert.Equal(traits.Count, p.TraitStates.Count);
    }

    [Fact]
    public void Import_OffMapStackWithCoordinateTokens_DoesNotWarnAboutThem()
    {
        // VASSAL Stack.getState always writes "map;x;y;ids"; its reader does not consume x/y for a null map and just fails to
        // find pieces "0" and "0". That is normal, not a data problem.
        var (r, log) = Run(new[]
        {
            "begin_save", "",
            Add("p1", PieceType("A"), "null;0;0;1;0"),
            Stack("st1", "null;0;0;p1;@@0"),
            "end_save",
        });
        Assert.Equal(1, r.PiecesImported);
        Assert.DoesNotContain(log.Items, d => d.Severity >= DiagnosticSeverity.Warning && d.Category == "save");
    }
}
