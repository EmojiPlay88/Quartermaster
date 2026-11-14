using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Quartermaster.Render;
using Quartermaster.Render.Update;

namespace Quartermaster.Desktop.Updates;

/// <summary>
/// Update now (WinForms UpdateDialog): downloads, verifies and unpacks the update under the updates folder with a progress bar and Cancel.
/// It closes itself once the download has stopped: <see cref="Staged"/> is set when the update is ready, <see cref="Cancelled"/> after Cancel,
/// <see cref="Error"/> says why otherwise (the download folder is then removed). It never starts the updater helper.
/// </summary>
public sealed class UpdateWindow : Window
{
    readonly IUpdateFeed _feed;
    readonly UpdateInfo _info;
    readonly string _updatesRoot;
    readonly CancellationTokenSource _cts = new();
    readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Height = 22 };
    readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1000, Height = 18 };
    bool _running = true;

    internal Button CancelButton { get; } = new() { Content = "Cancel", MinWidth = 75, IsCancel = true };

    public StagedUpdate? Staged { get; private set; }

    /// <summary>Plain text for the user when the download did not finish (null after Cancel or success).</summary>
    public string? Error { get; private set; }

    public bool Cancelled { get; private set; }

    /// <summary>Completes once the download has stopped and the window has closed.</summary>
    public Task Done => _done.Task;

    internal string StatusText => _status.Text ?? "";

    /// <summary>The progress bar as a fraction (0..1).</summary>
    internal double ProgressFraction => _bar.Value / 1000.0;

    public UpdateWindow(IUpdateFeed feed, UpdateInfo info, string updatesRoot)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _updatesRoot = updatesRoot ?? throw new ArgumentNullException(nameof(updatesRoot));
        Title = $"Updating to Quartermaster {AppVersion.Format(info.Version)}";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        CanMinimize = false;
        CanMaximize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _status.Text = UpdateOffer.DownloadStatus(info.AssetName.Length > 0 ? info.AssetName : UpdateChecker.ZipName(info.Version), info.ZipSize);
        var body = new StackPanel { Margin = new Thickness(10, 10, 10, 0) };
        body.Children.Add(_status);
        body.Children.Add(_bar);
        CancelButton.HorizontalAlignment = HorizontalAlignment.Right;
        CancelButton.Margin = new Thickness(8, 12, 8, 8);
        var root = new StackPanel();
        root.Children.Add(body);
        root.Children.Add(CancelButton);
        Content = root;

        CancelButton.Click += (_, _) => CancelDownload();
        Opened += (_, _) => _ = Run();
        Closing += (_, e) =>
        {
            // The close box cancels like the button; the window closes itself once the download has stopped and cleaned up.
            if (!_running) return;
            e.Cancel = true;
            CancelDownload();
        };
        Closed += (_, _) => _done.TrySetResult();
    }

    void CancelDownload()
    {
        if (!_running || !CancelButton.IsEnabled) return;
        CancelButton.IsEnabled = false;
        _status.Text = UpdateOffer.Cancelling;
        _cts.Cancel();
    }

    async Task Run()
    {
        var progress = new Progress<double>(f =>
        {
            _bar.Value = Math.Clamp(f * 1000, 0, 1000);
            if (f >= 1 && !_cts.IsCancellationRequested) _status.Text = UpdateOffer.Unpacking;
        });
        try
        {
            Staged = await Task.Run(() => UpdateInstaller.Download(_feed, _info, _updatesRoot, progress, _cts.Token));
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            Cancelled = true;
        }
        catch (OperationCanceledException ex)
        {
            Fail("The download timed out.", ex);   // without our Cancel this is a timeout
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(ex.Message, ex);   // UpdateRefused, IOException, …: the same plain path
        }
        finally
        {
            _running = false;   // always: otherwise Closing would keep cancelling and the window could never close
            _cts.Dispose();
            Close();
        }
    }

    void Fail(string message, Exception ex)
    {
        Error = message;
        System.Diagnostics.Trace.WriteLine(ex is UpdateRefused or IOException or UnauthorizedAccessException
            ? "update: download failed: " + ex.Message
            : "update: download failed unexpectedly: " + ex);
    }
}
