using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Turns;

/// <summary>
/// The replay bar of a received turn, docked above the log (WinForms ReplayBar): Next, Play all, Stop, the step counter and the log line just
/// replayed. Play all steps on a timer so each line shows and Stop can pause it. Renders <see cref="TurnReplayModel"/>; owns no game logic.
/// </summary>
public sealed class ReplayBar : UserControl
{
    readonly Button _next = Make("Next", "replay.next");
    readonly Button _playAll = Make("Play all", "replay.playAll");
    readonly Button _stop = Make("Stop", "replay.stop");
    readonly TextBlock _title = new() { FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 3, 0) };
    readonly TextBlock _step = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 3, 0) };
    readonly TextBlock _line = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(TurnUiModel.MaxPlayStepMs) };
    TurnReplayModel? _model;

    static Button Make(string text, string name) =>
        new() { Content = text, Name = name, MinWidth = 70, MinHeight = 24, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    public ReplayBar()
    {
        IsVisible = false;
        Name = "ReplayBar";
        Background = new SolidColorBrush(Color.FromRgb(225, 236, 250));
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(4, 3, 4, 0) };
        foreach (var c in new Control[] { _title, _next, _playAll, _stop, _step }) top.Children.Add(c);
        var panel = new DockPanel { MinHeight = 56 };
        _line.Height = 22;
        DockPanel.SetDock(_line, Dock.Bottom);
        panel.Children.Add(_line);   // its own row: one line, ellipsis, full text in the tooltip
        panel.Children.Add(top);
        Content = panel;
        _next.Click += (_, _) => Act(() => { _timer.Stop(); _model?.Next(); });
        _playAll.Click += (_, _) => Act(() =>
        {
            if (_model is not { Done: false } m) { Sync(); return; }
            if (_timer.IsEnabled)
            {
                // Second click ("Finish"): the rest replays at once.
                _timer.Stop();
                m.PlayAll();
                Sync();
                return;
            }
            _timer.Interval = TimeSpan.FromMilliseconds(TurnUiModel.PlayStepMs(m.Count));
            _timer.Start();
            Sync();
        });
        _stop.Click += (_, _) => Act(() => { _timer.Stop(); _model?.Stop(); Sync(); });
        _timer.Tick += (_, _) => Act(() =>
        {
            if (_model is not { Done: false } m) { _timer.Stop(); Sync(); return; }
            m.NextShown();   // steps that write no log line take no time of their own
            if (m.Done) _timer.Stop();
            Sync();
        });
    }

    /// <summary>A step threw something unexpected (the model reports replay failures itself): the owner shows it.</summary>
    public event Action<Exception>? Failed;

    /// <summary>The model whose turn is shown (null hides the bar).</summary>
    public TurnReplayModel? Model => _model;

    /// <summary>True while Play all is stepping.</summary>
    public bool Playing => _timer.IsEnabled;

    public string StepText => _step.Text ?? "";
    public string LineText => _line.Text ?? "";

    internal Button NextButton => _next;
    internal Button PlayAllButton => _playAll;
    internal Button StopButton => _stop;

    public void Bind(TurnReplayModel? model)
    {
        _timer.Stop();
        if (_model != null) _model.Changed -= Sync;
        _model = model;
        if (model != null)
        {
            model.Changed += Sync;
            _title.Text = $"Turn from {TurnUiModel.ShownName(model.Turn.FromName)}:";
        }
        IsVisible = model != null;
        Sync();
    }

    void Act(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _timer.Stop();
            Failed?.Invoke(ex);
        }
    }

    void Sync()
    {
        var m = _model;
        var running = m is { Done: false };
        _next.IsEnabled = running;
        _playAll.IsEnabled = running;
        _playAll.Content = running && _timer.IsEnabled ? "Finish" : "Play all";   // while playing, a click finishes at once
        _stop.IsEnabled = running && _timer.IsEnabled;
        _step.Text = m == null ? "" : TurnUiModel.StepText(m.Step, m.Count);
        _line.Text = m?.CurrentLine ?? (m == null ? "" : "Press Next or Play all to replay the turn.");
        ToolTip.SetTip(_line, _line.Text);
    }
}
