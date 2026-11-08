using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Shell;

/// <summary>A file-type filter for the pickers, e.g. ("VASSAL modules (*.vmod)", ["*.vmod"]).</summary>
public sealed record FileFilter(string Name, IReadOnlyList<string> Patterns)
{
    public FileFilter(string name, params string[] patterns) : this(name, (IReadOnlyList<string>)patterns) { }
}

/// <summary>The setup chooser's answer: a predefined setup (null = "New game from module") and the difficulty.</summary>
public sealed record SetupChoice(SetupDef? Setup, Difficulty Difficulty);

/// <summary>
/// Every dialog the main window shows: file pickers, Yes/No questions, warnings and the setup chooser. The window only talks to dialogs
/// through this, so tests swap in a fake and never open a real window.
/// </summary>
public interface IDialogs
{
    /// <summary>An existing file's local path, or null when cancelled.</summary>
    Task<string?> OpenFileAsync(Window owner, string title, IReadOnlyList<FileFilter> filters);

    /// <summary>A path to save to (with <paramref name="defaultExtension"/> added when the name has none), or null when cancelled.</summary>
    Task<string?> SaveFileAsync(Window owner, string title, FileFilter filter, string defaultExtension);

    /// <summary>A Yes/No question with No as the default (WinForms MessageBoxDefaultButton.Button2). True = Yes.</summary>
    Task<bool> AskYesNoAsync(Window owner, string message, string caption);

    /// <summary>A warning with an OK button.</summary>
    Task ShowWarningAsync(Window owner, string message, string caption);

    /// <summary>The setup chooser (WinForms SetupChooserDialog); null when cancelled.</summary>
    Task<SetupChoice?> ChooseSetupAsync(Window owner, ModuleModel model, bool preselectNew, Difficulty difficulty);
}

/// <summary>The real dialogs: the OS file pickers (Avalonia storage provider), small classic message boxes and the setup chooser.</summary>
public sealed class Dialogs : IDialogs
{
    /// <summary>Shown when a picker returns a location without a local path (e.g. a cloud or portal item).</summary>
    public const string NotLocalText = "This location can't be opened directly; choose a file on this computer.";

    public async Task<string?> OpenFileAsync(Window owner, string title, IReadOnlyList<FileFilter> filters)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = filters.Select(Type).ToList(),
        });
        if (files.Count == 0) return null;
        if (files[0].TryGetLocalPath() is { } path) return path;
        await MessageBox.ShowAsync(owner, NotLocalText, "Quartermaster", yesNo: false);
        return null;
    }

    public async Task<string?> SaveFileAsync(Window owner, string title, FileFilter filter, string defaultExtension)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            DefaultExtension = defaultExtension,
            FileTypeChoices = new[] { Type(filter) },
            ShowOverwritePrompt = true,
        });
        if (file == null) return null;
        if (file.TryGetLocalPath() is not { } path)
        {
            await MessageBox.ShowAsync(owner, NotLocalText, "Quartermaster", yesNo: false);
            return null;
        }
        return Path.HasExtension(path) ? path : path + "." + defaultExtension;
    }

    static FilePickerFileType Type(FileFilter f) => new(f.Name) { Patterns = f.Patterns.ToList() };

    public Task<bool> AskYesNoAsync(Window owner, string message, string caption) =>
        MessageBox.ShowAsync(owner, message, caption, yesNo: true);

    public Task ShowWarningAsync(Window owner, string message, string caption) =>
        MessageBox.ShowAsync(owner, message, caption, yesNo: false);

    public Task<SetupChoice?> ChooseSetupAsync(Window owner, ModuleModel model, bool preselectNew, Difficulty difficulty) =>
        new SetupChooser(model, difficulty, preselectNew).ShowDialog<SetupChoice?>(owner);
}

/// <summary>A small classic message box: the text, then OK (or Yes / No with No focused, as WinForms Button2 default).</summary>
sealed class MessageBox : Window
{
    MessageBox(string message, string caption, bool yesNo)
    {
        Title = caption;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MinWidth = 260;
        MaxWidth = 520;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(10) };
        Button Add(string text, bool result, bool isDefault = false, bool isCancel = false)
        {
            var b = new Button { Content = text, MinWidth = 75, IsDefault = isDefault, IsCancel = isCancel, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) => Close(result);
            buttons.Children.Add(b);
            return b;
        }
        Button focus;
        if (yesNo)
        {
            Add("Yes", true);
            focus = Add("No", false, isDefault: true, isCancel: true);
        }
        else focus = Add("OK", true, isDefault: true, isCancel: true);

        var panel = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(16, 16, 16, 6) });
        Content = panel;
        Opened += (_, _) => focus.Focus();
    }

    public static async Task<bool> ShowAsync(Window owner, string message, string caption, bool yesNo) =>
        await new MessageBox(message, caption, yesNo).ShowDialog<bool?>(owner) == true;
}
