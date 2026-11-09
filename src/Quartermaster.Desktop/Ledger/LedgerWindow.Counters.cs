using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Quartermaster.Core;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Ledgers;

/// <summary>
/// Counter pictures in the Ledger (WinForms LedgerWindow.Counters): a 64×48 picture at the left of each unit row's Name cell (unit rows 52 px
/// while Show counters is on), drawn lazily on a worker for the rows on screen first (<see cref="CounterThumbnails"/>, at most 600 kept), a big
/// preview on hover (<see cref="CounterPreview"/>) and double-click to centre the piece on its map. Pictures come only from the player's view;
/// holder and group rows have none.
/// </summary>
public sealed partial class LedgerWindow
{
    static readonly PixelSize ImageSize = new(CounterImages.RowSize.Width, CounterImages.RowSize.Height);
    static readonly IBrush PlaceholderBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0));   // Silver
    const int ImageLeft = 2;
    const int CompactRowHeight = LedgerViewRules.DefaultRowHeight;
    CounterThumbnails _thumbs = null!;
    CounterPreview _preview = null!;
    Action<string> _pictureReady = null!;

    bool CountersOn => _showCounters.IsChecked == true;
    int NameImagePad => CountersOn ? ImageSize.Width + ImageLeft + 6 : 0;

    /// <summary>Only unit rows need room for a picture: group headers and holder rows stay compact.</summary>
    double RowHeightFor(LedgerRowEx r) =>
        !CountersOn ? CompactRowHeight
        : r.PieceId != null ? LedgerViewRules.RowHeight(true)
        : CompactRowHeight + (r.IsGroupHeader ? 4 : 0);

    internal CounterThumbnails Thumbnails => _thumbs;
    internal CounterPreview Preview => _preview;

    /// <summary>The counter picture of a row on screen; null while it is drawn, for hidden units and for holder and group rows.</summary>
    internal Bitmap? PictureOf(int row) => Need(row).Picture as Bitmap;
    internal bool PlaceholderShown(int row) => Need(row).PlaceholderShown;
    internal Rect PictureBounds(int row) => BoundsIn(Need(row).PictureBox);

    void InitCounters()
    {
        _thumbs = new CounterThumbnails(_s) { Shown = () => _live.Select(r => r.Picture) };
        _preview = new CounterPreview(_s);
        _pictureReady = id => Run(() => { foreach (var r in _live.Where(r => r.Data.PieceId == id).ToList()) r.UpdatePicture(); });
    }

    /// <summary>The rows on screen are drawn first and kept in the thumbnail cache.</summary>
    void PrioritiseShown()
    {
        if (!_closing) _thumbs.Prioritise(_live.Select(r => r.Data.PieceId).OfType<string>());
    }

    void HidePreview() => _preview?.Hide();

    /// <summary>The viewed piece behind a unit row, or null (holder, group header, hidden or gone).</summary>
    PieceInstance? RowPiece(LedgerRowEx r) =>
        r.PieceId is { } id && _s.View?.Pieces.TryGetValue(id, out var p) == true ? p : null;

    void ShowPreview(LedgerRowView r, Point at)
    {
        if (_closing || !CountersOn || RowPiece(r.Data) is null) { HidePreview(); return; }
        _preview.Show(r.PictureBox, at, r.Data.PieceId!, r.Data.Row.Name);
    }

    /// <summary>Double-click a unit row's counter picture (Show counters on): centres its piece on the map (WinForms DoubleClickCounter / OverImage).</summary>
    void DoubleClickUnit(int row)
    {
        if (_centre == null || !CountersOn || row < 0 || row >= _rows.Count || _rows[row].PieceId is not { } id || RowPiece(_rows[row]) == null) return;
        HidePreview();
        _centre(id);
    }

    /// <summary>Stops the preview and thumbnail workers and waits briefly for renders in flight, so the session can be disposed. Safe twice.</summary>
    internal void StopRendering()
    {
        _preview.Dispose();
        _thumbs.Dispose();
    }

    internal sealed partial class LedgerRowView
    {
        Image? _picture;
        Rectangle? _placeholder;
        Border? _pictureBox;

        public object? Picture => _picture?.Source;
        public bool PlaceholderShown => _placeholder?.IsVisible == true;
        public Control PictureBox => _pictureBox ?? (Control)_cells[0];

        /// <summary>The Name cell: the counter picture in the cell's left padding (WinForms paints it at Left + 2, centred), the name after it.</summary>
        Control NameCell(TextBlock text)
        {
            if (!_w.CountersOn || Data.PieceId == null || Data.IsGroupHeader) return text;
            _picture = new Image { Width = ImageSize.Width, Height = ImageSize.Height, Stretch = Stretch.None, IsHitTestVisible = false };
            _placeholder = new Rectangle
            {
                Width = ImageSize.Width, Height = ImageSize.Height, Stroke = PlaceholderBrush, StrokeThickness = 1,
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 1, 1 }, IsVisible = false, IsHitTestVisible = false,
            };
            _pictureBox = new Border
            {
                Width = ImageSize.Width, Height = ImageSize.Height, Margin = new Thickness(ImageLeft - 4, 0, 0, 0), Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Child = new Panel { Children = { _placeholder, _picture } },
            };
            // Hovering the picture shows the big counter preview (WinForms HoverCounter); leaving it hides it.
            _pictureBox.PointerEntered += (_, e) => _w.Run(() => _w.ShowPreview(this, e.GetPosition(_pictureBox)));
            _pictureBox.PointerMoved += (_, e) => _w.Run(() => _w.ShowPreview(this, e.GetPosition(_pictureBox)));
            _pictureBox.PointerExited += (_, _) => _w.HidePreview();
            _pictureBox.DoubleTapped += (_, e) => { e.Handled = true; _w.Run(() => _w.DoubleClickUnit(Index)); };
            text.Margin = new Thickness(_w.NameImagePad - 4, 0, 0, 0);   // the cell's own 4 px padding is part of the picture's room
            return new Panel { Children = { _pictureBox, text } };
        }

        /// <summary>The unit's counter from the viewed state only: a dotted placeholder until the worker has drawn it, nothing when hidden.</summary>
        public void UpdatePicture()
        {
            if (_picture == null || _placeholder == null) return;
            var drawable = _w.RowPiece(Data) != null;
            var bmp = drawable ? _w._thumbs.Get(Data.PieceId!, ImageSize, _w._pictureReady) : null;
            if (!ReferenceEquals(_picture.Source, bmp)) _picture.Source = bmp;
            _placeholder.IsVisible = drawable && bmp == null;
        }
    }
}
