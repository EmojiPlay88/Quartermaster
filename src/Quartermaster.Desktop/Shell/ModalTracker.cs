using Avalonia.Controls;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// Counts the modal dialogs, message boxes and file pickers that are open (WinForms MainForm.ModalOpen looked at Application.OpenForms; Avalonia
/// has no such list). The hot-seat handover screen waits while one is open. Every <c>ShowDialog</c> of the app goes through
/// <see cref="ShowDialogTracked{T}"/> (a test guards this).
/// </summary>
public static class ModalTracker
{
    /// <summary>How many modal dialogs (or OS pickers) are open now, all windows together.</summary>
    public static int Depth { get; private set; }

    public static bool Open => Depth > 0;

    /// <summary><see cref="Window.ShowDialog{TResult}(Window)"/>, counted in <see cref="Depth"/> until it closes.</summary>
    public static Task<T?> ShowDialogTracked<T>(this Window dlg, Window owner) => Track(() => dlg.ShowDialog<T?>(OwnerFor(owner)));

    /// <summary><see cref="Window.ShowDialog(Window)"/>, counted in <see cref="Depth"/> until it closes.</summary>
    public static Task ShowDialogTracked(this Window dlg, Window owner) => Track(() => dlg.ShowDialog(OwnerFor(owner)));

    /// <summary>Counts <paramref name="modal"/> (a picker, a hook standing for a dialog) as open until its task ends.</summary>
    public static async Task<T> Track<T>(Func<Task<T>> modal)
    {
        Depth++;
        try { return await modal(); }
        finally { Depth--; }
    }

    /// <inheritdoc cref="Track{T}(Func{Task{T}})"/>
    public static async Task Track(Func<Task> modal)
    {
        Depth++;
        try { await modal(); }
        finally { Depth--; }
    }

    /// <summary>
    /// A dialog cannot be owned by a hidden window (Avalonia refuses it). While the hot-seat handover screen stands in for the main window
    /// (closing it asks the unsaved-changes question, say), the question is owned by the handover screen instead.
    /// </summary>
    internal static Window OwnerFor(Window owner) =>
        !owner.IsVisible && owner is MainWindow { Handover: { IsVisible: true } h } ? h : owner;
}
