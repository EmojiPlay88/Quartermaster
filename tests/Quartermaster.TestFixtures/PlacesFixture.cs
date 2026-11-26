using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.TestFixtures;

/// <summary>
/// Invented Riverland module for places and keyed values (B6). Map "North": board "Riverside" (hex grid, 800x900) then board "Delta"
/// (hex grid, 300x200, so it repeats Riverside's low hex names such as "0102"); map "South": board "Square" (square grid). A palette slot
/// "Units/Test Unit" (gpid 1). Hex names are four digits, column then row ("0412").
/// </summary>
public static class PlacesFixture
{
    private const string V = "VASSAL.build.";
    private const string BP = "VASSAL.build.module.map.boardPicker.";
    private const string HexNum = "first=\"H\" hType=\"N\" vType=\"N\" hLeading=\"1\" vLeading=\"1\" hOff=\"1\" vOff=\"1\" stagger=\"true\"";

    private static string HexBoard(string name, string image) =>
        $"<{BP}Board name=\"{name}\" image=\"{image}\"><{BP}board.HexGrid x0=\"0\" y0=\"0\" dx=\"60\" dy=\"70\">" +
        $"<{BP}board.mapgrid.HexGridNumbering {HexNum}/></{BP}board.HexGrid></{BP}Board>";

    private static string SquareBoard(string name, string image) =>
        $"<{BP}Board name=\"{name}\" image=\"{image}\"><{BP}board.SquareGrid x0=\"25\" y0=\"25\" dx=\"50\" dy=\"50\">" +
        $"<{BP}board.mapgrid.SquareGridNumbering first=\"H\" hType=\"N\" vType=\"N\" sep=\"\" hLeading=\"1\" vLeading=\"1\" hOff=\"1\" vOff=\"1\"/></{BP}board.SquareGrid></{BP}Board>";

    private static FixtureModule Build()
    {
        var xml = $$"""
            <?xml version="1.0" encoding="UTF-8" standalone="no"?>
            <{{V}}GameModule name="Riverland" version="1" VassalVersion="3.7.1">
              <{{V}}module.PieceWindow defaultWidth="0" hidden="false" hotkey="" scale="1.0" text="Units" tooltip="" visible="true">
                <{{V}}widget.ListWidget entryName="Units">
                  <{{V}}widget.PieceSlot entryName="Test Unit" gpid="1" height="32" width="32">+/null/piece;;;unit.svg;Test Unit/null;0;0;1</{{V}}widget.PieceSlot>
                </{{V}}widget.ListWidget>
              </{{V}}module.PieceWindow>
              <{{V}}module.Map mapName="North"><{{V}}module.map.BoardPicker>
                {{HexBoard("Riverside", "river.png")}}
                {{HexBoard("Delta", "delta.png")}}
              </{{V}}module.map.BoardPicker></{{V}}module.Map>
              <{{V}}module.Map mapName="South"><{{V}}module.map.BoardPicker>
                {{SquareBoard("Square", "sq.png")}}
              </{{V}}module.map.BoardPicker></{{V}}module.Map>
            </{{V}}GameModule>
            """;
        return new FixtureModule().WithBuildFile(xml).AddImage("river.png", 800, 900).AddImage("delta.png", 300, 200)
            .AddImage("sq.png", 400, 400).AddSvg("unit.svg");
    }

    /// <summary>A fresh copy of the module as a .vmod for opening it in a session or window (the caller disposes it).</summary>
    public static FixtureModule Module() => Build();

    private static readonly Lazy<ModuleModel> LazyModel = new(() =>
    {
        using var fx = Build();
        using var a = VmodArchive.Open(fx.Build());
        return ModuleModelBuilder.Build(a, new DiagnosticLog());
    });

    public static ModuleModel Model => LazyModel.Value;

    public static BoardDef Board(string name) => Model.Maps.SelectMany(m => m.Boards).Single(b => b.Name == name);

    /// <summary>The map position of the centre of hex <paramref name="hex"/> on board <paramref name="board"/>.</summary>
    public static PointD At(string board, string hex)
    {
        var b = Board(board);
        var local = b.Grid!.LocationToPoint(hex) ?? throw new InvalidOperationException("no hex " + hex);
        return new PointD(b.Offset.X + local.X, b.Offset.Y + local.Y);
    }

    public static string MapOf(string board) => Model.Maps.Single(m => m.Boards.Any(b => b.Name == board)).Id;

    /// <summary>
    /// Properties: "FordDepth" (place: keyed by hex, Public, labels "Ford states"), "Mines" (place, Private), "BargesDue" (Holders, keyed by
    /// turn, Public), "HullCount" (Holders, keyed by table "Hull types", kept by "Boatyard", Public), "Cargo" (Pieces, keyed by table "Hull
    /// types", Private), "Strength" (Pieces, unkeyed, Public). Holders "Boatyard" (public) and "Blueyard" (side Blue). Sides Red and Blue by
    /// the Side marker.
    /// </summary>
    public static RulesPack Pack() => new()
    {
        Format = 4,
        Sides = new()
        {
            new SideDef { Id = "red", Name = "Red", UnitFilter = "marker(\"Side\") == \"Red\"" },
            new SideDef { Id = "blue", Name = "Blue", UnitFilter = "marker(\"Side\") == \"Blue\"" },
        },
        Properties = new()
        {
            new PropertyDef { Id = "p-ford", Name = "FordDepth", Scope = PropertyScope.Holders, KeyedBy = "hex", Labels = "t-ford", Visibility = Visibility.Public },
            new PropertyDef { Id = "p-mines", Name = "Mines", Scope = PropertyScope.Holders, KeyedBy = "hex", Visibility = Visibility.Private },
            new PropertyDef { Id = "p-due", Name = "BargesDue", Scope = PropertyScope.Holders, KeyedBy = "turn", Visibility = Visibility.Public },
            new PropertyDef
            {
                Id = "p-hulls", Name = "HullCount", Scope = PropertyScope.Holders, KeyedBy = "table:t-hulls", Holders = new() { "yard", "blueyard" },
                Visibility = Visibility.Public, Default = 1,
            },
            new PropertyDef { Id = "p-cargo", Name = "Cargo", Scope = PropertyScope.Pieces, KeyedBy = "table:t-hulls", Visibility = Visibility.Private },
            new PropertyDef { Id = "p-str", Name = "Strength", Scope = PropertyScope.Pieces, Visibility = Visibility.Public, Default = 2 },
        },
        Holders = new()
        {
            new HolderDef { Id = "yard", Name = "Boatyard" },
            new HolderDef { Id = "blueyard", Name = "Blueyard", SideId = "blue" },
        },
        Tables = new()
        {
            new TableDef
            {
                Id = "t-ford", Name = "Ford states", Kind = TableKind.OneD, RowKeys = new() { "1", "2", "3" }, ColumnKeys = new() { "Label" },
                Cells = new() { new() { "Shallow" }, new() { "Deep" }, new() { "Flooded" } },
            },
            new TableDef
            {
                Id = "t-hulls", Name = "Hull types", Kind = TableKind.OneD, RowKeys = new() { "Barge", "Raft", "Keelboat" }, ColumnKeys = new() { "Crew" },
                Cells = new() { new() { "4" }, new() { "2" }, new() { "6" } },
            },
        },
        Phases = new() { new PhaseDef { Id = "ph-move", Name = "Movement" } },
    };
}
