using System.Reflection;
using Avalonia.Controls;
using Quartermaster.Desktop.Shell;

namespace Quartermaster.Desktop.Tests;

/// <summary>An <see cref="IDialogs"/> whose shortcut capture always throws and whose warning records itself, then throws when asked to.</summary>
public class ThrowingDialogs : DispatchProxy
{
    public readonly List<(string Message, string Caption)> Warnings = new();
    public bool ThrowOnWarning;

    public static (IDialogs Dialogs, ThrowingDialogs State) Create(bool throwOnWarning)
    {
        var d = Create<IDialogs, ThrowingDialogs>();
        var state = (ThrowingDialogs)(object)d;
        state.ThrowOnWarning = throwOnWarning;
        return (d, state);
    }

    protected override object? Invoke(MethodInfo? m, object?[]? args)
    {
        switch (m!.Name)
        {
            case "AskShortcutAsync": throw new InvalidOperationException("capture boom");
            case "ShowWarningAsync":
                Warnings.Add(((string)args![1]!, (string)args[2]!));
                if (ThrowOnWarning) throw new InvalidOperationException("warning boom");
                return Task.CompletedTask;
            default: return null;
        }
    }
}
