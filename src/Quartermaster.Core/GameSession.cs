namespace Quartermaster.Core;

public sealed class GameSession
{
    readonly List<GameCommand> _log = new();

    public GameSession(GameState initial, string moduleSha256)
    {
        State = initial;
        ModuleSha256 = moduleSha256;
    }

    public GameState State { get; }
    public string ModuleSha256 { get; }
    public IReadOnlyList<GameCommand> Log => _log;
    public event Action<GameCommand>? Changed;

    /// <summary>The single entry point for changing game state.</summary>
    public void Execute(GameCommand c)
    {
        State.Apply(c);
        _log.Add(c);
        Changed?.Invoke(c);
    }

    internal void RestoreLog(IEnumerable<GameCommand> log) => _log.AddRange(log);
}
