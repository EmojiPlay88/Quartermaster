using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Quartermaster.Core;
using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// --smoke-online &lt;seconds&gt; [--screenshot-online &lt;png&gt;]: starts the relay (the solution's Quartermaster.Relay build, as a child
/// process) on a free loopback port; a host window creates a lobby for the given module and rules pack (invented Red/Blue sides), the
/// Online lobby window lists it, a second window joins it as a guest, chooses Blue, sends one intent (a dice roll) and a chat line, and the
/// host leaves. Screenshots: <c>-lobby</c>, <c>-host</c>, <c>-guest</c>. Needs QUARTERMASTER_LAYOUT_ROOT (everything it writes stays there)
/// and --smoke N. The relay is found through <see cref="RelayExeVar"/> or next to / above the App's output.
/// </summary>
static class OnlineSmoke
{
    const string Password = "red-and-blue";
    const string LobbyName = "Smoke lobby";
    public const string RelayExeVar = "QUARTERMASTER_RELAY_EXE";

    public static void Run(StartupOptions opts)
    {
        Process? relay = null;
        try
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppDataRoot.EnvVar)))
                throw new InvalidOperationException("QUARTERMASTER_LAYOUT_ROOT must be set for the online smoke.");
            if (opts.ModulePath == null || opts.RulesPath == null || !opts.Smoke)
                throw new InvalidOperationException("--smoke-online needs a module path, --rules <pack> and --smoke N.");
            var exe = FindRelay() ?? throw new InvalidOperationException(
                $"The relay was not found. Build Quartermaster.Relay (dotnet build Quartermaster.slnx) or set {RelayExeVar} to Quartermaster.Relay.exe or .dll.");
            var port = FreePort();
            var dataDir = Path.Combine(AppDataRoot.Path, "smoke-relay");
            relay = StartRelay(exe, port, dataDir);
            if (!WaitForPort(relay, port, TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException(relay.HasExited ? $"The relay exited at start (code {relay.ExitCode})." : "The relay did not start listening within 30 s.");
            Program.AppLog($"smoke: relay ({Path.GetFileName(exe)}) listening on 127.0.0.1:{port}");
            Exercise(opts, port, TimeSpan.FromSeconds(Math.Clamp(opts.SmokeOnline ?? 60, 10, 600)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("smoke: online FAILED: " + ex.Message);
            Program.ExitCode = 1;
        }
        finally
        {
            if (relay != null)
            {
                try { if (!relay.HasExited) { relay.Kill(entireProcessTree: true); relay.WaitForExit(10_000); } }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("smoke: relay stop: " + ex.Message); }
                relay.Dispose();
            }
        }
    }

    /// <summary>The relay binary: the env var, the App's own folder, or the solution's Relay build found above the App's output.</summary>
    static string? FindRelay()
    {
        if (Environment.GetEnvironmentVariable(RelayExeVar) is { Length: > 0 } env)
            return File.Exists(env) ? Path.GetFullPath(env) : null;
        var here = AppContext.BaseDirectory;
        foreach (var name in new[] { "Quartermaster.Relay.exe", "Quartermaster.Relay.dll" })
            if (File.Exists(Path.Combine(here, name))) return Path.Combine(here, name);
        for (var dir = new DirectoryInfo(here); dir != null; dir = dir.Parent)
        {
            var relayBin = Path.Combine(dir.FullName, "src", "Quartermaster.Relay", "bin");
            if (!Directory.Exists(relayBin)) continue;
            return Directory.EnumerateFiles(relayBin, "Quartermaster.Relay.exe", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(relayBin, "Quartermaster.Relay.dll", SearchOption.AllDirectories))
                .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        return null;
    }

    static Process StartRelay(string exe, int port, string dataDir)
    {
        var dll = exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(dll ? "dotnet" : exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        if (dll) psi.ArgumentList.Add(exe);
        psi.ArgumentList.Add("--Relay:Port=" + port.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--Relay:DataDir=" + dataDir);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("The relay could not be started.");
        // Drained (and dropped) so a full pipe never blocks the relay; its own log holds nothing secret but is not needed here.
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    static bool WaitForPort(Process p, int port, TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        while (DateTime.UtcNow < until && !p.HasExited)
        {
            try
            {
                using var c = new TcpClient();
                c.Connect(IPAddress.Loopback, port);
                return true;
            }
            catch (SocketException) { Thread.Sleep(100); }
        }
        return false;
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static void Check(bool ok, string what)
    {
        Program.AppLog($"smoke: online {what} {(ok ? "ok" : "FAILED")}");
        if (!ok) Program.ExitCode = 1;
    }

    static bool Pump(Func<bool> done, TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        while (!done() && DateTime.UtcNow < until)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }
        for (var i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(20); }
        return done();
    }

    static string ShotPath(string? basePath, string suffix)
    {
        if (basePath == null) return "";
        var full = Path.GetFullPath(basePath);
        return Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + suffix + ".png");
    }

    static bool LogHas(MainForm f, string text) =>
        f.Session?.View?.Log?.Any(e => e.Text?.Contains(text, StringComparison.Ordinal) == true) == true;

    static void Exercise(StartupOptions opts, int port, TimeSpan max)
    {
        var module = Path.GetFullPath(opts.ModulePath!);
        var rules = Path.GetFullPath(opts.RulesPath!);
        var relayText = $"127.0.0.1:{port}";
        var uri = OnlineLobbyModel.ParseRelay(relayText, out var err) ?? throw new InvalidOperationException(err);
        var store = new SettingsStore(SettingsStore.DefaultRoot);
        store.Load(null);
        store.Update(s => s with { RelayAddress = relayText });
        var lib = new ModuleLibrary(store);
        lib.Add(module);
        Task.Run(() => lib.RefreshAsync(null, CancellationToken.None)).GetAwaiter().GetResult();
        var row = lib.Rows.First(r => !r.Missing && string.Equals(r.Entry.Path, module, StringComparison.OrdinalIgnoreCase));
        lib.SetRulesPack(row.Entry, rules);
        row = lib.Rows.First(r => string.Equals(r.Entry.Path, module, StringComparison.OrdinalIgnoreCase));
        var formOpts = opts with { ModulePath = null, RulesPath = null, SmokeSeconds = null, SmokeOnline = null, ScreenshotOnline = null };

        // ---- host ----
        var hostLaunch = new LaunchRequest(module, rules, LaunchKind.HostOnline,
            new NewGameRequest(GameMode.Online, null, Difficulty.SemiAuto, null, LobbyName, Password), null,
            new OnlineLaunch(uri, "Red host", Password, LobbyName, new[] { "red" }));
        using var host = new MainForm(formOpts, hostLaunch);
        host.Show();
        Pump(() => host.HostOnline != null || host.OnlineError != null, max);
        Check(host.HostOnline != null, $"host lobby created ({host.OnlineError ?? host.OnlineStatusText})");
        if (host.HostOnline is not { } hs) return;
        var code = hs.Listing.Code;

        // ---- lobby window ----
        using var lobby = new OnlineLobbyForm(row, lib, store) { StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
        lobby.Show();
        Pump(() => lobby.StateText.StartsWith("Connected", StringComparison.Ordinal), max);   // the first refresh runs on Load
        Check(lobby.RowsForSmoke.All(r => r.Listing.Code != code), "own lobby hidden from its host");
        OnlineLobbyModel.UnmarkHosted(code);   // from here on the lobby window plays a second computer
        Wait(lobby.RefreshAsync(manual: true), max);
        var listed = lobby.RowsForSmoke.FirstOrDefault(r => r.Listing.Code == code);
        Check(listed is { Lobby: LobbyName, Locked: true, FreeSides: "Blue", Players: "1/2", CannotJoin: null }, $"lobby listed ({lobby.RowsForSmoke.Count} row(s))");
        Check(lobby.Text == OnlineLobbyModel.Title(row.Entry.Title), $"lobby title '{lobby.Text}'");
        if (opts.ScreenshotOnline != null) Shot(lobby, ShotPath(opts.ScreenshotOnline, "-lobby"));
        if (listed == null) return;
        var bad = Wait(lobby.JoinAsync(listed.Listing, "Blue guest", "wrong password"), max);
        Check(bad == null, "wrong password refused by the relay");
        var join = Wait(lobby.JoinAsync(listed.Listing, "Blue guest", Password), max);
        Check(join is { Kind: LaunchKind.JoinOnline, Online.Client: not null }, "joined through the lobby window");
        lobby.Close();
        if (join == null) return;

        // ---- guest ----
        using var guest = new MainForm(formOpts, join);
        guest.StartPosition = FormStartPosition.Manual;
        guest.Location = new Point(80, 80);
        guest.Show();
        Pump(() => guest.GuestOnline?.State is GuestState.ChoosingSide or GuestState.Refused, max);
        Check(guest.GuestOnline?.State == GuestState.ChoosingSide, $"guest welcomed ({guest.GuestOnline?.State})");
        Check(guest.ChooseSidesForSmoke(new[] { "blue" }), "side choice sent");
        Pump(() => guest.GuestOnline?.State == GuestState.Playing, max);
        Check(guest.GuestOnline?.State == GuestState.Playing && guest.Session?.IsGuest == true, "guest playing Blue");
        Check(guest.Session?.CanSave == false, "guest cannot save");
        Pump(() => hs.Seats.All.Count == 2 && host.OnlineStatusText.Contains("2 players", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        Check(host.OnlineStatusText == OnlineLobbyModel.StatusText(code, 2), $"host status '{host.OnlineStatusText}'");

        // One intent: a dice-tool roll the host makes for the guest.
        guest.Actions?.Roll(new DiceSpec(1, 6, 0), "Smoke roll");
        Check(Pump(() => LogHas(host, "Smoke roll") && LogHas(guest, "Smoke roll"), max), "guest intent (roll) ran on the host");
        Check(guest.SendChatForSmoke("Hello from Blue"), "guest chat box present");
        Check(Pump(() => LogHas(host, "Hello from Blue") && LogHas(guest, "Hello from Blue"), max), "guest chat reached the host's log");
        Check(host.SendChatForSmoke("Hello from Red"), "host chat box present");
        Check(Pump(() => LogHas(guest, "Hello from Red"), max), "host chat reached the guest");
        Check(guest.OnlineStatusText.StartsWith($"Online: {code} · ", StringComparison.Ordinal), $"guest status '{guest.OnlineStatusText}'");

        if (opts.ScreenshotOnline != null)
        {
            Pump(() => host.TilingDoneForSmoke && guest.TilingDoneForSmoke, TimeSpan.FromSeconds(30));
            host.SaveScreenshotForSmoke(ShotPath(opts.ScreenshotOnline, "-host"));
            guest.SaveScreenshotForSmoke(ShotPath(opts.ScreenshotOnline, "-guest"));
        }

        foreach (var (who, f) in new[] { ("host", host), ("guest", guest) })
            foreach (var d in f.Session?.Log.Items.Where(x => x.Severity >= Quartermaster.Vassal.DiagnosticSeverity.Warning) ?? Enumerable.Empty<Quartermaster.Vassal.Diagnostic>())
                Program.AppLog($"smoke: {who} diagnostic: [{d.Category}] {d.Message}");

        // The host leaves: the guest is told, the host plays on locally.
        host.LeaveOnlineForSmoke();
        Check(host.HostOnline == null && host.Session?.Game != null, "host left and plays on locally");
        Check(host.Session?.ViewSides == null && host.Session?.Game?.State.Players.Count == 0, "host back to solo with the full view");
        Pump(() => guest.GuestOnline?.State == GuestState.Refused, max);
        Check(guest.GuestOnline?.StatusText == HostSession.HostEndedText, $"guest told the host left ('{guest.GuestOnline?.StatusText}')");
        guest.Close();
        host.Close();
        Pump(() => false, TimeSpan.FromMilliseconds(300));
    }

    static T? Wait<T>(Task<T> t, TimeSpan max)
    {
        Pump(() => t.IsCompleted, max);
        return t.IsCompletedSuccessfully ? t.Result : default;
    }

    static void Wait(Task t, TimeSpan max) => Pump(() => t.IsCompleted, max);

    static void Shot(Form f, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Program.AppLog("smoke: screenshot saved to " + path);
    }
}
