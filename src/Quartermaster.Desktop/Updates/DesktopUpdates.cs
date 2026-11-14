using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Quartermaster.Desktop.Launcher;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Render.Update;

namespace Quartermaster.Desktop.Updates;

/// <summary>
/// The update screens of the Desktop app (WinForms AppUpdates and the launcher's update part) for this run. The feed is the test feed when
/// <see cref="UpdateFeeds.EnvVar"/> is set, GitHub when this copy is a packaged one (<see cref="InstallLayout.Detect"/> found its marker),
/// and none otherwise (a dev build: nothing is shown, checked or downloaded). Update now installs: the helper swaps the app folder (zip, tar)
/// or the whole unit (<c>.app</c>, AppImage) after the app exits; a .deb or .rpm is handed to the system's package installer. The state
/// (last check, skipped version) lives in <c>&lt;UpdatesRoot&gt;/state.json</c>, not in the shared settings.json.
/// </summary>
public sealed class DesktopUpdates : IUpdateSlot
{
    public const string FolderName = "Updates";
    /// <summary>The stage-5a folder, removed on start.</summary>
    const string OldFolderName = "Updates-desktop";
    const string GameOpenText = "Close the game and use the launcher to update.";
    /// <summary>Help → Check for updates… with a game open (WinForms MainForm.CheckForUpdates).</summary>
    public const string InGameText = "Close the game and use the launcher to update.\n\nGo back to the launcher now (File → Back to launcher)?";
    public const string InGameCaption = "Check for updates";
    const string Caption = "Quartermaster";
    const string StateFile = "state.json";

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    DateTime? _lastCheckUtc;
    string? _skippedVersion;

    /// <summary>The test feed, GitHub for a packaged copy, or null (a dev build: no update UI at all).</summary>
    public IUpdateFeed? Feed { get; }

    /// <summary>How this copy is installed (null for a dev build: Update now then only offers the release page).</summary>
    public InstallLayout? Layout { get; }

    /// <summary><c>AppDataRoot.Sub("Updates")</c>: downloads, the helper's journal and markers, state.json.</summary>
    public string UpdatesRoot { get; }

    /// <summary>The automatic check already ran this run (a failed one is not retried until the next start).</summary>
    public bool CheckedThisRun { get; set; }

    /// <summary>Help → Check for updates… went back to the launcher: it checks as soon as it opens.</summary>
    public bool CheckOnNextLauncher { get; set; }

    /// <summary>The update the last check found this run (the launcher shows it again after Back to launcher).</summary>
    public UpdateInfo? Offered { get; set; }

    /// <summary>Later was clicked: the bar stays hidden until the app starts again.</summary>
    public bool Later { get; set; }

    /// <summary>When the last successful check ran (state.json; setting it saves).</summary>
    public DateTime? LastCheckUtc
    {
        get => _lastCheckUtc;
        set { _lastCheckUtc = value; SaveState(); }
    }

    /// <summary>The version the user chose to skip, as "0.2.0" (state.json; setting it saves).</summary>
    public string? SkippedVersion
    {
        get => _skippedVersion;
        set { _skippedVersion = value; SaveState(); }
    }

    /// <summary>The running version (tests offer an update to an older one).</summary>
    internal Version Current { get; set; } = AppVersion.Current;

    /// <summary>Opens a release page in the browser (tests record it instead).</summary>
    internal Action<TopLevel, Uri> OpenPage { get; set; } = (top, uri) => _ = LaunchPage(top, uri);

    /// <summary>The update bar of the launcher attached last (null before any or without a feed).</summary>
    internal UpdateBar? Bar { get; private set; }

    /// <summary>The check started last (manual or automatic).</summary>
    internal Task CheckTask { get; private set; } = Task.CompletedTask;

    /// <summary>The last Update now (its window, then the message).</summary>
    internal Task UpdateNowTask { get; private set; } = Task.CompletedTask;

    internal WhatsNewWindow? WhatsNew { get; private set; }
    internal UpdateWindow? Updating { get; private set; }

    /// <summary>Starts a process: the updater helper, or <c>xdg-open</c> for a package (tests record it instead).</summary>
    internal Func<ProcessStartInfo, Process?> StartProcess { get; set; } = psi => Process.Start(psi);

    /// <summary>Ends the app once the helper runs: closes the launcher, which ends the launch loop and so the app (tests record it instead).</summary>
    internal Action<LauncherWindow> RequestExit { get; set; } = w => w.Close();

    /// <summary>True while a game window is open (the launcher never updates then).</summary>
    internal Func<bool> GameOpen { get; set; } = () =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d && d.Windows.OfType<MainWindow>().Any(w => w.IsVisible);

    /// <summary>True when another process runs from one of the given executables (it may hold a game the swap would break).</summary>
    internal Func<IReadOnlyList<string>, bool> OthersRunning { get; set; } = exes => RunningCopies.OthersRunning(exes, Environment.ProcessId);

    internal DesktopUpdates(IUpdateFeed? feed, string updatesRoot, InstallLayout? layout = null)
    {
        Feed = feed;
        Layout = layout;
        UpdatesRoot = updatesRoot ?? throw new ArgumentNullException(nameof(updatesRoot));
        if (feed != null) LoadState();
    }

    /// <summary>
    /// The app's instance: a <see cref="FolderFeed"/> when <see cref="UpdateFeeds.EnvVar"/> is set, else GitHub when this copy is packaged
    /// (a marker beside the exe), else no feed. With a feed, downloads of other versions left in the updates folder are removed; the old
    /// stage-5a <c>Updates-desktop</c> folder is always removed (both best effort).
    /// </summary>
    public static DesktopUpdates FromEnvironment(string? appDataRoot = null) =>
        FromEnvironment(appDataRoot, Environment.GetEnvironmentVariable, AppContext.BaseDirectory);

    /// <summary>As <see cref="FromEnvironment(string?)"/>, reading the environment through <paramref name="env"/> and the marker in
    /// <paramref name="baseDir"/> (tests pass their own, so they never depend on, or change, the process environment or the build folder).</summary>
    internal static DesktopUpdates FromEnvironment(string? appDataRoot, Func<string, string?> env, string baseDir)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(baseDir);
        var data = appDataRoot ?? AppDataRoot.Path;
        var root = Path.Combine(data, FolderName);
        RemoveOldFolder(Path.Combine(data, OldFolderName));
        var dir = env(UpdateFeeds.EnvVar);
        var layout = InstallLayout.Detect(baseDir, env);
        IUpdateFeed? feed = !string.IsNullOrWhiteSpace(dir) ? new FolderFeed(dir)
            : layout != null ? new GitHubFeed(GitHubFeed.CreateClient())
            : null;
        var u = new DesktopUpdates(feed, root, layout);
        if (feed != null) UpdateInstaller.CleanupOld(root, AppVersion.Current);
        return u;
    }

    static void RemoveOldFolder(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("update: could not remove " + dir + ": " + ex.Message);
        }
    }

    // ---------- state.json ----------

    sealed record State(
        [property: JsonPropertyName("lastCheckUtc")] DateTime? LastCheckUtc,
        [property: JsonPropertyName("skippedVersion")] string? SkippedVersion);

    void LoadState()
    {
        try
        {
            var path = Path.Combine(UpdatesRoot, StateFile);
            if (!File.Exists(path)) return;
            var s = JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json);
            _lastCheckUtc = s?.LastCheckUtc is { } t ? DateTime.SpecifyKind(t.ToUniversalTime(), DateTimeKind.Utc) : null;
            _skippedVersion = s?.SkippedVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            System.Diagnostics.Trace.WriteLine("update: could not read the update state: " + ex.Message);
        }
    }

    void SaveState()
    {
        try
        {
            Directory.CreateDirectory(UpdatesRoot);
            var path = Path.Combine(UpdatesRoot, StateFile);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new State(_lastCheckUtc, _skippedVersion), Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine("update: could not save the update state: " + ex.Message);
        }
    }

    // ---------- the launcher ----------

    /// <summary>
    /// Fills the launcher once it is open (WinForms LauncherForm.StartUpdateUi): the update bar and the "Check for updates" link, then the
    /// check Help asked for, the offer already found this run, or the automatic check when due. Nothing at all without a feed.
    /// </summary>
    public void Attach(LauncherWindow w)
    {
        ArgumentNullException.ThrowIfNull(w);
        if (Feed is not { } feed) return;
        var bar = new UpdateBar();
        Bar = bar;
        w.UpdateBarHost.Children.Add(bar);
        var link = LauncherWindow.Link("Check for updates");
        w.FooterLinks.Children.Add(link);
        var cts = new CancellationTokenSource();
        var ui = new LauncherUi(w, bar, link, cts.Token);
        w.Closed += (_, _) =>
        {
            ui.Closed = true;
            cts.Cancel();
        };
        link.Click += (_, _) => StartCheck(feed, ui, manual: true);

        if (CheckOnNextLauncher)
        {
            CheckOnNextLauncher = false;
            StartCheck(feed, ui, manual: true);
            return;
        }
        if (Offered is { } offered)
        {
            if (!Later && SkippedVersion != AppVersion.Format(offered.Version)) ShowOffer(ui, offered);
            return;
        }
        if (UpdateOffer.AutoCheckDue(w.Store.Current.CheckUpdatesOnStart, CheckedThisRun, LastCheckUtc, DateTime.UtcNow))
            StartCheck(feed, ui, manual: false);
    }

    sealed class LauncherUi(LauncherWindow w, UpdateBar bar, Button link, CancellationToken ct)
    {
        public LauncherWindow Window { get; } = w;
        public UpdateBar Bar { get; } = bar;
        public Button Link { get; } = link;
        public CancellationToken Token { get; } = ct;
        public bool Closed { get; set; }
    }

    void StartCheck(IUpdateFeed feed, LauncherUi ui, bool manual)
    {
        if (ui.Closed || !CheckTask.IsCompleted) return;
        ui.Link.IsEnabled = false;
        CheckTask = Guarded(ui, () => CheckAsync(feed, ui, manual, SkippedVersion));
    }

    /// <summary>Checks on a worker; the automatic check is silent unless an update is found (a failure is only traced).</summary>
    async Task CheckAsync(IUpdateFeed feed, LauncherUi ui, bool manual, string? skipped)
    {
        CheckResult r;
        var current = Current;
        var kind = Layout?.Kind ?? PackageKind.WinZip;   // no layout (test feed on a dev build): the win zip, as stage 5a
        try { r = await Task.Run(() => UpdateChecker.Check(feed, current, skipped, ui.Token, kind)); }
        catch (OperationCanceledException) { return; }
        CheckedThisRun = true;
        if (r.Outcome != CheckOutcome.Failed) LastCheckUtc = DateTime.UtcNow;
        if (r.Outcome == CheckOutcome.Available) Offered = r.Info;
        if (r.Outcome == CheckOutcome.Failed) System.Diagnostics.Trace.WriteLine($"update: {(manual ? "" : "automatic ")}check failed: {r.Error}");
        if (ui.Closed) return;
        ui.Link.IsEnabled = true;
        switch (r.Outcome)
        {
            case CheckOutcome.Available:
                if (manual) Later = false;
                if (manual || !Later) ShowOffer(ui, r.Info!);
                break;
            case CheckOutcome.Skipped when manual:
                if (!await ui.Window.Dialogs.AskYesNoAsync(ui.Window, UpdateOffer.SkippedQuestion(r.Info!), Caption, defaultNo: false)) break;
                if (ui.Closed) break;
                SkippedVersion = null;
                Offered = r.Info;
                Later = false;
                ShowOffer(ui, r.Info!);
                break;
            case CheckOutcome.UpToDate when manual:
                ui.Bar.Clear();
                await ui.Window.Dialogs.ShowInfoAsync(ui.Window, UpdateOffer.ManualText(r), Caption);
                break;
            case CheckOutcome.Failed when manual:
                await ui.Window.Dialogs.ShowWarningAsync(ui.Window, UpdateOffer.ManualText(r), Caption);
                break;
        }
    }

    /// <summary>The yellow bar: "Quartermaster 0.2.0 is available." What's new · Update now · Later · Skip this version.</summary>
    void ShowOffer(LauncherUi ui, UpdateInfo info)
    {
        var v = AppVersion.Format(info.Version);
        ui.Bar.Show(UpdateOffer.BarText(info),
            ("What's new", () => ShowWhatsNew(ui, info)),
            ("Update now", () => StartUpdateNow(ui, info)),
            ("Later", () => { Later = true; ui.Bar.Clear(); }),
            ("Skip this version", () =>
            {
                SkippedVersion = v;
                Offered = null;
                ui.Bar.Clear();
            }));
    }

    void ShowWhatsNew(LauncherUi ui, UpdateInfo info)
    {
        if (ui.Closed) return;
        var dlg = new WhatsNewWindow(info, OpenPage);
        WhatsNew = dlg;
        _ = Guarded(ui, () => dlg.ShowDialogTracked(ui.Window));
    }

    void StartUpdateNow(LauncherUi ui, UpdateInfo info)
    {
        if (ui.Closed || !UpdateNowTask.IsCompleted || Feed is not { } feed) return;
        UpdateNowTask = Guarded(ui, () => UpdateNowAsync(feed, ui, info));
    }

    /// <summary>
    /// Update now (WinForms AppUpdates.UpdateNow): refuses while a game is open or another copy runs; offers the release page when this copy
    /// cannot update itself (no layout, folder not writable, helper or file list missing); else downloads with progress. A .deb or .rpm is
    /// then handed to the package installer and the app keeps running; otherwise the helper starts and the app exits so it can swap.
    /// Any problem leaves everything as it was and says so plainly.
    /// </summary>
    async Task UpdateNowAsync(IUpdateFeed feed, LauncherUi ui, UpdateInfo info)
    {
        if (await RefusedWhileRunning(ui)) return;
        if (Layout is not { } l)
        {
            await OfferPage(ui, info, UpdateInstaller.NoManifestText + " Open the download page?");
            return;
        }
        if (l.SelfUpdates)
        {
            var unit = l.Kind is PackageKind.MacApp or PackageKind.LinuxAppImage;
            var swapDir = unit ? Path.GetDirectoryName(Path.GetFullPath(l.UnitPath!))! : l.AppDir;
            if (!UpdateInstaller.AppFolderWritable(swapDir))
            {
                await OfferPage(ui, info, "Quartermaster can't update itself in this folder. Open the download page?");
                return;
            }
            if (!File.Exists(Path.Combine(l.AppDir, UpdateInstaller.HelperName)))
            {
                await OfferPage(ui, info, $"Quartermaster can't update itself: {UpdateInstaller.HelperName} is missing from its folder. Open the download page?");
                return;
            }
            if (l.Kind != PackageKind.LinuxAppImage && !UpdateInstaller.CanSelfUpdate(l.AppDir))
            {
                await OfferPage(ui, info, UpdateInstaller.NoManifestText + " Open the download page?");
                return;
            }
        }

        var dlg = new UpdateWindow(feed, info, UpdatesRoot);
        Updating = dlg;
        _ = dlg.ShowDialogTracked(ui.Window);
        await dlg.Done;
        if (ui.Closed) return;
        if (dlg.Error is { } error)
        {
            await ui.Window.Dialogs.ShowWarningAsync(ui.Window, NotInstalled(error), Caption);
            return;
        }
        if (dlg.Staged is not { } staged) return;   // cancelled

        if (!l.SelfUpdates)
        {
            if (PackageHandOff.Open(staged.StagedDir, StartProcess))
                await ui.Window.Dialogs.ShowInfoAsync(ui.Window,
                    $"Quartermaster {AppVersion.Format(info.Version)} was downloaded. Your system's package installer opens now to install it; when it is done, start Quartermaster again.",
                    Caption);
            else
                await ui.Window.Dialogs.ShowWarningAsync(ui.Window, "Could not open the package installer. The package is saved at:\n\n" + staged.StagedDir, Caption);
            return;
        }

        if (await RefusedWhileRunning(ui)) return;   // the download took a while: check again right before handing over
        try
        {
            UpdateInstaller.LaunchHelper(staged, l, Environment.ProcessId, StartProcess);
        }
        catch (Exception ex) when (ex is UpdateRefused or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            System.Diagnostics.Trace.WriteLine("update: could not start the helper: " + ex.Message);
            await ui.Window.Dialogs.ShowWarningAsync(ui.Window, NotInstalled(ex.Message), Caption);
            return;
        }
        System.Diagnostics.Trace.WriteLine($"update: helper started for {AppVersion.Format(info.Version)}; closing so it can apply the update");
        RequestExit(ui.Window);
    }

    static string NotInstalled(string why) => string.Format(System.Globalization.CultureInfo.InvariantCulture, UpdateOffer.NotInstalledFormat, why);

    /// <summary>A game window or another copy of Quartermaster is open: says so and returns true.</summary>
    async Task<bool> RefusedWhileRunning(LauncherUi ui)
    {
        var text = GameOpen() ? GameOpenText : OthersRunning(RunningExes()) ? RunningCopies.CloseOthersText : null;
        if (text == null) return false;
        await ui.Window.Dialogs.ShowInfoAsync(ui.Window, text, Caption);
        return true;
    }

    /// <summary>The executables another copy may run from: the unit's for <c>.app</c> and AppImage, else either exe name of the app folder.</summary>
    IReadOnlyList<string> RunningExes()
    {
        if (Layout is not { } l) return Environment.ProcessPath is { } p ? [p] : [];
        if (l.Kind is PackageKind.MacApp or PackageKind.LinuxAppImage) return [l.ExePath];
        return new[] { l.ExePath, Path.Combine(l.AppDir, "Quartermaster.exe"), Path.Combine(l.AppDir, UpdateInstaller.AppExe) }
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    async Task OfferPage(LauncherUi ui, UpdateInfo info, string question)
    {
        if (await ui.Window.Dialogs.AskYesNoAsync(ui.Window, question, Caption, defaultNo: false) && !ui.Closed)
            OpenPage(ui.Window, info.PageUrl);
    }

    /// <summary>Runs a launcher handler so that nothing it throws reaches the dispatcher: it is traced and shown as a warning.</summary>
    static async Task Guarded(LauncherUi ui, Func<Task> a)
    {
        try { await a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("update: " + ex.Message);
            if (ui.Closed) return;
            try { await ui.Window.Dialogs.ShowWarningAsync(ui.Window, ex.Message, Caption); }
            catch (Exception inner) when (inner is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("update: " + inner.Message); }
        }
        finally
        {
            if (!ui.Closed) ui.Link.IsEnabled = true;
        }
    }

    /// <summary>The browser on the release page; when it does not open, the address is shown instead (WinForms AppUpdates.OpenPage).</summary>
    static async Task LaunchPage(TopLevel top, Uri uri)
    {
        var ok = false;
        try { ok = await top.Launcher.LaunchUriAsync(uri); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("update: could not open " + uri + ": " + ex.Message); }
        if (ok || top is not Window w) return;
        try { await new Shell.Dialogs().ShowInfoAsync(w, "Could not open the web browser. The page is:\n\n" + uri.AbsoluteUri, Caption); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("update: " + ex.Message); }
    }
}
