using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Core;
using Quartermaster.Render;

// Namespace Shell, not "Players": a namespace of that name would hide Quartermaster.Core.Players (as PlayersEditor).
namespace Quartermaster.Desktop.Shell;

/// <summary>
/// Hot-seat handover screen (WinForms HandoverForm): shown in place of the main window (which is hidden with every other Quartermaster window
/// while it is up): <c>Pass to {player} ({sides})</c> and a Continue button. Closing it asks the main window to close (the usual
/// unsaved-changes prompt): a user close is cancelled and raised as <see cref="CloseRequested"/>.
/// </summary>
public sealed class HandoverWindow : Window
{
    readonly TextBlock _text = new()
    {
        FontSize = 18 * 96.0 / 72,   // WinForms 18 pt bold
        FontWeight = FontWeight.Bold,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(12),
    };
    bool _closingForReal;

    public HandoverWindow(PlayerDef player, string sidesText)
    {
        Title = "Quartermaster";
        Name = "HandoverWindow";
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        MinWidth = 360;
        MinHeight = 220;
        this.Bind(BackgroundProperty, this.GetResourceObservable("Classic.FaceBrush"));

        ContinueButton = new Button
        {
            Content = "Continue", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(20, 4, 20, 4), Margin = new Thickness(0, 12, 0, 0),
        };
        ContinueButton.Click += (_, _) => Continued?.Invoke();

        // Two equal rows: the text sits at the bottom of the top half, the button at the top of the bottom half (WinForms TableLayoutPanel).
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,*") };
        Grid.SetRow(_text, 0);
        Grid.SetRow(ContinueButton, 1);
        layout.Children.Add(_text);
        layout.Children.Add(ContinueButton);
        Content = layout;
        Opened += (_, _) => ContinueButton.Focus();
        SetPlayer(player, sidesText);
    }

    /// <summary>The Continue button (tests click it).</summary>
    internal Button ContinueButton { get; }

    public PlayerDef Player { get; private set; } = null!;

    /// <summary>The big text: <c>Pass to {player} ({sides})</c>.</summary>
    public string Message => _text.Text ?? "";

    /// <summary>The player clicked Continue; the host takes over for <see cref="Player"/> and closes this.</summary>
    public event Action? Continued;

    /// <summary>The user closed the screen; the host decides (it asks the main window to close).</summary>
    public event Action? CloseRequested;

    /// <summary>Retargets the screen (a second handover became due while this one is up).</summary>
    public void SetPlayer(PlayerDef p, string sidesText)
    {
        Player = p;
        _text.Text = HandoverText.MessageFor(p, sidesText);
    }

    /// <summary>Closes the screen without raising <see cref="CloseRequested"/>.</summary>
    public void CloseForReal()
    {
        _closingForReal = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closingForReal && !e.Cancel && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            CloseRequested?.Invoke();
            return;
        }
        base.OnClosing(e);
    }
}
