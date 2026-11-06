namespace Quartermaster.Render;

/// <summary>Generation counter for async init: a ticket is current only until the next ticket is issued or the gate is invalidated.</summary>
public sealed class InitGate
{
    private int _gen;

    public int Next() => ++_gen;

    public void Invalidate() => _gen++;

    public bool IsCurrent(int ticket) => ticket == _gen;
}
