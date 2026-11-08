using System.Globalization;
using System.Security;
using System.Text;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.DemoModule;

/// <summary>
/// The invented "Riverland" demo: a small hex map, two sides of self-drawn counters, a turn track and a rules pack, used by every wiki
/// example. All names, numbers and art are made up. Output is deterministic: building twice gives byte-identical files.
/// </summary>
public static class DemoContent
{
    public const string North = "Northern League";
    public const string South = "Southern Compact";
    public const string VmodName = "Riverland-Demo.vmod";
    public const string RulesName = "Riverland-Demo.qrules";
    public const string MapName = "Riverland Map";
    public const string BoardName = "Riverland";

    private const string NorthColour = "#3f6fb5";
    private const string SouthColour = "#b5543f";
    private const int BoardW = 700, BoardH = 650;
    private const int Cols = 12, Rows = 8, TurnBoxes = 6;
    private const double HexH = 64.0;
    private static readonly double HexDx = Math.Sqrt(3) / 2 * HexH;   // column spacing (VASSAL's default for dy=64)
    private static readonly double HexSide = HexH / Math.Sqrt(3);      // flat-topped hex: centre to left/right corner
    private const int X0 = 40, Y0 = 40;

    /// <summary>One counter: palette name, image, markers and (units only) starting Fuel / Ammo / Readiness, set up at hex (Col, Row).</summary>
    public sealed record Counter(string Name, string Code, string Type, string Side, string Kind, int Col, int Row, int Fuel = 0, int Ammo = 0, int Readiness = 0)
    {
        public string Image => Code.ToLowerInvariant().Replace(' ', '-') + ".svg";
        public bool IsUnit => Kind == "Unit";
    }

    public static IReadOnlyList<Counter> Counters { get; } = new Counter[]
    {
        new("NL 1 Rifles", "NL-1", "RIF", North, "Unit", 1, 1, 3, 3, 3),
        new("NL 2 Rifles", "NL-2", "RIF", North, "Unit", 2, 1, 3, 3, 3),
        new("NL 3 Rifles", "NL-3", "RIF", North, "Unit", 3, 1, 3, 3, 3),
        new("NL 4 Armour", "NL-4", "ARM", North, "Unit", 4, 1, 4, 2, 2),
        new("NL 5 Armour", "NL-5", "ARM", North, "Unit", 5, 2, 4, 2, 2),
        new("NL 6 Recon", "NL-6", "REC", North, "Unit", 6, 1, 1, 2, 4),   // starts low on fuel, so the Low fuel alert shows at once
        new("NL 7 Guns", "NL-7", "GUN", North, "Unit", 7, 1, 2, 4, 2),
        new("NL Depot", "NL-D", "DEPOT", North, "Depot", 2, 0),
        new("SC 1 Rifles", "SC-1", "RIF", South, "Unit", 4, 6, 3, 3, 3),
        new("SC 2 Rifles", "SC-2", "RIF", South, "Unit", 5, 6, 3, 3, 3),
        new("SC 3 Armour", "SC-3", "ARM", South, "Unit", 6, 6, 4, 2, 2),
        new("SC 4 Armour", "SC-4", "ARM", South, "Unit", 7, 6, 4, 2, 2),
        new("SC 5 Recon", "SC-5", "REC", South, "Unit", 8, 6, 3, 2, 4),
        new("SC 6 Guns", "SC-6", "GUN", South, "Unit", 9, 5, 2, 4, 2),
        new("SC 7 Engineers", "SC-7", "ENG", South, "Unit", 10, 6, 2, 2, 3),
        new("SC Depot", "SC-D", "DEPOT", South, "Depot", 9, 7),
        new("Turn", "Turn", "TURN", "None", "Marker", -1, -1),
    };

    /// <summary>Writes <see cref="VmodName"/> and <see cref="RulesName"/> into <paramref name="outDir"/> (created if needed).</summary>
    public static (string VmodPath, string RulesPath) Build(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var vmodPath = Path.Combine(outDir, VmodName);
        var rulesPath = Path.Combine(outDir, RulesName);

        var grid = BuildGrid();
        using (var fx = new FixtureModule())
        {
            fx.WithBuildFile(BuildFile(grid));
            fx.AddFile("images/riverland-map.svg", Utf8(BoardSvg(grid)));
            foreach (var c in Counters) fx.AddFile("images/" + c.Image, Utf8(CounterSvg(c)));
            fx.AddFile("riverland-rules.txt", Utf8(RulesText));
            File.Copy(fx.Build(), vmodPath, overwrite: true);
        }

        var pack = Pack(VmodArchive.ComputeSha256(vmodPath));
        File.WriteAllBytes(rulesPath, RulesPackIo.ToBytes(pack));
        return (vmodPath, rulesPath);
    }

    // ---- rules pack ----

    /// <summary>The demo rules pack for a module with the given SHA-256.</summary>
    public static RulesPack Pack(string moduleSha) => RulesPackIo.CreateEmpty("Riverland demo rules", moduleSha) with
    {
        Id = "riverland-demo",
        Properties = new()
        {
            new PropertyDef
            {
                Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, SheetField = "Fuel", AppliesTo = "marker(\"Kind\") == \"Unit\"",
                Outliner = OutlinerAggregate.Sum, Visibility = Visibility.Public, Notes = "Fuel points. Moving costs fuel; supply refills it (at most 6).",
            },
            new PropertyDef
            {
                Id = "ammo", Name = "Ammo", Scope = PropertyScope.Pieces, SheetField = "Ammo", AppliesTo = "marker(\"Kind\") == \"Unit\"",
                Outliner = OutlinerAggregate.Sum, Visibility = Visibility.Public, Notes = "Ammunition points. Firing costs 1; supply refills it (at most 4).",
            },
            new PropertyDef
            {
                Id = "readiness", Name = "Readiness", Scope = PropertyScope.Pieces, SheetField = "Readiness", AppliesTo = "marker(\"Kind\") == \"Unit\"",
                Outliner = OutlinerAggregate.Average, Visibility = Visibility.Private,
                Notes = "How ready the unit is to fight. Private: only the side that owns the unit sees it.",
            },
        },
        Tables = new()
        {
            new TableDef
            {
                Id = "t-supply", Name = "SupplyByDistance", Kind = TableKind.OneD,
                RowKeys = new() { "0", "1-2", "3-4", "5+" }, ColumnKeys = new() { "Value" },
                Cells = new() { new() { "3" }, new() { "2" }, new() { "1" }, new() { "0" } },
                Notes = "Supply points a unit receives, by hexes to its own depot.",
            },
        },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "f-fuel-supply", Name = "Fuel supply", Text = "min(Fuel + lookup(SupplyByDistance, Hexes), 6)", AppliesTo = "has(Fuel)",
                Bindings = new() { UnitVar("Fuel", "fuel"), HexesInput() },
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Op = OutputOp.Set } },
                Notes = "Adds supply by distance to the depot, up to 6.",
            },
            new FormulaDef
            {
                Id = "f-ammo-supply", Name = "Ammo supply", Text = "min(Ammo + Gain, 4)", AppliesTo = "has(Ammo)",
                Bindings = new()
                {
                    UnitVar("Ammo", "ammo"),
                    new VariableBinding { Variable = "Gain", Kind = BindingKind.Table, TableId = "t-supply", RowExpr = "Hexes" },
                    HexesInput(),
                },
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "ammo", Op = OutputOp.Set } },
                Notes = "Same table as fuel, read through a table variable; at most 4.",
            },
            new FormulaDef
            {
                Id = "f-move-fuel", Name = "Fuel for movement", Text = "ceil(Moved / 2)", AppliesTo = "has(Fuel)",
                Bindings = new() { new VariableBinding { Variable = "Moved", Kind = BindingKind.Moved } },
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Op = OutputOp.Subtract } },
                Notes = "One fuel per two hexes moved, rounded up.",
            },
            new FormulaDef
            {
                Id = "f-attack", Name = "Attack roll", Text = "Die + floor(Readiness / 2)", AppliesTo = "has(Readiness)",
                Bindings = new()
                {
                    new VariableBinding { Variable = "Die", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 },
                    UnitVar("Readiness", "readiness"),
                },
                Notes = "One die plus half the unit's readiness. 6 or more wins the fight.",
            },
            new FormulaDef
            {
                Id = "f-fire-ammo", Name = "Ammo for firing", Text = "if(Ammo > 0, 1, 0)", AppliesTo = "has(Ammo)",
                Bindings = new() { UnitVar("Ammo", "ammo") },
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "ammo", Op = OutputOp.Subtract } },
                Notes = "Firing uses 1 ammo (nothing when the unit has none left).",
            },
        },
        Sides = new()
        {
            new SideDef { Id = "north", Name = North, UnitFilter = "marker(\"Side\") == \"" + North + "\"" },
            new SideDef { Id = "south", Name = South, UnitFilter = "marker(\"Side\") == \"" + South + "\"" },
        },
        Phases = new()
        {
            new PhaseDef
            {
                Id = "ph-supply", Name = "Supply",
                Instructions = "Every unit draws fuel and ammo from its own depot. The nearer the depot, the more it gets.",
                OnStart = new()
                {
                    new StartEntry { FormulaId = "f-fuel-supply", ForEach = ForEachMode.Units, UnitFilter = "has(Fuel)" },
                    new StartEntry { FormulaId = "f-ammo-supply", ForEach = ForEachMode.Units, UnitFilter = "has(Ammo)" },
                },
            },
            new PhaseDef
            {
                Id = "ph-movement", Name = "Movement",
                Instructions = "Move units. Every two hexes moved cost one fuel. Units with no fuel cannot move.",
                OnMove = new() { new MoveEntry { FormulaId = "f-move-fuel", UnitFilter = "has(Fuel)" } },
                Warnings = new() { new PhaseWarning { Condition = "Fuel <= 0", Message = "{unit} has no fuel and cannot move" } },
            },
            new PhaseDef
            {
                Id = "ph-combat", Name = "Combat",
                Instructions = "Units next to an enemy may attack. Roll the Attack roll; 6 or more wins.",
                Checklist = new() { "Choose attackers", "Run Attack roll for each attacker", "Run Ammo for firing for each attacker" },
                Warnings = new() { new PhaseWarning { Condition = "Ammo <= 0", Message = "{unit} is out of ammo" } },
            },
        },
        Alerts = new()
        {
            new AlertDef { Id = "a-low-fuel", Name = "Low fuel", Condition = "Fuel < 2", Message = "{unit} is low on fuel", Severity = AlertSeverity.Warning },
        },
    };

    private static VariableBinding UnitVar(string name, string property) =>
        new() { Variable = name, Kind = BindingKind.Unit, Property = property };

    private static VariableBinding HexesInput() => new()
    {
        Variable = "Hexes", Kind = BindingKind.Input, Min = 0, Max = 20, AutoValue = "2", Caption = "Hexes from this unit to its depot",
    };

    // ---- module ----

    private static string BuildFile(IMapGrid grid)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"no\"?>\n");
        sb.Append("<VASSAL.build.GameModule name=\"Riverland\" version=\"1.0\" VassalVersion=\"3.7.0\" description=\"Invented demo module for the Quartermaster wiki\">\n");
        sb.Append("  <VASSAL.build.module.PlayerRoster>\n");
        sb.Append("    <entry>").Append(X(North)).Append("</entry>\n");
        sb.Append("    <entry>").Append(X(South)).Append("</entry>\n");
        sb.Append("  </VASSAL.build.module.PlayerRoster>\n");
        sb.Append("  <VASSAL.build.module.Documentation>\n");
        sb.Append("    <VASSAL.build.module.documentation.HelpFile fileName=\"riverland-rules.txt\" fileType=\"archive\" title=\"Riverland Rules\"/>\n");
        sb.Append("  </VASSAL.build.module.Documentation>\n");
        sb.Append("  <VASSAL.build.module.PieceWindow hidden=\"false\" name=\"Counters\" scale=\"1.0\" text=\"Counters\" tooltip=\"\">\n");
        sb.Append("    <VASSAL.build.widget.TabWidget entryName=\"Counters\">\n");
        var gpid = 1;
        foreach (var group in new[] { North, South, "None" })
        {
            sb.Append("      <VASSAL.build.widget.ListWidget entryName=\"").Append(X(group == "None" ? "Markers" : group)).Append("\">\n");
            foreach (var c in Counters.Where(c => c.Side == group))
                sb.Append("        ").Append(Slot(c, gpid++, "null", 0, 0)).Append('\n');
            sb.Append("      </VASSAL.build.widget.ListWidget>\n");
        }
        sb.Append("    </VASSAL.build.widget.TabWidget>\n");
        sb.Append("  </VASSAL.build.module.PieceWindow>\n");

        sb.Append("  <VASSAL.build.module.Map mapName=\"").Append(MapName).Append("\" backgroundcolor=\"255,255,255\" markMoved=\"Always\">\n");
        sb.Append("    <VASSAL.build.module.map.BoardPicker>\n");
        sb.Append("      <VASSAL.build.module.map.boardPicker.Board image=\"riverland-map.svg\" name=\"").Append(BoardName).Append("\" reversible=\"false\">\n");
        sb.Append(GridXml());
        sb.Append("      </VASSAL.build.module.map.boardPicker.Board>\n");
        sb.Append("    </VASSAL.build.module.map.BoardPicker>\n");
        sb.Append("    <VASSAL.build.module.map.StackMetrics disabled=\"false\" exSepX=\"6\" exSepY=\"18\" unexSepX=\"2\" unexSepY=\"4\"/>\n");
        gpid = 101;
        foreach (var c in Counters)
        {
            var (x, y) = c.Kind == "Marker" ? TurnBoxCentre(0) : HexCentre(grid, c.Col, c.Row);
            sb.Append("    <VASSAL.build.module.map.SetupStack name=\"").Append(X(c.Name)).Append("\" owningBoard=\"").Append(BoardName)
              .Append("\" useGridLocation=\"false\" location=\"\" x=\"").Append(I(x)).Append("\" y=\"").Append(I(y)).Append("\">\n");
            sb.Append("      ").Append(Slot(c, gpid++, MapName, x, y)).Append('\n');
            sb.Append("    </VASSAL.build.module.map.SetupStack>\n");
        }
        sb.Append("  </VASSAL.build.module.Map>\n");
        sb.Append("</VASSAL.build.GameModule>\n");
        return sb.ToString();
    }

    /// <summary>A ZonedGrid: hexes everywhere, except the turn-track boxes, which are free-placement zones named "Turn 1" ... "Turn 6".</summary>
    private static string GridXml()
    {
        var sb = new StringBuilder();
        sb.Append("        <VASSAL.build.module.map.boardPicker.board.ZonedGrid>\n");
        for (var i = 0; i < TurnBoxes; i++)
        {
            var (x, y, w, h) = TurnBox(i);
            sb.Append("          <VASSAL.build.module.map.boardPicker.board.mapgrid.Zone name=\"Turn ").Append(i + 1)
              .Append("\" locationFormat=\"$name$\" useParentGrid=\"false\" path=\"")
              .Append($"{x},{y};{x + w},{y};{x + w},{y + h};{x},{y + h}").Append("\"/>\n");
        }
        sb.Append("          <VASSAL.build.module.map.boardPicker.board.HexGrid color=\"93,107,74\" cornersLegal=\"false\" dotsVisible=\"false\" dx=\"")
          .Append(D(HexDx)).Append("\" dy=\"").Append(D(HexH)).Append("\" edgesLegal=\"false\" sideways=\"false\" snapTo=\"true\" visible=\"false\" x0=\"")
          .Append(X0).Append("\" y0=\"").Append(Y0).Append("\">\n");
        sb.Append("            <VASSAL.build.module.map.boardPicker.board.mapgrid.HexGridNumbering color=\"93,107,74\" first=\"H\" fontSize=\"9\" hDescend=\"false\" ")
          .Append("hDrawOff=\"0\" hLeading=\"1\" hOff=\"1\" hType=\"N\" locationFormat=\"$gridLocation$\" rotateText=\"0\" sep=\"\" stagger=\"false\" ")
          .Append("vDescend=\"false\" vDrawOff=\"0\" vLeading=\"1\" vOff=\"1\" vType=\"N\" visible=\"false\"/>\n");
        sb.Append("          </VASSAL.build.module.map.boardPicker.board.HexGrid>\n");
        sb.Append("        </VASSAL.build.module.map.boardPicker.board.ZonedGrid>\n");
        return sb.ToString();
    }

    /// <summary>The board's grid, parsed from the same XML the module uses (so labels and set-up positions match what players see).</summary>
    private static IMapGrid BuildGrid()
    {
        var xml = "<VASSAL.build.module.map.boardPicker.Board name=\"" + BoardName + "\" width=\"" + BoardW + "\" height=\"" + BoardH + "\">\n"
                  + GridXml() + "</VASSAL.build.module.map.boardPicker.Board>";
        using var ms = new MemoryStream(Utf8(xml));
        var board = BuildFileParser.Parse(ms);
        return GridFactory.From(board, new DiagnosticLog(), new SizeD(BoardW, BoardH))
               ?? throw new InvalidOperationException("The demo board has no grid.");
    }

    private static (int X, int Y) HexCentre(IMapGrid grid, int col, int row)
    {
        var x = X0 + col * HexDx;
        var y = Y0 + row * HexH + (col % 2 == 1 ? HexH / 2 : 0);
        var p = grid.Snap(new PointD(x, y));
        return ((int)p.X, (int)p.Y);
    }

    private static (int X, int Y, int W, int H) TurnBox(int i) => (80 + i * 70, 574, 64, 56);

    private static (int X, int Y) TurnBoxCentre(int i)
    {
        var (x, y, w, h) = TurnBox(i);
        return (x + w / 2, y + h / 2);
    }

    /// <summary>A PieceSlot: markers Side/Kind/Type, a property sheet for units, then the basic piece.</summary>
    private static string Slot(Counter c, int gpid, string map, int x, int y)
    {
        var types = new List<string> { "mark;" + SequenceEncoder.Encode(new[] { "Side", "Kind", "Type" }, ',') };
        var states = new List<string> { SequenceEncoder.Encode(new[] { c.Side, c.Kind, c.Type }, ',') };
        if (c.IsUnit)
        {
            types.Add("propertysheet;0Fuel~0Ammo~0Readiness;Unit Sheet;");
            states.Add($"{c.Fuel}~{c.Ammo}~{c.Readiness}");
        }
        types.Add($"piece;;;{c.Image};{c.Name}");
        states.Add($"{map};{x};{y};{gpid};0");
        var text = SequenceEncoder.Encode(new[] { "+", "null", PieceTypeParser.EncodeState(types), PieceTypeParser.EncodeState(states) }, '/');
        return $"<VASSAL.build.widget.PieceSlot entryName=\"{X(c.Name)}\" gpid=\"{gpid}\" height=\"48\" width=\"48\">{X(text)}</VASSAL.build.widget.PieceSlot>";
    }

    // ---- art (self-drawn SVG) ----

    private static string CounterSvg(Counter c)
    {
        var fill = c.Side == North ? NorthColour : c.Side == South ? SouthColour : "#6b6b6b";
        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"48\" height=\"48\" viewBox=\"0 0 48 48\">\n");
        sb.Append($"  <rect x=\"1\" y=\"1\" width=\"46\" height=\"46\" rx=\"4\" fill=\"{fill}\" stroke=\"#1e1e1e\" stroke-width=\"2\"/>\n");
        if (c.Kind == "Marker")
        {
            sb.Append("  <circle cx=\"24\" cy=\"24\" r=\"15\" fill=\"#ffffff\" stroke=\"#1e1e1e\" stroke-width=\"1.5\"/>\n");
            sb.Append(Text(24, 28, 10, "#1e1e1e", "TURN"));
        }
        else
        {
            sb.Append(Text(24, 12, 9, "#ffffff", c.Code));
            sb.Append("  <rect x=\"7\" y=\"16\" width=\"34\" height=\"17\" fill=\"#ffffff\" stroke=\"#1e1e1e\" stroke-width=\"1\"/>\n");
            sb.Append(Text(24, 28, c.Type.Length > 3 ? 8 : 10, "#1e1e1e", c.Type));
            if (c.IsUnit) sb.Append(Text(24, 43, 8, "#ffffff", c.Side == North ? "NORTH" : "SOUTH"));
        }
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static string BoardSvg(IMapGrid grid)
    {
        var forest = new HashSet<(int, int)> { (0, 4), (1, 5), (2, 5), (8, 2), (9, 2), (10, 3), (3, 6), (11, 1) };
        var hills = new HashSet<(int, int)> { (2, 3), (5, 4), (6, 4), (8, 0), (11, 6) };
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{BoardW}\" height=\"{BoardH}\" viewBox=\"0 0 {BoardW} {BoardH}\">\n");
        sb.Append($"  <rect width=\"{BoardW}\" height=\"{BoardH}\" fill=\"#cfe0b4\"/>\n");
        for (var c = 0; c < Cols; c++)
            for (var r = 0; r < Rows; r++)
            {
                var (cx, cy) = HexCentre(grid, c, r);
                var fill = forest.Contains((c, r)) ? "#93b874" : hills.Contains((c, r)) ? "#dccb9c" : "none";
                sb.Append("  <polygon points=\"").Append(HexPoints(cx, cy)).Append($"\" fill=\"{fill}\" stroke=\"#5d6b4a\" stroke-width=\"1\"/>\n");
            }
        // The river that gives Riverland its name, between the two sides.
        sb.Append("  <path d=\"M 0,282 C 110,250 220,330 340,288 S 560,250 700,300\" fill=\"none\" stroke=\"#4f8fd0\" stroke-width=\"9\" stroke-linecap=\"round\"/>\n");
        for (var c = 0; c < Cols; c++)
            for (var r = 0; r < Rows; r++)
            {
                var (cx, cy) = HexCentre(grid, c, r);
                sb.Append(Text(cx, cy - 20, 8, "#4a5638", grid.LocationName(new PointD(cx, cy)), bold: false));
            }
        sb.Append($"  <rect x=\"0\" y=\"562\" width=\"{BoardW}\" height=\"{BoardH - 562}\" fill=\"#e8e2cf\"/>\n");
        sb.Append(Text(42, 607, 12, "#1e1e1e", "Turn"));
        for (var i = 0; i < TurnBoxes; i++)
        {
            var (x, y, w, h) = TurnBox(i);
            sb.Append($"  <rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" fill=\"#ffffff\" stroke=\"#1e1e1e\" stroke-width=\"1.5\"/>\n");
            sb.Append(Text(x + 9, y + 13, 10, "#1e1e1e", (i + 1).ToString(CultureInfo.InvariantCulture)));
        }
        sb.Append(Text(590, 600, 18, "#1e1e1e", "RIVERLAND"));
        sb.Append(Text(590, 618, 9, "#4a5638", "invented demo map"));
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static string HexPoints(int cx, int cy)
    {
        var pts = new (double X, double Y)[]
        {
            (cx - HexSide, cy), (cx - HexSide / 2, cy - HexH / 2), (cx + HexSide / 2, cy - HexH / 2),
            (cx + HexSide, cy), (cx + HexSide / 2, cy + HexH / 2), (cx - HexSide / 2, cy + HexH / 2),
        };
        return string.Join(" ", pts.Select(p => D(p.X) + "," + D(p.Y)));
    }

    private static string Text(double x, double y, int size, string fill, string text, bool bold = true) =>
        $"  <text x=\"{D(x)}\" y=\"{D(y)}\" font-family=\"sans-serif\" font-size=\"{size}\"{(bold ? " font-weight=\"bold\"" : "")} text-anchor=\"middle\" fill=\"{fill}\">{X(text)}</text>\n";

    private const string RulesText =
        """
        RIVERLAND - invented demo rules for the Quartermaster wiki

        Two sides, the Northern League and the Southern Compact, face each other across a river.
        Each turn has three phases: Supply, Movement and Combat.

        Supply: every unit gets fuel and ammo from its own depot. 0 hexes away: 3 points;
        1-2 hexes: 2; 3-4 hexes: 1; 5 or more: none. Fuel is capped at 6, ammo at 4.
        Movement: every two hexes moved cost one fuel (rounded up). No fuel, no movement.
        Combat: roll one die and add half the unit's readiness (rounded down); 6 or more wins.
        Firing uses one ammo.

        Readiness is secret: only the side that owns a unit may see it.
        This game is made up for documentation and is free to use.

        """;

    // ---- helpers ----

    private static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s.Replace("\r\n", "\n"));
    private static string X(string s) => SecurityElement.Escape(s) ?? "";
    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string D(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
}
