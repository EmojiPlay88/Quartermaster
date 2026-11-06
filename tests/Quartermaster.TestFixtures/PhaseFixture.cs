using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.TestFixtures;

/// <summary>Invented module and rules pack for phase and difficulty tests. Synthetic names and numbers only.</summary>
public static class PhaseFixture
{
    public const string RedOne = "r1";
    public const string RedTwo = "r2";
    public const string BlueOne = "b1";

    private static string Add(string id, string name, int x) =>
        "+/" + SequenceEncoder.Encode(new[] { id, $"propertysheet;0Fuel;Sheet;\tpiece;;;unit.svg;{name}", $"40\tMain Map;{x};50;1;0" }, '/');

    /// <summary>MinimalBuildFile plus a setup "Start" holding "Red 1", "Red 2" and "Blue 1", each with a Fuel sheet row of 40.</summary>
    public static FixtureModule Module(bool grid = true)
    {
        var xml = FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            "<VASSAL.build.module.PredefinedSetup name=\"Start\" file=\"s.vsav\" useFile=\"true\" isMenu=\"false\"/></VASSAL.build.GameModule>");
        if (!grid) xml = System.Text.RegularExpressions.Regex.Replace(xml, @"<VASSAL\.build\.module\.map\.boardPicker\.board\.SquareGrid[^>]*/>", "");
        return new FixtureModule().WithBuildFile(xml).AddImage("board.png", 200, 100).AddSvg("unit.svg")
            .AddSave("s.vsav", new[] { "begin_save", "", Add(RedOne, "Red 1", 30), Add(RedTwo, "Red 2", 30), Add(BlueOne, "Blue 1", 30), "end_save" });
    }

    public static RulesPack Pack(string moduleSha) => new()
    {
        Format = 2,
        Id = "phase-fixture",
        Name = "Phase fixture",
        ModuleSha256 = moduleSha,
        Properties = new()
        {
            new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, SheetField = "Fuel" },
            new PropertyDef { Id = "water", Name = "Water", Scope = PropertyScope.Pieces, Default = 10 },
        },
        Tables = new()
        {
            new TableDef
            {
                Id = "t-cost", Name = "TestTerrainCost", Kind = TableKind.OneD,
                RowKeys = new() { "Clear", "Sand" }, ColumnKeys = new() { "Value" },
                Cells = new() { new() { "2" }, new() { "3" } },
            },
        },
        Formulas = new()
        {
            new FormulaDef
            {
                Id = "f-weather", Name = "Weather roll", Text = "R",
                Bindings = new() { new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 } },
            },
            new FormulaDef
            {
                Id = "f-water", Name = "Daily water", Text = "W - 1",
                Bindings = new() { new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" } },
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
            },
            new FormulaDef
            {
                Id = "f-fuel", Name = "Fuel for movement", Text = "Moved * lookup(TestTerrainCost, Terrain)",
                Bindings = new()
                {
                    new VariableBinding { Variable = "Moved", Kind = BindingKind.Moved },
                    new VariableBinding { Variable = "Terrain", Kind = BindingKind.Input, AutoValue = "\"Clear\"" },
                },
                Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Op = OutputOp.Subtract } },
            },
        },
        Sides = new()
        {
            new SideDef { Id = "red", Name = "Red", UnitFilter = "startsWith(name(), \"Red\")" },
            new SideDef { Id = "blue", Name = "Blue", UnitFilter = "startsWith(name(), \"Blue\")" },
        },
        Phases = new()
        {
            new PhaseDef { Id = "ph-weather", Name = "Weather", OnStart = new() { new StartEntry { FormulaId = "f-weather", ForEach = ForEachMode.Once } } },
            new PhaseDef { Id = "ph-supply", Name = "Supply", SideId = "red", OnStart = new() { new StartEntry { FormulaId = "f-water", ForEach = ForEachMode.Units } } },
            new PhaseDef { Id = "ph-move", Name = "Movement", SideId = "red", OnMove = new() { new MoveEntry { FormulaId = "f-fuel" } } },
            new PhaseDef
            {
                Id = "ph-combat", Name = "Combat", Checklist = new() { "Resolve attacks" },
                Warnings = new() { new PhaseWarning { Condition = "Fuel <= 0", Message = "Out of fuel", IsDecision = true } },
            },
            new PhaseDef { Id = "ph-end", Name = "End" },
        },
        Blocks = new() { new PhaseBlock { FirstPhaseId = "ph-move", LastPhaseId = "ph-combat", Count = 2, Label = "Round" } },
    };
}
