using Quartermaster.Core;
using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// The game log: one row per <see cref="LogEntry"/>, filterable by kind and text, copyable and exportable. The list is virtual, refreshes are
/// debounced 150 ms, and the view follows new rows unless the user has scrolled up. Hosted docked at the bottom of the main window or in a
/// floating <see cref="LogFloatForm"/>; the same instance moves between them.
/// </summary>
sealed class LogPanel : UserControl
{
    readonly ModuleSession _s;
    readonly Action<string> _centre;
    readonly ToolStrip _bar = new() { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
    readonly Dictionary<string, ToolStripButton> _kindButtons = new();
    readonly ToolStripTextBox _search = new() { AutoSize = false, Width = 140 };
    readonly ToolStripButton _popButton = new("Pop out") { Alignment = ToolStripItemAlignment.Right };
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, VirtualMode = true, FullRowSelect = true, HideSelection = false,
        MultiSelect = true, GridLines = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 150 };
    GameSession? _attached;
    IReadOnlyList<LogEntry> _rows = Array.Empty<LogEntry>();
    bool _showMoves = true;
    string _timeFormat = "HH:mm:ss";

    /// <summary>Smoke: handle of the search box's edit control (a main-window-hosted text box).</summary>
    public IntPtr SearchHandleForSmoke => _search.Control.Handle;

    public LogPanel(ModuleSession s, Action<string> centreOnPiece)
    {
        _s = s;
        _centre = centreOnPiece;
        _list.Columns.Add("Time", 64);
        _list.Columns.Add("Kind", 60);
        _list.Columns.Add("Turn · Phase", 100);
        _list.Columns.Add("Text", 600);

        foreach (var kind in LogFilter.AllKinds)
        {
            var b = new ToolStripButton(LogFilter.KindLabel(kind)) { CheckOnClick = true, Checked = true, DisplayStyle = ToolStripItemDisplayStyle.Text };
            b.CheckedChanged += (_, _) => Refresh_();
            _kindButtons[kind] = b;
            _bar.Items.Add(b);
        }
        _bar.Items.Add(new ToolStripSeparator());
        _bar.Items.Add(new ToolStripLabel("Search:"));
        _search.TextChanged += (_, _) => Refresh_();
        _bar.Items.Add(_search);
        _bar.Items.Add(new ToolStripSeparator());
        _bar.Items.Add(new ToolStripButton("Copy", null, (_, _) => Guard(Copy)));
        _bar.Items.Add(new ToolStripButton("Export log…", null, (_, _) => Guard(Export)));
        _popButton.Click += (_, _) => PopToggleRequested?.Invoke();
        _bar.Items.Add(_popButton);

        _list.RetrieveVirtualItem += OnRetrieve;
        _list.DoubleClick += (_, _) => Guard(CentreSelected);
        _list.Resize += (_, _) => FitTextColumn();
        _debounce.Tick += (_, _) => { _debounce.Stop(); Guard(Reload); };

        Controls.Add(_list);
        Controls.Add(_bar);
        Name = "LogPanel";
        _s.GameReplaced += OnGameReplaced;
        _s.ViewChanged += Refresh_;   // debounced; also a new viewer
        Attach();
        Reload();
    }

    /// <summary>Settings: whether move/place/delete rows are listed (display only; the log itself is untouched) and the time column format.</summary>
    public void ApplySettings(Settings s)
    {
        _showMoves = s.LogShowMoves;
        _timeFormat = s.LogTimeFormat;
        Reload();
    }

    /// <summary>Raised by the Pop out / Dock button; the host moves the panel.</summary>
    public event Action? PopToggleRequested;

    public void SetFloating(bool floating) => _popButton.Text = floating ? "Dock" : "Pop out";

    ChatBox? _chat;

    /// <summary>The chat line under the log while the game is online; null otherwise.</summary>
    public ChatBox? Chat => _chat;

    /// <summary>Shows the chat line (online) with what sends a line, or removes it (null).</summary>
    public void SetChat(Action<string>? send)
    {
        if (_chat != null)
        {
            Controls.Remove(_chat);
            _chat.Dispose();
            _chat = null;
        }
        if (send == null) return;
        _chat = new ChatBox(send) { Dock = DockStyle.Bottom };
        Controls.Add(_chat);
        _chat.SendToBack();   // docks first: the list fills what is left above it
        _bar.SendToBack();
    }
    public int RowCountForSmoke => _list.VirtualListSize;

    // ---------- game wiring ----------

    void Attach()
    {
        if (_attached != null) _attached.Changed -= OnGameChanged;
        if (_attached != null) _attached.Rewound -= OnRewound;
        _attached = _s.Game;
        if (_attached != null) _attached.Changed += OnGameChanged;
        if (_attached != null) _attached.Rewound += OnRewound;
    }

    void OnGameReplaced() { Attach(); Refresh_(); }
    void OnGameChanged(GameCommand _) => Refresh_();
    void OnRewound() => Refresh_();

    /// <summary>Schedules a debounced reload on the UI thread (events may arrive from any thread).</summary>
    void Refresh_()
    {
        try
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                if (IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) { _debounce.Stop(); _debounce.Start(); } });
            }
            else { _debounce.Stop(); _debounce.Start(); }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { /* shutting down */ }
    }

    LogFilterSpec Spec() =>
        new(_kindButtons.Where(k => k.Value.Checked).Select(k => k.Key).ToHashSet(), _search.Text ?? "");

    /// <summary>Recomputes the visible rows now (also used by smoke so the screenshot does not wait for the debounce).</summary>
    public void Reload()
    {
        var log = _s.View?.Log ?? (IReadOnlyList<LogEntry>)Array.Empty<LogEntry>();   // the player's view: other sides' private lines redacted
        var follow = IsAtBottom();
        // Selection is by row index, which changes when the rows do: re-select by Seq so Copy / double-click act on what is highlighted.
        var selected = _list.SelectedIndices.Cast<int>().Where(i => i < _rows.Count).Select(i => _rows[i].Seq).ToHashSet();
        _rows = LogFilter.Apply(log, Spec());
        if (!_showMoves) _rows = _rows.Where(r => LogFilter.DisplayKind(r.Kind) is not (LogKind.Move or LogKind.Place or LogKind.Delete)).ToList();
        _list.SelectedIndices.Clear();
        _list.VirtualListSize = _rows.Count;
        if (selected.Count > 0)
            for (var i = 0; i < _rows.Count; i++) if (selected.Contains(_rows[i].Seq)) _list.SelectedIndices.Add(i);
        _list.Invalidate();
        if (follow && _rows.Count > 0) _list.EnsureVisible(_rows.Count - 1);
    }

    /// <summary>True when the last row is in view (or the list is empty): new rows then scroll into view; a user who scrolled up is left alone.</summary>
    bool IsAtBottom()
    {
        var n = _list.VirtualListSize;
        if (n == 0 || !_list.IsHandleCreated) return true;
        return _list.GetItemRect(n - 1).Bottom <= _list.ClientSize.Height + 1;
    }

    void FitTextColumn()
    {
        if (_list.Columns.Count == 4)
            _list.Columns[3].Width = Math.Max(120, _list.ClientSize.Width - _list.Columns[0].Width - _list.Columns[1].Width - _list.Columns[2].Width - 2);
    }

    void OnRetrieve(object? s, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _rows.Count) { e.Item = new ListViewItem(new[] { "", "", "", "" }); return; }
        var r = _rows[e.ItemIndex];
        e.Item = new ListViewItem(new[] { LogFilter.Time(r, _timeFormat), LogFilter.DisplayKind(r.Kind), LogFilter.TurnPhase(r), r.Text ?? "" });
    }

    // ---------- actions ----------

    IReadOnlyList<LogEntry> SelectedOrAll()
    {
        var sel = _list.SelectedIndices.Cast<int>().Where(i => i < _rows.Count).OrderBy(i => i).Select(i => _rows[i]).ToList();
        return sel.Count > 0 ? sel : _rows;
    }

    void Copy()
    {
        var text = LogFilter.ToText(SelectedOrAll());
        if (text.Length > 0) Clipboard.SetText(text);
    }

    void Export()
    {
        using var dlg = new SaveFileDialog { Title = "Export log", Filter = "Text file (*.txt)|*.txt|HTML file (*.html)|*.html", FileName = "game-log" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var rows = _rows;
        File.WriteAllText(dlg.FileName, dlg.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? LogFilter.ToHtml(rows) : LogFilter.ToText(rows), System.Text.Encoding.UTF8);
    }

    void CentreSelected()
    {
        var i = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
        if (i < 0 || i >= _rows.Count || _rows[i].PieceId is not { Length: > 0 } id) return;
        _centre(id);
    }

    void Guard(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("log panel: " + ex.Message);
            _s.Log.Add(Quartermaster.Vassal.DiagnosticSeverity.Warning, "log", "The log panel could not complete an action: " + ex.Message);
        }
    }

    public void SaveScreenshot(string path)
    {
        Reload();
        using var bmp = new Bitmap(Math.Max(Width, 1), Math.Max(Height, 1));
        DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Program.AppLog("smoke: log screenshot saved to " + path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _s.GameReplaced -= OnGameReplaced;
            _s.ViewChanged -= Refresh_;
            if (_attached != null) _attached.Changed -= OnGameChanged;
            if (_attached != null) _attached.Rewound -= OnRewound;
            _debounce.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Top-level window for a popped-out <see cref="LogPanel"/>. Closing it, or "Dock to Main Window", returns the panel to the main window.</summary>
sealed class LogFloatForm : Form, WindowManager.ICustomDock
{
    readonly Action _dockBack;
    bool _closingForReal;

    public LogFloatForm(Action dockBack)
    {
        _dockBack = dockBack;
        Text = "Log";
        ShowIcon = false;
    }

    /// <summary>Closes the window without sending the panel home first (the host already moved it, or is shutting down).</summary>
    public void CloseForReal() { _closingForReal = true; Close(); }

    public void DockBack() => _dockBack();

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closingForReal && !e.Cancel && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; BeginInvoke(_dockBack); return; }
        base.OnFormClosing(e);
    }
}
