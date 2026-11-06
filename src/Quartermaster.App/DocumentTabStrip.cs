using System.Windows.Forms.VisualStyles;
using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>Owner-drawn browser-style tab strip for MDI children. Pure view: owns no game logic.</summary>
sealed class DocumentTabStrip : Control
{
    const int MaxTab = 160, MinTab = 80, ArrowW = 17, CloseSize = 11;
    readonly List<Form> _tabs = new();
    readonly ToolTip _tip = new();
    readonly ContextMenuStrip _menu = new();
    Font? _bold;
    Form? _active;
    int _first;          // first visible tab (overflow scrolling)
    int _hover = -1, _hoverClose = -1, _menuTab = -1;
    string? _tipText;

    public event Action<Form>? TabActivated;
    public event Action<Form>? TabCloseRequested;
    public event Action<IReadOnlyList<Form>>? TabsCloseRequested;

    public IReadOnlyList<Form> Tabs => _tabs;
    public Form? ActiveTab => _active;

    public DocumentTabStrip()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        Dock = DockStyle.Top;
        Height = 24;
        Visible = false;
        _menu.Items.Add("&Close", null, (_, _) => { if (Tab(_menuTab) is { } f) TabCloseRequested?.Invoke(f); });
        _menu.Items.Add("Close &Others", null, (_, _) => { if (_menuTab >= 0) TabsCloseRequested?.Invoke(TabOrder.Others(_tabs, _menuTab)); });
        _menu.Items.Add("Close &All", null, (_, _) => TabsCloseRequested?.Invoke(_tabs.ToList()));
    }

    Form? Tab(int i) => i >= 0 && i < _tabs.Count ? _tabs[i] : null;

    /// <summary>Reconciles tabs with the given live children (opening order is preserved) and the active child.</summary>
    public void Sync(IEnumerable<Form> children, Form? active)
    {
        var live = children.Where(c => !c.IsDisposed).ToList();
        foreach (var gone in _tabs.Where(t => !live.Contains(t)).ToList()) Remove(gone);
        foreach (var c in live)
            if (!_tabs.Contains(c)) { _tabs.Add(c); c.TextChanged += OnTitleChanged; }
        if (active != null && _tabs.Contains(active)) _active = active;
        Visible = _tabs.Count > 0;
        EnsureActiveVisible();
        Invalidate();
    }

    void Remove(Form f)
    {
        f.TextChanged -= OnTitleChanged;
        _tabs.Remove(f);
        if (_active == f) _active = null;
        _first = Math.Clamp(_first, 0, Math.Max(0, _tabs.Count - 1));
    }

    void OnTitleChanged(object? s, EventArgs e) => Invalidate();

    // ---------- layout ----------

    bool Overflows => _tabs.Count * MinTab > ClientSize.Width - 4;
    int Available => Overflows ? ClientSize.Width - 2 * ArrowW : ClientSize.Width;
    int TabW => TabOrder.TabWidth(_tabs.Count, Available - 4, MaxTab, MinTab);
    int VisibleCount => Math.Max(1, (Available - 4) / TabW);

    Rectangle TabRect(int i) => new(2 + (i - _first) * TabW, 2, TabW, Height - 2);

    Rectangle CloseRect(int i)
    {
        var r = TabRect(i);
        return new Rectangle(r.Right - CloseSize - 5, r.Top + (r.Height - CloseSize) / 2 + 1, CloseSize, CloseSize);
    }

    Rectangle LeftArrow => new(ClientSize.Width - 2 * ArrowW, 3, ArrowW, Height - 5);
    Rectangle RightArrow => new(ClientSize.Width - ArrowW, 3, ArrowW, Height - 5);

    void EnsureActiveVisible()
    {
        if (!Overflows) { _first = 0; return; }
        var i = _active == null ? -1 : _tabs.IndexOf(_active);
        if (i >= 0)
        {
            if (i < _first) _first = i;
            else if (i >= _first + VisibleCount) _first = i - VisibleCount + 1;
        }
        _first = Math.Clamp(_first, 0, Math.Max(0, _tabs.Count - VisibleCount));
    }

    int HitTab(Point p)
    {
        if (Overflows && p.X >= ClientSize.Width - 2 * ArrowW) return -1;
        for (var i = _first; i < _tabs.Count && i < _first + VisibleCount; i++)
            if (TabRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); EnsureActiveVisible(); }

    // ---------- painting ----------

    public static string Shorten(string s) => s.Length <= 24 ? s : s[..23] + "…";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(SystemColors.Control);
        for (var i = _first; i < _tabs.Count && i < _first + VisibleCount; i++)
            if (_tabs[i] != _active) DrawTab(g, i, false);
        using (var pen = new Pen(SystemColors.ControlLightLight)) g.DrawLine(pen, 0, Height - 1, ClientSize.Width, Height - 1);
        var ai = _active == null ? -1 : _tabs.IndexOf(_active);
        if (ai >= _first && ai < _first + VisibleCount) DrawTab(g, ai, true);
        if (Overflows)
        {
            ControlPaint.DrawScrollButton(g, LeftArrow, ScrollButton.Left, _first > 0 ? ButtonState.Normal : ButtonState.Inactive);
            ControlPaint.DrawScrollButton(g, RightArrow, ScrollButton.Right, _first + VisibleCount < _tabs.Count ? ButtonState.Normal : ButtonState.Inactive);
        }
    }

    void DrawTab(Graphics g, int i, bool active)
    {
        var r = TabRect(i);
        r = active ? new Rectangle(r.X - 2, r.Y - 2, r.Width + 4, r.Height + 2) : new Rectangle(r.X, r.Y + 2, r.Width, r.Height - 2);
        if (TabRenderer.IsSupported)
            TabRenderer.DrawTabItem(g, r, "", Font, false, active ? TabItemState.Selected : TabItemState.Normal);
        else
        {
            g.FillRectangle(SystemBrushes.Control, r);
            ControlPaint.DrawBorder3D(g, r, Border3DStyle.Raised, Border3DSide.Left | Border3DSide.Top | Border3DSide.Right);
        }
        var text = new Rectangle(r.X + 6, r.Y + 2, r.Width - CloseSize - 14, r.Height - 2);
        _bold ??= new Font(Font, FontStyle.Bold);
        TextRenderer.DrawText(g, Shorten(_tabs[i].Text), active ? _bold : Font, text, SystemColors.ControlText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        var c = CloseRect(i);
        if (i == _hoverClose) ControlPaint.DrawBorder3D(g, Rectangle.Inflate(c, 2, 2), Border3DStyle.RaisedInner);
        using var pen = new Pen(SystemColors.ControlText, 1.5f);
        g.DrawLine(pen, c.Left + 2, c.Top + 2, c.Right - 3, c.Bottom - 3);
        g.DrawLine(pen, c.Right - 3, c.Top + 2, c.Left + 2, c.Bottom - 3);
    }

    // ---------- mouse ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var h = HitTab(e.Location);
        var hc = h >= 0 && CloseRect(h).Contains(e.Location) ? h : -1;
        if (h != _hover || hc != _hoverClose)
        {
            _hover = h; _hoverClose = hc;
            Invalidate();
            var t = h >= 0 ? _tabs[h].Text : null;
            if (t != _tipText) { _tipText = t; _tip.SetToolTip(this, t); }
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1 || _hoverClose != -1) { _hover = _hoverClose = -1; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (Overflows && e.Button == MouseButtons.Left)
        {
            if (LeftArrow.Contains(e.Location)) { Scroll(-1); return; }
            if (RightArrow.Contains(e.Location)) { Scroll(1); return; }
        }
        var i = HitTab(e.Location);
        if (i < 0 || Tab(i) is not { } f) return;
        switch (e.Button)
        {
            case MouseButtons.Left:
                if (CloseRect(i).Contains(e.Location)) TabCloseRequested?.Invoke(f); else TabActivated?.Invoke(f);
                break;
            case MouseButtons.Middle:
                TabCloseRequested?.Invoke(f);
                break;
            case MouseButtons.Right:
                _menuTab = i;
                TabActivated?.Invoke(f);
                _menu.Show(this, e.Location);
                break;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Overflows) Scroll(e.Delta < 0 ? 1 : -1);
    }

    void Scroll(int d)
    {
        _first = Math.Clamp(_first + d, 0, Math.Max(0, _tabs.Count - VisibleCount));
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tip.Dispose(); _menu.Dispose(); _bold?.Dispose(); }
        base.Dispose(disposing);
    }
}
