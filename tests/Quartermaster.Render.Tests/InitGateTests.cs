using Quartermaster.Render;

namespace Quartermaster.Render.Tests;

public class InitGateTests
{
    [Fact] public void FreshTicket_IsCurrent() { var g = new InitGate(); Assert.True(g.IsCurrent(g.Next())); }

    [Fact] public void ReplacedTicket_IsStale()
    {
        var g = new InitGate();
        var a = g.Next(); var b = g.Next();
        Assert.False(g.IsCurrent(a)); Assert.True(g.IsCurrent(b));
    }

    [Fact] public void Invalidate_MakesTicketStale() { var g = new InitGate(); var a = g.Next(); g.Invalidate(); Assert.False(g.IsCurrent(a)); }
}
