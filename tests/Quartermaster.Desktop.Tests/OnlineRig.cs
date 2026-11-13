using Avalonia.Controls;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Desktop.Launcher;
using Quartermaster.Desktop.Online;
using Quartermaster.Net;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>The invented TEST module in the launcher's library, its rules pack file and its "Start" setup (Red 1, Red 2, Blue 1).</summary>
sealed record OnlineFixture(ModuleRow Row, ModuleLibrary Library, SettingsStore Store, string ModulePath, string PackPath, SetupDef Setup);

/// <summary>
/// Shared plumbing of the online game-window tests (port stage 5c tasks 4 and 5): a real relay running in process on loopback (its
/// certificate pinned in the test app data root, so nobody is asked), game windows on temp layout roots, and the lobby join a launcher
/// hands over. Invented TEST content only; headless, nothing is ever shown on screen.
/// </summary>
sealed class OnlineRig : IAsyncDisposable
{
    readonly List<IDisposable> _disposables = new();
    readonly List<Window> _windows = new();

    OnlineRig(InProcessRelay relay) => Relay = relay;

    public string Tmp { get; } = Path.Combine(Path.GetTempPath(), "qm-online-" + Guid.NewGuid().ToString("N"));
    public InProcessRelay Relay { get; }

    public static async Task<OnlineRig> StartAsync()
    {
        var rig = new OnlineRig(await InProcessRelay.StartAsync());
        Directory.CreateDirectory(rig.Tmp);
        await rig.Relay.ClientAsync(RelayTrust.Pins());   // pins the relay's certificate where the windows look: nobody is asked
        return rig;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var w in _windows)
        {
            try
            {
                if (w is MainWindow m)
                {
                    if (m.Session is { } s) FormulaWindows.CloseAllFor(s);
                    m.ForceClose();
                }
                else w.Close();
            }
            catch (InvalidOperationException) { }
        }
        Dispatcher.UIThread.RunJobs();
        await Relay.DisposeAsync();
        foreach (var d in _disposables) d.Dispose();
        try { Directory.Delete(Tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The phase fixture module in a library, with <paramref name="pack"/> (given the module's sha) saved and set as its pack.</summary>
    public async Task<OnlineFixture> ModuleAsync(Func<string, RulesPack> pack)
    {
        var fx = PhaseFixture.Module();
        _disposables.Add(fx);
        var path = fx.Build();
        var store = new SettingsStore(Path.Combine(Tmp, "lobby-layout"));
        store.Load(null);
        var lib = new ModuleLibrary(store);
        lib.Add(path);
        await lib.RefreshAsync(null, CancellationToken.None);
        var row = lib.Rows[0];
        var packPath = Path.Combine(Tmp, "test-" + Guid.NewGuid().ToString("N") + ".qrules");
        RulesPackIo.Save(pack(row.Entry.Sha256!), packPath);
        lib.SetRulesPack(row.Entry, packPath);
        using var probe = ModuleSession.Open(path, Path.Combine(Tmp, "cache"), null);
        return new OnlineFixture(row, lib, store, path, packPath, probe.Model.Setups[0]);   // "Start": Red 1, Red 2, Blue 1
    }

    /// <summary>A shown game window on the layout root "layout-{name}" (the same name is the same root).</summary>
    public MainWindow Window(FakeDialogs dialogs, string name)
    {
        var w = new MainWindow(dialogs, Path.Combine(Tmp, "cache"), null, Path.Combine(Tmp, "layout-" + name)) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        return w;
    }

    public LaunchRequest HostRequest(OnlineFixture f, IReadOnlyList<string> hostSides, string? savePath = null, Uri? relay = null) =>
        new(f.ModulePath, f.PackPath, LaunchKind.HostOnline,
            savePath == null ? new NewGameRequest(GameMode.Online, f.Setup, Difficulty.FullyAuto, null, "TEST lobby", null) : null, savePath,
            new OnlineLaunch(relay ?? Relay.Uri, "Hosty", "", "TEST lobby", hostSides));

    /// <summary>A host window that opened the TEST lobby holding <paramref name="hostSides"/>.</summary>
    public async Task<MainWindow> HostAsync(OnlineFixture f, FakeDialogs dialogs, IReadOnlyList<string> hostSides)
    {
        var w = Window(dialogs, "host");
        await w.StartFromLaunchAsync(HostRequest(f, hostSides));
        Assert.NotNull(w.HostOnline);
        return w;
    }

    /// <summary>Joins the lobby through the lobby window (relay field typed, the join run) and returns what it hands the launcher.</summary>
    public async Task<LaunchRequest> JoinRequestAsync(OnlineFixture f, FakeDialogs dialogs, LobbyListing listing, string name)
    {
        var lobby = new OnlineLobbyWindow(f.Row, f.Library, f.Store, dialogs);
        _windows.Add(lobby);
        lobby.Show();
        Pump();
        lobby.RelayBox.Text = Relay.Address;
        var req = await lobby.JoinAsync(listing, name, "");
        Assert.NotNull(req);
        lobby.Close();
        Pump();
        return req!;
    }

    /// <summary>A guest window (layout root "layout-guest-{name}") started from a lobby join, as the launcher would start it.</summary>
    public async Task<(MainWindow Window, LaunchRequest Request)> GuestAsync(OnlineFixture f, FakeDialogs dialogs, LobbyListing listing, string name)
    {
        var req = await JoinRequestAsync(f, dialogs, listing, name);
        var g = Window(dialogs, "guest-" + name);
        await g.StartFromLaunchAsync(req);
        Assert.NotNull(g.GuestOnline);
        return (g, req);
    }

    public static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>Runs the UI thread (jobs, timers, network continuations) until <paramref name="done"/>.</summary>
    public static void PumpUntil(Func<bool> done, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            Pump();
            if (done()) return;
            Assert.True(DateTime.UtcNow < until, "timed out");
            using var slice = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
    }

    /// <summary>Runs the UI thread for <paramref name="ms"/> (timers included).</summary>
    public static void PumpFor(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        PumpUntil(() => DateTime.UtcNow >= until, seconds: Math.Max(5, ms / 1000 + 5));
    }
}
