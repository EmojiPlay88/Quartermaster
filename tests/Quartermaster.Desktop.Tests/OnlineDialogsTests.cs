using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Online;
using Quartermaster.Desktop.Shell;
using Quartermaster.Net;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 5c task 2: the online dialogs (side choice, join, host a saved game, seats) and the certificate trust prompt.
/// Invented TEST sides Red/Blue/Green; every window is built headless and never shown except the seats dialog.</summary>
public sealed class OnlineDialogsTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-onlinedlg-" + Guid.NewGuid().ToString("N"));
    readonly List<IDisposable> _disposables = new();
    readonly List<Window> _windows = new();

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        Dispatcher.UIThread.RunJobs();
        foreach (var d in _disposables) d.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static RulesPack Pack(params string[] sides) => new()
    {
        Sides = sides.Select(s => new SideDef { Id = s.ToLowerInvariant(), Name = s }).ToList(),
    };

    static SideDef[] Sides(params string[] names) => names.Select(s => new SideDef { Id = s.ToLowerInvariant(), Name = s }).ToArray();

    static LobbyRow Row(bool locked) => new(
        new LobbyListing("", "TEST lobby", "Hosty", new string('0', 64), "Fixture", new[] { "Red", "Blue" }, new[] { "Blue" }, 1, 2, locked, Protocol.ProtocolVersion),
        "TEST lobby", "Hosty", "Blue", "1/2", locked, null);

    static void Pump() => Dispatcher.UIThread.RunJobs();

    [AvaloniaFact]
    public void SideChoice_single_side_preticked_and_ok_requires_a_tick()
    {
        var one = new SideChoiceDialog("Choose your side", "Play these sides:", Sides("Red"));
        Assert.Equal(new[] { "red" }, one.Chosen);
        Assert.Null(one.TryOk());   // ticked: accepted
        var two = new SideChoiceDialog("Sides for Bob", "Sides this player holds:", Sides("Red", "Blue"));
        Assert.Empty(two.Chosen);
        Assert.Equal(SideChoiceModel.TickOneText, two.TryOk());
        two.Boxes[1].IsChecked = true;
        Assert.Equal(new[] { "blue" }, two.Chosen);
        var given = new SideChoiceDialog("t", "p", Sides("Red", "Blue"), new[] { "red", "gone" });
        Assert.Equal(new[] { "red" }, given.Chosen);   // a tick for an absent side is simply not shown
    }

    [AvaloniaFact]
    public void Join_dialog_hides_password_for_open_lobby_and_refuses_empty_name()
    {
        var open = new JoinDialog(Row(false), "Bob");
        Assert.Equal("Join TEST lobby", open.Title);
        Assert.False(open.PasswordShown);
        var locked = new JoinDialog(Row(true), "Bob");
        Assert.True(locked.PasswordShown);
        Assert.Equal('*', locked.PasswordBox.PasswordChar);
        locked.NameBox.Text = "   ";
        Assert.Equal(HostSession.NoNameText, locked.TryOk());
        Assert.Equal("", locked.PlayerName);
        locked.NameBox.Text = "Carol";
        locked.PasswordBox.Text = "pw";
        Assert.Equal("Carol", locked.PlayerName);
        Assert.Equal("pw", locked.Password);
    }

    [AvaloniaFact]
    public void HostSaved_warning_follows_password_and_problem_blocks_ok()
    {
        var d = new HostSavedDialog(Pack("Red", "Blue", "Green"), "Hosty", "Hosty's game", new[] { "red" });
        Assert.Equal("Host a saved game", d.Title);
        Assert.True(d.Warning.IsVisible);
        Assert.Equal(OnlineLobbyModel.OpenLobbyWarning, d.Warning.Text);
        d.PasswordBox.Text = "secret";   // same tick, no pump
        Assert.False(d.Warning.IsVisible);
        d.PasswordBox.Text = "";
        Assert.True(d.Warning.IsVisible);

        Assert.Null(d.Problem);
        d.LobbyBox.Text = "  ";
        Assert.Equal("Give the lobby a name.", d.TryOk());
        d.LobbyBox.Text = "Game";
        foreach (var b in d.Sides.Boxes) b.IsChecked = true;
        Assert.Equal(OnlineLobbyModel.NoOpenSideText, d.TryOk());
        d.Sides.Boxes[2].IsChecked = false;
        Assert.Null(d.Problem);
        Assert.Equal(new[] { "red", "blue" }, d.HostSides);
        Assert.Equal("Game", d.LobbyName);
    }

    [AvaloniaFact]
    public void HostSidesPanel_shows_needs_pack_with_one_side()
    {
        var one = new HostSidesPanel(Pack("Red"), "Hosty");
        Assert.Equal("HostSidesPanel", one.Name);
        Assert.Equal(OnlineLobbyModel.NeedsPackText, one.NeedsPack?.Text);
        Assert.Equal(OnlineLobbyModel.NeedsPackText, one.Problem);
        var none = new HostSidesPanel(null, "Hosty");
        Assert.NotNull(none.NeedsPack);
        var three = new HostSidesPanel(Pack("Red", "Blue", "Green"), "Hosty");
        Assert.Null(three.NeedsPack);
        Assert.Equal(new[] { "red" }, three.HostSides);   // the first side by default
        Assert.Null(three.Problem);
        three.NameBox.Text = "";
        Assert.Equal(HostSession.NoNameText, three.Problem);
        three.NameBox.Text = "Zed";
        Assert.Equal(new PlayerDef("host", "Zed", new[] { "red" }).Sides, three.ToPlayer().Sides);
        Assert.Equal("Zed", three.ToPlayer().Name);
    }

    // ---- a host over a fake transport ----

    sealed class FakeTransport : ITransport
    {
        public Task Send(string? to, byte[] payload) => Task.CompletedTask;
        public Task UpdateListing(LobbyListing listing) => Task.CompletedTask;
        public void Inject(string from, GamePayload p) => Received?.Invoke(from, Framing.Encode(p));
        public event Action<string, byte[]>? Received;
        public event Action<string>? MemberLeft;
        public event Action<string>? Disconnected;
        public event Action? Reconnected;
        public event Action<IReadOnlyList<string>>? MembersListed;
        public event Action? HostLeft;
        public event Action? HostBack;
        public void Silence() { MemberLeft?.Invoke(""); Disconnected?.Invoke(""); Reconnected?.Invoke(); MembersListed?.Invoke(Array.Empty<string>()); HostLeft?.Invoke(); HostBack?.Invoke(); }
    }

    (HostSession Host, FakeTransport T, RulesPack Pack, Queue<Action> Posted) OpenHost()
    {
        var fx = PhaseFixture.Module();
        _disposables.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _disposables.Add(s);
        s.LoadSetup(s.Model.Setups[0], Difficulty.FullyAuto, new[] { new PlayerDef("p1", "Hosty", new[] { "red" }) });
        s.NewRules("t");
        s.UpdateRules(_ => PackFor(s.Archive.Sha256));
        var c = new PhaseController(s, (cmd, st) => CommandLog.Prepare(cmd, st, s));
        var t = new FakeTransport();
        var posted = new Queue<Action>();
        var listing = new LobbyListing("", "TEST lobby", "Hosty", s.Archive.Sha256, "Fixture", new[] { "Red", "Blue", "Green" },
            new[] { "Blue", "Green" }, 1, 3, false, Protocol.ProtocolVersion);
        var h = new HostSession(s, new IntentExecutor(s, c), t, "pw", "abc-234", listing, a => { lock (posted) posted.Enqueue(a); });
        _disposables.Add(h);
        return (h, t, s.Rules!, posted);
    }

    static RulesPack PackFor(string sha)
    {
        var p = PhaseFixture.Pack(sha);
        return p with
        {
            Format = 4,
            Sides = new()
            {
                new SideDef { Id = "red", Name = "Red", UnitFilter = "startsWith(name(), \"Red\")" },
                new SideDef { Id = "blue", Name = "Blue", UnitFilter = "startsWith(name(), \"Blue\")" },
                new SideDef { Id = "green", Name = "Green", UnitFilter = "startsWith(name(), \"Green\")" },
            },
        };
    }

    static void RunPosted(Queue<Action> posted)
    {
        while (true)
        {
            Action? a;
            lock (posted) a = posted.Count > 0 ? posted.Dequeue() : null;
            if (a == null) return;
            a();
        }
    }

    [AvaloniaFact]
    public void Seats_dialog_follows_seats_changed_and_unsubscribes_on_close()
    {
        var (host, t, pack, posted) = OpenHost();
        RunPosted(posted);
        var d = new SeatsDialog(host, pack);
        _windows.Add(d);
        d.Show();
        Pump();
        Assert.Equal("Players", d.Title);
        Assert.Equal(new[] { "Hosty", "(open seat)" }, d.Rows.Select(r => r.Player));
        Assert.False(d.AssignButton.IsEnabled);
        Assert.False(d.FreeButton.IsEnabled);

        t.Inject("m1", new JoinRequest("Bob", null, host.Listing.ModuleSha256 ?? ""));
        RunPosted(posted);
        Pump();
        Assert.Equal(2, host.Seats.All.Count);   // Bob took a seat and the open line stayed
        Assert.Equal(new[] { "Hosty", "Bob", "(open seat)" }, d.Rows.Select(r => r.Player));

        d.List.SelectedItem = d.Rows[0];
        Assert.True(d.AssignButton.IsEnabled);
        Assert.False(d.FreeButton.IsEnabled);   // the host's own seat can't be freed

        d.Close();
        Pump();
        var before = d.Rows;
        t.Inject("m2", new JoinRequest("Cy", null, host.Listing.ModuleSha256 ?? ""));
        RunPosted(posted);
        Assert.Same(before, d.Rows);   // closed: no refresh
    }

    [AvaloniaFact]
    public void Relay_trust_on_ui_thread_returns_false()
    {
        var d = new FakeDialogs { Answer = true };
        var w = new Window();
        var c = new CertificateCheck("relay.example:443", "AA:BB", null);
        Assert.False(RelayTrust.Ask(w, d, c));
        Assert.Empty(d.Questions);   // never asked: it would have deadlocked
    }

    [AvaloniaFact]
    public void Relay_trust_off_the_ui_thread_asks_on_the_ui_thread()
    {
        var d = new FakeDialogs { Answer = true };
        var w = new Window();
        var c = new CertificateCheck("relay.example:443", "AA:BB", "CC:DD");
        var task = Task.Run(() => RelayTrust.Ask(w, d, c));
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < until) { Pump(); Thread.Sleep(5); }
        Assert.True(task.IsCompleted);
        Assert.True(task.Result);
        Assert.Single(d.Questions);
    }
}
