using Quartermaster.Core;
using Quartermaster.Render;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Quartermaster.App;

/// <summary>
/// A big hover preview of one counter (about 200 px on its long side, see <see cref="CounterImages.PreviewSize"/>) with the unit's name under it.
/// The picture is rendered fresh at that size on a worker, so it stays sharp. <see cref="Show"/> on hover, <see cref="Hide"/> on leave or scroll.
/// UI thread only; never throws.
/// </summary>
sealed class CounterPreviewTip : IDisposable
{
    const int Pad = 6;
    readonly CounterImages _src;
    readonly Popup _popup = new();
    string? _key;          // what is shown or being rendered; null when hidden
    int _generation;

    public CounterPreviewTip(CounterImages src) => _src = src;

    /// <summary>True when the preview is on screen (smoke checks).</summary>
    public bool Visible => _popup.Visible;
    public Rectangle Bounds => _popup.Bounds;

    /// <summary>Smoke: draws the popup window into a bitmap of its size (no screen copy).</summary>
    public void DrawToBitmap(Bitmap bmp) => _popup.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));

    /// <summary>Shows the piece's preview near <paramref name="screenAt"/>. Same piece and state as already shown: nothing happens.</summary>
    public void Show(PieceInstance piece, string name, Point screenAt)
    {
        try
        {
            var key = CounterImages.Key(piece, default) + "\u001f" + name;
            if (key == _key) return;
            Hide();
            _key = key;
            var gen = ++_generation;
            var copy = new PieceInstance { Id = piece.Id, TypeString = piece.TypeString, TraitStates = new List<string>(piece.TraitStates), Moved = piece.Moved };
            var screen = Screen.FromPoint(screenAt).WorkingArea;
            var size = CounterImages.PreviewSize(_src.NativeBounds(copy), new SKSizeI(screen.Width, screen.Height));
            if (_src.TryGetCached(copy, size) is { } hit) { Place(hit, name, screenAt); return; }
            Task.Run(() =>
            {
                var img = _src.Render(copy, size);   // shared cache image: never disposed here
                if (img == null) return;
                try
                {
                    if (_popup.IsDisposed || !_popup.IsHandleCreated) return;   // the handle is made in Popup's constructor
                    _popup.BeginInvoke(() => { if (gen == _generation && _key == key) Place(img, name, screenAt); });
                }
                catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    void Place(SKImage img, string name, Point screenAt)
    {
        try
        {
            _popup.SetContent(img.ToBitmap(), name);
            var size = _popup.PreferredClientSize(Pad);
            var area = Screen.FromPoint(screenAt).WorkingArea;
            var at = PreviewPlacement.Place(new SKPointI(screenAt.X, screenAt.Y), new SKSizeI(size.Width, size.Height),
                new SKRectI(area.Left, area.Top, area.Right, area.Bottom));
            _popup.Bounds = new Rectangle(at.X, at.Y, size.Width, size.Height);
            if (!_popup.Visible) _popup.ShowNoActivate();
            _popup.Invalidate();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    public void Hide()
    {
        _key = null;
        _generation++;
        try { if (!_popup.IsDisposed && _popup.Visible) _popup.Hide(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    public void Dispose()
    {
        _generation++;
        _popup.Dispose();
    }

    /// <summary>The borderless top-most window that paints the picture and the name. Never takes focus.</summary>
    sealed class Popup : Form
    {
        Bitmap? _bmp;
        string _name = "";

        public Popup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = SystemColors.Info;
            DoubleBuffered = true;
            Font = SystemFonts.StatusFont ?? Control.DefaultFont;
            CreateHandle();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8, CS_DROPSHADOW = 0x20000;
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }

        public void SetContent(Bitmap bmp, string name)
        {
            _bmp?.Dispose();
            _bmp = bmp;
            _name = name;
        }

        public Size PreferredClientSize(int pad)
        {
            var w = _bmp?.Width ?? 0;
            var h = _bmp?.Height ?? 0;
            var text = TextRenderer.MeasureText(_name, Font, new Size(Math.Max(w, 120), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return new Size(Math.Max(w, Math.Min(text.Width, 360)) + pad * 2, h + text.Height + pad * 3);
        }

        public void ShowNoActivate()
        {
            const int SW_SHOWNOACTIVATE = 4;
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
            Visible = true;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                var g = e.Graphics;
                g.Clear(BackColor);
                using (var pen = new Pen(SystemColors.InfoText)) g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
                var pad = 6;
                var top = pad;
                if (_bmp != null)
                {
                    g.DrawImage(_bmp, (ClientSize.Width - _bmp.Width) / 2, top, _bmp.Width, _bmp.Height);
                    top += _bmp.Height + pad;
                }
                TextRenderer.DrawText(g, _name, Font, new Rectangle(pad, top, ClientSize.Width - pad * 2, ClientSize.Height - top - pad / 2), SystemColors.InfoText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _bmp?.Dispose(); _bmp = null; }
            base.Dispose(disposing);
        }
    }
}
