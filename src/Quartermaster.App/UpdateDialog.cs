using Quartermaster.Render;
using Quartermaster.Render.Update;

namespace Quartermaster.App;

/// <summary>
/// Update now: downloads, verifies and unpacks the update with a progress bar and Cancel. Closes with OK and <see cref="Staged"/> set once
/// the update is ready to apply; on Cancel or any problem nothing is changed (the download folder is removed) and <see cref="Error"/> says why.
/// </summary>
sealed class UpdateDialog : Form
{
    readonly IUpdateFeed _feed;
    readonly UpdateInfo _info;
    readonly string _updatesRoot;
    readonly CancellationTokenSource _cts = new();
    readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 18, Minimum = 0, Maximum = 1000, Style = ProgressBarStyle.Continuous };
    readonly Label _status = new() { Dock = DockStyle.Top, AutoSize = false, Height = 22, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
    readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, MinimumSize = new Size(75, 26) };
    bool _running = true;

    public StagedUpdate? Staged { get; private set; }

    /// <summary>Plain text for the user when the download did not finish (null after Cancel).</summary>
    public string? Error { get; private set; }

    public UpdateDialog(IUpdateFeed feed, UpdateInfo info, string updatesRoot)
    {
        _feed = feed;
        _info = info;
        _updatesRoot = updatesRoot;
        Text = $"Updating to Quartermaster {AppVersion.Format(info.Version)}";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(420, 104);

        _status.Text = $"Downloading {UpdateChecker.ZipName(info.Version)} ({info.ZipSize / 1024.0 / 1024.0:0.0} MB)…";
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 10, 10, 0) };
        body.Controls.Add(_bar);
        body.Controls.Add(_status);
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false };
        right.Controls.Add(_cancel);
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(8, 6, 8, 6) };
        buttons.Controls.Add(right);
        Controls.Add(body);
        Controls.Add(buttons);
        CancelButton = _cancel;

        _cancel.Click += (_, _) =>
        {
            _cancel.Enabled = false;
            _status.Text = "Cancelling…";
            _cts.Cancel();
        };
        Shown += (_, _) => _ = Run();
        FormClosing += (_, e) =>
        {
            // The close box cancels like the button; the dialog closes itself once the download has stopped and cleaned up.
            if (!_running) return;
            e.Cancel = true;
            if (_cancel.Enabled) _cancel.PerformClick();
        };
    }

    async Task Run()
    {
        var progress = new Progress<double>(f =>
        {
            if (IsDisposed) return;
            _bar.Value = (int)Math.Clamp(f * 1000, 0, 1000);
            if (f >= 1 && !_cts.IsCancellationRequested) _status.Text = "Checking and unpacking…";
        });
        var result = DialogResult.Abort;
        try
        {
            Staged = await Task.Run(() => UpdateInstaller.Download(_feed, _info, _updatesRoot, progress, _cts.Token));
            result = DialogResult.OK;
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            result = DialogResult.Cancel;
        }
        catch (OperationCanceledException ex)
        {
            // Without our Cancel this is the HTTP timeout.
            Fail("The download timed out.", ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Expected: UpdateRefused, IOException, HttpRequestException…; anything else gets the same plain path (the full exception is logged).
            Fail(ex.Message, ex);
        }
        finally
        {
            // Always: otherwise FormClosing would keep cancelling and the dialog could never close.
            _running = false;
            if (!IsDisposed) DialogResult = result;
        }
    }

    void Fail(string message, Exception ex)
    {
        Error = message;
        Program.AppLog(ex is UpdateRefused or IOException or UnauthorizedAccessException or HttpRequestException
            ? "update: download failed: " + ex.Message
            : "update: download failed unexpectedly: " + ex);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts.Dispose();
        base.Dispose(disposing);
    }
}
