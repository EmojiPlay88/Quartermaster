using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Quartermaster.Desktop.Shell;

namespace Quartermaster.Desktop.Turns;

/// <summary>A small "Sending turn…" wait (WinForms MainForm.SendWaitForm) that closes when the send finishes or the time is up. The user
/// cannot close it (an application or OS shutdown still can); it checks every 100 ms.</summary>
sealed class SendWaitWindow : Window
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly Task _work;
    readonly DateTime _end;
    readonly TextBlock _label;
    bool _finished;

    public SendWaitWindow(Task work, TimeSpan limit)
    {
        _work = work;
        _end = DateTime.UtcNow + limit;
        Title = "Send Turn";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _label = new TextBlock { Text = "Sending turn…", Margin = new Thickness(24) };
        Content = _label;
        _timer.Tick += (_, _) => { if (_work.IsCompleted || DateTime.UtcNow >= _end) Finish(); };
        Opened += (_, _) => _timer.Start();
        // Only the user's own close is refused: an application or OS shutdown goes through.
        Closing += (_, e) => { if (!_finished && !e.IsProgrammatic && e.CloseReason == WindowCloseReason.WindowClosing) e.Cancel = true; };
        Closed += (_, _) => _timer.Stop();
    }

    internal string LabelText => _label.Text ?? "";

    void Finish()
    {
        _finished = true;
        _timer.Stop();
        Close();
    }

    /// <summary>Shows the wait (modal over <paramref name="owner"/>, or on its own when null) until it closes; true when the work ended within the limit.</summary>
    public async Task<bool> RunAsync(Window? owner)
    {
        if (owner != null) await this.ShowDialogTracked(owner);
        else
        {
            var closed = new TaskCompletionSource();
            Closed += (_, _) => closed.TrySetResult();
            Show();
            await closed.Task;
        }
        return _work.IsCompleted;
    }
}
