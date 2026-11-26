using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>B6 task 5 helpers shared with the Desktop Ledger: the Places CSV and the grid location name under a map point.</summary>
public class PlacesCsvTests
{
    [Fact]
    public void Places_csv_header_rows_and_quoting()
    {
        var cols = new[] { new PropertyDef { Id = "a", Name = "Ford, depth" }, new PropertyDef { Id = "b", Name = "Mines" } };
        var rows = new[] { new PlaceRow("0412", new double?[] { 3, null }), new PlaceRow("05\"05", new double?[] { 1.5, 2 }) };
        Assert.Equal("Hex,\"Ford, depth\",Mines\r\n0412,3,\r\n\"05\"\"05\",1.5,2\r\n", LedgerViewRules.PlacesCsv(cols, rows));
        Assert.Equal("Hex\r\n", LedgerViewRules.PlacesCsv(Array.Empty<PropertyDef>(), Array.Empty<PlaceRow>()));
    }

    [Fact]
    public void Location_name_is_the_hex_under_the_point()
    {
        var north = PlacesFixture.Model.Maps.Single(m => m.Name == "North");
        Assert.Equal("0412", MapStatusText.LocationName(north, PlacesFixture.At("Riverside", "0412")));
        Assert.Equal("", MapStatusText.LocationName(north, new PointD(5000, 5000)));
    }
}
