using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tools;

/// <summary>Lists the module's <see cref="DiagnosticLog"/> and updates live while open (WinForms DiagnosticsWindow). Entries can arrive from any thread.</summary>
public sealed class DiagnosticsWindow : Window
{
    static readonly double[] Widths = { 70, 90, 520, 160 };
    static readonly string[] Headers = { "Severity", "Category", "Message", "Context" };

    readonly DiagnosticLog _log;
    readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple, Name = "DiagnosticsList" };
    readonly ComboBox _filter = new() { Width = 140, Name = "DiagnosticsFilter" };
    readonly Button _copy = new() { Content = "Copy all", Name = "DiagnosticsCopy" };
    readonly ConcurrentQueue<Diagnostic> _pending = new();
    readonly List<Diagnostic> _shown = new();
    int _drainQueued;
    bool _subscribed;
    bool _closed;

    internal DiagnosticsWindow(DiagnosticLog log)
    {
        _log = log;
        Title = "Diagnostics";
        ShowInTaskbar = false;
        Width = 880;
        Height = 400;

        foreach (var n in DiagnosticsView.FilterNames) _filter.Items.Add(n);
        _filter.SelectedIndex = 0;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4) };
        bar.Children.Add(new TextBlock { Text = "Show:", VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(_filter);
        bar.Children.Add(_copy);

        var header = new Grid { ColumnDefinitions = Columns() };
        for (var i = 0; i < Headers.Length; i++)
        {
            var cell = new Border { BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(4, 2), Child = new TextBlock { Text = Headers[i], FontWeight = FontWeight.Bold } };
            cell.Bind(Border.BorderBrushProperty, cell.GetResourceObservable("Classic.BorderBrush"));
            cell.Bind(Border.BackgroundProperty, cell.GetResourceObservable("Classic.FaceBrush"));
            Grid.SetColumn(cell, i);
            header.Children.Add(cell);
        }
        _list.ItemTemplate = new FuncDataTemplate<Diagnostic>((d, _) => d == null ? new Panel() : RowControl(d), supportsRecycling: false);
        var table = new DockPanel { Width = Widths.Sum() + 24 };
        DockPanel.SetDock(header, Dock.Top);
        table.Children.Add(header);
        table.Children.Add(_list);
        var scroller = new ScrollViewer
        {
            Content = table, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(scroller);
        Content = root;

        _filter.SelectionChanged += (_, _) => Repopulate();
        _copy.Click += (_, _) => CopyAll();

        // Subscribe before the snapshot so nothing is lost; Repopulate de-dupes by draining the queue afterwards.
        _log.Added += OnAdded;
        _subscribed = true;
        Repopulate();
    }

    static ColumnDefinitions Columns()
    {
        var d = new ColumnDefinitions();
        foreach (var w in Widths) d.Add(new ColumnDefinition(w, GridUnitType.Pixel));
        return d;
    }

    static Control RowControl(Diagnostic d)
    {
        var g = new Grid { ColumnDefinitions = Columns() };
        var texts = new[] { d.Severity.ToString(), d.Category, d.Message, d.Context ?? "" };
        for (var i = 0; i < texts.Length; i++)
        {
            var t = new TextBlock { Text = texts[i], Margin = new Thickness(4, 1), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }
        return g;
    }

    DiagnosticsFilter Filter => DiagnosticsView.FilterFor(_filter.SelectedIndex);

    // Runs on whichever thread logged; must never throw back into the logger.
    void OnAdded(Diagnostic d)
    {
        try
        {
            _pending.Enqueue(d);
            if (_closed || Interlocked.Exchange(ref _drainQueued, 1) != 0) return;
            Dispatcher.UIThread.Post(Drain);
        }
        catch (Exception) { Interlocked.Exchange(ref _drainQueued, 0); /* shutting down */ }
    }

    void Drain()
    {
        Interlocked.Exchange(ref _drainQueued, 0);
        if (_closed) return;
        var added = false;
        while (_pending.TryDequeue(out var d))
            if (DiagnosticsView.Passes(d, Filter)) { _shown.Add(d); added = true; }
        if (added) ShowRows();
    }

    void Repopulate()
    {
        // Snapshot first, then the queue: an entry logged meanwhile is in the queue and maybe in the snapshot; the snapshot wins (no duplicate),
        // and an entry only in the queue is added after it. The window subscribed before the first snapshot, so nothing falls between.
        BeforeSnapshot?.Invoke();
        var snapshot = _log.Items;
        AfterSnapshot?.Invoke();
        var seen = new HashSet<Diagnostic>(snapshot, ReferenceEqualityComparer.Instance);
        _shown.Clear();
        _shown.AddRange(DiagnosticsView.Apply(snapshot, Filter));
        while (_pending.TryDequeue(out var d))
            if (!seen.Contains(d) && DiagnosticsView.Passes(d, Filter)) _shown.Add(d);
        ShowRows();
    }

    void ShowRows() => _list.ItemsSource = _shown.ToList();

    /// <summary>The Copy all button: the shown entries, tab separated, to the clipboard.</summary>
    internal async Task CopyAllAsync()
    {
        var text = DiagnosticsView.CopyText(DiagnosticsView.Apply(_log.Items, Filter));
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) {
                var t = new Avalonia.Input.DataTransfer();
                t.Add(Avalonia.Input.DataTransferItem.CreateText(text));
                await cb.SetDataAsync(t);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("clipboard busy: " + ex.Message); }
    }

    async void CopyAll() => await CopyAllAsync();

    protected override void OnClosed(EventArgs e)
    {
        Unsubscribe();
        base.OnClosed(e);
    }

    void Unsubscribe()
    {
        _closed = true;
        if (!_subscribed) return;
        _subscribed = false;
        _log.Added -= OnAdded;
    }

    // ---------- what tests read and drive ----------

    /// <summary>Test seams: run inside <see cref="Repopulate"/> just before and just after the log is snapshotted.</summary>
    internal Action? BeforeSnapshot, AfterSnapshot;

    internal void RepopulateForTest() => Repopulate();

    /// <summary>The entries shown now.</summary>
    internal IReadOnlyList<Diagnostic> Shown => _shown;
    internal ComboBox FilterBox => _filter;
    internal Button CopyButton => _copy;
    internal static IReadOnlyList<string> ColumnHeaders => Headers;
    internal static IReadOnlyList<double> ColumnWidths => Widths;
}
