using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>
/// The phase panel: current phase name and instructions, the rule reference link, matching warnings (with Acknowledge for decision warnings),
/// "Decisions left" (checklist, pending runs, decision warnings) and the Next Phase button. Refreshes debounced on game / rules changes.
/// Hosted by <see cref="PhaseDock"/> (docked right, or floating); the same instance moves between them.
/// </summary>
sealed class PhasePanel : UserControl
{
    readonly ModuleSession _s;
    readonly PhaseController _c;
    readonly Action _next;
    readonly Action<string> _centre;
    readonly Action<string> _openRun;
    readonly Action<string, int> _openDoc;
    readonly PhaseWatcher _watch;

    readonly ToolTip _notesTip = new() { ShowAlways = true, UseAnimation = false, UseFading = false, AutoPopDelay = 30000 };
    readonly Label _title = new() { Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    readonly TextBox _instructions = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };
    readonly LinkLabel _ref = new() { Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Visible = false };
    readonly Label _warnCaption = new() { Text = "Warnings", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft };
    readonly TreeView _warnings = new() { Dock = DockStyle.Fill, HideSelection = false, ShowRootLines = true };
    readonly Button _ack = new() { Text = "Acknowledge", Dock = DockStyle.Fill, FlatStyle = FlatStyle.System, Enabled = false };
    readonly Label _decCaption = new() { Text = "Decisions left", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft };
    readonly CheckedListBox _checklist = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    readonly ListBox _open = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly Button _nextButton = new() { Text = "Next Phase ▶", Dock = DockStyle.Fill, FlatStyle = FlatStyle.System };
    readonly Label _count = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    readonly Button _pop = new() { Text = "Pop out", Dock = DockStyle.Fill, FlatStyle = FlatStyle.System };
    readonly Label _none = new() { Text = PhaseStrip.NoPhasesText, Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopLeft, Visible = false };

    PhaseView? _view;
    IReadOnlyList<Decision> _openRows = Array.Empty<Decision>();
    RuleRef? _refTarget;
    bool _updating;

    public PhasePanel(ModuleSession s, PhaseController c, Action next, Action<string> centre, Action<string> openRun, Action<string, int> openDoc)
    {
        _s = s; _c = c; _next = next; _centre = centre; _openRun = openRun; _openDoc = openDoc;
        Name = "PhasePanel";
        MinimumSize = new Size(180, 200);

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 11, Padding = new Padding(4) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(Control ctl, SizeType type, float size)
        {
            t.RowStyles.Add(new RowStyle(type, size));
            t.Controls.Add(ctl, 0, t.RowStyles.Count - 1);
        }
        _title.Font = new Font(Font, FontStyle.Bold);
        Row(_title, SizeType.Absolute, 22);
        Row(_instructions, SizeType.Percent, 30);
        Row(_ref, SizeType.Absolute, 20);
        Row(_warnCaption, SizeType.Absolute, 20);
        Row(_warnings, SizeType.Percent, 25);
        Row(_ack, SizeType.Absolute, 26);
        Row(_decCaption, SizeType.Absolute, 20);
        Row(_checklist, SizeType.Percent, 22);
        Row(_open, SizeType.Percent, 23);
        Row(_nextButton, SizeType.Absolute, 30);
        Row(_count, SizeType.Absolute, 20);
        t.RowCount = t.RowStyles.Count;

        var foot = new Panel { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(4, 0, 4, 2) };
        foot.Controls.Add(_pop);
        Controls.Add(t);
        Controls.Add(foot);
        Controls.Add(_none);
        _none.BringToFront();

        _pop.Click += (_, _) => PopToggleRequested?.Invoke();
        _nextButton.Click += (_, _) => Guard(_next);
        _ack.Click += (_, _) => Guard(AckSelected);
        _ref.LinkClicked += (_, _) => Guard(OpenRef);
        _checklist.ItemCheck += (_, e) => Guard(() => OnItemCheck(e));
        _warnings.AfterSelect += (_, _) => UpdateAckEnabled();
        _warnings.NodeMouseDoubleClick += (_, e) => Guard(() => { if (e.Node?.Tag is string id) _centre(id); });
        _open.DoubleClick += (_, _) => Guard(OpenSelected);
        _watch = new PhaseWatcher(this, _s, () => Reload());
        Reload();
    }

    /// <summary>Raised by the Pop out / Dock button; the host moves the panel.</summary>
    public event Action? PopToggleRequested;

    public void SetFloating(bool floating) => _pop.Text = floating ? "Dock" : "Pop out";

    public void Flush() => _watch.Flush();

    /// <summary>Highlights a checklist item (used when the Next Phase dialog jumps to it).</summary>
    public void SelectChecklist(int index)
    {
        if (index < 0 || index >= _checklist.Items.Count) return;
        _checklist.SelectedIndex = index;
        _checklist.Focus();
    }

    public string NextButtonText => _nextButton.Text;
    public string CountText => _count.Text;
    public Color CountColor => _count.ForeColor;
    public int WarningNodesForSmoke => _warnings.Nodes.Count;

    // ---------- rendering ----------

    public void Reload()
    {
        _updating = true;
        try
        {
            var v = _c.View();
            _view = v;
            var show = !v.NoPhases;
            _none.Visible = !show;
            foreach (Control ctl in Controls) if (ctl is TableLayoutPanel tl) tl.Visible = show;
            _nextButton.Enabled = show && (v.BeforeStart || v.NextName != null);
            if (!show)
            {
                _count.Text = "";
                _nextButton.Text = "Next Phase ▶";
                return;
            }
            _title.Text = v.BeforeStart ? "Before turn 1" : v.PlayingText is { } playing ? playing : v.Current is { } cur
                ? (v.SideName != null ? $"{v.SideName} player — {cur.Name}" : cur.Name) : "";
            _instructions.Text = (v.Current?.Instructions ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
            _notesTip.SetToolTip(_title, v.Current?.Notes ?? "");
            ShowRef(v);
            ShowWarnings(v);
            ShowDecisions(v);
            _nextButton.Text = v.BeforeStart ? "Start game ▶" : v.NextName != null ? "Next Phase ▶ " + v.NextName : "Next Phase ▶";
            var n = v.Decisions.Count;
            _count.Text = n == 1 ? "1 decision left" : $"{n} decisions left";
            _count.ForeColor = n > 0 ? Color.Red : SystemColors.ControlText;
            _count.Visible = !v.BeforeStart;
        }
        finally { _updating = false; }
    }

    void ShowRef(PhaseView v)
    {
        _refTarget = null;
        if (v.Current?.RuleRefId is { Length: > 0 } id && _s.Rules?.ById<RuleRef>(id) is { } r)
        {
            _refTarget = r;
            _ref.Text = r.Page > 0 ? $"Rule: {r.Label} (p. {r.Page})" : $"Rule: {r.Label}";
            _ref.Links.Clear();
            _ref.Links.Add(0, _ref.Text.Length);
            _ref.Visible = true;
        }
        else _ref.Visible = false;
    }

    void ShowWarnings(PhaseView v)
    {
        var open = _warnings.Nodes.Cast<TreeNode>().Where(n => n.IsExpanded && n.Tag is WarningHit).Select(n => ((WarningHit)n.Tag!).Index).ToHashSet();
        var selIndex = (_warnings.SelectedNode?.Tag as WarningHit)?.Index;
        _warnings.BeginUpdate();
        _warnings.Nodes.Clear();
        foreach (var w in v.Warnings)
        {
            var n = w.PieceIds.Count;
            var head = w.Message.Replace("{unit}", "").Trim();
            var node = new TreeNode($"{head} — {n} {(n == 1 ? "unit" : "units")}{(w.IsDecision ? (w.Acknowledged ? " (acknowledged)" : " (decision)") : "")}") { Tag = w };
            for (var i = 0; i < w.UnitTexts.Count; i++)
                node.Nodes.Add(new TreeNode(w.UnitTexts[i]) { Tag = i < w.PieceIds.Count ? w.PieceIds[i] : null });
            _warnings.Nodes.Add(node);
            if (open.Contains(w.Index)) node.Expand();
            if (selIndex == w.Index) _warnings.SelectedNode = node;
        }
        _warnings.EndUpdate();
        UpdateAckEnabled();
    }

    void ShowDecisions(PhaseView v)
    {
        // Hot-seat: another side's phase shows none of its decisions (its checklist included).
        var items = v.PlayingText != null ? new List<string>() : v.Current?.Checklist ?? new List<string>();
        var ticked = _s.View?.Ticked;
        _checklist.BeginUpdate();
        _checklist.Items.Clear();
        for (var i = 0; i < items.Count; i++) _checklist.Items.Add(items[i] ?? "", ticked?.Contains(i) == true);
        _checklist.EndUpdate();
        _openRows = v.Decisions.Where(d => d.Kind != DecisionKind.Checklist).ToList();
        var sel = _open.SelectedIndex;
        _open.BeginUpdate();
        _open.Items.Clear();
        foreach (var d in _openRows) _open.Items.Add(d.Text);
        _open.EndUpdate();
        if (sel >= 0 && sel < _open.Items.Count) _open.SelectedIndex = sel;
    }

    // ---------- actions ----------

    WarningHit? SelectedWarning() => (_warnings.SelectedNode is { } n ? (n.Parent ?? n) : null)?.Tag as WarningHit;

    void UpdateAckEnabled() => _ack.Enabled = SelectedWarning() is { IsDecision: true, Acknowledged: false };

    void AckSelected()
    {
        if (SelectedWarning() is { IsDecision: true, Acknowledged: false } w) _c.Acknowledge(w.Index);
        _watch.Flush();
    }

    void OnItemCheck(ItemCheckEventArgs e)
    {
        if (_updating) return;
        _c.Tick(e.Index, e.NewValue == CheckState.Checked);
        _watch.Request();
    }

    void OpenRef()
    {
        if (_refTarget is { } r) _openDoc(r.DocumentEntry, Math.Max(1, r.Page));
    }

    void OpenSelected()
    {
        var i = _open.SelectedIndex;
        if (i < 0 || i >= _openRows.Count) return;
        var d = _openRows[i];
        if (d.Kind == DecisionKind.Pending && d.RunId != null) _openRun(d.RunId);
        else if (d.PieceIds.FirstOrDefault() is { } id) _centre(id);
    }

    void Guard(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("phase panel: " + ex.Message);
            _s.Log.Add(Quartermaster.Vassal.DiagnosticSeverity.Warning, "phases", "The phase panel could not complete an action: " + ex.Message);
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
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _watch.Dispose(); _title.Font.Dispose(); _notesTip.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>Top-level window for a popped-out <see cref="PhasePanel"/>; closing it returns the panel to the main window.</summary>
sealed class PhaseFloatForm : Form, WindowManager.ICustomDock
{
    readonly Action _dockBack;
    bool _closingForReal;

    public PhaseFloatForm(Action dockBack)
    {
        _dockBack = dockBack;
        Text = "Phases";
        ShowIcon = false;
    }

    public void CloseForReal() { _closingForReal = true; Close(); }
    public void DockBack() => _dockBack();

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closingForReal && !e.Cancel && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; BeginInvoke(_dockBack); return; }
        base.OnFormClosing(e);
    }
}
