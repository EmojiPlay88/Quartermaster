using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Documents;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Preferences;

/// <summary>
/// Settings > OCR languages (port stage 4c): the tessdata_fast catalog with each language's size and status, and Download / Cancel / Remove.
/// Downloads act at once through the app's <see cref="OcrLanguageService"/>; OK, Apply, Cancel and Restore defaults never touch them.
/// The statuses are read off the UI thread (the first read hashes files) and posted back.
/// </summary>
public sealed class OcrLanguagesPage : SettingsPage
{
    public const string PageKey = "ocr-languages";
    public const string SourceText = "Downloads from github.com/tesseract-ocr/tessdata_fast (tag 4.1.0, Apache-2.0). Nothing is downloaded unless you click Download.";
    public const string WindowsText = "Windows OCR languages are added in Windows Settings.";
    public const string CloseText = "A language download is still running. Stop it and close Settings?";

    static readonly string[] Titles = { "Name", "Code", "Size", "Status" };
    static readonly double[] Widths = { 190, 60, 70, 140 };

    sealed class LangRow(OcrLanguageEntry entry, ListBoxItem item, TextBlock status)
    {
        public OcrLanguageEntry Entry { get; } = entry;
        public ListBoxItem Item { get; } = item;
        public TextBlock Status { get; } = status;
        public OcrLanguageState State { get; set; } = OcrLanguageState.NotInstalled;
        public string? Error { get; set; }
    }

    readonly OcrLanguageService _svc;
    readonly Dispatcher _ui;
    readonly List<LangRow> _rows = new();
    readonly Dictionary<string, LangRow> _byCode = new(StringComparer.OrdinalIgnoreCase);
    bool _attached, _refreshing, _again;
    Window? _closedHooked;

    internal readonly TextBox FilterBox = new() { Width = 220, PlaceholderText = "Name or code" };
    internal readonly ListBox List = new() { SelectionMode = SelectionMode.Multiple };
    internal readonly Button DownloadButton = new() { Content = "Download", Margin = new Thickness(0, 0, 6, 0) };
    internal readonly Button StopButton = new() { Content = "Cancel", Margin = new Thickness(0, 0, 6, 0) };
    internal readonly Button RemoveButton = new() { Content = "Remove" };
    internal readonly TextBlock SourceLine = new() { Text = SourceText, TextWrapping = TextWrapping.Wrap };
    internal readonly TextBlock? WindowsLine = OperatingSystem.IsWindows() ? new() { Text = WindowsText, TextWrapping = TextWrapping.Wrap } : null;
    internal readonly TextBlock ErrorLine = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, MaxHeight = 34 };

    public OcrLanguagesPage(OcrLanguageService service)
    {
        _svc = service;
        _ui = Dispatcher.UIThread;   // taken on the UI thread: StatusChanged fires on workers, which must not reach the static getter

        var header = new Grid { Margin = new Thickness(4, 0, 0, 2) };
        for (var i = 0; i < Titles.Length; i++)
        {
            header.ColumnDefinitions.Add(new ColumnDefinition(Widths[i], GridUnitType.Pixel));
            var t = new TextBlock { Text = Titles[i], FontWeight = FontWeight.SemiBold };
            Grid.SetColumn(t, i);
            header.Children.Add(t);
        }

        var filter = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 6) };
        filter.Children.Add(new TextBlock { Text = "Filter:", VerticalAlignment = VerticalAlignment.Center });
        filter.Children.Add(FilterBox);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        bar.Children.Add(DownloadButton);
        bar.Children.Add(StopButton);
        bar.Children.Add(RemoveButton);

        var notes = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };
        notes.Children.Add(ErrorLine);
        notes.Children.Add(SourceLine);
        if (WindowsLine != null) notes.Children.Add(WindowsLine);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(filter, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(notes, Dock.Bottom);
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(filter);
        root.Children.Add(notes);
        root.Children.Add(bar);
        root.Children.Add(header);
        root.Children.Add(List);
        Content = root;

        FilterBox.TextChanged += (_, _) => ApplyFilter();
        List.SelectionChanged += (_, _) => UpdateButtons();
        DownloadButton.Click += (_, _) => Download();
        StopButton.Click += (_, _) => _svc.CancelAll();
        RemoveButton.Click += async (_, _) => await RemoveAsync();
        UpdateButtons();

        // Follows the service only while shown (a page that is not selected is not in the visual tree).
        AttachedToVisualTree += (_, _) =>
        {
            if (_attached) return;
            _attached = true;
            _svc.StatusChanged += OnStatusChanged;
            if (TopLevel.GetTopLevel(this) is Window w && _closedHooked != w) { _closedHooked = w; w.Closed += (_, _) => Detach(); }
            Refresh();
        };
        DetachedFromVisualTree += (_, _) => Detach();
    }

    public override string Title => "OCR languages";
    public override string Key => PageKey;

    /// <summary>The app's language service (one per app).</summary>
    internal OcrLanguageService Service => _svc;

    /// <summary>True once the statuses have been read and shown.</summary>
    internal bool StatusesLoaded { get; private set; }

    internal IReadOnlyList<string> ColumnTitles => Titles;

    /// <summary>Every row (filtered or not): name, code, size, status.</summary>
    internal IReadOnlyList<(string Name, string Code, string Size, string Status)> RowsForTest => _rows.Select(Cells).ToList();

    /// <summary>The rows the filter lets through.</summary>
    internal IReadOnlyList<(string Name, string Code, string Size, string Status)> VisibleRowsForTest =>
        (List.ItemsSource as IEnumerable<ListBoxItem> ?? []).Select(i => Cells(_byCode[(string)i.Tag!])).ToList();

    static (string, string, string, string) Cells(LangRow r) => (r.Entry.Name, r.Entry.Code, Mb(r.Entry.Bytes), r.Status.Text ?? "");

    /// <summary>Selects exactly these rows.</summary>
    internal void SelectCodes(params string[] codes)
    {
        List.SelectedItems?.Clear();
        foreach (var c in codes)
            if (_byCode.TryGetValue(c, out var r)) List.SelectedItems?.Add(r.Item);
        UpdateButtons();
    }

    public override string? CloseQuestion => _svc.Idle.IsCompleted ? null : CloseText;

    public override void StopForClose() => _svc.CancelAll();

    public override void Load(Settings s) { }
    public override Settings Collect(Settings s) => s;

    // ---- status ----

    void OnStatusChanged() => _ui.Post(Refresh);

    void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _svc.StatusChanged -= OnStatusChanged;
    }

    /// <summary>Reads the statuses on a worker and shows them; changes that arrive meanwhile cause one more read.</summary>
    void Refresh()
    {
        if (!_attached) return;
        if (_refreshing) { _again = true; return; }
        _refreshing = true;
        _ = Task.Run(_svc.Statuses).ContinueWith(t => _ui.Post(() =>
        {
            _refreshing = false;
            if (!_attached) return;
            if (t.IsFaulted)
            {
                var message = t.Exception?.GetBaseException().Message;
                System.Diagnostics.Trace.WriteLine("ocr languages: " + message);
                ErrorLine.Text = "The languages could not be read: " + message;
                ErrorLine.IsVisible = true;
            }
            else Show(t.Result);
            if (_again) { _again = false; Refresh(); }
        }), TaskScheduler.Default);
    }

    void Show(IReadOnlyList<OcrLanguageStatus> statuses)
    {
        var added = false;
        foreach (var s in statuses)
        {
            if (!_byCode.TryGetValue(s.Entry.Code, out var row))
            {
                row = NewRow(s.Entry);
                _rows.Add(row);
                _byCode[s.Entry.Code] = row;
                added = true;
            }
            row.State = s.State;
            row.Error = s.Error;
            row.Status.Text = StateText(s);
        }
        var errors = _rows.Where(r => r.Error != null).Select(r => $"{r.Entry.Name} ({r.Entry.Code}): {r.Error}").ToList();
        ErrorLine.Text = string.Join("\n", errors);
        ErrorLine.IsVisible = errors.Count > 0;
        if (added) ApplyFilter();
        StatusesLoaded = true;
        UpdateButtons();
    }

    static string StateText(OcrLanguageStatus s) => s.State switch
    {
        OcrLanguageState.Bundled => "Bundled",
        OcrLanguageState.Installed => "Installed",
        OcrLanguageState.Damaged => "Damaged — download again",
        OcrLanguageState.Queued => "Queued",
        OcrLanguageState.Downloading => "Downloading " + ((int)Math.Floor(Math.Clamp(s.Progress, 0, 1) * 100)).ToString(CultureInfo.InvariantCulture) + "%",
        _ => "Not installed",
    };

    static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) + " MB";

    static LangRow NewRow(OcrLanguageEntry e)
    {
        var g = new Grid();
        var status = new TextBlock();
        var cells = new[] { new TextBlock { Text = e.Name }, new TextBlock { Text = e.Code }, new TextBlock { Text = Mb(e.Bytes) }, status };
        for (var i = 0; i < cells.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition(Widths[i], GridUnitType.Pixel));
            cells[i].TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(cells[i], i);
            g.Children.Add(cells[i]);
        }
        return new LangRow(e, new ListBoxItem { Content = g, Tag = e.Code, Padding = new Thickness(4, 2) }, status);
    }

    void ApplyFilter()
    {
        var f = FilterBox.Text?.Trim() ?? "";
        var selected = SelectedRows().Select(r => r.Item).ToList();
        var items = _rows.Where(r => f.Length == 0 || r.Entry.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Entry.Code.Contains(f, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Item).ToList();
        List.ItemsSource = items;
        foreach (var i in selected.Where(items.Contains)) List.SelectedItems?.Add(i);
        UpdateButtons();
    }

    List<LangRow> SelectedRows() =>
        (List.SelectedItems?.OfType<ListBoxItem>() ?? []).Select(i => _byCode.GetValueOrDefault((string)i.Tag!)).OfType<LangRow>().ToList();

    static bool CanDownload(LangRow r) => r.State is OcrLanguageState.NotInstalled or OcrLanguageState.Damaged;
    static bool CanRemove(LangRow r) => r.State is OcrLanguageState.Installed or OcrLanguageState.Damaged;

    void UpdateButtons()
    {
        var sel = SelectedRows();
        DownloadButton.IsEnabled = sel.Any(CanDownload);
        RemoveButton.IsEnabled = sel.Any(CanRemove);
        StopButton.IsEnabled = _rows.Any(r => r.State is OcrLanguageState.Downloading or OcrLanguageState.Queued);
    }

    // ---- actions ----

    void Download()
    {
        var codes = SelectedRows().Where(CanDownload).Select(r => r.Entry.Code).ToList();
        if (codes.Count == 0) return;
        DownloadButton.IsEnabled = false;
        // Enqueue checks the files (a hash for an uncached one): off the UI thread.
        _ = Task.Run(() => _svc.Enqueue(codes)).ContinueWith(t =>
        {
            if (t.IsFaulted) System.Diagnostics.Trace.WriteLine("ocr languages: " + t.Exception?.GetBaseException().Message);
        }, TaskScheduler.Default);
    }

    async Task RemoveAsync()
    {
        var codes = SelectedRows().Where(CanRemove).Select(r => r.Entry.Code).ToList();
        if (codes.Count == 0) return;
        RemoveButton.IsEnabled = false;
        foreach (var code in codes)
        {
            try { await Task.Run(() => _svc.RemoveAsync(code)); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                System.Diagnostics.Trace.WriteLine("ocr languages: " + ex.Message);
                ErrorLine.Text = $"{code}: {ex.Message}";
                ErrorLine.IsVisible = true;
            }
        }
        Refresh();
    }
}
