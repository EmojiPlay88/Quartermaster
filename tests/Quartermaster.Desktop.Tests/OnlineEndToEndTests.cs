using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Desktop.Map;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>
/// Port stage 5c task 5: online play end to end in the Avalonia windows (as Render's Piece6bEndToEndTests one level down). One in-process
/// relay on loopback; a host window holding Green (a moderator side with no pieces), a Red guest window and a Blue guest window. Red has
/// secrets in the game: a private fuel value typed by the Red guest, a private depot stock, a private-dice formula result and a piece on a
/// Red-owned map. Invented TEST module, pack and values only; headless, nothing is ever shown on screen.
/// </summary>
public sealed class OnlineEndToEndTests : IAsyncLifetime
{
    const double RedFuel = 31313;             // typed by the Red guest: private to Red
    const double RedStock = 42424;            // the Red depot's private supply (a hidden input of f-mix for Blue)
    const int SecretRoll = 8461;              // the private-dice formula's roll (its result is Red 2's private fuel)
    const string SecretName = "TEST Red Phantom";
    const string RedMap = "TEST Red Command Post";
    static readonly string[] RedMarkers = { RedFuel.ToString(), RedStock.ToString(), SecretRoll.ToString(), SecretName, "secret1" };

    readonly FakeDialogs _hostDialogs = new() { Answer = true };
    readonly FakeDialogs _redDialogs = new() { Answer = true, SidesAnswer = new[] { "red" } };
    readonly FakeDialogs _blueDialogs = new() { Answer = true, SidesAnswer = new[] { "blue" } };
    OnlineRig _rig = null!;

    public async ValueTask InitializeAsync() => _rig = await OnlineRig.StartAsync();

    public async ValueTask DisposeAsync() => await _rig.DisposeAsync();

    // ---------- fixture ----------

    /// <summary>The phase fixture pack with a third side (Green), private fuel and a private Red depot, a Red-owned map, a formula with a
    /// hidden input for Blue (f-mix) and a private-dice formula (f-secret).</summary>
    static RulesPack Pack(string sha)
    {
        var p = PhaseFixture.Pack(sha);
        return p with
        {
            Format = 4,
            Name = "TEST online e2e pack",
            Sides = p.Sides.Append(new SideDef { Id = "green", Name = "Green", UnitFilter = "startsWith(name(), \"Green\")" }).ToList(),
            Properties = p.Properties.Select(x => x.Id == "fuel" ? x with { Visibility = Visibility.Private } : x)
                .Append(new PropertyDef { Id = "supply", Name = "Supply", Scope = PropertyScope.Holders, Visibility = Visibility.Private }).ToList(),
            Holders = new() { new HolderDef { Id = "reddepot", Name = "Red Depot", SideId = "red" } },
            MapOwners = new() { new MapOwner { MapId = RedMap, SideId = "red" } },
            Formulas = p.Formulas.Concat(new[]
            {
                new FormulaDef
                {
                    Id = "f-secret", Name = "Secret Attack", Text = "R", PrivateRolls = true,
                    Bindings = new() { new VariableBinding { Variable = "R", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 10000 } },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "fuel", Op = OutputOp.Set } },
                },
                new FormulaDef
                {
                    Id = "f-mix", Name = "Mixed supply", Text = "W + S",
                    Bindings = new()
                    {
                        new VariableBinding { Variable = "W", Kind = BindingKind.Unit, Property = "water" },
                        new VariableBinding { Variable = "S", Kind = BindingKind.Holder, HolderId = "reddepot", Property = "supply" },
                    },
                    Outputs = new() { new FormulaOutput { Target = OutputTarget.Unit, Property = "water", Op = OutputOp.Set } },
                },
            }).ToList(),
        };
    }

    /// <summary>Red's secrets the host keeps: a piece on the Red-owned map, the depot stock, a private-dice formula run on Red 2.</summary>
    static void AddHostSecrets(ModuleSession s)
    {
        var g = s.Game!;
        var type = PieceTypeParser.EncodeState(new[] { "piece;;;unit.svg;" + SecretName });
        g.Execute(new AddPiece("secret1", type, PieceTypeParser.EncodeState(new[] { "" }), RedMap, new PointD(10, 10), null, SecretName + " placed", Who: "Hosty"));
        g.Execute(new LedgerDelta(LedgerKey.Holder("reddepot"), "supply", 0, RedStock, "manual", null, null, null, null, null, "Hosty"));
        var runner = new FormulaRunner(s.Rules!, s.Model, (n, _) => Enumerable.Repeat(SecretRoll, n).ToList());
        var trace = runner.Run(g.State, new RunRequest("f-secret", PhaseFixture.RedTwo, new Dictionary<string, string>(),
            new Dictionary<string, string>(), new Dictionary<string, IReadOnlyList<int>>()));
        Assert.Null(trace.Error);
        foreach (var cmd in runner.Commands(trace, null, "manual", null, "red")) g.Execute(cmd);
    }

    sealed record Table(MainWindow Host, MainWindow Red, MainWindow Blue, OnlineFixture Fixture, LaunchRequest BlueRequest);

    static void Pump() => OnlineRig.Pump();

    static void PumpUntil(Func<bool> done, int seconds = 20) => OnlineRig.PumpUntil(done, seconds);

    /// <summary>The host (Green) opens the lobby; Red joins and takes Red, then Blue joins and takes Blue (in that order: the rejoin token
    /// file is per game code in the shared test app data root, so the last one written is Blue's).</summary>
    async Task<Table> Seated()
    {
        var f = await _rig.ModuleAsync(Pack);
        var host = await _rig.HostAsync(f, _hostDialogs, new[] { "green" });
        var (red, _) = await _rig.GuestAsync(f, _redDialogs, host.HostOnline!.Listing, "Ann");
        PumpUntil(() => red.GuestOnline is { State: GuestState.Playing } && red.Session?.Game != null);
        var (blue, blueReq) = await _rig.GuestAsync(f, _blueDialogs, host.HostOnline!.Listing, "Bob");
        PumpUntil(() => blue.GuestOnline is { State: GuestState.Playing } && blue.Session?.Game != null);
        return new Table(host, red, blue, f, blueReq);
    }

    static string Json(GameState? st) => JsonSerializer.Serialize(st, QuartermasterJson.Options);

    /// <summary>True once the guest holds exactly the view the host computes for <paramref name="side"/>.</summary>
    static bool Settled(MainWindow host, MainWindow guest, string side)
    {
        var hs = host.Session!;
        return guest.Session?.Game is { } g
            && Json(PlayerView.For(hs.Game!.State, hs.Rules, hs.Model, new HashSet<string> { side })) == Json(g.State);
    }

    /// <summary>A numeric marker counts only as a whole token (never inside a timestamp, id or hash), as Render's AssertNoMarker.</summary>
    static bool Has(string text, string marker) =>
        marker.All(char.IsDigit)
            ? System.Text.RegularExpressions.Regex.IsMatch(text, "(?<![0-9A-Za-z+/.])" + marker + "(?![0-9A-Za-z+/])")
            : text.Contains(marker, StringComparison.Ordinal);

    /// <summary>Every cell of every Ledger tab of the window.</summary>
    static string LedgerTexts(MainWindow w)
    {
        var lw = w.OpenLedger() ?? throw new InvalidOperationException("no ledger");
        Pump();
        var sb = new System.Text.StringBuilder();
        foreach (var key in lw.TabKeys)
        {
            lw.SelectTab(key);
            lw.Reload();
            Pump();
            var cols = lw.Headers.Count;
            for (var r = 0; r < lw.Rows.Count; r++)
                for (var c = 0; c < cols; c++) sb.Append(lw.CellText(r, c)).Append('|');
            sb.Append('\n');
        }
        lw.Close();
        Pump();
        return sb.ToString();
    }

    static string LogTexts(MainWindow w) => string.Join("\n", w.Log!.Rows.Select(r => r.Text));

    static string VisibleTexts(Window win) =>
        string.Join("|", win.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text))
        + "|" + string.Join("|", win.GetVisualDescendants().OfType<TextBox>().Select(t => t.Text));

    static Button ButtonOf(Window win, string winFormsLabel) =>
        win.GetVisualDescendants().OfType<Button>().Single(b => b.Content is string t && Shell.MenuText.ToWinForms(t) == winFormsLabel);

    // ---------- tests ----------

    [AvaloniaFact]
    public async Task Guest_sees_only_own_side()
    {
        var t = await Seated();
        AddHostSecrets(t.Host.Session!);
        // Red types its own private fuel; the host does it.
        t.Red.Actions!.EditLedger(LedgerKey.Piece(PhaseFixture.RedOne), "fuel", RedFuel, "TEST topped up");
        PumpUntil(() => Ledger.Get(t.Host.Session!.Game!.State, LedgerKey.Piece(PhaseFixture.RedOne), "fuel") == RedFuel);
        t.Host.HostOnline!.FlushSnapshots();
        PumpUntil(() => Settled(t.Host, t.Red, "red") && Settled(t.Host, t.Blue, "blue"));

        // Ledger: Red's own value reaches Red (so the check below proves something); none of Red's secrets reach Blue.
        // The positive controls wait for the posted UI refresh (a window may still show the previous snapshot for a moment); the
        // leak checks below then run on the refreshed texts.
        var redLedger = "";
        PumpUntil(() => Has(redLedger = LedgerTexts(t.Red), RedFuel.ToString()));
        var blueLedger = "";
        PumpUntil(() => (blueLedger = LedgerTexts(t.Blue)).Contains("Blue 1", StringComparison.Ordinal));
        foreach (var m in RedMarkers) Assert.False(Has(blueLedger, m), "Blue's ledger shows Red's " + m);
        Assert.DoesNotContain("31313", blueLedger, StringComparison.Ordinal);

        // A formula window over Red's hidden depot: the box is masked, the value never shown.
        var win = FormulaWindows.Show(t.Blue, t.Blue.Session!, "f-mix", PhaseFixture.BlueOne, null);
        Assert.NotNull(win);
        Pump();
        Assert.Equal(FormulaMasking.HostCalculatesText, win!.BoxText("S"));
        var formulaTexts = VisibleTexts(win);
        foreach (var m in RedMarkers) Assert.False(Has(formulaTexts, m), "Blue's formula window shows Red's " + m);
        win.Close();
        Pump();

        // Log lines: Red's log holds the secret piece and the private roll (the host, holding only Green, sees neither); Blue's has none.
        PumpUntil(() => LogTexts(t.Red) is var rl && rl.Contains(SecretName, StringComparison.Ordinal) && Has(rl, SecretRoll.ToString()));
        Assert.Contains(t.Host.Session!.Game!.State.Log, l => l.Text.Contains(SecretName, StringComparison.Ordinal));   // the full game has it
        PumpUntil(() => t.Blue.Log!.Rows.Count > 0);
        var blueLog = LogTexts(t.Blue);
        foreach (var m in RedMarkers) Assert.False(Has(blueLog, m), "Blue's log shows Red's " + m);

        // Map pieces: Blue's maps draw its own and the public pieces, never the piece on Red's map.
        var maps = t.Blue.GetVisualDescendants().OfType<MapView>().ToList();
        Assert.NotEmpty(maps);
        var drawn = maps.SelectMany(v => v.Session.Game!.State.Pieces.Values.Where(p => p.MapId == v.Map.Id)).ToList();
        Assert.Contains(drawn, p => p.Id == PhaseFixture.BlueOne);
        Assert.DoesNotContain(t.Blue.Session!.Game!.State.Pieces.Values, p => p.Id == "secret1" || p.MapId == RedMap);
        Assert.Contains(t.Host.Session!.Game!.State.Pieces.Values, p => p.Id == "secret1");
        Assert.Contains(t.Red.Session!.Game!.State.Pieces.Values, p => p.Id == "secret1");
    }

    [AvaloniaFact]
    public async Task Guest_formula_apply_goes_through_host()
    {
        var t = await Seated();
        Assert.True(t.Blue.Actions!.IsRemote);
        var key = LedgerKey.Piece(PhaseFixture.BlueOne);
        Assert.Equal(10, Ledger.Get(t.Blue.Session!.Game!.State, key, "water") ?? 10);

        var win = FormulaWindows.Show(t.Blue, t.Blue.Session!, "f-water", PhaseFixture.BlueOne, null);
        Assert.NotNull(win);
        Pump();
        ButtonOf(win!, "&Apply to ledger").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        // The host runs it and its game changes; the guest's view follows in the next snapshot; the window closes once the host agreed.
        PumpUntil(() => Ledger.Get(t.Host.Session!.Game!.State, key, "water") == 9);
        PumpUntil(() => Ledger.Get(t.Blue.Session!.Game!.State, key, "water") == 9);
        PumpUntil(() => !win!.IsVisible);
        Assert.Empty(FormulaWindows.OpenFor(t.Blue.Session!));
        Assert.Null(Ledger.Get(t.Host.Session!.Game!.State, LedgerKey.Piece(PhaseFixture.RedOne), "water"));   // nothing else changed
    }

    [AvaloniaFact]
    public async Task Guest_drops_and_rejoins_same_seat()
    {
        var t = await Seated();
        var h = t.Host.HostOnline!;
        var seat = t.Blue.GuestOnline!.PlayerId;
        PumpUntil(() => h.Seats.All.Count(s => s.Connected) == 3);

        // Blue's connection goes away (its app dropped): the host sees the seat disconnected, the game goes on.
        await t.BlueRequest.Online!.Client!.DisposeAsync();
        PumpUntil(() => h.Seats.All.Count(s => s.Connected) == 2);
        t.Blue.ForceClose();
        Pump();

        // Blue comes back with the same name on the same layout root: same seat, same side, no side question.
        var dialogs = new FakeDialogs { Answer = true };   // SidesAnswer null: a question would leave the guest choosing
        var (again, _) = await _rig.GuestAsync(t.Fixture, dialogs, h.Listing, "Bob");
        PumpUntil(() => again.GuestOnline is { State: GuestState.Playing } && again.Session?.Game != null);
        OnlineRig.PumpFor(700);   // past the side-question delay
        Assert.Equal(new[] { "blue" }, again.GuestOnline!.Sides.ToArray());
        Assert.Equal(seat, again.GuestOnline.PlayerId);
        Assert.Equal(GuestState.Playing, again.GuestOnline.State);
        Assert.Equal(0, again.SideQuestionsAsked);
        Assert.Equal(3, h.Seats.All.Count);
        PumpUntil(() => h.Seats.All.Count(s => s.Connected) == 3);
    }

    [AvaloniaFact]
    public async Task Host_leaves_guest_sees_banner_then_returns_to_launcher()
    {
        var t = await Seated();
        t.Blue.LauncherAvailable = true;   // started by the launcher loop: leaving goes back to it
        // Review Focus 3: the guest has a formula window open when the host leaves.
        var win = FormulaWindows.Show(t.Blue, t.Blue.Session!, "f-mix", PhaseFixture.BlueOne, null);
        Assert.NotNull(win);
        Pump();
        Assert.True(win!.IsVisible);
        var blueSession = t.Blue.Session!;

        await t.Host.LeaveOnlineGameAsync();
        Pump();
        Assert.Null(t.Host.HostOnline);
        Assert.NotNull(t.Host.Session!.Game);   // the host plays on locally

        PumpUntil(() => t.Blue.ReturnToLauncher);
        Assert.Contains(_blueDialogs.Infos, i => i == (HostSession.HostEndedText, "Online game"));
        Assert.Null(t.Blue.GuestOnline);
        Assert.False(t.Blue.IsVisible);
        Assert.True(t.Blue.ReturnToLauncher);
        // The formula window went with its game window (nothing left open over a closed game).
        Pump();
        Assert.False(win.IsVisible);
        Assert.Empty(FormulaWindows.OpenFor(blueSession));
    }

    [AvaloniaFact]
    public async Task Host_leaves_while_guest_side_choice_is_open()
    {
        var f = await _rig.ModuleAsync(Pack);
        var host = await _rig.HostAsync(f, _hostDialogs, new[] { "green" });
        // The guest's side question stays open (as a modal dialog would) while the host leaves.
        var held = new TaskCompletionSource<IReadOnlyList<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new FakeDialogs { Answer = true, HoldSides = held };
        var (guest, _) = await _rig.GuestAsync(f, dialogs, host.HostOnline!.Listing, "Cleo");
        guest.LauncherAvailable = true;
        PumpUntil(() => guest.SideQuestionsAsked == 1);
        Assert.Equal(GuestState.ChoosingSide, guest.GuestOnline!.State);

        await host.LeaveOnlineGameAsync();
        PumpUntil(() => guest.ReturnToLauncher);
        Assert.Contains(dialogs.Infos, i => i == (HostSession.HostEndedText, "Online game"));
        Assert.Null(guest.GuestOnline);
        Assert.False(guest.IsVisible);

        // The question is answered only now, over a window that went back to the launcher: nothing happens, nothing throws.
        held.SetResult(new[] { "blue" });
        OnlineRig.PumpFor(300);
        Assert.Null(guest.GuestOnline);
        Assert.True(guest.ReturnToLauncher);
        Assert.Equal(1, guest.SideQuestionsAsked);
        Assert.Null(host.HostOnline);
    }

    [AvaloniaFact]
    public async Task Relay_stops_guest_shows_relay_lost_banner_without_crash()
    {
        var t = await Seated();
        // Review Focus 3: the guest has a formula window open, with an Apply on its way to the host, when the relay goes away.
        var win = FormulaWindows.Show(t.Blue, t.Blue.Session!, "f-mix", PhaseFixture.BlueOne, null);
        Assert.NotNull(win);
        var apply = ButtonOf(win!, "&Apply to ledger");
        PumpUntil(() => apply.IsEnabled);
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(apply.IsEnabled);   // the send is pending: no second Apply
        // Stopped without running the UI thread: the host cannot have answered before the relay is gone, so no answer can ever come.
        Assert.True(Task.Run(async () => await _rig.Relay.DisposeAsync()).Wait(TimeSpan.FromSeconds(60)), "the relay did not stop");
        Assert.False(apply.IsEnabled);

        PumpUntil(() => t.Blue.OnlineBannerText == GuestSession.RelayLostText);
        PumpUntil(() => t.Host.OnlineBannerText == GuestSession.RelayLostText);
        // The pending Apply ends as "no answer" (the guest fails what it waits for when it pauses); the window stays open with its boxes.
        PumpUntil(() => win!.StatusText.Contains(GuestSession.NoAnswerText, StringComparison.Ordinal));
        Assert.True(win!.IsVisible);
        Assert.True(apply.IsEnabled);   // nothing pending any more: Apply may be tried again
        // A second Apply while paused is refused at once, never sent, never left pending.
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        Assert.True(apply.IsEnabled);
        Assert.Contains(GuestSession.RelayLostText, win.StatusText, StringComparison.Ordinal);
        Assert.Contains(win, FormulaWindows.OpenFor(t.Blue.Session!));
        OnlineRig.PumpFor(300);
        // Still open, still showing the last game; nothing threw on the UI thread.
        Assert.True(t.Blue.IsVisible);
        Assert.True(t.Host.IsVisible);
        Assert.True(win.IsVisible);
        Assert.NotNull(t.Blue.Session!.Game);
        Assert.NotNull(t.Blue.GuestOnline);
        Assert.Equal(GuestState.Paused, t.Blue.GuestOnline!.State);
        Assert.NotNull(t.Host.HostOnline);
    }
}
