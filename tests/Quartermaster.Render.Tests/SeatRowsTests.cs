using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

/// <summary>Rows of the host's Players dialog. Invented TEST sides Red/Blue/Green.</summary>
public class SeatRowsTests
{
    private static RulesPack Pack(params (string Id, string Name)[] sides) =>
        new() { Sides = sides.Select(s => new SideDef { Id = s.Id, Name = s.Name }).ToList() };

    private static RulesPack Rgb() => Pack(("red", "Red"), ("blue", "Blue"), ("green", "Green"));

    private static Seats Table(out Seat blue, out Seat cleo)
    {
        var seats = new Seats(new PlayerDef("host", "Hanna", new[] { "red" }));
        blue = seats.AddGuest("Bob", Array.Empty<string>()).Seat;
        blue.MemberId = "m1";
        blue.Sides = new[] { "blue" };
        cleo = seats.AddGuest("Cleo", Array.Empty<string>()).Seat;
        cleo.MemberId = "m2";
        return seats;
    }

    [Fact]
    public void Rows_show_status_and_open_seat()
    {
        var seats = Table(out _, out _);
        var rows = SeatRows.For(seats, Rgb());
        Assert.Equal(4, rows.Count);
        Assert.Equal(("Hanna", "Red", "host (you)"), (rows[0].Player, rows[0].Sides, rows[0].Status));
        Assert.Equal(("Bob", "Blue", "connected"), (rows[1].Player, rows[1].Sides, rows[1].Status));
        Assert.Equal(("Cleo", "—", "choosing a side"), (rows[2].Player, rows[2].Sides, rows[2].Status));
        Assert.Equal(("(open seat)", "Green", "free", true, null), (rows[3].Player, rows[3].Sides, rows[3].Status, rows[3].Greyed, rows[3].Seat));
        Assert.All(rows.Take(3), r => Assert.False(r.Greyed));
        Assert.Same(seats.All[1], rows[1].Seat);
    }

    [Fact]
    public void No_open_row_when_nothing_is_free()
    {
        var seats = Table(out _, out var cleo);
        cleo.Sides = new[] { "green" };
        Assert.Equal(3, SeatRows.For(seats, Rgb()).Count);
    }

    [Fact]
    public void Left_seat_is_greyed_and_freeable()
    {
        var seats = Table(out var blue, out _);
        blue.MemberId = null;
        var row = SeatRows.For(seats, Rgb())[1];
        Assert.Equal("left", row.Status);
        Assert.True(row.Greyed);
        Assert.True(SeatRows.CanFree(blue));
        Assert.True(SeatRows.CanAssign(blue));
    }

    [Fact]
    public void Host_seat_never_freeable()
    {
        var seats = Table(out var blue, out var cleo);
        Assert.False(SeatRows.CanFree(seats.Host));
        Assert.False(SeatRows.CanFree(blue));          // connected and holds a side
        Assert.True(SeatRows.CanFree(cleo));           // connected, no side
        Assert.False(SeatRows.CanFree(null));
        Assert.False(SeatRows.CanAssign(null));
        Assert.True(SeatRows.CanAssign(seats.Host));
    }

    [Fact]
    public void Offered_is_free_plus_own_in_pack_order()
    {
        var seats = Table(out var blue, out _);
        var pack = Pack(("green", "Green"), ("red", "Red"), ("blue", "Blue"), ("white", "White"));
        Assert.Equal(new[] { "green", "blue", "white" }, SeatRows.Offered(seats, pack, blue).Select(s => s.Id));
    }

    [Fact]
    public void Unnamed_side_shows_id()
    {
        var seats = new Seats(new PlayerDef("host", "Hanna", new[] { "red" }));
        var rows = SeatRows.For(seats, Pack(("red", ""), ("zed", "")));
        Assert.Equal("red", rows[0].Sides);
        Assert.Equal("zed", rows[1].Sides);
    }

    [Fact]
    public void Texts_match_the_dialog()
    {
        Assert.Equal("Free Cleo's seat? Their sides become open seats again.", SeatRows.FreeQuestion("Cleo"));
        Assert.Equal("Sides for Cleo", SeatRows.AssignTitle("Cleo"));
    }
}
