using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Desktop.Shell;

/// <summary>Palette drag payloads on Avalonia, and placing a dropped counter through the shared <see cref="PaletteModel.Place"/>.</summary>
public static class PaletteDrop
{
    /// <summary>The drag payload: the slot key (<see cref="PaletteModel.Key"/>) under <see cref="PaletteModel.DragFormat"/>.</summary>
    public static readonly DataFormat<string> Format = DataFormat.CreateStringApplicationFormat(PaletteModel.DragFormat);

    public static PieceSlotDef? Find(ModuleSession s, string? key) => PaletteModel.Find(s.Model, key);

    /// <summary>The slot carried by a drag, or null when the data is not a palette drag of this module.</summary>
    public static PieceSlotDef? SlotFrom(ModuleSession s, IDataTransfer? data) =>
        data != null && data.Contains(Format) ? Find(s, data.TryGetValue(Format)) : null;

    /// <summary>The drag payload for a slot.</summary>
    public static DataTransfer DataFor(PieceSlotDef slot)
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(Format, PaletteModel.Key(slot)));
        return data;
    }

    /// <summary>Places the counter (shared with WinForms: <see cref="PaletteModel.Place"/>).</summary>
    public static (bool Placed, string Message) Place(ModuleSession s, MapDef map, PieceSlotDef slot, PointD mapPoint, bool snap, IActionPort? actions = null) =>
        PaletteModel.Place(s, map, slot, mapPoint, snap, actions);
}

/// <summary>
/// The counter palette, as WinForms PalettePanel: a search box, the module's folder tree, and the selected folder's (or the search's)
/// counters with their images. Thumbnails are drawn on a worker thread and cached. Dragging a counter onto a map places it.
/// </summary>
public sealed class PaletteDock : DockPanel
{
    /// <summary>Thumbnail size (WinForms PaletteThumbs.Size).</summary>
    public static readonly PixelSize ThumbSize = new(64, 48);
    const int CacheCap = 3000;

    readonly TextBox _search = new() { Width = 150 };
    readonly TreeView _tree = new();
    readonly ListBox _list = new();
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly Dictionary<string, Bitmap> _cache = new();
    readonly Queue<string> _cacheOrder = new();
    ModuleSession? _s;
    IReadOnlyList<PieceSlotDef> _listed = Array.Empty<PieceSlotDef>();
    CancellationTokenSource? _cts;
    Task? _worker;
    PointerPressedEventArgs? _press;
    PieceSlotDef? _pressSlot;
    Point _pressAt;

    public PaletteDock()
    {
        var bar = new DockPanel { Margin = new Thickness(2) };
        var label = new TextBlock { Text = "Search:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) };
        DockPanel.SetDock(label, Dock.Left);
        bar.Children.Add(label);
        _search.HorizontalAlignment = HorizontalAlignment.Left;
        bar.Children.Add(_search);
        SetDock(bar, Dock.Top);
        Children.Add(bar);

        var split = new Grid { RowDefinitions = new RowDefinitions("140,4,*") };
        Grid.SetRow(_tree, 0);
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(splitter, 1);
        _list.ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new WrapPanel());
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        Grid.SetRow(_list, 2);
        split.Children.Add(_tree);
        split.Children.Add(splitter);
        split.Children.Add(_list);
        Children.Add(split);

        _tree.SelectionChanged += (_, _) => Guard(ShowSelection);
        _search.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Guard(ShowSelection); };
        _list.AddHandler(PointerPressedEvent, OnListPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        _list.AddHandler(PointerMovedEvent, OnListMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        _list.AddHandler(PointerReleasedEvent, (_, _) => { _press = null; _pressSlot = null; }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>The counters currently listed.</summary>
    public IReadOnlyList<PieceSlotDef> ListedSlots => _listed;

    /// <summary>The search text (filters after a short pause, as WinForms).</summary>
    public string SearchText { get => _search.Text ?? ""; set { _search.Text = value; _debounce.Stop(); Guard(ShowSelection); } }

    /// <summary>Shows the session's palette (none for null). Selects the first folder that holds counters.</summary>
    public void Bind(ModuleSession? s)
    {
        StopWorker();
        ClearCache();
        _s = s;
        _search.Text = "";
        _tree.ItemsSource = null;
        Show(Array.Empty<PieceSlotDef>());
        if (s == null) return;
        var root = PaletteModel.Build(s.Model);
        var top = Node(root);
        top.IsExpanded = true;
        _tree.ItemsSource = new[] { top };
        _tree.SelectedItem = FirstWithSlots(top) ?? top;
        Guard(ShowSelection);
    }

    static TreeViewItem Node(PaletteNode n)
    {
        var t = new TreeViewItem { Header = n.Title, Tag = n };
        t.ItemsSource = n.Children.Select(Node).ToList();
        return t;
    }

    static TreeViewItem? FirstWithSlots(TreeViewItem t)
    {
        if (t.Tag is PaletteNode { Slots.Count: > 0 }) return t;
        foreach (var c in t.ItemsSource?.OfType<TreeViewItem>() ?? Enumerable.Empty<TreeViewItem>())
            if (FirstWithSlots(c) is { } f) { t.IsExpanded = true; return f; }
        return null;
    }

    void ShowSelection()
    {
        if (_s == null) return;
        var text = (_search.Text ?? "").Trim();
        IReadOnlyList<PieceSlotDef> slots = text.Length > 0 ? PaletteModel.Search(_s.Model, text)
            : _tree.SelectedItem is TreeViewItem { Tag: PaletteNode n } ? n.Slots : Array.Empty<PieceSlotDef>();
        Show(slots);
    }

    void Show(IReadOnlyList<PieceSlotDef> slots)
    {
        StopWorker();
        _listed = slots;
        var items = new List<ListBoxItem>(slots.Count);
        var missing = new List<(Image Img, PieceSlotDef Slot)>();
        foreach (var slot in slots)
        {
            var img = new Image { Width = ThumbSize.Width, Height = ThumbSize.Height, Stretch = Stretch.None };
            if (_cache.TryGetValue(PaletteModel.Key(slot), out var bmp)) img.Source = bmp;
            else missing.Add((img, slot));
            var text = new TextBlock { Text = slot.EntryName, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 80 };
            var panel = new StackPanel { Width = 84, Children = { img, text } };
            var item = new ListBoxItem { Content = panel, Tag = slot, Padding = new Thickness(2) };
            ToolTip.SetTip(item, slot.EntryName);
            items.Add(item);
        }
        _list.ItemsSource = items;
        if (missing.Count == 0 || _s is not { } s) return;
        var cts = _cts = new CancellationTokenSource();
        _worker = Task.Run(() => Work(s, missing, cts.Token), cts.Token);
    }

    /// <summary>Renders the thumbnails in list order on a worker thread and hands them to the UI thread.</summary>
    void Work(ModuleSession s, List<(Image Img, PieceSlotDef Slot)> missing, CancellationToken ct)
    {
        foreach (var (img, slot) in missing)
        {
            if (ct.IsCancellationRequested || s.Disposed) return;
            var bmp = Render(s, slot);
            if (bmp == null) continue;
            Dispatcher.UIThread.Post(() =>
            {
                if (ct.IsCancellationRequested) { bmp.Dispose(); return; }
                Remember(PaletteModel.Key(slot), bmp);
                img.Source = bmp;
            });
        }
    }

    /// <summary>The slot's counter fitted into <see cref="ThumbSize"/> (WinForms PaletteThumbs.Render), or null when it cannot be drawn.</summary>
    public static Bitmap? Render(ModuleSession s, PieceSlotDef slot)
    {
        try
        {
            var (type, state) = PieceFactory.FromSlot(s.Model, slot, "", new PointD(0, 0));
            var count = PieceTypeParser.Parse(type, new DiagnosticLog(), null).Traits.Count;
            var piece = new PieceInstance { Id = "thumb", TypeString = type, TraitStates = PieceTypeParser.SplitState(state, count).ToList() };
            var img = s.Counters.Render(piece, new SKSizeI(ThumbSize.Width, ThumbSize.Height));   // cached by CounterImages: not ours to dispose
            if (img == null) return null;
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = new MemoryStream(data.ToArray());
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    void Remember(string key, Bitmap bmp)
    {
        if (_cache.Remove(key, out var old)) old.Dispose();
        _cache[key] = bmp;
        _cacheOrder.Enqueue(key);
        while (_cache.Count > CacheCap && _cacheOrder.TryDequeue(out var k))
            if (_cache.Remove(k, out var gone) && !ReferenceEquals(gone, bmp)) gone.Dispose();
    }

    void ClearCache()
    {
        foreach (var b in _cache.Values) b.Dispose();
        _cache.Clear();
        _cacheOrder.Clear();
    }

    /// <summary>Stops the thumbnail worker and waits briefly for an in-flight render, so the module archive can be closed safely.</summary>
    public void StopWorker()
    {
        _cts?.Cancel();
        try { _worker?.Wait(2000); }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException) { }
        _cts?.Dispose();
        _cts = null;
        _worker = null;
    }

    void OnListPressed(object? sender, PointerPressedEventArgs e)
    {
        _press = null;
        _pressSlot = null;
        if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed) return;
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { Tag: PieceSlotDef slot }) return;
        _press = e;
        _pressSlot = slot;
        _pressAt = e.GetPosition(_list);
    }

    async void OnListMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press || _pressSlot is not { } slot) return;
        var p = e.GetPosition(_list);
        if (Math.Abs(p.X - _pressAt.X) <= 4 && Math.Abs(p.Y - _pressAt.Y) <= 4) return;
        _press = null;
        _pressSlot = null;
        try { await DragDrop.DoDragDropAsync(press, PaletteDrop.DataFor(slot), DragDropEffects.Copy); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("palette drag: " + ex); }
    }

    static void Guard(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("palette: " + ex); }
    }
}
