using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Quartermaster.Core;
using Quartermaster.Desktop.Online;
using Quartermaster.Desktop.Turns;
using Quartermaster.Net;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>
/// Port stage 5d task 6: the play-by-email round trip end to end. Two game windows act as two computers (each its own layout root and turns
/// folder) over one in-process loopback relay; Alice plays Red, Bob plays Blue, and Fuel is a private piece value. Turns written by Render
/// <see cref="TurnSender"/> directly, as the WinForms app writes them, open in Desktop and the reverse. Invented TEST content only; the
/// mailbox passwords stay in the in-memory store; headless, nothing is ever shown on screen.
/// </summary>
public sealed class TurnsEndToEndTests : IAsyncLifetime
{
    OnlineRig _rig = null!;

    static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });
    static readonly PlayerDef Bob = new("p2", "Bob", new[] { "blue" });
    const string Password = "TEST pw";
    const string RedValue = "1717", BlueValue = "4242", RedNote = "TEST red secret", BlueNote = "TEST blue secret";

    public async ValueTask InitializeAsync() => _rig = await OnlineRig.StartAsync();

    public async ValueTask DisposeAsync() => await _rig.DisposeAsync();

    // ---------- fixture ----------

    /// <summary>Red and Blue sides and Fuel, a private piece value (sheet row Fuel, 40 at the start) summed in the Outliner.</summary>
    static RulesPack Pack(string sha) => RulesPackIo.CreateEmpty("TEST turns pack", sha) with
    {
        Sides = new()
        {
            new SideDef { Id = "red", Name = "Red", UnitFilter = "startsWith(name(), \"Red\")" },
            new SideDef { Id = "blue", Name = "Blue", UnitFilter = "startsWith(name(), \"Blue\")" },
        },
        Properties = new()
        {
            new PropertyDef { Id = "fuel", Name = "Fuel", Scope = PropertyScope.Pieces, SheetField = "Fuel", Visibility = Visibility.Private, Outliner = OutlinerAggregate.Sum },
        },
    };

    static void Pump() => OnlineRig.Pump();

    static void PumpUntil(Func<bool> done, int seconds = 20) => OnlineRig.PumpUntil(done, seconds);

    static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>A window of its own computer: its layout root and its turns folder ("turns-{name}").</summary>
    MainWindow Computer(FakeDialogs d, string name)
    {
        var w = _rig.Window(d, name + "-" + Guid.NewGuid().ToString("N"));
        w.TurnsRoot = Path.Combine(_rig.Tmp, "turns-" + name + "-" + Guid.NewGuid().ToString("N"));
        return w;
    }

    /// <summary>A window with the TEST module and pack open and no game yet.</summary>
    MainWindow Receiver(OnlineFixture f, FakeDialogs d, string name)
    {
        var w = Computer(d, name);
        Assert.True(w.OpenModule(f.ModulePath));
        w.Session!.OpenRules(f.PackPath);
        Pump();
        return w;
    }

    /// <summary>A side's private value change (manual ledger edit) with a private note, then a move of the same piece.</summary>
    static void Play(GameSession g, string pieceId, string side, double fuel, string note, double dx)
    {
        var key = LedgerKey.Piece(pieceId);
        using (g.BeginAction())
            g.Execute(new LedgerDelta(key, "fuel", Ledger.Get(g.State, key, "fuel") ?? 0, fuel, "manual", null, null, note, null, PieceId: pieceId, SideId: side));
        var p = g.State.Pieces[pieceId];
        using (g.BeginAction()) g.Execute(new MovePieces(new[] { pieceId }, p.MapId, new PointD(p.Pos.X + dx, p.Pos.Y), null));
    }

    string NewPath(string name) => Path.Combine(_rig.Tmp, name + "-" + Guid.NewGuid().ToString("N") + ".qturn");

    /// <summary>Plays the running replay to its end (Play all, then Finish).</summary>
    static void Finish(MainWindow w)
    {
        Click(w.ReplayBarControl.PlayAllButton);
        Click(w.ReplayBarControl.PlayAllButton);
        Pump();
    }

    /// <summary>The watch's five-minute poll, brought forward: the same watch (game, identity, password) started again polls at once.</summary>
    static async Task PollNowAsync(MainWindow w)
    {
        Assert.True(w.MailboxWatching);
        var id = w.TurnIdentityForTests!;
        w.Mailbox!.Watch(id.GameId, id, Password);
        await w.Mailbox.CurrentPoll;
    }

    /// <summary>Sends the open game's turn to <paramref name="to"/> through the mailbox and waits until it is in the relay box.</summary>
    async Task<string> SendByMailboxAsync(MainWindow w, FakeDialogs d, PlayerDef to)
    {
        var path = NewPath("sent");
        d.SendTurnAnswer = new SendTurnChoice(to, true, _rig.Relay.Address, Password);
        d.SaveFiles.Enqueue(path);
        await w.SendTurnAsync();
        PumpUntil(() => w.StatusText == "The turn is in the mailbox on the relay.");
        Assert.Null(w.MailboxSendTask);
        return path;
    }

    static int Col(Ledgers.LedgerWindow lw, string header) => lw.Headers.ToList().IndexOf(header);

    static string FuelOf(Ledgers.LedgerWindow lw, string name)
    {
        lw.Flush();
        var i = lw.Rows.Select((r, k) => (r, k)).First(x => x.r.Row.Name == name).k;
        return lw.CellText(i, Col(lw, "Fuel"));
    }

    /// <summary>
    /// Every window that renders values shows public information only: the ledger (every tab) has no Fuel value, the Outliner no
    /// private value or sum, and the log no private note.
    /// </summary>
    static void AssertPublicOnly(MainWindow w, params string[] privateTexts)
    {
        Assert.Equal(0, w.Session!.ViewSides?.Count);
        var lw = w.OpenLedger()!;
        lw.ShowEmptyButton.IsChecked = true;
        var checkedRows = 0;
        foreach (var key in lw.TabKeys)
        {
            lw.SelectTab(key);
            lw.Flush();
            var fuel = Col(lw, "Fuel");
            if (fuel < 0) continue;
            checkedRows += lw.Rows.Count;
            for (var i = 0; i < lw.Rows.Count; i++)
                Assert.True(lw.CellText(i, fuel).Length == 0, $"ledger tab {key}, row {lw.Rows[i].Row.Name}: Fuel '{lw.CellText(i, fuel)}' during the replay");
        }
        Assert.True(checkedRows >= 3, "no ledger rows with a Fuel column were checked");
        lw.SelectTab(lw.TabKeys[0]);
        var cells = OutlinerCells(w);
        Assert.NotEmpty(cells);
        foreach (var text in privateTexts) Assert.DoesNotContain(cells, c => c.Contains(text, StringComparison.Ordinal));
        var log = w.Log!;
        log.Reload();
        Assert.DoesNotContain(log.Rows, l => (l.Text ?? "").Contains("secret", StringComparison.Ordinal));
        Assert.DoesNotContain("secret", log.CopyText(), StringComparison.Ordinal);
        // The replay bar's line and its tooltip: the step as the public view shows it.
        var bar = w.ReplayBarControl;
        foreach (var shown in new[] { bar.LineText, bar.LineTip })
        {
            Assert.DoesNotContain("secret", shown, StringComparison.Ordinal);
            foreach (var text in privateTexts) Assert.DoesNotContain(text, shown, StringComparison.Ordinal);
        }
    }

    /// <summary>Every cell of the Outliner (all rows expanded), refreshed now.</summary>
    static List<string> OutlinerCells(MainWindow w)
    {
        var o = w.Outliner!;
        w.FlushAlerts();
        Pump();
        o.Flush();
        o.ExpandAll();
        var cells = new List<string>();
        for (var r = 0; r < o.RowNodes.Count; r++)
            for (var c = 0; c < o.ValueColumnCount + 8; c++)
                if (o.CellText(r, c) is { Length: > 0 } t) cells.Add(t);
        return cells;
    }

    static void ShowOutliner(MainWindow w)
    {
        if (!w.OutlinerShown) w.ClickMenu("&Outliner");
        Pump();
        Assert.True(w.OutlinerShown);
    }

    // ---------- tests ----------

    [AvaloniaFact]
    public async Task Round_trip_through_mailbox()
    {
        var f = await _rig.ModuleAsync(Pack);
        var relay = _rig.Relay.Address;

        // Alice's computer: a new game of Alice and Bob; she sets Red 1's fuel and moves, then sends the turn to Bob through the mailbox.
        var ad = new FakeDialogs { Answer = true, WhoAnswer = new WhoChoice(Alice.Id, false, "", "") };
        var aw = Computer(ad, "alice");
        Assert.True(aw.OpenModule(f.ModulePath));
        aw.Session!.OpenRules(f.PackPath);
        aw.Session.LoadSetup(f.Setup, Difficulty.FullyAuto, new[] { Alice, Bob });
        Pump();
        if (aw.Handover is { } h) Click(h.ContinueButton);
        Pump();
        var ag = aw.Session.Game!;
        var gameId = ag.State.GameId;
        Play(ag, PhaseFixture.RedOne, "red", 1717, RedNote, 10);
        var t1Path = await SendByMailboxAsync(aw, ad, Bob);
        Assert.True(aw.MailboxWatching);
        Assert.Equal(new[] { "red" }, aw.Session.ViewSides!.ToArray());

        // Bob's computer: no game yet, so the first turn comes as a file; he chooses the mailbox on the relay.
        var bd = new FakeDialogs { Answer = true, WhoAnswer = new WhoChoice(Bob.Id, true, relay, Password) };
        var bw = Receiver(f, bd, "bob");
        Assert.True(await bw.OpenTurnFileAsync(t1Path));
        Assert.Equal(TurnDialogRules.FromPrompt("Alice"), Assert.Single(bd.WhoCalls).Prompt);
        ShowOutliner(bw);
        Click(bw.ReplayBarControl.NextButton);
        AssertPublicOnly(bw, RedValue, "40", "80");
        Finish(bw);
        Assert.Null(bw.TurnReplay);
        var bg = bw.Session!.Game!;
        Assert.Equal(StateHash.Of(ag.State), StateHash.Of(bg.State));
        Assert.Equal(new[] { "blue" }, bw.Session.ViewSides!.ToArray());
        var blw = bw.OpenLedger()!;
        Assert.Equal("", FuelOf(blw, "Red 1"));   // Alice's value is hers
        Assert.Equal("40", FuelOf(blw, "Blue 1"));
        // Bob's watch runs; the turn he played from the file is still in his box and goes (played), never a notice.
        Assert.True(bw.MailboxWatching);
        await bw.Mailbox!.CurrentPoll;
        Pump();
        Assert.Equal("", bw.TurnNoticeText);

        // Bob plays and sends back through the mailbox: Alice's next poll raises the notice.
        Play(bg, PhaseFixture.BlueOne, "blue", 4242, BlueNote, -10);
        await SendByMailboxAsync(bw, bd, Alice);
        Assert.Empty(bd.ChooseCalls);
        Assert.Single(bd.WhoCalls);   // Bob is known for the game
        await PollNowAsync(aw);
        PumpUntil(() => aw.TurnNoticeText.Length > 0);
        Assert.Equal(TurnUiModel.NewTurnWaitingText("Bob"), aw.TurnNoticeText);

        // Alice replays: public-only while it runs, her own view after it, and Bob's turn cannot be undone.
        ShowOutliner(aw);
        aw.Alerts.ClickNotice();
        PumpUntil(() => aw.TurnReplay != null);
        Assert.Equal("", aw.TurnNoticeText);
        Click(aw.ReplayBarControl.NextButton);
        AssertPublicOnly(aw, RedValue, BlueValue, "40", "80");
        Finish(aw);
        Assert.Null(aw.TurnReplay);
        Assert.Equal("Bob's turn is replayed. Your turn.", aw.StatusText);
        ag = aw.Session.Game!;   // the turn's start, replayed to Bob's end
        Assert.Equal(StateHash.Of(bg.State), StateHash.Of(ag.State));
        Assert.Equal(new[] { "red" }, aw.Session.ViewSides!.ToArray());
        var alw = aw.OpenLedger()!;
        Assert.Equal(RedValue, FuelOf(alw, "Red 1"));
        Assert.Equal("", FuelOf(alw, "Blue 1"));
        Assert.NotNull(aw.MailboxDeleteTask);
        await aw.MailboxDeleteTask!;
        Pump();
        Assert.True(aw.MailboxWatching);

        // Alice's own move undoes; below it lies Bob's turn, which does not.
        var replayed = StateHash.Of(ag.State);
        Assert.False(aw.History!.CanUndo);
        var r2 = ag.State.Pieces[PhaseFixture.RedTwo];
        using (ag.BeginAction()) ag.Execute(new MovePieces(new[] { r2.Id }, r2.MapId, new PointD(r2.Pos.X + 5, r2.Pos.Y), null));
        Assert.True(aw.History.CanUndo);
        aw.History.Undo();
        Assert.Equal(replayed, StateHash.Of(ag.State));
        Assert.False(aw.History.CanUndo);
        aw.History.Undo();
        Assert.Equal(replayed, StateHash.Of(ag.State));
        Pump();
        Assert.False(aw.FindMenuItem("&Undo", prefix: true)!.IsEnabled);

        // Alice answers through the mailbox: Bob's notice, his replay, and both computers hold the same game.
        r2 = ag.State.Pieces[PhaseFixture.RedTwo];
        using (ag.BeginAction()) ag.Execute(new MovePieces(new[] { r2.Id }, r2.MapId, new PointD(r2.Pos.X + 5, r2.Pos.Y), null));
        await SendByMailboxAsync(aw, ad, Bob);
        await PollNowAsync(bw);
        PumpUntil(() => bw.TurnNoticeText.Length > 0);
        Assert.Equal(TurnUiModel.NewTurnWaitingText("Alice"), bw.TurnNoticeText);
        ShowOutliner(bw);
        await bw.OpenWaitingTurnAsync();
        Assert.NotNull(bw.TurnReplay);
        AssertPublicOnly(bw, RedValue, BlueValue, "40", "80");
        Finish(bw);
        Assert.Equal(StateHash.Of(ag.State), StateHash.Of(bw.Session.Game!.State));
        Assert.Equal(BlueValue, FuelOf(blw, "Blue 1"));
        Assert.Equal("", FuelOf(blw, "Red 1"));
        Assert.False(bw.History!.CanUndo);

        // Each computer kept its own identity: who it is, and the mailbox.
        Assert.Equal(Alice.Id, TurnIdentity.Load(gameId, aw.TurnsRoot, null).LocalPlayerId);
        Assert.Equal(Bob.Id, TurnIdentity.Load(gameId, bw.TurnsRoot, null).LocalPlayerId);
        Assert.Single(ad.WhoCalls);
        Assert.Empty(ad.Warnings);
        Assert.Empty(bd.Warnings);
        Assert.Empty(ad.ChooseCalls);
    }

    [AvaloniaFact]
    public async Task Turn_file_from_winforms_format_opens()
    {
        var f = await _rig.ModuleAsync(Pack);

        // Bob's computer runs the WinForms app: its turn is built and written by Render's TurnSender and TurnFiles, as MainForm.Turns does.
        using var bob = ModuleSession.Open(f.ModulePath, Path.Combine(_rig.Tmp, "cache"), null);
        bob.OpenRules(f.PackPath);
        bob.LoadSetup(f.Setup, Difficulty.FullyAuto, new[] { Alice, Bob });
        var bobId = TurnIdentity.Load(bob.Game!.State.GameId, Path.Combine(_rig.Tmp, "turns-winforms-" + Guid.NewGuid().ToString("N")), null);
        bobId.LocalPlayerId = Bob.Id;
        Play(bob.Game, PhaseFixture.BlueOne, "blue", 4242, BlueNote, -10);
        using (bob.Game.BeginAction()) bob.Game.Execute(new RollDice("TEST roll", 1, 6, new[] { 5 }));
        var t1 = TurnSender.Build(bob, Alice.Id, bobId);
        var path = NewPath("winforms");
        TurnFiles.Write(path, t1);
        TurnSender.AfterSend(bob);
        bobId.RecordSent(t1.StartHash);

        // Desktop opens and replays it.
        var ad = new FakeDialogs { Answer = true, WhoAnswer = new WhoChoice(Alice.Id, false, "", "") };
        var aw = Receiver(f, ad, "alice");
        Assert.True(await aw.OpenTurnFileAsync(path));
        var m = aw.TurnReplay!;
        Assert.Equal(TurnUiModel.StepText(0, m.Count), aw.ReplayBarControl.StepText);
        Finish(aw);
        Assert.True(m.Done);
        Assert.Null(m.Error);
        Assert.True(m.EndMatches);
        Assert.Equal(StateHash.Of(bob.Game.State), StateHash.Of(aw.Session!.Game!.State));
        Assert.Equal("Bob's turn is replayed. Your turn.", aw.StatusText);
        Assert.Empty(ad.Warnings);

        // The reverse: Desktop's answer opens in the WinForms app's session (Render's TurnOpener and replay).
        Play(aw.Session.Game, PhaseFixture.RedOne, "red", 1717, RedNote, 10);
        ad.SendTurnAnswer = new SendTurnChoice(Bob, false, "", "");
        var back = NewPath("desktop");
        ad.SaveFiles.Enqueue(back);
        await aw.SendTurnAsync();
        Assert.True(File.Exists(back));
        var t2 = TurnFiles.Read(back);
        Assert.Equal(TurnCheck.Ok, TurnOpener.Check(bob, t2, bobId));
        var replay = bob.OpenTurn(t2, Bob.Id, bobId);
        while (!replay.Done) replay.Next();
        Assert.Null(replay.Error);
        Assert.True(replay.EndMatches);
        Assert.Equal(StateHash.Of(aw.Session.Game.State), StateHash.Of(bob.Game!.State));
    }

    [AvaloniaFact]
    public async Task Hidden_values_public_only_during_replay()
    {
        var f = await _rig.ModuleAsync(Pack);

        // Bob (a Render session) sets Blue 1's fuel and sends; Alice (the window) replays it step by step.
        using var bob = ModuleSession.Open(f.ModulePath, Path.Combine(_rig.Tmp, "cache"), null);
        bob.OpenRules(f.PackPath);
        bob.LoadSetup(f.Setup, Difficulty.FullyAuto, new[] { Alice, Bob });
        var bobId = TurnIdentity.Load(bob.Game!.State.GameId, Path.Combine(_rig.Tmp, "turns-bob-" + Guid.NewGuid().ToString("N")), null);
        bobId.LocalPlayerId = Bob.Id;
        Play(bob.Game, PhaseFixture.BlueOne, "blue", 4242, BlueNote, -10);
        var t1 = TurnSender.Build(bob, Alice.Id, bobId);
        TurnSender.AfterSend(bob);

        var ad = new FakeDialogs { Answer = true, WhoAnswer = new WhoChoice(Alice.Id, false, "", "") };
        var aw = Receiver(f, ad, "alice");
        var t1Path = NewPath("t1");
        TurnFiles.Write(t1Path, t1);
        Assert.True(await aw.OpenTurnFileAsync(t1Path));
        ShowOutliner(aw);
        var lw = aw.OpenLedger()!;
        lw.ShowEmptyButton.IsChecked = true;
        // At the start, after each step and at the last step: no Fuel value of either side (Red's own 40s included).
        AssertPublicOnly(aw, BlueValue, "40", "80");
        while (aw.TurnReplay is { } m && m.Step < m.Count - 1)
        {
            Click(aw.ReplayBarControl.NextButton);
            AssertPublicOnly(aw, BlueValue, "40", "80");
        }
        Finish(aw);
        Assert.Equal("40", FuelOf(lw, "Red 1"));
        Assert.Equal("", FuelOf(lw, "Blue 1"));
        Assert.Contains("40", OutlinerCells(aw));   // the checks above can see a value when one is shown
        Assert.DoesNotContain(OutlinerCells(aw), c => c.Contains(BlueValue, StringComparison.Ordinal));

        // Alice's own value goes to Bob; Bob's next turn, replayed here, shows neither side's values while it runs.
        Play(aw.Session!.Game!, PhaseFixture.RedOne, "red", 1717, RedNote, 10);
        ad.SendTurnAnswer = new SendTurnChoice(Bob, false, "", "");
        var t2Path = NewPath("t2");
        ad.SaveFiles.Enqueue(t2Path);
        await aw.SendTurnAsync();
        Assert.Equal(RedValue, FuelOf(lw, "Red 1"));
        var r = bob.OpenTurn(TurnFiles.Read(t2Path), Bob.Id, bobId);
        while (!r.Done) r.Next();
        Assert.True(r.EndMatches);
        Play(bob.Game!, PhaseFixture.BlueOne, "blue", 4343, BlueNote + " 2", -5);
        var t3Path = NewPath("t3");
        TurnFiles.Write(t3Path, TurnSender.Build(bob, Alice.Id, bobId));
        TurnSender.AfterSend(bob);

        Assert.True(await aw.OpenTurnFileAsync(t3Path));
        AssertPublicOnly(aw, RedValue, BlueValue, "4343", "40", "80");
        Click(aw.ReplayBarControl.NextButton);
        AssertPublicOnly(aw, RedValue, BlueValue, "4343", "40", "80");
        Finish(aw);
        Assert.Equal(RedValue, FuelOf(lw, "Red 1"));
        Assert.Equal("", FuelOf(lw, "Blue 1"));
        Assert.Contains(RedValue, OutlinerCells(aw));
        Assert.DoesNotContain(OutlinerCells(aw), c => c.Contains("4343", StringComparison.Ordinal));
        aw.Log!.Reload();
        Assert.Contains(aw.Log.Rows, l => (l.Text ?? "").Contains(RedNote, StringComparison.Ordinal));
        Assert.DoesNotContain(aw.Log.Rows, l => (l.Text ?? "").Contains(BlueNote, StringComparison.Ordinal));
        Assert.Empty(ad.Warnings);
    }
}
