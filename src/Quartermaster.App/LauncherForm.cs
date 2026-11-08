using System.Globalization;
using Quartermaster.Render;
using Quartermaster.Render.Update;

namespace Quartermaster.App;

/// <summary>
/// The first window: the module list with Add/Remove, the module's rules pack, the Online lobby and Play offline.
/// <see cref="Result"/> is what to play once the dialog closed with OK; closing the window otherwise ends the app.
/// </summary>
sealed class LauncherForm : Form
{
    public const string NotFound = "(file not found)";

    readonly SettingsStore _store;
    readonly ModuleLibrary _library;
    readonly RecentGames _recent;
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, GridLines = false,
    };
    readonly Label _rules = new() { AutoSize = true, Text = "Rules pack: (none)", Anchor = AnchorStyles.Left };
    readonly LinkLabel _change = new() { AutoSize = true, Text = "change…", Anchor = AnchorStyles.Left };
    readonly LinkLabel _none = new() { AutoSize = true, Text = "use (none)", Anchor = AnchorStyles.Left, Visible = false };
    readonly Button _add = new() { Text = "Add module…", AutoSize = true, MinimumSize = new Size(90, 26) };
    readonly Button _remove = new() { Text = "Remove", AutoSize = true, MinimumSize = new Size(75, 26) };
    readonly Button _online = new() { Text = "Online lobby ▶", AutoSize = true, MinimumSize = new Size(110, 26) };
    readonly Panel _onlineHost = new() { AutoSize = true, Margin = new Padding(3), Padding = Padding.Empty };
    readonly Button _play = new() { Text = "Play offline ▶", AutoSize = true, MinimumSize = new Size(110, 26) };
    readonly Label _progress = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };
    readonly ToolTip _tip = new();
    readonly Panel _playHost = new() { AutoSize = true, Margin = new Padding(3), Padding = Padding.Empty };
    readonly CancellationTokenSource _cts = new();
    readonly string? _justPlayed;
    readonly UpdateBar _noticeBar = new() { Name = "UpdateNotice" };
    readonly UpdateBar _updateBar = new() { Name = "UpdateBar" };
    readonly Label _version = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 3, 6, 3), Text = "Quartermaster " + AppVersion.Text };
    readonly LinkLabel _checkLink = new() { AutoSize = true, Text = "Check for updates", Margin = new Padding(3, 3, 3, 3) };
    readonly bool _checkUpdates;
    Task _updateCheck = Task.CompletedTask;
    Task _refresh = Task.CompletedTask;
    bool _closing;
    bool _addedDuringRefresh; // set when a module is added while a refresh runs: one more refresh hashes it

    public LaunchRequest? Result { get; private set; }

    /// <summary>The refresh started when the window opened (screenshots wait on it).</summary>
    public Task RefreshTask => _refresh;

    /// <summary>The Last played cell of the first row (smoke checks).</summary>
    public string? FirstLastPlayedText => _list.Items.Count > 0 ? _list.Items[0].SubItems[3].Text : null;

    public bool OnlineEnabled => _online.Enabled;

    /// <summary>The update check started when the window opened (screenshots wait on it).</summary>
    public Task UpdateCheckTask => _updateCheck;

    /// <summary>The update bar's text, or null while it is hidden (smoke checks).</summary>
    public string? UpdateBarText => _updateBar.Visible ? _updateBar.Message : null;

    public IReadOnlyList<string> UpdateBarActions => _updateBar.Actions;

    /// <param name="justPlayed">After Back to launcher: the module just played, selected regardless of "Select the last module in the launcher".</param>
    /// <param name="checkForUpdates">False for smokes and screenshots that must never reach the network: no update check and no update bar.</param>
    public LauncherForm(SettingsStore store, string layoutRoot, string? justPlayed = null, bool checkForUpdates = true)
    {
        _store = store;
        _checkUpdates = checkForUpdates;
        try { _justPlayed = justPlayed == null ? null : Path.GetFullPath(justPlayed); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { _justPlayed = justPlayed; }
        _library = new ModuleLibrary(store);
        _recent = new RecentGames(store, layoutRoot);
        Text = "Quartermaster";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 400);
        MinimumSize = new Size(560, 320);
        AutoScaleMode = AutoScaleMode.Font;
        ShowIcon = false;

        _list.Columns.Add("Module", 200);
        _list.Columns.Add("Version", 70);
        _list.Columns.Add("File", 240);
        _list.Columns.Add("Last played", 120);

        // Disabled buttons get no mouse messages, so their tooltips hang on a host panel around the button as well.
        _onlineHost.Controls.Add(_online);

        var rulesRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(8, 6, 8, 0) };
        rulesRow.Controls.Add(_rules);
        rulesRow.Controls.Add(_change);
        rulesRow.Controls.Add(_none);
        rulesRow.Controls.Add(_progress);

        var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false };
        left.Controls.Add(_add);
        left.Controls.Add(_remove);
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
        right.Controls.Add(_onlineHost);
        _playHost.Controls.Add(_play);
        right.Controls.Add(_playHost);
        var buttons = new Panel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(8, 6, 8, 6) };
        buttons.Controls.Add(right);
        buttons.Controls.Add(left);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(8, 0, 8, 4) };
        footer.Controls.Add(_version);
        footer.Controls.Add(_checkLink);

        var bottom = new Panel { Dock = DockStyle.Bottom, AutoSize = true };
        bottom.Controls.Add(buttons);
        bottom.Controls.Add(rulesRow);
        bottom.Controls.Add(footer);
        buttons.BringToFront();
        footer.BringToFront();
        rulesRow.SendToBack();
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 8, 8, 0) };
        listHost.Controls.Add(_list);
        Controls.Add(listHost);
        Controls.Add(bottom);
        Controls.Add(_updateBar);
        Controls.Add(_noticeBar); // docked first: the topmost strip
        AcceptButton = _play;
        _checkLink.Visible = checkForUpdates;
        _checkLink.LinkClicked += (_, _) => Guarded(() => StartUpdateCheck(manual: true));

        _add.Click += (_, _) => Guarded(AddModules);
        _remove.Click += (_, _) => Guarded(RemoveSelected);
        _play.Click += (_, _) => Guarded(PlayOffline);
        _online.Click += (_, _) => Guarded(OpenLobby);
        _change.LinkClicked += (_, _) => Guarded(ChangeRules);
        _none.LinkClicked += (_, _) => Guarded(ClearRules);
        _tip.SetToolTip(_none, "Play this module without a rules pack.");
        _list.SelectedIndexChanged += (_, _) => Guarded(UpdateButtons);
        _list.DoubleClick += (_, _) => Guarded(PlayOffline);
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) { e.Handled = true; Guarded(RemoveSelected); }
        };
        Load += (_, _) => Guarded(OnLoaded);
        FormClosing += (_, _) => { _closing = true; try { _cts.Cancel(); } catch (ObjectDisposedException) { } };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _closing = true; try { _cts.Cancel(); } catch (ObjectDisposedException) { } _cts.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>Runs a handler so that nothing it throws reaches the message loop.</summary>
    void Guarded(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("launcher: " + ex.Message);
            if (!Program.Smoke && !IsDisposed) MessageBox.Show(this, ex.Message, "Quartermaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    ModuleRow? Selected => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as ModuleRow : null;

    void OnLoaded()
    {
        Fill(preselectLast: true);
        StartRefresh();
        StartUpdateUi();
    }

    // ---------- updates ----------

    /// <summary>On show: the one-off notice after an update, then the update already found this run, or the background check when due.</summary>
    void StartUpdateUi()
    {
        if (AppUpdates.Notice is { } n)
        {
            AppUpdates.Notice = null;
            var actions = new List<(string, Action)>();
            if (n.Link is { } link) actions.Add((n.LinkText ?? "Release page", () => Guarded(() => AppUpdates.OpenPage(this, link))));
            actions.Add(("Close", () => _noticeBar.Clear()));
            _noticeBar.Show(n.Text, actions.ToArray());
        }
        if (!_checkUpdates) return;
        if (AppUpdates.CheckOnNextLauncher)
        {
            AppUpdates.CheckOnNextLauncher = false;
            StartUpdateCheck(manual: true);
            return;
        }
        if (AppUpdates.Offered is { } offered)
        {
            if (!AppUpdates.Later && _store.Current.SkippedVersion != AppVersion.Format(offered.Version)) ShowOffer(offered);
            return;
        }
        if (AppUpdates.CheckedThisRun || !_store.Current.CheckUpdatesOnStart || !UpdateChecker.Due(_store.Current.LastUpdateCheckUtc, DateTime.UtcNow)) return;
        StartUpdateCheck(manual: false);
    }

    /// <summary>Checks on a worker; the automatic check is silent unless an update is found (a failure goes to the app log only).</summary>
    void StartUpdateCheck(bool manual)
    {
        if (_closing || !_updateCheck.IsCompleted) return;
        _checkLink.Enabled = false;
        _updateCheck = CheckAsync(manual, _store.Current.SkippedVersion);
    }

    async Task CheckAsync(bool manual, string? skipped)
    {
        CheckResult r;
        try { r = await Task.Run(() => UpdateChecker.Check(AppUpdates.Feed, AppVersion.Current, skipped, _cts.Token)); }
        catch (OperationCanceledException) { return; }
        AppUpdates.CheckedThisRun = true;
        if (r.Outcome != CheckOutcome.Failed)
        {
            try { _store.Update(s => s with { LastUpdateCheckUtc = DateTime.UtcNow }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.AppLog("update: could not save the check time: " + ex.Message); }
        }
        if (r.Outcome == CheckOutcome.Available) AppUpdates.Offered = r.Info;
        if (r.Outcome == CheckOutcome.Failed) Program.AppLog($"update: {(manual ? "" : "automatic ")}check failed: {r.Error}");
        if (_closing || IsDisposed) return;
        Guarded(() =>
        {
            _checkLink.Enabled = true;
            switch (r.Outcome)
            {
                case CheckOutcome.Available:
                    if (manual) AppUpdates.Later = false;
                    if (manual || !AppUpdates.Later) ShowOffer(r.Info!);
                    break;
                case CheckOutcome.Skipped when manual:
                    var v = AppVersion.Format(r.Info!.Version);
                    if (MessageBox.Show(this, $"Quartermaster {v} is available, but you chose to skip it. Offer it again?", "Quartermaster",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) break;
                    _store.Update(s => s with { SkippedVersion = null });
                    AppUpdates.Offered = r.Info;
                    AppUpdates.Later = false;
                    ShowOffer(r.Info!);
                    break;
                case CheckOutcome.UpToDate when manual:
                    _updateBar.Clear();
                    MessageBox.Show(this, $"Quartermaster {AppVersion.Text} is up to date.", "Quartermaster", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    break;
                case CheckOutcome.Failed when manual:
                    MessageBox.Show(this, "Could not check for updates.\n\n" + r.Error, "Quartermaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
            }
        });
    }

    /// <summary>The yellow bar: "Quartermaster 0.2.0 is available." What's new · Update now · Later · Skip this version.</summary>
    void ShowOffer(UpdateInfo info)
    {
        var v = AppVersion.Format(info.Version);
        _updateBar.Show($"Quartermaster {v} is available.",
            ("What's new", () => Guarded(() => { using var dlg = new WhatsNewDialog(info); dlg.ShowDialog(this); })),
            ("Update now", () => Guarded(() => AppUpdates.UpdateNow(this, info))),
            ("Later", () => { AppUpdates.Later = true; _updateBar.Clear(); }),
            ("Skip this version", () => Guarded(() =>
            {
                _store.Update(s => s with { SkippedVersion = v });
                AppUpdates.Offered = null;
                _updateBar.Clear();
            })));
        // The launcher only exists while no game is open, but never offer to update over one.
        _updateBar.SetActionEnabled("Update now", !AppUpdates.GameOpen);
    }

    void StartRefresh()
    {
        if (_closing) return;
        var total = _library.Rows.Count;
        if (total == 0) return;
        var done = 0;
        _progress.Text = "Checking modules…";
        var progress = new Progress<ModuleRow>(_ =>
        {
            if (_closing || IsDisposed) return;
            done++;
            _progress.Text = $"Checking modules… {done} of {total}";
        });
        _refresh = RefreshAsyncSafe(progress);
    }

    async Task RefreshAsyncSafe(IProgress<ModuleRow> progress)
    {
        try { await _library.RefreshAsync(progress, _cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("launcher: refresh failed: " + ex.Message); }
        if (_closing || IsDisposed) return;
        Guarded(() =>
        {
            _progress.Text = "";
            Fill(preselectLast: false);
            // Only re-run for modules added since this refresh started (an unhashable file never loops).
            if (_addedDuringRefresh)
            {
                _addedDuringRefresh = false;
                StartRefresh();
            }
        });
    }

    /// <summary>Rebuilds the list from the library, keeping the selection (or choosing the last module when asked).</summary>
    void Fill(bool preselectLast)
    {
        var keep = Selected?.Entry.Path;
        var rows = _library.Rows;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var r in rows)
            {
                var e = r.Entry;
                var file = SafeFileName(e.Path);
                var item = new ListViewItem(new[]
                {
                    string.IsNullOrWhiteSpace(e.Title) ? file : e.Title,
                    e.Version,
                    r.Missing ? $"{file} {NotFound}" : file,
                    e.LastPlayed is { } lp ? lp.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) : "",
                }) { Tag = r };
                if (r.Missing) item.ForeColor = SystemColors.GrayText;
                _list.Items.Add(item);
            }
            var want = keep;
            if (want == null && preselectLast && _justPlayed != null)
                want = rows.FirstOrDefault(r => string.Equals(r.Entry.Path, _justPlayed, StringComparison.OrdinalIgnoreCase))?.Entry.Path;
            if (want == null && preselectLast && _store.Current.ReopenLastModule)
                want = rows.FirstOrDefault(r => string.Equals(r.Entry.Path, _store.Current.LastModulePath, StringComparison.OrdinalIgnoreCase))?.Entry.Path
                    ?? rows.FirstOrDefault(r => !r.Missing)?.Entry.Path;
            SelectPath(want);
        }
        finally { _list.EndUpdate(); }
        UpdateButtons();
    }

    void SelectPath(string? path)
    {
        if (path == null) return;
        foreach (ListViewItem it in _list.Items)
        {
            if (it.Tag is ModuleRow r && string.Equals(r.Entry.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                it.Selected = true;
                it.Focused = true;
                it.EnsureVisible();
                return;
            }
        }
    }

    static string SafeFileName(string path)
    {
        try { return Path.GetFileName(path); } catch (ArgumentException) { return path; }
    }

    void UpdateButtons()
    {
        var row = Selected;
        _remove.Enabled = row != null;
        _play.Enabled = row != null && !row.Missing;
        _online.Enabled = row != null && !row.Missing;
        _change.Enabled = row is { Missing: false };
        string? pack = null;
        if (row == null) _rules.Text = "Rules pack: (none)";
        else
        {
            pack = _library.RulesPackFor(row.Entry);
            _rules.Text = "Rules pack: " + (string.IsNullOrEmpty(pack) ? "(none)" : SafeFileName(pack.TrimEnd('\\', '/')));
        }
        _none.Visible = row is { Missing: false } && !string.IsNullOrEmpty(pack);
        var hint = row is { Missing: true } ? "This module's file was not found. Remove it or add it again." : "";
        _tip.SetToolTip(_playHost, hint);
        _tip.SetToolTip(_play, hint);
        var onlineHint = row == null ? "Select a module first." : hint.Length > 0 ? hint : "Join or host a live online game of this module.";
        _tip.SetToolTip(_onlineHost, onlineHint);
        _tip.SetToolTip(_online, onlineHint);
    }

    /// <summary>Online lobby ▶: the selected module's lobbies; a join or a new lobby closes the launcher with that request.</summary>
    void OpenLobby()
    {
        if (Selected is not { } row || row.Missing) return;
        if (row.Entry.Sha256 is not { Length: > 0 })
        {
            MessageBox.Show(this, "This module is still being checked; try again in a moment.", "Quartermaster", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new OnlineLobbyForm(row, _library, _store);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result is { } req)
        {
            Result = req;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    void AddModules()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "VASSAL modules (*.vmod)|*.vmod|All files (*.*)|*.*", Title = "Add module", Multiselect = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string? last = null;
        foreach (var f in dlg.FileNames)
        {
            try { _library.Add(f); last = Path.GetFullPath(f); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog($"launcher: could not add {f}: {ex.Message}"); }
        }
        Fill(preselectLast: false);
        SelectPath(last);
        if (last == null) return;
        if (_refresh.IsCompleted) StartRefresh(); // hash what was just added
        else _addedDuringRefresh = true;          // the running refresh took its snapshot before: run again when it ends
    }

    void RemoveSelected()
    {
        if (Selected is not { } row) return;
        _library.Remove(row.Entry.Path);
        Fill(preselectLast: false);
    }

    void ChangeRules()
    {
        if (Selected is not { } row) return;
        if (row.Entry.Sha256 is not { Length: > 0 })
        {
            MessageBox.Show(this, "This module is still being checked; try again in a moment.", "Quartermaster", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new OpenFileDialog { Filter = "Rules packs (*.qrules)|*.qrules|All files (*.*)|*.*", Title = "Rules pack for " + row.Entry.Title };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _library.SetRulesPack(row.Entry, dlg.FileName);
        UpdateButtons();
    }

    /// <summary>The "use (none)" link: forget the module's rules pack so it plays without one.</summary>
    void ClearRules()
    {
        if (Selected is not { } row) return;
        _library.SetRulesPack(row.Entry, null);
        UpdateButtons();
    }

    void PlayOffline()
    {
        if (Selected is not { } row) return;
        if (row.Missing)
        {
            MessageBox.Show(this, "This module's file was not found. Remove it from the list or add it again.", "Quartermaster", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (row.Entry.Sha256 is not { Length: > 0 } && !_refresh.IsCompleted)
        {
            MessageBox.Show(this, "This module is still being checked; try again in a moment.", "Quartermaster", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new PlayOfflineForm(row, _library, _recent, _store);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result is { } req)
        {
            Result = req;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
