using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Quartermaster.Desktop.Shell;

/// <summary>"Saving map as picture" (WinForms ExportProgressDialog): "Rendering...", a 0..100 bar and Cancel, which stops the render. Shown owned by the main window; closing it by hand cancels too.</summary>
sealed class ExportProgressDialog : Window
{
    readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100 };
    readonly Button _cancel = new() { Content = "Cancel", Width = 75, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
    readonly CancellationTokenSource _cts;
    bool _done;

    public ExportProgressDialog(CancellationTokenSource cts)
    {
        _cts = cts;
        Title = "Saving map as picture";
        Width = 300;
        Height = 100;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _cancel.Click += (_, _) => Cancel();
        var panel = new StackPanel { Margin = new Thickness(12, 12), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Rendering..." });
        panel.Children.Add(_bar);
        panel.Children.Add(_cancel);
        Content = panel;
        // WinForms has no control box: the title bar's close is a cancel. When the owner closes (or the export was cancelled) it just closes,
        // and the owner's close cancels the export; a veto here would block the main window's own close.
        Closing += (_, e) =>
        {
            if (_done || _cts.IsCancellationRequested) return;
            if (e.CloseReason == WindowCloseReason.OwnerWindowClosing) { Cancel(); return; }
            e.Cancel = true;
            Cancel();
        };
    }

    /// <summary>The progress shown, 0..100, and how many updates arrived (tests).</summary>
    internal int Percent => (int)_bar.Value;
    internal int Updates { get; private set; }
    internal Button CancelButton => _cancel;

    public void SetProgress(double p)
    {
        Updates++;
        _bar.Value = (int)Math.Clamp(Math.Round(p * 100), 0, 100);
    }

    void Cancel()
    {
        _cancel.IsEnabled = false;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Closes for good (the export finished).</summary>
    public void Finish()
    {
        _done = true;
        Close();
    }
}
