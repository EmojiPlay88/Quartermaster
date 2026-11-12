using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Desktop.Launcher;
using Quartermaster.Render.Update;

namespace Quartermaster.Desktop.Updates;

/// <summary>What's new (WinForms WhatsNewDialog): the release notes of an update as plain text (the Markdown is not rendered), with a link
/// to the release page.</summary>
public sealed class WhatsNewWindow : Window
{
    internal TextBox NotesBox { get; }
    internal Button PageLink { get; } = LauncherWindow.Link("Open the release page");
    internal Button CloseButton { get; } = new() { Content = "Close", MinWidth = 75, IsDefault = true, IsCancel = true };

    /// <param name="openPage">Opens the release page (the browser; tests record it).</param>
    public WhatsNewWindow(UpdateInfo info, Action<TopLevel, Uri> openPage)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(openPage);
        Title = UpdateOffer.WhatsNewTitle(info);
        Width = 560;
        Height = 380;
        MinWidth = 360;
        MinHeight = 240;
        ShowInTaskbar = false;
        CanMinimize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        LauncherWindow.AddLinkStyle(this);

        NotesBox = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(info.Notes) ? UpdateOffer.NoNotes : info.Notes.ReplaceLineEndings(),
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily(Quartermaster.Desktop.Theme.ClassicFonts.Mono),
            CaretIndex = 0,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(NotesBox, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        PageLink.Margin = new Thickness(3, 0);
        PageLink.Click += (_, _) => openPage(this, info.PageUrl);
        CloseButton.Click += (_, _) => Close();

        var buttons = new DockPanel { Margin = new Thickness(8, 6), Height = 28 };
        DockPanel.SetDock(PageLink, Dock.Left);
        CloseButton.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Children.Add(PageLink);
        buttons.Children.Add(CloseButton);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new Border { Padding = new Thickness(8, 8, 8, 0), Child = NotesBox });
        Content = root;
    }
}
