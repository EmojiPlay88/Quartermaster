using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>MDI child listing the module's <see cref="DiagnosticLog"/>; live-updates while open.</summary>
sealed class DiagnosticsWindow : Form
{
    readonly DiagnosticLog _log;
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false,
    };
    readonly ToolStripComboBox _filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 140 };
    readonly ConcurrentQueue<Diagnostic> _pending = new();
    int _drainQueued;
    bool _subscribed;

    public DiagnosticsWindow(DiagnosticLog log)
    {
        _log = log;
        Text = "Diagnostics";
        ShowInTaskbar = false;
        _list.Columns.Add("Severity", 70);
        _list.Columns.Add("Category", 90);
        _list.Columns.Add("Message", 520);
        _list.Columns.Add("Context", 160);

        _filter.Items.AddRange(DiagnosticsView.FilterNames.Cast<object>().ToArray());
        _filter.SelectedIndex = 0;
        var copy = new ToolStripButton("Copy all") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
        bar.Items.Add(new ToolStripLabel("Show:"));
        bar.Items.Add(_filter);
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(copy);
        Controls.Add(_list);
        Controls.Add(bar);

        _filter.SelectedIndexChanged += (_, _) => Repopulate();
        copy.Click += (_, _) => CopyAll();

        // Subscribe before the snapshot so nothing is lost; Repopulate de-dupes by draining the queue afterwards.
        _log.Added += OnAdded;
        _subscribed = true;
        Repopulate();
    }

    DiagnosticsFilter Filter => DiagnosticsView.FilterFor(_filter.SelectedIndex);

    // Runs on whichever thread logged; must never throw back into the logger.
    void OnAdded(Diagnostic d)
    {
        try
        {
            _pending.Enqueue(d);
            if (!IsHandleCreated || IsDisposed) return; // OnHandleCreated drains what arrived early
            if (Interlocked.Exchange(ref _drainQueued, 1) != 0) return;
            try { BeginInvoke(new Action(Drain)); }
            catch (Exception) { Interlocked.Exchange(ref _drainQueued, 0); }
        }
        catch (Exception) { /* window is closing */ }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Drain();
    }

    void Drain()
    {
        Interlocked.Exchange(ref _drainQueued, 0);
        _list.BeginUpdate();
        try
        {
            while (_pending.TryDequeue(out var d)) AddRow(d);
        }
        finally { _list.EndUpdate(); }
    }

    void AddRow(Diagnostic d)
    {
        if (!DiagnosticsView.Passes(d, Filter)) return;
        _list.Items.Add(new ListViewItem(new[] { d.Severity.ToString(), d.Category, d.Message, d.Context ?? "" }));
    }

    void Repopulate()
    {
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            while (_pending.TryDequeue(out _)) { }
            foreach (var d in _log.Items) AddRow(d);
        }
        finally { _list.EndUpdate(); }
    }

    void CopyAll()
    {
        var shown = new List<Diagnostic>();
        foreach (var d in _log.Items) if (DiagnosticsView.Passes(d, Filter)) shown.Add(d);
        try { Clipboard.SetText(DiagnosticsView.CopyText(shown)); }
        catch (ExternalException) { /* clipboard busy */ }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Unsubscribe();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        Unsubscribe();
        base.Dispose(disposing);
    }

    void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        _log.Added -= OnAdded;
    }
}
