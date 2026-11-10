using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Shell;
using Quartermaster.Desktop.Theme;
using Quartermaster.Documents;
using Quartermaster.Render;
using Quartermaster.Render.Search;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Desktop.Documents;

/// <summary>
/// Document viewer (WinForms PdfViewerControl): a PDF one page at a time with its own navigation bar (page box "of N", Previous/Next,
/// Zoom out/in, Fit width, Fit page) and search-hit boxes; text files in a read-only monospace box; HTML in the system browser.
/// Pages render off the UI thread through <see cref="PdfPages"/>; stale results are dropped and disposed. Hosted by a document window
/// (and later the Rules Workbench); the <c>toolbar</c> parts (document list, Used on this page) are added by a later task.
/// </summary>
public sealed partial class DocumentViewer : UserControl, IDisposable
{
    /// <summary>Search-hit boxes: yellow at 40 % alpha.</summary>
    public static readonly Color HighlightColor = Color.FromArgb(0x66, 0xFF, 0xFF, 0x00);

    public const double MinZoom = 0.1, MaxZoom = 8.0, ZoomStep = 1.25;
    public const string OpenFailedPrefix = "This document could not be opened: ";
    public const string HtmlText = "This document opens in your web browser.";
    public const string OpenInBrowserText = "Open in browser";

    const int MaxCachedPages = 3;
    const double PageMargin = 8;
    /// <summary>Room left for the vertical scroll bar when fitting the width.</summary>
    const double ScrollBarAllowance = 18;
    static readonly TimeSpan RenderDelay = TimeSpan.FromMilliseconds(150);
    /// <summary>WinForms SystemColors.AppWorkspace, behind the page.</summary>
    static readonly IBrush Workspace = new SolidColorBrush(Color.FromRgb(0xAB, 0xAB, 0xAB));

    enum Fit { None, Width, Page }

    sealed record CachedPage(int Page, double Dpi, Bitmap Bitmap);

    readonly ModuleSession _s;
    readonly IDialogs _dialogs;
    readonly StackPanel _nav = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(4, 2), IsVisible = false };
    readonly TextBlock _pageLabel = new() { Text = "Page:", VerticalAlignment = VerticalAlignment.Center };
    readonly NumericUpDown _pageBox = new()
    {
        Minimum = 1, Maximum = 1, Value = 1, Increment = 1, FormatString = "0", Width = 80, ShowButtonSpinner = false,
        VerticalAlignment = VerticalAlignment.Center,
    };
    readonly TextBlock _ofLabel = new() { Text = "of 0", VerticalAlignment = VerticalAlignment.Center };
    readonly Button _prev = new() { Content = "<" };
    readonly Button _next = new() { Content = ">" };
    readonly Button _zoomOut = new() { Content = "Zoom out" };
    readonly Button _zoomIn = new() { Content = "Zoom in" };
    readonly Button _fitWidth = new() { Content = "Fit width" };
    readonly Button _fitPage = new() { Content = "Fit page" };
    readonly TextBlock _zoomLabel = new() { Text = "100 %", VerticalAlignment = VerticalAlignment.Center, MinWidth = 44 };
    readonly ContentControl _body = new();
    readonly ScrollViewer _scroller = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Workspace,
    };
    readonly PdfPageView _pageView = new(HighlightColor) { Margin = new Thickness(PageMargin), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
    readonly DispatcherTimer _renderTimer = new() { Interval = RenderDelay };

    readonly InitGate _docGate = new(), _renderGate = new(), _findGate = new();
    readonly List<CachedPage> _cache = new();   // most recently used last
    PdfPages? _pages;
    PdfHighlights? _highlights;
    CancellationTokenSource? _renderCts;
    TaskCompletionSource _rendering = Done();
    string? _file;
    bool _failed, _loggedFailure, _loading, _findPending, _renderPending, _syncing, _released, _disposed;
    int _page = 1;
    int _pageCount;
    string? _search;
    double _zoom = 1;
    Fit _fit = Fit.Width;
    (int Page, double Dpi)? _inFlight;
    int _staleDropped;

    /// <param name="toolbar">Adds the document list, tool items and Used on this page (DocumentViewer.Toolbar.cs).</param>
    /// <param name="dialogs">Opens HTML documents in the browser; the real <see cref="Shell.Dialogs"/> when null.</param>
    public DocumentViewer(ModuleSession s, bool toolbar, IDialogs? dialogs = null)
    {
        _s = s;
        _dialogs = dialogs ?? new Dialogs();
        Toolbar = toolbar;
        ToolTip.SetTip(_prev, "Previous page");
        ToolTip.SetTip(_next, "Next page");
        _nav.Children.Add(_pageLabel);
        _nav.Children.Add(_pageBox);
        _nav.Children.Add(_ofLabel);
        _nav.Children.Add(_prev);
        _nav.Children.Add(_next);
        _nav.Children.Add(_zoomOut);
        _nav.Children.Add(_zoomIn);
        _nav.Children.Add(_fitWidth);
        _nav.Children.Add(_fitPage);
        _nav.Children.Add(_zoomLabel);
        _scroller.Content = _pageView;

        _prev.Click += (_, _) => GoToPage(_page - 1);
        _next.Click += (_, _) => GoToPage(_page + 1);
        _pageBox.ValueChanged += (_, e) => { if (!_syncing && e.NewValue is { } v) GoToPage((int)v); };
        _zoomOut.Click += (_, _) => SetZoom(_zoom / ZoomStep, Fit.None);
        _zoomIn.Click += (_, _) => SetZoom(_zoom * ZoomStep, Fit.None);
        _fitWidth.Click += (_, _) => SetFit(Fit.Width);
        _fitPage.Click += (_, _) => SetFit(Fit.Page);
        _scroller.SizeChanged += (_, _) => { if (_fit != Fit.None) ApplyFit(debounce: true); };
        _pageView.PointerPressed += (_, _) => _pageView.Focus();
        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); RenderNow(); };
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnPreviewWheel, RoutingStrategies.Tunnel);

        var root = new DockPanel();
        if (toolbar) InitToolbar(root);   // DocumentViewer.Toolbar.cs: the toolbar row holds the navigation bar
        else
        {
            DockPanel.SetDock(_nav, Dock.Top);
            root.Children.Add(_nav);
        }
        root.Children.Add(_body);
        Content = root;
        if (toolbar) ShowText(_pdfDocs.Count == 0 ? NoPdfDocumentsText : ChooseDocumentText);
        UpdateZoomLabel();
    }

    /// <summary>The <c>toolbar</c> flag given at construction (document list, tool items and Used on this page exist only when true).</summary>
    public bool Toolbar { get; }

    public DocumentDef? Document { get; private set; }

    /// <summary>The current 1-based page.</summary>
    public int Page => _page;

    /// <summary>Pages of the open PDF; 0 for other documents and while the PDF is being read.</summary>
    public int PageCount => _pageCount;

    /// <summary>The term highlighted on the page, null when none.</summary>
    public string? SearchTerm => _search;

    public event Action<int>? PageChanged;

    /// <summary>Another document was opened (the page may be unchanged).</summary>
    public event Action? DocumentOpened;

    /// <summary>Page DIPs per PDF point (1 = 100 %). Setting it leaves Fit width / Fit page.</summary>
    public double Zoom
    {
        get => _zoom;
        set => SetZoom(value, Fit.None);
    }

    public bool FitWidth => _fit == Fit.Width;

    public bool FitPage => _fit == Fit.Page;

    /// <summary>Completes when the latest page render has been shown (or dropped); never faults. For tests.</summary>
    public Task Rendering => _rendering.Task;

    // ---------- test and host access ----------

    internal StackPanel NavigationBar => _nav;
    internal TextBlock PageLabel => _pageLabel;
    internal NumericUpDown PageBox => _pageBox;
    internal TextBlock OfLabel => _ofLabel;
    internal Button PrevButton => _prev;
    internal Button NextButton => _next;
    internal Button ZoomOutButton => _zoomOut;
    internal Button ZoomInButton => _zoomIn;
    internal Button FitWidthButton => _fitWidth;
    internal Button FitPageButton => _fitPage;
    internal TextBlock ZoomLabel => _zoomLabel;
    internal PdfPageView PageView => _pageView;
    internal ScrollViewer Scroller => _scroller;
    internal Control? Body => _body.Content as Control;

    /// <summary>The texts of the message shown in the body (failure, HTML note).</summary>
    internal IReadOnlyList<string> BodyTexts => Body switch
    {
        TextBlock t => new[] { t.Text ?? "" },
        Panel p => p.Children.OfType<TextBlock>().Select(t => t.Text ?? "").ToList(),
        _ => Array.Empty<string>(),
    };

    /// <summary>The DPI last asked for (72 x zoom x render scaling, before the pixel cap).</summary>
    internal double LastRequestedDpi { get; private set; }

    /// <summary>Renders whose bitmap arrived after a newer request and was disposed unseen.</summary>
    internal int StaleDropped => Volatile.Read(ref _staleDropped);

    /// <summary>Replaces <see cref="PdfPages.RenderAsync"/> (tests): page, requested DPI, cancellation.</summary>
    internal Func<int, double, CancellationToken, Task<SKBitmap>>? RenderOverride { get; set; }

    /// <summary>A PDF is being read, a render is waiting for its delay, or hit boxes are being found.</summary>
    internal bool Busy => _loading || _renderPending || _findPending;

    internal bool RenderInFlight => _inFlight != null;

    internal string StateForTests =>
        $"loading={_loading} renderPending={_renderPending} find={_findPending} inFlight={_inFlight} rendering={_rendering.Task.Status} zoom={_zoom} cache={string.Join(",", _cache.Select(c => c.Page + "@" + c.Dpi))}";

    /// <summary>Released its PDF and bitmaps (detached from the visual tree or disposed).</summary>
    internal bool Released => _released;

    // ---------- opening ----------

    /// <summary>Opens the document at a page; <paramref name="search"/> highlights a term on PDF pages.</summary>
    public void Open(DocumentDef d, int page = 1, string? search = null)
    {
        if (_disposed) return;
        page = Math.Clamp(page, 1, 9999);
        if (Equals(Document, d) && d.Kind == DocumentKind.Pdf && _file != null && !_failed)
        {
            GoToPage(page, search);
            return;
        }
        _search = Normalize(search);
        _loggedFailure = false;
        var newDoc = !Equals(Document, d);
        Document = d;
        var changed = page != _page;
        _page = page;
        CloseDocument();
        try
        {
            _file = DocumentCache.Extract(_s.Archive, _s.CacheDir, d.EntryName);
            switch (d.Kind)
            {
                case DocumentKind.Text:
                    ShowBody(new TextBox
                    {
                        IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap,
                        FontFamily = new FontFamily(ClassicFonts.Mono), FontSize = ClassicFonts.MonoSizePx, Text = File.ReadAllText(_file),
                        [ScrollViewer.HorizontalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto,
                        [ScrollViewer.VerticalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto,
                    });
                    break;
                case DocumentKind.Html:
                    ShowHtml();
                    _dialogs.OpenInBrowser(TopLevel.GetTopLevel(this), _file);
                    break;
                default:
                    StartLoad(_file);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _file = null;
            Fail(OpenFailedPrefix + ex.Message, ex);
            if (newDoc) DocumentOpened?.Invoke();
            return;
        }
        if (newDoc) DocumentOpened?.Invoke();
        if (changed) PageChanged?.Invoke(_page);
    }

    /// <summary>Moves to a 1-based page (clamped); <paramref name="search"/> as in <see cref="Open"/> (a plain page move clears it).</summary>
    public void GoToPage(int page, string? search = null)
    {
        if (_disposed) return;
        page = Math.Clamp(page, 1, _pageCount > 0 ? _pageCount : 9999);
        var changed = page != _page;
        _page = page;
        _search = Normalize(search);
        SyncBar();
        if (_pages != null)
        {
            _scroller.Offset = new Vector(_scroller.Offset.X, 0);
            // Pages can differ in size or orientation (landscape charts, /Rotate): re-fit or resize for the new page.
            if (_fit != Fit.None) ApplyFit(debounce: false);
            else UpdatePageSize();
            ShowPage(debounce: false);
            StartFind();
        }
        if (changed) PageChanged?.Invoke(_page);
    }

    static string? Normalize(string? search) => string.IsNullOrWhiteSpace(search) ? null : search.Trim();

    void StartLoad(string file)
    {
        _loading = true;
        var ticket = _docGate.Next();
        ShowBody(_scroller);
        Task.Run(() =>
        {
            var pages = PdfPages.Open(file);
            PdfHighlights? hits = null;
            try { hits = PdfHighlights.Open(file); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                System.Diagnostics.Trace.WriteLine("document viewer: no search boxes for " + file + ": " + ex.Message);
            }
            return (pages, hits);
        }).ContinueWith(t =>
        {
            var error = t.Exception?.GetBaseException();   // observed here: never unobserved
            var result = t.IsCompletedSuccessfully ? t.Result : default;
            Dispatcher.UIThread.Post(() => PdfLoaded(ticket, result.pages, result.hits, error));
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    void PdfLoaded(int ticket, PdfPages? pages, PdfHighlights? hits, Exception? error)
    {
        if (!_docGate.IsCurrent(ticket) || _released || _disposed)
        {
            if (pages != null || hits != null) DisposeQuietly(pages, hits);
            return;
        }
        if (pages == null)
        {
            _loading = false;
            _failed = true;
            Fail(OpenFailedPrefix + (error?.Message ?? PdfPages.CantOpenAsScanMessage), error);
            return;
        }
        _pages = pages;
        _highlights = hits;
        _pageCount = pages.PageCount;
        _nav.IsVisible = true;
        var clamped = Math.Clamp(_page, 1, _pageCount);
        var changed = clamped != _page;
        _page = clamped;
        SyncBar();
        // Lay out with the navigation bar shown before fitting, so the first render is at the final zoom.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_docGate.IsCurrent(ticket) || _released) return;
            _loading = false;
            if (_fit != Fit.None) ApplyFit(debounce: false);
            UpdatePageSize();
            ShowPage(debounce: false);
            StartFind();
            // A freshly opened document reacts to Page Down / zoom keys without a click first.
            if (TopLevel.GetTopLevel(this) is { } top && !(top.FocusManager?.GetFocusedElement() is Visual f && Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(this, f)))
                _pageView.Focus();
        }, DispatcherPriority.Background);
        if (changed) PageChanged?.Invoke(_page);
    }

    void ShowHtml()
    {
        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = HtmlText, TextWrapping = TextWrapping.Wrap });
        var open = new Button { Content = OpenInBrowserText, HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) => { if (_file != null) _dialogs.OpenInBrowser(TopLevel.GetTopLevel(this), _file); };
        panel.Children.Add(open);
        ShowBody(panel);
    }

    void Fail(string message, Exception? ex)
    {
        System.Diagnostics.Trace.WriteLine("document viewer failed: " + ex);
        if (!_loggedFailure) _s.Log.Add(DiagnosticSeverity.Warning, "document", message, Document?.Title ?? "");   // once per Open
        _loggedFailure = true;
        ShowText(message);
    }

    void ShowText(string text) => ShowBody(new TextBlock { Text = text, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap });

    void ShowBody(Control c)
    {
        _nav.IsVisible = _pages != null;
        _body.Content = c;
    }

    void SyncBar()
    {
        _syncing = true;
        try
        {
            _pageBox.Maximum = Math.Max(1, _pageCount);
            _pageBox.Value = Math.Clamp(_page, 1, Math.Max(1, _pageCount));
            _ofLabel.Text = "of " + _pageCount;
            _prev.IsEnabled = _page > 1;
            _next.IsEnabled = _page < _pageCount;
        }
        finally { _syncing = false; }
    }

    // ---------- zoom ----------

    void SetFit(Fit fit)
    {
        _fit = fit;
        ApplyFit(debounce: true);
        UpdateZoomLabel();
    }

    void SetZoom(double zoom, Fit fit)
    {
        if (!double.IsFinite(zoom)) return;
        _fit = fit;
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _zoom = zoom;
        UpdatePageSize();
        UpdateZoomLabel();
        ShowPage(debounce: true);
    }

    void ApplyFit(bool debounce)
    {
        var z = _fit switch { Fit.Width => FitWidthZoom(), Fit.Page => FitPageZoom(), _ => null };
        if (z is not { } zoom) return;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        UpdatePageSize();
        UpdateZoomLabel();
        ShowPage(debounce);
    }

    SizeD? CurrentPagePoints => _pages?.PageSize(_page);

    /// <summary>The zoom that fits the page's width into the viewer (null before a PDF is read or laid out).</summary>
    internal double? FitWidthZoom()
    {
        if (CurrentPagePoints is not { } p || _scroller.Bounds.Width < 1 || p.W <= 0) return null;
        return Math.Clamp((_scroller.Bounds.Width - ScrollBarAllowance - 2 * PageMargin) / p.W, MinZoom, MaxZoom);
    }

    double? FitPageZoom()
    {
        if (CurrentPagePoints is not { } p || _scroller.Bounds.Width < 1 || _scroller.Bounds.Height < 1 || p.W <= 0 || p.H <= 0) return null;
        return Math.Clamp(Math.Min((_scroller.Bounds.Width - 2 * PageMargin) / p.W, (_scroller.Bounds.Height - 2 * PageMargin) / p.H), MinZoom, MaxZoom);
    }

    void UpdatePageSize()
    {
        if (CurrentPagePoints is not { } p) return;
        _pageView.Width = Math.Max(1, p.W * _zoom);
        _pageView.Height = Math.Max(1, p.H * _zoom);
    }

    void UpdateZoomLabel() => _zoomLabel.Text = $"{Math.Round(_zoom * 100)} %";

    void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_pages == null || e.Source is TextBox) return;
        var primary = (e.KeyModifiers & (ShortcutGestures.IsMac ? KeyModifiers.Meta : KeyModifiers.Control)) != 0;
        if (primary)
        {
            switch (e.Key)
            {
                case Key.OemPlus or Key.Add: SetZoom(_zoom * ZoomStep, Fit.None); break;
                case Key.OemMinus or Key.Subtract: SetZoom(_zoom / ZoomStep, Fit.None); break;
                case Key.D0 or Key.NumPad0: SetFit(Fit.Width); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.PageUp: GoToPage(_page - 1); break;
            case Key.PageDown: GoToPage(_page + 1); break;
            case Key.Home: GoToPage(1); break;
            case Key.End: GoToPage(_pageCount); break;
            default: return;
        }
        e.Handled = true;
    }

    void OnPreviewWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_pages == null || e.Delta.Y == 0) return;
        if ((e.KeyModifiers & (ShortcutGestures.IsMac ? KeyModifiers.Meta : KeyModifiers.Control)) == 0) return;   // plain wheel scrolls
        SetZoom(e.Delta.Y > 0 ? _zoom * ZoomStep : _zoom / ZoomStep, Fit.None);
        e.Handled = true;
    }

    // ---------- rendering ----------

    double RenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    static bool SameDpi(double a, double b) => Math.Abs(a - b) < 0.5;

    /// <summary>Shows the current page (a cached picture, scaled, while a sharper one renders) and asks for a render when needed.</summary>
    void ShowPage(bool debounce)
    {
        if (_pages == null || _released) return;
        var page = _page;
        var size = _pages.PageSize(page);
        var dpi = PdfPages.ClampDpi(size, 72 * _zoom * RenderScaling);
        var cached = _cache.LastOrDefault(c => c.Page == page);
        if (cached != null)
        {
            Touch(cached);
            if (!ReferenceEquals(_pageView.Bitmap, cached.Bitmap)) _pageView.Show(page, cached.Bitmap);
        }
        else if (_pageView.ShownPage != page) _pageView.Show(page, null);

        if ((cached != null && SameDpi(cached.Dpi, dpi)) || (_inFlight is { } f && f.Page == page && SameDpi(f.Dpi, dpi) && !_renderPending))
        {
            if (cached != null && SameDpi(cached.Dpi, dpi)) CancelRender();   // already sharp: anything still running is stale
            _renderTimer.Stop();
            _renderPending = false;
            CompleteRenderingIfIdle();
            return;
        }
        if (_rendering.Task.IsCompleted) _rendering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (debounce)
        {
            _renderPending = true;
            _renderTimer.Stop();
            _renderTimer.Start();
        }
        else
        {
            _renderTimer.Stop();
            RenderNow();
        }
    }

    void CancelRender()
    {
        _renderGate.Invalidate();
        _inFlight = null;
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = null;
    }

    void RenderNow()
    {
        _renderPending = false;
        if (_pages == null || _released || _disposed) { CompleteRenderingIfIdle(); return; }
        var page = _page;
        var size = _pages.PageSize(page);
        var dpi = 72 * _zoom * RenderScaling;
        var clamped = PdfPages.ClampDpi(size, dpi);
        var cached = _cache.LastOrDefault(c => c.Page == page);
        if (cached != null && SameDpi(cached.Dpi, clamped)) { CancelRender(); CompleteRenderingIfIdle(); return; }
        CancelRender();
        LastRequestedDpi = dpi;
        var ticket = _renderGate.Next();
        _renderCts = new CancellationTokenSource();
        _inFlight = (page, clamped);
        Task<SKBitmap> task;
        try { task = RenderOverride?.Invoke(page, dpi, _renderCts.Token) ?? _pages.RenderAsync(page, dpi, _renderCts.Token); }
        catch (ObjectDisposedException) { _inFlight = null; CompleteRenderingIfIdle(); return; }
        task.ContinueWith(t => Rendered(t, ticket, page, clamped), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Thread pool: turns the page picture into an Avalonia bitmap, or drops it when a newer request came in meanwhile.</summary>
    void Rendered(Task<SKBitmap> t, int ticket, int page, double dpi)
    {
        if (!t.IsCompletedSuccessfully)
        {
            var error = t.Exception?.GetBaseException();   // observed
            Dispatcher.UIThread.Post(() =>
            {
                if (!_renderGate.IsCurrent(ticket)) return;
                _inFlight = null;
                if (error != null) System.Diagnostics.Trace.WriteLine("document viewer: page " + page + " did not render: " + error.Message);
                CompleteRenderingIfIdle();
            });
            return;
        }
        var sk = t.Result;
        Bitmap? bitmap = null;
        try
        {
            if (_renderGate.IsCurrent(ticket) && !_released) bitmap = ToAvalonia(sk);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("document viewer: page " + page + " could not be shown: " + ex.Message);
        }
        finally { sk.Dispose(); }
        if (bitmap == null) Interlocked.Increment(ref _staleDropped);
        Dispatcher.UIThread.Post(() =>
        {
            if (bitmap == null || !_renderGate.IsCurrent(ticket) || _released)
            {
                if (bitmap != null) { bitmap.Dispose(); Interlocked.Increment(ref _staleDropped); }
                if (_renderGate.IsCurrent(ticket)) { _inFlight = null; CompleteRenderingIfIdle(); }
                return;
            }
            _inFlight = null;
            Install(page, dpi, bitmap);
            CompleteRenderingIfIdle();
        });
    }

    static Bitmap ToAvalonia(SKBitmap sk)
    {
        SKBitmap? copy = null;
        try
        {
            var src = sk;
            if (sk.ColorType is not (SKColorType.Bgra8888 or SKColorType.Rgba8888))
            {
                copy = sk.Copy(SKColorType.Bgra8888) ?? throw new InvalidOperationException("unsupported page picture format");
                src = copy;
            }
            var format = src.ColorType == SKColorType.Bgra8888 ? PixelFormat.Bgra8888 : PixelFormat.Rgba8888;
            var alpha = src.AlphaType switch
            {
                SKAlphaType.Unpremul => AlphaFormat.Unpremul,
                SKAlphaType.Opaque => AlphaFormat.Opaque,
                _ => AlphaFormat.Premul,
            };
            return new Bitmap(format, alpha, src.GetPixels(), new PixelSize(src.Width, src.Height), new Vector(96, 96), src.RowBytes);
        }
        finally { copy?.Dispose(); }
    }

    void Install(int page, double dpi, Bitmap bitmap)
    {
        var old = _cache.FirstOrDefault(c => c.Page == page);
        if (old != null) _cache.Remove(old);
        _cache.Add(new CachedPage(page, dpi, bitmap));
        if (page == _page) _pageView.Show(page, bitmap);
        if (old != null && !ReferenceEquals(old.Bitmap, _pageView.Bitmap)) old.Bitmap.Dispose();
        while (_cache.Count > MaxCachedPages)
        {
            var victim = _cache.FirstOrDefault(c => !ReferenceEquals(c.Bitmap, _pageView.Bitmap));
            if (victim == null) break;
            _cache.Remove(victim);
            victim.Bitmap.Dispose();
        }
    }

    void Touch(CachedPage c)
    {
        _cache.Remove(c);
        _cache.Add(c);
    }

    void CompleteRenderingIfIdle()
    {
        if (!_renderPending && _inFlight == null) _rendering.TrySetResult();
    }

    static TaskCompletionSource Done()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    // ---------- search boxes ----------

    void StartFind()
    {
        var ticket = _findGate.Next();
        var hits = _highlights;
        var term = _search;
        var page = _page;
        _pageView.SetHighlights(Array.Empty<RectD>());
        if (hits == null || term == null) { _findPending = false; return; }
        _findPending = true;
        Task.Run(() => hits.Find(page, term)).ContinueWith(t =>
        {
            var boxes = t.IsCompletedSuccessfully ? t.Result : Array.Empty<RectD>();
            _ = t.Exception;   // observed
            Dispatcher.UIThread.Post(() =>
            {
                if (!_findGate.IsCurrent(ticket) || _released) return;
                _findPending = false;
                _pageView.SetHighlights(boxes);
                if (boxes.Count > 0)
                {
                    var first = boxes[0];
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_findGate.IsCurrent(ticket) && !_released) _pageView.BringIntoView(_pageView.BoxRect(first).Inflate(24));
                    }, DispatcherPriority.Background);
                }
            });
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    // ---------- lifetime ----------

    /// <summary>Drops the open document: stale work is invalidated, the PDF and every bitmap released.</summary>
    void CloseDocument()
    {
        _docGate.Invalidate();
        _findGate.Invalidate();
        CancelRender();
        _renderTimer.Stop();
        _renderPending = _loading = _findPending = false;
        _pageView.Show(0, null);
        _pageView.SetHighlights(Array.Empty<RectD>());
        foreach (var c in _cache) c.Bitmap.Dispose();
        _cache.Clear();
        var (pages, hits) = (_pages, _highlights);
        _pages = null;
        _highlights = null;
        _pageCount = 0;
        _failed = false;
        _file = null;
        _nav.IsVisible = false;
        SyncBar();
        _rendering.TrySetResult();
        if (pages != null || hits != null) DisposeQuietly(pages, hits);
    }

    /// <summary>
    /// Disposes the PDF objects on the thread pool, never on the UI thread: PdfPages waits up to 2 s for a render and PdfHighlights
    /// waits for a running Find. Faults are traced and observed.
    /// </summary>
    static void DisposeQuietly(PdfPages? pages, PdfHighlights? hits)
    {
        void Run()
        {
            try { pages?.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine(ex.Message); }
            try { hits?.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine(ex.Message); }
        }
        Task.Run(Run).ContinueWith(t => System.Diagnostics.Trace.WriteLine("document viewer dispose: " + t.Exception?.GetBaseException().Message),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        if (_top != null) _top.ScalingChanged += OnScalingChanged;
        if (!_released || _disposed) return;
        _released = false;
        // Back in a window: read the PDF again at the same page.
        if (Document is { Kind: DocumentKind.Pdf } && _file is { } file && !_failed) StartLoad(file);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_top != null) _top.ScalingChanged -= OnScalingChanged;
        _top = null;
        base.OnDetachedFromVisualTree(e);
        Release();
    }

    TopLevel? _top;

    void OnScalingChanged(object? sender, EventArgs e) => ShowPage(debounce: true);

    /// <summary>Releases the PDF and bitmaps but keeps the document, page and term, so a re-attach can show them again.</summary>
    void Release()
    {
        if (_released) return;
        _released = true;
        var (file, failed) = (_file, _failed);
        CloseDocument();
        (_file, _failed) = (file, failed);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Release();
        _disposed = true;
        _renderTimer.Stop();
    }
}
