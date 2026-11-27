using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core.Tests;

/// <summary>B7: the schedule's zone map is resolved by the one resolver, <see cref="MapRefs.Find"/>. Invented names.</summary>
public class ScheduleZoneMapTests
{
    private static MapDef Map(string id, string name) => new(id, name, false, Array.Empty<string>(), Array.Empty<BoardDef>(), 0, 0, default, default!, Array.Empty<SetupStackDef>(), "");

    [Fact]
    public void Zone_map_by_name_and_spaces_resolves()
    {
        var maps = new[] { Map("map-1", "North holding box"), Map("map-2", "South pier") };
        Assert.Same(maps[0], ScheduleDue.ZoneMapIn(maps, "  North holding box "));
        Assert.Same(maps[1], ScheduleDue.ZoneMapIn(maps, "map-2 "));
        Assert.Null(ScheduleDue.ZoneMapIn(maps, "north holding box"));   // ordinal, as the validator
        Assert.Null(ScheduleDue.ZoneMapIn(maps, "  "));
        Assert.Null(ScheduleDue.ZoneMapIn(maps, null));
    }

    [Fact]
    public void Zone_map_id_wins_over_name()
    {
        var byName = Map("map-1", "map-2");
        var byId = Map("map-2", "South pier");
        Assert.Same(byId, ScheduleDue.ZoneMapIn(new[] { byName, byId }, "map-2"));
    }
}
