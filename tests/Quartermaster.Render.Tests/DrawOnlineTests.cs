using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

/// <summary>
/// B9 #55 online and by turn file: the host draws for a guest, pins the draw dice per sender (a preview and its Apply draw the same counters),
/// never sends them or the split before Apply, and ignores any dice a guest types. Invented Riverland data (<see cref="DrawTestBase"/>).
/// </summary>
[Collection(AppDataRootEnv.Name)]
public class DrawOnlineTests : DrawTestBase
{
    private static FormulaIntent Shoot(string shots = "3", bool preview = false, string piece = "nl1", Dictionary<string, string>? extra = null)
    {
        var typed = new Dictionary<string, string> { ["Shots"] = shots };
        foreach (var (k, v) in extra ?? new()) typed[k] = v;
        return new FormulaIntent("f", piece, null, typed, preview, Preview: preview);
    }

    private static FormulaBatchIntent Batch(bool preview, string shots = "2") =>
        new("f", new[] { "nl1", "nl2" }.Select(p => new FormulaBatchRow(p, new Dictionary<string, string> { ["Shots"] = shots }, null)).ToList(),
            preview, preview);

    [Fact]
    public void Guest_preview_then_apply_same_split()
    {
        var (host, c) = Open();
        var x = new IntentExecutor(host, c);
        var guest = Guest(host, North);
        var m = Dialog(guest);
        Assert.Contains("Hits", m.Hidden);
        m.SetBoxText("Shots", "3");
        var dv = m.Evaluate(guest.Game!.State);
        Assert.Equal(FormulaMasking.HostCalculatesText, Box(dv, "Hits").Text);
        Assert.Equal(Tooltip, Box(dv, "Hits").Tooltip);
        Assert.False(NamesBarge(Shown(dv)), Shown(dv));

        foreach (var face in new[] { 1, 1, 1 }) Next.Enqueue(face);
        var preview = x.Execute("p1", North, m.Intent(null, calculate: true, preview: true)).Verdict!;
        Assert.False(preview.Applied);
        Assert.Equal(3, RollerCalls);
        m.ApplyVerdict(preview);
        Assert.DoesNotContain(m.PinnedRolls.Keys, k => k.StartsWith("draw:", StringComparison.Ordinal));
        // Asking again draws nothing new.
        x.Execute("p1", North, m.Intent(null, calculate: true, preview: true));
        Assert.Equal(3, RollerCalls);

        var apply = x.Execute("p1", North, m.Intent(null, calculate: false)).Verdict!;
        Assert.True(apply.Applied, apply.Message);
        Assert.Equal(3, RollerCalls);
        Assert.Equal(new double?[] { 0, 1, 0, 2 }, AllBoats(host));
        Assert.Contains(host.Game!.State.Log, l => l.Text == "Shore battery: Hits: 3 on SC 1 Barge" && l.SideId == "south");
        // The guest's next snapshot has no draw line; the south's has it.
        Assert.DoesNotContain(PlayerView.For(host.Game.State, host.Rules, host.Model, North).Log, l => l.Text.StartsWith("Shore battery: Hits:", StringComparison.Ordinal));
        Assert.Contains(PlayerView.For(host.Game.State, host.Rules, host.Model, South).Log, l => l.Text == "Shore battery: Hits: 3 on SC 1 Barge");
    }

    [Fact]
    public void Guest_preview_verdict_has_no_draw_keys_or_shares()
    {
        foreach (var boats in new[] { Visibility.Public, Visibility.Private })
        {
            var (host, c) = Open(boats);
            var x = new IntentExecutor(host, c);
            var v = x.Execute("p1", North, Shoot(preview: true)).Verdict!;
            Assert.False(v.Applied);
            Assert.Null(v.Message);
            var json = Json(v);
            Assert.False(NamesBarge(json), json);
            Assert.DoesNotContain("draw:", json);
            Assert.DoesNotContain(v.Rolls?.Keys ?? Array.Empty<string>(), k => k.StartsWith("draw:", StringComparison.Ordinal));
            if (boats == Visibility.Public)
            {
                Assert.Equal("3", v.Values!["Hits"]);   // the acting side learns k
                Assert.Contains(v.Boxes, b => b.Var == "Hits" && b.State == FormulaBox.Ok);
            }
            else
            {
                Assert.Null(v.Rolls);
                Assert.False(v.Values?.ContainsKey("Hits") ?? false);
                Assert.Contains(v.Boxes, b => b.Var == "Hits" && b.State == FormulaBox.Hidden);
            }
            // The previewed draw stays pinned on the host for this sender's Apply.
            var calls = RollerCalls;
            Assert.True(x.Execute("p1", North, Shoot()).Verdict!.Applied);
            Assert.Equal(calls, RollerCalls);
            Assert.Equal(new double?[] { 3, 0, 0, 0 }, AllBoats(host));
        }
    }

    [Fact]
    public void Two_guests_pins_independent()
    {
        var (host, c) = Open();
        var x = new IntentExecutor(host, c);
        foreach (var face in new[] { 1, 1, 1 }) Next.Enqueue(face);
        x.Execute("p1", North, Shoot(preview: true));   // Alice: SC 1 Barge three times
        x.Execute("p3", North, Shoot(preview: true));   // Dan: the highest faces (SC 4 Barge twice, SC 2 Barge once)
        Assert.Equal(6, RollerCalls);
        foreach (var face in new[] { 3, 3, 3 }) Next.Enqueue(face);   // what a new draw would take

        Assert.True(x.Execute("p3", North, Shoot()).Verdict!.Applied);
        Assert.Equal(new double?[] { 3, 0, 0, 0 }, AllBoats(host));
        host.Game!.Undo();
        Assert.True(x.Execute("p1", North, Shoot()).Verdict!.Applied);
        Assert.Equal(new double?[] { 0, 1, 0, 2 }, AllBoats(host));
        Assert.Equal(6, RollerCalls);
        Assert.Equal(3, Next.Count);
    }

    [Fact]
    public void Guest_batch_rows_draw_independently_and_apply_as_one_step()
    {
        var (host, c) = Open(split: true);
        var x = new IntentExecutor(host, c);
        // Row 1 (hex 0101: SC 1 Barge 3, SC 2 Barge 1) draws faces 4 then 1; row 2 (hex 0301: SC 4 Barge 2) draws 1 then 1.
        foreach (var face in new[] { 4, 1, 1, 1 }) Next.Enqueue(face);
        var preview = x.Execute("p1", North, Batch(preview: true)).BatchVerdict!;
        Assert.Equal(2, preview.Rows.Count);
        Assert.Equal(4, RollerCalls);
        var json = Json(preview);
        Assert.False(NamesBarge(json), json);
        Assert.DoesNotContain("draw:", json);
        var actions = host.Game!.Actions.Count;
        var apply = x.Execute("p1", North, Batch(preview: false)).BatchVerdict!;
        Assert.True(apply.Applied, apply.Message);
        Assert.Equal(4, RollerCalls);
        Assert.Equal(actions + 1, host.Game.Actions.Count);
        Assert.Equal(new double?[] { 2, 0, 0, 0 }, AllBoats(host));
        host.Game.Undo();
        Assert.Equal(new double?[] { 3, 1, 0, 2 }, AllBoats(host));
    }

    [Fact]
    public void Guest_cannot_supply_draw_dice()
    {
        var (host, c) = Open();
        var x = new IntentExecutor(host, c);
        var forged = new Dictionary<string, string> { ["draw:Hits#1"] = "1", ["draw:Hits#2"] = "1", ["draw:Hits#3"] = "1", ["Hits"] = "0" };
        var v = x.Execute("p1", North, Shoot(extra: forged)).Verdict!;
        Assert.True(v.Applied, v.Message);   // ignored: the host draws (the highest faces), never the typed dice
        Assert.Equal(new double?[] { 3, 0, 0, 0 }, AllBoats(host));
        Assert.Equal(3, RollerCalls);
    }

    private TurnIdentity Identity(string who, string gameId, string? player = null)
    {
        var id = TurnIdentity.Load(gameId, Path.Combine(Tmp, "turns-" + who), TestProtector.Create());
        if (player != null) id.LocalPlayerId = player;
        return id;
    }

    [Fact]
    public void Turn_file_replay_keeps_split()
    {
        var (alice, c) = Open();
        alice.SetViewSides(North);
        var m = Dialog(alice);
        m.SetBoxText("Shots", "3");
        foreach (var face in new[] { 1, 1, 1 }) Next.Enqueue(face);
        Assert.True(c.ApplyFormula(null, "nl1", m.Evaluate(alice.Game!.State).Trace, false));
        Assert.Equal(new double?[] { 0, 1, 0, 2 }, AllBoats(alice));
        var t = TurnSender.Build(alice, Bob.Id, Identity("alice", alice.Game.State.GameId!, Alice.Id));
        var turnFile = TurnFiles.FromBytes(TurnFiles.ToBytes(t));   // through the file's bytes

        var (bob, _) = Open();   // Bob's computer: the same module and pack
        var calls = RollerCalls;
        var model = bob.OpenTurn(turnFile, Bob.Id, Identity("bob", turnFile.GameId));
        model.PlayAll();
        Assert.Null(model.Error);
        Assert.Equal(calls, RollerCalls);   // replay re-executes the commands: nothing drawn
        Assert.Equal(new double?[] { 0, 1, 0, 2 }, AllBoats(bob));
        Assert.Equal(alice.Game.State.Log.Select(l => l.Text).Where(l => l.StartsWith("Shore battery", StringComparison.Ordinal)),
            bob.Game!.State.Log.Select(l => l.Text).Where(l => l.StartsWith("Shore battery", StringComparison.Ordinal)));
        Assert.Contains(bob.View!.Log, l => l.Text == "Shore battery: Hits: 3 on SC 1 Barge");   // Bob holds the south
    }
}
