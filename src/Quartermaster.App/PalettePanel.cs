using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>
/// Game-master counter palette: the module's folder tree on top, the selected folder's (or the search's) counters below as thumbnails.
/// Thumbnails are drawn lazily on a worker thread; each list gets its own ImageList (disposed when the list changes). Dragging a counter
/// starts an OLE drag in format <see cref="PaletteModel.DragFormat"/>, which map canvases accept.
/// </summary>
sealed class PalettePanel : UserControl
{
    const int CacheCap = 3000, Batch = 20;
    readonly ModuleSession _s;
    readonly PaletteThumbs _thumbs;
    readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false };
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.LargeIcon, MultiSelect = false, HideSelection = false, ShowItemToolTips = true };
    readonly TextBox _search = new() { Width = 150 };
    readonly ToolStripButton _popButton = new("Pop out") { Alignment = ToolStripItemAlignment.Right };
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 250 };
    readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Bmp)>> _cache = new();
    readonly LinkedList<(string Key, Bitmap Bmp)> _lru = new();
    readonly System.Windows.Forms.Timer _scroll = new() { Interval = 100 };
    readonly object _gate = new();
    List<(ListViewItem Item, PieceSlotDef Slot)> _pending = new();
    ListViewItem[] _items = Array.Empty<ListViewItem>();
    (int First, int Last) _range = (-1, -1);
    Task? _worker;
    volatile bool _closing;
    CancellationTokenSource? _cts;
    ImageList? _images;
    int _generation;

    public PalettePanel(ModuleSession s)
    {
        _s = s;
        _thumbs = new PaletteThumbs(s);
        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
        bar.Items.Add(new ToolStripLabel("Search:"));
        bar.Items.Add(new ToolStripControlHost(_search));
        _popButton.Click += (_, _) => PopToggleRequested?.Invoke();
        bar.Items.Add(_popButton);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 140 };
        split.Panel1.Controls.Add(_tree);
        split.Panel2.Controls.Add(_list);
        Controls.Add(split);
        Controls.Add(bar);

        FillTree(PaletteModel.Build(s.Model));
        _tree.AfterSelect += (_, _) => Guard(ShowSelection);
        _search.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _scroll.Tick += (_, _) => Guard(UpdateRange);
        _scroll.Start();
        _debounce.Tick += (_, _) => { _debounce.Stop(); Guard(ShowSelection); };
        _list.ItemDrag += (_, e) => Guard(() => Drag(e.Item as ListViewItem));
        if (_tree.Nodes.Count > 0) _tree.SelectedNode = _tree.Nodes[0];
    }

    public event Action? PopToggleRequested;

    public void SetFloating(bool floating) => _popButton.Text = floating ? "Dock" : "Pop out";

    /// <summary>Smoke: the counters currently listed.</summary>
    public IReadOnlyList<PieceSlotDef> ListedSlots => _list.Items.Cast<ListViewItem>().Select(i => (PieceSlotDef)i.Tag!).ToList();

    /// <summary>Smoke: selects the first folder holding counters, which lists it.</summary>
    public void SelectFirstFolderWithSlots()
    {
        static TreeNode? Find(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Tag is PaletteNode { Slots.Count: > 0 }) return n;
                if (Find(n.Nodes) is { } f) return f;
            }
            return null;
        }
        if (Find(_tree.Nodes) is { } node) _tree.SelectedNode = node;
    }

    /// <summary>Smoke: scrolls to the last listed counter and returns how many of the last 12 have a real thumbnail after the worker had time.</summary>
    public int ScrollToEndAndCountThumbs()
    {
        if (_items.Length == 0) return 0;
        _list.EnsureVisible(_items.Length - 1);
        var until = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < until && _items.Skip(_items.Length - 12).Any(i => i.ImageIndex == 0)) { Application.DoEvents(); Thread.Sleep(20); }
        int pend; lock (_gate) pend = _pending.Count;
        Program.AppLog($"smoke: palette range {_range} pending {pend}");
        return _items.Skip(_items.Length - 12).Count(i => i.ImageIndex > 0);
    }

    void FillTree(PaletteNode root)
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        static TreeNode Add(PaletteNode n)
        {
            var t = new TreeNode(n.Title) { Tag = n };
            foreach (var c in n.Children) t.Nodes.Add(Add(c));
            return t;
        }
        var top = Add(root);
        _tree.Nodes.Add(top);
        top.Expand();
        _tree.EndUpdate();
    }

    void ShowSelection()
    {
        IReadOnlyList<PieceSlotDef> slots;
        var text = _search.Text.Trim();
        if (text.Length > 0) slots = PaletteModel.Search(_s.Model, text);
        else if (_tree.SelectedNode?.Tag is PaletteNode n) slots = n.Slots;
        else slots = Array.Empty<PieceSlotDef>();
        Show(slots);
    }

    void Show(IReadOnlyList<PieceSlotDef> slots)
    {
        _cts?.Cancel();
        var gen = ++_generation;
        var old = _images;
        _list.BeginUpdate();
        _list.Items.Clear();
        var images = new ImageList { ImageSize = PaletteThumbs.Size, ColorDepth = ColorDepth.Depth32Bit };
        _ = images.Handle; // with a handle, Images.Add copies the bitmap, so ours can be disposed or cached freely
        using (var ph = Placeholder()) images.Images.Add(ph);
        _list.LargeImageList = images;
        _images = images;
        var items = new List<ListViewItem>(slots.Count);
        var missing = new List<(ListViewItem Item, PieceSlotDef Slot)>();
        foreach (var slot in slots)
        {
            var item = new ListViewItem(slot.EntryName, 0) { Tag = slot, ToolTipText = slot.EntryName };
            items.Add(item);
            if (TryCached(PaletteModel.Key(slot), out var bmp))
            {
                images.Images.Add(bmp, Color.Transparent);
                item.ImageIndex = images.Images.Count - 1;
            }
            else missing.Add((item, slot));
        }
        _list.Items.AddRange(items.ToArray());
        _items = items.ToArray();
        _list.EndUpdate();
        old?.Dispose();
        _range = (-1, -1);
        lock (_gate) _pending = missing;
        if (missing.Count == 0) return;
        Prioritise(CurrentRange());
        var cts = _cts = new CancellationTokenSource();
        var token = cts.Token;
        _worker = Task.Run(() => Work(gen, token), token);
    }

    void Work(int gen, CancellationToken token)
    {
        var batch = new List<(int Gen, ListViewItem Item, PieceSlotDef Slot, Bitmap Bmp)>();
        while (!token.IsCancellationRequested && !_closing)
        {
            (ListViewItem Item, PieceSlotDef Slot) next;
            lock (_gate)
            {
                if (_pending.Count == 0) break;
                next = _pending[0];
                _pending.RemoveAt(0);
            }
            var bmp = _thumbs.Render(next.Slot);
            if (bmp != null) batch.Add((gen, next.Item, next.Slot, bmp));
            bool empty;
            lock (_gate) empty = _pending.Count == 0;
            if (batch.Count >= Batch || (empty && batch.Count > 0)) Flush(batch);
        }
        foreach (var b in batch) b.Bmp.Dispose();
    }

    void Flush(List<(int Gen, ListViewItem Item, PieceSlotDef Slot, Bitmap Bmp)> batch)
    {
        var send = batch.ToList();
        batch.Clear();
        try
        {
            if (_closing || IsDisposed || !IsHandleCreated) throw new ObjectDisposedException(nameof(PalettePanel));
            BeginInvoke(() => { foreach (var b in send) Apply(b.Gen, b.Item, b.Slot, b.Bmp); });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            foreach (var b in send) b.Bmp.Dispose();
        }
    }

    (int First, int Last) CurrentRange()
    {
        if (_items.Length == 0) return (0, 0);
        // TopItem is unavailable in icon views; item rows are monotone in Y, so binary-search the first item that is not scrolled off the top.
        int lo = 0, hi = _items.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_items[mid].Bounds.Bottom > 0) hi = mid; else lo = mid + 1;
        }
        var first = lo;
        var last = first;
        while (last + 1 < _items.Length && _items[last + 1].Bounds.Top < _list.ClientSize.Height) last++;
        return (first, last);
    }

    void UpdateRange()
    {
        var r = CurrentRange();
        if (r != _range) Prioritise(r);
    }

    /// <summary>Re-sorts the pending thumbnails so the visible range, then its neighbours, come first.</summary>
    void Prioritise((int First, int Last) r)
    {
        _range = r;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            var rank = new Dictionary<ListViewItem, int>();
            var order = ThumbQueue.Order(_items.Length, r.First, r.Last);
            for (var k = 0; k < order.Count; k++) rank[_items[order[k]]] = k;
            _pending = _pending.OrderBy(p => rank.GetValueOrDefault(p.Item, int.MaxValue)).ToList();
        }
    }

    bool TryCached(string key, out Bitmap bmp)
    {
        if (_cache.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            bmp = node.Value.Bmp;
            return true;
        }
        bmp = null!;
        return false;
    }

    void Apply(int gen, ListViewItem item, PieceSlotDef slot, Bitmap bmp)
    {
        if (IsDisposed || _closing) { bmp.Dispose(); return; }
        var key = PaletteModel.Key(slot);
        if (_cache.TryGetValue(key, out var old)) { _lru.Remove(old); old.Value.Bmp.Dispose(); }
        _cache[key] = _lru.AddFirst((key, bmp));
        while (_cache.Count > CacheCap && _lru.Last is { } last)
        {
            _lru.RemoveLast();
            _cache.Remove(last.Value.Key);
            last.Value.Bmp.Dispose();
        }
        if (gen != _generation || _images == null || item.ListView != _list) return;
        _images.Images.Add(bmp, Color.Transparent);
        item.ImageIndex = _images.Images.Count - 1;
    }

    void ClearCache()
    {
        foreach (var n in _lru) n.Bmp.Dispose();
        _lru.Clear();
        _cache.Clear();
    }

    /// <summary>Stops the thumbnail worker and waits briefly for an in-flight render, so the module archive can be closed safely.</summary>
    public void StopWorker()
    {
        _closing = true;
        _scroll.Stop();
        _cts?.Cancel();
        try { _worker?.Wait(2000); }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException) { }
    }

    static Bitmap Placeholder()
    {
        var b = new Bitmap(PaletteThumbs.Size.Width, PaletteThumbs.Size.Height);
        using var g = Graphics.FromImage(b);
        g.Clear(Color.Transparent);
        using var pen = new Pen(SystemColors.ControlDark) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
        g.DrawRectangle(pen, 6, 4, b.Width - 13, b.Height - 9);
        return b;
    }

    void Drag(ListViewItem? item)
    {
        if (item?.Tag is not PieceSlotDef slot) return;
        var data = new DataObject();
        data.SetData(PaletteModel.DragFormat, PaletteModel.Key(slot));
        DoDragDrop(data, DragDropEffects.Copy);
    }

    static void Guard(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("palette: " + ex); }
    }

    public void SaveScreenshot(string path)
    {
        Update();
        using var bmp = new Bitmap(Math.Max(Width, 1), Math.Max(Height, 1));
        DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Program.AppLog("smoke: palette screenshot saved to " + path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopWorker();
            _scroll.Dispose();
            _debounce.Dispose();
            _list.LargeImageList = null;
            _images?.Dispose();
            ClearCache();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Top-level window for a popped-out <see cref="PalettePanel"/>; closing it returns the panel to the main window.</summary>
sealed class PaletteFloatForm : Form, WindowManager.ICustomDock
{
    readonly Action _dockBack;
    bool _closingForReal;

    public PaletteFloatForm(Action dockBack)
    {
        _dockBack = dockBack;
        Text = "Counter palette";
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
