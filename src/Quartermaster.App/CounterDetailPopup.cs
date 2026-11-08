using Quartermaster.Core;
using Quartermaster.Render;
using SkiaSharp.Views.Desktop;

namespace Quartermaster.App;

/// <summary>
/// Borderless, non-activating window listing every piece of a stack at 100% with its name and properties. It is mouse-transparent except over
/// its ledger value lines (which show their breakdown on hover) and is never placed under the cursor. The content is rendered once with Skia into a bitmap.
/// </summary>
sealed class CounterDetailPopup : Form
{
    Bitmap? _image;
    // Ledger value lines: pixel rectangle -> breakdown, shown with the shared tip on hover. Everywhere else the popup is click-through.
    readonly List<(Rectangle Rect, Breakdown Tip)> _hot = new();
    readonly BreakdownTip _tip = new();

    /// <summary>True when the popup has ledger lines that can be hovered (it then takes mouse input).</summary>
    public bool Interactive => _hot.Count > 0;
    public event Action? PointerEntered;
    public event Action? PointerLeft;

    CounterDetailPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        // WM_NCHITTEST -> HTTRANSPARENT everywhere but on the ledger lines, so a click or drag over the rest of the popup reaches the map.
        if (m.Msg == 0x0084 && !OnHotLine(m.LParam)) { m.Result = new IntPtr(-1); return; }
        base.WndProc(ref m);
    }

    bool OnHotLine(IntPtr lParam)
    {
        if (_hot.Count == 0) return false;
        var v = unchecked((int)(long)lParam);
        var p = PointToClient(new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF)));
        foreach (var (rect, _) in _hot) if (rect.Contains(p)) return true;
        return false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_image != null) e.Graphics.DrawImageUnscaled(_image, 0, 0);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        PointerEntered?.Invoke();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _tip.Hide();
        PointerLeft?.Invoke();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        HoverAt(e.Location);
    }

    /// <summary>The hover code path: shows the breakdown of the ledger line under <paramref name="p"/> (client pixels), else hides it.</summary>
    void HoverAt(Point p)
    {
        foreach (var (rect, tip) in _hot)
            if (rect.Contains(p)) { _tip.Show(this, new Point(rect.Right + 6, rect.Top), tip); return; }
        _tip.Hide();
    }

    /// <summary>Smoke: hovers the first ledger line through the same path as the mouse. False when there is none.</summary>
    public bool SmokeHoverFirstLine()
    {
        if (_hot.Count == 0) return false;
        HoverAt(new Point(_hot[0].Rect.Left + 2, _hot[0].Rect.Top + 2));
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _image?.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }

    public void ShowAt(Point screen)
    {
        // Never under the cursor: a click or drag right after the hover must land on the map, not on the popup.
        Location = PopupPlacement.Place(screen, Size, Screen.FromPoint(screen).WorkingArea);
        Show();
    }

    public static CounterDetailPopup Create(ModuleSession s, GameState state, StackInstance stack, PieceRenderer pieces, float scale = 1f)
    {
        var layout = CounterPopupLayout.Build(s, state, stack, pieces);
        var bmp = layout.RenderBitmap(scale);
        var form = new CounterDetailPopup { _image = bmp.ToBitmap() };
        bmp.Dispose();
        form.ClientSize = form._image.Size;
        foreach (var line in layout.HotLines)
        {
            var r = line.PixelRect(scale);
            form._hot.Add((new Rectangle(r.Left, r.Top, r.Width, r.Height), line.Tip));
        }
        return form;
    }
}
