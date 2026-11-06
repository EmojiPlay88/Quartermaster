using System.Runtime.InteropServices;
using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// Owns docked (MDI tab) versus floating (top-level, multi-monitor) state of document windows, the pop-out/dock operations,
/// the "Dock to Main Window" system-menu item and the per-module layout memory. Windows are never re-created: the same
/// Form instances move between the MDI area and the desktop, so game events and contents stay attached.
/// </summary>
sealed class WindowManager
{
    const uint WM_SYSCOMMAND = 0x0112, WM_NCLBUTTONDOWN = 0x00A1, MF_STRING = 0, MF_SEPARATOR = 0x800;
    const int HTCAPTION = 2;
    const int DockCommandId = 0x7A10; // system command ids must have the low 4 bits clear

    [DllImport("user32.dll")] static extern IntPtr GetSystemMenu(IntPtr hWnd, bool revert);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr hMenu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Adds "Dock to Main Window" to a floating window's system menu and reports its selection.</summary>
    sealed class SysMenuHook : NativeWindow
    {
        readonly Form _form;
        readonly Action _dock;

        public SysMenuHook(Form form, Action dock)
        {
            _form = form; _dock = dock;
            form.HandleCreated += OnCreated;
            if (form.IsHandleCreated) Attach();
        }

        void OnCreated(object? s, EventArgs e) => Attach();

        void Attach()
        {
            if (Handle != IntPtr.Zero) ReleaseHandle();
            AssignHandle(_form.Handle);
            var menu = GetSystemMenu(_form.Handle, false);
            if (menu == IntPtr.Zero) return;
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenu(menu, MF_STRING, (UIntPtr)DockCommandId, "Dock to Main Window");
        }

        public void Detach()
        {
            _form.HandleCreated -= OnCreated;
            if (Handle != IntPtr.Zero) ReleaseHandle();
            if (_form.IsHandleCreated) GetSystemMenu(_form.Handle, true); // revert to the stock menu
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SYSCOMMAND && ((int)m.WParam.ToInt64() & 0xFFF0) == DockCommandId)
            {
                _form.BeginInvoke(_dock);
                return;
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>Working areas with the DPI of each monitor (physical pixels).</summary>
    internal static ScreenInfo[] Screens() => Screen.AllScreens.Select(sc => new ScreenInfo(sc.WorkingArea, MonitorDpi(sc.WorkingArea))).ToArray();

    internal static ScreenInfo PrimaryScreen()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        return new ScreenInfo(wa, MonitorDpi(wa));
    }

    static int MonitorDpi(Rectangle area)
    {
        try
        {
            var h = MonitorFromPoint(new Point(area.X + area.Width / 2, area.Y + area.Height / 2), 2 /* nearest */);
            if (h != IntPtr.Zero && GetDpiForMonitor(h, 0, out var x, out _) == 0 && x > 0) return (int)x;
        }
        catch (Exception) { /* old OS / missing export */ }
        return 96;
    }

    /// <summary>
    /// Places a form from saved bounds: target monitor and size are worked out by <see cref="ScreenFit.Restore"/>; the location is set
    /// first and the handle created so the form is on the target monitor (and its DPI has settled) before the size is applied.
    /// </summary>
    internal static void ApplyBounds(Form f, Rectangle saved, int savedDpi)
    {
        var rect = ScreenFit.Restore(saved, savedDpi, Screens(), PrimaryScreen());
        f.StartPosition = FormStartPosition.Manual;
        f.Location = rect.Location;
        _ = f.Handle;
        f.Location = rect.Location;
        f.Size = rect.Size;
    }

    /// <summary>Optional hook for windows whose content cannot survive a handle recreation (WebView2).</summary>
    internal interface IReparentAware
    {
        void BeforeReparent();
        void AfterReparent();
    }

    sealed class FloatState
    {
        public bool ShowIcon, KeyPreview, MinimizeBox, MaximizeBox, ShowInTaskbar;
        public Icon? Icon;
        public SysMenuHook? Hook;
        public Size SizeAtBegin;
        public bool Moving;
    }

    readonly MainForm _host;
    readonly DocumentTabStrip _tabs;
    readonly WindowLayoutStore _store;
    readonly Dictionary<Form, string> _keys = new();
    readonly Dictionary<Form, string> _titles = new();
    readonly Dictionary<Form, FloatState> _float = new();
    readonly List<Form> _order = new();
    Dictionary<string, SavedWindow> _saved = new();
    string? _moduleKey;

    public WindowManager(MainForm host, DocumentTabStrip tabs, WindowLayoutStore store)
    {
        _host = host; _tabs = tabs; _store = store;
    }

    public IReadOnlyList<Form> Floating => _order;
    public event Action? FloatingChanged;
    public bool IsFloating(Form f) => _float.ContainsKey(f);
    public string TitleOf(Form f) => _titles.TryGetValue(f, out var t) ? t : f.Text;

    // ---------- layout memory ----------

    public void BeginModule(string? moduleKey)
    {
        _moduleKey = moduleKey;
        _saved = moduleKey == null ? new() : _store.LoadModule(moduleKey).ToDictionary(p => p.Key, p => p.Value);
    }

    public SavedWindow? Saved(string key) => _saved.TryGetValue(key, out var s) ? s : null;

    /// <summary>Snapshots every live window and writes the module layout. Call before the windows are closed.</summary>
    public void EndModule()
    {
        foreach (var f in _keys.Keys.ToList()) Record(f);
        if (_moduleKey != null) _store.SaveModule(_moduleKey, _saved);
        _moduleKey = null;
        _saved = new();
    }

    void Record(Form f)
    {
        if (!_keys.TryGetValue(f, out var key) || f.IsDisposed) return;
        _saved.TryGetValue(key, out var prev);
        if (_float.ContainsKey(f))
        {
            var max = f.WindowState == FormWindowState.Maximized;
            _saved[key] = new SavedWindow(true, f.WindowState == FormWindowState.Normal ? f.Bounds : f.RestoreBounds, max, f.DeviceDpi);
        }
        else
            _saved[key] = new SavedWindow(false, prev.Bounds, f.WindowState == FormWindowState.Maximized, prev.Dpi);
    }

    public SavedWindow? LoadMain() => _store.LoadMain();
    public void SaveMain(SavedWindow w) => _store.SaveMain(w);

    // ---------- registration ----------

    /// <summary>Starts tracking a document window under its layout key (call before it is shown).</summary>
    public void Register(Form w, string key)
    {
        _keys[w] = key;
        _titles[w] = w.Text;
        w.TextChanged += (_, _) => OnTextChanged(w);
        w.FormClosing += (_, e) => { if (!e.Cancel) Record(w); };
        w.FormClosed += (_, _) => OnClosed(w);
    }

    const string FloatSuffix = " — Quartermaster";
    bool _retitling;

    /// <summary>A window renamed itself (the workbench does, per rules pack): keep the remembered title and the floating caption current.</summary>
    void OnTextChanged(Form w)
    {
        if (_retitling || !_titles.ContainsKey(w) || w.IsDisposed) return;
        _retitling = true;
        try
        {
            var text = w.Text;
            if (_float.ContainsKey(w))
            {
                var title = text.EndsWith(FloatSuffix, StringComparison.Ordinal) ? text[..^FloatSuffix.Length] : text;
                _titles[w] = title;
                if (title == text) w.Text = title + FloatSuffix;
                FloatingChanged?.Invoke();
            }
            else _titles[w] = text;
            _host.SyncTabs(null);
        }
        finally { _retitling = false; }
    }

    void OnClosed(Form w)
    {
        _keys.Remove(w);
        _titles.Remove(w);
        if (_float.Remove(w, out var st)) { st.Hook?.Detach(); _order.Remove(w); FloatingChanged?.Invoke(); }
        _host.SyncTabs(w);
    }

    // ---------- floating ----------

    Rectangle DefaultBounds(Point? cursor)
    {
        var w = Math.Max(640, _host.Width * 3 / 4);
        var h = Math.Max(480, _host.Height * 3 / 4);
        if (cursor is { } c)
            return new Rectangle(c.X - w / 2, c.Y - SystemInformation.CaptionHeight / 2, w, h);
        return new Rectangle(_host.Left + (_host.Width - w) / 2, _host.Top + (_host.Height - h) / 2, w, h);
    }

    /// <summary>Makes a registered, not-yet-shown window a floating window at the saved bounds (layout restore).</summary>
    public void ShowFloating(Form w, SavedWindow saved)
    {
        if (saved.Bounds.IsEmpty) ConfigureFloating(w, DefaultBounds(null), _host.DeviceDpi, saved.Maximized);
        else ConfigureFloating(w, saved.Bounds, saved.Dpi, saved.Maximized);
        Program.AppLog("restored floating: " + TitleOf(w));
        w.Show();
    }

    /// <summary>Pops a docked window out into its own top-level window. <paramref name="cursor"/> places it under the pointer.</summary>
    public void PopOut(Form f, Point? cursor = null)
    {
        if (f.IsDisposed || _float.ContainsKey(f) || !_keys.ContainsKey(f)) return;
        var dpi = _host.DeviceDpi;
        SavedWindow s = default;
        var bounds = cursor != null ? DefaultBounds(cursor)
            : _saved.TryGetValue(_keys[f], out s) && !s.Bounds.IsEmpty ? s.Bounds : DefaultBounds(null);
        if (cursor == null && s.Dpi > 0 && !s.Bounds.IsEmpty) dpi = s.Dpi;
        (f as IReparentAware)?.BeforeReparent();
        f.SuspendLayout();
        f.Hide();
        f.WindowState = FormWindowState.Normal;
        f.MdiParent = null;
        ConfigureFloating(f, bounds, dpi, false);
        f.ResumeLayout();
        f.Show();
        (f as IReparentAware)?.AfterReparent();
        f.Activate();
        _host.SyncTabs(null);
        if (cursor != null && Control.MouseButtons == MouseButtons.Left)
            f.BeginInvoke(() => BeginMove(f)); // keep dragging the new window with the same mouse press
    }

    static void BeginMove(Form f)
    {
        if (f.IsDisposed || !f.IsHandleCreated) return;
        ReleaseCapture();
        SendMessage(f.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    void ConfigureFloating(Form f, Rectangle wanted, int wantedDpi, bool maximized)
    {
        var st = new FloatState
        {
            ShowIcon = f.ShowIcon, KeyPreview = f.KeyPreview, MinimizeBox = f.MinimizeBox, MaximizeBox = f.MaximizeBox,
            ShowInTaskbar = f.ShowInTaskbar, Icon = f.Icon,
        };
        f.MdiParent = null;
        f.Owner = null;
        f.FormBorderStyle = FormBorderStyle.Sizable;
        f.ShowInTaskbar = true;
        f.ShowIcon = true;
        f.Icon = _host.Icon;
        f.MinimizeBox = f.MaximizeBox = true;
        ApplyBounds(f, wanted, wantedDpi);
        f.Text = TitleOf(f) + " — Quartermaster";
        f.KeyPreview = true;
        f.KeyDown += FloatKeyDown;
        f.ResizeBegin += OnResizeBegin;
        f.ResizeEnd += OnResizeEnd;
        st.Hook = new SysMenuHook(f, () => Dock(f));
        _float[f] = st;
        _order.Add(f);
        if (maximized) f.WindowState = FormWindowState.Maximized;
        FloatingChanged?.Invoke();
    }

    // ---------- docking ----------

    public void Dock(Form f)
    {
        if (f.IsDisposed || !_float.TryGetValue(f, out var st)) return;
        Record(f); // remembers the floating bounds for the next pop-out
        _float.Remove(f);
        _order.Remove(f);
        st.Hook?.Detach();
        f.KeyDown -= FloatKeyDown;
        f.ResizeBegin -= OnResizeBegin;
        f.ResizeEnd -= OnResizeEnd;
        (f as IReparentAware)?.BeforeReparent();
        f.SuspendLayout();
        f.Hide();
        f.WindowState = FormWindowState.Normal;
        f.Text = TitleOf(f);
        f.ShowInTaskbar = st.ShowInTaskbar;
        f.ShowIcon = st.ShowIcon;
        f.Icon = st.Icon;
        f.KeyPreview = st.KeyPreview;
        f.MinimizeBox = st.MinimizeBox;
        f.MaximizeBox = st.MaximizeBox;
        _saved[_keys[f]] = new SavedWindow(false, _saved[_keys[f]].Bounds, true, _saved[_keys[f]].Dpi);
        _host.PrepareDock(f);
        f.ResumeLayout();
        f.Show();
        (f as IReparentAware)?.AfterReparent();
        f.Activate();
        _host.SyncTabs(null);
        FloatingChanged?.Invoke();
    }

    void OnResizeBegin(object? s, EventArgs e)
    {
        if (s is Form f && _float.TryGetValue(f, out var st)) { st.SizeAtBegin = f.Size; st.Moving = true; }
    }

    /// <summary>A move (not a resize) that ends with the pointer over the tab strip docks the window.</summary>
    void OnResizeEnd(object? s, EventArgs e)
    {
        if (s is not Form f || !_float.TryGetValue(f, out var st) || !st.Moving) return;
        st.Moving = false;
        if (f.Size != st.SizeAtBegin || !_tabs.Visible || _host.IsDisposed) return;
        var strip = _tabs.RectangleToScreen(_tabs.ClientRectangle);
        if (strip.Contains(Cursor.Position)) f.BeginInvoke(() => Dock(f));
    }

    void FloatKeyDown(object? s, KeyEventArgs e)
    {
        if (s is not Form f) return;
        if (e.KeyData == (Keys.Control | Keys.W)) { e.Handled = true; f.Close(); }
        else if (e.KeyData == Keys.F11 || e.KeyData == (Keys.Alt | Keys.Enter))
        {
            e.Handled = true;
            f.WindowState = f.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        }
    }

    public void CloseFloating()
    {
        foreach (var f in _order.ToList()) if (!f.IsDisposed) f.Close();
    }
}
