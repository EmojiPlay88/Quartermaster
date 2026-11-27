using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>B8 #2: a turn file's receiver and a pack test (PackHarness.ViewFor) see what a seated player of the alliance sees.</summary>
[Collection(AppDataRootEnv.Name)]
public class AllianceTurnFileTests : AllianceTestBase
{
    private TurnIdentity Identity(string who, string gameId, string? player = null)
    {
        var id = TurnIdentity.Load(gameId, Path.Combine(Tmp, "turns-" + who), TestProtector.Create());
        if (player != null) id.LocalPlayerId = player;
        return id;
    }

    [Fact]
    public void Turn_view_sides_include_allies()
    {
        var alice = Session();
        var g = alice.Game!;
        using (g.BeginAction()) g.Execute(new MovePieces(new[] { PhaseFixture.RedOne }, "Main Map", new PointD(40, 50), null));
        var t = TurnSender.Build(alice, Cara.Id, Identity("alice", g.State.GameId!, Alice.Id));

        var cara = Session();   // Cara's computer: the same module and pack
        var model = cara.OpenTurn(t, Cara.Id, Identity("cara", t.GameId));
        model.PlayAll();
        Assert.False(cara.IsReplaying);
        Assert.Equal(new[] { "hills" }, cara.PlayerSides!);
        Assert.Equal(new[] { "hills", "north" }, cara.ViewSides!.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(CampSupply, Supply(cara.View!, "hillcamp"));
        Assert.Null(Supply(cara.View!, "southdepot"));

        // The windows' path (TurnUiModel.ViewSides of the local player) widens the same way.
        cara.SetViewSides(TurnUiModel.ViewSides(cara.Game!.State, Alice.Id));
        Assert.Equal(new[] { "hills", "north" }, cara.ViewSides!.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Failed_replay_restores_raw_player_sides()
    {
        var alice = Session();
        var g = alice.Game!;
        using (g.BeginAction()) g.Execute(new MovePieces(new[] { PhaseFixture.RedOne }, "Main Map", new PointD(40, 50), null));
        var t = TurnSender.Build(alice, Cara.Id, Identity("alice", g.State.GameId!, Alice.Id));

        var cara = Session();
        cara.SetViewSides(Hills);
        var start = t.Start.DeepClone();
        start.Pieces[PhaseFixture.BlueOne].TraitStates = null!;   // a malformed step makes the replay fail
        var bad = t with { Start = start, Commands = t.Commands.Append(new SetTraitState(PhaseFixture.BlueOne, 0, "s")).ToList() };
        var model = cara.OpenTurn(bad, Cara.Id, Identity("cara", t.GameId));
        model.PlayAll();
        Assert.NotNull(model.Error);
        Assert.Equal(new[] { "hills" }, cara.PlayerSides!);   // the raw sides come back, not the held set
        Assert.Equal(new[] { "hills", "north" }, cara.ViewSides!.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void PackHarness_view_for_includes_allies()
    {
        using var fx = PhaseFixture.Module();
        var vmod = fx.Build();
        string sha;
        using (var a = Quartermaster.Vassal.VmodArchive.Open(vmod)) sha = a.Sha256;
        var rules = Path.Combine(Tmp, "p.qrules");
        RulesPackIo.Save(Pack(sha), rules);
        using var h = PackHarness.Open(vmod, rules, Path.Combine(Tmp, "hcache"));
        h.NewGame();
        h.Session.Game!.Execute(new LedgerDelta(LedgerKey.Holder("hillcamp"), "p-supply", 0, CampSupply, "manual", null, null, null, null));
        var north = h.ViewFor("north");
        Assert.Equal(new[] { "hills", "north" }, north.ViewSides!);
        Assert.Equal(CampSupply, Supply(north, "hillcamp"));
        var south = h.ViewFor("south");
        Assert.Equal(new[] { "south" }, south.ViewSides!);
        Assert.Null(Supply(south, "hillcamp"));
    }
}
