using Avalonia.Controls;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// Avalonia raises <see cref="TextBox.TextChanged"/> through the dispatcher, after the code that set <see cref="TextBox.Text"/> has
/// returned, so a "loading" flag around a programmatic load does not cover it. Handlers that must tell typing from loading listen
/// to the Text property instead, which changes synchronously.
/// </summary>
public static class TextEdits
{
    /// <summary>Runs <paramref name="changed"/> synchronously whenever the box's text changes (typed or set in code).</summary>
    public static void OnTextSet(this TextBox box, Action changed) =>
        box.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) changed(); };
}
