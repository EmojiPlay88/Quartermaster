using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>Thread-safe cache of parsed piece types keyed by (already prototype-expanded) type string.</summary>
public sealed class PieceTypeCache(DiagnosticLog log)
{
    private static readonly ConditionalWeakTable<ImageSource, PieceTypeCache> Shared = new();
    private readonly ConcurrentDictionary<string, PieceType> _types = new(StringComparer.Ordinal);

    public DiagnosticLog Log { get; } = log;

    public PieceType Get(string typeString) =>
        _types.GetOrAdd(typeString ?? "", t => PieceTypeParser.Parse(t, Log, null));

    /// <summary>The cache shared by everything drawing or hit-testing with <paramref name="images"/>.</summary>
    public static PieceTypeCache For(ImageSource images) => Shared.GetValue(images, i => new PieceTypeCache(i.Log));
}
