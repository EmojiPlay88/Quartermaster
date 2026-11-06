namespace Quartermaster.Vassal;

public enum DiagnosticSeverity { Info, Warning, Error }

public sealed record Diagnostic(DiagnosticSeverity Severity, string Category, string Message, string? Context);

/// <summary>Thread-safe diagnostics sink. <see cref="Added"/> is raised on the adding thread, outside the lock.</summary>
public sealed class DiagnosticLog
{
    private readonly object _gate = new();
    private readonly List<Diagnostic> _items = new();
    private readonly HashSet<string> _keys = new();

    public event Action<Diagnostic>? Added;

    /// <summary>Snapshot of the entries at the time of the call.</summary>
    public IReadOnlyList<Diagnostic> Items
    {
        get { lock (_gate) return _items.ToArray(); }
    }

    public void Add(DiagnosticSeverity s, string category, string message, string? context = null)
    {
        var d = new Diagnostic(s, category, message, context);
        lock (_gate) _items.Add(d);
        Added?.Invoke(d);
    }

    /// <summary>Adds the entry unless <paramref name="dedupeKey"/> was already used. Returns whether it was added.</summary>
    public bool AddOnce(string dedupeKey, DiagnosticSeverity s, string category, string message, string? context = null)
    {
        var d = new Diagnostic(s, category, message, context);
        lock (_gate)
        {
            if (!_keys.Add(dedupeKey)) return false;
            _items.Add(d);
        }
        Added?.Invoke(d);
        return true;
    }
}
