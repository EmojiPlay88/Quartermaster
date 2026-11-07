using System.Globalization;
using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// The first window: the module list with Add/Remove, the module's rules pack, the (not yet available) online lobby, and Play offline.
/// <see cref="Result"/> is what to play once the dialog closed with OK; closing the window otherwise ends the app.
/// </summary>
sealed class LauncherForm : Form
{
    public const string OnlineTip = "Online play arrives with the online update.";
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
    readonly Button _add = new() { Text = "Add module…", AutoSize = true, MinimumSize = new Size(90, 26) };
    readonly Button _remove = new() { Text = "Remove", AutoSize = true, MinimumSize = new Size(75, 26) };
    readonly Button _online = new() { Text = "Online lobby ▶", AutoSize = true, MinimumSize = new Size(110, 26), Enabled = false };
    readonly Button _play = new() { Text = "Play offline ▶", AutoSize = true, MinimumSize = new Size(110, 26) };
    readonly Label _progress = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };
    readonly ToolTip _tip = new();
    readonly Panel _playHost = new() { AutoSize = true, Margin = new Padding(3), Padding = Padding.Empty };
    readonly CancellationTokenSource _cts = new();
    Task _refresh = Task.CompletedTask;
    bool _closing;

    public LaunchRequest? Result { get; private set; }

    /// <summary>The refresh started when the window opened (screenshots wait on it).</summary>
    public Task RefreshTask => _refresh;

    /// <summary>The Last played cell of the first row (smoke checks).</summary>
    public string? FirstLastPlayedText => _list.Items.Count > 0 ? _list.Items[0].SubItems[3].Text : null;

    public bool OnlineEnabled => _online.Enabled;

    public LauncherForm(SettingsStore store, string layoutRoot)
    {
        _store = store;
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

        // Disabled buttons get no mouse messages, so the tooltip hangs on a host panel around the button as well.
        var onlineHost = new Panel { AutoSize = true, Margin = new Padding(3), Padding = Padding.Empty };
        onlineHost.Controls.Add(_online);
        _tip.SetToolTip(onlineHost, OnlineTip);
        _tip.SetToolTip(_online, OnlineTip);

        var rulesRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(8, 6, 8, 0) };
        rulesRow.Controls.Add(_rules);
        rulesRow.Controls.Add(_change);
        rulesRow.Controls.Add(_progress);

        var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false };
        left.Controls.Add(_add);
        left.Controls.Add(_remove);
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
        right.Controls.Add(onlineHost);
        _playHost.Controls.Add(_play);
        right.Controls.Add(_playHost);
        var buttons = new Panel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(8, 6, 8, 6) };
        buttons.Controls.Add(right);
        buttons.Controls.Add(left);

        var bottom = new Panel { Dock = DockStyle.Bottom, AutoSize = true };
        bottom.Controls.Add(buttons);
        bottom.Controls.Add(rulesRow);
        buttons.BringToFront();
        rulesRow.SendToBack();
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 8, 8, 0) };
        listHost.Controls.Add(_list);
        Controls.Add(listHost);
        Controls.Add(bottom);
        AcceptButton = _play;

        _add.Click += (_, _) => Guarded(AddModules);
        _remove.Click += (_, _) => Guarded(RemoveSelected);
        _play.Click += (_, _) => Guarded(PlayOffline);
        _change.LinkClicked += (_, _) => Guarded(ChangeRules);
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
        _change.Enabled = row is { Missing: false };
        if (row == null) _rules.Text = "Rules pack: (none)";
        else
        {
            var pack = _library.RulesPackFor(row.Entry);
            _rules.Text = "Rules pack: " + (string.IsNullOrEmpty(pack) ? "(none)" : SafeFileName(pack.TrimEnd('\\', '/')));
        }
        var hint = row is { Missing: true } ? "This module's file was not found. Remove it or add it again." : "";
        _tip.SetToolTip(_playHost, hint);
        _tip.SetToolTip(_play, hint);
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
        if (_refresh.IsCompleted) StartRefresh(); // hash what was just added
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
