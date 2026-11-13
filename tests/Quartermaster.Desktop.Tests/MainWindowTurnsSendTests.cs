using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Quartermaster.Core;
using Quartermaster.Desktop.Online;
using Quartermaster.Desktop.Shell;
using Quartermaster.Desktop.Turns;
using Quartermaster.Net;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>
/// Port stage 5d task 4: the turn game and Game → Send Turn… (WinForms MainForm.Turns, first half), the relay mailbox send and
/// File → Export Viewer Copy…. Invented TEST content only (players Alice / Bob, sides Red / Blue); the in-process loopback relay; the
/// mailbox passwords stay in the in-memory store the test module initializer sets; headless, nothing is ever shown on screen.
/// </summary>
public sealed class MainWindowTurnsSendTests : IAsyncLifetime
{
    OnlineRig _rig = null!;

    static readonly PlayerDef Alice = new("p1", "Alice", new[] { "red" });
    static readonly PlayerDef Bob = new("p2", "Bob", new[] { "blue" });

    public async ValueTask InitializeAsync() => _rig = await OnlineRig.StartAsync();

    public async ValueTask DisposeAsync() => await _rig.DisposeAsync();

    // ---------- fixture ----------

    static RulesPack Pack(string sha) => RulesPackIo.CreateEmpty("TEST turns pack", sha) with
    {
        Sides = new()
        {
            new SideDef { Id = "red", Name = "Red", UnitFilter = "startsWith(name(), \"Red\")" },
            new SideDef { Id = "blue", Name = "Blue", UnitFilter = "startsWith(name(), \"Blue\")" },
        },
    };

    static void Pump() => OnlineRig.Pump();

    static void PumpUntil(Func<bool> done, int seconds = 20) => OnlineRig.PumpUntil(done, seconds);

    /// <summary>A window with the fixture module, the TEST pack and the "Start" setup; Alice and Bob play when <paramref name="players"/>.</summary>
    async Task<(MainWindow W, FakeDialogs D, OnlineFixture F)> Game(bool players = true)
    {
        var f = await _rig.ModuleAsync(Pack);
        var d = new FakeDialogs { Answer = true };
        var w = _rig.Window(d, "turns-" + Guid.NewGuid().ToString("N"));
        Assert.True(w.OpenModule(f.ModulePath));
        w.Session!.OpenRules(f.PackPath);
        w.Session.LoadSetup(f.Setup, Difficulty.FullyAuto, players ? new[] { Alice, Bob } : null);
        Pump();
        if (w.Handover is { } h) h.ContinueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        return (w, d, f);
    }

    static void Move(GameSession g, string id, double dx)
    {
        var p = g.State.Pieces[id];
        using (g.BeginAction()) g.Execute(new MovePieces(new[] { id }, p.MapId, new PointD(p.Pos.X + dx, p.Pos.Y), null));
    }

    string TurnPath(string name) => Path.Combine(_rig.Tmp, name + ".qturn");

    /// <summary>Scripts one send: who-are-you (Alice) when asked, the send dialog to Bob, the file picker.</summary>
    static void Script(FakeDialogs d, string path, bool mailbox = false, string relay = "", string password = "")
    {
        d.WhoAnswer = new WhoChoice(Alice.Id, false, "", "");
        d.SendTurnAnswer = new SendTurnChoice(Bob, mailbox, relay, password);
        d.SaveFiles.Enqueue(path);
    }

    /// <summary>Stores an identity for the game (the window loads it from the default root, the test app data folder).</summary>
    static void StoreIdentity(string gameId, Action<TurnIdentity> set)
    {
        var id = TurnIdentity.Load(gameId);
        set(id);
        id.Save();
    }

    static string? Tip(Control c) => ToolTip.GetTip(c) as string;

    // ---------- tests ----------

    [AvaloniaFact]
    public async Task Send_turn_writes_file_records_sent_and_switches_to_turn_view()
    {
        var (w, d, _) = await Game();
        var s = w.Session!;
        var g = s.Game!;
        Assert.NotNull(w.HotSeat);   // a local two-player game is a hot-seat game until a turn is sent
        // A received copy of an earlier turn of this game goes once this turn is sent.
        var received = ReceivedTurns.Folder(g.State.GameId)!;
        Directory.CreateDirectory(received);
        File.WriteAllText(Path.Combine(received, "old.qturn"), "x");

        Move(g, PhaseFixture.RedOne, 10);
        var path = TurnPath("send");
        Script(d, path);
        await w.SendTurnAsync();

        // Who are you (nobody known yet), then the send dialog from Alice, then the picker.
        var who = Assert.Single(d.WhoCalls);
        Assert.Equal(TurnDialogRules.WhoPrompt, who.Prompt);
        Assert.Null(who.Mailbox);
        var send = Assert.Single(d.SendTurnCalls);
        Assert.Equal("Alice", send.FromName);
        Assert.False(send.Mailbox.Use);
        Assert.Equal("Save Turn", d.PickerTitles.Last());
        Assert.Equal("qturn", d.DefaultExtensions.Last());
        Assert.Equal(TurnUiModel.DefaultFileName(s.Model.Name, g.State.GameId, g.State.Turn.Turn), d.SuggestedNames.Last());

        Assert.True(File.Exists(path));
        var t = TurnFiles.Read(path);
        Assert.Equal(Alice.Id, t.FromPlayerId);
        Assert.Equal(Bob.Id, t.ToPlayerId);
        Assert.Equal("Turn saved to send.qturn", w.StatusText);
        Assert.Empty(d.Warnings);

        // Remembered: Alice on this computer, the turn sent; the received copies are gone.
        var id = TurnIdentity.Load(g.State.GameId);
        Assert.Equal(Alice.Id, id.LocalPlayerId);
        Assert.Contains(t.StartHash, id.SentStartHashes);
        Assert.Equal(Alice.Id, w.TurnIdentityForTests?.LocalPlayerId);
        Assert.Empty(ReceivedTurns.List(g.State.GameId));

        // A play-by-email game now: no hot-seat, Alice's view, the next turn starts here.
        Assert.Null(w.HotSeat);
        Assert.False(w.PlayerButton.IsVisible);
        Assert.Equal(new[] { "red" }, s.ViewSides!.ToArray());
        Assert.Equal(g.Log.Count, g.TurnBase);

        // The sent turn cannot be undone.
        Assert.True(w.History!.UndoBlockedBySentTurn);
        var before = g.State.Pieces[PhaseFixture.RedOne].Pos;
        w.History.Undo();
        Assert.Equal(before, g.State.Pieces[PhaseFixture.RedOne].Pos);
        Pump();
        var undo = w.FindMenuItem("&Undo", prefix: true)!;
        Assert.False(undo.IsEnabled);
        Assert.Equal(HistoryController.AlreadySentText, Tip(undo));

        // The next send knows Alice: no who-are-you again.
        Move(g, PhaseFixture.RedOne, 5);
        Script(d, TurnPath("send2"));
        await w.SendTurnAsync();
        Assert.Single(d.WhoCalls);
        Assert.Equal(2, d.SendTurnCalls.Count);
        Assert.True(File.Exists(TurnPath("send2")));
    }

    [AvaloniaFact]
    public async Task Send_turn_needs_two_players_and_refuses_online()
    {
        var (solo, sd, _) = await Game(players: false);
        await solo.SendTurnAsync();
        Assert.Equal(TurnUiModel.NeedPlayersText, Assert.Single(sd.Warnings).Message);
        Assert.Empty(sd.SendTurnCalls);
        Assert.Empty(sd.WhoCalls);

        var f = await _rig.ModuleAsync(Pack);
        var hd = new FakeDialogs { Answer = true };
        var host = await _rig.HostAsync(f, hd, new[] { "red" });
        await host.SendTurnAsync();
        Assert.Equal(TurnUiModel.OnlineText, Assert.Single(hd.Warnings).Message);
        Assert.Empty(hd.SendTurnCalls);
    }

    [AvaloniaFact]
    public async Task Empty_turn_logs_passed_and_write_failure_undoes_it()
    {
        var (w, d, _) = await Game();
        var g = w.Session!.Game!;
        var count = g.Log.Count;
        Assert.Equal(count, g.TurnBase);   // nothing played yet: a pass

        // The file cannot be written (the path is a folder): nothing was sent, the pass line goes again, and no redo is left.
        var folder = Path.Combine(_rig.Tmp, "a-folder.qturn");
        Directory.CreateDirectory(folder);
        Script(d, folder);
        await w.SendTurnAsync();
        var warning = Assert.Single(d.Warnings).Message;
        Assert.StartsWith($"Could not write the turn file '{folder}':\n", warning, StringComparison.Ordinal);
        Assert.Equal(count, g.Log.Count);
        Assert.False(g.CanRedo);
        Assert.Null(TurnIdentity.Load(g.State.GameId).LocalPlayerId);   // the identity stays as it was
        Assert.NotNull(w.HotSeat);

        // Written: "Alice passed." is the turn.
        var path = TurnPath("pass");
        Script(d, path);
        await w.SendTurnAsync();
        Assert.Equal(count + 1, g.Log.Count);
        Assert.Equal(g.Log.Count, g.TurnBase);
        var t = TurnFiles.Read(path);
        var line = Assert.IsType<AddLogLine>(Assert.Single(t.Commands));
        Assert.Equal("Alice passed.", line.Text);
    }

    [AvaloniaFact]
    public async Task Mailbox_send_reaches_relay_and_sets_status()
    {
        var (w, d, _) = await Game();
        var g = w.Session!.Game!;
        var gameId = g.State.GameId;
        Move(g, PhaseFixture.RedOne, 10);
        Script(d, TurnPath("mail"), mailbox: true, relay: _rig.Relay.Address, password: "TEST pw");
        await w.SendTurnAsync();
        Assert.Equal("Sending the turn through the relay…", w.StatusText);
        Assert.NotNull(w.MailboxSendTask);
        PumpUntil(() => w.StatusText == "The turn is in the mailbox on the relay.");
        Assert.Null(w.MailboxSendTask);
        Assert.Empty(d.ChooseCalls);

        // Remembered for the game: the mailbox, its relay and password; the relay becomes the default relay.
        var id = TurnIdentity.Load(gameId);
        Assert.True(id.UseMailbox);
        Assert.Equal(_rig.Relay.Address, id.Relay);
        Assert.Equal("TEST pw", id.GetPassword());
        Assert.Equal(_rig.Relay.Address, w.SettingsStore.Current.RelayAddress);

        // Bob's mailbox has it.
        var bobId = TurnIdentity.Load(gameId, Path.Combine(_rig.Tmp, "turns-bob"), null);
        bobId.LocalPlayerId = Bob.Id;
        bobId.Relay = _rig.Relay.Address;
        var got = new List<(TurnFile Turn, string Item)>();
        using var bob = new MailboxService(_ => new RelayClient(RelayTrust.Pins()), a => a());
        bob.TurnWaiting += (t, item) => { lock (got) got.Add((t, item)); };
        bob.Watch(gameId, bobId, "TEST pw");
        PumpUntil(() => { lock (got) return got.Count > 0; });
        Assert.Equal("Alice", got[0].Turn.FromName);
        Assert.Equal(Bob.Id, got[0].Turn.ToPlayerId);
    }

    [AvaloniaFact]
    public async Task Mailbox_failure_offers_retry_then_close_keeps_file()
    {
        var (w, d, _) = await Game();
        Move(w.Session!.Game!, PhaseFixture.RedOne, 10);
        var path = TurnPath("fail");
        // Nothing listens there: every attempt fails.
        var dead = DeadPort();
        Script(d, path, mailbox: true, relay: "127.0.0.1:" + dead, password: "");
        d.ChooseAnswer = 0;   // Retry
        await w.SendTurnAsync();
        PumpUntil(() => d.ChooseCalls.Count >= 2, 60);
        d.ChooseAnswer = 1;   // Close
        PumpUntil(() => w.MailboxSendTask == null && w.StatusText == $"The file is saved at {path}; send it another way.", 60);
        foreach (var (message, buttons) in d.ChooseCalls)
        {
            Assert.StartsWith("The turn could not be sent through the relay: ", message, StringComparison.Ordinal);
            Assert.EndsWith($"\nThe file is saved at {path}; send it another way.", message, StringComparison.Ordinal);
            Assert.Equal(new[] { "Retry", "Close" }, buttons);
        }
        Assert.True(File.Exists(path));
        Assert.Equal(Alice.Id, TurnIdentity.Load(w.Session.Game!.State.GameId).LocalPlayerId);
    }

    [AvaloniaFact]
    public async Task Close_during_mailbox_send_asks_and_no_keeps_window()
    {
        var (w, d, _) = await Game();
        Move(w.Session!.Game!, PhaseFixture.RedOne, 10);
        // A relay that takes the connection and never answers: the send stays in flight.
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var held = new List<TcpClient>();
        var accepting = Task.Run(async () =>
        {
            try { while (true) { var c = await silent.AcceptTcpClientAsync(); lock (held) held.Add(c); } }
            catch (Exception) { }
        });
        try
        {
            var path = TurnPath("held");
            Script(d, path, mailbox: true, relay: "127.0.0.1:" + ((IPEndPoint)silent.LocalEndpoint).Port, password: "");
            await w.SendTurnAsync();
            Assert.NotNull(w.MailboxSendTask);
            w.SaveGame(Path.Combine(_rig.Tmp, "held.qsave"));   // nothing unsaved: only the send is asked about

            d.WaitResult = false;   // the 15 s wait ran out
            d.Answer = false;       // No: stay
            w.Close();
            PumpUntil(() => d.Questions.Count > 0);
            Pump();
            var (message, caption) = Assert.Single(d.Questions);
            Assert.Equal($"The turn is still being sent through the relay. Leave anyway?\nThe file is saved at {path}; send it another way.", message);
            Assert.Equal("Send Turn", caption);
            Assert.True(d.YesNoDefaultNo.Single());
            Assert.Equal(TimeSpan.FromSeconds(15), Assert.Single(d.WaitLimits));
            Assert.True(w.IsVisible);
            Assert.NotNull(w.MailboxSendTask);

            // Yes: the window goes.
            d.Answer = true;
            w.Close();
            PumpUntil(() => !w.IsVisible);
        }
        finally
        {
            silent.Stop();
            lock (held) foreach (var c in held) c.Dispose();
            await accepting;
        }
    }

    [AvaloniaFact]
    public async Task Unreadable_password_asked_once_per_session()
    {
        var (w, d, _) = await Game();
        var gameId = w.Session!.Game!.State.GameId;
        // A password stored on another machine (or a store entry that is gone): the bytes are there, nothing reads them.
        StoreIdentity(gameId, id =>
        {
            id.LocalPlayerId = Alice.Id;
            id.UseMailbox = true;
            id.Relay = "127.0.0.1:1";
            id.ProtectedPassword = Encoding.UTF8.GetBytes("qm-secret:v1:" + Guid.NewGuid().ToString("N"));
        });
        Assert.True(TurnIdentity.Load(gameId).HasStoredPassword);
        Assert.Null(TurnIdentity.Load(gameId).GetPassword());

        d.PasswordAnswer = "TEST again";
        d.SendTurnAnswer = null;   // the send dialog is cancelled each time
        await w.SendTurnAsync();
        await w.SendTurnAsync();
        Assert.Single(d.PasswordCalls);
        Assert.Equal(2, d.SendTurnCalls.Count);
        Assert.All(d.SendTurnCalls, c => Assert.Equal("TEST again", c.Mailbox.Password));
        Assert.All(d.SendTurnCalls, c => Assert.True(c.Mailbox.Use));
        Assert.Empty(d.WhoCalls);
        // Stored again: this time the store keeps it.
        Assert.Equal("TEST again", TurnIdentity.Load(gameId).GetPassword());
    }

    [AvaloniaFact]
    public async Task Unreadable_password_of_unused_mailbox_is_never_asked()
    {
        var (w, d, _) = await Game();
        var gameId = w.Session!.Game!.State.GameId;
        // Protected bytes nothing reads, but the game does not use the mailbox: the password is never needed, so never asked for.
        StoreIdentity(gameId, id =>
        {
            id.LocalPlayerId = Alice.Id;
            id.UseMailbox = false;
            id.ProtectedPassword = Encoding.UTF8.GetBytes("qm-secret:v1:" + Guid.NewGuid().ToString("N"));
        });
        d.PasswordAnswer = "TEST unused";
        d.SendTurnAnswer = null;
        await w.SendTurnAsync();
        Assert.Empty(d.PasswordCalls);
        var call = Assert.Single(d.SendTurnCalls);
        Assert.False(call.Mailbox.Use);
        Assert.Equal("", call.Mailbox.Password);
    }

    [AvaloniaFact]
    public async Task Guest_export_viewer_copy_writes_viewer_save()
    {
        var f = await _rig.ModuleAsync(Pack);
        var hd = new FakeDialogs { Answer = true };
        var gd = new FakeDialogs { Answer = true, SidesAnswer = new[] { "blue" } };
        var host = await _rig.HostAsync(f, hd, new[] { "red" });
        var (guest, _) = await _rig.GuestAsync(f, gd, host.HostOnline!.Listing, "Carol");
        PumpUntil(() => guest.GuestOnline is { State: GuestState.Playing } && guest.Session?.Game != null);

        var guestItem = guest.FindMenuItem("&Export Viewer Copy…")!;
        Assert.True(guestItem.IsVisible);
        Assert.True(guestItem.IsEnabled);
        Assert.False(host.FindMenuItem("&Export Viewer Copy…")!.IsVisible);

        var path = Path.Combine(_rig.Tmp, "Viewer copy.qsave");
        gd.SaveFiles.Enqueue(path);
        await guest.ExportViewerCopyAsync();
        Assert.Equal("Export Viewer Copy", gd.PickerTitles.Last());
        Assert.Equal("Viewer copy", gd.SuggestedNames.Last());
        Assert.Equal("qsave", gd.DefaultExtensions.Last());
        Assert.Equal("Viewer copy saved: Viewer copy.qsave", guest.StatusText);

        var viewer = _rig.Window(new FakeDialogs(), "viewer");
        Assert.True(viewer.OpenModule(f.ModulePath));
        viewer.OpenGame(path);
        Pump();
        Assert.True(viewer.Session!.IsViewerCopy);
        Assert.Equal(ModuleSession.ViewerCopyText, viewer.Session.ViewerBanner);
        Assert.False(viewer.FindMenuItem("&Export Viewer Copy…")!.IsVisible);
        Assert.False(viewer.SendTurnItem!.IsEnabled);
        Assert.Equal(ModuleSession.ViewerCopyText, Tip(viewer.SendTurnItem));
    }

    [AvaloniaFact]
    public async Task Send_turn_menu_gates_and_tooltips()
    {
        var f = await _rig.ModuleAsync(Pack);
        var d = new FakeDialogs();
        var w = _rig.Window(d, "gates");
        var item = w.SendTurnItem!;
        Assert.Equal("Send &Turn…", MenuText.ToWinForms((string)item.Header!));
        var menu = (MenuItem)item.Parent!;
        Assert.Same(item, menu.Items[0]);   // first in the Game menu
        Assert.False(menu.IsVisible);       // no game
        Assert.DoesNotContain("&Game", w.MenuLabels().Keys);

        Assert.True(w.OpenModule(f.ModulePath));
        w.Session!.OpenRules(f.PackPath);
        w.Session.LoadSetup(f.Setup, Difficulty.FullyAuto, new[] { Alice, Bob });
        Pump();
        Assert.True(menu.IsVisible);
        Assert.Equal(new[] { "Send &Turn…", "&Players…", "&Leave Online Game" }, w.MenuLabels()["&Game"]);
        Assert.False(w.FindMenuItem("&Players…")!.IsVisible);
        Assert.False(w.FindMenuItem("&Leave Online Game")!.IsVisible);
        Assert.True(item.IsEnabled);
        Assert.True(string.IsNullOrEmpty(Tip(item)));
        Assert.False(w.FindMenuItem("&Export Viewer Copy…")!.IsVisible);

        // While a received turn replays: off, with the reason.
        var g = w.Session.Game!;
        Move(g, PhaseFixture.RedOne, 10);
        var aliceId = TurnIdentity.Load(g.State.GameId, Path.Combine(_rig.Tmp, "turns-alice"), null);
        aliceId.LocalPlayerId = Alice.Id;
        var t = TurnSender.Build(w.Session, Bob.Id, aliceId);
        var bobId = TurnIdentity.Load(g.State.GameId, Path.Combine(_rig.Tmp, "turns-bob"), null);
        w.Session.OpenTurn(t, Bob.Id, bobId);
        Pump();
        Assert.True(w.Session.IsReplaying);
        Assert.False(item.IsEnabled);
        Assert.Equal(ModuleSession.ReplayingText, Tip(item));

        // Online (host): off, "Leave the online game first."
        var host = await _rig.HostAsync(f, new FakeDialogs { Answer = true }, new[] { "red" });
        Assert.False(host.SendTurnItem!.IsEnabled);
        Assert.Equal(TurnUiModel.OnlineText, Tip(host.SendTurnItem));
        Assert.True(((MenuItem)host.SendTurnItem.Parent!).IsVisible);
        Assert.True(host.FindMenuItem("&Players…")!.IsVisible);
    }

    /// <summary>A loopback port nobody listens on.</summary>
    static int DeadPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
