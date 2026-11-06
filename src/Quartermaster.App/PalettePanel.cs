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
    const int ThumbCap = 600, CacheCap = 2000;
    readonly ModuleSession _s;
    readonly PaletteThumbs _thumbs;
    readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false };
    readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.LargeIcon, MultiSelect = false, HideSelection = false, ShowItemToolTips = true };
    readonly TextBox _search = new() { Width = 150 };
    readonly ToolStripButton _popButton = new("Pop out") { Alignment = ToolStripItemAlignment.Right };
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 250 };
    readonly Dictionary<string, Bitmap> _cache = new();
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
        var missing = new List<(ListViewItem Item, PieceSlotDef Slot)>();
        foreach (var slot in slots)
        {
            var item = new ListViewItem(slot.EntryName, 0) { Tag = slot, ToolTipText = slot.EntryName };
            _list.Items.Add(item);
            if (_cache.TryGetValue(PaletteModel.Key(slot), out var bmp))
            {
                images.Images.Add(bmp, Color.Transparent);
                item.ImageIndex = images.Images.Count - 1;
            }
            else if (missing.Count < ThumbCap) missing.Add((item, slot));
        }
        _list.EndUpdate();
        old?.Dispose();
        if (missing.Count == 0) return;
        var cts = _cts = new CancellationTokenSource();
        var token = cts.Token;
        _ = Task.Run(() =>
        {
            foreach (var (item, slot) in missing)
            {
                if (token.IsCancellationRequested) return;
                var bmp = _thumbs.Render(slot);
                if (bmp == null) continue;
                try
                {
                    if (IsDisposed || !IsHandleCreated) { bmp.Dispose(); return; }
                    BeginInvoke(() => Apply(gen, item, slot, bmp));
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { bmp.Dispose(); return; }
            }
        }, token);
    }

    void Apply(int gen, ListViewItem item, PieceSlotDef slot, Bitmap bmp)
    {
        if (IsDisposed) { bmp.Dispose(); return; }
        if (_cache.Count >= CacheCap) ClearCache();
        _cache[PaletteModel.Key(slot)] = bmp;
        if (gen != _generation || _images == null || item.ListView != _list) return;
        _images.Images.Add(bmp, Color.Transparent);
        item.ImageIndex = _images.Images.Count - 1;
    }

    void ClearCache()
    {
        foreach (var b in _cache.Values) b.Dispose();
        _cache.Clear();
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
            _cts?.Cancel();
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
