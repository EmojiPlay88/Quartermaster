using System.Globalization;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>
/// "Play offline" for one module: Continue (newest autosave), recent saves, New game… and Open saved game…. <see cref="Result"/> is the
/// launch request when the dialog closed with OK; Back returns to the launcher.
/// </summary>
sealed class PlayOfflineForm : Form
{
    readonly ModuleRow _row;
    readonly ModuleLibrary _library;
    readonly SettingsStore _store;
    readonly IReadOnlyList<RecentSave> _saves;
    readonly ContinueInfo? _continue;
    readonly Button _continueBtn = new() { Text = "Continue", AutoSize = true, MinimumSize = new Size(100, 26) };
    readonly Label _continueLine = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    readonly ListView _recent = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    readonly Button _new = new() { Text = "New game…", AutoSize = true, MinimumSize = new Size(100, 26) };
    readonly Button _open = new() { Text = "Open saved game…", AutoSize = true, MinimumSize = new Size(120, 26) };
    readonly Button _openSel = new() { Text = "Open selected", AutoSize = true, MinimumSize = new Size(100, 26), Enabled = false };
    readonly Button _back = new() { Text = "Back", DialogResult = DialogResult.Cancel, MinimumSize = new Size(75, 26) };

    public LaunchRequest? Result { get; private set; }

    public PlayOfflineForm(ModuleRow row, ModuleLibrary library, RecentGames recent, SettingsStore store)
    {
        _row = row;
        _library = library;
        _store = store;
        var sha = row.Entry.Sha256;
        _saves = Safe(() => recent.For(sha), Array.Empty<RecentSave>());
        _continue = string.IsNullOrEmpty(sha) ? null : Safe(() => recent.Continue(sha), null);

        Text = "Play offline – " + (string.IsNullOrWhiteSpace(row.Entry.Title) ? Path.GetFileName(row.Entry.Path) : row.Entry.Title);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(520, 360);
        CancelButton = _back;

        _continueLine.Text = ContinueText(_continue);
        _continueBtn.Enabled = _continue != null;
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(8, 8, 8, 4) };
        top.Controls.Add(_continueBtn);
        top.Controls.Add(_continueLine);

        _recent.Columns.Add("Recent games", 330);
        _recent.Columns.Add("Saved", 140);
        foreach (var s in _saves)
        {
            var name = Path.GetFileName(s.Path);
            var item = new ListViewItem(new[]
            {
                s.Missing ? $"{name} {LauncherForm.NotFound}" : name,
                s.Missing ? "" : s.LastWrite.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
            }) { Tag = s };
            if (s.Missing) item.ForeColor = SystemColors.GrayText;
            _recent.Items.Add(item);
        }
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 4) };
        listHost.Controls.Add(_recent);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8), FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.Add(_new);
        buttons.Controls.Add(_open);
        buttons.Controls.Add(_openSel);
        buttons.Controls.Add(_back);

        Controls.Add(listHost);
        Controls.Add(top);
        Controls.Add(buttons);
        top.SendToBack();

        _continueBtn.Click += (_, _) => Guarded(() => Finish(LaunchKind.Continue, null, _continue?.Path));
        _new.Click += async (_, _) => await GuardedAsync(NewGame);
        _open.Click += (_, _) => Guarded(OpenDialog);
        _openSel.Click += (_, _) => Guarded(OpenSelected);
        _recent.DoubleClick += (_, _) => Guarded(OpenSelected);
        _recent.SelectedIndexChanged += (_, _) => Guarded(() => _openSel.Enabled = SelectedSave is { Missing: false });
        _recent.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = true; Guarded(OpenSelected); }
        };
    }

    static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch (Exception ex) when (ex is not OutOfMemoryException) { return fallback; }
    }

    public static string ContinueText(ContinueInfo? c)
    {
        if (c == null) return "Continue: (no autosave yet)";
        var when = c.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        var detail = c.Turn > 0 ? $"turn {c.Turn}" : "";
        if (!string.IsNullOrEmpty(c.Phase)) detail = detail.Length > 0 ? detail + ", " + c.Phase : c.Phase;
        return detail.Length > 0 ? $"Continue: autosave {when} ({detail})" : $"Continue: autosave {when}";
    }

    void Guarded(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("launcher: " + ex.Message);
            if (!Program.Smoke && !IsDisposed) MessageBox.Show(this, ex.Message, "Quartermaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    async Task GuardedAsync(Func<Task> a)
    {
        try { await a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("launcher: " + ex.Message);
            if (!Program.Smoke && !IsDisposed) MessageBox.Show(this, ex.Message, "Quartermaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    RecentSave? SelectedSave => _recent.SelectedItems.Count == 1 ? _recent.SelectedItems[0].Tag as RecentSave : null;

    void Finish(LaunchKind kind, NewGameRequest? ng, string? savePath)
    {
        var rules = Safe(() => _library.RulesPackFor(_row.Entry), null);
        _finishing = true;
        Result = new LaunchRequest(_row.Entry.Path, string.IsNullOrEmpty(rules) ? null : rules, kind, ng, savePath);
        DialogResult = DialogResult.OK;
        Close();
    }

    void OpenSelected()
    {
        if (SelectedSave is not { } s) return;
        if (s.Missing)
        {
            MessageBox.Show(this, "That saved game's file was not found.", "Quartermaster", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Finish(LaunchKind.OpenSave, null, s.Path);
    }

    void OpenDialog()
    {
        using var dlg = new OpenFileDialog { Filter = "Quartermaster saves (*.qsave)|*.qsave|All files (*.*)|*.*", Title = "Open saved game" };
        var newest = _saves.FirstOrDefault(s => !s.Missing);
        if (newest != null)
        {
            var dir = Safe(() => Path.GetDirectoryName(newest.Path), null);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        }
        if (dlg.ShowDialog(this) == DialogResult.OK) Finish(LaunchKind.OpenSave, null, dlg.FileName);
    }

    /// <summary>Disables the action buttons while a module loads or the setup dialog is open (no second click can start another run).</summary>
    bool _busy, _finishing;

    /// <summary>Closing is refused while a module loads or the setup dialog is open (the choice would be lost).</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy && !_finishing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        if (IsDisposed) return;
        _new.Enabled = _open.Enabled = _back.Enabled = !busy;
        _continueBtn.Enabled = !busy && _continue != null;
        _openSel.Enabled = !busy && SelectedSave is { Missing: false };
    }

    async Task NewGame()
    {
        SetBusy(true);
        try { await NewGameCore(); }
        finally { SetBusy(false); }
    }

    async Task NewGameCore()
    {
        var rules = Safe(() => _library.RulesPackFor(_row.Entry), null);
        ModuleSession? session = null;
        var prev = Cursor.Current;
        UseWaitCursor = true;
        Cursor.Current = Cursors.WaitCursor;
        try
        {
            var path = _row.Entry.Path;
            session = await Task.Run(() =>
            {
                var s = ModuleSession.Open(path);
                try
                {
                    if (!string.IsNullOrEmpty(rules) && !string.Equals(s.RulesPath, rules, StringComparison.OrdinalIgnoreCase)
                        && (File.Exists(rules) || Directory.Exists(rules)))
                    {
                        try { s.OpenRules(rules); } catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("launcher: rules pack not loaded: " + ex.Message); }
                    }
                    return s;
                }
                catch { s.Dispose(); throw; }
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UseWaitCursor = false;
            MessageBox.Show(this, $"Could not open '{_row.Entry.Path}':\n{ex.Message}", "Quartermaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        finally
        {
            UseWaitCursor = false;
            Cursor.Current = prev;
        }
        using (session!)
        {
            if (IsDisposed || !Visible) return;
            using var dlg = new SetupChooserDialog(session!.Model, GameMode.Offline, _store.Current.LastDifficulty, session.Rules, preselectNew: false);
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Request is not { } req) return;
            _store.Update(cur => cur with { LastDifficulty = req.Difficulty });
            Finish(LaunchKind.NewGame, req, null);
        }
    }
}
